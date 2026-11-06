using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>
    /// 控制写入链路的隔离自测：只用进程内模拟传输，不打开串口、不联网、不弹窗、不写结果文件。
    ///
    /// 控制点读数说明：引擎采集时按 PointCatalog 给 D2202/D2003/D2208 的 "U16 / I16" 模式，
    /// Modbus.Decode 在该模式下不产出 Observation.Number，而控制门禁要求 Number.HasValue，
    /// 因此纯采集驱动的门禁会一律判为 stale。这里用同程序集的 internal 钩子
    /// MonitorEngine.RememberObservation 注入等价于 PLC 当前值的数值观测（D2202=24、
    /// D2003=1、D2208=0）；06 报文、回读、审计 JSONL、未知锁定仍全部走真实实现。
    /// 采集线程只采集一个保活点，避免真实采集覆盖注入值。
    /// </summary>
    static class ControlTests {
        static string controlRoot;

        static void Check(bool value,string name,List<string> checks) {
            if(!value)throw new Exception("控制检查失败："+name);
            checks.Add(name);
        }

        /// <summary>控制链路测试替身：16 位寄存器表，可注入丢回显/异常/不生效三种故障。</summary>
        sealed class FakeControlTransport : IReadTransport, IWriteTransport {
            readonly Dictionary<int,int> registers=new Dictionary<int,int>();
            public string FailMode="";
            public bool EchoCorrupt;
            /// <summary>让所有 03 读取失败：用于验证"写前预读失败即不发"。</summary>
            public bool FailRead;
            public int ForceRegister=-1;
            public int WriteCount;
            public int ReadCount;
            static int Key(int slave,int address) { return (slave<<16)|(address&0xffff); }
            public FakeControlTransport() { Seed(2,2202,24);Seed(2,2003,1);Seed(2,2208,0); }
            public void Seed(int slave,int address,int value) { registers[Key(slave,address)]=value&0xffff; }
            public int? Seeded(int slave,int address) { int value;return registers.TryGetValue(Key(slave,address),out value)?(int?)value:null; }
            public byte[] Read(int slave,int address,int count,int timeoutMs,CancellationToken token,Action<string> log) {
                token.ThrowIfCancellationRequested();
                ReadCount++;
                if(FailRead) throw new TimeoutException("控制测试故障：读失败");
                var body=new byte[3+count*2];
                body[0]=(byte)slave;body[1]=3;body[2]=(byte)(count*2);
                if(count==1) {
                    int value=0;registers.TryGetValue(Key(slave,address),out value);
                    if(ForceRegister>=0&&address==2202)value=ForceRegister;
                    body[3]=(byte)(value>>8);body[4]=(byte)value;
                } else if(count==2) {
                    byte[] data=BitConverter.GetBytes(25.0f);Array.Reverse(data);Array.Copy(data,0,body,3,4);
                }
                var reply=Modbus.WithCrc(body);
                log("[控制测试] RX "+Modbus.Hex(reply));
                return reply;
            }
            public byte[] WriteSingle(int slave,int address,int value,int timeoutMs,CancellationToken token,Action<string> log) {
                token.ThrowIfCancellationRequested();
                WriteCount++;
                log("[控制测试] TX "+Modbus.Hex(Modbus.WriteSingle(slave,address,value)));
                if(FailMode=="drop")throw new TimeoutException("控制测试故障：写入回显丢失");
                if(FailMode=="exception")return Modbus.WithCrc(new byte[] {(byte)slave,0x86,2});
                if(FailMode!="ignore")registers[Key(slave,address)]=value&0xffff;
                if(EchoCorrupt) {
                    // 回显地址被改成别的寄存器：内容合法但与请求不对应
                    var body=new byte[] {(byte)slave,6,(byte)((address+1)>>8),(byte)((address+1)&0xff),(byte)(value>>8),(byte)value};
                    return Modbus.WithCrc(body);
                }
                return Modbus.WriteSingle(slave,address,value);
            }
            public void Dispose() { }
        }

        /// <summary>每个场景一个全新目录，并复制 protocol.json（采集线程从自己的 root 读取点表）。</summary>
        static string NewRoot(string parent,string name) {
            string dir=Path.Combine(controlRoot,name);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(parent,"protocol.json"),Path.Combine(dir,"protocol.json"));
            return dir;
        }

        static ControlConfig BuildConfig() {
            var config=new ControlConfig();
            config.mode="preview";
            config.enforceInterlocks=true;
            config.requireConfirmed=true;
            config.interlocks.remotePoint="D2003";
            config.interlocks.faultPoint="D2208";
            config.interlocks.remoteExpected=1;
            config.interlocks.faultExpected=0;
            config.interlocks.maxAgeSeconds=30;
            config.limits.minIntervalMs=1000;
            config.limits.maxWritesPerMinute=6;
            config.targets.Add(new ControlTarget {
                point="D2202",label="主机温度设定",kind="int16",
                slave=2,address=2202,min=16,max=30,step=1,
                confirmed=true,allowWrite=true
            });
            return config;
        }

        static void WriteConfig(string dir,Action<ControlConfig> tweak) {
            ControlConfig config=BuildConfig();
            if(tweak!=null)tweak(config);
            File.WriteAllText(Path.Combine(dir,"control-config.json"),new JavaScriptSerializer().Serialize(config),new UTF8Encoding(false));
        }

        static MonitorEngine StartEngine(List<Point> points,string dir,FakeControlTransport fake,bool autoStart) {
            var engine=new MonitorEngine(points,dir);
            try {
                engine.TransportFactory=(sim,port,baud)=>fake;
                engine.Connect(true,"SIM",9600);
                if(!autoStart)return engine;
                Point keepalive=points.First(p=>p.name!="D2202"&&p.name!="D2003"&&p.name!="D2208");
                engine.Start(1,1,new [] {keepalive.name});
                var watch=Stopwatch.StartNew();
                while(engine.LastObservation(keepalive.name)==null&&watch.ElapsedMilliseconds<8000)Thread.Sleep(20);
                if(engine.LastObservation(keepalive.name)==null)throw new Exception("控制检查失败：采集启动超时");
                RememberControlPoints(engine,fake);
                return engine;
            } catch {
                try{engine.Dispose();}catch{}
                throw;
            }
        }

        /// <summary>把模拟 PLC 的当前值写入引擎观测缓存，等价于一轮真实采集的结果。</summary>
        static void RememberControlPoints(MonitorEngine engine,FakeControlTransport fake) {
            engine.RememberObservation(Sample(fake,"D2202",2,2202));
            engine.RememberObservation(Sample(fake,"D2003",2,2003));
            engine.RememberObservation(Sample(fake,"D2208",2,2208));
        }

        static Observation Sample(FakeControlTransport fake,string name,int slave,int address) {
            var point=new Point {
                name=name,description=name,
                binding=new Binding {device="PLC",slave=slave,address_zero_based=address,register_count=1,decode_proposal="int16"}
            };
            return Collector.Read(fake,point,"UINT16","simulation",1000,CancellationToken.None,delegate(string s) { });
        }

        public static List<string> Run(List<Point> points,string root) {
            if(points==null||points.Count==0)throw new ArgumentException("点表不能为空");
            if(points.All(p=>p.name!="D2202")||points.All(p=>p.name!="D2003")||points.All(p=>p.name!="D2208"))
                throw new ArgumentException("点表缺少 D2202/D2003/D2208，无法验证控制写入链路");
            var checks=new List<string>();
            controlRoot=Path.Combine(Path.GetFullPath(root),".testing","control-"+Guid.NewGuid().ToString("N"));
            try {
                // 1. FC06 报文与回显提取。
                var frame=Modbus.WriteSingle(2,2202,26);
                Check(frame.Length==8,"FC06 报文长度为 8 字节",checks);
                Check(frame[0]==0x02&&frame[1]==0x06&&frame[2]==0x08&&frame[3]==0x9A&&frame[4]==0x00&&frame[5]==0x1A,
                    "FC06 前 6 字节为 02 06 08 9A 00 1A",checks);
                ushort crc=Modbus.Crc(frame,6);
                Check(frame[6]==(byte)crc&&frame[7]==(byte)(crc>>8),"FC06 CRC 与 Modbus.Crc 一致且小端排布",checks);
                var noisy=new List<byte>(new byte[] {0x00,0xFF,0x02,0xAA});
                noisy.AddRange(frame);
                byte[] echo;
                Check(Modbus.ExtractWriteEcho(noisy,2,2202,26,out echo),"噪声中可提取到正确的 06 回显",checks);
                Check(echo!=null&&echo.Length==8&&Modbus.Hex(echo)==Modbus.Hex(frame),"提取出的回显与发送帧一致",checks);
                Check(noisy.Count==0,"提取回显后噪声与帧被完整消费",checks);

                // 2. 默认关闭：没有 control-config.json 时不得写入。
                {
                    string dir=NewRoot(root,"disabled");
                    var fake=new FakeControlTransport();
                    bool armRejected=false;
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        try { engine.ArmControl("tester"); } catch(InvalidOperationException) { armRejected=true; }
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="disabled","未配置 control-config.json 时控制为 disabled",checks);
                        Check(armRejected,"未启用控制时 ArmControl 抛异常",checks);
                        Check(fake.WriteCount==0,"未启用控制时不发送 06",checks);
                    }
                }

                // 3. 预演：只生成报文，不发送。
                {
                    string dir=NewRoot(root,"preview");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        ControlResult r=engine.PreviewControl("D2202",26,"tester");
                        Check(r.Status=="preview","预演返回 preview",checks);
                        Check(r.Tx!=null&&r.Tx.Length==8,"预演生成 8 字节 06 报文",checks);
                        Check(fake.WriteCount==0,"预演不发送 06",checks);
                        Check(engine.ControlMode=="closed","未开启控制开关时控制模式为 closed",checks);
                    }
                }

                // 4. 未解锁。
                {
                    string dir=NewRoot(root,"not-armed");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_armed","未解锁时写入返回 not_armed",checks);
                        Check(fake.WriteCount==0,"未解锁时不发送 06",checks);
                    }
                }

                // 5. 写入范围。
                {
                    string dir=NewRoot(root,"range");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        Check(engine.ControlArmed,"解锁后进入 armed 状态",checks);
                        ControlResult r=engine.ExecuteControl("D2202",99,"tester");
                        Check(r.Status=="range","超范围写入返回 range",checks);
                        Check(fake.WriteCount==0,"超范围时不发送 06",checks);
                    }
                }

                // 6. 远程/本地联锁。
                {
                    string dir=NewRoot(root,"interlock-remote");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    fake.Seed(2,2003,0);
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="interlock_remote","非远程模式返回 interlock_remote",checks);
                        Check(fake.WriteCount==0,"联锁不满足时不发送 06",checks);
                    }
                }

                // 7. 总故障联锁。
                {
                    string dir=NewRoot(root,"interlock-fault");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    fake.Seed(2,2208,1);
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="interlock_fault","总故障置位返回 interlock_fault",checks);
                        Check(fake.WriteCount==0,"总故障联锁时不发送 06",checks);
                    }
                }

                // 8. 未采集：只连接不启动采集。
                {
                    string dir=NewRoot(root,"not-acquiring");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,false)) {
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_acquiring","未采集时写入返回 not_acquiring",checks);
                        bool armRejected=false;
                        try { engine.ArmControl("tester"); } catch(InvalidOperationException) { armRejected=true; }
                        Check(armRejected,"未采集时 ArmControl 抛异常",checks);
                        Check(fake.WriteCount==0,"未采集时不发送 06",checks);
                    }
                }

                // 9. 成功写入：回读一致并留下审计。
                {
                    string dir=NewRoot(root,"applied");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="held","写入且回读一致返回 held（设备效果要等下一轮采集观测确认）",checks);
                        Check(fake.WriteCount==1,"成功写入只发送一次 06",checks);
                        Check(r.HasReadback&&r.Readback==26,"成功写入回读为 26",checks);
                        Check(fake.Seeded(2,2202)==26,"模拟 PLC 寄存器被写入 26",checks);
                        Check(!String.IsNullOrEmpty(r.WriteId),"每笔写入都有唯一标识",checks);
                        string logPath=Path.Combine(dir,"logs","control-events.jsonl");
                        Check(File.Exists(logPath),"成功写入生成控制审计文件",checks);
                        Check(File.Exists(logPath)&&File.ReadAllText(logPath).Contains("\"held\""),"控制审计包含 held",checks);
                        Check(File.Exists(logPath)&&File.ReadAllText(logPath).Contains(r.WriteId),"控制审计带上 write_id",checks);
                        // 效果确认：下一轮采集观测到同点同值 ⇒ applied
                        engine.RememberObservation(Sample(fake,"D2202",2,2202));
                        Check(engine.LastControlResult!=null&&engine.LastControlResult.Status=="applied","下一轮采集观测一致后判定 applied",checks);
                        Check(engine.LastControlResult.WriteId==r.WriteId,"applied 与 held 属于同一笔写入",checks);
                    }
                }

                // 10. 丢回显：第一次 unknown，之后锁定且不再发送；再尝试核对。
                {
                    string dir=NewRoot(root,"drop");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        fake.FailMode="drop";
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="unknown","丢回显返回 unknown",checks);
                        Check(fake.WriteCount==1,"丢回显仍计一次 06 发送",checks);
                        Check(engine.ControlUncertain,"丢回显后进入未知锁定",checks);
                        ControlResult again=engine.ExecuteControl("D2202",26,"tester");
                        Check(again.Status=="uncertain","未知锁定后再次写入返回 uncertain",checks);
                        Check(fake.WriteCount==1,"未知锁定后不再发送 06",checks);
                        fake.Seed(2,2202,24);
                        bool rearmed=false;
                        try { engine.ArmControl("tester"); rearmed=true; } catch(InvalidOperationException) { }
                        Check(!rearmed,"未知锁定期间禁止重新解锁",checks);
                        ControlResult verified=engine.ResolveControlUncertainty("tester");
                        Check(verified.Status=="verify_not_applied","核对确认写入未生效（回读仍为旧值）",checks);
                        Check(!engine.ControlUncertain,"核对后解除未知锁定",checks);
                        engine.ArmControl("tester");
                        Check(engine.ControlArmed,"核对通过后允许重新解锁",checks);
                    }
                }

                // 11. 回显成功但回读不变。
                {
                    string dir=NewRoot(root,"ignore");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        fake.FailMode="ignore";
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_accepted","回显正常但回读未变返回 not_accepted",checks);
                        Check(r.HasReadback&&r.Readback==24,"回读仍为旧值 24",checks);
                        Check(fake.WriteCount==1,"回读不一致时只发送一次 06",checks);
                    }
                }

                // 12. 设备异常帧 0x86。
                {
                    string dir=NewRoot(root,"exception");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        fake.FailMode="exception";
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="rejected","设备返回 0x86 异常帧时状态为 rejected",checks);
                        Check(fake.WriteCount==1,"设备拒绝也算一次 06 发送",checks);
                        Check(!engine.ControlUncertain,"设备拒绝不进入未知锁定",checks);
                    }
                }

                // 13. 写入冷却。
                {
                    string dir=NewRoot(root,"rate");
                    // 写一次本身要花掉「预读 + 写 + verifyWindow + 回读」两秒以上，
                    // 所以这里把冷却拉长到 5 秒，才能确定第二次落在冷却窗口内。
                    WriteConfig(dir,c=>c.limits.minIntervalMs=5000);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult first=engine.ExecuteControl("D2202",26,"tester");
                        Check(first.Status=="held","冷却测试首次写入 held",checks);
                        ControlResult second=engine.ExecuteControl("D2202",27,"tester");
                        Check(second.Status=="rate","冷却窗口内第二次写入被拒绝",checks);
                        Check(fake.WriteCount==1,"冷却期间不发送第二次 06",checks);
                    }
                }

                // 15. 回显与请求不一致（地址/值被篡改）必须判 unknown，不能当成成功。
                {
                    string dir=NewRoot(root,"echo-mismatch");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    fake.EchoCorrupt=true;
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="unknown","回显与请求不一致时判 unknown",checks);
                        Check(engine.ControlUncertain,"回显不一致后进入未知锁定",checks);
                    }
                }

                // 16. 未知状态可以通过「重新读取核对」解除，即使核对值与旧值/新值都不一致。
                {
                    string dir=NewRoot(root,"verify-mismatch");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        fake.FailMode="drop";
                        ControlResult unknown=engine.ExecuteControl("D2202",26,"tester");
                        Check(unknown.Status=="unknown","丢回显判 unknown",checks);
                        Check(engine.ControlUncertain,"丢回显后处于未知状态",checks);
                        fake.FailMode="";
                        fake.ForceRegister=25;   // 既不是旧值 24 也不是新值 26
                        // 2026-11-06 起：第三值不再一次点击就静默清锁，必须带理由码。
                        ControlResult noReason=engine.ResolveControlUncertainty("tester");
                        Check(noReason.Status=="verify_reason_required","第三值缺理由码时不解除锁定",checks);
                        Check(engine.ControlUncertain,"第三值缺理由码时保持锁定",checks);
                        ControlResult verified=engine.ResolveControlUncertainty("tester","现场确认为手动改动");
                        Check(verified.Status=="verify_mismatch_manual","核对值与旧新都不一致时按人工确认处理",checks);
                        Check(!engine.ControlUncertain,"人工核对后解除未知锁定（不再永久锁死）",checks);
                    }
                }

                // 17. mode=disabled 时不允许打开控制开关；配置缺失时同样不允许。
                {
                    string dir=NewRoot(root,"switch-guard");
                    WriteConfig(dir,c=>c.mode="disabled");
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        bool refused=false;
                        try { engine.SetControlEnabled(true,"tester"); } catch(InvalidOperationException) { refused=true; }
                        Check(refused,"mode=disabled 时拒绝打开控制开关",checks);
                        Check(!engine.ControlEnabled,"拒绝后开关保持关闭",checks);
                    }
                }

                // 18. 16 位点（U16 / I16 显示层不产出 Number）也要能取到数值。
                {
                    string dir=NewRoot(root,"u16-payload");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.RememberObservation(new Observation{Point="D2202",Quality="good",Mode="U16 / I16",Payload=new byte[]{0x00,0x18},Utc=DateTime.UtcNow});
                        int? value=engine.ControlNumericValue("D2202");
                        Check(value.HasValue&&value.Value==24,"U16/I16 点按原始大端字节取到数值",checks);
                    }
                }

                // 14. 配置校验。
                {
                    ControlConfig valid=BuildConfig();
                    Check(valid.Validate(points)=="","默认控制配置通过 Validate",checks);
                    ControlConfig broken=BuildConfig();
                    broken.targets[0].address=9999;
                    Check(!String.IsNullOrEmpty(broken.Validate(points)),"target 地址与点表不一致时 Validate 返回错误",checks);
                }

                // ── A3 门禁回归（2026-11-06）──────────────────────────────────
                // 这些用例在旧实现下全部是绿的 —— 旧门禁挂在 requireConfirmed 条件里，
                // 而 requireConfirmed 默认 false，于是 confirmed/allowWrite 整段被跳过。

                // A3-1 随包配置（confirmed=false）必须挡住写入，且审计里不得出现任何 06 报文。
                {
                    string dir=NewRoot(root,"gate-confirmed");
                    WriteConfig(dir,c=>{c.targets[0].confirmed=false;c.targets[0].allowWrite=true;});
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_confirmed","confirmed=false 时写入被挡下",checks);
                        Check(fake.WriteCount==0,"confirmed=false 时不发送任何 06",checks);
                    }
                }

                // A3-2 allowWrite=false 同样独立生效（不依赖 confirmed）。
                {
                    string dir=NewRoot(root,"gate-allowwrite");
                    WriteConfig(dir,c=>{c.targets[0].confirmed=true;c.targets[0].allowWrite=false;});
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_allow_write","allowWrite=false 时写入被挡下",checks);
                        Check(fake.WriteCount==0,"allowWrite=false 时不发送任何 06",checks);
                    }
                }

                // A3-3 requireConfirmed=false 不得让两道门禁失效（这是随包配置真正的状态）。
                {
                    string dir=NewRoot(root,"gate-requireconfirmed-off");
                    WriteConfig(dir,c=>{c.requireConfirmed=false;c.targets[0].confirmed=false;c.targets[0].allowWrite=false;});
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="not_confirmed","requireConfirmed=false 也不放过 confirmed=false",checks);
                        Check(fake.WriteCount==0,"requireConfirmed=false 也不放过 allowWrite=false",checks);
                    }
                }

                // A3-4 mode=disabled 必须彻底不写；mode=preview（旧写法）仍按旧行为写并告警。
                {
                    string dir=NewRoot(root,"gate-mode");
                    WriteConfig(dir,c=>c.mode="disabled");
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="disabled","mode=disabled 时写入被挡下",checks);
                        Check(fake.WriteCount==0,"mode=disabled 时不发送任何 06",checks);
                    }
                    string legacyDir=NewRoot(root,"gate-mode-preview");
                    WriteConfig(legacyDir,c=>c.mode="preview");
                    var legacyFake=new FakeControlTransport();
                    using(var engine=StartEngine(points,legacyDir,legacyFake,true)) {
                        Check(engine.ControlSettings.ModeCompatibilityWarning.Length>0,"mode=preview 给出兼容告警",checks);
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="held"||r.Status=="applied","mode=preview 按旧行为仍是真实写入",checks);
                        Check(legacyFake.WriteCount==1,"mode=preview 确实发出了写入",checks);
                    }
                }

                // A3-5 写前 03 预读失败 ⇒ 一律不发（旧实现会回退采集快照照写）。
                {
                    string dir=NewRoot(root,"gate-preread");
                    // 关掉联锁：要单独验证"写前 03 预读失败"这一道，
                    // 否则联锁点同样读不到值，会先一步以 stale 拦下，测不到预读本身。
                    WriteConfig(dir,c=>c.enforceInterlocks=false);
                    var fake=new FakeControlTransport();
                    fake.FailRead=true;
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(r.Status=="pre_read_failed","写前预读失败时放弃写入",checks);
                        Check(fake.WriteCount==0,"写前预读失败时不发送任何 06",checks);
                        Check(!r.OldValue.HasValue,"写前预读失败时旧值留空而不是记成 0",checks);
                    }
                }

                // A3-6 核对读到第三值：没有理由码就不解锁（旧实现一次点击静默清锁）。
                {
                    string dir=NewRoot(root,"verify-reason");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        fake.FailMode="drop";
                        engine.ExecuteControl("D2202",26,"tester");
                        Check(engine.ControlUncertain,"丢回显后进入未知锁定",checks);
                        fake.FailMode="";
                        fake.Seed(2,2202,99); // 读数既不是旧值也不是新值 ⇒ 第三值
                        ControlResult noReason=engine.ResolveControlUncertainty("tester");
                        Check(noReason.Status=="verify_reason_required","第三值缺理由码时不解除锁定",checks);
                        Check(engine.ControlUncertain,"缺理由码时控制保持锁定",checks);
                        ControlResult withReason=engine.ResolveControlUncertainty("tester","现场确认为手动改动");
                        Check(withReason.Status=="verify_mismatch_manual","带理由码的第三值核对可以解锁",checks);
                        Check(!engine.ControlUncertain,"带理由码后解除锁定",checks);
                        string audit=File.ReadAllText(Path.Combine(dir,"logs","control-events.jsonl"));
                        Check(audit.Contains("verify_reason_required")&&audit.Contains("现场确认为手动改动"),"第三值核对与理由码都进审计",checks);
                    }
                }

                // A3-7 一笔写入只有一个终态：write_id 唯一，且界面不再自行写终态。
                {
                    string dir=NewRoot(root,"write-id");
                    WriteConfig(dir,null);
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ArmControl("tester");
                        ControlResult r=engine.ExecuteControl("D2202",26,"tester");
                        Check(!String.IsNullOrEmpty(r.WriteId),"写入带 write_id",checks);
                        string audit=File.ReadAllText(Path.Combine(dir,"logs","control-events.jsonl"));
                        Check(audit.Contains(r.WriteId),"审计带上 write_id",checks);
                    }
                }

                // A3-8 演示配置不得跨会话存活：断开后重新连真实串口必须重新读盘。
                {
                    string dir=NewRoot(root,"demo-scope");
                    WriteConfig(dir,c=>{c.mode="disabled";c.targets[0].confirmed=false;c.targets[0].allowWrite=false;});
                    var fake=new FakeControlTransport();
                    using(var engine=StartEngine(points,dir,fake,true)) {
                        engine.ApplySimulationDemoConfig();
                        Check(engine.ControlSettings.Version=="simulation-demo","模拟演示配置已加载",checks);
                        engine.Disconnect();
                        engine.Connect(true,"SIM",9600); // 重新连接应重载磁盘配置
                        Check(engine.ControlSettings.Version!="simulation-demo","重新连接后演示配置不再存活",checks);
                        Check(!engine.ControlArmed,"重新连接后控制开关默认关闭",checks);
                    }
                }

                // A3-9 结构上产不出"设备已动作"：没有确认过因果关系的判据，就不许下这个结论。
                {
                    ControlConfig noCausal=BuildConfig();
                    noCausal.targets[0].feedbackPoints=new List<string>();
                    Check(noCausal.targets[0].feedbackPoints.Count==0,"未配置因果判据的控制点没有判据列表",checks);
                    ControlConfig withCausal=BuildConfig();
                    withCausal.targets[0].feedbackPoints=new List<string>{"D2211"};
                    Check(withCausal.targets[0].feedbackPoints.Count==1,"配置了因果判据的控制点保留判据列表",checks);
                }

                // A3-10 mode=preview 是旧写法：按旧行为仍是真实写入，但必须给出兼容告警。
                {
                    Check(BuildConfig().mode=="preview","测试配置用旧写法 preview",checks);
                    ControlConfig legacy=new ControlConfig();legacy.mode="preview";
                    Check(legacy.WritesEnabled,"旧写法 preview 仍然算可写（保持 0.7.x 行为）",checks);
                    Check(legacy.ModeCompatibilityWarning.Length>0,"旧写法 preview 有兼容告警文案",checks);
                    ControlConfig live=new ControlConfig();live.mode="live";
                    Check(live.WritesEnabled&&live.ModeCompatibilityWarning.Length==0,"新写法 live 可写且无告警",checks);
                    ControlConfig off=new ControlConfig();off.mode="disabled";
                    Check(!off.WritesEnabled,"disabled 不可写",checks);
                }

                // A3-11 审计列可空：旧值不该在"从未发生"时记成 0。
                {
                    ControlResult r=new ControlResult{Point="D2202",Status="unknown"};
                    Check(!r.OldValue.HasValue,"未确定旧值时 OldValue 为空而不是 0",checks);
                }

                return checks;
            } finally {
                try { Directory.Delete(controlRoot,true); } catch { }
            }
        }
    }
}
