using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>历史会话日志查看器：列出 logs/session-*.log，给出样本/超时统计并预览最后 200 行。全程只读。</summary>
    public sealed class SessionLogForm : Form {
        readonly string logsRoot;
        readonly ListBox list=new ListBox{Dock=DockStyle.Fill,IntegralHeight=false};
        readonly ListBox content=new ListBox{Dock=DockStyle.Fill,IntegralHeight=false,HorizontalScrollbar=true,Font=new Font("Consolas",9)};
        readonly Label summary=new Label{AutoSize=true,ForeColor=UiTheme.Muted,Padding=new Padding(8,10,3,0),Text="尚未读取"};
        public SessionLogForm(string appRoot) {
            logsRoot=Path.Combine(appRoot,"logs");
            Text="历史会话日志（只读）";StartPosition=FormStartPosition.CenterParent;ClientSize=new Size(980,620);
            MinimumSize=new Size(720,420);Font=new Font("Microsoft YaHei UI",9);BackColor=UiTheme.Canvas;
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=40,WrapContents=false,Padding=new Padding(4,3,4,0),BackColor=Color.White};
            var refresh=new StyledActionButton{Text="刷新日志列表",Width=124,Margin=Padding.Empty,MinimumSize=new Size(40,30)};
            bar.Controls.Add(refresh);bar.Controls.Add(summary);
            var host=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=new Padding(4)};
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,320));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            host.Controls.Add(list,0,0);host.Controls.Add(content,1,0);
            Controls.Add(host);Controls.Add(bar);
            refresh.Click+=(s,e)=>LoadFiles();
            list.SelectedIndexChanged+=(s,e)=>ShowSelected();
            LoadFiles();
        }
        sealed class Entry {
            public readonly FileInfo File;
            public Entry(FileInfo file) { File=file; }
            public override string ToString() { return File.Name+" · "+Math.Max(1,File.Length/1024)+" KB · "+File.LastWriteTime.ToString("MM-dd HH:mm:ss"); }
        }
        void LoadFiles() {
            list.BeginUpdate();
            try {
                list.Items.Clear();
                if(Directory.Exists(logsRoot))foreach(FileInfo file in new DirectoryInfo(logsRoot).GetFiles("session-*.log").OrderByDescending(x=>x.LastWriteTimeUtc).Take(200))list.Items.Add(new Entry(file));
            } catch(Exception ex) {
                summary.Text="读取日志目录失败："+ex.Message;
                list.EndUpdate();
                return;
            }
            list.EndUpdate();
            summary.Text="共 "+list.Items.Count+" 个会话日志"+(list.Items.Count==0?"（本运行目录还没有 logs/session-*.log）":"");
        }
        void ShowSelected() {
            Entry entry=list.SelectedItem as Entry;
            if(entry==null)return;
            content.Items.Clear();
            if(entry.File.Length>8L*1024*1024) { content.Items.Add("日志过大（"+(entry.File.Length/1024/1024)+" MB），请在文件管理器里打开查看。"); return; }
            try {
                string[] lines=File.ReadAllLines(entry.File.FullName);
                int samples=lines.Count(x=>x.Contains("SAMPLE "));
                int timeouts=lines.Count(x=>x.Contains("quality=timeout"));
                int errors=lines.Count(x=>x.Contains("quality=error")||x.Contains("失败"));
                string[] tail=lines.Skip(Math.Max(0,lines.Length-200)).ToArray();
                content.Items.Add("文件："+entry.File.Name+" · "+entry.File.Length+" 字节 · 样本 "+samples+" 行 · 超时 "+timeouts+" · 异常/失败 "+errors+"（下方为最后 "+tail.Length+" 行）");
                content.Items.AddRange(tail);
                summary.Text=entry.File.Name+" · 样本 "+samples+" · 超时 "+timeouts;
            } catch(Exception ex) {
                content.Items.Add("读取日志失败："+ex.Message);
            }
        }
    }
}
