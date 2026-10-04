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
            var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(20)};Controls.Add(panel);
            panel.Controls.Add(new Label{Text="实验云连接",Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold),AutoSize=true});
            panel.Controls.Add(new Label{Text="独立数据源："+config.DeviceId,AutoSize=true});panel.Controls.Add(enabled);
            panel.Controls.Add(new Label{Text="服务地址（HTTPS 域名根，与 BMS 使用同一云服务）",AutoSize=true});panel.Controls.Add(endpoint);
            panel.Controls.Add(new Label{Text="本地实验程序名称",AutoSize=true});panel.Controls.Add(alias);
            panel.Controls.Add(new Label{Text="实验设备令牌（留空保留已保存令牌，不能直接假定 BMS 令牌有实验权限）",AutoSize=true});panel.Controls.Add(token);panel.Controls.Add(simulation);
            var save=new Button{Text="保存并应用",Width=160,Height=34};panel.Controls.Add(save);panel.Controls.Add(status);panel.Controls.Add(contact);panel.Controls.Add(lease);panel.Controls.Add(uploads);
            var diagnose=new Button{Text="连接诊断（只发送心跳，不上传测点）",Width=300,Height=34};panel.Controls.Add(diagnose);panel.Controls.Add(diagnostic);
            diagnose.Click+=async(s,e)=>{diagnose.Enabled=false;diagnostic.Text="正在验证地址、网络、TLS、认证和云端接口响应…";try{var result=await publisher.DiagnoseAsync(CancellationToken.None);diagnostic.Text="诊断分类："+result.Category+"；"+result.Message+"（"+result.CompletedLocal+"）";}catch(Exception ex){diagnostic.Text="诊断失败："+ex.Message;}finally{diagnose.Enabled=true;}};
            var clear=new Button{Text="清除保存的令牌",Width=160,Height=32};panel.Controls.Add(clear);
            enabled.Checked=config.Enabled;simulation.Checked=config.IncludeSimulation;endpoint.Text=config.Endpoint;alias.Text=config.Alias;
            save.Click+=(s,e)=> {try {var next=CloudConfiguration.Load(config.SavePath,config.DeviceId);next.Enabled=enabled.Checked;next.IncludeSimulation=simulation.Checked;next.Endpoint=endpoint.Text.Trim();next.Alias=alias.Text.Trim();if(token.Text.Length>0)next.Token=token.Text;next.Save();Volatile.Write(ref config,next);token.Clear();status.Text="已保存；等待下一次心跳更新租约";}catch(Exception ex){MessageBox.Show(this,ex.Message,"云连接设置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
            clear.Click+=(s,e)=>{try{var next=CloudConfiguration.Load(config.SavePath,config.DeviceId);next.Token="";next.Enabled=false;next.Save();Volatile.Write(ref config,next);enabled.Checked=false;token.Clear();status.Text="已清除令牌并关闭云连接";}catch(Exception ex){status.Text=ex.Message;}};
            panel.Controls.Add(new Label{Text="本地持续采集和 SQLite 记录独立于网络；远端仅在有效观看租约内接收最新快照。\n填写云服务地址和实验设备令牌并保存后，可运行连接诊断检查心跳与观看租约；网络或认证异常请查看诊断结果。\n心跳在线只说明云连接状态，不代表本地串口已连接或正在采集。模拟/实机来源分别标记，失败或未响应测点不会作为零值发送。",AutoSize=true,MaximumSize=new Size(900,0),Margin=new Padding(0,18,0,0)});
            publisher.StatusChanged+=(s,e)=>PublishStatusLabel();timer.Tick+=(s,e)=>RefreshStatus();timer.Start();RefreshStatus();
            Disposed+=(s,e)=>{timer.Stop();timer.Dispose();engine.ObservationReceived-=OnObservation;engine.ConnectionChanged-=OnConnectionChanged;publisher.Dispose();};
        }
        void RefreshStatus(){CloudPublisherStatus snapshot=publisher.SnapshotStatus;status.Text="运行状态："+snapshot.State;contact.Text="最后心跳联系："+snapshot.LastContactLocal;lease.Text="观看租约："+(snapshot.LeaseValid?"有效至 "+snapshot.LeaseUntilLocal:"无有效租约");uploads.Text="最后上传："+snapshot.LastUploadLocal+"；快照确认："+snapshot.Accepted+" 次；失败："+snapshot.Failed+" 次；待发送设备："+snapshot.QueuedDevices+"；当前测点："+snapshot.QueuedPoints;EventHandler h=StatusChanged;if(h!=null)h(this,EventArgs.Empty);PublishStatusLabel();}
        void PublishStatusLabel(){Action<string> h=CloudStatusChanged;if(h==null)return;string label=CurrentStatusLabel;try{if(IsHandleCreated&&InvokeRequired)BeginInvoke((Action)(()=>h(label)));else h(label);}catch{}}
        void OnObservation(Observation observation){publisher.Publish(observation);}
        void OnConnectionChanged(bool connected,string source){publisher.ResetCaptureSession();}
    }
}
