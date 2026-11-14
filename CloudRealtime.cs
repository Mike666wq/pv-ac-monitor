using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Acme.Cloud;
namespace ExperimentMonitor
{
    [DataContract]
    public sealed class CloudHeartbeatReply
    {
        [DataMember(Name="subscriptionId",IsRequired=true)]public string SubscriptionId{get;set;}
        [DataMember(Name="leaseSeconds",IsRequired=true)]public int LeaseSeconds{get;set;}
        [DataMember(Name="requestedDevices",IsRequired=true)]public string[] RequestedDevices{get;set;}
        [DataMember(Name="requestedHistoryPoints",EmitDefaultValue=false)]public string[] RequestedHistoryPoints{get;set;}
        [DataMember(Name="backfillSeconds",EmitDefaultValue=false)]public int BackfillSeconds{get;set;}
        // 以下为设备配置下行（云端说了算）。全部可选且不带 IsRequired：
        // 旧云端回包里没有这些字段，标了必填就会反序列化失败、整端离线。
        [DataMember(Name="protocolVersion",EmitDefaultValue=false)]public int? ProtocolVersion{get;set;}
        [DataMember(Name="state",EmitDefaultValue=false)]public string State{get;set;}
        [DataMember(Name="uploadAllowed",EmitDefaultValue=false)]public bool? UploadAllowed{get;set;}
        [DataMember(Name="configVersion",EmitDefaultValue=false)]public long? ConfigVersion{get;set;}
        [DataMember(Name="config",EmitDefaultValue=false)]public CloudDeviceConfig Config{get;set;}
        [DataMember(Name="commands",EmitDefaultValue=false)]public CloudDeviceCommand[] Commands{get;set;}
    }
    [DataContract]
    sealed class CloudHeartbeatRequest
    {
        [DataMember(Name="deviceId")]public string DeviceId{get;set;}
        [DataMember(Name="alias")]public string Alias{get;set;}
        [DataMember(Name="module")]public string Module{get;set;}
        [DataMember(Name="schemaVersion")]public int SchemaVersion{get;set;}
        // 设备自述：把「只有现场能看到的体检结果」搭车送到云端。全部可选，坏数据由服务端降级。
        [DataMember(Name="client",EmitDefaultValue=false)]public CloudClientReport Client{get;set;}
        // 接入协议字段必须留在类末尾：契约检查器只读本类开头的 600 字符窗口。
        // protocolVersion 单独一个整数，绝不复用 schemaVersion（实验端有 schemaVersion===1 的硬断言）。
        [DataMember(Name="protocolVersion",EmitDefaultValue=false)]public int? ProtocolVersion{get;set;}
        [DataMember(Name="machineKey",EmitDefaultValue=false)]public string MachineKey{get;set;}
        [DataMember(Name="reportedConfigVersion",EmitDefaultValue=false)]public long? ReportedConfigVersion{get;set;}
        [DataMember(Name="reportedCommandId",EmitDefaultValue=false)]public string ReportedCommandId{get;set;}
    }
    [DataContract]
    public sealed class CloudSnapshotPost
    {
        [DataMember(Name="subscriptionId")]public string SubscriptionId{get;set;}
        [DataMember(Name="snapshot")]public ExperimentCloudSnapshot Snapshot{get;set;}
    }
    [DataContract]
    public sealed class ExperimentBackfillPoint
    {
        [DataMember(Name="source")]public string Source{get;set;}
        [DataMember(Name="equipmentId")]public string EquipmentId{get;set;}
        [DataMember(Name="id")]public string Id{get;set;}
        [DataMember(Name="connectionSessionId")]public string ConnectionSessionId{get;set;}
        [DataMember(Name="acquisitionSessionId")]public string AcquisitionSessionId{get;set;}
        [DataMember(Name="observedUtc")]public string ObservedUtc{get;set;}
        [DataMember(Name="value")]public double? Value{get;set;}
        [DataMember(Name="quality")]public string Quality{get;set;}
        [DataMember(Name="unit")]public string Unit{get;set;}
        [DataMember(Name="configVersion")]public string ConfigVersion{get;set;}
        [DataMember(Name="acquisitionRound")]public long AcquisitionRound{get;set;}
    }
    [DataContract]
    public sealed class ExperimentBackfillPost
    {
        [DataMember(Name="subscriptionId")]public string SubscriptionId{get;set;}
        [DataMember(Name="schemaVersion")]public int SchemaVersion{get;set;}
        [DataMember(Name="module")]public string Module{get;set;}
        [DataMember(Name="points")]public ExperimentBackfillPoint[] Points{get;set;}
    }
    public interface ICloudTransport
    {
        Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token);
        Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token);
        Task SendBackfillAsync(CloudConfiguration configuration,ExperimentBackfillPost post,CancellationToken token);
    }
    /// <summary>
    /// 设备自助接入（认领/轮询/确认）。刻意独立成一个接口：
    /// 自测里的假传输不必实现它，老路径与既有断言一行都不用改。
    /// </summary>
    public interface ICloudEnrollmentTransport
    {
        Task<CloudClaimReply> ClaimAsync(CloudConfiguration configuration,CloudClaimRequest request,CancellationToken token);
        Task<CloudClaimReply> ClaimStatusAsync(CloudConfiguration configuration,string claimId,CancellationToken token);
        Task<CloudAckReply> AcknowledgeAsync(CloudConfiguration configuration,CloudAckRequest request,CancellationToken token);
    }
    public sealed class HttpsCloudTransport:ICloudTransport,ICloudTokenRefresher,ICloudEnrollmentTransport
    {
        // 传输引擎在 Acme.Cloud：请求拼装、5s 整体时限、64KiB 响应上限、禁重定向、
        // 超时/取消/HTTP 状态码分类，三端共用同一份实现。
        static readonly DataContractJsonSerializer replySerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatReply));
        static readonly DataContractJsonSerializer acknowledgementSerializer=new DataContractJsonSerializer(typeof(CloudAcknowledgement));
        static readonly DataContractJsonSerializer refreshSerializer=new DataContractJsonSerializer(typeof(CloudTokenRefreshReply));
        static readonly DataContractJsonSerializer claimSerializer=new DataContractJsonSerializer(typeof(CloudClaimReply));
        static readonly DataContractJsonSerializer ackSerializer=new DataContractJsonSerializer(typeof(CloudAckReply));
        readonly CloudHttpTransport engine;
        public HttpsCloudTransport(){ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;engine=new CloudHttpTransport();}
        internal HttpsCloudTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback){engine=new CloudHttpTransport(factory,timeoutMilliseconds,testLoopback);}
        // 服务层把自述块的生产者挂在这里；传输层只负责搭车，不关心内容。
        public Func<CloudClientReport> ClientReportProvider;
        // 接入状态的生产者：心跳要带上「我已应用到的配置版本」和机器钥匙。
        public Func<CloudEnrollment> EnrollmentProvider;
        public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token)
        {
            CloudHeartbeatRequest request=new CloudHeartbeatRequest{DeviceId=configuration.DeviceId,Alias=configuration.Alias,Module="experiment",SchemaVersion=1};
            Func<CloudClientReport> provider=ClientReportProvider;
            if(provider!=null)try{request.Client=provider();}catch{}
            CloudEnrollment enrollment=EnrollmentProvider!=null?EnrollmentProvider():null;
            if(enrollment!=null)
            {
                // protocolVersion 只在真接入时才声明：没认领过的设备仍旧发老格式。
                if(enrollment.ProtocolVersion>0)request.ProtocolVersion=enrollment.ProtocolVersion;
                if(!String.IsNullOrEmpty(enrollment.MachineKey))request.MachineKey=enrollment.MachineKey;
                if(enrollment.ReportedConfigVersion>0)request.ReportedConfigVersion=enrollment.ReportedConfigVersion;
                if(!String.IsNullOrEmpty(enrollment.LastAppliedCommandId))request.ReportedCommandId=enrollment.LastAppliedCommandId;
            }
            return engine.SendAsync<CloudHeartbeatReply>(configuration,"/api/experiment/heartbeat",request,replySerializer,token);
        }
        /// <summary>用当前令牌换新令牌：设备自助续期，管理员不参与。</summary>
        public Task<CloudTokenRefreshReply> RefreshTokenAsync(CloudConfiguration configuration,CancellationToken token)
        {return engine.SendAsync<CloudTokenRefreshReply>(configuration,"/api/experiment/device/refresh",new CloudRefreshRequest(),refreshSerializer,token);}
        public async Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/snapshots",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认快照");}
        public async Task SendBackfillAsync(CloudConfiguration configuration,ExperimentBackfillPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/backfill",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认历史回填");}
        // 认领三连：传入的 configuration 只是「地址 + Bearer」的临时载体，绝不 Save()。
        public Task<CloudClaimReply> ClaimAsync(CloudConfiguration configuration,CloudClaimRequest request,CancellationToken token)
        {return engine.SendAsync<CloudClaimReply>(configuration,CloudDeviceRoutes.Claim,request,claimSerializer,token);}
        public Task<CloudClaimReply> ClaimStatusAsync(CloudConfiguration configuration,string claimId,CancellationToken token)
        {return engine.SendAsync<CloudClaimReply>(configuration,CloudDeviceRoutes.ClaimStatus(claimId),new CloudRefreshRequest(),claimSerializer,token);}
        public Task<CloudAckReply> AcknowledgeAsync(CloudConfiguration configuration,CloudAckRequest request,CancellationToken token)
        {return engine.SendAsync<CloudAckReply>(configuration,CloudDeviceRoutes.Ack,request,ackSerializer,token);}
    }
}
