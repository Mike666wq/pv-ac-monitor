using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using DrawingPoint=System.Drawing.Point;

namespace ExperimentMonitor {
    /// <summary>Main application shell. Acquisition and connection state are owned by MonitorEngine.</summary>
    public sealed class DashboardForm : Form {
        readonly List<Point> points;
        readonly string appRoot;
        readonly string settingsPath;
        public sealed class SavedSettings { public bool Simulation=true; public string Port=""; public int Period=2,Timeout=8; }
        public MonitorEngine Engine { get; private set; }
        public Panel HistoryHost { get; private set; }
        public Panel CloudHost { get; private set; }
        public Panel SettingsHost { get; private set; }
        internal TabControl MainNavigation { get; private set; }

        readonly ComboBox port=new ComboBox { Width=100,DropDownStyle=ComboBoxStyle.DropDownList };
        readonly CheckBox simulation=new CheckBox { Text="模拟数据",Checked=true,AutoSize=true };
        readonly NumericUpDown period=new NumericUpDown { Minimum=1,Maximum=3600,Value=2,Width=72 };
        readonly NumericUpDown timeout=new NumericUpDown { Minimum=1,Maximum=30,Value=8,Width=66 };
        readonly StyledActionButton refresh=new StyledActionButton { Text="刷新",Width=68 };
        readonly StyledActionButton start=new StyledActionButton { Text="开始采集",Width=112,BackColor=UiTheme.Blue,IconGlyph="▶" };
        readonly StyledActionButton once=new StyledActionButton { Text="采集一轮",Width=96 };
        readonly StyledActionButton stop=new StyledActionButton { Text="停止",Width=80 };
        readonly StyledActionButton headerAction=new StyledActionButton { Text="连接",Width=104,BackColor=UiTheme.Blue };
        Label headerTitle;
        readonly Label connectionState=StatusLabel("未连接"),captureState=StatusLabel("未采集"),recordState=StatusLabel("本地记录待机"),cloudState=StatusLabel("云端未启用");
        readonly Label counts=new Label { AutoSize=true,Text="点表已载入 · 4 台设备 / 37 个测点" };
        readonly TextBox log=new TextBox { Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",9) };
        readonly DataTable table=new DataTable();
        readonly Dictionary<string,DataRow> rows=new Dictionary<string,DataRow>();
        readonly Dictionary<string,Label[]> cards=new Dictionary<string,Label[]>();
        readonly Dictionary<string,DateTime> lastGood=new Dictionary<string,DateTime>();
        string countedSession;
        readonly ExperimentChart overviewChart=new ExperimentChart { Dock=DockStyle.Fill };
        readonly System.Windows.Forms.Timer freshness=new System.Windows.Forms.Timer { Interval=1000 };
        readonly ToolTip tips=new ToolTip { AutoPopDelay=12000,InitialDelay=350,ReshowDelay=100,ShowAlways=true };
        HistoryPage historyPage;
        CloudPage cloudPage; int good,bad,diagnosticRequests,diagnosticGood,diagnosticBad; bool closed; string displayedSource,shownStoreError;

        public DashboardForm(List<Point> items,string root) {
            if(items==null)throw new ArgumentNullException("items");points=items;
            appRoot=Path.GetFullPath(root);settingsPath=Path.Combine(appRoot,"settings.json");Engine=new MonitorEngine(points,appRoot);
            AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
            Text="实验监控 · 全实验数据工作台";ClientSize=new Size(1440,900);MinimumSize=new Size(960,640);Font=new Font("Microsoft YaHei UI",9);BackColor=UiTheme.Canvas;StartPosition=FormStartPosition.CenterScreen;
            BuildShell();BuildPages();WireEngine();
            headerAction.Click+=(s,e)=>{if(Engine.IsConnected){try{Engine.Disconnect();}catch(Exception ex){ShowError("断开连接失败",ex);}}else ConnectCaptureSource();};
            refresh.Click+=(s,e)=>RefreshPorts();
            start.Click+=(s,e)=>StartCapture(false);
            once.Click+=(s,e)=>StartCapture(true);
            stop.Click+=(s,e)=>StopCapture();
            period.ValueChanged+=(s,e)=>{if(Engine.IsConnected)try{Engine.SetPeriodSeconds((int)period.Value);}catch(Exception ex){AppendLog("采集周期更新失败："+ex.Message);}};
            freshness.Tick+=(s,e)=>RefreshFreshness();freshness.Start();
            FormClosing+=(s,e)=>{SaveSettings();try{if(Engine.IsRunning)Engine.Stop();Engine.Disconnect();closed=true;freshness.Stop();}catch(Exception ex){e.Cancel=true;MessageBox.Show(this,"正在等待采集线程和本地记录安全关闭，请稍后重试。\r\n"+ex.Message,"关闭程序",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
            FormClosed+=(s,e)=>{freshness.Dispose();tips.Dispose();Engine.Dispose();};
            RefreshPorts();LoadSettings();UiTheme.Apply(this);if(headerTitle!=null)headerTitle.ForeColor=Color.White;RefreshControls();
        }

        void BuildShell() {
            var shell=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=Padding.Empty,BackColor=UiTheme.Canvas };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,30));Controls.Add(shell);
            // 深蓝标题栏(对齐 BMS):左侧产品名,右侧 连接/断开 主按钮。
            var header=new Panel { Dock=DockStyle.Fill,Height=56,BackColor=UiTheme.Ink,Padding=new Padding(16,10,12,10) };
            headerTitle=new Label { Text="实验监控  /  全实验数据工作台",AutoSize=true,Location=new DrawingPoint(16,12),Font=new Font("Microsoft YaHei UI",13,FontStyle.Bold),ForeColor=Color.White,BackColor=Color.Transparent };
            header.Controls.Add(headerTitle);
            headerAction.Dock=DockStyle.Right;headerAction.Margin=new Padding(0,0,4,0);header.Controls.Add(headerAction);
            shell.Controls.Add(header,0,0);
            // 单行紧凑工具栏:连接/断开已移入标题栏;窗口过窄时允许折行。
            var toolbar=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,AutoScroll=false,Margin=Padding.Empty,BackColor=Color.FromArgb(247,250,253),Padding=new Padding(10,6,10,6) };
            simulation.Margin=new Padding(4,10,8,0);
            toolbar.Controls.Add(simulation);toolbar.Controls.Add(TextLabel("串口"));
            toolbar.Controls.Add(new RoundedInputHost(port) { Width=112 });toolbar.Controls.Add(refresh);toolbar.Controls.Add(TextLabel("9600 / 8N1"));
            toolbar.Controls.Add(TextLabel("周期(s)"));toolbar.Controls.Add(new RoundedInputHost(period) { Width=82 });
            toolbar.Controls.Add(TextLabel("超时(s)"));toolbar.Controls.Add(new RoundedInputHost(timeout) { Width=76 });
            toolbar.Controls.Add(start);toolbar.Controls.Add(once);toolbar.Controls.Add(stop);
            shell.Controls.Add(toolbar,0,1);
            MainNavigation=new TabControl { Dock=DockStyle.Fill,DrawMode=TabDrawMode.OwnerDrawFixed,ItemSize=new Size(120,34),SizeMode=TabSizeMode.Fixed,Padding=new DrawingPoint(12,4) };
            MainNavigation.DrawItem+=(s,e)=>{bool selected=(e.State&DrawItemState.Selected)!=0;using(var b=new SolidBrush(selected?Color.White:Color.FromArgb(247,250,253)))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,MainNavigation.TabPages[e.Index].Text,MainNavigation.Font,e.Bounds,selected?UiTheme.Blue:UiTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);if(selected)using(var p=new Pen(UiTheme.Blue,3))e.Graphics.DrawLine(p,e.Bounds.Left+17,e.Bounds.Bottom-2,e.Bounds.Right-17,e.Bounds.Bottom-2);};
            shell.Controls.Add(MainNavigation,0,2);
            // 底部状态栏:状态灯与点表/采集统计合并为一行。
            var statusBar=new Panel { Dock=DockStyle.Fill,BackColor=Color.FromArgb(229,237,246),Padding=new Padding(10,0,10,0) };
            var states=new FlowLayoutPanel { Dock=DockStyle.Left,AutoSize=true,WrapContents=false,AutoScroll=false,Margin=Padding.Empty,Padding=new Padding(0,2,0,0),BackColor=Color.Transparent };
            states.Controls.Add(connectionState);states.Controls.Add(captureState);states.Controls.Add(recordState);states.Controls.Add(cloudState);
            counts.AutoSize=true;counts.Dock=DockStyle.Right;counts.TextAlign=ContentAlignment.MiddleLeft;counts.Padding=new Padding(8,6,4,0);counts.Margin=Padding.Empty;counts.ForeColor=UiTheme.Muted;counts.BackColor=Color.Transparent;
            statusBar.Controls.Add(states);statusBar.Controls.Add(counts);shell.Controls.Add(statusBar,0,3);
        }

        void BuildPages() {
            TabPage live=AddPage("实时监控"),trends=AddPage("趋势分析"),records=AddPage("数据记录"),cloud=AddPage("云端连接"),diag=AddPage("通信诊断");
            var liveTabs=new TabControl { Dock=DockStyle.Fill,DrawMode=TabDrawMode.OwnerDrawFixed,ItemSize=new Size(104,30),SizeMode=TabSizeMode.Fixed };
            liveTabs.DrawItem+=(s,e)=>{bool selected=(e.State&DrawItemState.Selected)!=0;TextRenderer.DrawText(e.Graphics,liveTabs.TabPages[e.Index].Text,Font,e.Bounds,selected?UiTheme.Blue:UiTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);if(selected)using(var p=new Pen(UiTheme.Blue,2))e.Graphics.DrawLine(p,e.Bounds.Left+18,e.Bounds.Bottom-1,e.Bounds.Right-18,e.Bounds.Bottom-1);};live.Controls.Add(liveTabs);
            var overview=new TabPage("总览") { BackColor=UiTheme.Canvas,Padding=new Padding(7) };var detail=new TabPage("测点详情") { BackColor=UiTheme.Canvas,Padding=new Padding(7) };liveTabs.TabPages.Add(overview);liveTabs.TabPages.Add(detail);
            BuildOverview(overview);BuildDetails(detail);
            var historyHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(2) };records.Controls.Add(historyHost);HistoryHost=historyHost;
            var cloudHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(2) };cloud.Controls.Add(cloudHost);CloudHost=cloudHost;
            SettingsHost=null;
            new TrendsPage(points,Engine,appRoot).AttachTo(trends);
            BuildDiagnostics(diag);
            MainNavigation.SelectedIndexChanged+=(s,e)=>{if(MainNavigation.SelectedTab==records&&historyPage!=null)historyPage.EnterPage(Engine.ActiveSource);};
        }
        TabPage AddPage(string name){var p=new TabPage(name){BackColor=UiTheme.Canvas,Padding=new Padding(7)};MainNavigation.TabPages.Add(p);return p;}

        void BuildOverview(TabPage page) { OverviewPageBuilder.Build(page,points,cards,tips,overviewChart,Font); }

        void BuildDetails(TabPage page) {
            foreach(string col in new[]{"测点","名称","设备","站号","零基地址","单位","实时值","质量","采样时间","耗时(ms)"})table.Columns.Add(col);
            foreach(Point p in points){PointInfo i=PointCatalog.Get(p);DataRow r=table.Rows.Add(p.name,i.Label,p.binding.device,p.binding.slave,p.binding.address_zero_based,i.Unit,"—","尚未采集","","");rows[p.name]=r;}
            var grid=new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoGenerateColumns=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,ScrollBars=ScrollBars.Both };
            string[] columns={"测点","名称","设备","站号","零基地址","单位","实时值","质量","采样时间","耗时(ms)"};float[] weights={55,110,90,45,75,90,145,105,155,65};for(int i=0;i<columns.Length;i++)grid.Columns.Add(new DataGridViewTextBoxColumn{Name="col"+i,HeaderText=columns[i],DataPropertyName=columns[i],FillWeight=weights[i],SortMode=DataGridViewColumnSortMode.NotSortable});grid.DataSource=table;
            grid.RowTemplate.Height=28;
            // 质量列彩色徽章:正常绿 / 过期·旧值黄 / 超时·异常红,其余灰。
            grid.CellFormatting+=(s,e)=>{
                if(e.ColumnIndex<0||e.ColumnIndex>=grid.Columns.Count||grid.Columns[e.ColumnIndex].DataPropertyName!="质量"||e.Value==null)return;
                string q=e.Value.ToString();Color fore=UiTheme.Muted,back=UiTheme.Surface;
                if(q.StartsWith("正常")){fore=UiTheme.Green;back=Color.FromArgb(226,244,238);}
                else if(q.StartsWith("过期")||q.Contains("旧值")){fore=UiTheme.Warning;back=Color.FromArgb(252,241,222);}
                else if(q.Contains("超时")||q.Contains("异常")||q.Contains("错误")||q.Contains("失败")){fore=UiTheme.Danger;back=Color.FromArgb(251,231,232);}
                e.CellStyle.ForeColor=fore;e.CellStyle.BackColor=back;e.CellStyle.SelectionBackColor=back;e.CellStyle.SelectionForeColor=fore;
            };
            page.Controls.Add(grid);
        }

        void BuildDiagnostics(TabPage page) {
            var shell=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(4) };shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,110));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));page.Controls.Add(shell);
            var box=new GroupBox { Text="只读 Modbus 03 手动读取",Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(8),BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink };
            // Let the controls wrap into additional rows at narrow widths. A fixed-height
            // diagnostic strip clipped the parser and send buttons on 960px windows.
            var controls=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,AutoScroll=false,Padding=new Padding(4) };
            NumericUpDown slave=Number(1,247,64),address=Number(0,65535,92),count=Number(1,8,64),diagTimeout=Number(1,30,64);
            ComboBox mode=new ComboBox { Width=145,DropDownStyle=ComboBoxStyle.DropDownList };mode.Items.AddRange(new object[]{"RAW 原始寄存器","UINT16","INT16","FLOAT ABCD","FLOAT CDAB"});mode.SelectedIndex=0;
            var read=new StyledActionButton { Text="发送读取",Width=128,BackColor=UiTheme.Blue };
            var clear=new StyledActionButton { Text="清空日志",Width=100 };
            CheckBox raw=new CheckBox { Text="记录原始收发日志",AutoSize=true,Checked=true,Margin=new Padding(6,10,3,0) };
            Label counters=new Label { AutoSize=true,Text="读取 0 次 · 成功 0 · 异常 0",Padding=new Padding(3,10,3,0),ForeColor=UiTheme.Muted };
            controls.Controls.Add(TextLabel("站号"));controls.Controls.Add(new RoundedInputHost(slave) { Width=76 });controls.Controls.Add(TextLabel("零基地址"));controls.Controls.Add(new RoundedInputHost(address) { Width=104 });controls.Controls.Add(TextLabel("寄存器数"));controls.Controls.Add(new RoundedInputHost(count) { Width=76 });controls.Controls.Add(TextLabel("超时(s)"));controls.Controls.Add(new RoundedInputHost(diagTimeout) { Width=76 });controls.Controls.Add(TextLabel("解析方式"));controls.Controls.Add(new RoundedInputHost(mode) { Width=160 });controls.Controls.Add(read);controls.Controls.Add(clear);controls.Controls.Add(raw);controls.Controls.Add(counters);box.Controls.Add(controls);box.AutoSize=false;shell.Controls.Add(box,0,0);
            bool sizingDiagnostics=false;
            Action fitDiagnostics=()=>{
                if(sizingDiagnostics||controls.ClientSize.Width<=0)return;
                sizingDiagnostics=true;
                try{
                    int height=controls.GetPreferredSize(new Size(controls.ClientSize.Width,0)).Height;
                    int required=controls.Top+height+box.Padding.Bottom;
                    if(box.Height!=required)box.Height=required;
                }finally{sizingDiagnostics=false;}
            };
            controls.SizeChanged+=(s,e)=>fitDiagnostics();
            controls.Layout+=(s,e)=>fitDiagnostics();
            fitDiagnostics();
            Label result=new Label { Dock=DockStyle.Fill,Text="尚未发送诊断读取，结果会显示在这里。",Padding=new Padding(9),ForeColor=UiTheme.Muted,AutoEllipsis=true };
            var resultBox=new GroupBox { Text="最近一次读取",Dock=DockStyle.Fill,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Padding=new Padding(6) };resultBox.Controls.Add(result);shell.Controls.Add(resultBox,0,1);
            // 提示语只留一处:作为通信日志的初始内容。
            log.Text="诊断只读且不写入正式历史记录。请先连接，再停止周期采集后发送读取。\r\n";
            var logBox=new GroupBox { Text="通信日志",Dock=DockStyle.Fill,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Padding=new Padding(6) };logBox.Controls.Add(log);shell.Controls.Add(logBox,0,2);
            raw.CheckedChanged+=(s,e)=>Engine.RecordRawLog=raw.Checked;clear.Click+=(s,e)=>{log.Clear();diagnosticRequests=diagnosticGood=diagnosticBad=0;counters.Text="读取 0 次 · 成功 0 · 异常 0";result.Text="日志已清空；正式历史记录与本地数据库不会受影响。";};
            read.Click+=async(s,e)=>{
                if(!Engine.IsConnected){MessageBox.Show(this,"请先连接数据源。","通信诊断",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                if(Engine.IsRunning){MessageBox.Show(this,"请先停止周期采集，再执行手动读取。","通信诊断",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                read.Enabled=false;string selectedMode=((string)mode.SelectedItem).Split(' ')[0];if(selectedMode=="RAW")selectedMode="RAW";
                result.Text="读取中…";
                diagnosticRequests++;
                try {DiagnosticResult r=await Engine.ReadDiagnosticAsync((int)slave.Value,(int)address.Value,(int)count.Value,selectedMode,(int)diagTimeout.Value);string status=r.Success?"正常":r.Status;if(r.Success)diagnosticGood++;else diagnosticBad++;counters.Text="读取 "+diagnosticRequests+" 次 · 成功 "+diagnosticGood+" · 异常 "+diagnosticBad;result.Text="最近收发时间："+r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")+"\r\n站号："+r.Slave+"　地址："+r.Address+"　数量："+r.Count+"　解析："+selectedMode+"\r\n结果："+status+" · "+r.Value+"\r\nTX："+r.TxHex+"\r\nRX："+r.RxHex+(String.IsNullOrEmpty(r.PayloadHex)?"":"\r\n数据："+r.PayloadHex)+"\r\n耗时："+r.Milliseconds+" ms";AppendLog("诊断读取 "+status+" · "+r.TxHex+" → "+r.RxHex);}
                catch(Exception ex){diagnosticBad++;counters.Text="读取 "+diagnosticRequests+" 次 · 成功 "+diagnosticGood+" · 异常 "+diagnosticBad;result.Text="读取失败："+ex.Message;AppendLog("诊断读取失败："+ex.Message);}
                finally{read.Enabled=true;}
            };
        }
        public void AttachHistoryPage(HistoryPage page){if(page==null)throw new ArgumentNullException("page");historyPage=page;HistoryHost.Controls.Clear();page.Dock=DockStyle.Fill;HistoryHost.Controls.Add(page);UiTheme.Apply(page);page.SetActiveSource(Engine.ActiveSource??"simulation");}
        public void AttachCloudPage(CloudPage page){if(page==null)throw new ArgumentNullException("page");if(cloudPage!=null)cloudPage.CloudStatusChanged-=OnCloudStatus;cloudPage=page;cloudPage.CloudStatusChanged+=OnCloudStatus;CloudHost.Controls.Clear();page.Dock=DockStyle.Fill;CloudHost.Controls.Add(page);UiTheme.Apply(page);OnCloudStatus(page.CurrentStatusLabel);}
        void OnCloudStatus(string value){Dispatch(()=>{
            CloudPublisherStatus snapshot=cloudPage==null?null:cloudPage.CurrentStatus;
            string state=snapshot==null?(value??""):snapshot.State;
            bool disabled=String.IsNullOrWhiteSpace(state)||state.Contains("未启用");
            cloudState.Text=disabled?"○ 云端未启用":"云端 · "+value;
            cloudState.ForeColor=disabled?UiTheme.Muted:state.Contains("在线")?UiTheme.Green:(state.Contains("失败")||state.Contains("错误")||state.Contains("认证")||state.Contains("TLS"))?UiTheme.Danger:UiTheme.Muted;
        });}
        public void SetCloudStatus(string value){Dispatch(()=>cloudState.Text=String.IsNullOrWhiteSpace(value)?"云端未启用":"云端 · "+value);}
        public void CapturePreview(string path){ShowInTaskbar=false;Opacity=0;Show();Application.DoEvents();using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(path);}Hide();Opacity=1;}
        public void CapturePreview(string path,int width,int height){ClientSize=new Size(width,height);CapturePreview(path);}

        void WireEngine(){
            Engine.ObservationReceived+=o=>Dispatch(()=>UpdateObservation(o));
            Engine.StatusChanged+=s=>Dispatch(()=>{captureState.Text=s;captureState.ForeColor=s.IndexOf("停止",StringComparison.OrdinalIgnoreCase)>=0?UiTheme.Muted:UiTheme.Blue;RefreshControls();});
            Engine.LogReceived+=s=>Dispatch(()=>AppendLog(s));
            Engine.ConnectionChanged+=(connected,source)=>Dispatch(()=>SetConnection(connected,source));
            Engine.PolicyChanged+=p=>Dispatch(()=>AppendLog("策略更新 · "+p.Type+" · "+p.Detail));
        }
        void ResetDisplay(){good=bad=0;lastGood.Clear();overviewChart.Clear();foreach(Label[] c in cards.Values){c[1].Text="—";c[3].Text="待机";c[3].ForeColor=UiTheme.Muted;}foreach(DataRow r in table.Rows){r["实时值"]="—";r["质量"]="尚未采集";r["采样时间"]="";r["耗时(ms)"]="";}counts.Text="已切换数据来源 · 等待首轮采样";}
        void Dispatch(Action action){if(closed||IsDisposed)return;try{if(IsHandleCreated)BeginInvoke(action);}catch(InvalidOperationException){} }
        void RefreshControls(){bool connected=Engine.IsConnected,running=Engine.IsRunning;if(!running&&String.IsNullOrEmpty(Engine.LastStoreError)){recordState.Text=Engine.CompletedCaptures>0?"○ 本地记录已停止":"○ 本地记录待机";recordState.ForeColor=UiTheme.Muted;}start.Enabled=once.Enabled=connected&&!running;stop.Enabled=running;headerAction.Text=connected?"断开连接":"连接";headerAction.Enabled=connected||!running;simulation.Enabled=port.Enabled=!connected&&!running;refresh.Enabled=!connected&&!running;timeout.Enabled=!running;period.Enabled=true;connectionState.Text=connected?(Engine.ConnectedSimulation?"● 模拟已连接":"● 串口已连接 · "+Engine.ConnectedPort):"○ 未连接";connectionState.ForeColor=connected?UiTheme.Green:UiTheme.Muted;captureState.Text=running?"● 正在采集":"○ 已停止";captureState.ForeColor=running?UiTheme.Green:UiTheme.Muted;}
        void ConnectCaptureSource(){try{if(!simulation.Checked&&String.IsNullOrWhiteSpace(port.Text))throw new InvalidOperationException("没有可用串口。请接入 USB-RS485 转换器后点击刷新。");Engine.Connect(simulation.Checked,port.Text,9600);SaveSettings();RefreshControls();}catch(Exception ex){MessageBox.Show(this,"连接失败："+ex.Message,"连接",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        void SetConnection(bool connected,string source){string key=source??Engine.ActiveSource;if(connected){if(displayedSource!=null&&key!=displayedSource)ResetDisplay();displayedSource=key;connectionState.Text=key=="simulation"?"● 模拟设备已连接":"● 串口已连接 · "+Engine.ConnectedPort+" · 9600 8N1";connectionState.ForeColor=UiTheme.Green;AppendLog("连接状态："+connectionState.Text);if(historyPage!=null)historyPage.SetActiveSource(key);}else{connectionState.Text="○ 未连接";connectionState.ForeColor=UiTheme.Muted;MarkLastValuesStale("连接已断开");AppendLog("连接已断开 · 最后采样保留");}RefreshControls();}
        void MarkLastValuesStale(string label){foreach(var p in lastGood){Label[] c;if(cards.TryGetValue(p.Key,out c)){c[3].Text=label+" · "+p.Value.ToLocalTime().ToString("HH:mm:ss");c[3].ForeColor=UiTheme.Warning;}DataRow r;if(rows.TryGetValue(p.Key,out r)&&r["质量"].ToString()=="正常")r["质量"]="过期 · "+label;}}
        void StartCapture(bool single){try{if(!Engine.IsConnected)throw new InvalidOperationException("请先连接数据源");SaveSettings();shownStoreError=null;good=bad=0;Engine.SetPeriodSeconds((int)period.Value);if(single)Engine.StartOnce((int)period.Value,(int)timeout.Value,points.Select(p=>p.name));else Engine.Start((int)period.Value,(int)timeout.Value,points.Select(p=>p.name));RefreshControls();captureState.Text=single?"● 正在采集一轮":"● 正在采集";}catch(Exception ex){ShowError(single?"单轮采集失败":"开始采集失败",ex);}}
        void StopCapture(){try{Engine.Stop();captureState.Text="○ 已停止 · 连接保持 · 本地记录已排空";MarkLastValuesStale("采集已停止");RefreshControls();}catch(Exception ex){ShowError("停止采集失败",ex);}}
        void RefreshFreshness(){RefreshControls();string fatal=Engine.LastStoreError;if(!String.IsNullOrWhiteSpace(fatal)){recordState.Text="! 本地记录失败 · "+fatal;recordState.ForeColor=UiTheme.Danger;if(shownStoreError!=fatal){shownStoreError=fatal;AppendLog("本地记录失败："+fatal);MessageBox.Show(this,"本地记录出现错误，采集已停止或正在停止。请检查数据目录和磁盘空间。\r\n\r\n"+fatal,"本地记录失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}}else if(Engine.Store!=null){var s=Engine.Store.Status;if(s!=null){recordState.Text=s.Recording?"● 本地记录正常 · "+s.Committed.ToString("N0")+" 条":"本地记录已停止";if(!String.IsNullOrEmpty(s.LastError))recordState.Text="! 本地记录错误 · "+s.LastError;recordState.ForeColor=String.IsNullOrEmpty(s.LastError)?(s.Recording?UiTheme.Green:UiTheme.Muted):UiTheme.Danger;}}else recordState.Text=Engine.CompletedCaptures>0?"○ 本地记录已停止":"○ 本地记录待机";
            foreach(var pair in lastGood)if((DateTime.UtcNow-pair.Value).TotalSeconds>Math.Max(10,(double)period.Value*3)){Label[] c;if(cards.TryGetValue(pair.Key,out c)&&!c[3].Text.Contains("旧值")&&!c[3].Text.Contains("采集已停止")&&!c[3].Text.Contains("连接已断开")){c[3].Text="旧值 · "+pair.Value.ToLocalTime().ToString("HH:mm:ss");c[3].ForeColor=UiTheme.Warning;}DataRow r;if(rows.TryGetValue(pair.Key,out r)&&r["质量"].ToString()=="正常")r["质量"]="过期";}}
        public void UpdateObservation(Observation o){if(o==null)return;if(!String.IsNullOrEmpty(o.SessionId)&&countedSession!=o.SessionId){countedSession=o.SessionId;good=bad=0;}bool valid=o.Quality=="good";if(valid)good++;else bad++;DateTime previous;bool hasOld=lastGood.TryGetValue(o.Point,out previous);
            DataRow r;if(rows.TryGetValue(o.Point,out r)){if(valid){r["实时值"]=DisplayNumber(o);r["采样时间"]=o.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture);}r["质量"]=valid?"正常":QualityLabel(o.Quality)+(hasOld?" · 保留旧值":" · 尚无有效值");r["耗时(ms)"]=o.Milliseconds.ToString(CultureInfo.InvariantCulture);}
            Label[] c;if(cards.TryGetValue(o.Point,out c)){if(valid){c[1].Text=DisplayCardValue(o);lastGood[o.Point]=o.Utc;}DateTime timestamp=valid?o.Utc:previous;c[3].Text=valid?"正常 · "+timestamp.ToLocalTime().ToString("HH:mm:ss"):QualityLabel(o.Quality)+(hasOld?" · 旧值 "+timestamp.ToLocalTime().ToString("HH:mm:ss"):" · 尚无有效值");c[3].ForeColor=valid?UiTheme.Green:UiTheme.Danger;tips.SetToolTip(c[1],valid?"实际值："+(o.Value??DisplayNumber(o))+"\r\n采样时间："+o.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"):o.Value);}
            overviewChart.Add(o);counts.Text="本次采集累计成功 "+good+" / 异常 "+bad+"　·　"+(o.Source=="serial"?"实机":"模拟")+"　·　最新测点 "+o.Point+"　·　本地记录按实际采样时间保存";
        }
        static string DisplayNumber(Observation o){if(o.Number.HasValue){double value=o.Number.Value;if(value==0)return "0";string s=value.ToString("G6",CultureInfo.InvariantCulture);if(s=="0"||s=="-0")s=value.ToString("G9",CultureInfo.InvariantCulture);return s;}return o.Value??"—";}
        static string DisplayCardValue(Observation o){if(o.Mode=="U16 / I16"&&o.Value!=null){int split=o.Value.IndexOf(" / I16=",StringComparison.Ordinal);if(split>0){string a=o.Value.Substring(0,split),b=o.Value.Substring(split+3);int annotation=b.IndexOf('（');if(annotation>=0)b=b.Substring(0,annotation);return a+Environment.NewLine+b;}}return DisplayNumber(o);}
        static string QualityLabel(string q){if(q=="timeout")return "超时";if(q=="protocol_exception")return "协议异常";if(q=="decode_error")return "解码异常";if(q=="error")return "通信异常";return String.IsNullOrEmpty(q)?"质量未知":q;}
        void AppendLog(string value){if(log.TextLength>300000)log.Text=log.Text.Substring(log.TextLength-150000);log.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture)+"  "+value+Environment.NewLine);}
        void LoadSettings(){try{if(!File.Exists(settingsPath))return;SavedSettings s=new JavaScriptSerializer().Deserialize<SavedSettings>(File.ReadAllText(settingsPath));if(s==null)return;simulation.Checked=s.Simulation;period.Value=Math.Max(period.Minimum,Math.Min(period.Maximum,s.Period));timeout.Value=Math.Max(timeout.Minimum,Math.Min(timeout.Maximum,s.Timeout));if(!String.IsNullOrEmpty(s.Port)){if(!port.Items.Contains(s.Port))port.Items.Add(s.Port);port.SelectedItem=s.Port;}}catch(Exception ex){AppendLog("设置读取失败，使用默认值："+ex.Message);}}
        void SaveSettings(){try{string dir=Path.GetDirectoryName(settingsPath);Directory.CreateDirectory(dir);File.WriteAllText(settingsPath,new JavaScriptSerializer().Serialize(new SavedSettings{Simulation=simulation.Checked,Port=port.Text,Period=(int)period.Value,Timeout=(int)timeout.Value}),new System.Text.UTF8Encoding(false));}catch(Exception ex){AppendLog("设置保存失败："+ex.Message);}}
        void RefreshPorts(){string old=port.Text;port.Items.Clear();port.Items.AddRange(SerialPort.GetPortNames().OrderBy(x=>x.Length).ThenBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray());if(port.Items.Contains(old))port.SelectedItem=old;else if(port.Items.Count>0)port.SelectedIndex=0;}
        void ShowError(string title,Exception ex){MessageBox.Show(this,ex.Message,title,MessageBoxButtons.OK,MessageBoxIcon.Error);}
        static Label TextLabel(string value){return new Label{Text=value,AutoSize=true,Padding=new Padding(2,6,2,0),ForeColor=UiTheme.Muted};}
        static Label StatusLabel(string value){return new Label{Text=value,AutoSize=true,Padding=new Padding(0,4,16,0),ForeColor=UiTheme.Muted,MaximumSize=new Size(480,0)};}
        static NumericUpDown Number(int min,int max,int width){return new NumericUpDown{Minimum=min,Maximum=max,Width=width};}
    }
}
