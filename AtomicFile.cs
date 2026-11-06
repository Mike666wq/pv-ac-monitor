using System;
using System.IO;

namespace ExperimentMonitor {
    /// <summary>
    /// 原子文件替换。默认使用 File.Replace：它是原子的，不存在"目标文件短暂不存在"的窗口。
    ///
    /// 为什么要留注入点：某些受限环境（沙箱、部分企业安全软件）禁止 replace 语义，
    /// 此时进程会直接抛出 UnauthorizedAccessException。生产默认不改变 —— 降级路径会引入一个
    /// "目标文件短暂不存在 ⇒ 崩溃在该窗口 ⇒ 分期状态丢失 ⇒ 数据写到另一个库文件 ⇒
    /// 跨库历史不一致"的窗口。只有测试环境才会注入降级实现。
    /// </summary>
    internal static class AtomicFile {
        /// <summary>可替换的原子替换实现。生产默认即 File.Replace。</summary>
        internal static Action<string, string, string> ReplaceFile = (source, destination, backup) => File.Replace(source, destination, backup);

        /// <summary>替换目标文件；目标不存在时直接改名。</summary>
        internal static void Replace(string source, string destination) {
            if (File.Exists(destination)) ReplaceFile(source, destination, null);
            else File.Move(source, destination);
        }

        /// <summary>
        /// 受限环境使用的降级替换：先备份原文件，再删除，最后改名。
        /// 与 ReplaceFile 一样是"覆盖"语义，但它不是原子的 —— 只在测试环境使用。
        /// </summary>
        internal static void ReplaceWithBackupFallback(string source, string destination, string backup) {
            try { File.Copy(destination, destination + ".bak", true); } catch { }
            File.Delete(destination);
            File.Move(source, destination);
        }

        /// <summary>
        /// 安装降级替换：先试原子替换，只有在环境拒绝 replace 语义时才退到备份后覆盖。
        /// 返回一个查询函数，用于如实记录"本次运行是否真的发生了降级"（环境限制 vs 代码缺陷）。
        /// </summary>
        internal static Func<bool> InstallFallback() {
            bool degraded = false;
            ReplaceFile = (source, destination, backup) => {
                try { File.Replace(source, destination, backup); }
                catch (UnauthorizedAccessException) { degraded = true; ReplaceWithBackupFallback(source, destination, backup); }
                catch (PlatformNotSupportedException) { degraded = true; ReplaceWithBackupFallback(source, destination, backup); }
                catch (IOException) { degraded = true; ReplaceWithBackupFallback(source, destination, backup); }
            };
            return () => degraded;
        }
    }
}
