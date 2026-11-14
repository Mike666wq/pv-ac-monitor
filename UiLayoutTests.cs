using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>Real control-tree layout and render checks. DPI scaling cases are simulated, not host monitor settings.</summary>
    internal static class UiLayoutTests {
        static int checks;
        static string tracePath;
        static readonly string[] MainPages={"实时监控","运行控制","趋势分析","数据记录","云端连接","通信诊断"};
        static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException("UI验收失败："+message);}
        static void Trace(string message){if(!String.IsNullOrEmpty(tracePath))File.AppendAllText(tracePath,DateTime.Now.ToString("HH:mm:ss.fff")+" "+message+Environment.NewLine);}

        internal static int Run(List<Point> points,string appRoot){
            checks=0;string output=Path.Combine(appRoot,"ui-validation");Directory.CreateDirectory(output);tracePath=Path.Combine(output,"ui-test-trace.log");File.WriteAllText(tracePath,"UI layout trace"+Environment.NewLine);
            var report=new Dictionary<string,object>();var cases=new List<Dictionary<string,object>>();
            VerifyHiddenInventoryLifecycle(points,output);
            checks+=TrendTimeWindowTests.Run();
            checks+=TrendWorkflowTests.Run(points,output);
            checks+=PageWorkflowTests.Run(points,output);
            foreach(var size in new[]{new[]{960,640},new[]{1440,900},new[]{2560,1600}})
                foreach(float scale in new[]{1f,1.25f,1.5f})
                    RunSize(points,output,size[0],size[1],scale,scale==1f?"100%":(scale==1.25f?"125% simulatedDPI":"150% simulatedDPI"),cases);
            report["passed"]=true;report["count"]=checks;report["checks"]=checks;report["cases"]=cases;report["dpiSimulationNotice"]="125%与150%通过缩放 WinForms 控件树模拟，不代表实际显示器 DPI 验证。";
            report["screenshots"]=Directory.GetFiles(output,"*.png").Select(Path.GetFileName).ToArray();
            string json=new JavaScriptSerializer().Serialize(report);File.WriteAllText(Path.Combine(output,"ui-test-result.json"),json,new System.Text.UTF8Encoding(false));File.WriteAllText(Path.Combine(appRoot,"ui-test-result.json"),json,new System.Text.UTF8Encoding(false));
            return checks;
        }

        static void VerifyHiddenInventoryLifecycle(List<Point> points,string output){
            string isolated=Path.Combine(output,"inventory-lifecycle-"+Guid.NewGuid().ToString("N"));
            string serial=Path.Combine(isolated,"data","storage","serial");Directory.CreateDirectory(serial);
            // Inventory discovery reads names/metadata only; these four empty files never represent field data.
            for(int i=0;i<4;i++)File.WriteAllBytes(Path.Combine(serial,"serial_cycle_fixture_"+i+".db"),new byte[0]);
            Exception failure=null;
            using(var form=new DashboardForm(points,isolated)){
                var history=new HistoryPage(points,isolated,form.Engine);form.AttachHistoryPage(history);
                form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async(s,e)=>{
                    try{
                        Check(!history.IsHandleCreated,"hidden history page has no native handle before first visit");
                        // Connecting to real hardware changes the source while the records tab is still hidden.
                        history.SetActiveSource("serial");await Task.Delay(350);
                        form.MainNavigation.SelectedTab=form.MainNavigation.TabPages.Cast<TabPage>().Single(p=>p.Text=="数据记录");
                        var inventory=Field<DataGridView>(history,"inventory");
                        await WaitAsync(()=>!Field<bool>(history,"inventoryBusy")&&inventory.Rows.Count==5,3000,"hidden source change must not permanently block inventory refresh");
                        Check(inventory.Rows.Count==5,"first records visit lists four databases plus path after hidden source change");
                        typeof(HistoryPage).GetMethod("RefreshInventory",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,new object[]{true});
                        await WaitAsync(()=>!Field<bool>(history,"inventoryBusy"),3000,"manual inventory refresh completes after first visit");
                        Check(inventory.Rows.Count==5,"manual refresh retains all four discovered databases");
                    }catch(Exception ex){failure=ex;}
                    finally{form.Close();}
                };
                Application.Run(form);
            }
            if(failure!=null)throw failure;
        }

        static void RunSize(List<Point> points,string output,int width,int height,float scale,string label,List<Dictionary<string,object>> cases){
            string title=width+"x"+height+" "+label;
            Trace("BEGIN "+title);
            string isolated=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"protocol.json"),Path.Combine(isolated,"protocol.json"));
            Exception failure=null;
            using(var form=new DashboardForm(points,isolated)){
                var history=new HistoryPage(points,isolated,form.Engine);
                form.AttachHistoryPage(history);
                form.AttachCloudPage(new CloudPage(isolated,form.Engine));
                form.ShowInTaskbar=false;form.Opacity=0;form.StartPosition=FormStartPosition.Manual;
                form.MaximumSize=new Size(20000,20000);
                if(scale!=1f)form.Scale(new SizeF(scale,scale));
                Size requested=new Size((int)Math.Round(width*scale),(int)Math.Round(height*scale));
                form.Shown+=async(s,e)=>{
                    try{
                        form.ClientSize=requested;
                        await Task.Delay(60);
                        Check(form.ClientSize==requested,title+" actual client size equals requested size");
                        TabControl nav=form.MainNavigation;
                        Check(nav!=null&&nav.TabPages.Count==6,title+" six navigation pages");
                        VerifyConnectionControls(form,title);
                        await WaitAsync(()=>Field<Label>(form,"cloudState").ForeColor==UiTheme.Muted,3000,"Disabled cloud should be neutral, not a failure");
                        Check(Field<Label>(form,"cloudState").ForeColor==UiTheme.Muted,title+" disabled cloud has neutral status");
                        InjectPreviewData(form,points);
                        foreach(string pageName in MainPages){
                            TabPage page=nav.TabPages.Cast<TabPage>().Single(x=>x.Text==pageName);
                            nav.SelectedTab=page;
                            await Task.Delay(60);
                            Trace("VERIFY PAGE "+title+" / "+pageName);
                            CapturePage(form,output,width,height,label,pageName);
                            VerifySelectedPage(form,page,title);
                            CapturePage(form,output,width,height,label,pageName);
                            Trace("SAVED PAGE "+title+" / "+pageName);
                        }
                        nav.SelectedIndex=0;
                        await Task.Delay(60);
                        TabControl inner=Find<TabControl>(nav.TabPages[0]);
                        Check(inner!=null&&inner.TabPages.Count==2,title+" overview and point details");
                        foreach(TabPage child in inner.TabPages){
                            inner.SelectedTab=child;
                            await Task.Delay(30);
                            Check(child.ClientSize.Width>0&&child.ClientSize.Height>0,title+" live detail layout");
                        }
                        inner.SelectedIndex=0;
                        VerifyCardText(form,points,title);
                        if(scale==1f&&(width==960||width==1440)){
                            await VerifyLifecycleAsync(form,points,output,width,height,label,title);
                        }
                        cases.Add(new Dictionary<string,object>{{"size",title},{"requestedWidth",requested.Width},{"requestedHeight",requested.Height},{"physicalWidth",form.ClientSize.Width},{"physicalHeight",form.ClientSize.Height},{"dpiSimulation",scale!=1f},{"pages",MainPages}});
                        Trace("CASE COMPLETE "+title);
                    }catch(Exception ex){failure=ex;Trace("FAIL "+ex);}
                    finally{
                        Trace("CLOSE BEGIN "+title);
                        form.Close();
                        Trace("CLOSE END "+title);
                    }
                };
                Application.Run(form);
                Check(form.IsDisposed,title+" window closes and disposes normally");
                Check(!form.Engine.IsConnected&&!form.Engine.IsRunning,title+" close releases acquisition resources");
            }
            if(failure!=null)throw new InvalidOperationException("UI case failed: "+title,failure);
        }

        static T Field<T>(object owner,string name){return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);}
        static async Task WaitAsync(Func<bool> condition,int milliseconds,string message){
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(!condition()){
                if(clock.ElapsedMilliseconds>=milliseconds)throw new TimeoutException(message);
                await Task.Delay(20);
            }
        }
        static async Task VerifyLifecycleAsync(DashboardForm form,List<Point> points,string output,int width,int height,string label,string title){
            // 连接/断开已合并为标题栏的 headerAction 切换按钮。
            StyledActionButton headerAction=Field<StyledActionButton>(form,"headerAction");
            Button start=Field<Button>(form,"start"),once=Field<Button>(form,"once"),stop=Field<Button>(form,"stop");
            // 宽表默认只显示常用三组(温度/实验电表/市电)的列。
            // 用产品自己的判定函数算期望列数，而不是在这里重写一份分组白名单 ——
            // 写死过一份，产品改成按角色过滤之后这里就假失败了。
            int expectedWideColumns=points.Count(p=>HistoryPage.DefaultColumnVisible(p.name))+2;
            CheckBox simulation=Field<CheckBox>(form,"simulation");simulation.Checked=true;
            Field<NumericUpDown>(form,"period").Value=1;
            Trace("LIFECYCLE BEGIN "+title);
            headerAction.PerformClick();
            await WaitAsync(()=>form.Engine.IsConnected,5000,"UI connection did not open");
            Check(!form.Engine.IsRunning,title+" connection does not acquire");
            start.PerformClick();
            await WaitAsync(()=>form.Engine.IsRunning&&GetCounter(form,"good")>0,10000,"UI start did not acquire");
            stop.PerformClick();
            await WaitAsync(()=>!form.Engine.IsRunning,5000,"UI stop did not drain");
            Check(form.Engine.IsConnected,title+" stop keeps connection");
            once.PerformClick();
            await WaitAsync(()=>!form.Engine.IsRunning,15000,"UI one-round acquisition did not finish");
            await WaitAsync(()=>GetCounter(form,"good")==points.Count,5000,"UI did not display all points");
            Check(form.Engine.IsConnected,title+" one round keeps connection");
            Check(String.IsNullOrEmpty(form.Engine.LastStoreError),title+" real UI acquisition committed successfully");
            headerAction.PerformClick();
            await WaitAsync(()=>!form.Engine.IsConnected,5000,"UI disconnect did not release");
            await WaitAsync(()=>Field<Label>(form,"connectionState").Text.Contains("未连接")&&headerAction.Text=="连接",5000,"UI connection state did not reflect disconnect");
            // 状态栏四段必须各自标明语义：四个同样颜色的圆点会让人以为它们是同一类信号。
            Check(Field<Label>(form,"connectionState").Text.Contains("链路")
                &&Field<Label>(form,"captureState").Text.Contains("采集")
                &&Field<Label>(form,"recordState").Text.Contains("记录")
                &&Field<Label>(form,"cloudState").Text.Contains("云端"),title+" 状态栏四段各自标明语义（链路/采集/记录/云端）");
            // 全局搜索：点名 / 名称 / 力控原名(alias) / 地址 四路都要能命中，且循环不越界。
            TextBox search=Field<TextBox>(form,"searchBox");
            MethodInfo searchNext=typeof(DashboardForm).GetMethod("SearchNext",BindingFlags.Instance|BindingFlags.NonPublic);
            search.Text="T1";                 string byName=(string)searchNext.Invoke(form,null);
            Check(byName.Contains("T1"),title+" 搜索按点名命中（"+byName+"）");
            search.Text="主机罐温度";           string byLabel=(string)searchNext.Invoke(form,null);
            Check(byLabel.Contains("T1"),title+" 搜索按名称命中（"+byLabel+"）");
            search.Text="303相变温度2";        string byAlias=(string)searchNext.Invoke(form,null);
            Check(byAlias.Contains("T1"),title+" 搜索按力控原名命中（"+byAlias+"）");
            search.Text="302";                 string byAddress=(string)searchNext.Invoke(form,null);
            Check(byAddress.Contains("T1"),title+" 搜索按寄存器地址命中（"+byAddress+"）");
            search.Text="绝不存在的点名";       string miss=(string)searchNext.Invoke(form,null);
            Check(miss.Contains("没有匹配"),title+" 搜索无命中时给出明确提示");
            search.Text="温度";                string first=(string)searchNext.Invoke(form,null);
            string second=(string)searchNext.Invoke(form,null);
            Check(first!=second&&first.Contains("第 1/")&&second.Contains("第 2/"),title+" 搜索在多个命中项之间循环");
            // 搜索框在标题栏上是常驻的，任何页签都点得到。以前它只在总览页真起作用，
            // 在别的页签点完屏幕上什么都不变、却还回报"定位成功" —— 那是说假话。
            form.MainNavigation.SelectedIndex=3;
            search.Text="T1";
            string fromOther=(string)searchNext.Invoke(form,null);
            Check(fromOther.Contains("T1")&&!fromOther.Contains("不在总览页"),title+" 在别的页签定位会切回总览并真正生效（"+fromOther+"）");
            Check(form.MainNavigation.SelectedIndex==0,title+" 定位后主视图已切回实时监控");
            // 结果必须回显在看得见的地方；以前 AppendLog 写进的是通信诊断页的日志。
            MethodInfo showResult=typeof(DashboardForm).GetMethod("ShowSearchResult",BindingFlags.Instance|BindingFlags.NonPublic);
            showResult.Invoke(form,new object[]{"回显测试"});
            Label caption=Field<Label>(form,"searchCaption");
            Check(caption!=null&&caption.Text=="回显测试",title+" 定位结果显示在标题栏（站在总览页也看得见）");
            Check(caption!=null&&caption.AutoEllipsis&&caption.MaximumSize.Width>0,title+" 回显文案有宽度上限，不会把标题挤掉");
            Check(caption!=null&&caption.MaximumSize.Width<=220,title+" 回显文案宽度上限足够窄（"+ (caption==null?0:caption.MaximumSize.Width) +"px）");
            // 走真实按钮，而不是直接调 SearchNext：用户是按按钮，这条链路上"结果有没有回显"才是他看到的。
            // 用"主机罐温度"（唯一命中 T1）而不是 "T1"：回显显示的是点名+名称，
            // 而 "T1" 还会经由别名命中别的点，断言就不稳。
            search.Text="主机罐温度";
            Field<Button>(form,"searchButton").PerformClick();
            Check(caption!=null&&caption.Text!="定位测点"&&caption.Text.Contains("T1"),title+" 点「定位」按钮后结果回显在标题栏（"+ (caption==null?"":caption.Text) +"）");
            Check(Field<Button>(form,"searchButton").Text=="定位",title+" 入口标注为「定位」而不是含糊的「查找」");
            // 只断言"找到了"是不够的：真正的要求是**把目标滚进可视区**。
            // 定位算法曾用 card.Parent.Top+card.Top，漏掉 section 在 stack 里的偏移，
            // 越靠后的分组偏得越多 —— 表现就是"查找了但什么都没动"。
            var overviewViewport=typeof(DashboardForm).GetField("overviewViewport",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form) as Panel;
            var cardMap=typeof(DashboardForm).GetField("cards",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form) as Dictionary<string,Label[]>;
            // 用户真正要的是"筛选"：输入关键字后，不匹配的卡片不该继续占着屏幕。
            // 之前的"定位"只滚一下闪一下，目标本来就在眼前时等于毫无反应。
            Func<int> visibleCards=()=>{
                int n=0;
                foreach(var kv in cardMap) {
                    Control c=kv.Value[1];
                    while(c!=null&&!(c is RoundedPanel)) c=c.Parent;
                    if(c!=null&&c.Visible) n++;
                }
                return n;
            };
            MethodInfo applyFilter=typeof(DashboardForm).GetMethod("ApplyCardFilter",BindingFlags.Instance|BindingFlags.NonPublic);
            int all=visibleCards();
            applyFilter.Invoke(form,new object[]{"电流"});
            int filtered=visibleCards();
            Check(filtered>0&&filtered<all,title+" 筛选「电流」只留下匹配的卡片（"+filtered+"/"+all+"）");
            Check(caption.Text.Contains("筛选")&&caption.Text.Contains("3"),title+" 筛选后在标题栏报出命中个数（"+caption.Text+"）");
            // 分组标题的个数必须跟着筛选走。
            // 过去它是建卡片时写死的（"指令 / 设定 · 4"），筛选后只剩 1 张卡片，
            // 标题却还写 4 —— 界面在说假话，而这正是本项目一直在消灭的那类问题。
            string sectionCounts=string.Join(" | ",SectionTitles(overviewViewport));
            Check(!HasStaleSectionCount(overviewViewport),
                title+" 筛选后分组标题的个数与实际可见卡片一致（"+sectionCounts+"）");
            applyFilter.Invoke(form,new object[]{"绝无此项"});
            Check(visibleCards()==0,title+" 无匹配时卡片墙清空");
            Check(caption.Text.Contains("没有匹配"),title+" 无匹配在标题栏如实提示（"+caption.Text+"）");
            applyFilter.Invoke(form,new object[]{""});
            Check(visibleCards()==all,title+" 清空关键字后卡片全部恢复（"+visibleCards()+"/"+all+"）");
            Check(!HasStaleSectionCount(overviewViewport),
                title+" 清空关键字后分组标题的个数恢复（"+string.Join(" | ",SectionTitles(overviewViewport))+"）");
            Check(overviewViewport!=null&&cardMap!=null&&cardMap.ContainsKey("0006H"),title+" 搜索定位所需的视口与卡片映射可用");
            search.Text="0006H"; searchNext.Invoke(form,null);
            Control lastCard=cardMap["0006H"][1];
            while(lastCard!=null&&!(lastCard is RoundedPanel)) lastCard=lastCard.Parent;
            System.Drawing.Point where=overviewViewport.PointToClient(lastCard.PointToScreen(System.Drawing.Point.Empty));
            Check(where.Y>=0&&where.Y<overviewViewport.ClientSize.Height,
                title+" 搜索把末尾分组的卡片滚进可视区（y="+where.Y+"，视口高 "+overviewViewport.ClientSize.Height+"）");
            search.Text="";
            TabPage page=form.MainNavigation.TabPages[3];
            form.MainNavigation.SelectedTab=page;
            HistoryPage history=Find<HistoryPage>(page);
            DataGridView grid=Field<DataGridView>(history,"grid");
            await WaitAsync(()=>!Field<bool>(history,"queryBusy")&&Field<int>(history,"appliedGeneration")==Field<int>(history,"generation")&&grid.Rows.Count>0&&!Field<bool>(history,"filterDirty"),10000,"UI auto-follow failed: "+DescribeHistoryState(page,grid));
            Check(grid.Columns.Count==expectedWideColumns,title+" acquired wide preview includes the default column subset");
            CapturePage(form,output,width,height,label,"records-after-capture-wide");
            CheckBox wide=Find<CheckBox>(page,c=>c.Text.Contains("采集轮宽表"));
            wide.Checked=false;
            wide.Checked=true;
            await WaitAsync(()=>!Field<bool>(history,"queryBusy")&&Field<int>(history,"appliedGeneration")==Field<int>(history,"generation")&&!Field<bool>(history,"pendingViewQuery")&&grid.Columns.Count==expectedWideColumns&&grid.Rows.Count>0,10000,"UI rapid view switch did not converge to the latest selection");
            Check(grid.Columns.Count==expectedWideColumns,title+" rapid view changes preserve latest selection");
            wide.Checked=false;
            await WaitAsync(()=>!Field<bool>(history,"queryBusy")&&Field<int>(history,"appliedGeneration")==Field<int>(history,"generation")&&grid.Columns.Count==8&&grid.Rows.Count>=points.Count,10000,"UI detail query did not complete");
            Check(grid.Rows.Count>=points.Count,title+" UI details include committed observations");
            long beforeVisibleRound=grid.Rows.Count;
            headerAction.PerformClick();
            await WaitAsync(()=>form.Engine.IsConnected,5000,"Reconnect from records page failed");
            once.PerformClick();
            await WaitAsync(()=>!form.Engine.IsRunning,15000,"Visible-page one round did not finish");
            await WaitAsync(()=>!Field<bool>(history,"queryBusy")&&Field<int>(history,"appliedGeneration")==Field<int>(history,"generation")&&grid.Rows.Count==beforeVisibleRound+points.Count,10000,"Auto-follow missed a completed short round while the records page stayed open");
            Check(grid.Rows.Count==beforeVisibleRound+points.Count,title+" visible record page follows a completed short round");
            headerAction.PerformClick();
            await WaitAsync(()=>!form.Engine.IsConnected,5000,"Final UI disconnect failed");
            await WaitAsync(()=>Field<Label>(form,"connectionState").Text.Contains("未连接")&&headerAction.Text=="连接"&&Field<Label>(form,"recordState").Text.Contains("已停止"),5000,"UI settled acquisition and recording states did not reflect disconnect");
            CapturePage(form,output,width,height,label,"records-after-capture-detail");
            long expected=grid.Rows.Count;
            HistoryFilter filter=(HistoryFilter)typeof(HistoryPage).GetMethod("MakeFilter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,null);
            string storage=Path.Combine(Field<string>(form,"appRoot"),"data","storage");
            Check(ExperimentHistory.Count(storage,filter)==expected,title+" UI details match database for applied filter");
            var exported=await ExperimentExport.ExportAsync(Path.Combine(Field<string>(form,"appRoot"),"data","storage"),filter,Path.Combine(output,"exports-"+width),"xlsx",null,CancellationToken.None);
            long exportableExpected=ExperimentHistory.Query(storage,filter).Count(r=>ExperimentExport.ExportablePoint(r.Point));Check(exported.Rows==exportableExpected,title+" full-range export contains every exportable record: exportable="+exportableExpected+", exported="+exported.Rows);
            Trace("LIFECYCLE END "+title);
        }
        static void VerifyConnectionControls(DashboardForm form,string title){
            foreach(string text in new[]{"连接","开始采集","采集一轮","停止"}){
                Control button=Find<Control>(form,c=>c is Button&&c.Text==text);Check(button!=null,title+" 有“"+text+"”控件");
                Check(button.Width>45&&button.Height>=22,title+" “"+text+"”操作区尺寸可用");
                Check(button!=null&&FullyVisible(button,form),title+" “"+text+"”完整位于可视区域内，未被父容器裁切 · "+DescribeBounds(button));
                if(button!=null){Size measured=TextRenderer.MeasureText(button.Text,button.Font,new Size(Math.Max(1,button.ClientSize.Width-12),Math.Max(1,button.ClientSize.Height-4)),TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);Check(measured.Width<=button.ClientSize.Width-8&&measured.Height<=button.ClientSize.Height-2,title+" “"+text+"”按钮文字完整容纳");}
            }
            foreach(string text in new[]{"串口","周期(s)","超时(s)"})Check(Find<Control>(form,c=>c is Label&&c.Text==text)!=null,title+" 有连接／采集参数 "+text);
            Label heading=Find<Label>(form,c=>c.Text.Contains("全实验数据工作台"));
            Check(heading!=null&&Contrast(heading.ForeColor,heading.Parent.BackColor)>=4.5,title+" 标题与背景对比度足够");
            Check(heading!=null&&FullyVisible(heading,form),title+" 页面主标题完整位于可视区域内");
        }
        static bool FullyVisible(Control child,Control root){
            if(child==null||!child.Visible||child.Width<=0||child.Height<=0)return false;
            Rectangle bounds=child.RectangleToScreen(child.ClientRectangle);
            for(Control parent=child.Parent;parent!=null;parent=parent.Parent){
                Rectangle clip=parent.RectangleToScreen(parent.ClientRectangle);
                if(!clip.Contains(bounds))return false;
                if(Object.ReferenceEquals(parent,root))return true;
            }
            return false;
        }
        static string DescribeBounds(Control child){
            if(child==null)return "null";
            string value=child.GetType().Name+" visible="+child.Visible+" bounds="+child.Bounds+" screen="+child.RectangleToScreen(child.ClientRectangle);
            for(Control parent=child.Parent;parent!=null;parent=parent.Parent)value+=" <- "+parent.GetType().Name+" visible="+parent.Visible+" bounds="+parent.Bounds+" screen="+parent.RectangleToScreen(parent.ClientRectangle);
            return value;
        }
        static double Contrast(Color a,Color b){double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05);}
        static double Luminance(Color c){Func<int,double> channel=v=>{double n=v/255.0;return n<=0.04045?n/12.92:Math.Pow((n+0.055)/1.055,2.4);};return 0.2126*channel(c.R)+0.7152*channel(c.G)+0.0722*channel(c.B);}
        static void VerifySelectedPage(DashboardForm form,TabPage page,string title){
            Check(page.ClientSize.Width>0&&page.ClientSize.Height>0,title+" 页面尺寸有效："+page.Text);
            Control fill=page.Controls.Cast<Control>().FirstOrDefault(c=>c.Dock==DockStyle.Fill);Check(fill!=null,title+" 页面主要内容使用可伸缩区域："+page.Text);
            if(page.Text=="趋势分析"){
                ExperimentChart chart=Find<ExperimentChart>(page);Check(chart!=null&&chart.ClientSize.Width>=250&&chart.ClientSize.Height>=170,title+" 趋势图保留绘图区");Panel chartScroll=chart==null?null:chart.Parent as Panel;Check(chartScroll!=null&&chartScroll.AutoScroll,title+" 多单位纵轴在可滚动区域完整查看");Check(chart==null||chart.Height>=chart.RequiredHeight,title+" 全选测点时图表高度覆盖所有单位轴");
                TrendsPage trends=Find<TrendsPage>(page);Check(trends!=null,title+" 趋势工作区存在");
                foreach(string field in new[]{"followLatest","source","from","to","quick","query","latest"}){
                    FieldInfo info=typeof(TrendsPage).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic);Control control=info==null?null:info.GetValue(trends) as Control;
                    Check(control!=null,title+" 趋势筛选控件保留："+field);
                    Check(control!=null&&FullyVisible(control,form),title+" 趋势筛选控件完整可见："+field+(control==null?"":" · "+DescribeBounds(control)));
                    if(control is DateTimePicker)Check(control.Width>=200,title+" 趋势时间框完整显示日期和秒："+field);
                }
            }
            if(page.Text=="数据记录"){
                Check(Find<DataGridView>(page)!=null,title+" 数据记录表存在");
                Check(Find<Button>(page,c=>c.Text=="查询记录")!=null,title+" 历史查询入口存在");
                Check(Find<DateTimePicker>(page)!=null,title+" 历史精确时间筛选存在");
                Check(Find<CheckBox>(page,c=>c.Text.Contains("采集轮宽表"))!=null,title+" 宽表/逐测点明细切换存在");
                Check(Find<Button>(page,c=>c.Text.Contains("导出数据"))!=null,title+" 历史导出入口存在");
            }
            if(page.Text=="通信诊断"){
                Check(Find<Control>(page,c=>c.Text.Contains("只读 Modbus 03"))!=null,title+" 诊断只读说明可见");
                Control send=Find<Control>(page,c=>c.Text=="发送读取");Check(send!=null,title+" 手动读取入口存在");
                Check(send!=null&&FullyVisible(send,form),title+" 手动读取按钮完整位于可视区域内，未被裁切"+(send==null?"":" · "+DescribeBounds(send)));
                Control clear=Find<Control>(page,c=>c.Text=="清空日志");Check(clear!=null&&FullyVisible(clear,form),DescribeBounds(clear)+title+" 清空日志按钮完整位于可视区域内");
                ComboBox parser=Find<ComboBox>(page,c=>c.Items.Contains("RAW 原始寄存器"));Check(parser!=null&&FullyVisible(parser,form),title+" 通信解析方式选择框完整可见"+(parser==null?"":" · "+DescribeBounds(parser)));
                // 按 Name 找通信日志，别按形状（Multiline && ReadOnly）找。
                // 0.8.11 给诊断页加了第二个"只读多行 TextBox"（最近一次读取结果，带垂直滚动条），
                // 形状谓词会先撞上它，于是 Both != Vertical 直接把 ui 套件打挂。
                TextBox log=Find<TextBox>(page,c=>c.Name=="commLog");Control logContainer=log==null?null:log.Parent;ScrollableControl logScroll=logContainer==null?null:logContainer.Parent as ScrollableControl;bool logReachable=logContainer!=null&&(FullyVisible(logContainer,form)||(logScroll!=null&&logScroll.AutoScroll));Check(log!=null&&log.Dock==DockStyle.Fill&&log.ScrollBars==ScrollBars.Both&&log.ClientSize.Width>=200&&log.ClientSize.Height>=50&&logReachable,title+" 通信日志保持可用高度，并完整可见或可通过诊断页滚动访问"+(log==null?"":" · client="+log.ClientSize.Width+"x"+log.ClientSize.Height+" dock="+log.Dock+" bars="+log.ScrollBars+" reachable="+logReachable)+" · "+DescribeBounds(log));
            }
            // 零配置改造后页面上不再有「服务地址（HTTPS…）」输入项，改为展示云端下发的接入状态。
            if(page.Text=="云端连接")Check(Find<Control>(page,c=>c.Text.Contains("接入状态")||c.Text.Contains("云端授权"))!=null,title+" 云端接入状态信息存在");
            if(page.Text=="运行控制"){
                Check(Find<Button>(page,c=>c.Text.StartsWith("控制开关"))!=null,title+" 控制开关存在");
                Check(Find<Button>(page,c=>c.Text.StartsWith("发送全部"))!=null,title+" 批量发送入口存在");
                Check(Find<Button>(page,c=>c.Text=="重新读取核对")!=null,title+" 控制核对入口存在");
                Check(Find<Label>(page,c=>c.Text.Contains("待发"))!=null,title+" 待发计数存在");
                Check(Find<Label>(page,c=>c.Text.Contains("联锁"))!=null,title+" 联锁提示存在");
                Check(Find<RichTextBox>(page)!=null,title+" 过程日志存在");
                Check(Find<DataGridView>(page)!=null,title+" 设定表存在");
                Check(Find<TabControl>(page)!=null,title+" 日志/详情页签存在");
            }
        }
        static void VerifyCardText(DashboardForm form,List<Point> points,string title){
            FieldInfo field=typeof(DashboardForm).GetField("cards",BindingFlags.Instance|BindingFlags.NonPublic);var map=field.GetValue(form) as Dictionary<string,Label[]>;Check(map!=null&&map.Count==points.Count,title+" 37个测点卡片全部创建");
            Panel viewport=Find<Panel>(form,c=>c.AutoScroll);Check(viewport!=null,title+" 全量测点可在纵向滚动区域访问");
            foreach(Point point in points)Check(map!=null&&map.ContainsKey(point.name),title+" "+point.name+" 卡片已纳入布局检查");
            foreach(var entry in map){
                Label label=entry.Value[0],value=entry.Value[1],unit=entry.Value[2],quality=entry.Value[3];
                // 新卡片版式:数值/单位标签按内容自适应宽度,布局区域是它们的父容器。
                Check(value.Parent.ClientSize.Width>=120,title+" "+entry.Key+" 数值区域不被挤窄");Check(value.Parent.ClientSize.Height>=30,title+" "+entry.Key+" 数值区域高度可显示完整字符");string rawUnit=PointCatalog.Get(entry.Key).Unit??"";bool unitless=rawUnit.Length==0||rawUnit=="原始值"||rawUnit=="原始码";Check(unitless?unit.Text.Length==0:unit.Text==rawUnit,title+" "+entry.Key+" 单位显示与点表一致");Check(label.ClientSize.Width>=120&&quality.ClientSize.Width>=120,title+" "+entry.Key+" 名称与质量文字完整可查看");
                string[] roles={"名称", "数值", "单位", "状态"};Label[] contents={label,value,unit,quality};
                for(int i=0;i<contents.Length;i++){if(i==2&&unitless)continue;Check(FitsOverviewContainers(contents[i],viewport),title+" "+entry.Key+" "+roles[i]+"完整位于卡片、流式布局和分组容器内，不被裁切 · "+DescribeBounds(contents[i]));}
                string[] lines=value.Text.Split(new[]{Environment.NewLine},StringSplitOptions.None);int maxWidth=0,lineHeight=0;foreach(string line in lines){Size measured=TextRenderer.MeasureText(String.IsNullOrEmpty(line)?" ":line,value.Font,new Size(Int32.MaxValue,Int32.MaxValue),TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);maxWidth=Math.Max(maxWidth,measured.Width);lineHeight=Math.Max(lineHeight,measured.Height);}
                if(value.AutoSize) {
                    // AutoSize 标签按内容自撑:校验不溢出父容器、行高不溢出所在行。
                    Check(value.ClientSize.Width<=value.Parent.ClientSize.Width+2,title+" "+entry.Key+" 数值未溢出数值行容器（"+value.Text.Replace(Environment.NewLine," / ")+"）");
                    Check(lineHeight*lines.Length<=value.Parent.ClientSize.Height+3,title+" "+entry.Key+" 数值行高未溢出数值行容器（"+(lineHeight*lines.Length)+" vs "+value.Parent.ClientSize.Height+"）");
                } else Check(maxWidth<=value.ClientSize.Width+2&&lineHeight*lines.Length<=value.ClientSize.Height+3,title+" "+entry.Key+" 数值绘制边界未裁切（"+value.Text.Replace(Environment.NewLine," / ")+"，测量"+maxWidth+"×"+(lineHeight*lines.Length)+"，区域"+value.ClientSize.Width+"×"+value.ClientSize.Height+"） · "+DescribeBounds(value));
            }
            Label d3=map["D3"][1];Check(d3.Text.Contains("E-08")||d3.Text.Contains("e-08"),title+" 微小非零值没有显示为零");
            Point p=points.First(x=>x.name=="D3");string before=d3.Text;form.UpdateObservation(new Observation{Point=p.name,Description=PointCatalog.Get(p).Label,Device=p.binding.device,Source="simulation",Mode=PointCatalog.Get(p).Mode,Quality="timeout",Status="timeout",Value="timeout",Number=null,Utc=DateTime.UtcNow,Unit=PointCatalog.Get(p).Unit});// 2026-11-06 起文案统一为"陈旧 Ns"（比"旧值"更能说明"这是多久以前的值"）。
            Check(d3.Text==before&&map["D3"][3].Text.Contains("陈旧"),title+" 通信故障时保留最后有效显示并标记陈旧（实际："+map["D3"][3].Text+"）");
            // 设定值是"命令"不是"读数"：卡片上必须同时给出实测与偏差，不能只有一个裸数字。
            Label setpointStatus=map["D2202"][3];
            Check(PointRole.IsSetpoint(PointCatalog.Get("D2202").Role),title+" D2202 在点表里是设定值角色");
            Check(setpointStatus.Text.Contains("实测")||setpointStatus.Text.Contains("设定"),
                title+" D2202 设定值卡片给出实测/偏差而不是裸数字（实际："+setpointStatus.Text+"）");
        }
        static bool FitsOverviewContainers(Control child,Panel viewport){
            bool foundViewport=false;
            for(Control parent=child.Parent;parent!=null;parent=parent.Parent){
                if(Object.ReferenceEquals(parent,viewport)){foundViewport=true;break;}
                Rectangle bounds=child.RectangleToScreen(child.ClientRectangle);
                Rectangle clip=parent.RectangleToScreen(parent.ClientRectangle);
                if(!clip.Contains(bounds))return false;
            }
            return foundViewport;
        }
        static string DescribeHistoryState(Control page,DataGridView grid){return "grid="+(grid==null?"null":grid.Rows.Count+" rows / "+grid.Columns.Count+" cols");}
        static void CapturePage(Form form,string output,int width,int height,string label,string page){string file=width+"x"+height+"-"+label.Replace(" ","-")+"-"+SafeName(page)+".png";using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,file));}}
        static int GetCounter(DashboardForm form,string field){FieldInfo f=typeof(DashboardForm).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic);return (int)f.GetValue(form);}
        static void InjectPreviewData(DashboardForm form,List<Point> points){
            DateTime now=DateTime.UtcNow;
            foreach(Point p in points){PointInfo info=PointCatalog.Get(p);string value=info.Mode=="U16 / I16"?"U16=0 / I16=0（符号/倍率待定）":info.Mode=="RAW"?"701":null;double? n=info.Mode=="U16 / I16"||info.Mode=="RAW"?null:(double?)(p.name=="D3"?0.0000000829:25.123456789);
                form.UpdateObservation(new Observation{Point=p.name,Description=info.Label,Device=p.binding.device,Source="simulation",Mode=info.Mode,Quality="good",Status="ok",Value=value,Number=n,Utc=now.AddMilliseconds(Array.IndexOf(points.ToArray(),p)),Unit=info.Unit,Milliseconds=12});}
        }
        static T Find<T>(Control root,Func<T,bool> match=null) where T:Control {foreach(Control child in root.Controls){T v=child as T;if(v!=null&&(match==null||match(v)))return v;T nested=Find(child,match);if(nested!=null)return nested;}return null;}
        static IEnumerable<T> All<T>(Control root) where T:Control {foreach(Control child in root.Controls){T value=child as T;if(value!=null)yield return value;foreach(T nested in All<T>(child))yield return nested;}}
        static string SafeName(string value){foreach(char c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');return value;}

        /// <summary>总览页各分组的标题（形如「温度 · 7」），用于诊断。</summary>
        static IEnumerable<string> SectionTitles(Control viewport) {
            if(viewport==null) yield break;
            foreach(Label title in All<Label>(viewport)) {
                if(title.Tag is GroupTitleInfo) yield return title.Text;
            }
        }

        /// <summary>
        /// 分组标题写的个数是否与该组实际可见卡片数一致。
        /// 标题的个数此前是建卡片时写死的，筛选后不同步 —— 界面在说假话。
        /// </summary>
        static bool HasStaleSectionCount(Control viewport) {
            if(viewport==null) return false;
            foreach(Control section in All<Control>(viewport)) {
                Label title=null;
                foreach(Control child in section.Controls) {
                    foreach(Control inner in child.Controls) {
                        var label=inner as Label;
                        if(label!=null&&label.Tag is GroupTitleInfo){title=label;break;}
                    }
                    if(title!=null) break;
                }
                if(title==null) continue;
                // 卡片是 section → flow → RoundedPanel 这一层，必须数直接子级；
                // 用 All<Control>(flow) 只拿到后代、数不到卡片自己。
                int visible=0;
                foreach(Control child in section.Controls) {
                    foreach(Control inner in child.Controls) {
                        if(inner is RoundedPanel&&inner.Visible) visible++;
                    }
                }
                if(!title.Text.EndsWith("·  "+visible.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}

