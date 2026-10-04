using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>Standalone realtime/history trend workspace; historic queries ignore the records table page size.</summary>
    public sealed class TrendsPage : UserControl {
        readonly List<Point> points; readonly MonitorEngine engine; readonly string dataRoot;
        readonly ExperimentChart chart=new ExperimentChart { Dock=DockStyle.Fill };
        readonly CheckedListBox devices=new CheckedListBox { Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false };
        readonly CheckedListBox channels=new CheckedListBox { Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false };
        readonly ComboBox source=new ComboBox { Width=110,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(3,0,3,0) };
        readonly ComboBox mode=new ComboBox { Width=130,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(3,0,3,0) };
        readonly DateTimePicker from=new DateTimePicker { Width=210,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Margin=new Padding(3,0,3,0) };
        readonly DateTimePicker to=new DateTimePicker { Width=210,Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Margin=new Padding(3,0,3,0) };
        readonly ComboBox quick=new ComboBox { Width=130,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(3,0,3,0) };
        readonly TextBox search=new TextBox { Width=160 };
        readonly Label status=new Label { AutoSize=true,Text="实时趋势等待采样；滚轮缩放、拖动平移、双击回到最新。" };
        readonly Button query=new Button { Text="查询完整范围",Width=112,Margin=new Padding(3,0,3,0) },latest=new Button { Text="回到最新",Width=92,Margin=new Padding(3,0,3,0) },all=new Button { Text="全选",Width=65 },none=new Button { Text="清空",Width=65 };
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=1000 };
        readonly Dictionary<string,List<ChartSample>> liveData=new Dictionary<string,List<ChartSample>>();
        readonly Dictionary<string,bool> pointChecked=new Dictionary<string,bool>();
        bool disposed,queryBusy,filtering,initializing=true; int generation; string liveSource,historySource;DateTime historyFrom,historyTo;CancellationTokenSource queryCancellation;readonly Dictionary<string,int> exactRequestIds=new Dictionary<string,int>();

        public TrendsPage(List<Point> points,MonitorEngine engine,string appRoot) {
            if(points==null)throw new ArgumentNullException("points");if(engine==null)throw new ArgumentNullException("engine");this.points=points;this.engine=engine;dataRoot=System.IO.Path.Combine(appRoot,"data","storage");
            Dock=DockStyle.Fill;AutoScaleMode=AutoScaleMode.Dpi;BackColor=UiTheme.Canvas;
            mode.Items.AddRange(new object[]{"实时趋势","历史趋势"});mode.SelectedIndex=0;
            source.Items.AddRange(new object[]{"实机","模拟"});source.SelectedIndex=engine.ActiveSource=="serial"?0:1;
            quick.Items.AddRange(new object[]{"快捷范围…","最近 15 分钟","最近 1 小时","最近 2 小时","今天"});quick.SelectedIndex=0;
            DateTime now=DateTime.Now;to.Value=now;from.Value=now.AddHours(-1);
            BuildLayout();Wire();PopulateFilters();SwitchMode();initializing=false;
            engine.ObservationReceived+=OnObservation;engine.ConnectionChanged+=OnConnection;
            timer.Tick+=(s,e)=>{if(mode.SelectedIndex==0&&chart.Visible)chart.Invalidate();};timer.Start();
            Disposed+=(s,e)=>DisposePage();
        }

        public void AttachTo(Control parent){if(parent==null)throw new ArgumentNullException("parent");parent.Controls.Add(this);}

        void BuildLayout(){
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(3) };root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));Controls.Add(root);
            var toolbar=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(4,3,2,2),BackColor=Color.White };
            var viewRow=new FlowLayoutPanel { Width=840,Height=34,AutoSize=false,WrapContents=false,AutoScroll=false,Margin=Padding.Empty,Padding=Padding.Empty };
            viewRow.Controls.Add(TextLabel("视图"));viewRow.Controls.Add(mode);viewRow.Controls.Add(TextLabel("来源"));viewRow.Controls.Add(source);toolbar.Controls.Add(viewRow);
            var timeRow=new FlowLayoutPanel { Width=840,Height=34,AutoSize=false,WrapContents=false,AutoScroll=false,Margin=Padding.Empty,Padding=Padding.Empty };
            timeRow.Controls.Add(TextLabel("时间从"));timeRow.Controls.Add(from);timeRow.Controls.Add(TextLabel("到"));timeRow.Controls.Add(to);toolbar.Controls.Add(timeRow);
            var actionRow=new FlowLayoutPanel { Width=840,Height=34,AutoSize=false,WrapContents=false,AutoScroll=false,Margin=Padding.Empty,Padding=Padding.Empty };
            actionRow.Controls.Add(quick);actionRow.Controls.Add(query);actionRow.Controls.Add(latest);toolbar.Controls.Add(actionRow);
            root.Controls.Add(toolbar,0,0);
            var split=new SplitContainer { Size=new Size(700,500),Dock=DockStyle.Fill,Panel1MinSize=205,Panel2MinSize=350,SplitterWidth=6,SplitterDistance=265,BackColor=UiTheme.Canvas,BorderStyle=BorderStyle.None };
            var left=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(5),BackColor=Color.White };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Absolute,32));left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Percent,36));left.RowStyles.Add(new RowStyle(SizeType.Absolute,24));left.RowStyles.Add(new RowStyle(SizeType.Percent,64));
            search.Dock=DockStyle.Fill;var searchRow=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,Margin=Padding.Empty };all.Width=58;none.Width=58;searchRow.Controls.Add(search);searchRow.Controls.Add(all);searchRow.Controls.Add(none);
            left.Controls.Add(TextLabel("搜索测点并按设备筛选"),0,0);left.Controls.Add(searchRow,0,1);left.Controls.Add(TextLabel("设备"),0,2);left.Controls.Add(devices,0,3);left.Controls.Add(TextLabel("测点"),0,4);left.Controls.Add(channels,0,5);split.Panel1.Controls.Add(left);
            var chartHost=new Panel { Dock=DockStyle.Fill,Padding=new Padding(6),BackColor=Color.White,AutoScroll=true };chart.Dock=DockStyle.Top;chartHost.Controls.Add(chart);chartHost.SizeChanged+=(s,e)=>{chart.Width=Math.Max(200,chartHost.ClientSize.Width-16);ResizeChart();};split.Panel2.Controls.Add(chartHost);root.Controls.Add(split,0,1);
            status.Dock=DockStyle.Fill;status.TextAlign=ContentAlignment.MiddleLeft;status.ForeColor=UiTheme.Muted;root.Controls.Add(status,0,2);
            latest.Click+=(s,e)=>{mode.SelectedIndex=0;chart.SetWindowMinutes(30);};
            all.Click+=(s,e)=>CheckAll(true);none.Click+=(s,e)=>CheckAll(false);
            search.TextChanged+=(s,e)=>FilterPoints();
            devices.ItemCheck+=(s,e)=>{if(initializing||filtering||!IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(!initializing&&!filtering){FilterPoints();UpdateLiveSelection();}}));}catch(InvalidOperationException){}};
            channels.ItemCheck+=(s,e)=>{if(initializing||filtering||!IsHandleCreated)return;string name=channels.Items[e.Index].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[name]=e.NewValue==CheckState.Checked;try{BeginInvoke((Action)(()=>{if(!initializing)UpdateLiveSelection();}));}catch(InvalidOperationException){}};
            quick.SelectedIndexChanged+=(s,e)=>{if(quick.SelectedIndex==0)return;DateTime n=DateTime.Now;to.Value=n;switch(quick.SelectedIndex){case 1:from.Value=n.AddMinutes(-15);break;case 2:from.Value=n.AddHours(-1);break;case 3:from.Value=n.AddHours(-2);break;case 4:from.Value=n.Date;break;}quick.SelectedIndex=0;};
            query.Click+=async(s,e)=>await QueryHistory();mode.SelectedIndexChanged+=(s,e)=>SwitchMode();
            EventHandler rangeChanged=(s,e)=>{if(mode.SelectedIndex==1){InvalidateHistoryQuery();status.Text="时间条件已更改 · 点击查询加载历史趋势";}};from.ValueChanged+=rangeChanged;to.ValueChanged+=rangeChanged;
            source.SelectedIndexChanged+=(s,e)=>{if(mode.SelectedIndex==1){InvalidateHistoryQuery();status.Text="来源已更改 · 点击查询加载历史趋势";}else ClearLive();};
        }
        void Wire(){chart.LegendVisibilityChanged+=(id,visible)=>status.Text=PointCatalog.Get(id).Label+" · "+(visible?"已显示":"已隐藏");chart.ExactSampleRequested+=OnExactSampleRequested;}
        void PopulateFilters(){foreach(string d in points.Select(p=>p.binding.device).Distinct())devices.Items.Add(d,true);foreach(Point p in points)pointChecked[p.name]=true;FilterPoints();}
        void FilterPoints(){if(disposed)return;for(int i=0;i<channels.Items.Count;i++){string id=channels.Items[i].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[id]=channels.GetItemChecked(i);}filtering=true;try{string text=search.Text??"";string[] selectedDevices=devices.CheckedItems.Cast<string>().ToArray();channels.BeginUpdate();channels.Items.Clear();foreach(Point p in points){bool show=selectedDevices.Contains(p.binding.device)&&(text.Length==0||p.name.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0||PointCatalog.Get(p).Label.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0||p.binding.device.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0);if(show){int index=channels.Items.Add(p.name+" · "+PointCatalog.Get(p).Label);channels.SetItemChecked(index,pointChecked[p.name]);}}channels.EndUpdate();}finally{filtering=false;}UpdateLiveSelection();}
        void CheckAll(bool value){for(int i=0;i<channels.Items.Count;i++){string id=channels.Items[i].ToString().Split(new[]{" · "},StringSplitOptions.None)[0];pointChecked[id]=value;channels.SetItemChecked(i,value);}UpdateLiveSelection();}
        string[] SelectedPoints(){return channels.CheckedItems.Cast<string>().Select(x=>x.Split(new[]{" · "},StringSplitOptions.None)[0]).ToArray();}
        void UpdateLiveSelection(){if(mode.SelectedIndex==0)chart.SelectPoints(SelectedPoints());else{InvalidateHistoryQuery();if(!queryBusy)status.Text="测点筛选已更改 · 点击查询加载完整历史趋势";}ResizeChart();}
        void ResizeChart(){if(chart.IsDisposed)return;chart.Height=Math.Max(210,chart.RequiredHeight);}
        void InvalidateHistoryQuery(){generation++;if(queryCancellation!=null)try{queryCancellation.Cancel();}catch{}}
        void SwitchMode(){InvalidateHistoryQuery();bool hist=mode.SelectedIndex==1;chart.RequestExactSamples=hist;from.Enabled=to.Enabled=quick.Enabled=query.Enabled=hist;source.Enabled=hist;latest.Enabled=!hist;if(hist){chart.SetSeriesData(new Dictionary<string,IEnumerable<ChartSample>>());status.Text="选择时间范围及测点，查询整个范围的历史数据。";}else{ClearLive();status.Text="实时趋势等待采样；滚轮缩放、拖动平移、双击回到最新。";chart.SelectPoints(SelectedPoints());}}
        void ClearLive(){liveData.Clear();chart.Clear();liveSource=engine.ActiveSource;UpdateLiveSelection();}
        void OnConnection(bool connected,string src){if(disposed)return;try{BeginInvoke((Action)(()=>{if(!connected||liveSource!=src)ClearLive();}));}catch{}}
        void OnObservation(Observation o){if(disposed||o==null)return;try{BeginInvoke((Action)(()=>{if(disposed||mode.SelectedIndex!=0)return;if(liveSource!=o.Source){liveData.Clear();chart.Clear();liveSource=o.Source;}List<ChartSample> list;if(!liveData.TryGetValue(o.Point,out list)){list=new List<ChartSample>();liveData[o.Point]=list;}list.Add(new ChartSample{Utc=o.Utc,Value=o.Quality=="good"?o.Number:null,Quality=o.Quality});if(list.Count>12000)list.RemoveRange(0,list.Count-12000);chart.Add(o);status.Text="实时趋势 · "+(o.Source=="serial"?"实机":"模拟")+" · "+(o.Quality=="good"?"最新 "+PointCatalog.Get(o.Point).Label+" = "+(o.Number.HasValue?o.Number.Value.ToString("G9",CultureInfo.InvariantCulture):o.Value):"异常点已保留断点");}));}catch{}}

        async Task QueryHistory(){if(queryBusy)return;if(to.Value<=from.Value){MessageBox.Show(this,"结束时间必须晚于开始时间。","历史趋势",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}string[] selected=SelectedPoints();if(selected.Length==0){MessageBox.Show(this,"请至少选择一个测点。","历史趋势",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}queryBusy=true;int token=++generation;queryCancellation=new CancellationTokenSource();CancellationTokenSource cancel=queryCancellation;query.Enabled=false;status.Text="正在逐库读取完整筛选范围并压缩趋势数据…";
            try{DateTime a=DisplayedSecondUtc(from),b=DisplayedSecondUtc(to);string src=(string)source.SelectedItem=="实机"?"serial":"simulation";historyFrom=a;historyTo=b;historySource=src;var filter=new HistoryFilter{FromUtc=a,ToUtc=b,Source=src,Points=selected,Devices=points.Where(p=>selected.Contains(p.name)).Select(p=>p.binding.device).Distinct().ToArray()};int bucketCount=Math.Max(300,Math.Min(2000,chart.ClientSize.Width*2));TrendAccumulator[] acc=selected.Select(x=>new TrendAccumulator(x,a,b,bucketCount,engine.CurrentPeriodSeconds)).ToArray();long rowsRead=0;
                await Task.Run(()=>ExperimentHistory.Stream(dataRoot,filter,row=>{rowsRead++;TrendAccumulator s=acc.FirstOrDefault(x=>x.Point==row.Point);if(s==null)return;double? value=row.Number;if(!value.HasValue&&row.RawNumber.HasValue&&PointCatalog.Get(row.Point).Mode!="U16 / I16")value=row.RawNumber;s.Add(new ChartSample{Utc=row.Utc,Value=value,Quality=row.Quality});},true,cancel.Token));
                if(disposed||token!=generation)return;var series=acc.ToDictionary(x=>x.Point,x=>(IEnumerable<ChartSample>)x.Finish());chart.SelectPoints(selected);chart.SetSeriesData(series);chart.SetTimeRange(a,b,false);ResizeChart();status.Text="历史趋势完成 · "+rowsRead.ToString("N0")+" 条原始记录 · "+selected.Length+" 个测点 · "+a.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" 至 "+b.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" · 桶内保留首末及极值，缺失保留断点";
            }catch(Exception ex){if(!disposed&&token==generation){chart.Clear();status.Text="历史趋势查询失败："+ex.Message;}}finally{queryBusy=false;if(queryCancellation==cancel)queryCancellation=null;cancel.Dispose();if(!disposed)query.Enabled=mode.SelectedIndex==1;}}

        void OnExactSampleRequested(string point,DateTime target){if(disposed||mode.SelectedIndex!=1||historyTo<=historyFrom||historySource==null)return;int request;exactRequestIds.TryGetValue(point,out request);request++;exactRequestIds[point]=request;int id=request;int span=Math.Max(60,engine.CurrentPeriodSeconds*4);DateTime a=target.AddSeconds(-span),b=target.AddSeconds(span);if(a<historyFrom)a=historyFrom;if(b>historyTo)b=historyTo;if(b<=a)return;Point p=points.FirstOrDefault(x=>x.name==point);if(p==null)return;var filter=new HistoryFilter{FromUtc=a,ToUtc=b,Source=historySource,Points=new[]{point},Devices=new[]{p.binding.device}};Task.Run(()=>{StoredObservation best=null;long bestDistance=long.MaxValue;ExperimentHistory.Stream(dataRoot,filter,row=>{long distance=Math.Abs((row.Utc-target).Ticks);if(distance<bestDistance){best=row;bestDistance=distance;}},true);return best;}).ContinueWith(t=>{if(t.IsFaulted||t.IsCanceled||disposed||id!=exactRequestIds[point]||t.Result==null)return;StoredObservation row=t.Result;double? n=row.Number;if(!n.HasValue&&row.RawNumber.HasValue&&PointCatalog.Get(point).Mode!="U16 / I16")n=row.RawNumber;chart.SetExactSample(point,new ChartSample{Utc=row.Utc,Value=n,Quality=row.Quality},target);},TaskScheduler.FromCurrentSynchronizationContext());}

        // The picker only presents whole seconds; normalize to the displayed local wall-clock
        // value before conversion so invisible sub-second ticks cannot shift the query range.
        static DateTime DisplayedSecondUtc(DateTimePicker picker){DateTime value=picker.Value;DateTime displayed=new DateTime(value.Year,value.Month,value.Day,value.Hour,value.Minute,value.Second,DateTimeKind.Local);return displayed.ToUniversalTime();}

        internal sealed class TrendAccumulator {
            sealed class Run {
                public int Id;public ChartSample First,Min,Max,Last;
                public Run(int id,ChartSample sample){Id=id;First=Min=Max=Last=sample;}
                public void Add(ChartSample sample){if(sample.Value.Value<Min.Value.Value)Min=sample;if(sample.Value.Value>Max.Value.Value)Max=sample;Last=sample;}
                public IEnumerable<ChartSample> Samples(){return new[]{First,Min,Max,Last}.Distinct();}
            }
            readonly DateTime from,to;readonly int buckets,gapSeconds;readonly List<ChartSample> output=new List<ChartSample>();
            int currentBucket=-1,nextRunId;Run firstRun,lastRun;ChartSample bucketMin,bucketMax,lastGood,gapMarker;int minRunId,maxRunId;bool bucketHasGood,gapSeen,leadingGap,broken;public string Point;
            // A bucket emits at most the first/last run plus the runs containing the global extrema.
            // Every retained run is separated from the next by a null sample, so compression never
            // draws a line across a retained communication gap. The amount stored is O(bucket count).
            public int OutputCount { get { return output.Count+(bucketHasGood?20:gapSeen?1:0); } }
            public TrendAccumulator(string point,DateTime from,DateTime to,int buckets,int period){if(buckets<1)throw new ArgumentOutOfRangeException("buckets");Point=point;this.from=from.ToUniversalTime();this.to=to.ToUniversalTime();this.buckets=buckets;gapSeconds=Math.Max(30,period*3);}
            int BucketFor(DateTime utc){double span=Math.Max(.001,(to-from).TotalSeconds);return (int)Math.Max(0,Math.Min(buckets-1,(utc.ToUniversalTime()-from).TotalSeconds/span*buckets));}
            void BeginBucket(int bucket){currentBucket=bucket;nextRunId=0;firstRun=lastRun=null;bucketMin=bucketMax=null;minRunId=maxRunId=0;bucketHasGood=false;gapSeen=leadingGap=false;gapMarker=null;}
            void MarkGap(ChartSample at,bool leading){if(!gapSeen){gapSeen=true;leadingGap=leading;gapMarker=new ChartSample{Utc=at.Utc.ToUniversalTime(),Quality=String.IsNullOrEmpty(at.Quality)?"gap":at.Quality,Value=null};}}
            public void Add(ChartSample sample){if(sample==null)throw new ArgumentNullException("sample");int bucket=BucketFor(sample.Utc);if(currentBucket!=bucket){FlushBucket();BeginBucket(bucket);}bool good=sample.Value.HasValue&&sample.Quality=="good";
                if(!good){MarkGap(sample,!bucketHasGood);broken=true;lastGood=null;return;}
                if(lastGood!=null&&(sample.Utc.ToUniversalTime()-lastGood.Utc.ToUniversalTime()).TotalSeconds>gapSeconds){MarkGap(new ChartSample{Utc=sample.Utc.AddTicks(-1),Quality="gap"},!bucketHasGood);broken=true;}
                Run run;if(lastRun==null||broken){run=new Run(nextRunId++,sample);if(firstRun==null)firstRun=run;lastRun=run;broken=false;}else{run=lastRun;run.Add(sample);}
                if(bucketMin==null||sample.Value.Value<bucketMin.Value.Value){bucketMin=sample;minRunId=run.Id;}if(bucketMax==null||sample.Value.Value>bucketMax.Value.Value){bucketMax=sample;maxRunId=run.Id;}bucketHasGood=true;lastGood=sample;
            }
            static void AddUnique(List<ChartSample> target,ChartSample sample){if(sample!=null&&!target.Contains(sample))target.Add(sample);}
            static long MidpointTicks(long a,long b){return a>=b?b: a+(b-a)/2;}
            void AddGapBetween(List<ChartSample> target,DateTime previous,DateTime next,string quality){long ticks=MidpointTicks(previous.Ticks,next.Ticks);target.Add(new ChartSample{Utc=new DateTime(ticks,DateTimeKind.Utc),Quality=quality??"gap",Value=null});}
            void FlushBucket(){if(currentBucket<0)return;if(!bucketHasGood){if(gapSeen)output.Add(gapMarker);currentBucket=-1;return;}
                var groups=new SortedDictionary<int,List<ChartSample>>();Action<int,IEnumerable<ChartSample>> add=(id,samples)=>{List<ChartSample> list;if(!groups.TryGetValue(id,out list)){list=new List<ChartSample>();groups[id]=list;}foreach(ChartSample s in samples)AddUnique(list,s);};
                if(!gapSeen){add(firstRun.Id,firstRun.Samples());}
                else{
                    add(firstRun.Id,firstRun.Samples());add(lastRun.Id,lastRun.Samples());
                    if(minRunId!=firstRun.Id&&minRunId!=lastRun.Id)add(minRunId,new[]{bucketMin});
                    if(maxRunId!=firstRun.Id&&maxRunId!=lastRun.Id)add(maxRunId,new[]{bucketMax});
                    if(minRunId==maxRunId&&minRunId!=firstRun.Id&&minRunId!=lastRun.Id)add(minRunId,new[]{bucketMin,bucketMax});
                }
                bool first=true;DateTime previous=DateTime.MinValue;
                foreach(var group in groups){List<ChartSample> samples=group.Value.OrderBy(x=>x.Utc).ToList();if(samples.Count==0)continue;DateTime start=samples[0].Utc.ToUniversalTime();if(first){if(leadingGap&&gapMarker!=null)output.Add(gapMarker);first=false;}else AddGapBetween(output,previous,start,"gap");output.AddRange(samples);previous=samples[samples.Count-1].Utc.ToUniversalTime();}
                if(gapSeen&&!broken&&gapMarker!=null&&gapMarker.Utc.ToUniversalTime()>previous){AddGapBetween(output,previous,gapMarker.Utc.ToUniversalTime(),gapMarker.Quality);}
                else if(gapSeen&&broken&&gapMarker!=null&&(!leadingGap||gapMarker.Utc.ToUniversalTime()>=previous)){AddGapBetween(output,previous,gapMarker.Utc.ToUniversalTime(),gapMarker.Quality);}
                currentBucket=-1;firstRun=lastRun=null;bucketMin=bucketMax=null;bucketHasGood=gapSeen=leadingGap=false;gapMarker=null;
            }
            public List<ChartSample> Finish(){FlushBucket();return output.OrderBy(x=>x.Utc).ToList();}
        }
        static Label TextLabel(string text){return new Label{Text=text,AutoSize=true,Padding=new Padding(2,7,2,0),ForeColor=UiTheme.Muted};}
        void DisposePage(){if(disposed)return;disposed=true;InvalidateHistoryQuery();timer.Stop();timer.Dispose();engine.ObservationReceived-=OnObservation;engine.ConnectionChanged-=OnConnection;}
    }
}
