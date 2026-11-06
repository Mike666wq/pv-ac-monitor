using System;
using System.Collections.Generic;
using System.Linq;

namespace ExperimentMonitor {
    /// <summary>一次 03 块读：站号 + 起始地址 + 寄存器数，以及它覆盖了哪些点。</summary>
    public sealed class ReadBlock {
        public int Slave, Address, Count;
        /// <summary>本块覆盖的测点名（按地址顺序）。</summary>
        public readonly List<string> Points = new List<string>();
        /// <summary>本块覆盖的寄存器数（各点 register_count 之和）。</summary>
        public int CoveredRegisters;
        /// <summary>空洞寄存器数 = 块长度 − 覆盖数。用于核对"零空洞"。</summary>
        public int Holes { get { return Count - CoveredRegisters; } }
        public int End { get { return Address + Count; } }
        public override string ToString() {
            return "slave " + Slave + " [" + Address + "," + Count + "] " + Points.Count + " 点";
        }
    }

    /// <summary>
    /// 块读规划：把按站号分组的点合并成尽量少的连续寄存器块。
    ///
    /// 三条硬规则（都是有依据的，不是优化偏好）：
    ///  1. **绝不填空洞**。两个点之间只要有一个寄存器没绑定，就分成两块。
    ///     读一个未绑定地址在 FX3U/EX3U 上不会报错（手册 p.97/p.67：0x0000–0x1F3F
    ///     对应 D0–D7999 连续无空洞；异常码 02H 只在越界时出现），但那个值没有
    ///     任何意义，把两个真实点放进同一个块只会让"值挂错行"的风险变大。
    ///  2. **不跨站号合并**。不同从站不可能在同一帧里回应。
    ///  3. **每块不超过 125 个寄存器**（FC03 的协议上限）。
    ///
    /// 切片只能按 (站号, 地址) 映射算偏移，**严禁用点名列表的下标** ——
    /// 点位在点表里的书写顺序不是地址顺序，按下标切片会整体错位而且不会报错。
    /// </summary>
    public static class BlockPlanner {
        /// <summary>FC03 单次可读的最大寄存器数。</summary>
        public const int MaxBlockRegisters = 125;

        public static List<ReadBlock> Plan(IEnumerable<Point> points) {
            var blocks = new List<ReadBlock>();
            if (points == null) return blocks;
            var bySlave = points
                .Where(p => p != null && p.binding != null && p.binding.register_count > 0 && p.binding.address_zero_based >= 0)
                .GroupBy(p => p.binding.slave)
                .OrderBy(g => g.Key);

            foreach (var group in bySlave) {
                ReadBlock current = null;
                foreach (var point in group.OrderBy(p => p.binding.address_zero_based).ThenBy(p => p.name, StringComparer.Ordinal)) {
                    int start = point.binding.address_zero_based;
                    int count = point.binding.register_count;
                    bool canExtend = current != null
                        && start >= current.Address
                        && start <= current.End                                  // 允许重叠（同地址多解），但没有空洞
                        && Math.Max(current.End, start + count) - current.Address <= MaxBlockRegisters;
                    if (!canExtend) {
                        current = new ReadBlock { Slave = group.Key, Address = start, Count = count };
                        blocks.Add(current);
                    } else {
                        current.Count = Math.Max(current.End, start + count) - current.Address;
                    }
                    current.Points.Add(point.name);
                    current.CoveredRegisters += count;
                }
            }
            return blocks;
        }

        /// <summary>
        /// 从块响应里取出某个点自己的字节。偏移按地址算，不按下标。
        /// 越界返回空数组（宁可空也不要错位）。
        /// </summary>
        public static byte[] Slice(ReadBlock block, Point point, byte[] payload) {
            if (block == null || point == null || point.binding == null || payload == null) return new byte[0];
            int offset = (point.binding.address_zero_based - block.Address) * 2;
            int length = point.binding.register_count * 2;
            if (offset < 0 || length <= 0 || offset + length > payload.Length) return new byte[0];
            var result = new byte[length];
            Array.Copy(payload, offset, result, 0, length);
            return result;
        }

        /// <summary>规划自检：返回问题清单，空表示这份计划可以直接用。</summary>
        public static List<string> Validate(List<ReadBlock> blocks, IEnumerable<Point> points) {
            var issues = new List<string>();
            if (blocks == null) { issues.Add("计划为空"); return issues; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocks) {
                if (block.Slave < 1 || block.Slave > 247) issues.Add("站号越界：" + block.Slave);
                if (block.Count < 1 || block.Count > MaxBlockRegisters) issues.Add("块长度越界：" + block);
                if (block.Address + block.Count > 65536) issues.Add("块越界：" + block);
                if (block.Holes != 0) issues.Add("块内有 " + block.Holes + " 个空洞寄存器：" + block);
                foreach (var name in block.Points) if (!seen.Add(name)) issues.Add("点被重复规划：" + name);
            }
            if (points != null) {
                foreach (var p in points) {
                    if (p == null || p.binding == null) continue;
                    if (!seen.Contains(p.name)) issues.Add("点没有被任何块覆盖：" + p.name);
                }
            }
            return issues;
        }

        /// <summary>规划的帧数（用于对比逐点读的次数）。</summary>
        public static int FrameCount(List<ReadBlock> blocks) { return blocks == null ? 0 : blocks.Count; }

        /// <summary>逐点读需要的帧数。</summary>
        public static int PerPointFrameCount(IEnumerable<Point> points) {
            return points == null ? 0 : points.Count(p => p != null && p.binding != null && p.binding.register_count > 0);
        }
    }
}
