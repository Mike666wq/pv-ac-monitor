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
        public static string Hex(byte[] data) { return BitConverter.ToString(data).Replace('-', ' '); }
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
        public double? Number;
        public double? RawNumber;
        public string Unit, Quality, SessionId, ConfigVersion;
        public long Round;
    }
    public interface IReadTransport : IDisposable {
        byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log);
    }
    public sealed class SerialTransport : IReadTransport {
        readonly SerialPort port;
        public SerialTransport(string name, int baud) {
            port = new SerialPort(name, baud, Parity.None, 8, StopBits.One);
            port.ReadTimeout = 50; port.WriteTimeout = 1000; port.DtrEnable = false; port.RtsEnable = false;
            port.Handshake = Handshake.None;
            try { port.Open(); } catch { port.Dispose(); throw; }
        }
        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            token.ThrowIfCancellationRequested();
            // Start only after a quiet interval; log bytes discarded before a new request.
            var quiet = Stopwatch.StartNew(); var guard = Stopwatch.StartNew(); var scratch = new byte[256];
            while (quiet.ElapsedMilliseconds < 10) {
                token.ThrowIfCancellationRequested();
                if (guard.ElapsedMilliseconds > 2000) throw new IOException("串口持续有数据，未获得发送前静默间隔");
                if (port.BytesToRead > 0) {
                    int n = port.Read(scratch, 0, Math.Min(scratch.Length, port.BytesToRead));
                    log("发送前丢弃 RX " + Modbus.Hex(scratch.Take(n).ToArray())); quiet.Restart();
                } else token.WaitHandle.WaitOne(1);
            }
            var tx = Modbus.Request(slave, address, count);
            log("TX " + Modbus.Hex(tx)); port.Write(tx, 0, tx.Length);
            var watch = Stopwatch.StartNew(); var input = new List<byte>();
            while (watch.ElapsedMilliseconds < timeoutMs) {
                token.ThrowIfCancellationRequested();
                try {
                    int n = port.Read(scratch, 0, scratch.Length);
                    var chunk = scratch.Take(n).ToArray(); log("RX " + Modbus.Hex(chunk)); input.AddRange(chunk);
                    byte[] frame;
                    if (Modbus.Extract(input, slave, count, out frame)) return frame;
                } catch (TimeoutException) { }
            }
            throw new TimeoutException("等待有效响应超时；接收缓存 " + Modbus.Hex(input.ToArray()));
        }
        public void Dispose() { port.Dispose(); }
    }
    public sealed class SimulationTransport : IReadTransport {
        int tick;
        public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
            if (token.WaitHandle.WaitOne(12)) token.ThrowIfCancellationRequested();
            log("[模拟] TX " + Modbus.Hex(Modbus.Request(slave, address, count)));
            var body = new byte[3 + count * 2]; body[0] = (byte)slave; body[1] = 3; body[2] = (byte)(count * 2);
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
            } else { int value=slave==1?(address==0?1653:address==3?12:20):slave==2?(address==302?24:0):701;body[3]=(byte)(value>>8);body[4]=(byte)value; }
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
            try {
                o.Rx = transport.Read(b.slave,b.address_zero_based,b.register_count,timeoutMs,token,log);
                var buf = new List<byte>(o.Rx); byte[] frame;
                if (!Modbus.Extract(buf,b.slave,b.register_count,out frame) || frame.Length != o.Rx.Length || buf.Count != 0)
                    throw new InvalidDataException("响应CRC、站号或长度不匹配");
                if (frame[1] == 0x83) { o.Status="设备异常 0x"+frame[2].ToString("X2"); }
                else {
                    o.Payload = frame.Skip(3).Take(b.register_count * 2).ToArray();
                    double? value; o.Value=Modbus.Decode(o.Payload,mode,out value); o.Number=value;
                    o.Status = o.Value.StartsWith("非有限") ? "解码待核对" : "收到响应 / 未实机核准";
                }
            } catch (OperationCanceledException) { throw; }
            catch (TimeoutException ex) { o.Status="超时"; o.Value=ex.Message; }
            catch (Exception ex) { o.Status="失败"; o.Value=ex.Message; }
            o.Utc=DateTime.UtcNow; o.RawNumber=o.Number;
            o.Quality=o.Status.StartsWith("收到")?"good":o.Status=="超时"?"timeout":o.Status.StartsWith("设备异常")?"protocol_exception":o.Status=="解码待核对"?"decode_error":"error";
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
