using System;

namespace ExperimentMonitor {
    internal sealed class TrendBounds {
        public DateTime FromUtc,ToUtc;
        public TrendBounds(DateTime from,DateTime to){FromUtc=from;ToUtc=to;if(to<=from)throw new ArgumentException("结束时间必须晚于开始时间");}
    }
    /// <summary>One timeline. Following moves a window; fixing it never changes acquisition.</summary>
    internal sealed class TrendTimeWindow {
        TimeSpan span=TimeSpan.FromHours(1);DateTime fixedFrom,fixedTo;
        public bool Following {get;private set;}
        public TimeSpan Span {get{return span;}}
        public TrendTimeWindow(){Following=true;}
        public void Follow(TimeSpan duration){if(duration<=TimeSpan.Zero)throw new ArgumentException("时间窗口必须大于零");span=duration;Following=true;}
        public void Fix(DateTime fromUtc,DateTime toUtc){var bounds=new TrendBounds(fromUtc.ToUniversalTime(),toUtc.ToUniversalTime());fixedFrom=bounds.FromUtc;fixedTo=bounds.ToUtc;span=fixedTo-fixedFrom;Following=false;}
        public void Resume(){Following=true;}
        public TrendBounds Resolve(DateTime nowUtc,DateTime? latestUtc){
            if(!Following)return new TrendBounds(fixedFrom,fixedTo);
            DateTime now=nowUtc.ToUniversalTime(),anchor=(latestUtc??now).ToUniversalTime();DateTime end=CeilingSecond(anchor);
            return new TrendBounds(end.Subtract(span),end);
        }
        public TrendTimeWindow Copy(){return new TrendTimeWindow{span=span,fixedFrom=fixedFrom,fixedTo=fixedTo,Following=Following};}
        static DateTime CeilingSecond(DateTime value){return new DateTime(value.Ticks-value.Ticks%TimeSpan.TicksPerSecond,DateTimeKind.Utc).AddSeconds(1);}
    }
}
