using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Data.SQLite;

using Acme.Cloud;
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
                    form.MainNavigation.SelectedIndex=3;await Wait(()=>!Field<bool>(history,"queryBusy"),"首次记录页加载未完成");
                    Field<DateTimePicker>(history,"from").Value=DateTime.Today.AddDays(-1);Field<DateTimePicker>(history,"to").Value=DateTime.Today;
                    Field<Button>(history,"query").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"旧日期范围查询未完成");DataGridView grid=Field<DataGridView>(history,"grid");
                    check(grid.Rows.Count==1&&Field<Label>(history,"summary").Text.Contains("历史查询"),"记录页显式旧日期查询显示一整轮历史");
                    // 分页条:六个按钮与两块文字必须真实可见,不能只在代码里存在。
                    Func<Control,bool> visibleWithin=c=>{if(c==null||!c.Visible||c.Width<=0||c.Height<=0)return false;System.Drawing.Rectangle bounds=c.RectangleToScreen(c.ClientRectangle);for(Control parent=c.Parent;parent!=null;parent=parent.Parent){if(!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))return false;if(Object.ReferenceEquals(parent,form))return true;}return false;};
                    var barButtons=new[]{Field<Button>(history,"oldest"),Field<Button>(history,"next"),Field<Button>(history,"newer"),Field<Button>(history,"latest"),Field<Button>(history,"jumpStart"),Field<Button>(history,"jumpTo")};
                    string barProblem=null;
                    foreach(Button button in barButtons)if(!visibleWithin(button)){barProblem=button.Text+" bounds="+button.Bounds+" parent="+button.Parent.GetType().Name+button.Parent.ClientRectangle;break;}
                    check(barProblem==null,"分页条六个按钮完整位于可视区域内，未被父容器裁切"+(barProblem==null?"":" · "+barProblem));
                    bool barSized=true;foreach(Button button in barButtons)if(button.Width<=45||button.Height<22)barSized=false;
                    check(barSized,"分页条按钮尺寸可用");
                    Label pageInfo=Field<Label>(history,"pageInfo"),coverage=Field<Label>(history,"coverage");
                    check(visibleWithin(pageInfo)&&pageInfo.Text.Contains("轮"),"分页条显示本页轮数");
                    check(visibleWithin(coverage)&&coverage.Text.Contains("覆盖"),"分页条显示本页覆盖时间段");
                    check(!Field<Button>(history,"next").Enabled,"范围内只有一轮时“更早”不可用");
                    Field<Button>(history,"latest").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"回到最新页未完成");
                    check(grid.Rows.Count==1,"回到最新页仍是一整轮");
                    Field<DateTimePicker>(history,"anchorTime").Value=DateTime.Today.AddDays(-1).AddSeconds(1);
                    Field<Button>(history,"jumpTo").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"跳到该时刻未完成");
                    check(grid.Rows.Count==1,"跳到该时刻返回包含该范围数据的整轮");
                    Find<CheckBox>(history,c=>c.Text.Contains("采集轮宽表")).Checked=false;await Wait(()=>!Field<bool>(history,"queryBusy")&&grid.Rows.Count==37,"明细视图未查询全部测点");
                    check(grid.Rows.Count==37,"宽表与明细切换由一轮展开为37条实际记录");
                    var toggle=Field<LinkLabel>(history,"storageToggle");typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(toggle,new object[]{new LinkLabelLinkClickedEventArgs(toggle.Links[0])});
                    Find<Button>(history,b=>b.Text=="刷新清单").PerformClick();await Wait(()=>!Field<bool>(history,"inventoryBusy"),"清单刷新未完成");
                    check(Field<DataGridView>(history,"inventory").Rows.Count==3,"清单发现当前周期库和迁移平库并显示路径行");
                    check(ExperimentHistory.Count(storage,new HistoryFilter{Source="simulation",FromUtc=DateTime.Today.AddDays(-1).ToUniversalTime(),ToUtc=DateTime.Today.ToUniversalTime()})==37,"旧日期界面结果与底层历史计数一致");
                    // —— 0.5.0:三个子标签 / 双向互跳 / 实验段 / 测点统计 / 阈值 ——
                    TabControl inner=Field<TabControl>(history,"recordTabs");
                    check(inner!=null&&inner.TabPages.Count==3&&inner.TabPages[0].Text=="数据记录"&&inner.TabPages[1].Text=="实验段"&&inner.TabPages[2].Text=="测点统计","记录页三个子标签：数据记录 / 实验段 / 测点统计");
                    check(inner.TabPages.Count==3,"记录页子标签数量正确");
                    bool hasTrendTab=false;foreach(TabPage tab in inner.TabPages)if(tab.Text=="历史趋势")hasTrendTab=true;
                    check(!hasTrendTab,"记录页不再重复趋势视图");
                    HistoryFilter applied=(HistoryFilter)typeof(HistoryPage).GetMethod("MakeFilter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,null);
                    Field<Button>(history,"openTrends").PerformClick();await Wait(()=>form.MainNavigation.SelectedIndex==2,"未跳转到趋势分析页");
                    TrendsPage trends=null;foreach(Control candidate in form.MainNavigation.TabPages[2].Controls)if(candidate is TrendsPage)trends=(TrendsPage)candidate;
                    check(trends!=null,"趋势分析页存在");
                    await Wait(()=>!Field<bool>(trends,"queryBusy"),"趋势页读取未完成");
                    check(Math.Abs((Field<DateTimePicker>(trends,"from").Value.ToUniversalTime()-applied.FromUtc).TotalSeconds)<2&&Math.Abs((Field<DateTimePicker>(trends,"to").Value.ToUniversalTime()-applied.ToUtc).TotalSeconds)<2,"趋势页收到记录页的时间范围");
                    Field<Button>(trends,"openRecords").PerformClick();await Wait(()=>form.MainNavigation.SelectedIndex==3,"未跳回记录页");
                    await Wait(()=>!Field<bool>(history,"queryBusy"),"回跳记录页后查询未完成");
                    check(Math.Abs((Field<DateTimePicker>(history,"from").Value.ToUniversalTime()-applied.FromUtc).TotalSeconds)<2,"记录页收到趋势页的时间范围");
                    inner.SelectedIndex=1;Field<Button>(history,"sessionRun").PerformClick();
                    await Wait(()=>Field<DataGridView>(history,"sessionTable").Rows.Count>0,"实验段统计未出结果");
                    DataGridView sessions=Field<DataGridView>(history,"sessionTable");
                    check(sessions.Rows.Count>=1&&sessions.Columns.Count==13,"实验段表按会话列出统计");
                    inner.SelectedIndex=2;Field<Button>(history,"pointRun").PerformClick();
                    await Wait(()=>Field<DataGridView>(history,"pointTable").Rows.Count>0,"测点统计未出结果");
                    DataGridView pointsTable=Field<DataGridView>(history,"pointTable");
                    check(pointsTable.Rows.Count>0&&pointsTable.Columns.Count==15,"测点统计表按测点列出统计");
                    int t1Index=-1;for(int i=0;i<pointsTable.Rows.Count;i++)if(Convert.ToString(pointsTable.Rows[i].Cells[0].Value)=="T1"){t1Index=i;break;}
                    check(t1Index>=0,"测点统计里包含 T1");
                    // 按列名定位，不写死索引 —— 点统计表加过列（异常点/未读取点），写死索引会写错列。
                    int loCol=-1,hiCol=-1;
                    for(int ci=0;ci<pointsTable.Columns.Count;ci++){
                        if(pointsTable.Columns[ci].HeaderText=="下限")loCol=ci;
                        if(pointsTable.Columns[ci].HeaderText=="上限")hiCol=ci;
                    }
                    check(loCol>0&&hiCol==loCol+1,"测点统计表的下限/上限列存在且相邻（"+loCol+"/"+hiCol+"）");
                    pointsTable.Rows[t1Index].Cells[loCol].Value="5";pointsTable.Rows[t1Index].Cells[hiCol].Value="40";
                    Field<Button>(history,"limitsSave").PerformClick();
                    await Wait(()=>File.Exists(Path.Combine(isolated,"limits.json")),"阈值文件未写入");
                    check(LimitStore.Load(isolated).ContainsKey("T1"),"阈值保存后可重新读回");
                    // 列选择的一键全选 / 清空
                    Find<CheckBox>(history,c=>c.Text.Contains("采集轮宽表")).Checked=true;
                    await Wait(()=>!Field<bool>(history,"queryBusy"),"切回宽表未完成");
                    MethodInfo setAllColumns=typeof(HistoryPage).GetMethod("SetAllColumns",BindingFlags.Instance|BindingFlags.NonPublic);
                    check(setAllColumns!=null,"列选择提供一键全选/清空入口");
                    string[] columnScope=Field<PointPicker>(history,"picker").SelectedPoints;
                    setAllColumns.Invoke(history,new object[]{true,columnScope});
                    int allColumns=grid.Columns.Count;
                    setAllColumns.Invoke(history,new object[]{false,columnScope});
                    check(allColumns>2&&grid.Columns.Count==2,"列选择的一键清空只留下时间与采集轮两列");
                    check(Field<Label>(history,"feedback").Text.Contains("未勾选"),"清空全部列后给出提示，不静默显示全部");
                    // 回归：在别的子标签下触发查询，不能把数据表永久留在隐藏状态。
                    inner.SelectedIndex=1;
                    Field<Button>(history,"query").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"子标签下的查询未完成");
                    inner.SelectedIndex=0;Application.DoEvents();
                    check(grid.Visible&&grid.Rows.Count>0,"在实验段子标签下查询后，回到数据记录仍显示表格数据");
                    // —— 0.6.0:统一的测点选择器 / 自动应用 / 统计排队 ——
                    PointPicker picker=Field<PointPicker>(history,"picker");
                    check(picker!=null,"记录页左栏是统一的测点选择器");
                    TreeView tree=(TreeView)typeof(PointPicker).GetField("tree",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(picker);
                    TextBox pickerSearch=(TextBox)typeof(PointPicker).GetField("search",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(picker);
                    check(tree!=null&&tree.Nodes.Count==4,"测点树按设备分组");
                    check(picker.SelectedPoints.Length==37,"默认勾选全部测点");
                    check(tree.Nodes[0].Text.StartsWith("DS666（"),"设备节点显示已选/总数");
                    HistoryFilter scoped=(HistoryFilter)typeof(HistoryPage).GetMethod("MakeFilter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,null);
                    check(scoped.Devices.Length==0,"记录页的查询条件不再带冗余的设备过滤");
                    check(scoped.Points.Length==picker.SelectedPoints.Length,"查询条件里的测点与选择器一致");
                    // 取消勾选一个测点:去抖后自动重新查询,不用再点“查询记录”。
                    string dropped=picker.SelectedPoints[0];
                    tree.Nodes[0].Nodes[0].Checked=false;
                    await Wait(()=>!Field<bool>(history,"queryBusy")&&!Field<bool>(history,"filterDirty"),"取消勾选后没有自动重新查询");
                    check(!picker.SelectedPoints.Contains(dropped),"取消勾选后测点集合已更新");
                    check(((HistoryFilter)typeof(HistoryPage).GetMethod("MakeFilter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,null)).Points.Length==36,"自动应用后查询条件跟随选择");
                    // 搜索 + 清空 = 旧的设备级批量取消勾选
                    int ds666=points.Count(p=>p.binding.device=="DS666");
                    pickerSearch.Text="DS666";await Task.Delay(80);
                    check(picker.ListedPoints.Length==ds666,"按设备名搜索能筛出该设备的全部测点");
                    picker.SetAllVisible(false);
                    await Wait(()=>!Field<bool>(history,"queryBusy"),"清空可见测点后未自动应用");
                    check(picker.SelectedPoints.Length==37-ds666,"搜索+清空等价于按设备取消勾选");
                    pickerSearch.Text="";await Task.Delay(80);
                    picker.SetAllVisible(true);
                    await Wait(()=>!Field<bool>(history,"queryBusy"),"全选后未自动应用");
                    check(picker.SelectedPoints.Length==37,"全选恢复全部测点");
                    // 范围统计:改条件后不显示上一轮的旧数字,并且最终更新到当前范围。
                    Field<DateTimePicker>(history,"from").Value=DateTime.Today.AddDays(-1);Field<DateTimePicker>(history,"to").Value=DateTime.Today;
                    Field<Button>(history,"query").PerformClick();await Wait(()=>!Field<bool>(history,"queryBusy"),"范围统计前的查询未完成");
                    check(!Field<Label>(history,"summary").Text.Contains("筛选共 0 轮"),"查询完成后摘要不显示上一轮范围的旧轮数");
                    await Wait(()=>Field<Label>(history,"summary").Text.Contains("筛选共 1 轮"),"范围统计最终没有更新到当前范围");
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
                    form.MainNavigation.SelectedIndex=5;Control page=form.MainNavigation.SelectedTab;
                    ComboBox mode=Find<ComboBox>(page,c=>c.Items.Contains("FLOAT CDAB"));string requested=swapped?"FLOAT CDAB":"FLOAT ABCD";mode.SelectedItem=requested;
                    // 诊断页的寄存器数输入框：站号 1..247、地址 0..65535、寄存器数 1..125、超时 1..30。
                    // 上限由 8 提到 125 之后，原来的 Maximum==8 选择器再也匹配不到控件（2026-11-06 修复）。
                    Find<NumericUpDown>(page,n=>n.Maximum==125).Value=2;Button read=Find<Button>(page,b=>b.Text=="发送读取");read.PerformClick();
                    DateTime end=DateTime.UtcNow.AddSeconds(5);while(!read.Enabled&&DateTime.UtcNow<end)await Task.Delay(20);
                    // 按 Name 找，不要绑控件类型：0.8.11 把结果区从 Label 换成只读多行 TextBox
                    // （Label 不会滚动、六行结果被裁掉），按类型找的断言当场就挂了。
                    TextBox result=Find<TextBox>(page,t=>t.Name=="diagnosticResult");
                    check(result!=null,"诊断结果区可按 Name=diagnosticResult 找到");
                    // 结果区必须可滚动、可复制 —— TX/RX 原始帧是要抄给厂家/贴工单的。
                    check(result!=null&&result.Multiline&&result.ReadOnly&&result.ScrollBars==ScrollBars.Vertical,
                        "诊断结果区是只读多行且带垂直滚动（长内容不会被裁掉）");
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
                    form.MainNavigation.SelectedIndex=4;
                    // 零配置改造：服务地址、令牌、启用开关、别名、模拟开关全部由云端下发，
                    // 页面上不再有任何可编辑输入框。
                    check(Find<TextBox>(cloud,t=>t.UseSystemPasswordChar)==null,"云端页不再有令牌输入框");
                    check(Find<TextBox>(cloud,t=>t!=null)==null,"云端页不再有文本输入框");
                    check(Find<CheckBox>(cloud,c=>c.Text.Contains("启用"))==null,"云端页不再有启用开关");
                    check(Find<Button>(cloud,b=>b.Text=="重新申请接入")!=null,"重新申请接入按钮存在");
                    Button diagnose=Find<Button>(cloud,b=>b.Text.StartsWith("连接诊断"));check(diagnose!=null,"连接诊断按钮存在");
                    // 未配置令牌时诊断只做本地判定，不会发出外部请求。
                    diagnose.PerformClick();DateTime end=DateTime.UtcNow.AddSeconds(3);while(!diagnose.Enabled&&DateTime.UtcNow<end)await Task.Delay(20);
                    string diagnosticText=Field<Label>(cloud,"diagnostic").Text;
                    check(diagnosticText.Contains("认证")||diagnosticText.Contains("未接入")||diagnosticText.Contains("等待"),"页面诊断对未接入设备给出真实结果，不发送外部请求");
                    check(Field<Label>(cloud,"status").Text.Length>0,"连接状态标签有内容");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};
                Application.Run(form);
            }
            if(failure!=null)throw failure;return checks;
        }
    }
}




