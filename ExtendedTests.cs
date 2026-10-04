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
            Check(PointCatalog.Get("D6").Label=="功率因数"&&PointCatalog.Get("T1").Label=="水箱","labels from field screenshot",checks);
            string folder=Path.Combine(root,".testing","integrated-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(folder,"protocol.json"));
            var observed=new List<Observation>();
            using(var engine=new MonitorEngine(points,folder)) {
                engine.TransportFactory=(sim,port,baud)=>new FaultTransport();
                engine.ObservationReceived+=o=>{lock(observed)observed.Add(o);};
                engine.Connect(true,"SIM",9600);engine.StartOnce(2,1,points.Select(p=>p.name));
                var until=DateTime.UtcNow.AddSeconds(10);while(engine.IsRunning&&DateTime.UtcNow<until)Thread.Sleep(10);
                Check(!engine.IsRunning,"single round shuts down",checks);
            }
            Check(observed.Count==31&&observed.Any(o=>o.Slave==1&&o.Quality=="good"),"mains timeout does not block solar or PLC",checks);
            Check(observed.Count(o=>o.Quality=="timeout")==1&&observed.First(o=>o.Quality=="timeout").Number==null,"offline device does not create zero values",checks);
            Check(observed.First(o=>o.Point=="T1").Number==24,"T1 direct integer 24",checks);
            var rows=ExperimentHistory.Query(Path.Combine(folder,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-2),ToUtc=DateTime.UtcNow.AddMinutes(1)},100);
            Check(rows.Count==observed.Count&&rows.Any(r=>r.Quality=="timeout"),"engine observations committed including failure",checks);
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
            File.WriteAllText(Path.Combine(root,"extended-test-result.json"),new JavaScriptSerializer().Serialize(new{passed=true,checks=checks,count=checks.Count+count+recordsChecks+failureChecks+cloudChecks+coreChecks.Count+uiChecks+workflowChecks+trendChecks.Count-7,hardware_tested=false,production_cloud_tested=false}));
        }
        sealed class RecoveryTransport:IReadTransport {
            readonly Func<bool> fail;readonly SimulationTransport sim=new SimulationTransport();
            public RecoveryTransport(Func<bool> fail){this.fail=fail;}
            public byte[] Read(int s,int a,int c,int timeout,CancellationToken token,Action<string> log){if(s==6&&fail())throw new TimeoutException("first probe offline");return sim.Read(s,a,c,timeout,token,log);}
            public void Dispose(){sim.Dispose();}
        }
    }
}

