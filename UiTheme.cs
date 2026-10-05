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
    // 与 BMS 的 StyledActionButton 同源:关键是 OnPaintBackground 显式清背景,
    // 缺了它圆角外部会残留上一帧内容(黑角/重影)。
    internal sealed class StyledActionButton:Button {
        bool hovered,pressed;string iconGlyph="";
        readonly Font ownedFont;
        public string IconGlyph { get { return iconGlyph; } set { iconGlyph=value??"";Invalidate(); } }
        public StyledActionButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;BackColor=Color.White;ForeColor=UiTheme.Ink;ownedFont=new Font("Microsoft YaHei UI",9,FontStyle.Bold);Font=ownedFont;Height=36;MinimumSize=new Size(72,36);AutoSize=false;Padding=new Padding(12,0,8,0);Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hovered=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)pressed=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnEnabledChanged(EventArgs e){hovered=false;pressed=false;Invalidate();base.OnEnabledChanged(e);}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)ownedFont.Dispose();}
        protected override void OnPaintBackground(PaintEventArgs e){Control parent=Parent;while(parent!=null&&parent.BackColor.A==0)parent=parent.Parent;e.Graphics.Clear(parent==null?SystemColors.Control:parent.BackColor);}
        protected override void OnPaint(PaintEventArgs e){
            OnPaintBackground(e);
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            float s=Math.Max(1f,DeviceDpi/96f),radius=8f*s;
            RectangleF body=new RectangleF(s,s,Math.Max(0,Width-3f*s),Math.Max(0,Height-4f*s));
            if(body.Width<2||body.Height<2)return;
            bool primary=BackColor.R<110&&BackColor.G<170&&BackColor.B<245;
            Color face=!Enabled?Color.FromArgb(235,239,245):pressed?(primary?ControlPaint.Dark(BackColor,.08f):Color.FromArgb(241,245,250)):hovered?(primary?ControlPaint.Light(BackColor,.06f):Color.FromArgb(247,249,252)):BackColor;
            Color textColor=Enabled?(primary?Color.White:ForeColor):Color.FromArgb(139,149,163);
            using(GraphicsPath shape=Round(body,radius))using(SolidBrush brush=new SolidBrush(face))using(Pen border=new Pen(Color.FromArgb(204,216,230),1f*s)){g.FillPath(brush,shape);if(!primary)g.DrawPath(border,shape);}
            int blockWidth=String.IsNullOrEmpty(iconGlyph)?0:Math.Min((int)(28*s),Math.Max(0,Width/3));
            if(blockWidth>0) {
                RectangleF tile=new RectangleF(Width-blockWidth-2f*s,2f*s,blockWidth,Math.Max(0,Height-6f*s));
                using(GraphicsPath tilePath=Round(tile,radius*.65f))using(SolidBrush tileBrush=new SolidBrush(primary?(Enabled?Color.FromArgb(34,255,255,255):Color.FromArgb(18,80,90,110)):Color.FromArgb(239,244,250)))g.FillPath(tileBrush,tilePath);
                using(StringFormat sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter})
                using(SolidBrush glyphBrush=new SolidBrush(textColor))using(Font glyphFont=new Font("Segoe UI Symbol",11f,FontStyle.Bold,GraphicsUnit.Point))
                    g.DrawString(iconGlyph,glyphFont,glyphBrush,tile,sf);
            }
            int textLeft=Padding.Left,textWidth=Math.Max(0,Width-blockWidth-Padding.Left-Padding.Right);
            TextRenderer.DrawText(g,Text,Font,new Rectangle(textLeft,0,textWidth,Height),textColor,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
        }
        internal static GraphicsPath Round(RectangleF r,float rad){GraphicsPath p=new GraphicsPath();float d=Math.Min(rad*2,Math.Min(r.Width,r.Height));if(d<2){p.AddRectangle(r);return p;}p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    }
    internal sealed class RoundedInputHost:Panel {
        readonly Control editor;public Control Editor{get{return editor;}}public string ErrorText{get;set;}
        public RoundedInputHost(Control input){if(input==null)throw new ArgumentNullException("input");editor=input;BackColor=UiTheme.Surface;Height=36;MinimumSize=new Size(48,32);Padding=new Padding(8,2,8,2);Margin=new Padding(4,2,4,2);BorderStyle=BorderStyle.None;if(input is TextBox)((TextBox)input).BorderStyle=BorderStyle.None;Controls.Add(input);input.Dock=DockStyle.Fill;input.Font=new Font("Microsoft YaHei UI",9);}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);float s=Math.Max(1f,DeviceDpi/96f);using(Pen pen=new Pen(!String.IsNullOrEmpty(ErrorText)?Color.Firebrick:UiTheme.Line,s))e.Graphics.DrawRectangle(pen,s,s,Width-2*s,Height-2*s);}
    }
}
