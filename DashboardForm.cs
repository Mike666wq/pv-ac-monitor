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
        readonly CheckBox simulation=new CheckBox { Text="模拟数据",Checked=false,AutoSize=true };
        readonly NumericUpDown period=new NumericUpDown { Minimum=1,Maximum=3600,Value=2,Width=72 };
        readonly NumericUpDown timeout=new NumericUpDown { Minimum=1,Maximum=30,Value=8,Width=66 };
        readonly StyledActionButton refresh=new StyledActionButton { Text="刷新",Width=68 };
        readonly StyledActionButton start=new StyledActionButton { Text="开始采集",Width=112,BackColor=UiTheme.Blue,IconGlyph="▶" };
        readonly StyledActionButton once=new StyledActionButton { Text="采集一轮",Width=96 };
        readonly StyledActionButton stop=new StyledActionButton { Text="停止",Width=80 };
        readonly StyledActionButton headerAction=new StyledActionButton { Text="连接",Width=104,BackColor=UiTheme.Blue };
        Label headerTitle;
        readonly TextBox searchBox=new TextBox{Width=150};readonly Button searchButton=new Button{Text="查找",AutoSize=true};
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
        HistoryPage historyPage;TrendsPage trendsPage;        CloudPage cloudPage; int good,bad,diagnosticRequests,diagnosticGood,diagnosticBad; bool closed; string displayedSource,shownStoreError;

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
            // 点表启动校验的结果必须让人看见，不能只写进 JSON 文件。
            // 没有横幅的"静默拒绝"会让人以为程序坏了，而不是以为点表漏登记了。
            if(!String.IsNullOrEmpty(PointCatalogValidation.StartupNotice)){AppendLog("点表校验 · "+PointCatalogValidation.StartupNotice);counts.Text="! 点表校验 · "+PointCatalogValidation.StartupNotice;counts.ForeColor=UiTheme.Warning;}
        }

        void BuildShell() {
            var shell=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=Padding.Empty,BackColor=UiTheme.Canvas };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,30));Controls.Add(shell);
            // 深蓝标题栏(对齐 BMS):左侧产品名,右侧 连接/断开 主按钮。
            var header=new Panel { Dock=DockStyle.Fill,Height=56,BackColor=UiTheme.Ink,Padding=new Padding(16,10,12,10) };
            headerTitle=new Label { Text="实验监控  /  全实验数据工作台",AutoSize=true,Location=new DrawingPoint(16,12),Font=new Font("Microsoft YaHei UI",13,FontStyle.Bold),ForeColor=Color.White,BackColor=Color.Transparent };
            header.Controls.Add(headerTitle);
            searchButton.Click+=(s,e)=>{AppendLog("查找 · "+SearchNext());searchBox.Focus();searchBox.SelectAll();};
            headerAction.Dock=DockStyle.Right;headerAction.Margin=new Padding(0,0,4,0);header.Controls.Add(headerAction);
            // 37 个点靠肉眼在卡片墙上找，等于训练使用者背位置。搜索放在标题栏（工具栏已经满了，
            // 挤到第二行会吃掉纵向空间）。四路匹配：点名 / 名称 / 力控原名(alias) / 地址。
            var searchHost=new FlowLayoutPanel { Dock=DockStyle.Right,AutoSize=true,WrapContents=false,AutoScroll=false,BackColor=Color.Transparent,Margin=Padding.Empty,Padding=new Padding(0,12,10,0) };
            var searchCaption=new Label { Text="查找",AutoSize=true,ForeColor=Color.FromArgb(198,214,232),BackColor=Color.Transparent,Padding=new Padding(0,6,6,0),Margin=Padding.Empty };
            searchHost.Controls.Add(searchCaption);searchHost.Controls.Add(new RoundedInputHost(searchBox) { Width=150,Margin=new Padding(0,3,6,0) });searchHost.Controls.Add(searchButton);
            header.Controls.Add(searchHost);
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
            // 四个圆点语义不同，不能连排：前两个是"现在"（链路、采集），后两个是"沉淀"（记录、云端）。
            // 四个一样颜色的圆点等于告诉使用者"这四件事一样重要、一样可靠"。
            var divider=new Label { Text="│",AutoSize=true,ForeColor=Color.FromArgb(180,196,214),Padding=new Padding(4,4,4,0),Margin=Padding.Empty,BackColor=Color.Transparent };
            states.Controls.Add(connectionState);states.Controls.Add(captureState);states.Controls.Add(divider);states.Controls.Add(recordState);states.Controls.Add(cloudState);
            counts.AutoSize=true;counts.AutoEllipsis=true;counts.MaximumSize=new Size(420,0);counts.Dock=DockStyle.Right;counts.TextAlign=ContentAlignment.MiddleLeft;counts.Padding=new Padding(8,6,4,0);counts.Margin=Padding.Empty;counts.ForeColor=UiTheme.Muted;counts.BackColor=Color.Transparent;
            statusBar.Controls.Add(states);statusBar.Controls.Add(counts);shell.Controls.Add(statusBar,0,3);
        }

        void BuildPages() {
            TabPage live=AddPage("实时监控"),control=AddPage("运行控制"),trends=AddPage("趋势分析"),records=AddPage("数据记录"),cloud=AddPage("云端连接"),diag=AddPage("通信诊断");
            var liveTabs=new TabControl { Dock=DockStyle.Fill,DrawMode=TabDrawMode.OwnerDrawFixed,ItemSize=new Size(104,30),SizeMode=TabSizeMode.Fixed };
            liveTabs.DrawItem+=(s,e)=>{bool selected=(e.State&DrawItemState.Selected)!=0;TextRenderer.DrawText(e.Graphics,liveTabs.TabPages[e.Index].Text,Font,e.Bounds,selected?UiTheme.Blue:UiTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);if(selected)using(var p=new Pen(UiTheme.Blue,2))e.Graphics.DrawLine(p,e.Bounds.Left+18,e.Bounds.Bottom-1,e.Bounds.Right-18,e.Bounds.Bottom-1);};live.Controls.Add(liveTabs);
            var overview=new TabPage("总览") { BackColor=UiTheme.Canvas,Padding=new Padding(7) };var detail=new TabPage("测点详情") { BackColor=UiTheme.Canvas,Padding=new Padding(7) };liveTabs.TabPages.Add(overview);liveTabs.TabPages.Add(detail);
            BuildOverview(overview);BuildDetails(detail);
            control.Controls.Add(new ControlPage(points,Engine,appRoot));
            var historyHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(2) };records.Controls.Add(historyHost);HistoryHost=historyHost;
            var cloudHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(2) };cloud.Controls.Add(cloudHost);CloudHost=cloudHost;
            SettingsHost=null;
            trendsPage=new TrendsPage(points,Engine,appRoot);trendsPage.AttachTo(trends);
            // 趋势页看到某一段 → 带着可见区间跳回记录页看原始值。
            trendsPage.OpenRecordsRequested+=(selection)=>{MainNavigation.SelectedIndex=3;if(historyPage!=null)historyPage.ApplySelection(selection);};
            BuildDiagnostics(diag);
            MainNavigation.SelectedIndexChanged+=(s,e)=>{if(MainNavigation.SelectedTab==records&&historyPage!=null)historyPage.EnterPage(Engine.ActiveSource);};
        }
        TabPage AddPage(string name){var p=new TabPage(name){BackColor=UiTheme.Canvas,Padding=new Padding(7)};MainNavigation.TabPages.Add(p);return p;}

        void BuildOverview(TabPage page) { overviewViewport=OverviewPageBuilder.Build(page,points,cards,tips,overviewChart,Font); }
        Panel overviewViewport;

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
                else if(q.StartsWith("过期")||q.Contains("旧值")||q.Contains("陈旧")){fore=UiTheme.Warning;back=Color.FromArgb(252,241,222);}
                else if(q.Contains("超时")||q.Contains("异常")||q.Contains("错误")||q.Contains("失败")){fore=UiTheme.Danger;back=Color.FromArgb(251,231,232);}
                e.CellStyle.ForeColor=fore;e.CellStyle.BackColor=back;e.CellStyle.SelectionBackColor=back;e.CellStyle.SelectionForeColor=fore;
            };
            page.Controls.Add(grid);
        }

        void BuildDiagnostics(TabPage page) {
            var shell=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(4),AutoScroll=true };shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,110));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));page.Controls.Add(shell);
            var box=new GroupBox { Text="只读 Modbus 03 手动读取",Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(8),BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink };
            // Let the controls wrap into additional rows at narrow widths. A fixed-height
            // diagnostic strip clipped the parser and send buttons on 960px windows.
            var controls=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,AutoScroll=false,Padding=new Padding(4) };
            NumericUpDown slave=Number(1,247,64),address=Number(0,65535,92),count=Number(1,125,64),diagTimeout=Number(1,30,64);
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
            var resultBox=new GroupBox { Text="最近一次读取",Dock=DockStyle.Fill,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Padding=new Padding(6) };resultBox.Controls.Add(result);
            // 历史会话日志放在独立只读窗口里:不占用诊断页的布局高度,也不影响通信日志的可视高度。
            var openSessionLogs=new StyledActionButton { Text="历史会话日志…",Width=132,Anchor=AnchorStyles.Top|AnchorStyles.Right,Margin=Padding.Empty,Location=new System.Drawing.Point(8,26) };
            openSessionLogs.Click+=(s,e)=>Dispatch(()=>{using(var dialog=new SessionLogForm(appRoot))dialog.ShowDialog(this);});
            resultBox.Controls.Add(openSessionLogs);
            resultBox.Resize+=(s,e)=>{openSessionLogs.Location=new System.Drawing.Point(Math.Max(8,resultBox.ClientSize.Width-openSessionLogs.Width-12),26);openSessionLogs.BringToFront();};
            shell.Controls.Add(resultBox,0,1);
            // 提示语只留一处:作为通信日志的初始内容。
            log.Text="诊断只读且不写入正式历史记录。请先连接，再停止周期采集后发送读取。\r\n";
            var logBox=new GroupBox { Text="通信日志",Dock=DockStyle.Fill,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Padding=new Padding(6),MinimumSize=new Size(0,100) };logBox.Controls.Add(log);shell.Controls.Add(logBox,0,2);
            raw.CheckedChanged+=(s,e)=>Engine.RecordRawLog=raw.Checked;clear.Click+=(s,e)=>{log.Clear();diagnosticRequests=diagnosticGood=diagnosticBad=0;counters.Text="读取 0 次 · 成功 0 · 异常 0";result.Text="日志已清空；正式历史记录与本地数据库不会受影响。";};
            read.Click+=async(s,e)=>{
                if(!Engine.IsConnected){MessageBox.Show(this,"请先连接数据源。","通信诊断",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                if(Engine.IsRunning){MessageBox.Show(this,"请先停止周期采集，再执行手动读取。","通信诊断",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                read.Enabled=false;string selectedMode=(string)mode.SelectedItem;if(selectedMode=="RAW 原始寄存器")selectedMode="RAW";
                result.Text="读取中…";
                diagnosticRequests++;
                try {DiagnosticResult r=await Engine.ReadDiagnosticAsync((int)slave.Value,(int)address.Value,(int)count.Value,selectedMode,(int)diagTimeout.Value);string status=r.Success?"正常":r.Status;if(r.Success)diagnosticGood++;else diagnosticBad++;counters.Text="读取 "+diagnosticRequests+" 次 · 成功 "+diagnosticGood+" · 异常 "+diagnosticBad;result.Text="最近收发时间："+r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")+"\r\n站号："+r.Slave+"　地址："+r.Address+"　数量："+r.Count+"　解析："+selectedMode+"\r\n结果："+status+" · "+r.Value+"\r\nTX："+r.TxHex+"\r\nRX："+r.RxHex+(String.IsNullOrEmpty(r.PayloadHex)?"":"\r\n数据："+r.PayloadHex)+"\r\n耗时："+r.Milliseconds+" ms";AppendLog("诊断读取 "+status+" · "+r.TxHex+" → "+r.RxHex);}
                catch(Exception ex){diagnosticBad++;counters.Text="读取 "+diagnosticRequests+" 次 · 成功 "+diagnosticGood+" · 异常 "+diagnosticBad;result.Text="读取失败："+ex.Message;AppendLog("诊断读取失败："+ex.Message);}
                finally{read.Enabled=true;}
            };
        }
        public void AttachHistoryPage(HistoryPage page){if(page==null)throw new ArgumentNullException("page");historyPage=page;HistoryHost.Controls.Clear();page.Dock=DockStyle.Fill;HistoryHost.Controls.Add(page);UiTheme.Apply(page);page.SetActiveSource(Engine.ActiveSource??"simulation");
            // 记录页看到某段 → 带着筛选条件跳到趋势页。
            page.OpenTrendsRequested+=(selection)=>{MainNavigation.SelectedIndex=2;if(trendsPage!=null)trendsPage.ApplySelection(selection);};}
        public void AttachCloudPage(CloudPage page){if(page==null)throw new ArgumentNullException("page");if(cloudPage!=null)cloudPage.CloudStatusChanged-=OnCloudStatus;cloudPage=page;cloudPage.CloudStatusChanged+=OnCloudStatus;CloudHost.Controls.Clear();page.Dock=DockStyle.Fill;CloudHost.Controls.Add(page);UiTheme.Apply(page);OnCloudStatus(page.CurrentStatusLabel);}
        void OnCloudStatus(string value){Dispatch(()=>{
            CloudPublisherStatus snapshot=cloudPage==null?null:cloudPage.CurrentStatus;
            string state=snapshot==null?(value??""):snapshot.State;
            bool disabled=String.IsNullOrWhiteSpace(state)||state.Contains("未启用");
            cloudState.Text=disabled?"☁ 云端 · 未启用":"☁ 云端 · "+value;
            cloudState.ForeColor=disabled?UiTheme.Muted:state.Contains("在线")?UiTheme.Green:(state.Contains("失败")||state.Contains("错误")||state.Contains("认证")||state.Contains("TLS"))?UiTheme.Danger:UiTheme.Muted;
        });}
        public void SetCloudStatus(string value){Dispatch(()=>cloudState.Text=String.IsNullOrWhiteSpace(value)?"☁ 云端 · 未启用":"☁ 云端 · "+value);}
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

        /// <summary>
        /// 设定值卡片的第三行：不给"正常 · 时间"，而是给出"实测 / 偏差 / 是否被跟踪"。
        /// 这是本程序区分"命令值"与"实测值"的唯一位置，改这里就等于改整个语义。
        /// 返回 null 表示这不是设定值，走普通卡片的呈现。
        /// </summary>
        string SetpointCardStatus(Observation o,bool valid,bool hasOld,DateTime timestamp) {
            if(o==null) return null;
            PointInfo meta=PointCatalog.Get(o.Point);
            if(!PointRole.IsSetpoint(meta.Role)) return null;
            if(!valid) return QualityLabel(o.Quality)+(hasOld?" · 设定旧值 "+timestamp.ToLocalTime().ToString("HH:mm:ss"):" · 尚无设定值");
            string unit=String.IsNullOrEmpty(meta.Unit)?"":" "+meta.Unit;
            if(String.IsNullOrEmpty(meta.PairedWith)||!o.Number.HasValue)
                return "设定"+unit+"（无配对实测点）";
            double? actual=Engine.ControlNumericValue(meta.PairedWith);
            if(!actual.HasValue) return "设定"+unit+" · 实测点 "+meta.PairedWith+" 无读数";
            double deviation=actual.Value-o.Number.Value;
            if(Math.Abs(deviation)<=1.0) return "实测 "+actual.Value.ToString("0.#",CultureInfo.InvariantCulture)+" · 已跟踪";
            // 偏差大时必须区分"正在趋近"和"根本没启动"——后者才是要报警的状态。
            string why=ActuatorIdle()?"设定未被跟踪":"正在趋近";
            return "实测 "+actual.Value.ToString("0.#",CultureInfo.InvariantCulture)+"（偏差 "+deviation.ToString("+0.#;-0.#;0",CultureInfo.InvariantCulture)+"）· "+why;
        }

        /// <summary>超过这个秒数就把卡片上的旧值清空（陈旧第二档）。</summary>
        const double StaleLimitSeconds=60;

        /// <summary>
        /// 设备分母：本轮真正有数据回来的设备数 / 点表里一共几台设备。
        /// 只说"已连接"是不够的 —— 串口开着、4 台设备里 2 台没响应，那还是"已连接"。
        /// 这个分母是"链路通"与"设备都在"的分界线。
        /// </summary>
        string DeviceHealth() {
            var total=new HashSet<int>();
            var alive=new HashSet<int>();
            DateTime now=DateTime.UtcNow;
            double limit=Math.Max(10,(double)period.Value*3);
            foreach(Point p in points) {
                if(p==null||p.binding==null) continue;
                total.Add(p.binding.slave);
                DateTime last;
                if(lastGood.TryGetValue(p.name,out last)&&(now-last).TotalSeconds<=limit) alive.Add(p.binding.slave);
            }
            if(total.Count==0) return "0/0 设备";
            return alive.Count+"/"+total.Count+" 设备";
        }

        /// <summary>
        /// 全局搜索：按 点名 / 名称 / 力控原名(alias) / 寄存器地址 四路匹配，
        /// 定位并高亮对应卡片，Enter 或再点一次"查找"就在命中项之间循环。
        ///
        /// 刻意**不重建卡片墙** —— 重建会丢掉滚动位置和"我原本在哪一组"的上下文，
        /// 使用者的感受是"整页闪了一下，我不知道被带到哪"。
        /// </summary>
        int searchCursor=-1;
        string searchTerm="";
        Panel searchViewport;
        readonly Dictionary<Control,Timer> searchHighlights=new Dictionary<Control,Timer>();

        string SearchNext() {
            string term=(searchBox.Text??"").Trim();
            if(term.Length==0) return "请输入点名、名称或地址";
            if(term!=searchTerm) { searchTerm=term; searchCursor=-1; }
            var hits=new List<string>();
            foreach(Point p in points) {
                if(p==null||p.binding==null) continue;
                PointInfo meta=PointCatalog.Get(p);
                string address=p.binding.address_zero_based.ToString(CultureInfo.InvariantCulture);
                bool match=(p.name??"").IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0
                    ||(meta.Label??"").IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0
                    ||(meta.Alias??"").IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0
                    ||address==term;
                if(match) hits.Add(p.name);
            }
            if(hits.Count==0) return "没有匹配「"+term+"」的测点";
            searchCursor=(searchCursor+1)%hits.Count;
            string target=hits[searchCursor];
            if(!FocusCard(target)) return "找到了 "+target+"，但它当前不在总览页";
            string suffix=hits.Count>1?"（第 "+(searchCursor+1)+"/"+hits.Count+" 个）":"";
            return "定位 "+target+" "+PointCatalog.Get(target).Label+suffix;
        }

        /// <summary>把某张卡片滚到可见区域并短暂高亮。</summary>
        bool FocusCard(string point) {
            Label[] cardLabels;
            if(!cards.TryGetValue(point,out cardLabels)||cardLabels==null||cardLabels.Length==0) return false;
            Control card=cardLabels[1];
            while(card!=null&&!(card is RoundedPanel)) card=card.Parent;
            if(card==null) return false;
            if(searchViewport==null) searchViewport=overviewViewport;
            if(searchViewport!=null&&!searchViewport.IsDisposed) {
                // 用屏幕坐标把卡片换算成视口的内容坐标，再设滚动位置。
                // 直接加 card.Parent.Top + card.Top 会漏掉 section 在 stack 里的偏移，
                // 越靠后的分组偏得越多 —— 表现就是"查找了但没滚到"。
                // AutoScrollPosition 的 setter 收正值（getter 返回负值）。
                DrawingPoint inViewport=searchViewport.PointToClient(card.PointToScreen(DrawingPoint.Empty));
                int contentY=(-searchViewport.AutoScrollPosition.Y)+inViewport.Y;
                searchViewport.AutoScrollPosition=new DrawingPoint(0,Math.Max(0,contentY-24));
            }
            HighlightCard(card);
            return true;
        }

        void HighlightCard(Control card) {
            Timer existing;
            if(searchHighlights.TryGetValue(card,out existing)) { try{existing.Stop();existing.Dispose();}catch{} searchHighlights.Remove(card); }
            Color original=card.BackColor;
            card.BackColor=Color.FromArgb(255,244,214);
            var timer=new Timer{Interval=1500};
            timer.Tick+=(s,e)=>{try{timer.Stop();if(!card.IsDisposed)card.BackColor=original;}catch{}try{timer.Dispose();}catch{}searchHighlights.Remove(card);};
            searchHighlights[card]=timer;
            timer.Start();
        }

        static T FindControl<T>(Control root,Func<T,bool> match) where T:Control {
            if(root==null) return null;
            foreach(Control child in root.Controls){T v=child as T;if(v!=null&&(match==null||match(v)))return v;T nested=FindControl(child,match);if(nested!=null)return nested;}
            return null;
        }

        /// <summary>Ctrl+F 聚焦搜索框。用 ProcessCmdKey 而不是 KeyDown：焦点在任何子控件时都要生效。</summary>
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
            if(keyData==(Keys.Control|Keys.F)) { searchBox.Focus();searchBox.SelectAll();return true; }
            if(keyData==Keys.Enter&&searchBox.Focused) { AppendLog("查找 · "+SearchNext());return true; }
            return base.ProcessCmdKey(ref msg,keyData);
        }

        /// <summary>陈旧时长的人读文案。</summary>
        static string AgeText(double seconds) {
            if(seconds<90) return ((int)Math.Max(0,seconds))+"s";
            if(seconds<5400) return ((int)(seconds/60))+"m";
            return ((int)(seconds/3600))+"h";
        }

        /// <summary>执行器是否停机：压缩机电流接近空载。</summary>
        bool ActuatorIdle() {
            double? current=Engine.ControlNumericValue("D2");
            return current.HasValue&&current.Value<0.5;
        }
        void Dispatch(Action action){if(closed||IsDisposed)return;try{if(IsHandleCreated)BeginInvoke(action);}catch(InvalidOperationException){} }
        void RefreshControls(){bool connected=Engine.IsConnected,running=Engine.IsRunning;if(!running&&String.IsNullOrEmpty(Engine.LastStoreError)){recordState.Text=Engine.CompletedCaptures>0?"▤ 记录 · 已停止":"▤ 记录 · 待机";recordState.ForeColor=UiTheme.Muted;}start.Enabled=once.Enabled=connected&&!running;stop.Enabled=running;headerAction.Text=connected?"断开连接":"连接";headerAction.Enabled=connected||!running;simulation.Enabled=port.Enabled=!connected&&!running;refresh.Enabled=!connected&&!running;timeout.Enabled=!running;period.Enabled=true;connectionState.Text=connected?(Engine.ConnectedSimulation?"● 链路 · 模拟 · "+DeviceHealth():"● 链路 · 已连接 · "+Engine.ConnectedPort+" · "+DeviceHealth()):"○ 链路 · 未连接";connectionState.ForeColor=connected?UiTheme.Green:UiTheme.Muted;captureState.Text=running?"▶ 采集 · 运行中":"⏸ 采集 · 已停止";captureState.ForeColor=running?UiTheme.Green:UiTheme.Muted;}
        void ConnectCaptureSource(){try{if(!simulation.Checked&&String.IsNullOrWhiteSpace(port.Text))throw new InvalidOperationException("没有可用串口。请接入 USB-RS485 转换器后点击刷新。");Engine.Connect(simulation.Checked,port.Text,9600);SaveSettings();RefreshControls();}catch(Exception ex){MessageBox.Show(this,"连接失败："+ex.Message,"连接",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
        void SetConnection(bool connected,string source){string key=source??Engine.ActiveSource;if(connected){if(displayedSource!=null&&key!=displayedSource)ResetDisplay();displayedSource=key;connectionState.Text=key=="simulation"?"● 链路 · 模拟 · "+DeviceHealth():"● 链路 · 已连接 · "+Engine.ConnectedPort+" · 9600 8N1 · "+DeviceHealth();connectionState.ForeColor=UiTheme.Green;AppendLog("连接状态："+connectionState.Text);if(historyPage!=null)historyPage.SetActiveSource(key);}else{connectionState.Text="○ 未连接";connectionState.ForeColor=UiTheme.Muted;MarkLastValuesStale("连接已断开");AppendLog("连接已断开 · 最后采样保留");}RefreshControls();}
        void MarkLastValuesStale(string label){foreach(var p in lastGood){Label[] c;if(cards.TryGetValue(p.Key,out c)){c[3].Text=label+" · "+p.Value.ToLocalTime().ToString("HH:mm:ss");c[3].ForeColor=UiTheme.Warning;}DataRow r;if(rows.TryGetValue(p.Key,out r)&&r["质量"].ToString()=="正常")r["质量"]="过期 · "+label;}}
        void StartCapture(bool single){try{if(!Engine.IsConnected)throw new InvalidOperationException("请先连接数据源");SaveSettings();shownStoreError=null;good=bad=0;Engine.SetPeriodSeconds((int)period.Value);if(single)Engine.StartOnce((int)period.Value,(int)timeout.Value,points.Select(p=>p.name));else Engine.Start((int)period.Value,(int)timeout.Value,points.Select(p=>p.name));RefreshControls();captureState.Text=single?"▶ 采集 · 运行中（单轮）":"▶ 采集 · 运行中";}catch(Exception ex){ShowError(single?"单轮采集失败":"开始采集失败",ex);}}
        void StopCapture(){try{Engine.Stop();captureState.Text="⏸ 采集 · 已停止 · 连接保持";MarkLastValuesStale("采集已停止");RefreshControls();}catch(Exception ex){ShowError("停止采集失败",ex);}}
        void RefreshFreshness(){RefreshControls();string fatal=Engine.LastStoreError;if(!String.IsNullOrWhiteSpace(fatal)){recordState.Text="! 记录 · 失败 · "+fatal;recordState.ForeColor=UiTheme.Danger;if(shownStoreError!=fatal){shownStoreError=fatal;AppendLog("本地记录失败："+fatal);MessageBox.Show(this,"本地记录出现错误，采集已停止或正在停止。请检查数据目录和磁盘空间。\r\n\r\n"+fatal,"本地记录失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}}else if(Engine.Store!=null){var s=Engine.Store.Status;if(s!=null){recordState.Text=s.Recording?"▤ 记录 · "+s.Committed.ToString("N0")+" 条":"▤ 记录 · 已停止";if(!String.IsNullOrEmpty(s.LastError))recordState.Text="! 记录 · 错误 · "+s.LastError;recordState.ForeColor=String.IsNullOrEmpty(s.LastError)?(s.Recording?UiTheme.Green:UiTheme.Muted):UiTheme.Danger;}}else recordState.Text=Engine.CompletedCaptures>0?"▤ 记录 · 已停止":"▤ 记录 · 待机";
            foreach(var pair in lastGood){double staleNow=(DateTime.UtcNow-pair.Value).TotalSeconds;if(staleNow>Math.Max(10,(double)period.Value*3)){Label[] c;if(cards.TryGetValue(pair.Key,out c)&&!c[3].Text.Contains("陈旧")&&!c[3].Text.Contains("采集已停止")&&!c[3].Text.Contains("连接已断开")){PointInfo staleMeta=PointCatalog.Get(pair.Key);c[3].Text=((!staleMeta.SemanticConfirmed||!String.IsNullOrEmpty(staleMeta.UnitQualifier))?"△ ":"")+"陈旧 "+AgeText(staleNow);c[3].ForeColor=UiTheme.Warning;if(staleNow>StaleLimitSeconds)c[1].Text="—";}DataRow r;if(rows.TryGetValue(pair.Key,out r)&&r["质量"].ToString()=="正常")r["质量"]="过期";}}}
        public void UpdateObservation(Observation o){if(o==null)return;if(!String.IsNullOrEmpty(o.SessionId)&&countedSession!=o.SessionId){countedSession=o.SessionId;good=bad=0;}bool valid=Quality.IsGood(o.Quality);if(valid)good++;else if(Quality.IsFailure(o.Quality))bad++;DateTime previous;bool hasOld=lastGood.TryGetValue(o.Point,out previous);
            DataRow r;if(rows.TryGetValue(o.Point,out r)){if(valid){r["实时值"]=DisplayNumber(o);r["采样时间"]=o.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture);}r["质量"]=valid?"正常":QualityLabel(o.Quality)+(hasOld?" · 保留旧值":" · 尚无有效值");r["耗时(ms)"]=o.Milliseconds.ToString(CultureInfo.InvariantCulture);}
            Label[] c;if(cards.TryGetValue(o.Point,out c)){if(valid){c[1].Text=DisplayCardValue(o);OverviewPageBuilder.FitValueFont(c[1]);lastGood[o.Point]=o.Utc;}DateTime timestamp=valid?o.Utc:previous;
                // 设定值是"命令"不是"读数"：卡片上必须同时给出实测值与偏差，
                // 否则使用者会把"设定 8"当成"实测 8"。
                string roleText=SetpointCardStatus(o,valid,hasOld,timestamp);
                if(roleText!=null){c[3].Text=roleText;}
                else {
                    PointInfo cardMeta=PointCatalog.Get(o.Point);
                    // 存疑标记：语义未裁决，或单位带限定语（如"二次侧"）。这类值可以显示，但不能当结论用。
                    string doubt=(!cardMeta.SemanticConfirmed||!String.IsNullOrEmpty(cardMeta.UnitQualifier))?"△ ":"";
                    if(valid) {
                        c[3].Text=doubt+"正常 · "+timestamp.ToLocalTime().ToString("HH:mm:ss");
                    } else if(!hasOld) {
                        // 从来没有读到过：不能显示"正常"，也不该显示数字。
                        c[3].Text=doubt+"○ 未接入";
                    } else {
                        double staleSeconds=(DateTime.UtcNow-previous).TotalSeconds;
                        // 陈旧分两档：短时间内保留最后有效值（可能只是这一轮没读到），
                        // 超过 60 秒就清空 —— 一个 10 分钟前的数字看起来和现在的数字一模一样，
                        // 那是最容易误导人的形态。
                        if(staleSeconds>StaleLimitSeconds) c[1].Text="—";
                        c[3].Text=doubt+QualityLabel(o.Quality)+" · 陈旧 "+AgeText(staleSeconds);
                    }
                }
                c[3].ForeColor=valid?UiTheme.Green:(Quality.IsFailure(o.Quality)?UiTheme.Danger:UiTheme.Muted);tips.SetToolTip(c[1],valid?"实际值："+(o.Value??DisplayNumber(o))+"\r\n采样时间："+o.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"):o.Value);}
            overviewChart.Add(o);
            // 状态栏右侧只放最必要的计数。原先这里还有一句"本地记录按实际采样时间保存"，
            // 属于说明文档的内容；它把 960 宽下的四段状态挤得看不见了。
            counts.Text="成功 "+good+" / 异常 "+bad+"　·　"+(o.Source=="serial"?"实机":"模拟")+"　·　"+o.Point;
        }
        static string DisplayNumber(Observation o){if(o.Number.HasValue){double value=o.Number.Value;if(value==0)return "0";string s=value.ToString("G6",CultureInfo.InvariantCulture);if(s=="0"||s=="-0")s=value.ToString("G9",CultureInfo.InvariantCulture);return s;}return o.Value??"—";}
        static string DisplayCardValue(Observation o){if(o.Mode=="U16 / I16"&&o.Value!=null){int split=o.Value.IndexOf(" / I16=",StringComparison.Ordinal);if(split>0){string a=o.Value.Substring(0,split),b=o.Value.Substring(split+3);int annotation=b.IndexOf('（');if(annotation>=0)b=b.Substring(0,annotation);return a+Environment.NewLine+b;}}return DisplayNumber(o);}
        static string QualityLabel(string q){return Quality.Label(q);}
        void AppendLog(string value){if(log.TextLength>300000)log.Text=log.Text.Substring(log.TextLength-150000);log.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture)+"  "+value+Environment.NewLine);}
        void LoadSettings(){try{if(!File.Exists(settingsPath))return;SavedSettings s=new JavaScriptSerializer().Deserialize<SavedSettings>(File.ReadAllText(settingsPath));if(s==null)return;simulation.Checked=false;period.Value=Math.Max(period.Minimum,Math.Min(period.Maximum,s.Period));timeout.Value=Math.Max(timeout.Minimum,Math.Min(timeout.Maximum,s.Timeout));if(!String.IsNullOrEmpty(s.Port)){if(!port.Items.Contains(s.Port))port.Items.Add(s.Port);port.SelectedItem=s.Port;}}catch(Exception ex){AppendLog("设置读取失败，使用默认值："+ex.Message);}}
        void SaveSettings(){try{string dir=Path.GetDirectoryName(settingsPath);Directory.CreateDirectory(dir);File.WriteAllText(settingsPath,new JavaScriptSerializer().Serialize(new SavedSettings{Simulation=simulation.Checked,Port=port.Text,Period=(int)period.Value,Timeout=(int)timeout.Value}),new System.Text.UTF8Encoding(false));}catch(Exception ex){AppendLog("设置保存失败："+ex.Message);}}
        void RefreshPorts(){string old=port.Text;port.Items.Clear();port.Items.AddRange(SerialPort.GetPortNames().OrderBy(x=>x.Length).ThenBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray());if(port.Items.Contains(old))port.SelectedItem=old;else if(port.Items.Count>0)port.SelectedIndex=0;}
        void ShowError(string title,Exception ex){MessageBox.Show(this,ex.Message,title,MessageBoxButtons.OK,MessageBoxIcon.Error);}
        static Label TextLabel(string value){return new Label{Text=value,AutoSize=true,Padding=new Padding(2,6,2,0),ForeColor=UiTheme.Muted};}
        static Label StatusLabel(string value){return new Label{Text=value,AutoSize=true,Padding=new Padding(0,4,16,0),ForeColor=UiTheme.Muted,MaximumSize=new Size(480,0)};}
        static NumericUpDown Number(int min,int max,int width){return new NumericUpDown{Minimum=min,Maximum=max,Width=width};}
    }
}










