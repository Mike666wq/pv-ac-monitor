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
    public sealed class HttpsCloudTransport:ICloudTransport
    {
        // 传输引擎在 Acme.Cloud：请求拼装、5s 整体时限、64KiB 响应上限、禁重定向、
        // 超时/取消/HTTP 状态码分类，三端共用同一份实现。
        static readonly DataContractJsonSerializer replySerializer=new DataContractJsonSerializer(typeof(CloudHeartbeatReply));
        static readonly DataContractJsonSerializer acknowledgementSerializer=new DataContractJsonSerializer(typeof(CloudAcknowledgement));
        readonly CloudHttpTransport engine;
        public HttpsCloudTransport(){ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;engine=new CloudHttpTransport();}
        internal HttpsCloudTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback){engine=new CloudHttpTransport(factory,timeoutMilliseconds,testLoopback);}
        public Task<CloudHeartbeatReply> HeartbeatAsync(CloudConfiguration configuration,CancellationToken token)
        {CloudHeartbeatRequest request=new CloudHeartbeatRequest{DeviceId=configuration.DeviceId,Alias=configuration.Alias,Module="experiment",SchemaVersion=1};return engine.SendAsync<CloudHeartbeatReply>(configuration,"/api/experiment/heartbeat",request,replySerializer,token);}
        public async Task SendSnapshotAsync(CloudConfiguration configuration,CloudSnapshotPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/snapshots",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认快照");}
        public async Task SendBackfillAsync(CloudConfiguration configuration,ExperimentBackfillPost post,CancellationToken token)
        {CloudAcknowledgement a=await engine.SendAsync<CloudAcknowledgement>(configuration,"/api/experiment/backfill",post,acknowledgementSerializer,token).ConfigureAwait(false);if(a==null||!a.Accepted)throw new InvalidDataException("云端未确认历史回填");}
    }
}
