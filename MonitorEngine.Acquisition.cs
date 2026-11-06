using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ExperimentMonitor {
    public sealed partial class MonitorEngine {
        /// <summary>一轮采集的上下文。把原先那一行 1500 字符的语句拆成可读的步骤。</summary>
        sealed class RoundContext {
            public IReadTransport Transport;
            public bool Simulation;
            public string Port;
            public int Baud;
            public int TimeoutMs;
            public string Source, Session, Version, RoundPath;
            public long Round;
            public DateTime RoundStarted;
            public ExperimentStore Store;
            public Action<string> Log;
            public Dictionary<int,int> Failed;
            public Dictionary<int,DateTime> ProbeAt;
            public CancellationToken Token;
        }

        /// <summary>站号连续失败后的重试间隔（秒）：5/10/20/40/60/120/300，之后封顶 300 秒。</summary>
        static int BackoffSeconds(int failures) {
            int[] steps = { 5, 10, 20, 40, 60, 120, 300 };
            int index = failures - 1;
            if (index < 0) index = 0;
            if (index >= steps.Length) index = steps.Length - 1;
            return steps[index];
        }

        /// <summary>
        /// 一个站号在一轮里的采集。
        ///
        /// 两条关键约定（2026-11-06 修订）：
        ///  1. 每轮每个点都必须产生一行 —— 成功、失败、未读取都要落库并通知界面。
        ///     旧实现里"站内任一点超时"会直接 break 掉该站剩余的点，那些点既不落库也不进统计，
        ///     于是"一轮 37 个点"实际只有 31 行，界面、统计和导出都看不出少了什么。
        ///     现场实测：直流表 ZU6682 超时后，同组 100% 健康的 ZI6682/ZW66822 被跳过 71%。
        ///  2. 只有端口级故障才重建串口 —— 超时和 CRC 错都不代表串口坏了。
        ///     旧实现对任何超时都重建通道；现场实测 ZU6682 的成败有 92% 相关于"上一笔事务
        ///     是不是站 6"（站 6 超时会重建串口），重建本身在打断总线上其它站的时序。
        /// </summary>
        void RunStation(int slave, List<Point> groupPoints, RoundContext ctx) {
            ctx.Token.ThrowIfCancellationRequested();
            DateTime probe;
            if (ctx.ProbeAt.TryGetValue(slave, out probe) && DateTime.UtcNow < probe) {
                // 站号在隔离期：本轮不读，但每个点都要留一行，否则这一轮会静默少几个点。
                EmitSkippedRows(groupPoints, "站号 " + slave + " 处于隔离期，" + ((int)Math.Ceiling((probe - DateTime.UtcNow).TotalSeconds)) + " 秒后重试", ctx);
                return;
            }
            for (int pi = 0; pi < groupPoints.Count; pi++) {
                ctx.Token.ThrowIfCancellationRequested();
                Point p = groupPoints[pi];
                var meta = PointCatalog.Get(p);
                Observation o = Collector.Read(ctx.Transport, p, meta.Mode, ctx.Source, ctx.TimeoutMs, ctx.Token, ctx.Log);
                o.SessionId = ctx.Session; o.ConfigVersion = ctx.Version; o.Round = ctx.Round;
                o.RoundStartedUtc = ctx.RoundStarted; o.Unit = meta.Unit; o.Description = meta.Label;
                if (o.Number.HasValue) o.Number *= meta.Scale;
                RememberObservation(o);
                ctx.Store.Record(o, ctx.Round, meta.Unit, ctx.RoundPath);
                PublishObservation(o);
                ctx.Log("SAMPLE round=" + ctx.Round + " point=" + o.Point + " quality=" + o.Quality + " value=" + o.Value);
                if (o.Quality == Quality.Timeout || o.Quality == Quality.Error || o.Quality == Quality.Ambiguous) {
                    int n; ctx.Failed.TryGetValue(slave, out n); ctx.Failed[slave] = ++n;
                    int delay = BackoffSeconds(n);
                    ctx.ProbeAt[slave] = DateTime.UtcNow.AddSeconds(delay);
                    ctx.Log("站号 " + slave + " 无有效响应，本轮其余点记为未读取，" + delay + " 秒后重试；其他设备继续");
                    EmitSkippedRows(groupPoints.GetRange(pi + 1, groupPoints.Count - pi - 1),
                        "同站前序点 " + o.Point + " " + Quality.Label(o.Quality) + "，本轮未读取", ctx);
                    if (o.FaultClass == "port" || o.Quality == Quality.Timeout) {
                        // 重建串口的两类触发：端口级故障（端口消失/读写失败）与超时。
                        // 超时也重建，是因为现场实测直流表 ZU6682 的成败有 92% 相关于
                        // "上一笔事务是不是站 6"（站 6 超时会重建通道，重建后第一次读反而通）——
                        // 重建带来的通道复位与缓冲清空对这条总线是有用的。
                        // 帧错（CRC/格式）与迟到帧不重建：端口本身是好的，重建只会打断总线上
                        // 其它站的时序，而这两类问题应该由隔离窗口与物理下界判定来处理。
                        ctx.Log(o.FaultClass == "port"
                            ? "端口级故障（" + o.Value + "），重建串口通道"
                            : "站号超时（" + o.Value + "），重建串口通道");
                        lock (sync) { if (Object.ReferenceEquals(connectedTransport, ctx.Transport)) connectedTransport = null; }
                        try { ctx.Transport.Dispose(); } catch { }
                        if (!ctx.Simulation && ctx.Token.WaitHandle.WaitOne(250)) ctx.Token.ThrowIfCancellationRequested();
                        ctx.Transport = TransportFactory(ctx.Simulation, ctx.Port, ctx.Baud);
                        bool adopted = false;
                        lock (sync) { if (connected && !disposed && !disconnecting) { connectedTransport = ctx.Transport; adopted = true; } }
                        if (!adopted) {
                            try { ctx.Transport.Dispose(); } catch { }
                            ctx.Token.ThrowIfCancellationRequested();
                            throw new IOException("连接状态已改变，已释放新建通道");
                        }
                    }
                    return;
                }
                if (o.Quality == Quality.Good && ctx.Failed.ContainsKey(slave)) {
                    ctx.Failed.Remove(slave); ctx.ProbeAt.Remove(slave);
                    ctx.Log("站号 " + slave + " 已恢复");
                }
            }
        }

        /// <summary>
        /// 为本轮没有读取的点逐个补一行：质量=skipped，数值列为空（绝不能写成 0）。
        /// 这些行同样落库、同样进统计口径 —— 一轮就是一轮，缺哪几个点要看得见。
        /// </summary>
        void EmitSkippedRows(List<Point> points, string reason, RoundContext ctx) {
            if (points == null || points.Count == 0) return;
            foreach (Point p in points) {
                var meta = PointCatalog.Get(p);
                Observation o = new Observation {
                    Point = p.name, Description = meta.Label, Device = p.binding.device, Slave = p.binding.slave,
                    Address = p.binding.address_zero_based, Count = p.binding.register_count, Mode = meta.Mode,
                    Source = ctx.Source, Status = "本轮未读取", Value = reason, Quality = Quality.Skipped,
                    Utc = DateTime.UtcNow, RoundStartedUtc = ctx.RoundStarted, Unit = meta.Unit,
                    SessionId = ctx.Session, ConfigVersion = ctx.Version, Round = ctx.Round,
                    Number = null, RawNumber = null
                };
                RememberObservation(o);
                ctx.Store.Record(o, ctx.Round, meta.Unit, ctx.RoundPath);
                PublishObservation(o);
                ctx.Log("SKIP round=" + ctx.Round + " point=" + o.Point + " quality=" + o.Quality + " reason=" + reason);
            }
        }
    }
}
