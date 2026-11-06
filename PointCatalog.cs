using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ExperimentMonitor {
    /// <summary>
    /// 测点的角色。这一维解决的是"这个数字是读到的，还是命令的"——
    /// 两者混在一起展示，使用者会把"设定 8"误读成"实测 8"。
    /// </summary>
    public static class PointRole {
        /// <summary>传感器/仪表实测值（温度、电压、电流、功率、电能、频率）。</summary>
        public const string Measurement="measurement";
        /// <summary>控制指令/设定值。由 PLC 自己持有并执行，PC 端只读；数值是"要求多少"，不是"现在多少"。</summary>
        public const string Setpoint="setpoint";
        /// <summary>状态/反馈位（故障、档位、模式、就绪、计时）。</summary>
        public const string Status="status";
        /// <summary>诊断用位串。</summary>
        public const string Diagnostic="diagnostic";
        public static bool IsSetpoint(string role) { return String.Equals(role,Setpoint,StringComparison.Ordinal); }
        public static bool IsMeasurement(string role) { return String.Equals(role,Measurement,StringComparison.Ordinal); }
    }
    /// <summary>
    /// 点表的元数据。字段全部只读（私有 setter）：点表一旦建好就不允许任何调用方就地改写。
    /// 旧实现把字段做成公开可变，`Get(Point)` 里还会就地改写共享实例的 Label/Mode ——
    /// 一次调用就能污染整个进程里的点表，多线程下还会撕裂 Mode 与 Name 的对应关系。
    /// 改成"构造后不可变"比"每次返回副本"更省，也把问题从"记得别改"变成"改不了"。
    /// </summary>
    public sealed class PointInfo {
        public string Name { get; private set; }
        public string Label { get; private set; }
        public string Group { get; private set; }
        /// <summary>只放量纲符号（℃ / V / A / kW / Hz / kWh）。一切限定语进 UnitQualifier。</summary>
        public string Unit { get; private set; }
        /// <summary>单位限定语，例如"二次侧"。把它从单位串里拿出来，
        /// 才能避免"A（二次侧）"这种写法在卡片上被截断成"A（二..."。</summary>
        public string UnitQualifier { get; private set; }
        public string Mode { get; private set; }
        /// <summary>力控工程原说明/画面名，用于对照；不覆盖现场使用的短标签。</summary>
        public string Alias { get; private set; }
        /// <summary>倍率是否已由现场证据确认。</summary>
        public bool ScaleConfirmed { get; private set; }
        /// <summary>工程值 = 原始值 × Scale。原始值（RawNumber）与报文始终保留，不因换算丢失。</summary>
        public double Scale { get; private set; }
        /// <summary>
        /// 语义是否已现场裁决。未裁决的点可以显示，但不能进结论区
        /// （告警、统计、联锁、阈值、导出"生效数"），界面上要带存疑标记。
        /// </summary>
        public bool SemanticConfirmed { get; private set; }
        /// <summary>角色：measurement / setpoint / status / diagnostic。见 PointRole。</summary>
        public string Role { get; private set; }
        /// <summary>
        /// 配对点：对设定值来说，是"反映这条指令效果的实测点"；
        /// 对实测点来说，是"驱动它的设定值"。同一对的两个点互相指向对方。
        /// 有这个配对，才能把"设定 vs 实测 vs 偏差"三者一起算出来。
        /// </summary>
        public string PairedWith { get; private set; }
        /// <summary>未登记到点表的点。只在 --allow-unknown-points 下才允许出现。</summary>
        public bool Unknown { get; private set; }

        internal PointInfo(string name,string label,string group,string unit,string mode,bool confirmed,string alias,double scale,string unitQualifier,bool semanticConfirmed,string role,string pairedWith) {
            Name=name;Label=label;Group=group;Unit=unit;Mode=mode;ScaleConfirmed=confirmed;Alias=alias??"";
            Scale=scale;UnitQualifier=unitQualifier??"";SemanticConfirmed=semanticConfirmed;Unknown=false;
            Role=String.IsNullOrEmpty(role)?PointRole.Measurement:role;PairedWith=pairedWith??"";
        }
        internal static PointInfo MakeUnknown(string name,string mode) {
            return new PointInfo(name??"",name??"","未登记点","","",false,"",1.0,"",false,PointRole.Measurement,""){Unknown=true};
        }
    }
    public static class PointCatalog {
        static readonly Dictionary<string,PointInfo> entries=new Dictionary<string,PointInfo>(StringComparer.Ordinal);

        /// <summary>
        /// 点表版本号：内容指纹。
        /// 以前这里是手写的常量（catalog-3），改了点表内容而版本不变，历史数据就无法判断
        /// 当初是用哪一版点表解释的。现在按内容算，改任何一处标签/单位/倍率/模式都会自动换版本。
        /// </summary>
        public static readonly string Version;

        /// <summary>
        /// 是否允许采集未登记到点表的点（--allow-unknown-points）。
        /// 默认拒绝：点表漏登记时应该立刻发现，而不是让那个点以"RAW/无单位"的身份
        /// 悄悄流进趋势、历史和导出。但拒绝不能做成"起不来" —— 现场一次漏改就整机不可用
        /// 是不可接受的，所以留了这个逃生开关，并配一条启动横幅。
        /// </summary>
        public static bool AllowUnknown { get; set; }

        /// <summary>
        /// 角色与配对表。集中声明而不是散在 37 个 Add 调用里，才能一眼核对
        /// "哪些是命令、哪些是读数、每个命令对哪个实测"。
        /// 配对依据是现场数据的定量核对：
        ///   D2202 ↔ T1  ：压缩机运转时 T1 落在 D2202±1 K（42%），停机后 T1 漂到环境温度（偏差最大 +21 K）
        ///   D2000 ↔ D2209：D2000=50 持续 71 轮，同期 D2209 恒为 0 ⇒ 这条联动已被证伪
        ///   D2001 ↔ D2210：两者恒为 0，无法证明关系
        ///   D2207       ：无配对量
        /// </summary>
        static readonly Dictionary<string,KeyValuePair<string,string>> roles=new Dictionary<string,KeyValuePair<string,string>>(StringComparer.Ordinal){
            {"D2202",new KeyValuePair<string,string>(PointRole.Setpoint,"T1")},
            {"D2000",new KeyValuePair<string,string>(PointRole.Setpoint,"D2209")},
            {"D2001",new KeyValuePair<string,string>(PointRole.Setpoint,"D2210")},
            {"D2207",new KeyValuePair<string,string>(PointRole.Setpoint,"")},
            {"T1",new KeyValuePair<string,string>(PointRole.Measurement,"D2202")},
            {"D2209",new KeyValuePair<string,string>(PointRole.Status,"D2000")},
            {"D2210",new KeyValuePair<string,string>(PointRole.Status,"D2001")},
            {"D2208",new KeyValuePair<string,string>(PointRole.Status,"")},
            {"D2211",new KeyValuePair<string,string>(PointRole.Status,"")},
            {"D2212",new KeyValuePair<string,string>(PointRole.Status,"")},
            {"D2213",new KeyValuePair<string,string>(PointRole.Status,"")},
            {"D2003",new KeyValuePair<string,string>(PointRole.Status,"")},
            {"0006H",new KeyValuePair<string,string>(PointRole.Diagnostic,"")}
        };
        static void Add(string id,string label,string group,string unit,string mode,bool confirmed=true,string alias="",double scale=1.0,string unitQualifier="",bool semanticConfirmed=true) {
            KeyValuePair<string,string> pair;
            string role=PointRole.Measurement,paired="";
            if(roles.TryGetValue(id,out pair)){role=pair.Key;paired=pair.Value;}
            entries[id]=new PointInfo(id,label,group,unit,mode,confirmed,alias,scale,unitQualifier,semanticConfirmed,role,paired);
        }

        static PointCatalog() {
            // 温度：标签沿用 0.5 的画面名；alias 记录力控点表 DESC，避免把两套命名混成一个。
            // 温度是 PLC 内部算好的浮点结果，PC 端不再套公式（老手稿的 0.025x-50 只作诊断提示）。
            Add("T0","相变1","温度","℃","FLOAT ABCD",true,"2203相变温度1");
            // T1 的身份与符号仍待现场裁决（倍率=1 ℃/LSB 已两次抓包证实：24↔24℃、3↔3℃）。
            // 不写"水箱"（那是推测），也不写"相变2"（那个名字属于 T1 的力控 DESC，但已被 D2206 占用）。
            Add("T1","主机罐温度","温度","℃","INT16",true,"303相变温度2",1.0,"",false);
            Add("T2","送风","温度","℃","FLOAT ABCD",true,"2205出风温度");
            Add("T3","环境","温度","℃","FLOAT ABCD",true,"2204环境温度");
            Add("T4","送水","温度","℃","FLOAT ABCD",true,"345送水温度");
            Add("T5","回水","温度","℃","FLOAT ABCD",true,"347回水温度");
            // 与 T1 同期差 18 K：数据否决了"两者是同一探头副本"的假设。力控的"温度4"才是它的原名。
            Add("D2206","温度4","温度","℃","FLOAT ABCD",true,"温度4（力控原名）",1.0,"",false);
            // 实验电表 DS666（站号 3）。0x2004/0x2006/0x2008 在真实数据里与 U×I 差 1000 倍，
            // 且 D3/D5 = PF（0.847/0.0089÷0.0105），所以单位是 kW/kvar/kVA，不是 W/var/VA。
            Add("D1","电压","实验电表","V","FLOAT ABCD",true,"8192电表电压");
            Add("D2","电流","实验电表","A","FLOAT ABCD",true,"8194电表电流");
            Add("D3","有功功率","实验电表","kW","FLOAT ABCD",true,"8196瞬时有功功率");
            Add("D4","无功功率","实验电表","kvar","FLOAT ABCD",true,"8198瞬时无功");
            Add("D5","视在功率","实验电表","kVA","FLOAT ABCD",true,"8200瞬时视在功率");
            Add("D6","功率因数","实验电表","","FLOAT ABCD",true,"8202瞬时总功（力控点表写错）");
            Add("D7","频率","实验电表","Hz","FLOAT ABCD",true,"8206电网频率");
            Add("D8","累计电能","实验电表","kWh","FLOAT ABCD",true,"16384有功总电能");
            // 市电 DDSU666（站号 6），寄存器表见 DDSU666 说明书表 8：200AH=PF、200CH=RESERVED。
            Add("DU1","市电电压","市电","V","FLOAT ABCD",true,"市电电压");
            Add("DU2","市电电流","市电","A","FLOAT ABCD",true,"市电电流");
            Add("DU3","有功功率","市电","kW","FLOAT ABCD",true,"市电瞬时功率");
            Add("DU6","市电功率因数","市电","","FLOAT ABCD",true,"市电总功（力控点表写错）");
            Add("DU7","频率","市电","Hz","FLOAT ABCD",true,"市电电网频率");
            Add("DU8","累计电能","市电","kWh","FLOAT ABCD",true,"市电总电能");
            // 力控点表把 0x200C 记成"功率因数"，但 DDSU666 说明书 0x200C = RESERVED（保留，float 2 字）。
            // 手册标 R/W ⇒ 读它不抛异常、只返回无意义值，所以从采集计划里删掉，只保留目录说明。
            Add("D9","保留(0x200C)","市电","","FLOAT ABCD",false,"力控点表写功率因数；手册 0x200C=RESERVED",1.0,"",false);
            // 太阳能 DJSF6682（站号 1，直流表 Modbus 地址表 2026-11-06 截图）：
            // 0x0000 电压 int16 ×0.1V；0x0003 电流 int16 ×0.001A；0x000F 视在功率 int16 ×1VA。
            // 表里写的是 Int（有符号）：反向电流/夜间倒灌是负数，用 UINT16 会显示成 65xxx。
            // 截图红字注明电流/功率是"二次侧数据，需 ×分流器倍数"，倍数未知 → 标未确认。
            Add("ZU6682","太阳能直流电压","太阳能","V","INT16",true,"太阳能发电电压",0.1);
            // 点名保留 ZW66822（历史库里的行就是这个 id，改名会让历史数据无法与新点表对上）；
            // 但语义必须改：0x000F 在直流表上是有功功率（单相直流没有"视在"这一说），单位 VA→W。
            Add("ZI6682","太阳能直流电流","太阳能","A","INT16",false,"太阳能电流",0.001,"二次侧",false);
            Add("ZW66822","直流有功功率","太阳能","W","INT16",false,"太阳能总视在功率（力控）",1.0,"二次侧",false);
            // 控制/设定：可写候选，但仍需 control-config.json 的 confirmed+allowWrite 才允许 06。
            // 16 位点统一按 INT16（FX 系列 D 寄存器是"最高位为符号"的 16 位有符号数），否则 Decode 会走
            // "U16 / I16"兜底分支、只出文本不出数值，趋势/历史/导出里全是空值。
            Add("D2000","送风风机速度","控制/设定","原始码","INT16",true,"送风风机速度",1.0,"",false);
            Add("D2001","模式控制","控制/设定","原始码","INT16",true,"模式控制",1.0,"",false);
            // D2202 是设定值不是读数：它与 T1 同量纲（压缩机运转时 |T1-D2202|<=1K 占 42%，停机后漂到环境温度），
            // 因此单位按 ℃ 显示，并标"推断待确认" —— 这正是"读到的数值"与"命令的数值"必须分开的原因。
            Add("D2202","主机温度设定","控制/设定","℃","INT16",true,"主机温度设定（由 T1 跟随推断为℃，待现场确认）",1.0,"",false);
            Add("D2207","放冷反馈设定","控制/设定","原始码","INT16",true,"放冷反馈设定",1.0,"",false);
            // 只读状态/联锁。
            Add("D2209","风机档位","运行状态","原始码","INT16",true,"风机状态（力控 DESC；2026-11-06 实测为档位 1/2/3/0）");
            Add("D2210","当前模式","运行状态","原始码","INT16",true,"当前模式",1.0,"",false);
            Add("D2213","水泵","运行状态","原始码","INT16",true,"水泵（实测 16:20/16:22 有 1→0→1 跳变，符合启停）",1.0,"",false);
            Add("D2211","主机就绪","运行状态","原始码","INT16",true,"制冷主机（压缩机未启动时已为 1，疑为就绪/允许）",1.0,"",false);
            // D2212 的"频率/负载"必须从目录里删掉：实测压缩机启动后它每 40 秒 +5 爬到 50，
            // 全程电学特征都是 13 W 空载 —— 这是 PLC 的定时斜坡指令，不是测量量，更不是频率。
            Add("D2212","压缩机指令","运行状态","原始码","INT16",true,"冷量反馈（力控 DESC）；实测 0→5→…→50，全程 13 W 空载 ⇒ PLC 定时斜坡指令",1.0,"",false);
            Add("D2208","总故障","运行状态","原始码","INT16",true,"总故障");
            Add("D2003","远程/本地","运行状态","原始码","INT16",true,"远程/本地",1.0,"",false);
            Add("0006H","原寄存器(位串)","诊断","原始码","RAW",true,"006H",1.0,"",false);

            Version=Fingerprint();
        }

        /// <summary>点表内容指纹（SHA1 前 8 字节 hex）。</summary>
        static string Fingerprint() {
            var sb=new StringBuilder();
            foreach(var pair in entries.Values.OrderBy(e=>e.Name,StringComparer.Ordinal)) {
                sb.Append(pair.Name).Append('|').Append(pair.Label).Append('|').Append(pair.Group).Append('|')
                  .Append(pair.Unit).Append('|').Append(pair.UnitQualifier).Append('|').Append(pair.Mode).Append('|')
                  .Append(pair.Scale.ToString("R",CultureInfo.InvariantCulture)).Append('|')
                  .Append(pair.SemanticConfirmed?"1":"0").Append('\n');
            }
            using(var sha=SHA256.Create())
                return "catalog-4-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-","").Substring(0,16);
        }

        /// <summary>已登记点的元数据。返回的是不可变实例，调用方改不了它。</summary>
        public static PointInfo Get(string id) {
            PointInfo p;
            if(entries.TryGetValue(id??"",out p)) return p;
            return PointInfo.MakeUnknown(id,"RAW");
        }

        /// <summary>
        /// 按绑定取元数据。绝不就地改写共享实例 —— 旧实现在这里把 Label 换成 description、
        /// 把 Mode 换成猜出来的默认模式，于是同一次运行里同一个点前后两次 Get 可能给出不同结果。
        /// 未登记点返回一个"未登记"标记，模式按绑定推断，但不写回点表。
        /// </summary>
        public static PointInfo Get(Point p) {
            if(p==null) return PointInfo.MakeUnknown("","RAW");
            PointInfo meta;
            if(entries.TryGetValue(p.name??"",out meta)) return meta;
            return PointInfo.MakeUnknown(p.name,Modbus.DefaultMode(p));
        }

        /// <summary>点表里所有已登记的点名。</summary>
        public static IEnumerable<string> Names { get { return entries.Keys.OrderBy(k=>k,StringComparer.Ordinal); } }

        /// <summary>协议里引用了点表没登记的点（启动期校验用）。</summary>
        public static List<string> Unregistered(IEnumerable<Point> points) {
            var missing=new List<string>();
            if(points==null) return missing;
            foreach(var p in points) if(p!=null&&!String.IsNullOrEmpty(p.name)&&!entries.ContainsKey(p.name)) missing.Add(p.name);
            return missing;
        }

        public static string Display(Observation o) {
            if(!Quality.IsGood(o.Quality)) return "—";
            return o.Number.HasValue?o.Number.Value.ToString("0.####",CultureInfo.InvariantCulture):o.Value;
        }
    }
}
