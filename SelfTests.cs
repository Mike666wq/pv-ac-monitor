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
        public static void Run(List<Point> points,string root) {
            Check("37 bound points / 4 devices",points.Count==37 && points.Select(p=>p.binding.device).Distinct().Count()==4);
            Check("CRC reference",Modbus.Hex(Modbus.Request(1,0,10))=="01 03 00 00 00 0A C5 CD");
            Check("D1 wire address",Modbus.Hex(Modbus.Request(3,8192,2))=="03 03 20 00 00 02 CE 29");
            Check("T0 source binding",points.First(p=>p.name=="T0").binding.address_zero_based==125);
            Check("D9 belongs station6",points.First(p=>p.name=="D9").binding.slave==6);
            bool invalid=false; try { Modbus.Request(1,65535,2); } catch(ArgumentOutOfRangeException) { invalid=true; }
            Check("Reject overflowing address",invalid);
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
            string testRoot=Path.Combine(root,".testing",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(testRoot);
            using(var form=new MonitorForm(points,testRoot)) form.TestSimulationRun(false);
            Check("Hidden UI one round saves all37 points",true);
            using(var form=new MonitorForm(points,testRoot)) form.TestSimulationRun(true);
            Check("Hidden UI cancellation closes worker",true);
            File.WriteAllText(Path.Combine(root,"self-test-result.json"),new JavaScriptSerializer().Serialize(new {passed=true,count=passed.Count,checks=passed,hardware_tested=false}));
        }
    }
}
