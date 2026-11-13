// @generated 来自 _base/src/AcmeCloud.cs —— 请勿手改。
// @source-sha256 f1ea9a3280267ad351f73102bd7f60cdb6ad90344d9ce3ceef32a284614757ba
// @sync node _base/sync-base.mjs --write
// @end-header
// ============================================================
// Acme.Cloud — 三端共用云传输层（基座 L2）
// ------------------------------------------------------------
// 消除了两份 CloudRealtime.cs 里逐字相同的：
//   HttpsCloudTransport.SendAsync（27/27 行）
//   CloudNetworkErrors（59/60 行，仅产品目录名不同）
//   CloudConfiguration（37/44 行）
// 各端只保留自己领域的 DTO 与路由。
// ============================================================
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
using System.Threading;
using System.Threading.Tasks;
using Acme.Base;

namespace Acme.Cloud
{
    public sealed class CloudConfiguration
    {
        public bool Enabled { get; set; }
        public bool IncludeSimulation { get; set; }
        public string DeviceId { get; set; }
        public string Alias { get; set; }
        public string Endpoint { get; set; }
        public string Token { get; set; }
        public string SavePath { get; set; }
        internal bool SavedTokenUnreadable;
        /// <summary>读取/解析失败时的日志回调。默认 null = 静默（保持实验端原有行为）；BMS 端在启动时挂到 CrashLogger。</summary>
        public static Action<string,Exception> LoadFailureLog;
        public static CloudConfiguration Load(string path,string deviceId=null)
        {
            // 配置文件读取/解析失败不得阻断启动（例如只读目录、文件被占用、内容损坏）：记录诊断日志并回退到默认配置。
            CloudConfiguration c=new CloudConfiguration{DeviceId=deviceId??Guid.NewGuid().ToString("N"),Endpoint="https://",Token="",Alias="",SavePath=path};
            if(!File.Exists(path))return c;
            Dictionary<string,string> map;
            try
            {
                map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(string line in File.ReadAllLines(path,Encoding.UTF8)){int i=line.IndexOf('=');if(i>0)map[line.Substring(0,i)]=line.Substring(i+1);}
            }
            catch(Exception e){if(LoadFailureLog!=null)LoadFailureLog("云端连接配置读取失败，已使用默认配置："+path,e);return c;}
            try
            {
                string value;if(map.TryGetValue("deviceId",out value)&&!String.IsNullOrWhiteSpace(value))c.DeviceId=value;if(map.TryGetValue("enabled",out value))c.Enabled=value=="1";if(map.TryGetValue("simulation",out value))c.IncludeSimulation=value=="1";if(map.TryGetValue("alias",out value))try{c.Alias=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}if(map.TryGetValue("endpoint",out value))try{c.Endpoint=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}
                if(map.TryGetValue("token",out value)&&value.Length>0)try{c.Token=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}catch{c.Token="";c.SavedTokenUnreadable=true;}
            }
            catch(Exception e){if(LoadFailureLog!=null)LoadFailureLog("云端连接配置解析异常，已部分采用默认值："+path,e);}
            return c;
        }
        public void Save()
        {
            // C24：无目录部分直接报错；失败路径清理 .partial-* 临时文件（含 DPAPI 密文）后重抛。
            if(String.IsNullOrWhiteSpace(SavePath))throw new InvalidOperationException("配置路径无效");
            string parentDirectory=Path.GetDirectoryName(SavePath);if(String.IsNullOrEmpty(parentDirectory))throw new InvalidOperationException("配置路径必须包含目录部分");
            Directory.CreateDirectory(parentDirectory);string secret="";if(!String.IsNullOrEmpty(Token))secret=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Token),null,DataProtectionScope.CurrentUser));
            string text="deviceId="+DeviceId+"\r\n"+"enabled="+(Enabled?"1":"0")+"\r\nsimulation="+(IncludeSimulation?"1":"0")+"\r\nendpoint="+B64(Endpoint)+"\r\nalias="+B64(Alias)+"\r\ntoken="+secret+"\r\n";string temp=SavePath+".partial-"+Guid.NewGuid().ToString("N");
            try{File.WriteAllText(temp,text,new UTF8Encoding(false));AtomicFile.Replace(temp,SavePath);}
            catch{try{if(File.Exists(temp))File.Delete(temp);}catch{}throw;}
        }
        static string B64(string s){return Convert.ToBase64String(Encoding.UTF8.GetBytes(s??""));}
        public bool HasValidEndpoint { get { Uri u;return Uri.TryCreate(Endpoint,UriKind.Absolute,out u)&&u.Scheme==Uri.UriSchemeHttps&&String.IsNullOrEmpty(u.UserInfo)&&String.IsNullOrEmpty(u.Fragment)&&String.IsNullOrEmpty(u.Query); } }
    }
    public sealed class CloudHttpException:Exception
    {
        public int StatusCode{get;private set;}
        public CloudHttpException(int status,string message):base(message){StatusCode=status;}
    }
    [DataContract] sealed class CloudAcknowledgement{[DataMember(Name="accepted")]public bool Accepted{get;set;}}
    public static class CloudNetworkErrors
    {
        /// <summary>LocalApplicationData 下的产品子目录名（各端启动时设置）。</summary>
        public static string FallbackFolder="Acme";
        static readonly object logLock=new object();
        static readonly Dictionary<string,long> lastWrittenTick=new Dictionary<string,long>(StringComparer.Ordinal);
        const int MaxLogBytes=256*1024;
        public static string Describe(Exception error)
        {
            if(error==null)return "网络错误：未知异常";
            CloudHttpException http=error as CloudHttpException;if(http!=null)return DescribeHttp(http.StatusCode);
            TimeoutException timeout=error as TimeoutException;if(timeout!=null)return "网络错误：请求超时";
            WebException web=error as WebException;
            if(web!=null)
            {
                string category;
                switch(web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:case WebExceptionStatus.ProxyNameResolutionFailure:category="DNS解析失败";break;
                    case WebExceptionStatus.ConnectFailure:category=SocketCategory(web.InnerException)??(web.InnerException is AuthenticationException?"TLS握手失败":"无法连接服务器");break;
                    case WebExceptionStatus.TrustFailure:category="TLS证书验证失败";break;
                    case WebExceptionStatus.SecureChannelFailure:category="TLS握手失败";break;
                    case WebExceptionStatus.Timeout:category="请求超时";break;
                    case WebExceptionStatus.ProtocolError:
                        HttpWebResponse response=web.Response as HttpWebResponse;category=response!=null?DescribeHttp((int)response.StatusCode):"HTTP协议响应失败";break;
                    default:
                        category=SocketCategory(web.InnerException);
                        if(category==null)category=web.InnerException is AuthenticationException?"TLS握手失败":WebCategory(web.Status);
                        break;
                }
                return "网络错误："+category+"（WebExceptionStatus="+web.Status+"/HResult=0x"+web.HResult.ToString("X8")+""+InnerIdentitySuffix(web.InnerException)+"）";
            }
            string socket=SocketCategory(error);if(socket!=null)return "网络错误："+socket+"（"+KnownIdentity(error)+"）";
            if(error is AuthenticationException)return "网络错误：TLS握手失败（"+KnownIdentity(error)+"）";
            if(error is InvalidDataException||error is System.Runtime.Serialization.SerializationException)return "协议错误：云端数据格式无效";
            if(error is IOException)return "网络错误：I/O请求失败（"+KnownIdentity(error)+"）";
            return "网络或协议错误："+KnownIdentity(error);
        }
        static string DescribeHttp(int status)
        {if(status==407)return "网络错误：代理要求认证（HTTP 407）";if(status==401||status==403)return "云端认证失败（HTTP "+status+"）";return "网络错误：HTTP响应失败（HTTP "+status+"）";}
        static string WebCategory(WebExceptionStatus status){return status==WebExceptionStatus.ProxyNameResolutionFailure?"DNS解析失败":status==WebExceptionStatus.ConnectFailure?"连接失败":status==WebExceptionStatus.TrustFailure?"TLS证书验证失败":status==WebExceptionStatus.SecureChannelFailure?"TLS握手失败":status==WebExceptionStatus.Timeout?"请求超时":status==WebExceptionStatus.ProtocolError?"HTTP协议响应失败":"请求失败";}
        static string SocketCategory(Exception e){while(e!=null){SocketException socket=e as SocketException;if(socket!=null){switch(socket.SocketErrorCode){case SocketError.HostNotFound:case SocketError.TryAgain:case SocketError.NoRecovery:case SocketError.NoData:return "DNS解析失败";case SocketError.ConnectionRefused:return "连接被拒绝";case SocketError.TimedOut:return "连接超时";default:return null;}}e=e.InnerException;}return null;}
        static string InnerIdentitySuffix(Exception e){return e==null?"":"; Inner="+KnownIdentity(e);}
        static string KnownIdentity(Exception e){if(e==null)return "异常类型未知";string type=e.GetType()==typeof(SocketException)?"SocketException":e.GetType()==typeof(AuthenticationException)?"AuthenticationException":e.GetType()==typeof(IOException)?"IOException":e.GetType()==typeof(TimeoutException)?"TimeoutException":e.GetType()==typeof(WebException)?"WebException":e.GetType()==typeof(InvalidDataException)?"InvalidDataException":e.GetType()==typeof(System.Runtime.Serialization.SerializationException)?"SerializationException":"Exception";return type+"/HResult=0x"+e.HResult.ToString("X8");}
        public static void Write(Exception error)
        {
            string description=Describe(error),category=description;int p=description.IndexOf('：');if(p>=0)category=description.Substring(p+1);p=category.IndexOf('（');if(p>=0)category=category.Substring(0,p);long tick=Stopwatch.GetTimestamp();
            lock(logLock)
            {
                long previousTick;if(lastWrittenTick.TryGetValue(category,out previousTick)&&tick-previousTick<Stopwatch.Frequency*60L)return;lastWrittenTick[category]=tick;
                try
                {
                    string line=DateTimeOffset.Now.ToString("o")+" "+description+Environment.NewLine;byte[] bytes=new UTF8Encoding(false).GetBytes(line);
                    if(!AppendLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs"),bytes))
                    {string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);if(!String.IsNullOrEmpty(local))AppendLog(Path.Combine(local,FallbackFolder,"logs"),bytes);}
                }catch{}
            }
        }
        static bool AppendLog(string directory,byte[] bytes)
        {try{Directory.CreateDirectory(directory);string path=Path.Combine(directory,"cloud-network-errors.log"),backup=path+".1";if(File.Exists(path)&&new FileInfo(path).Length+bytes.Length>MaxLogBytes){if(File.Exists(backup))File.Delete(backup);File.Move(path,backup);}using(FileStream f=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read))f.Write(bytes,0,bytes.Length);return true;}catch{return false;}}
    }

    /// <summary>
    /// HTTP 传输引擎 —— 从两份 CloudRealtime.cs 中逐字相同的 SendAsync 提取。
    /// 各端 HttpsCloudTransport 只保留自己的路由与 DTO，全部经由本引擎发请求。
    /// 硬约束（与 spec 一致，不要各自调参）：
    ///   整体时限 5000ms、响应上限 64KiB、禁止重定向、Bearer 设备令牌、
    ///   401/403 归为"认证失败"、超时与取消必须可区分。
    /// </summary>
    public sealed class CloudHttpTransport
    {
        readonly Func<Uri,HttpWebRequest> requestFactory;
        readonly bool testLoopback;
        readonly int timeoutMilliseconds;
        public int TimeoutMilliseconds{get{return timeoutMilliseconds;}}
        public CloudHttpTransport()
        {
            timeoutMilliseconds=5000;
            // 注意：这里**刻意不设置** ServicePointManager.SecurityProtocol。
            // BMS 端有断言守着"未设置全局TLS版本，保留系统默认协议"，基座不得偷偷改全局状态。
            // 需要显式 TLS1.2 的端（实验端）在自己的 HttpsCloudTransport 构造函数里设置。
        }
        internal CloudHttpTransport(Func<Uri,HttpWebRequest> factory,int timeoutMilliseconds,bool testLoopback)
        {
            if(factory==null)throw new ArgumentNullException("factory");
            if(timeoutMilliseconds<1||timeoutMilliseconds>5000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            requestFactory=factory;this.timeoutMilliseconds=timeoutMilliseconds;this.testLoopback=testLoopback;
        }
        public async Task<T> SendAsync<T>(CloudConfiguration c,string route,object payload,DataContractJsonSerializer responseSerializer,CancellationToken token)
        {
            Uri uri=BuildUri(c,route);HttpWebRequest req=requestFactory==null?(HttpWebRequest)WebRequest.Create(uri):requestFactory(uri);if(requestFactory!=null){IPAddress injectedIp;if(req==null||req.RequestUri==null||!IPAddress.TryParse(req.RequestUri.Host,out injectedIp)||!IPAddress.IsLoopback(injectedIp))throw new InvalidOperationException("测试传输仅允许 loopback 请求");}req.Method="POST";req.ContentType="application/json; charset=utf-8";req.Accept="application/json";req.Timeout=timeoutMilliseconds;req.ReadWriteTimeout=timeoutMilliseconds;req.AllowAutoRedirect=false;req.Proxy=requestFactory==null?WebRequest.DefaultWebProxy:null;req.Headers[HttpRequestHeader.Authorization]="Bearer "+(c.Token??"");
            using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeoutMilliseconds);using(deadline.Token.Register(delegate{try{req.Abort();}catch{}}))
                {
                    try
                    {
                        byte[] bytes;using(MemoryStream payloadStream=new MemoryStream()){new DataContractJsonSerializer(payload.GetType()).WriteObject(payloadStream,payload);bytes=payloadStream.ToArray();}
                        using(Stream body=await req.GetRequestStreamAsync().ConfigureAwait(false)){await body.WriteAsync(bytes,0,bytes.Length,deadline.Token).ConfigureAwait(false);}
                        using(HttpWebResponse response=(HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                        {
                            int status=(int)response.StatusCode;if(status<200||status>=300)throw new CloudHttpException(status,status==401||status==403?"云端认证失败":"云端 HTTP "+status);
                            if(response.ContentLength>65536)throw new InvalidDataException("云端响应超过64 KiB");using(Stream stream=response.GetResponseStream()){if(stream==null)throw new InvalidDataException("云端响应为空");using(MemoryStream ms=new MemoryStream()){byte[] block=new byte[4096];int n,total=0;while((n=await stream.ReadAsync(block,0,block.Length,deadline.Token).ConfigureAwait(false))>0){total+=n;if(total>65536)throw new InvalidDataException("云端响应超过64 KiB");ms.Write(block,0,n);}if(total==0)throw new InvalidDataException("云端响应为空");ms.Position=0;return (T)responseSerializer.ReadObject(ms);}}
                        }
                    }
                    catch(WebException e)
                    {
                        HttpWebResponse response=e.Response as HttpWebResponse;if(response!=null){int status=(int)response.StatusCode;response.Close();throw new CloudHttpException(status,status==401||status==403?"云端认证失败":"云端 HTTP "+status);}
                        if(deadline.IsCancellationRequested){if(token.IsCancellationRequested)throw new OperationCanceledException("云端请求已取消",e,token);throw new TimeoutException("云端请求超过"+timeoutMilliseconds+"毫秒时限",e);}throw;
                    }
                    catch(OperationCanceledException e){if(token.IsCancellationRequested)throw new OperationCanceledException("云端请求已取消",e,token);throw new TimeoutException("云端请求超过"+timeoutMilliseconds+"毫秒时限",e);}
                }
            }
        }
        Uri BuildUri(CloudConfiguration c,string route)
        {Uri u;if(c==null||!Uri.TryCreate(c.Endpoint,UriKind.Absolute,out u))throw new InvalidOperationException("仅允许 HTTPS 服务地址");if(!c.HasValidEndpoint){IPAddress ip;if(!testLoopback||u.Scheme!=Uri.UriSchemeHttp||!IPAddress.TryParse(u.Host,out ip)||!IPAddress.IsLoopback(ip))throw new InvalidOperationException("仅允许 HTTPS 服务地址");}return new Uri(new Uri(c.Endpoint.TrimEnd('/')+"/"),route.TrimStart('/'));}
    }
}
