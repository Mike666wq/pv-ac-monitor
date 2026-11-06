using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace ExperimentMonitor {
    public class Binding {
        public string device { get; set; }
        public int slave { get; set; }
        public int address_zero_based { get; set; }
        public int register_count { get; set; }
        public string decode_proposal { get; set; }
        public string forcecontrol_connection { get; set; }
    }
    public class Point {
        public string name { get; set; }
        public string description { get; set; }
        public Binding binding { get; set; }
    }
    public class Configuration {
        public string version { get; set; }
        public List<Point> points { get; set; }
        public static List<Point> Load(string path) {
            var c = new JavaScriptSerializer().Deserialize<Configuration>(File.ReadAllText(path));
            if (c == null || c.points == null) throw new InvalidDataException("点表缺失");
            var points = c.points.Where(p => p.binding != null).ToList();
            if (points.Count == 0 || points.Select(p => p.name).Distinct().Count() != points.Count)
                throw new InvalidDataException("点表必须包含不重名的设备绑定点");
            foreach (var p in points) Modbus.Request(p.binding.slave, p.binding.address_zero_based, p.binding.register_count);
            return points;
        }
    }
    public static class Modbus {
        public static ushort Crc(byte[] data, int length) {
            ushort crc = 0xffff;
            for (int i = 0; i < length; i++) {
                crc ^= data[i];
                for (int b = 0; b < 8; b++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xa001 : 0));
            }
            return crc;
        }
        public static byte[] WithCrc(byte[] body) {
            var output = new byte[body.Length + 2];
            Array.Copy(body, output, body.Length);
            ushort crc = Crc(body, body.Length);
            output[body.Length] = (byte)crc; output[body.Length + 1] = (byte)(crc >> 8);
            return output;
        }
        public static byte[] Request(int slave, int address, int count) {
            if (slave < 1 || slave > 247 || address < 0 || address > 65535 || count < 1 || count > 125 || address + count > 65536)
                throw new ArgumentOutOfRangeException("读取范围", "站号1..247，零基地址0..65535，数量1..125且不能越界");
            return WithCrc(new byte[] {(byte)slave, 3, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count});
        }
        /// <summary>功能码 06：写单个保持寄存器。v1 只支持这一个写功能码。</summary>
        public static byte[] WriteSingle(int slave, int address, int value) {
            if (slave < 1 || slave > 247 || address < 0 || address > 65535 || value < 0 || value > 65535)
                throw new ArgumentOutOfRangeException("写入范围", "站号1..247，零基地址0..65535，写入值0..65535");
            return WithCrc(new byte[] {(byte)slave, 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value});
        }
        /// <summary>06 的回显必须是同一站号/地址/值；0x86 异常帧也在此识别，交由调用方判定。</summary>
        public static bool ExtractWriteEcho(List<byte> buffer, int slave, int address, int value, out byte[] frame) {
            frame = null;
            for (int offset = 0; offset + 2 < buffer.Count; offset++) {
                if (buffer[offset] != slave) continue;
                int function = buffer[offset + 1];
                int size;
                if (function == 0x86) size = 5;
                else if (function == 6 && offset + 5 < buffer.Count && buffer[offset + 2] == (byte)(address >> 8) &&
                         buffer[offset + 3] == (byte)address && buffer[offset + 4] == (byte)(value >> 8) && buffer[offset + 5] == (byte)value) size = 8;
                else continue;
                if (offset + size > buffer.Count) continue;
                var candidate = buffer.GetRange(offset, size).ToArray();
                ushort crc = Crc(candidate, size - 2);
                if (candidate[size - 2] != (byte)crc || candidate[size - 1] != (byte)(crc >> 8)) continue;
                buffer.RemoveRange(0, offset + size); frame = candidate; return true;
            }
            if (buffer.Count > 1024) buffer.RemoveRange(0, buffer.Count - 260);
            return false;
        }
        /// <summary>写回读使用 03 读同一个寄存器；返回 null 表示没有可用的数值。</summary>
        public static int? ReadSingleValue(byte[] frame) {
            if (frame == null || frame.Length != 7 || frame[1] != 3 || frame[2] != 2) return null;
            return (frame[3] << 8) | frame[4];
        }
        public static string Hex(byte[] data) { return BitConverter.ToString(data).Replace('-', ' '); }
        /// <summary>MODBUS RTU 帧间静默要求 3.5 字符时间。9600 8N1（10 bit/字节）时约 3.65 ms。</summary>
        public static double CharTimeMs(int baud) { return 10.0 * 3.5 / (baud <= 0 ? 9600 : baud) * 1000.0; }
        /// <summary>FC03 响应帧长度：站号1 + 功能码1 + 字节数1 + 数据2N + CRC2。</summary>
        public static int ResponseLength(int count) { return 5 + 2 * count; }
        /// <summary>
        /// 一次 03 请求的物理最小往返时间：响应帧自身在线路上的传输时间 + 从站最小处理时间（手册 T12 &lt; 1 ms）。
        /// 低于该值的"响应"在物理上不可能属于本次请求，只能是上一笔事务的迟到帧 —— 03 响应不回显起始地址，
        /// 这是唯一不依赖"站号+字节数签名唯一性"的迟到帧判据。
        /// </summary>
        public static double PhysicalFloorMs(int count, int baud) {
            return ResponseLength(count) * 10.0 / (baud <= 0 ? 9600 : baud) * 1000.0 + 1.0;
        }
        // Resynchronise through noise, unrelated frames, bad CRC and partial input.
        // No RTU transaction ID exists: a late reply with the same slave/length is ambiguous.
        public static bool Extract(List<byte> buffer, int slave, int count, out byte[] frame) {
            frame = null;
            for (int offset = 0; offset + 2 < buffer.Count; offset++) {
                if (buffer[offset] != slave) continue;
                int function = buffer[offset + 1];
                int size;
                if (function == 0x83) size = 5;
                else if (function == 3 && buffer[offset + 2] == count * 2) size = 5 + count * 2;
                else continue;
                if (offset + size > buffer.Count) continue;
                var candidate = buffer.GetRange(offset, size).ToArray();
                ushort crc = Crc(candidate, size - 2);
                if (candidate[size - 2] != (byte)crc || candidate[size - 1] != (byte)(crc >> 8)) continue;
                buffer.RemoveRange(0, offset + size); frame = candidate; return true;
            }
            if (buffer.Count > 1024) buffer.RemoveRange(0, buffer.Count - 260);
            return false;
        }
        public static string Decode(byte[] payload, string mode, out double? number) {
            number = null;
            if (mode.StartsWith("FLOAT")) {
                if (payload.Length != 4) throw new InvalidDataException("浮点解码需要2个寄存器");
                int[] order = mode == "FLOAT CDAB" ? new [] {2,3,0,1} :
                    mode == "FLOAT BADC" ? new [] {1,0,3,2} : mode == "FLOAT DCBA" ? new [] {3,2,1,0} : new [] {0,1,2,3};
                byte[] little = new byte[4];
                for (int i = 0; i < 4; i++) little[3 - i] = payload[order[i]];
                float value = BitConverter.ToSingle(little, 0);
                if (float.IsNaN(value) || float.IsInfinity(value)) return "非有限浮点值（核对字节序）";
                number = value; return value.ToString("G9", CultureInfo.InvariantCulture);
            }
            int[] words = new int[payload.Length / 2];
            for (int i = 0; i < words.Length; i++) words[i] = (payload[i * 2] << 8) | payload[i * 2 + 1];
            if (mode == "RAW") return "原寄存器 " + string.Join(" / ", words.Select(w => w.ToString(CultureInfo.InvariantCulture)).ToArray());
            if (words.Length != 1) throw new InvalidDataException("整数解码需要1个寄存器");
            if (mode == "UINT16") { number = words[0]; return words[0].ToString(CultureInfo.InvariantCulture); }
            if (mode == "INT16 /100（候选）") { number = (short)words[0] / 100.0; return number.Value.ToString("G9", CultureInfo.InvariantCulture) + "（候选，未确认）"; }
            if (mode == "INT16") { number = (short)words[0]; return ((short)words[0]).ToString(CultureInfo.InvariantCulture); }
            return "U16=" + words[0] + " / I16=" + (short)words[0] + "（符号/倍率待定）";
        }
        public static string DefaultMode(Point p) {
            if (p.binding.decode_proposal.StartsWith("float32")) return "FLOAT ABCD";
            return p.binding.decode_proposal.StartsWith("int16") ? "U16 / I16" : "RAW";
        }
    }
    public class Observation {
        public string Point, Description, Device, Mode, Source, Status, Value;
        public int Slave, Address, Count;
        public DateTime Utc;
        // Timestamp identifying the beginning of the acquisition round. Kept separately
        // from Utc, which is the actual response-completion time for this point.
        public DateTime RoundStartedUtc;
        public byte[] Tx = new byte[0], Rx = new byte[0], Payload = new byte[0];
        public long Milliseconds;
        // Measured round trip of the transaction that produced this observation, in
        // milliseconds, measured from the moment the request left the port.
        public long RoundTripMs;
        // Fault classification for the transport layer: "" (none), "timeout",
        // "frame" (CRC/length/protocol), "port" (port disappeared or I/O failed),
        // "busy" (bus not silent, or port held by another program),
        // "late" (response arrived before the physical minimum round trip).
        public string FaultClass;
        public double? Number;
        public double? RawNumber;
        public string Unit, Quality, SessionId, ConfigVersion;
        public long Round;
    }
    /// <summary>总线持续有数据，无法获得发送前静默间隔：通常是同一总线上还有另一个主站。</summary>
    public sealed class BusBusyException : IOException {
        public BusBusyException(string message) : base(message) { }
    }
    /// <summary>端口级故障：端口消失、被占用或读写失败。重建串口对这类故障才有意义。</summary>
    public sealed class PortFaultException : IOException {
        public PortFaultException(string message, Exception inner) : base(message, inner) { }
    }
    public interface IReadTransport : IDisposable {
        byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log);
    }
    /// <summary>
    /// 传输层时序元数据。用于迟到帧判定：请求真正上线的时刻，以及本次请求可能收到的
    /// 响应的物理最小往返时间。实现该接口即启用下界校验；返回 0 表示不启用。
    /// 现有测试替身不必实现，接口保持向后兼容。
    /// </summary>
    public interface ITimedTransport {
        /// <summary>最近一次请求真正写入端口的时刻（Stopwatch 刻度）；尚未发出请求时为 0。</summary>
        long LastRequestSentTicks { get; }
        /// <summary>本次请求可能收到的响应帧对应的物理最小往返毫秒数；0 表示不做下界判定。</summary>
        int PhysicalFloorMs(int count);
    }
    /// <summary>写通道单独成接口：只读测试替身无需实现写，控制功能会被门禁拒绝。</summary>
    public interface IWriteTransport {
        byte[] WriteSingle(int slave, int address, int value, int timeoutMs, CancellationToken token, Action<string> log);
    }
    public sealed class SerialTransport : IReadTransport, IWriteTransport, ITimedTransport {
        readonly SerialPort port;
        readonly int baud;
        // Per-station quarantine after a communication fault. A slave that just failed to
        // answer may still deliver its answer late; that stale frame would otherwise be
        // accepted as the answer to the next request on the same station.
        readonly Dictionary<int, long> quarantineUntil = new Dictionary<int, long>();
        public SerialTransport(string name, int baud) {
            this.baud = baud <= 0 ? 9600 : baud;
            port = new SerialPort(name, this.baud, Parity.None, 8, StopBits.One);
            port.ReadTimeout = 50; port.WriteTimeout = 1000; port.DtrEnable = false; port.RtsEnable = false;
            port.Handshake = Handshake.None;
            try { port.Open(); } catch (UnauthorizedAccessException ex) { port.Dispose(); throw new BusBusyException("串口被占用（另一个程序可能正在使用 " + name + "）：" + ex.Message); }
              catch (IOException ex) { port.Dispose(); throw new PortFaultException("打开串口失败：" + name + "（" + ex.Message + "）", ex); }
              catch { port.Dispose(); throw; }
        }
        public long LastRequestSentTicks { get; private set; }
        public int PhysicalFloorMs(int count) { return (int)Math.Ceiling(Modbus.PhysicalFloorMs(count, baud)); }
        /// <summary>
        /// 发送前静默：MODBUS RTU 要求帧间至少 3.5 字符时间（9600 8N1 约 3.65 ms）。
        /// 现行实现固定 10 ms，是规范值的 2.7 倍，占实测轮时的 22%。这里按波特率取规范值并留余量。
        /// </summary>
        int QuietMs { get { return (int)Math.Ceiling(Modbus.CharTimeMs(baud) + 1.5); } }
        void WaitForQuiet(CancellationToken token, Action<string> log) {
            var quiet = Stopwatch.StartNew(); var guard = Stopwatch.StartNew(); var scratch = new byte[256];
            int required = QuietMs;
            while (quiet.ElapsedMilliseconds < required) {
                token.ThrowIfCancellationRequested();
                if (guard.ElapsedMilliseconds > 2000) throw new BusBusyException("串口持续有数据，未获得发送前静默间隔");
                if (port.BytesToRead > 0) {
                    int n = port.Read(scratch, 0, Math.Min(scratch.Length, port.BytesToRead));
                    log("发送前丢弃 RX " + Modbus.Hex(scratch.Take(n).ToArray())); quiet.Restart();
                } else token.WaitHandle.WaitOne(1);
            }
        }
        /// <summary>
        /// 故障后隔离：超时或帧错的站号在一个物理帧窗口内不再发请求，期间到达的字节一律丢弃。
        /// 窗口按帧长自适应（count=1 约 16 ms，count=12 约 39 ms），而不是固定数百毫秒。
        /// </summary>
        void WaitQuarantine(int slave, int count, CancellationToken token, Action<string> log) {
            long until;
            if (!quarantineUntil.TryGetValue(slave, out until)) return;
            long now = Stopwatch.GetTimestamp();
            if (until <= now) { quarantineUntil.Remove(slave); return; }
            double waitMs = (until - now) * 1000.0 / Stopwatch.Frequency;
            log("站号 " + slave + " 通信故障后隔离 " + waitMs.ToString("0") + " ms（丢弃迟到帧）");
            var deadline = Stopwatch.StartNew(); var scratch = new byte[256];
            int limit = (int)Math.Ceiling(waitMs);
            while (deadline.ElapsedMilliseconds < limit) {
                token.ThrowIfCancellationRequested();
                if (port.BytesToRead > 0) {
                    int n = port.Read(scratch, 0, Math.Min(scratch.Length, port.BytesToRead));
                    log("隔离期丢弃 RX " + Modbus.Hex(scratch.Take(n).ToArray()));
                } else token.WaitHandle.WaitOne(1);
            }
            quarantineUntil.Remove(slave);
        }
        void MarkQuarantine(int slave, int count) {
            double quarantineMs = Modbus.PhysicalFloorMs(count, baud) + 2 * Modbus.CharTimeMs(baud);
            long ticks = (long)(quarantineMs * Stopwatch.Frequency / 1000.0);
            quarantineUntil[slave] = Stopwatch.GetTimestamp() + ticks;
        }
        public byte[] WriteSingle(int slave, int address, int value, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            WaitForQuiet(token, log);
            var tx = Modbus.WriteSingle(slave, address, value);
            log("TX " + Modbus.Hex(tx)); port.Write(tx, 0, tx.Length);
            LastRequestSentTicks = Stopwatch.GetTimestamp();
            var watch = Stopwatch.StartNew(); var input = new List<byte>(); var scratch = new byte[64];
            while (watch.ElapsedMilliseconds < timeoutMs) {
                token.ThrowIfCancellationRequested();
                try {
                    int n = port.Read(scratch, 0, scratch.Length);
                    var chunk = scratch.Take(n).ToArray(); log("RX " + Modbus.Hex(chunk)); input.AddRange(chunk);
                    byte[] frame;
                    if (Modbus.ExtractWriteEcho(input, slave, address, value, out frame)) return frame;
                } catch (TimeoutException) { }
                catch (IOException ex) { throw new PortFaultException("串口读写失败：" + ex.Message, ex); }
            }
            throw new TimeoutException("写入回显超时；接收缓存 " + Modbus.Hex(input.ToArray()));
        }
        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            WaitQuarantine(slave, count, token, log);
            WaitForQuiet(token, log);
            var scratch = new byte[256];
            var tx = Modbus.Request(slave, address, count);
            log("TX " + Modbus.Hex(tx)); port.Write(tx, 0, tx.Length);
            LastRequestSentTicks = Stopwatch.GetTimestamp();
            var watch = Stopwatch.StartNew(); var input = new List<byte>();
            while (watch.ElapsedMilliseconds < timeoutMs) {
                token.ThrowIfCancellationRequested();
                try {
                    int n = port.Read(scratch, 0, scratch.Length);
                    var chunk = scratch.Take(n).ToArray(); log("RX " + Modbus.Hex(chunk)); input.AddRange(chunk);
                    byte[] frame;
                    if (Modbus.Extract(input, slave, count, out frame)) return frame;
                } catch (TimeoutException) { }
                catch (IOException ex) { throw new PortFaultException("串口读写失败：" + ex.Message, ex); }
            }
            MarkQuarantine(slave, count);
            throw new TimeoutException("等待有效响应超时；接收缓存 " + Modbus.Hex(input.ToArray()));
        }
        public void Dispose() { port.Dispose(); }
    }
    public sealed class SimulationTransport : IReadTransport, IWriteTransport, ITimedTransport {
        int tick;
        readonly Dictionary<int, int> registers = new Dictionary<int, int>();
        public int WriteCount { get; private set; }
        /// <summary>测试故障注入：drop=丢回显，exception=0x86 异常，ignore=回显成功但值不变。</summary>
        public string FailMode = "";
        public long LastRequestSentTicks { get; private set; }
        /// <summary>模拟器不做物理下界判定：它没有真实线路时序，启用会把仿真结果误判为迟到帧。</summary>
        public int PhysicalFloorMs(int count) { return 0; }
        static int Key(int slave, int address) { return (slave << 16) | (address & 0xffff); }
        /// <summary>模拟 PLC 的合理初值：远程=1、无故障=0、设定=24、风机/主机/水泵=1 等，便于演示控制闭环。</summary>
        static int SimulationRegister(int slave, int address) {
            if (slave == 2) {
                switch (address) {
                    case 2000: return 60;
                    case 2001: return 1;
                    case 2003: return 1;
                    case 2202: return 24;
                    case 2207: return 45;
                    case 2208: return 0;
                    case 2209: return 1;
                    case 2210: return 2;
                    case 2211: return 1;
                    case 2212: return 50;
                    case 2213: return 1;
                    case 302: return 24;
                }
                return 0;
            }
            if (slave == 1) return address == 0 ? 1653 : address == 3 ? 12 : 20;
            return 701;
        }
        public void Seed(int slave, int address, int value) { registers[Key(slave, address)] = value & 0xffff; }
        public int? Seeded(int slave, int address) { int value; return registers.TryGetValue(Key(slave, address), out value) ? value : (int?)null; }
        public byte[] WriteSingle(int slave, int address, int value, int timeoutMs, CancellationToken token, Action<string> log) {
            if (token.WaitHandle.WaitOne(12)) token.ThrowIfCancellationRequested();
            WriteCount++;
            log("[模拟] TX " + Modbus.Hex(Modbus.WriteSingle(slave, address, value)));
            if (FailMode == "drop") throw new TimeoutException("模拟写入回显丢失");
            if (FailMode == "exception") { var failure = Modbus.WithCrc(new byte[] { (byte)slave, 0x86, 2 }); log("[模拟] RX " + Modbus.Hex(failure)); return failure; }
            if (FailMode != "ignore") { registers[Key(slave, address)] = value & 0xffff; SimulateDeviceReaction(slave, address, value); }
            var echo = Modbus.WriteSingle(slave, address, value); log("[模拟] RX " + Modbus.Hex(echo)); return echo;
        }

        /// <summary>
        /// 模拟设备动作：写设定点后联动一个状态点，让「观察窗口」能看到返回信号变化（仅模拟，真实设备行为以现场为准）。
        /// D2202 主机温度设定 → D2212 冷量反馈 = 设定值；D2001 模式控制 → D2210 当前模式；
        /// D2000 风机速度 → D2209 风机状态（>0 为 1）；D2207 放冷设定 → D2213 水泵（1）。
        /// </summary>
        void SimulateDeviceReaction(int slave, int address, int value) {
            if (slave != 2) return;
            switch (address) {
                case 2202: registers[Key(2, 2212)] = value & 0xffff; break;
                case 2001: registers[Key(2, 2210)] = value & 0xffff; break;
                case 2000: registers[Key(2, 2209)] = value > 0 ? 1 : 0; break;
                case 2207: registers[Key(2, 2213)] = 1; break;
            }
        }
        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            if (token.WaitHandle.WaitOne(12)) token.ThrowIfCancellationRequested();
            log("[模拟] TX " + Modbus.Hex(Modbus.Request(slave, address, count)));
            LastRequestSentTicks = Stopwatch.GetTimestamp();
            var body = new byte[3 + count * 2]; body[0] = (byte)slave; body[1] = 3; body[2] = (byte)(count * 2);
            int seeded;
            if (count == 1 && registers.TryGetValue(Key(slave, address), out seeded)) {
                body[3] = (byte)(seeded >> 8); body[4] = (byte)seeded;
                var seededReply = Modbus.WithCrc(body); log("[模拟] RX " + Modbus.Hex(seededReply)); return seededReply;
            }
            if (count == 1) {
                int simulated = SimulationRegister(slave, address);
                body[3] = (byte)(simulated >> 8); body[4] = (byte)simulated;
                var simulatedReply = Modbus.WithCrc(body); log("[模拟] RX " + Modbus.Hex(simulatedReply)); return simulatedReply;
            }
            if (count == 2) {
                float value;
                if(slave==2)value=25f+(address-125)*.05f+(float)Math.Sin(++tick*.08)*.4f;
                else if(address==8192)value=230.5f+(float)Math.Sin(++tick*.1);
                else if(address==8194)value=1.25f;
                else if(address==8196)value=.245f;
                else if(address==8198)value=-.075f;
                else if(address==8200)value=.288f;
                else if(address==8202||address==8204)value=.85f;
                else if(address==8206)value=50f;
                else if(address==16384)value=359.27f+tick*.0001f;
                else value=1.25f;
                byte[] data = BitConverter.GetBytes(value); Array.Reverse(data); Array.Copy(data, 0, body, 3, 4);
            } else {
                // 模拟器只建模了 1 个和 2 个寄存器的点。早先这里会给 count>=3 填一个
                // "字节数声明正确、CRC 合法、但数据段其余全是 0" 的畸形帧 —— 块读一旦上线，
                // 测试会全绿而现场读到全 0。现在把这个静默陷阱变成显式契约。
                throw new NotSupportedException("模拟器不支持一次读取 " + count + " 个寄存器（仅建模了 1 与 2）。块读测试请改用 FixtureTransport。");
            }
            byte[] reply = Modbus.WithCrc(body); log("[模拟] RX " + Modbus.Hex(reply)); return reply;
        }
        public void Dispose() { }
    }
    public static class Collector {
        public static Observation Read(IReadTransport transport, Point p, string mode, string source, int timeoutMs, CancellationToken token, Action<string> log) {
            Binding b = p.binding;
            var o = new Observation { Point=p.name, Description=p.description, Device=b.device, Slave=b.slave,
                Address=b.address_zero_based, Count=b.register_count, Mode=mode, Source=source,
                Utc=DateTime.UtcNow, Tx=Modbus.Request(b.slave,b.address_zero_based,b.register_count), Status="失败", Value="" };
            var watch = Stopwatch.StartNew();
            var timed = transport as ITimedTransport;
            try {
                o.Rx = transport.Read(b.slave,b.address_zero_based,b.register_count,timeoutMs,token,log);
                var buf = new List<byte>(o.Rx); byte[] frame;
                if (!Modbus.Extract(buf,b.slave,b.register_count,out frame) || frame.Length != o.Rx.Length || buf.Count != 0)
                    throw new InvalidDataException("响应CRC、站号或长度不匹配");
                if (frame[1] == 0x83) { o.Status="设备异常 0x"+frame[2].ToString("X2"); o.FaultClass="frame"; }
                else {
                    // 迟到帧判定：03 响应不回显起始地址，同站同字节数的两笔响应在协议层无法区分。
                    // 但响应帧自身在线路上的传输时间是物理下限 —— 低于这个往返时间的"响应"
                    // 不可能属于本次请求，只能是上一笔事务迟到的帧。
                    long sent = timed != null ? timed.LastRequestSentTicks : 0;
                    int floor = timed != null ? timed.PhysicalFloorMs(b.register_count) : 0;
                    if (sent > 0 && floor > 0) {
                        double roundTripMs = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                        o.RoundTripMs = (long)Math.Round(roundTripMs);
                        if (roundTripMs + 1.0 < floor) {
                            o.Status = "响应早于物理下限（疑似迟到帧）";
                            o.Value = "往返 " + roundTripMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms < 物理下限 " + floor + " ms";
                            o.FaultClass = "late"; o.Quality = Quality.Ambiguous;
                            o.Number = null; o.RawNumber = null; o.Payload = new byte[0];
                            o.Utc = DateTime.UtcNow; o.Milliseconds = watch.ElapsedMilliseconds;
                            return o;
                        }
                    }
                    o.Payload = frame.Skip(3).Take(b.register_count * 2).ToArray();
                    double? value; o.Value=Modbus.Decode(o.Payload,mode,out value); o.Number=value;
                    o.Status = o.Value.StartsWith("非有限") ? "解码待核对" : "收到响应 / 未实机核准";
                }
            } catch (OperationCanceledException) { throw; }
            catch (BusBusyException ex) { o.Status="总线忙"; o.Value=ex.Message; o.FaultClass="busy"; }
            catch (PortFaultException ex) { o.Status="端口故障"; o.Value=ex.Message; o.FaultClass="port"; }
            catch (TimeoutException ex) { o.Status="超时"; o.Value=ex.Message; o.FaultClass="timeout"; }
            catch (InvalidDataException ex) { o.Status="失败"; o.Value=ex.Message; o.FaultClass="frame"; }
            catch (UnauthorizedAccessException ex) { o.Status="总线忙"; o.Value=ex.Message; o.FaultClass="busy"; }
            catch (IOException ex) { o.Status="端口故障"; o.Value=ex.Message; o.FaultClass="port"; }
            catch (Exception ex) { o.Status="失败"; o.Value=ex.Message; o.FaultClass="frame"; }
            o.Utc=DateTime.UtcNow; o.RawNumber=o.Number;
            if (o.Quality==null) o.Quality=o.Status.StartsWith("收到")?Quality.Good:o.Status=="超时"?Quality.Timeout:o.Status.StartsWith("设备异常")?Quality.ProtocolException:o.Status=="解码待核对"?Quality.DecodeError:Quality.Error;
            o.Milliseconds=watch.ElapsedMilliseconds; return o;
        }
    }
    // Writes on the single acquisition worker: no silent queue loss and no UI disk I/O.
    public sealed class Journal : IDisposable {
        readonly StreamWriter samples, frames;
        public string DirectoryPath { get; private set; }
        public Journal(string root, string source) {
            DirectoryPath=Path.Combine(root, source, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(DirectoryPath);
            try {
                samples=Open("samples.csv"); frames=Open("frames.csv");
                samples.WriteLine("utc,source,point,description,device,slave,address_zero_based,register_count,decode,status,value,elapsed_ms,tx_hex,rx_hex,payload_hex");
                frames.WriteLine("utc,source,message"); samples.Flush(); frames.Flush();
            } catch { if(samples!=null) samples.Dispose(); if(frames!=null) frames.Dispose(); throw; }
        }
        StreamWriter Open(string name) { return new StreamWriter(new FileStream(Path.Combine(DirectoryPath,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new System.Text.UTF8Encoding(true)); }
        public static string Cell(string value) {
            value=value??"";
            // Guard CSV text cells against spreadsheet formula interpretation.
            if(value.Length>0 && "=+@-\t\r".IndexOf(value[0])>=0) value="'"+value;
            return "\""+value.Replace("\"","\"\"")+"\"";
        }
        public void Frame(string source,string text) { frames.WriteLine(Cell(DateTime.UtcNow.ToString("o"))+","+Cell(source)+","+Cell(text)); frames.Flush(); }
        public void Sample(Observation o) {
            string[] fields={o.Utc.ToString("o"),o.Source,o.Point,o.Description,o.Device,o.Slave.ToString(),o.Address.ToString(),o.Count.ToString(),o.Mode,o.Status,o.Value,o.Milliseconds.ToString(),Modbus.Hex(o.Tx),Modbus.Hex(o.Rx),Modbus.Hex(o.Payload)};
            samples.WriteLine(string.Join(",",fields.Select(Cell).ToArray())); samples.Flush();
        }
        public void Dispose() { try { samples.Dispose(); } finally { frames.Dispose(); } }
    }
}
