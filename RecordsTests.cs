using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;

namespace ExperimentMonitor {
    public static class RecordsTests {
        static void Require(bool value, string name) {
            if (!value) throw new Exception("记录页/导出验收失败:" + name);
        }

        static string Cell(XmlDocument doc, XmlNamespaceManager ns, string address) {
            string cell = "//s:sheetData/s:row/s:c[@r='" + address + "']";
            XmlNode node = doc.SelectSingleNode(cell + "/s:v | " + cell + "/s:is/s:t", ns);
            return node == null ? null : node.InnerText;
        }

        static XmlDocument Sheet(ZipArchive archive, string name) {
            var doc = new XmlDocument();
            using (var stream = archive.GetEntry("xl/worksheets/" + name).Open()) doc.Load(stream);
            return doc;
        }

        public static int Run() {
            string root = Path.Combine(Path.GetTempPath(), "experiment-record-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            int checks = 0;
            int previousVolumeSize = ExperimentExport.RowsPerVolume;
            try {
                DateTime hiddenMillis = new DateTime(2026, 10, 4, 12, 34, 56, 789, DateTimeKind.Unspecified);
                DateTime expectedUtc = new DateTime(2026, 10, 4, 12, 34, 56, DateTimeKind.Local).ToUniversalTime();
                Require(HistoryPage.LocalDisplaySecondToUtc(hiddenMillis) == expectedUtc, "历史筛选使用界面可见秒精度转换UTC");
                Require(HistoryPage.NextLocalSecond(hiddenMillis) == new DateTime(2026, 10, 4, 12, 34, 57, DateTimeKind.Local), "跟随最新的结束上界为下一整秒");
                checks++;

                DateTime start = DateTime.UtcNow.AddMinutes(-3);
                var filter = new HistoryFilter {
                    Source = "simulation", FromUtc = start.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1),
                    Devices = new[] { "PLC" }, Points = new[] { "T0", "T1" }
                };
                using (var store = new ExperimentStore(root, "simulation", "records-test")) {
                    store.Ready.GetAwaiter().GetResult();
                    for (int round = 1; round <= 2; round++) {
                        foreach (string point in new[] { "T0", "T1" }) {
                            PointInfo meta = PointCatalog.Get(point);
                            bool failed = round == 2 && point == "T1";
                            double number = round == 1 ? 24.125 : 25.375;
                            store.Record(new Observation {
                                Point = point, Description = meta.Label, Device = "PLC", Source = "simulation",
                                Utc = start.AddSeconds(round * 10 + (point == "T0" ? 1 : 2)),
                                RoundStartedUtc = start.AddSeconds(round * 10), SessionId = store.SessionId, Round = round,
                                Slave = 1, Address = 125, Count = 2, Unit = meta.Unit, Mode = meta.Mode,
                                Number = failed ? (double?)null : number, RawNumber = failed ? (double?)null : number,
                                Value = failed ? "等待超时" : number.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                                Quality = failed ? "timeout" : "good", Status = failed ? "超时" : "正常",
                                Tx = new byte[] { 1 }, Rx = new byte[] { 1, 2 }, Payload = new byte[] { 0, 1 }, ConfigVersion = "test-config"
                            }, round, meta.Unit);
                        }
                    }
                    store.FlushAsync().GetAwaiter().GetResult();
                }

                long roundCount;
                var rounds = ExperimentHistory.QueryRoundPage(root, filter, 0, 10, out roundCount, false);
                Require(roundCount == 2 && rounds.Count == 2 && rounds.All(round => round.Count == 2), "宽表查询每页返回完整采集轮");
                checks++;

                var xlsx = ExperimentExport.ExportAsync(root, filter, root, "xlsx", null, CancellationToken.None).GetAwaiter().GetResult();
                Require(xlsx.Rows == 4 && xlsx.Files.Length == 1, "Excel 单工作簿及记录总数");
                checks++;
                using (var archive = ZipFile.OpenRead(Path.Combine(xlsx.OutputDirectory, xlsx.Files[0]))) {
                    var workbook = new XmlDocument();
                    using (var stream = archive.GetEntry("xl/workbook.xml").Open()) workbook.Load(stream);
                    var ns = new XmlNamespaceManager(workbook.NameTable);
                    ns.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                    Require(workbook.SelectNodes("//s:sheet", ns).Count == 4, "四张业务工作表");
                    checks++;

                    foreach (string sheetName in new[] { "sheet1.xml", "sheet2.xml", "sheet3.xml" }) {
                        XmlDocument sheet = Sheet(archive, sheetName);
                        Require(sheet.SelectNodes("//s:sheetData/s:row", ns).Count == (sheetName == "sheet1.xml" ? 3 : 5), sheetName + "轮次数/明细数完整");
                        checks++;
                        if (sheetName == "sheet1.xml") {
                            Require(Cell(sheet, ns, "B1").Contains(PointCatalog.Get("T1").Label) && Cell(sheet, ns, "C1").Contains(PointCatalog.Get("T0").Label), "Logical point order matches the columns asserted below");
                            checks++;
                            Require(String.CompareOrdinal(Cell(sheet, ns, "A2"), Cell(sheet, ns, "A3")) < 0 && Cell(sheet, ns, "B2") == "24.125", "宽表按真实轮次时间升序且保留数值");
                            Require(Cell(sheet, ns, "B3") == null && Cell(sheet, ns, "C3") == "25.375", "超时测点在宽表留空且同轮有效测点保留原数值");
                            checks++;
                        }
                    }

                    XmlDocument quality = Sheet(archive, "sheet3.xml");
                    Require(quality.OuterXml.Contains("timeout") && quality.OuterXml.Contains("超时"), "质量表保留超时质量和状态说明");
                    checks++;
                    XmlDocument detail = Sheet(archive, "sheet2.xml");
                    Require(detail.OuterXml.Contains("等待超时"), "测点明细保留原始失败描述");
                    checks++;
                    XmlDocument info = Sheet(archive, "sheet4.xml");
                    Require(info.OuterXml.Contains("记录条数") && info.OuterXml.Contains("轮次开始时间"), "说明表写入实际导出元数据");
                    checks++;
                }

                var csv = ExperimentExport.ExportAsync(root, filter, root, "csv", null, CancellationToken.None).GetAwaiter().GetResult();
                Require(csv.Rows == 4 && csv.Files.Count(file => file.EndsWith(".csv")) == 3, "CSV 输出三套数据文件");
                checks++;
                foreach (string name in csv.Files) {
                    string body = File.ReadAllText(Path.Combine(csv.OutputDirectory, name));
                    Require(body.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3, "CSV 文件包含真实数据行:" + name);
                    checks++;
                }

                ExperimentExport.RowsPerVolume = 3;
                var split = ExperimentExport.ExportAsync(root, filter, root, "xlsx", null, CancellationToken.None).GetAwaiter().GetResult();
                Require(split.Files.Length == 2 && split.Rows == 4, "两点一轮时按整轮分卷");
                checks++;
                var splitRounds = new System.Collections.Generic.HashSet<string>();
                foreach (string name in split.Files) {
                    using (var archive = ZipFile.OpenRead(Path.Combine(split.OutputDirectory, name))) {
                        XmlDocument detail = Sheet(archive, "sheet2.xml");
                        var ns = new XmlNamespaceManager(detail.NameTable);
                        ns.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                        Require(detail.SelectNodes("//s:sheetData/s:row", ns).Count == 3, "分卷中同一轮明细不被切开");
                        XmlDocument wide = Sheet(archive, "sheet1.xml");
                        Require(wide.SelectNodes("//s:sheetData/s:row", ns).Count == 2, "宽表与明细在同一卷且该卷只含一整轮");
                        splitRounds.Add(Cell(wide, ns, "A2"));
                        checks++;
                    }
                }
                Require(splitRounds.Count == 2, "两个时间升序采集轮各自完整落在不同卷");
                checks++;
                return checks;
            } finally {
                ExperimentExport.RowsPerVolume = previousVolumeSize;
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
