using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Data.SQLite;

namespace ExperimentMonitor {
    internal static class PageWorkflowTests {
        static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
        static T Find<T>(Control root,Func<T,bool> predicate) where T:Control {if(root is T&&predicate((T)root))return (T)root;foreach(Control c in root.Controls){T found=Find(c,predicate);if(found!=null)return found;}return null;}
        sealed class FloatTransport:IReadTransport {
            readonly bool swapped;public FloatTransport(bool swapped){this.swapped=swapped;}
            public byte[] Read(int slave,int address,int count,int timeout,CancellationToken token,Action<string> log){return Modbus.WithCrc(swapped?new byte[]{(byte)slave,3,4,0,0,0x41,0x48}:new byte[]{(byte)slave,3,4,0x41,0x48,0,0});}
            public void Dispose(){}
        }
        internal static int Run(List<Point> points,string root){return Records(points,root)+Diagnostic(points,root,false)+Diagnostic(points,root,true)+CloudSettings(points,root);}
        static int Records(List<Point> points,string root){
            string isolated=Path.Combine(root,"records-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"protocol.json"),Path.Combine(isolated,"protocol.json"));int checks=0;Exception failure=null;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("页面工作流失败："+message);checks++;};
            using(var form=new DashboardForm(points,isolated)){
                var history=new HistoryPage(points,isolated,form.Engine);form.AttachHistoryPage(history);form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async(s,e)=>{try{
                    check(!form.Engine.IsConnected&&!form.Engine.IsRunning,"启动不自动连接或采集");
                    Field<CheckBox>(form,"simulation").Checked=true;Field<Button>(form,"headerAction").PerformClick();check(form.Engine.IsConnected&&!form.Engine.IsRunning,"连接按钮仅打开通道");
                    Field<Button>(form,"once").PerformClick();await Wait(()=>!form.Engine.IsRunning&&form.Engine.CompletedCaptures>0,"单轮采集未结束");
                    check(form.Engine.IsConnected,"单轮按钮完成后保持连接");
                    string storage=Path.Combine(isolated,"data","storage"),source=System.Linq.Enumerable.Single(ExperimentHistory.Databases(storage,"simulation"));
                    check(ExperimentHistory.Count(storage,new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddDays(-1),ToUtc=DateTime.UtcNow.AddDays(1)})==37,"单轮完成后实际数据库保存37条记录");
                    // Create a synthetic previous-day migration fixture from the isolated acquisition, never field data.
                    string old=Path.Combine(storage,"simulation-previous-day-30d.db");File.Copy(source,old);
                    using(var c=new SQLiteConnection("Data Source="+old+";Version=3;")){c.Open();using(var q=new SQLiteCommand("UPDATE observations SET utc_ticks=utc_ticks-@shift,round_started_utc_ticks=round_started_utc_ticks-@shift,session=COALESCE(session,'')||'-previousday'",c)){q.Parameters.AddWithValue("@shift",TimeSpan.FromDays(1).Ticks);q.ExecuteNonQuery();}}
                    form.MainNavigation.SelectedIndex=2;await Wait(()=>!Field<bool>(history,"queryBusy"),"首次记录页加载未完成");
                    Field<DateTimePicker>(history,"from").Value=DateTime.Today.AddDays(-1);Field<DateTimePicker>(history,"to").Value=DateTime.Today;
                    Field<Button>(history,"query").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"旧日期范围查询未完成");DataGridView grid=Field<DataGridView>(history,"grid");
                    check(grid.Rows.Count==1&&Field<Label>(history,"summary").Text.Contains("历史查询"),"记录页显式旧日期查询显示一整轮历史");
                    Find<CheckBox>(history,c=>c.Text.Contains("采集轮宽表")).Checked=false;await Wait(()=>!Field<bool>(history,"queryBusy")&&grid.Rows.Count==37,"明细视图未查询全部测点");
                    check(grid.Rows.Count==37,"宽表与明细切换由一轮展开为37条实际记录");
                    var toggle=Field<LinkLabel>(history,"storageToggle");typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(toggle,new object[]{new LinkLabelLinkClickedEventArgs(toggle.Links[0])});
                    Find<Button>(history,b=>b.Text=="刷新清单").PerformClick();await Wait(()=>!Field<bool>(history,"inventoryBusy"),"清单刷新未完成");
                    check(Field<DataGridView>(history,"inventory").Rows.Count==3,"清单发现当前周期库和迁移平库并显示路径行");
                    check(ExperimentHistory.Count(storage,new HistoryFilter{Source="simulation",FromUtc=DateTime.Today.AddDays(-1).ToUniversalTime(),ToUtc=DateTime.Today.ToUniversalTime()})==37,"旧日期界面结果与底层历史计数一致");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};
                Application.Run(form);
            }
            if(failure!=null)throw failure;return checks;
        }
        static async Task Wait(Func<bool> condition,string message){DateTime end=DateTime.UtcNow.AddSeconds(8);while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException(message);await Task.Delay(20);}}
        static int Diagnostic(List<Point> points,string root,bool swapped){
            string isolated=Path.Combine(root,"diagnostic-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);int checks=0;Exception failure=null;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("页面工作流失败："+message);checks++;};
            using(var form=new DashboardForm(points,isolated)){
                form.ShowInTaskbar=false;form.Opacity=0;form.Engine.TransportFactory=(sim,port,baud)=>new FloatTransport(swapped);
                form.Shown+=async(s,e)=>{try{
                    Field<CheckBox>(form,"simulation").Checked=true;Field<Button>(form,"headerAction").PerformClick();
                    check(form.Engine.IsConnected&&!form.Engine.IsRunning,"手动诊断连接不启动采集");
                    form.MainNavigation.SelectedIndex=4;Control page=form.MainNavigation.SelectedTab;
                    ComboBox mode=Find<ComboBox>(page,c=>c.Items.Contains("FLOAT CDAB"));string requested=swapped?"FLOAT CDAB":"FLOAT ABCD";mode.SelectedItem=requested;
                    Find<NumericUpDown>(page,n=>n.Maximum==8).Value=2;Button read=Find<Button>(page,b=>b.Text=="发送读取");read.PerformClick();
                    DateTime end=DateTime.UtcNow.AddSeconds(5);while(!read.Enabled&&DateTime.UtcNow<end)await Task.Delay(20);
                    Label result=Find<Label>(page,l=>l.Text.StartsWith("最近收发时间："));
                    check(read.Enabled&&result!=null&&result.Text.Contains("解析："+requested),"诊断保持用户选择的完整浮点字节序 "+requested);
                    check(result.Text.Contains("正常 · 12.5"),"已知合成响应按所选字节序正确解码 "+requested);
                    check(ExperimentHistory.Count(Path.Combine(isolated,"data","storage"),new HistoryFilter{Source="simulation",FromUtc=DateTime.UtcNow.AddDays(-1),ToUtc=DateTime.UtcNow.AddDays(1)})==0,"手动诊断不混入正式历史记录");
                    Find<Button>(page,b=>b.Text=="清空日志").PerformClick();check(Field<int>(form,"diagnosticRequests")==0&&Field<int>(form,"diagnosticGood")==0,"清空日志重置诊断计数");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};
                Application.Run(form);
            }
            if(failure!=null)throw failure;return checks;
        }
        static int CloudSettings(List<Point> points,string root){
            string isolated=Path.Combine(root,"cloud-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);int checks=0;Exception failure=null;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("页面工作流失败："+message);checks++;};
            using(var form=new DashboardForm(points,isolated)){
                var cloud=new CloudPage(isolated,form.Engine);form.AttachCloudPage(cloud);form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async(s,e)=>{try{
                    form.MainNavigation.SelectedIndex=3;string mockToken="fixture-token-no-production";
                    Field<CheckBox>(cloud,"enabled").Checked=false;Field<TextBox>(cloud,"endpoint").Text="https://example.invalid";Field<TextBox>(cloud,"alias").Text="test fixture";Field<TextBox>(cloud,"token").Text=mockToken;
                    Find<Button>(cloud,b=>b.Text=="保存并应用").PerformClick();string path=Path.Combine(isolated,"cloud-settings.json");var saved=CloudConfiguration.Load(path);
                    check(!saved.Enabled&&saved.Endpoint=="https://example.invalid"&&saved.Alias=="test fixture"&&saved.Token==mockToken,"云页面保存操作实际持久化当前配置");
                    check(!File.ReadAllText(path).Contains(mockToken)&&Field<TextBox>(cloud,"token").Text.Length==0,"保存令牌受DPAPI保护且输入框清空");
                    Find<Button>(cloud,b=>b.Text=="清除保存的令牌").PerformClick();saved=CloudConfiguration.Load(path);check(saved.Token.Length==0&&!saved.Enabled,"清除令牌实际清除凭据并禁用发布");
                    Button diagnose=Find<Button>(cloud,b=>b.Text.StartsWith("连接诊断"));diagnose.PerformClick();DateTime end=DateTime.UtcNow.AddSeconds(3);while(!diagnose.Enabled&&DateTime.UtcNow<end)await Task.Delay(20);
                    check(Field<Label>(cloud,"diagnostic").Text.Contains("认证"),"页面诊断对未配置令牌给出真实认证结果，不发送外部请求");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};
                Application.Run(form);
            }
            if(failure!=null)throw failure;return checks;
        }
    }
}
