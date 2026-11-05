# 运行控制（Modbus 06 下置）设计与现场启用

本文对应源码 `MonitorEngine.Control.cs`、`ControlConfig.cs`、`ControlPage.cs`、`ControlConfirmForm.cs`、`Protocol.cs`（`Modbus.WriteSingle` / `ExtractWriteEcho` / `IWriteTransport`）、`ExperimentStore.cs`（`control_events`）、`PointCatalog.cs`。适用于 0.7.0 起的控制写入链路。

- 0.6.0 及更早，README 顶部「限制」写的是「程序不会控制设备」。0.7.0 起该表述不再成立：程序具备**默认关闭**的 06 单寄存器下置能力，且必须同时满足配置与现场解锁条件。当前 `AssemblyInfo.cs` 仍标 0.6.0.0，README 与 CHANGELOG 尚未同步，本文不代表已发版。
- 所有控制点的读数与语义均**未现场验证**，凡未执行事项一律标注「待现场确认」。

## 1. 目标与非目标

目标：PLC 是控制权威，PC 只做三件事——按 06 功能码下置**一个** 16 位寄存器、读回核对、写审计。每一次写入都要能在配置里查到「哪个点、哪个站号、哪个地址、允许范围、是否开放」，在日志里查到「谁、何时、旧值/新值、报文、结果」。默认状态下程序对设备仍是纯只读，行为与 0.6.0 一致。

非目标（明确不做）：

- 不做 PID、闭环调节、斜坡/限幅，这些属于 PLC 侧。
- 不做 FStrategy 式软 PLC（力控的 FStrategy 是独立付费组件，本工程没有配置，见第 12 节）。
- 不做自动控制、定时写入、脚本写入、按阈值触发写入。
- 不做云端下行：云连接只上传快照，没有下发通道，也不接受远端控制指令。
- v1 不做 16 功能码（写多寄存器），因此不做 float32 / 32 位整数写入。

## 2. 安全原则

1. **默认关闭**：`control-config.json` 缺失、解析失败或校验失败，一律退化为 `mode=disabled`，所有写入被门禁拒绝。
2. **白名单**：只有出现在 `targets` 里、且能反查到 `protocol.json` 绑定点的点才可能被写；配置里自填的站号/地址必须与绑定完全一致，否则加载即失败。
3. **双重门**：`confirmed=true`（现场确认该点可写、语义正确）**且** `allowWrite=true`（本次运行允许写入）才放行，两者默认都是 `false`。
4. **会话内解锁**：`armed` 只是内存状态，不写配置文件；重启、重新加载配置、停止采集都会回到锁定。
5. **自动上锁**：采集停止、解锁闲置超时、联锁失效、写入结果未知，都会立即上锁。
6. **联锁前置**：写入前要求「远程模式」与「无总故障」两个只读点新鲜且等于期望值；`remoteExpected` 未填则只能预演。
7. **写后回显 + 回读**：06 回显必须与请求逐字节一致（含 CRC），随后用 03 读同一寄存器核对实际值。
8. **单飞**：写入串行化，同一时刻只有一个在途事务；请求入队，由采集线程在轮次开始处处理。
9. **无自动重试**：超时、回显为空、回显异常、回读失败都不重发。
10. **结果未知即锁定**：无法判定「是否已写入」时置 `uncertain` 并上锁，直到人工核对。
11. **全程审计**：`preview` 与所有被拒绝的尝试都写审计；真实 06 报文额外进 `control_events` 表。
12. **界面不碰串口**：写逻辑只在引擎里，页面只能入队并等待结果（在线程池里等，不阻塞 UI）。

## 3. 写协议（06 单寄存器）

请求结构（`Modbus.WriteSingle`，站号 1..247、零基地址 0..65535、值 0..65535；越界直接抛 `ArgumentOutOfRangeException`，不产生报文）：

| 字节 | 内容 |
|---|---|
| 0 | 站号 |
| 1 | 功能码 `0x06` |
| 2–3 | 寄存器地址，高字节先 |
| 4–5 | 写入值，高字节先 |
| 6–7 | CRC16/Modbus，低字节先（初值 FFFF，多项式 A001） |

正常回显是从站原样返回的同样 8 字节。`ExtractWriteEcho` 在接收缓存里重同步，要求站号、功能码、地址、写入值全部匹配且 CRC 正确才认这一帧；同时识别 `0x86` 异常帧（5 字节：站号、`0x86`、异常码、CRC）。异常码由 `ExceptionHint` 转中文：`0x01` 非法功能、`0x02` 非法数据地址、`0x03` 非法数据值、`0x04` 从站故障、`0x06` 从站忙。

示例：向 D2202（站号 2、零基地址 2202 = `0x089A`）写入 26（`0x001A`）：

```
请求 / 正常回显   02 06 08 9A 00 1A 2A 7D
回读请求（03）    02 03 08 9A 00 01 A6 76
回读响应          02 03 02 00 1A <CRC>     ; 高字节先的 26
设备拒绝示例      02 86 02 33 A1           ; 0x02 非法数据地址
```

CRC 以程序计算为准（该算法已在自测中对照已知帧校验），上面字节仅供人工比对，不要在任何脚本里写死 CRC。v1 边界：只支持 16 位单寄存器——配置校验要求目标点 `register_count == 1` 且 `kind == "int16"`，浮点/32 位点连配置都过不了。浮点或 32 位写入需要 16 功能码，本版**不做**，需要时另立设计、不复用 06 通道。符号语义也未定：16 位点当前按 UINT16 回读，`protocol.json` 的 `decode_proposal` 仍是 `int16_big_endian_signedness_pending`。

## 4. `control-config.json`

放在运行目录（与 `protocol.json` 同级）。加载后用文件字节的 SHA-256 前 16 位十六进制作为 `Version`，写进每条审计记录；文件缺失时 `Version="none"`。校验失败不会让程序崩溃，而是进入 `config_error` 门禁。

| 字段 | 说明 | 校验 / 默认 |
|---|---|---|
| `schemaVersion` | 配置结构版本 | 当前 1，未参与校验 |
| `mode` | `disabled` / `preview` | 仅这两个值；`armed` 是会话内状态，不写文件 |
| `requireOperatorAck` | 声明「写入需操作者确认」 | 引擎未读取：操作者始终必填 |
| `autoLockIdleSeconds` | 解锁闲置超时秒数 | 引擎按 `max(10, 该值)` 生效 |
| `interlocks.remotePoint` | 远程/本地只读点 | 不能为空，须为单寄存器可读点 |
| `interlocks.remoteExpected` | 远程模式的期望值 | `null` = 语义未裁决，只能预演；非空须 0..65535 |
| `interlocks.faultPoint` | 总故障只读点 | 不能为空，须为单寄存器可读点 |
| `interlocks.faultExpected` | 无故障时的值 | 0..65535 |
| `interlocks.maxAgeSeconds` | 联锁读数最大年龄 | 1..3600 |
| `interlocks.requireAcquiring` | 声明「要求采集中」 | 引擎固定要求采集中，字段不改变行为 |
| `limits.minIntervalMs` | 两次写入最小间隔 | 100..600000 |
| `limits.maxWritesPerMinute` | 每分钟最多写入次数 | 1..600 |
| `limits.verifyWindowMs` | 回读窗口 | 100..30000；只校验，引擎未使用（回读立即执行一次） |
| `targets[].point` | 测点名，必须在 `protocol.json` 已绑定 | 非空、不重复 |
| `targets[].label` | 界面/审计显示名 | 建议与 `PointCatalog` 一致 |
| `targets[].kind` | 写入类型 | v1 只允许 `int16` |
| `targets[].slave` / `address` | 站号 / 零基地址 | 必须与绑定完全一致 |
| `targets[].min` / `max` | 允许写入范围 | 0..65535 且 `min<=max`；**默认都是 0，即未显式配置时只能写 0** |
| `targets[].step` | 界面步长 | 必须 > 0，默认 1 |
| `targets[].unit` / `note` | 单位 / 备注 | 仅展示 |
| `targets[].confirmed` | 现场已确认可写 | 默认 `false` |
| `targets[].allowWrite` | 本次运行开放写入 | 默认 `false` |

默认关闭示例（可直接当出厂模板，所有点都不可写）：

```json
{
  "schemaVersion": 1, "mode": "disabled", "requireOperatorAck": true, "autoLockIdleSeconds": 600,
  "interlocks": { "remotePoint": "D2003", "remoteExpected": null, "faultPoint": "D2208",
                  "faultExpected": 0, "maxAgeSeconds": 10, "requireAcquiring": true },
  "limits": { "minIntervalMs": 1000, "maxWritesPerMinute": 6, "verifyWindowMs": 1500 },
  "targets": [
    { "point": "D2202", "label": "主机温度设定", "kind": "int16", "unit": "℃", "slave": 2, "address": 2202,
      "min": 16, "max": 30, "step": 1, "confirmed": false, "allowWrite": false, "note": "读数为 0，语义待确认；范围按现场手册调整" },
    { "point": "D2001", "label": "模式控制", "kind": "int16", "unit": "原始码", "slave": 2, "address": 2201,
      "min": 0, "max": 10, "step": 1, "confirmed": false, "allowWrite": false, "note": "模式枚举未知" },
    { "point": "D2000", "label": "送风风机速度", "kind": "int16", "unit": "原始码", "slave": 2, "address": 2000,
      "min": 0, "max": 100, "step": 1, "confirmed": false, "allowWrite": false, "note": "倍率/量程未知" },
    { "point": "D2207", "label": "放冷反馈设定", "kind": "int16", "unit": "原始码", "slave": 2, "address": 2207,
      "min": 0, "max": 100, "step": 1, "confirmed": false, "allowWrite": false, "note": "语义待确认" }
  ]
}
```

注意 `mode=preview` 并不等于「只预演」：它只是允许解锁。真正拦住写入的是 `confirmed`、`allowWrite`、会话内 `armed` 和联锁；只把 `mode` 改成 `preview` 而 `allowWrite=false`，一次也写不出去。

## 5. 控制目标表

地址来自力控工程绑定还原，均为 **Modbus 零基地址**（力控连接项数字 − 1），站号 2（PLC），读用 03、写用 06、单寄存器。

| 点名 | 语义 | 力控连接项 | 零基地址 | 十六进制 | 类别 |
|---|---|---|---|---:|---|
| D2202 | 主机温度设定 | `HRS2203` | 2202 | `0x089A` | 控制/设定 |
| D2001 | 模式控制 | `HRS2202` | 2201 | `0x0899` | 控制/设定 |
| D2000 | 送风风机速度 | `HRS2001` | 2000 | `0x07D0` | 控制/设定 |
| D2207 | 放冷反馈设定 | `HRS2208` | 2207 | `0x089F` | 控制/设定 |
| D2003 | 远程/本地 | `HRS2004` | 2003 | `0x07D3` | 只读联锁 |
| D2208 | 总故障 | `HRS2209` | 2208 | `0x08A0` | 只读联锁 |
| D2209 | 风机状态 | `HRS2210` | 2209 | `0x08A1` | 只读状态 |
| D2210 | 当前模式 | `HRS2211` | 2210 | `0x08A2` | 只读状态 |
| D2211 | 制冷主机 | `HRS2212` | 2211 | `0x08A3` | 只读状态 |
| D2212 | 冷量反馈 | `HRS2213` | 2212 | `0x08A4` | 只读状态 |
| D2213 | 水泵 | `HRS2214` | 2213 | `0x08A5` | 只读状态 |

- 只读点不能放进 `targets`（放进去就等于声明可写）；联锁点只由 `interlocks` 引用。
- 「控制/设定」与「运行状态」两组都不进导出宽表（`ExperimentExport.ExportablePoint` 显式排除），避免把设定值当测量值导出。
- **2026-10-03 实机读数全部为 0**（四个设定点与各状态点均如此）：既不能证明寄存器可写，也不能确定 0 表示「关闭」「未启用」还是「未刷新」，可写性与语义都要现场确认，在那之前只能预演。也正因为读数全为 0，`faultExpected=0` 的联锁会「看起来通过」——这是**假通过**，不能用来证明联锁逻辑已被现场验证。

## 6. 门禁与状态码

门禁按固定顺序短路，提示的是**第一个**不满足的条件（例如未解锁且 `confirmed=false` 时先报 `not_confirmed`）。

| 状态 | 触发条件 | 处理 |
|---|---|---|
| `disabled` | 配置缺失或 `mode=disabled` | 拒写；预演仍可用（不发报文） |
| `config_error` | 配置解析/校验失败 | 拒写拒解锁；修配置后重新加载 |
| `disconnected` | 未连接数据源 | 先连接 |
| `not_acquiring` | 未开始采集，或当前是单轮采集 | 改连续采集；单轮不处理写入 |
| `no_target` | 该点不在 `targets` | 先加配置并确认 |
| `not_confirmed` | `confirmed=false` | 现场确认后改配置 |
| `not_allow_write` | `allowWrite=false` | 本次未开放，改配置后重新加载 |
| `not_armed` | 会话未解锁 | 解锁（需联锁语义已裁决） |
| `uncertain` | 上次写入结果未知 | 先「重新读取核对」，不允许新写 |
| `rate` | 未到 `minIntervalMs` 或超 `maxWritesPerMinute` | 等待后重试，不排队堆积 |
| `interlock_unverified` | `remoteExpected=null` | 只能预演；现场裁决远程标志后填写 |
| `stale` | 联锁点无有效读数、质量非 good 或超 `maxAgeSeconds` | 等下一轮刷新 |
| `interlock_remote` | D2003 不等于期望值（非远程） | 先把 PLC 切到远程，不在本地强写 |
| `interlock_fault` | D2208 不等于 `faultExpected`（有总故障） | 先排故 |
| `ready` | 以上全部通过 | 允许入队发送 06 |

执行结果状态（同一次操作的最终判定）：

| 状态 | 含义 | 处理 |
|---|---|---|
| `preview` | 只生成 06 报文，未发送 | 审计里有 Tx、无 Rx；永远不发 |
| `blocked_ack` | 缺少操作者 | 记录操作者后重试 |
| `range` | 超出 `min..max` | 修正数值；未产生报文 |
| `no_write_channel` | 传输不是 `IWriteTransport`（只读替身） | 换实机/模拟写通道 |
| `unknown` | 回显超时、回显为空、传输异常、排队超时 | 立即上锁并走核对；**不重试** |
| `rejected` | 收到 `0x86` 异常帧 | 明确被拒绝，不上锁；按异常码查地址/值/权限 |
| `unverified` | 回显正常但回读失败 | 上锁并走核对 |
| `not_accepted` | 回读仍等于旧值 | 记录；检查写保护/量程 |
| `mismatch` | 回读是第三个值 | 记录但**不上锁**；现场应立即人工确认是否有其他主站在写 |
| `applied` | 回读等于写入值 | 正常完成 |
| `verify_applied` / `verify_not_applied` | 核对读回等于新值 / 旧值 | 解除锁定 |
| `verify_mismatch` | 核对值既非旧值也非新值 | 保持锁定 |
| `busy` / `no_uncertainty` / `cancelled` / `error` | 核对超时、无待核对项、采集中止、异常 | 分别提示；`error` 记日志并保留现场 |

## 7. 调度

- 采集线程每轮开始时先调用 `ProcessControlQueue(roundPath, store, source, timeoutMs, token, log)`，把队列整批取出逐个执行，然后才读本轮测点。写与读因此天然串行，共用同一个串口对象和同一套帧间静默/取消逻辑。
- 页面线程只做两件事：`ExecuteControl` 入队，然后在 `ManualResetEventSlim` 上等待（上限 `min(30000, max(8000, timeout*2000+5000))` 毫秒）；等待发生在线程池线程里，不冻界面。
- **不在 UI 线程直接写串口**的原因：① `SerialPort` 归采集线程所有，跨线程并发读写会让半帧残留污染下一轮读；② 写入必须夹在帧间静默里，还要能被「停止采集」的令牌中断；③ UI 线程阻塞会冻结界面且无法取消。
- **写超时/回读失败不自动重试**的原因：Modbus RTU 没有事务号，回显丢失时无法区分「没执行」和「执行了但应答丢了」；重发可能造成第二次写入。温度设定这类幂等值或许无害，模式切换、风机速度这类命令没有幂等保证，所以宁可停下来让人看。排队超时同样判 `unknown`（`uncertainDetail="排队超时，无法确认 06 是否已发送"`），因为请求可能已被采集线程取走。
- **`unknown` 的解除路径**：`ResolveControlUncertainty` 在采集轮里插入只读核对项，用 03 读该点——读到新值 → `verify_applied` 解除；读到旧值 → `verify_not_applied` 解除；读到第三值 → `verify_mismatch` 保持锁定。
- **实现现状（已修）**：`MarkUncertain` 同时置 `controlUncertain=true` 与 `controlArmed=false`（安全上锁）；`ResolveControlUncertainty` **不要求已解锁**，只要求采集运行中，核对完成后清除 `uncertain`；`ArmControl` 在 `uncertain` 时仍拒绝解锁。因此正确顺序是：未知 → 保持锁定 → 点「重新读取核对」→ 核对通过 → 重新「解锁控制」。`ControlReadiness` 先判 `uncertain` 再判 `not_armed`，界面提示不会再被“未解锁”掩盖。
- **16 位控制点的数值视图**：这些点显示层保留 `U16=… / I16=…（符号/倍率待定）`，`Modbus.Decode` 不会给 `Observation.Number` 赋值；控制门禁通过 `MonitorEngine.ControlNumericValue` 从大端原始寄存器补一个 0..65535 的数值视图，因此联锁与旧值比较不依赖显示层。这也是所有控制点必须按无符号整数处理的原因（8..15 位原码语义仍需现场确认）。

## 8. 审计

- `logs/control-events.jsonl`：每条结果追加一行 JSON（UTF-8 无 BOM，`JavaScriptSerializer` 序列化），字段为 `UtcTicks`、`Source`、`Session`、`Operator`、`Point`、`Label`、`Slave`、`Address`、`Function`、`OldValue`、`NewValue`、`Readback`、`Status`、`Detail`、`Tx`、`Rx`、`ElapsedMs`、`ConfigVersion`。
- `control_events` 表（SQLite，随采集分期库）：`id`、`utc_ticks`、`source`、`session`、`operator`、`point`、`label`、`slave`、`address`、`function`、`old_value`、`new_value`、`readback`、`status`、`detail`、`tx`、`rx`、`elapsed_ms`、`config_version`，索引 `ix_control_time(utc_ticks,id)`。写入走 `RecordControl` 的工作队列，与观测记录同批提交。
- 只有采集运行中才有 `store` 与 `roundPath`：未采集时只写 JSONL，数据库里没有对应行。审计写失败只记普通日志、不阻断控制流程（刻意的：不能因为日志盘满卡住串口）。
- `ConfigVersion` 把每条记录钉在当时的配置版本上，配置一改版本就变，事后能判断「当时允许写的是什么」。
- `preview` 与所有门禁拒绝也记录：普通拒绝的 `tx` 为空串；`preview` 例外，它记录生成的报文但没有 `rx`。所以「有 Tx 无 Rx」既可能是预演，也可能是发送前就失败——用 `status` 区分，别只看报文。

## 9. 界面行为

- 「运行控制」页（`ControlPage`，主窗体第 2 个标签）是**控制台**，页面本身不碰串口，所有 06 发送都走引擎。布局按「重点」分层：
  1. **控制卡**（左上）：控制开关（默认关闭）、门禁提示（含回读窗口毫秒数）、控制点下拉、**大号当前值**、大号设定值输入框、发送/预演；底部一行状态：连接（模拟数据或 `COM10 · 9600 8N1`）、采集（周期/超时）、联锁值+数据年龄、现场确认计数、本次会话已发/成功/异常。
  2. **返回信号卡**（右上）：D2208/D2209/D2210/D2211/D2212/D2213 的「写入前 / 当前 / 变化」。
  3. **五步流程时间线**（通栏）：① 设定 → ② 下发 → ③ 回显 → ④ 回读 → ⑤ 设备，每步显示**值、时间戳、状态**（已提交/已发出/回显一致/一致/未见动作），这是「一次写入到底发生了什么」的主视觉。
  4. **设定表**（左下）：勾选多个点批量发送，也有「本点发送」只发一行；列含当前值、要发送的值、范围、站号/地址、**采样时间**、**上次写入**。
  5. **过程与详情**（右下页签）：`过程日志` 按结果着色并可导出；`调试详情` 保留最近一次写入的 06 请求/回显、03 预读/回读原始报文、旧值来源、耗时、操作者、配置版本与审计位置，可一键复制。
- 设定值输入框在用户没有手动修改时会**跟随当前值**（方便做同值写自检），手动改过之后不再覆盖，重新选点或发送完成后恢复跟随。
- 预演：调用 `PreviewControl`，生成 06 报文并写审计，**永不发送**，时间线只填到第 ② 步并标注「未发送（预演）」。
- 发送流程：`ExecuteControl` → 门禁（开关、连接、采集、范围、冷却/限频、可选的联锁与确认状态）→ 06 发送 → 回显校验 → 立即 03 回读 → 判定 `applied / not_accepted / mismatch / rejected / unknown` → 审计。
- 硬约束（保留）：单飞（一次一个写入）、1 秒冷却、每分钟 6 次上限、写后必须回显 + 03 回读、结果未知不自动重试（需「重新读取核对」）、每次发送写 `control_events` + `logs/control-events.jsonl`。
- 联锁与现场确认默认**只提示不拦截**（`enforceInterlocks=false`、`requireConfirmed=false`），因为 D2003「远程」和 D2208「无故障」的现场语义还没裁决；需要严格时在 `control-config.json` 里改成 true。
- `control-config.json` 的 `mode=disabled` 是**强制关闭**（任何开关状态都发不出去）；`mode=preview` 时才由界面开关决定。程序启动时开关一律是「关闭」。
- **调试细节**（为现场联调准备）：控制台顶部显示数据源（`COM10 · 9600 8N1` 或「模拟数据」）、采集周期/超时、联锁点实时值与数据年龄、现场确认计数、本次会话「已发/成功/异常」；「门禁」行直接给出当前不可发送的原因与回读窗口毫秒数。
- 设定表每行显示点名/名称/当前值/**采样时间**（超时或质量差标黄）/允许范围/站号·地址/上次写入结果；「本点发送」按钮可只发一个点，勾选多个点用「发送勾选点」批量发（逐点串行、逐点回读、逐点审计）。
- **调试详情面板**（右下）保留最近一次写入的完整信息：点、站号/地址、写入值、旧值及来源（写前 03 预读 / 采集快照）、回读值与是否一致、状态、总耗时与回读耗时、**06 请求/回显原始字节、03 预读 TX/RX、03 回读 TX/RX**、操作者、配置版本、审计落盘位置、判定明细；「复制详情」一键复制到剪贴板。
- 测试过程日志按事件着色（成功绿色、未知/拒绝红色、其余黄色/灰色），支持「导出过程」保存为 `logs/control-session-<时间>.txt`，方便现场留证与回传。

## 10. 现场启用清单

按顺序做，任一步不通过就停在原地。每步都要留证。

1. **关闭力控对同一串口的占用**。同一个 COM 口不能两个程序并行轮询或并行写。留证：力控已退出/已断开该连接的截图、设备管理器端口未被占用、本程序连接成功且四台设备读数刷新的会话日志。
2. **只读基线**。先只在只读模式跑够若干轮，记录 D2202/D2001/D2000/D2207/D2003/D2208/D2209–D2213 的原始值；裁决 D9（8204 与 8202 冲突）与 `0x00010000` 类型标志的语义。留证：`logs/session-*.log` 的原始 TX/RX、对应时段记录页截图、读数导出、书面裁决结论（谁、依据哪份资料、结论是什么）。
3. **D2202 同值写自检**。把当前读数自身作为目标值写一次（旧值 = 新值），观察回显与回读。这一步只验证「06 通、回显对、回读一致」，不改变工况。留证：`logs/control-events.jsonl` 中该条的 `tx`/`rx`/`readback`/`status=applied`、现场仪表读数照片。若在这里就 `rejected`/`unknown`，停止，不进入下一步。
4. **最小变更并恢复**。在 D2202 上做 0.5–1 ℃ 单步变更，回读确认后立即写回原值并确认恢复。留证：两次 `applied` 记录的报文与时间戳、仪表前后读数照片、两次操作之间 PLC 无报警。
5. **逐点改配置**。一次只把一个点改成 `confirmed=true` + `allowWrite=true`：先在 `mode=preview` 下预演核对报文，再解锁实写，写完立刻把该点 `allowWrite` 收回 `false`。D2001/D2000/D2207 逐个走这一步，不允许批量打开。留证：每次改动前后的 `control-config.json` 副本与 `ConfigVersion`、对应审计记录、现场确认人签字。
6. **异常即停**。出现 `unknown`/`unverified`/`interlock_*`/`rejected`/`mismatch` 任一项：立即停止采集、退出程序、不重试，导出 `logs/control-events.jsonl`、`logs/session-*.log` 与 `control_events` 查询结果交现场分析。留证：上述三份导出、发生时刻的照片/视频、PLC 侧诊断缓冲、以及「下次启用前必须完成」的整改说明。

## 11. 测试与验收

控制自测应覆盖以下 15 类场景（模拟传输已具备夹具：`SimulationTransport.WriteCount`、`Seed`/`Seeded`、`FailMode=drop|exception|ignore`）：

| # | 场景 | 期望 |
|---:|---|---|
| 1 | 无 `control-config.json` | `disabled`，0 字节写 TX |
| 2 | 配置损坏 / `mode` 非法 / 地址与 `protocol.json` 不符 | `config_error`，0 字节写 TX |
| 3 | `mode=disabled` 下预演与执行 | 预演生成报文不发送；执行 `disabled` |
| 4 | 未连接 / 未采集 / 单轮采集 | `disconnected` / `not_acquiring`，0 字节写 TX |
| 5 | 未解锁 | `not_armed`，0 字节写 TX |
| 6 | `confirmed=false` 与 `allowWrite=false` 分别验证 | `not_confirmed` / `not_allow_write`，0 字节写 TX |
| 7 | 非目标点、重复 target、非单寄存器点 | 配置校验失败或 `no_target` |
| 8 | 超出 `min..max`；`max<=min` 的配置 | `range`，0 字节写 TX；配置校验失败 |
| 9 | 连续写入触发 `minIntervalMs` / `maxWritesPerMinute` | `rate`，多余请求不发报文 |
| 10 | `remoteExpected=null` | `interlock_unverified`，预演可用、实写不可用 |
| 11 | 联锁点缺失/过期/质量差、D2003 非远程、D2208 有故障 | `stale` / `interlock_remote` / `interlock_fault` |
| 12 | 正常 06（回显一致、回读一致） | `applied`，恰好 1 帧 06 + 1 帧 03 |
| 13 | `FailMode=exception`（`0x86`） | `rejected`，不上锁，不重试 |
| 14 | `FailMode=drop`、回显为空、回读失败 | `unknown` / `unverified`，上锁，无第二次 06 |
| 15 | `FailMode=ignore`（值不变）与第三值 | `not_accepted` / `mismatch`；核对分别得 `verify_applied` / `verify_not_applied` / `verify_mismatch` |

补充：并发两次写入必须串行（单飞），排队超时判 `unknown`；审计必须同时落 JSONL 与数据库；`preview`/`blocked` 记录里 `tx` 为空（`preview` 除外）。**当前覆盖状态**：`ControlTests.cs` 已实现 51 项检查并全部通过，覆盖 FC06 帧/CRC、噪声中提取回显、`disabled`、`preview`、`not_armed`、`range`、`interlock_remote`、`interlock_fault`、`not_acquiring`、`applied`（含审计 `applied` 与模拟寄存器真实变化）、`unknown`（丢回显）→ 再次写入 `uncertain`、禁止重新解锁、`verify_not_applied` 后解除并允许重新解锁、`not_accepted`（回读仍为 24）、`rejected`（0x86）、`rate`、配置 `Validate` 失败；另有 UI 冒烟检查「运行控制」页、解锁/核对入口和 6 页导航。`ControlTests` 已接入 `--self-test` 与 `--test-control`。

验收标准：「默认配置下任何写入 0 字节 TX」——不带 `control-config.json`（或 `mode=disabled`）时，跑完整自测与全部界面操作，串口侧不得出现任何 06 帧，`SimulationTransport.WriteCount` 必须为 0。

已知沙箱限制不影响控制单测：`PartitionCycle` 的 `File.Replace` 与既有 UI 云端两项检查在本机沙箱下的失败属于环境问题；控制单测只用临时目录 + `SimulationTransport`，不碰 `File.Replace`，也不联网。

## 12. 限制与未决

- **PLC 程序与寄存器手册缺失**：所有地址来自力控工程配置还原，不是厂家手册；写权限、量程、单位、枚举值均无权威依据。
- **D9 语义冲突未裁决**：点表绑 8204（`0x200C`，float32），画面注释指向 8202/`0x200A`（市电总功）。程序把 D9 标为「原始指标8204」、DU6 标为「原始指标8202」，两个都不下结论。**待现场确认**。
- **`0x00010000` 类型标志未裁决**：部分 HRS 点类型标志是 `65538`（`0x00010002`），另一些是 `2`。高位是否代表读写权限没有驱动定义支持，不能据此推断「可写」。**待现场确认**。
- **力控 FStrategy 是独立付费组件，本工程没有配置**：导出工程里 `Func` 目录为空、事件动作为零条目，没有任何现成软 PLC 逻辑可供对照或迁移；本程序也不实现它。
- **所有控制点读数为 0**（2026-10-03 实测）：可写性、语义、联锁有效性全部**待现场确认**。
- **`unknown` 后的恢复**：按「重新读取核对 → 重新解锁」执行；核对不要求已解锁，但要求采集运行中（见第 7 节）。
- **控制自测已落成**：`ControlTests.cs` 51 项，模拟传输 + 独立临时目录，不碰真实串口；已接入 `--test-control` 与 `--self-test`。
- **`control-config.json` 已随包发布**：模板默认 `mode=disabled`、`remoteExpected=null`、全部目标 `confirmed=false/allowWrite=false`；运行目录没有该文件时会回退读取安装目录模板，仍然 `disabled`。
- **`targets[].min/max`**：模板显式填写范围；`max<=min`（例如都写 0）会被配置校验拒绝，避免只能写 0 却误以为「写入无效」。
- 本文不声称任何现场验证、签发或生效结论；上述每一项在闭环前都保持「待现场确认」。

## 13. 与力控控制逻辑的对照（2026-11-05 研究结论）

### 13.1 这份力控工程本身**没有任何控制配置**（证据）

| 检查对象 | 证据 | 结论 |
|---|---|---|
| 事件/菜单/键盘动作、用户函数 | `sys\evtactda.dat`、`menuact.dat`、`keyactda.dat`、`userfunc.dat` 均为 `00 00` | 0 条动作 |
| 画面脚本 | `DRAW1__Contents.bin` 无任何含 `=` 的可打印串；`EXCE__Contents.bin` 的 12,240 条 `ScriptN` 全是 `ScriptType="6"`/`DbTagName=""`/`MouseAction="0"` 的报表单元格脚本 | 无控制动作 |
| 脚本目录 | 解压后无 `Func` 目录 | 无脚本 |
| FStrategy | `sys\MainSub.dat` 只登记 `CPlugInCustom`/`RouterCtrl.dll` | 无软 PLC 策略 |
| DB 点类型 | `db\par00.dic`~`par05.dic` 显示力控自带「控制/计算点（含 PID/FORMULA）」「逻辑点」「输出点」，但本工程 40 点**全部建成模拟量点** | 控制能力未被使用 |

⇒ 本程序不是「迁移既有力控控制配置」，而是**新建一条受控下置链路**；力控工程能提供的只是点表、地址规则与驱动配置。

### 13.2 驱动层：写功能码与力控一致

- PLC 走 `IO Servers\PLC_Modicon_MODBUS`，两份 `CommPar.dat` 一致：9600 / 8N1，PLC 更新周期 100 ms、超时 8 s。
- 力控官方《技术简讯 30 期》：**「03 与 06 号代码对应 4 号寄存器，03 负责读，06 负责写」**，写多寄存器则是 16 号（在驱动配置里与 06 并列的选项）。本工程**勾选了 06、未勾选 16**。
- ⇒ 本程序只用 **06 写单个保持寄存器**，与力控在本工程的实际能力一致；32 位/浮点写入必须走 16，本程序不做（也做不了）。

### 13.3 力控官方控制链路 vs 本程序

| 环节 | 力控 | 本程序 |
|---|---|---|
| 画面输入 | 动画连接-数值输入输出（运行时直接改值即触发下置）/ 触敏动作脚本 | 设定表勾选 + 填值（可一次多点） |
| 动作 | 脚本引擎（IF/WHILE/FOR/SWITCH）、条件动作、数据改变动作、窗口/键/应用程序动作 | 无脚本；固定路径：门禁 → 06 → 回显 → 回读 → 审计 |
| 写变量 | View 变量 → DB 点参数 | ControlCommand → 直接进 06 报文（无通用变量层） |
| 驱动下置 | IO 驱动按配置功能码写设备 | `Modbus.WriteSingle`（站号/功能码/地址/值/CRC） |
| **回读校验** | **无**；靠下一轮采集刷新覆盖，人工看画面 | **写后立即 03 回读 + 回显逐字节校验** → `applied/not_accepted/mismatch/rejected/unverified/unknown` |
| 审计 | 用户级操作日志 + 变量级「记录操作」 | `control_events` 表 + `logs/control-events.jsonl`（含 Tx/Rx/回读/耗时/配置版本 SHA） |
| 权限 | 用户等级 / 角色 / 安全区（危险分散）/ 电子签名 | 无用户体系；操作者 = Windows 用户名 + 配置白名单 |

**力控有而本程序没有**：脚本与条件/数据改变动作、FC16 写多寄存器、用户权限与安全区、变量级安全级别与记录操作、电子签名、点报警体系、FStrategy 软 PLC、动画连接。
**本程序有而力控没有**：写后回读校验、结果未知状态机与人工核对通道、限频与单飞、预演（只生成报文）、配置版本钉住审计。

### 13.4 两个必须写进现场清单的坑

1. **`D2001` 点名与地址不符**：11 个控制/状态点里点名==零基地址，唯独 `D2001` 点名写 2001、实际连接项是 `HRS2202`（零基 **2201**）——疑为原工程笔误（应为 D2201）。**现场按点名核对会找错寄存器**；以 `protocol.json` 的 `address_zero_based` 为准。程序里 `control-config.json` 的 `D2001.address=2201` 是正确的（配置校验强制与点表一致）。
2. **`0x00010000` 不是读写权限**：该高位同样出现在太阳能表的三个纯读点（ZU/ZI/ZW6682）和 `0006H` 上；`db\ConfSave.mdb` 扫描不到任何「读写/只读/下置」字符串。⇒ **每个点是否可写仍然无证据**，不能凭类型标志推断。

### 13.5 仍然无证据的清单（现场裁决用）

- `D2003`（远程/本地）、`D2208`（总故障）、`D2209/D2210/D2211/D2213` 的 0/1 枚举含义：工程里既无 `OFFMES/ONMES`（未用开关量点）、无 `LINETBL` 枚举表、无报警定义、无历史曲线，2026-10-03 实机读数**全为 0**。
- `D2202` 的单位与量程、`D2001` 的模式码、`D2000` 的风机单位、`D2207` 的「放冷」含义：工程 `UNIT` 全空、`SCALEFL` 全 0。
- PLC 是否允许 06 写这些寄存器、是否有写保护区：**无证据**。
