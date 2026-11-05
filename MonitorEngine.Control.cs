using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>
    /// 控制写入链路（v1）：只做 06 单寄存器下置。默认关闭；门禁/回读/审计都在这里，
    /// 不把写逻辑散落到界面。PLC 是控制权威，PC 只负责下发与核对。
    /// </summary>
    public sealed partial class MonitorEngine {
        sealed class PendingControl {
            public ControlCommand Command;
            public bool Verify;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            public ControlResult Result;
            public volatile bool Cancelled;
        }
        readonly object controlSync = new object();
        readonly object controlLogSync = new object();
        readonly List<PendingControl> controlQueue = new List<PendingControl>();
        readonly Dictionary<string, Observation> lastByPoint = new Dictionary<string, Observation>(StringComparer.Ordinal);
        readonly List<DateTime> controlWriteTimes = new List<DateTime>();
        ControlConfig controlSettings = ControlConfig.Disabled();
        string controlConfigError = "";
        bool controlArmed, controlUncertain;
        DateTime controlArmedUtc = DateTime.MinValue, lastControlWriteUtc = DateTime.MinValue;
        string uncertainPoint = "", uncertainDetail = "";
        int? uncertainOld, uncertainNew;

        public ControlConfig ControlSettings { get { lock (controlSync) return controlSettings; } }
        public string ControlConfigError { get { lock (controlSync) return controlConfigError; } }
        public bool ControlArmed { get { lock (controlSync) return controlArmed; } }
        public bool ControlUncertain { get { lock (controlSync) return controlUncertain; } }
        public string ControlUncertainPoint { get { lock (controlSync) return uncertainPoint; } }
        public string ControlUncertainDetail { get { lock (controlSync) return uncertainDetail; } }
        public ControlResult LastControlResult { get; private set; }
        public string ControlMode {
            get {
                lock (controlSync) {
                    if (!String.IsNullOrEmpty(controlConfigError)) return "config_error";
                    return controlArmed ? "enabled" : "closed";
                }
            }
        }
        /// <summary>控制开关（会话内）。默认关闭；打开后即可按点位发送 06，回读与审计照旧。</summary>
        public bool ControlEnabled { get { lock (controlSync) return controlArmed; } }
        public void SetControlEnabled(bool enabled, string operatorName) {
            if (enabled) {
                lock (controlSync) {
                    if (!String.IsNullOrEmpty(controlConfigError)) throw new InvalidOperationException("控制配置无效：" + controlConfigError);
                    if (controlSettings == null || controlSettings.mode == "disabled") throw new InvalidOperationException("control-config.json 的 mode=disabled：控制被强制关闭");
                    if (controlSettings.targets == null || controlSettings.targets.Count == 0) throw new InvalidOperationException("没有控制点定义：control-config.json 缺失或为空");
                    if (controlUncertain) throw new InvalidOperationException("上一次写入结果未知，请先点「重新读取核对」");
                    controlArmed = true;
                    controlArmedUtc = DateTime.UtcNow;
                }
                Log("控制开关已开启 operator=" + (operatorName ?? "") + " config=" + ControlSettings.Version);
            } else {
                bool changed;
                lock (controlSync) { changed = controlArmed; controlArmed = false; }
                if (changed) Log("控制开关已关闭 operator=" + (operatorName ?? ""));
            }
            FireControlState();
        }
        public event Action<ControlResult> ControlCompleted;
        public event Action ControlStateChanged;

        void InitializeControl() { ReloadControlConfig(); }

        public void ReloadControlConfig() {
            ControlConfig next; string error;
            next = ControlConfig.Load(root, points, out error);
            lock (controlSync) {
                controlArmed = false;
                controlUncertain = false;
                uncertainPoint = "";
                uncertainOld = uncertainNew = null;
                uncertainDetail = "";
                controlSettings = next;
                controlConfigError = error ?? "";
            }
            Log("控制配置加载：mode=" + (next == null ? "null" : next.mode) + " version=" + (next == null ? "-" : next.Version) + (String.IsNullOrEmpty(error) ? "" : "；错误=" + error));
            FireControlState();
        }

        public Observation LastObservation(string point) {
            lock (controlSync) {
                Observation value;
                return lastByPoint.TryGetValue(point ?? "", out value) ? value : null;
            }
        }

        /// <summary>模拟演示：只在模拟数据连接时加载一份“可写”的内存配置，不落盘、不影响真实设备。</summary>
        public void ApplySimulationDemoConfig() {
            bool simulation;
            lock (sync) simulation = connected && connectedSimulation;
            if (!simulation) throw new InvalidOperationException("只能在模拟数据已连接时加载演示配置");
            var demo = new ControlConfig { mode = "preview", Version = "simulation-demo", requireOperatorAck = true, autoLockIdleSeconds = 600 };
            demo.interlocks.remoteExpected = 1;
            demo.interlocks.faultExpected = 0;
            demo.interlocks.maxAgeSeconds = 30;
            demo.limits.minIntervalMs = 1000;
            demo.limits.maxWritesPerMinute = 6;
            demo.targets.Add(new ControlTarget { point = "D2202", label = "主机温度设定", kind = "int16", unit = "℃", slave = 2, address = 2202, min = 16, max = 30, step = 1, confirmed = true, allowWrite = true, note = "模拟演示" });
            demo.targets.Add(new ControlTarget { point = "D2001", label = "模式控制", kind = "int16", unit = "", slave = 2, address = 2201, min = 0, max = 10, step = 1, confirmed = true, allowWrite = true, note = "模拟演示" });
            demo.targets.Add(new ControlTarget { point = "D2000", label = "送风风机速度", kind = "int16", unit = "", slave = 2, address = 2000, min = 0, max = 100, step = 1, confirmed = true, allowWrite = true, note = "模拟演示" });
            demo.targets.Add(new ControlTarget { point = "D2207", label = "放冷反馈设定", kind = "int16", unit = "", slave = 2, address = 2207, min = 0, max = 100, step = 1, confirmed = true, allowWrite = true, note = "模拟演示" });
            lock (controlSync) {
                controlSettings = demo;
                controlConfigError = "";
                controlArmed = false;
                controlUncertain = false;
                uncertainPoint = "";
                uncertainOld = uncertainNew = null;
                uncertainDetail = "";
            }
            Log("已加载模拟演示控制配置（仅模拟数据，不会写入真实 PLC）");
            FireControlState();
        }

        internal void RememberObservation(Observation o) {
            if (o == null || String.IsNullOrEmpty(o.Point)) return;
            lock (controlSync) lastByPoint[o.Point] = o;
        }

        /// <summary>控制/状态点当前数值。16 位点在显示层保留 U16/I16 双解，不进入趋势数字列，
        /// 但控制门禁需要无符号数值，所以这里按大端原始寄存器补一个数值视图。</summary>
        public int? ControlNumericValue(string point) {
            lock (controlSync) {
                Observation value;
                return lastByPoint.TryGetValue(point ?? "", out value) ? NumericValue(value) : null;
            }
        }

        static int? NumericValue(Observation o) {
            if (o == null || o.Quality != "good") return null;
            if (o.Number.HasValue) return (int)Math.Round(o.Number.Value);
            if (o.Payload != null && o.Payload.Length >= 2 && (o.Mode == "U16 / I16" || o.Mode == "UINT16" || o.Mode == "RAW"))
                return (o.Payload[0] << 8) | o.Payload[1];
            return null;
        }

        public ControlGateInfo ControlReadiness(string point) {
            bool connectedNow, runningNow, singleNow;
            lock (sync) { connectedNow = connected; runningNow = worker != null; singleNow = single; }
            lock (controlSync) {
                var gate = new ControlGateInfo();
                if (!String.IsNullOrEmpty(controlConfigError)) { gate.Status = "config_error"; gate.Detail = controlConfigError; return gate; }
                if (controlSettings == null || controlSettings.mode == "disabled") { gate.Status = "disabled"; gate.Detail = "control-config.json 的 mode=disabled：控制被强制关闭"; return gate; }
                if (controlSettings.targets == null || controlSettings.targets.Count == 0) { gate.Status = "disabled"; gate.Detail = "没有控制点定义：control-config.json 缺失或为空"; return gate; }
                if (!connectedNow) { gate.Status = "disconnected"; gate.Detail = "请先连接数据源"; return gate; }
                if (!runningNow) { gate.Status = "not_acquiring"; gate.Detail = "请先开始采集；控制要求采集运行中"; return gate; }
                if (singleNow) { gate.Status = "not_acquiring"; gate.Detail = "单轮采集不处理控制写入，请使用连续采集"; return gate; }
                ControlTarget target = controlSettings.Find(point);
                if (target == null) { gate.Status = "no_target"; gate.Detail = "control-config.json 没有这个控制点"; return gate; }
                if (controlSettings.requireConfirmed && !target.confirmed) { gate.Status = "not_confirmed"; gate.Detail = target.label + " 尚未现场确认（confirmed=false）"; return gate; }
                if (controlSettings.requireConfirmed && !target.allowWrite) { gate.Status = "not_allow_write"; gate.Detail = target.label + " 未开放写入（allowWrite=false）"; return gate; }
                if (controlUncertain) { gate.Status = "uncertain"; gate.Detail = "上一次写入结果未知（" + uncertainPoint + "），请先重新读取核对"; return gate; }
                if (!controlArmed) { gate.Status = "not_armed"; gate.Detail = "控制开关未开启"; return gate; }
                ControlGateInfo rate = RateGateLocked();
                if (rate != null) return rate;
                if (controlSettings.enforceInterlocks) {
                    ControlGateInfo interlock = InterlockGateLocked(controlSettings);
                    if (interlock != null) return interlock;
                }
                gate.Status = "ready";
                gate.Detail = "可发送";
                return gate;
            }
        }

        ControlGateInfo RateGateLocked() {
            var now = DateTime.UtcNow;
            var limits = controlSettings.limits ?? new ControlLimits();
            if (lastControlWriteUtc != DateTime.MinValue && (now - lastControlWriteUtc).TotalMilliseconds < limits.minIntervalMs) {
                return new ControlGateInfo { Status = "rate", Detail = "写间隔至少 " + limits.minIntervalMs + " ms；请稍后再试" };
            }
            controlWriteTimes.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (controlWriteTimes.Count >= limits.maxWritesPerMinute) {
                return new ControlGateInfo { Status = "rate", Detail = "每分钟最多 " + limits.maxWritesPerMinute + " 次写入；请稍后再试" };
            }
            return null;
        }

        ControlGateInfo InterlockGateLocked(ControlConfig config) {
            var inter = config.interlocks ?? new ControlInterlocks();
            if (!inter.remoteExpected.HasValue) {
                return new ControlGateInfo { Status = "interlock_unverified", Detail = "联锁语义未确认：interlocks.remoteExpected 为空，只能预演" };
            }
            Observation remote, fault;
            var now = DateTime.UtcNow;
            int? remoteValue, faultValue;
            if (!lastByPoint.TryGetValue(inter.remotePoint ?? "", out remote) || (remoteValue = NumericValue(remote)) == null) {
                return new ControlGateInfo { Status = "stale", Detail = "联锁点 " + inter.remotePoint + " 没有有效读数" };
            }
            if ((now - remote.Utc).TotalSeconds > inter.maxAgeSeconds) {
                return new ControlGateInfo { Status = "stale", Detail = "联锁点 " + inter.remotePoint + " 已过期（>" + inter.maxAgeSeconds + " s）" };
            }
            if (remoteValue.Value != inter.remoteExpected.Value) {
                return new ControlGateInfo { Status = "interlock_remote", Detail = inter.remotePoint + "=" + remoteValue.Value + "，要求 " + inter.remoteExpected.Value + "（远程模式）" };
            }
            if (!lastByPoint.TryGetValue(inter.faultPoint ?? "", out fault) || (faultValue = NumericValue(fault)) == null) {
                return new ControlGateInfo { Status = "stale", Detail = "联锁点 " + inter.faultPoint + " 没有有效读数" };
            }
            if ((now - fault.Utc).TotalSeconds > inter.maxAgeSeconds) {
                return new ControlGateInfo { Status = "stale", Detail = "联锁点 " + inter.faultPoint + " 已过期（>" + inter.maxAgeSeconds + " s）" };
            }
            if (faultValue.Value != inter.faultExpected) {
                return new ControlGateInfo { Status = "interlock_fault", Detail = inter.faultPoint + "=" + faultValue.Value + "，要求 " + inter.faultExpected + "（无总故障）" };
            }
            return null;
        }

        public void ArmControl(string operatorName) {
            if (String.IsNullOrWhiteSpace(operatorName)) throw new ArgumentException("操作者不能为空", "operatorName");
            bool connectedNow, runningNow;
            lock (sync) { connectedNow = connected; runningNow = worker != null; }
            lock (controlSync) {
                if (!String.IsNullOrEmpty(controlConfigError)) throw new InvalidOperationException("控制配置无效：" + controlConfigError);
                if (controlSettings == null || controlSettings.targets == null || controlSettings.targets.Count == 0) throw new InvalidOperationException("没有控制点定义：control-config.json 缺失或为空");
                if (!connectedNow) throw new InvalidOperationException("请先连接数据源");
                if (!runningNow) throw new InvalidOperationException("请先开始采集；控制要求采集运行中");
                if (controlUncertain) throw new InvalidOperationException("上一次写入结果未知，请先重新读取核对");
                controlArmed = true;
                controlArmedUtc = DateTime.UtcNow;
            }
            Log("控制已解锁 operator=" + operatorName + " config=" + ControlSettings.Version);
            FireControlState();
        }

        public void DisarmControl(string reason) {
            bool changed;
            lock (controlSync) { changed = controlArmed; controlArmed = false; }
            if (changed) { Log("控制已上锁：" + (reason ?? "")); FireControlState(); }
        }

        /// <summary>页面定时调用：闲置超时、联锁失效时可自动关闭控制开关。默认不自动关闭。</summary>
        public void TickControl() {
            bool armed;
            lock (controlSync) armed = controlArmed;
            if (!armed) return;
            string reason = null;
            lock (controlSync) {
                if (!controlArmed) return;
                var settings = controlSettings;
                if (settings != null && settings.autoLockIdleSeconds > 0 && (DateTime.UtcNow - controlArmedUtc).TotalSeconds > settings.autoLockIdleSeconds)
                    reason = "开关闲置超时";
                if (reason == null && settings != null && settings.enforceInterlocks) {
                    ControlGateInfo gate = InterlockGateLocked(settings);
                    if (gate != null) reason = gate.Detail;
                }
            }
            if (reason != null) DisarmControl(reason);
        }

        public ControlResult PreviewControl(string point, int value, string operatorName) {
            ControlConfig config; string error;
            lock (controlSync) { config = controlSettings; error = controlConfigError; }
            var result = NewResult(point, value, operatorName);
            if (!String.IsNullOrEmpty(error)) return Blocked(result, "config_error", error);
            ControlTarget target = config == null ? null : config.Find(point);
            if (target == null) return Blocked(result, "no_target", "control-config.json 没有这个控制点");
            string range = RangeProblem(target, value);
            if (range != null) return Blocked(result, "range", range);
            result.Label = target.label;
            result.Slave = target.slave;
            result.Address = target.address;
            result.Tx = Modbus.WriteSingle(target.slave, target.address, value);
            lock (controlSync) {
                Observation old;
                int? previewOld = null;
                if (lastByPoint.TryGetValue(point, out old)) previewOld = NumericValue(old);
                if (previewOld.HasValue) result.OldValue = previewOld.Value;
            }
            result.Status = "preview";
            result.Detail = "预演：只生成 06 报文，未发送";
            result.Utc = DateTime.UtcNow;
            LastControlResult = result;
            AppendControlEvent(result, null, null);
            return result;
        }

        public ControlResult ExecuteControl(string point, int value, string operatorName) {
            var result = NewResult(point, value, operatorName);
            ControlGateInfo gate = ControlReadiness(point);
            if (!gate.CanWrite) return Blocked(result, gate.Status, gate.Detail);
            if (String.IsNullOrWhiteSpace(operatorName)) return Blocked(result, "blocked_ack", "缺少操作者；控制写入必须记录操作者");
            ControlTarget target = ControlSettings == null ? null : ControlSettings.Find(point);
            string range = RangeProblem(target, value);
            if (range != null) return Blocked(result, "range", range);
            result.Label = target.label; result.Slave = target.slave; result.Address = target.address;
            var pending = new PendingControl { Command = new ControlCommand { Point = point, Value = value, Operator = operatorName, Confirmed = true } };
            lock (controlSync) {
                if (!controlArmed || worker == null) return Blocked(result, "not_armed", "控制未解锁或采集已停止");
                controlQueue.Add(pending);
            }
            int timeoutSeconds, period;
            lock (sync) { timeoutSeconds = currentTimeoutMs; period = periodSeconds; }
            int timeoutMs = timeoutSeconds;
            // 等待预算必须覆盖「采集周期 + 一次写超时 + 一次回读超时 + 余量」，
            // 否则会出现「界面已报超时，但随后 worker 仍把 06 发出去」的错误结论。
            long waitMs = Math.Min(180000L, Math.Max(20000L, (long)period * 1000L + (long)timeoutMs * 2L + 8000L));
            if (!pending.Done.Wait((int)waitMs)) {
                pending.Cancelled = true;
                lock (controlSync) controlQueue.Remove(pending);
                int? knownOld;
                lock (controlSync) {
                    controlUncertain = true;
                    controlArmed = false;
                    uncertainPoint = point;
                    Observation known;
                    knownOld = lastByPoint.TryGetValue(point, out known) ? NumericValue(known) : (int?)null;
                    uncertainOld = knownOld;
                    uncertainNew = value;
                    uncertainDetail = "排队超时：写入已提交但结果待定";
                }
                var unknown = Blocked(result, "unknown", "排队超时（等待 " + (waitMs / 1000) + " s）：写入已提交但结果待定，请点「重新读取核对」确认");
                unknown.OldValue = knownOld ?? 0;
                LastControlResult = unknown;
                AppendControlEvent(unknown, null, null);
                FireControlState();
                return unknown;
            }
            return pending.Result;
        }

        public ControlResult ResolveControlUncertainty(string operatorName) {
            var result = new ControlResult { Point = uncertainPoint, Operator = operatorName ?? "", Utc = DateTime.UtcNow };
            PendingControl pending;
            bool runningNow;
            lock (sync) runningNow = worker != null;
            lock (controlSync) {
                if (!controlUncertain) return Blocked(result, "no_uncertainty", "当前没有待核对的写入");
                if (!runningNow) return Blocked(result, "not_acquiring", "请先开始采集，核对需要读取 PLC");
                pending = new PendingControl { Verify = true, Command = new ControlCommand { Point = uncertainPoint, Operator = operatorName ?? "" } };
                controlQueue.Add(pending);
            }
            if (!pending.Done.Wait(10000)) {
                pending.Cancelled = true;
                lock (controlSync) controlQueue.Remove(pending);
                return Blocked(result, "busy", "核对读取超时；控制保持锁定");
            }
            return pending.Result;
        }

        ControlResult NewResult(string point, int value, string operatorName) {
            var result = new ControlResult { Point = point, NewValue = value, Operator = operatorName ?? "", Utc = DateTime.UtcNow };
            ControlTarget target = ControlSettings == null ? null : ControlSettings.Find(point);
            if (target != null) { result.Label = target.label; result.Slave = target.slave; result.Address = target.address; }
            return result;
        }

        ControlResult Blocked(ControlResult result, string status, string detail) {
            result.Status = status; result.Detail = detail ?? ""; result.Utc = DateTime.UtcNow;
            LastControlResult = result;
            AppendControlEvent(result, null, null);
            return result;
        }

        static string RangeProblem(ControlTarget target, int value) {
            if (target == null) return "没有控制点定义";
            if (value < target.min || value > target.max) {
                return String.Format(CultureInfo.InvariantCulture, "{0} 允许 {1:0.###}..{2:0.###}{3}，收到 {4}",
                    target.label, target.min, target.max, String.IsNullOrEmpty(target.unit) ? "" : " " + target.unit, value);
            }
            return null;
        }

        internal void ProcessControlQueue(string roundPath, ExperimentStore store, string source, int timeoutMs, CancellationToken token, Action<string> log) {
            List<PendingControl> batch;
            lock (controlSync) {
                if (controlQueue.Count == 0) return;
                batch = new List<PendingControl>(controlQueue);
                controlQueue.Clear();
            }
            foreach (var pending in batch) {
                if (pending.Cancelled) {
                    pending.Result = new ControlResult { Status = "cancelled", Detail = "已取消", Utc = DateTime.UtcNow };
                    pending.Done.Set();
                    continue;
                }
                try {
                    pending.Result = pending.Verify
                        ? ExecuteVerify(pending.Command, store, roundPath, timeoutMs, token, log)
                        : ExecuteOne(pending, roundPath, store, timeoutMs, token, log);
                } catch (OperationCanceledException) {
                    pending.Result = new ControlResult { Status = "cancelled", Detail = "采集已停止", Utc = DateTime.UtcNow };
                    AppendControlEvent(pending.Result, store, roundPath);
                } catch (Exception e) {
                    pending.Result = new ControlResult { Status = "error", Detail = e.Message, Utc = DateTime.UtcNow };
                    AppendControlEvent(pending.Result, store, roundPath);
                }
                pending.Done.Set();
                ControlResult published = pending.Result;
                LastControlResult = published;
                var handler = ControlCompleted;
                if (handler != null) foreach (Action<ControlResult> f in handler.GetInvocationList()) try { f(published); } catch { }
            }
        }

        ControlResult ExecuteOne(PendingControl pending, string roundPath, ExperimentStore store, int timeoutMs, CancellationToken token, Action<string> log) {
            var command = pending.Command;
            var result = NewResult(command.Point, command.Value, command.Operator);
            ControlGateInfo gate = ControlReadiness(command.Point);
            if (!gate.CanWrite) {
                result.Status = gate.Status; result.Detail = gate.Detail; result.Utc = DateTime.UtcNow;
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            ControlTarget target = ControlSettings.Find(command.Point);
            string range = RangeProblem(target, command.Value);
            if (range != null) { result.Status = "range"; result.Detail = range; AppendControlEvent(result, store, roundPath); return result; }
            // 旧值以「写前即时 03 预读」为准，采集快照可能过时（上一轮或更早），否则 not_accepted/mismatch 判定会失真。
            int? oldValue = null;
            Observation pre = ReadBack(target, timeoutMs, token, log);
            if (pre != null && pre.Number.HasValue) oldValue = (int)Math.Round(pre.Number.Value);
            result.PreReadTx = pre == null ? new byte[0] : (pre.Tx ?? new byte[0]);
            result.PreReadRx = pre == null ? new byte[0] : (pre.Rx ?? new byte[0]);
            result.OldSource = pre != null && pre.Number.HasValue ? "写前 03 预读" : "采集快照";
            if (!oldValue.HasValue) {
                Observation old;
                if (lastByPoint.TryGetValue(target.point, out old)) oldValue = NumericValue(old);
            }
            if (!oldValue.HasValue) {
                result.Status = "stale"; result.Detail = "目标点没有有效读数"; AppendControlEvent(result, store, roundPath); return result;
            }
            if (pending.Cancelled) {
                result.Status = "cancelled"; result.Detail = "已取消（尚未发出）";
                AppendControlEvent(result, store, roundPath); return result;
            }
            result.OldValue = oldValue.Value;
            result.NewValue = command.Value;
            result.Tx = Modbus.WriteSingle(target.slave, target.address, command.Value);
            var writeTransport = connectedTransport as IWriteTransport;
            if (writeTransport == null) {
                result.Status = "no_write_channel"; result.Detail = "当前传输不支持写"; AppendControlEvent(result, store, roundPath); return result;
            }
            lock (controlSync) {
                controlWriteTimes.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 60);
                controlWriteTimes.Add(DateTime.UtcNow);
                lastControlWriteUtc = DateTime.UtcNow;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] echo;
            bool writeIssued = false;
            try {
                log("CONTROL TX " + Modbus.Hex(result.Tx));
                writeIssued = true;
                echo = writeTransport.WriteSingle(target.slave, target.address, command.Value, timeoutMs, token, log);
            } catch (OperationCanceledException) {
                if (writeIssued) {
                    result.Status = "unknown"; result.Detail = "写入过程中采集被停止，结果未知；请重新读取核对";
                    result.Milliseconds = watch.ElapsedMilliseconds;
                    MarkUncertain(target, result, "写入过程中被停止");
                    AppendControlEvent(result, store, roundPath);
                    return result;
                }
                throw;
            } catch (TimeoutException e) {
                result.Status = "unknown"; result.Detail = "写入回显超时：" + e.Message; result.Milliseconds = watch.ElapsedMilliseconds;
                MarkUncertain(target, result, "写入回显超时");
                AppendControlEvent(result, store, roundPath);
                return result;
            } catch (Exception e) {
                result.Status = "unknown"; result.Detail = "写入异常：" + e.Message; result.Milliseconds = watch.ElapsedMilliseconds;
                MarkUncertain(target, result, "写入异常");
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            result.Rx = echo;
            if (echo == null || echo.Length < 2) {
                result.Status = "unknown"; result.Detail = "写入回显为空"; result.Milliseconds = watch.ElapsedMilliseconds;
                MarkUncertain(target, result, "写入回显为空");
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            if (echo[1] == 0x86) {
                int code = echo.Length > 2 ? echo[2] : 0;
                result.Status = "rejected";
                result.Detail = "设备拒绝写入：异常码 0x" + code.ToString("X2") + ExceptionHint(code);
                result.Milliseconds = watch.ElapsedMilliseconds;
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            // 回显必须与请求完全对应（站号/功能码/地址/值），否则一律按未知处理。
            if (echo.Length < 8 || echo[0] != (byte)target.slave || echo[1] != 6 ||
                echo[2] != (byte)(target.address >> 8) || echo[3] != (byte)target.address ||
                echo[4] != (byte)(command.Value >> 8) || echo[5] != (byte)command.Value) {
                result.Status = "unknown"; result.Detail = "回显与请求不一致，无法确认写入结果";
                result.Milliseconds = watch.ElapsedMilliseconds;
                MarkUncertain(target, result, "回显与请求不一致");
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            // PLC 可能异步刷新寄存器：按 verifyWindowMs 等一下再回读；不符再等一次重读。
            int verifyWindow = controlSettings == null || controlSettings.limits == null ? 1500 : controlSettings.limits.verifyWindowMs;
            if (verifyWindow > 0 && token.WaitHandle.WaitOne(verifyWindow)) token.ThrowIfCancellationRequested();
            Observation readback = ReadBack(target, timeoutMs, token, log);
            if (readback != null && readback.Number.HasValue && (int)Math.Round(readback.Number.Value) != command.Value && verifyWindow > 0) {
                if (token.WaitHandle.WaitOne(verifyWindow)) token.ThrowIfCancellationRequested();
                Observation again = ReadBack(target, timeoutMs, token, log);
                if (again != null && again.Number.HasValue) readback = again;
            }
            if (readback == null || !readback.Number.HasValue) {
                result.Status = "unverified"; result.Detail = "写入回显正常，但回读失败；已锁定控制，请重新读取核对";
                result.Milliseconds = watch.ElapsedMilliseconds;
                MarkUncertain(target, result, "回读失败");
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            result.Readback = (int)Math.Round(readback.Number.Value);
            result.HasReadback = true;
            result.ReadbackTx = readback.Tx ?? new byte[0];
            result.ReadbackRx = readback.Rx ?? new byte[0];
            result.ReadbackMs = readback.Milliseconds;
            result.Milliseconds = watch.ElapsedMilliseconds;
            if (result.Readback.Value == command.Value) {
                result.Status = "applied"; result.Detail = "回读一致：" + result.OldValue + " → " + result.Readback.Value;
            } else if (result.Readback.Value == result.OldValue) {
                result.Status = "not_accepted"; result.Detail = "PLC 未接受：回读仍为 " + result.OldValue;
            } else {
                result.Status = "mismatch"; result.Detail = "回读为 " + result.Readback.Value + "，既不是旧值也不是新值";
            }
            AppendControlEvent(result, store, roundPath);
            return result;
        }

        ControlResult ExecuteVerify(ControlCommand command, ExperimentStore store, string roundPath, int timeoutMs, CancellationToken token, Action<string> log) {
            var result = new ControlResult { Point = uncertainPoint, Operator = command == null ? "" : command.Operator, Utc = DateTime.UtcNow };
            ControlTarget target = ControlSettings == null ? null : ControlSettings.Find(uncertainPoint);
            if (target == null) { result.Status = "verify_mismatch"; result.Detail = "控制点定义已变化，无法核对"; return result; }
            Observation readback = ReadBack(target, timeoutMs, token, log);
            if (readback == null || !readback.Number.HasValue) {
                result.Status = "verify_mismatch"; result.Detail = "核对读取失败；控制保持锁定";
                AppendControlEvent(result, store, roundPath);
                return result;
            }
            int value = (int)Math.Round(readback.Number.Value);
            result.Readback = value; result.HasReadback = true; result.Label = target.label; result.Slave = target.slave; result.Address = target.address;
            bool cleared = false;
            lock (controlSync) {
                if (uncertainNew.HasValue && value == uncertainNew.Value) { result.Status = "verify_applied"; result.Detail = "核对：写入已生效（当前 " + value + "）"; cleared = true; }
                else if (uncertainOld.HasValue && value == uncertainOld.Value) { result.Status = "verify_not_applied"; result.Detail = "核对：写入未生效（当前仍为 " + value + "）"; cleared = true; }
                else {
                    // 既不是旧值也不是新值：无法判定，但也不能把控制永久锁死。
                    // 以当前读数建立新基线，操作者在审计里能追到这条手动确认。
                    result.Status = "verify_mismatch_manual";
                    result.Detail = "核对：当前值 " + value + " 与写入前/后都不一致；已按当前值建立基线，请人工确认设备状态";
                    cleared = true;
                }
                if (cleared) { controlUncertain = false; uncertainPoint = ""; uncertainOld = uncertainNew = null; uncertainDetail = ""; }
            }
            AppendControlEvent(result, store, roundPath);
            FireControlState();
            return result;
        }

        void MarkUncertain(ControlTarget target, ControlResult result, string reason) {
            lock (controlSync) {
                controlUncertain = true;
                uncertainPoint = target.point;
                uncertainOld = result.OldValue;
                uncertainNew = result.NewValue;
                uncertainDetail = reason;
                controlArmed = false;
            }
            Log("控制结果未知，已锁定：" + reason + " point=" + target.point);
            FireControlState();
        }

        Observation ReadBack(ControlTarget target, int timeoutMs, CancellationToken token, Action<string> log) {
            var transport = connectedTransport;
            if (transport == null) return null;
            var point = new Point {
                name = "控制回读 " + target.point,
                description = "控制回读 " + target.label,
                binding = new Binding { device = "PLC", slave = target.slave, address_zero_based = target.address, register_count = 1, decode_proposal = "int16" }
            };
            Observation o = Collector.Read(transport, point, "UINT16", "control", timeoutMs, token, log);
            return o.Quality == "good" && o.Number.HasValue ? o : null;
        }

        static string ExceptionHint(int code) {
            if (code == 1) return "（非法功能）";
            if (code == 2) return "（非法数据地址）";
            if (code == 3) return "（非法数据值）";
            if (code == 4) return "（从站设备故障）";
            if (code == 6) return "（从站忙）";
            return "";
        }

        internal void FailPendingControls(string reason) {
            List<PendingControl> pending;
            lock (controlSync) { pending = new List<PendingControl>(controlQueue); controlQueue.Clear(); }
            foreach (var p in pending) {
                p.Cancelled = true;
                if (!p.Done.IsSet) {
                    p.Result = new ControlResult { Status = "cancelled", Detail = reason ?? "已取消", Utc = DateTime.UtcNow };
                    p.Done.Set();
                }
            }
        }

        void FireControlState() {
            var handler = ControlStateChanged;
            if (handler != null) foreach (Action f in handler.GetInvocationList()) try { f(); } catch { }
        }

        bool AppendControlEvent(ControlResult result, ExperimentStore store, string roundPath) {
            var config = controlSettings;
            if (store == null || String.IsNullOrEmpty(roundPath)) {
                // 界面线程发起的预演/拦截没有轮次上下文，这里回落到当前采集轮，保证 JSONL 与数据库成对。
                lock (sync) { if (store == null) store = Store; if (String.IsNullOrEmpty(roundPath)) roundPath = activeRoundPath; }
            }
            var e = new ControlEvent {
                UtcTicks = DateTime.UtcNow.Ticks,
                Source = SafeSource(),
                Session = captureSession,
                Operator = result.Operator ?? "",
                Point = result.Point ?? "",
                Label = result.Label ?? "",
                Slave = result.Slave,
                Address = result.Address,
                Function = 6,
                OldValue = result.OldValue,
                NewValue = result.NewValue,
                Readback = result.HasReadback ? result.Readback : null,
                Status = result.Status ?? "",
                Detail = result.Detail ?? "",
                Tx = Modbus.Hex(result.Tx ?? new byte[0]),
                Rx = Modbus.Hex(result.Rx ?? new byte[0]),
                ElapsedMs = result.Milliseconds,
                ConfigVersion = config == null ? "" : config.Version
            };
            bool ok = true;
            lock (controlLogSync) {
                try {
                    string dir = Path.Combine(root, "logs");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "control-events.jsonl"), new JavaScriptSerializer().Serialize(e) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                } catch (Exception ex) { ok = false; Log("控制审计 JSONL 写入失败：" + ex.Message); }
            }
            if (store != null && !String.IsNullOrEmpty(roundPath)) {
                try { store.RecordControl(e, roundPath); } catch (Exception ex) { ok = false; Log("控制审计入库失败：" + ex.Message); }
            }
            result.AuditOk = ok;
            return ok;
        }

        string SafeSource() {
            lock (sync) return connected && !connectedSimulation ? "serial" : (connected ? "simulation" : "serial");
        }
    }
}
