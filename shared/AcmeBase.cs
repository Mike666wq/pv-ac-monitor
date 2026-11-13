// @generated 来自 _base/src/AcmeBase.cs —— 请勿手改。
// @source-sha256 74c48a7d3a482d799b045d87666908979e4e9ebdc49824618064fbd160f5da38
// @sync node _base/sync-base.mjs --write
// @end-header
// ============================================================
// Acme.Base — 三端共用基础设施（基座 L2）
// ------------------------------------------------------------
// 由 _基座/ 统一维护，BmsSerialDemo 与 ExperimentMonitorDemo 的构建脚本
// 直接编译本文件，不再各自持有副本。
// 改动本文件 = 同时改了三端，这是重点。
// ============================================================
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Acme.Base
{
    public static class CrashLogger
    {
        /// <summary>LocalApplicationData 下的产品子目录名（各端启动时设置）。</summary>
        public static string FallbackFolder="Acme";
        static readonly object sync = new object();
        public static void Write(string exitType, Exception error)
        {
            try {
                string body = "时间: " + DateTime.Now.ToString("o") + "\r\n退出类型: " + exitType + "\r\n版本: " + Assembly.GetExecutingAssembly().GetName().Version + "\r\n运行时: " + Environment.Version + "\r\n位数: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + "\r\n操作系统: " + Environment.OSVersion + "\r\n异常:\r\n" + ExceptionText(error) + "\r\n";
                string name = "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log";
                lock(sync) { try { string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, name), body, Encoding.UTF8); }
                    catch { string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FallbackFolder, "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, name), body, Encoding.UTF8); } }
            } catch { }
        }
        static string ExceptionText(Exception error)
        {
            if(error==null)return "<无异常对象>";
            try{return error.ToString();}catch{
                string type="<类型不可读>",message="<消息不可读>",stack="<调用栈不可读>";
                try{type=error.GetType().FullName;}catch{}try{message=error.Message;}catch{}try{stack=error.StackTrace;}catch{}
                return type+"\r\n"+message+"\r\n"+stack+"\r\n（完整异常格式化失败，已保留可读取的诊断字段）";
            }
        }
    }
    public static class AtomicFile {
        /// <summary>可替换的原子替换实现。生产默认即 File.Replace。</summary>
        public static Action<string, string, string> ReplaceFile = (source, destination, backup) => File.Replace(source, destination, backup);

        /// <summary>替换目标文件；目标不存在时直接改名。</summary>
        public static void Replace(string source, string destination) {
            if (File.Exists(destination)) ReplaceFile(source, destination, null);
            else File.Move(source, destination);
        }

        /// <summary>
        /// 受限环境使用的降级替换：先备份原文件，再删除，最后改名。
        /// 与 ReplaceFile 一样是"覆盖"语义，但它不是原子的 —— 只在测试环境使用。
        /// </summary>
        public static void ReplaceWithBackupFallback(string source, string destination, string backup) {
            try { File.Copy(destination, destination + ".bak", true); } catch { }
            File.Delete(destination);
            File.Move(source, destination);
        }

        /// <summary>
        /// 安装降级替换：先试原子替换，只有在环境拒绝 replace 语义时才退到备份后覆盖。
        /// 返回一个查询函数，用于如实记录"本次运行是否真的发生了降级"（环境限制 vs 代码缺陷）。
        /// </summary>
        public static Func<bool> InstallFallback() {
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
