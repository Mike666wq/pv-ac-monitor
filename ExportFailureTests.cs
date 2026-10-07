using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;

namespace ExperimentMonitor {
    /// <summary>Failure and snapshot-boundary checks for the history export pipeline.</summary>
    public static class ExportFailureTests {
        sealed class SyncProgress<T> : IProgress<T> {
            readonly Action<T> report;
            public SyncProgress(Action<T> report) { this.report = report; }
            public void Report(T value) { if (report != null) report(value); }
        }

        static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }

        static HistoryFilter Filter(DateTime start, DateTime end) {
            return new HistoryFilter { Source = "simulation", FromUtc = start.AddSeconds(-5), ToUtc = end.AddSeconds(5), Devices = new string[0], Points = new string[0] };
        }

        static void Populate(string storage, Point point, int count, DateTime start) {
            using (var store = new ExperimentStore(storage, "simulation", "{export-failure-test:true}")) {
                store.Ready.GetAwaiter().GetResult();
                for (int i = 0; i < count; i++) {
                    DateTime utc = start.AddMilliseconds(i);
                    store.Record(new Observation {
                        Point = point.name, Description = point.description, Device = point.binding.device,
                        Source = "simulation", Status = "正常", Value = (i + 1).ToString(CultureInfo.InvariantCulture),
                        Slave = point.binding.slave, Address = point.binding.address_zero_based, Count = point.binding.register_count,
                        Utc = utc, RoundStartedUtc = utc, Round = i + 1, Number = i + 1,
                        RawNumber = i + 1, Unit = "test", Quality = "good", ConfigVersion = "export-test",
                        SessionId = store.SessionId
                    }, i + 1, "test");
                }
                store.FlushAsync().GetAwaiter().GetResult();
                Require(store.LastError.Length == 0, "导出测试夹具应完整写入本地模拟库");
            }
        }

        static void AppendOne(string storage, Point point, long round, DateTime utc) {
            using (var store = new ExperimentStore(storage, "simulation", "{export-freeze-append:true}")) {
                store.Ready.GetAwaiter().GetResult();
                store.Record(new Observation {
                    Point = point.name, Description = point.description, Device = point.binding.device,
                    Source = "simulation", Status = "正常", Value = "appended", Slave = point.binding.slave,
                    Address = point.binding.address_zero_based, Count = point.binding.register_count,
                    Utc = utc, RoundStartedUtc = utc, Round = round, Number = 999999, RawNumber = 999999,
                    Unit = "test", Quality = "good", ConfigVersion = "concurrent-append", SessionId = store.SessionId
                }, round, "test");
                store.FlushAsync().GetAwaiter().GetResult();
                Require(store.LastError.Length == 0, "快照冻结期间追加的模拟记录应成功写入数据库");
            }
        }

        static int XlsxDetailRows(string path) {
            using (var zip = ZipFile.OpenRead(path)) {
                var doc = new XmlDocument();
                using (var stream = zip.GetEntry("xl/worksheets/sheet2.xml").Open()) doc.Load(stream);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                return doc.SelectNodes("//s:sheetData/s:row", ns).Count;
            }
        }

        static int CsvDataRows(string path) {
            return File.ReadAllLines(path).Length - 1;
        }

        static void RequireNoPublishedResult(string parent) {
            Require(Directory.GetFiles(parent, "*", SearchOption.TopDirectoryOnly).Length == 0, "取消/失败时不能发布半成品文件");
            Require(Directory.GetDirectories(parent, "实验监控_导出暂存-*", SearchOption.TopDirectoryOnly).Length == 0, "取消/失败后必须清理目标目录暂存区");
            Require(Directory.GetDirectories(parent, "实验数据-*", SearchOption.TopDirectoryOnly).Length == 0, "取消/失败时不能保留正式结果目录");
        }

        public static int Run(List<Point> points, string root) {
            if (points == null || points.Count == 0) throw new ArgumentException("点表不能为空", "points");
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("根目录不能为空", "root");
            Point point = points.First(p => ExperimentExport.ExportablePoint(p.name));
            string scratch = Path.Combine(Path.GetFullPath(root), ".export-failure-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            int checks = 0;
            try {
                DateTime start = DateTime.UtcNow.AddMinutes(-2);
                string storage = Path.Combine(scratch, "storage");
                Directory.CreateDirectory(storage);
                Populate(storage, point, 300, start);
                HistoryFilter filter = Filter(start, start.AddSeconds(1));

                // An existing file used as the destination proves preflight errors include the path and preserve user data.
                string blockedPath = Path.Combine(scratch, "existing-target.txt");
                const string sentinel = "preserve-existing-target";
                File.WriteAllText(blockedPath, sentinel);
                DateTime fromBefore = filter.FromUtc, toBefore = filter.ToUtc;
                try {
                    ExperimentExport.ExportAsync(storage, filter, blockedPath, "xlsx", null, CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("已有文件路径应拒绝作为导出目录");
                } catch (IOException e) {
                    Require(e.Message.IndexOf(blockedPath, StringComparison.OrdinalIgnoreCase) >= 0, "目标错误应指出发生问题的路径");
                }
                Require(File.ReadAllText(blockedPath) == sentinel, "拒绝导出不能改写已有文件");
                Require(filter.FromUtc == fromBefore && filter.ToUtc == toBefore, "导出预检失败不能更改当前筛选条件");
                checks++;

                // Cancellation occurs inside the synchronous progress callback after snapshot streaming has begun.
                string cancelParent = Path.Combine(scratch, "cancel-output");
                Directory.CreateDirectory(cancelParent);
                var cancel = new CancellationTokenSource();
                bool cancelledDuringSnapshot = false;
                var cancelProgress = new SyncProgress<ExportProgress>(p => {
                    if (!cancelledDuringSnapshot && p.Phase == "固定导出快照" && p.CompletedRows >= 256) {
                        cancelledDuringSnapshot = true;
                        cancel.Cancel();
                    }
                });
                bool cancelled = false;
                try { ExperimentExport.ExportAsync(storage, filter, cancelParent, "xlsx", cancelProgress, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelledDuringSnapshot && cancelled, "测试必须在快照处理中触发并观察到取消");
                RequireNoPublishedResult(cancelParent);
                cancel.Dispose();
                checks++;

                // SQLite's upper IDs are frozen before the synchronous first progress callback appends a new row.
                string frozenParent = Path.Combine(scratch, "frozen-output");
                Directory.CreateDirectory(frozenParent);
                int firstExportCount = 300;
                bool xlsxAppended = false;
                var xlsxProgress = new SyncProgress<ExportProgress>(p => {
                    if (!xlsxAppended && p.Phase == "固定导出快照" && p.CompletedRows >= 256) {
                        xlsxAppended = true;
                        AppendOne(storage, point, 301, start.AddSeconds(2));
                    }
                });
                ExportResult frozenXlsx = ExperimentExport.ExportAsync(storage, filter, frozenParent, "xlsx", xlsxProgress, CancellationToken.None).GetAwaiter().GetResult();
                Require(xlsxAppended && frozenXlsx.Rows == firstExportCount, "Excel 行数应固定在导出启动时的记录上界");
                Require(frozenXlsx.Files.Length == 1 && XlsxDetailRows(Path.Combine(frozenXlsx.OutputDirectory, frozenXlsx.Files[0])) == firstExportCount + 1, "Excel 明细不得包含快照后追加的记录");
                checks++;

                int secondExportCount = firstExportCount + 1;
                bool csvAppended = false;
                var csvProgress = new SyncProgress<ExportProgress>(p => {
                    if (!csvAppended && p.Phase == "固定导出快照" && p.CompletedRows >= 256) {
                        csvAppended = true;
                        AppendOne(storage, point, 302, start.AddSeconds(3));
                    }
                });
                ExportResult frozenCsv = ExperimentExport.ExportAsync(storage, filter, frozenParent, "csv", csvProgress, CancellationToken.None).GetAwaiter().GetResult();
                string detailCsv = frozenCsv.Files.Single(x => x.Contains("明细"));
                Require(csvAppended && frozenCsv.Rows == secondExportCount, "CSV 行数应固定在导出启动时的记录上界");
                Require(CsvDataRows(Path.Combine(frozenCsv.OutputDirectory, detailCsv)) == secondExportCount, "CSV 明细不得包含快照后追加的记录");
                checks++;

                // The injected failure is after staged copy, so cleanup proves publication stays atomic.
                string stageFailureParent = Path.Combine(scratch, "stage-failure-output");
                Directory.CreateDirectory(stageFailureParent);
                bool stageFailureHookReached = false;
                ExportNaming.BeforePublishForTests = stage => { stageFailureHookReached = true; throw new IOException("injected publish failure"); };
                bool publishFailed = false;
                try { ExperimentExport.ExportAsync(storage, filter, stageFailureParent, "xlsx", null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException e) { publishFailed = e.Message.Contains("injected publish failure"); }
                finally { ExportNaming.BeforePublishForTests = null; }
                Require(stageFailureHookReached && publishFailed, "应能在发布阶段注入可识别的失败");
                RequireNoPublishedResult(stageFailureParent);
                checks++;

                string stageParent = Path.Combine(scratch, "stage-cancel-output");
                Directory.CreateDirectory(stageParent);
                var stageCancel = new CancellationTokenSource();
                bool stageHookReached = false;
                ExportNaming.BeforePublishForTests = stage => { stageHookReached = true; stageCancel.Cancel(); };
                bool stageCancelled = false;
                try { ExperimentExport.ExportAsync(storage, filter, stageParent, "xlsx", null, stageCancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { stageCancelled = true; }
                finally { ExportNaming.BeforePublishForTests = null; stageCancel.Dispose(); }
                Require(stageHookReached && stageCancelled, "应在目标目录暂存完成后取消原子发布");
                RequireNoPublishedResult(stageParent);
                checks++;
                return checks;
            } finally {
                try { ExportNaming.BeforePublishForTests = null; } catch { }
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
