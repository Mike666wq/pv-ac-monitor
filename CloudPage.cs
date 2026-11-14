using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Acme.Cloud;
namespace ExperimentMonitor {
    public sealed class CloudPage:UserControl {
        // 云端只有一个站点，地址是编译期常量：现场不再需要填服务地址。
        internal const string CloudEndpoint="https://pv-ac.bbben.xyz";
        CloudConfiguration config;readonly CloudPublisher publisher;
        // 零配置：服务地址、令牌、启用开关、别名、模拟开关全部由云端下发，页面上不再有输入框。
        readonly Label aliasValue=new Label{AutoSize=true,ForeColor=UiTheme.Ink,Margin=new Padding(2,0,2,6)};
        readonly Label machineValue=new Label{AutoSize=true,ForeColor=UiTheme.Ink,Margin=new Padding(2,0,2,6)};
        readonly Label accessValue=new Label{AutoSize=true,MaximumSize=new Size(560,0),ForeColor=UiTheme.Ink};
        readonly Label enrollStateValue=new Label{AutoSize=true,ForeColor=UiTheme.Ink};
        readonly Label status=new Label{AutoSize=true,MaximumSize=new Size(1000,0)};
        readonly Label contact=new Label{AutoSize=true},lease=new Label{AutoSize=true},uploads=new Label{AutoSize=true},diagnostic=new Label{AutoSize=true,MaximumSize=new Size(1000,0)};
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};
        static readonly System.Text.RegularExpressions.Regex DeviceIdPattern=new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_-]{16,64}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        readonly MonitorEngine engine;
        public event EventHandler StatusChanged;
        public event Action<string> CloudStatusChanged;
        public CloudPublisherStatus CurrentStatus { get { return publisher.SnapshotStatus; } }
        public string CurrentStatusLabel { get {CloudPublisherStatus s=publisher.SnapshotStatus;return s.State+"；租约"+(s.LeaseValid?"有效":"无效")+"；已确认 "+s.Accepted+"，失败 "+s.Failed;} }
        public CloudPage(string root,MonitorEngine engine) {
            this.engine=engine;Dock=DockStyle.Fill;
            // 身份与凭据放稳定位置（ProgramData）：安装目录会被整包替换，放那里等于每次升级都要重新注册。
            // 但 --root= 指定的测试根必须保持隔离，不能写进真实 ProgramData。
            bool productionRoot=String.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
            string settingsDirectory=productionRoot?CloudIdentity.ResolveSettingsDirectory("ExperimentMonitorDemo",root):root;
            string configPath=Path.Combine(settingsDirectory,"cloud-settings.json");
            string legacyConfigPath=Path.Combine(root,"cloud-settings.json");
            string deviceId=null;
            if(productionRoot)
            {
                // 历史版本把设备编号存在 cloud-settings.json 内部（实验端没有独立的 device-id 文件），
                // 只换路径不搬内容，云端就会看到「多了一台新设备」。两步走：先搬文件，再对齐编号。
                if(!File.Exists(configPath)&&!String.Equals(configPath,legacyConfigPath,StringComparison.OrdinalIgnoreCase))
                    CloudSettingsMigration.AdoptConfig(configPath,legacyConfigPath);
                // 编号以「实际生效的配置文件」为准：它既可能是刚搬过来的旧配置，也可能早就是新配置。
                string existingId=File.Exists(configPath)?CloudConfiguration.Load(configPath,null).DeviceId:null;
                if(existingId!=null&&DeviceIdPattern.IsMatch(existingId))
                {
                    deviceId=existingId;
                    CloudIdentity.WriteDeviceIdTo(settingsDirectory,existingId);
                    CloudIdentity.WriteMachineDeviceId("ExperimentMonitorDemo",existingId);
                }
                else
                {
                    string legacyId;
                    if(CloudSettingsMigration.TryReadLegacyIdentity(legacyConfigPath,delegate(string v){return DeviceIdPattern.IsMatch(v);},out legacyId))
                    {
                        deviceId=legacyId;
                        CloudIdentity.WriteDeviceIdTo(settingsDirectory,legacyId);
                        CloudIdentity.WriteMachineDeviceId("ExperimentMonitorDemo",legacyId);
                    }
                    else deviceId=CloudIdentity.LoadOrCreateDeviceId("ExperimentMonitorDemo",root,delegate(string v){return DeviceIdPattern.IsMatch(v);},null,null);
                }
            }
            config=CloudConfiguration.Load(configPath,deviceId);
            // 地址与开关都由程序决定，不再读本地配置：云端才是唯一真相源。
            // --root= 测试隔离时保持关闭，避免把测试数据发到真实云端。
            config.Endpoint=CloudEndpoint;
            config.Enabled=productionRoot;
            // 接入状态写进实际生效的设置目录；--root= 测试根不写真实 ProgramData。
            publisher=new CloudPublisher(()=>Volatile.Read(ref config),null,Path.Combine(root,"data","storage"),productionRoot?settingsDirectory:null);engine.ObservationReceived+=OnObservation;engine.ConnectionChanged+=OnConnectionChanged;
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
            identity.Controls.Add(FieldLabel("设备别名（由云端分配）"));
            identity.Controls.Add(aliasValue);
            identity.Controls.Add(FieldLabel("机器钥匙"));
            identity.Controls.Add(machineValue);
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(identity,0,leftCol.RowCount-1);
            var settings=Section("云端授权");
            settings.Controls.Add(FieldLabel("接入状态"));
            settings.Controls.Add(enrollStateValue);
            settings.Controls.Add(FieldLabel("允许上传范围（由云端下发，本地不填）"));
            settings.Controls.Add(accessValue);
            var reclaim=new StyledActionButton{Text="重新申请接入",Width=150,BackColor=UiTheme.Blue};
            var diagnose=new StyledActionButton{Text="连接诊断（只发心跳，不上传测点）",Width=280};
            var actions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=Padding.Empty,Padding=new Padding(0,4,0,0)};
            actions.Controls.Add(reclaim);actions.Controls.Add(diagnose);settings.Controls.Add(actions);
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(settings,0,leftCol.RowCount-1);
            var credentials=Section("凭据备份");
            credentials.Controls.Add(new Label{Text="换机器或重装系统时用：导出后凭同一口令在另一台机器导入，独立数据源编号不变、无需重新注册。",AutoSize=true,MaximumSize=new Size(660,0),ForeColor=UiTheme.Muted,Margin=new Padding(2,0,2,6)});
            var exportCredential=new StyledActionButton{Text="导出凭据",Width=150};
            var importCredential=new StyledActionButton{Text="导入凭据",Width=150};
            var credentialActions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=Padding.Empty,Padding=new Padding(0,4,0,0)};
            credentialActions.Controls.Add(exportCredential);credentialActions.Controls.Add(importCredential);credentials.Controls.Add(credentialActions);
            exportCredential.Click+=delegate{ExportCredentials();};importCredential.Click+=delegate{ImportCredentials();};
            leftCol.RowCount++;leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));leftCol.Controls.Add(credentials,0,leftCol.RowCount-1);
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
            reclaim.Click+=delegate{Reclaim();};

            publisher.StatusChanged+=(s,e)=>PublishStatusLabel();timer.Tick+=(s,e)=>RefreshStatus();timer.Start();RefreshStatus();
            Disposed+=(s,e)=>{timer.Stop();timer.Dispose();engine.ObservationReceived-=OnObservation;engine.ConnectionChanged-=OnConnectionChanged;publisher.Dispose();};
        }
        // 分组容器:圆角白底 + 小标题(与总览页分区同风格),宽度跟随父容器拉伸。
        // 直接 Add 到分组的子控件会被移入内部单列网格(AutoSize 行)并撑满整行。
        void ExportCredentials()
        {
            CloudConfiguration current=Volatile.Read(ref config);
            using(SaveFileDialog dialog=new SaveFileDialog{Filter="设备凭据 (*.acmedevice)|*.acmedevice",FileName="PV-AC-Monitor-"+current.DeviceId+".acmedevice",OverwritePrompt=true})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string password=PromptPassword("导出设备凭据","设置一个至少 8 位的口令。文件里只有口令才能解开，请单独保管——忘记口令只能重新签发令牌。");
                if(password==null)return;
                try{CloudCredentialBundle.ExportToFile(current,password,dialog.FileName);MessageBox.Show(this,"凭据已导出：\r\n"+dialog.FileName+"\r\n\r\n口令不会写进文件，请自行保管。","导出凭据",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                catch(Exception e){MessageBox.Show(this,"导出失败："+e.Message,"导出凭据",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        void ImportCredentials()
        {
            using(OpenFileDialog dialog=new OpenFileDialog{Filter="设备凭据 (*.acmedevice)|*.acmedevice|所有文件 (*.*)|*.*"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string password=PromptPassword("导入设备凭据","输入导出时设置的口令。");
                if(password==null)return;
                try
                {
                    CloudConfiguration imported=CloudCredentialBundle.ImportFromFile(dialog.FileName,password,Volatile.Read(ref config).SavePath);
                    // 身份写回稳定位置、凭据落盘，再切换运行态：任一步失败都不会留下半截状态。
                    CloudIdentity.WriteMachineDeviceId("ExperimentMonitorDemo",imported.DeviceId);
                    imported.Save();
                    Volatile.Write(ref config,imported);
                    status.Text="凭据已导入；独立数据源编号 "+imported.DeviceId+"，重启本程序后生效";
                    MessageBox.Show(this,"凭据已导入。独立数据源编号："+imported.DeviceId+"\r\n\r\n本地数据按编号区分，重启本程序后生效。","导入凭据",MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
                catch(Exception e){MessageBox.Show(this,"导入失败："+e.Message,"导入凭据",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        /// <summary>口令输入框。凭据唯一的保护就是它，所以用密码字符并禁止空提交。</summary>
        string PromptPassword(string title,string hint)
        {
            using(Form form=new Form{Text=title,Width=460,Height=200,FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,BackColor=Color.White})
            {
                Label label=new Label{Text=hint,Left=14,Top=12,Width=420,Height=52,ForeColor=UiTheme.Muted};
                TextBox box=new TextBox{Left=14,Top=70,Width=420,UseSystemPasswordChar=true};
                Button ok=new Button{Text="确定",Left=272,Top=112,Width=76,Height=30,DialogResult=DialogResult.OK};
                Button cancel=new Button{Text="取消",Left=358,Top=112,Width=76,Height=30,DialogResult=DialogResult.Cancel};
                form.Controls.Add(label);form.Controls.Add(box);form.Controls.Add(ok);form.Controls.Add(cancel);
                form.AcceptButton=ok;form.CancelButton=cancel;
                if(form.ShowDialog(this)!=DialogResult.OK)return null;
                return box.Text;
            }
        }
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
        /// <summary>
        /// 唯一保留的手动动作：把「我是谁」重新交回云端排队。
        /// 用于设备被删除/令牌被撤销后由现场人员发起重新接入——仍然不需要填任何东西。
        /// </summary>
        void Reclaim()
        {
            string question="将清除本机保存的接入凭据，并重新向云端提交接入申请。\r\n管理员批准后自动恢复，期间本地采集与记录不受影响。\r\n\r\n继续？";
            if(MessageBox.Show(this,question,"重新申请接入",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
            try
            {
                var next=CloudConfiguration.Load(config.SavePath,config.DeviceId);
                next.Token="";next.Save();Volatile.Write(ref config,next);
                publisher.ResetEnrollment();RefreshStatus();
                MessageBox.Show(this,"已重新提交接入申请。请在云端「待批准设备」中确认。","重新申请接入",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception ex){MessageBox.Show(this,"重新申请失败："+ex.Message,"重新申请接入",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        void RefreshStatus(){CloudPublisherStatus snapshot=publisher.SnapshotStatus;
            aliasValue.Text=publisher.EnrollmentAlias;machineValue.Text=publisher.MachineKeyMasked;accessValue.Text=publisher.EnrollmentAccess;enrollStateValue.Text=publisher.EnrollmentState;
            status.Text=snapshot.State;contact.Text=snapshot.LastContactLocal;lease.Text=snapshot.LeaseValid?"有效至 "+snapshot.LeaseUntilLocal:"无有效租约";uploads.Text=snapshot.LastUploadLocal+" · 确认 "+snapshot.Accepted+" · 失败 "+snapshot.Failed+" · 待发设备 "+snapshot.QueuedDevices+" · 测点 "+snapshot.QueuedPoints;EventHandler h=StatusChanged;if(h!=null)h(this,EventArgs.Empty);PublishStatusLabel();}
        void PublishStatusLabel(){Action<string> h=CloudStatusChanged;if(h==null)return;string label=CurrentStatusLabel;try{if(IsHandleCreated&&InvokeRequired)BeginInvoke((Action)(()=>h(label)));else h(label);}catch{}}
        void OnObservation(Observation observation){publisher.Publish(observation);}
        void OnConnectionChanged(bool connected,string source){publisher.ResetCaptureSession();}
    }
}
