using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    /// <summary>Persistent source-specific database partition schedule.</summary>
    public sealed class PartitionCycle : IDisposable {
        sealed class State {
            public int Version = 2;
            public int ActiveDays = 30;
            public int PendingDays;
            public int Epoch = 1;
            public long AnchorTicks;
            public long PendingBoundaryTicks;
            public int ImmediateDays;
        }

        readonly object sync = new object();
        readonly string root, source, statePath;
        State state;
        DateTime roundStartUtc = DateTime.MinValue;
        long roundNumber;
        string currentPath;

        public int ActiveDays { get { lock (sync) return state.ActiveDays; } }
        public int PendingDays { get { lock (sync) return state.ImmediateDays > 0 ? state.ImmediateDays : state.PendingDays; } }
        public string CurrentPath { get { lock (sync) return currentPath; } }
        public string StartupNotice { get; private set; }

        public PartitionCycle(string dataRoot, string source, string settingsPath) {
            if (source != "serial" && source != "simulation") throw new ArgumentException("来源无效", "source");
            root = Path.GetFullPath(dataRoot);
            this.source = source;
            statePath = Path.GetFullPath(settingsPath);
            StartupNotice = string.Empty;
            Directory.CreateDirectory(root);
            Load();
        }

        void Load() {
            try {
                if (!File.Exists(statePath)) { state = new State(); return; }
                State loaded = new JavaScriptSerializer().Deserialize<State>(File.ReadAllText(statePath, Encoding.UTF8));
                if (loaded == null || (loaded.Version != 1 && loaded.Version != 2) ||
                    loaded.ActiveDays < 1 || loaded.ActiveDays > 3650 || loaded.PendingDays < 0 || loaded.PendingDays > 3650 ||
                    loaded.Epoch < 1 || loaded.AnchorTicks < 0 || loaded.PendingBoundaryTicks < 0 || loaded.ImmediateDays < 0 || loaded.ImmediateDays > 3650)
                    throw new InvalidDataException("分期状态字段无效");
                // Version 1 persisted ActiveDays immediately while an in-flight round was
                // still using the previous partition. Preserve the saved schedule safely.
                if (loaded.Version == 1) {
                    loaded.Version = 2;
                    loaded.ImmediateDays = 0;
                }
                state = loaded;
                if (state.PendingDays == 0) state.PendingBoundaryTicks = 0;
            } catch (Exception e) {
                string backup = statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                try { if (File.Exists(statePath)) File.Move(statePath, backup); } catch { }
                state = new State();
                StartupNotice = "分期设置损坏，已隔离原配置并恢复30天默认值：" + e.Message;
            }
        }

        public void EnsureInitialized(DateTime utc) {
            utc = utc.ToUniversalTime();
            lock (sync) InitializeLocked(utc);
        }

        void InitializeLocked(DateTime utc) {
            if (state.AnchorTicks != 0) return;
            state.AnchorTicks = utc.Ticks;
            if (state.PendingDays > 0) state.PendingBoundaryTicks = utc.AddDays(state.ActiveDays).Ticks;
            Save();
        }

        public string Summary(DateTime now) {
            now = now.ToUniversalTime();
            lock (sync) {
                if (state.AnchorTicks == 0) return "当前每 " + state.ActiveDays + " 天一库；连接时开始计期。" +
                    (PendingDaysLocked() > 0 ? "下一分期改为 " + PendingDaysLocked() + " 天。" : "");
                DateTime anchor = new DateTime(state.AnchorTicks, DateTimeKind.Utc);
                DateTime start = CycleStart(now, anchor, state.ActiveDays);
                DateTime boundary = state.PendingDays > 0 && state.PendingBoundaryTicks > 0
                    ? new DateTime(state.PendingBoundaryTicks, DateTimeKind.Utc) : start.AddDays(state.ActiveDays);
                string text = "当前每 " + state.ActiveDays + " 天一库；下一切换 " + boundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (state.ImmediateDays > 0) text += "；当前轮次结束后立即改为 " + state.ImmediateDays + " 天";
                else if (state.PendingDays > 0) text += " 起改为 " + state.PendingDays + " 天";
                return text + "。";
            }
        }

        int PendingDaysLocked() { return state.ImmediateDays > 0 ? state.ImmediateDays : state.PendingDays; }

        public string ResolvePath(DateTime utc) {
            utc = utc.ToUniversalTime();
            lock (sync) {
                InitializeLocked(utc);
                return PathForLocked(utc);
            }
        }

        public string BeginRound(DateTime utc, long round) {
            utc = utc.ToUniversalTime();
            lock (sync) {
                if (roundStartUtc != DateTime.MinValue) throw new InvalidOperationException("上一采集轮次尚未完成");
                InitializeLocked(utc);
                ApplyPendingAtBoundary(utc);
                roundStartUtc = utc;
                roundNumber = round;
                currentPath = PathForLocked(utc);
                return currentPath;
            }
        }

        public void CompleteRound(long round, DateTime endUtc) {
            endUtc = endUtc.ToUniversalTime();
            lock (sync) {
                if (roundStartUtc == DateTime.MinValue || round != roundNumber) return;
                roundStartUtc = DateTime.MinValue;
                roundNumber = 0;
                if (state.ImmediateDays > 0) {
                    state.ActiveDays = state.ImmediateDays;
                    state.ImmediateDays = 0;
                    state.PendingDays = 0;
                    state.PendingBoundaryTicks = 0;
                    state.AnchorTicks = endUtc.Ticks;
                    state.Epoch++;
                    Save();
                }
            }
        }

        public void Configure(int days, bool immediate, DateTime now, bool collecting, long round) {
            if (days < 1 || days > 3650) throw new ArgumentOutOfRangeException("days", "分期范围为1到3650天");
            now = now.ToUniversalTime();
            lock (sync) {
                InitializeLocked(now);
                if (immediate) {
                    if (collecting && roundStartUtc != DateTime.MinValue) {
                        state.ImmediateDays = days;
                        state.PendingDays = 0;
                        state.PendingBoundaryTicks = 0;
                    } else {
                        state.ActiveDays = days;
                        state.ImmediateDays = 0;
                        state.PendingDays = 0;
                        state.PendingBoundaryTicks = 0;
                        state.AnchorTicks = now.Ticks;
                        state.Epoch++;
                    }
                } else {
                    state.ImmediateDays = 0;
                    state.PendingDays = days == state.ActiveDays ? 0 : days;
                    if (state.PendingDays == 0) state.PendingBoundaryTicks = 0;
                    else {
                        DateTime anchor = new DateTime(state.AnchorTicks, DateTimeKind.Utc);
                        DateTime cycleStart = CycleStart(now, anchor, state.ActiveDays);
                        // If configuration is changed exactly at a boundary while idle,
                        // the next collection round should use the new schedule at once.
                        DateTime boundary = now.Ticks == cycleStart.Ticks ? cycleStart : cycleStart.AddDays(state.ActiveDays);
                        state.PendingBoundaryTicks = boundary.Ticks;
                    }
                }
                Save();
            }
        }

        void ApplyPendingAtBoundary(DateTime utc) {
            if (state.ImmediateDays > 0) {
                state.ActiveDays = state.ImmediateDays;
                state.ImmediateDays = 0;
                state.PendingDays = 0;
                state.PendingBoundaryTicks = 0;
                state.AnchorTicks = utc.Ticks;
                state.Epoch++;
                Save();
                return;
            }
            if (state.PendingDays <= 0 || state.PendingBoundaryTicks <= 0 || utc.Ticks < state.PendingBoundaryTicks) return;
            DateTime boundary = new DateTime(state.PendingBoundaryTicks, DateTimeKind.Utc);
            state.ActiveDays = state.PendingDays;
            state.PendingDays = 0;
            state.PendingBoundaryTicks = 0;
            state.AnchorTicks = boundary.Ticks;
            state.Epoch++;
            Save();
        }

        string PathForLocked(DateTime utc) {
            DateTime anchor = new DateTime(state.AnchorTicks, DateTimeKind.Utc);
            DateTime start = CycleStart(utc, anchor, state.ActiveDays);
            string file = source + "_cycle_" + start.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) +
                "_" + state.ActiveDays.ToString(CultureInfo.InvariantCulture) + "d_" + state.Epoch.ToString("D6", CultureInfo.InvariantCulture) + "_001.db";
            return Path.Combine(root, source, file);
        }

        static DateTime CycleStart(DateTime utc, DateTime anchor, int days) {
            long span = TimeSpan.FromDays(days).Ticks;
            long delta = utc.Ticks - anchor.Ticks;
            long n = delta >= 0 ? delta / span : -(((-delta) + span - 1) / span);
            return new DateTime(anchor.Ticks + n * span, DateTimeKind.Utc);
        }

        void Save() {
            state.Version = 2;
            string parent = Path.GetDirectoryName(statePath);
            Directory.CreateDirectory(parent);
            string tmp = statePath + "." + Guid.NewGuid().ToString("N") + ".partial";
            try {
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) fs.Flush(true);
                if (File.Exists(statePath)) File.Replace(tmp, statePath, null); else File.Move(tmp, statePath);
            } finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
        }

        public void Dispose() { }
    }
}
