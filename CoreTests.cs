using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExperimentMonitor {
    /// <summary>Core lifecycle/partition integration checks. Uses only deterministic in-process transports.</summary>
    public static class CoreTests {
        sealed class FakeTransport : IReadTransport {
            readonly Func<int, int, int, CancellationToken, byte[]> read;
            public int Reads;
            public bool Disposed;
            public FakeTransport(Func<int,int,int,CancellationToken,byte[]> read) { this.read = read; }
            public byte[] Read(int slave,int address,int count,int timeoutMs,CancellationToken token,Action<string> log) {
                Interlocked.Increment(ref Reads);
                if (Disposed) throw new ObjectDisposedException("FakeTransport");
                return read(slave,address,count,token);
            }
            public void Dispose() { Disposed = true; }
        }

        static byte[] Reply(int slave,int count) {
            byte[] body=new byte[3+count*2];body[0]=(byte)slave;body[1]=3;body[2]=(byte)(count*2);
            if(count==2){byte[] f=BitConverter.GetBytes(12.5f);Array.Reverse(f);Array.Copy(f,0,body,3,4);}
            else if(count==1){body[3]=0;body[4]=24;}
            return Modbus.WithCrc(body);
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static void WaitStopped(MonitorEngine engine){var sw=System.Diagnostics.Stopwatch.StartNew();while(engine.IsRunning&&sw.ElapsedMilliseconds<20000)Thread.Sleep(10);Require(!engine.IsRunning,"采集线程未在20秒内结束");}

        public static List<string> Run(List<Point> points,string applicationRoot) {
            if(points==null||points.Count==0)throw new ArgumentException("点表不能为空");
            var passed=new List<string>();
            string root=Path.Combine(applicationRoot,".core-tests-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);File.Copy(Path.Combine(applicationRoot,"protocol.json"),Path.Combine(root,"protocol.json"));
            try {
                DateTime start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
                string data=Path.Combine(root,"data");string state=Path.Combine(root,"settings","partition.json");
                using(var cycle=new PartitionCycle(data,"simulation",state)){
                    string p=cycle.ResolvePath(start);
                    Require(p.IndexOf("20260101",StringComparison.Ordinal)>=0,"分期文件名应使用连接时建立的有效锚点日期，不应回退到公元1年");
                    cycle.Configure(7,false,start.AddHours(1),false,0);
                    Require(cycle.PendingDays==7&&cycle.ActiveDays==30,"首次配置下个分期应持久化为待生效状态");
                    cycle.BeginRound(start.AddDays(29),1);cycle.CompleteRound(1,start.AddDays(29).AddMinutes(1));
                    cycle.BeginRound(start.AddDays(30),2);
                    Require(cycle.ActiveDays==7&&cycle.PendingDays==0,"到达分期边界后应切换到新周期");
                    cycle.CompleteRound(2,start.AddDays(30).AddMinutes(1));
                    cycle.BeginRound(start.AddDays(30).AddMinutes(2),3);
                    cycle.Configure(15,true,start.AddDays(30).AddMinutes(3),true,3);
                    Require(cycle.ActiveDays==7&&cycle.PendingDays==15,"立即新分期必须等待当前轮次完成");
                    cycle.CompleteRound(3,start.AddDays(30).AddMinutes(4));
                    Require(cycle.ActiveDays==15&&cycle.PendingDays==0,"当前轮次完成后立即分期设置应生效");
                }
                passed.Add("分期锚点、首个延后边界、立即切分和轮次边界");

                string persistedState=Path.Combine(root,"settings","partition-persist.json");
                using(var cycle=new PartitionCycle(Path.Combine(root,"persist-data"),"serial",persistedState)){
                    cycle.ResolvePath(start);cycle.Configure(15,false,start.AddHours(2),false,0);
                }
                using(var resumed=new PartitionCycle(Path.Combine(root,"persist-data"),"serial",persistedState)){
                    resumed.EnsureInitialized(start.AddDays(3));
                    Require(resumed.ActiveDays==30&&resumed.PendingDays==15,"重启后应恢复原周期和待生效周期");
                    resumed.BeginRound(start.AddDays(30),1);
                    Require(resumed.ActiveDays==15&&resumed.PendingDays==0,"重启后在原边界应切换待生效周期");
                    resumed.CompleteRound(1,start.AddDays(30).AddMinutes(1));
                }
                passed.Add("分期锚点与待生效策略重启持久化");

                string captureRoot=Path.Combine(root,"capture");Directory.CreateDirectory(captureRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(captureRoot,"protocol.json"));
                FakeTransport last=null;
                using(var engine=new MonitorEngine(points,captureRoot)){
                    engine.TransportFactory=(sim,port,baud)=>last=new FakeTransport((slave,address,count,token)=>Reply(slave,count));
                    engine.Connect(true,"模拟",9600);
                    Require(engine.IsConnected&&!engine.IsRunning&&last.Reads==0,"连接不应自动采集");
                    engine.StartOnce(2,2,points.Select(p=>p.name));WaitStopped(engine);
                    Require(engine.IsConnected&&last.Reads==points.Count,"单轮结束应保留连接并完成所有点");
                    Require(ExperimentHistory.Count(Path.Combine(captureRoot,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-1),ToUtc=DateTime.UtcNow.AddMinutes(1)})==points.Count,"所有点应本地落库");
                    var first=ExperimentHistory.QueryPage(Path.Combine(captureRoot,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-1),ToUtc=DateTime.UtcNow.AddMinutes(1)},0,10);
                    Require(first.Count>0&&first.All(o=>o.Round==1&&o.RoundStartedUtc.HasValue),"轮次和轮次起始时间应随记录落库");
                    string session1=first[0].Session;
                    engine.StartOnce(2,2,points.Select(p=>p.name));WaitStopped(engine);
                    var all=ExperimentHistory.QueryPage(Path.Combine(captureRoot,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-1),ToUtc=DateTime.UtcNow.AddMinutes(1)},0,100);
                    Require(all.Any(o=>o.Session!=session1&&o.Round==1),"每次采集会话的轮次必须重新编号且会话ID唯一");
                    Require(Directory.GetFiles(Path.Combine(captureRoot,"logs"),"session-*.log").Length==2,"同秒内重复会话必须生成独立日志");
                    engine.Disconnect();Require(!engine.IsConnected,"断开必须释放连接");
                }
                passed.Add("模拟连接不自动读取、单轮持久在线、唯一会话/日志及采集时间戳落库");

                if(points.Count>=2){
                    string cancelRoot=Path.Combine(root,"cancel-round");Directory.CreateDirectory(cancelRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(cancelRoot,"protocol.json"));
                    using(var engine=new MonitorEngine(points,cancelRoot)){
                        int calls=0;var secondEntered=new ManualResetEvent(false);
                        engine.TransportFactory=(sim,port,baud)=>new FakeTransport((slave,address,count,token)=>{
                            if(Interlocked.Increment(ref calls)==2){secondEntered.Set();while(!token.WaitHandle.WaitOne(10)){}token.ThrowIfCancellationRequested();}
                            return Reply(slave,count);
                        });
                        engine.Connect(true,"模拟",9600);engine.StartOnce(2,2,points.Take(2).Select(p=>p.name));
                        Require(secondEntered.WaitOne(3000),"取消测试未进入第二个点");engine.Stop();WaitStopped(engine);
                        var f=new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-1),ToUtc=DateTime.UtcNow.AddMinutes(1)};
                        var partial=ExperimentHistory.QueryPage(Path.Combine(cancelRoot,"data","storage"),f,0,10);
                        Require(partial.Count==1&&partial[0].Round==1,"中途取消应保留已完成读数且不生成未完成读数");
                        engine.StartOnce(2,2,points.Take(1).Select(p=>p.name));WaitStopped(engine);
                        var resumedRows=ExperimentHistory.QueryPage(Path.Combine(cancelRoot,"data","storage"),f,0,10);
                        Require(resumedRows.Count==2&&resumedRows.Any(o=>o.Session!=partial[0].Session&&o.Round==1),"取消后再次采集应开始全新会话且轮次从1重新计数");
                        engine.Disconnect();
                    }
                    passed.Add("轮次中途取消排空已提交记录，重启采集使用独立会话");
                }

                string periodRoot=Path.Combine(root,"period");Directory.CreateDirectory(periodRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(periodRoot,"protocol.json"));
                using(var engine=new MonitorEngine(points,periodRoot)){
                    var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int reads=0;
                    engine.TransportFactory=(sim,port,baud)=>new FakeTransport((slave,address,count,token)=>{
                        if(Interlocked.Increment(ref reads)==1){entered.Set();while(!release.WaitOne(10))token.ThrowIfCancellationRequested();}
                        return Reply(slave,count);
                    });
                    engine.Connect(true,"模拟",9600);engine.Start(5,2,new[]{points[0].name});Require(entered.WaitOne(3000),"周期边界测试未进入首轮");
                    engine.SetPeriodSeconds(1);release.Set();var sw=System.Diagnostics.Stopwatch.StartNew();while(Volatile.Read(ref reads)<2&&sw.ElapsedMilliseconds<4000)Thread.Sleep(10);
                    Require(Volatile.Read(ref reads)>=2,"周期变更应在当前轮次完成后应用");engine.Stop();WaitStopped(engine);
                    Require(File.ReadAllText(Path.Combine(periodRoot,"logs","policy-events.jsonl")).Contains("period"),"周期变更必须留有策略记录");engine.Disconnect();
                }
                passed.Add("采集周期在轮次完成后应用并持久记录策略事件");

                string storeFailureRoot=Path.Combine(root,"store-failure");Directory.CreateDirectory(storeFailureRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(storeFailureRoot,"protocol.json"));
                using(var engine=new MonitorEngine(points,storeFailureRoot)){
                    FakeTransport fake=null;engine.TransportFactory=(sim,port,baud)=>fake=new FakeTransport((slave,address,count,token)=>Reply(slave,count));
                    engine.Connect(true,"模拟",9600);
                    string storage=Path.Combine(storeFailureRoot,"data","storage");File.WriteAllText(Path.Combine(storage,"simulation"),"block directory creation");
                    engine.StartOnce(2,2,new[]{points[0].name});WaitStopped(engine);
                    Require(!String.IsNullOrEmpty(engine.LastStoreError),"SQLite writer打开失败必须保留可见的存储错误");
                    Require(engine.IsConnected&&fake!=null,"本地记录故障不应伪报串口已断开");engine.Disconnect();
                }
                passed.Add("本地数据库写入失败停止采集并保留错误状态");

                string diagnosticRoot=Path.Combine(root,"diagnostic");Directory.CreateDirectory(diagnosticRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(diagnosticRoot,"protocol.json"));
                using(var engine=new MonitorEngine(points,diagnosticRoot)){
                    FakeTransport fake=null;var entered=new ManualResetEvent(false);
                    engine.TransportFactory=(sim,port,baud)=>fake=new FakeTransport((slave,address,count,token)=>{entered.Set();while(!token.WaitHandle.WaitOne(10)){}token.ThrowIfCancellationRequested();return Reply(slave,count);});
                    engine.Connect(true,"模拟",9600);Task<DiagnosticResult> diagnostic=engine.ReadDiagnosticAsync(1,0,1,"UINT16",5);
                    Require(entered.WaitOne(3000),"诊断请求未进入传输层");bool blocked=false;try{engine.StartOnce(2,2,new[]{points[0].name});}catch(InvalidOperationException){blocked=true;}Require(blocked,"诊断期间必须禁止开始采集");
                    engine.Disconnect();DiagnosticResult result=diagnostic.GetAwaiter().GetResult();Require(result.Status=="cancelled"&&!engine.IsConnected,"断开必须取消诊断并释放连接");
                    Require(ExperimentHistory.Count(Path.Combine(diagnosticRoot,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddDays(-1),ToUtc=DateTime.UtcNow.AddDays(1)})==0,"手动诊断不能进入正式历史记录");
                }
                passed.Add("诊断互斥、断开取消诊断、诊断数据不混入正式历史");

                string reopenRoot=Path.Combine(root,"reopen");Directory.CreateDirectory(reopenRoot);File.Copy(Path.Combine(root,"protocol.json"),Path.Combine(reopenRoot,"protocol.json"));
                using(var engine=new MonitorEngine(points,reopenRoot)){
                    int created=0;engine.TransportFactory=(sim,port,baud)=>{if(Interlocked.Increment(ref created)==1)return new FakeTransport((slave,address,count,token)=>{throw new TimeoutException("test timeout");});throw new IOException("test reopen failure");};
                    engine.Connect(true,"模拟",9600);engine.StartOnce(2,1,new[]{points[0].name});WaitStopped(engine);
                    Require(!engine.IsConnected,"串口重连失败后不得残留虚假已连接状态");
                }
                passed.Add("设备超时后的通道重开失败正确转为断开");

                string historyRoot=Path.Combine(captureRoot,"data","storage");var filter=new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddMinutes(-1),ToUtc=DateTime.UtcNow.AddMinutes(1)};
                var streamed=new List<StoredObservation>();ExperimentHistory.Stream(historyRoot,filter,streamed.Add,true,CancellationToken.None);
                Require(streamed.Count>=points.Count&&streamed.Zip(streamed.Skip(1),(a,b)=>a.Utc<=b.Utc).All(x=>x),"全范围历史流应升序并覆盖所有分区记录");
                long totalRounds;var roundPage=ExperimentHistory.QueryRoundPage(historyRoot,filter,0,10,out totalRounds);
                Require(totalRounds==2&&roundPage.Count==2&&roundPage.All(rows=>rows.Count==points.Count),"宽表分页应返回完整轮次，而不是拆开的测点行");
                var oneRound=ExperimentHistory.QueryRoundPage(historyRoot,filter,1,1,out totalRounds);
                Require(totalRounds==2&&oneRound.Count==1&&oneRound[0].Count==points.Count,"宽表分页按轮次偏移并保持完整点集");
                passed.Add("历史查询游标流式升序、完整轮次分页及嵌套新库");
                return passed;
            } finally { try{Directory.Delete(root,true);}catch{} }
        }
    }
}
