using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>
    /// 圆角面板(对齐 BMS 卡片/分区外观):自身负责圆角填充与描边,
    /// 子控件保持不透明的 Surface 背景即可,无需透明传递。
    /// </summary>
    internal sealed class RoundedPanel : Panel {
        readonly float radius;
        internal RoundedPanel(float cornerRadius=7) {
            radius=cornerRadius;
            BackColor=UiTheme.Surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnPaintBackground(PaintEventArgs e) {
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            float s=Math.Max(1f,DeviceDpi/96f);
            Color behind=Parent!=null?Parent.BackColor:UiTheme.Canvas;
            using(var b=new SolidBrush(behind))g.FillRectangle(b,ClientRectangle);
            RectangleF r=new RectangleF(s/2f,s/2f,Width-1.5f*s,Height-1.5f*s);
            using(GraphicsPath path=Round(r,radius*s))using(var fill=new SolidBrush(UiTheme.Surface))g.FillPath(fill,path);
            using(GraphicsPath path=Round(r,radius*s))using(var pen=new Pen(UiTheme.Line,s))g.DrawPath(pen,path);
        }
        internal static GraphicsPath Round(RectangleF r,float rad) {
            GraphicsPath p=new GraphicsPath();float d=Math.Min(rad*2,Math.Min(r.Width,r.Height));
            if(d<2){p.AddRectangle(r);return p;}
            p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);
            p.CloseFigure();return p;
        }
    }

    /// <summary>卡片状态行:写入的文本前自动带状态圆点,圆点颜色跟随 ForeColor(语义色由各状态路径设置)。读取返回带圆点的完整文本。</summary>
    internal sealed class CardStatusLabel : Label {
        public override string Text { get { return base.Text; } set { base.Text=String.IsNullOrEmpty(value)?value:"● "+value; } }
    }

    /// <summary>实时监控 → 总览页:分组流式卡片网格 + 总览趋势图。</summary>
    internal static class OverviewPageBuilder {
        const int CardMinWidth=200,CardHeight=110;

        internal static void Build(TabPage page,List<Point> points,Dictionary<string,Label[]> cards,ToolTip tips,ExperimentChart overviewChart,Font baseFont) {
            var viewport=new Panel { Dock=DockStyle.Fill,AutoScroll=true,BackColor=UiTheme.Canvas };page.Controls.Add(viewport);
            var stack=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=0,Padding=new Padding(2) };viewport.Controls.Add(stack);
            string[] groupOrder={"温度","实验电表","市电","太阳能","运行状态","诊断"};
            foreach(var group in points.GroupBy(p=>PointCatalog.Get(p).Group).OrderBy(g=>Array.IndexOf(groupOrder,g.Key)<0?99:Array.IndexOf(groupOrder,g.Key))) {
                var section=new RoundedPanel { Dock=DockStyle.Top,Height=140,Padding=Padding.Empty,Margin=new Padding(2,2,2,8) };
                var header=new Panel { Dock=DockStyle.Top,Height=34,BackColor=UiTheme.Surface,Padding=new Padding(12,7,12,0) };
                var title=new Label { Text=group.Key+"  ·  "+group.Count(),AutoSize=true,ForeColor=UiTheme.Ink,Font=new Font(baseFont.FontFamily,10,FontStyle.Bold),BackColor=UiTheme.Surface };
                header.Controls.Add(title);section.Controls.Add(header);
                var flow=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=true,AutoScroll=false,Padding=new Padding(8,0,8,6),Margin=Padding.Empty,BackColor=UiTheme.Surface };
                section.Controls.Add(flow);
                // Bring the header to the top of the z-order so docking fills correctly.
                section.Controls.SetChildIndex(header,section.Controls.Count-1);
                stack.RowCount++;stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));stack.Controls.Add(section,0,stack.RowCount-1);
                foreach(Point p in group) {
                    PointInfo info=PointCatalog.Get(p);
                    var card=new RoundedPanel { Width=CardMinWidth,Height=CardHeight,Margin=new Padding(4),Padding=new Padding(10,8,10,8) };
                    var grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty,Padding=Padding.Empty,BackColor=UiTheme.Surface };
                    // 显式 Percent 列:缺省列样式在宽度收缩时可能让 Fill 子控件溢出容器。
                    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    grid.RowStyles.Add(new RowStyle(SizeType.Absolute,22));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,20));
                    bool unconfirmed=!info.ScaleConfirmed;
                    var label=new Label { Text=info.Label+"  "+p.name,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=UiTheme.Muted,Font=new Font(baseFont.FontFamily,8.5f),AutoEllipsis=true,BackColor=UiTheme.Surface,Margin=Padding.Empty,Padding=new Padding(0,0,unconfirmed?86:0,0) };
                    var value=new Label { Text="—",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft,ForeColor=UiTheme.Ink,Font=new Font(baseFont.FontFamily,info.Mode=="U16 / I16"?9.5f:17,FontStyle.Bold),AutoEllipsis=false,UseCompatibleTextRendering=true,BackColor=UiTheme.Surface };
                    string unitText=info.Unit??"";
                    unitText=unitText.Replace(" · 倍率待核准","").Replace("· 倍率待核准","").Trim();
                    // “原始值/原始码”是无单位占位,不占用单位列,让大数值(含 E 记数法)用满整行。
                    if(unitText=="原始值"||unitText=="原始码")unitText="";
                    var unit=new Label { Text=unitText,AutoSize=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft,ForeColor=UiTheme.Muted,Font=new Font(baseFont.FontFamily,8.5f),BackColor=UiTheme.Surface,Margin=Padding.Empty,Padding=Padding.Empty };
                    var status=new CardStatusLabel { Text="待机",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=UiTheme.Muted,Font=new Font(baseFont.FontFamily,8),AutoEllipsis=true,BackColor=UiTheme.Surface,Margin=Padding.Empty };
                    // 值与单位同行:单位文本建卡后不再变化,按其实测宽度设 Absolute 列,
                    // 数值列 Percent 填充(AutoEllipsis 兜底),任何 DPI 下都不会溢出。
                    value.AutoSize=false;value.AutoEllipsis=true;value.Margin=Padding.Empty;
                    int unitWidth=String.IsNullOrEmpty(unitText)?0:TextRenderer.MeasureText(unitText,unit.Font).Width+8;
                    var valueRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,Padding=Padding.Empty,BackColor=UiTheme.Surface };
                    valueRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    if(unitWidth>0){valueRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,unitWidth));valueRow.Controls.Add(unit,1,0);}
                    valueRow.Controls.Add(value,0,0);
                    grid.Controls.Add(label,0,0);grid.Controls.Add(valueRow,0,1);grid.Controls.Add(status,0,2);card.Controls.Add(grid);
                    if(unconfirmed) {
                        var badge=new Label { Text="倍率待核准",AutoSize=true,ForeColor=UiTheme.Ink,BackColor=Color.FromArgb(245,197,66),Font=new Font(baseFont.FontFamily,7.5f),Padding=new Padding(3,1,3,1) };
                        // AutoSize 的最终宽度在创建时还未确定,位置必须在布局事件里按实际宽度计算。
                        card.Layout+=(s,e)=>{ badge.SetBounds(card.Width-badge.Width-card.Padding.Right-2,6,0,0,BoundsSpecified.Location); };
                        card.Controls.Add(badge);badge.BringToFront();
                    }
                    flow.Controls.Add(card);cards[p.name]=new[]{label,value,unit,status};
                    tips.SetToolTip(card,p.binding.device+" · 站号 "+p.binding.slave+" · 地址 "+p.binding.address_zero_based+" · "+info.Label+(info.ScaleConfirmed?"":" · 倍率待核准")+(info.Mode=="U16 / I16"?" · U16/I16 原始值，不作状态映射":""));
                }
                bool resizePending=false,resizing=false;
                Action resizeGroup=null;
                resizeGroup=delegate {
                    if(resizePending||viewport.IsDisposed||!viewport.IsHandleCreated)return;
                    resizePending=true;
                    viewport.BeginInvoke((MethodInvoker)delegate {
                        resizePending=false;
                        if(resizing||viewport.IsDisposed||flow.IsDisposed||section.IsDisposed)return;
                        resizing=true;
                        try {
                            int innerWidth=flow.ClientSize.Width-flow.Padding.Horizontal;
                            if(innerWidth<=0)return;
                            int marginWidth=flow.Controls.Count==0?0:flow.Controls[0].Margin.Horizontal;
                            // 以首张卡当前宽度为基准:DPI 缩放后卡片已被放大,字体也同比放大,二者保持一致。
                            int naturalWidth=flow.Controls.Count==0?CardMinWidth:flow.Controls[0].Width;
                            int columns=Math.Max(1,(innerWidth+marginWidth)/Math.Max(1,naturalWidth+marginWidth));
                            int cardWidth=Math.Max(1,(innerWidth-columns*marginWidth)/columns);
                            foreach(Control card in flow.Controls)if(card.Width!=cardWidth)card.Width=cardWidth;
                            flow.PerformLayout();

                            // Use the layout panel's actual wrapped child positions. A row
                            // count inferred from viewport width misses scaled margins,
                            // section padding, and the width left after a vertical scrollbar.
                            int laidOutBottom=flow.Padding.Top;
                            foreach(Control card in flow.Controls)
                                laidOutBottom=Math.Max(laidOutBottom,card.Bottom+card.Margin.Bottom);
                            Size preferred=flow.GetPreferredSize(new Size(Math.Max(1,flow.ClientSize.Width),0));
                            int contentHeight=Math.Max(laidOutBottom,preferred.Height)+flow.Padding.Bottom;
                            int requiredHeight=flow.Top+contentHeight+section.Padding.Bottom+3;
                            if(section.Height!=requiredHeight)section.Height=requiredHeight;
                        } finally {resizing=false;}
                    });
                };
                viewport.ClientSizeChanged+=(s,e)=>resizeGroup();
                viewport.HandleCreated+=(s,e)=>resizeGroup();
                page.SizeChanged+=(s,e)=>resizeGroup();
                if(viewport.IsHandleCreated)resizeGroup();
            }
            overviewChart.SelectPoints(points.Where(p=>p.name.StartsWith("T",StringComparison.Ordinal)||p.name=="D2206").Select(p=>p.name));
            overviewChart.Dock=DockStyle.Fill;overviewChart.MinimumSize=new Size(280,220);overviewChart.Height=Math.Max(230,overviewChart.RequiredHeight);
            stack.RowCount++;stack.RowStyles.Add(new RowStyle(SizeType.Absolute,Math.Max(230,overviewChart.RequiredHeight)));stack.Controls.Add(overviewChart,0,stack.RowCount-1);
            viewport.SizeChanged+=(s,e)=>{int width=Math.Max(320,viewport.ClientSize.Width-8);stack.Width=width;overviewChart.Width=width-8;};
        }
    }
}
