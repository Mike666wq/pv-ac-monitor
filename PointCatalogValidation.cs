using System;
using System.Collections.Generic;
using System.Linq;

namespace ExperimentMonitor {
    /// <summary>
    /// 启动期的点表校验。每条都对应一种"用户会直接看到"的故障形态，
    /// 不是泛泛的"配置合法性检查"：
    ///
    ///  1. 未登记的点       → 它会以 RAW/无单位的身份流进趋势、历史和导出
    ///  2. 绑定地址重复     → 两个点抢同一个寄存器，其中一个的数值永远是错的
    ///  3. 宽度与寄存器数不符 → 解码必然出错（float32 读 1 个寄存器）
    ///  4. 测量点没有单位   → 卡片上只有一个裸数字，没人知道它是什么
    ///  5. 倍率为 0         → 工程值恒为 0，而且看起来"有数"
    ///  6. 分组不在白名单   → 点会掉进"其他测点"，排序和导出筛选一起失效
    ///
    /// 校验失败不一定阻止启动：现场一次漏改就让整机起不来是不可接受的。
    /// 这里只负责把问题说清楚，由调用方决定是拒绝还是带横幅继续。
    /// </summary>
    public static class PointCatalogValidation {
        /// <summary>启动时压成一行的问题摘要，界面用来显示横幅；空串表示点表健康。</summary>
        public static string StartupNotice="";
        static readonly string[] KnownGroups = { "温度", "实验电表", "市电", "太阳能", "控制/设定", "运行状态", "诊断" };

        public sealed class Issue {
            public string Code="",Message="";
            /// <summary>严重到必须阻止启动（客户端会因此显示错误数据，而不是少显示）。</summary>
            public bool Blocking;
            public override string ToString(){return Code+"："+Message;}
        }

        /// <summary>返回全部问题；空列表表示点表健康。</summary>
        public static List<Issue> Check(List<Point> points) {
            var issues=new List<Issue>();
            if(points==null||points.Count==0) {
                issues.Add(new Issue{Code="no-points",Message="点表为空：protocol.json 里没有任何设备绑定点",Blocking=true});
                return issues;
            }

            // 1. 未登记的点
            foreach(string name in PointCatalog.Unregistered(points))
                issues.Add(new Issue{Code="unregistered",Message="点表没有登记 "+name+"：它会以 RAW/无单位的形式进入趋势、历史与导出",Blocking=false});

            // 2. 绑定地址重复
            var seen=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var p in points.Where(p=>p!=null&&p.binding!=null)) {
                string key=p.binding.slave+"/"+p.binding.address_zero_based+"/"+p.binding.register_count;
                string other;
                if(seen.TryGetValue(key,out other))
                    issues.Add(new Issue{Code="duplicate-binding",Message="站号 "+p.binding.slave+" 地址 "+p.binding.address_zero_based+" 被 "+other+" 与 "+p.name+" 同时绑定",Blocking=true});
                else seen[key]=p.name;
            }

            foreach(var p in points.Where(p=>p!=null&&p.binding!=null)) {
                var meta=PointCatalog.Get(p);
                if(meta.Unknown) continue; // 已在上面单独报告

                // 3. 宽度与寄存器数
                int expected=expectedRegisters(meta.Mode);
                if(expected>0&&p.binding.register_count!=expected)
                    issues.Add(new Issue{Code="width-mismatch",Message=p.name+" 的模式是 "+meta.Mode+"（需要 "+expected+" 个寄存器），但绑定只有 "+p.binding.register_count+" 个",Blocking=true});

                // 4. 测量点没有单位（控制点与 RAW 位串除外）
                bool measurement=meta.Group=="温度"||meta.Group=="实验电表"||meta.Group=="市电"||meta.Group=="太阳能";
                if(measurement&&String.IsNullOrWhiteSpace(meta.Unit)&&meta.Mode!="RAW"&&!meta.Label.Contains("功率因数")&&!meta.Label.Contains("保留"))
                    issues.Add(new Issue{Code="missing-unit",Message=p.name+"（"+meta.Label+"）是测量点但没有单位",Blocking=false});

                // 5. 倍率为 0
                if(meta.Scale==0)
                    issues.Add(new Issue{Code="zero-scale",Message=p.name+" 的倍率是 0：工程值会恒为 0，而且界面上看起来还有数",Blocking=true});

                // 6. 分组白名单
                if(Array.IndexOf(KnownGroups,meta.Group)<0)
                    issues.Add(new Issue{Code="unknown-group",Message=p.name+" 的分组是「"+meta.Group+"」，不在已知分组里：它会掉进「其他测点」分组，排序与导出筛选一起失效",Blocking=false});
            }
            return issues;
        }

        static int expectedRegisters(string mode) {
            if(String.IsNullOrEmpty(mode)) return 0;
            if(mode.StartsWith("FLOAT",StringComparison.Ordinal)) return 2;
            if(mode=="RAW") return 0;              // 位串：宽度由绑定决定
            if(mode.StartsWith("INT16",StringComparison.Ordinal)||mode.StartsWith("UINT16",StringComparison.Ordinal)) return 1;
            return 0;
        }

        /// <summary>把问题列表压成一行启动横幅文案；没有需要提示的返回空串。</summary>
        public static string Summary(List<Issue> issues) {
            if(issues==null||issues.Count==0) return "";
            int blocking=issues.Count(i=>i.Blocking);
            string head=blocking>0
                ? "点表有 "+blocking+" 项会直接产生错误数据"
                : "点表有 "+issues.Count+" 项需要核对";
            return head+"："+String.Join("；",issues.Take(3).Select(i=>i.Message).ToArray())+(issues.Count>3?"…（共 "+issues.Count+" 项，见启动日志）":"");
        }
    }
}
