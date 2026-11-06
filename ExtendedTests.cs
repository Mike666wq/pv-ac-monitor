using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
namespace ExperimentMonitor {
    static class ExtendedTests {
        static void Check(bool value,string name,List<string> checks){if(!value)throw new Exception("扩展检查失败："+name);checks.Add(name);}
        sealed class FaultTransport:IReadTransport {
            readonly SimulationTransport inner=new SimulationTransport();
            public byte[] Read(int slave,int address,int count,int timeout,CancellationToken token,Action<string> log){if(slave==6)throw new TimeoutException("simulated offline");return inner.Read(slave,address,count,timeout,token,log);}
            public void Dispose(){inner.Dispose();}
        }
        public static void Preview(DashboardForm form,List<Point> points,string path) {
            using(var sim=new SimulationTransport())foreach(var p in points){var info=PointCatalog.Get(p);var o=Collector.Read(sim,p,info.Mode,"simulation",1000,CancellationToken.None,s=>{});o.Unit=info.Unit;form.UpdateObservation(o);}
            form.CapturePreview(path);
        }
        public static void Run(List<Point> points,string root) {
            var checks=new List<string>();
            var coreChecks=CoreTests.Run(points,root);
            File.WriteAllText(Path.Combine(root,"core-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,count=coreChecks.Count,checks=coreChecks,hardware_tested=false}));
            checks.Add("Core lifecycle / partition integration: "+coreChecks.Count);
            int count=StorageTests.Run();checks.Add("SQLite / XLSX / CSV integration: "+count);
            int recordsChecks=RecordsTests.Run(),failureChecks=ExportFailureTests.Run(points,root);checks.Add("Round-based history / export records: "+recordsChecks);
            File.WriteAllText(Path.Combine(root,"records-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,storageChecks=count,recordsChecks=recordsChecks,failureChecks=failureChecks,count=count+recordsChecks+failureChecks,hardware_tested=false}));
            int cloudChecks=CloudSelfTests.Run();File.WriteAllText(Path.Combine(root,"cloud-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,count=cloudChecks,production_cloud_tested=false}));checks.Add("Cloud lease / credentials / retry / HTTP integration: "+cloudChecks);
            var trendChecks=TrendTests.Run();File.WriteAllText(Path.Combine(root,"trend-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,count=trendChecks.Count,checks=trendChecks,hardware_tested=false,production_cloud_tested=false}));checks.Add("Trend compression / continuity regression: "+trendChecks.Count);
            var controlChecks=ControlTests.Run(points,root);File.WriteAllText(Path.Combine(root,"control-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,count=controlChecks.Count,checks=controlChecks,hardware_tested=false}));checks.Add("Control write path / gates / readback / audit: "+controlChecks.Count);
            // 2026-11-06 点语义裁决：D6 是功率因数（力控 DESC 写错）；T1 的身份未裁决，
            // 既不能再叫"水箱"（推测），也不能叫"相变2"（那个名字属于 D2206）。
            Check(PointCatalog.Get("D6").Label=="功率因数"&&PointCatalog.Get("T1").Label=="主机罐温度","labels from field screenshot",checks);
            Check(PointCatalog.Get("D2206").Label=="温度4","D2206 用回力控原名温度4",checks);
            Check(PointCatalog.Get("D2212").Label.IndexOf("频率")<0&&PointCatalog.Get("D2212").Label.IndexOf("负载")<0,"D2212 标签里的频率/负载已删除（它不是测量量）",checks);
            Check(PointCatalog.Get("ZI6682").Unit=="A"&&PointCatalog.Get("ZI6682").UnitQualifier=="二次侧","限定语从单位串里拆出来",checks);
            Check(PointCatalog.Get("ZW66822").Unit=="W"&&PointCatalog.Get("ZW66822").Label=="直流有功功率","直流表 0x000F 是有功功率，单位 W",checks);
            Check(PointCatalog.Get("T1").SemanticConfirmed==false&&PointCatalog.Get("D1").SemanticConfirmed,"未裁决点与已裁决点在点表里可区分",checks);
            Check(PointCatalog.Version.StartsWith("catalog-4-")&&PointCatalog.Version.Length>18,"点表版本是内容指纹而不是手写常量",checks);
            string folder=Path.Combine(root,".testing","integrated-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(folder,"protocol.json"));
            var observed=new List<Observation>();
            using(var engine=new MonitorEngine(points,folder)) {
                engine.TransportFactory=(sim,port,baud)=>new FaultTransport();
                engine.ObservationReceived+=o=>{lock(observed)observed.Add(o);};
                engine.Connect(true,"SIM",9600);engine.StartOnce(2,1,points.Select(p=>p.name));
                var until=DateTime.UtcNow.AddSeconds(10);while(engine.IsRunning&&DateTime.UtcNow<until)Thread.Sleep(10);
                Check(!engine.IsRunning,"single round shuts down",checks);
            }
            // 2026-11-06 修订：每轮每个点都必须有一行。
            // 旧断言锁死"31 行"（= 37 − 6），那个 6 正是站内超时后被 break 掉、
            // 从此既不落库也不进统计的直流表其余点。现在必须是 37 行齐全：
            // 失败的点记 timeout，后面同站的点记 skipped（数值列为空，绝不是 0）。
            Check(observed.Count==points.Count&&observed.Any(o=>o.Slave==1&&Quality.IsGood(o.Quality)),"mains timeout does not block solar or PLC",checks);
            Check(observed.Count(o=>o.Quality==Quality.Timeout)==1&&observed.First(o=>o.Quality==Quality.Timeout).Number==null,"offline device does not create zero values",checks);
            var skipped=observed.Where(o=>o.Quality==Quality.Skipped).ToList();
            Check(skipped.Count==points.Count(p=>p.binding.slave==6)-1&&skipped.All(o=>!o.Number.HasValue),"offline station still reports every point as unread",checks);
            Check(observed.Select(o=>o.Point).Distinct().Count()==points.Count,"every point produces exactly one row per round",checks);
            Check(observed.First(o=>o.Point=="T1").Number==24,"T1 direct integer 24",checks);
            var rows=ExperimentHistory.Query(Path.Combine(folder,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-2),ToUtc=DateTime.UtcNow.AddMinutes(1)},100);
            Check(rows.Count==observed.Count&&rows.Any(r=>r.Quality==Quality.Timeout),"engine observations committed including failure",checks);
            Check(rows.Count(r=>r.Quality==Quality.Skipped)==skipped.Count&&rows.Where(r=>r.Quality==Quality.Skipped).All(r=>r.Number==null),"unread points are persisted without a value",checks);
            using(var restored=new ManualResetEventSlim())using(var engine=new MonitorEngine(points,folder)) {
                int mainsAttempts=0;
                engine.TransportFactory=(sim,port,baud)=>new RecoveryTransport(()=>Interlocked.Increment(ref mainsAttempts)==1);
                engine.ObservationReceived+=o=>{if(o.Slave==6&&o.Quality=="good")restored.Set();};
                engine.Connect(true,"SIM",9600);engine.Start(1,1,points.Select(p=>p.name));
                Check(restored.Wait(10000),"offline device automatically returns after probe",checks);
                engine.Stop();Check(!engine.IsRunning,"continuous capture cancellation drains writer",checks);
            }
            using(var form=new DashboardForm(points,folder)) {
                form.AttachHistoryPage(new HistoryPage(points,folder,form.Engine));
                form.AttachCloudPage(new CloudPage(folder,form.Engine));
                Preview(form,points,Path.Combine(folder,"ui.png"));
                Check(File.Exists(Path.Combine(folder,"ui.png")),"new dashboard hidden render",checks);
            }
            int uiChecks=UiLayoutTests.Run(points,root);
            int workflowChecks=WorkflowTests.Run(points,root);
            checks.Add("UI layout and screenshots: "+uiChecks);
            checks.Add("Fresh-directory end-to-end workflow: "+workflowChecks);
            File.WriteAllText(Path.Combine(root,"extended-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,checks=checks,count=checks.Count+count+recordsChecks+failureChecks+cloudChecks+coreChecks.Count+uiChecks+workflowChecks+trendChecks.Count+controlChecks.Count-7,hardware_tested=false,production_cloud_tested=false}));
        }
        sealed class RecoveryTransport:IReadTransport {
            readonly Func<bool> fail;readonly SimulationTransport sim=new SimulationTransport();
            public RecoveryTransport(Func<bool> fail){this.fail=fail;}
            public byte[] Read(int s,int a,int c,int timeout,CancellationToken token,Action<string> log){if(s==6&&fail())throw new TimeoutException("first probe offline");return sim.Read(s,a,c,timeout,token,log);}
            public void Dispose(){sim.Dispose();}
        }
    }
}

