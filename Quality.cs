using System;
using System.Globalization;

namespace ExperimentMonitor {
    /// <summary>
    /// 观测质量的单一真相源。
    ///
    /// 过去质量的判定散落在十来个地方（总览、历史、趋势、统计、导出、云端、控制回读），
    /// 每处各写一份字符串比较。改一处忘一处就会出现"界面上显示正常、统计里算超时"、
    /// "导出把未读取的点写成 0"这类互相矛盾。现在取值、语义与呈现方式收在这一处。
    ///
    /// 语义表：
    ///   good              读到有效数据。唯一可参与统计、告警、阈值判定、导出生效数的状态。
    ///   timeout           本轮超时无响应。
    ///   protocol_exception 设备回 MODBUS 异常码（0x83）。
    ///   decode_error      报文收到了但解码不成立（非有限浮点、字节序存疑等）。
    ///   error             其它通信失败（CRC 错、端口故障、总线忙等）。
    ///   ambiguous         响应早于物理下限，归属不可信（疑似上一笔事务的迟到帧）。
    ///   skipped           本轮没有读取这个点（站号在隔离期，或同站前序点失败后本轮跳过）。
    ///                     它不是一次新的失败：失败已经在真正失败的那个点上计过了。
    /// </summary>
    internal static class Quality {
        public const string Good = "good";
        public const string Timeout = "timeout";
        public const string ProtocolException = "protocol_exception";
        public const string DecodeError = "decode_error";
        public const string Error = "error";
        public const string Ambiguous = "ambiguous";
        public const string Skipped = "skipped";

        /// <summary>是否读到了有效数据。</summary>
        public static bool IsGood(string q) { return String.Equals(q, Good, StringComparison.Ordinal); }

        /// <summary>是否算一次失败（界面异常计数、导出统计里的失败点数）。skipped 不重复计数。</summary>
        public static bool IsFailure(string q) {
            if (IsGood(q)) return false;
            return !String.Equals(q, Skipped, StringComparison.Ordinal);
        }

        /// <summary>是否代表"这一轮没有真正读到数据"（超时、未读取、归属存疑）。</summary>
        public static bool IsMissing(string q) {
            return String.Equals(q, Timeout, StringComparison.Ordinal)
                || String.Equals(q, Skipped, StringComparison.Ordinal)
                || String.Equals(q, Ambiguous, StringComparison.Ordinal);
        }

        /// <summary>
        /// 宽表/趋势的数值列：没有有效数值时必须留空。
        /// 过去这里会把"超时：等待有效响应超时；接收缓存 ..."这样的文本写进数值列，
        /// 导出后用 Excel 求和会直接报错，趋势也会把它当成一个数值点。
        /// </summary>
        public static string NumericCell(double? number) {
            return number.HasValue ? number.Value.ToString("G17", CultureInfo.InvariantCulture) : "";
        }

        /// <summary>质量的中文标签（界面、明细表、导出质量表共用）。</summary>
        public static string Label(string q) {
            if (IsGood(q)) return "正常";
            if (String.Equals(q, Timeout, StringComparison.Ordinal)) return "超时";
            if (String.Equals(q, ProtocolException, StringComparison.Ordinal)) return "协议异常";
            if (String.Equals(q, DecodeError, StringComparison.Ordinal)) return "解码异常";
            if (String.Equals(q, Ambiguous, StringComparison.Ordinal)) return "响应存疑";
            if (String.Equals(q, Skipped, StringComparison.Ordinal)) return "未读取";
            if (String.Equals(q, Error, StringComparison.Ordinal)) return "通信异常";
            return String.IsNullOrEmpty(q) ? "质量未知" : q;
        }
    }
}
