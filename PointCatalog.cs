using System;
using System.Collections.Generic;
using System.Globalization;
namespace ExperimentMonitor {
    public sealed class PointInfo {
        public string Name,Label,Group,Unit,Mode;
        /// <summary>力控工程原说明/画面名，用于对照；不覆盖现场使用的短标签。</summary>
        public string Alias="";
        public bool ScaleConfirmed=true;
        public double Scale=1;
    }
    public static class PointCatalog {
        public const string Version="catalog-2";
        static readonly Dictionary<string,PointInfo> entries=new Dictionary<string,PointInfo>(StringComparer.Ordinal);
        static void Add(string id,string label,string group,string unit,string mode,bool confirmed=true,string alias="") {
            entries[id]=new PointInfo{Name=id,Label=label,Group=group,Unit=unit,Mode=mode,ScaleConfirmed=confirmed,Alias=alias};
        }
        static PointCatalog() {
            // 温度：标签沿用 0.5 的画面名；alias 记录力控点表 DESC，避免把两套命名混成一个。
            Add("T0","相变1","温度","℃","FLOAT ABCD",true,"2203相变温度1");
            Add("T1","水箱","温度","℃","INT16",true,"303相变温度2");
            Add("T2","送风","温度","℃","FLOAT ABCD",true,"2205出风温度");
            Add("T3","环境","温度","℃","FLOAT ABCD",true,"2204环境温度");
            Add("T4","送水","温度","℃","FLOAT ABCD",true,"345送水温度");
            Add("T5","回水","温度","℃","FLOAT ABCD",true,"347回水温度");
            Add("D2206","相变2","温度","℃","FLOAT ABCD",true,"温度4");
            // 实验电表 DS666。D6(8202) 的实测 P/S 关系支持功率因数，力控点表 DESC 的“瞬时总功”不采用。
            Add("D1","电压","实验电表","V","FLOAT ABCD",true,"8192电表电压");
            Add("D2","电流","实验电表","A","FLOAT ABCD",true,"8194电表电流");
            Add("D3","有功功率","实验电表","W · 倍率待核准","FLOAT ABCD",false,"8196瞬时有功功率");
            Add("D4","无功功率","实验电表","var · 倍率待核准","FLOAT ABCD",false,"8198瞬时无功");
            Add("D5","视在功率","实验电表","VA · 倍率待核准","FLOAT ABCD",false,"8200瞬时视在功率");
            Add("D6","功率因数","实验电表","","FLOAT ABCD",true,"8202瞬时总功（力控点表）");
            Add("D7","频率","实验电表","Hz","FLOAT ABCD",true,"8206电网频率");
            Add("D8","累计电能","实验电表","kWh","FLOAT ABCD",true,"16384有功总电能");
            // 市电 DDSU666。D9(8204) 与力控画面注释 200AH/8202 冲突，暂不叫功率因数。
            Add("DU1","市电电压","市电","V","FLOAT ABCD",true,"市电电压");
            Add("DU2","市电电流","市电","A","FLOAT ABCD",true,"市电电流");
            Add("DU3","有功功率","市电","W · 倍率待核准","FLOAT ABCD",false,"市电瞬时功率");
            Add("DU6","原始指标8202","市电","","FLOAT ABCD",false,"市电总功（力控点表）");
            Add("DU7","频率","市电","Hz","FLOAT ABCD",true,"市电电网频率");
            Add("DU8","累计电能","市电","kWh","FLOAT ABCD",true,"市电总电能");
            Add("D9","原始指标8204","市电","","FLOAT ABCD",true,"功率因数（力控点表；待现场裁决）");
            // 太阳能 DJSF6682。
            Add("ZU6682","太阳能直流电压","太阳能","V","UINT16",false,"太阳能发电电压");
            Add("ZI6682","太阳能直流电流","太阳能","A","UINT16",false,"太阳能电流");
            Add("ZW66822","视在功率","太阳能","VA","UINT16",false,"太阳能总视在功率");
            // 控制/设定：可写候选，但仍需 control-config.json 的 confirmed+allowWrite 才允许 06。
            Add("D2000","送风风机速度","控制/设定","原始码","U16 / I16",true,"送风风机速度");
            Add("D2001","模式控制","控制/设定","原始码","U16 / I16",true,"模式控制");
            Add("D2202","主机温度设定","控制/设定","原始码","U16 / I16",true,"主机温度设定");
            Add("D2207","放冷反馈设定","控制/设定","原始码","U16 / I16",true,"放冷反馈设定");
            // 只读状态/联锁。
            Add("D2209","风机状态","运行状态","原始码","U16 / I16",true,"风机状态");
            Add("D2210","当前模式","运行状态","原始码","U16 / I16",true,"当前模式");
            Add("D2213","水泵","运行状态","原始码","U16 / I16",true,"水泵");
            Add("D2211","制冷主机","运行状态","原始码","U16 / I16",true,"制冷主机");
            Add("D2212","冷量反馈","运行状态","原始码","U16 / I16",true,"冷量反馈");
            Add("D2208","总故障","运行状态","原始码","U16 / I16",true,"总故障");
            Add("D2003","远程/本地","运行状态","原始码","U16 / I16",true,"远程/本地");
            Add("0006H","原寄存器","诊断","原始码","RAW",true,"006H");
        }
        public static PointInfo Get(string id) {
            PointInfo p;if(entries.TryGetValue(id,out p)) return p;
            return new PointInfo{Name=id,Label=id,Group="其他测点",Unit="",Mode="RAW",ScaleConfirmed=false};
        }
        public static PointInfo Get(Point p) { var meta=Get(p.name);if(meta.Group=="其他测点") {meta.Label=p.description;meta.Mode=Modbus.DefaultMode(p);}return meta; }
        public static string Display(Observation o) {
            if(o.Quality!="good") return "—";
            return o.Number.HasValue?o.Number.Value.ToString("0.###",CultureInfo.InvariantCulture):o.Value;
        }
    }
}
