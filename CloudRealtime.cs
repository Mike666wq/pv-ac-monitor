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
    public sealed class HttpsCloudTransport:ICloudTransport,ICloudTokenRefresher
    {
        // 传输引擎在 Acme.Cloud：请求拼装、5s 整体时限、64KiB 响应上限、禁重定向、
        // 超时/取消/HTTP 状态码分类，三端共用同一份实现。
        static readonly DataContractJsonSerializer replySerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatReply));
        static readonly DataContractJsonSerializer acknowledgementSerializer=new DataContractJsonSerializer(typeof(CloudAcknowledgement));
        static readonly DataContractJsonSerializer refreshSerializer=new DataContractJsonSerializer(typeof(CloudTokenRefreshReply));
        readonly CloudHttpTransport engine;
        public HttpsCloudTransport(){ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;engine=new CloudHttpTransport();}
        internal HttpsCloudTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback){engine=new CloudHttpTransport(factory,timeoutMilliseconds,testLoopback);}
        // 服务层把自述块的生产者挂在这里；传输层只负责搭车，不关心内容。
        public Func<CloudClientReport> ClientReportProvider;
        public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token)
        {
            CloudHeartbeatRequest request=new CloudHeartbeatRequest{DeviceId=configuration.DeviceId,Alias=configuration.Alias,Module="experiment",SchemaVersion=1};
            Func<CloudClientReport> provider=ClientReportProvider;
            if(provider!=null)try{request.Client=provider();}catch{}
            return engine.SendAsync<CloudHeartbeatReply>(configuration,"/api/experiment/heartbeat",request,replySerializer,token);
        }
        /// <summary>用当前令牌换新令牌：设备自助续期，管理员不参与。</summary>
        public Task<CloudTokenRefreshReply> RefreshTokenAsync(CloudConfiguration configuration,CancellationToken token)
        {return engine.SendAsync<CloudTokenRefreshReply>(configuration,"/api/experiment/device/refresh",new CloudRefreshRequest(),refreshSerializer,token);}
        public async Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/snapshots",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认快照");}
        public async Task SendBackfillAsync(CloudConfiguration configuration,ExperimentBackfillPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/backfill",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认历史回填");}
    }
}
