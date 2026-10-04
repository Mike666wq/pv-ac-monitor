using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExperimentMonitor {
    internal static class UiTheme {
        internal static readonly Color Canvas=Color.FromArgb(243,247,251),Surface=Color.White,Ink=Color.FromArgb(31,52,75),Muted=Color.FromArgb(100,119,141),Blue=Color.FromArgb(54,112,220),Line=Color.FromArgb(218,227,237),Green=Color.FromArgb(0,142,105),Warning=Color.FromArgb(191,116,18),Danger=Color.FromArgb(190,52,60);
        internal static void Apply(Control root) {
            if(root==null)return;root.BackColor=root is Form||root is TabPage||root is TabControl?Canvas:root.BackColor;
            foreach(Control c in root.Controls) {
                c.ForeColor=Ink;
                DataGridView grid=c as DataGridView;
                if(grid!=null){grid.BackgroundColor=Surface;grid.GridColor=Line;grid.BorderStyle=BorderStyle.None;grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(235,241,248);grid.ColumnHeadersDefaultCellStyle.ForeColor=Ink;grid.ColumnHeadersDefaultCellStyle.SelectionBackColor=grid.ColumnHeadersDefaultCellStyle.BackColor;grid.DefaultCellStyle.BackColor=Surface;grid.DefaultCellStyle.ForeColor=Ink;grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(220,235,252);grid.DefaultCellStyle.SelectionForeColor=Ink;grid.RowTemplate.Height=30;}
                Button b=c as Button;if(b!=null&&!(b is StyledActionButton)){b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Line;b.BackColor=Surface;b.ForeColor=Ink;b.Cursor=Cursors.Hand;}
                if(c is GroupBox)c.BackColor=Surface;
                if(c is TextBox)c.BackColor=Surface;
                Apply(c);
            }
        }
    }
    internal sealed class StyledActionButton:Button {
        bool hovered,pressed;public string IconGlyph {get;set;}
        public StyledActionButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;BackColor=Color.White;ForeColor=UiTheme.Ink;Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold);Height=36;MinimumSize=new Size(72,36);Padding=new Padding(10,0,10,0);Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}protected override void OnMouseLeave(EventArgs e){hovered=pressed=false;Invalidate();base.OnMouseLeave(e);}protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnPaint(PaintEventArgs e){Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=Math.Max(1f,DeviceDpi/96f);RectangleF r=new RectangleF(s,s,Width-3*s,Height-4*s);bool primary=BackColor.R<110&&BackColor.G<170&&BackColor.B<245;Color face=!Enabled?Color.FromArgb(235,239,245):pressed?ControlPaint.Dark(BackColor,.06f):hovered?ControlPaint.Light(BackColor,.06f):BackColor;using(GraphicsPath p=Round(r,7*s))using(SolidBrush b=new SolidBrush(face))using(Pen pen=new Pen(UiTheme.Line,s)){g.FillPath(b,p);if(!primary)g.DrawPath(pen,p);}TextRenderer.DrawText(g,(String.IsNullOrEmpty(IconGlyph)?"":IconGlyph+"  ")+(Text??""),Font,Rectangle.Round(new RectangleF(8*s,0,Width-16*s,Height)),Enabled?(primary?Color.White:ForeColor):Color.Gray,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);}
        static GraphicsPath Round(RectangleF r,float rad){GraphicsPath p=new GraphicsPath();float d=Math.Min(rad*2,Math.Min(r.Width,r.Height));if(d<2){p.AddRectangle(r);return p;}p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    }
    internal sealed class RoundedInputHost:Panel {
        readonly Control editor;public Control Editor{get{return editor;}}public string ErrorText{get;set;}
        public RoundedInputHost(Control input){if(input==null)throw new ArgumentNullException("input");editor=input;BackColor=UiTheme.Surface;Height=36;MinimumSize=new Size(48,32);Padding=new Padding(8,2,8,2);Margin=new Padding(4,2,4,2);BorderStyle=BorderStyle.None;if(input is TextBox)((TextBox)input).BorderStyle=BorderStyle.None;Controls.Add(input);input.Dock=DockStyle.Fill;input.Font=new Font("Microsoft YaHei UI",9);}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);float s=Math.Max(1f,DeviceDpi/96f);using(Pen pen=new Pen(!String.IsNullOrEmpty(ErrorText)?Color.Firebrick:UiTheme.Line,s))e.Graphics.DrawRectangle(pen,s,s,Width-2*s,Height-2*s);}
    }
}
