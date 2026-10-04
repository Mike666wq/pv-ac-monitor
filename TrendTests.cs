using System;
using System.Collections.Generic;
using System.Linq;

namespace ExperimentMonitor {
    /// <summary>Regression checks for history trend compression semantics.</summary>
    static class TrendTests {
        static void Check(bool value,string name,List<string> checks) {
            if(!value)throw new Exception("趋势回归检查失败："+name);
            checks.Add(name);
        }
        static DateTime At(DateTime start,int seconds) { return start.AddSeconds(seconds); }
        static ChartSample Good(DateTime utc,double value) { return new ChartSample{Utc=utc,Value=value,Quality="good"}; }
        static ChartSample Missing(DateTime utc,string quality) { return new ChartSample{Utc=utc,Value=null,Quality=quality}; }
        static TrendsPage.TrendAccumulator Acc(DateTime start,int seconds,int buckets,int period) {
            return new TrendsPage.TrendAccumulator("D6",start,start.AddSeconds(seconds),buckets,period);
        }
        static void Add(TrendsPage.TrendAccumulator acc,params ChartSample[] samples) { foreach(ChartSample sample in samples)acc.Add(sample); }
        static bool HasValueAt(IEnumerable<ChartSample> samples,DateTime utc,double value) {
            return samples.Any(x=>x.Value.HasValue&&x.Utc.ToUniversalTime()==utc.ToUniversalTime()&&x.Value.Value==value);
        }
        static bool IsSorted(IList<ChartSample> samples) {
            for(int i=1;i<samples.Count;i++)if(samples[i-1].Utc.ToUniversalTime()>samples[i].Utc.ToUniversalTime())return false;
            return true;
        }
        static bool HasGapBetween(IList<ChartSample> samples,DateTime left,DateTime right) {
            return samples.Any(x=>!x.Value.HasValue&&x.Utc.ToUniversalTime()>left.ToUniversalTime()&&x.Utc.ToUniversalTime()<right.ToUniversalTime());
        }

        public static List<string> Run() {
            var checks=new List<string>();
            DateTime start=new DateTime(2026,1,2,3,4,5,DateTimeKind.Utc);

            var extrema=Acc(start,120,1,10);
            Add(extrema,Good(At(start,1),8),Good(At(start,2),2),Good(At(start,3),14),Good(At(start,4),6));
            List<ChartSample> fullRange=extrema.Finish();
            Check(HasValueAt(fullRange,At(start,1),8)&&HasValueAt(fullRange,At(start,4),6)&&HasValueAt(fullRange,At(start,2),2)&&HasValueAt(fullRange,At(start,3),14),"single-bucket full-range first/last/min/max survive",checks);

            var fault=Acc(start,120,1,10);
            Add(fault,Good(At(start,1),1),Missing(At(start,2),"timeout"),Good(At(start,3),3));
            List<ChartSample> afterFault=fault.Finish();
            Check(HasGapBetween(afterFault,At(start,1),At(start,3)),"timeout remains a break between valid runs",checks);

            var outage=Acc(start,2000,1,10);
            Add(outage,Good(At(start,10),1),Good(At(start,20),2),Good(At(start,1000),3),Good(At(start,1010),4));
            List<ChartSample> afterOutage=outage.Finish();
            Check(HasGapBetween(afterOutage,At(start,20),At(start,1000)),"large timestamp hole remains a break within one bucket",checks);

            var dense=Acc(start,100001,1,10);
            for(int i=0;i<100000;i++) {
                DateTime utc=At(start,i);
                if((i&1)==0) {
                    double value=i==20000?-500:(i==80000?900000:i);
                    dense.Add(Good(utc,value));
                } else dense.Add(Missing(utc,"bad"));
            }
            List<ChartSample> compressed=dense.Finish();
            Check(compressed.Count<=20&&dense.OutputCount<=20,"100000 alternating good/bad samples stay within one bucket's output bound",checks);
            Check(IsSorted(compressed)&&HasValueAt(compressed,At(start,0),0)&&HasValueAt(compressed,At(start,99998),99998)&&HasValueAt(compressed,At(start,20000),-500)&&HasValueAt(compressed,At(start,80000),900000),"dense compressed output is time-sorted and retains first/last/global extrema",checks);

            var continuous=Acc(start,100,4,10);
            for(int i=0;i<=10;i++)continuous.Add(Good(At(start,i*10),i));
            List<ChartSample> acrossBuckets=continuous.Finish();
            Check(!acrossBuckets.Any(x=>!x.Value.HasValue)&&HasValueAt(acrossBuckets,start,0)&&HasValueAt(acrossBuckets,At(start,100),10),"continuous samples remain connected across bucket boundaries",checks);
            Check(IsSorted(acrossBuckets),"continuous cross-bucket output is chronological",checks);

            var brokenAcrossBuckets=Acc(start,2000,4,10);
            Add(brokenAcrossBuckets,Good(At(start,10),1),Good(At(start,20),2),Good(At(start,1000),3),Good(At(start,1010),4));
            List<ChartSample> acrossGap=brokenAcrossBuckets.Finish();
            Check(HasGapBetween(acrossGap,At(start,20),At(start,1000))&&IsSorted(acrossGap),"cross-bucket outage retains a chronological break",checks);
            return checks;
        }
    }
}
