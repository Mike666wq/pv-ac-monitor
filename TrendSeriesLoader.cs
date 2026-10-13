using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ExperimentMonitor {
    internal sealed class TrendSeriesResult {
        public TrendBounds Bounds;public DateTime? LatestUtc;public long Rows;public string Source;
        public Dictionary<string,IEnumerable<ChartSample>> Series;
    }
    internal static class TrendSeriesLoader {
        // Both fixed and following views cross the same read-only database seam.
        public static TrendSeriesResult Read(string root,string source,string[] selected,List<Point> points,TrendTimeWindow window,int buckets,int period,CancellationToken token){
            token.ThrowIfCancellationRequested();
            var latestFilter=new HistoryFilter{Source=source,FromUtc=new DateTime(0,DateTimeKind.Utc),ToUtc=new DateTime(DateTime.MaxValue.Ticks,DateTimeKind.Utc)};
            StoredObservation latest=ExperimentHistory.QueryPage(root,latestFilter,0,1).FirstOrDefault();
            var bounds=window.Resolve(DateTime.UtcNow,latest==null?(DateTime?)null:latest.Utc);
            var filter=new HistoryFilter{Source=source,FromUtc=bounds.FromUtc,ToUtc=bounds.ToUtc,Points=selected,Devices=points.Where(p=>selected.Contains(p.name)).Select(p=>p.binding.device).Distinct().ToArray()};
            var acc=selected.ToDictionary(id=>id,id=>new Accumulator(id,bounds.FromUtc,bounds.ToUtc,buckets,period));long rows=0;
            ExperimentHistory.Stream(root,filter,row=>{rows++;Accumulator target;if(!acc.TryGetValue(row.Point,out target))return;double? value=row.Number;if(!value.HasValue&&row.RawNumber.HasValue&&PointCatalog.Get(row.Point).Mode!="U16 / I16")value=row.RawNumber;target.Add(new ChartSample{Utc=row.Utc,Value=value,Quality=row.Quality});},true,token);
            return new TrendSeriesResult{Bounds=bounds,LatestUtc=latest==null?(DateTime?)null:latest.Utc,Rows=rows,Source=source,Series=acc.ToDictionary(x=>x.Key,x=>(IEnumerable<ChartSample>)x.Value.Finish())};
        }
        internal sealed class Accumulator {
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
            public Accumulator(string point,DateTime from,DateTime to,int buckets,int period){if(buckets<1)throw new ArgumentOutOfRangeException("buckets");Point=point;this.from=from.ToUniversalTime();this.to=to.ToUniversalTime();this.buckets=buckets;gapSeconds=Math.Max(30,period*3);}
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
    }
}
