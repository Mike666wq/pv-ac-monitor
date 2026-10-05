using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
namespace ExperimentMonitor {
    public sealed class CloudPage:UserControl {
        CloudConfiguration config;readonly CloudPublisher publisher;
        readonly TextBox endpoint=new TextBox{Width=520},alias=new TextBox{Width=320},token=new TextBox{Width=520,UseSystemPasswordChar=true};
        readonly CheckBox enabled=new CheckBox{Text="启用实验云连接",AutoSize=true},simulation=new CheckBox{Text="允许上传模拟数据（联调时使用）",AutoSize=true};
        readonly Label status=new Label{AutoSize=true,MaximumSize=new Size(1000,0)};
        readonly Label contact=new Label{AutoSize=true},lease=new Label{AutoSize=true},uploads=new Label{AutoSize=true},diagnostic=new Label{AutoSize=true,MaximumSize=new Size(1000,0)};
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};
        readonly MonitorEngine engine;
        public event EventHandler StatusChanged;
        public event Action<string> CloudStatusChanged;
        public CloudPublisherStatus CurrentStatus { get { return publisher.SnapshotStatus; } }
        public string CurrentStatusLabel { get {CloudPublisherStatus s=publisher.SnapshotStatus;return s.State+"；租约"+(s.LeaseValid?"有效":"无效")+"；已确认 "+s.Accepted+"，失败 "+s.Failed;} }
        public CloudPage(string root,MonitorEngine engine) {
            this.engine=engine;Dock=DockStyle.Fill;config=CloudConfiguration.Load(Path.Combine(root,"cloud-settings.json"));
            publisher=new CloudPublisher(()=>Volatile.Read(ref config));engine.ObservationReceived+=OnObservation;engine.ConnectionChanged+=OnConnectionChanged;
            // 双栏分组布局(对齐 BMS 云端连接页):左 设备身份+连接设置,右 连接状态。
            // 左右两栏顶边平齐:不再单独放大标题(选项卡已标明页面);
            // 外边距 14、栏间距 12,左右框的边线严格对齐。
            var shell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=new Padding(14,10,14,10),BackColor=UiTheme.Canvas};
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,56));shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));Controls.Add(shell);
            // 左栏:滚动视口 + 单列网格,分组 Dock=Top 撑满列宽(FlowLayoutPanel 里 AutoSize 会抵消拉伸)。
            var left=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty,BackColor=UiTheme.Canvas};
            var leftCol=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,BackColor=UiTheme.Canvas,Margin=Padding.Empty,Padding=new Padding(0,2,6,2)};
            leftCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            leftCol.RowCount=1;
            left.Controls.Add(leftCol);
            left.SizeChanged+=(s,e)=>{int w=left.ClientSize.Width-(left.VerticalScroll.Visible?17:0);if(w>50&&leftCol.Width!=w)leftCol.Width=w;};
            shell.Controls.Add(left,0,0);
            leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var identity=Section("设备身份");
            identity.Controls.Add(FieldLabel("独立数据源"));
            identity.Controls.Add(new Label{Text=config.DeviceId,AutoSize=true,ForeColor=UiTheme.Ink,Margin=new Padding(2,0,2,6)});
            identity.Controls.Add(FieldLabel("本地实验程序名称"));
            identity.Controls.Add(new RoundedInputHost(alias){Margin=new Padding(2,0,2,4)});
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(identity,0,leftCol.RowCount-1);
            var settings=Section("连接设置");
            settings.Controls.Add(enabled);
            settings.Controls.Add(FieldLabel("服务地址（HTTPS 域名根，与 BMS 使用同一云服务）"));
            settings.Controls.Add(new RoundedInputHost(endpoint){Margin=new Padding(2,0,2,4)});
            settings.Controls.Add(FieldLabel("实验设备令牌（留空保留已保存令牌）"));
            settings.Controls.Add(new RoundedInputHost(token){Margin=new Padding(2,0,2,4)});
            settings.Controls.Add(simulation);
            var save=new StyledActionButton{Text="保存并应用",Width=150,BackColor=UiTheme.Blue};
            var clear=new StyledActionButton{Text="清除保存的令牌",Width=150};
            var diagnose=new StyledActionButton{Text="连接诊断（只发心跳，不上传测点）",Width=280};
            var actions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=Padding.Empty,Padding=new Padding(0,4,0,0)};
            actions.Controls.Add(save);actions.Controls.Add(clear);actions.Controls.Add(diagnose);settings.Controls.Add(actions);
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(settings,0,leftCol.RowCount-1);
            var notes=new Label{Text="本地持续采集和 SQLite 记录独立于网络；远端仅在有效观看租约内接收最新快照。\n填写云服务地址和实验设备令牌并保存后，可运行连接诊断检查心跳与观看租约；网络或认证异常请查看诊断结果。\n心跳在线只说明云连接状态，不代表本地串口已连接或正在采集。模拟/实机来源分别标记，失败或未响应测点不会作为零值发送。",AutoSize=true,MaximumSize=new Size(700,0),ForeColor=UiTheme.Muted,Margin=new Padding(2,12,2,2)};
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(notes,0,leftCol.RowCount-1);
            // 说明与诊断文本的换行宽度跟随各自容器。
            left.SizeChanged+=(s,e)=>{notes.MaximumSize=new Size(Math.Max(260,left.ClientSize.Width-24),0);};
            var stateSection=Section("连接状态");
            var grid=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=2,RowCount=4,Dock=DockStyle.Top};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,86));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.Controls.Add(Caption("运行状态"),0,0);grid.Controls.Add(Value(status),1,0);
            grid.Controls.Add(Caption("最后心跳"),0,1);grid.Controls.Add(Value(contact),1,1);
            grid.Controls.Add(Caption("观看租约"),0,2);grid.Controls.Add(Value(lease),1,2);
            grid.Controls.Add(Caption("最后上传"),0,3);grid.Controls.Add(Value(uploads),1,3);
            stateSection.Controls.Add(grid);
            stateSection.Controls.Add(new Label{Text="诊断结果",AutoSize=true,ForeColor=UiTheme.Muted,Margin=new Padding(2,10,2,2)});
            stateSection.SizeChanged+=(s,e)=>{diagnostic.MaximumSize=new Size(Math.Max(220,stateSection.ClientSize.Width-40),0);};
            stateSection.Controls.Add(diagnostic);
            shell.Controls.Add(stateSection,1,0);
            diagnose.Click+=async(s,e)=>{diagnose.Enabled=false;diagnostic.Text="正在验证地址、网络、TLS、认证和云端接口响应…";try{var result=await publisher.DiagnoseAsync(CancellationToken.None);diagnostic.Text="诊断分类："+result.Category+"；"+result.Message+"（"+result.CompletedLocal+"）";}catch(Exception ex){diagnostic.Text="诊断失败："+ex.Message;}finally{diagnose.Enabled=true;}};
            enabled.Checked=config.Enabled;simulation.Checked=config.IncludeSimulation;endpoint.Text=config.Endpoint;alias.Text=config.Alias;
            save.Click+=(s,e)=> {try {var next=CloudConfiguration.Load(config.SavePath,config.DeviceId);next.Enabled=enabled.Checked;next.IncludeSimulation=simulation.Checked;next.Endpoint=endpoint.Text.Trim();next.Alias=alias.Text.Trim();if(token.Text.Length>0)next.Token=token.Text;next.Save();Volatile.Write(ref config,next);token.Clear();status.Text="已保存；等待下一次心跳更新租约";}catch(Exception ex){MessageBox.Show(this,ex.Message,"云连接设置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
            clear.Click+=(s,e)=>{try{var next=CloudConfiguration.Load(config.SavePath,config.DeviceId);next.Token="";next.Enabled=false;next.Save();Volatile.Write(ref config,next);enabled.Checked=false;token.Clear();status.Text="已清除令牌并关闭云连接";}catch(Exception ex){status.Text=ex.Message;}};
            publisher.StatusChanged+=(s,e)=>PublishStatusLabel();timer.Tick+=(s,e)=>RefreshStatus();timer.Start();RefreshStatus();
            Disposed+=(s,e)=>{timer.Stop();timer.Dispose();engine.ObservationReceived-=OnObservation;engine.ConnectionChanged-=OnConnectionChanged;publisher.Dispose();};
        }
        // 分组容器:圆角白底 + 小标题(与总览页分区同风格),宽度跟随父容器拉伸。
        // 直接 Add 到分组的子控件会被移入内部单列网格(AutoSize 行)并撑满整行。
        static RoundedPanel Section(string title) {
            var section=new RoundedPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(14,8,14,12),Margin=new Padding(2,4,2,10) };
            var grid=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,BackColor=UiTheme.Surface,Margin=Padding.Empty,Padding=Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            // RowCount 必须显式初始化:默认 0 时 (0,0) 隐式放置不推进计数,后续子控件会与标题同格。
            grid.RowCount=1;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading=new Label{Text=title,AutoSize=true,ForeColor=UiTheme.Ink,Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold),Margin=new Padding(0,0,0,6)};
            grid.Controls.Add(heading,0,0);
            section.Controls.Add(grid);
            section.ControlAdded+=(s,e)=>{ if(e.Control!=grid){ section.Controls.Remove(e.Control); e.Control.Dock=DockStyle.Top; grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.Controls.Add(e.Control,0,grid.RowCount-1); } };
            return section;
        }
        static Label FieldLabel(string text){return new Label{Text=text,AutoSize=true,ForeColor=UiTheme.Muted,Margin=new Padding(2,4,2,2)};}
        static Label Caption(string text){return new Label{Text=text,AutoSize=true,ForeColor=UiTheme.Muted,Margin=new Padding(2,3,2,3)};}
        static Label Value(Label label){label.AutoSize=true;label.ForeColor=UiTheme.Ink;label.Margin=new Padding(2,3,2,3);label.MaximumSize=new Size(280,0);return label;}
        void RefreshStatus(){CloudPublisherStatus snapshot=publisher.SnapshotStatus;status.Text=snapshot.State;contact.Text=snapshot.LastContactLocal;lease.Text=snapshot.LeaseValid?"有效至 "+snapshot.LeaseUntilLocal:"无有效租约";uploads.Text=snapshot.LastUploadLocal+" · 确认 "+snapshot.Accepted+" · 失败 "+snapshot.Failed+" · 待发设备 "+snapshot.QueuedDevices+" · 测点 "+snapshot.QueuedPoints;EventHandler h=StatusChanged;if(h!=null)h(this,EventArgs.Empty);PublishStatusLabel();}
        void PublishStatusLabel(){Action<string> h=CloudStatusChanged;if(h==null)return;string label=CurrentStatusLabel;try{if(IsHandleCreated&&InvokeRequired)BeginInvoke((Action)(()=>h(label)));else h(label);}catch{}}
        void OnObservation(Observation observation){publisher.Publish(observation);}
        void OnConnectionChanged(bool connected,string source){publisher.ResetCaptureSession();}
    }
}
