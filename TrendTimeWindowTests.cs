using System;

namespace ExperimentMonitor {
    internal static class TrendTimeWindowTests {
        public static int Run(){int count=0;Action<bool,string> check=(ok,name)=>{if(!ok)throw new InvalidOperationException("时间窗口失败："+name);count++;};
            DateTime now=new DateTime(2026,10,13,12,0,0,DateTimeKind.Utc),last=now.AddDays(-1).AddMilliseconds(300);var model=new TrendTimeWindow();
            TrendBounds first=model.Resolve(now,last);check(first.ToUtc==last.AddMilliseconds(-300).AddSeconds(1)&&first.ToUtc-first.FromUtc==TimeSpan.FromHours(1),"默认最近1小时以最新已有记录为终点");
            TrendBounds next=model.Resolve(now,last.AddMinutes(10));check(next.FromUtc-first.FromUtc==TimeSpan.FromMinutes(10)&&next.ToUtc-first.ToUtc==TimeSpan.FromMinutes(10),"有新记录时起点终点共同移动");
            check(model.Resolve(now.AddHours(2),last).ToUtc==first.ToUtc,"无新记录不伪装时间在推进");
            model.Fix(now.AddDays(-2),now.AddDays(-1));TrendBounds fixedRange=model.Resolve(now.AddDays(1),now.AddDays(1));check(!model.Following&&fixedRange.FromUtc==now.AddDays(-2)&&fixedRange.ToUtc==now.AddDays(-1),"手工固定范围不受后续新数据影响");
            model.Resume();TrendBounds resumed=model.Resolve(now,now);check(model.Following&&resumed.ToUtc-resumed.FromUtc==TimeSpan.FromDays(1),"回到最新保留当前窗口长度");
            var copy=model.Copy();model.Fix(now.AddMinutes(-3),now);check(copy.Following&&copy.Span==TimeSpan.FromDays(1),"在途查询使用独立时间窗口快照");
            bool invalid=false;try{model.Fix(now,now);}catch(ArgumentException){invalid=true;}check(invalid,"非法起止时间不成为可应用范围");
            model.Follow(TimeSpan.FromMinutes(15));check(model.Resolve(now,null).ToUtc-model.Resolve(now,null).FromUtc==TimeSpan.FromMinutes(15),"无库记录时仍能显示可理解的最近窗口");
            model.Fix(now.ToLocalTime().Date.ToUniversalTime(),now.AddSeconds(1));TrendBounds today=model.Resolve(now,last.AddDays(-2));check(!model.Following&&today.FromUtc==now.ToLocalTime().Date.ToUniversalTime()&&today.ToUtc>today.FromUtc,"今天固定范围不会错误跳回旧记录所在日期");
            return count;
        }
    }
}
