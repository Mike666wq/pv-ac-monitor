using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>One persisted-data timeline. Time selection and following are independent of acquisition.</summary>
    public sealed class TrendsPage : UserControl {
        readonly List<Point> points;readonly MonitorEngine engine;readonly string dataRoot;
        readonly ExperimentChart chart=new ExperimentChart{Dock=DockStyle.Fill,RequestExactSamples=true,UseExplicitGaps=true};
        readonly CheckedListBox devices=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false};
        readonly CheckedListBox channels=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false};
        readonly ComboBox source=new ComboBox{Width=110,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(3,0,3,0)};
        readonly DateTimePicker from=new DateTimePicker{Width=210,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Margin=new Padding(3,0,3,0)};
        readonly DateTimePicker to=new DateTimePicker{Width=210,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Margin=new Padding(3,0,3,0)};
        readonly ComboBox quick=new ComboBox{Width=150,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(3,0,3,0)};
        readonly CheckBox followLatest=new CheckBox{Text="跟随最新记录",Checked=true,AutoSize=true,Margin=new Padding(3,8,12,3)};
        readonly TextBox search=new TextBox{Width=160};
        readonly Label status=new Label{AutoSize=true,Text="读取最近已保存记录…"};
        readonly StyledActionButton query=new StyledActionButton{Text="应用范围",Width=112,BackColor=UiTheme.Blue,IconGlyph="⌕",Margin=new Padding(3,0,3,0)},latest=new StyledActionButton{Text="回到最新",Width=100,Margin=new Padding(3,0,3,0)},all=new StyledActionButton{Text="全选",Width=72},none=new StyledActionButton{Text="清空",Width=72};
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};
        readonly TrendTimeWindow window=new TrendTimeWindow();
        readonly Dictionary<string,bool> pointChecked=new Dictionary<string,bool>();
        readonly Dictionary<string,int> exactRequestIds=new Dictionary<string,int>();
        bool disposed,queryBusy,filtering,initializing=true,updatingControls,sourceChosen,rangeDirty,pendingRefresh;
        int generation,loadedGeneration=-1;DateTime loadedFrom,loadedTo,lastRevisionCheck=DateTime.MinValue;string loadedSource,lastRevision="";long lastCommitted=-2,lastCompleted=-1;
        CancellationTokenSource queryCancellation;
        public TrendsPage(List<Point> points,MonitorEngine engine,string appRoot){
            if(points==null)throw new ArgumentNullException("points");if(engine==null)throw new ArgumentNullException("engine");this.points=points;this.engine=engine;dataRoot=System.IO.Path.Combine(appRoot,"data","storage");
            Dock=DockStyle.Fill;AutoScaleMode=AutoScaleMode.Dpi;BackColor=UiTheme.Canvas;
            source.Items.AddRange(new object[]{"实机","模拟"});source.SelectedIndex=InitialSource()=="simulation"?1:0;
            quick.Items.AddRange(new object[]{"最近15分钟","最近1小时","最近2小时","今天（固定）","自定义"});quick.SelectedIndex=1;
            SetBounds(window.Resolve(DateTime.UtcNow,null));BuildLayout();Wire();PopulateFilters();initializing=false;
            engine.ConnectionChanged+=OnConnection;timer.Tick+=(s,e)=>Tick();
            HandleCreated+=(s,e)=>RequestRefresh();VisibleChanged+=(s,e)=>{if(Visible){timer.Start();RequestRefresh();}else timer.Stop();};
            Disposed+=(s,e)=>DisposePage();UpdateActions();
        }
        public void AttachTo(Control parent){if(parent==null)throw new ArgumentNullException("parent");parent.Controls.Add(this);}
        void BuildLayout(){
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(3) };root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));Controls.Add(root);
            // 两行工具区:行内容自适应宽度,不再硬编码 840px(消除右侧 39% 留白)。
            var toolbar=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(6,4,6,4),BackColor=Color.White };
            toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var viewRow=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,AutoScroll=false,Margin=Padding.Empty,Padding=Padding.Empty };
            viewRow.Controls.Add(TextLabel("数据来源"));viewRow.Controls.Add(new RoundedInputHost(source) { Width=122 });
            viewRow.Controls.Add(TextLabel("开始"));viewRow.Controls.Add(from);viewRow.Controls.Add(TextLabel("结束（不含）"));viewRow.Controls.Add(to);
            viewRow.Controls.Add(new RoundedInputHost(quick) { Width=142 });toolbar.Controls.Add(viewRow,0,0);
            var actionRow=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,AutoScroll=false,Margin=Padding.Empty,Padding=new Padding(0,2,0,0) };
            actionRow.Controls.Add(followLatest);actionRow.Controls.Add(query);actionRow.Controls.Add(latest);toolbar.Controls.Add(actionRow,0,1);
            root.Controls.Add(toolbar,0,0);
            var split=new SplitContainer { Size=new Size(700,500),Dock=DockStyle.Fill,Panel1MinSize=205,Panel2MinSize=350,SplitterWidth=6,SplitterDistance=265,BackColor=UiTheme.Canvas,BorderStyle=BorderStyle.None };
            // 设备列表固定高度(4 台设备,去掉底部死空间),测点列表吃掉剩余高度;搜索框拉满整行。
            var left=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(5),BackColor=Color.White };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Absolute,40));left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Absolute,100));left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var searchRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty,Padding=Padding.Empty };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.Controls.Add(new RoundedInputHost(search) { Dock=DockStyle.Fill },0,0);searchRow.Controls.Add(all,1,0);searchRow.Controls.Add(none,2,0);
            left.Controls.Add(TextLabel("搜索测点并按设备筛选"),0,0);left.Controls.Add(searchRow,0,1);left.Controls.Add(TextLabel("设备"),0,2);left.Controls.Add(devices,0,3);left.Controls.Add(TextLabel("测点"),0,4);left.Controls.Add(channels,0,5);split.Panel1.Controls.Add(left);
            var chartHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(6),BackColor=Color.White,AutoScroll=true };chart.Dock=DockStyle.Top;chartHost.Controls.Add(chart);chartHost.SizeChanged+=(s,e)=>{chart.Width=Math.Max(200,chartHost.ClientSize.Width-16);ResizeChart();};split.Panel2.Controls.Add(chartHost);root.Controls.Add(split,0,1);
            status.Dock=DockStyle.Fill;status.TextAlign=ContentAlignment.MiddleLeft;status.ForeColor=UiTheme.Muted;root.Controls.Add(status,0,2);
        }
        void Wire(){
            chart.LegendVisibilityChanged+=(id,visible)=>status.Text=PointCatalog.Get(id).Label+" · "+(visible?"已显示":"已隐藏");chart.ExactSampleRequested+=OnExactSampleRequested;
            chart.UserNavigated+=()=>{if(!followLatest.Checked||loadedGeneration!=generation)return;window.Fix(loadedFrom,loadedTo);SetFollowing(false);SetQuick(4);rangeDirty=false;pendingRefresh=false;InvalidateQuery();loadedGeneration=generation;status.Text="已暂停跟随以查看图表；双击恢复已应用范围，或点击“回到最新”。";UpdateActions();};
            latest.Click+=(s,e)=>ResumeFollowing();query.Click+=(s,e)=>ApplyRange();
            followLatest.CheckedChanged+=(s,e)=>{if(initializing||updatingControls)return;if(followLatest.Checked)ResumeFollowing();else FreezeDisplayed();};
            quick.SelectedIndexChanged+=(s,e)=>{if(initializing||updatingControls)return;int i=quick.SelectedIndex;
                if(i<3){window.Follow(TimeSpan.FromMinutes(i==0?15:i==1?60:120));SetFollowing(true);rangeDirty=false;}
                else if(i==3){DateTime now=DateTime.Now;DateTime end=new DateTime(now.Year,now.Month,now.Day,now.Hour,now.Minute,now.Second,DateTimeKind.Local).AddSeconds(1);window.Fix(DateTime.Today.ToUniversalTime(),end.ToUniversalTime());SetFollowing(false);SetBounds(window.Resolve(DateTime.UtcNow,null));rangeDirty=false;}
                else{FreezeDisplayed();return;}InvalidateQuery();RequestRefresh();
            };
            from.ValueChanged+=(s,e)=>TimeEdited();to.ValueChanged+=(s,e)=>TimeEdited();
            source.SelectedIndexChanged+=(s,e)=>{if(initializing||updatingControls)return;sourceChosen=true;lastRevision="";InvalidateQuery();RequestRefresh();};
            all.Click+=(s,e)=>CheckAll(true);none.Click+=(s,e)=>CheckAll(false);search.TextChanged+=(s,e)=>FilterPoints();
            devices.ItemCheck+=(s,e)=>{if(initializing||filtering||!IsHandleCreated)return;BeginInvoke((Action)(()=>{if(!disposed){FilterPoints();}}));};
            channels.ItemCheck+=(s,e)=>{if(initializing||filtering||!IsHandleCreated)return;string id=channels.Items[e.Index].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[id]=e.NewValue==CheckState.Checked;BeginInvoke((Action)(()=>{if(!disposed)SelectionChanged();}));};
        }
        string InitialSource(){if(engine.ActiveSource!=null)return engine.ActiveSource;try{bool serial=ExperimentHistory.Databases(dataRoot,"serial").Any(),sim=ExperimentHistory.Databases(dataRoot,"simulation").Any();if(sim&&!serial)return "simulation";}catch(System.IO.IOException){}catch(UnauthorizedAccessException){}return "serial";}
        string SelectedSource(){return (string)source.SelectedItem=="模拟"?"simulation":"serial";}
        void PopulateFilters(){foreach(string d in points.Select(p=>p.binding.device).Distinct())devices.Items.Add(d,true);foreach(Point p in points)pointChecked[p.name]=true;FilterPoints();}
        void FilterPoints(){if(disposed)return;for(int i=0;i<channels.Items.Count;i++){string id=channels.Items[i].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[id]=channels.GetItemChecked(i);}filtering=true;try{string text=search.Text??"";string[] chosen=devices.CheckedItems.Cast<string>().ToArray();channels.BeginUpdate();channels.Items.Clear();foreach(Point p in points)if(chosen.Contains(p.binding.device)&&(text.Length==0||p.name.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0||PointCatalog.Get(p.name).Label.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0||p.binding.device.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0)){int i=channels.Items.Add(p.name+" · "+PointCatalog.Get(p.name).Label);channels.SetItemChecked(i,pointChecked[p.name]);}channels.EndUpdate();}finally{filtering=false;}SelectionChanged();}
        void CheckAll(bool value){filtering=true;try{for(int i=0;i<channels.Items.Count;i++){string id=channels.Items[i].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[id]=value;channels.SetItemChecked(i,value);}}finally{filtering=false;}SelectionChanged();}
        string[] SelectedPoints(){return channels.CheckedItems.Cast<string>().Select(x=>x.Split(new[]{" · "},StringSplitOptions.None)[0]).ToArray();}
        void SelectionChanged(){if(initializing)return;InvalidateQuery();RequestRefresh();}
        void TimeEdited(){if(initializing||updatingControls)return;SetFollowing(false);SetQuick(4);rangeDirty=true;InvalidateQuery();status.Text="时间范围已修改，点击“应用范围”查询；后台采集不受影响。";UpdateActions();}
        bool ValidRange(){return DisplayedSecondUtc(to)>DisplayedSecondUtc(from);}
        void ApplyRange(){if(!ValidRange()){status.Text="结束时间必须晚于开始时间。";return;}if(!followLatest.Checked)window.Fix(DisplayedSecondUtc(from),DisplayedSecondUtc(to));rangeDirty=false;InvalidateQuery();RequestRefresh();}
        void FreezeDisplayed(){if(!ValidRange()){status.Text="结束时间必须晚于开始时间。";return;}window.Fix(DisplayedSecondUtc(from),DisplayedSecondUtc(to));SetFollowing(false);SetQuick(4);rangeDirty=false;InvalidateQuery();RequestRefresh();}
        void ResumeFollowing(){if(rangeDirty&&ValidRange())window.Fix(DisplayedSecondUtc(from),DisplayedSecondUtc(to));window.Resume();SetFollowing(true);rangeDirty=false;SetQuick(window.Span==TimeSpan.FromMinutes(15)?0:window.Span==TimeSpan.FromHours(1)?1:window.Span==TimeSpan.FromHours(2)?2:4);InvalidateQuery();RequestRefresh();}
        void SetFollowing(bool value){updatingControls=true;try{followLatest.Checked=value;}finally{updatingControls=false;}}
        void SetQuick(int index){updatingControls=true;try{quick.SelectedIndex=index;}finally{updatingControls=false;}}
        void SetBounds(TrendBounds bounds){updatingControls=true;try{from.Value=bounds.FromUtc.ToLocalTime();to.Value=bounds.ToUtc.ToLocalTime();}finally{updatingControls=false;}}
        void ResizeChart(){if(!chart.IsDisposed)chart.Height=Math.Max(210,chart.RequiredHeight);}
        void UpdateActions(){query.Enabled=!queryBusy&&SelectedPoints().Length>0&&ValidRange();latest.Enabled=true;}
        void InvalidateQuery(){generation++;if(queryCancellation!=null)try{queryCancellation.Cancel();}catch(ObjectDisposedException){}}
        string Revision(){return String.Join("|",ExperimentHistory.Databases(dataRoot,SelectedSource()).Select(p=>{var file=new System.IO.FileInfo(p);return p+":"+file.Length+":"+file.LastWriteTimeUtc.Ticks;}));}
        void Tick(){if(disposed||!Visible||!followLatest.Checked||rangeDirty)return;ExperimentStore store=engine.Store;long committed=store!=null&&engine.ActiveSource==SelectedSource()?store.Committed:-1,completed=engine.CompletedCaptures;
            bool changed=committed!=lastCommitted||completed!=lastCompleted;lastCommitted=committed;lastCompleted=completed;
            if(DateTime.UtcNow-lastRevisionCheck>TimeSpan.FromSeconds(5)){lastRevisionCheck=DateTime.UtcNow;try{string revision=Revision();changed|=revision!=lastRevision;lastRevision=revision;}catch(Exception ex){status.Text="读取本地记录状态失败："+ex.Message;return;}}
            if(changed)RequestRefresh();
        }
        async void RequestRefresh(){
            if(disposed||initializing||!Visible||!IsHandleCreated)return;UpdateActions();
            if(rangeDirty){status.Text="时间范围待应用；点击“应用范围”读取选定范围。";return;}
            string[] selected=SelectedPoints();if(selected.Length==0){chart.Clear();status.Text="请至少选择一个测点。";return;}
            if(queryBusy){pendingRefresh=true;return;}
            queryBusy=true;query.Enabled=false;int token=generation;var cancel=new CancellationTokenSource();queryCancellation=cancel;
            string selectedSource=SelectedSource();TrendTimeWindow snapshot=window.Copy();status.Text="正在读取所选来源与时间范围的已保存记录…";
            try{
                int buckets=Math.Max(300,Math.Min(2000,chart.ClientSize.Width*2));int period=engine.CurrentPeriodSeconds;
                TrendSeriesResult result=await Task.Run(()=>TrendSeriesLoader.Read(dataRoot,selectedSource,selected,points,snapshot,buckets,period,cancel.Token));
                if(disposed||token!=generation)return;
                loadedFrom=result.Bounds.FromUtc;loadedTo=result.Bounds.ToUtc;loadedSource=result.Source;loadedGeneration=token;
                string[] drawable=selected.Where(id=>result.Series.ContainsKey(id)&&result.Series[id].Any(sample=>sample.Value.HasValue)).ToArray();
                if(drawable.Length==0)drawable=selected.Where(id=>result.Series.ContainsKey(id)&&result.Series[id].Any()).ToArray();
                SetBounds(result.Bounds);chart.SelectPoints(drawable);chart.SetSeriesData(result.Series);chart.SetTimeRange(loadedFrom,loadedTo,false);chart.Height=Math.Max(210,chart.RequiredHeight);
                string caption=(snapshot.Following?"跟随最新":"固定范围")+" · "+(result.Source=="serial"?"实机":"模拟")+" · "+result.Rows.ToString("N0")+" 条原始记录";
                if(result.Rows==0)caption+=" · 所选范围无记录，请检查来源、时间和测点";
                else if(!result.Series.Values.SelectMany(x=>x).Any(x=>x.Value.HasValue))caption+=" · 只有原始码或异常记录，无有效工程数值";
                else if(drawable.Length<selected.Length)caption+=" · 可绘制 "+drawable.Length+" 个测点，其余测点无有效工程数值";
                if(result.LatestUtc.HasValue)caption+=" · 数据截止 "+result.LatestUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                status.Text=caption;
            }catch(OperationCanceledException){}catch(Exception ex){if(!disposed&&token==generation){chart.Clear();status.Text="趋势记录查询失败："+ex.Message;}}
            finally{queryBusy=false;if(queryCancellation==cancel)queryCancellation=null;cancel.Dispose();if(!disposed){UpdateActions();bool pending=pendingRefresh;pendingRefresh=false;if(pending&&!rangeDirty&&Visible)BeginInvoke((Action)(()=>RequestRefresh()));}}
        }
        void Dispatch(Action action){if(disposed)return;try{Control target=IsHandleCreated?this:(Control)FindForm();if(target!=null&&target.IsHandleCreated)target.BeginInvoke((Action)(()=>{if(!disposed)action();}));}catch(InvalidOperationException){}}
        void OnConnection(bool connected,string src){Dispatch(()=>{if(!sourceChosen&&followLatest.Checked&&(src=="serial"||src=="simulation")){updatingControls=true;try{source.SelectedIndex=src=="serial"?0:1;}finally{updatingControls=false;}InvalidateQuery();RequestRefresh();}});}
        void OnExactSampleRequested(string point,DateTime target){if(disposed||queryBusy||loadedGeneration!=generation||loadedSource==null)return;int token=generation,request;exactRequestIds.TryGetValue(point,out request);int id=++request;exactRequestIds[point]=id;int span=Math.Max(60,engine.CurrentPeriodSeconds*4);DateTime a=target.AddSeconds(-span),b=target.AddSeconds(span);if(a<loadedFrom)a=loadedFrom;if(b>loadedTo)b=loadedTo;if(b<=a)return;Point p=points.FirstOrDefault(x=>x.name==point);if(p==null)return;
            var filter=new HistoryFilter{FromUtc=a,ToUtc=b,Source=loadedSource,Points=new[]{point},Devices=new[]{p.binding.device}};
            Task.Run(()=>{StoredObservation best=null;long distance=long.MaxValue;ExperimentHistory.Stream(dataRoot,filter,row=>{long d=Math.Abs((row.Utc-target).Ticks);if(d<distance){best=row;distance=d;}},true);return best;}).ContinueWith(t=>{if(disposed||t.IsFaulted||t.IsCanceled||token!=generation||id!=exactRequestIds[point]||t.Result==null)return;var row=t.Result;double? value=row.Number;if(!value.HasValue&&row.RawNumber.HasValue&&PointCatalog.Get(point).Mode!="U16 / I16")value=row.RawNumber;chart.SetExactSample(point,new ChartSample{Utc=row.Utc,Value=value,Quality=row.Quality},target);},TaskScheduler.FromCurrentSynchronizationContext());
        }
        static DateTime DisplayedSecondUtc(DateTimePicker picker){DateTime value=picker.Value;return new DateTime(value.Year,value.Month,value.Day,value.Hour,value.Minute,value.Second,DateTimeKind.Local).ToUniversalTime();}
        static Label TextLabel(string text){return new Label{Text=text,AutoSize=true,Padding=new Padding(2,7,2,0),ForeColor=UiTheme.Muted};}
        void DisposePage(){if(disposed)return;disposed=true;InvalidateQuery();timer.Stop();timer.Dispose();engine.ConnectionChanged-=OnConnection;}
    }
}
