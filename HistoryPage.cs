using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>BMS-style local history: follow newest while visible; freeze when the user changes filters.</summary>
    public sealed class HistoryPage : UserControl {
        readonly string dataRoot,appRoot; readonly MonitorEngine engine; readonly List<Point> allPoints;
        readonly DateTimePicker from=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Width=210};
        readonly DateTimePicker to=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Width=210};
        readonly ComboBox source=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=118};
        readonly CheckedListBox devices=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false};
        readonly CheckedListBox channels=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false};
        readonly TextBox deviceSearch=new TextBox{Dock=DockStyle.Fill}; readonly TextBox pointSearch=new TextBox{Dock=DockStyle.Fill};
        readonly DataGridView grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false};
        // —— 实验段 / 测点统计 ——
        readonly DataGridView sessionTable=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,RowTemplate={Height=28}};
        readonly DataGridView pointTable=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false,RowTemplate={Height=28}};
        readonly StyledActionButton sessionRun=new StyledActionButton{Text="统计实验段",Width=120,BackColor=UiTheme.Blue},sessionStop=new StyledActionButton{Text="停止统计",Width=96,Visible=false};
        readonly StyledActionButton pointRun=new StyledActionButton{Text="统计测点",Width=112,BackColor=UiTheme.Blue},pointStop=new StyledActionButton{Text="停止统计",Width=96,Visible=false},limitsSave=new StyledActionButton{Text="保存阈值",Width=104},pointLocate=new StyledActionButton{Text="看该测点记录",Width=124};
        readonly Label sessionStatus=new Label{AutoSize=true,ForeColor=UiTheme.Muted,Text="点“统计实验段”按当前筛选范围汇总；一次“开始采集”算一段。"};
        readonly Label pointStatus=new Label{AutoSize=true,ForeColor=UiTheme.Muted,Text="点“统计测点”按当前筛选范围汇总；下限/上限可直接在表里改。"};
        readonly StyledActionButton openTrends=new StyledActionButton{Text="在趋势页查看 ▸",Width=152};
        readonly Dictionary<string,Limit> limits=new Dictionary<string,Limit>(StringComparer.Ordinal);
        List<SessionSummary> sessionRows=new List<SessionSummary>();List<PointSummary> pointRows=new List<PointSummary>();string sessionSignature="",pointSignature="";
        bool sessionBusy,pointBusy;CancellationTokenSource sessionToken,pointToken;
        public event Action<HistorySelection> OpenTrendsRequested;
        readonly Label summary=new Label{AutoSize=true,ForeColor=Color.FromArgb(75,96,120),TextAlign=ContentAlignment.MiddleLeft};
        readonly Label feedback=new Label{AutoSize=true,ForeColor=Color.FromArgb(75,96,120),Text="选择时间范围并查询记录。"};
        readonly Label exportStatus=new Label{AutoSize=true,Visible=false,ForeColor=Color.FromArgb(45,108,175)};
        readonly StyledActionButton query=new StyledActionButton{Text="查询记录",Width=120,BackColor=UiTheme.Blue,IconGlyph="⌕"},follow=new StyledActionButton{Text="回到最新",Width=104},next=new StyledActionButton{Text="‹ 更早 200 轮",Width=124,Enabled=false},export=new StyledActionButton{Text="导出数据  ▾",Width=128},cancel=new StyledActionButton{Text="取消导出",Visible=false,Width=96},columnPick=new StyledActionButton{Text="列选择  ▾",Width=100};
        readonly ContextMenuStrip exportMenu=new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};
        readonly ComboBox partitionPreset=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=100};
        readonly NumericUpDown partitionCustom=new NumericUpDown{Minimum=1,Maximum=3650,Width=86};
        readonly StyledActionButton applyNext=new StyledActionButton{Text="下个分期生效",Width=116},applyNow=new StyledActionButton{Text="立即新分期",Width=108};
        readonly Dictionary<string,bool> columnVisible=new Dictionary<string,bool>(StringComparer.Ordinal);
        Label emptyHint;
        // 默认列子集:常用三组(温度/实验电表/市电);用户在「列选择」里的勾选优先。
        bool ColumnVisible(string name){bool v;if(columnVisible.TryGetValue(name,out v))return v;string g=PointCatalog.Get(name).Group;return g=="温度"||g=="实验电表"||g=="市电";}
        readonly Label storageStatus=new Label{AutoSize=true,MaximumSize=new Size(1000,0),ForeColor=Color.FromArgb(75,96,120)};
        readonly DataGridView inventory=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,EnableHeadersVisualStyles=false};
        readonly Panel storageBody=new Panel{Dock=DockStyle.Fill,Visible=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
        readonly LinkLabel storageToggle=new LinkLabel{Text="本地存储状态与分库设置  ▸",AutoSize=true,LinkColor=Color.FromArgb(45,108,175)};
        readonly ProgressBar progress=new ProgressBar{Width=120,Height=16,Visible=false};
        TableLayoutPanel outerLayout;TabControl recordTabs;
        readonly StyledActionButton oldest=new StyledActionButton{Text="« 最早",Width=84},stopQuery=new StyledActionButton{Text="取消查询",Width=96,Visible=false},newer=new StyledActionButton{Text="更新 ›",Width=96},latest=new StyledActionButton{Text="最新 »",Width=84},jumpStart=new StyledActionButton{Text="跳到开始时间",Width=150},jumpTo=new StyledActionButton{Text="跳到该时刻",Width=124};
        // 分页条一律固定高度、固定列宽、不使用 AutoSize:标签文本变化不触发布局链重排。
        readonly Label pageInfo=new Label{AutoSize=false,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold),ForeColor=Color.FromArgb(25,52,82),Width=240,Height=26,Margin=new Padding(8,2,8,0)};
        readonly Label coverage=new Label{AutoSize=false,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(75,96,120),Dock=DockStyle.Fill,Margin=Padding.Empty};
        readonly DateTimePicker anchorTime=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm",Width=176,ShowUpDown=true};
        readonly ComboBox pageSizeBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=96};
        // —— 分页与定位:一律用时间锚点重新定位,不逐页前推;页码只在从最新端连续前进时累加 ——
        const int NavNewest=0,NavOlder=1,NavNewer=2,NavEarliest=3,NavAtTime=4;
        int pageSize=200,pageIndex,queuedNav=-1,appliedNav=NavNewest;
        DateTime? pendingAnchor,pageFrom,pageTo;bool pendingDescending=true,hasOlder,hasNewer,layoutGuard;DateTime queuedAt;
        readonly CheckBox hideTimeout=new CheckBox{Text="隐藏仅超时的轮次",Checked=false,AutoSize=true,Margin=new Padding(8,7,8,0)};
        bool countBusy,hideTimeoutRounds,noPointColumns;ToolStripDropDown columnDrop;string countSignature="";long countTotal=-1;long lastQueryMs;CancellationTokenSource queryToken;HistoryFilter summaryFilter,countFilter;int summaryUnits,summaryValid,summaryTimeout;bool summaryRoundView,countRoundView;HistoryLoad lastLoad;readonly System.Windows.Forms.Timer countTimer=new System.Windows.Forms.Timer{Interval=1500};
        readonly HashSet<string> selectedDevices=new HashSet<string>(StringComparer.Ordinal),selectedPoints=new HashSet<string>(StringComparer.Ordinal);
        CancellationTokenSource exportCancel; bool following=true,explicitSource,queryBusy,pendingViewQuery,pendingFollowQuery,disposed,entered,inventoryBusy,filterDirty=true,wideView=true,rebuildingLists,settingSource,updatingFollowTime; int offset,generation,appliedGeneration,partitionDays=30,serialPartitionDays=30,simulationPartitionDays=30; long lastCommitted=-1,lastCompletedCaptures=-1; string activeSource="simulation",previewSignature=""; DateTime lastInventory=DateTime.MinValue;

        public HistoryPage(List<Point> points,string root,MonitorEngine engine) {
            if(engine==null)throw new ArgumentNullException("engine");this.engine=engine;if(points==null)throw new ArgumentNullException("points");allPoints=points;appRoot=root;dataRoot=Path.Combine(root,"data","storage");Dock=DockStyle.Fill;AutoScaleMode=AutoScaleMode.Dpi;BackColor=Color.FromArgb(239,244,249);Padding=new Padding(8);
            from.Value=DateTime.Now.AddHours(-2);to.Value=DateTime.Now;
            source.Items.AddRange(new object[]{"serial","simulation"});source.SelectedItem="simulation";source.FormattingEnabled=true;source.Format+=(s,e)=>{string v=e.ListItem as string;e.Value=v=="serial"?"实机":"模拟";};
            BuildLayout();LoadPartitionSetting();WireEvents();StyleGrid(grid);StyleGrid(inventory);UiTheme.Apply(this);VisibleChanged+=OnVisibleChanged;Disposed+=(s,e)=>DisposePage();
            foreach(var pair in LimitStore.Load(appRoot))limits[pair.Key]=pair.Value;
            if(limits.Count>0)pointStatus.Text="已加载 "+limits.Count+" 个测点的阈值；点“统计测点”查看越限。";
        }
        void BuildLayout(){
            var viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=BackColor};var outer=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,RowCount=7,BackColor=BackColor,Padding=Padding.Empty,MinimumSize=new Size(0,680)};outerLayout=outer;outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,42));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,94));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,48));outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,36));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,25));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,0));viewport.Controls.Add(outer);Controls.Add(viewport);viewport.Resize+=(s,e)=>ResizeOuter();ResizeOuter();
            var titleRow=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(10,5,8,4)};var title=new Label{Text="本地数据记录",Font=new Font("Microsoft YaHei UI",14,FontStyle.Bold),ForeColor=Color.FromArgb(25,52,82),Dock=DockStyle.Left,Width=220,TextAlign=ContentAlignment.MiddleLeft};titleRow.Controls.Add(title);
            export.Anchor=AnchorStyles.Top|AnchorStyles.Right;cancel.Anchor=AnchorStyles.Top|AnchorStyles.Right;exportStatus.Anchor=AnchorStyles.Top|AnchorStyles.Right;progress.Anchor=AnchorStyles.Top|AnchorStyles.Right;titleRow.Controls.Add(export);titleRow.Controls.Add(cancel);titleRow.Controls.Add(exportStatus);titleRow.Controls.Add(progress);titleRow.Resize+=(s,e)=>PositionExport(titleRow);outer.Controls.Add(titleRow,0,0);
            var filters=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,BackColor=Color.White,Padding=new Padding(7,5,5,3),Margin=new Padding(0,4,0,3)};
            filters.Controls.Add(MakeLabel("数据源"));filters.Controls.Add(new RoundedInputHost(source){Width=130});filters.Controls.Add(MakeLabel("开始时间"));filters.Controls.Add(from);filters.Controls.Add(MakeLabel("结束时间（不含）"));filters.Controls.Add(to);
            var quick=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=128};quick.Items.AddRange(new object[]{"快捷范围…","最近 15 分钟","最近 1 小时","最近 2 小时","今天"});quick.SelectedIndex=0;quick.SelectedIndexChanged+=(s,e)=>{if(quick.SelectedIndex==0)return;DateTime now=DateTime.Now;to.Value=NextLocalSecond(now);from.Value=quick.SelectedIndex==1?now.AddMinutes(-15):quick.SelectedIndex==2?now.AddHours(-1):quick.SelectedIndex==3?now.AddHours(-2):now.Date;MarkDirty();quick.SelectedIndex=0;};filters.Controls.Add(new RoundedInputHost(quick){Width=140});filters.Controls.Add(query);filters.Controls.Add(follow);filters.Controls.Add(openTrends);filters.Controls.Add(columnPick);outer.Controls.Add(filters,0,1);
            var summaryRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,BackColor=Color.White,Padding=new Padding(8,4,4,2)};summaryRow.Controls.Add(summary);outer.Controls.Add(summaryRow,0,2);
            var split=new SplitContainer{Dock=DockStyle.Fill,Size=new Size(800,500),Panel1MinSize=190,Panel2MinSize=380,SplitterDistance=258,BackColor=BackColor};outer.Controls.Add(split,0,3);
            var pickLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,BackColor=Color.White,Padding=new Padding(5)};pickLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,24));pickLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));pickLayout.RowStyles.Add(new RowStyle(SizeType.Percent,32));pickLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,24));pickLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));pickLayout.RowStyles.Add(new RowStyle(SizeType.Percent,68));split.Panel1.Controls.Add(pickLayout);
            pickLayout.Controls.Add(MakeLabel("设备"),0,0);pickLayout.Controls.Add(new RoundedInputHost(deviceSearch){Dock=DockStyle.Fill},0,1);pickLayout.Controls.Add(devices,0,2);pickLayout.Controls.Add(MakeLabel("测点"),0,3);pickLayout.Controls.Add(new RoundedInputHost(pointSearch){Dock=DockStyle.Fill},0,4);pickLayout.Controls.Add(channels,0,5);
            foreach(string d in allPoints.Select(p=>p.binding.device).Distinct()){devices.Items.Add(d,true);selectedDevices.Add(d);}
            foreach(Point p in allPoints){channels.Items.Add(p.name+" · "+PointCatalog.Get(p.name).Label,true);selectedPoints.Add(p.name);}
            // 迷你 Tab 样式对齐主选项卡(蓝色下划线选中态)。
            var tabs=new TabControl{Dock=DockStyle.Fill,DrawMode=TabDrawMode.OwnerDrawFixed,ItemSize=new Size(110,30),SizeMode=TabSizeMode.Fixed,Padding=new System.Drawing.Point(10,3)};
            tabs.DrawItem+=(s,e)=>{bool sel=(e.State&DrawItemState.Selected)!=0;using(var b=new SolidBrush(sel?Color.White:Color.FromArgb(247,250,253)))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,tabs.TabPages[e.Index].Text,tabs.Font,e.Bounds,sel?UiTheme.Blue:UiTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);if(sel)using(var p=new Pen(UiTheme.Blue,2))e.Graphics.DrawLine(p,e.Bounds.Left+18,e.Bounds.Bottom-2,e.Bounds.Right-18,e.Bounds.Bottom-2);};
            var records=new TabPage("数据记录"){BackColor=Color.White};var sessions=new TabPage("实验段"){BackColor=Color.White};var pointTab=new TabPage("测点统计"){BackColor=Color.White};tabs.TabPages.Add(records);tabs.TabPages.Add(sessions);tabs.TabPages.Add(pointTab);records.Controls.Add(grid);
            emptyHint=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=UiTheme.Muted,BackColor=Color.White,Text="当前筛选范围没有记录。",Visible=false};
            records.Controls.Add(emptyHint);emptyHint.BringToFront();
            // 分页条放在表格正上方:两行固定高度;两行都用按比例分列,窗口被压窄时控件一起收窄而不是被裁掉。
            var pageBar=new Panel{Dock=DockStyle.Top,Height=82,BackColor=Color.White,Padding=new Padding(6,2,6,2)};
            var bar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.White,Margin=Padding.Empty,Padding=Padding.Empty};
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute,40));bar.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            var navRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,BackColor=Color.White,Margin=Padding.Empty,Padding=Padding.Empty};
            navRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,13));
            navRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,15));
            navRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));
            navRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,15));
            navRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,13));
            // 固定高度行里的子控件必须零外边距:36 高的按钮 + 6 上下边距会被 40 高的行裁掉。
            oldest.Margin=Padding.Empty;next.Margin=Padding.Empty;newer.Margin=Padding.Empty;latest.Margin=Padding.Empty;jumpStart.Margin=Padding.Empty;jumpTo.Margin=Padding.Empty;
            // 允许按钮随列宽收窄(默认最小宽 72 会在窄窗口里顶出列外)。
            oldest.MinimumSize=new Size(40,30);next.MinimumSize=new Size(40,30);newer.MinimumSize=new Size(40,30);latest.MinimumSize=new Size(40,30);jumpStart.MinimumSize=new Size(40,30);jumpTo.MinimumSize=new Size(40,30);
            oldest.Dock=DockStyle.Fill;next.Dock=DockStyle.Fill;newer.Dock=DockStyle.Fill;latest.Dock=DockStyle.Fill;pageInfo.Dock=DockStyle.Fill;
            navRow.Controls.Add(oldest,0,0);navRow.Controls.Add(next,1,0);navRow.Controls.Add(pageInfo,2,0);navRow.Controls.Add(newer,3,0);navRow.Controls.Add(latest,4,0);
            var infoRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,BackColor=Color.White,Margin=Padding.Empty,Padding=Padding.Empty};
            infoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,28));
            infoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,14));
            infoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,17));
            infoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,26));
            infoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,15));
            pageSizeBox.Items.AddRange(new object[]{"200 轮","500 轮"});pageSizeBox.SelectedIndex=0;
            jumpStart.Dock=DockStyle.Fill;jumpTo.Dock=DockStyle.Fill;
            var pageSizeHost=new RoundedInputHost(pageSizeBox){Dock=DockStyle.Fill,Margin=Padding.Empty,MinimumSize=Size.Empty};
            var anchorHost=new RoundedInputHost(anchorTime){Dock=DockStyle.Fill,Margin=Padding.Empty,MinimumSize=Size.Empty};
            infoRow.Controls.Add(coverage,0,0);
            infoRow.Controls.Add(pageSizeHost,1,0);
            infoRow.Controls.Add(jumpStart,2,0);
            infoRow.Controls.Add(anchorHost,3,0);
            infoRow.Controls.Add(jumpTo,4,0);
            bar.Controls.Add(navRow,0,0);bar.Controls.Add(infoRow,0,1);pageBar.Controls.Add(bar);records.Controls.Add(pageBar);
            BuildSessionTab(sessions);BuildPointTab(pointTab);
            split.Panel2.Controls.Add(tabs);
            recordTabs=tabs;tabs.SelectedIndexChanged+=(s,e)=>{if(tabs.SelectedIndex==0)RenderRows();};
            var pager=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,BackColor=Color.White,Padding=new Padding(4,0,2,0)};pager.Controls.Add(stopQuery);pager.Controls.Add(feedback);outer.Controls.Add(pager,0,4);
            storageToggle.Margin=new Padding(8,4,0,2);outer.Controls.Add(storageToggle,0,5);
            BuildStorageBody();
            // Fold-out storage controls are placed beneath the record panel without introducing another tab.
            outer.Controls.Add(storageBody,0,6);
            var view=new CheckBox{Text="按采集轮宽表显示",Checked=true,AutoSize=true,Margin=new Padding(8,7,8,0)};var viewRow=(FlowLayoutPanel)summaryRow;viewRow.Controls.Add(view);viewRow.Controls.Add(hideTimeout);columnPick.Enabled=view.Checked;view.CheckedChanged+=async(s,e)=>{wideView=view.Checked;columnPick.Enabled=view.Checked;generation++;if(!filterDirty){if(queryBusy)pendingViewQuery=true;else await ApplyAndQuery();}};hideTimeout.CheckedChanged+=(s,e)=>{hideTimeoutRounds=hideTimeout.Checked;RenderRows();};
            grid.ScrollBars=ScrollBars.Both;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;grid.RowTemplate.Height=28;grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;grid.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;
        }
        void BuildStorageBody(){
            var shell=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,RowCount=3,Padding=new Padding(8),BackColor=Color.White,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,60));shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var settings=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,AutoSize=true};settings.Controls.Add(MakeLabel("分库周期"));partitionPreset.Items.AddRange(new object[]{"7 天","15 天","30 天","自定义"});partitionPreset.SelectedIndex=2;settings.Controls.Add(new RoundedInputHost(partitionPreset){Width=112});settings.Controls.Add(new RoundedInputHost(partitionCustom){Width=96});settings.Controls.Add(applyNext);settings.Controls.Add(applyNow);settings.Controls.Add(storageStatus);shell.Controls.Add(settings,0,0);
            var invHeading=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};invHeading.Controls.Add(MakeLabel("本地数据库（按来源分开保存）"));var refresh=new StyledActionButton{Text="刷新清单",Width=96};refresh.Click+=(s,e)=>RefreshInventory(true);invHeading.Controls.Add(refresh);shell.Controls.Add(invHeading,0,1);
            var cols=new[]{"来源","数据库文件","大小 MiB","状态"};foreach(string c in cols)inventory.Columns.Add(c,c);inventory.Columns[0].FillWeight=45;inventory.Columns[1].FillWeight=200;inventory.Columns[2].FillWeight=55;inventory.Columns[3].FillWeight=85;inventory.MinimumSize=new Size(0,170);shell.Controls.Add(inventory,0,2);storageBody.Controls.Add(shell);
            storageToggle.LinkClicked+=(s,e)=>{bool show=!storageBody.Visible;storageBody.Visible=show;RowStyle rs=outerLayout.RowStyles[6];if(show)rs.SizeType=SizeType.AutoSize;else{rs.SizeType=SizeType.Absolute;rs.Height=0;}storageToggle.Text=show?"本地存储状态与分库设置  ▾":"本地存储状态与分库设置  ▸";ResizeOuter();};
        }
        void BuildSessionTab(TabPage page){
            sessionRun.Margin=Padding.Empty;sessionStop.Margin=Padding.Empty;sessionStatus.Margin=new Padding(10,11,4,0);
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,WrapContents=false,BackColor=Color.White,Padding=new Padding(2,2,0,0)};
            bar.Controls.Add(sessionRun);bar.Controls.Add(sessionStop);bar.Controls.Add(sessionStatus);
            string[] headers={"会话","开始","结束","时长","轮数","应有","缺口","最大间隔","记录数","超时轮","超时率","越限轮"};
            int[] weights={90,128,128,80,52,52,52,84,74,58,62,58};
            for(int i=0;i<headers.Length;i++){sessionTable.Columns.Add(headers[i],headers[i]);sessionTable.Columns[i].FillWeight=weights[i];sessionTable.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;}
            page.Controls.Add(sessionTable);page.Controls.Add(bar);
            sessionTable.CellDoubleClick+=(s,e)=>{if(e.RowIndex<0||e.RowIndex>=sessionTable.Rows.Count)return;SessionSummary summary=sessionTable.Rows[e.RowIndex].Tag as SessionSummary;if(summary!=null)ApplySession(summary);};
        }
        void BuildPointTab(TabPage page){
            pointRun.Margin=Padding.Empty;pointStop.Margin=Padding.Empty;limitsSave.Margin=Padding.Empty;pointLocate.Margin=Padding.Empty;pointStatus.Margin=new Padding(10,11,4,0);
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,WrapContents=false,BackColor=Color.White,Padding=new Padding(2,2,0,0)};
            bar.Controls.Add(pointRun);bar.Controls.Add(pointStop);bar.Controls.Add(limitsSave);bar.Controls.Add(pointLocate);bar.Controls.Add(pointStatus);
            string[] headers={"测点","名称","单位","有效点","超时点","越限点","最小","最大","均值","标准差","平均间隔","下限","上限"};
            int[] weights={58,90,44,60,58,58,84,84,84,74,74,70,70};
            for(int i=0;i<headers.Length;i++){pointTable.Columns.Add(headers[i],headers[i]);pointTable.Columns[i].FillWeight=weights[i];pointTable.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;if(i<11)pointTable.Columns[i].ReadOnly=true;}
            pointTable.Columns[11].DefaultCellStyle.BackColor=Color.FromArgb(248,251,254);
            pointTable.Columns[12].DefaultCellStyle.BackColor=Color.FromArgb(248,251,254);
            page.Controls.Add(pointTable);page.Controls.Add(bar);
        }
        static string Duration(TimeSpan span){
            if(span.TotalSeconds<0)return "—";
            if(span.TotalDays>=1)return ((int)span.TotalDays)+" 天 "+span.ToString(@"hh\:mm");
            if(span.TotalHours>=1)return span.ToString(@"h\:mm\:ss");
            if(span.TotalMinutes>=1)return span.ToString(@"m\:ss");
            return span.TotalSeconds.ToString("0")+" 秒";
        }
        static string SessionLabel(SessionSummary s){
            if(s==null)return "";
            if(s.Legacy)return "(无会话)";
            string value=s.Session??"";
            return value.Length>8?value.Substring(0,8):value;
        }
        async void RunSessionStats(){
            if(sessionBusy||disposed)return;
            HistoryFilter f;
            try{f=MakeFilter();}catch(Exception ex){sessionStatus.Text="筛选条件不完整："+ex.Message;return;}
            sessionBusy=true;sessionRun.Enabled=false;sessionStop.Visible=true;sessionStatus.Text="正在按筛选范围汇总实验段…";
            var cts=new CancellationTokenSource();sessionToken=cts;CancellationToken token=cts.Token;
            var dictionary=new Dictionary<string,Limit>(limits,StringComparer.Ordinal);
            try{
                List<SessionSummary> list=await Task.Run(()=>HistoryStats.Sessions(dataRoot,f,dictionary,token));
                if(disposed||token.IsCancellationRequested)return;
                sessionRows=list;sessionSignature=Signature(f);RenderSessionStats(list);
                long rounds=list.Sum(x=>x.Rounds);
                sessionStatus.Text="共 "+list.Count+" 段 · "+rounds.ToString("N0")+" 轮 · 双击一行定位到该段";
            }catch(OperationCanceledException){if(!disposed)sessionStatus.Text="统计已取消。";}
            catch(Exception ex){if(!disposed)sessionStatus.Text="统计失败："+ex.Message;}
            finally{sessionBusy=false;if(sessionToken==cts)sessionToken=null;try{cts.Dispose();}catch{}if(!disposed){sessionRun.Enabled=true;sessionStop.Visible=false;}}
        }
        void RenderSessionStats(List<SessionSummary> list){
            sessionTable.SuspendLayout();
            try{
                sessionTable.Rows.Clear();
                foreach(SessionSummary s in list){
                    int index=sessionTable.Rows.Add(
                        SessionLabel(s),
                        s.FromUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        s.ToUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        Duration(s.ToUtc-s.FromUtc),
                        s.Rounds.ToString("N0"),
                        s.ExpectedRounds.ToString("N0"),
                        s.MissingRounds>0?s.MissingRounds.ToString("N0"):"—",
                        s.MaxGapTicks>0?Duration(new TimeSpan(s.MaxGapTicks)):"—",
                        s.Records.ToString("N0"),
                        s.TimeoutRounds.ToString("N0"),
                        s.Rounds>0?((double)s.TimeoutRounds/s.Rounds).ToString("P1"):"—",
                        s.OverLimitRounds<0?"—":s.OverLimitRounds.ToString("N0"));
                    sessionTable.Rows[index].Tag=s;
                    if(s.MissingRounds>0)sessionTable.Rows[index].Cells[6].Style.ForeColor=Color.Firebrick;
                    if(s.TimeoutRounds>0)sessionTable.Rows[index].Cells[9].Style.ForeColor=Color.Firebrick;
                }
            }finally{sessionTable.ResumeLayout();}
        }
        async void RunPointStats(){
            if(pointBusy||disposed)return;
            HistoryFilter f;
            try{f=MakeFilter();}catch(Exception ex){pointStatus.Text="筛选条件不完整："+ex.Message;return;}
            pointBusy=true;pointRun.Enabled=false;pointStop.Visible=true;pointStatus.Text="正在按筛选范围汇总测点…";
            var cts=new CancellationTokenSource();pointToken=cts;CancellationToken token=cts.Token;
            var dictionary=new Dictionary<string,Limit>(limits,StringComparer.Ordinal);
            try{
                List<PointSummary> list=await Task.Run(()=>HistoryStats.Points(dataRoot,f,dictionary,token));
                if(disposed||token.IsCancellationRequested)return;
                pointRows=list;pointSignature=Signature(f);RenderPointStats(list);
                int limited=list.Count(x=>x.HasLimit);
                pointStatus.Text="共 "+list.Count+" 个测点"+(limited>0?" · 其中 "+limited+" 个已配阈值":" · 未配置阈值")+" · 下限/上限可直接在表里改";
            }catch(OperationCanceledException){if(!disposed)pointStatus.Text="统计已取消。";}
            catch(Exception ex){if(!disposed)pointStatus.Text="统计失败："+ex.Message;}
            finally{pointBusy=false;if(pointToken==cts)pointToken=null;try{cts.Dispose();}catch{}if(!disposed){pointRun.Enabled=true;pointStop.Visible=false;}}
        }

        void RenderPointStats(List<PointSummary> list){
            pointTable.SuspendLayout();
            try{
                pointTable.Rows.Clear();
                foreach(PointSummary p in list){
                    int index=pointTable.Rows.Add(
                        p.Point,p.Label,p.Unit,
                        p.Good.ToString("N0"),
                        p.Timeout.ToString("N0"),
                        p.OverLimit<0?"—":p.OverLimit.ToString("N0"),
                        p.Min.HasValue?p.Min.Value.ToString("0.###"):"—",
                        p.Max.HasValue?p.Max.Value.ToString("0.###"):"—",
                        p.Mean.HasValue?p.Mean.Value.ToString("0.###"):"—",
                        p.StdDev.HasValue?p.StdDev.Value.ToString("0.###"):"—",
                        p.AverageIntervalTicks>0?Duration(new TimeSpan(p.AverageIntervalTicks)):"—",
                        p.Lo.HasValue?p.Lo.Value.ToString("0.###"):"",
                        p.Hi.HasValue?p.Hi.Value.ToString("0.###"):"");
                    pointTable.Rows[index].Tag=p;
                    if(p.OverLimit>0)pointTable.Rows[index].Cells[5].Style.ForeColor=Color.Firebrick;
                }
            }finally{pointTable.ResumeLayout();}
        }
        PointSummary SelectedPointRow(){
            return pointTable.CurrentRow==null?null:pointTable.CurrentRow.Tag as PointSummary;
        }
        void SaveLimits(){
            try{
                var updated=new Dictionary<string,Limit>(StringComparer.Ordinal);
                foreach(DataGridViewRow row in pointTable.Rows){
                    PointSummary summary=row.Tag as PointSummary;
                    if(summary==null)continue;
                    string lo=(row.Cells[11].Value??"").ToString().Trim();
                    string hi=(row.Cells[12].Value??"").ToString().Trim();
                    var limit=new Limit();double value;
                    if(lo.Length>0){
                        if(!double.TryParse(lo,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value))throw new ArgumentException("测点 "+summary.Point+" 的下限不是数字");
                        limit.Lo=value;
                    }
                    if(hi.Length>0){
                        if(!double.TryParse(hi,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value))throw new ArgumentException("测点 "+summary.Point+" 的上限不是数字");
                        limit.Hi=value;
                    }
                    if(limit.Lo.HasValue&&limit.Hi.HasValue&&limit.Lo.Value>=limit.Hi.Value)throw new ArgumentException("测点 "+summary.Point+" 的下限必须小于上限");
                    if(limit.Active)updated[summary.Point]=limit;
                }
                LimitStore.Save(appRoot,updated);
                limits.Clear();foreach(var pair in updated)limits[pair.Key]=pair.Value;
                pointStatus.Text="阈值已保存到 "+LimitStore.PathFor(appRoot)+" · 正在重算越限";
                RunPointStats();
            }catch(Exception ex){pointStatus.Text="保存阈值失败："+ex.Message;}
        }
        void ApplySession(SessionSummary summary){
            if(summary==null||disposed)return;
            following=false;
            DateTime fromValue=TruncateToDisplayedSecond(summary.FromUtc.ToLocalTime());
            DateTime toValue=TruncateToDisplayedSecond(summary.ToUtc.ToLocalTime()).AddSeconds(1);
            if(fromValue<from.MinDate)fromValue=from.MinDate;
            if(toValue>to.MaxDate)toValue=to.MaxDate;
            if(toValue<=fromValue)toValue=fromValue.AddSeconds(1);
            from.Value=fromValue;to.Value=toValue;
            recordTabs.SelectedIndex=0;
            pageIndex=0;appliedNav=NavEarliest;ApplyNav(NavEarliest,default(DateTime));
            if(Visible)StartQuery();else MarkDirty();
            feedback.Text="已按实验段「"+SessionLabel(summary)+"」设置时间范围。";
        }
        void ApplyPoint(PointSummary summary){
            if(summary==null||disposed)return;
            selectedPoints.Clear();selectedPoints.Add(summary.Point);
            selectedDevices.Clear();
            foreach(Point point in allPoints)if(point.name==summary.Point)selectedDevices.Add(point.binding.device);
            if(selectedDevices.Count==0)foreach(string device in allPoints.Select(x=>x.binding.device).Distinct())selectedDevices.Add(device);
            RebuildVisibleDevices();RebuildVisiblePoints();
            recordTabs.SelectedIndex=0;
            if(Visible)StartQuery();else MarkDirty();
            feedback.Text="已只保留测点 "+summary.Point+"（"+summary.Label+"）。";
        }
        /// <summary>当前界面上的筛选条件(源/时间/测点),用于跨页面交接。条件不完整时返回 null。</summary>
        public HistorySelection CurrentSelection(){
            try{
                HistoryFilter filter=MakeFilter();
                return new HistorySelection{Source=filter.Source,FromUtc=filter.FromUtc,ToUtc=filter.ToUtc,Points=filter.Points};
            }catch{return null;}
        }
        /// <summary>应用别的页面交接过来的条件:改源/时间/测点后定位到范围起点。</summary>
        public void ApplySelection(HistorySelection selection){
            if(selection==null||disposed)return;
            bool previous=settingSource;settingSource=true;
            try{int index=source.Items.IndexOf(selection.Source);if(index>=0)source.SelectedItem=selection.Source;}finally{settingSource=previous;}
            following=false;explicitSource=true;
            selectedPoints.Clear();selectedDevices.Clear();
            if(selection.Points!=null)foreach(string name in selection.Points){selectedPoints.Add(name);foreach(Point point in allPoints)if(point.name==name)selectedDevices.Add(point.binding.device);}
            if(selectedDevices.Count==0)foreach(string device in allPoints.Select(x=>x.binding.device).Distinct())selectedDevices.Add(device);
            RebuildVisibleDevices();RebuildVisiblePoints();
            DateTime fromValue=TruncateToDisplayedSecond(selection.FromUtc.ToLocalTime());
            DateTime toValue=TruncateToDisplayedSecond(selection.ToUtc.ToLocalTime());
            if(fromValue<from.MinDate)fromValue=from.MinDate;
            if(toValue>to.MaxDate)toValue=to.MaxDate;
            if(toValue<=fromValue)toValue=fromValue.AddSeconds(1);
            from.Value=fromValue;to.Value=toValue;
            recordTabs.SelectedIndex=0;
            pageIndex=0;appliedNav=NavEarliest;ApplyNav(NavEarliest,default(DateTime));
            if(Visible)StartQuery();else MarkDirty();
            feedback.Text="已按趋势分析页的可见区间定位；跟随已关闭。";
        }
        /// <summary>宽表列的一次性全选/清空。只影响显示列,不改变查询语义。</summary>
        void SetAllColumns(bool visible){
            foreach(Point point in allPoints)columnVisible[point.name]=visible;
            if(!filterDirty){
                if(queryBusy)pendingViewQuery=true;
                else{Task<bool> ignored=ApplyAndQuery();}
            }
        }
        void WireEvents(){
            query.Click+=async(s,e)=>{following=false;await NavTo(NavNewest,default(DateTime));};
            follow.Click+=async(s,e)=>{ResetPaging();pageIndex=0;appliedNav=NavNewest;pendingAnchor=null;SetToNow();MarkDirty();following=true;await RunQuery();};
            oldest.Click+=async(s,e)=>await NavTo(NavEarliest,default(DateTime));
            next.Click+=async(s,e)=>await NavTo(NavOlder,default(DateTime));
            newer.Click+=async(s,e)=>await NavTo(NavNewer,default(DateTime));
            latest.Click+=async(s,e)=>await NavTo(NavNewest,default(DateTime));
            jumpStart.Click+=async(s,e)=>await NavTo(NavEarliest,default(DateTime));
            jumpTo.Click+=async(s,e)=>{
                DateTime at=TruncateToDisplayedSecond(anchorTime.Value);
                if(at<TruncateToDisplayedSecond(from.Value)||at>TruncateToDisplayedSecond(to.Value)){feedback.Text="定位时刻必须落在所选时间范围之内。";return;}
                if(!wideView){feedback.Text="“跳到该时刻”按采集轮定位，请先勾选“按采集轮宽表显示”。";return;}
                await NavTo(NavAtTime,LocalDisplaySecondToUtc(at));};
            pageSizeBox.SelectedIndexChanged+=(s,e)=>{int n=pageSizeBox.SelectedIndex==1?500:200;if(n==pageSize)return;pageSize=n;pageIndex=0;ResetPaging();if(filterDirty){feedback.Text="每页轮数已改为 "+n+"；点击“查询记录”后生效。";}else if(!queryBusy){Task<bool> ignored=RunQuery();}};
            stopQuery.Click+=(s,e)=>{if(queryToken!=null)try{queryToken.Cancel();}catch{}};
            source.SelectedIndexChanged+=(s,e)=>{if(source.SelectedItem==null)return;if(!settingSource){explicitSource=true;following=false;offset=0;MarkDirty();}LoadSourcePartitionSetting();RefreshStorageStatus();RefreshInventory(false);};
            devices.ItemCheck+=(s,e)=>{if(rebuildingLists)return;string item=devices.Items[e.Index] as string;bool check=e.NewValue==CheckState.Checked;BeginInvoke((Action)(()=>{if(check)selectedDevices.Add(item);else selectedDevices.Remove(item);offset=0;following=false;MarkDirty();RebuildVisibleDevices();RebuildVisiblePoints();}));};channels.ItemCheck+=(s,e)=>{if(rebuildingLists)return;string item=channels.Items[e.Index] as string;string point=item.Split(new[]{" · "},StringSplitOptions.None)[0];bool check=e.NewValue==CheckState.Checked;BeginInvoke((Action)(()=>{if(check)selectedPoints.Add(point);else selectedPoints.Remove(point);offset=0;following=false;MarkDirty();RebuildVisiblePoints();}));};
            from.ValueChanged+=(s,e)=>{if(!updatingFollowTime){following=false;MarkDirty();}};to.ValueChanged+=(s,e)=>{if(!updatingFollowTime){following=false;MarkDirty();}};deviceSearch.TextChanged+=(s,e)=>RebuildVisibleDevices();pointSearch.TextChanged+=(s,e)=>RebuildVisiblePoints();
            exportMenu.Items.Add("导出为 Excel 工作簿…",null,async(s,e)=>await ExportAsync("xlsx"));exportMenu.Items.Add("导出为 CSV…",null,async(s,e)=>await ExportAsync("csv"));export.Click+=(s,e)=>exportMenu.Show(export,new System.Drawing.Point(0,export.Height));cancel.Click+=(s,e)=>{if(exportCancel!=null)exportCancel.Cancel();};
            applyNext.Click+=(s,e)=>ApplyPartition(false);applyNow.Click+=(s,e)=>ApplyPartition(true);
            // 列选择弹出面板:勾选状态只影响宽表显示列,不改变查询语义。
            // 列选择弹出面板:勾选状态只影响宽表显示列,不改变查询语义。
            columnPick.Click+=(s,e)=>{
                var list=new CheckedListBox{Width=250,Height=340,CheckOnClick=true,IntegralHeight=false,BorderStyle=BorderStyle.None,BackColor=Color.White};
                foreach(Point p in allPoints)list.Items.Add(p.name+" · "+PointCatalog.Get(p.name).Label,ColumnVisible(p.name));
                Action refresh=()=>{try{list.BeginInvoke((Action)(async()=>{if(!filterDirty){if(queryBusy)pendingViewQuery=true;else await ApplyAndQuery();}}));}catch{}};
                bool bulk=false;
                list.ItemCheck+=(ls,le)=>{if(bulk)return;string n=list.Items[le.Index].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];columnVisible[n]=le.NewValue==CheckState.Checked;refresh();};
                // 一键全选/清空:40 个测点逐个点太费事。用下拉菜单项而不是自绘面板,避免控件宿主不渲染。
                // bulk 期间屏蔽逐项回调,否则一次全选会排队几十次重查。
                Action<bool> applyAll=visible=>{bulk=true;try{SetAllColumns(visible);list.BeginUpdate();for(int i=0;i<list.Items.Count;i++)list.SetItemChecked(i,visible);list.EndUpdate();}finally{bulk=false;}};
                var selectAll=new ToolStripMenuItem("全选（勾选全部测点列）");
                var selectNone=new ToolStripMenuItem("清空（只留时间与采集轮）");
                selectAll.Click+=(a,b)=>applyAll(true);
                selectNone.Click+=(a,b)=>applyAll(false);
                var drop=new ToolStripDropDown{AutoSize=true};
                drop.Items.Add(selectAll);drop.Items.Add(selectNone);drop.Items.Add(new ToolStripSeparator());
                var listHost=new ToolStripControlHost(list){Margin=Padding.Empty,Padding=new Padding(2),AutoSize=false,Size=new Size(256,348)};
                drop.Items.Add(listHost);
                columnDrop=drop;
                drop.Show(columnPick,new System.Drawing.Point(0,columnPick.Height));
            };
            timer.Tick+=(s,e)=>FollowTick();
            countTimer.Tick+=(s,e)=>{countTimer.Stop();StartCount();};
            // —— 实验段 / 测点统计 / 跨页跳转 ——
            sessionRun.Click+=(s,e)=>RunSessionStats();
            sessionStop.Click+=(s,e)=>{if(sessionToken!=null)try{sessionToken.Cancel();}catch{}};
            pointRun.Click+=(s,e)=>RunPointStats();
            pointStop.Click+=(s,e)=>{if(pointToken!=null)try{pointToken.Cancel();}catch{}};
            limitsSave.Click+=(s,e)=>SaveLimits();
            pointLocate.Click+=(s,e)=>{PointSummary summary=SelectedPointRow();if(summary==null){pointStatus.Text="先在表里选中一个测点。";return;}ApplyPoint(summary);};
            openTrends.Click+=(s,e)=>{HistorySelection selection=CurrentSelection();if(selection==null){feedback.Text="筛选条件不完整，先补全时间与测点再跳转。";return;}Action<HistorySelection> handler=OpenTrendsRequested;if(handler==null){feedback.Text="当前窗口不支持跳转到趋势页。";return;}handler(selection);};
        }
        static Label MakeLabel(string text){return new Label{Text=text,AutoSize=true,Margin=new Padding(4,7,4,3),ForeColor=Color.FromArgb(88,105,125)};}
        static Label FixedLabel(string text,ContentAlignment align){return new Label{Text=text,AutoSize=false,AutoEllipsis=true,Dock=DockStyle.Fill,TextAlign=align,Margin=Padding.Empty,ForeColor=Color.FromArgb(88,105,125)};}
        /// <summary>外框尺寸只在真正需要时改动,并阻止 Resize → 改尺寸 → Resize 的自激循环。</summary>
        void ResizeOuter(){
            if(layoutGuard||disposed||outerLayout==null)return;
            Control viewport=outerLayout.Parent;if(viewport==null||viewport.IsDisposed)return;
            layoutGuard=true;
            try{
                int width=Math.Max(200,viewport.ClientSize.Width);
                int floor=680;
                if(storageBody.Visible)floor+=storageBody.GetPreferredSize(new Size(Math.Max(200,width-8),0)).Height+8;
                int height=Math.Max(viewport.ClientSize.Height,floor);
                if(outerLayout.Width!=width)outerLayout.Width=width;
                if(outerLayout.Height!=height)outerLayout.Height=height;
            }finally{layoutGuard=false;}
        }
        void RebuildVisibleDevices(){if(disposed)return;rebuildingLists=true;try{string q=deviceSearch.Text.Trim();devices.BeginUpdate();devices.Items.Clear();foreach(string d in allPoints.Select(p=>p.binding.device).Distinct().Where(x=>x.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)){int i=devices.Items.Add(d);devices.SetItemChecked(i,selectedDevices.Contains(d));}devices.EndUpdate();}finally{rebuildingLists=false;}}
        void RebuildVisiblePoints(){if(disposed)return;rebuildingLists=true;try{string q=pointSearch.Text.Trim();channels.BeginUpdate();channels.Items.Clear();foreach(Point p in allPoints){string dev=p.binding.device,name=p.name+" · "+PointCatalog.Get(p.name).Label;if(selectedDevices.Contains(dev)&&name.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0){int i=channels.Items.Add(name);channels.SetItemChecked(i,selectedPoints.Contains(p.name));}}channels.EndUpdate();}finally{rebuildingLists=false;}}
        static void StyleGrid(DataGridView v){v.ColumnHeadersHeight=56;v.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;v.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;v.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(232,241,250);v.ColumnHeadersDefaultCellStyle.ForeColor=Color.FromArgb(29,58,89);v.ColumnHeadersDefaultCellStyle.Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold);v.ColumnHeadersDefaultCellStyle.SelectionBackColor=Color.FromArgb(232,241,250);v.DefaultCellStyle.SelectionBackColor=Color.FromArgb(211,231,250);v.DefaultCellStyle.SelectionForeColor=Color.FromArgb(25,52,82);v.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(248,251,254);}
        void PositionExport(Control parent){progress.Location=new System.Drawing.Point(Math.Max(10,parent.ClientSize.Width-450),10);exportStatus.Location=new System.Drawing.Point(Math.Max(10,parent.ClientSize.Width-315),11);cancel.Location=new System.Drawing.Point(Math.Max(10,parent.ClientSize.Width-214),5);export.Location=new System.Drawing.Point(Math.Max(10,parent.ClientSize.Width-138),4);}
        public void EnterPage(string active){if(!String.IsNullOrEmpty(active))activeSource=active;if(!explicitSource&&following)SetSource(activeSource);if(!entered){entered=true;SetToNow();following=true;MarkDirty();}if(Visible){timer.Start();RefreshInventory(true);RefreshStorageStatus();if(following){SetToNow();StartQuery();}}}
        public void SetActiveSource(string value){if(String.IsNullOrEmpty(value))return;activeSource=value;if(explicitSource||!following)return;SetSource(value);offset=0;SetToNow();following=true;MarkDirty();if(Visible)StartQuery();}
        void OnVisibleChanged(object s,EventArgs e){if(Visible){if(entered){timer.Start();RefreshInventory(true);RefreshStorageStatus();if(following){SetToNow();StartQuery();}}}else timer.Stop();}
        void SetSource(string value){int i=source.Items.IndexOf(value);if(i>=0&&source.SelectedIndex!=i){settingSource=true;try{source.SelectedItem=value;}finally{settingSource=false;}}}
        static DateTime TruncateToDisplayedSecond(DateTime value){return new DateTime(value.Year,value.Month,value.Day,value.Hour,value.Minute,value.Second,DateTimeKind.Local);}
        internal static DateTime LocalDisplaySecondToUtc(DateTime value){return TruncateToDisplayedSecond(value).ToUniversalTime();}
        internal static DateTime NextLocalSecond(DateTime value){DateTime second=TruncateToDisplayedSecond(value);return second.AddSeconds(1);}
        void SetToNow(){DateTime upper=NextLocalSecond(DateTime.Now);if(upper>to.MaxDate)upper=TruncateToDisplayedSecond(to.MaxDate);updatingFollowTime=true;try{to.Value=upper;}finally{updatingFollowTime=false;}}
        void StartQuery(){if(queryBusy){if(following&&Visible)pendingFollowQuery=true;return;}Task<bool> ignored=RunQuery();}
        void MarkDirty(){if(disposed)return;generation++;filterDirty=true;feedback.Text="筛选条件已更改；点击“查询记录”后应用。";}
        string Signature(HistoryFilter f){return f.Source+"|"+f.FromUtc.Ticks+"|"+f.ToUtc.Ticks+"|"+String.Join(",",f.Devices.OrderBy(x=>x))+"|"+String.Join(",",f.Points.OrderBy(x=>x));}
        HistoryFilter MakeFilter(){DateTime a=TruncateToDisplayedSecond(from.Value),b=TruncateToDisplayedSecond(to.Value);if(b<=a)throw new InvalidOperationException("结束时间必须晚于开始时间（结束时刻不包含）。");string[] ds=selectedDevices.ToArray();string[] ps=allPoints.Where(p=>selectedDevices.Contains(p.binding.device)&&selectedPoints.Contains(p.name)).Select(p=>p.name).ToArray();if(ds.Length==0||ps.Length==0)throw new InvalidOperationException("请至少选择一台设备和一个测点。");return new HistoryFilter{FromUtc=a.ToUniversalTime(),ToUtc=b.ToUniversalTime(),Source=(string)source.SelectedItem,Devices=ds,Points=ps};}
        async Task<bool> ApplyAndQuery(){return await RunQuery();}
        sealed class HistoryLoad{public List<List<StoredObservation>> Rounds;public List<StoredObservation> Rows;public int Units;public bool HasMore,HasOlder,HasNewer;public ExperimentHistory.RoundCursor Next;public DateTime FromUtc,ToUtc;}
        void ResetPaging(){offset=0;}
        // —— 分页导航:页码只在从最新端连续前进时累加,锚点/最早端进入不显示页码 ——
        int PageIndexFor(int kind){
            switch(kind){
                case NavNewest:return 1;
                case NavOlder:return pageIndex>0?pageIndex+1:0;
                case NavNewer:return pageIndex>1?pageIndex-1:0;
                default:return 0;
            }
        }
        /// <summary>把一次导航意图翻译成"这一页的时间锚点 + 取数方向";锚点在执行这一刻才计算。</summary>
        void ApplyNav(int kind,DateTime at){
            following=false;
            if(!wideView){
                pendingAnchor=null;
                if(kind==NavOlder)offset+=pageSize;
                else if(kind==NavNewer)offset=Math.Max(0,offset-pageSize);
                else offset=0;
                return;
            }
            offset=0;
            switch(kind){
                case NavNewest:pendingAnchor=null;pendingDescending=true;break;
                case NavOlder:pendingAnchor=pageFrom.HasValue?(DateTime?)pageFrom.Value.AddTicks(-1):null;pendingDescending=true;break;
                case NavNewer:pendingAnchor=pageTo.HasValue?(DateTime?)pageTo.Value.AddTicks(1):null;pendingDescending=false;break;
                case NavEarliest:pendingAnchor=LocalDisplaySecondToUtc(TruncateToDisplayedSecond(from.Value));pendingDescending=false;break;
                default:pendingAnchor=at;pendingDescending=false;break;
            }
        }
        async Task NavTo(int kind,DateTime at){
            if(queryBusy){queuedNav=kind;queuedAt=at;feedback.Text="查询进行中，已记下这次定位，本次查询结束后自动执行。";return;}
            queuedNav=-1;
            int index=PageIndexFor(kind);appliedNav=kind;pageIndex=index;
            ApplyNav(kind,at);
            await RunQuery();
        }
        HistoryLoad LoadPreview(HistoryFilter f,bool roundView,CancellationToken token,DateTime? anchor,bool descending,int nav){
         if(roundView){
          ExperimentHistory.RoundPage page=anchor.HasValue
              ?ExperimentHistory.QueryRoundWindowFrom(dataRoot,f,anchor.Value,pageSize,!descending,token)
              :ExperimentHistory.QueryRoundWindow(dataRoot,f,null,pageSize,descending,token);
          // 方向标记由"这一次是怎么取出来的"推出,不额外探两个方向:
          // 最新端=没有更新的;更早=一定还有更新的(就是从更新的一页翻过来的);更新=一定还有更早的;
          // 最早端=没有更早的;跳到该时刻只有"还有更早的"需要真探一次。
          bool older=false,newer=false;
          switch(nav){
           case NavOlder:older=page.HasMore;newer=true;break;
           case NavNewer:older=true;newer=page.HasMore;break;
           case NavEarliest:older=false;newer=page.HasMore;break;
           case NavAtTime:newer=page.HasMore;older=page.Rounds.Count>0&&page.FromUtc>f.FromUtc&&ExperimentHistory.HasRoundBefore(dataRoot,f,page.FromUtc);break;
           default:older=page.HasMore;newer=false;break;
          }
          return new HistoryLoad{Rounds=page.Rounds,Rows=page.Rounds.SelectMany(x=>x).ToList(),Units=page.Rounds.Count,HasMore=page.HasMore,HasOlder=older,HasNewer=newer,Next=page.Next,FromUtc=page.FromUtc,ToUtc=page.ToUtc};
         }
         List<StoredObservation> rows=ExperimentHistory.QueryPage(dataRoot,f,offset,pageSize+1);
         bool more=rows.Count>pageSize;if(more)rows.RemoveRange(pageSize,rows.Count-pageSize);
         HistoryLoad load=new HistoryLoad{Rows=rows,Units=rows.Count,HasMore=more,HasOlder=offset>0,HasNewer=more};
         if(rows.Count>0){load.FromUtc=rows.Min(r=>r.Utc);load.ToUtc=rows.Max(r=>r.Utc);}
         return load;
        }
        async Task<bool> RunQuery(){
         if(queryBusy||disposed)return false;if(!Visible)return false;
         queryBusy=true;int token=++generation;query.Enabled=false;feedback.Text="正在查询记录…";stopQuery.Visible=true;
         var watch=System.Diagnostics.Stopwatch.StartNew();
         queryToken=new CancellationTokenSource();CancellationTokenSource cts=queryToken;CancellationToken cancel=cts.Token;
         try{
          HistoryFilter f=MakeFilter();string signature=Signature(f);bool roundView=wideView;
          DateTime? anchor=pendingAnchor;bool descending=pendingDescending;int nav=appliedNav;
          HistoryLoad data=await Task.Run(()=>LoadPreview(f,roundView,cancel,anchor,descending,nav));
          if(disposed||token!=generation||(string)source.SelectedItem!=f.Source||roundView!=wideView)return false;
          watch.Stop();lastQueryMs=watch.ElapsedMilliseconds;
          lastLoad=data;summaryFilter=f;summaryRoundView=roundView;summaryUnits=data.Units;
          summaryValid=0;summaryTimeout=0;
          if(roundView)foreach(List<StoredObservation> group in data.Rounds){bool good=false;foreach(StoredObservation r in group)if(r.Quality=="good"){good=true;break;}if(good)summaryValid++;else summaryTimeout++;}
          RenderRows();
          appliedGeneration=token;previewSignature=signature;filterDirty=false;
          hasOlder=data.HasOlder;hasNewer=data.HasNewer;
          pageFrom=roundView&&data.Units>0?(DateTime?)data.FromUtc:null;
          pageTo=roundView&&data.Units>0?(DateTime?)data.ToUtc:null;
          RenderSummary();RenderFeedback(data);RenderPageBar();
          ScheduleCount(f,signature,roundView);
          return true;}
         catch(OperationCanceledException){if(!disposed&&token==generation)feedback.Text="查询已取消；筛选条件已保留。";return false;}
         catch(Exception ex){if(!disposed&&token==generation){grid.Rows.Clear();feedback.Text="查询失败："+ex.Message+"；请重试。当前筛选条件已保留。";summary.Text="记录查询不可用";}return false;}
         finally{queryBusy=false;if(queryToken==cts)queryToken=null;try{cts.Dispose();}catch{}stopQuery.Visible=false;bool rerun=(pendingFollowQuery&&following)||(pendingViewQuery&&!filterDirty);pendingFollowQuery=pendingViewQuery=false;
          int queued=queuedNav;DateTime queuedAtValue=queuedAt;queuedNav=-1;
          if(!disposed){query.Enabled=true;
           if(queued>=0&&Visible)try{BeginInvoke((Action)(async()=>{await NavTo(queued,queuedAtValue);}));}catch{}
           else if(rerun&&Visible)try{BeginInvoke((Action)StartQuery);}catch{StartQuery();}}}}
        void RenderRows(){
         if(lastLoad==null||summaryFilter==null)return;
         HistoryLoad data=lastLoad;HistoryFilter filter=summaryFilter;
         // 填表期间只挂起布局,不去动 Visible:Visible 读的是“父容器可见性”,在别的子标签里渲染时会被误判成隐藏并永久留在隐藏状态。
         grid.SuspendLayout();
         try{
          grid.Rows.Clear();
          List<StoredObservation> rows=data.Rows??new List<StoredObservation>();
          if(wideView)BuildWidePreview(data.Rounds??new List<List<StoredObservation>>(),filter);else BuildDetailPreview(rows);
          if(emptyHint!=null)emptyHint.Visible=rows.Count==0;
         }finally{grid.ResumeLayout(false);if(!grid.Visible)grid.Visible=true;}
        }
        void RenderSummary(){
         if(summaryFilter==null)return;
         string src=summaryFilter.Source=="serial"?"实机":"模拟";
         string total=countTotal>=0?countTotal.ToString("N0"):(following?"范围跟随中":"统计中…");
         string text=(following?"● 跟随最新":"○ 历史查询")+"　·　"+src+"　·　筛选共 "+total+(summaryRoundView?" 轮":" 条")+"　·　本页 "+summaryUnits.ToString("N0")+(summaryRoundView?" 轮":" 条");
         if(summaryRoundView)text+="　·　有效轮 "+summaryValid+" / 超时轮 "+summaryTimeout;
         summary.Text=text;
        }
        /// <summary>分页条:页码只反映"从最新端连续翻了多远";两个方向按钮直接用"还有没有"驱动,不靠点击意图推。</summary>
        void RenderPageBar(){
         if(summaryFilter==null)return;
         try{
          bool spanOk=lastLoad!=null&&summaryUnits>0&&lastLoad.FromUtc>DateTime.MinValue&&lastLoad.FromUtc<=lastLoad.ToUtc;
          double minutes=spanOk?(lastLoad.ToUtc-lastLoad.FromUtc).TotalMinutes:0;
          string span=spanOk?lastLoad.FromUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" – "+lastLoad.ToUtc.ToLocalTime().ToString("HH:mm:ss")+"（"+minutes.ToString("0")+" 分钟）":"—";
          string tail=!summaryRoundView?"":(hasOlder?"还有更早的数据":"已到范围最早")+(hasNewer?" · 还有更新的数据":"");
          coverage.Text=summaryUnits<=0?"本页没有记录":"覆盖 "+span+(tail.Length>0?"　·　"+tail:"");
          pageInfo.Text=PageLabelText();
          oldest.Enabled=summaryRoundView;latest.Enabled=summaryRoundView;jumpStart.Enabled=summaryRoundView;jumpTo.Enabled=summaryRoundView;
          next.Text="‹ 更早";newer.Text="更新 ›";
          // 数量与单位由 pageInfo 统一表达,按钮只说方向,避免在 960 宽的窄窗里被截断。
          next.Enabled=hasOlder;newer.Enabled=hasNewer;
         }catch(Exception ex){feedback.Text="[分页条] "+ex.GetType().Name+": "+ex.Message;}
        }
        string PageLabelText(){
         if(!summaryRoundView)return summaryUnits<=0?"无记录":"第 "+(offset+1)+" – "+(offset+summaryUnits)+" 条";
         string units="本页 "+summaryUnits+" 轮";
         if(pageIndex>0)return "第 "+pageIndex+" 页　·　"+units;
         if(!hasOlder)return "范围内最早　·　"+units;
         return "更早的一段　·　"+units;
        }
        void RenderFeedback(HistoryLoad data){
         if(summaryUnits==0){feedback.Text="当前筛选范围没有记录。";return;}
         string span=data!=null&&data.FromUtc<=data.ToUtc?data.FromUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" ~ "+data.ToUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"";
         string hidden=hideTimeoutRounds&&summaryRoundView?"　·　已隐藏仅超时的轮次":"";
         string columnHint=noPointColumns&&wideView?"　·　未勾选任何测点列（点「列选择」→ 全选）":"";
         feedback.Text=(wideView?"当前页显示完整采集轮；":"当前页显示逐测点明细；")+"本页时间 "+span+"　·　耗时 "+lastQueryMs+" ms"+hidden+columnHint+"　·　（本地时间；结束时刻不包含）";
        }
        /// <summary>范围总量在后台统计:页面稳定 1.5 s 后才做,避免全量 GROUP BY 和翻页查询抢同一块磁盘。</summary>
        void ScheduleCount(HistoryFilter f,string signature,bool roundView){
            countFilter=f;countRoundView=roundView;
            if(following)return;
            if(signature==countSignature&&countTotal>=0)return;
            countTimer.Stop();countTimer.Start();
        }
        async void StartCount(){
         if(disposed||following||countBusy||countFilter==null)return;
         HistoryFilter f=countFilter;bool roundView=countRoundView;string signature=Signature(f);
         if(countTotal>=0&&signature==countSignature)return;
         countBusy=true;countTotal=-1;countSignature=signature;
         try{long total=await Task.Run(()=>roundView?ExperimentHistory.CountRounds(dataRoot,f):ExperimentHistory.Count(dataRoot,f));if(disposed)return;countTotal=total;RenderSummary();}
         catch{}finally{countBusy=false;}
        }
        void BuildDetailPreview(List<StoredObservation> rows){grid.Columns.Clear();string[] headers={"采样时间（本地）","测点","名称","设备","工程值","原始值 HEX","单位","质量"};int[] widths={165,130,170,100,160,150,100,110};for(int i=0;i<headers.Length;i++)grid.Columns.Add(headers[i],headers[i]);for(int i=0;i<widths.Length;i++)grid.Columns[i].Width=widths[i];foreach(StoredObservation r in rows){PointInfo rinfo=PointCatalog.Get(r.Point);string runit=String.IsNullOrEmpty(rinfo.Unit)?r.Unit:rinfo.Unit;string engineering=r.Number.HasValue?r.Number.Value.ToString("G17",System.Globalization.CultureInfo.InvariantCulture):r.Value;string raw=r.RawNumber.HasValue?r.RawNumber.Value.ToString("G17",System.Globalization.CultureInfo.InvariantCulture):r.Payload;int rowIndex=grid.Rows.Add(r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),r.Point,rinfo.Label,r.Device,engineering,raw,runit,QualityLabel(r.Quality));Limit detailLimit=LimitFor(r.Point);if(detailLimit!=null&&r.Number.HasValue&&detailLimit.Violates(r.Number.Value)){DataGridViewCell cell=grid.Rows[rowIndex].Cells[4];cell.Style.BackColor=Color.FromArgb(255,235,235);cell.Style.ForeColor=Color.Firebrick;}}}
        void BuildWidePreview(List<List<StoredObservation>> rounds,HistoryFilter filter){grid.Columns.Clear();grid.Columns.Add("time","轮次开始时间（本地）");grid.Columns.Add("round","采集轮");var points=allPoints.Select((p,index)=>new{Point=p,Index=index}).Where(x=>filter.Points.Contains(x.Point.name)&&ColumnVisible(x.Point.name)).OrderBy(x=>DeviceRank(x.Point.binding.device)).ThenBy(x=>x.Index).Select(x=>x.Point).ToList();
            // 列选择把全部测点都关掉时回退为显示全部筛选测点,避免空表困惑。
            if(points.Count==0&&columnVisible.Count==0)points=allPoints.Select((p,index)=>new{Point=p,Index=index}).Where(x=>filter.Points.Contains(x.Point.name)).OrderBy(x=>DeviceRank(x.Point.binding.device)).ThenBy(x=>x.Index).Select(x=>x.Point).ToList();

            noPointColumns=points.Count==0;
            foreach(Point p in points){PointInfo info=PointCatalog.Get(p.name);string unit=info.Unit??"";grid.Columns.Add(p.name,info.Label+(String.IsNullOrEmpty(unit)?"":"（"+unit+"）"));}foreach(DataGridViewColumn c in grid.Columns)c.Width=150;
            foreach(var group in rounds.OrderByDescending(g=>g.Min(r=>r.RoundStartedUtc??r.Utc))){if(hideTimeoutRounds&&!group.Any(r=>r.Quality=="good"))continue;var byPoint=group.GroupBy(x=>x.Point).ToDictionary(g=>g.Key,g=>g.OrderBy(x=>x.Utc).First());StoredObservation sample=group.First();DateTime start=sample.RoundStartedUtc??group.Min(x=>x.Utc);var cells=new List<object>{start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),sample.Round};var over=new List<int>();for(int pi=0;pi<points.Count;pi++){Point p=points[pi];StoredObservation o;if(!byPoint.TryGetValue(p.name,out o)){cells.Add("");continue;}cells.Add(o.Number.HasValue?((object)o.Number.Value.ToString("G17",System.Globalization.CultureInfo.InvariantCulture)):((o.Quality=="good"?"":QualityLabel(o.Quality)+"：")+o.Value));Limit limit=LimitFor(p.name);if(o.Number.HasValue&&limit!=null&&limit.Violates(o.Number.Value))over.Add(2+pi);}int wideRow=grid.Rows.Add(cells.ToArray());foreach(int column in over){DataGridViewCell cell=grid.Rows[wideRow].Cells[column];cell.Style.BackColor=Color.FromArgb(255,235,235);cell.Style.ForeColor=Color.Firebrick;}}}
        Limit LimitFor(string point){Limit limit;return limits.TryGetValue(point,out limit)&&limit!=null&&limit.Active?limit:null;}
        static int DeviceRank(string device){string[] order={"DS666","PLC","DDSU666","DJSF6682"};int i=Array.IndexOf(order,device);return i<0?Int32.MaxValue:i;}
        static string QualityLabel(string q){return q=="good"?"正常":q=="timeout"?"超时":q=="error"?"异常":q=="protocol_exception"?"协议异常":q=="decode_error"?"解码异常":String.IsNullOrEmpty(q)?"未知":q;}
        async Task ExportAsync(string format){if(exportCancel!=null)return;try{HistoryFilter f=MakeFilter();string sig=Signature(f);if(filterDirty||sig!=previewSignature){if(MessageBox.Show(this,"筛选条件尚未应用，是否先按当前条件查询并确认预览后导出？","确认导出范围",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;if(!await ApplyAndQuery())return;f=MakeFilter();sig=Signature(f);}if(sig!=previewSignature){throw new InvalidOperationException("当前预览与筛选条件不一致，请重新查询后导出。");}
          string destination=null;while(true){using(var d=new FolderBrowserDialog{Description="选择当前用户可写的导出目录。程序会先在临时目录生成，再复制到目标目录并原子发布。",ShowNewFolderButton=true}){if(!String.IsNullOrEmpty(destination))d.SelectedPath=destination;if(d.ShowDialog(this)!=DialogResult.OK)return;destination=d.SelectedPath;}try{ExportNaming.VerifyDestination(destination);ExportNaming.CheckDestinationSpace(destination);break;}catch(Exception ex){DialogResult retry=MessageBox.Show(this,ex.Message+"\r\n\r\n重新选择导出目录？筛选条件保持不变。","目标目录不可写",MessageBoxButtons.RetryCancel,MessageBoxIcon.Warning);if(retry!=DialogResult.Retry)return;}}
          exportCancel=new CancellationTokenSource();SetExporting(true);var progressReport=new Progress<ExportProgress>(p=>{if(!disposed){exportStatus.Visible=true;exportStatus.Text=p.Phase+" · "+p.CompletedRows.ToString("N0")+(p.TotalRows>0?" / "+p.TotalRows.ToString("N0"):"");}});ExportResult r=await ExperimentExport.ExportAsync(dataRoot,f,destination,format,progressReport,exportCancel.Token,Signature(f)==sessionSignature?sessionRows:null,Signature(f)==pointSignature?pointRows:null);if(!disposed){exportStatus.Visible=true;exportStatus.Text=(format=="xlsx"?"Excel":"CSV")+" · "+r.Rows.ToString("N0")+" 条完成";MessageBox.Show(this,"导出完成：\r\n"+r.OutputDirectory+"\r\n记录 "+r.Rows.ToString("N0")+" 条。","导出完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
          catch(OperationCanceledException){if(!disposed){exportStatus.Visible=true;exportStatus.Text="导出已取消";}}catch(Exception ex){if(!disposed)MessageBox.Show(this,"导出未完成，数据库和既有文件未覆盖。\r\n"+ex.Message,"导出失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{if(exportCancel!=null){exportCancel.Dispose();exportCancel=null;}if(!disposed)SetExporting(false);}}
        void SetExporting(bool busy){export.Enabled=!busy;query.Enabled=!busy;follow.Enabled=!busy;next.Enabled=!busy&&next.Enabled;cancel.Visible=busy;cancel.Enabled=busy;progress.Visible=busy;progress.Style=ProgressBarStyle.Marquee;}
        async void FollowTick(){if(disposed||!Visible)return;if(following)SetToNow();RefreshStorageStatus();if(DateTime.Now-lastInventory>TimeSpan.FromSeconds(15))RefreshInventory(false);if(!following)return;ExperimentStore st=engine.Store;long committed=st!=null&&st.Status!=null&&st.Status.Recording&&engine.ActiveSource==(string)source.SelectedItem?st.Committed:-1;if(filterDirty&&!queryBusy){lastCommitted=committed;await RunQuery();return;}long completed=engine.CompletedCaptures;if(committed!=lastCommitted||completed!=lastCompletedCaptures){lastCompletedCaptures=completed;lastCommitted=committed;await RunQuery();}}
        void LoadPartitionSetting(){string path=Path.Combine(appRoot,"partition-days.json");try{var s=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<PartitionSetting>(File.ReadAllText(path));if(s.SerialDays>=1&&s.SerialDays<=3650)serialPartitionDays=s.SerialDays;if(s.SimulationDays>=1&&s.SimulationDays<=3650)simulationPartitionDays=s.SimulationDays;if(s.Days>=1&&s.Days<=3650){if(serialPartitionDays==30)serialPartitionDays=s.Days;if(simulationPartitionDays==30)simulationPartitionDays=s.Days;}}catch{}LoadSourcePartitionSetting();partitionPreset.SelectedIndexChanged+=(s,e)=>partitionCustom.Enabled=partitionPreset.SelectedIndex==3;}
        void LoadSourcePartitionSetting(){partitionDays=(string)source.SelectedItem=="serial"?serialPartitionDays:simulationPartitionDays;partitionCustom.Value=partitionDays;partitionPreset.SelectedIndex=partitionDays==7?0:partitionDays==15?1:partitionDays==30?2:3;partitionCustom.Enabled=partitionPreset.SelectedIndex==3;}
        int SelectedDays(){return partitionPreset.SelectedIndex==0?7:partitionPreset.SelectedIndex==1?15:partitionPreset.SelectedIndex==2?30:(int)partitionCustom.Value;}
        void ApplyPartition(bool immediate){try{partitionDays=SelectedDays();string selected=(string)source.SelectedItem;engine.ConfigurePartition(selected,partitionDays,immediate);if(selected=="serial")serialPartitionDays=partitionDays;else simulationPartitionDays=partitionDays;SavePartitionSetting();RefreshStorageStatus();}catch(Exception ex){MessageBox.Show(this,ex.Message,"分库设置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        void SavePartitionSetting(){string path=Path.Combine(appRoot,"partition-days.json"),tmp=path+".partial";Directory.CreateDirectory(appRoot);File.WriteAllText(tmp,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new PartitionSetting{Days=partitionDays,SerialDays=serialPartitionDays,SimulationDays=simulationPartitionDays}));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
        void RefreshStorageStatus(){var st=engine.Store;string label="来源："+(source.SelectedItem as string=="serial"?"实机":"模拟")+"　·　分库："+partitionDays+" 天";if(st!=null&&st.Status!=null&&(string)source.SelectedItem==engine.ActiveSource){var s=st.Status;label+="　·　"+(s.Recording?"正在记录":"已停止")+"　·　已写入 "+s.Committed.ToString("N0")+" 条　·　队列 "+s.Pending+" 条 / "+(s.QueuedBytes/1024).ToString("N0")+" KiB　·　可用空间 "+(s.AvailableDiskBytes/1024/1024).ToString("N0")+" MiB";if(s.DiskWarning)label+="（磁盘空间偏低）";if(!String.IsNullOrEmpty(s.LastError))label+="　·　记录错误："+s.LastError;}storageStatus.Text=label+"　·　路径："+dataRoot;}
        sealed class InventoryEntry{public string Source,Path;}
        async void RefreshInventory(bool force){
            // A source change can occur on the live tab before the history page has a handle.
            // Defer that load to EnterPage; never leave a busy flag waiting for a missing UI callback.
            if(disposed||!IsHandleCreated||inventoryBusy||(!force&&DateTime.Now-lastInventory<TimeSpan.FromSeconds(15)))return;
            inventoryBusy=true;lastInventory=DateTime.Now;
            try{
                var entries=await Task.Run(()=>ExperimentHistory.Databases(dataRoot,"serial").Select(x=>new InventoryEntry{Source="serial",Path=x})
                    .Concat(ExperimentHistory.Databases(dataRoot,"simulation").Select(x=>new InventoryEntry{Source="simulation",Path=x}))
                    .OrderBy(x=>x.Source).ThenBy(x=>x.Path).ToList());
                if(disposed||!IsHandleCreated)return;
                string writingPath=null;bool writing=false;ExperimentStore active=engine.Store;
                if(active!=null){ExperimentStoreStatus status=active.Status;if(status!=null){writingPath=status.DatabasePath;writing=status.Recording;}}
                inventory.Rows.Clear();
                foreach(InventoryEntry entry in entries){FileInfo i=new FileInfo(entry.Path);bool isCurrent=!String.IsNullOrEmpty(writingPath)&&String.Equals(i.FullName,writingPath,StringComparison.OrdinalIgnoreCase);
                    inventory.Rows.Add(entry.Source=="serial"?"实机":"模拟",i.Name,(i.Length/1048576.0).ToString("N2"),isCurrent?(writing?"当前周期 · 正在写入":"当前周期 · 未在写入"):"历史/未在写入");}
                inventory.Rows.Add("",dataRoot,"","");
            }catch(Exception ex){if(!disposed&&IsHandleCreated)feedback.Text="数据库清单读取失败："+ex.Message;}
            finally{inventoryBusy=false;}
        }
        public sealed class PartitionSetting{public int Days{get;set;}public int SerialDays{get;set;}public int SimulationDays{get;set;}}
        void DisposePage(){if(disposed)return;disposed=true;generation++;timer.Stop();timer.Dispose();countTimer.Stop();countTimer.Dispose();if(exportCancel!=null)try{exportCancel.Cancel();}catch{} }
    }
}

















