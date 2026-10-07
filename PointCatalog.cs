using System;
using System.Collections.Generic;
using System.Globalization;
namespace ExperimentMonitor {
    public sealed class PointInfo {
        public string Name,Label,Group,Unit,Mode;
        public bool ScaleConfirmed=true;
        public double Scale=1;
    }
    public static class PointCatalog {
        public const string Version="catalog-1";
        static readonly Dictionary<string,PointInfo> entries=new Dictionary<string,PointInfo>(StringComparer.Ordinal);
        static void Add(string id,string label,string group,string unit,string mode,bool confirmed=true) {
            entries[id]=new PointInfo{Name=id,Label=label,Group=group,Unit=unit,Mode=mode,ScaleConfirmed=confirmed};
        }
        static PointCatalog() {
            string[] ids={"T0","T1","T2","T3","T4","T5","D2206"};
            string[] labels={"相变1","水箱","送风","环境","送水","回水","相变2"};
            for(int i=0;i<ids.Length;i++) Add(ids[i],labels[i],"温度","℃",ids[i]=="T1"?"INT16":"FLOAT ABCD");
            string[] electric={"空调外机电压","空调外机电流","有功功率","无功功率","视在功率","功率因数","频率","累计电能"};
            string[] units={"V","A","W","var","VA","","Hz","kWh"};
            for(int i=0;i<8;i++) Add("D"+(i+1),electric[i],"实验电表",units[i],"FLOAT ABCD",i<2||i>4);
            string[] mains={"DU1","DU2","DU3","DU6","DU7","DU8","D9"};
            string[] mainLabels={"市电电压","市电电流","有功功率","原始指标8202","频率","累计电能","功率因数"};
            string[] mainUnits={"V","A","W","","Hz","kWh",""};
            for(int i=0;i<mains.Length;i++) Add(mains[i],mainLabels[i],"市电",mainUnits[i],"FLOAT ABCD",i!=2&&i!=3);
            Add("ZU6682","太阳能直流电压","太阳能","V","UINT16",false);
            Add("ZI6682","太阳能直流电流","太阳能","A","UINT16",false);
            Add("ZW66822","视在功率","太阳能","VA","UINT16",false);
            string[] stateIds={"D2000","D2209","D2210","D2213","D2207","D2211","D2202","D2212","D2003","D2001","D2208"};
            string[] stateLabels={"送风风机","风机状态","当前模式","水泵","放冷反馈","制冷主机","主机设定","冷量反馈","远程/本地","模式控制","总故障"};
            for(int i=0;i<stateIds.Length;i++) Add(stateIds[i],stateLabels[i],"运行状态","原始码","U16 / I16");
            Add("0006H","原寄存器","诊断","原始码","RAW");
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
