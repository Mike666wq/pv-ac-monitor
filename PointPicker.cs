using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExperimentMonitor {
    /// <summary>
    /// 测点选择器:设备为父节点、测点为子节点;支持按测点名/名称/设备名搜索,以及作用于当前可见项的全选/清空。
    /// 记录页与趋势分析页共用这一个实现,避免两处各写一套设备/测点联动逻辑。
    /// 语义:这里勾选的是"参与取数的测点"(查询、统计、导出都以它为准)。
    /// </summary>
    public sealed class PointPicker : UserControl {
        sealed class Node {
            public string Device;      // 设备节点
            public string Point;       // 测点节点
            public TreeNode Tree;
            public readonly List<Node> Children=new List<Node>();
        }
        readonly List<Point> points;
        readonly HashSet<string> known=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> selected=new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string,Node> pointNodes=new Dictionary<string,Node>(StringComparer.Ordinal);
        readonly List<string> listed=new List<string>();
        readonly TreeView tree=new TreeView{CheckBoxes=true,FullRowSelect=true,ShowLines=true,ShowRootLines=true,HideSelection=false,Indent=16,Scrollable=true,Dock=DockStyle.Fill};
        readonly TextBox search=new TextBox{Dock=DockStyle.Fill};
        readonly StyledActionButton selectAll=new StyledActionButton{Text="全选",Width=74,Height=30,MinimumSize=new Size(48,26),Margin=Padding.Empty};
        readonly StyledActionButton selectNone=new StyledActionButton{Text="清空",Width=74,Height=30,MinimumSize=new Size(48,26),Margin=Padding.Empty};
        readonly Label summary=new Label{AutoSize=false,AutoEllipsis=true,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=UiTheme.Muted,Margin=Padding.Empty};
        bool internalUpdate;
        /// <summary>用户操作导致勾选变化时触发;程序化设置( SetSelection )不触发。</summary>
        public event Action SelectionChanged;
        /// <summary>已勾选的测点名(协议顺序)。</summary>
        public string[] SelectedPoints{get{return points.Where(p=>selected.Contains(p.name)).Select(p=>p.name).ToArray();}}
        /// <summary>当前列表里可见的测点名(受搜索影响),全选/清空只作用于这些。</summary>
        public string[] ListedPoints{get{return listed.ToArray();}}
        public bool HasSelection{get{return selected.Count>0;}}

        public PointPicker(List<Point> points){
            this.points=points??new List<Point>();
            foreach(Point p in this.points)known.Add(p.name);
            Dock=DockStyle.Fill;BackColor=Color.White;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=Padding.Empty,Margin=Padding.Empty};
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.Controls.Add(summary,0,0);
            layout.Controls.Add(new RoundedInputHost(search){Dock=DockStyle.Fill},0,1);
            var bar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,Padding=Padding.Empty,BackColor=Color.White};
            bar.Controls.Add(selectAll);bar.Controls.Add(selectNone);
            layout.Controls.Add(bar,0,2);
            layout.Controls.Add(tree,0,3);
            Controls.Add(layout);
            tree.AfterCheck+=(s,e)=>OnAfterCheck(e.Node);
            tree.NodeMouseDoubleClick+=(s,e)=>{Node node=e.Node.Tag as Node;if(node==null)return;e.Node.Checked=!e.Node.Checked;};
            search.TextChanged+=(s,e)=>Rebuild();
            selectAll.Click+=(s,e)=>SetAllVisible(true);
            selectNone.Click+=(s,e)=>SetAllVisible(false);
            Rebuild();
        }

        bool Match(Point p,string q){
            if(q.Length==0)return true;
            PointInfo info=PointCatalog.Get(p.name);
            return p.name.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0
                ||(info!=null&&info.Label!=null&&info.Label.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)
                ||(p.binding.device!=null&&p.binding.device.IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0);
        }

        /// <summary>按当前搜索重建树;勾选状态以 selected 为准,不因搜索而丢失。</summary>
        void Rebuild(){
            internalUpdate=true;
            tree.BeginUpdate();
            try{
                tree.Nodes.Clear();pointNodes.Clear();listed.Clear();
                string q=(search.Text??"").Trim();
                foreach(string device in points.Select(p=>p.binding.device).Distinct()){
                    List<Point> group=points.Where(p=>p.binding.device==device).ToList();
                    if(q.Length>0)group=group.Where(p=>Match(p,q)).ToList();
                    if(group.Count==0)continue;
                    Node deviceNode=new Node{Device=device};
                    TreeNode deviceTree=new TreeNode(device){Tag=deviceNode};
                    deviceNode.Tree=deviceTree;
                    foreach(Point p in group){
                        Node pointNode=new Node{Point=p.name};
                        PointInfo info=PointCatalog.Get(p.name);
                        string label=info==null||info.Label==null?"":info.Label;
                        pointNode.Tree=new TreeNode(p.name+(label.Length>0?" · "+label:"")){Tag=pointNode};
                        deviceNode.Children.Add(pointNode);
                        pointNodes[p.name]=pointNode;
                        listed.Add(p.name);
                        deviceTree.Nodes.Add(pointNode.Tree);
                    }
                    tree.Nodes.Add(deviceTree);
                }
                tree.ExpandAll();
            }finally{tree.EndUpdate();internalUpdate=false;}
            RefreshChecks();
            UpdateSummary();
        }

        /// <summary>把 selected 同步回树上的勾选状态;设备节点显示 已选/总数,部分选中时保持勾选但文字标明。</summary>
        void RefreshChecks(){
            internalUpdate=true;
            tree.BeginUpdate();
            try{
                foreach(Node node in pointNodes.Values)node.Tree.Checked=selected.Contains(node.Point);
                foreach(TreeNode deviceTree in tree.Nodes){
                    Node deviceNode=deviceTree.Tag as Node;if(deviceNode==null)continue;
                    int total=deviceNode.Children.Count;
                    int on=deviceNode.Children.Count(c=>selected.Contains(c.Point));
                    deviceTree.Checked=on>0;
                    deviceTree.Text=deviceNode.Device+"（"+on+"/"+total+"）";
                }
            }finally{tree.EndUpdate();internalUpdate=false;}
        }

        void OnAfterCheck(TreeNode treeNode){
            if(internalUpdate)return;
            Node node=treeNode.Tag as Node;if(node==null)return;
            bool value=treeNode.Checked;
            if(node.Device!=null){
                foreach(Node child in node.Children){if(value)selected.Add(child.Point);else selected.Remove(child.Point);}
            }else if(node.Point!=null){
                if(value)selected.Add(node.Point);else selected.Remove(node.Point);
            }
            RefreshChecks();UpdateSummary();RaiseChanged();
        }

        void UpdateSummary(){
            summary.Text="已选 "+selected.Count+" / "+points.Count+" 个测点";
        }

        void RaiseChanged(){Action handler=SelectionChanged;if(handler!=null)handler();}

        /// <summary>程序化设置勾选集合(跨页交接、单点定位);不触发 SelectionChanged。</summary>
        public void SetSelection(IEnumerable<string> names){
            selected.Clear();
            if(names!=null)foreach(string name in names)if(known.Contains(name))selected.Add(name);
            RefreshChecks();UpdateSummary();
        }

        /// <summary>对当前可见(受搜索影响)的测点全选或清空。</summary>
        public void SetAllVisible(bool value){
            foreach(string name in listed){if(value)selected.Add(name);else selected.Remove(name);}
            RefreshChecks();UpdateSummary();RaiseChanged();
        }
    }
}


