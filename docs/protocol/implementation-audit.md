> ## ⚠ 定版说明（2026-11-06 晚，0.8.0 复核）
>
> **本审计针对 0.7.0，其中多数"必须改"项已在 0.8.0 修复。**
> 不要把本文当作当前验收基线 —— 当前基线是 `CHANGELOG.md` 的 0.8.0 条目与
> `test-matrix.json`（**13 套件 / 4646 项断言 / 8 项需真机或现场的缺口**，2026-11-10 于 0.8.7 复核）；另见 `docs\protocol\audit-0.8.2.md`。
> 本文保留原样，是因为它记录的"缺陷是什么、怎么发现的"依然有参考价值。
>
> | 原编号 | 缺陷 | 0.8.0 状态 |
> |---|---|---|
> | A | 直流表物理比例没有落地（37 点 Scale 全是 1） | **已修**（0.7.1）：`PointCatalog.Add` 增加 scale 参数；ZU6682 ×0.1 V、ZI6682 ×0.001 A、ZW66822 ×1.0 W。`SelfTests` 有"1754→175.4 V"等断言。 |
> | B | 随包 control-config 为 `preview` 且 `requireConfirmed=false`，门禁整段短路 | **已修**（A3）：`confirmed` 与 `allowWrite` 各自独立生效；随包 4 个目标全部 `false` ⇒ 一条 06 都不会发；`mode` 语义收敛为 disabled/live/preview（preview 是旧写法，按旧行为写并告警）。`ControlTests` 新增 31 条门禁回归。 |
> | C | 写链路等待预算单位错误（秒当毫秒） | **已修**（0.7.1）：改为 `timeoutSeconds × 2000 ms`，等待预算覆盖完整链路。 |
> | D | 03 响应不回显地址，迟到帧可能被当成本次响应 | **已修**（A1）：按**物理最小往返时间**判定（`Δ ≥ (5+2N)·10/9600 + 1 ms`），低于下界判 `ambiguous` 且不出数值；另加按站隔离窗口（按帧长自适应）。用 `Stopwatch` 计时。 |
>
> 其余仍在的项：
> - §0.3 的文档不一致：README 的版本号与控制默认值已随 0.8.0 更新；
>   "逐次二次确认"这条不一致**不会修**——0.8.0 明确否决每次弹窗（实测写入是成组爆发，
>   每次弹窗必然被训练掉），并已在 README 中写明"没有二次确认弹窗"。
> - §0.4 中"温度块点名数字与连接项对不上"仍然成立：点名里的数字不是寄存器号，
>   `docs/field/TROUBLESHOOTING.md` 第 12 条已把这个陷阱写给现场。
> - D2001 地址 2201 vs 2001 仍未裁决（2026-11-06 决定：保持 2201、只读不写）。

---
# 现有实现与协议的符合性审计（implementation audit）

- 审计对象：`ExperimentMonitorDemo`（PV-AC-Monitor 0.7.0）
- 审计时间：2026-11-06
- 审计方式：**只读代码与配置**，未构建、未运行测试、未改任何代码/配置（本文件是唯一写入）
- 审计依据：`protocol.json`（v0.9，40 点/37 绑定）、`point-contract.json`、`control-config.json`、`PointCatalog.cs`、`Protocol.cs`、`MonitorEngine*.cs`、`Control*.cs`、`DashboardForm.cs`、`OverviewPage.cs`、`Charts.cs`、`TrendSeriesLoader.cs`、`HistoryPage.cs`、`ExperimentExport.cs`、`README.md`、`CONTROL-DESIGN.md`、`ACCEPTANCE.md`、`CHANGELOG.md`、`PROTOCOL-REFERENCE.md`、`ForceControlAnalysis\result\串口通信协议-V0.9.md`
- 判定口径：
  - **必须改** = 会导致误读数值、误写 PLC、或让现场人员按文档做出错误判断
  - **建议改** = 不立即致命，但削弱可维护性/可测试性/性能
  - **待现场确认** = 代码侧无法裁决，必须到现场用表/PLC 核对后回填

---

## 0. 结论摘要（先看这里）

### 0.1 三个最严重的实现缺陷

| # | 缺陷 | 后果 | 位置 |
|---|---|---|---|
| A | **直流表物理比例完全没有落地**。`PointInfo.Scale` 字段存在、`MonitorEngine` 也已经乘了，但 `PointCatalog.Add()` 没有 scale 参数，37 个点的 `Scale` 全是 1。 | 太阳能电压显示 1592 V（应 159.2 V，**10 倍**）、电流显示 892 A（应 0.892 A，**1000 倍**）。趋势、导出、云端快照全部是错值。 | `PointCatalog.cs:15-17,45-47`；`MonitorEngine.cs:83` 已是正确挂钩点 |
| B | **随包的 `control-config.json` 是 `mode=preview` 且 `enforceInterlocks=false`、`requireConfirmed=false`**，而 README / CONTROL-DESIGN / ACCEPTANCE 三份文档都声称"默认关闭、必须现场确认后手动解锁"。 | 现场拿到包就能开控制开关直接发 06；`D2202` 的允许范围是 0–60 ℃（猜的），`D2000` 是 0–100（可能是 0–3 档位）。这是唯一能**真的动设备**的路径。 | `control-config.json:3,6,7`；`README.md:7,105`；`CONTROL-DESIGN.md:205,259`；`ACCEPTANCE.md:17` |
| C | **写链路等待预算单位错误**：`currentTimeoutMs` 存的是**秒**，`ExecuteControl` 直接把它当毫秒用。 | `waitMs = period*1000 + 8 + 8000 ≈ 20000`（被下限夹住），而真实最坏耗时 ≈ period + 8 s 写 + 1.5 s 观察 + 2×8 s 回读 ≈ 27.5 s。合法写入会被判"排队超时 → 结果未知"，进而**误上锁**，现场会以为 PLC 有问题。 | `MonitorEngine.Control.cs:301-306`；`MonitorEngine.cs:67,76` |

### 0.2 一个必须知晓的协议层风险

| # | 风险 | 说明 |
|---|---|---|
| D | **03 响应帧不回显寄存器地址**，`Modbus.Extract` 只校验「站号 + 功能码 + 字节数 + CRC」。同一从站、同为 count=1 或 count=2 的两个点，**上一笔请求的迟到响应会被当作本次请求的响应接受**。 | 代码注释已承认这点（`Protocol.cs:91-92`），也有发送前静默丢弃（`Protocol.cs:167-177`），但**没有时间窗校验**。最坏后果：控制写入后的 03 回读被一个陈旧的 `D2209` 响应满足 → 误判 `applied`。见 R-05 / R-07 与附录 C.3。 |

### 0.3 文档与代码不一致的 5 处硬伤

1. 默认控制模式：文档说 `disabled`，随包文件是 `preview`（见 B）。
2. `README.md:25` 仍写"设备通信使用 Modbus RTU 功能码 03，**只读寄存器**"，与 0.7.0 的 06 写入自相矛盾（同一文件 `:3` 又说有 06）。
3. `README.md:7` 写"逐次二次确认"，但 `ControlConfirmForm.cs` **从未被实例化**（全仓仅自身定义），`requireOperatorAck` 也被引擎忽略（`CONTROL-DESIGN.md:68` 已如实承认"引擎未读取"）。即：**当前没有任何二次确认弹窗**。
4. `README.md:89`「在设定表里**勾选**要发送的控制点…点『发送勾选点』」、`CONTROL-DESIGN.md:198,207` 同样的"勾选/发送勾选点/本点发送"以及"列含**采样时间**、**上次写入**"，而实际 UI（`ControlPage.cs:93-100`）只有 8 列、只有「发送全部已修改」+ 行内按钮，`PreviewControl` 也无界面入口（`CHANGELOG.md:5,8,10` 才是对的）。
5. `ControlTests` 项数三处不一致：`ACCEPTANCE.md:28` 与 `CONTROL-DESIGN.md:244` 写 51 项，`CHANGELOG.md:25` 写 60 项，实际源码 `Check(` 调用 **61** 处。

### 0.4 已被代码/配置推翻的 `PROTOCOL-REFERENCE.md` 结论

| PROTOCOL-REFERENCE 的说法 | 实际情况 |
|---|---|
| §5「地址规则 D n ↔ 地址 n … ✓ 正确」 | 规则本身对，但**温度块 6 个点（T0/T2/T3/T4/T5/D2206）的点名数字（2203/2205/2204/345/347/2206）与连接项（HRF126/130/132/134/136/128）对不上**，差 2073–2079（T0/D2206/T2/T3）或恰好 212（T4/T5）。文档只在 §7 含糊提了一句"D2203 是否存在"，没有把"这批点靠名字核对一定找错寄存器"列为结论。 |
| §1.4「D128–D7999 是 **EEPROM** 保持区，频繁写有寿命限制」 | FX3U 的 D 区保持是**电池后备 RAM**，EEPROM 用于程序/参数区。结论（别循环写）方向正确，但**机理表述不严谨**，不宜作为"必须改代码"的依据。 |
| §6.3「D2001 地址 2201 → 2001」 | 代码与配置**仍统一在 2201**（`protocol.json` D2001、`control-config.json` D2001.address、`ApplySimulationDemoConfig` 全部 2201）。`PROTOCOL-REFERENCE` 已提出但未落地；而 `control-config.json:44,50` 只做了告警。**是否要改仍未裁决**（见 C-01 与 M-13）。 |
| §3「三个直流表点都没有乘比例」 | 属实，且**比文档描述的更严重**：`Scale` 机制已存在但从未被赋值（见 A），所以这不是"缺功能"而是"填错常量"。 |

---

## 1. 串口与 RTU 帧

### R-01 串口参数来源

- **位置**：`DashboardForm.cs:51,64,81,209,225-226`；`MonitorEngine.cs:52-60`；`settings.json:1`
- **现状**：
  - `settings.json` = `{"Simulation":false,"Port":"COM10","Period":10,"Timeout":8}`。
  - `DashboardForm` 构造时算 `settingsPath`（`:51`，用于保存到 `:226` 和读取到 `:225`），但 `RefreshPorts()`（`:227`）和 `Connect`（`:209`）用的是**下拉框实际内容**，而 `LoadSettings` 的注释与实现都会把保存的端口塞进下拉框（`:225`）。
  - 波特率在 DashboardForm 里是**硬编码 9600**：`:209` `Engine.Connect(simulation.Checked,port.Text,9600)`；工具栏文字 `:81` `"9600 / 8N1"` 也是常量。
  - `elsewhere`：`Program.cs:52,121-122` 的旧 `MonitorForm` 反而有波特率下拉框（默认 9600）并**强制把 "COM9" 塞进端口列表且默认选中 COM9**。
- **问题/风险**：
  1. 任务背景说现场是 **COM9**，`settings.json` 写的是 **COM10**，`MonitorForm` 默认 **COM9**，`CONTROL-DESIGN.md:195,206` 的示例写 `COM10` —— 四处口径不一。`settings.json` 若随包发布，会让程序启动就选中一个不存在的口。
  2. `Simulation` 在 `LoadSettings` 里被强制 `false`（`:225`），也就是 `settings.json` 的 `Simulation` 字段**写而不读**（写入见 `:226`）。这本身是刻意的安全行为（README:21 有说明），但字段留着会误导。
  3. 波特率不可调、界面写死 9600：与协议（9600 8N1）一致，属可接受；但 `MonitorEngine`/`SerialTransport` 本身**支持任意 baud**（`Protocol.cs:160-162`），只有 UI 写死，属于能力与暴露面不一致。
- **判定**：**必须改**（1、2 项）；3 项**建议改**。
- **修法**：
  - 把 `settings.json` 的 `Port` 统一改成现场实际口（COM9），或**干脆随包删除 `settings.json`**（代码在没有该文件时走 `RefreshPorts` 默认，行为正常）。改 `settings.json:1`。
  - `DashboardForm.cs:225` 删掉无效的 `simulation.Checked=false;` 变成注释说明，或让 `SavedSettings` 保留 `Simulation` 字段但明确注释"读入即忽略"。
  - `DashboardForm.cs:81` 把 `"9600 / 8N1"` 改为 `$"{baud} / 8N1"` 并加一个 `NumericUpDown baud`（`Program.cs:52` 已有现成写法可抄），既然后端已支持。

### R-02 打开/关闭/重连/线程模型

- **位置**：`Protocol.cs:158-165`（构造与 `Open`）、`:213`（`Dispose`）；`MonitorEngine.cs:52-61`（连接/断开）、`:76-77`（起线程）、`:83-84`（故障退避与重建通道）、`:86`（收尾）
- **现状**：
  - `SerialTransport` 在构造函数里 `new SerialPort(...)` + `Open()`，失败则 `Dispose` 后重抛（`:164`）。`ReadTimeout=50`、`WriteTimeout=1000`、`DtrEnable=false`、`RtsEnable=false`、`Handshake=None`。
  - 采集是**单后台线程** `worker`（`MonitorEngine.cs:76`，`IsBackground=true`，Name="实验串口采集"）。
  - 断线重连：某站号首次 `timeout/error` 时**整站跳过**并按 5→10→20→40→60 s 指数退避（`:83`），同时 `Dispose` 旧通道、新建一个（`:83` 后半），并做 `adopted` 双检避免与 UI 的 Disconnect 竞争。
  - `Disconnect` 有 10 s 等待诊断结束的上限（`:61`），失败会保留连接并抛错。
- **问题/风险**：
  1. 重连只重建 `SerialTransport`，**不重置 `failed`/`probeAt` 之外的任何状态**；如果串口是被其他程序（力控）占着，`new SerialPort.Open()` 会抛 `UnauthorizedAccessException`，此时 `adopted=false` → 直接 `throw IOException("连接状态已改变…")` → 外层 `catch` 让整轮采集停止（`:86` 里 `connectedTransport==null` → 断开连接）。行为是对的（不静默），但**用户看到的是"串口重连失败，连接已断开"**，而不是"COM9 被占用"。建议把 `UnauthorizedAccessException` 单独翻译成"串口被占用，请先关闭力控"。
  2. `SerialPort` 对象被 `Read`/`WriteSingle` 共用，**没有加锁**。目前靠"同一时刻只有一个 worker 线程操作串口"这个约定成立——`BeginDiagnostic` 会拒绝与采集并存（`:90`），控制写走队列（`MonitorEngine.Control.cs:373`），`ReadBack` 也只在 worker 上跑。**约定是对的，但没有编译期/运行期保护**：任何人以后在 UI 线程调 `Collector.Read(connectedTransport,…)` 就会与 worker 抢同一个 `SerialPort`。建议在 `SerialTransport` 内部加 `lock(_io)`（或 `SemaphoreSlim`）兜底。
  3. `WriteSingle` 里 `port.Write` 之后**没有 `DiscardInBuffer`**，只在写前 `WaitForQuiet`。见 §1.6。
- **判定**：1、3 **建议改**；2 **建议改**（护栏）。
- **修法**：
  - `MonitorEngine.cs:83` 的 `catch{}` 分支：捕获 `UnauthorizedAccessException` 时 `log("串口被占用：关闭力控对 " + port + " 的采集后重试")`，并把它作为不可恢复错误处理（不要退避重试）。
  - `Protocol.cs:159` 加 `readonly object io=new object();`，在 `Read`/`WriteSingle`/`WaitForQuiet` 全用 `lock(io)` 包住。

### R-03 请求帧构造（站号/功能码/起址/数量）

- **位置**：`Protocol.cs:54-58`（FC03）、`:59-64`（FC06）
- **现状**：
  - FC03：`[slave, 0x03, addrHi, addrLo, cntHi, cntLo, crcLo, crcHi]`。
  - FC06：`[slave, 0x06, addrHi, addrLo, valHi, valLo, crcLo, crcHi]`。
  - 参数校验：站号 1..247、地址 0..65535、数量 1..125、`address+count<=65536`、写入值 0..65535（`:55,61`）。
  - `Configuration.Load`（`:28-36`）会对**每个绑定**调用一次 `Modbus.Request` 做越界自检，坏点表在启动期就炸。
- **问题/风险**：**无。构造正确**：大端高字节先发、站号范围、地址范围、数量上限都符合 MODBUS 规范。唯一可挑的是 `count<=125`（规范 0x7D=125，正确）。
- **判定**：✅ 符合。

### R-04 CRC16

- **位置**：`Protocol.cs:39-46`（`Crc`）、`:47-53`（`WithCrc`）
- **现状**：初值 `0xFFFF`，逐字节异或，8 次 `crc>>1 ^ 0xA001`，写入时 `output[n]=(byte)crc; output[n+1]=(byte)(crc>>8)` 即**低字节先发**。
- **问题/风险**：**无。** 与任务书要求（初值 0xFFFF、多项式 0xA001、低字节先发）完全一致。`SelfTests.cs:26` 用参考报文 `01 03 00 00 00 0A C5 CD` 钉住，`:27` 再用 `03 03 20 00 00 02 CE 29` 钉住现场点，**这层测试是可靠的**。
- **判定**：✅ 符合。

### R-05 响应帧校验（站号/功能码/字节数/CRC）

- **位置**：`Protocol.cs:93-110`（`Extract`）、`:312-313`（`Collector` 二次校验）、`:94`（诊断路径同样二次校验）
- **现状**：从 `offset=0` 起逐字节滑动，只在 `buffer[offset]==slave` 时判定：
  - `function==0x83` → 帧长 5；
  - `function==3` 且 `buffer[offset+2]==count*2` → 帧长 `5+count*2`；
  - 其他一律 `continue`（滑到下一字节）；
  - 长度够才验 CRC，CRC 不符继续滑（重同步）。
  - 中途还做 `if (buffer.Count>1024) buffer.RemoveRange(0, buffer.Count-260)` 防内存膨胀（`:108`）。
  - `Collector.Read` 再抽一次并要求 `frame.Length==o.Rx.Length && buf.Count==0`（`:312`），即"传输层必须刚好吐一帧"。
- **问题/风险**：
  1. **正常**：CRC 错/站号错/字节数错都不会被误接受，能在噪声中重同步（`SelfTests.cs:38-41` 有对应用例）。
  2. **风险 D（§0.2）**：帧里没有地址字段，无法证明"这帧对应我这次请求"。若某笔请求超时，其响应迟到并在下一笔请求的接收窗口内到达，且两者 `slave` 与 `count` 相同，就会被当成本次响应。**两个点同站号同长度就足够触发**（本点表里 D1…D8 全是 slave3/count2，D2208~D2213 全是 slave2/count1，极易同构）。
  3. `Extract` 的循环上界 `offset+2 < buffer.Count` 意味着**只有 2 字节时扫不到**，会等下一批字节，行为正确。
- **判定**：2 **必须改（加固）**，其余符合。
- **修法**（见 §1.6 的代码片段）：在传输层加"最小应答时延"闸门，并把这条风险写进 `ACCEPTANCE.md` 的现场核验项。

### R-06 异常响应 0x83 / 0x86

- **位置**：`Protocol.cs:99,314`（读：0x83）；`Protocol.cs:72,481-488`（写：0x86）
- **现状**：
  - 读：识别 0x83，`o.Status="设备异常 0x"+code`，`Quality="protocol_exception"`，**不产生数值**（`SelfTests.cs:59-60` 有断言）。
  - 写：识别 0x86，`status="rejected"`，并用 `ExceptionHint`（`MonitorEngine.Control.cs:587-594`）翻译 01/02/03/04/06。
  - 异常码 05/08/0A/0B 未翻译（返回空串，只显示十六进制）。
- **问题/风险**：
  1. 读路径的异常码**没有翻译**（只有 `0x02` 这种原始码），现场看不懂"非法数据地址"这个关键信息——而 `PROTOCOL-REFERENCE.md:47` 明确说"访问未使用地址会返回异常响应"。温度块地址可疑时，异常码翻译恰恰是最有用的诊断。
  2. `ExceptionHint` 只覆盖 6 个码，MODBUS 还有 05(确认)、07(否定确认)、08(存储奇偶错)。
- **判定**：**建议改**。
- **修法**：把 `ExceptionHint` 从 `MonitorEngine.Control.cs:587` 提到 `Protocol.cs` 的 `Modbus` 静态类里，读路径 `Protocol.cs:314` 改成：
  ```csharp
  if (frame[1] == 0x83) { o.Status = "设备异常 0x" + frame[2].ToString("X2") + Modbus.ExceptionHint(frame[2]); }
  ```
  并补齐 05/07/08。

### R-07 超时 / 重试 / 帧间静默 / 短帧 / 粘帧

- **位置**：`Protocol.cs:167-177`（`WaitForQuiet`）、`:195-212`（`Read` 超时循环）、`:178-194`（`WriteSingle`）；`MonitorEngine.cs:83`（站级退避）
- **现状**：
  - **帧间静默**：每次发送前 `WaitForQuiet` 要求连续 10 ms 无 RX 才发；期间收到的字节**丢弃并记日志**；2 s 拿不到静默就抛 `IOException`。9600 8N1 下 1 字符 = 1.042 ms，t3.5 = **3.65 ms**，所以 10 ms 足够，这条**满足 RTU 要求**。
  - **超时**：`SerialPort.ReadTimeout=50`，内层 `catch(TimeoutException){}` 空转，外层用 `Stopwatch` 到 `timeoutMs`（= `超时(s)*1000`，`MonitorEngine.cs:80`）抛出。
  - **重试**：**没有逐点重试**。一次超时即该点失败；靠**站级**退避（`MonitorEngine.cs:83`，5/10/20/40/60 s）+ 整站跳过。
  - **短帧**：靠 `offset+size > buffer.Count` 继续等，正确。
  - **粘帧**：`Extract` 拿到一帧后 `RemoveRange(0, offset+size)`，剩余留给下一帧；`Collector` 要求 `buf.Count==0` 否则判"长度不匹配"。
- **问题/风险**：
  1. 没有逐点重试是**设计选择**（对慢设备友好），但对**偶发 CRC 错**（RS-485 现场常态）没有第二次机会，直接进入 5 s 退避 → 一轮数据全是"超时"。建议对 `error`（CRC 错，说明有活体）与 `timeout`（可能真的没设备）区别对待：**CRC 错立即原地重试 1 次**（成本 1 帧），超时不重试。
  2. `WaitForQuiet` 的 2 s 硬上限在强噪声（接线松/终端电阻缺失）时会抛 `IOException` 让整轮采集停止，而不是"跳过本轮"。现场更希望的是"记录噪声、继续下一轮"。
  3. **风险 D 的时序闸门缺失**（见 R-05）。
- **判定**：1、2 **建议改**；3 **必须改（加固）**。
- **修法**：
  - `Protocol.cs:195` 的 `Read` 增加 `int retries` 参数（默认 0），`Collector.Read` 只在 `o.Status=="失败"`（即 CRC/长度错）时重发一次。
  - `WaitForQuiet` 的 `throw` 改为返回 `bool`，`Collector` 侧把"无静默"落成 `Quality="bus_noise"` 并只跳过该点。
  - 时序闸门：
    ```csharp
    // Protocol.cs:200 附近，port.Write 之后
    long txStart = watch.ElapsedMilliseconds;
    long minFirstByte = (long)Math.Ceiling(tx.Length * 10.0 / baud) + 1; // 请求尚未发完就到的字节必为陈旧帧
    long firstByteAt = -1;
    ...
    int n = port.Read(scratch, 0, scratch.Length);
    if (firstByteAt < 0) firstByteAt = watch.ElapsedMilliseconds;
    if (firstByteAt < minFirstByte) { log("丢弃早于请求结束的陈旧字节 " + Modbus.Hex(chunk)); input.Clear(); firstByteAt = -1; continue; }
    ```
    `SerialTransport` 需要把 `baud` 存成字段（构造参数已有，`:160`）。

### R-08 支持的功能码 / 批量读是否合并

- **位置**：`Protocol.cs:54-64`（只有 03 与 06）；`MonitorEngine.cs:83`（逐点读）
- **现状**：
  - 读：**只有功能码 03**（保持寄存器）。**没有 04**（输入寄存器）。
  - 写：**只有功能码 06**（单寄存器）。**没有 0x10（16，写多寄存器）**，**没有 0x16（22，掩码写）**。
  - 注释明确：`Protocol.cs:59` "v1 只支持这一个写功能码"；`ControlConfig.cs:118,121` 强制"单寄存器 + kind=int16"。
  - **批量读完全没有合并**：37 个绑定 = 每轮最多 37 次独立 03 请求。
- **问题/风险**：
  1. **04 缺失**：`PROTOCOL-REFERENCE.md:27` 指出 FX3U 从站支持 0x04；DDSU666 手册里部分量（若按 3x 区）也要 04。目前所有点按 4x 处理，实测能读到值（力控也是这么配的），所以**大概率不需要**，但属于"未验证的能力缺口"。
  2. **未合并批量读是真实的效率与可靠性问题**：本点表里有三块天然连续区：
     - `D1..D8`（slave 3，地址 8192..8207，**16 个寄存器一次读完**）→ 今天要发 8 次；
     - `DU1..DU8`（slave 6，同样 8192..8207）→ 8 次 → 1 次；
     - `T0/D2206/T2/T3/T4/T5`（slave 2，地址 **125..136 连续 12 个寄存器**）→ 6 次 → 1 次。
     合计每轮 **37 次 → 16 次**，9600 速率下每轮通信时间大约从 ~1.3 s 降到 ~0.6 s，且**减少 21 次可能被打断的机会**。
  3. 掩码写（0x16）用不上：本点表没有"只改某几位"的需求，不实现是对的（`PROTOCOL-REFERENCE.md:34` 也这么结论）。
- **判定**：2 **建议改**（有明确收益，但不是正确性问题）；1 **待现场确认**（若无值读不出再考虑）；3 符合。
- **修法**（批量合并的最小侵入做法，不改协议层）：
  - 在 `PointCatalog` 或新加 `PointGrouping.cs` 里对 `chosen` 先做"同 slave + 连续地址 + 同 mode + 同 count"的最长连续合并，形成 `ReadBlock{slave, address, count, Point[] members}`；
  - `Collector` 增加 `ReadBlock`，一次读回后按成员偏移切片再 `Decode`；
  - `MonitorEngine.cs:83` 的 `foreach(var p in group)` 改成 `foreach(var block in blocks)`。
  - 注意：合并后**单点失败会变成整块失败**，要保留"整块失败时回退逐点重读一次"的降级路径，否则可靠性反而下降。

---

## 2. 点模型

### P-01 40 点覆盖 / point-contract.json 与 protocol.json 谁为准

- **位置**：`protocol.json`（version 0.9，40 points，37 bound + 3 unbound）、`point-contract.json`、`Program.cs:23`、`tools/control-runner/Runner.cs:141`
- **现状**：
  - `protocol.json`：40 个 tag，其中 **3 个无 binding**（`ZU01V`「乘0.1为实际电压」、`ZI0001A`「乘0.001为实际电流」、`ZW1VA`「总视在功率」）。有 binding 的 37 个就是 `Configuration.Load` 实际返回的（`Protocol.cs:31` 过滤 `binding != null`）。
  - `point-contract.json`：37 条，字段与 `PointCatalog` 一一对应，**由 `--export-config` 从代码生成**（`Program.cs:23`），并被 `package.ps1:37` 打进发布包、被 `verify-package.ps1:49` 做哈希比对。
  - **重要**：全仓**没有任何代码读取 `point-contract.json`**（只有 `Runner.cs:141` 写、`Program.cs:23` 写、打包/校验脚本读写）。它是**派生产物**。
- **问题/风险**：
  1. 三点未绑定（`ZU01V/ZI0001A/ZW1VA`）与三个已绑定点（`ZU6682/ZI6682/ZW66822`）**语义重复**——前者带"×0.1/×0.001"的说明、后者是实际在读的点且**没有乘比例**。这正好印证 §0.1-A：**说明写在未绑定点上，实现落在已绑定点上，两边没接上**。
  2. 「谁为准」的答案是：**`protocol.json`（绑定）+ `PointCatalog.cs`（单位/模式/标签）双真相源，`point-contract.json` 是它们投影出来的产物**。但仓库里**checked-in 的 `point-contract.json` 会过期**：改了 `PointCatalog.cs` 而忘记 `--export-config`，打包后交付物里的点契约就是旧的，且 `verify-package.ps1` 只比"两个副本是否一致"，**不会发现它与代码不一致**。
- **判定**：1 **必须改**（明确删除或接管未绑定点）；2 **建议改**（加一致性校验）。
- **修法**：
  - 删除 `protocol.json` 里 3 个未绑定 tag，或把它们标记 `"binding": null, "note": "历史说明，未建点"` 并在 `PointCatalog` 里删掉对应条目（现在本来就没有条目，是安全的）。
  - 在 `--self-test`（`SelfTests.cs:24`）里加一条：重新序列化一份 contract 并与 `point-contract.json` 逐字节比对，不一致就 fail。这样 CI 能挡住"改了代码没导契约"。

### P-02 地址计算规则（力控 HRS(n+1) → 零基 n）

- **位置**：`protocol.json` 每条 `binding.forcecontrol_connection` 与 `address_zero_based`；`Protocol.cs:15`；`SelfTests.cs:28`
- **现状**：37 条全部满足 `address_zero_based = 连接项数字 - 1`（`HRF8193→8192`、`HRS2001→2000`、`HRF16385→16384`…）。这条规则**在数据层是自洽的**，代码不参与换算（直接读 `address_zero_based`），换算发生在生成 `protocol.json` 时。
- **问题/风险**：**温度块 6 个点的"点名数字"与"连接项数字"差距极大**，现场靠名字核对必然找错寄存器：

  | 点 | 点名里的数字 | 实际连接项 | 零基地址 |
  |---|---|---|---|
  | T0 | 2203 | `HRF126` | **125** |
  | D2206 | （点名即 2206） | `HRF128` | **127** |
  | T2 | 2205 | `HRF130` | **129** |
  | T3 | 2204 | `HRF132` | **131** |
  | T4 | 345 | `HRF134` | **133** |
  | T5 | 347 | `HRF136` | **135** |
  | （对照）T1 | 303 | `HRS303` | **302** ✅一致 |
  | （对照）D2202 | — | `HRS2203` | 2202 ✅一致 |

  旧分析文档已注意到这点（`串口通信协议-V0.9.md:96`："T0 的说明含 2203，但实际读取 125/126 两个寄存器"），但**只当成备注**。而 `SelfTests.cs:28` 的断言 `T0 address == 125` 只是把现状写死，**不构成正确性证据**（它无法区分"125 对"还是"2202 对"）。
- **判定**：**待现场确认**（且是本次审计里**最该优先确认**的一项）。
- **修法**：
  - 现场用通信诊断页（`DashboardForm.cs:137` 的"只读 Modbus 03 手动读取"）对 **同一物理量分别读 125/126 与 2202/2203**，看哪个是 25 ℃ 量级的 float。
  - 确认后**要么改地址、要么改标签**，并在 `PointCatalog.Add` 的 `alias` 里写清"点名数字 ≠ 寄存器号"，避免下一个人再踩。
  - 顺手把 `SelfTests.cs:28` 的断言改成"对每个点断言 `address==连接项-1`"，而不是硬编码某个点的期望值——现在这条断言实际上在保护一个可能错的常量。

### P-03 数据类型处理（float32 字节序 / int16 / 跨寄存器）

- **位置**：`Protocol.cs:111-131`（`Decode`）、`:132-135`（`DefaultMode`）
- **现状**：
  - float32：支持 `ABCD`（默认）、`CDAB`、`BADC`、`DCBA` 四种。实现是把 4 字节按 `order[]` 重排成小端再 `BitConverter.ToSingle`。默认 ABCD 是**线路大端**，正确。
  - `NaN`/`Infinity` 判为"非有限浮点值（核对字节序）"，`Number=null`（`SelfTests.cs:53` 有断言）。
  - 整数：先拼 16 位大端 word，再按 `UINT16` / `INT16` / `(short)/100` 解释；`RAW` 返回全部 word 文本。
  - 跨寄存器 float：靠 `register_count=2` + `Extract` 要求 `buffer[offset+2]==count*2`，一次读 2 个寄存器拼 4 字节，**实现正确**。
  - `SelfTests.cs:45-52` 对 4 种字节序、INT16/UINT16/候选值都有断言。
- **问题/风险**：
  1. **`DefaultMode` 只有 code 层在用，而 37 个点全部走 `PointCatalog` 的 Mode，`protocol.json` 的 `decode_proposal` 对已知点完全不起作用**（`PointCatalog.cs:67` 只在"其他测点"分支才回落到 `Modbus.DefaultMode(p)`）。于是出现了**同一含义的两个真相源**，且已经不一致：
     - `T1`：`protocol.json` 是 `int16_big_endian_signedness_pending`（符号待定），`PointCatalog.cs:21` 是 **`INT16`（有符号）** → 代码已经偷偷做了决定，把 -1 显示成 -1 是对的，但这是"猜的"，且文档没记录这个决定。
     - 其余 HRS 点是 `"U16 / I16"`（双解显示、不进趋势），`T1` 却是单解 `INT16`，**同类点处理不一致**，看不出依据。
  2. `Decode` 里 `"INT16 /100（候选）"` 是**魔法字符串**，全仓没有任何点用它（死代码），但 `SelfTests.cs:52` 还在测它。
  3. `U16 / I16` 模式返回的是**自然语言字符串**（`Protocol.cs:130`），下游要靠字符串切分解析（`DashboardForm.cs:222` 用 `IndexOf(" / I16=")` 切）。这属于把"展示"塞进了"解码"，一旦改文案就静默坏掉。
- **判定**：1 **必须改**（消歧）；2 **建议改**（删）；3 **建议改**。
- **修法**：
  - 把 `Mode` 设为**唯一真相**：`protocol.json` 增加 `"mode"` 字段（或直接让 `point-contract.json` 成为输入），`PointCatalog` 不再硬编码 Mode；最省事的过渡方案是在 `PointCatalog` 顶部加一段注释并让 `--self-test` 断言"`PointCatalog.Get(p).Mode` 与 `Modbus.DefaultMode(p)` 的兼容性"（`T1` 会立刻暴露）。
  - `PointCatalog.cs:21` 的 `T1` 改回 `"U16 / I16"`，或把 `protocol.json` 的 T1 改成 `int16_big_endian`（**待现场确认**哪个对）。
  - `Protocol.cs:130` 改成 `Modbus.Decode` 输出结构化结果（`o.Number` 用无符号值、`o.RawNumber` 用有符号值，另加 `o.ValueText` 给人看），`DashboardForm.cs:222` 不再切字符串。

### P-04 比例 / 偏移字段（**本次审计的核心问题**）

- **位置**：`PointCatalog.cs:9-10`（字段）、`:15-17`（`Add` 没有 scale 参数）、`:45-47`（三个直流点）；`MonitorEngine.cs:83`（已乘）；`Protocol.cs:323`（`RawNumber` 保留原值）
- **现状**：
  - `PointInfo` 有 `double Scale=1` 与 `bool ScaleConfirmed=true`。
  - `MonitorEngine.cs:83` 里 **`if(o.Number.HasValue)o.Number*=meta.Scale;` 已经接好**，而且顺序正确：`Collector` 先把未缩放值存进 `RawNumber`（`Protocol.cs:323`），engine 再乘 `Scale` 写 `Number`。**也就是说：机制完备，只是常数没填。**
  - `Add(...)` **没有 scale 形参**，因此 37 个点的 `Scale` 一律是默认 1；`PointCatalog.cs:45-47` 的 `ZU6682/ZI6682/ZW66822` 全是 1。
  - `ScaleConfirmed` 被 `confirmed` 形参赋值（`PointCatalog.cs:16` `ScaleConfirmed=confirmed`），**语义被混用**：它其实表示"解码可信度"，不是"比例已确认"。
- **问题/风险**：
  1. **权重最大的错值来源**。按 `PROTOCOL-REFERENCE.md:106-117`，直流表：电压 ×0.1、电流 ×0.001、功率 ×1。当前电压/电流**分别错 10 倍和 1000 倍**，而且**单位已经显示成 V / A**（`PointCatalog.cs:45-46`），也就是"错误的数 + 正确的单位"，比不显示单位更危险。
  2. 三个点还都是 **`UINT16`**（`PointCatalog.cs:45-47`）：如果直流侧出现负电流（反充）或负电压，`UINT16` 会读成 654xx 量级的巨值，再乘 0.001 仍是 65 A 的假值。**必须同时裁决符号性**。
  3. 交流侧（D1–D9、DU1–DU8）目前是 `float32`，理论无需比例；但 `PointCatalog.cs:30-32,39` 把 **"W · 倍率待核准" 这类说明文字放进了 `Unit` 字段**，这个字符串会进数据库 `unit` 列、进导出 Excel 的单位列、进趋势图分栏名与图例（`Charts.cs:33,81,85`），还会导致 `Charts` **按单位字符串分组**把 `D3` 单独分到一个"W · 倍率待核准"轴。
- **判定**：1 **必须改**；2 **待现场确认**（符号性）+ **建议改**（改成 `INT16` 并在文档标注）；3 **建议改**。
- **修法**：
  1. `PointCatalog.cs:15` 加 scale 形参并给三个直流点赋值：
     ```csharp
     // PointCatalog.cs:15
     static void Add(string id,string label,string group,string unit,string mode,
                     bool confirmed=true,string alias="",double scale=1){
         entries[id]=new PointInfo{Name=id,Label=label,Group=group,Unit=unit,Mode=mode,
             ScaleConfirmed=confirmed,Alias=alias,Scale=scale};
     }
     // PointCatalog.cs:45-47
     Add("ZU6682","太阳能直流电压","太阳能","V","INT16",false,"太阳能发电电压",0.1);
     Add("ZI6682","太阳能直流电流","太阳能","A","INT16",false,"太阳能电流",0.001);
     Add("ZW66822","视在功率","太阳能","VA","INT16",false,"太阳能总视在功率",1);
     ```
     `MonitorEngine.cs:83` **不需要改**（已正确）。改完记得跑 `--export-config` 重新生成 `point-contract.json`（否则见 P-01-2）。
  2. 把"倍率待核准"从 `Unit` 拆出去：`PointInfo` 增加 `string Note`，三处 `Unit` 改成 `"W"/"var"/"VA"`。这样趋势轴、导出、图例立刻变干净，且不丢信息（`Note` 进界面 tooltip）。
  3. 单位显示已经做了（见 D-01），不需要额外工作；**小数位**目前是全局 `"G6"/"0.###"`，见 D-02。

### P-05 `point-contract.json` / `protocol.json` 的重复与冲突

- **位置**：`protocol.json`（version/points/binding/decode_proposal/forcecontrol_connection）、`point-contract.json`（id/equipmentId/slave/addressZeroBased/registerCount/metadata）
- **现状**：两个文件携带**相同信息两次**：`slave`/`addressZeroBased`/`registerCount` 在两边都有；`decode_proposal` 只在 protocol.json，`metadata.Mode` 只在 contract。**实际运行时只读 protocol.json + 编译进 exe 的 PointCatalog**。
- **问题/风险**：
  1. 已发现的具体冲突：`protocol.json` 里 `T1.decode_proposal = int16_big_endian_signedness_pending`，`point-contract.json` 里 `T1.metadata.Mode = "INT16"` —— **同一文件对里就写着两个不同的答案**。同样地 `D2001` 在两处都是 2201（一致），`0006H` 在 contract 里 `Mode:"RAW"`、protocol 里 `raw_register_bit_selection_pending`（含义不同）。
  2. 外部消费方（云端/交接）如果读 `point-contract.json`，拿到的是"代码投影"，而 `protocol.json` 是"力控投影"，两者会随版本漂移。
- **判定**：**建议改**（现状可维护，但需要一条自动一致性检查）。
- **修法**：在 `--self-test` 里加 3 条断言：(a) 两份文件点集合相同；(b) slave/address/count 三元组逐点相同；(c) 每个 `decode_proposal` 与 `metadata.Mode` 的映射不矛盾（至少 `float32*` ↔ `FLOAT*`、`int16*` ↔ `U16 / I16|INT16|UINT16`）。跑不通就说明有人只改了一边。

---

## 3. 控制写入

### C-01 控制点地址 / 范围 / 步进

- **位置**：`control-config.json` targets；`ControlConfig.cs:93-127`（`Validate`）；`MonitorEngine.Control.cs:364-371`（`RangeProblem`）；`ControlPage.cs:218,235`
- **现状**：

  | 点 | 站号 | 地址 | kind | unit | min..max | step | confirmed/allowWrite |
  |---|---|---|---|---|---|---|---|
  | D2202 主机温度设定 | 2 | 2202 | int16 | ℃ | 0..60 | 1 | false / false |
  | D2001 模式控制 | 2 | **2201** | int16 | — | 0..10 | 1 | false / false |
  | D2000 送风风机速度 | 2 | 2000 | int16 | — | 0..100 | 1 | false / false |
  | D2207 放冷反馈设定 | 2 | 2207 | int16 | — | 0..100 | 1 | false / false |

  `Validate` 会强制 `target.slave/address` 与 `protocol.json` 一致（`ControlConfig.cs:119-120`），所以**改地址必须两边一起改**，这是好设计。
- **问题/风险**：
  1. **范围是猜的，且猜得很宽**：`D2202` 0..60 ℃（冷冻水设定写到 60 ℃ 会直接违反工况）、`D2000` 0..100（若真是 0–3 档位，写 100 大概率得到异常码 03，但万一风机按百分比解释就会全速）。`control-config.json:36,64,78` 的 note 已如实写明"无工程依据"。**范围闸门是目前唯一挡住误值的防线**，它本身建立在猜测上。
  2. `D2001` 地址 2201 vs 点名 2001：`control-config.json:50` 的 note 写得非常清楚（"⚠ 点名写的是 2001，但连接项是 HRS2202（零基 2201），疑为原工程笔误 D2201"），`README.md:113` 和 `CONTROL-DESIGN.md:300` 也都写了。**代码内部一致（2201），文档也一致，但 `PROTOCOL-REFERENCE.md` §6 第 3 条建议改成 2001，尚未裁决。**
  3. **模拟器与点表不一致**：`Protocol.cs:227` 的 `SimulationRegister` 与 `:265` 的 `SimulateDeviceReaction` 用的是 **case 2001**，而真实绑定是 **2201**。后果：模拟模式下写 D2001 会落到 `registers[Key(2,2201)]` 但**不会联动 D2210（当前模式）**，于是"设备已动作"这一步在模拟里**永远演示不出来**，也测不出反馈链路。
  4. `ControlConfig.Validate` 要求 `kind=="int16"`（`:121`），但 `PointCatalog` 里这 4 个点是 `"U16 / I16"`。两者不冲突（target 的 kind 只用于 UI 输入解析），但**同一个点有两个类型名**，容易误解。
- **判定**：1 **待现场确认**（并建议先收紧）；3 **建议改**；2 **待现场确认**；4 **建议改**。
- **修法**：
  - 未确认前把 `min/max` 收到"当前实测值 ± 1"或干脆 `min==max==当前读数`（写出去等于同值自检，`ControlConfig.cs:123` 会拒绝 `0..0`，所以用"当前读数..当前读数+1"更现实）；`control-config.json:32,46,60,74`（四个 `max` 字段）。
  - `Protocol.cs:227,265` 的 `case 2001` 改成 `case 2201`（两处），使模拟与点表一致；这样 `D2001 → D2210` 的联动才能在模拟里看到。
  - 两个类型名统一：`ControlTarget.kind` 改名为 `valueType` 并在 `Validate` 里接受 `"U16 / I16"`，或在 note 里说明"kind 只约束输入解析"。

### C-02 写入用 06 还是 10 / 16

- **位置**：`Protocol.cs:59-64`；`ControlConfig.cs:118`；`CONTROL-DESIGN.md:281`
- **现状**：只用 **06**。`Validate` 明确拒绝 `register_count != 1` 的点（`ControlConfig.cs:118`），所以**32 位/浮点控制点结构上就不可能**。与力控工程"勾选 06、未勾选 16"（`CONTROL-DESIGN.md:280`）一致。
- **问题/风险**：**无正确性问题**。若将来出现 float 型设定点（如温度设定是 float32），必须新写 0x10，届时"写后回读校验"要按 4 字节比较。
- **判定**：✅ 符合。

### C-03 写后回读校验 / 失败提示

- **位置**：`MonitorEngine.Control.cs:405-530`（`ExecuteOne`）、`:500-527`（观察窗 + 回读 + 判定）
- **现状**（这条链路做得相当完整）：
  1. 写前先 **03 预读**取旧值（`:419`），失败才退回采集快照（`:424-427`）；
  2. 发出 06，校验回显逐字节等于请求（`:490-498`），不等 → `unknown` + 上锁；
  3. 等 `verifyWindowMs`(1500 ms)（`:500-501`）；
  4. 03 回读；不一致则**再等一次窗口、再读一次**（`:503-507`）；
  5. 判定 `applied / not_accepted / mismatch / unverified`（`:521-527`）；
  6. 任一异常路径都 `MarkUncertain`（上锁 + 记录旧值/新值），并强制人工"重新读取核对"（`:331-348`）。
- **问题/风险**：
  1. **回读路径受风险 D 威胁**（见 §0.2 / R-05）：回读是 03 且**不带地址回显**，一个迟到的同站号同长度帧可以被误认为回读值，从而产生**假 `applied`**。这是控制链里最需要加固的点。
  2. `ReadBack`（`:575-585`）构造的是**临时 `Point`**，`decode_proposal="int16"` 但实际传 `mode="UINT16"`（`:583`）——两处不一致，只是当前恰好都用无符号。若将来 target 变成有符号语义（负设定值），这里会悄悄错。
  3. 没有"写同一值则跳过"的保护（见 C-05）。
- **判定**：1 **必须改（加固，同 R-05）**；2 **建议改**。
- **修法**：
  - 回读前额外做一次"发送前静默丢弃 + 时序闸门"，并把 `ReadbackRx` 与 `ReadbackTx` 的**时间差**记进审计（`ControlResult.ReadbackMs` 已有位），现场可据此判断是否可疑。
  - `MonitorEngine.Control.cs:581` 的 `decode_proposal="int16"` 改 `"int16_big_endian"`，并让 `:583` 的 mode 与 `target.kind` 联动（`UINT16`/`INT16`）。

### C-04 EEPROM / 写入频次保护 / 防重复写

- **位置**：`ControlConfig.cs:24`（`ControlLimits`）、`MonitorEngine.Control.cs:176-187`（`RateGateLocked`）、`:442-446`（记录写入时刻）
- **现状**：
  - 冷却 `minIntervalMs=1000`、每分钟上限 `maxWritesPerMinute=6`（默认值，`control-config.json:17-18` 也是这两个）。
  - 单飞：队列串行处理（`:373-403`），一次只有一个 06 在飞。
  - 频次表 `controlWriteTimes` **只在内存里**，进程重启即清空。
- **问题/风险**：
  1. **没有"值相同则跳过"**：可以对同一个寄存器做无限次同值写，每次都消耗一次 EEPROM/保持区写周期。`CONTROL-DESIGN.md:219` 甚至**建议**先做同值写自检——单次没问题，但界面上"发送"是行内按钮，反复点很容易连续写。
  2. 频次表不持久化：重启程序 → 立刻又能连写 6 次/分。对"有寿命的保持区"（无论它是 EEPROM 还是电池 RAM）这是缺口。
  3. `PROTOCOL-REFERENCE.md:71` 说"控制写入应保持人工单次，不要做定时/循环写"——代码侧没有**定时/自动写**路径（正确），但也没有**运维级**的写入配额（例如"单次会话最多 N 次写"）。
- **判定**：1 **建议改**；2、3 **建议改**（可选）。
- **修法**：
  - 在 `ExecuteOne` 的 `:437` 之前加：
    ```csharp
    if (oldValue.HasValue && oldValue.Value == command.Value && result.OldSource == "写前 03 预读") {
        result.Status = "noop"; result.Detail = "读数已等于目标值，未发送 06（避免无效写）"; 
        AppendControlEvent(result, store, roundPath); return result;
    }
    ```
    注意：`ControlPage` 的"同值自检"场景会被这条挡住，所以要么给自检单独一个 `allowSameValue` 开关，要么把 `noop` 视为成功（UI 上显示"已相同"）。
  - 把写入时刻落到 `logs/control-events.jsonl` 之外的小文件（如 `data/control-rate.json`），启动时加载并剔除 60 s 外的记录。

### C-05 确认流程 / 二次确认

- **位置**：`ControlConfig.cs:51`（`requireOperatorAck`）、`ControlConfirmForm.cs`（整个文件）、`MonitorEngine.Control.cs:218-233`（`ArmControl`）、`ControlPage.cs:681`
- **现状**：
  - `ControlConfirmForm` **全仓零处实例化**（`Get-ChildItem -Filter *.cs | Select-String ControlConfirmForm` 只命中自身定义）。是**死代码**。
  - `requireOperatorAck` **引擎不读**（`CONTROL-DESIGN.md:68` 已承认）。
  - 实际"确认"= 顶部"控制开关"按钮（`ControlPage.cs:681` 调 `SetControlEnabled`），加每行的"发送"按钮。**没有逐次确认弹窗**。
- **问题/风险**：`README.md:7` 明确承诺"再在页面里『解锁控制』并**逐次二次确认**"。现场按 README 操作时不会看到确认框，**会以为程序坏了**；更糟的是程序员的心理预期（"还有一道确认"）与实际不符。
- **判定**：**必须改**（二选一：接上确认框，或改文档）。
- **修法**（推荐接上，成本低）：
  - `ControlPage.cs` 的 `ExecuteWithCooldown`（`:519`）之前插入：
    ```csharp
    if (engine.ControlSettings.requireOperatorAck) {
        using (var dlg = new ControlConfirmForm("确认写入", 
            target.label + "（" + target.point + "）\r\n" + value + (String.IsNullOrEmpty(target.unit)?"":" "+target.unit), 
            "确认")) { if (dlg.ShowDialog(this) != DialogResult.OK) return null; }
    }
    ```
    注意这段必须在 **UI 线程**（`ExecuteWithCooldown` 目前跑在 ThreadPool 上，`:484`），所以确认框要挪到 `:470` 的循环里（提交前）。
  - 或者最小改动：改 `README.md:7` 为"解锁控制开关后逐行发送（无双次确认弹窗）"，并删掉 `ControlConfirmForm.cs`。

### C-06 门禁与联锁的"只提示不拦截"

- **位置**：`ControlConfig.cs:53-56`（默认 `false`）、`control-config.json:6-7`、`MonitorEngine.Control.cs:160-169`
- **现状**：`enforceInterlocks=false`、`requireConfirmed=false` 是**默认值也是随包值**。门禁仍然检查"连接/采集中/未单轮/有 target/未 unknown/已 armed/频次"，但**跳过**联锁（D2003 远程、D2208 无故障）与"现场已确认"。
- **问题/风险**：这本身是**合理的现场联调策略**（语义未裁决时别乱拦），但它与 §0.1-B 叠加：`mode=preview` + 不拦联锁 + 范围是猜的 ⇒ **一条几乎没有业务约束的写入通道**。文档（`CONTROL-DESIGN.md:204`）写得很清楚，但 README/ACCEPTANCE 的"默认 disabled"承诺让人以为还有一层。
- **判定**：**必须改**（把随包默认值改回 `disabled`，或在 README 首段加粗"随包是 preview、联锁仅提示"）。
- **修法**：`control-config.json:3` 改 `"mode": "disabled"`；`README.md:7` 与 `CONTROL-DESIGN.md:259` 的口径保持；把"preview 联锁仅提示"作为**显式的现场步骤 0** 写进 `CONTROL-DESIGN.md` §10。

### C-07 `D9` 与保留地址 `0x200C`

- **位置**：`protocol.json`（D9：slave 6，addr 8204，count 2，`HRF8205`）；`PointCatalog.cs:43`；`PROTOCOL-REFERENCE.md:91`
- **现状**：D9 绑 **8204 = 0x200C**，按 DDSU666 手册这是**保留地址**；`PointCatalog` 已把它标为「原始指标8204」，`ScaleConfirmed=true`（意味着"解码可信"，其实读的是保留地址）。`DU6` 绑 8202 标「原始指标8202」。
- **问题/风险**：
  1. 读保留地址的响应**未定义**：可能是 0、可能是 float32 垃圾、也可能返回异常码 02。当前一律按 float32 解码并计入"正常"质量。
  2. **风险被低估**：`PROTOCOL-REFERENCE.md:99` 说"界面显示『—』就是这个原因"——但代码里只有 `Quality != good` 才显示 `—`（`PointCatalog.cs:69`）。如果设备对保留地址不返回异常，而是返回 0，界面就会显示 **"0"（一个看起来正常的读数）**，比 `—` 危险得多。
  3. `PointCatalog.cs:43` 的 `ScaleConfirmed=true` 与"待现场裁决"矛盾。
- **判定**：**必须改**（至少把 `ScaleConfirmed` 改 false 并在 UI 上打标；最好删点）。
- **修法**：
  - 最省事：`PointCatalog.cs:43` 改 `Add("D9","保留地址8204（0x200C）","市电","","FLOAT ABCD",false,"功率因数（力控点表；待现场裁决）")`，并在 `OverviewPage`/`DashboardForm` 里对 `Group=="诊断"` 之外再加一个"未裁决"视觉标记。
  - 或者从 `protocol.json` 删掉 D9 绑定（点数 37→36，需同步 `SelfTests.cs:25` 的 37 断言、README:5、ACCEPTANCE 的"37 个绑定测点"）。

---

## 4. 展示层取值

### D-01 单位显示

- **位置**：`OverviewPage.cs:87-101`（卡片单位标签）、`:105`（tooltip 含站号/地址）、`:89`（`"原始码"/"原始值"` → 显示为空）；`DashboardForm.cs:121`（表格"单位"列）、`:118`（初始化填 `i.Unit`）；`Charts.cs:33,81,85`（按单位分组、轴标注"单位：xxx"或"原始值"）；`HistoryPage.cs:160,245`（统计表"单位"列、`p.Unit`）
- **现状**：**单位是全面显示的**（总览卡片、明细表格、趋势轴、图例 tooltip、统计表、导出）。空单位/原始码被正确降级为"原始值/空白"（`OverviewPage.cs:89`、`Charts.cs:85`）。`UiLayoutTests.cs:276` 还有"单位显示与点表一致"的断言。
- **问题/风险**：显示机制没问题，**问题在数据**：`PointCatalog` 把"倍率待核准"塞进 `Unit`（`PointCatalog.cs:30-32,39`），导致 `Charts.cs:81` 的 `GroupBy(Unit)` 把 `D3` 分成一个独立轴，轴标题变成 **"单位：W · 倍率待核准"**——这行字会进客户截图的 PPT。且导出 Excel 的单位列同样带这串话。
- **判定**：**建议改**（数据层面，见 P-04-3）。
- **修法**：见 P-04 第 3 点（`Unit` 只放符号，说明进 `Note`）。

### D-02 数值格式 / 小数位

- **位置**：`DashboardForm.cs:221`（`DisplayNumber`，`"G6"`，特殊处理 0）、`:222`（`DisplayCardValue`，切 `" / I16="`）；`PointCatalog.cs:70`（`"0.###"`）
- **现状**：
  - 主显示路径是 `"G6"`（6 位有效数字）；`PointCatalog.Display` 用 `"0.###"`（**但这个方法是死代码，全仓无调用**）。
  - 状态点显示两行 `U16=60 / I16=60`（`DashboardForm.cs:222` 从字符串切）。
- **问题/风险**：
  1. **`G6` 在累计电能上会变成科学计数法**：`D8/DU8` 是 kWh float32，跑几天就是 5–6 位数；超过 6 位有效数字时 `ToString("G6")` 输出 `1.23457E+06` 这种，现场/导出都很难看，且**丢精度**。
  2. 直流表乘 0.001 后（P-04 修完），电流需要 4 位小数才看得出变化，`G6` 在 0.001 量级还能应付（3–2 位有效），但**没有按点配置小数位的机制**。
  3. `PointCatalog.Display` 是死代码，容易让后来者以为显示走的是 `"0.###"`。
- **判定**：**建议改**。
- **修法**：
  - `PointInfo` 加 `int Decimals`（默认 -1 表示用 G 格式），`DisplayNumber` 改为：
    ```csharp
    var info = PointCatalog.Get(o.Point);
    if (info.Decimals >= 0 && o.Number.HasValue) return o.Number.Value.ToString("F" + info.Decimals, CultureInfo.InvariantCulture);
    double v = o.Number.Value;
    if (Math.Abs(v) >= 1e6) return v.ToString("N1", CultureInfo.InvariantCulture); // 累计电能不转科学计数法
    ... // 原 G6 逻辑
    ```
  - 删掉 `PointCatalog.Display`，或让它成为唯一格式化入口（一并被 UI 调用）。

### D-03 状态点不产出数值（趋势里的洞）

- **位置**：`Protocol.cs:130`（`U16 / I16` → `Number=null`）、`TrendSeriesLoader.cs:20`（`if(!value.HasValue && row.RawNumber.HasValue && Mode!="U16 / I16") value=row.RawNumber;`）
- **现状**：`U16 / I16` 的点**永远没有数值列**；趋势加载器还**显式排除**它们回落到 `RawNumber`。所以 `D2000/D2001/D2003/D2208/D2209/D2210/D2211/D2212/D2213` 在趋势里是"无有效数值（null）"。
- **问题/风险**：
  1. 这是**刻意的设计**（`SelfTests.cs:51` 有断言"未知单位不产生数值趋势"），避免把原始码当物理量画曲线。**方向正确**。
  2. 但它把"状态点"也一起牺牲了：D2209 风机状态 0/1、D2208 总故障 0/1 是**最需要看时间序列的点**（现场要看"什么时候报的故障"）。现在记录页有值、趋势页没有。
  3. 控制侧的 `ControlNumericValue`（`MonitorEngine.Control.cs:132-145`）**已经能**从大端原始字节取无符号值，说明"有数值视图"这件事技术上已经具备，只是没接到趋势。
- **判定**：**建议改**。
- **修法**：给 `PointInfo` 加 `bool NumericRaw`（状态点/原始码为 true），`Collector` 对这类点把 `Number` 设为无符号原值、`Value` 保留双解文本；`TrendSeriesLoader.cs:20` 的排除条件改成 `PointCatalog.Get(row.Point).NumericRaw`。这样既不误会物理量，又能看状态时间线。

### D-04 电表点专用换算

- **位置**：`PointCatalog.cs:28-43`；`MonitorEngine.cs:83`
- **现状**：交流表（D1–D9/DU1–DU8）全部 float32、`Scale=1`。**没有额外换算**（也不需要——表本身给 float32 工程值）。直流表见 P-04。
- **问题/风险**：唯一缺的是直流表比例（P-04），以及 `D3/D4/D5/DU3` 的"倍率待核准"未裁决（`PointCatalog.cs:30-32,39` 的 `ScaleConfirmed=false`）。注意：如果 DS666/DDSU666 实际给的是"二次侧值"需要乘 CT/PT 变比，那 `ScaleConfirmed=false` 就是正确的保守标记，**但界面没有任何"倍率未核准"的视觉提示**（只有 Unit 字符串里那行字）。
- **判定**：**待现场确认** + **建议改**（视觉标记）。
- **修法**：`OverviewPage` 的卡片在 `!info.ScaleConfirmed` 时给数值加一个"待核准"角标（或黄色圆点），比塞在单位字符串里可靠。

---

## 5. 文档一致性

| # | 文件:行 | 文档说 | 代码/配置实际 | 判定 | 修法 |
|---|---|---|---|---|---|
| M-01 | `README.md:7,105`、`CONTROL-DESIGN.md:205,259`、`ACCEPTANCE.md:17` | "默认 `control-config.json` 为 `mode=disabled`…不会发送任何写入" | 随包 `control-config.json:3` = **`preview`**，`enforceInterlocks/requireConfirmed` 均 `false` | **必须改** | 二选一：把 `control-config.json:3` 改回 `disabled`（推荐，与所有文档一致），或把这 5 处文档改成"随包为 preview、联锁仅提示" |
| M-02 | `README.md:25` | "设备通信使用 Modbus RTU 功能码 03，**只读寄存器**" | 0.7.0 起有 06 写入（同文件 `:3` 已写）；`Protocol.cs:59` | **必须改** | 改为"读用 03；写用 06 单寄存器，默认关闭" |
| M-03 | `README.md:7` | "解锁控制"并"**逐次二次确认**" | `ControlConfirmForm` 从未实例化；`requireOperatorAck` 不被读取 | **必须改** | 接上确认框（见 C-05）或删掉该承诺 |
| M-04 | `README.md:89`、`CONTROL-DESIGN.md:198,207` | 设定表"**勾选**"控制点、点"**发送勾选点**"、"**本点发送**"；表格列含"**采样时间**""**上次写入**" | `ControlPage.cs:93-100` 实际只有 8 列：点名/名称/当前值/设定值/单位/范围/最近结果/操作，**无勾选列、无采样时间、无上次写入**；按钮是「发送全部已修改」（`:77`）+ 行内操作 | **必须改** | 按 `CHANGELOG.md:5,8` 的现状重写这三段，并删掉不存在的两列 |
| M-05 | `CONTROL-DESIGN.md:201`、`:219` | "预演：调用 `PreviewControl`…时间线填到第②步"；现场步骤 5"先在 `mode=preview` 下**预演核对报文**" | `PreviewControl`（`MonitorEngine.Control.cs:260`）**无 UI 入口**；`CHANGELOG.md:10` 明确"删除预演按钮" | **必须改** | 步骤 5 改成"用通信诊断页手动 03 读该地址 + 对照 `Modbus.WriteSingle` 生成的报文"，或给"预演"加回一个只读按钮 |
| M-06 | `ACCEPTANCE.md:28`、`CONTROL-DESIGN.md:244` | `ControlTests` **51** 项 | 实际 `Check(` **61** 处；`CHANGELOG.md:25` 又写 **60** | **必须改** | 三处统一为 61（或改成"≥60，以 `--test-control` 输出为准"，避免每次加测试又漂） |
| M-07 | `CONTROL-DESIGN.md:195,206` | 示例显示 `COM10 · 9600 8N1` | `settings.json` 是 COM10，但现场是 COM9、`Program.cs:121` 默认 COM9 | **建议改** | 文档示例改 `COM9` |
| M-08 | `CONTROL-DESIGN.md:68` | `requireOperatorAck`「声明『写入需操作者确认』…引擎未读取」 | ✅ 与代码一致 | ✅ | — |
| M-09 | `CONTROL-DESIGN.md:22,110,140` | `mode=disabled` 是强制关闭、`preview` 只是允许解锁 | ✅ 与 `ControlConfig.cs:94`、`MonitorEngine.Control.cs:153` 一致 | ✅ | — |
| M-10 | `ACCEPTANCE.md:21-25` | 2026-10-04 的 run ID、4161/4168 项、49 张截图 | 这些是历史记录，本次未复核（禁止跑构建） | **待现场确认** | 建议在 `ACCEPTANCE.md` 顶部加"本节为历史快照，最新结果以 `test-output/` 与 `*-test-result.json` 为准" |
| M-11 | `PROTOCOL-REFERENCE.md:71` | D128–D7999 是 **EEPROM** | FX3U 保持区是电池后备 RAM（EEPROM 是程序区） | **建议改** | 改成"保持区（电池后备），频繁写仍有寿命/磨损风险"；结论（别循环写）保留 |
| M-12 | `PROTOCOL-REFERENCE.md:46,137` | "D n ↔ 地址 n" 与"地址推导成立" | 规则成立，但**温度块 6 点的点名数字与连接项无关**（见 P-02） | **必须改** | 在 §1.2 后补一节"点名数字 ≠ 寄存器号"的对照表，并把 `T0/T2/T3/T4/T5/D2206` 列进去 |
| M-13 | `PROTOCOL-REFERENCE.md:140` | "D2001 → 应在 2001，配置 2201，**✗ 需改**" | 代码与全部配置仍统一 2201，且 `control-config.json:50`、`README.md:113`、`CONTROL-DESIGN.md:300` 都主张"以 protocol.json 的 2201 为准" | **待现场确认**（文档内部自相矛盾，必须收敛到一个结论） | 改成"待裁决：两种假设并列，现场按 2001 与 2201 各读一次由 PLC 值裁决" |
| M-14 | `README.md:5` | "37 个绑定测点" | ✅ 与 `protocol.json`（37 bound）、`SelfTests.cs:25` 一致 | ✅ | — |
| M-15 | `README.md:67` | 导出排除 0006H 与"运行状态"分组 | ✅ `ExperimentExport.cs:31` 排除 `0006H` + `运行状态` + **`控制/设定`**（文档漏了第三类） | **建议改** | 补上 `控制/设定` |

---

## 6. 测试覆盖

### T-01 现有覆盖（做得好的部分）

- **协议字节层很扎实**（`SelfTests.cs:24-67`，`--self-test` 会跑）：
  - `:26` CRC 参考帧 `01 03 00 00 00 0A C5 CD`
  - `:27` 现场帧 `03 03 20 00 00 02 CE 29`
  - `:32-36` 逐字节喂入，**不允许中间某步接受半帧 CRC**
  - `:37-40` 噪声 + 坏 CRC + 错站号 + 粘帧的重同步
  - `:41` 字节数错误被拒
  - `:42-43` 异常帧 **校验 CRC 后**才接受
  - `:45-53` 4 种 float 字节序 + INT16/UINT16/候选/Infinity
  - `:54-62` `Collector` 端到端：正常 / CRC 错不出值 / 异常码不出值 / **超时绝不补 0**
  - `:63-66` 取消传播
- **控制链路 61 项**（`ControlTests.cs`）：FC06 帧/CRC、噪声中提取回显、`disabled/preview/not_armed/range/interlock_*/not_acquiring`、`applied`+审计、`unknown`→再写被 `uncertain` 拦、`verify_not_applied` 解锁、`not_accepted`、`rejected(0x86)`、`rate`、`Validate` 失败。
- **UI**：`UiLayoutTests.cs:276` 断言"单位显示与点表一致"；`:287` 断言通信故障保留旧值并标记；`ExtendedTests.cs:41,49` 断言单站故障不阻塞其它站。
- `StorageTests/RecordsTests/ExportFailureTests/WorkflowTests/PageWorkflowTests/TrendTests/TrendTimeframeTests` 与协议无关（记录/导出/导航）。

### T-02 缺口（应补的用例）

| # | 应补的测试 | 断言什么 | 为什么重要 |
|---|---|---|---|
| T-02-1 | `Scale` 端到端 | 给 `ZU6682` 喂 1592 → `o.Number==159.2` 且 `o.RawNumber==1592` | 当前 **0 个测试**覆盖比例，P-04 改完后没有回归网 |
| T-02-2 | 点表地址不变量 | 对**每个**点断言 `address == 连接项数字-1`（而不是写死 T0==125） | 现有 `SelfTests.cs:28` 把可能错的常量钉死了 |
| T-02-3 | 陈旧帧不被接受 | 先喂一帧 D2209（slave2/count1）的响应，再发起 D2202 的 03 读，断言回读**不是** D2209 的值 | 风险 D 的护栏，改完 `minFirstByte` 才有测试 |
| T-02-4 | 回读误判防护 | 模拟"写成功但回读收到上一个寄存器的值"，断言不出现 `applied` | 假 `applied` 是最坏的错误方向 |
| T-02-5 | `currentTimeoutMs` 单位 | 以 `period=2, timeout=8` 调 `ExecuteControl`，断言 `waitMs >= 2*1000 + 8*1000*2 + 8000` | C-01-1 的 bug 没有任何测试能发现 |
| T-02-6 | D2001 模拟联动 | 模拟写 2201 后断言 D2210 == 写入值 | 现在 `case 2001` 写错，测不出来 |
| T-02-7 | 未绑定 tag | 断言 `points.Count==37` 且 3 个 `ZU01V/ZI0001A/ZW1VA` **不在** points 里 | 防止有人把说明当点加回来 |
| T-02-8 | `protocol.json` ↔ `point-contract.json` | 见 P-05（点集/三元组/Mode 映射三条一致性断言） | 双真相源漂移 |
| T-02-9 | 点表 Mode 一致性 | `T1` 的 `PointCatalog.Mode` 与 `Modbus.DefaultMode` 不矛盾 | 抓 `int16` vs `U16 / I16` 的分歧 |
| T-02-10 | 状态点趋势 | 断言 `D2208` 在 `TrendSeriesLoader` 里有数值（D-03 修完后） | 现在状态点静默丢序列 |
| T-02-11 | 大数格式化 | `DisplayNumber` 对 1234567 kWh 不输出科学计数法 | D-02 |
| T-02-12 | 随包默认值 | 读仓库里的 `control-config.json`，断言 `mode=="disabled"`（M-01 修完后） | 文档承诺"默认 0 字节 TX"应被测试钉住，`CONTROL-DESIGN.md:246` 自己也这么要求 |

---

## 7. 风险清单（按严重度排序）

| # | 风险 | 触发条件 | 后果 | 修法 | 判定 |
|---|---|---|---|---|---|
| **1** | 直流表比例缺失 | 任何一次太阳能采集 | 电压 ×10、电流 ×1000**系统性错值**，趋势/导出/云端全错，且单位显示正确让人不会怀疑 | P-04 修法（`PointCatalog.cs:15,45-47`） | **必须改** |
| **2** | 随包 `mode=preview` + 联锁/确认不拦 + 范围是猜的 | 现场点"控制开关"再点"发送" | **真的会写 PLC**；D2202 可写到 60 ℃、D2000 可写到 100 | `control-config.json:3` 改 `disabled`；范围收紧（C-01） | **必须改** |
| **3** | 写等待预算单位错 | 采集周期 ≥2 s、写+回读耗时正常偏慢 | 合法写入被判 `unknown` → **误上锁**，现场误判 PLC 故障，反复解锁 | `MonitorEngine.Control.cs:303` 改成 `*1000`（见下方片段） | **必须改** |
| **4** | D9 读保留地址 `0x200C` | 每次采集 | 设备若对保留地址返回 0，界面显示 **"0"**（看起来正常）；若返回垃圾 float 则显示荒谬值。`ScaleConfirmed=true` 还放大可信度 | C-07（改标签+`ScaleConfirmed=false`，或删点） | **必须改** |
| **5** | 03 响应无地址回显 → 陈旧帧被误认 | 一次超时后紧跟一次同站号同长度读；最坏落在控制回读窗口 | 测量点瞬间错值（下一轮会纠正）；**控制回读可能假 `applied`**（不可纠正） | R-05/R-07 的 `minFirstByte` 时序闸门 | **必须改（加固）** |
| **6** | 温度块地址疑云（点名 2203 vs 寄存器 125） | 第一次现场核对 | 现场按名字找寄存器必然找错；若 125 其实是别的量，全部温度测点错 | P-02 现场双读裁决 | **待现场确认** |
| **7** | ZI6682 地址 3 vs 手册 0x0001 | 每次采集 | 电流读数可能来自错误寄存器 | P-02/`PROTOCOL-REFERENCE.md:116` | **待现场确认** |
| **8** | `D2001` 2201 vs 2001 | 一旦允许写 D2001 | **写错寄存器**（D2201 可能是别的量）；力控工程笔误会被原样继承 | 现场读 2001/2201 裁决；确认前保持 `allowWrite=false` | **待现场确认** |
| **9** | 直流表 `UINT16` 符号 | 出现反充/负电流 | 655xx/1000 = 65 A 假值 | P-04 改 `INT16` 或现场裁决 | **待现场确认** |
| **10** | 批量读未合并 | 常态 | 每轮 37 次事务，通信窗口被拉长，遇噪声的概率升高；轮间隔被压缩 | R-08 合并为 16 次 + 失败降级回读 | **建议改** |
| **11** | 串口无锁护栏 | 未来有人在 UI 线程碰 `connectedTransport` | 半帧残留污染下一轮读（当前靠约定不成立） | R-02-2 `lock(io)` | **建议改** |
| **12** | 同值重复写无抑制 | 现场反复点"发送" | 保持区写周期被无谓消耗 | C-04 `noop` 保护 | **建议改** |
| **13** | 状态点在趋势里消失 | 查看 D2208/D2209 历史 | 无法回溯"什么时候报的故障" | D-03 | **建议改** |
| **14** | 累计电能显示科学计数法 | kWh > 1e6 | 记录页/导出出现 `1.23457E+06` | D-02 | **建议改** |
| **15** | `point-contract.json` 过期 | 改代码未重导 | 交付的契约与程序不符，`verify-package` 查不出 | P-01-2 自测断言 | **建议改** |

---

## 附录 A：「必须改」清单（可直接照做）

| 序 | 文件:行 | 改成什么 |
|---|---|---|
| A1 | `PointCatalog.cs:15-17` | `Add` 增加 `double scale=1` 形参并写进 `Scale` |
| A2 | `PointCatalog.cs:45` | `Add("ZU6682",…,"INT16",false,"太阳能发电电压",0.1)` |
| A3 | `PointCatalog.cs:46` | `Add("ZI6682",…,"INT16",false,"太阳能电流",0.001)` |
| A4 | `PointCatalog.cs:47` | `Add("ZW66822",…,"INT16",false,"太阳能总视在功率",1)` |
| A5 | `PointCatalog.cs:30-32,39` | `Unit` 只留 `"W"/"var"/"VA"`，把"倍率待核准"移到新字段 `Note`（或 alias） |
| A6 | `PointCatalog.cs:43` | D9 标签改「保留地址8204（0x200C）」，`confirmed` 改 `false` |
| A7 | `control-config.json:3` | `"mode": "disabled"`（与 README/CONTROL-DESIGN/ACCEPTANCE 一致） |
| A8 | `control-config.json:31-32,45-46,59-60,73-74` | `min`/`max` 收到现场实测值附近（确认前建议 `min=当前读数, max=当前读数+1`） |
| A9 | `MonitorEngine.Control.cs:303` | `int timeoutMs = timeoutSeconds * 1000;` |
| A10 | `MonitorEngine.Control.cs:306` | 预算里补上观察窗：`+ (long)(verifyWindowMs)*2L + 8000L`（`verifyWindowMs` 从 `controlSettings.limits` 取） |
| A11 | `Protocol.cs:195-212` | 加 `minFirstByte` 时序闸门（丢弃早于请求发送完毕的字节）——同时消解风险 5 |
| A12 | `Protocol.cs:227,265` | `case 2001` → `case 2201`（两处），让模拟与点表一致 |
| A13 | `ControlPage.cs`（`:470` 循环内） | 发送前按 `requireOperatorAck` 弹 `ControlConfirmForm`；或删 `ControlConfirmForm.cs` 并改 README |
| A14 | `README.md:25` | 改成"读 03 / 写 06，写默认关闭" |
| A15 | `README.md:7` | 删除"逐次二次确认"或按 A13 实现后保留 |
| A16 | `README.md:89`、`CONTROL-DESIGN.md:198,207` | 按"发送全部已修改 + 行内发送、8 列（无采样时间/上次写入）"重写 |
| A17 | `CONTROL-DESIGN.md:201,219` | 删除"预演按钮"相关描述（`PreviewControl` 已无 UI 入口） |
| A18 | `ACCEPTANCE.md:28`、`CONTROL-DESIGN.md:244`、`CHANGELOG.md:25` | 控制测试项数统一为 61（或改成"以 `--test-control` 输出为准"） |
| A19 | `PROTOCOL-REFERENCE.md:46-48` | 补"点名数字 ≠ 寄存器号"章节，列出 `T0/T2/T3/T4/T5/D2206` |
| A20 | `PROTOCOL-REFERENCE.md:140` 与 `CONTROL-DESIGN.md:300` | 二选一收敛，不要一边写"需改 2001"、一边写"2201 正确" |

## 附录 B：「待现场确认」清单（按建议顺序）

1. **温度块地址**：对 `T0` 同时读 125(HRF126) 与 2202(HRF... 点名 2203)，看哪个是 25 ℃ 量级 float；`T2/T3/T4/T5/D2206` 同理。（用通信诊断页，只读，零风险。）
2. **直流表**：`ZU6682` 读 0 与 `ZI6682` 读 3，与表显对照；确认电流是 0x0001 还是 0x0003；确认是否负值。
3. **D2001**：分别读 2001 与 2201，看哪个是模式码；确认前 `allowWrite` 保持 `false`。
4. **D2000**：风机是"档位 0–3"还是"百分比 0–100"（写 1/2/3 后看 D2209）。
5. **D9/0x200C**：读 8204，看返回是异常码、0，还是 float。据此决定删点还是改标签。
6. **D2003 / D2208 的 0/1 枚举**：决定 `remoteExpected`/`faultExpected` 的取值，之后才能开 `enforceInterlocks=true`。
7. **`0x00010000` 类型标志**：是否与写权限相关（`CONTROL-DESIGN.md:301` 结论是无关，但现场可顺手验一次写只读点看回什么异常码）。

## 附录 C：三处关键修复的完整片段

### C.1 直流表比例（A1–A4）

```csharp
// ExperimentMonitorDemo/PointCatalog.cs
static void Add(string id,string label,string group,string unit,string mode,
                bool confirmed=true,string alias="",double scale=1){
    entries[id]=new PointInfo{Name=id,Label=label,Group=group,Unit=unit,Mode=mode,
        ScaleConfirmed=confirmed,Alias=alias,Scale=scale};
}

// 太阳能 DJSF6682：手册给出 ×0.1 V / ×0.001 A / ×1 VA
Add("ZU6682","太阳能直流电压","太阳能","V","INT16",false,"太阳能发电电压",0.1);
Add("ZI6682","太阳能直流电流","太阳能","A","INT16",false,"太阳能电流",0.001);
Add("ZW66822","视在功率","太阳能","VA","INT16",false,"太阳能总视在功率",1);
```
`MonitorEngine.cs:83` 的 `o.Number*=meta.Scale;` **保持原样**；`Protocol.cs:323` 的 `o.RawNumber=o.Number;` 在缩放之前执行，所以原始值仍然完整落库（`raw_number` 列）。改完必须跑一次 `--export-config` 重新生成 `point-contract.json`。

### C.2 写链路等待预算（A9–A10）

```csharp
// ExperimentMonitorDemo/MonitorEngine.Control.cs:301-306
int timeoutSeconds, period, verifyWindow;
lock (sync) { timeoutSeconds = currentTimeoutMs; period = periodSeconds; }   // currentTimeoutMs 实际是「秒」
verifyWindow = controlSettings == null || controlSettings.limits == null ? 1500 : controlSettings.limits.verifyWindowMs;
int timeoutMs = timeoutSeconds * 1000;                                        // ← 关键修复
long waitMs = Math.Min(180000L,
    Math.Max(20000L,
        (long)period * 1000L                     // 等下一轮开始
      + (long)timeoutMs                         // 一次 06 写超时
      + (long)verifyWindow * 2L                 // 两次观察窗（内层可能二次回读）
      + (long)timeoutMs * 2L                    // 两次 03 回读超时
      + 8000L));                                // 余量
```
顺带把字段名 `currentTimeoutMs` 改成 `currentTimeoutSeconds`（`MonitorEngine.cs:67,76` 及所有引用），否则下一个人还会踩同一个坑。

### C.3 陈旧帧时序闸门（A11）

```csharp
// ExperimentMonitorDemo/Protocol.cs  SerialTransport
readonly SerialPort port;
readonly int baud;                     // ← 新增字段，构造函数里 this.baud = baud;

public byte[] Read(int slave, int address, int count, int timeoutMs, CancellationToken token, Action<string> log) {
    token.ThrowIfCancellationRequested();
    WaitForQuiet(token, log);
    var scratch = new byte[256];
    var tx = Modbus.Request(slave, address, count);
    var watch = Stopwatch.StartNew();
    log("TX " + Modbus.Hex(tx)); port.Write(tx, 0, tx.Length);
    // 一个字节都要发 10 bit；任何早于「请求发完」到达的字节必然是上一笔的残留。
    long minFirstByte = (long)Math.Ceiling(tx.Length * 10.0 / baud) + 1;
    long firstByteAt = -1;
    var input = new List<byte>();
    while (watch.ElapsedMilliseconds < timeoutMs) {
        token.ThrowIfCancellationRequested();
        try {
            int n = port.Read(scratch, 0, scratch.Length);
            var chunk = scratch.Take(n).ToArray();
            if (firstByteAt < 0) firstByteAt = watch.ElapsedMilliseconds;
            if (firstByteAt < minFirstByte) {
                log("丢弃早于请求结束的陈旧字节 " + Modbus.Hex(chunk));
                input.Clear(); firstByteAt = -1; continue;
            }
            log("RX " + Modbus.Hex(chunk)); input.AddRange(chunk);
            byte[] frame;
            if (Modbus.Extract(input, slave, count, out frame)) return frame;
        } catch (TimeoutException) { }
    }
    throw new TimeoutException("等待有效响应超时；接收缓存 " + Modbus.Hex(input.ToArray()));
}
```
`WriteSingle`（`Protocol.cs:178-194`）同样处理：0x86 与 06 回显都带地址，被误认的风险本来就低，加上闸门可让审计里的 `Rx` 时间更可信。

---

## 附录 D：本次审计未能覆盖的部分

- **未运行任何构建或测试**（按任务约束），因此 §6 的"实际 61 项"是**静态计数**（`Select-String "Check\("`），不是运行结果。
- **未核对 DDSU666 / DJSF6682 / FX3U 原始手册**，只依据 `PROTOCOL-REFERENCE.md` 的转述与旧分析文档；`PROTOCOL-REFERENCE.md` 自身标注"部分未核实"。
- **未审计云端链路**（`CloudPublisher/CloudRealtime`）对 `Unit`/`Number` 的消费方式；若 `Unit` 字符串按 P-04-3 改写，需另做一次云端快照字段核对。
- **未审计 `ExperimentStore` 的 `raw_number`/`number` 双列语义**是否与 C.1 的缩放顺序完全一致（本报告只按 `Protocol.cs:323` 与 `MonitorEngine.cs:83` 的调用顺序推断）。
---

## 附录 E. Lead 落地记录与勘误（2026-11-06 18:25）

审计完成后，Lead 在 0.7.1 中已落地以下条目（其余按报告建议保留，待用户决策/现场确认）：

| 审计条目 | 处理 |
|---|---|
| A1 直流表 `Scale` 未落地（P-04） | **已修**：`PointCatalog.Add(..., double scale)`，ZU=0.1 / ZI=0.001 / ZW=1；`SelfTests` 新增 4 条回归 |
| A2 功率单位 W/var/VA（D-01） | **已修**：D3/D4/D5/DU3 → kW/kvar/kVA（力控运行截图 + 2026-10-03 抓包双证） |
| A3 DU6/D9 语义（C-07） | **已修**：DU6 → 市电功率因数；D9 → 保留(0x200C)、`ScaleConfirmed=false` |
| A9/A10 写等待预算单位（C-03） | **已修**：`timeoutSeconds*2000L` |
| D9 `ScaleConfirmed=true` | **已修**（同上，置 false） |
| 附录 A 其余 15 条 | 未改：D2001 地址、陈旧帧时序闸门、批量读合并、文档口径（control mode）、测试补充等，见 `PROTOCOL-REFERENCE.md` §5.3 |

**勘误 1（重要）**：本报告"待现场确认 ③ ZI6682 读 3 而手册是 0x0001"——**这条是错的**。
`meters.md` 用表格地址列 + 每行示例帧 `Addr 04 00 03 00 01` 双重确认：**电流就在 0x0003**，
项目配置正确，不需要"按 0x0001 试读"。

**勘误 2**：本报告写作时 `control-config.json` 的 `mode=preview` 被列为"最严重缺陷 #2"。
实际这是用户 2026-11-06 16:34 现场测试写入（日志有 D2000=1/2/50 的 applied 记录）时的使用状态，
不是事故；未擅自改回 `disabled`，只把 README/CONTROL-DESIGN 的口径差异列为待办。

**勘误 3**：本报告"温度块 6 点点名数字与寄存器严重不符"——需区分两套表示：
点名里的 2203/2205/… 是 PLC 内部**整数结果字**，标签实际读的是**浮点结果字**（125/129/…），
两者并存不是错误；现场按点名找会找错，所以标签用 T0–T5 而不是 D2203。
