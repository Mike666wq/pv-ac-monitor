using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>单个测点的告警阈值。只填一边表示只判单边。</summary>
    public sealed class Limit {
        public double? Lo,Hi;
        public bool Active { get { return Lo.HasValue||Hi.HasValue; } }
        public bool Violates(double value) { return (Lo.HasValue&&value<Lo.Value)||(Hi.HasValue&&value>Hi.Value); }
    }

    /// <summary>一段采集会话（一次"开始采集"到停止）的统计。</summary>
    public sealed class SessionSummary {
        public string Session;                 // 原始会话标识（旧库可能为空）
        public bool Legacy;                    // 无会话记录的旧数据,按库区分
        public DateTime FromUtc,ToUtc;
        public long Rounds,Records,GoodRounds,TimeoutRounds;
        public long OverLimitRounds=-1;        // -1 表示未配置阈值
        public long ExpectedRounds,MissingRounds,MaxGapTicks,PeriodTicks;
        public int Devices,Points;
    }

    /// <summary>一个测点在筛选范围内的统计。</summary>
    public sealed class PointSummary {
        public string Point,Label,Unit;
        public long Good,Timeout,Error,OverLimit=-1;
        public double? Min,Max,Mean,StdDev;
        public long AverageIntervalTicks;
        public double? Lo,Hi;
        public bool HasLimit { get { return Lo.HasValue||Hi.HasValue; } }
    }

    /// <summary>记录页「实验段」「测点统计」两个子标签的数据来源。一次筛选范围扫描 + 逐库聚合。</summary>
    public static class HistoryStats {
        sealed class RoundAgg {
            public string SessionKey;public long MinUtc,MaxUtc,Rows,Good;public int Devices,Points;
        }
        sealed class PointAgg {
            public long Total,Good,Timeout,Other,NumberCount,SumMin,SumMax;
            public double Sum,SumSquares;public long MinUtc=long.MaxValue,MaxUtc=long.MinValue;
        }
        static string SessionKey(string file,string session) { return String.IsNullOrEmpty(session)?"\u001flegacy:"+file:session; }
        static string RoundKey(string sessionKey,long round) { return sessionKey+"\u001f"+round.ToString(CultureInfo.InvariantCulture); }
        static string Text(object value) { return value==null||value==DBNull.Value?"":Convert.ToString(value,CultureInfo.InvariantCulture); }

        public static List<SessionSummary> Sessions(string root,HistoryFilter f,IDictionary<string,Limit> limits,CancellationToken token) {
            if(f==null)throw new ArgumentNullException("filter");
            var rounds=new Dictionary<string,RoundAgg>(StringComparer.Ordinal);
            foreach(string file in ExperimentHistory.Databases(root,f.Source)) {
                token.ThrowIfCancellationRequested();
                using(var c=ExperimentHistory.OpenRead(file))
                using(var q=ExperimentHistory.Command(c,f,
                    "SELECT session,round,MIN(utc_ticks),MAX(utc_ticks),COUNT(*),SUM(CASE WHEN quality='good' THEN 1 ELSE 0 END),COUNT(DISTINCT device),COUNT(DISTINCT point)",long.MaxValue)) {
                    q.CommandText+=" GROUP BY session,round";
                    using(var r=q.ExecuteReader()) while(r.Read()) {
                        string sessionKey=SessionKey(file,Text(r.GetValue(0)));
                        long round=r.GetInt64(1);
                        string key=RoundKey(sessionKey,round);
                        RoundAgg agg;if(!rounds.TryGetValue(key,out agg)){agg=new RoundAgg{SessionKey=sessionKey,MinUtc=long.MaxValue};rounds.Add(key,agg);}
                        long min=Convert.ToInt64(r.GetValue(2)),max=Convert.ToInt64(r.GetValue(3));
                        if(min<agg.MinUtc)agg.MinUtc=min;
                        if(max>agg.MaxUtc)agg.MaxUtc=max;
                        agg.Rows+=Convert.ToInt64(r.GetValue(4));
                        agg.Good+=Convert.ToInt64(r.GetValue(5));
                        int devices=Convert.ToInt32(r.GetValue(6)),points=Convert.ToInt32(r.GetValue(7));
                        if(devices>agg.Devices)agg.Devices=devices;
                        if(points>agg.Points)agg.Points=points;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            bool hasLimits=LimitsActive(limits);
            var overLimit=hasLimits?OverLimitRoundKeys(root,f,limits,token):new HashSet<string>(StringComparer.Ordinal);
            var bySession=new Dictionary<string,List<KeyValuePair<string,RoundAgg>>>(StringComparer.Ordinal);
            foreach(var pair in rounds) {
                List<KeyValuePair<string,RoundAgg>> list;
                if(!bySession.TryGetValue(pair.Value.SessionKey,out list)){list=new List<KeyValuePair<string,RoundAgg>>();bySession.Add(pair.Value.SessionKey,list);}
                list.Add(pair);
            }
            var result=new List<SessionSummary>();
            foreach(var group in bySession) {
                bool legacy=group.Key.StartsWith("\u001flegacy:",StringComparison.Ordinal);
                var summary=new SessionSummary{Session=legacy?"":group.Key,Legacy=legacy,OverLimitRounds=hasLimits?0:-1};
                var starts=new List<long>(group.Value.Count);
                long fromTicks=long.MaxValue,toTicks=0;
                foreach(var pair in group.Value) {
                    RoundAgg agg=pair.Value;
                    summary.Rounds++;summary.Records+=agg.Rows;
                    if(agg.Good>0)summary.GoodRounds++;else summary.TimeoutRounds++;
                    if(agg.MinUtc<fromTicks)fromTicks=agg.MinUtc;
                    if(agg.MaxUtc>toTicks)toTicks=agg.MaxUtc;
                    if(agg.Devices>summary.Devices)summary.Devices=agg.Devices;
                    if(agg.Points>summary.Points)summary.Points=agg.Points;
                    starts.Add(agg.MinUtc);
                    if(hasLimits&&overLimit.Contains(pair.Key))summary.OverLimitRounds++;
                }
                if(fromTicks<long.MaxValue)summary.FromUtc=new DateTime(fromTicks,DateTimeKind.Utc);
                if(toTicks>0)summary.ToUtc=new DateTime(toTicks,DateTimeKind.Utc);
                if(starts.Count>1) {
                    starts.Sort();
                    var gaps=new List<long>(starts.Count-1);
                    for(int i=1;i<starts.Count;i++){long delta=starts[i]-starts[i-1];if(delta>0)gaps.Add(delta);}
                    if(gaps.Count>0) {
                        summary.MaxGapTicks=gaps.Max();
                        var sorted=gaps.OrderBy(x=>x).ToList();
                        summary.PeriodTicks=sorted[sorted.Count/2];
                        long span=starts[starts.Count-1]-starts[0];
                        if(summary.PeriodTicks>0)summary.ExpectedRounds=1+(long)Math.Round(span/(double)summary.PeriodTicks);
                        if(summary.ExpectedRounds>summary.Rounds)summary.MissingRounds=summary.ExpectedRounds-summary.Rounds;
                    }
                }
                if(summary.ExpectedRounds==0)summary.ExpectedRounds=summary.Rounds;
                result.Add(summary);
            }
            return result.OrderByDescending(x=>x.FromUtc).ToList();
        }

        static bool LimitsActive(IDictionary<string,Limit> limits) {
            if(limits==null)return false;
            foreach(var pair in limits)if(pair.Value!=null&&pair.Value.Active)return true;
            return false;
        }

        /// <summary>有越限的采集轮（按会话+轮号去重）。只在配置了阈值时才查,否则返回空集。</summary>
        static HashSet<string> OverLimitRoundKeys(string root,HistoryFilter f,IDictionary<string,Limit> limits,CancellationToken token) {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            var active=limits.Where(x=>x.Value!=null&&x.Value.Active).ToList();
            foreach(string file in ExperimentHistory.Databases(root,f.Source)) {
                token.ThrowIfCancellationRequested();
                using(var c=ExperimentHistory.OpenRead(file))
                using(var q=ExperimentHistory.Command(c,f,"SELECT session,round",long.MaxValue)) {
                    var clauses=new List<string>();var parameters=new List<string>();
                    int index=0;
                    foreach(var pair in active) {
                        var parts=new List<string>();
                        if(pair.Value.Lo.HasValue){parts.Add("number<@llo"+index);parameters.Add("@llo"+index);parameters.Add(pair.Value.Lo.Value.ToString("R",CultureInfo.InvariantCulture));}
                        if(pair.Value.Hi.HasValue){parts.Add("number>@lhi"+index);parameters.Add("@lhi"+index);parameters.Add(pair.Value.Hi.Value.ToString("R",CultureInfo.InvariantCulture));}
                        clauses.Add("(point=@lp"+index+" AND quality='good' AND number IS NOT NULL AND ("+String.Join(" OR ",parts.ToArray())+"))");
                        parameters.Add("@lp"+index);parameters.Add(pair.Key);
                        index++;
                    }
                    q.CommandText+=" AND ("+String.Join(" OR ",clauses.ToArray())+") GROUP BY session,round";
                    for(int i=0;i<parameters.Count;i+=2)q.Parameters.AddWithValue(parameters[i],parameters[i+1]);
                    using(var r=q.ExecuteReader())while(r.Read())keys.Add(RoundKey(SessionKey(file,Text(r.GetValue(0))),r.GetInt64(1)));
                }
            }
            return keys;
        }

        public static List<PointSummary> Points(string root,HistoryFilter f,IDictionary<string,Limit> limits,CancellationToken token) {
            if(f==null)throw new ArgumentNullException("filter");
            var aggregates=new Dictionary<string,PointAgg>(StringComparer.Ordinal);
            foreach(string file in ExperimentHistory.Databases(root,f.Source)) {
                token.ThrowIfCancellationRequested();
                using(var c=ExperimentHistory.OpenRead(file))
                using(var q=ExperimentHistory.Command(c,f,
                    "SELECT point,COUNT(*),SUM(CASE WHEN quality='good' THEN 1 ELSE 0 END),SUM(CASE WHEN quality='timeout' THEN 1 ELSE 0 END),"
                    +"SUM(CASE WHEN quality IS NOT NULL AND quality<>'good' AND quality<>'timeout' THEN 1 ELSE 0 END),"
                    +"MIN(number),MAX(number),SUM(number),SUM(number*number),COUNT(number),MIN(utc_ticks),MAX(utc_ticks)",long.MaxValue)) {
                    q.CommandText+=" GROUP BY point";
                    using(var r=q.ExecuteReader()) while(r.Read()) {
                        string point=Text(r.GetValue(0));
                        PointAgg agg;if(!aggregates.TryGetValue(point,out agg)){agg=new PointAgg();aggregates.Add(point,agg);}
                        agg.Total+=Convert.ToInt64(r.GetValue(1));agg.Good+=Convert.ToInt64(r.GetValue(2));agg.Timeout+=Convert.ToInt64(r.GetValue(3));agg.Other+=Convert.ToInt64(r.GetValue(4));
                        if(!r.IsDBNull(5)){long min=Convert.ToInt64(r.GetValue(5));if(agg.NumberCount==0||min<agg.SumMin)agg.SumMin=min;}
                        if(!r.IsDBNull(6)){long max=Convert.ToInt64(r.GetValue(6));if(agg.NumberCount==0||max>agg.SumMax)agg.SumMax=max;}
                        if(!r.IsDBNull(7))agg.Sum+=Convert.ToDouble(r.GetValue(7),CultureInfo.InvariantCulture);
                        if(!r.IsDBNull(8))agg.SumSquares+=Convert.ToDouble(r.GetValue(8),CultureInfo.InvariantCulture);
                        agg.NumberCount+=Convert.ToInt64(r.GetValue(9));
                        long from=Convert.ToInt64(r.GetValue(10)),to=Convert.ToInt64(r.GetValue(11));
                        if(from<agg.MinUtc)agg.MinUtc=from;
                        if(to>agg.MaxUtc)agg.MaxUtc=to;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            var result=new List<PointSummary>();
            foreach(var pair in aggregates) {
                PointAgg agg=pair.Value;
                PointInfo info=PointCatalog.Get(pair.Key);
                var summary=new PointSummary{Point=pair.Key,Label=info.Label,Unit=info.Unit,Good=agg.Good,Timeout=agg.Timeout,Error=agg.Other};
                if(agg.NumberCount>0) {
                    summary.Min=agg.SumMin;summary.Max=agg.SumMax;
                    summary.Mean=agg.Sum/agg.NumberCount;
                    double variance=agg.SumSquares/agg.NumberCount-summary.Mean.Value*summary.Mean.Value;
                    summary.StdDev=Math.Sqrt(Math.Max(0,variance));
                    if(agg.Good>1&&agg.MaxUtc>agg.MinUtc)summary.AverageIntervalTicks=(agg.MaxUtc-agg.MinUtc)/(agg.Good-1);
                }
                Limit limit;
                if(limits!=null&&limits.TryGetValue(pair.Key,out limit)&&limit!=null&&limit.Active) {
                    summary.Lo=limit.Lo;summary.Hi=limit.Hi;
                    summary.OverLimit=CountOverLimit(root,f,pair.Key,limit,token);
                }
                result.Add(summary);
            }
            return result.OrderBy(x=>x.Point,StringComparer.Ordinal).ToList();
        }

        static long CountOverLimit(string root,HistoryFilter f,string point,Limit limit,CancellationToken token) {
            long total=0;
            foreach(string file in ExperimentHistory.Databases(root,f.Source)) {
                token.ThrowIfCancellationRequested();
                using(var c=ExperimentHistory.OpenRead(file))
                using(var q=ExperimentHistory.Command(c,f,"SELECT COUNT(*)",long.MaxValue)) {
                    var parts=new List<string>();
                    q.CommandText+=" AND point=@p AND quality='good' AND number IS NOT NULL";
                    q.Parameters.AddWithValue("@p",point);
                    if(limit.Lo.HasValue){parts.Add("number<@lo");q.Parameters.AddWithValue("@lo",limit.Lo.Value);}
                    if(limit.Hi.HasValue){parts.Add("number>@hi");q.Parameters.AddWithValue("@hi",limit.Hi.Value);}
                    q.CommandText+=" AND ("+String.Join(" OR ",parts.ToArray())+")";
                    total+=Convert.ToInt64(q.ExecuteScalar());
                }
            }
            return total;
        }
    }

    /// <summary>越限阈值表（limits.json,与 settings.json 同级）。缺省即不配置,不做越限判定。</summary>
    public static class LimitStore {
        public static string PathFor(string appRoot) { return Path.Combine(appRoot,"limits.json"); }
        public static Dictionary<string,Limit> Load(string appRoot) {
            var result=new Dictionary<string,Limit>(StringComparer.Ordinal);
            try {
                string path=PathFor(appRoot);
                if(!File.Exists(path))return result;
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
                object points;
                if(data==null||!data.TryGetValue("points",out points))return result;
                var map=points as Dictionary<string,object>;
                if(map==null)return result;
                foreach(var pair in map) {
                    var item=pair.Value as Dictionary<string,object>;
                    if(item==null)continue;
                    var limit=new Limit();
                    object value;
                    if(item.TryGetValue("lo",out value)&&value!=null)limit.Lo=Convert.ToDouble(value,CultureInfo.InvariantCulture);
                    if(item.TryGetValue("hi",out value)&&value!=null)limit.Hi=Convert.ToDouble(value,CultureInfo.InvariantCulture);
                    if(limit.Lo.HasValue&&limit.Hi.HasValue&&limit.Lo.Value>=limit.Hi.Value)continue;
                    if(limit.Active)result[pair.Key]=limit;
                }
            }catch{}
            return result;
        }
        public static void Save(string appRoot,IDictionary<string,Limit> limits) {
            var points=new Dictionary<string,object>(StringComparer.Ordinal);
            if(limits!=null)foreach(var pair in limits) {
                if(pair.Value==null||!pair.Value.Active)continue;
                if(pair.Value.Lo.HasValue&&pair.Value.Hi.HasValue&&pair.Value.Lo.Value>=pair.Value.Hi.Value)
                    throw new ArgumentException("测点 "+pair.Key+" 的下限必须小于上限");
                var item=new Dictionary<string,object>();
                if(pair.Value.Lo.HasValue)item["lo"]=pair.Value.Lo.Value;
                if(pair.Value.Hi.HasValue)item["hi"]=pair.Value.Hi.Value;
                points[pair.Key]=item;
            }
            string json=new JavaScriptSerializer().Serialize(new{version=1,points});
            Directory.CreateDirectory(appRoot);
            string path=PathFor(appRoot),tmp=path+".partial";
            File.WriteAllText(tmp,json,new System.Text.UTF8Encoding(false));
            File.Copy(tmp,path,true);
            File.Delete(tmp);
        }
    }
}

