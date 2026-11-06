using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>
    /// 统一测试入口：逐个套件独立执行，**一个套件失败不影响其它套件**，最后汇总成矩阵。
    ///
    /// 为什么需要它：原来的 <c>--self-test</c> 在 ExtendedTests 里顺序调用各套件，
    /// 第一个抛异常就整体中断 —— 于是"存储套件挂了"会把后面 UI、控制、工作流的结果
    /// 全部掩埋，看报告的人只能看到一个失败，看不到其余 5 个套件其实是好的。
    /// 更糟的是，那些**根本没写用例**的项目（需要真机/现场）在报告里和"跑不过"长得一样。
    ///
    /// 这份矩阵把三种状态分开记：
    ///   passed  通过
    ///   failed  跑了但没过（代码缺陷）
    ///   gap     根本没写自动化用例（附原因，绝不伪装成 skipped）
    /// </summary>
    public static class TestMatrix {
        public sealed class Entry {
            public string suite = "";
            public string status = "";          // passed / failed
            public int count;
            public long ms;
            public string detail = "";
        }

        /// <summary>没有自动化用例的项，如实申报原因。这里列出来的每一项都需要真机或现场。</summary>
        public static readonly string[][] Gaps = new string[][] {
            new[] { "块读 vs 逐点读对拍", "需要真机：未绑定寄存器是否被 PLC 接受、块读与逐点读是否逐位一致" },
            new[] { "迟到帧闸门不误杀", "需要真机：物理下界判据是否会把正常响应误判为 ambiguous" },
            new[] { "真实串口的故障恢复", "需要真机：SerialTransport 的 CRC/超时/端口重建路径无法在沙箱内驱动" },
            new[] { "控制写入端到端", "需要真机：真实 PLC 写入与设备动作确认" },
            new[] { "站号 6 无响应根因", "需要现场：DDSU666 为何自 2026-09-30 起无响应" },
            new[] { "D2000 / D2001 / D2207 语义", "需要现场：操作面板端口并观察寄存器变化" },
            new[] { "ZI6682 分流器倍数", "需要现场：发电工况采样 + 独立基准（充电器面板/铭牌/钳形表）" },
            new[] { "T1 与 D2206 的最终命名", "需要现场：探头贴附对照" },
        };

        public static List<Entry> RunAll(List<Point> points, string root) {
            var entries = new List<Entry>();
            // 顺序刻意从轻到重：轻的套件先跑完，重的失败了也不影响前面已出的结论。
            Run(entries, "self", root, "self-test-result.json", () => SelfTests.Run(points, root));
            Run(entries, "core", root, "core-test-result.json", () => {
                var checks = CoreTests.Run(points, root);
                Write(root, "core-test-result.json", checks.Count, checks);
            });
            Run(entries, "storage+records+export", root, "records-test-result.json", () => {
                int storage = StorageTests.Run(), records = RecordsTests.Run(), failures = ExportFailureTests.Run(points, root);
                WriteCounts(root, "records-test-result.json", storage, records, failures);
            });
            Run(entries, "cloud", root, "cloud-test-result.json", () => {
                Write(root, "cloud-test-result.json", CloudSelfTests.Run(), null);
            });
            Run(entries, "trend", root, "trend-test-result.json", () => {
                var checks = TrendTests.Run();
                Write(root, "trend-test-result.json", checks.Count, checks);
            });
            Run(entries, "control", root, "control-test-result.json", () => {
                var checks = ControlTests.Run(points, root);
                Write(root, "control-test-result.json", checks.Count, checks);
            });
            Run(entries, "workflow", root, "workflow-test-result.json", () => WorkflowTests.Run(points, root));
            Run(entries, "resources", root, "resource-test-result.json", () => {
                var checks = ResourceTests.Run(points, root, 8);
                Write(root, "resource-test-result.json", checks.Count, checks);
            });
            Run(entries, "ui", root, "ui-test-result.json", () => UiLayoutTests.Run(points, root));
            return entries;
        }

        static void Run(List<Entry> entries, string name, string root, string resultFile, Action action) {
            var entry = new Entry { suite = name };
            var watch = Stopwatch.StartNew();
            try { action(); entry.status = "passed"; }
            catch (Exception ex) {
                entry.status = "failed";
                entry.detail = ex.GetType().Name + "：" + (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ");
                if (entry.detail.Length > 400) entry.detail = entry.detail.Substring(0, 400) + "…";
            }
            entry.ms = watch.ElapsedMilliseconds;
            entry.count = ReadCount(root, resultFile);
            entries.Add(entry);
        }

        static int ReadCount(string root, string file) {
            try {
                string path = Path.Combine(root, file);
                if (!File.Exists(path)) return 0;
                var map = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (map == null) return 0;
                object value;
                if (map.TryGetValue("count", out value) && value != null) return Convert.ToInt32(value);
                if (map.TryGetValue("checks", out value) && value != null) {
                    var list = value as System.Collections.ArrayList;
                    if (list != null) return list.Count;
                }
            } catch { }
            return 0;
        }

        static void Write(string root, string file, int count, List<string> checks) {
            var payload = new Dictionary<string, object>();
            payload["passed"] = true;
            payload["count"] = count;
            if (checks != null) payload["checks"] = checks;
            File.WriteAllText(Path.Combine(root, file), new JavaScriptSerializer().Serialize(payload), new System.Text.UTF8Encoding(false));
        }

        static void WriteCounts(string root, string file, int storage, int records, int failures) {
            var payload = new Dictionary<string, object> {
                { "passed", true }, { "storageChecks", storage }, { "recordsChecks", records },
                { "failureChecks", failures }, { "count", storage + records + failures }
            };
            File.WriteAllText(Path.Combine(root, file), new JavaScriptSerializer().Serialize(payload), new System.Text.UTF8Encoding(false));
        }

        /// <summary>把矩阵写成 JSON；返回失败套件数（0 表示全通过）。</summary>
        public static int WriteReport(List<Entry> entries, string root) {
            int failed = entries.Count(e => e.status == "failed");
            var gaps = Gaps.Select(g => new Dictionary<string, object> { { "item", g[0] }, { "reason", g[1] } }).ToList();
            var payload = new Dictionary<string, object> {
                { "generatedUtc", DateTime.UtcNow.ToString("o") },
                { "suites", entries },
                { "suitesPassed", entries.Count - failed },
                { "suitesFailed", failed },
                { "totalAssertions", entries.Sum(e => (long)e.count) },
                { "gaps", gaps },
                { "gapsCount", gaps.Count },
                { "note", "gaps 是「根本没写自动化用例」的项目，原因如实写在 reason 里；它们不是 skipped，也不是 failed。" }
            };
            File.WriteAllText(Path.Combine(root, "test-matrix.json"), new JavaScriptSerializer().Serialize(payload), new System.Text.UTF8Encoding(false));
            return failed;
        }
    }
}
