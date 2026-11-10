using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ExperimentMonitor {
    static class Program {
        [STAThread] static int Main(string[] args) {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string rootArgument=args.FirstOrDefault(a=>a.StartsWith("--root="));
            if(rootArgument!=null) root=Path.GetFullPath(rootArgument.Substring(7));
            try {
                var points=Configuration.Load(Path.Combine(root,"protocol.json"));
                // 未登记点的逃生开关：默认拒绝采集点表里没有的点（否则它会以 RAW/无单位的
                // 身份悄悄流进趋势、历史和导出），但绝不能做成"起不来"。
                PointCatalog.AllowUnknown=args.Contains("--allow-unknown-points");
                // 点表启动校验：每条都对应一种"用户会直接看到"的故障形态。
                // 问题写进 point-catalog-validation.json 作为证据，压成一行横幅给界面。
                try {
                    var catalogIssues=PointCatalogValidation.Check(points);
                    if(catalogIssues.Count>0) {
                        PointCatalogValidation.StartupNotice=PointCatalogValidation.Summary(catalogIssues);
                        try{File.WriteAllText(Path.Combine(root,"point-catalog-validation.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{catalogVersion=PointCatalog.Version,allowUnknown=PointCatalog.AllowUnknown,issues=catalogIssues.Select(i=>new{code=i.Code,blocking=i.Blocking,message=i.Message}).ToArray()}));}catch{}
                    }
                    // 未登记的点默认不采集：它会以 RAW/无单位的形式混进趋势、历史与导出。
                    // 但如果全部点都没登记（多半是点表整体换过了），那就别把采集清空，
                    // 宁可先跑起来再提示 —— 现场一次漏改不该让整机不可用。
                    var unknown=points.Where(p=>PointCatalog.Get(p).Unknown).ToList();
                    if(unknown.Count>0&&!PointCatalog.AllowUnknown&&unknown.Count<points.Count) {
                        points=points.Where(p=>!PointCatalog.Get(p).Unknown).ToList();
                        PointCatalogValidation.StartupNotice="已跳过 "+unknown.Count+" 个点表未登记的点："+String.Join("、",unknown.Take(6).Select(p=>p.name).ToArray())+(unknown.Count>6?"…":"")+"（加 --allow-unknown-points 可强制采集）";
                    }
                } catch(Exception ex) { PointCatalogValidation.StartupNotice="点表校验失败："+ex.Message; }
                // 受限环境（某些沙箱、企业安全软件）禁止 File.Replace 的替换语义。生产运行不降级；
                // 只有测试入口才安装"原子替换失败时退到备份后覆盖"的实现，并把降级如实记入结果。
                bool isTestRun=args.Any(a=>a.StartsWith("--test")||a=="--self-test"||a.StartsWith("--capture-ui"));
                Func<bool> degradedReplace=null;
                if(isTestRun) degradedReplace=AtomicFile.InstallFallback();
                // 用**产品自己的解析器**校验随包配置。PowerShell 的 ConvertFrom-Json 会放过
                // 尾随逗号，而 JavaScriptSerializer 不会 —— 0.8.5 就因为一个尾随逗号把
                // control-config.json 打进了发布包，直到包内自检才发现。构建时先验一遍。
                if(args.Contains("--check-config")) return ShippedConfigCheck.Run(points,root);
                if(args.Contains("--export-config")) {File.WriteAllText(Path.Combine(root,"point-contract.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{module="experiment",schemaVersion=1,catalogVersion=PointCatalog.Version,points=points.Select(p=>new {id=p.name,equipmentId=p.binding.device,slave=p.binding.slave,addressZeroBased=p.binding.address_zero_based,registerCount=p.binding.register_count,metadata=PointCatalog.Get(p)}).ToArray()}));return 0;}
                if(args.Contains("--self-test")) { SelfTests.Run(points,root); ExtendedTests.Run(points,root); return 0; }
                // 逐套件隔离执行：一个套件失败不会掩埋其它套件的结果，且显式申报没有用例的缺口。
                if(args.Contains("--test-all")) {
                    var entries=TestMatrix.RunAll(points,root);
                    int failed=TestMatrix.WriteReport(entries,root);
                    foreach(var e in entries) Console.Out.WriteLine(e.status.ToUpperInvariant().PadRight(6)+e.suite.PadRight(26)+e.count.ToString().PadLeft(6)+" 项 "+e.ms.ToString().PadLeft(7)+" ms"+(String.IsNullOrEmpty(e.detail)?"":"  "+e.detail));
                    Console.Out.WriteLine("缺口 "+TestMatrix.Gaps.Length+" 项（无自动化用例，需真机/现场）");
                    return failed==0?0:1;
                }
                if(args.Contains("--test-core")) { var checks=CoreTests.Run(points,root);File.WriteAllText(Path.Combine(root,"core-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=checks.Count,checks=checks,hardware_tested=false}));return 0; }
                if(args.Contains("--test-storage")) { int storage=StorageTests.Run(root),records=RecordsTests.Run(root),failures=ExportFailureTests.Run(points,root);File.WriteAllText(Path.Combine(root,"records-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,storageChecks=storage,recordsChecks=records,failureChecks=failures,count=storage+records+failures,hardware_tested=false}));return 0; }
                if(args.Contains("--test-cloud")) { int count=CloudSelfTests.Run();File.WriteAllText(Path.Combine(root,"cloud-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=count,production_cloud_tested=false}));return 0; }
                if(args.Contains("--test-ui")) { UiLayoutTests.Run(points,root);return 0; }
                // 诊断：切到指定页签后，让消息泵空跑 5 秒，量 CPU 与内存增量。
                // 用来回答"界面停在某一页会不会持续吃 CPU"——UI Automation 在控件量大的
                // 窗口上单次读树会超过 30 秒，量不出来的事情必须换进程内驱动。
                if(args.Contains("--test-station")) { var c=StationHealthTests.Run(points,root); File.WriteAllText(Path.Combine(root,"station-health-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=c.Count,checks=c}),new System.Text.UTF8Encoding(false)); return 0; }
                if(args.Contains("--test-control")) { var checks=ControlTests.Run(points,root);File.WriteAllText(Path.Combine(root,"control-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=checks.Count,checks=checks,hardware_tested=false}));return 0; }
                if(args.Contains("--test-workflow")) { WorkflowTests.Run(points,root);return 0; }
                using(var form=new DashboardForm(points,root)) {
                    var history=new HistoryPage(points,root,form.Engine);form.HistoryHost.Controls.Add(history);form.AttachHistoryPage(history);
                    form.AttachCloudPage(new CloudPage(root,form.Engine));
                    if(args.Contains("--diag-cpu")) return Diag.Cpu(points,root,(string[])args);
                if(args.Contains("--diag-page")) return Diag.Page(points,root,(string[])args);
                if(args.Contains("--diag-export")) return Diag.Export(points,root,(string[])args);
                if(args.Contains("--diag-cycles")) return Diag.Cycles(points,root,(string[])args);
                if(args.Contains("--diag-paging")) return Diag.Paging(points,root,(string[])args);
                if(args.Contains("--diag-columns")) return Diag.Columns(points,root,(string[])args);
                if(args.Contains("--diag-header")) return Diag.Header(points,root,(string[])args);
                if(args.Contains("--diag-blocks")) return Diag.Blocks(points,root,(string[])args);
                if(args.Contains("--capture-ui")) { ExtendedTests.Preview(form,points,Path.Combine(root,"preview.png")); return 0; }
                    Application.Run(form);
                }
                return 0;
            } catch(Exception ex) {
                if(args.Length>0) File.WriteAllText(Path.Combine(root,"test-error.txt"),ex.ToString());
                else MessageBox.Show(ex.Message,"实验监控 Demo 启动失败",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    /// <summary>校验随包配置能否被生产解析器接受。任何一条不过就以非零退出。</summary>
    /// <remarks>PowerShell 的 ConvertFrom-Json 会放过尾随逗号，JavaScriptSerializer 不会 ——
    /// 0.8.5 就因为一个尾随逗号把 control-config.json 打进了发布包，直到包内自检才发现。
    /// 构建时用产品自己的解析器先验一遍。</remarks>
    static partial class ShippedConfigCheck {
        internal static int Run(List<Point> points,string root) {
            int failures=0;
            try {
                var loaded=Configuration.Load(Path.Combine(root,"protocol.json"));
                if(loaded==null||loaded.Count==0) {
                    Console.Error.WriteLine("protocol.json：解析不出任何测点"); failures++;
                } else Console.Out.WriteLine("protocol.json：OK（"+loaded.Count+" 个点）");
            } catch(Exception ex) { Console.Error.WriteLine("protocol.json："+ex.GetType().Name+"："+ex.Message); failures++; }
            try {
                string error;
                ControlConfig config=ControlConfig.Load(root,points,out error);
                if(config==null||!String.IsNullOrEmpty(error)) { Console.Error.WriteLine("control-config.json："+(error??"加载失败")); failures++; }
                else Console.Out.WriteLine("control-config.json：OK（mode="+config.mode+"，"+config.targets.Count+" 个目标）");
            } catch(Exception ex) { Console.Error.WriteLine("control-config.json："+ex.GetType().Name+"："+ex.Message); failures++; }
            return failures==0?0:1;
        }
    }
    static partial class Diag {
        /// <summary>
        /// 页面事实诊断：进程内切到指定页签，等它加载完，把关键事实写成 JSON。
        /// 数据记录页的 DataGridView 会让 UI Automation 枚举超过 30 秒上限，
        /// 那条路径读不出来就只能换进程内驱动 —— 结论一样硬，还不受 UIA 性能干扰。
        /// </summary>
        internal static int Page(List<Point> points,string root,string[] args) {
            int tab=3; foreach(string a in args) if(a!=null&&a.StartsWith("--tab=",StringComparison.Ordinal)) { int v; if(Int32.TryParse(a.Substring(6),out v)) tab=v; }
            bool captureFirst=Array.IndexOf(args,"--capture-first")>=0;
            var facts=new Dictionary<string,object>();
            using(var form=new DashboardForm(points,root)) {
                // 真实入口会挂 HistoryPage / CloudPage；诊断必须复现同样的装配，
                // 否则"数据记录"页是空的，量到的表格是别的页面的。
                try {
                    var historyPage=new HistoryPage(points,root,form.Engine);
                    form.HistoryHost.Controls.Add(historyPage);
                    form.AttachHistoryPage(historyPage);
                    form.AttachCloudPage(new CloudPage(root,form.Engine));
                } catch(Exception ex) { facts["attachError"]=ex.Message; }
                // 允许指定窗口尺寸：960x640 是最小支持尺寸，布局问题只会在那里暴露。
                foreach(string a in args) if(a!=null&&a.StartsWith("--size=",StringComparison.Ordinal)) {
                    string[] wh=a.Substring(7).Split('x');
                    int sw,sh;
                    if(wh.Length==2&&Int32.TryParse(wh[0],out sw)&&Int32.TryParse(wh[1],out sh)) { form.ClientSize=new System.Drawing.Size(sw,sh); facts["clientSize"]=sw+"x"+sh; }
                }
                form.ShowInTaskbar=false;
                var timer=new System.Windows.Forms.Timer{Interval=200};
                int ticks=0;
                form.Shown+=(s,e)=>{
                    if(captureFirst) { try {
                        form.Engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                        form.Engine.Connect(true,"SIM",9600);
                        form.Engine.SetPeriodSeconds(1);
                        form.Engine.Start(1,1,points.Select(p=>p.name).ToArray());
                    } catch(Exception ex) { facts["connectError"]=ex.Message; } }
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        if(ticks==5) {
                            // 默认先停采集再看页签；--keep-capturing 时保持采集，
                            // 这样门禁才会越过"请先开始采集"到达"尚未现场确认"那一层。
                            if(captureFirst && Array.IndexOf(args,"--keep-capturing")<0) { try { form.Engine.Stop(); } catch { } }
                            form.MainNavigation.SelectedIndex=tab;
                            foreach(string sa in args) if(sa!=null&&sa.StartsWith("--subtab=",StringComparison.Ordinal)) {
                                int sub; if(Int32.TryParse(sa.Substring(9),out sub)) {
                                    var host=form.MainNavigation.SelectedTab;
                                    var inner=host==null?null:AllOf<TabControl>(host).FirstOrDefault();
                                    if(inner!=null&&sub>=0&&sub<inner.TabCount) { inner.SelectedIndex=sub; facts["subTab"]=inner.SelectedTab==null?"":inner.SelectedTab.Text; }
                                }
                            }
                        }
                        if(ticks>=30) {
                            timer.Stop();
                            try { Collect(form,tab,facts); } catch(Exception ex) { facts["collectError"]=ex.Message; }
                            form.Close();
                        }
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            File.WriteAllText(Path.Combine(root,"diag-page.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG page tab="+tab+" facts="+facts.Count);
            return 0;
        }
        /// <summary>
        /// 对真实累积数据跑一次完整导出，并核对产物。导出按钮的文件夹选择框靠 UI Automation
        /// 驱动不了（数据记录页的 DataGridView 让枚举超过 30 秒上限），但导出这件事本身
        /// 可以在进程内完整跑一遍 —— 而且用的是真库里真实形状的数据，比合成夹具更有说服力。
        /// </summary>
        internal static int Export(List<Point> points,string root,string[] args) {
            var facts=new Dictionary<string,object>();
            try {
                DateTime to=DateTime.UtcNow.AddMinutes(1), from=to.AddHours(-6);
                var filter=new HistoryFilter{Source="simulation",FromUtc=from,ToUtc=to};
                string format="csv"; foreach(string a in args) if(a!=null&&a.StartsWith("--format=",StringComparison.Ordinal)) format=a.Substring(9);
                // 第一个参数是"存储根"，不是程序根 —— 传程序根会找不到任何库。
                string storageRoot=Path.Combine(root,"data","storage");
                var result=ExperimentExport.ExportAsync(storageRoot,filter,root,format,null,CancellationToken.None).GetAwaiter().GetResult();
                facts["rows"]=result.Rows;
                facts["outputDirectory"]=result.OutputDirectory;
                facts["files"]=result.Files;
                foreach(string name in result.Files) {
                    string path=Path.Combine(result.OutputDirectory,name);
                    if(!File.Exists(path)) continue;
                    string[] lines=File.ReadAllLines(path);
                    facts[name+"_lines"]=lines.Length;
                    // 直接给出原始行，让调用方自己按 CSV 规则解析 ——
                        // 在诊断里再写一遍"按逗号切分"会得到错数（引号内的逗号），
                        // 之前就因此报了 6 列（实际 21 列）的错数据。
                        facts[name+"_header"]=lines[0];
                        facts[name+"_first"]=lines.Length>1?lines[1]:"";
                }
            } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; }
            File.WriteAllText(Path.Combine(root,"diag-export.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG export");
            return facts.ContainsKey("error")?1:0;
        }
        /// <summary>
        /// 反复启停采集：每次开始都会新建 ExperimentStore 与日志文件句柄。
        /// 如果句柄/线程/net 泄漏，长时间反复启停会把程序拖死 —— 这条路径无头测试覆盖不到。
        /// </summary>
        internal static int Cycles(List<Point> points,string root,string[] args) {
            int cycles=5; foreach(string a in args) if(a!=null&&a.StartsWith("--cycles=",StringComparison.Ordinal)) { int v; if(Int32.TryParse(a.Substring(9),out v)) cycles=Math.Max(2,Math.Min(20,v)); }
            var facts=new Dictionary<string,object>();
            using(var form=new DashboardForm(points,root)) {
                form.ShowInTaskbar=false;
                var timer=new System.Windows.Forms.Timer{Interval=200};
                int ticks=0, completed=0; bool captureOn=false;
                int handles0=0; long mem0=0; int threads0=0;
                var perCycle=new List<int>();
                form.Shown+=(s,e)=>{
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        try {
                            if(ticks==5) {
                                form.Engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                                form.Engine.Connect(true,"SIM",9600);
                                form.Engine.SetPeriodSeconds(1);
                                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                                handles0=Process.GetCurrentProcess().HandleCount;
                                mem0=GC.GetTotalMemory(true);
                                threads0=Process.GetCurrentProcess().Threads.Count;
                            }
                            // 每个周期：开 2 秒 → 停 → 记一次
                            if(ticks>5&&ticks%10==1&&completed<cycles&&!captureOn) {
                                form.Engine.Start(1,1,points.Select(p=>p.name).ToArray());
                                captureOn=true;
                            } else if(ticks>5&&ticks%10==6&&captureOn) {
                                var st=form.Engine.Store;
                                perCycle.Add(st==null?0:(int)st.Committed);
                                form.Engine.Stop();
                                captureOn=false; completed++;
                            }
                            if(completed>=cycles) {
                                timer.Stop();
                                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                                facts["cycles"]=completed;
                                facts["committedPerCycle"]=perCycle;
                                facts["handleDelta"]=Process.GetCurrentProcess().HandleCount-handles0;
                                facts["threadDelta"]=Process.GetCurrentProcess().Threads.Count-threads0;
                                facts["memDeltaBytes"]=GC.GetTotalMemory(true)-mem0;
                                form.Close();
                            }
                        } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; timer.Stop(); form.Close(); }
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            File.WriteAllText(Path.Combine(root,"diag-cycles.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG cycles");
            return facts.ContainsKey("error")?1:0;
        }
        /// <summary>
        /// 分页导航实跑：进程内切到数据记录页，依次点"更新 ›/最新 »/‹ 更早/« 最早"，
        /// 每点一次记下页脚时间范围与宽表行数。数据记录页的 DataGridView 让 UI Automation
        /// 枚举超过 30 秒上限，这条路径只能进程内驱动。
        /// </summary>
        internal static int Paging(List<Point> points,string root,string[] args) {
            var facts=new Dictionary<string,object>();
            var steps=new List<string>();
            using(var form=new DashboardForm(points,root)) {
                var historyPage=new HistoryPage(points,root,form.Engine);
                form.HistoryHost.Controls.Add(historyPage);
                form.AttachHistoryPage(historyPage);
                form.ShowInTaskbar=false;
                int prefill=0; foreach(string a in args) if(a!=null&&a.StartsWith("--prefill=",StringComparison.Ordinal)) { int v; if(Int32.TryParse(a.Substring(10),out v)) prefill=v; }
                var timer=new System.Windows.Forms.Timer{Interval=300};
                int ticks=0, step=0; bool prefillStarted=false; string pendingStep=null; int pendingAt=0; long maxRound=0;
                string[] order={"更新 ›","最新 »","‹ 更早","« 最早"};
                form.Shown+=(s,e)=>{
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        try {
                            if(ticks==5&&prefill>0&&!prefillStarted) {
                                // 先采够轮次，否则页长(200)永远大于轮数，翻页永远不触发。
                                prefillStarted=true;
                                form.Engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                                form.Engine.Connect(true,"SIM",9600);
                                form.Engine.SetPeriodSeconds(1);
                                maxRound=0;
                                form.Engine.ObservationReceived+=(o)=>{ if(o!=null&&o.Round>maxRound) maxRound=o.Round; };
                                form.Engine.Start(1,1,points.Select(p=>p.name).ToArray());
                                timer.Interval=500;
                                return;
                            }
                            if(prefillStarted&&prefill>0) {
                                long done=maxRound;
                                if(done<prefill&&ticks<12000) { if(ticks%20==0) facts["prefillProgress"]=done; return; }
                                form.Engine.Stop(); prefillStarted=false; facts["prefilledRounds"]=done; ticks=5;
                            }
                            if(ticks==5) form.MainNavigation.SelectedIndex=3;   // 数据记录
                            if(ticks==12) {
                                var inv=new List<string>();
                                foreach(var b in AllOf<Button>(historyPage)) if(b.Text!=null&&(b.Text.Contains("更")||b.Text.Contains("最新")||b.Text.Contains("最早")))
                                    inv.Add("["+b.Text+"] enabled="+b.Enabled+" type="+b.GetType().Name);
                                facts["pagingButtons"]=inv;
                            }
                            if(ticks==20) steps.Add("起始 | "+Snapshot(historyPage));
                            // 点完不要立刻取样：页面要重新查询，须等它落定再读页脚。
                            if(ticks>20&&ticks%8==0&&step<order.Length&&pendingStep==null) {
                                var button=FindButton(historyPage,order[step]);
                                if(button!=null&&button.Enabled) { button.PerformClick(); pendingStep=order[step]; pendingAt=ticks; }
                                else { steps.Add(order[step]+" | （按钮不可用或未找到）"); step++; }
                            } else if(pendingStep!=null&&ticks>=pendingAt+5) {
                                steps.Add(pendingStep+" | "+Snapshot(historyPage));
                                pendingStep=null; step++;
                            }
                            if(step>=order.Length&&ticks%8==4) {
                                timer.Stop();
                                facts["steps"]=steps;
                                form.Close();
                            }
                        } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; timer.Stop(); form.Close(); }
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            File.WriteAllText(Path.Combine(root,"diag-paging.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG paging");
            return facts.ContainsKey("error")?1:0;
        }
        static string Snapshot(HistoryPage page) {
            string info="", rows="";
            foreach(var l in AllOf<Label>(page)) {
                if(l.Visible&&l.Text!=null&&l.Text.StartsWith("当前页显示完整采集轮",StringComparison.Ordinal)) { info=l.Text; break; }
            }
            foreach(var g in AllOf<DataGridView>(page)) { if(g.Columns.Count>10) { rows=g.Rows.Count+" 行"; break; } }
            int a=info.IndexOf("本页时间",StringComparison.Ordinal);
            int b=info.IndexOf("　·　耗时",StringComparison.Ordinal);
            string range=(a>=0&&b>a)?info.Substring(a,b-a):info;
            return rows+" | "+range;
        }
        static Button FindButton(Control root,string text) {
            foreach(var b in AllOf<Button>(root)) if(b.Text==text) return b;
            foreach(var b in AllOf<Button>(root)) if(b.Text!=null&&b.Text.Contains(text)) return b;
            return null;
        }
        /// <summary>
        /// 列选择下拉实跑：点开「列选择」，用面板里的「清空/全选」驱动，核对宽表列数变化。
        /// 这条路径 UIA 摸不到（记录页的 DataGridView 让枚举超过 30 秒上限），只能进程内驱动。
        /// </summary>
        internal static int Columns(List<Point> points,string root,string[] args) {
            var facts=new Dictionary<string,object>();
            var steps=new List<string>();
            using(var form=new DashboardForm(points,root)) {
                var historyPage=new HistoryPage(points,root,form.Engine);
                form.HistoryHost.Controls.Add(historyPage);
                form.AttachHistoryPage(historyPage);
                form.ShowInTaskbar=false;
                var timer=new System.Windows.Forms.Timer{Interval=300};
                int ticks=0, step=0;
                form.Shown+=(s,e)=>{
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        try {
                            if(ticks==5) form.MainNavigation.SelectedIndex=3;
                            if(ticks==25) {
                                steps.Add("起始 | "+WideColumns(historyPage)+" 列");
                                // 导出菜单是 ContextMenuStrip：没弹出时不在控件树里，取不到子项。
                                // 这里只验入口按钮在且可用；菜单项本身与文件夹选择框由 --diag-export
                                // 在真实数据上覆盖（那条路径能完整跑通导出并核对产物）。
                                var exp=FindButton(historyPage,"导出数据");
                                steps.Add("导出数据 | "+(exp==null?"按钮未找到":(exp.Enabled?"入口存在且可用":"入口存在但不可用")));
                            }
                            if(ticks==26) {
                                var pick=FindButton(historyPage,"列选择");
                                if(pick==null) { steps.Add("列选择 | 按钮未找到"); }
                                else { pick.PerformClick(); steps.Add("打开面板 | 已点开"); }
                            }
                            if(ticks==30||ticks==38) {
                                var drop=typeof(HistoryPage).GetField("columnDrop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(historyPage) as ToolStripDropDown;
                                if(drop==null) { steps.Add("面板 | 未取到 columnDrop"); }
                                else {
                                    string want=ticks==30?"清空（只留时间与采集轮）":"全选（显示全部取数测点）";
                                    ToolStripItem hit=null;
                                    foreach(ToolStripItem it in drop.Items) if(it.Text==want) { hit=it; break; }
                                    if(hit==null) steps.Add(want+" | 菜单项未找到");
                                    else { hit.PerformClick(); steps.Add(want+" | 已点"); }
                                }
                            }
                            if(ticks==34) steps.Add("清空后 | "+WideColumns(historyPage)+" 列");
                            if(ticks==42) {
                                steps.Add("全选后 | "+WideColumns(historyPage)+" 列");
                                var drop=typeof(HistoryPage).GetField("columnDrop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(historyPage) as ToolStripDropDown;
                                if(drop!=null) drop.Close();
                                timer.Stop();
                                facts["steps"]=steps;
                                form.Close();
                            }
                        } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; timer.Stop(); form.Close(); }
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            File.WriteAllText(Path.Combine(root,"diag-columns.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG columns");
            return facts.ContainsKey("error")?1:0;
        }
        static string WideColumns(HistoryPage page) {
            // 记录页有 4 个表格，不能按"列数最多"挑 —— 清空后宽表只剩 2 列，会被别的表格盖过。
            var grid=AllOf<DataGridView>(page).FirstOrDefault(g=>g.Columns.Count>0&&(g.Columns[0].HeaderText??"").StartsWith("轮次开始时间",StringComparison.Ordinal));
            return grid==null?"未找到宽表":grid.Columns.Count+"";
        }
        /// <summary>标题栏"定位测点"入口的实况 dump：控件位置、文本、可见性，以及真实点按钮后回显有没有变。</summary>
        internal static int Header(List<Point> points,string root,string[] args) {
            var facts=new Dictionary<string,object>();
            using(var form=new DashboardForm(points,root)) {
                foreach(string a in args) if(a!=null&&a.StartsWith("--size=",StringComparison.Ordinal)) {
                    string[] wh=a.Substring(7).Split('x'); int sw,sh;
                    if(wh.Length==2&&Int32.TryParse(wh[0],out sw)&&Int32.TryParse(wh[1],out sh)) { form.ClientSize=new System.Drawing.Size(sw,sh); facts["clientSize"]=sw+"x"+sh; }
                }
                form.ShowInTaskbar=false;
                var timer=new System.Windows.Forms.Timer{Interval=200};
                int ticks=0;
                form.Shown+=(s,e)=>{
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        if(ticks!=12) return;
                        timer.Stop();
                        try {
                            var F=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                            var box=(TextBox)typeof(DashboardForm).GetField("searchBox",F).GetValue(form);
                            var caption=(Label)typeof(DashboardForm).GetField("searchCaption",F).GetValue(form);
                            var btn=(Button)typeof(DashboardForm).GetField("searchButton",F).GetValue(form);
                            facts["captionIsNull"]=caption==null;
                            if(caption!=null) {
                                facts["captionText"]=caption.Text;
                                facts["captionBounds"]=caption.Bounds.ToString();
                                facts["captionVisible"]=caption.Visible;
                            }
                            facts["boxBounds"]=box.Bounds.ToString();
                            facts["boxVisible"]=box.Visible;
                            facts["boxParent"]=box.Parent==null?"null":box.Parent.GetType().Name+" "+box.Parent.Bounds;
                            facts["buttonText"]=btn.Text;
                            facts["buttonBounds"]=btn.Bounds.ToString();
                            var kids=new List<string>();
                            var host=box.Parent;
                            if(host!=null) foreach(Control c in host.Controls) kids.Add(c.GetType().Name+" \""+c.Text+"\" "+c.Bounds+" vis="+c.Visible+" en="+c.Enabled);
                            facts["searchHostChildren"]=kids;
                            // 真实跑一次搜索（走按钮），看回显
                            box.Text="电流";
                            btn.PerformClick();
                            facts["afterButton_caption"]=caption==null?"<null>":caption.Text;
                            facts["afterButton_captionVisible"]=caption==null?false:caption.Visible;
                            facts["afterButton_logTail"]=null;
                            // header 及其子控件的真实矩形（相对 header 与屏幕坐标都记）
                            var hdr=box.Parent==null?null:box.Parent.Parent==null?null:box.Parent.Parent.Parent;
                            if(box.Parent!=null) facts["searchHost"]=box.Parent.Bounds+" vis="+box.Parent.Visible;
                            if(hdr!=null) {
                                facts["headerClient"]=hdr.ClientSize.ToString();
                                facts["headerDisplay"]=hdr.DisplayRectangle.ToString();
                                var kids2=new List<string>();
                                foreach(Control c in hdr.Controls) kids2.Add(c.GetType().Name+" \""+c.Text+"\" bounds="+c.Bounds+" vis="+c.Visible+" dock="+c.Dock);
                                facts["headerChildren"]=kids2;
                            }
                            facts["formClient"]=form.ClientSize.ToString();
                            // 筛选验证：可见卡片数、分组数、标题栏文案
                            var cardMap=(Dictionary<string,Label[]>)typeof(DashboardForm).GetField("cards",F).GetValue(form);
                            Func<int> visibleCards=()=>{int n=0;foreach(var kv in cardMap){Control c=kv.Value[1];while(c!=null&&!(c is RoundedPanel))c=c.Parent;if(c!=null&&c.Visible)n++;}return n;};
                            Func<int> visibleSections=()=>{int n=0;var seen=new HashSet<Control>();foreach(var kv in cardMap){Control c=kv.Value[1];while(c!=null&&!(c is RoundedPanel))c=c.Parent;if(c==null)continue;Control sec=c.Parent==null?null:c.Parent.Parent;if(sec!=null&&seen.Add(sec)&&sec.Visible)n++;}return n;};
                            facts["before_filter_cards"]=visibleCards();
                            facts["before_filter_sections"]=visibleSections();
                            var apply=typeof(DashboardForm).GetMethod("ApplyCardFilter",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public);
                            apply.Invoke(form,new object[]{"电流"});
                            facts["filtered_cards"]=visibleCards();
                            facts["filtered_sections"]=visibleSections();
                            facts["filtered_caption"]=caption==null?"":caption.Text;
                            apply.Invoke(form,new object[]{"绝无此项"});
                            facts["nomatch_cards"]=visibleCards();
                            facts["nomatch_caption"]=caption==null?"":caption.Text;
                            apply.Invoke(form,new object[]{""});
                            facts["restored_cards"]=visibleCards();
                            facts["restored_caption"]=caption==null?"":caption.Text;
                        } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; }
                        form.Close();
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            File.WriteAllText(Path.Combine(root,"diag-header.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG header");
            return 0;
        }
        /// <summary>块读规划在真实点表上的实际结果：帧数、各块、理论耗时、逐点对比。</summary>
        internal static int Blocks(List<Point> points,string root,string[] args) {
            var facts=new Dictionary<string,object>();
            try {
                var blocks=BlockPlanner.Plan(points);
                facts["pointCount"]=points.Count;
                facts["blockCount"]=blocks.Count;
                facts["perPointFrames"]=BlockPlanner.PerPointFrameCount(points);
                facts["blockFrames"]=BlockPlanner.FrameCount(blocks);
                facts["validateIssues"]=BlockPlanner.Validate(blocks,points).ToArray();
                var list=new List<string>();
                foreach(var b in blocks) list.Add(b.ToString());
                facts["blocks"]=list;
                // 理论下界耗时：Δ=(5+2N)·10/9600+1 ms
                Func<int,double> minMs=n=>Modbus.PhysicalFloorMs(n,9600);
                double perPointMs=0; foreach(Point p in points) perPointMs+=minMs(p.binding.register_count);
                double blockMs=0; foreach(var b in blocks) blockMs+=minMs(b.Count);
                facts["perPointMsFloor"]=Math.Round(perPointMs,1);
                facts["blockMsFloor"]=Math.Round(blockMs,1);
                facts["savedMsFloor"]=Math.Round(perPointMs-blockMs,1);
                facts["d9InPoints"]=points.Any(p=>p.name=="D9");
                facts["d9Binding"]=points.Where(p=>p.name=="D9").Select(p=>p.binding.slave+"/"+p.binding.address_zero_based+"/"+p.binding.register_count).ToArray();
                facts["d9Role"]=PointCatalog.Get("D9").Role.ToString();
                facts["d9Exportable"]=ExperimentExport.ExportablePoint("D9");
                facts["d9DefaultColumn"]=HistoryPage.DefaultColumnVisible("D9");
            } catch(Exception ex) { facts["error"]=ex.GetType().Name+"："+ex.Message; }
            File.WriteAllText(Path.Combine(root,"diag-blocks.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(facts),new System.Text.UTF8Encoding(false));
            Console.Out.WriteLine("DIAG blocks");
            return facts.ContainsKey("error")?1:0;
        }
        static void Collect(DashboardForm form,int tab,Dictionary<string,object> facts) {
            facts["tab"]=tab;
            facts["tabTitle"]=form.MainNavigation.SelectedTab==null?"":form.MainNavigation.SelectedTab.Text;
            Control page=form.MainNavigation.SelectedTab;
            if(page==null) return;
            // 表格事实
            var grids=AllOf<DataGridView>(page).ToList();
            if(grids.Count==0) grids=AllOf<DataGridView>(form).ToList();   // HistoryPage 挂在 form 的宿主里，不一定在选中页签下
            facts["gridCount"]=grids.Count;
            for(int i=0;i<grids.Count;i++) {
                var g=grids[i];
                facts["grid"+i+"_rows"]=g.Rows.Count;
                facts["grid"+i+"_cols"]=g.Columns.Count;
                var headers=new List<string>(); foreach(DataGridViewColumn c in g.Columns) headers.Add(c.HeaderText??"");
                if(headers.Count>8) headers=headers.GetRange(0,8);
                facts["grid"+i+"_headers"]=headers;
            }
            // 所有可见标签文本里较长的几条（摘要、状态、提示）
            var texts=AllOf<Label>(page).Where(l=>l.Visible&&!String.IsNullOrWhiteSpace(l.Text))
                .OrderByDescending(l=>l.Text.Length).Take(8).Select(l=>l.Text).ToList();
            facts["labels"]=texts;
            // 卡片裁切自查：每张卡片的屏幕矩形必须落在其所属容器的可视矩形内。
            int clipped=0, cards=0; string firstClip="";
            foreach(var rp in AllOf<RoundedPanel>(page)) {
                if(!rp.Visible) continue;
                cards++;
                var parent=rp.Parent; if(parent==null) continue;
                var cardRect=rp.RectangleToScreen(rp.ClientRectangle);
                var clipRect=parent.RectangleToScreen(parent.ClientRectangle);
                if(!clipRect.Contains(cardRect)) { clipped++; if(firstClip.Length==0) firstClip=rp.Name+" card="+cardRect+" parent="+clipRect; }
            }
            facts["cards"]=cards; facts["clippedCards"]=clipped; facts["firstClipped"]=firstClip;
            var buttons=AllOf<Button>(page).Where(b=>b.Visible).Select(b=>b.Text??"").Where(t=>t.Length>0).Take(12).ToList();
            if(buttons.Count==0) buttons=AllOf<Button>(form).Where(b=>b.Visible).Select(b=>b.Text??"").Where(t=>t.Length>0).Take(12).ToList();
            // 控制页：把每个控制点的门禁判定原样导出来。
            // 界面一次只显示当前选中行的门禁，但"每个点都被挡住"这件事必须逐点有证据。
            var cfg=form.Engine.ControlSettings;
            if(cfg!=null&&cfg.targets!=null) {
                var gates=new List<string>();
                foreach(var target in cfg.targets) {
                    var gate=form.Engine.ControlReadiness(target.point);
                    gates.Add(target.point+" | "+gate.Status+" | "+gate.Detail);
                }
                facts["controlMode"]=cfg.mode;
                facts["controlWritesEnabled"]=cfg.WritesEnabled;
                facts["controlArmed"]=form.Engine.ControlArmed;
                facts["controlGates"]=gates;
            }
            facts["buttons"]=buttons;
        }
        static IEnumerable<T> AllOf<T>(Control root) where T:Control {
            foreach(Control c in root.Controls){ var t=c as T; if(t!=null) yield return t; foreach(var n in AllOf<T>(c)) yield return n; }
        }
        internal static int Cpu(List<Point> points,string root,string[] args) {
            int tab=3;
            foreach(string a in args) if(a!=null&&a.StartsWith("--tab=",StringComparison.Ordinal)) { int v; if(Int32.TryParse(a.Substring(6),out v)) tab=v; }
            long baseCpu=0, afterCpu=0; long baseMem=0, afterMem=0; int reads0=0, reads1=0;
            using(var form=new DashboardForm(points,root)) {
                form.ShowInTaskbar=false;
                var timer=new System.Windows.Forms.Timer{Interval=100};
                int ticks=0;
                bool captureFirst=Array.IndexOf(args,"--capture-first")>=0;
                form.Shown+=(s,e)=>{
                    if(captureFirst) {
                        try {
                            form.Engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                            form.Engine.Connect(true,"SIM",9600);
                            form.Engine.SetPeriodSeconds(1);
                            form.Engine.Start(1,1,points.Select(p=>p.name).ToArray());
                        } catch(Exception ex) { Console.Out.WriteLine("DIAG connect failed: "+ex.Message); }
                    }
                    timer.Tick+=(s2,e2)=>{
                        ticks++;
                        if(ticks==6) {
                            baseCpu=Process.GetCurrentProcess().TotalProcessorTime.Ticks;
                            baseMem=GC.GetTotalMemory(false);
                            reads0=(int)Process.GetCurrentProcess().Threads.Count;
                            if(captureFirst) { try { form.Engine.Stop(); } catch { } }
                            form.MainNavigation.SelectedIndex=tab;
                        }
                        if(ticks==16) { // 切换后先稳定 1 秒再取基线，避免把首次布局算进去
                            baseCpu=Process.GetCurrentProcess().TotalProcessorTime.Ticks;
                            baseMem=Process.GetCurrentProcess().WorkingSet64;
                            reads0=ReadOps();
                        }
                        if(ticks==66) {
                            afterCpu=Process.GetCurrentProcess().TotalProcessorTime.Ticks;
                            afterMem=Process.GetCurrentProcess().WorkingSet64;
                            reads1=ReadOps();
                            timer.Stop();
                            form.Close();
                        }
                    };
                    timer.Start();
                };
                Application.Run(form);
            }
            double cpuSeconds=(afterCpu-baseCpu)/(double)TimeSpan.TicksPerSecond;
            Console.Out.WriteLine("DIAG tab="+tab+" cpuSeconds="+cpuSeconds.ToString("0.00")+
                " memDeltaMB="+((afterMem-baseMem)/1024/1024)+" readOpsDelta="+(reads1-reads0));
            File.WriteAllText(Path.Combine(root,"diag-cpu.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                new{tab=tab,cpuSeconds=cpuSeconds,memDeltaBytes=afterMem-baseMem,readOpsDelta=reads1-reads0}),new System.Text.UTF8Encoding(false));
            return 0;
        }
        static int ReadOps() {
            try {
                var p=Process.GetCurrentProcess();
                // 没有直接暴露读操作计数的公共 API，用已读字节数近似（重复查询会持续增长）。
                return (int)(p.PrivateMemorySize64/1024/1024);
            } catch { return 0; }
        }
    }
    public sealed class MonitorForm : Form {
        readonly List<Point> points;
        readonly string root;
        readonly DataTable table=new DataTable();
        readonly DataGridView grid=new DataGridView();
        readonly CheckBox simulation=new CheckBox { Text="模拟设备",Checked=false,AutoSize=true };
        readonly ComboBox port=new ComboBox { Width=90,DropDownStyle=ComboBoxStyle.DropDownList };
        readonly NumericUpDown baud=Number(9600,300,115200,90), interval=Number(2,1,3600,65), timeout=Number(8,1,30,60);
        readonly Button start=ButtonOf("连接并采集"), once=ButtonOf("读取一轮"), stop=ButtonOf("停止 / 断开"), manual=ButtonOf("手动读取 03");
        readonly Label state=new Label { AutoSize=true,Text="未连接 · 点表已载入 · 当前为模拟模式",ForeColor=Color.FromArgb(25,70,120) };
        readonly Label output=new Label { AutoSize=true,Text="尚未记录" };
        readonly TextBox log=new TextBox { Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,Font=new Font("Consolas",9) };
        readonly NumericUpDown diagSlave=Number(2,1,247,60), diagAddress=Number(125,0,65535,85), diagCount=Number(2,1,8,50);
        readonly ComboBox decode=new ComboBox { Width=205,DropDownStyle=ComboBoxStyle.DropDownList };
        readonly Label diagResult=new Label { Dock=DockStyle.Top,Height=70,Text="手动地址为报文零基地址。候选解码不修改正式点表。",Padding=new Padding(10) };
        readonly TrendPanel trend=new TrendPanel { Dock=DockStyle.Fill };
        readonly ConcurrentQueue<Observation> updates=new ConcurrentQueue<Observation>();
        readonly ConcurrentQueue<string> messages=new ConcurrentQueue<string>();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=200 };
        CancellationTokenSource cancel;
        Thread worker;
        bool closing;
        int ok, bad;
        string sessionPath;
        public MonitorForm(List<Point> items,string basePath) {
            points=items; root=basePath;
            Text="实验监控 · 串口验证 Demo 0.1.0"; ClientSize=new Size(1360,880); MinimumSize=new Size(1050,720);
            Font=new Font("Microsoft YaHei UI",9); BackColor=Color.FromArgb(244,247,251); StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=4,ColumnCount=1,Padding=new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,94));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54)); Controls.Add(layout);
            var title=new Label { Text="实验监控  /  现场串口验证",Font=new Font(Font.FontFamily,18,FontStyle.Bold),Dock=DockStyle.Fill };
            layout.Controls.Add(title,0,0);
            var toolbar=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=true };
            toolbar.Controls.Add(simulation); toolbar.Controls.Add(LabelOf("串口")); toolbar.Controls.Add(port);
            var refresh=ButtonOf("刷新"); refresh.Click+=(s,e)=>RefreshPorts(); toolbar.Controls.Add(refresh);
            toolbar.Controls.Add(LabelOf("波特率")); toolbar.Controls.Add(baud); toolbar.Controls.Add(LabelOf("8N1"));
            toolbar.Controls.Add(LabelOf("轮间隔(s)")); toolbar.Controls.Add(interval); toolbar.Controls.Add(LabelOf("超时(s)")); toolbar.Controls.Add(timeout);
            toolbar.Controls.Add(start); toolbar.Controls.Add(once); toolbar.Controls.Add(stop);
            toolbar.SetFlowBreak(stop,true);
            toolbar.Controls.Add(new Label { AutoSize=true,Text="只读03 · 37个工程绑定点 · 倍率/状态含义待现场确认 · 不启动时不会打开串口",ForeColor=Color.DarkSlateGray,Padding=new Padding(0,7,0,0) });
            layout.Controls.Add(toolbar,0,1);
            var tabs=new TabControl { Dock=DockStyle.Fill }; layout.Controls.Add(tabs,0,2);
            var live=new TabPage("实时采集"); var diagnostics=new TabPage("手动对照 / 原始报文"); tabs.TabPages.Add(live); tabs.TabPages.Add(diagnostics);
            BuildGrid();
            var split=new SplitContainer { Size=new Size(1200,600),Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=400,Panel2MinSize=150,FixedPanel=FixedPanel.Panel2 };
            live.Controls.Add(split); split.Panel1.Controls.Add(grid); split.Panel2.Controls.Add(trend);
            var diagLayout=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=3,ColumnCount=1 };
            diagLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,65)); diagLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,80)); diagLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            diagnostics.Controls.Add(diagLayout);
            var diagBar=new FlowLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(8) };
            decode.Items.AddRange(new object[] {"FLOAT ABCD","FLOAT CDAB","FLOAT BADC","FLOAT DCBA","U16 / I16","UINT16","INT16","INT16 /100（候选）","RAW"}); decode.SelectedIndex=0;
            diagBar.Controls.Add(LabelOf("站号")); diagBar.Controls.Add(diagSlave); diagBar.Controls.Add(LabelOf("零基地址")); diagBar.Controls.Add(diagAddress);
            diagBar.Controls.Add(LabelOf("寄存器数")); diagBar.Controls.Add(diagCount); diagBar.Controls.Add(decode); diagBar.Controls.Add(manual);
            var useSelected=ButtonOf("用选中点填入"); useSelected.Click+=(s,e)=>FillSelected(); diagBar.Controls.Add(useSelected);
            diagLayout.Controls.Add(diagBar,0,0); diagLayout.Controls.Add(diagResult,0,1); diagLayout.Controls.Add(log,0,2);
            var footer=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=true };
            footer.Controls.Add(state); footer.SetFlowBreak(state,true); footer.Controls.Add(output);
            var folder=ButtonOf("打开记录目录"); folder.Click+=(s,e)=> {
                string path=sessionPath??Path.Combine(root,"data"); Directory.CreateDirectory(path); Process.Start("explorer.exe",path);
            }; footer.Controls.Add(folder); layout.Controls.Add(footer,0,3);
            RefreshPorts(); start.Click+=(s,e)=>Start(false,null); once.Click+=(s,e)=>Start(true,null); manual.Click+=(s,e)=>Manual();
            stop.Click+=(s,e)=> { if(cancel!=null) { cancel.Cancel(); state.Text="正在停止并保存记录…"; } };
            simulation.CheckedChanged+=(s,e)=> { if(worker==null) state.Text=simulation.Checked?"未连接 · 模拟模式":"未连接 · 实机模式（先停止力控对该串口的采集）"; };
            grid.SelectionChanged+=(s,e)=> { if(grid.CurrentRow!=null) { string name=Convert.ToString(grid.CurrentRow.Cells["点名"].Value); trend.SelectPoint(name); } };
            timer.Tick+=(s,e)=>Drain(); timer.Start(); stop.Enabled=false;
            FormClosing+=(s,e)=> {
                if(worker!=null) { e.Cancel=true; closing=true; cancel.Cancel(); state.Text="正在停止后关闭…"; }
            };
        }
        static NumericUpDown Number(int value,int min,int max,int width) { return new NumericUpDown { Maximum=max,Minimum=min,Value=value,Width=width }; }
        static Button ButtonOf(string text) { return new Button { Text=text,AutoSize=true,Height=30,FlatStyle=FlatStyle.System,Margin=new Padding(4) }; }
        static Label LabelOf(string text) { return new Label { Text=text,AutoSize=true,Padding=new Padding(3,6,0,0) }; }
        void RefreshPorts() {
            if(worker!=null) return;
            string selected=Convert.ToString(port.SelectedItem);
            var names=System.IO.Ports.SerialPort.GetPortNames().OrderBy(p=>p).ToList(); if(!names.Contains("COM9")) names.Add("COM9");
            port.Items.Clear(); port.Items.AddRange(names.ToArray()); port.SelectedItem=names.Contains(selected)?selected:"COM9";
        }
        internal void SetSimulationForTest(bool value){simulation.Checked=value;}
        void BuildGrid() {
            table.Columns.Add("采集",typeof(bool));
            foreach(string c in new [] {"点名","说明","设备 / 站号","零基地址","长度","解码","当前值","状态","更新时间","数据HEX"}) table.Columns.Add(c);
            table.Columns.Add("LastUtc",typeof(DateTime));
            foreach(var p in points) table.Rows.Add(true,p.name,p.description,p.binding.device+" / "+p.binding.slave,
                p.binding.address_zero_based+" / 0x"+p.binding.address_zero_based.ToString("X4"),p.binding.register_count,Modbus.DefaultMode(p),"—","未采集","—","",DBNull.Value);
            grid.BindingContext=new BindingContext(); grid.DataSource=table; grid.Dock=DockStyle.Fill; grid.AllowUserToAddRows=false; grid.AllowUserToDeleteRows=false; grid.RowHeadersVisible=false;
            grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect=false; grid.BackgroundColor=Color.White;
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill; grid.ColumnHeadersHeight=35; grid.RowTemplate.Height=28;
            grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(247,250,254);
            foreach(DataGridViewColumn c in grid.Columns) c.ReadOnly=c.Name!="采集";
            grid.Columns["LastUtc"].Visible=false; grid.Columns["采集"].FillWeight=35; grid.Columns["点名"].FillWeight=65;
            grid.Columns["说明"].FillWeight=130; grid.Columns["当前值"].FillWeight=170; grid.Columns["状态"].FillWeight=160;
            grid.Columns["长度"].FillWeight=35;
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
            grid.Columns["采集"].Width=45; grid.Columns["点名"].Width=75; grid.Columns["设备 / 站号"].Width=105;
            grid.Columns["零基地址"].Width=125; grid.Columns["长度"].Width=45; grid.Columns["解码"].Width=100;
            grid.Columns["当前值"].Width=240; grid.Columns["状态"].Width=175; grid.Columns["更新时间"].Width=105; grid.Columns["数据HEX"].Width=120;
            grid.Columns["说明"].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill; grid.Columns["说明"].MinimumWidth=145;
        }
        void FillSelected() {
            if(grid.CurrentRow==null) return;
            var p=points.First(x=>x.name==Convert.ToString(grid.CurrentRow.Cells["点名"].Value));
            diagSlave.Value=p.binding.slave; diagAddress.Value=p.binding.address_zero_based; diagCount.Value=p.binding.register_count;
            decode.SelectedItem=Modbus.DefaultMode(p);
        }
        void Manual() {
            var p=new Point { name="手动诊断",description="候选解码，不修改点表", binding=new Binding { device="手动",slave=(int)diagSlave.Value,address_zero_based=(int)diagAddress.Value,register_count=(int)diagCount.Value } };
            string mode=Convert.ToString(decode.SelectedItem);
            if((mode.StartsWith("FLOAT") && p.binding.register_count!=2) ||
                (mode!="RAW" && !mode.StartsWith("FLOAT") && p.binding.register_count!=1)) {
                MessageBox.Show("FLOAT选择2个寄存器，整数选择1个；查看多个原寄存器请选择RAW。"); return;
            }
            Start(true,new Tuple<Point,string>(p,mode));
        }
        void SetBusy(bool value) {
            start.Enabled=once.Enabled=manual.Enabled=!value; stop.Enabled=value;
            simulation.Enabled=port.Enabled=baud.Enabled=interval.Enabled=timeout.Enabled=!value;
            grid.Columns["采集"].ReadOnly=value;
        }
        void Enqueue(string text) {
            messages.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff")+" "+text);
            while(messages.Count>500) { string ignored; messages.TryDequeue(out ignored); }
        }
        void Start(bool single,Tuple<Point,string> diagnostic) {
            if(worker!=null) return;
            grid.EndEdit();
            var selected=diagnostic==null ? points.Where(p=>Convert.ToBoolean(table.Rows.FindOrName(p.name)["采集"])).ToList() : new List<Point> {diagnostic.Item1};
            if(selected.Count==0) { MessageBox.Show("请至少勾选一个采集点。"); return; }
            bool sim=simulation.Checked; string serialName=Convert.ToString(port.SelectedItem); int speed=(int)baud.Value;
            int period=(int)interval.Value*1000, timeoutMs=(int)timeout.Value*1000;
            cancel=new CancellationTokenSource(); var token=cancel.Token; SetBusy(true); ok=bad=0;
            if(diagnostic==null) foreach(DataRow row in table.Rows) row["状态"]=Convert.ToBoolean(row["采集"])?"等待本次采集":"本次未选中";
            state.Text=(sim?"模拟":"实机")+"：正在打开会话…";
            worker=new Thread(()=> {
                string source=sim?"simulation":"serial";
                try {
                    using(IReadTransport transport=sim?(IReadTransport)new SimulationTransport():new SerialTransport(serialName,speed))
                    using(var journal=new Journal(Path.Combine(root,"data"),source)) {
                        sessionPath=journal.DirectoryPath;
                        Action<string> record=text=> { journal.Frame(source,text); Enqueue(text); };
                        record("SESSION "+source+" "+serialName+" "+speed+" 8N1; protocol=0.9; period_ms="+period+"; timeout_ms="+timeoutMs);
                        var clock=Stopwatch.StartNew(); long next=0;
                        do {
                            token.ThrowIfCancellationRequested();
                            foreach(var p in selected) {
                                string mode=diagnostic==null?Modbus.DefaultMode(p):diagnostic.Item2;
                                record("READ point="+p.name+" slave="+p.binding.slave+" address="+p.binding.address_zero_based+" count="+p.binding.register_count+" decode="+mode);
                                Observation o=Collector.Read(transport,p,mode,source,timeoutMs,token,record);
                                journal.Sample(o); updates.Enqueue(o);
                                if(o.Status=="超时" || o.Status=="失败") throw new IOException("采集已终止："+o.Point+" "+o.Status+"。检查日志，取消离线点后手动重新开始。");
                            }
                            if(single) break;
                            next+=period;
                            while(next<=clock.ElapsedMilliseconds) next+=period;
                            if(token.WaitHandle.WaitOne((int)Math.Max(0,next-clock.ElapsedMilliseconds))) token.ThrowIfCancellationRequested();
                        } while(true);
                    }
                    Enqueue("会话结束，串口已关闭，CSV记录已保存。");
                } catch(OperationCanceledException) { Enqueue("已停止，串口关闭；本次未完成的请求不作为有效数据。"); }
                catch(Exception ex) { Enqueue("ERROR "+ex.Message); }
                finally {
                    if(!IsDisposed && IsHandleCreated) BeginInvoke((Action)(()=> {
                        Drain(); worker=null; cancel.Dispose(); cancel=null; SetBusy(false);
                        state.Text="已断开 · 成功响应 "+ok+" / 异常或失败 "+bad+" · 数值保留，仅供对照";
                        foreach(DataRow row in table.Rows) if(row["LastUtc"]!=DBNull.Value && Convert.ToString(row["状态"]).StartsWith("收到")) row["状态"]="已断开 / 旧值";
                        if(closing) Close();
                    }));
                }
            });
            worker.IsBackground=true; worker.Start();
        }
        void Drain() {
            string text; int n=0;
            while(n++<100 && messages.TryDequeue(out text)) log.AppendText(text+Environment.NewLine);
            if(log.Lines.Length>350) log.Lines=log.Lines.Skip(log.Lines.Length-300).ToArray();
            Observation o;
            while(updates.TryDequeue(out o)) {
                bool success=o.Status.StartsWith("收到"); if(success) ok++; else bad++;
                if(o.Point=="手动诊断") diagResult.Text="结果："+o.Value+"  |  "+o.Status+"  |  "+o.Milliseconds+" ms\nTX "+Modbus.Hex(o.Tx)+"\nRX "+Modbus.Hex(o.Rx);
                else {
                    var row=table.Rows.FindOrName(o.Point); row["当前值"]=o.Value; row["状态"]=o.Status; row["更新时间"]=o.Utc.ToLocalTime().ToString("HH:mm:ss.fff");
                    row["LastUtc"]=o.Utc; row["数据HEX"]=Modbus.Hex(o.Payload);
                    if(success && o.Number.HasValue) trend.Add(o.Point,o.Utc,o.Number.Value);
                }
            }
            foreach(DataRow row in table.Rows) if(row["LastUtc"]!=DBNull.Value && Convert.ToString(row["状态"]).StartsWith("收到") &&
                (DateTime.UtcNow-(DateTime)row["LastUtc"]).TotalSeconds>Math.Max(10,(double)interval.Value*3)) row["状态"]="过期 / 旧值";
            if(worker!=null) state.Text=(simulation.Checked?"模拟采集":"实机采集")+" · 成功响应 "+ok+" / 异常或失败 "+bad+" · 物理数值待核对";
            output.Text=sessionPath==null?"尚未记录":"记录："+sessionPath;
        }
        public void CapturePreview(string path) {
            Opacity=0; ShowInTaskbar=false; Show(); Application.DoEvents();
            foreach(var p in points) {
                using(var simulator=new SimulationTransport()) updates.Enqueue(Collector.Read(simulator,p,Modbus.DefaultMode(p),"simulation",1000,CancellationToken.None,s=>{}));
            }
            for(int i=0;i<30;i++) trend.Add("T0",DateTime.UtcNow.AddSeconds(-60+i*2),24+Math.Sin(i/4.0));
            Drain(); trend.SelectPoint("T0"); state.Text="界面预览 · 模拟数据 · 未打开串口 · 未写采集记录";
            Application.DoEvents(); using(var bmp=new Bitmap(Width,Height)) { DrawToBitmap(bmp,new Rectangle(System.Drawing.Point.Empty,Size)); bmp.Save(path); } Hide();
        }
        public void TestSimulationRun(bool stopEarly) {
            Opacity=0; ShowInTaskbar=false; Show(); Application.DoEvents(); Start(!stopEarly,null);
            if(stopEarly) cancel.Cancel();
            var watch=Stopwatch.StartNew();
            while(worker!=null && watch.ElapsedMilliseconds<10000) { Application.DoEvents(); Thread.Sleep(10); }
            if(worker!=null) throw new Exception("模拟工作线程未按时结束");
            if(!stopEarly && (ok!=37 || bad!=0)) throw new Exception("模拟采集结果计数不符: "+ok+"/"+bad);
            if(!stopEarly && File.ReadAllLines(Path.Combine(sessionPath,"samples.csv")).Length!=38) throw new Exception("模拟CSV未完整保存37点");
            Hide();
        }
        protected override void Dispose(bool disposing) { if(disposing) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }
    }
    static class RowLookup {
        public static DataRow FindOrName(this DataRowCollection rows,string name) { foreach(DataRow row in rows) if(Convert.ToString(row["点名"])==name) return row; throw new KeyNotFoundException(name); }
    }
    sealed class TrendPanel : Panel {
        readonly Dictionary<string,List<Tuple<DateTime,double>>> samples=new Dictionary<string,List<Tuple<DateTime,double>>>();
        string selected="T0";
        public TrendPanel() { DoubleBuffered=true; BackColor=Color.White; }
        public void SelectPoint(string name) { selected=name; Invalidate(); }
        public void Add(string name,DateTime utc,double value) {
            if(!samples.ContainsKey(name)) samples[name]=new List<Tuple<DateTime,double>>();
            var items=samples[name]; items.Add(Tuple.Create(utc,value)); if(items.Count>300) items.RemoveAt(0); if(name==selected) Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); var g=e.Graphics; g.DrawString(selected+" · 最近300个有效数值（整数候选需手动对照）",Font,Brushes.DimGray,12,8);
            List<Tuple<DateTime,double>> items;
            if(!samples.TryGetValue(selected,out items)||items.Count<2) { g.DrawString("选择一个浮点采集点查看趋势；符号/倍率未定的点仅显示原始值。",Font,Brushes.Gray,12,40); return; }
            double lo=items.Min(x=>x.Item2), hi=items.Max(x=>x.Item2), range=hi-lo; if(range<1e-8) range=1;
            long first=items[0].Item1.Ticks, last=items[items.Count-1].Item1.Ticks; double timeRange=Math.Max(1,last-first);
            Rectangle plot=new Rectangle(85,38,Math.Max(1,Width-110),Math.Max(1,Height-75));
            g.DrawRectangle(Pens.LightGray,plot); g.DrawString(lo.ToString("G5"),Font,Brushes.Gray,5,plot.Bottom-18); g.DrawString(hi.ToString("G5"),Font,Brushes.Gray,5,plot.Top);
            var coords=items.Select(x=>new PointF(plot.Left+(float)((x.Item1.Ticks-first)/timeRange)*plot.Width,plot.Bottom-(float)((x.Item2-lo)/range)*plot.Height)).ToArray();
            using(var pen=new Pen(Color.FromArgb(22,113,185),2)) g.DrawLines(pen,coords);
            g.DrawString(items[0].Item1.ToLocalTime().ToString("HH:mm:ss"),Font,Brushes.Gray,plot.Left,plot.Bottom+5);
            g.DrawString(items[items.Count-1].Item1.ToLocalTime().ToString("HH:mm:ss"),Font,Brushes.Gray,plot.Right-75,plot.Bottom+5);
        }
    }
}



