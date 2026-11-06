using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ExperimentMonitor {
    /// <summary>
    /// 好设备替身：按真实寄存器表建模，支持任意读取长度（1..125），永不产生异常帧。
    ///
    /// 存在的原因：模拟器只建模了 1 个和 2 个寄存器的点，而且对 count&gt;=3 会返回一个
    /// "字节数声明正确、CRC 合法、但数据段其余全是 0" 的畸形帧。用这种替身验收块读，
    /// 测试会全绿而现场读到全 0 —— 那是假绿灯，不是测试。
    ///
    /// 寄存器表按站号分段，每段声明了"哪些地址有效"，未声明的地址返回 0
    /// （与 PLC 读未绑定 D 寄存器的真实行为一致：不会抛异常，返回该 D 的当前值）。
    /// </summary>
    public sealed class FixtureTransport : IReadTransport, IWriteTransport, ITimedTransport {
        /// <summary>站号 → (起始地址, 有效寄存器个数)。读取范围超出该段时按 0 填充。</summary>
        readonly Dictionary<int, KeyValuePair<int, int>> segments = new Dictionary<int, KeyValuePair<int, int>>();
        readonly Dictionary<long, ushort> words = new Dictionary<long, ushort>();
        readonly Dictionary<long, float> floats = new Dictionary<long, float>();
        /// <summary>把某个地址读成保留地址（0x200C）：DDSU666 手册标 R/W，但返回的是无意义值。</summary>
        public int ReservedSlave = -1, ReservedAddress = -1;
        public long LastRequestSentTicks { get; private set; }
        public int PhysicalFloorMs(int count) { return 0; }
        public int WriteCount { get; private set; }
        public readonly List<string> WriteLog = new List<string>();

        public FixtureTransport DeclareSegment(int slave, int startAddress, int registerCount) {
            segments[slave] = new KeyValuePair<int, int>(startAddress, registerCount);
            return this;
        }
        /// <summary>声明一个双寄存器浮点点（ABCD 线上字节序，与抓包一致）。</summary>
        public FixtureTransport SetFloat(int slave, int address, float value) {
            floats[((long)slave << 32) | (uint)address] = value; return this;
        }
        /// <summary>声明一个单寄存器整数值。</summary>
        public FixtureTransport SetWord(int slave, int address, ushort value) {
            words[((long)slave << 32) | (uint)address] = value; return this;
        }
        public void MarkReserved(int slave, int address) { ReservedSlave = slave; ReservedAddress = address; }

        static long Key(int slave, int address) { return ((long)slave << 32) | (uint)address; }

        bool InSegment(int slave, int address) {
            KeyValuePair<int, int> segment;
            if (!segments.TryGetValue(slave, out segment)) return false;
            return address >= segment.Key && address < segment.Key + segment.Value;
        }

        ushort WordAt(int slave, int address) {
            if (slave == ReservedSlave && address == ReservedAddress) return 0x7A7A; // 保留寄存器：有效但不��意义
            float f;
            if (floats.TryGetValue(Key(slave, address), out f)) {
                // ABCD 线上字节序：高位字在前，低位字在后
                var bytes = BitConverter.GetBytes(f);
                return (ushort)((bytes[0] << 8) | bytes[1]);
            }
            ushort w;
            if (words.TryGetValue(Key(slave, address), out w)) return w;
            if (InSegment(slave, address)) return 0;
            return 0;
        }

        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            LastRequestSentTicks = Stopwatch.GetTimestamp();
            log("[夹具] TX " + Modbus.Hex(Modbus.Request(slave, address, count)));
            var body = new byte[3 + count * 2];
            body[0] = (byte)slave; body[1] = 3; body[2] = (byte)(count * 2);
            for (int i = 0; i < count; i++) {
                ushort w = WordAt(slave, address + i);
                body[3 + i * 2] = (byte)(w >> 8); body[4 + i * 2] = (byte)w;
            }
            var reply = Modbus.WithCrc(body);
            log("[夹具] RX " + Modbus.Hex(reply));
            return reply;
        }

        public byte[] WriteSingle(int slave, int address, int value, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            WriteCount++;
            WriteLog.Add(slave + "/" + address + "=" + value);
            var body = new byte[] { (byte)slave, 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value };
            var reply = Modbus.WithCrc(body);
            log("[夹具] TX " + Modbus.Hex(reply) + " / RX " + Modbus.Hex(reply));
            return reply;
        }
        public void Dispose() { }
    }

    /// <summary>
    /// 坏设备替身：按脚本逐帧投递任意字节，用来重现协议层最难重现的失败形态。
    ///
    /// 现有 6 个传输层替身都只能"返回一个 byte[]"，因此无法表达"上一笔事务的帧先到"
    /// "超时之后才迟发"这类情况 —— 而这恰好是本次要修的核心失败模式。
    /// </summary>
    public sealed class ScriptedTransport : IReadTransport, IWriteTransport, ITimedTransport {
        public sealed class Step {
            /// <summary>要返回的原始字节；为空且 Throw 为真时抛出 TimeoutException。</summary>
            public byte[] Bytes;
            /// <summary>投递延迟（毫秒）。用于制造"迟到帧"：上一次请求的帧延迟若干毫秒后才到。</summary>
            public int DelayMs;
            /// <summary>本步是否直接抛出超时异常（模拟无响应）。</summary>
            public bool Throw;
            /// <summary>本步对应的站号；用于按站号匹配脚本。</summary>
            public int Slave = -1;
        }
        public readonly Queue<Step> Script = new Queue<Step>();
        public int WriteCount { get; private set; }
        public long LastRequestSentTicks { get; private set; }
        /// <summary>启用物理下界判定的往返下限（毫秒）。0 表示不启用。</summary>
        public int FloorMs;
        public int PhysicalFloorMs(int count) { return FloorMs; }
        /// <summary>每次 Read 调用都会记录，便于断言"第几笔读拿到了哪一帧"。</summary>
        public readonly List<string> Trace = new List<string>();

        public ScriptedTransport Enqueue(byte[] bytes, int delayMs, int slave) {
            Script.Enqueue(new Step { Bytes = bytes, DelayMs = delayMs, Slave = slave }); return this;
        }
        public ScriptedTransport EnqueueTimeout(int slave) {
            Script.Enqueue(new Step { Throw = true, Slave = slave }); return this;
        }

        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            LastRequestSentTicks = Stopwatch.GetTimestamp();
            Step step = null;
            while (Script.Count > 0) {
                var candidate = Script.Peek();
                if (candidate.Slave >= 0 && candidate.Slave != slave) break;
                step = Script.Dequeue(); break;
            }
            if (step == null) throw new TimeoutException("脚本已耗尽，无可投递的帧（站号 " + slave + "）");
            if (step.DelayMs > 0) {
                if (token.WaitHandle.WaitOne(step.DelayMs)) token.ThrowIfCancellationRequested();
            }
            if (step.Throw) {
                Trace.Add(slave + "/" + address + "/" + count + " -> timeout");
                log("[脚本] 超时");
                throw new TimeoutException("脚本注入的超时");
            }
            Trace.Add(slave + "/" + address + "/" + count + " -> " + step.Bytes.Length + " bytes");
            log("[脚本] RX " + Modbus.Hex(step.Bytes));
            return step.Bytes;
        }

        public byte[] WriteSingle(int slave, int address, int value, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            WriteCount++;
            var body = new byte[] { (byte)slave, 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value };
            var reply = Modbus.WithCrc(body);
            log("[脚本] TX " + Modbus.Hex(reply) + " / RX " + Modbus.Hex(reply));
            return reply;
        }
        public void Dispose() { }
    }
}
