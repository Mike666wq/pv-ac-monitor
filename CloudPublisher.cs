using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Acme.Cloud;
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
        // 离线诊断：把本机看到的失败阶段与次数攒起来，随下一次心跳送出去。
        readonly DateTime startedUtc=DateTime.UtcNow;int consecutiveFailures;DateTime? lastSuccessUtc,lastRefreshUtc;string lastErrorStage,lastErrorCode,lastErrorAt;int lastErrorHttp,lastErrorCount;
        // 设备接入：云端是真相源，这份只是本地缓存 + 认领期凭据。
        // settingsDirectory 为 null 时整个接入能力关闭（自测与旧调用点因此一行都不用改）。
        CloudEnrollment enrollment;readonly string enrollmentPath;readonly object enrollmentGate=new object();
        // null = 云端还没说过这一项，按「允许」处理；收到配置后才有确定值。
        bool? uploadAllowed,allowSimulation;string deviceState;
        long lastEnrollTick;int enrollIntervalMs=5000;
        public string Status { get { lock(gate)return status+"；已确认 "+sentCount+"，失败 "+failedCount; } }
        public CloudPublisherStatus SnapshotStatus { get {lock(gate)return new CloudPublisherStatus{State=status,LastContactLocal=lastContact.HasValue?lastContact.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LeaseUntilLocal=leaseUntilUtc.HasValue?leaseUntilUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LastUploadLocal=lastUpload.HasValue?lastUpload.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—",LeaseValid=leaseUntilUtc.HasValue&&leaseUntilUtc.Value>DateTime.UtcNow,Accepted=sentCount,Failed=failedCount,QueuedDevices=devices.Count,QueuedPoints=devices.Values.Sum(d=>d.Points.Count),DiagnosticCategory=diagnosticCategory,DiagnosticMessage=diagnosticMessage};} }
        public event EventHandler StatusChanged;
        public CloudPublisher(Func<CloudConfiguration> configuration,ICloudTransport transport=null,string historyRoot=null,string settingsDirectory=null) {
            if(configuration==null)throw new ArgumentNullException("configuration");
            this.configuration=configuration;this.transport=transport??new HttpsCloudTransport();this.historyRoot=String.IsNullOrWhiteSpace(historyRoot)?null:Path.GetFullPath(historyRoot);
            HttpsCloudTransport https=this.transport as HttpsCloudTransport;
            if(https!=null)https.ClientReportProvider=BuildClientReport;
            if(!String.IsNullOrEmpty(settingsDirectory))
            {
                // 机器钥匙本地自生成、人不用看：它是「我是这台机器」的唯一凭据，
                // 与 device-id.txt 完全解耦（那是业务编号，绝不由这里生成或改写）。
                try
                {
                    enrollmentPath=Path.Combine(settingsDirectory,CloudEnrollmentStore.FileName);
                    enrollment=CloudEnrollmentStore.Load(enrollmentPath,CloudIdentity.LoadOrCreateMachineKey("ExperimentMonitorDemo",AppDomain.CurrentDomain.BaseDirectory));
                    uploadAllowed=enrollment.IsActive?enrollment.UploadAllowed:(bool?)null;
                    allowSimulation=enrollment.ProtocolVersion>0?enrollment.AllowSimulation:(bool?)null;
                    deviceState=enrollment.State;
                }
                catch(Exception e){CloudNetworkErrors.Write(e);}
            }
            if(https!=null)https.EnrollmentProvider=delegate{return enrollment;};
            worker=Task.Run((Func<Task>)Run);
        }
        // 给只读界面用的接入视图：只暴露字符串，不把可变对象交出去。
        public string EnrollmentState{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null)return "未启用接入";switch(e.State){case "active":return "已接入";case "pending":return "等待管理员批准";case "approved":return "已批准，正在激活";case "suspended":return "已被云端停用（本地上传暂停）";case "revoked":return "已被云端注销";case "rejected":return "接入被拒绝";default:return "未接入";}}}
        public string EnrollmentAlias{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;return e==null||String.IsNullOrEmpty(e.Alias)?"（等待云端分配）":e.Alias;}}
        public string EnrollmentAccess{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null||e.ProtocolVersion<=0)return "（等待云端下发）";List<string> parts=new List<string>();if(e.AllowedEquipment!=null&&e.AllowedEquipment.Length>0)parts.Add("仪器 "+String.Join("、",e.AllowedEquipment));parts.Add(e.AllowSimulation?"允许模拟数据":"仅真实设备");return String.Join(" · ",parts.ToArray());}}
        public string MachineKeyMasked{get{CloudEnrollment e;lock(enrollmentGate)e=enrollment;if(e==null||String.IsNullOrEmpty(e.MachineKey))return "—";string k=e.MachineKey;return k.Length<=10?k:k.Substring(0,7)+"…"+k.Substring(k.Length-4);}}
        /// <summary>清掉本地认领状态，让下一拍重新走认领流程。人依旧不需要填任何东西。</summary>
        public void ResetEnrollment()
        {
            lock(enrollmentGate)
            {
                if(enrollment==null)return;
                enrollment.ClaimSecret=null;enrollment.ClaimId=null;enrollment.State="unclaimed";enrollment.ProtocolVersion=0;
                try{CloudEnrollmentStore.Save(enrollment);}catch(Exception e){CloudNetworkErrors.Write(e);}
            }
            lock(gate){uploadAllowed=null;allowSimulation=null;deviceState="unclaimed";}
            lastEnrollTick=0;
        }
        static string NewClaimSecret()
        {
            byte[] buffer=new byte[32];
            using(RandomNumberGenerator rng=RandomNumberGenerator.Create())rng.GetBytes(buffer);
            return Convert.ToBase64String(buffer).Replace('+','-').Replace('/','_').TrimEnd('=');
        }
        /// <summary>认领请求只要「地址 + Bearer」两个信息，用内存副本承载，绝不写盘。</summary>
        static CloudConfiguration ClaimCarrier(CloudConfiguration c,string secret)
        {
            return new CloudConfiguration{DeviceId=c.DeviceId,Alias=c.Alias,Endpoint=c.Endpoint,Enabled=true,IncludeSimulation=c.IncludeSimulation,Token=secret,SavePath=c.SavePath};
        }
        /// <summary>把云端下发的配置与状态应用到运行时。protocolVersion 缺失 = 旧云端，一律不动。</summary>
        void ApplyDeviceReply(CloudHeartbeatReply reply,CloudConfiguration c)
        {
            if(reply==null||!reply.ProtocolVersion.HasValue)return;
            bool changed=false;
            if(!String.IsNullOrEmpty(reply.State)&&!String.Equals(deviceState,reply.State,StringComparison.Ordinal)){deviceState=reply.State;changed=true;}
            if(reply.UploadAllowed.HasValue&&uploadAllowed!=reply.UploadAllowed){uploadAllowed=reply.UploadAllowed;changed=true;}
            CloudEnrollment state;lock(enrollmentGate){state=enrollment;}
            if(state!=null)
            {
                if(reply.Config!=null)
                {
                    state.ApplyConfig(reply.Config,reply.ConfigVersion.HasValue?reply.ConfigVersion.Value:0);
                    allowSimulation=state.AllowSimulation;uploadAllowed=state.UploadAllowed;changed=true;
                }
                if(!String.IsNullOrEmpty(reply.State)&&!String.Equals(state.State,reply.State,StringComparison.Ordinal)){state.State=reply.State;changed=true;}
                ApplyCommands(reply.Commands,state,c);
                if(changed)try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception e){CloudNetworkErrors.Write(e);}
            }
        }
        /// <summary>命令按 commandId 去重，只认比上次更新的那条。</summary>
        void ApplyCommands(CloudDeviceCommand[] commands,CloudEnrollment state,CloudConfiguration c)
        {
            if(commands==null||commands.Length==0)return;
            foreach(CloudDeviceCommand command in commands)
            {
                if(command==null||String.IsNullOrEmpty(command.CommandId))continue;
                if(String.Equals(command.CommandId,state.LastAppliedCommandId,StringComparison.Ordinal))continue;
                string type=command.Type??"";
                if(type=="wipe-credentials"||type=="reclaim")
                {
                    state.ClaimSecret=null;state.ClaimId=null;state.State="revoked";state.ProtocolVersion=0;
                    state.LastAppliedCommandId=command.CommandId;
                    uploadAllowed=false;deviceState="revoked";
                    // 同时清掉设备令牌：否则客户端会一直拿着旧令牌心跳，
                    // 永远停在「已注销」而不会重新发起认领。
                    if(c!=null&&!String.IsNullOrEmpty(c.Token)){try{c.Token="";c.Save();}catch{}}
                }
                else if(type=="stop-upload"){uploadAllowed=false;state.LastAppliedCommandId=command.CommandId;}
                else if(type=="resume-upload"){uploadAllowed=true;state.LastAppliedCommandId=command.CommandId;}
                else {state.LastAppliedCommandId=command.CommandId;}
            }
        }
        /// <summary>没有设备令牌时走这里：首次发起认领、之后轮询；批准后拿到令牌就落盘并确认。</summary>
        async Task<bool> EnsureEnrolledAsync(CloudConfiguration c)
        {
            ICloudEnrollmentTransport enrollmentTransport=transport as ICloudEnrollmentTransport;
            CloudEnrollment state;lock(enrollmentGate){state=enrollment;}
            if(enrollmentTransport==null||state==null)return false;
            // 已注销也允许重新发起认领：令牌已被清空，说明确实是人工重新接入。
            long now=Stopwatch.GetTimestamp();
            if(lastEnrollTick!=0&&(now-lastEnrollTick)*1000.0/Stopwatch.Frequency<enrollIntervalMs)return false;
            lastEnrollTick=now;
            try
            {
                CloudClaimReply reply;
                if(String.IsNullOrEmpty(state.ClaimSecret)||String.IsNullOrEmpty(state.ClaimId))
                {
                    if(String.IsNullOrEmpty(state.MachineKey))state.MachineKey=CloudIdentity.LoadOrCreateMachineKey("ExperimentMonitorDemo",AppDomain.CurrentDomain.BaseDirectory);
                    state.ClaimSecret=NewClaimSecret();state.Module="experiment";
                    reply=await enrollmentTransport.ClaimAsync(ClaimCarrier(c,state.ClaimSecret),
                        new CloudClaimRequest{MachineKey=state.MachineKey,Module="experiment",ProtocolVersion=2,DesiredAlias=Environment.MachineName,Client=BuildClientReport()},stop.Token).ConfigureAwait(false);
                }
                else reply=await enrollmentTransport.ClaimStatusAsync(ClaimCarrier(c,state.ClaimSecret),state.ClaimId,stop.Token).ConfigureAwait(false);
                if(reply==null){Update("等待批准：云端未返回接入结果");return false;}
                if(!String.IsNullOrEmpty(reply.ClaimId))state.ClaimId=reply.ClaimId;
                if(!String.IsNullOrEmpty(reply.State))state.State=reply.State;
                if(!String.IsNullOrEmpty(reply.DeviceToken)&&!String.IsNullOrEmpty(reply.DeviceId))
                {
                    // 先落盘成功，再切换运行态：中途掉电不会留下「有令牌但云端不认」的状态。
                    string previousToken=c.Token,previousId=c.DeviceId;
                    c.Token=reply.DeviceToken;c.DeviceId=reply.DeviceId;
                    try{c.Save();}catch{c.Token=previousToken;c.DeviceId=previousId;throw;}
                    state.DeviceId=reply.DeviceId;state.State="active";state.ProtocolVersion=2;
                    if(reply.Config!=null)state.ApplyConfig(reply.Config,reply.ConfigVersion.HasValue?reply.ConfigVersion.Value:0);
                    state.ClaimSecret=null;
                    try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception e){CloudNetworkErrors.Write(e);}
                    allowSimulation=state.AllowSimulation;uploadAllowed=state.UploadAllowed;deviceState="active";
                    try{await enrollmentTransport.AcknowledgeAsync(c,new CloudAckRequest{ClaimId=state.ClaimId,AckType="activation",DeviceId=reply.DeviceId,ConfigVersion=reply.ConfigVersion,Persisted=true},stop.Token).ConfigureAwait(false);}
                    catch(Exception ackError){CloudNetworkErrors.Write(ackError);}
                    enrollIntervalMs=15000;Update("在线；设备已接入，等待观看租约");
                    return true;
                }
                if(String.Equals(reply.State,"rejected",StringComparison.Ordinal))
                {
                    state.State="rejected";try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch{}
                    Update("接入被拒绝："+(String.IsNullOrEmpty(reply.Message)?"管理员拒绝了该设备":reply.Message));await Delay(30000);return false;
                }
                // 认领凭据必须落盘：否则进程重启会生成新 secret，而云端那条记录的哈希对不上。
                try{lock(enrollmentGate)CloudEnrollmentStore.Save(state);}catch(Exception saveError){CloudNetworkErrors.Write(saveError);}
                Update("等待批准：已提交接入申请，等待管理员在云端网页批准");
                return false;
            }
            catch(CloudHttpException e)
            {
                CloudNetworkErrors.Write(e);
                if(e.StatusCode==429)
                {
                    // 限流不是错误，是「来太勤了」：把认领间隔翻倍，别继续撞墙。
                    enrollIntervalMs=Math.Min(60000,Math.Max(5000,enrollIntervalMs*2));
                    Update("等待批准：云端限流，已自动放慢申请节奏");
                    return false;
                }
                if(e.StatusCode==401||e.StatusCode==403){state.ClaimSecret=null;state.ClaimId=null;lastEnrollTick=0;Update("重新申请接入：认领凭据已失效");}
                else Update("等待批准："+CloudNetworkErrors.Describe(e));
                return false;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){CloudNetworkErrors.Write(e);Update("等待批准："+CloudNetworkErrors.Describe(e)+"；本地采集继续");return false;}
        }
        static void ValidateHeartbeatReply(CloudHeartbeatReply reply) {
            if(reply==null||reply.RequestedDevices==null||reply.RequestedDevices.Length>16||reply.LeaseSeconds<0||reply.LeaseSeconds>3600||reply.BackfillSeconds<0||reply.BackfillSeconds>300||(reply.LeaseSeconds>0&&String.IsNullOrWhiteSpace(reply.SubscriptionId))||reply.RequestedDevices.Any(x=>String.IsNullOrWhiteSpace(x)||x.Length>80)||reply.RequestedDevices.Distinct().Count()!=reply.RequestedDevices.Length)throw new InvalidDataException("租约或请求设备列表无效");
            if(reply.RequestedHistoryPoints!=null){if(reply.RequestedHistoryPoints.Length>64||reply.RequestedHistoryPoints.Distinct(StringComparer.Ordinal).Count()!=reply.RequestedHistoryPoints.Length)throw new InvalidDataException("历史趋势选择列表无效");foreach(string selection in reply.RequestedHistoryPoints){if(String.IsNullOrWhiteSpace(selection)||selection.Length>170)throw new InvalidDataException("历史趋势选择无效");int slash=selection.IndexOf('/');if(slash<=0||slash!=selection.LastIndexOf('/')||slash==selection.Length-1||!reply.RequestedDevices.Contains(selection.Substring(0,slash)))throw new InvalidDataException("历史趋势选择不属于当前请求设备");}}
        }
        public async Task<CloudDiagnosticResult> DiagnoseAsync(CancellationToken token) { CloudConfiguration c=configuration();var result=new CloudDiagnosticResult{CompletedLocal=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")};try{if(c==null||!c.HasValidEndpoint){result.Category="地址";result.Message="地址无效：请输入 HTTPS 服务根地址，不要填写 URL 凭据、查询参数或片段。";}else if(String.IsNullOrWhiteSpace(c.Token)){result.Category="认证";result.Message="认证未配置：请保存实验设备令牌。";}else{CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,token).ConfigureAwait(false);ValidateHeartbeatReply(reply);ApplyDeviceReply(reply,c);result.Heartbeat=reply;result.Category="接口响应";result.Message="HTTPS 心跳成功；租约 "+(reply.LeaseSeconds>0?reply.LeaseSeconds+" 秒":"未授予")+"，请求设备 "+reply.RequestedDevices.Length+" 台。此诊断不会上传测点快照。";lock(gate){lastContact=DateTime.UtcNow;diagnosticCategory=result.Category;diagnosticMessage=result.Message;}result.Success=true;}}catch(OperationCanceledException){result.Success=false;result.Category="已取消";result.Message="连接诊断已取消。";}catch(Exception e){result.Success=false;result.Category=DiagnosticCategory(e);result.Message=CloudNetworkErrors.Describe(e);CloudNetworkErrors.Write(e);}result.CompletedLocal=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");lock(gate){diagnosticCategory=result.Category;diagnosticMessage=result.Message;}return result; }
        static string DiagnosticCategory(Exception e){if(e is CloudHttpException){int s=((CloudHttpException)e).StatusCode;return s==401||s==403?"认证":"接口响应";}if(e is System.Security.Authentication.AuthenticationException)return "TLS证书";if(e is System.Net.WebException){var w=(System.Net.WebException)e;if(w.Status==System.Net.WebExceptionStatus.TrustFailure||w.Status==System.Net.WebExceptionStatus.SecureChannelFailure)return "TLS证书";if(w.Status==System.Net.WebExceptionStatus.NameResolutionFailure||w.Status==System.Net.WebExceptionStatus.ConnectFailure||w.Status==System.Net.WebExceptionStatus.Timeout)return "网络";}if(e is InvalidDataException||e is System.Runtime.Serialization.SerializationException)return "接口响应";if(e is ArgumentException||e is UriFormatException)return "地址";return "网络";}
        public void Publish(Observation o) {
            if(o==null||String.IsNullOrEmpty(o.Point)||String.IsNullOrEmpty(o.Device))return;
            CloudConfiguration c;try{c=configuration();}catch{return;}// 上传闸门只在这里拦：采集与 SQLite 在别处，物理上碰不到。
            if(c==null||!c.Enabled||uploadAllowed==false||(o.Source=="simulation"&&!(allowSimulation??c.IncludeSimulation)))return;
            lock(gate) {
                if(disposed)return;
                if(!String.Equals(acquisitionSession,o.SessionId??"",StringComparison.Ordinal)) { devices.Clear();sent.Clear();acquisitionSession=o.SessionId??"";connectionSession=Guid.NewGuid().ToString("N"); }
                string key=o.Source+":"+o.Device; Device d;
                if(!devices.TryGetValue(key,out d)) { if(devices.Count>=16)return; d=new Device{Source=o.Source,Id=o.Device,Slave=o.Slave,AcquisitionSession=o.SessionId};devices[key]=d; }
                if(d.Points.Count>=128&&!d.Points.ContainsKey(o.Point))return;
                string quality=String.IsNullOrEmpty(o.Quality)?(o.Number.HasValue?Quality.Good:Quality.Unknown):o.Quality;
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
                List<ExperimentBackfillPoint> points=rows.OrderBy(x=>x.Utc).ThenBy(x=>x.Database,StringComparer.Ordinal).ThenBy(x=>x.Id).Select(x=>{string quality=String.IsNullOrWhiteSpace(x.Quality)?(x.Number.HasValue?Quality.Good:Quality.Unknown):x.Quality;double? value=Quality.IsGood(quality)&&x.Number.HasValue&&!Double.IsNaN(x.Number.Value)&&!Double.IsInfinity(x.Number.Value)?x.Number:null;string session=HistoricalSession(x);return new ExperimentBackfillPoint{Source=x.Source,EquipmentId=x.Device,Id=x.Point,ConnectionSessionId=session,AcquisitionSessionId=session,ObservedUtc=x.Utc.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),Value=value,Quality=quality,Unit=x.Unit??"",ConfigVersion=String.IsNullOrWhiteSpace(x.ConfigVersion)?"legacy":x.ConfigVersion,AcquisitionRound=Math.Max(0,x.Round)};}).ToList();
                for(int offset=0;offset<points.Count;offset+=128){token.ThrowIfCancellationRequested();ExperimentBackfillPoint[] batch=points.Skip(offset).Take(128).ToArray();await transport.SendBackfillAsync(c,new ExperimentBackfillPost{SubscriptionId=subscription,SchemaVersion=1,Module="experiment",Points=batch},token).ConfigureAwait(false);if(offset+128<points.Count)await Task.Delay(50,token).ConfigureAwait(false);}
                lock(backfillGate){if(!token.IsCancellationRequested&&String.Equals(backfillRunningSubscription,subscription,StringComparison.Ordinal))backfilledSubscription=subscription;}
            }
            catch(OperationCanceledException){throw;}
            catch(Exception e){CloudNetworkErrors.Write(e);Update("在线；5分钟历史预热失败，实时采集继续");}
        }
        void Update(string text) { bool changed;lock(gate){changed=status!=text;status=text;}if(changed){EventHandler h=StatusChanged;if(h!=null)try{h(this,EventArgs.Empty);}catch{}} }
        void RecordSuccess(){lock(gate){consecutiveFailures=0;lastSuccessUtc=DateTime.UtcNow;lastErrorStage=null;lastErrorCode=null;lastErrorHttp=0;lastErrorAt=null;lastErrorCount=0;}}
        void RecordFailure(Exception e)
        {
            string stage=null;try{stage=CloudNetworkErrors.StageOf(e);}catch{}
            CloudHttpException http=e as CloudHttpException;
            lock(gate){consecutiveFailures++;if(stage==null)return;if(!String.Equals(stage,lastErrorStage,StringComparison.Ordinal)){lastErrorStage=stage;lastErrorAt=DateTime.UtcNow.ToString("o");lastErrorCount=0;}lastErrorCode=e.GetType().Name;lastErrorHttp=http!=null?http.StatusCode:0;lastErrorCount++;}
        }
        CloudClientReport BuildClientReport()
        {
            CloudConfiguration c=null;try{c=configuration();}catch{}
            CloudClientReport r;
            lock(gate)
            {
                r=new CloudClientReport{Version=typeof(CloudPublisher).Assembly.GetName().Version.ToString(),StartedUtc=startedUtc.ToString("o"),UptimeSec=(long)Math.Max(0,(DateTime.UtcNow-startedUtc).TotalSeconds),LocalTimeUtc=DateTime.UtcNow.ToString("o"),TzOffsetMin=(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,CloudEnabled=c!=null&&c.Enabled,TokenDecryptable=c!=null?!c.SavedTokenUnreadable:true,LastSuccessUtc=lastSuccessUtc.HasValue?lastSuccessUtc.Value.ToString("o"):null,ConsecutiveFailures=consecutiveFailures};
                if(lastErrorStage!=null)r.LastError=new CloudClientError{Stage=lastErrorStage,Code=lastErrorCode,HttpStatus=lastErrorHttp,AtUtc=lastErrorAt,Count=lastErrorCount};
            }
            r.Source=new CloudClientSource{Session=connectionSession};
            return r;
        }
        async Task Run() {
            Stopwatch clock=Stopwatch.StartNew();long heartbeatAt=0,uploadAt=0,leaseUntil=0;int backoff=1;string subscription="",activeSignature=null;bool pendingRefresh=false;HashSet<string> requested=new HashSet<string>();
            while(!stop.IsCancellationRequested) {
                CloudConfiguration c=null;try{c=configuration();}catch{}
                if(c==null||!c.Enabled){CancelBackfill();lock(backfillGate)backfilledSubscription=null;leaseUntil=0;subscription="";activeSignature=null;lock(gate){devices.Clear();sent.Clear();leaseUntilUtc=null;}Update("未启用");await Delay(250);continue;}
                string signature=c.DeviceId+"|"+c.Endpoint+"|"+c.Token+"|"+c.IncludeSimulation;
                if(activeSignature!=signature){CancelBackfill();lock(backfillGate)backfilledSubscription=null;heartbeatAt=0;uploadAt=0;leaseUntil=0;subscription="";requested.Clear();lock(gate){sent.Clear();leaseUntilUtc=null;if(activeSignature!=null){devices.Clear();connectionSession=Guid.NewGuid().ToString("N");}}activeSignature=signature;}
                if(!c.HasValidEndpoint){Update("云端地址无效");await Delay(250);continue;}
                if(String.IsNullOrWhiteSpace(c.Token))
                {
                    // 没令牌不等于「配置错了」：新版走自助接入，人不需要填任何东西。
                    if(enrollment==null){Update(c.SavedTokenUnreadable?"凭据无法解密，请重新填写令牌":"等待 HTTPS 地址和设备令牌");await Delay(250);continue;}
                    if(!await EnsureEnrolledAsync(c).ConfigureAwait(false)){await Delay(250);continue;}
                }
                bool uploading=false;
                try {
                    if(clock.ElapsedMilliseconds>=heartbeatAt) {
                        CloudHeartbeatReply reply=await transport.HeartbeatAsync(c,stop.Token).ConfigureAwait(false);lock(gate)lastContact=DateTime.UtcNow;RecordSuccess();
                        ValidateHeartbeatReply(reply);ApplyDeviceReply(reply,c);
                        if(subscription!=reply.SubscriptionId)lock(gate)sent.Clear();subscription=reply.SubscriptionId;requested=new HashSet<string>(reply.RequestedDevices,StringComparer.Ordinal);
                        leaseUntil=clock.ElapsedMilliseconds+reply.LeaseSeconds*1000L;lock(gate)leaseUntilUtc=reply.LeaseSeconds>0?DateTime.UtcNow.AddSeconds(reply.LeaseSeconds):(DateTime?)null;heartbeatAt=clock.ElapsedMilliseconds+(reply.LeaseSeconds>0?Math.Min(15000,Math.Max(3000,reply.LeaseSeconds*500)):15000);if(reply.LeaseSeconds>0&&!String.IsNullOrWhiteSpace(subscription))ScheduleWarmBackfill(c,reply,requested,subscription);else CancelBackfill();
                    }
                    if(clock.ElapsedMilliseconds<leaseUntil&&!String.IsNullOrWhiteSpace(subscription)&&clock.ElapsedMilliseconds>=uploadAt) {
                        ExperimentCloudSnapshot snapshot=null;string selectedKey=null,selectedSession=null;long version=0;
                        lock(gate) {
                            foreach(var entry in devices.OrderBy(x=>x.Value.Version)) {
                                Device d=entry.Value;long done;if(!requested.Contains(d.Id)||(d.Source=="simulation"&&!c.IncludeSimulation)||(sent.TryGetValue(entry.Key,out done)&&done==d.Version))continue;
                                long now=Stopwatch.GetTimestamp();if(!d.Points.Values.Any(x=>x.InitialAge+(now-x.Tick)/(double)Stopwatch.Frequency<=30))continue;
                                selectedKey=entry.Key;version=d.Version;selectedSession=connectionSession;if(d.Frozen!=null&&d.FrozenVersion==version&&d.FrozenSession==connectionSession){snapshot=d.Frozen;break;}
                                var points=d.Points.Values.Select(x=> { var p=x.Point;bool stale=x.InitialAge+(now-x.Tick)/(double)Stopwatch.Frequency>30;return new ExperimentCloudPoint{Id=p.Id,Description=p.Description,Unit=p.Unit,Value=stale?null:p.Value,RawValue=stale?null:p.RawValue,DisplayValue=stale?null:p.DisplayValue,Quality=stale?Quality.Stale:p.Quality,ObservedUtc=p.ObservedUtc,RoundStartedUtc=p.RoundStartedUtc,Round=p.Round,ConfigVersion=p.ConfigVersion,Address=p.Address,Count=p.Count,Mode=p.Mode}; }).OrderBy(x=>x.Id).ToArray();
                                snapshot=new ExperimentCloudSnapshot{DeviceId=c.DeviceId,ConnectionSessionId=connectionSession,AcquisitionSessionId=d.AcquisitionSession,Sequence=version,CapturedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),Source=d.Source,EquipmentId=d.Id,Slave=d.Slave,Points=points};d.Frozen=snapshot;d.FrozenVersion=version;d.FrozenSession=connectionSession;break;
                            }
                        }
                        if(snapshot!=null) {
                            uploading=true;await transport.SendSnapshotAsync(c,new CloudSnapshotPost{SubscriptionId=subscription,Snapshot=snapshot},stop.Token).ConfigureAwait(false);
                            lock(gate){if(connectionSession==selectedSession){sent[selectedKey]=version;sentCount++;lastUpload=DateTime.UtcNow;}}backoff=1;uploadAt=clock.ElapsedMilliseconds+1000;Update("在线；已上传实验快照");
                        }else Update("在线；租约有效，等待新鲜数据");
                    }else Update(clock.ElapsedMilliseconds>=leaseUntil?"在线；无人观看或租约过期":"在线；观看租约有效");
                }catch(OperationCanceledException){if(stop.IsCancellationRequested)break;failedCount++;uploadAt=clock.ElapsedMilliseconds+backoff*1000L;heartbeatAt=uploadAt;backoff=Math.Min(60,backoff*2);Update("请求取消；本地采集继续");}
                catch(Exception e){lock(gate)failedCount++;CloudNetworkErrors.Write(e);RecordFailure(e);CloudHttpException http=e as CloudHttpException;bool auth=http!=null&&(http.StatusCode==401||http.StatusCode==403);if(http!=null&&http.StatusCode==409){CancelBackfill();leaseUntil=0;subscription="";lock(gate)leaseUntilUtc=null;heartbeatAt=clock.ElapsedMilliseconds+500;uploadAt=heartbeatAt;Update("观看租约失效，重新请求心跳");continue;}if(http!=null&&http.StatusCode==429){CancelBackfill();leaseUntil=0;heartbeatAt=clock.ElapsedMilliseconds+30000;lock(gate)leaseUntilUtc=null;backoff=Math.Min(60,backoff*2);Update("限流等待；稍后重试");continue;}long next=clock.ElapsedMilliseconds+(auth?60000:backoff*1000L);if(uploading)uploadAt=next;else heartbeatAt=next;if(auth){CancelBackfill();leaseUntil=0;heartbeatAt=next;lock(gate)leaseUntilUtc=null;
                    // 只有「云端认为我还活着」时才续期；被注销/未激活时续期端点同样会 401，无脑重试就是死循环。
                    bool activeEnough=enrollment!=null&&String.Equals(deviceState,"active",StringComparison.Ordinal);
                    if(activeEnough)pendingRefresh=true;
                    else if(enrollment!=null){CloudEnrollment st;lock(enrollmentGate)st=enrollment;if(st!=null&&!st.IsRevoked){st.ClaimSecret=null;st.ClaimId=null;lastEnrollTick=0;try{lock(enrollmentGate)CloudEnrollmentStore.Save(st);}catch{}}}
                }backoff=Math.Min(60,backoff*2);Update(CloudNetworkErrors.Describe(e)+"；本地采集继续");}
                if(pendingRefresh)
                {
                    pendingRefresh=false;
                    ICloudTokenRefresher refresher=transport as ICloudTokenRefresher;
                    bool tooSoon=lastRefreshUtc.HasValue&&(DateTime.UtcNow-lastRefreshUtc.Value).TotalMinutes<5;
                    if(refresher!=null&&!tooSoon)
                    {
                        try
                        {
                            CloudTokenRefreshReply renewed=await refresher.RefreshTokenAsync(c,stop.Token).ConfigureAwait(false);
                            if(renewed!=null&&!String.IsNullOrWhiteSpace(renewed.DeviceToken))
                            {
                                // 先落盘成功再切换运行态：换证途中掉电不会留下「有令牌但云端不认」的状态。
                                string previous=c.Token;c.Token=renewed.DeviceToken;
                                try{c.Save();}catch{c.Token=previous;throw;}
                                lastRefreshUtc=DateTime.UtcNow;
                                heartbeatAt=clock.ElapsedMilliseconds;uploadAt=0;backoff=1;
                                Update("在线；设备令牌已自动续期");
                            }
                        }
                        catch(Exception refreshError){CloudNetworkErrors.Write(refreshError);Update("认证失败；自动续期失败："+CloudNetworkErrors.Describe(refreshError));}
                    }
                }
                await Delay(250);
            }
        }
        async Task Delay(int ms){try{await Task.Delay(ms,stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;}CancelBackfill();stop.Cancel();try{worker.GetAwaiter().GetResult();}catch{}Task pending;lock(backfillGate)pending=backfillTask;try{if(pending!=null)pending.GetAwaiter().GetResult();}catch{}stop.Dispose();}
    }
}



