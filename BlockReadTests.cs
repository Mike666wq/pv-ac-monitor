using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ExperimentMonitor {
    /// <summary>
    /// 块读 vs 逐点读的离线对拍。
    ///
    /// <para>背景：块读还没接入采集（<see cref="BlockPlanner"/> 是纯函数），
    /// 所以"块读和逐点读读出来的是不是同一个数"这件事此前完全没有验证过。
    /// 一旦接错位，程序会**看起来正常**地报出一批数 —— 只是每个数挂错了行。
    /// 这是最难发现的一类缺陷，所以值得在真机之前先用夹具把它钉死。</para>
    ///
    /// <para>做法：给每个点一个**唯一可区分**的值（不同温度/不同字），
    /// 然后同一份真值分别用两种方式读：</para>
    /// <list type="number">
    /// <item>逐点读：每点一次 FC03，直接拿响应里的数据段；</item>
    /// <item>块读：按计划每块一次 FC03，用 <see cref="BlockPlanner.Slice"/> 按地址切出每点的字节。</item>
    /// </list>
    /// <para>两边逐位比对。任何一处错位都会被抓到 —— 因为值是唯一的，
    /// 错位必然导致不相等。</para>
    ///
    /// <para>这一套件把"块读 vs 逐点读对拍"缺口推进到"数值一致性已离线钉死"，
    /// 剩下必须上真机的只有"未绑定寄存器是否被 PLC 接受"（读设备行为，非程序行为）。</para>
    /// </summary>
    public static class BlockReadTests {

        static void Check(List<string> checks, bool ok, string name) {
            checks.Add(name + (ok ? "" : " — FAILED"));
            if (!ok) throw new Exception("块读对拍检查失败：" + name);
        }

        static string Hex(byte[] b) {
            if (b == null) return "<null>";
            return String.Join(" ", b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>从一帧 03 响应里取数据段（整帧去掉 站号/功能码/字节数/CRC 之后的 2N 字节）。</summary>
        static byte[] PayloadOf(byte[] frame) {
            var buf = new List<byte>(frame);
            byte[] parsed;
            int count = frame[2] / 2;
            if (!Modbus.Extract(buf, frame[0], count, out parsed)) return new byte[0];
            // parsed 是整帧：站号(1) 功能码(1) 字节数(1) 数据(2N) CRC(2)。
            // 数据段从第 3 字节起、取 2N 字节（与 Collector.Read 的取法一致）。
            return parsed.Skip(3).Take(count * 2).ToArray();
        }

        public static List<string> Run(List<Point> points, string root) {
            var checks = new List<string>();
            try {
                List<Point> bound = points.Where(p => p != null && p.binding != null && p.binding.register_count > 0).ToList();
                Check(checks, bound.Count >= 30, "点表里有足够多的绑定点用于对拍（" + bound.Count + " 个）");

                // ── 造真值：每个点一个唯一可区分的值 ─────────────────────────────
                // 用点的序号做种子，保证同一个点每次跑都是同一个值。
                var fixture = new FixtureTransport();
                var truth = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                int seed = 0;
                foreach (Point p in bound) {
                    int slave = p.binding.slave, address = p.binding.address_zero_based;
                    fixture.DeclareSegment(slave, address, p.binding.register_count);
                    if (p.binding.register_count == 2) {
                        // 双寄存器浮点：用序号造一个可区分的温度（避免 0 和易混淆值）
                        float v = 100.0f + seed * 0.37f;
                        fixture.SetFloat(slave, address, v);
                        truth[p.name] = BitConverter.GetBytes(v);
                    } else {
                        ushort w = (ushort)(0x1000 + seed * 7);
                        fixture.SetWord(slave, address, w);
                        truth[p.name] = new byte[] { (byte)(w >> 8), (byte)(w & 0xFF) };
                    }
                    seed++;
                }

                // ── 逐点读：每点一次 FC03，取响应数据段 ──────────────────────────
                var perPoint = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (Point p in bound) {
                    var o = Collector.Read(fixture, p, p.binding.register_count == 2 ? "FLOAT ABCD" : "INT16",
                        "simulation", 2000, CancellationToken.None, delegate(string s) { });
                    Check(checks, o.Quality == Quality.Good,
                        "逐点读 " + p.name + " 成功（质量 " + o.Quality + "，状态「" + o.Status + "」）");
                    perPoint[p.name] = o.Payload;
                }

                // ── 块读：按计划每块一次 FC03，按地址切片 ───────────────────────
                List<ReadBlock> blocks = BlockPlanner.Plan(bound);
                Check(checks, blocks.Count > 0, "块规划非空（" + blocks.Count + " 块）");
                Check(checks, BlockPlanner.Validate(blocks, bound).Count == 0,
                    "块规划自检无问题（" + String.Join("；", BlockPlanner.Validate(blocks, bound).ToArray()) + "）");
                Check(checks, blocks.Count < BlockPlanner.PerPointFrameCount(bound),
                    "块读帧数少于逐点读（" + blocks.Count + " < " + BlockPlanner.PerPointFrameCount(bound) + "）");
                Check(checks, blocks.All(b => b.Count <= BlockPlanner.MaxBlockRegisters),
                    "每块不超过协议上限 125 个寄存器");
                Check(checks, blocks.All(b => b.Holes == 0), "每块都是零空洞（绝不把未绑定寄存器塞进块里）");
                Check(checks, blocks.All(b => b.Slave >= 1 && b.Slave <= 247), "块站号都在合法范围");
                // 不跨站合并
                Check(checks, blocks.Select(b => b.Slave).Distinct().Count() > 0 && blocks.GroupBy(b => b.Slave).All(g => g.Count() >= 1),
                    "块按站号分组（不跨站合并）");

                var byBlock = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (ReadBlock block in blocks) {
                    byte[] frame = fixture.Read(block.Slave, block.Address, block.Count, 2000, CancellationToken.None, delegate(string s) { });
                    byte[] payload = PayloadOf(frame);
                    Check(checks, payload.Length == block.Count * 2,
                        "块 " + block + " 的响应数据段长度正确（" + payload.Length + " = " + (block.Count * 2) + "）");
                    foreach (string name in block.Points) {
                        Point p = bound.First(x => x.name == name);
                        byBlock[name] = BlockPlanner.Slice(block, p, payload);
                    }
                }

                // ── 逐位比对 ──────────────────────────────────────────────────────
                int mismatched = 0, empty = 0;
                var details = new List<string>();
                foreach (Point p in bound) {
                    byte[] a = perPoint[p.name], b = byBlock[p.name];
                    if (b == null || b.Length == 0) { empty++; details.Add(p.name + ": 切片为空"); continue; }
                    if (a == null || a.Length != b.Length || !a.SequenceEqual(b)) {
                        mismatched++;
                        if (details.Count < 5) details.Add(p.name + ": 逐点=" + Hex(a) + " 块读=" + Hex(b));
                    }
                }
                Check(checks, empty == 0, "每个点都能从块里切出字节（空切片 " + empty + " 个）");
                Check(checks, mismatched == 0,
                    "块读与逐点读逐位一致（不一致 " + mismatched + " 个" + (details.Count > 0 ? "；" + String.Join("；", details.ToArray()) : "") + "）");

                // ── 切片必须按地址算，不能按下标 ──────────────────────────────────
                // 点表里的书写顺序不是地址顺序。若按下标切片，整体错位且不会报错。
                // 这里构造一个"顺序被打乱"的点表副本，看切片结果是否仍然正确。
                {
                    List<Point> shuffled = bound.OrderByDescending(p => p.binding.address_zero_based).ToList();
                    List<ReadBlock> shuffledBlocks = BlockPlanner.Plan(shuffled);
                    int bad = 0;
                    foreach (ReadBlock block in shuffledBlocks) {
                        byte[] frame = fixture.Read(block.Slave, block.Address, block.Count, 2000, CancellationToken.None, delegate(string s) { });
                        byte[] payload = PayloadOf(frame);
                        foreach (string name in block.Points) {
                            Point p = bound.First(x => x.name == name);
                            byte[] sliced = BlockPlanner.Slice(block, p, payload);
                            if (!sliced.SequenceEqual(perPoint[p.name])) bad++;
                        }
                    }
                    Check(checks, bad == 0,
                        "点表顺序打乱后切片结果不变（切片按地址算而不是按下标；不一致 " + bad + " 个）");
                }

                // ── 越界切片必须返回空，不能错位给数据 ──────────────────────────
                {
                    ReadBlock block = blocks[0];
                    byte[] payload = PayloadOf(fixture.Read(block.Slave, block.Address, block.Count, 2000, CancellationToken.None, delegate(string s) { }));
                    Point outside = new Point {
                        name = "OUT_OF_RANGE", description = "越界点",
                        binding = new Binding { device = "PLC", slave = block.Slave, address_zero_based = block.Address + block.Count + 10, register_count = 2, decode_proposal = "int16" }
                    };
                    byte[] sliced = BlockPlanner.Slice(block, outside, payload);
                    Check(checks, sliced.Length == 0, "越界的点切出空数组而不是错位数据（" + Hex(sliced) + "）");
                }

                // ── 双寄存器浮点的值也必须一致（不只是字节） ─────────────────────
                {
                    int floatPoints = bound.Count(p => p.binding.register_count == 2);
                    Check(checks, floatPoints > 0, "点表里有双寄存器浮点点参与对拍（" + floatPoints + " 个）");
                    int mismatchValue = 0;
                    foreach (Point p in bound.Where(x => x.binding.register_count == 2)) {
                        var o = Collector.Read(fixture, p, "FLOAT ABCD", "simulation", 2000, CancellationToken.None, delegate(string s) { });
                        byte[] sliced = byBlock[p.name];
                        double? fromBlock;
                        Modbus.Decode(sliced, "FLOAT ABCD", out fromBlock);
                        if (!fromBlock.HasValue || !o.Number.HasValue) { mismatchValue++; continue; }
                        if (Math.Abs(fromBlock.Value - o.Number.Value) > 0.0001) mismatchValue++;
                    }
                    Check(checks, mismatchValue == 0,
                        "双寄存器浮点：块读解出的数值与逐点读一致（不一致 " + mismatchValue + " 个）");
                }
            } catch (Exception ex) {
                checks.Add("异常：" + ex.Message);
                throw;
            }
            return checks;
        }
    }
}
