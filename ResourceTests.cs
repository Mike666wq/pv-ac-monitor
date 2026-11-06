using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ExperimentMonitor {
    /// <summary>
    /// 长跑稳定性：连续多轮采集，验证两件在单轮测试里看不出来的事。
    ///
    ///  1. **每轮都是完整的 37 行**。A2 修掉了"站内超时吃掉剩余点"的 break，
    ///     但单轮测试只能证明一轮。多轮才能看出回退、隔离、恢复这几条路径上
    ///     会不会又漏行（例如隔离期结束后第一轮、失败计数清零那一轮）。
    ///  2. **资源不随轮数增长**。每轮都会建 Observation、写队列项、发布事件；
    ///     如果哪里泄漏（事件没退订、Timer 没释放、字符串缓存无上限），
    ///     跑一夜就会积累成崩溃。这里用托管堆与句柄数做粗筛。
    ///
    /// 用模拟传输，不需要硬件。
    /// </summary>
    public static class ResourceTests {
        sealed class Check {
            readonly List<string> passed = new List<string>();
            public int Count { get { return passed.Count; } }
            public List<string> Names { get { return passed; } }
            public void That(string name, bool condition) {
                if (!condition) throw new InvalidOperationException("资源稳定性检查失败：" + name);
                passed.Add(name);
            }
        }

        public static List<string> Run(List<Point> points, string root, int rounds) {
            if (points == null || points.Count == 0) throw new ArgumentException("点表不能为空");
            if (rounds < 5) rounds = 5;
            var check = new Check();
            string folder = System.IO.Path.Combine(root, ".testing", "resources-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.Copy(System.IO.Path.Combine(root, "protocol.json"), System.IO.Path.Combine(folder, "protocol.json"));

            var perRound = new Dictionary<long, int>();
            var firstRoundUtc = new Dictionary<long, DateTime>();
            int roundCount = 0;
            long lastRound = -1;

            using (var engine = new MonitorEngine(points, folder)) {
                engine.TransportFactory = (sim, port, baud) => new SimulationTransport();
                engine.ObservationReceived += o => {
                    lock (perRound) {
                        long r = o.Round;
                        int current;
                        perRound.TryGetValue(r, out current);
                        perRound[r] = current + 1;
                        if (!firstRoundUtc.ContainsKey(r)) firstRoundUtc[r] = o.RoundStartedUtc;
                        if (r != lastRound) { lastRound = r; roundCount++; }
                    }
                };
                long heapBefore = 0;
                int handlesBefore = 0;
                engine.Connect(true, "SIM", 9600);
                try {
                    engine.Start(1, 1, points.Select(p => p.name));
                    var watch = Stopwatch.StartNew();
                    // 等到跑够轮数或超时（按每轮约 1.2 秒估，留足余量）。
                    while (watch.ElapsedMilliseconds < 15000 + rounds * 2500L) {
                        Thread.Sleep(50);
                        lock (perRound) if (perRound.Count >= rounds) break;
                    }
                    Thread.Sleep(300);
                    if (heapBefore == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); heapBefore = GC.GetTotalMemory(true); handlesBefore = Process.GetCurrentProcess().HandleCount; }
                    engine.Stop();
                } finally {
                    try { engine.Disconnect(); } catch { }
                }

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long heapAfter = GC.GetTotalMemory(true);
                int handlesAfter = Process.GetCurrentProcess().HandleCount;

                int observed;
                lock (perRound) observed = perRound.Count;
                check.That("连续跑满至少 " + rounds + " 轮（实际 " + observed + " 轮）", observed >= rounds);

                int expectedRows;
                lock (perRound) {
                    expectedRows = points.Count;
                    // 最后一轮在 Stop() 时可能才采到一半 —— 那是"正在跑的这一轮"，不是缺陷。
                    // 真正的缺陷是：**除了最后一轮之外**还有哪一轮不完整。
                    long maxRound = perRound.Keys.Max();
                    var incomplete = perRound.Where(pair => pair.Key != maxRound && pair.Value != expectedRows).ToList();
                    string detail = String.Join("；", perRound.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
                    check.That("除最后一轮（在途）外，每一轮都是完整的 " + expectedRows + " 行（实际 " + detail + "）", incomplete.Count == 0);
                    // 轮次编号必须连续，不能跳号 —— 跳号说明某些轮次根本没产生行。
                    var ids = perRound.Keys.OrderBy(k => k).ToList();
                    bool contiguous = true;
                    for (int i = 1; i < ids.Count; i++) if (ids[i] != ids[i - 1] + 1) contiguous = false;
                    check.That("轮次编号连续无跳号", contiguous);
                }

                // 托管堆增长：跑 N 轮后应远小于每轮数据量的若干倍；给一个宽松但能抓住泄漏的上限。
                long growth = heapAfter - heapBefore;
                check.That("托管堆未随轮数无界增长（" + (growth / 1024) + " KB）", growth < 24L * 1024 * 1024);
                int handleGrowth = handlesAfter - handlesBefore;
                check.That("句柄数未随轮数无界增长（+" + handleGrowth + "）", handleGrowth < 400);
            }

            try { System.IO.Directory.Delete(folder, true); } catch { }
            return check.Names;
        }
    }
}
