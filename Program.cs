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
                if(args.Contains("--export-config")) {File.WriteAllText(Path.Combine(root,"point-contract.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{module="experiment",schemaVersion=1,catalogVersion=PointCatalog.Version,points=points.Select(p=>new {id=p.name,equipmentId=p.binding.device,slave=p.binding.slave,addressZeroBased=p.binding.address_zero_based,registerCount=p.binding.register_count,metadata=PointCatalog.Get(p)}).ToArray()}));return 0;}
                if(args.Contains("--self-test")) { SelfTests.Run(points,root); ExtendedTests.Run(points,root); return 0; }
                if(args.Contains("--test-core")) { var checks=CoreTests.Run(points,root);File.WriteAllText(Path.Combine(root,"core-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=checks.Count,checks=checks,hardware_tested=false}));return 0; }
                if(args.Contains("--test-storage")) { int storage=StorageTests.Run(),records=RecordsTests.Run(),failures=ExportFailureTests.Run(points,root);File.WriteAllText(Path.Combine(root,"records-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,storageChecks=storage,recordsChecks=records,failureChecks=failures,count=storage+records+failures,hardware_tested=false}));return 0; }
                if(args.Contains("--test-cloud")) { int count=CloudSelfTests.Run();File.WriteAllText(Path.Combine(root,"cloud-test-result.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{passed=true,count=count,production_cloud_tested=false}));return 0; }
                if(args.Contains("--test-ui")) { UiLayoutTests.Run(points,root);return 0; }
                if(args.Contains("--test-workflow")) { WorkflowTests.Run(points,root);return 0; }
                using(var form=new DashboardForm(points,root)) {
                    var history=new HistoryPage(points,root,form.Engine);form.HistoryHost.Controls.Add(history);form.AttachHistoryPage(history);
                    form.AttachCloudPage(new CloudPage(root,form.Engine));
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
    public sealed class MonitorForm : Form {
        readonly List<Point> points;
        readonly string root;
        readonly DataTable table=new DataTable();
        readonly DataGridView grid=new DataGridView();
        readonly CheckBox simulation=new CheckBox { Text="模拟设备",Checked=true,AutoSize=true };
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



