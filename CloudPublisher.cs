using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ExperimentMonitor {
    public sealed class CloudPublisherStatus { public string State,LastContactLocal,LeaseUntilLocal,LastUploadLocal,DiagnosticCategory,DiagnosticMessage; public bool LeaseValid; public long Accepted,Failed,QueuedDevices,QueuedPoints; }
    public sealed class CloudDiagnosticResult { public string Category,Message,CompletedLocal; public bool Success; public CloudHeartbeatReply Heartbeat; }
    [DataContract] public sealed class ExperimentCloudPoint {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="description")] public string Description;
        [DataMember(Name="unit")] public string Unit;
        [DataMember(Name="value")] public double? Value;
        [DataMember(Name="rawValue")] public double? RawValue;
        [DataMember(Name="displayValue")] public string DisplayValue;
        [DataMember(Name="quality")] public string Quality;
        [DataMember(Name="observedUtc")] public string ObservedUtc;
        [DataMember(Name="roundStartedUtc",EmitDefaultValue=false)] public string RoundStartedUtc;
        [DataMember(Name="acquisitionRound")] public long Round;
        [DataMember(Name="configVersion")] public string ConfigVersion;
        [DataMember(Name="addressZeroBased")] public int Address;
        [DataMember(Name="registerCount")] public int Count;
        [DataMember(Name="decodeMode")] public string Mode;
    }
    [DataContract] public sealed class ExperimentCloudSnapshot {
        [DataMember(Name="schemaVersion")] public int SchemaVersion=1;
        [DataMember(Name="module")] public string Module="experiment";
        [DataMember(Name="deviceId")] public string DeviceId;
        [DataMember(Name="connectionSessionId")] public string ConnectionSessionId;
        [DataMember(Name="acquisitionSessionId")] public string AcquisitionSessionId;
        [DataMember(Name="sequence")] public long Sequence;
        [DataMember(Name="capturedUtc")] public string CapturedUtc;
        [DataMember(Name="source")] public string Source;
        [DataMember(Name="equipmentId")] public string EquipmentId;
        [DataMember(Name="slave")] public int Slave;
        [DataMember(Name="points")] public ExperimentCloudPoint[] Points;
    }
    public sealed class CloudPublisher : IDisposable {
        sealed class Sample { public ExperimentCloudPoint Point; public long Tick; public double InitialAge; }
        sealed class Device { public string Source,Id,AcquisitionSession; public int Slave; public long Version; public long FrozenVersion; public string FrozenSession; public ExperimentCloudSnapshot Frozen; public Dictionary<string,Sample> Points=new Dictionary<string,Sample>(); }
        readonly object gate=new object(),backfillGate=new object(); readonly Func<CloudConfiguration> configuration; readonly ICloudTransport transport; readonly string historyRoot;
        readonly CancellationTokenSource stop=new CancellationTokenSource(); readonly Task worker;Task backfillTask=Task.FromResult(0);CancellationTokenSource backfillCancel;string backfillRunningSubscription;
        readonly Dictionary<string,Device> devices=new Dictionary<string,Device>(); readonly Dictionary<string,long> sent=new Dictionary<string,long>();
        string connectionSession=Guid.NewGuid().ToString("N"),acquisitionSession="",status="未启用",backfilledSubscription=null; long sequence,sentCount,failedCount; bool disposed;DateTime? lastContact,lastUpload,leaseUntilUtc;string diagnosticCategory="未测试",diagnosticMessage="尚未执行连接诊断";
        public string Status { get { lock(gate)return status+"；已确认 "+sentCount+"，失败 "+failedCount; } }
        public CloudPublisherStatus SnapshotStatus { get {lock(gate)return new CloudPublisherStatus{State=status,LastContactLocal=lastContact.HasValue?lastContact.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LeaseUntilLocal=leaseUntilUtc.HasValue?leaseUntilUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LastUploadLocal=lastUpload.HasValue?lastUpload.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LeaseValid=leaseUntilUtc.HasValue&&leaseUntilUtc.Value>DateTime.UtcNow,Accepted=sentCount,Failed=failedCount,QueuedDevices=devices.Count,QueuedPoints=devices.Values.Sum(d=>d.Points.Count),DiagnosticCategory=diagnosticCategory,DiagnosticMessage=diagnosticMessage};} }
        public event EventHandler StatusChanged;
        public CloudPublisher(Func<CloudConfiguration> configuration,ICloudTransport transport=null,string historyRoot=null) { if(configuration==null)throw new ArgumentNullException("configuration");this.configuration=configuration;this.transport=transport??new HttpsCloudTransport();this.historyRoot=String.IsNullOrWhiteSpace(historyRoot)?null:Path.GetFullPath(historyRoot);worker=Task.Run((Func<Task>)Run); }
        static void ValidateHeartbeatReply(CloudHeartbeatReply reply) {
            if(reply==null||reply.RequestedDevices==null||reply.RequestedDevices.Length>16||reply.LeaseSeconds<0||reply.LeaseSeconds>3600||reply.BackfillSeconds<0||reply.BackfillSeconds>300||(reply.LeaseSeconds>0&&String.IsNullOrWhiteSpace(reply.SubscriptionId))||reply.RequestedDevices.Any(x=>String.IsNullOrWhiteSpace(x)||x.Length>80)||reply.RequestedDevices.Distinct().Count()!=reply.RequestedDevices.Length)throw new InvalidDataException("租约或请求设备列表无效");
            if(reply.RequestedHistoryPoints!=null){if(reply.RequestedHistoryPoints.Length>64||reply.RequestedHistoryPoints.Distinct(StringComparer.Ordinal).Count()!=reply.RequestedHistoryPoints.Length)throw new InvalidDataException("历史趋势选择列表无效");foreach(string selection in reply.RequestedHistoryPoints){if(String.IsNullOrWhiteSpace(selection)||selection.Length>170)throw new InvalidDataException("历史趋势选择无效");int slash=selection.IndexOf('/');if(slash<=0||slash!=selection.LastIndexOf('/')||slash==selection.Length-1||!reply.RequestedDevices.Contains(selection.Substring(0,slash)))throw new InvalidDataException("历史趋势选择不属于当前请求设备");}}
        }
        public async Task<CloudDiagnosticResult> DiagnoseAsync(CancellationToken token) { CloudConfiguration c=configuration();var result=new CloudDiagnosticResult{CompletedLocal=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")};try{if(c==null||!c.HasValidEndpoint){result.Category="地址";result.Message="地址无效：请输入 HTTPS 服务根地址，不要填写 URL 凭据、查询参数或片段。";}else if(String.IsNullOrWhiteSpace(c.Token)){result.Category="认证";result.Message="认证未配置：请保存实验设备令牌。";}else{CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,token).ConfigureAwait(false);ValidateHeartbeatReply(reply);result.Heartbeat=reply;result.Category="接口响应";result.Message="HTTPS 心跳成功；租约 "+(reply.LeaseSeconds>0?reply.LeaseSeconds+" 秒":"未授予")+"，请求设备 "+reply.RequestedDevices.Length+" 台。此诊断不会上传测点快照。";lock(gate){lastContact=DateTime.UtcNow;diagnosticCategory=result.Category;diagnosticMessage=result.Message;}result.Success=true;}}catch(OperationCanceledException){result.Success=false;result.Category="已取消";result.Message="连接诊断已取消。";}catch(Exception e){result.Success=false;result.Category=DiagnosticCategory(e);result.Message=CloudNetworkErrors.Describe(e);CloudNetworkErrors.Write(e);}result.CompletedLocal=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");lock(gate){diagnosticCategory=result.Category;diagnosticMessage=result.Message;}return result; }
        static string DiagnosticCategory(Exception e){if(e is CloudHttpException){int s=((CloudHttpException)e).StatusCode;return s==401||s==403?"认证":"接口响应";}if(e is System.Security.Authentication.AuthenticationException)return "TLS证书";if(e is System.Net.WebException){var w=(System.Net.WebException)e;if(w.Status==System.Net.WebExceptionStatus.TrustFailure||w.Status==System.Net.WebExceptionStatus.SecureChannelFailure)return "TLS证书";if(w.Status==System.Net.WebExceptionStatus.NameResolutionFailure||w.Status==System.Net.WebExceptionStatus.ConnectFailure||w.Status==System.Net.WebExceptionStatus.Timeout)return "网络";}if(e is InvalidDataException||e is System.Runtime.Serialization.SerializationException)return "接口响应";if(e is ArgumentException||e is UriFormatException)return "地址";return "网络";}
        public void Publish(Observation o) {
            if(o==null||String.IsNullOrEmpty(o.Point)||String.IsNullOrEmpty(o.Device))return;
            CloudConfiguration c;try{c=configuration();}catch{return;}if(c==null||!c.Enabled||(o.Source=="simulation"&&!c.IncludeSimulation))return;
            lock(gate) {
                if(disposed)return;
                if(!String.Equals(acquisitionSession,o.SessionId??"",StringComparison.Ordinal)) { devices.Clear();sent.Clear();acquisitionSession=o.SessionId??"";connectionSession=Guid.NewGuid().ToString("N"); }
                string key=o.Source+":"+o.Device; Device d;
                if(!devices.TryGetValue(key,out d)) { if(devices.Count>=16)return; d=new Device{Source=o.Source,Id=o.Device,Slave=o.Slave,AcquisitionSession=o.SessionId};devices[key]=d; }
                if(d.Points.Count>=128&&!d.Points.ContainsKey(o.Point))return;
                string quality=String.IsNullOrEmpty(o.Quality)?(o.Number.HasValue?"good":"unknown"):o.Quality;
                bool valid=o.Number.HasValue&&!Double.IsNaN(o.Number.Value)&&!Double.IsInfinity(o.Number.Value);
                d.Points[o.Point]=new Sample{Tick=Stopwatch.GetTimestamp(),InitialAge=Math.Max(0,(DateTime.UtcNow-o.Utc.ToUniversalTime()).TotalSeconds),Point=new ExperimentCloudPoint{Id=o.Point,Description=o.Description,Unit=o.Unit??"",Value=valid?o.Number:null,RawValue=o.RawNumber.HasValue&&!Double.IsNaN(o.RawNumber.Value)&&!Double.IsInfinity(o.RawNumber.Value)?o.RawNumber:null,DisplayValue=o.Value,Quality=quality,ObservedUtc=o.Utc.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),RoundStartedUtc=o.RoundStartedUtc==DateTime.MinValue?null:o.RoundStartedUtc.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),Round=o.Round,ConfigVersion=o.ConfigVersion??"",Address=o.Address,Count=o.Count,Mode=o.Mode}};
                d.Version=++sequence;
            }
        }
        public void ResetCaptureSession() { lock(gate){devices.Clear();sent.Clear();connectionSession=Guid.NewGuid().ToString("N");acquisitionSession="";} }
        static string HistoricalSession(StoredObservation row){string value=String.IsNullOrWhiteSpace(row.Session)?"experiment-history-"+Path.GetFileNameWithoutExtension(row.Database??"unknown"):row.Session;return value.Length<=128?value:value.Substring(0,128);}
        void CancelBackfill()
        {
            CancellationTokenSource cancel;lock(backfillGate){cancel=backfillCancel;backfillRunningSubscription=null;}
            if(cancel!=null)try{cancel.Cancel();}catch(ObjectDisposedException){}
        }
        void ScheduleWarmBackfill(CloudConfiguration c,CloudHeartbeatReply reply,HashSet<string> requested,string subscription)
        {
            if(historyRoot==null||reply==null||reply.BackfillSeconds<=0||String.IsNullOrWhiteSpace(subscription))return;
            CancellationTokenSource previous=null,next=null;HashSet<string> requestedCopy=new HashSet<string>(requested,StringComparer.Ordinal);
            lock(backfillGate)
            {
                if(String.Equals(backfilledSubscription,subscription,StringComparison.Ordinal))return;
                if(String.Equals(backfillRunningSubscription,subscription,StringComparison.Ordinal)&&backfillTask!=null&&!backfillTask.IsCompleted)return;
                previous=backfillCancel;next=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);backfillCancel=next;backfillRunningSubscription=subscription;
                backfillTask=Task.Run(async delegate
                {
                    try{await Task.Delay(250,next.Token).ConfigureAwait(false);await WarmBackfillAsync(c,reply,requestedCopy,subscription,next.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException){if(stop.IsCancellationRequested)return;}
                    finally{lock(backfillGate){if(Object.ReferenceEquals(backfillCancel,next)){backfillCancel=null;backfillRunningSubscription=null;}}next.Dispose();}
                });
            }
            if(previous!=null)try{previous.Cancel();}catch(ObjectDisposedException){}
        }
        async Task WarmBackfillAsync(CloudConfiguration c,CloudHeartbeatReply reply,HashSet<string> requested,string subscription,CancellationToken token)
        {
            try
            {
                int seconds=Math.Min(300,reply.BackfillSeconds);DateTime toUtc=DateTime.UtcNow.AddSeconds(1),fromUtc=DateTime.UtcNow.AddSeconds(-seconds);List<StoredObservation> rows=new List<StoredObservation>();string[] devices=requested.ToArray();
                foreach(string source in c.IncludeSimulation?new[]{"serial","simulation"}:new[]{"serial"})
                {
                    if(reply.RequestedHistoryPoints==null){var filter=new HistoryFilter{Source=source,FromUtc=fromUtc,ToUtc=toUtc,Devices=devices};ExperimentHistory.Stream(historyRoot,filter,rows.Add,true,token);}
                    else foreach(string selection in reply.RequestedHistoryPoints){int slash=selection.IndexOf('/');string equipmentId=selection.Substring(0,slash),pointId=selection.Substring(slash+1);var filter=new HistoryFilter{Source=source,FromUtc=fromUtc,ToUtc=toUtc,Devices=new[]{equipmentId},Points=new[]{pointId}};ExperimentHistory.Stream(historyRoot,filter,rows.Add,true,token);}
                }
                rows=rows.GroupBy(x=>(x.Database??"")+"|"+x.Id.ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal).Select(g=>g.First()).ToList();
                List<ExperimentBackfillPoint> points=rows.OrderBy(x=>x.Utc).ThenBy(x=>x.Database,StringComparer.Ordinal).ThenBy(x=>x.Id).Select(x=>{string quality=String.IsNullOrWhiteSpace(x.Quality)?(x.Number.HasValue?"good":"unknown"):x.Quality;double? value=quality=="good"&&x.Number.HasValue&&!Double.IsNaN(x.Number.Value)&&!Double.IsInfinity(x.Number.Value)?x.Number:null;string session=HistoricalSession(x);return new ExperimentBackfillPoint{Source=x.Source,EquipmentId=x.Device,Id=x.Point,ConnectionSessionId=session,AcquisitionSessionId=session,ObservedUtc=x.Utc.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),Value=value,Quality=quality,Unit=x.Unit??"",ConfigVersion=String.IsNullOrWhiteSpace(x.ConfigVersion)?"legacy":x.ConfigVersion,AcquisitionRound=Math.Max(0,x.Round)};}).ToList();
                for(int offset=0;offset<points.Count;offset+=128){token.ThrowIfCancellationRequested();ExperimentBackfillPoint[] batch=points.Skip(offset).Take(128).ToArray();await transport.SendBackfillAsync(c,new ExperimentBackfillPost{SubscriptionId=subscription,SchemaVersion=1,Module="experiment",Points=batch},token).ConfigureAwait(false);if(offset+128<points.Count)await Task.Delay(50,token).ConfigureAwait(false);}
                lock(backfillGate){if(!token.IsCancellationRequested&&String.Equals(backfillRunningSubscription,subscription,StringComparison.Ordinal))backfilledSubscription=subscription;}
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){CloudNetworkErrors.Write(e);Update("在线；5分钟历史预热失败，实时采集继续");}
        }
        void Update(string text) { bool changed;lock(gate){changed=status!=text;status=text;}if(changed){EventHandler h=StatusChanged;if(h!=null)try{h(this,EventArgs.Empty);}catch{}} }
        async Task Run() {
            Stopwatch clock=Stopwatch.StartNew();long heartbeatAt=0,uploadAt=0,leaseUntil=0;int backoff=1;string subscription="",activeSignature=null;HashSet<string> requested=new HashSet<string>();
            while(!stop.IsCancellationRequested) {
                CloudConfiguration c=null;try{c=configuration();}catch{}
                if(c==null||!c.Enabled){CancelBackfill();lock(backfillGate)backfilledSubscription=null;leaseUntil=0;subscription="";activeSignature=null;lock(gate){devices.Clear();sent.Clear();leaseUntilUtc=null;}Update("未启用");await Delay(250);continue;}
                string signature=c.DeviceId+"|"+c.Endpoint+"|"+c.Token+"|"+c.IncludeSimulation;
                if(activeSignature!=signature){CancelBackfill();lock(backfillGate)backfilledSubscription=null;heartbeatAt=0;uploadAt=0;leaseUntil=0;subscription="";requested.Clear();lock(gate){sent.Clear();leaseUntilUtc=null;if(activeSignature!=null){devices.Clear();connectionSession=Guid.NewGuid().ToString("N");}}activeSignature=signature;}
                if(!c.HasValidEndpoint||String.IsNullOrWhiteSpace(c.Token)){Update(c.SavedTokenUnreadable?"凭据无法解密，请重新填写令牌":"等待 HTTPS 地址和设备令牌");await Delay(250);continue;}
                bool uploading=false;
                try {
                    if(clock.ElapsedMilliseconds>=heartbeatAt) {
                        CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,stop.Token).ConfigureAwait(false);lock(gate)lastContact=DateTime.UtcNow;
                        ValidateHeartbeatReply(reply);
                        if(subscription!=reply.SubscriptionId)lock(gate)sent.Clear();subscription=reply.SubscriptionId;requested=new HashSet<string>(reply.RequestedDevices,StringComparer.Ordinal);
                        leaseUntil=clock.ElapsedMilliseconds+reply.LeaseSeconds*1000L;lock(gate)leaseUntilUtc=reply.LeaseSeconds>0?DateTime.UtcNow.AddSeconds(reply.LeaseSeconds):(DateTime?)null;heartbeatAt=clock.ElapsedMilliseconds+(reply.LeaseSeconds>0?Math.Min(15000,Math.Max(250,reply.LeaseSeconds*500)):15000);if(reply.LeaseSeconds>0&&!String.IsNullOrWhiteSpace(subscription))ScheduleWarmBackfill(c,reply,requested,subscription);else CancelBackfill();
                    }
                    if(clock.ElapsedMilliseconds<leaseUntil&&!String.IsNullOrWhiteSpace(subscription)&&clock.ElapsedMilliseconds>=uploadAt) {
                        ExperimentCloudSnapshot snapshot=null;string selectedKey=null,selectedSession=null;long version=0;
                        lock(gate) {
                            foreach(var entry in devices.OrderBy(x=>x.Value.Version)) {
                                Device d=entry.Value;long done;if(!requested.Contains(d.Id)||(d.Source=="simulation"&&!c.IncludeSimulation)||(sent.TryGetValue(entry.Key,out done)&&done==d.Version))continue;
                                long now=Stopwatch.GetTimestamp();if(!d.Points.Values.Any(x=>x.InitialAge+(now-x.Tick)/(double)Stopwatch.Frequency<=30))continue;
                                selectedKey=entry.Key;version=d.Version;selectedSession=connectionSession;if(d.Frozen!=null&&d.FrozenVersion==version&&d.FrozenSession==connectionSession){snapshot=d.Frozen;break;}
                                var points=d.Points.Values.Select(x=> { var p=x.Point;bool stale=x.InitialAge+(now-x.Tick)/(double)Stopwatch.Frequency>30;return new ExperimentCloudPoint{Id=p.Id,Description=p.Description,Unit=p.Unit,Value=stale?null:p.Value,RawValue=stale?null:p.RawValue,DisplayValue=stale?null:p.DisplayValue,Quality=stale?"stale":p.Quality,ObservedUtc=p.ObservedUtc,RoundStartedUtc=p.RoundStartedUtc,Round=p.Round,ConfigVersion=p.ConfigVersion,Address=p.Address,Count=p.Count,Mode=p.Mode}; }).OrderBy(x=>x.Id).ToArray();
                                snapshot=new ExperimentCloudSnapshot{DeviceId=c.DeviceId,ConnectionSessionId=connectionSession,AcquisitionSessionId=d.AcquisitionSession,Sequence=version,CapturedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),Source=d.Source,EquipmentId=d.Id,Slave=d.Slave,Points=points};d.Frozen=snapshot;d.FrozenVersion=version;d.FrozenSession=connectionSession;break;
                            }
                        }
                        if(snapshot!=null) {
                            uploading=true;await transport.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId=subscription,Snapshot=snapshot},stop.Token).ConfigureAwait(false);
                            lock(gate){if(connectionSession==selectedSession){sent[selectedKey]=version;sentCount++;lastUpload=DateTime.UtcNow;}}backoff=1;uploadAt=clock.ElapsedMilliseconds+1000;Update("在线；已上传实验快照");
                        }else Update("在线；租约有效，等待新鲜数据");
                    }else Update(clock.ElapsedMilliseconds>=leaseUntil?"在线；无人观看或租约过期":"在线；观看租约有效");
                }catch(OperationCanceledException){if(stop.IsCancellationRequested)break;failedCount++;uploadAt=clock.ElapsedMilliseconds+backoff*1000L;heartbeatAt=uploadAt;backoff=Math.Min(60,backoff*2);Update("请求取消；本地采集继续");}
                catch(Exception e){lock(gate)failedCount++;CloudNetworkErrors.Write(e);CloudHttpException http=e as CloudHttpException;bool auth=http!=null&&(http.StatusCode==401||http.StatusCode==403);if(http!=null&&http.StatusCode==409){CancelBackfill();leaseUntil=0;subscription="";lock(gate)leaseUntilUtc=null;heartbeatAt=clock.ElapsedMilliseconds+500;uploadAt=heartbeatAt;Update("观看租约失效，重新请求心跳");continue;}long next=clock.ElapsedMilliseconds+(auth?60000:backoff*1000L);if(uploading)uploadAt=next;else heartbeatAt=next;if(auth){CancelBackfill();leaseUntil=0;heartbeatAt=next;lock(gate)leaseUntilUtc=null;}backoff=Math.Min(60,backoff*2);Update(CloudNetworkErrors.Describe(e)+"；本地采集继续");}
                await Delay(250);
            }
        }
        async Task Delay(int ms){try{await Task.Delay(ms,stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;}CancelBackfill();stop.Cancel();try{worker.GetAwaiter().GetResult();}catch{}Task pending;lock(backfillGate)pending=backfillTask;try{if(pending!=null)pending.GetAwaiter().GetResult();}catch{}stop.Dispose();}
    }
}



