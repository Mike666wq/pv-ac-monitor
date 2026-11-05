using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>
    /// 运行控制台：一行状态条 + 设定表（每行一个控制点）+ 底部过程日志/调试详情。
    /// 一次写入是一个完整事务：读基线 → 读改变前 → 06 下发 → 回显 → 03 回读 → 观察设备返回信号。
    /// </summary>
    public sealed class ControlPage : UserControl {
        readonly MonitorEngine engine;
        readonly List<Point> points;
        readonly string root;
        readonly System.Windows.Forms.Timer timer;
        readonly System.Windows.Forms.Timer animationTimer;

        readonly Button switchButton, sendAllButton, verifyButton, exportButton, clearButton, copyButton;
        readonly Label connLabel, acqLabel, gateLabel, interlockLabel, statLabel, pendingLabel;
        readonly DataGridView planGrid;
        readonly RichTextBox logBox, detailBox;
        readonly Dictionary<string, bool> edited = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly Dictionary<string, StepRecord> steps = new Dictionary<string, StepRecord>(StringComparer.Ordinal);
        readonly Dictionary<string, int?> feedbackBefore = new Dictionary<string, int?>(StringComparer.Ordinal);
        string lastUncertainNotice = "";
        int sentCount, okCount, badCount;
        bool busy, wasRunning, suppressEdit, compactRows;

        sealed class StepRecord {
            public int[] Steps = new int[] { 0, 0, 0, 0 };
            public bool Observing;
            public DateTime ObserveUntil;
            public Dictionary<string, int?> Baseline = new Dictionary<string, int?>(StringComparer.Ordinal);
            public string Note = "";
            public DateTime[] DoneAt = new DateTime[4];
            public bool SameValue;
            public ControlResult Last;
        }

        public ControlPage(List<Point> points, MonitorEngine engine, string root) {
            this.points = points ?? new List<Point>();
            this.engine = engine;
            this.root = root ?? "";
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Canvas;

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8), BackColor = UiTheme.Canvas };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            Controls.Add(shell);

            // ── 状态条 ────────────────────────────────────────────────────────────
            var status = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = UiTheme.Surface, Padding = new Padding(2, 4, 2, 2), Margin = new Padding(0, 0, 0, 4) };
            switchButton = new Button { Text = "控制开关：关闭", Width = 150, Height = 32, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), ForeColor = UiTheme.Danger, BackColor = Color.FromArgb(253, 240, 240), Margin = new Padding(0, 0, 10, 0) };
            switchButton.Click += delegate { ToggleSwitch(); };
            connLabel = Chip("连接 —");
            acqLabel = Chip("采集 —");
            gateLabel = Chip("门禁 —");
            interlockLabel = Chip("联锁 —");
            statLabel = Chip("已发 0 · 成功 0 · 异常 0");
            verifyButton = new Button { Text = "重新读取核对", Width = 116, Height = 30, Visible = false };
            verifyButton.Click += delegate { VerifyClicked(); };
            status.Controls.AddRange(new Control[] { switchButton, connLabel, acqLabel, gateLabel, interlockLabel, statLabel, verifyButton });
            shell.Controls.Add(status, 0, 0);

            // ── 工具条 ────────────────────────────────────────────────────────────
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = UiTheme.Surface, Padding = new Padding(2, 4, 2, 0), Margin = new Padding(0, 0, 0, 4) };
            pendingLabel = new Label { AutoSize = true, Text = "待发 0 个点", ForeColor = UiTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f), Margin = new Padding(4, 8, 16, 0) };
            sendAllButton = new Button { Text = "发送全部已修改", Width = 140, Height = 30, BackColor = UiTheme.Blue, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
            sendAllButton.Click += delegate { SendAllModified(); };
            bar.Controls.Add(pendingLabel);
            bar.Controls.Add(sendAllButton);
            var legend = new Label { AutoSize = true, Text = "① 下发  ② 回显  ③ 回读  ④ 设备", ForeColor = UiTheme.Muted, Font = new Font("Microsoft YaHei UI", 8.5f), Margin = new Padding(16, 9, 0, 0) };
            bar.Controls.Add(legend);
            shell.Controls.Add(bar, 0, 1);

            // ── 设定表 ────────────────────────────────────────────────────────────
            planGrid = new DataGridView {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.Both, CellBorderStyle = DataGridViewCellBorderStyle.Single, GridColor = Color.FromArgb(226, 232, 240),
                AllowUserToResizeRows = false, RowTemplate = { Height = 34 }, Font = new Font("Microsoft YaHei UI", 10)
            };
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "点名", FillWeight = 9, MinimumWidth = 70, ReadOnly = true, Name = "point" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", FillWeight = 16, MinimumWidth = 100, ReadOnly = true, Name = "label" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "当前值", FillWeight = 9, MinimumWidth = 70, ReadOnly = true, Name = "current" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "设定值", FillWeight = 10, MinimumWidth = 80, Name = "value" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单位", FillWeight = 5, MinimumWidth = 55, ReadOnly = true, Name = "unit" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "范围", FillWeight = 8, MinimumWidth = 75, ReadOnly = true, Name = "range" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最近结果", FillWeight = 30, MinimumWidth = 230, ReadOnly = true, Name = "result" });
            planGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "操作", FillWeight = 12, MinimumWidth = 150, ReadOnly = true, Name = "ops" });
            planGrid.Columns["current"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["current"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["value"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["value"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["range"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["range"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            planGrid.Columns["value"].DefaultCellStyle.BackColor = Color.FromArgb(252, 253, 255);
            planGrid.Columns["result"].DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            planGrid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e) {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string name = planGrid.Columns[e.ColumnIndex].Name;
                if (name == "eq") { FillCurrent(planGrid.Rows[e.RowIndex]); return; }
                if (name == "send") SendRow(planGrid.Rows[e.RowIndex]);
            };
            planGrid.CellEndEdit += delegate { ValidateCell(planGrid.CurrentCell); UpdatePending(); };
            planGrid.CellMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e) {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (planGrid.Columns[e.ColumnIndex].Name != "ops") return;
                if (e.X < 8 + 64) SendRow(planGrid.Rows[e.RowIndex]);
                else if (e.X < 8 + 64 + 6 + 76) FillCurrent(planGrid.Rows[e.RowIndex]);
            };
            planGrid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                if (suppressEdit || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (planGrid.Columns[e.ColumnIndex].Name != "value") return;
                ControlTarget editedTarget = planGrid.Rows[e.RowIndex].Tag as ControlTarget;
                if (editedTarget != null) edited[editedTarget.point] = true;
                UpdatePending();
            };
            planGrid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e) {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string columnName = planGrid.Columns[e.ColumnIndex].Name;
                if (columnName != "result" && columnName != "ops") return;
                e.Handled = true;
                e.PaintBackground(e.ClipBounds, true);
                if (columnName == "ops") {
                    DrawOps(e.Graphics, e.CellBounds);
                } else {
                    ControlTarget target = planGrid.Rows[e.RowIndex].Tag as ControlTarget;
                    if (target != null) {
                        StepRecord record;
                        if (!steps.TryGetValue(target.point, out record)) record = new StepRecord();
                        DrawSteps(e.Graphics, e.CellBounds, record);
                    }
                }
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Border);
            };
            planGrid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e) {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (planGrid.Columns[e.ColumnIndex].Name != "result") return;
                ControlTarget target = planGrid.Rows[e.RowIndex].Tag as ControlTarget;
                StepRecord record;
                if (target != null && steps.TryGetValue(target.point, out record)) e.ToolTipText = record.Note;
            };
            planGrid.SelectionChanged += delegate { ShowSelectedDetail(); };
            planGrid.SizeChanged += delegate { AdjustRowHeight(); };

            var gridFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Color.FromArgb(206, 217, 230), Margin = new Padding(0, 0, 0, 6) };
            planGrid.BorderStyle = BorderStyle.None;
            planGrid.Margin = Padding.Empty;
            gridFrame.Controls.Add(planGrid);
            shell.Controls.Add(gridFrame, 0, 2);

            // ── 底部：过程日志 / 调试详情 ─────────────────────────────────────────
            var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9) };
            var logPage = new TabPage("过程日志") { BackColor = UiTheme.Surface, Padding = new Padding(4) };
            logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, Font = new Font("Consolas", 9.5f), WordWrap = false, DetectUrls = false };
            var logBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, FlowDirection = FlowDirection.RightToLeft, BackColor = UiTheme.Surface };
            exportButton = new Button { Text = "导出日志", Width = 88, Height = 26 };
            exportButton.Click += delegate { ExportProcess(); };
            clearButton = new Button { Text = "清空", Width = 60, Height = 26 };
            clearButton.Click += delegate { logBox.Clear(); };
            logBar.Controls.Add(exportButton);
            logBar.Controls.Add(clearButton);
            logPage.Controls.Add(logBox);
            logPage.Controls.Add(logBar);

            var detailPage = new TabPage("调试详情") { BackColor = UiTheme.Surface, Padding = new Padding(4) };
            detailBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, Font = new Font("Consolas", 9f), WordWrap = false, DetectUrls = false };
            var detailBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, FlowDirection = FlowDirection.RightToLeft, BackColor = UiTheme.Surface };
            copyButton = new Button { Text = "复制详情", Width = 88, Height = 26 };
            copyButton.Click += delegate {
                try { Clipboard.SetText(detailBox.Text); Log("[复制] 调试详情已复制", UiTheme.Green); }
                catch (Exception ex) { Log("[复制] 失败：" + ex.Message, UiTheme.Danger); }
            };
            detailBar.Controls.Add(copyButton);
            detailPage.Controls.Add(detailBox);
            detailPage.Controls.Add(detailBar);
            tabs.TabPages.Add(logPage);
            tabs.TabPages.Add(detailPage);
            shell.Controls.Add(tabs, 0, 3);

            try { typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(planGrid, true, null); } catch { }
            animationTimer = new System.Windows.Forms.Timer { Interval = 50 };
            animationTimer.Tick += delegate { TickAnimation(); };
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { RefreshState(); };
            timer.Start();

            BuildPlanRows();
            RefreshState();
        }

        static Label Chip(string text) {
            return new Label { AutoSize = true, Text = text, ForeColor = UiTheme.Muted, Margin = new Padding(2, 6, 12, 0), Font = new Font("Microsoft YaHei UI", 8.5f) };
        }

        List<ControlTarget> Targets() {
            ControlConfig config = engine.ControlSettings;
            return config == null || config.targets == null ? new List<ControlTarget>() : config.targets;
        }

        void BuildPlanRows() {
            planGrid.SuspendLayout();
            try {
                planGrid.Rows.Clear();
                steps.Clear();
                foreach (ControlTarget target in Targets()) {
                    int index = planGrid.Rows.Add(target.point, target.label, "—", "", String.IsNullOrEmpty(target.unit) ? "—" : target.unit, target.min + " – " + target.max, "", "");
                    planGrid.Rows[index].Tag = target;
                    edited[target.point] = false;
                    steps[target.point] = new StepRecord();
                }
            } finally { planGrid.ResumeLayout(); }
            UpdatePending();
            AdjustRowHeight();
        }

        static string RangeText(ControlTarget target) {
            return target.min + ".." + target.max + (String.IsNullOrEmpty(target.unit) ? "" : " " + target.unit);
        }

        // ── 结果绘制：四段胶囊 + 阶段说明（抗锯齿，观察期带剩余进度条）────────
        static readonly string[] StepLabels = { "①下发", "②回显", "③回读", "④设备" };
        void DrawSteps(Graphics graphics, Rectangle bounds, StepRecord record) {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int gap = 4;
            int pillW = Math.Max(38, Math.Min(60, (bounds.Width - 16 - 3 * gap) / 4));
            int pillH = compactRows ? 16 : 20;
            int left = bounds.Left + 8;
            int top = bounds.Top + Math.Max(6, (bounds.Height - (pillH + 24)) / 2);
            for (int i = 0; i < 4; i++) {
                int state = record.Steps[i];
                Color background = state == 2 ? Color.FromArgb(0, 142, 105) : state == 3 ? Color.FromArgb(190, 52, 60) : state == 1 ? Color.FromArgb(54, 112, 220) : state == 4 ? Color.FromArgb(191, 116, 18) : state == 5 ? Color.FromArgb(130, 145, 160) : Color.FromArgb(226, 232, 240);
                Color foreground = state == 0 ? Color.FromArgb(120, 133, 148) : Color.White;
                Rectangle rect = new Rectangle(left + i * (pillW + gap), top, pillW, pillH);
                using (var path = Rounded(rect, 9)) {
                    using (var brush = new SolidBrush(background)) graphics.FillPath(brush, path);
                    if (state == 1) {
                        double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 260.0);
                        using (var overlay = new SolidBrush(Color.FromArgb((int)(36 + 40 * pulse), 255, 255, 255))) graphics.FillPath(overlay, path);
                    }
                    double flashAge = (DateTime.UtcNow - record.DoneAt[i]).TotalMilliseconds;
                    if (record.DoneAt[i] != DateTime.MinValue && flashAge >= 0 && flashAge < 320) {
                        using (var overlay = new SolidBrush(Color.FromArgb((int)(170 * (1 - flashAge / 320)), 255, 255, 255))) graphics.FillPath(overlay, path);
                    }
                }
                using (var font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold)) using (var brush = new SolidBrush(foreground)) {
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                        graphics.DrawString(StepLabels[i], font, brush, rect, format);
                    }
                }
            }
            if (record.Steps[3] == 1) {
                double total = Math.Max(1, ObservationSeconds());
                double remain = Math.Max(0, (record.ObserveUntil - DateTime.UtcNow).TotalSeconds);
                int barW = Math.Max(1, (int)Math.Round(pillW * (remain / total)));
                Rectangle bar = new Rectangle(left + 3 * (pillW + gap), top + pillH + 3, pillW, 4);
                using (var backPath = Rounded(bar, 2)) using (var back = new SolidBrush(Color.FromArgb(226, 232, 240))) graphics.FillPath(back, backPath);
                Rectangle frontRect = new Rectangle(bar.X, bar.Y, barW, bar.Height);
                if (barW > 2) {
                    using (var frontPath = Rounded(frontRect, 2))
                    using (var front = new LinearGradientBrush(frontRect, Color.FromArgb(54, 112, 220), Color.FromArgb(127, 176, 255), LinearGradientMode.Horizontal))
                        graphics.FillPath(front, frontPath);
                }
            }
            string detail = StageDetail(record);
            using (var font = new Font("Microsoft YaHei UI", 8.5f)) using (var brush = new SolidBrush(StageColor(record))) {
                graphics.DrawString(detail, font, brush, left, top + pillH + 9);
            }
        }

        void TickAnimation() {
            if (IsDisposed) return;
            bool animating = false;
            int column = ColumnIndex("result");
            foreach (DataGridViewRow row in planGrid.Rows) {
                ControlTarget target = TargetOf(row);
                if (target == null) continue;
                StepRecord record;
                if (!steps.TryGetValue(target.point, out record)) continue;
                bool observing = record.Steps[3] == 1;
                bool flashing = false;
                for (int i = 0; i < 4; i++) {
                    if (record.DoneAt[i] == DateTime.MinValue) continue;
                    double age = (DateTime.UtcNow - record.DoneAt[i]).TotalMilliseconds;
                    if (age >= 0 && age < 360) flashing = true;
                }
                if (observing || flashing) {
                    animating = true;
                    if (column >= 0) planGrid.InvalidateCell(column, row.Index);
                }
            }
            if (!animating) animationTimer.Stop();
        }

        void EnsureAnimating() {
            if (animationTimer == null) return;
            if (!animationTimer.Enabled) animationTimer.Start();
        }

        static GraphicsPath Rounded(Rectangle rect, int radius) {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void DrawOps(Graphics graphics, Rectangle bounds) {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int h = 26, w1 = 56, w2 = 70, gap = 6;
            int x = bounds.Left + 8;
            int y = bounds.Top + (bounds.Height - h) / 2;
            DrawPill(graphics, new Rectangle(x, y, w1, h), "发送", Color.FromArgb(54, 112, 220), Color.White, true);
            DrawPill(graphics, new Rectangle(x + w1 + gap, y, w2, h), "= 当前", Color.FromArgb(235, 241, 248), UiTheme.Ink, false);
        }

        static void DrawPill(Graphics graphics, Rectangle rect, string text, Color background, Color foreground, bool filled) {
            using (var path = Rounded(rect, 8)) {
                using (var brush = new SolidBrush(background)) graphics.FillPath(brush, path);
                if (!filled) using (var pen = new Pen(Color.FromArgb(208, 218, 230), 1f)) graphics.DrawPath(pen, path);
                using (var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                using (var brush = new SolidBrush(foreground))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                    graphics.DrawString(text, font, brush, rect, format);
                }
            }
        }

        void AdjustRowHeight() {
            if (planGrid.Rows.Count == 0) return;
            int available = planGrid.ClientSize.Height - planGrid.ColumnHeadersHeight - 2;
            int height = available / planGrid.Rows.Count;
            compactRows = height < 50;
            height = Math.Max(34, Math.Min(96, height));
            planGrid.RowTemplate.Height = height;
            foreach (DataGridViewRow row in planGrid.Rows) {
                if (row.Height != height) row.Height = height;
            }
            int total = planGrid.ColumnHeadersHeight + planGrid.Rows.Count * height + 4;
            if (planGrid.Parent != null && planGrid.Parent.Height != total) planGrid.Parent.Height = total;
        }

        static string StageDetail(StepRecord r) {
            if (r == null) return "还没有发送";
            if (r.Steps[3] == 2) return "设备已动作";
            if (r.Steps[3] == 5) return "同值写：设备状态无变化（正常）";
            if (r.Steps[3] == 4) return "观察结束：6 个返回信号无变化";
            if (r.Steps[3] == 1) { int left = Math.Max(0, (int)Math.Ceiling((r.ObserveUntil - DateTime.UtcNow).TotalSeconds)); return "观察设备中，剩余 " + left + " 秒"; }
            if (r.Steps[2] == 3) return "回读不一致，请核对";
            if (r.Steps[2] == 2) return "回读一致";
            if (r.Steps[1] == 3) return "回显异常/为空，结果未知";
            if (r.Steps[1] == 2) return "回显一致，正在回读";
            if (r.Steps[0] == 3) return "被拦截，未发送";
            if (r.Steps[0] == 1) return "正在下发 06 报文";
            if (r.Steps[0] == 2) return "已下发，等待回显";
            return "还没有发送";
        }

        static Color StageColor(StepRecord r) {
            if (r == null) return Color.FromArgb(130, 145, 160);
            if (r.Steps[3] == 2) return Color.FromArgb(0, 142, 105);
            if (r.Steps[3] == 4) return Color.FromArgb(191, 116, 18);
            if (r.Steps[3] == 5) return Color.FromArgb(130, 145, 160);
            if (r.Steps[2] == 3 || r.Steps[1] == 3) return Color.FromArgb(190, 52, 60);
            if (r.Steps[3] == 1) return Color.FromArgb(54, 112, 220);
            if (r.Steps[2] == 2) return Color.FromArgb(0, 142, 105);
            return Color.FromArgb(100, 119, 141);
        }

        // ── 表格辅助 ────────────────────────────────────────────────────────────
        ControlTarget TargetOf(DataGridViewRow row) { return row == null ? null : row.Tag as ControlTarget; }

        void FillCurrent(DataGridViewRow row) {
            ControlTarget target = TargetOf(row);
            if (target == null) return;
            int? current = engine.ControlNumericValue(target.point);
            if (!current.HasValue) { Log("[= 当前] " + target.point + " 还没有有效读数", UiTheme.Warning); return; }
            row.Cells[3].Value = current.Value.ToString(CultureInfo.InvariantCulture);
            row.Cells[3].Style.ForeColor = UiTheme.Ink;
            UpdatePending();
        }

        void UpdatePending() {
            int count = 0;
            foreach (DataGridViewRow row in planGrid.Rows) {
                ControlTarget target = TargetOf(row);
                if (target == null) continue;
                int value;
                if (!TryValue(row, out value)) continue;
                int? current = engine.ControlNumericValue(target.point);
                if (!current.HasValue || current.Value != value) count++;
            }
            pendingLabel.Text = "待发 " + count + " 个点";
            sendAllButton.Enabled = !busy && count > 0;
        }

        bool TryValue(DataGridViewRow row, out int value) {
            value = 0;
            string text = Convert.ToString(row.Cells[3].Value).Trim();
            return Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        void ValidateCell(DataGridViewCell cell) {
            if (cell == null || cell.ColumnIndex != 3) return;
            ControlTarget target = cell.OwningRow == null ? null : TargetOf(cell.OwningRow);
            if (target == null) return;
            int value;
            if (!Int32.TryParse(Convert.ToString(cell.Value).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
                cell.Style.ForeColor = UiTheme.Danger; cell.ToolTipText = "必须是整数"; return;
            }
            if (value < target.min || value > target.max) {
                cell.Style.ForeColor = UiTheme.Warning; cell.ToolTipText = "超出范围 " + target.min + ".." + target.max; return;
            }
            cell.Style.ForeColor = UiTheme.Ink; cell.ToolTipText = "";
        }

        // ── 发送 ────────────────────────────────────────────────────────────────
        void SendRow(DataGridViewRow row) {
            ControlTarget target = TargetOf(row);
            if (target == null) return;
            int value;
            if (!TryValue(row, out value)) { Log("[发送] " + target.point + " 的设定值不是整数", UiTheme.Warning); return; }
            if (value < target.min || value > target.max) { Log("[发送] " + target.point + " 的设定值 " + value + " 超出范围", UiTheme.Warning); return; }
            SendItems(new List<DataGridViewRow> { row });
        }

        void SendAllModified() {
            List<DataGridViewRow> rows = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in planGrid.Rows) {
                ControlTarget target = TargetOf(row);
                if (target == null) continue;
                int value;
                if (!TryValue(row, out value)) { Log("[发送] " + target.point + " 的设定值不是整数，已跳过", UiTheme.Warning); continue; }
                if (value < target.min || value > target.max) { Log("[发送] " + target.point + " 的设定值 " + value + " 超出范围，已跳过", UiTheme.Warning); continue; }
                int? current = engine.ControlNumericValue(target.point);
                if (current.HasValue && current.Value == value) continue;
                rows.Add(row);
            }
            if (rows.Count == 0) { Log("[发送] 没有「已修改且有效」的设定值", UiTheme.Warning); return; }
            SendItems(rows);
        }

        void SendItems(List<DataGridViewRow> rows) {
            if (!engine.ControlEnabled) { Log("[发送] 控制开关未开启，已拦截（未发送任何字节）", UiTheme.Warning); return; }
            feedbackBefore.Clear();
            foreach (string feedback in FeedbackPoints()) feedbackBefore[feedback] = engine.ControlNumericValue(feedback);
            string op = OperatorName();
            List<string> pointsToSend = new List<string>();
            foreach (DataGridViewRow row in rows) {
                ControlTarget target = TargetOf(row);
                int value;
                if (target == null || !TryValue(row, out value)) continue;
                pointsToSend.Add(target.point);
                StepRecord record = Record(target.point);
                record.Steps = new int[] { 1, 0, 0, 0 };
                record.DoneAt = new DateTime[4];
                EnsureAnimating();
                record.Observing = false;
                record.Note = "已提交，等待下发";
                Log("[输入] " + target.point + " " + target.label + "  " + OldText(target) + " → " + value + (String.IsNullOrEmpty(target.unit) ? "" : " " + target.unit), UiTheme.Ink);
            }
            planGrid.InvalidateColumn(ColumnIndex("result"));
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate {
                foreach (string point in pointsToSend) {
                    ControlTarget target = Targets().FirstOrDefault(t => t.point == point);
                    if (target == null) continue;
                    int value;
                    DataGridViewRow row = FindRow(point);
                    if (row == null || !TryValue(row, out value)) continue;
                    Dictionary<string, int?> snapshot = new Dictionary<string, int?>(StringComparer.Ordinal);
                    foreach (string feedback in FeedbackPoints()) snapshot[feedback] = engine.ControlNumericValue(feedback);
                    ControlResult result = ExecuteWithCooldown(target, value, op);
                    if (IsDisposed) return;
                    try { BeginInvoke(new Action(delegate { ReportResult(result, point, snapshot); })); } catch { }
                    if (result != null && result.Status == "unknown") {
                        try { BeginInvoke(new Action(delegate { Log("[停止] " + point + " 结果未知，整批发送已停止；请先点「重新读取核对」", UiTheme.Danger); })); } catch { }
                        break;
                    }
                }
                try { BeginInvoke(new Action(delegate { busy = false; RefreshState(); })); } catch { }
            });
            RefreshState();
        }

        DataGridViewRow FindRow(string point) {
            foreach (DataGridViewRow row in planGrid.Rows) {
                ControlTarget target = TargetOf(row);
                if (target != null && target.point == point) return row;
            }
            return null;
        }

        int ColumnIndex(string name) {
            for (int i = 0; i < planGrid.Columns.Count; i++) if (planGrid.Columns[i].Name == name) return i;
            return -1;
        }

        ControlResult ExecuteWithCooldown(ControlTarget target, int value, string op) {
            ControlResult result;
            try { result = engine.ExecuteControl(target.point, value, op); }
            catch (Exception ex) { result = new ControlResult { Status = "error", Point = target.point, NewValue = value, Operator = op, Detail = ex.Message, Utc = DateTime.UtcNow }; }
            if (result != null && result.Status == "rate") {
                System.Threading.Thread.Sleep(1300);
                try { result = engine.ExecuteControl(target.point, value, op); }
                catch (Exception ex) { result = new ControlResult { Status = "error", Point = target.point, NewValue = value, Operator = op, Detail = ex.Message, Utc = DateTime.UtcNow }; }
            }
            return result;
        }

        StepRecord Record(string point) {
            StepRecord record;
            if (!steps.TryGetValue(point, out record)) { record = new StepRecord(); steps[point] = record; }
            return record;
        }

        void ReportResult(ControlResult result, string point, Dictionary<string, int?> snapshot) {
            if (result == null) return;
            StepRecord record = Record(point);
            record.Last = result;
            sentCount++;
            if (result.Status == "applied") okCount++; else badCount++;
            bool sent = result.Tx != null && result.Tx.Length > 0;
            bool echoed = result.Rx != null && result.Rx.Length > 0;
            bool rejected = result.Status == "rejected";
            bool readbackOk = result.HasReadback && result.Readback.Value == result.NewValue;
            record.Steps[0] = sent ? 2 : 3;
            record.Steps[1] = rejected ? 3 : echoed ? 2 : 3;
            record.Steps[2] = result.HasReadback ? (readbackOk ? 2 : 3) : 3;
            for (int i = 0; i < 3; i++) if (record.Steps[i] >= 2) record.DoneAt[i] = DateTime.UtcNow;
            EnsureAnimating();

            string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            Color tone = result.Status == "applied" ? UiTheme.Green : result.Status == "unknown" || rejected ? UiTheme.Danger : UiTheme.Warning;
            Log(stamp + " [结果] " + result.Point + " " + result.Status + (String.IsNullOrEmpty(result.Detail) ? "" : " · " + result.Detail), tone);
            if (sent) Log(stamp + " [TX ] " + Modbus.Hex(result.Tx) + "   （06 slave=" + result.Slave + " addr=" + result.Address + " value=" + result.NewValue + "）", UiTheme.Muted);
            if (echoed) Log(stamp + " [RX ] " + Modbus.Hex(result.Rx) + "   （PLC 回显）", UiTheme.Muted);
            if (result.HasReadback) Log(stamp + " [回读] 03 → " + result.Point + " = " + result.Readback.Value + (readbackOk ? "（一致）" : "（不一致）"), UiTheme.Muted);
            string change = StateChangeText(result);
            if (!String.IsNullOrEmpty(change)) Log(stamp + " [状态] " + result.Point + " " + change, UiTheme.Muted);
            Log(stamp + " [审计] " + (result.AuditOk ? "control_events + logs/control-events.jsonl 已记录" : "⚠ 审计写入失败，请检查磁盘与日志目录"), result.AuditOk ? UiTheme.Muted : UiTheme.Danger);

            edited[point] = false;
            record.Baseline = snapshot ?? new Dictionary<string, int?>(StringComparer.Ordinal);
            record.SameValue = result.OldValue == result.NewValue;
            if (result.HasReadback && (result.Status == "applied" || result.Status == "not_accepted" || result.Status == "mismatch")) StartObservation(record);
            else if (result.Status == "unknown" || result.Status == "rejected") record.Note = result.Status + "：" + result.Detail;

            statLabel.Text = "已发 " + sentCount + " · 成功 " + okCount + " · 异常 " + badCount;
            planGrid.InvalidateColumn(ColumnIndex("result"));
            ShowDetail(result, record);
            UpdatePending();
        }

        string StateChangeText(ControlResult result) {
            StringBuilder sb = new StringBuilder();
            sb.Append(result.Point).Append(" ").Append(result.OldValue).Append(" → ").Append(result.HasReadback ? result.Readback.Value : result.NewValue);
            int? before;
            if (feedbackBefore.TryGetValue("D2209", out before)) sb.Append(" | 风机 ").Append(Show(before)).Append("→").Append(Show(engine.ControlNumericValue("D2209")));
            if (feedbackBefore.TryGetValue("D2208", out before)) sb.Append(" | 故障 ").Append(Show(before)).Append("→").Append(Show(engine.ControlNumericValue("D2208")));
            return sb.ToString();
        }

        static string Show(int? value) { return value.HasValue ? value.Value.ToString() : "—"; }


        void StartObservation(StepRecord record) {
            record.Observing = true;
            record.Steps[3] = 1;
            int seconds = ObservationSeconds();
            record.ObserveUntil = DateTime.UtcNow.AddSeconds(seconds);
            record.Baseline.Clear();
            foreach (string feedback in FeedbackPoints()) record.Baseline[feedback] = engine.ControlNumericValue(feedback);
            record.Note = "观察设备返回信号 " + seconds + " 秒";
        }

        int ObservationSeconds() {
            ControlConfig config = engine.ControlSettings;
            if (config == null || config.limits == null) return 10;
            return Math.Max(5, Math.Min(60, config.limits.observeFeedbackSeconds));
        }

        void TickObservation() {
            if (steps.Count == 0) return;
            bool dirty = false;
            foreach (var pair in steps) {
                StepRecord record = pair.Value;
                if (!record.Observing) continue;
                bool changed = false;
                foreach (var baseline in record.Baseline) {
                    int? now = engine.ControlNumericValue(baseline.Key);
                    if (baseline.Value.HasValue && now.HasValue && baseline.Value.Value != now.Value) { changed = true; break; }
                }
                if (changed) {
                    record.Observing = false;
                    record.Steps[3] = 2;
                    record.Note = "设备返回信号有变化（已动作）";
                    dirty = true;
                } else if (DateTime.UtcNow >= record.ObserveUntil) {
                    record.Observing = false;
                    record.Steps[3] = 4;
                    record.Note = "观察窗口结束，设备返回信号无变化（未动作）";
                    dirty = true;
                }
            }
            if (dirty) planGrid.InvalidateColumn(ColumnIndex("result"));
        }

        // ── 详情 ────────────────────────────────────────────────────────────────
        void ShowSelectedDetail() {
            ControlTarget target = planGrid.SelectedRows.Count > 0 ? TargetOf(planGrid.SelectedRows[0]) : null;
            if (target == null) return;
            StepRecord record;
            if (steps.TryGetValue(target.point, out record) && record.Last != null) ShowDetail(record.Last, record);
            else detailBox.Text = target.point + " " + target.label + "\r\n\r\n还没有发送记录。\r\n在「设定值」填写后点该行「发送」，或点「发送全部已修改」。";
        }

        void ShowDetail(ControlResult r, StepRecord record) {
            if (detailBox == null || r == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("点        " + r.Point + " " + r.Label);
            sb.AppendLine("地址      slave " + r.Slave + " / " + r.Address + "（仅调试显示）");
            sb.AppendLine("改变前    " + r.OldValue + "（" + (String.IsNullOrEmpty(r.OldSource) ? "—" : r.OldSource) + "）");
            sb.AppendLine("写入      " + r.NewValue);
            sb.AppendLine("改变后    " + (r.HasReadback ? r.Readback.Value + (r.Readback.Value == r.NewValue ? "（一致）" : "（不一致）") : "未回读"));
            sb.AppendLine("状态      " + r.Status);
            sb.AppendLine("耗时      总 " + r.Milliseconds + " ms · 回读 " + r.ReadbackMs + " ms");
            sb.AppendLine();
            sb.AppendLine("06 请求   " + Modbus.Hex(r.Tx ?? new byte[0]));
            sb.AppendLine("06 回显   " + Modbus.Hex(r.Rx ?? new byte[0]));
            sb.AppendLine("03 预读   TX " + Modbus.Hex(r.PreReadTx ?? new byte[0]));
            sb.AppendLine("          RX " + Modbus.Hex(r.PreReadRx ?? new byte[0]));
            sb.AppendLine("03 回读   TX " + Modbus.Hex(r.ReadbackTx ?? new byte[0]));
            sb.AppendLine("          RX " + Modbus.Hex(r.ReadbackRx ?? new byte[0]));
            sb.AppendLine();
            sb.AppendLine("设备返回信号（观测 " + ObservationSeconds() + " 秒）");
            foreach (string point in FeedbackPoints()) {
                PointInfo info = PointCatalog.Get(point);
                int? before;
                record.Baseline.TryGetValue(point, out before);
                sb.AppendLine("  " + point.PadRight(6) + " " + (info.Label ?? "").PadRight(8) + " " + Show(before) + " → " + Show(engine.ControlNumericValue(point)) + (record.Steps[3] == 2 ? "  已动作" : record.Steps[3] == 4 ? "  未动作" : record.Steps[3] == 1 ? "  观察中" : ""));
            }
            sb.AppendLine();
            sb.AppendLine("操作者    " + r.Operator);
            sb.AppendLine("配置版本  " + (engine.ControlSettings == null ? "—" : engine.ControlSettings.Version));
            sb.AppendLine("审计      " + (r.AuditOk ? "control_events + logs/control-events.jsonl" : "写入失败，请检查磁盘与日志目录"));
            sb.AppendLine("明细      " + r.Detail);
            detailBox.Text = sb.ToString();
        }

        List<string> FeedbackPoints() {
            ControlConfig config = engine.ControlSettings;
            if (config != null && config.feedbackPoints != null && config.feedbackPoints.Count > 0) return config.feedbackPoints;
            return new List<string> { "D2208", "D2209", "D2210", "D2211", "D2212", "D2213" };
        }

        // ── 状态刷新 ────────────────────────────────────────────────────────────
        void ToggleSwitch() {
            try {
                bool enabled = !engine.ControlEnabled;
                engine.SetControlEnabled(enabled, OperatorName());
                Log(enabled ? "[开关] 控制开关已开启，可以发送" : "[开关] 控制开关已关闭，不再发送任何写入", enabled ? UiTheme.Green : UiTheme.Muted);
            } catch (Exception ex) {
                Log("[开关] 无法开启：" + ex.Message, UiTheme.Danger);
            }
            RefreshState();
        }

        void VerifyClicked() {
            busy = true; RefreshState();
            string op = OperatorName();
            ThreadPool.QueueUserWorkItem(delegate {
                ControlResult result;
                try { result = engine.ResolveControlUncertainty(op); }
                catch (Exception ex) { result = new ControlResult { Status = "error", Detail = ex.Message, Utc = DateTime.UtcNow }; }
                if (IsDisposed) return;
                try { BeginInvoke(new Action(delegate { busy = false; Log("[核对] " + result.Status + " · " + result.Detail, result.Status == "verify_applied" ? UiTheme.Green : UiTheme.Warning); RefreshState(); })); } catch { }
            });
        }

        void RefreshState() {
            if (IsDisposed) return;
            try { engine.TickControl(); } catch { }
            ControlConfig config = engine.ControlSettings;
            bool enabled = engine.ControlEnabled;
            switchButton.Text = enabled ? "控制开关：开启" : "控制开关：关闭";
            switchButton.ForeColor = enabled ? UiTheme.Green : UiTheme.Danger;
            switchButton.BackColor = enabled ? Color.FromArgb(232, 248, 242) : Color.FromArgb(253, 240, 240);
            connLabel.Text = "连接 " + (engine.IsConnected ? (engine.IsSimulation ? "模拟数据" : engine.ConnectedPortName + " · " + engine.ConnectedBaud + " 8N1") : "未连接");
            connLabel.ForeColor = engine.IsConnected ? UiTheme.Green : UiTheme.Muted;
            acqLabel.Text = "采集 " + (engine.IsRunning ? "运行中 · 周期 " + engine.CurrentPeriodSeconds + "s · 超时 " + engine.CurrentTimeoutSeconds + "s" : "已停止");
            acqLabel.ForeColor = engine.IsRunning ? UiTheme.Green : UiTheme.Muted;
            interlockLabel.Text = "联锁 D2003=" + ValueOf("D2003") + " D2208=" + ValueOf("D2208") + " · " + Age("D2003") + "（提示）";
            ControlTarget first = Targets().FirstOrDefault();
            ControlGateInfo gate = engine.ControlReadiness(first == null ? "" : first.point);
            if (gate != null && gate.Status == "ready") { gateLabel.Text = "门禁 可发送"; gateLabel.ForeColor = UiTheme.Green; }
            else {
                string gateText = gate == null ? "—" : gate.Detail;
                if (gateText.Contains("mode=disabled")) gateText = "已禁用（配置 mode=disabled）";
                else if (gateText.Contains("控制开关未开启")) gateText = "开关未开启";
                else if (gateText.Length > 24) gateText = gateText.Substring(0, 24) + "…";
                gateLabel.Text = "门禁 " + gateText;
                gateLabel.ForeColor = UiTheme.Warning;
            }
            verifyButton.Enabled = !busy && engine.ControlUncertain;
            if (engine.ControlUncertain) {
                string notice = engine.ControlUncertainPoint + " · " + engine.ControlUncertainDetail;
                if (notice != lastUncertainNotice) { lastUncertainNotice = notice; Log("[状态] 上一次写入结果未知：" + notice + "；请点「重新读取核对」确认", UiTheme.Danger); }
            } else lastUncertainNotice = "";
            if (engine.IsRunning && !wasRunning) LogBaseline();
            wasRunning = engine.IsRunning;
            TickObservation();
            RefreshRows();
        }

        void LogBaseline() {
            StringBuilder sb = new StringBuilder("[基线] 采集开始，控制点：");
            foreach (ControlTarget target in Targets()) sb.Append(" ").Append(target.point).Append("=").Append(ValueOf(target.point));
            sb.Append("；联锁： D2003=").Append(ValueOf("D2003")).Append(" D2208=").Append(ValueOf("D2208"));
            Log(sb.ToString(), UiTheme.Blue);
        }

        void RefreshRows() {
            bool editing = planGrid.IsCurrentCellInEditMode;
            foreach (DataGridViewRow row in planGrid.Rows) {
                ControlTarget target = TargetOf(row);
                if (target == null) continue;
                int? current = engine.ControlNumericValue(target.point);
                row.Cells[2].Value = current.HasValue ? current.Value.ToString() : "—";
                row.Cells[2].ToolTipText = current.HasValue ? "最近采集值" : "尚未采集到有效值";
                bool userEdited; edited.TryGetValue(target.point, out userEdited);
                if (!editing && !userEdited && current.HasValue) {
                    int follow = current.Value < target.min ? (int)target.min : current.Value > target.max ? (int)target.max : current.Value;
                    if (Convert.ToString(row.Cells[3].Value) != follow.ToString(CultureInfo.InvariantCulture)) {
                        suppressEdit = true;
                        try { row.Cells[3].Value = follow.ToString(CultureInfo.InvariantCulture); } finally { suppressEdit = false; }
                    }
                }
            }
            UpdatePending();
        }

        string ValueOf(string point) {
            int? numeric = engine.ControlNumericValue(point);
            return numeric.HasValue ? numeric.Value.ToString() : "—";
        }

        string Age(string point) {
            Observation o = engine.LastObservation(point);
            if (o == null) return "无数据";
            double seconds = (DateTime.UtcNow - o.Utc).TotalSeconds;
            return seconds < 0 ? "—" : seconds < 10 ? seconds.ToString("0.0") + "s" : (seconds < 60 ? (int)seconds + "s" : (int)(seconds / 60) + "m" + (int)(seconds % 60) + "s");
        }

        void Log(string line, Color tone) {
            if (logBox == null) return;
            if (logBox.Lines.Length > 800) {
                int cut = logBox.GetFirstCharIndexFromLine(logBox.Lines.Length - 600);
                logBox.Select(0, cut);
                logBox.SelectedText = "";
            }
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = tone;
            logBox.AppendText(line + Environment.NewLine);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        void Log(string line) { Log(line, UiTheme.Ink); }

        void ExportProcess() {
            try {
                string dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "control-session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, logBox.Text, new System.Text.UTF8Encoding(false));
                Log("[导出] 测试过程已保存：" + path, UiTheme.Green);
            } catch (Exception ex) {
                Log("[导出] 失败：" + ex.Message, UiTheme.Danger);
            }
        }

        static string OperatorName() {
            string name = Environment.UserName;
            return String.IsNullOrWhiteSpace(name) ? "operator" : name;
        }

        string OldText(ControlTarget target) {
            int? current = engine.ControlNumericValue(target.point);
            return current.HasValue ? current.Value.ToString() : "—";
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                try { timer.Stop(); timer.Dispose(); } catch { }
                try { animationTimer.Stop(); animationTimer.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
