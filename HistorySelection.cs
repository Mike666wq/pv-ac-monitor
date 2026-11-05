using System;

namespace ExperimentMonitor {
    /// <summary>一次跨页面交接用的筛选条件（源 / 时间范围 / 测点）。只在跳转那一刻传递,不是长期共享状态。</summary>
    public sealed class HistorySelection {
        public string Source="serial";
        public DateTime FromUtc,ToUtc;
        public string[] Points=new string[0];
        public HistorySelection Copy() {
            return new HistorySelection{Source=Source,FromUtc=FromUtc.ToUniversalTime(),ToUtc=ToUtc.ToUniversalTime(),Points=(string[])Points.Clone()};
        }
    }
}
