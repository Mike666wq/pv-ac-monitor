using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace ExperimentMonitor {
    static class WorkflowTests {
        static void Check(bool ok,string name,List<string> checks) { if(!ok)throw new Exception("完整流程验收失败："+name); checks.Add(name); }
        static void Round(MonitorEngine engine,List<Point> points) {
            engine.StartOnce(1,1,points.Select(p=>p.name));
            DateTime until=DateTime.UtcNow.AddSeconds(15);
            while(engine.IsRunning&&DateTime.UtcNow<until)Thread.Sleep(10);
            if(engine.IsRunning)throw new TimeoutException("完整流程单轮采集未结束");
        }
        public static int Run(List<Point> points,string root) {
            var checks=new List<string>();
            string folder=Path.Combine(root,".testing","workflow-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(folder,"protocol.json"));
            string storage=Path.Combine(folder,"data","storage");
            DateTime before=DateTime.UtcNow.AddSeconds(-1);
            var captured=new List<Observation>();
            using(var engine=new MonitorEngine(points,folder)) {
                engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                engine.ObservationReceived+=o=>{lock(captured)captured.Add(o);};
                Check(!engine.IsConnected&&!engine.IsRunning,"启动不自动连接或采集",checks);
                engine.Connect(true,"SIM",9600);
                Thread.Sleep(50);
                Check(engine.IsConnected&&!engine.IsRunning&&captured.Count==0,"连接仅打开通道",checks);
                Round(engine,points);
                Check(engine.IsConnected&&captured.Count==37,"单轮完成保持连接且37点到达",checks);
                Round(engine,points);
                Check(engine.IsConnected&&captured.Count==74,"再次单轮保留连接",checks);
                engine.Disconnect();
                Check(!engine.IsConnected&&!engine.IsRunning,"断开释放连接",checks);
                engine.Connect(false,"TEST-TRANSPORT",9600); // Injected transport; no physical serial is opened.
                Round(engine,points);
                engine.Disconnect();
            }
            DateTime after=DateTime.UtcNow.AddSeconds(1);
            var filter=new HistoryFilter { Source="simulation",FromUtc=before,ToUtc=after,Points=points.Select(p=>p.name).ToArray() };
            var rows=ExperimentHistory.Query(storage,filter,1000);
            Check(rows.Count==74&&ExperimentHistory.Count(storage,filter)==74,"新分期记录查询与提交数量一致",checks);
            Check(rows.Select(r=>r.Session).Distinct().Count()==2,"反复采集会话唯一不合并轮次",checks);
            Check(rows.All(r=>r.RoundStartedUtc.HasValue&&r.RoundStartedUtc.Value<=r.Utc),"保存真实轮次开始和采样时间",checks);
            Check(ExperimentHistory.Count(storage,new HistoryFilter { Source="serial",FromUtc=before,ToUtc=after })==37,"实机与模拟来源分库存储",checks);
            using(var restarted=new MonitorEngine(points,folder)) {
                Check(!restarted.IsConnected&&!restarted.IsRunning&&ExperimentHistory.Count(storage,filter)==74,"重启只恢复配置且历史保持可读",checks);
            }
            var excel=ExperimentExport.ExportAsync(storage,filter,folder,"xlsx",null,CancellationToken.None).GetAwaiter().GetResult();
            Check(excel.Rows==74,"Excel导出包含完整74条样本",checks);
            using(var zip=ZipFile.OpenRead(Path.Combine(excel.OutputDirectory,excel.Files.First(f=>f.EndsWith(".xlsx"))))) {
                var workbook=new XmlDocument();using(var stream=zip.GetEntry("xl/workbook.xml").Open())workbook.Load(stream);
                var ns=new XmlNamespaceManager(workbook.NameTable);ns.AddNamespace("s","http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                Check(workbook.SelectNodes("//s:sheet",ns).Count==4,"工作簿包含宽表明细质量说明四表",checks);
                var wide=new XmlDocument();using(var stream=zip.GetEntry("xl/worksheets/sheet1.xml").Open())wide.Load(stream);
                Check(wide.SelectNodes("//s:sheetData/s:row",ns).Count==3,"宽表74点对应两轮不是74行",checks);
                var detail=new XmlDocument();using(var stream=zip.GetEntry("xl/worksheets/sheet2.xml").Open())detail.Load(stream);
                Check(detail.SelectNodes("//s:sheetData/s:row",ns).Count==75,"明细74样本完整保存",checks);
                var values=wide.SelectNodes("//s:row[@r='2']/s:c/s:v",ns).Cast<XmlNode>().Select(n=>n.InnerText).ToArray();
                Check(values.Contains("24")&&values.Any(n=>n.StartsWith("-0.075",StringComparison.Ordinal)),"宽表温度及负无功值为数值单元格",checks);
            }
            var csv=ExperimentExport.ExportAsync(storage,filter,folder,"csv",null,CancellationToken.None).GetAwaiter().GetResult();
            Check(csv.Rows==74&&csv.Files.Count(f=>f.EndsWith(".csv"))>=3,"CSV包含宽表明细质量记录",checks);
            string wideCsv=csv.Files.First(f=>f.Contains("宽表")&&f.EndsWith(".csv"));
            Check(File.ReadAllLines(Path.Combine(csv.OutputDirectory,wideCsv)).Length==3,"CSV宽表与Excel轮次数一致",checks);
            Check(Directory.Exists(excel.OutputDirectory)&&csv.OutputDirectory!=excel.OutputDirectory,"重复导出保留既有结果",checks);
            File.WriteAllText(Path.Combine(root,"workflow-test-result.json"),new JavaScriptSerializer().Serialize(new {passed=true,count=checks.Count,checks=checks,hardware_tested=false,physical_serial_opened=false,production_cloud_tested=false}));
            return checks.Count;
        }
    }
}
