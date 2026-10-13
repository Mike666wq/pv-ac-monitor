using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace ExperimentMonitor {
    internal static class TrendWorkflowTests {
        static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
        static T Find<T>(Control root) where T:Control {if(root is T)return (T)root;foreach(Control c in root.Controls){T found=Find<T>(c);if(found!=null)return found;}return null;}
        static async Task Wait(Func<bool> condition,string message){DateTime end=DateTime.UtcNow.AddSeconds(8);while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException(message);await Task.Delay(20);}}
        static bool HasValues(TrendsPage page){return Field<Dictionary<string,List<ChartSample>>>(Field<ExperimentChart>(page,"chart"),"data").Values.SelectMany(x=>x).Any(s=>s.Value.HasValue);}
        internal static int Run(List<Point> points,string root){return RunCase(points,root,false)+RunCase(points,root,true)+OfflineHistory(points,root)+CompressedLineRendering();}
        static int CompressedLineRendering(){
            DateTime a=DateTime.UtcNow.Date;var acc=new TrendSeriesLoader.Accumulator("T0",a,a.AddSeconds(120),1,1);
            for(int i=0;i<120;i++)acc.Add(new ChartSample{Utc=a.AddSeconds(i),Value=i,Quality="good"});
            using(var chart=new ExperimentChart{Size=new Size(800,250),UseExplicitGaps=true}){
                chart.SelectPoints(new[]{"T0"});chart.SetSeriesData(new Dictionary<string,IEnumerable<ChartSample>>{{"T0",acc.Finish()}});chart.SetTimeRange(a,a.AddSeconds(120),false);
                if(!PaintedCenter(chart))throw new InvalidOperationException("趋势工作流失败：连续历史数据的压缩端点必须连成折线");
                chart.SetSeriesData(new Dictionary<string,IEnumerable<ChartSample>>{{"T0",new[]{new ChartSample{Utc=a,Value=0,Quality="good"},new ChartSample{Utc=a.AddSeconds(60),Value=null,Quality="timeout"},new ChartSample{Utc=a.AddSeconds(119),Value=119,Quality="good"}}}});chart.SetTimeRange(a,a.AddSeconds(120),false);
                if(PaintedCenter(chart))throw new InvalidOperationException("趋势工作流失败：故障断点不可被历史折线跨越");
            }return 2;
        }
        static bool PaintedCenter(ExperimentChart chart){
            using(var bitmap=new Bitmap(chart.Width,chart.Height)){
                chart.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));Rectangle plot=Field<List<Rectangle>>(chart,"plotRects")[0];int x=plot.Left+plot.Width/2,y=plot.Top+plot.Height/2;
                for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++){Color c=bitmap.GetPixel(x+dx,y+dy);if(c.B>170&&c.R<100&&c.G<160)return true;}return false;
            }
        }
        static int OfflineHistory(List<Point> points,string root){
            string isolated=Path.Combine(root,"trend-offline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);DateTime utc=DateTime.Today.AddDays(-1).AddHours(12).ToUniversalTime();
            using(var store=new ExperimentStore(Path.Combine(isolated,"data","storage"),"serial","{}")){store.Ready.GetAwaiter().GetResult();store.Record(new Observation{Point="T0",Description=PointCatalog.Get("T0").Label,Device="PLC",Source="serial",Utc=utc,RoundStartedUtc=utc,Number=12.5,RawNumber=12.5,Value="12.5",Quality="good",Mode="FLOAT ABCD",Unit="℃"},1,"℃");store.FlushAsync().GetAwaiter().GetResult();}
            Exception failure=null;int checks=0;Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("统一趋势失败："+message);checks++;};
            using(var form=new DashboardForm(points,isolated)){form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async(s,e)=>{try{
                    form.MainNavigation.SelectedIndex=1;TrendsPage page=Find<TrendsPage>(form.MainNavigation.SelectedTab);await Wait(()=>!Field<bool>(page,"queryBusy")&&HasValues(page),"未自动读取已有历史记录");
                    check(!form.Engine.IsConnected&&(string)Field<ComboBox>(page,"source").SelectedItem=="实机","离线默认读取唯一已有来源，无须连接采集");
                    check(Field<CheckBox>(page,"followLatest").Checked,"默认跟随最近记录");
                    DateTime a=Field<DateTime>(page,"loadedFrom"),b=Field<DateTime>(page,"loadedTo");check(b-a==TimeSpan.FromHours(1)&&b.Date==utc.Date,"默认滚动1小时锚定已有记录，不跳到空的当前日期");
                    Field<DateTimePicker>(page,"from").Value=DateTime.Today.AddDays(-1);Field<DateTimePicker>(page,"to").Value=DateTime.Today;
                    check(!Field<CheckBox>(page,"followLatest").Checked,"手改时间自动停止跟随");
                    check(Field<DateTime>(page,"loadedFrom")==a&&Field<DateTime>(page,"loadedTo")==b,"输入时间但未应用，不伪装图表已切换范围");
                    Field<Button>(page,"query").PerformClick();await Wait(()=>!Field<bool>(page,"queryBusy")&&!Field<bool>(page,"rangeDirty"),"固定范围查询未完成");
                    check(HasValues(page)&&Field<DateTime>(page,"loadedFrom")==DateTime.Today.AddDays(-1).ToUniversalTime(),"应用固定范围后查询昨日数据库");
                    Field<Button>(page,"latest").PerformClick();await Wait(()=>!Field<bool>(page,"queryBusy")&&Field<CheckBox>(page,"followLatest").Checked,"回到最新未恢复滚动");
                    check(HasValues(page)&&Field<DateTime>(page,"loadedTo")-Field<DateTime>(page,"loadedFrom")==TimeSpan.FromDays(1),"回到最新保留用户窗口长度与来源");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};Application.Run(form);
            }if(failure!=null)throw failure;return checks;
        }
        static int RunCase(List<Point> points,string root,bool serial){
            string isolated=Path.Combine(root,"trend-flow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"protocol.json"),Path.Combine(isolated,"protocol.json"));
            Exception failure=null;int checks=0;Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("统一趋势失败："+message);checks++;};
            using(var form=new DashboardForm(points,isolated)){form.ShowInTaskbar=false;form.Opacity=0;form.Engine.TransportFactory=(sim,port,baud)=>new SimulationTransport();
                Field<ComboBox>(form,"port").Items.Add("TEST_VIRTUAL_PORT");Field<ComboBox>(form,"port").SelectedItem="TEST_VIRTUAL_PORT";Field<CheckBox>(form,"simulation").Checked=!serial;Field<NumericUpDown>(form,"period").Value=1;
                form.Shown+=async(s,e)=>{try{
                    Field<Button>(form,"headerAction").PerformClick();Field<Button>(form,"start").PerformClick();await Wait(()=>form.Engine.Store!=null&&form.Engine.Store.Committed>=37,"未真正采集并写盘");
                    form.MainNavigation.SelectedIndex=1;TrendsPage page=Find<TrendsPage>(form.MainNavigation.SelectedTab);await Wait(()=>!Field<bool>(page,"queryBusy")&&HasValues(page),"默认最近记录未加载");
                    check((string)Field<ComboBox>(page,"source").SelectedItem==(serial?"实机":"模拟"),"默认来源采用实际采集来源");
                    DateTime oldA=Field<DateTime>(page,"loadedFrom"),oldB=Field<DateTime>(page,"loadedTo");
                    await Wait(()=>Field<DateTime>(page,"loadedTo")>oldB&&!Field<bool>(page,"queryBusy"),"新记录入库后时间轴未向前跟随");
                    DateTime newA=Field<DateTime>(page,"loadedFrom"),newB=Field<DateTime>(page,"loadedTo");check(newA-oldA==newB-oldB&&newB-newA==TimeSpan.FromHours(1),"起点终点一起推进，保持最近1小时");
                    var chart=Field<ExperimentChart>(page,"chart");using(var bitmap=new Bitmap(chart.Width,chart.Height))chart.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));Rectangle plot=Field<List<Rectangle>>(chart,"plotRects")[0];int x=plot.Left+plot.Width/2,y=plot.Top+plot.Height/2;
                    typeof(ExperimentChart).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chart,new object[]{new MouseEventArgs(MouseButtons.Left,1,x,y,0)});
                    typeof(ExperimentChart).GetMethod("OnMouseMove",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chart,new object[]{new MouseEventArgs(MouseButtons.Left,1,x+12,y,0)});
                    typeof(ExperimentChart).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chart,new object[]{new MouseEventArgs(MouseButtons.Left,1,x+12,y,0)});
                    check(!Field<CheckBox>(page,"followLatest").Checked,"拖动查看图表自动暂停跟随，不能被刷新立即拉回");await Wait(()=>!Field<bool>(page,"queryBusy"),"暂停在途更新未完成");
                    DateTime frozenA=Field<DateTime>(page,"loadedFrom"),frozenB=Field<DateTime>(page,"loadedTo");long written=form.Engine.Store.Committed;await Wait(()=>form.Engine.Store!=null&&form.Engine.Store.Committed>written,"固定分析时采集被错误停止");await Task.Delay(1100);
                    check(form.Engine.IsRunning&&Field<DateTime>(page,"loadedFrom")==frozenA&&Field<DateTime>(page,"loadedTo")==frozenB,"固定分析不停止采集且不被新记录改写");
                    Field<Button>(page,"latest").PerformClick();await Wait(()=>!Field<bool>(page,"queryBusy")&&Field<DateTime>(page,"loadedTo")>frozenB,"回到最新未读取后续记录");check(HasValues(page),"回到最新由数据库恢复后续记录");
                    Field<Button>(form,"stop").PerformClick();await Task.Delay(100);await Wait(()=>!Field<bool>(page,"queryBusy"),"停止后的最终写盘未显示");
                    Field<CheckBox>(page,"followLatest").Checked=false;await Wait(()=>!Field<bool>(page,"queryBusy"),"固定最后记录失败");DateTime stopped=Field<DateTime>(page,"loadedTo");
                    Field<Button>(form,"headerAction").PerformClick();await Task.Delay(100);check(HasValues(page)&&Field<DateTime>(page,"loadedTo")==stopped,"断开不清除固定分析数据");
                    string chosen=(string)Field<ComboBox>(page,"source").SelectedItem;Field<Button>(page,"latest").PerformClick();await Wait(()=>!Field<bool>(page,"queryBusy"),"离线回到最新失败");check((string)Field<ComboBox>(page,"source").SelectedItem==chosen,"回到最新不擅自切换分析来源");
                }catch(Exception ex){failure=ex;}finally{form.Close();}};Application.Run(form);
            }if(failure!=null)throw failure;return checks;
        }
    }
}
