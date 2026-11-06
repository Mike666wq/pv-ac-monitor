using System;
using System.Collections.Generic;
using System.Globalization;
namespace ExperimentMonitor {
    public sealed class PointInfo {
        public string Name,Label,Group,Unit,Mode;
        /// <summary>力控工程原说明/画面名，用于对照；不覆盖现场使用的短标签。</summary>
        public string Alias="";
        public bool ScaleConfirmed=true;
        /// <summary>工程值 = 原始值 × Scale。原始值（RawNumber）与报文始终保留，不因换算丢失。</summary>
        public double Scale=1;
    }
    public static class PointCatalog {
        public const string Version="catalog-3";
        static readonly Dictionary<string,PointInfo> entries=new Dictionary<string,PointInfo>(StringComparer.Ordinal);
        static void Add(string id,string label,string group,string unit,string mode,bool confirmed=true,string alias="",double scale=1.0) {
            entries[id]=new PointInfo{Name=id,Label=label,Group=group,Unit=unit,Mode=mode,ScaleConfirmed=confirmed,Alias=alias,Scale=scale};
        }
        static PointCatalog() {
            // 温度：标签沿用 0.5 的画面名；alias 记录力控点表 DESC，避免把两套命名混成一个。
            // 温度是 PLC 内部算好的浮点结果，PC 端不再套公式（老手稿的 0.025x-50 只作诊断提示）。
            Add("T0","相变1","温度","℃","FLOAT ABCD",true,"2203相变温度1");
            Add("T1","水箱","温度","℃","INT16",true,"303相变温度2");
            Add("T2","送风","温度","℃","FLOAT ABCD",true,"2205出风温度");
            Add("T3","环境","温度","℃","FLOAT ABCD",true,"2204环境温度");
            Add("T4","送水","温度","℃","FLOAT ABCD",true,"345送水温度");
            Add("T5","回水","温度","℃","FLOAT ABCD",true,"347回水温度");
            Add("D2206","相变2","温度","℃","FLOAT ABCD",true,"温度4");
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
            // 力控点表把 0x200C 记成“功率因数”，但 DDSU666 说明书 0x200C = RESERVED（保留，float 2 字）。
            // 保留采集（保持与历史/力控一致），但不再当作有物理含义的量。
            Add("D9","保留(0x200C)","市电","","FLOAT ABCD",false,"力控点表写功率因数；手册 0x200C=RESERVED");
            // 太阳能 DJSF6682（站号 1，直流表 Modbus 地址表 2026-11-06 截图）：
            // 0x0000 电压 int16 ×0.1V；0x0003 电流 int16 ×0.001A；0x000F 视在功率 int16 ×1VA。
            // 表里写的是 Int（有符号）：反向电流/夜间倒灌是负数，用 UINT16 会显示成 65xxx。
            // 截图红字注明电流/功率是“二次侧数据，需 ×分流器倍数”，倍数未知，故两点仍标记未确认。
            Add("ZU6682","太阳能直流电压","太阳能","V","INT16",true,"太阳能发电电压",0.1);
            Add("ZI6682","太阳能直流电流","太阳能","A（二次侧）","INT16",false,"太阳能电流",0.001);
            Add("ZW66822","视在功率","太阳能","VA（二次侧）","INT16",false,"太阳能总视在功率",1.0);
            // 控制/设定：可写候选，但仍需 control-config.json 的 confirmed+allowWrite 才允许 06。
            // 16 位点统一按 INT16（FX 系列 D 寄存器是“最高位为符号”的 16 位有符号数），否则 Decode 会走
            // “U16 / I16”兜底分支、只出文本不出数值，趋势/历史/导出里全是空值。
            Add("D2000","送风风机速度","控制/设定","原始码","INT16",true,"送风风机速度");
            Add("D2001","模式控制","控制/设定","原始码","INT16",true,"模式控制");
            Add("D2202","主机温度设定","控制/设定","原始码","INT16",true,"主机温度设定");
            Add("D2207","放冷反馈设定","控制/设定","原始码","INT16",true,"放冷反馈设定");
            // 只读状态/联锁。
            Add("D2209","风机档位","运行状态","原始码","INT16",true,"风机状态（力控 DESC；2026-11-06 实测为档位 1/2/3/0）");
            Add("D2210","当前模式","运行状态","原始码","INT16",true,"当前模式");
            Add("D2213","水泵","运行状态","原始码","INT16",true,"水泵（实测 16:20/16:22 有 1→0→1 跳变，符合启停）");
            Add("D2211","制冷主机","运行状态","原始码","INT16",true,"制冷主机（压缩机未启动时已为 1，疑为就绪/允许）");
            Add("D2212","压缩机频率/负载","运行状态","原始码","INT16",true,"冷量反馈（力控 DESC）；实测为压缩机启动后每 40 秒 +5 的斜坡 5→50，疑为变频频率/负载率");
            Add("D2208","总故障","运行状态","原始码","INT16",true,"总故障");
            Add("D2003","远程/本地","运行状态","原始码","INT16",true,"远程/本地");
            Add("0006H","原寄存器(位串)","诊断","原始码","RAW",true,"006H");
        }
        public static PointInfo Get(string id) {
            PointInfo p;if(entries.TryGetValue(id,out p)) return p;
            return new PointInfo{Name=id,Label=id,Group="其他测点",Unit="",Mode="RAW",ScaleConfirmed=false};
        }
        public static PointInfo Get(Point p) { var meta=Get(p.name);if(meta.Group=="其他测点") {meta.Label=p.description;meta.Mode=Modbus.DefaultMode(p);}return meta; }
        public static string Display(Observation o) {
            if(!Quality.IsGood(o.Quality)) return "—";
            return o.Number.HasValue?o.Number.Value.ToString("0.####",CultureInfo.InvariantCulture):o.Value;
        }
    }
}
