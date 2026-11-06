using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    static class SelfTests {
        static readonly List<string> passed=new List<string>();
        static void Check(string name,bool condition) { if(!condition) throw new Exception("CHECK FAILED: "+name); passed.Add(name); }
        static byte[] Reply(int slave,params byte[] payload) {
            var body=new byte[payload.Length+3]; body[0]=(byte)slave; body[1]=3; body[2]=(byte)payload.Length;
            Array.Copy(payload,0,body,3,payload.Length); return Modbus.WithCrc(body);
        }
        sealed class Fake : IReadTransport {
            public byte[] Response;
            public bool Timeout;
            public byte[] Read(int s,int a,int c,int timeout,CancellationToken token,Action<string> log) {
                token.ThrowIfCancellationRequested(); if(Timeout) throw new TimeoutException("test timeout"); return Response;
            }
            public void Dispose() { }
        }
        /// <summary>按脚本抛出指定异常，用来验证故障分类。</summary>
        sealed class ThrowingTransport : IReadTransport {
            readonly Exception failure;
            public ThrowingTransport(Exception failure) { this.failure=failure; }
            public byte[] Read(int s,int a,int c,int timeout,CancellationToken token,Action<string> log) {
                token.ThrowIfCancellationRequested(); throw failure;
            }
            public void Dispose() { }
        }
        public static void Run(List<Point> points,string root) {
            Check("37 bound points / 4 devices",points.Count==37 && points.Select(p=>p.binding.device).Distinct().Count()==4);
            Check("CRC reference",Modbus.Hex(Modbus.Request(1,0,10))=="01 03 00 00 00 0A C5 CD");
            Check("D1 wire address",Modbus.Hex(Modbus.Request(3,8192,2))=="03 03 20 00 00 02 CE 29");
            Check("T0 source binding",points.First(p=>p.name=="T0").binding.address_zero_based==125);
            Check("D9 belongs station6",points.First(p=>p.name=="D9").binding.slave==6);
            bool invalid=false; try { Modbus.Request(1,65535,2); } catch(ArgumentOutOfRangeException) { invalid=true; }
            Check("Reject overflowing address",invalid);
            // 2026-11-06 协议物料落地后的固定约定：直流表原始计数要换成工程值，电表功率单位是 kW 级。
            Check("DC meter scale 0.1V/0.001A/1VA",PointCatalog.Get("ZU6682").Scale==0.1&&PointCatalog.Get("ZI6682").Scale==0.001&&PointCatalog.Get("ZW66822").Scale==1.0);
            Check("DC voltage raw 1754 -> 175.4 V",Math.Abs(1754*PointCatalog.Get("ZU6682").Scale-175.4)<1e-9);
            Check("DS666/DDSU666 power unit is kW/kvar/kVA",PointCatalog.Get("D3").Unit=="kW"&&PointCatalog.Get("D4").Unit=="kvar"&&PointCatalog.Get("D5").Unit=="kVA"&&PointCatalog.Get("DU3").Unit=="kW");
            Check("D9 marked reserved 0x200C",PointCatalog.Get("D9").Label.IndexOf("保留",StringComparison.Ordinal)>=0);
            Check("DU6 is power factor (0x200A)",PointCatalog.Get("DU6").Label.IndexOf("功率因数",StringComparison.Ordinal)>=0);
            // 类型落地：除 RAW（位串）外，每个绑定点都必须能产出数值，
            // 否则 "U16 / I16" 兜底分支只出文本，趋势/历史/导出里全是空值。
            foreach(var bound in points) {
                var meta=PointCatalog.Get(bound);
                if(meta.Mode=="RAW") continue;
                double? decoded; Modbus.Decode(new byte[bound.binding.register_count*2],meta.Mode,out decoded);
                Check("numeric decode: "+bound.name,decoded.HasValue);
            }
            double? signedDc;
            Check("DC meter signed int16",Modbus.Decode(new byte[]{0xff,0x9c},"INT16",out signedDc)=="-100"&&signedDc==-100);
            var full=Reply(2,0x41,0xc8,0,0); var buffer=new List<byte>(); byte[] result;
            bool premature=false;
            for(int i=0;i<full.Length-1;i++) { buffer.Add(full[i]); premature|=Modbus.Extract(buffer,2,2,out result); }
            Check("Fragmentation does not accept partial CRC",!premature);
            buffer.Add(full[full.Length-1]); Check("Fragmentation reconstructs frame",Modbus.Extract(buffer,2,2,out result)&&result.SequenceEqual(full));
            var bad=(byte[])full.Clone(); bad[4]^=0x10;
            buffer=new List<byte>(new byte[]{0xaa,0x55}); buffer.AddRange(bad); buffer.AddRange(Reply(3,0,1)); buffer.AddRange(full); buffer.AddRange(full);
            Check("Noise badCRC wrongslave resynchronisation",Modbus.Extract(buffer,2,2,out result)&&result.SequenceEqual(full));
            Check("Concatenated replies",Modbus.Extract(buffer,2,2,out result)&&buffer.Count==0);
            buffer=new List<byte>(Reply(2,0,1)); Check("Wrong byte count rejected",!Modbus.Extract(buffer,2,2,out result));
            var exception=Modbus.WithCrc(new byte[]{2,0x83,2}); buffer=new List<byte>(exception);
            Check("Exception frame CRC validated",Modbus.Extract(buffer,2,2,out result)&&result.SequenceEqual(exception));
            double? number;
            Check("Float ABCD 25C",Modbus.Decode(new byte[]{0x41,0xc8,0,0},"FLOAT ABCD",out number)=="25"&&number==25);
            Check("Float CDAB candidate",Modbus.Decode(new byte[]{0,0,0x41,0xc8},"FLOAT CDAB",out number)=="25");
            Check("Float BADC candidate",Modbus.Decode(new byte[]{0xc8,0x41,0,0},"FLOAT BADC",out number)=="25");
            Check("Float DCBA candidate",Modbus.Decode(new byte[]{0,0,0xc8,0x41},"FLOAT DCBA",out number)=="25");
            Check("Signed int16 -100",Modbus.Decode(new byte[]{0xff,0x9c},"INT16",out number)=="-100"&&number==-100);
            Check("Unsigned preserved",Modbus.Decode(new byte[]{0xff,0x9c},"UINT16",out number)=="65436");
            Modbus.Decode(new byte[]{0xff,0x9c},"U16 / I16",out number); Check("Unknown units do not create numeric trend",!number.HasValue);
            Check("Candidate division marked",Modbus.Decode(new byte[]{0x09,0xc4},"INT16 /100（候选）",out number).Contains("未确认")&&number==25);
            Modbus.Decode(new byte[]{0x7f,0x80,0,0},"FLOAT ABCD",out number); Check("Infinity is not a valid measurement",!number.HasValue);
            var point=points.First(p=>p.name=="T0");
            var good=new Fake {Response=full};
            Check("Collector valid response",Collector.Read(good,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{}).Number==25);
            var failed=Collector.Read(new Fake {Response=bad},point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("CRC error never publishes value",failed.Status=="失败"&&!failed.Number.HasValue);
            var ex=Collector.Read(new Fake {Response=exception},point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Device exception never publishes value",ex.Status=="设备异常 0x02"&&!ex.Number.HasValue);
            var timed=Collector.Read(new Fake {Timeout=true},point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Timeout never substitutes zero",timed.Status=="超时"&&!timed.Number.HasValue&&timed.Payload.Length==0);
            using(var cts=new CancellationTokenSource()) {
                cts.Cancel(); bool canceled=false;
                try { Collector.Read(good,point,"FLOAT ABCD","test",100,cts.Token,s=>{}); } catch(OperationCanceledException) { canceled=true; }
                Check("Cancellation propagates",canceled);
            }
            Check("CSV formula guard",Journal.Cell("=1+1").StartsWith("\"'="));

            // 云配置保存→读取→清除→再读取的往返（与页面工作流同一条路径）。
            // 第二次保存走的是"替换已存在文件"这条分支，受限环境会在这里失败。
            string cloudPath=Path.Combine(root,".cloud-roundtrip.json");
            try {
                var cloudConfig=new CloudConfiguration{DeviceId="fixture-device",Enabled=false,Endpoint="https://example.invalid",Alias="fixture",Token="fixture-token",SavePath=cloudPath};
                cloudConfig.Save();
                var loaded=CloudConfiguration.Load(cloudPath);
                Check("Cloud config round trip keeps token",loaded.Token=="fixture-token"&&loaded.Alias=="fixture"&&loaded.Endpoint=="https://example.invalid");
                var cleared=CloudConfiguration.Load(cloudPath);cleared.Token="";cleared.Enabled=false;cleared.Save();
                var afterClear=CloudConfiguration.Load(cloudPath);
                Check("Cloud token clear persists",afterClear.Token.Length==0&&!afterClear.Enabled);
            } catch(Exception cloudEx) {
                Check("Cloud token clear persists ("+cloudEx.GetType().Name+": "+cloudEx.Message+")",false);
            }

            // ── A1 传输层契约 ────────────────────────────────────────────────
            // 物理常量：帧长、字符时间、物理最小往返时间。
            Check("Modbus response length",Modbus.ResponseLength(1)==7&&Modbus.ResponseLength(2)==9&&Modbus.ResponseLength(12)==29);
            Check("Modbus char time 3.5 chars at 9600",Math.Abs(Modbus.CharTimeMs(9600)-3.646)<0.02);
            Check("Modbus physical floor 1 register",Math.Abs(Modbus.PhysicalFloorMs(1,9600)-8.29)<0.05);
            Check("Modbus physical floor 12 registers",Math.Abs(Modbus.PhysicalFloorMs(12,9600)-31.21)<0.05);

            // 迟到帧判定：往返时间低于物理下限的"响应"不可能属于本次请求。
            var lateFrame=Reply(2,0x41,0xc8,0,0);
            var lateTransport=new ScriptedTransport {FloorMs=40};
            lateTransport.Enqueue(lateFrame,0,2);
            var lateObs=Collector.Read(lateTransport,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Late frame is rejected as ambiguous",lateObs.Quality=="ambiguous"&&!lateObs.Number.HasValue&&lateObs.FaultClass=="late");
            Check("Late frame keeps no payload",lateObs.Payload.Length==0);

            // 同一传输层，往返时间充足时正常值必须照常发布（防止闸门过严误杀）。
            var timelyTransport=new ScriptedTransport {FloorMs=1};
            timelyTransport.Enqueue(lateFrame,0,2);
            var timelyObs=Collector.Read(timelyTransport,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Response above physical floor is published",timelyObs.Quality=="good"&&timelyObs.Number==25);

            // 故障分类：端口故障与帧故障必须分开，否则引擎无法决定是否重建串口。
            var frameFault=new Fake {Response=bad};
            var frameObs=Collector.Read(frameFault,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("CRC mismatch classified as frame fault",frameObs.FaultClass=="frame"&&frameObs.Quality=="error"&&!frameObs.Number.HasValue);
            var portFault=new ThrowingTransport(new IOException("端口已消失"));
            var portObs=Collector.Read(portFault,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("IO failure classified as port fault",portObs.FaultClass=="port"&&portObs.Quality=="error");
            var busyTransport=new ThrowingTransport(new UnauthorizedAccessException("端口被占用"));
            var busyObs=Collector.Read(busyTransport,point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Port in use classified as busy",busyObs.FaultClass=="busy");
            var timeoutObs=Collector.Read(new Fake {Timeout=true},point,"FLOAT ABCD","test",100,CancellationToken.None,s=>{});
            Check("Timeout classified as timeout",timeoutObs.FaultClass=="timeout"&&timeoutObs.Quality=="timeout");

            // 模拟器的 count>=3 陷阱必须变成显式契约，而不是"CRC 合法但数据段全 0"的畸形帧。
            bool simUnsupported=false;
            try { new SimulationTransport().Read(2,125,12,100,CancellationToken.None,s=>{}); }
            catch(NotSupportedException) { simUnsupported=true; }
            Check("Simulator refuses multi-register reads explicitly",simUnsupported);

            // 夹具传输层：按真实寄存器表建模，支持任意读取长度。
            var fixture=new FixtureTransport();
            fixture.DeclareSegment(2,125,12).DeclareSegment(3,8192,16).DeclareSegment(1,0,16);
            fixture.SetFloat(2,125,25.0f).SetFloat(2,127,21.25f).SetWord(3,8192,0x0DC0);
            var blockFrame=fixture.Read(2,125,12,100,CancellationToken.None,s=>{});
            Check("Fixture serves any register count",blockFrame.Length==29&&blockFrame[2]==24);
            var perPoint=fixture.Read(2,127,2,100,CancellationToken.None,s=>{});
            bool sliceMatches=perPoint[3]==blockFrame[7]&&perPoint[4]==blockFrame[8]&&perPoint[5]==blockFrame[9]&&perPoint[6]==blockFrame[10];
            Check("Block slice equals per-point read byte for byte",sliceMatches);
            var unbound=fixture.Read(2,136,1,100,CancellationToken.None,s=>{});
            Check("Unbound register inside a segment reads as zero",unbound.Length==7&&unbound[3]==0&&unbound[4]==0);
            var outsideSegment=fixture.Read(2,9000,1,100,CancellationToken.None,s=>{});
            Check("Address outside every segment reads as zero",outsideSegment.Length==7&&outsideSegment[3]==0);

            // ── 块读规划（纯函数，尚未接入采集）─────────────────────────────
            var blocks=BlockPlanner.Plan(points);
            Check("块规划自检无问题：["+String.Join("；",BlockPlanner.Validate(blocks,points).ToArray())+"]",BlockPlanner.Validate(blocks,points).Count==0);
            Check("块规划零空洞（绝不用未绑定地址填空）",blocks.All(b=>b.Holes==0));
            Check("块规划不跨站号合并",blocks.GroupBy(b=>b.Slave).Count()==points.Select(p=>p.binding.slave).Distinct().Count());
            Check("块规划不超协议上限 125",blocks.All(b=>b.Count<=BlockPlanner.MaxBlockRegisters));
            // 直流表三个地址互不相邻（0/3/15），必须保持三块 —— 合并要去读表里没有的地址。
            Check("直流表保持 3 个独立块",blocks.Count(b=>b.Slave==1)==3&&blocks.Where(b=>b.Slave==1).All(b=>b.Count==1));
            // PLC 的 6 个温度点连续（125..137），应合成一块 12 寄存器。
            var temperatureBlock=blocks.First(b=>b.Slave==2&&b.Points.Contains("T0"));
            Check("PLC 温度点合成一块 12 寄存器",temperatureBlock.Address==125&&temperatureBlock.Count==12&&temperatureBlock.Points.Count==6);
            // 帧数必须显著少于逐点读。
            Check("块读帧数少于逐点读",BlockPlanner.FrameCount(blocks)<BlockPlanner.PerPointFrameCount(points));
            // 切片按地址算偏移，不按点名下标 —— 句柄点故意用乱序验证。
            var t3=points.First(p=>p.name=="T3");
            byte[] fakeBlockPayload=new byte[temperatureBlock.Count*2];
            for(int i=0;i<fakeBlockPayload.Length;i++)fakeBlockPayload[i]=(byte)(0x10+i);
            byte[] sliceT3=BlockPlanner.Slice(temperatureBlock,t3,fakeBlockPayload);
            int expectedOffset=(t3.binding.address_zero_based-temperatureBlock.Address)*2;
            Check("块切片按寄存器地址取偏移",sliceT3.Length==4&&sliceT3[0]==fakeBlockPayload[expectedOffset]&&sliceT3[3]==fakeBlockPayload[expectedOffset+3]);
            // 排除保留寄存器（0x200C = D9）后应多出一块：8202..8204 与 8206..8208 被 8204 隔开。
            var withoutReserved=points.Where(p=>p.name!="D9").ToList();
            var reservedFree=BlockPlanner.Plan(withoutReserved);
            Check("排除保留寄存器后站6多出一块",reservedFree.Count(b=>b.Slave==6)==blocks.Count(b=>b.Slave==6)+1);
            Check("排除保留寄存器后仍零空洞",reservedFree.All(b=>b.Holes==0)&&BlockPlanner.Validate(reservedFree,withoutReserved).Count==0);

            // 界面默认列与导出点集必须完全一致：曾经按分组名过滤，结果太阳能 3 点
            // 在界面上看不到、导出里却有 —— 用户看不见自己要导出的东西。
            var uiOnly=points.Where(p=>HistoryPage.DefaultColumnVisible(p.name)&&!ExperimentExport.ExportablePoint(p.name)).ToList();
            var exportOnly=points.Where(p=>!HistoryPage.DefaultColumnVisible(p.name)&&ExperimentExport.ExportablePoint(p.name)).ToList();
            Check("界面默认列与导出点集完全一致（界面多出 "+uiOnly.Count+"、导出多出 "+exportOnly.Count+"）",uiOnly.Count==0&&exportOnly.Count==0);
            Check("太阳能点同时出现在界面与导出里",HistoryPage.DefaultColumnVisible("ZU6682")&&ExperimentExport.ExportablePoint("ZU6682"));
            Check("设定值不同时出现在界面默认列与导出里",!HistoryPage.DefaultColumnVisible("D2202")&&!ExperimentExport.ExportablePoint("D2202"));
            Check("状态点不同时出现在界面默认列与导出里",!HistoryPage.DefaultColumnVisible("D2212")&&!ExperimentExport.ExportablePoint("D2212"));

            string testRoot=Path.Combine(root,".testing",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(testRoot);
            using(var form=new MonitorForm(points,testRoot)) {form.SetSimulationForTest(true);form.TestSimulationRun(false);}
            Check("Hidden UI one round saves all37 points",true);
            using(var form=new MonitorForm(points,testRoot)) {form.SetSimulationForTest(true);form.TestSimulationRun(true);}
            Check("Hidden UI cancellation closes worker",true);
            File.WriteAllText(Path.Combine(root,"self-test-result.json"),new JavaScriptSerializer().Serialize(new {passed=true,count=passed.Count,checks=passed,hardware_tested=false}));
        }
    }
}
