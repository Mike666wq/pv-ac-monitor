using System;
using System.Drawing;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>控制写入/解锁前的二次确认。不发送任何报文，只做文字确认。</summary>
    internal sealed class ControlConfirmForm : Form {
        readonly TextBox phrase;
        readonly CheckBox acknowledge;
        readonly Button confirm;
        public bool Confirmed { get; private set; }

        public ControlConfirmForm(string title, string body, string requiredPhrase) {
            if (requiredPhrase == null) requiredPhrase = "确认";
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(560, 300);
            BackColor = UiTheme.Surface;
            Font = new Font("Microsoft YaHei UI", 9);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            layout.Controls.Add(new TextBox { Text = body, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 250, 253), BorderStyle = BorderStyle.FixedSingle }, 0, 1);

            acknowledge = new CheckBox { Text = "我已获得现场许可，并确认写入目标、地址和数值", AutoSize = true, ForeColor = UiTheme.Ink, Margin = new Padding(0, 10, 0, 4) };
            layout.Controls.Add(acknowledge, 0, 2);

            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            row.Controls.Add(new Label { Text = "请输入“" + requiredPhrase + "”以继续：", AutoSize = true, Padding = new Padding(0, 8, 6, 0), ForeColor = UiTheme.Muted });
            phrase = new TextBox { Width = 180 };
            row.Controls.Add(new RoundedInputHost(phrase) { Width = 196 });
            layout.Controls.Add(row, 0, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 96, Height = 34, Margin = new Padding(6, 0, 0, 0) };
            confirm = new Button { Text = "确认", Width = 96, Height = 34, Enabled = false, BackColor = UiTheme.Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            confirm.FlatAppearance.BorderSize = 0;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(confirm);
            layout.Controls.Add(buttons, 0, 4);

            EventHandler validate = delegate { confirm.Enabled = acknowledge.Checked && String.Equals(phrase.Text.Trim(), requiredPhrase, StringComparison.Ordinal); };
            acknowledge.CheckedChanged += validate;
            phrase.TextChanged += validate;
            confirm.Click += delegate { if (!confirm.Enabled) return; Confirmed = true; DialogResult = DialogResult.OK; Close(); };
            AcceptButton = confirm;
            CancelButton = cancel;
        }
    }
}
