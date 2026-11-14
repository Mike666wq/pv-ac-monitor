// @generated 来自 _base/src/AcmeCloud.cs —— 请勿手改。
// @source-sha256 e48a5e2d824db498b79e0e4b1ad4a11acf3090acf45c93808cd2846376f9580c
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
    /// <summary>
    /// 设备身份与凭据的稳定存放位置。
    /// 安装目录会被整包替换（发布包明确不含 settings/），凭据放在那里等于每次升级都要重新注册。
    /// 优先级：ProgramData（机器级，升级/重装都不丢）→ LocalAppData（旧位置，兼容读）→ 安装目录（更旧，兼容读）。
    /// </summary>
    public static class CloudIdentity
    {
        public static string MachineSettings(string product)
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),product,"settings"); }
        public static string UserSettings(string product)
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),product,"settings"); }
        public static string InstallSettings(string installDirectory)
        { return Path.Combine(installDirectory,"settings"); }

        /// <summary>目录可用 = 能建目录且能写文件。ProgramData 默认只给 Users 读，所以必须实测而不是猜。</summary>
        public static bool IsUsable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe=Path.Combine(directory,".write-probe-"+Guid.NewGuid().ToString("N"));
                using(FileStream s=new FileStream(probe,FileMode.CreateNew,FileAccess.Write)){s.WriteByte(0);}
                File.Delete(probe);
                return true;
            }
            catch(Exception ex)
            {
                return false;
            }
        }

        /// <summary>返回第一个可写的设置目录；全都不可写时退回安装目录（只读，行为与旧版本一致）。</summary>
        public static string ResolveSettingsDirectory(string product,string installDirectory)
        {
            string machine=MachineSettings(product);bool mu=IsUsable(machine);
            string user=UserSettings(product);bool uu=mu?false:IsUsable(user);
            string result=mu?machine:(uu?user:InstallSettings(installDirectory));
            return result;
        }

        /// <summary>可能存有设备编号的路径，按可信度从高到低。只用于读取与一次性迁移。</summary>
        public static string[] LegacyDeviceIdPaths(string product,string installDirectory)
        {
            return new[]{
                Path.Combine(MachineSettings(product),"device-id.txt"),
                Path.Combine(UserSettings(product),"device-id.txt"),
                Path.Combine(InstallSettings(installDirectory),"device-id.txt")
            };
        }

        /// <summary>
        /// 读取设备编号：按优先级找第一个合法值；若它不在最高优先级位置，顺带写回一份，
        /// 让下一次启动（包括升级后）直接从稳定位置读到。全部失败才生成新的——并调用 onGenerated 让调用方告警。
        /// </summary>
        public static string LoadOrCreateDeviceId(string product,string installDirectory,Func<string,bool> isValid,Action<string,string> onMigrated,Action<string> onGenerated)
        {
            string primary=Path.Combine(MachineSettings(product),"device-id.txt");
            string found=null,foundPath=null;
            foreach(string path in LegacyDeviceIdPaths(product,installDirectory))
            {
                string value;
                if(TryRead(path,isValid,out value)){found=value;foundPath=path;break;}
            }
            if(found==null)
            {
                string id=Guid.NewGuid().ToString("N");
                bool persisted=TryWrite(primary,id)||TryWrite(Path.Combine(UserSettings(product),"device-id.txt"),id)||TryWrite(Path.Combine(InstallSettings(installDirectory),"device-id.txt"),id);
                if(!persisted&&onGenerated!=null)onGenerated(id);
                return id;
            }
            if(!String.Equals(foundPath,primary,StringComparison.OrdinalIgnoreCase))
            {
                // 从旧位置搬到稳定位置：搬成功才算迁移完成，失败也不影响本次运行。
                if(TryWrite(primary,found)&&onMigrated!=null)onMigrated(foundPath,primary);
            }
            return found;
        }

        /// <summary>把设备编号写到指定目录的 device-id.txt（用于「实际生效的设置目录」可能与 ProgramData 不同的情况）。</summary>
        public static bool WriteDeviceIdTo(string settingsDirectory,string deviceId)
        {
            if(String.IsNullOrWhiteSpace(settingsDirectory)||String.IsNullOrWhiteSpace(deviceId))return false;
            return TryWrite(Path.Combine(settingsDirectory,"device-id.txt"),deviceId);
        }

        /// <summary>
        /// 机器钥匙：本地自生成、人不看不管的稳定标识，用于向云端认领。
        /// 它不是秘密（服务端存明文，管理页要展示），所以刻意不用硬件指纹——
        /// 指纹换硬件就变、克隆会撞、还不可轮换，而随机值可以一键重置。
        /// </summary>
        public static string LoadOrCreateMachineKey(string product,string installDirectory)
        {
            string primary=Path.Combine(MachineSettings(product),"machine-key.txt");
            string existing;
            if(TryRead(primary,IsMachineKey,out existing))return existing;
            string[] legacy={
                Path.Combine(UserSettings(product),"machine-key.txt"),
                Path.Combine(InstallSettings(installDirectory),"machine-key.txt")
            };
            for(int i=0;i<legacy.Length;i++)
                if(TryRead(legacy[i],IsMachineKey,out existing)){TryWrite(primary,existing);return existing;}
            string created=NewMachineKey();
            // 三个位置逐级尝试，最后一个兜底是安装目录：
            // 只要有一处写成功，下次启动就能读到同一把钥匙；全都失败的话，
            // 每次启动都会生成新钥匙，云端就会看到一台又一台「新设备」。
            // 三处都写不了时本次仍返回这把钥匙，但下次启动会生成新的（云端会看到多台新设备）。
            // 这里不抛异常：连不上云端也不该阻断本地采集与记录。
            bool saved=TryWrite(primary,created);
            if(!saved)saved=TryWrite(Path.Combine(UserSettings(product),"machine-key.txt"),created);
            if(!saved)TryWrite(Path.Combine(InstallSettings(installDirectory),"machine-key.txt"),created);
            return created;
        }

        public static bool IsMachineKey(string value)
        {
            if(String.IsNullOrEmpty(value)||value.Length!=35)return false;
            if(!value.StartsWith("mk_",StringComparison.Ordinal))return false;
            for(int i=3;i<value.Length;i++){char c=value[i];if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))return false;}
            return true;
        }

        /// <summary>16 字节 CSPRNG -> mk_ + 32 位小写 hex。用 RandomNumberGenerator，不用 Guid。</summary>
        public static string NewMachineKey()
        {
            byte[] buffer=new byte[16];
            using(RandomNumberGenerator rng=RandomNumberGenerator.Create())rng.GetBytes(buffer);
            StringBuilder sb=new StringBuilder("mk_",35);
            for(int i=0;i<buffer.Length;i++)sb.Append(buffer[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>导入凭据后把设备编号写回稳定位置，让下一次启动（含升级后）直接读到同一个身份。</summary>
        public static bool WriteMachineDeviceId(string product,string deviceId)
        {
            if(String.IsNullOrWhiteSpace(deviceId))return false;
            return TryWrite(Path.Combine(MachineSettings(product),"device-id.txt"),deviceId);
        }

        public static bool TryRead(string path,Func<string,bool> isValid,out string value)
        {
            value=null;
            try
            {
                if(!File.Exists(path))return false;
                // 必须去掉 BOM：记事本存成「UTF-8 带 BOM」会让编号校验直接作废。
                string candidate=File.ReadAllText(path).Trim().TrimStart('\uFEFF').Trim();
                if(isValid(candidate)){value=candidate;return true;}
            }
            catch{}
            return false;
        }

        public static bool TryWrite(string path,string value)
        {
            try
            {
                string dir=Path.GetDirectoryName(path);if(String.IsNullOrEmpty(dir))return false;
                Directory.CreateDirectory(dir);
                string temp=path+".partial-"+Guid.NewGuid().ToString("N");
                File.WriteAllText(temp,value,new UTF8Encoding(false));
                AtomicFile.Replace(temp,path);
                return true;
            }
            catch{return false;}
        }
    }

    /// <summary>客户端自述的失败阶段。与云端 HEARTBEAT.stageEnum 一一对应。</summary>
    public static class CloudClientStage
    {
        public const string Dns="DNS",Proxy="PROXY",Tcp="TCP",Tls="TLS",Auth="AUTH",Http="HTTP",Protocol="PROTOCOL",Serial="SERIAL";
    }

    [DataContract]
    public sealed class CloudClientError
    {
        [DataMember(Name="stage")]public string Stage{get;set;}
        [DataMember(Name="code",EmitDefaultValue=false)]public string Code{get;set;}
        [DataMember(Name="httpStatus",EmitDefaultValue=false)]public int HttpStatus{get;set;}
        [DataMember(Name="atUtc",EmitDefaultValue=false)]public string AtUtc{get;set;}
        [DataMember(Name="message",EmitDefaultValue=false)]public string Message{get;set;}
        [DataMember(Name="count",EmitDefaultValue=false)]public int Count{get;set;}
    }

    [DataContract]
    public sealed class CloudClientChannel
    {
        [DataMember(Name="port",EmitDefaultValue=false)]public string Port{get;set;}
        [DataMember(Name="open",EmitDefaultValue=false)]public bool? Open{get;set;}
        [DataMember(Name="lastFrameUtc",EmitDefaultValue=false)]public string LastFrameUtc{get;set;}
    }

    [DataContract]
    public sealed class CloudClientSource
    {
        [DataMember(Name="session",EmitDefaultValue=false)]public string Session{get;set;}
        [DataMember(Name="serial",EmitDefaultValue=false)]public CloudClientChannel Serial{get;set;}
        [DataMember(Name="modbus",EmitDefaultValue=false)]public CloudClientChannel Modbus{get;set;}
    }

    /// <summary>
    /// 心跳里搭车上报的现场体检结果。字段缺省 = 未知，绝不等于「正常」。
    /// 布尔用 bool? ：false（明确的否）必须能发出去，不能和「没这个字段」混为一谈。
    /// </summary>
    [DataContract]
    public sealed class CloudClientReport
    {
        [DataMember(Name="version",EmitDefaultValue=false)]public string Version{get;set;}
        [DataMember(Name="startedUtc",EmitDefaultValue=false)]public string StartedUtc{get;set;}
        [DataMember(Name="uptimeSec",EmitDefaultValue=false)]public long UptimeSec{get;set;}
        [DataMember(Name="localTimeUtc",EmitDefaultValue=false)]public string LocalTimeUtc{get;set;}
        [DataMember(Name="tzOffsetMin",EmitDefaultValue=false)]public int TzOffsetMin{get;set;}
        [DataMember(Name="cloudEnabled",EmitDefaultValue=false)]public bool? CloudEnabled{get;set;}
        [DataMember(Name="tokenDecryptable",EmitDefaultValue=false)]public bool? TokenDecryptable{get;set;}
        [DataMember(Name="lastSuccessUtc",EmitDefaultValue=false)]public string LastSuccessUtc{get;set;}
        [DataMember(Name="consecutiveFailures",EmitDefaultValue=false)]public int ConsecutiveFailures{get;set;}
        [DataMember(Name="nextRetryInSec",EmitDefaultValue=false)]public int NextRetryInSec{get;set;}
        [DataMember(Name="lastError",EmitDefaultValue=false)]public CloudClientError LastError{get;set;}
        [DataMember(Name="source",EmitDefaultValue=false)]public CloudClientSource Source{get;set;}
    }

    /// <summary>
    /// 旧配置搬迁。
    ///
    /// 背景：身份与凭据从「安装目录」搬到 ProgramData 之后，旧版本留下的配置文件还在安装目录里。
    /// 如果只改新路径而不搬内容，现场表现为「服务地址空了、令牌没了、设备编号变成另一台」——
    /// 这比不改还糟，所以搬迁必须和换路径同一版发布。
    ///
    /// 规则（三条都是「只在不丢东西的前提下才动」）：
    ///   · 目标不存在 → 直接复制一份过去，旧文件保留。
    ///   · 目标存在但没有令牌 → 说明它没有被真正配置过，允许被旧配置覆盖。
    ///   · 目标已有令牌 → 一行都不动。
    /// </summary>
    public static class CloudSettingsMigration
    {
        /// <summary>目标不存在时从旧位置搬一份过去，返回最终应当使用的路径。永不抛错，永不删除旧文件。</summary>
        public static string AdoptConfig(string targetPath, string legacyPath)
        {
            try
            {
                if (File.Exists(targetPath)) return targetPath;
                if (String.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath)) return targetPath;
                if (String.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase)) return targetPath;
                string directory = Path.GetDirectoryName(targetPath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = targetPath + ".partial-" + Guid.NewGuid().ToString("N");
                try { File.Copy(legacyPath, temp, true); AtomicFile.Replace(temp, targetPath); }
                catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            catch { }
            return targetPath;
        }

        /// <summary>目标配置里是否已经有令牌。没有就说明它可以被旧配置接管。</summary>
        public static bool HasToken(string path)
        {
            try { return File.Exists(path) && !String.IsNullOrWhiteSpace(CloudConfiguration.Load(path, null).Token); }
            catch { return false; }
        }

        /// <summary>
        /// 从旧配置里取设备身份。
        /// 历史版本（实验端）把 deviceId 存在云连接配置文件内部，没有独立的 device-id 文件；
        /// 不把它读出来就会新生成一个编号，云端看到的是「多了一台新设备」。
        /// 只要旧配置文件存在且里面的编号合法就采纳——它本来就是用户升级前看到的那个编号，
        /// 与有没有令牌无关（没配过云连接的机器同样有编号，同样不该变）。
        /// 调用方还要用 HasToken 判断目标位置能否被接管：目标已经配好令牌时不许覆盖，只保留它自己的身份。
        /// </summary>
        public static bool TryReadLegacyIdentity(string legacyPath, Func<string, bool> isValid, out string deviceId)
        {
            deviceId = null;
            try
            {
                if (String.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath)) return false;
                CloudConfiguration legacy = CloudConfiguration.Load(legacyPath, null);
                if (legacy == null || String.IsNullOrWhiteSpace(legacy.DeviceId)) return false;
                if (isValid != null && !isValid(legacy.DeviceId)) return false;
                deviceId = legacy.DeviceId;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>续期请求体为空；定义一个 DataContract 只是为了让序列化器有确定的类型。</summary>
    [DataContract]
    public sealed class CloudRefreshRequest { }

    [DataContract]
    public sealed class CloudTokenRefreshReply
    {
        [DataMember(Name="deviceId")]public string DeviceId{get;set;}
        [DataMember(Name="deviceToken")]public string DeviceToken{get;set;}
        [DataMember(Name="rotatedAt")]public string RotatedAt{get;set;}
    }

    /// <summary>
    /// 设备自助续期能力：用当前令牌换新令牌，管理员不参与。
    /// 只有真实 HTTPS 传输实现它；测试替身不实现，服务层用 as 探测——这样加能力不会打挂既有假传输。
    /// </summary>
    public interface ICloudTokenRefresher
    {
        Task<CloudTokenRefreshReply> RefreshTokenAsync(CloudConfiguration configuration,CancellationToken token);
    }

    /// <summary>
    /// 凭据导出/导入：换机器、重装系统、DPAPI 因换 Windows 用户解不开时的兜底。
    ///
    /// 为什么不直接用 DPAPI：CryptProtectData 只在「同一用户 + 同一台机器」能解密，
    /// 而这里要解决的恰恰就是跨机/跨用户。
    ///
    /// 格式：口令派生密钥（PBKDF2-SHA256，12 万轮）→ AES-256-CBC 加密 → HMAC-SHA256 校验。
    /// 先加密后 MAC（encrypt-then-MAC），口令错与文件被改都会在校验阶段被拒，
    /// 不会把随机明文喂给 JSON 解析器。
    /// </summary>
    public static class CloudCredentialBundle
    {
        const string Magic="acmedevice/1";
        const int SaltBytes=16,IvBytes=16,KeyBytes=64,Iterations=120000;
        const int MacBytes=32;

        [DataContract]
        sealed class Payload
        {
            [DataMember(Name="deviceId")]public string DeviceId{get;set;}
            [DataMember(Name="endpoint")]public string Endpoint{get;set;}
            [DataMember(Name="alias")]public string Alias{get;set;}
            [DataMember(Name="token")]public string Token{get;set;}
            [DataMember(Name="exportedUtc")]public string ExportedUtc{get;set;}
        }

        public static void ExportToFile(CloudConfiguration configuration,string password,string path)
        {
            string text=Export(configuration,password);
            string directory=Path.GetDirectoryName(path);
            if(!String.IsNullOrEmpty(directory))Directory.CreateDirectory(directory);
            string temp=path+".partial-"+Guid.NewGuid().ToString("N");
            try{File.WriteAllText(temp,text,new UTF8Encoding(false));AtomicFile.Replace(temp,path);}
            catch{try{if(File.Exists(temp))File.Delete(temp);}catch{}throw;}
        }

        public static CloudConfiguration ImportFromFile(string path,string password,string savePath)
        {
            string text;
            try{text=File.ReadAllText(path,Encoding.UTF8);}
            catch(Exception e){throw new InvalidOperationException("凭据文件无法读取："+path,e);}
            return Import(text,password,savePath);
        }

        public static string Export(CloudConfiguration configuration,string password)
        {
            if(configuration==null)throw new ArgumentNullException("configuration");
            RequirePassword(password);
            Payload payload=new Payload{DeviceId=configuration.DeviceId,Endpoint=configuration.Endpoint,Alias=configuration.Alias,Token=configuration.Token,ExportedUtc=DateTime.UtcNow.ToString("o")};
            byte[] json;
            using(MemoryStream buffer=new MemoryStream()){new DataContractJsonSerializer(typeof(Payload)).WriteObject(buffer,payload);json=buffer.ToArray();}
            byte[] salt=RandomBytes(SaltBytes),iv=RandomBytes(IvBytes),keys=DeriveKeys(password,salt);
            byte[] cipher;
            using(AesCryptoServiceProvider aes=new AesCryptoServiceProvider{Key=Slice(keys,0,32),IV=iv,Mode=CipherMode.CBC,Padding=PaddingMode.PKCS7})
            using(MemoryStream output=new MemoryStream())
            {
                using(CryptoStream stream=new CryptoStream(output,aes.CreateEncryptor(),CryptoStreamMode.Write))stream.Write(json,0,json.Length);
                cipher=output.ToArray();
            }
            byte[] mac=ComputeMac(Slice(keys,32,32),salt,iv,cipher);
            StringBuilder text=new StringBuilder();
            text.Append(Magic).Append('\n');
            text.Append("deviceId=").Append(configuration.DeviceId).Append('\n');
            text.Append("salt=").Append(Convert.ToBase64String(salt)).Append('\n');
            text.Append("iv=").Append(Convert.ToBase64String(iv)).Append('\n');
            text.Append("ct=").Append(Convert.ToBase64String(cipher)).Append('\n');
            text.Append("mac=").Append(Convert.ToBase64String(mac)).Append('\n');
            return text.ToString();
        }

        public static CloudConfiguration Import(string text,string password,string savePath)
        {
            RequirePassword(password);
            if(text==null)throw new InvalidOperationException("凭据内容为空");
            string[] lines=text.Replace("\r\n","\n").Split('\n');
            if(lines.Length==0||lines[0].Trim()!=Magic)throw new InvalidOperationException("不是本系统的凭据文件（缺少 "+Magic+" 标识）");
            Dictionary<string,string> map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for(int i=1;i<lines.Length;i++){int at=lines[i].IndexOf('=');if(at>0)map[lines[i].Substring(0,at).Trim()]=lines[i].Substring(at+1).Trim();}
            string deviceId;map.TryGetValue("deviceId",out deviceId);
            byte[] salt=Field(map,"salt"),iv=Field(map,"iv"),cipher=Field(map,"ct"),mac=Field(map,"mac");
            if(salt.Length!=SaltBytes||iv.Length!=IvBytes||mac.Length!=MacBytes)throw new InvalidOperationException("凭据文件结构损坏");
            byte[] keys=DeriveKeys(password,salt);
            byte[] expected=ComputeMac(Slice(keys,32,32),salt,iv,cipher);
            if(!FixedTimeEquals(mac,expected))throw new InvalidOperationException("口令不正确，或凭据文件已被修改");
            Payload payload;
            try
            {
                using(AesCryptoServiceProvider aes=new AesCryptoServiceProvider{Key=Slice(keys,0,32),IV=iv,Mode=CipherMode.CBC,Padding=PaddingMode.PKCS7})
                using(MemoryStream input=new MemoryStream(cipher))
                using(CryptoStream stream=new CryptoStream(input,aes.CreateDecryptor(),CryptoStreamMode.Read))
                    payload=(Payload)new DataContractJsonSerializer(typeof(Payload)).ReadObject(stream);
            }
            catch(Exception e){throw new InvalidOperationException("凭据文件解密失败",e);}
            if(payload==null||String.IsNullOrWhiteSpace(payload.DeviceId))throw new InvalidOperationException("凭据内容缺少设备编号");
            return new CloudConfiguration{DeviceId=payload.DeviceId,Endpoint=payload.Endpoint,Alias=payload.Alias,Token=payload.Token,Enabled=true,SavePath=savePath};
        }

        static void RequirePassword(string password)
        {
            if(String.IsNullOrEmpty(password)||password.Length<8)throw new ArgumentException("口令至少 8 个字符：这是凭据唯一的保护，不能空着。");
        }
        static byte[] RandomBytes(int count){byte[] buffer=new byte[count];using(RandomNumberGenerator rng=RandomNumberGenerator.Create())rng.GetBytes(buffer);return buffer;}
        static byte[] Slice(byte[] source,int offset,int count){byte[] result=new byte[count];Array.Copy(source,offset,result,0,count);return result;}
        static byte[] DeriveKeys(string password,byte[] salt){using(Rfc2898DeriveBytes kdf=new Rfc2898DeriveBytes(password,salt,Iterations))return kdf.GetBytes(KeyBytes);}
        static byte[] ComputeMac(byte[] key,byte[] salt,byte[] iv,byte[] cipher)
        {
            byte[] buffer=new byte[salt.Length+iv.Length+cipher.Length];
            Array.Copy(salt,0,buffer,0,salt.Length);Array.Copy(iv,0,buffer,salt.Length,iv.Length);Array.Copy(cipher,0,buffer,salt.Length+iv.Length,cipher.Length);
            using(HMACSHA256 hmac=new HMACSHA256(key))return hmac.ComputeHash(buffer);
        }
        static bool FixedTimeEquals(byte[] a,byte[] b)
        {
            if(a==null||b==null||a.Length!=b.Length)return false;
            int diff=0;for(int i=0;i<a.Length;i++)diff|=a[i]^b[i];
            return diff==0;
        }
        static byte[] Field(Dictionary<string,string> map,string name)
        {
            string value;
            if(!map.TryGetValue(name,out value)||String.IsNullOrEmpty(value))throw new InvalidOperationException("凭据文件缺少字段："+name);
            try{return Convert.FromBase64String(value);}catch{throw new InvalidOperationException("凭据文件字段不是合法 Base64："+name);}
        }
    }

    /// <summary>
    /// 设备接入路由。与业务前缀（/api/realtime 等）彻底分开：
    /// 认领发生在「还没有设备令牌」的时刻，而业务前缀每个端点都要 Bearer 令牌。
    /// 必须与 _base/contract/contract.mjs 的 DEVICE_ROUTES 保持一致。
    /// </summary>
    public static class CloudDeviceRoutes
    {
        public const string Prefix="/api/device";
        public const string Claim="/api/device/claim";
        public const string Ack="/api/device/ack";
        public static string ClaimStatus(string claimId){return Prefix+"/claim/"+claimId;}
    }

    /// <summary>云端下发的设备配置。注意：这里绝不会有串口/波特率/采样周期——采集参数归本地。</summary>
    [DataContract]
    public sealed class CloudDeviceConfig
    {
        [DataMember(Name="deviceId",EmitDefaultValue=false)]public string DeviceId{get;set;}
        [DataMember(Name="alias",EmitDefaultValue=false)]public string Alias{get;set;}
        [DataMember(Name="module",EmitDefaultValue=false)]public string Module{get;set;}
        [DataMember(Name="allowedAddresses",EmitDefaultValue=false)]public int[] AllowedAddresses{get;set;}
        [DataMember(Name="allowedPacks",EmitDefaultValue=false)]public int[] AllowedPacks{get;set;}
        [DataMember(Name="allowedEquipment",EmitDefaultValue=false)]public string[] AllowedEquipment{get;set;}
        [DataMember(Name="allowSimulation",EmitDefaultValue=false)]public bool? AllowSimulation{get;set;}
        [DataMember(Name="displayTimeZone",EmitDefaultValue=false)]public string DisplayTimeZone{get;set;}
        [DataMember(Name="uploadEnabled",EmitDefaultValue=false)]public bool? UploadEnabled{get;set;}
        [DataMember(Name="heartbeatIntervalSec",EmitDefaultValue=false)]public int? HeartbeatIntervalSec{get;set;}
        [DataMember(Name="statusReason",EmitDefaultValue=false)]public string StatusReason{get;set;}
        [DataMember(Name="changedBy",EmitDefaultValue=false)]public string ChangedBy{get;set;}
        [DataMember(Name="changedAtUtc",EmitDefaultValue=false)]public string ChangedAtUtc{get;set;}
        [DataMember(Name="note",EmitDefaultValue=false)]public string Note{get;set;}
    }

    [DataContract]
    public sealed class CloudDeviceCommand
    {
        [DataMember(Name="commandId")]public string CommandId{get;set;}
        [DataMember(Name="type")]public string Type{get;set;}
        [DataMember(Name="reason",EmitDefaultValue=false)]public string Reason{get;set;}
        [DataMember(Name="issuedAtUtc",EmitDefaultValue=false)]public string IssuedAtUtc{get;set;}
        [DataMember(Name="expiresAtUtc",EmitDefaultValue=false)]public string ExpiresAtUtc{get;set;}
    }

    [DataContract]
    public sealed class CloudClaimRequest
    {
        [DataMember(Name="machineKey")]public string MachineKey{get;set;}
        [DataMember(Name="module")]public string Module{get;set;}
        [DataMember(Name="protocolVersion",EmitDefaultValue=false)]public int? ProtocolVersion{get;set;}
        [DataMember(Name="desiredAlias",EmitDefaultValue=false)]public string DesiredAlias{get;set;}
        [DataMember(Name="client",EmitDefaultValue=false)]public CloudClientReport Client{get;set;}
    }

    [DataContract]
    public sealed class CloudClaimReply
    {
        [DataMember(Name="claimId")]public string ClaimId{get;set;}
        [DataMember(Name="state")]public string State{get;set;}
        [DataMember(Name="pollIntervalSec",EmitDefaultValue=false)]public int? PollIntervalSec{get;set;}
        [DataMember(Name="expiresAtUtc",EmitDefaultValue=false)]public string ExpiresAtUtc{get;set;}
        [DataMember(Name="serverTimeUtc",EmitDefaultValue=false)]public string ServerTimeUtc{get;set;}
        [DataMember(Name="message",EmitDefaultValue=false)]public string Message{get;set;}
        // 以下仅在已批准时出现；令牌明文只出现一次。
        [DataMember(Name="deviceId",EmitDefaultValue=false)]public string DeviceId{get;set;}
        [DataMember(Name="deviceToken",EmitDefaultValue=false)]public string DeviceToken{get;set;}
        [DataMember(Name="configVersion",EmitDefaultValue=false)]public long? ConfigVersion{get;set;}
        [DataMember(Name="config",EmitDefaultValue=false)]public CloudDeviceConfig Config{get;set;}
        [DataMember(Name="tokenDelivered",EmitDefaultValue=false)]public bool? TokenDelivered{get;set;}
    }

    [DataContract]
    public sealed class CloudAckRequest
    {
        [DataMember(Name="claimId")]public string ClaimId{get;set;}
        [DataMember(Name="ackType")]public string AckType{get;set;}
        [DataMember(Name="deviceId",EmitDefaultValue=false)]public string DeviceId{get;set;}
        [DataMember(Name="configVersion",EmitDefaultValue=false)]public long? ConfigVersion{get;set;}
        [DataMember(Name="persisted")]public bool Persisted{get;set;}
        [DataMember(Name="deliveryLost",EmitDefaultValue=false)]public bool DeliveryLost{get;set;}
    }

    [DataContract]
    public sealed class CloudAckReply
    {
        [DataMember(Name="state")]public string State{get;set;}
        [DataMember(Name="deviceId",EmitDefaultValue=false)]public string DeviceId{get;set;}
        [DataMember(Name="configVersion",EmitDefaultValue=false)]public long? ConfigVersion{get;set;}
        [DataMember(Name="retryAfterSec",EmitDefaultValue=false)]public int? RetryAfterSec{get;set;}
        [DataMember(Name="serverTimeUtc",EmitDefaultValue=false)]public string ServerTimeUtc{get;set;}
    }

    /// <summary>
    /// 本地设备接入状态。只在这里保存「我是谁、云端允许我做什么」，
    /// 刻意不往 CloudConfiguration 加字段：那是 k=v 全量重写，漏一处就静默清空。
    /// 云端才是真相源，这份只是本地缓存 + 认领期凭据。
    /// </summary>
    public sealed class CloudEnrollment
    {
        public string MachineKey{get;set;}
        public string ClaimSecret{get;set;}          // 明文只存在内存；落盘用 DPAPI
        public string ClaimId{get;set;}
        public string DeviceId{get;set;}
        public string State{get;set;}                // pending/approved/active/suspended/revoked/rejected/expired/conflict
        public long ReportedConfigVersion{get;set;}
        public string LastAppliedCommandId{get;set;}
        public bool UploadAllowed{get;set;}
        public bool AllowSimulation{get;set;}
        public string Alias{get;set;}
        public string Module{get;set;}
        public int[] AllowedPacks{get;set;}
        public int[] AllowedAddresses{get;set;}
        public string[] AllowedEquipment{get;set;}
        public int HeartbeatIntervalSec{get;set;}
        /// <summary>下行协议版本：0 = 未接入（按老协议发心跳），2 = 已声明支持设备配置下发。</summary>
        public int ProtocolVersion{get;set;}
        public string StatusReason{get;set;}
        public string ChangedBy{get;set;}
        public string ChangedAtUtc{get;set;}
        public string SavePath{get;set;}

        public bool IsActive { get { return String.Equals(State,"active",StringComparison.Ordinal); } }
        public bool IsPending { get { return String.Equals(State,"pending",StringComparison.Ordinal)||String.Equals(State,"approved",StringComparison.Ordinal); } }
        public bool IsRevoked { get { return String.Equals(State,"revoked",StringComparison.Ordinal); } }

        /// <summary>把云端下发的 config 合并进来。字段缺省表示「云端没改这一项」，保留原值。</summary>
        public void ApplyConfig(CloudDeviceConfig config,long configVersion)
        {
            if(config==null)return;
            if(!String.IsNullOrEmpty(config.Alias))Alias=config.Alias;
            if(!String.IsNullOrEmpty(config.Module))Module=config.Module;
            if(config.AllowedPacks!=null)AllowedPacks=config.AllowedPacks;
            if(config.AllowedAddresses!=null)AllowedAddresses=config.AllowedAddresses;
            if(config.AllowedEquipment!=null)AllowedEquipment=config.AllowedEquipment;
            if(config.AllowSimulation.HasValue)AllowSimulation=config.AllowSimulation.Value;
            if(config.UploadEnabled.HasValue)UploadAllowed=config.UploadEnabled.Value;
            if(config.HeartbeatIntervalSec.HasValue&&config.HeartbeatIntervalSec.Value>0)HeartbeatIntervalSec=config.HeartbeatIntervalSec.Value;
            if(config.StatusReason!=null)StatusReason=config.StatusReason;
            if(config.ChangedBy!=null)ChangedBy=config.ChangedBy;
            if(config.ChangedAtUtc!=null)ChangedAtUtc=config.ChangedAtUtc;
            if(configVersion>0)ReportedConfigVersion=configVersion;
        }
    }

    /// <summary>
    /// device-enrollment.json 的读写。格式与 CloudConfiguration 一致的 k=v 文本，
    /// claimSecret 用 DPAPI(CurrentUser) 加密。读取失败一律回退默认值，绝不阻断启动。
    /// </summary>
    public static class CloudEnrollmentStore
    {
        public static Action<string,Exception> LoadFailureLog;
        public const string FileName="device-enrollment.json";

        public static CloudEnrollment Load(string path,string machineKey)
        {
            CloudEnrollment e=new CloudEnrollment{MachineKey=machineKey,State="unclaimed",HeartbeatIntervalSec=15,SavePath=path};
            if(String.IsNullOrWhiteSpace(path)||!File.Exists(path))return e;
            Dictionary<string,string> map;
            try
            {
                map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(String line in File.ReadAllLines(path,Encoding.UTF8)){int i=line.IndexOf('=');if(i>0)map[line.Substring(0,i)]=line.Substring(i+1);}
            }
            catch(Exception ex){if(LoadFailureLog!=null)LoadFailureLog("设备接入状态读取失败，按未认领处理："+path,ex);return e;}
            String value;
            if(map.TryGetValue("machineKey",out value)&&!String.IsNullOrWhiteSpace(value))e.MachineKey=value;
            if(map.TryGetValue("claimId",out value))e.ClaimId=value;
            if(map.TryGetValue("deviceId",out value))e.DeviceId=value;
            if(map.TryGetValue("state",out value)&&!String.IsNullOrWhiteSpace(value))e.State=value;
            if(map.TryGetValue("alias",out value))try{e.Alias=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}
            if(map.TryGetValue("module",out value))e.Module=value;
            if(map.TryGetValue("lastAppliedCommandId",out value))e.LastAppliedCommandId=value;
            if(map.TryGetValue("statusReason",out value))try{e.StatusReason=Encoding.UTF8.GetString(Convert.FromBase64String(value));}catch{}
            long parsed;
            if(map.TryGetValue("reportedConfigVersion",out value)&&Int64.TryParse(value,out parsed))e.ReportedConfigVersion=parsed;
            int parsedInterval;
            if(map.TryGetValue("heartbeatIntervalSec",out value)&&Int32.TryParse(value,out parsedInterval))e.HeartbeatIntervalSec=parsedInterval;
            if(map.TryGetValue("protocolVersion",out value)&&Int32.TryParse(value,out parsedInterval))e.ProtocolVersion=parsedInterval;
            if(map.TryGetValue("uploadAllowed",out value))e.UploadAllowed=value=="1";
            if(map.TryGetValue("allowSimulation",out value))e.AllowSimulation=value=="1";
            if(map.TryGetValue("allowedPacks",out value))e.AllowedPacks=ParseInts(value);
            if(map.TryGetValue("allowedAddresses",out value))e.AllowedAddresses=ParseInts(value);
            if(map.TryGetValue("allowedEquipment",out value))e.AllowedEquipment=ParseStrings(value);
            if(map.TryGetValue("claimSecret",out value)&&value.Length>0)
                try{e.ClaimSecret=Decrypt(value);}catch{e.ClaimSecret=null;}
            return e;
        }

        public static void Save(CloudEnrollment e)
        {
            if(e==null)throw new ArgumentNullException("enrollment");
            if(String.IsNullOrWhiteSpace(e.SavePath))throw new InvalidOperationException("设备接入状态路径无效");
            String directory=Path.GetDirectoryName(e.SavePath);
            if(String.IsNullOrEmpty(directory))throw new InvalidOperationException("设备接入状态路径必须包含目录部分");
            Directory.CreateDirectory(directory);
            String secret="";
            if(!String.IsNullOrEmpty(e.ClaimSecret))secret=Encrypt(e.ClaimSecret);
            StringBuilder text=new StringBuilder();
            text.Append("machineKey=").Append(e.MachineKey).Append('\n');
            text.Append("claimId=").Append(e.ClaimId??"").Append('\n');
            text.Append("deviceId=").Append(e.DeviceId??"").Append('\n');
            text.Append("state=").Append(e.State??"unclaimed").Append('\n');
            text.Append("module=").Append(e.Module??"").Append('\n');
            text.Append("alias=").Append(B64(e.Alias)).Append('\n');
            text.Append("statusReason=").Append(B64(e.StatusReason)).Append('\n');
            text.Append("reportedConfigVersion=").Append(e.ReportedConfigVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            text.Append("heartbeatIntervalSec=").Append(e.HeartbeatIntervalSec.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            text.Append("protocolVersion=").Append(e.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            text.Append("lastAppliedCommandId=").Append(e.LastAppliedCommandId??"").Append('\n');
            text.Append("uploadAllowed=").Append(e.UploadAllowed?"1":"0").Append('\n');
            text.Append("allowSimulation=").Append(e.AllowSimulation?"1":"0").Append('\n');
            text.Append("allowedPacks=").Append(JoinInts(e.AllowedPacks)).Append('\n');
            text.Append("allowedAddresses=").Append(JoinInts(e.AllowedAddresses)).Append('\n');
            text.Append("allowedEquipment=").Append(JoinStrings(e.AllowedEquipment)).Append('\n');
            text.Append("claimSecret=").Append(secret).Append('\n');
            String temp=e.SavePath+".partial-"+Guid.NewGuid().ToString("N");
            try{File.WriteAllText(temp,text.ToString(),new UTF8Encoding(false));AtomicFile.Replace(temp,e.SavePath);}
            catch{try{if(File.Exists(temp))File.Delete(temp);}catch{}throw;}
        }

        static String B64(String value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value??""));}
        static String Encrypt(String value){return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser));}
        static String Decrypt(String value){return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}
        static int[] ParseInts(String value)
        {
            if(String.IsNullOrWhiteSpace(value))return new int[0];
            String[] parts=value.Split(',');List<int> list=new List<int>();
            for(int i=0;i<parts.Length;i++){int parsed;if(Int32.TryParse(parts[i].Trim(),out parsed))list.Add(parsed);}
            return list.ToArray();
        }
        static String[] ParseStrings(String value)
        {
            if(String.IsNullOrWhiteSpace(value))return new string[0];
            String[] parts=value.Split(',');List<String> list=new List<String>();
            for(int i=0;i<parts.Length;i++){String item=parts[i].Trim();if(item.Length>0)list.Add(item);}
            return list.ToArray();
        }
        static String JoinInts(int[] values)
        {
            if(values==null||values.Length==0)return "";
            StringBuilder sb=new StringBuilder();
            for(int i=0;i<values.Length;i++){if(i>0)sb.Append(',');sb.Append(values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));}
            return sb.ToString();
        }
        static String JoinStrings(String[] values)
        {
            if(values==null||values.Length==0)return "";
            return String.Join(",",values);
        }
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

        /// <summary>
        /// 把异常归到契约里的失败阶段（DNS/PROXY/TCP/TLS/AUTH/HTTP/PROTOCOL）。
        /// 用途是让云端能说「断在哪一段」而不是笼统一句「网络错误」；认不出来就返回 null，不猜。
        /// </summary>
        public static string StageOf(Exception error)
        {
            if(error==null)return null;
            CloudHttpException http=error as CloudHttpException;
            if(http!=null)return http.StatusCode==407?CloudClientStage.Proxy:(http.StatusCode==401||http.StatusCode==403?CloudClientStage.Auth:CloudClientStage.Http);
            if(error is TimeoutException)return CloudClientStage.Tcp;
            WebException web=error as WebException;
            if(web!=null)
            {
                switch(web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:case WebExceptionStatus.ProxyNameResolutionFailure:return CloudClientStage.Dns;
                    case WebExceptionStatus.ConnectFailure:
                        string socket=SocketCategory(web.InnerException);
                        if(socket!=null&&socket.StartsWith("DNS",StringComparison.Ordinal))return CloudClientStage.Dns;
                        return web.InnerException is AuthenticationException?CloudClientStage.Tls:CloudClientStage.Tcp;
                    case WebExceptionStatus.TrustFailure:case WebExceptionStatus.SecureChannelFailure:return CloudClientStage.Tls;
                    case WebExceptionStatus.Timeout:return CloudClientStage.Tcp;
                    case WebExceptionStatus.ProtocolError:
                        HttpWebResponse response=web.Response as HttpWebResponse;
                        if(response==null)return CloudClientStage.Http;
                        int code=(int)response.StatusCode;
                        return code==407?CloudClientStage.Proxy:(code==401||code==403?CloudClientStage.Auth:CloudClientStage.Http);
                    default:return CloudClientStage.Tcp;
                }
            }
            string inner=SocketCategory(error);
            if(inner!=null)return inner.StartsWith("DNS",StringComparison.Ordinal)?CloudClientStage.Dns:CloudClientStage.Tcp;
            if(error is AuthenticationException)return CloudClientStage.Tls;
            if(error is InvalidDataException||error is System.Runtime.Serialization.SerializationException)return CloudClientStage.Protocol;
            if(error is IOException)return CloudClientStage.Tcp;
            return null;
        }
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
