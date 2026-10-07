using System;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization.Json;
namespace ExperimentMonitor {
    public static class CloudSelfTests {
        sealed class Fake : ICloudTransport {
            public int Heartbeats,Uploads,Backfills,BackfillAttempts;public int Lease=10,BackfillSeconds=0;public bool FailUpload,FailFirstBackfill,BlockBackfill;public string SubscriptionId="mock-lease";public string[] RequestedDevices=new[]{"PLC"},RequestedHistoryPoints=null;public CloudSnapshotPost Last;public ExperimentBackfillPost LastBackfill;public CloudHeartbeatReply ReplyOverride;readonly TaskCompletionSource<object> backfillRelease=new TaskCompletionSource<object>();
            public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration c,CancellationToken t){Interlocked.Increment(ref Heartbeats);return Task.FromResult(ReplyOverride??new CloudHeartbeatReply{SubscriptionId=Lease>0?SubscriptionId:"",LeaseSeconds=Lease,RequestedDevices=RequestedDevices,RequestedHistoryPoints=RequestedHistoryPoints,BackfillSeconds=BackfillSeconds});}
            public Task SendSnapshotAsync(CloudConfiguration c,CloudSnapshotPost p,CancellationToken t){if(FailUpload)throw new IOException("controlled mock failure");Last=p;Interlocked.Increment(ref Uploads);return Task.FromResult(0);}
            public Task SendBackfillAsync(CloudConfiguration c,ExperimentBackfillPost p,CancellationToken t){LastBackfill=p;int attempt=Interlocked.Increment(ref BackfillAttempts);if(FailFirstBackfill&&attempt==1){var failed=new TaskCompletionSource<object>();failed.SetException(new IOException("controlled backfill failure"));return failed.Task;}if(BlockBackfill)return CompleteBackfillAfterRelease(p,t);Interlocked.Increment(ref Backfills);return Task.FromResult(0);}
            async Task CompleteBackfillAfterRelease(ExperimentBackfillPost p,CancellationToken t){using(t.Register(delegate{backfillRelease.TrySetCanceled();})){await backfillRelease.Task.ConfigureAwait(false);}LastBackfill=p;Interlocked.Increment(ref Backfills);}
            public void ReleaseBackfill(){backfillRelease.TrySetResult(null);}
        }
        static void Require(bool yes,string message){if(!yes)throw new Exception("云端测试："+message);}
        static Observation Sample(string source,DateTime utc){return new Observation{Point="T0",Description="相变1",Device="PLC",Slave=2,Address=125,Count=2,Source=source,Utc=utc,RoundStartedUtc=utc.AddSeconds(-1),Number=25.125,RawNumber=25.125,Value="25.125",Quality="good",Unit="℃",SessionId="test-capture",Round=1,ConfigVersion="test-v1",Mode="FLOAT ABCD"};}
        static void Wait(Func<bool> f){DateTime until=DateTime.UtcNow.AddSeconds(4);while(!f()&&DateTime.UtcNow<until)Thread.Sleep(20);Require(f(),"等待 mock 超时");}
        static void HttpFixture(CloudConfiguration c,string reply,int status,bool snapshot,out string body,out string auth,out string route) {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            string capturedBody=null,capturedAuth=null,capturedRoute=null;
            var server=Task.Run(()=> { try { using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream()) {
                client.ReceiveTimeout=6000;var bytes=new MemoryStream();int ch;string headers="";
                while((ch=stream.ReadByte())>=0){bytes.WriteByte((byte)ch);if(bytes.Length>16384)throw new IOException("mock header too large");headers=Encoding.ASCII.GetString(bytes.ToArray());if(headers.EndsWith("\r\n\r\n"))break;}
                int length=0;foreach(string line in headers.Split(new[]{"\r\n"},StringSplitOptions.None)){if(line.StartsWith("POST "))capturedRoute=line;if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=Int32.Parse(line.Substring(15).Trim());if(line.StartsWith("Authorization:",StringComparison.OrdinalIgnoreCase))capturedAuth=line.Substring(14).Trim();}
                byte[] payload=new byte[length];int at=0;while(at<length){int n=stream.Read(payload,at,length-at);if(n==0)break;at+=n;}capturedBody=Encoding.UTF8.GetString(payload,0,at);
                byte[] response=Encoding.UTF8.GetBytes(reply);byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" Mock\r\nContent-Type: application/json\r\n"+(status==302?"Location: http://127.0.0.1:"+port+"/redirect\r\n":"")+"Content-Length: "+response.Length+"\r\nConnection: close\r\n\r\n");stream.Write(header,0,header.Length);stream.Write(response,0,response.Length);
            }}catch(IOException){}catch(SocketException){} });
            try {
                var http=new HttpsCloudTransport(u=>{var r=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+u.AbsolutePath);r.ServicePoint.Expect100Continue=false;return r;},5000,true);
                if(snapshot)http.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId="local",Snapshot=new ExperimentCloudSnapshot{DeviceId=c.DeviceId,EquipmentId="PLC",Points=new[]{new ExperimentCloudPoint{Id="T0",Description="相变1",Value=25.125,Quality="good",RoundStartedUtc=DateTime.UtcNow.AddSeconds(-1).ToString("o",CultureInfo.InvariantCulture)}}}},CancellationToken.None).GetAwaiter().GetResult();
                else http.HeartbeatAsync(c,CancellationToken.None).GetAwaiter().GetResult();
            }finally{listener.Stop();server.Wait(6500);body=capturedBody;auth=capturedAuth;route=capturedRoute;}
        }
        public static int Run() {
            int checks=0;string root=Path.Combine(Environment.CurrentDirectory,"experiment-cloud-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var c=CloudConfiguration.Load(Path.Combine(root,"cloud.txt"));c.Enabled=true;c.Endpoint="https://example.invalid";c.Token="mock-token-no-production";c.Alias="实验室";c.Save();var loaded=CloudConfiguration.Load(c.SavePath);Require(loaded.DeviceId==c.DeviceId&&loaded.Token==c.Token&&loaded.Alias==c.Alias&&!File.ReadAllText(c.SavePath).Contains(c.Token),"DPAPI 或身份持久化");checks++;
            loaded.Endpoint="http://127.0.0.1";Require(!loaded.HasValidEndpoint,"生产 HTTP 必须拒绝");loaded.Endpoint="https://user:pass@example.invalid";Require(!loaded.HasValidEndpoint,"URL 凭据必须拒绝");checks++;
            var f=new Fake{ReplyOverride=new CloudHeartbeatReply{SubscriptionId="bad-lease",LeaseSeconds=10,RequestedDevices=null}};using(var p=new CloudPublisher(()=>c,f)){CloudDiagnosticResult diagnostic=p.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();Require(!diagnostic.Success&&diagnostic.Category=="接口响应","无效请求设备列表不得报告连接诊断成功");checks++;}
            f=new Fake();using(var p=new CloudPublisher(()=>c,f)){p.Publish(Sample("serial",DateTime.UtcNow));Wait(()=>f.Uploads==1);Require(f.Last.Snapshot.Module=="experiment"&&f.Last.Snapshot.Points[0].Value==25.125&&f.Last.Snapshot.EquipmentId=="PLC", "独立实验契约");Require(!String.IsNullOrEmpty(f.Last.Snapshot.Points[0].RoundStartedUtc),"快照测点保留真实轮次开始时间");string json;using(var ms=new MemoryStream()){new DataContractJsonSerializer(typeof(CloudSnapshotPost)).WriteObject(ms,f.Last);json=Encoding.UTF8.GetString(ms.ToArray());}Require(json.Contains("\"module\":\"experiment\"")&&json.Contains("roundStartedUtc")&&!json.Contains("Pack"),"独立契约 JSON 含轮次开始时间且不得伪装 BMS Pack");checks++;}
            f=new Fake{Lease=0};using(var p=new CloudPublisher(()=>c,f)){p.Publish(Sample("serial",DateTime.UtcNow));Wait(()=>f.Heartbeats>0);Thread.Sleep(300);Require(f.Uploads==0,"无租约不上传");checks++;}
            f=new Fake();using(var p=new CloudPublisher(()=>c,f)){p.Publish(Sample("simulation",DateTime.UtcNow));Wait(()=>f.Heartbeats>0);Thread.Sleep(300);Require(f.Uploads==0,"默认模拟不上云");checks++;}
            f=new Fake();using(var p=new CloudPublisher(()=>c,f)){p.Publish(Sample("serial",DateTime.UtcNow.AddMinutes(-1)));Wait(()=>f.Heartbeats>0);Thread.Sleep(300);Require(f.Uploads==0,"过期样本不上云");checks++;}
            f=new Fake{FailUpload=true};using(var p=new CloudPublisher(()=>c,f)){p.Publish(Sample("serial",DateTime.UtcNow));Wait(()=>p.Status.Contains("失败 1"));f.FailUpload=false;Wait(()=>f.Uploads>0);Require(f.Last.Snapshot.Sequence==1,"失败重试保留序号");checks++;}
            string historyRoot=Path.Combine(root,"history");var historical=Sample("serial",DateTime.UtcNow.AddMinutes(-1));historical.Tx=new byte[0];historical.Rx=new byte[0];historical.Payload=new byte[0];var historicalOther=Sample("serial",DateTime.UtcNow.AddSeconds(-50));historicalOther.Point="T1";historicalOther.Description="相变2";historicalOther.Tx=new byte[0];historicalOther.Rx=new byte[0];historicalOther.Payload=new byte[0];using(var historyStore=new ExperimentStore(historyRoot,"serial","{}",30)){historyStore.Ready.GetAwaiter().GetResult();historyStore.Record(historical,1,"℃");historyStore.Record(historicalOther,1,"℃");historyStore.FlushAsync().GetAwaiter().GetResult();}
            f=new Fake{Lease=1,BackfillSeconds=300,RequestedHistoryPoints=new[]{"PLC/T0"}};using(var p=new CloudPublisher(()=>c,f,historyRoot)){Wait(()=>f.Backfills==1);Require(f.LastBackfill!=null&&f.LastBackfill.Points.Length==1&&f.LastBackfill.Points[0].EquipmentId=="PLC"&&f.LastBackfill.Points[0].Id=="T0"&&f.LastBackfill.Points[0].Value==25.125,"5分钟历史预热只读取当前主趋势测点");Wait(()=>f.Heartbeats>=2);Require(f.Backfills==1,"同一观看订阅不得重复历史预热");f.SubscriptionId="mock-lease-2";Wait(()=>f.Backfills==2);Require(f.LastBackfill.SubscriptionId=="mock-lease-2","新订阅必须再次执行实验历史预热");checks++;}
            f=new Fake{Lease=1,BackfillSeconds=300,FailFirstBackfill=true};using(var p=new CloudPublisher(()=>c,f,historyRoot)){Wait(()=>f.BackfillAttempts>=2);Require(f.Backfills==1&&f.LastBackfill.Points.Length==2,"实验历史预热失败后必须在同一订阅重试；旧云端未下发测点选择时保持全设备兼容");checks++;}
            f=new Fake{Lease=10,BackfillSeconds=300,RequestedHistoryPoints=new[]{"PLC/T0"},BlockBackfill=true};using(var p=new CloudPublisher(()=>c,f,historyRoot)){p.Publish(Sample("serial",DateTime.UtcNow));Wait(()=>f.BackfillAttempts>0);Require(f.Uploads==1,"历史回填阻塞时实时快照仍应优先上传");f.ReleaseBackfill();Wait(()=>f.Backfills==1);checks++;}
            string body,auth,route;
            HttpFixture(c,"{\"subscriptionId\":\"local\",\"leaseSeconds\":0,\"requestedDevices\":[]}",200,false,out body,out auth,out route);
            Require(auth=="Bearer "+c.Token&&route.Contains("/api/experiment/heartbeat")&&body.Contains("\"module\":\"experiment\""),"实际 HTTP 心跳契约");checks++;
            HttpFixture(c,"{\"accepted\":true}",200,true,out body,out auth,out route);Require(route.Contains("/api/experiment/snapshots")&&body.Contains("\"points\"")&&body.Contains("25.125")&&body.Contains("roundStartedUtc")&&!body.Contains("Pack"),"实际 HTTP 快照含轮次开始时间且保持独立契约");checks++;
            bool rejected=false;try{HttpFixture(c,"{}",302,false,out body,out auth,out route);}catch(CloudHttpException e){rejected=e.StatusCode==302;}Require(rejected,"重定向必须拒绝");checks++;
            rejected=false;try{HttpFixture(c,new string('x',65537),200,false,out body,out auth,out route);}catch(InvalidDataException){rejected=true;}Require(rejected,"超 64 KiB 响应必须拒绝");checks++;
            rejected=false;try{HttpFixture(c,"{\"accepted\":false}",200,true,out body,out auth,out route);}catch(InvalidDataException){rejected=true;}Require(rejected,"未确认快照必须失败");checks++;
            return checks;
        }
    }
}



