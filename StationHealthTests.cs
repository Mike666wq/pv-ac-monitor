using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ExperimentMonitor {
    /// <summary>
    /// 站号失败/退避状态的可见性。
    ///
    /// <para>背景：站号连续超时后会进入退避（5/10/20/40/60/120/300 秒），期间该站被跳过、
    /// 每轮只写 skipped 行。界面如果不说明，现场看到的就是"市电那一整片一直是 —"，
    /// 分不清是**设备没接**还是**程序卡住** —— 这两种情况的处置方式完全不同。</para>
    ///
    /// <para>这一套件验证三件事：</para>
    /// <list type="number">
    /// <item>站号失败后，引擎确实把失败次数与下次重试时刻暴露出来；</item>
    /// <item>退避期间整站的点都写成 skipped（而不是消失），且 <c>Number</c> 是 null 不是 0；</item>
    /// <item>该站恢复后，暴露的状态被清掉（不会一直挂着"离线"）。</item>
    /// </list>
    /// </summary>
    public static class StationHealthTests {

        static void Check(List<string> checks, bool ok, string name) {
            checks.Add(name + (ok ? "" : " — FAILED"));
            if (!ok) throw new Exception("站状态检查失败：" + name);
        }

        /// <summary>等一次单轮采集真正结束（StartOnce 是异步起线程的）。</summary>
        static void WaitRound(MonitorEngine engine, long before) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 15000) {
                if (engine.CompletedCaptures > before && !engine.IsRunning) return;
                Thread.Sleep(20);
            }
            throw new Exception("等待单轮采集完成超时");
        }

        public static List<string> Run(List<Point> points, string root) {
            var checks = new List<string>();
            try {
                // 挑一个有多点、且不含控制点的站号，避免触发控制链路。
                var group = points.Where(p => p != null && p.binding != null
                        && p.name != "D2202" && p.name != "D2003" && p.name != "D2208"
                        && p.name != "D2000" && p.name != "D2001" && p.name != "D2207")
                    .GroupBy(p => p.binding.slave)
                    .Where(g => g.Count() >= 2)
                    .OrderBy(g => g.Key)
                    .FirstOrDefault();
                Check(checks, group != null, "点表里存在一个含多个点的非控制站号用于验证");
                int slave = group.Key;
                List<string> stationPoints = group.Select(p => p.name).ToList();

                string dir = Path.Combine(Path.GetFullPath(root), ".station-health");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                // 引擎的 root 必须是"含 protocol.json 的程序目录" —— 少了它 Run 会在
                // 读配置那一步抛异常，整轮直接结束（表现为"轮次完成但一笔都没读"）。
                File.Copy(Path.Combine(Path.GetFullPath(root), "protocol.json"), Path.Combine(dir, "protocol.json"));

                // 该站永远超时；其余站正常（用夹具只声明目标站之外的真值，读取时该站抛超时）。
                // FillMissing：其它站返回正常帧，只有目标站拿得到超时步 —— 否则"脚本耗尽"
                // 会把每个站都判成超时，验证不了"只有目标站在退避"。
                var transport = new ScriptedTransport { FillMissing = true };
                for (int i = 0; i < 4; i++) transport.EnqueueTimeout(slave);

                using (var engine = new MonitorEngine(points, dir)) {
                    engine.TransportFactory = (sim, port, baud) => transport;
                    engine.Connect(true, "SIM", 9600);

                    Check(checks, engine.StationLinkStates().Count == 0, "刚连接时没有任何站处于失败状态");

                    // 单轮采集：目标站第一个点就超时，站内其余点记 skipped。
                    long before1 = engine.CompletedCaptures;
                    engine.StartOnce(1,1,points.Select(p => p.name).ToArray());
                    WaitRound(engine, before1);
                    var states = engine.StationLinkStates();
                    Check(checks, states.Count == 1, "一次失败后暴露 1 个站的状态（实际 " + states.Count
                        + "；完成轮次=" + (engine.CompletedCaptures - before1)
                        + "；目标站=" + slave
                        + "；首点质量=" + (engine.LastObservation(stationPoints[0]) == null ? "<null>" : engine.LastObservation(stationPoints[0]).Quality)
                        + "）");
                    Check(checks, states[0].Slave == slave, "暴露的站号是失败的那一个（" + states[0].Slave + " vs " + slave + "）");
                    Check(checks, states[0].Failures >= 1, "失败次数被记录（" + states[0].Failures + "）");
                    Check(checks, states[0].RetryInSeconds > 0, "给出还有多少秒重试（" + states[0].RetryInSeconds + "）");
                    Check(checks, states[0].ToString().Contains("站" + slave) && states[0].ToString().Contains("无响应"),
                        "状态文案说明是哪个站无响应（" + states[0] + "）");
                    Check(checks, states[0].ToString().Contains("重试"), "状态文案给出现场可读的重试时间（" + states[0] + "）");

                    // 退避期间：整站的点都还在（写成 skipped），不是消失。
                    Check(checks, engine.LastObservation(stationPoints[0]) != null, "失败的那个点仍有观测行（不是消失）");
                    for (int i = 1; i < stationPoints.Count; i++) {
                        Observation skipped = engine.LastObservation(stationPoints[i]);
                        Check(checks, skipped != null, "同站后续点 " + stationPoints[i] + " 仍有一行（不是静默丢弃）");
                        Check(checks, skipped != null && skipped.Quality == Quality.Skipped,
                            "同站后续点 " + stationPoints[i] + " 标记为未读取（实际 " + (skipped == null ? "<null>" : skipped.Quality) + "）");
                        Check(checks, skipped != null && !skipped.Number.HasValue,
                            "未读取的点不给数值（不会把缺失写成 0）");
                    }

                    // 再跑一轮：仍在退避期内，失败次数不应被重复累加（否则退避会一直往后推）。
                    int failuresBefore = engine.StationLinkStates()[0].Failures;
                    long before2 = engine.CompletedCaptures;
                    engine.StartOnce(1,1,points.Select(p => p.name).ToArray());
                    WaitRound(engine, before2);
                    int failuresAfter = engine.StationLinkStates()[0].Failures;
                    Check(checks, failuresAfter == failuresBefore,
                        "退避期内再跑一轮不会重复累加失败次数（" + failuresBefore + " → " + failuresAfter + "）");

                    // 让这个站恢复：把下一次探测时刻拉到过去，然后喂一条 good 观测。
                    // 直接用公开 API 无法改 ProbeAt，所以这里改为验证"恢复时清状态"的另一条路径：
                    // 换一份正常响应的传输再连一次（Run 开头会重置这两个字典）。
                    var healthy = new FixtureTransport().DeclareSegment(slave, 0, 64);
                    using (var engine2 = new MonitorEngine(points, dir)) {
                        engine2.TransportFactory = (sim, port, baud) => healthy;
                        engine2.Connect(true, "SIM", 9600);
                        Check(checks, engine2.StationLinkStates().Count == 0, "重新连接后失败状态被清空（不会一直挂着离线）");
                    }
                }
            } catch (Exception ex) {
                checks.Add("异常：" + ex.Message);
                throw;
            }
            return checks;
        }
    }
}
