using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Diagnostics;

namespace ExperimentMonitor {
    /// <summary>
    /// 迟到帧闸门的离线验证。
    ///
    /// <para>背景：MODBUS RTU 没有事务号，03 响应又不回显起始地址 ——
    /// 同站、同字节数的两笔响应在协议层无法区分。上一笔事务迟到的帧如果正好卡在
    /// 下一笔请求之后，就会被当成"本次响应"读进去，那是一个**看起来正常、实际属于
    /// 上一笔**的数。这是"程序说谎"的一种，而且是最危险的一种。</para>
    ///
    /// <para>判据（Protocol.cs 的 Collector.Read）：响应帧自身在线路上的传输时间
    /// + 从站最小处理时间是物理下限；往返时间低于下限的"响应"不可能属于本次请求，
    /// 判为 ambiguous。</para>
    ///
    /// <para>这一套件要回答两个问题：
    /// ① <b>不误杀</b>：正常响应（哪怕很慢）绝不能被判成 ambiguous；
    /// ② <b>不放过</b>：早于物理下限的帧必须被判成 ambiguous，且<b>不能被静默丢弃</b> ——
    /// 它仍要占一行，让"这一轮这个点没读到数"看得见。</para>
    ///
    /// <para>这一套件把原先"需要真机"的缺口推进到"逻辑已离线钉死，真机只需复核真实时延分布"。
    /// 剩下的不确定性是：真机上从站的实际处理时延分布、以及帧是否会在缓冲区里错位。
    /// 那两项仍然需要真机。</para>
    /// </summary>
    public static class LateFrameTests {

        static void Check(List<string> checks, bool ok, string name) {
            checks.Add(name + (ok ? "" : " — FAILED"));
            if (!ok) throw new Exception("迟到帧闸门检查失败：" + name);
        }

        /// <summary>造一帧合法的 03 响应（站号 + 功能码 + 字节数 + 数据 + CRC）。</summary>
        static byte[] Response(int slave, int count, ushort value) {
            var body = new List<byte> { (byte)slave, 3, (byte)(count * 2) };
            // 数据段必须正好 count*2 字节（每寄存器 2 字节），少一个都会被 Modbus.Extract 判成长度不匹配。
            for (int i = 0; i < count * 2; i++) body.Add(i == 0 ? (byte)(value >> 8) : (byte)(value & 0xFF));
            return Modbus.WithCrc(body.ToArray());
        }

        /// <summary>用 ScriptedTransport 读一次，返回这条观测的质量与状态。</summary>
        static Observation ReadOnce(ScriptedTransport transport, int slave, int address, int count, string mode) {
            var point = new Point {
                name = "T1", description = "主机罐温度",
                binding = new Binding { device = "PLC", slave = slave, address_zero_based = address, register_count = count, decode_proposal = "int16" }
            };
            return Collector.Read(transport, point, mode, "simulation", 2000, CancellationToken.None, delegate(string s) { });
        }

        public static List<string> Run(List<Point> points, string root) {
            var checks = new List<string>();
            try {
                // 9600 8N1 下 1 个寄存器的物理下限：响应帧 5+2*1=7 字节 → 7*10/9600*1000+1 = 8.29 ms
                int floor = (int)Math.Ceiling(Modbus.PhysicalFloorMs(1, 9600));
                Check(checks, floor >= 8 && floor <= 10, "1 寄存器的物理下限在 8~10 ms 之间（实测 " + floor + " ms）");

                // ── ① 不误杀：正常响应（哪怕慢）绝不能被判 ambiguous ──────────────────
                // 从物理下限一路扫到 3 秒。只要有一个被误判，就说明判据过严。
                // 用 FakeRoundTripMs 精确指定往返时间，不用真实睡眠 ——
                // 真实睡眠受 Windows 计时器粒度（~15.6 ms）限制，边界根本测不准。
                int[] safeDelays = { floor, floor + 1, floor + 2, floor + 10, 50, 200, 1000, 3000 };
                foreach (int delay in safeDelays) {
                    var t = new ScriptedTransport { FloorMs = floor, FakeRoundTripMs = delay };
                    t.Enqueue(Response(2, 1, 26), 0, 2);
                    Observation o = ReadOnce(t, 2, 302, 1, "INT16");
                    Check(checks, o.Quality == Quality.Good,
                        "正常响应（往返 " + delay + " ms）不被判成 ambiguous（实际质量 " + o.Quality + "，状态「" + o.Status + "」，值「" + o.Value + "」）");
                    Check(checks, o.Number.HasValue && Math.Abs(o.Number.Value - 26) < 0.001,
                        "正常响应（往返 " + delay + " ms）取到正确值 26（实际 " + (o.Number.HasValue ? o.Number.Value.ToString("0.###", CultureInfo.InvariantCulture) : "null") + "）");
                    Check(checks, o.FaultClass != "late", "正常响应（往返 " + delay + " ms）不带 late 故障标记");
                }

                // ── ② 不放过：早于物理下限的帧必须判 ambiguous ──────────────────────
                {
                    var t = new ScriptedTransport { FloorMs = floor, FakeRoundTripMs = 0 };
                    t.Enqueue(Response(2, 1, 26), 0, 2);
                    Observation o = ReadOnce(t, 2, 302, 1, "INT16");
                    Check(checks, o.Quality == Quality.Ambiguous,
                        "零延迟到达的帧被判成 ambiguous（实际质量 " + o.Quality + "）");
                    Check(checks, o.FaultClass == "late", "零延迟到达的帧带 late 故障标记");
                    Check(checks, !o.Number.HasValue, "被判 ambiguous 的观测没有数值（不会拿一个无意义的数去当读数）");
                    Check(checks, o.Payload != null && o.Payload.Length == 0, "被判 ambiguous 的观测不带载荷");
                    Check(checks, o.Status.Contains("物理下限"), "ambiguous 的状态说明点明原因（实际「" + o.Status + "」）");
                }

                // ── ③ 边界逐毫秒扫描：判据必须在某处翻转，且翻转点与公式一致 ──────
                // 判据是 roundTrip + 1.0 < floor ⇒ 超过 floor-1 即被接受。
                {
                    int lastRejected = -1, firstAccepted = -1;
                    for (int rt = 0; rt <= floor + 5; rt++) {
                        var t = new ScriptedTransport { FloorMs = floor, FakeRoundTripMs = rt };
                        t.Enqueue(Response(2, 1, 26), 0, 2);
                        Observation o = ReadOnce(t, 2, 302, 1, "INT16");
                        if (o.Quality == Quality.Ambiguous) lastRejected = rt;
                        else if (firstAccepted < 0) firstAccepted = rt;
                    }
                    Check(checks, lastRejected >= 0, "下界以下的往返时间确实被判 ambiguous（最大被拒往返 " + lastRejected + " ms）");
                    Check(checks, firstAccepted >= 0, "下界附近的往返时间确实被判 good（最小被接受往返 " + firstAccepted + " ms）");
                    Check(checks, firstAccepted == lastRejected + 1,
                        "翻转点连续：被判 ambiguous 的最大往返 " + lastRejected + " ms，被接受的最小往返 " + firstAccepted + " ms");
                    Check(checks, firstAccepted >= floor - 1,
                        "翻转点不早于物理下界减 1 ms（实际 " + firstAccepted + " vs " + (floor - 1) + "），保证不会误杀正常响应");
                }

                // ── ④ 2 寄存器（float32）用各自的下界 ───────────────────────────────
                {
                    int floor2 = (int)Math.Ceiling(Modbus.PhysicalFloorMs(2, 9600));
                    Check(checks, floor2 > floor, "2 个寄存器的物理下限比 1 个大（" + floor2 + " > " + floor + " ms）");
                    var ok = new ScriptedTransport { FloorMs = floor2 };
                    ok.Enqueue(Response(2, 2, 0), floor2 + 20, 2);
                    Observation good = ReadOnce(ok, 2, 302, 2, "FLOAT ABCD");
                    Check(checks, good.Quality == Quality.Good, "2 寄存器正常响应不被误杀（实际质量 " + good.Quality + "）");
                    var late = new ScriptedTransport { FloorMs = floor2 };
                    late.Enqueue(Response(2, 2, 0), 0, 2);
                    Observation bad = ReadOnce(late, 2, 302, 2, "FLOAT ABCD");
                    Check(checks, bad.Quality == Quality.Ambiguous, "2 寄存器零延迟帧被判 ambiguous（实际质量 " + bad.Quality + "）");
                }

                // ── ⑤ ambiguous 不能被静默丢弃：它仍要占一行，且下一轮能恢复 ──────
                // 这是"停止说谎"的关键：不能因为这一轮读到的是迟到帧，就把整个点从
                // 这一轮里抹掉 —— 那会让界面和统计以为"这个点没被采集过"。
                {
                    var t = new ScriptedTransport { FloorMs = floor };
                    t.Enqueue(Response(2, 1, 26), 0, 2);   // 第一轮：迟到帧
                    t.Enqueue(Response(2, 1, 27), floor + 30, 2); // 第二轮：正常
                    Observation first = ReadOnce(t, 2, 302, 1, "INT16");
                    Check(checks, first.Quality == Quality.Ambiguous, "第一轮判定 ambiguous");
                    Check(checks, first.Point == "T1" && !String.IsNullOrEmpty(first.Status),
                        "ambiguous 观测仍然带着点名与状态说明（不会被抹成空行）");
                    Observation second = ReadOnce(t, 2, 302, 1, "INT16");
                    Check(checks, second.Quality == Quality.Good, "迟到帧之后的下一轮恢复正常读取");
                    Check(checks, second.Number.HasValue && Math.Abs(second.Number.Value - 27) < 0.001,
                        "恢复后取到的是下一笔的值 27（不是上一笔的残留）");
                }

                // ── ⑥ 迟到帧不触发重建串口 ─────────────────────────────────────────
                // 迟到帧说明通道是好的（帧本身完整、CRC 正确），重建端口只会打断
                // 总线上其它站的时序。判据必须把"late"与"port"分开。
                {
                    var t = new ScriptedTransport { FloorMs = floor };
                    t.Enqueue(Response(2, 1, 26), 0, 2);
                    Observation o = ReadOnce(t, 2, 302, 1, "INT16");
                    Check(checks, o.FaultClass == "late" && o.FaultClass != "port",
                        "迟到帧归类为 late 而不是 port（不会因此重建串口）");
                }
            } catch (Exception ex) {
                checks.Add("异常：" + ex.Message);
                throw;
            }
            return checks;
        }
    }
}
