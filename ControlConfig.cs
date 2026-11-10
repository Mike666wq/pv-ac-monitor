using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>控制功能的本地配置。默认关闭；只有 confirmed && allowWrite 的目标才允许真实写入。</summary>
    public sealed class ControlTarget {
        /// <summary>kind 是**被校验的配置契约**：程序只支持 16 位单寄存器写入，
        /// 配置里写成别的类型会在加载时直接被拒（见 Validate）。它不是"没实现"的字段 ——
        /// 它守的是"别把一个 32 位目标当成 16 位写"这件事。</summary>
        public string point="",label="",kind="int16",unit="",note="";
        public int slave,address;
        public double min,max;
        public bool confirmed,allowWrite;
        /// <summary>
        /// 因果判据：写入这个点之后，真正能证明"设备动作了"的返回信号。
        /// 空列表 ⇒ 结构上无法判定"设备已动作"（不是靠文案提醒，是数据结构上产不出这个结论）。
        /// 现场已证伪的联动不要写进来：D2209（写 D2000 并不移动 D2209）、
        /// D2212（PLC 定时斜坡指令，自己就会变）。写入的是"因果"，不是"看起来会变的点"。
        /// </summary>
        public List<string> feedbackPoints=new List<string>();
        public override string ToString(){return point+" "+label;}
    }
    public sealed class ControlInterlocks {
        public string remotePoint="D2003";public int? remoteExpected=null;
        public string faultPoint="D2208";public int faultExpected=0;
        public int maxAgeSeconds=10;
    }
    public sealed class ControlLimits {public int minIntervalMs=1000,maxWritesPerMinute=6,verifyWindowMs=1500,observeFeedbackSeconds=10;}
    /// <summary>控制页/引擎共用的门禁结果。Status=="ready" 才允许发送 06。</summary>
    public sealed class ControlGateInfo {
        public string Status="",Detail="";
        public bool CanWrite{get{return Status=="ready";}}
    }
    public sealed class ControlCommand {
        public string Point="",Operator="";public int Value;
        /// <summary>人工核对时的理由码：读数既不是写前旧值也不是写入新值时，必填。</summary>
        public string Reason="";
    }
    public sealed class ControlResult {
        public string Status="",Detail="",Point="",Label="",Operator="",WriteId="";
        public int Slave,Address,NewValue;public int? OldValue;public int? Readback;public bool HasReadback;
        public byte[] Tx=new byte[0],Rx=new byte[0];public long Milliseconds;public DateTime Utc=DateTime.UtcNow;
        public bool AuditOk=true;
        /// <summary>调试用：写前 03 预读与写后 03 回读的原始请求/响应帧，以及旧值来源。</summary>
        public byte[] PreReadTx=new byte[0],PreReadRx=new byte[0],ReadbackTx=new byte[0],ReadbackRx=new byte[0];
        public string OldSource="";public long ReadbackMs;
        public bool Applied{get{return Status=="applied";}}
    }
    public sealed class ControlEvent {
        public long UtcTicks;public string Source="",Session="",Operator="",Point="",Label="";
        public int Slave,Address,Function=6;public int? OldValue,NewValue,Readback;
        public string Status="",Detail="",Tx="",Rx="",ConfigVersion="";public long ElapsedMs;
        /// <summary>一笔写入的唯一标识。同一笔写入只允许有一个终态。</summary>
        public string WriteId="";
        /// <summary>证据串：tx|rx|pre|rb|effect|fb|gate|note|ms|epoch|noise|decision。
        /// 同一批信息同生共死放进一列，避免给审计表反复加列。</summary>
        public string Evidence="";
    }
    public sealed class ControlConfig {
        public int schemaVersion=1;
        /// <summary>
        /// 控制模式。
        ///   disabled = 控制完全关闭（默认）。
        ///   live     = 允许真实写入（0.8 起唯一表示"真的写"的取值）。
        ///   preview  = 0.7.x 的旧写法。在旧版本里它**等同于真实写入**，所以这里按旧行为继续执行
        ///               真实写入，并在启动时告警一次，避免老配置静默变成"不写"。
        /// 预演（只生成 06 报文、不发送）一直都有，是界面上的独立按钮，与 mode 无关。
        /// </summary>
        public string mode="disabled";
        public int autoLockIdleSeconds=0;
        /// <summary>联锁不满足时是否拦截发送。默认 false：联锁只做提示，现场联锁语义未确认时不阻断测试。</summary>
        public bool enforceInterlocks=false;
        /// <summary>
        /// 已废弃（0.8 起不再影响任何门禁）。**保留这个字段是有意的**：
        /// 现场的老配置文件里可能还写着它，而回归测试要证明"配置里写了 requireConfirmed=false
        /// 也照样挡得住" —— 字段在，才写得出来、才验得掉。
        /// confirmed 与 allowWrite 现在**各自独立生效**：
        /// 只要目标点 confirmed=false 或 allowWrite=false，就不允许真实写入，与本字段无关。
        /// 保留字段只为兼容旧配置文件；界面上会明确标出"已废弃"。
        /// </summary>
        public bool requireConfirmed=false;
        public ControlInterlocks interlocks=new ControlInterlocks();
        public ControlLimits limits=new ControlLimits();
        public List<ControlTarget> targets=new List<ControlTarget>();
        /// <summary>写后要观察的返回信号（风机/主机/水泵/冷量/故障/当前模式）。</summary>
        public List<string> feedbackPoints=new List<string>{"D2208","D2209","D2210","D2211","D2212","D2213"};
        [ScriptIgnore] public string Version="none";

        public static ControlConfig Disabled(){return new ControlConfig();}
        /// <summary>是否允许真实写入。preview 是旧写法，等价于 live。</summary>
        public bool WritesEnabled{get{return String.Equals(mode,"live",StringComparison.OrdinalIgnoreCase)||String.Equals(mode,"preview",StringComparison.OrdinalIgnoreCase);}}
        /// <summary>兼容旧配置时需要提示的话；空串表示没有需要提醒的地方。</summary>
        [ScriptIgnore] public string ModeCompatibilityWarning{get{return String.Equals(mode,"preview",StringComparison.OrdinalIgnoreCase)?"control-config.json 的 mode=preview 是 0.7.x 的旧写法：旧版本里它等同于真实写入，这里按旧行为继续执行真实写入。只想预演请使用界面上的预演按钮，不要依赖 mode。":"";}}
        public ControlTarget Find(string point){
            if(String.IsNullOrEmpty(point)||targets==null)return null;
            return targets.FirstOrDefault(t=>t!=null&&String.Equals(t.point,point,StringComparison.Ordinal));
        }
        /// <summary>从运行目录读取；缺文件/损坏/校验失败都退化为 disabled，并返回原因。</summary>
        public static ControlConfig Load(string root,List<Point> points,out string error){
            error="";
            string path=Path.Combine(root,"control-config.json");
            if(!File.Exists(path))return Disabled();
            try{
                byte[] bytes=File.ReadAllBytes(path);
                var config=new JavaScriptSerializer{MaxJsonLength=1024*1024}.Deserialize<ControlConfig>(Encoding.UTF8.GetString(bytes));
                if(config==null){error="control-config.json 为空";return Disabled();}
                if(config.interlocks==null)config.interlocks=new ControlInterlocks();
                if(config.limits==null)config.limits=new ControlLimits();
                if(config.targets==null)config.targets=new List<ControlTarget>();
                if(config.feedbackPoints==null||config.feedbackPoints.Count==0)config.feedbackPoints=new List<string>{"D2208","D2209","D2210","D2211","D2212","D2213"};
                using(var sha=SHA256.Create())config.Version=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").Substring(0,16);
                string problem=config.Validate(points);
                if(!String.IsNullOrEmpty(problem)){error=problem;return config;}
                return config;
            }catch(Exception e){
                error="control-config.json 无法解析："+e.Message;
                return Disabled();
            }
        }
        /// <summary>结构校验；返回空串表示可用。校验失败时调用方必须按 disabled 处理。</summary>
        public string Validate(List<Point> points){
            // 0.8.0 起 mode 有三个合法值：disabled / live / preview（preview 是旧写法，等价于 live）。
            // 白名单漏掉 live 会让随包配置被判为"无法解析"，门禁直接进 config_error ——
            // 写入确实是挡住了，但提示词变成"mode 只能为 disabled 或 preview"，与随包文件自相矛盾。
            if(mode!="disabled"&&mode!="live"&&mode!="preview")
                return "mode 只能为 disabled / live / preview，收到「"+mode+"」（armed 是会话内状态，不写入文件）";
            if(String.IsNullOrWhiteSpace(interlocks.remotePoint)||String.IsNullOrWhiteSpace(interlocks.faultPoint))return "联锁点不能为空";
            if(interlocks.maxAgeSeconds<1||interlocks.maxAgeSeconds>3600)return "maxAgeSeconds 必须在 1..3600 秒";
            if(interlocks.remoteExpected.HasValue&&(interlocks.remoteExpected.Value<0||interlocks.remoteExpected.Value>65535))return "remoteExpected 超出 16 位范围";
            if(interlocks.faultExpected<0||interlocks.faultExpected>65535)return "faultExpected 超出 16 位范围";
            if(limits.minIntervalMs<100||limits.minIntervalMs>600000)return "minIntervalMs 必须在 100..600000";
            if(limits.maxWritesPerMinute<1||limits.maxWritesPerMinute>600)return "maxWritesPerMinute 必须在 1..600";
            if(limits.verifyWindowMs<100||limits.verifyWindowMs>30000)return "verifyWindowMs 必须在 100..30000";
            if(String.IsNullOrWhiteSpace(interlocks.remotePoint)||points==null)return "点表缺失";
            Point remote=points.FirstOrDefault(p=>p.name==interlocks.remotePoint);
            Point fault=points.FirstOrDefault(p=>p.name==interlocks.faultPoint);
            if(remote==null||remote.binding==null||remote.binding.register_count!=1)return "联锁点 "+interlocks.remotePoint+" 不是单寄存器可读点";
            if(fault==null||fault.binding==null||fault.binding.register_count!=1)return "联锁点 "+interlocks.faultPoint+" 不是单寄存器可读点";
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(string feedback in feedbackPoints){
                Point point=points.FirstOrDefault(p=>p.name==feedback);
                if(point==null||point.binding==null||point.binding.register_count!=1)return "feedbackPoints 中的 "+feedback+" 不是单寄存器可读点";
            }
            foreach(var target in targets){
                if(target==null)return "targets 含空项";
                if(String.IsNullOrWhiteSpace(target.point))return "target 缺少 point";
                if(!seen.Add(target.point))return "target 重复："+target.point;
                Point point=points.FirstOrDefault(p=>p.name==target.point);
                if(point==null||point.binding==null)return "target 不属于已绑定测点："+target.point;
                if(point.binding.register_count!=1)return target.point+" 不是单寄存器点，v1 只支持 16 位单寄存器";
                if(target.slave!=point.binding.slave)return target.point+" 站号与 protocol.json 不一致";
                if(target.address!=point.binding.address_zero_based)return target.point+" 地址与 protocol.json 不一致";
                if(target.kind!="int16")return target.point+" kind 仅支持 int16";
                if(target.min<0||target.max>65535||target.min>target.max)return target.point+" 写入范围必须为 0..65535 且 min<=max";
                if(target.max<=target.min)return target.point+" max 必须大于 min（0..0 只能写 0，禁止）";
                }
            return "";
        }
        public static string Describe(ControlTarget t){
            if(t==null)return "—";
            return t.label+"（"+t.point+"）";
        }
        public static string FormatValue(ControlTarget t,double value){
            return t==null?value.ToString("G9",CultureInfo.InvariantCulture):value.ToString("0.###",CultureInfo.InvariantCulture)+(String.IsNullOrEmpty(t.unit)?"":" "+t.unit);
        }
    }
}
