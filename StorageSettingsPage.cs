using System;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace ExperimentMonitor {
    public sealed class StorageSettingsPage:UserControl {
        public StorageSettingsPage(string root,MonitorEngine engine) {
            Dock=DockStyle.Fill;var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(20),AutoScroll=true};Controls.Add(panel);
            panel.Controls.Add(new Label{Text="本地记录设置",Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold),AutoSize=true});
            string path=Path.Combine(root,"partition-days.json");int days=30;
            try{if(File.Exists(path)){var data=new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));if(data.Days>=1&&data.Days<=3650)days=data.Days;}}catch{}
            panel.Controls.Add(new Label{Text="数据库分期天数",AutoSize=true});var preset=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=120};preset.Items.AddRange(new object[]{"7 天","15 天","30 天","自定义"});preset.SelectedIndex=days==7?0:days==15?1:days==30?2:3;panel.Controls.Add(preset);
            var period=new NumericUpDown{Minimum=1,Maximum=3650,Value=days,Width=100,Enabled=preset.SelectedIndex==3};preset.SelectedIndexChanged+=(s,e)=>period.Enabled=preset.SelectedIndex==3;panel.Controls.Add(period);
            var next=new Button{Text="下个分期生效",Width=130,Height=34};panel.Controls.Add(next);var immediate=new Button{Text="立即新分期",Width=130,Height=34};panel.Controls.Add(immediate);
            var message=new Label{AutoSize=true,MaximumSize=new Size(850,0)};panel.Controls.Add(message);
            Action<bool> apply=now=>{try{int selected=preset.SelectedIndex==0?7:preset.SelectedIndex==1?15:preset.SelectedIndex==2?30:(int)period.Value;engine.ConfigurePartition(selected,now);string tmp=path+".partial";File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(new Settings{Days=selected}));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);message.Text="已保存："+selected+" 天；"+(now?"立即开始新分期。":"下个安全边界开始生效。");}catch(Exception ex){message.Text=ex.Message;}};next.Click+=(s,e)=>apply(false);immediate.Click+=(s,e)=>apply(true);
            panel.Controls.Add(new Label{Text="本地数据："+Path.Combine(root,"data","storage")+"\n\n模拟与实机按来源分库，采集时持续记录全部测点，与云端观看状态无关。数据记录页集中提供时间、设备、测点筛选和 Excel/CSV 导出。温度和电气单位来自工程配置与实时画面；倍率未核准的点保留原始读数。云连接默认关闭，启用后仅在有效观看租约内上传实时快照，完整历史保存在本机。",AutoSize=true,MaximumSize=new Size(850,0),Margin=new Padding(0,20,0,0)});
        }
        public sealed class Settings {public int Days{get;set;}}
    }
}
