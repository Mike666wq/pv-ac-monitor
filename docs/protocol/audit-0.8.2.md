# 0.8.2 全面审计：对照既定要求的差距与补进方案

> 审计时间：2026-11-10　审计对象：`main` @ `5fd2d0b`（0.8.2.0）
> 方法：源码逐行核查 + 进程内运行期取值 + 独立审计员交叉复核；每条结论都带 `文件:行号` 与实际数值。
> 基线：`--test-all` 10 套件全通过，8 项申报缺口（全部需真机或现场）。

---

## 0. 结论摘要

| 维度 | 判定 | 要点 |
|---|---|---|
| 协议映射 | **基本达标** | 7 条已知裁决落地 6 条；**D9 只改了标签，采集/导出仍按测量点处理**（本轮已修） |
| 控制门禁 | **基本达标** | 三道门真正独立、五步链路齐全；**效果确认会被坏质量观测静默丢弃**（本轮已修） |
| 数据完整性 | **达标** | 每轮每点必有行、WAL+checkpoint、历史元数据行内存储、config_version 内容指纹，均有断言 |
| 功能完整性 | **有缺口** | 块读未接入（但实测收益仅 10.1%）；若干配置字段声明了没实现 |
| UI | **基本达标** | 四态/四段语义诚实、卡片零裁切；**状态栏初始文案写死点数**、**验收清单与产品脱节**（本轮已修） |
| 文档 | **有缺口** | README 版本号过时、验收清单 D 段仍是 0.8.1 之前的"定位"语义 |

---

## 1. 本轮已修复（5 项，均带回归断言）

### P0-1　D9（保留地址 0x200C）被当成测量点导出

- **问题**：`PointCatalog.cs:151-153` 注释写着"从采集计划里删掉，只保留目录说明"，**实际没删**。
  `roles` 表里没有 D9 条目 → 角色落默认值 `measurement` → `ExperimentExport.ExportablePoint()`
  只判 `IsMeasurement` → **D9 进导出、进记录页默认列**。
  运行期实测：`d9InPoints=True, role=measurement, exportable=True, defaultColumn=True`。
- **为什么是问题**：DDSU666 手册 0x200C = RESERVED，没有物理量。导出一列没有物理意义的数，
  等于程序替力控的错标背书。
- **修法**：D9 角色改 `diagnostic`（`PointCatalog.cs` roles 表），**仍然进采集**（保持"每轮 37 行"
  这个不能破的约定），但不再进导出与默认列。注释同步改正。
- **断言**：`D9 角色是 diagnostic 而非 measurement` / `D9 不进导出` / `D9 不在记录页默认列` /
  `D9 仍然在采集点集里`。

### P0-2　效果确认被坏质量观测静默丢弃（注释与代码相反）

- **问题**：`MonitorEngine.Control.cs:152` 先把 `pendingEffect` 置空，`:154` 才判质量。
  注释写的是"这一轮没读到数：**继续等下一轮，不下结论**"，代码实际是**直接丢弃**。
  写入后的第一条观测经常是 timeout/skipped/ambiguous（站 2 一进隔离期就是），
  于是效果确认**永久消失**：没有 `applied`、没有 `reverted`、连审计行都没有，
  界面胶囊停在"回读一致，等待观测"。
- **为什么是问题**：0.8.0 的主线是"让程序停止说谎"，而这里正是程序在沉默地丢结论。
  **原测试只覆盖 good 路径**，坏质量路径零覆盖。
- **修法**：把清空动作移到"真的有结论"之后；坏质量继续等；另加 120 秒等待上限，
  超时如实记 `verify_timeout`，不再无限挂着。
- **断言**：新增场景 9b（4 条）：坏质量观测不下结论、坏质量之后下一条好观测**仍能**判定 applied。

### P0-3　"整轮未读取"被算成"超时轮"

- **问题**：`HistoryStats.cs:94` `if(agg.Good>0)GoodRounds++;else TimeoutRounds++;`
  站隔离期内整轮都是 skipped 行，**一次超时都没发生**，却被计成超时轮。
  这与 `Quality.cs` 的语义表矛盾（那里明确 skipped 不算 failure），
  且 `HistoryStats` 用自己的 SQL，绕过了单一真相源。
- **修法**：SQL 增加每轮 timeout 计数，三分法（有有效值 / 真超时 / 整轮未读取）；
  界面"实验段"表新增**「未读取轮」**列（此前这个维度根本没有显示位）。
- **断言**：`PageWorkflowTests.cs:81` 的列数期望同步 12 → 13。

### P1-4　状态栏初始文案写死点数

- **问题**：`DashboardForm.cs:45` 初始写 `"点表已载入 · 4 台设备 / 37 个测点"`，
  只有采集开始后才被真实数据覆盖（L489）。点表一改（加点、删点、设备变动），
  启动后的第一屏就在说假话 —— 与 0.8.x 一直在修的是同一类问题。
- **修法**：改为按点表实算（`PointTableSummary()`：站号去重算设备数、绑定点数算测点数）。

### P1-5　mode=preview 的兼容告警用户永远看不到

- **问题**：`ControlConfig.cs:96` `WritesEnabled` 把 `preview` 与 `live` 同等对待
  （preview **会真实写入**），而 `ModeCompatibilityWarning`（`:98`）**只有测试在读**
  （生产零读取点）。用户把配置从 live 改成 preview，界面不会有任何提示。
- **修法**：① 载入配置时把告警写进通信日志；② 更要紧的是挂到**门禁详情**上 ——
  `ControlReadiness()` 现在无论挡在哪一条，都会把兼容告警附在详情里。

---

## 2. 已核实达标的部分（本轮复核）

| 项 | 证据 |
|---|---|
| float32 = ABCD | `Protocol.cs:127-131` `order={0,1,2,3}`，`little[3-i]=payload[order[i]]`（payload[0]=MSB） |
| DS666 单相无 √3 | 全仓无 `Math.Sqrt`（只有 `HistoryStats.cs:191` 标准差）、无 1.732、无"三相" |
| D2001 保持 2201 只读 | `protocol.json` slave=2/addr=2201；`control-config.json` confirmed=false/allowWrite=false |
| D2212 = 定时斜坡指令 | `PointCatalog.cs:179` label=压缩机指令，角色 status；`feedbackPoints` 不含 D2212 |
| T1 倍率 1℃/LSB | `PointCatalog.cs:127` `Scale=1.0` |
| ZW66822 = 直流有功功率 W | `PointCatalog.cs:162` unit=`W` |
| 三道门各自独立 | `MonitorEngine.Control.cs` confirmed / allowWrite 各自 return；全仓 `requireConfirmed` 只剩声明与注释 |
| 随包默认全不可写 | 4 个 target 全 `confirmed=false, allowWrite=false`（mode=live，挡的是逐点标志位） |
| 写入五步齐全 | 门禁→预读→下发回显→回读→效果确认，前四步失败即停；预读失败 `pre_read_failed` 且不发任何报文 |
| 每轮每点必有行 | `MonitorEngine.Acquisition.cs` 站内失败/隔离期都 `EmitSkippedRows`；skipped 行 `Number=null` 不落 0（`ExtendedTests.cs:59-70` 有断言） |
| WAL + 关闭 checkpoint | `ExperimentStore.cs:73` WAL；`:135` 写线程 finally 里 `wal_checkpoint(TRUNCATE)` |
| 历史元数据不被今天改写 | `HistoryPage.cs` / `ExperimentExport.cs` 均优先行内 `Description`/`Unit`，缺失才回退点表 |
| config_version 是内容指纹 | `PointCatalog.cs:188-198` 对全部点拼 `name|label|group|unit|...` 取 SHA256 前 16 hex |
| 设备分母是真算的 | `DashboardForm.DeviceHealth()` 按 lastGood 存活站号算，不是写死 |
| 卡片四态存在 | `DashboardForm.cs:475/482` ○未接入 / △存疑 / 陈旧 Ns / 正常 |

---

## 3. 尚未修复 —— 补进方案（按优先级）

### P1-6　死字段：声明了但没有生产读取点

| 字段 | 位置 | 现状 | 补进方案 |
|---|---|---|---|
| `requireConfirmed` | `ControlConfig.cs:86` | 只剩声明+注释 | **删掉**。它已废弃，留着只会让人以为它还在起作用；`README.md:42` 已经解释了 0.7.x 的教训，不需要这个字段 |
| `requireOperatorAck` | `ControlConfig.cs:77` | 零读取；配套 `ControlConfirmForm` 全仓只定义不实例化 | **删掉字段与 ControlConfirmForm.cs**，或**实现二次确认**。0.8.0 已明确否决"每次弹窗"（实测写入是成组爆发），所以建议删而不是实现 |
| `requireAcquiring` | `ControlConfig.cs:29` | 零读取；"必须采集中"是硬编码 | **删掉**，或接进门禁（`ControlReadinessCore` 里那一行硬编码改成读配置） |
| `Held` | `ControlConfig.cs:52` | 死属性（状态字符串 `held` 在用） | 删 |
| `Command.Confirmed` / `.Preview` | `ControlConfig.cs:38` | 死字段 | 删 |
| `PointSummary.Error` | `HistoryStats.cs:31,186` | 算出来没人消费；且把 skipped/ambiguous/protocol/decode 混成一列 | **要么显示、要么删**。建议显示为点统计表的「异常点」列（口径与 `Quality.IsFailure` 对齐），因为现场确实需要区分"超时"和"其它异常" |
| `ControlTarget.kind` / `.step` | `ControlConfig.cs:13,15` | 只校验不消费 | 删，或接进 UI（把 step 显示成"每次步进多少"） |
| `ControlEvent.Target` | `ControlConfig.cs:61` | 写入值 = `result.Point`，完全重复 | 删 |

**做法**：一次性删除 + 删对应的测试断言，避免"测试保护死代码"（`ModeCompatibilityWarning` 就是这么变成死代码的）。

### P1-7　块读未接入 —— 但实测收益远低于预期，**不建议现在接**

用真实点表跑 `BlockPlanner.Plan`（`--diag-blocks`）：

| 项 | 数值 |
|---|---|
| 逐点 | 37 帧，理论下界 **350.5 ms/轮** |
| 块读 | **16 块 / 16 帧**，理论下界 **220.2 ms/轮** |
| 节省 | **130.4 ms/轮** |
| 对照现场实测 p50 = 1295.7 ms | 块读只省 **10.1%** |
| 对照现场实测 p90 = 9376 ms | 块读只省 **1.4%** |

**结论**：p90 的 9376 ms ≈ 一次 8 秒超时（站 6 无响应），**瓶颈不在帧数**。
块读是"锦上添花"，不是"救命稻草"。现在接入要动采集主路径，而收益上限 10%，风险收益比不划算。

**建议**：保持未启用，但在 `docs/field/BLOCK-READ-VERIFICATION.md` 里补上这张实测表，
把"要不要启用"变成一个有数字的决策，而不是"看起来更快所以要做"。
等站 6 的根因查清（见下）、p90 回到 p50 水平之后，再评估。

### P1-8　站 6 隔离期的口径

`MonitorEngine.Acquisition.cs:66-72`：站内任一点超时 → 本轮该站其余点记 skipped，
该站进入退避隔离（5/10/20/40/60/120/300 秒）。**这是对的设计**（其它站继续）。
但退避到 300 秒后，站 6 每 5 分钟才探一次，一次 8 秒超时 —— 这就是 p90 ≈ 9376 ms 的来源。

**建议**：站 6 的探测定时器应与"该站是否已知离线"联动：
连续 N 次超时后降低探测频率并**在状态栏显示"DDSU666 已离线 N 分钟"**，
让现场知道这不是程序卡住，而是设备确实没接。

### P2-9　文档与现实脱节（本轮一并修）

| 文件 | 问题 | 应改为 |
|---|---|---|
| `docs/field/ACCEPTANCE-CHECKLIST.md` D 段 | 仍按 0.8.1 之前的"搜索定位"语义写（"输入 T1 回车，定位并高亮"）。0.8.2 已改成**筛选**：输入即筛、不匹配的不显示 | 改为筛选语义：输入 `T1` → 只剩匹配的卡片；输入乱码 → 提示"没有匹配…" |
| `README.md:38` | "当前版本：**0.8.0**" | 0.8.3 |
| `README.md:86` | 发布包仍是 `0.8.0` | 0.8.3 |
| `README.md:18` | "约 8850 项断言" | 改为实际数 |
| `docs/protocol/implementation-audit.md:5` | "8 套件 / 4449 项断言" | 10 套件 |
| `docs/field/OPERATIONS.md:60` | 举例用 `PV-AC-Monitor-0.8.0\` | 改成 `0.8.3` 或泛指 |

---

## 4. 8 项申报缺口的可推进性

| 缺口 | 能否靠离线手段推进 | 办法 |
|---|---|---|
| 块读 vs 逐点读对拍 | **能** | 用 `FixtureTransport` 造真值寄存器表，块读与逐点读各自解同一份真值，逐位比对。`BlockPlanner.Slice` 的切片逻辑已有单测，缺的是端到端对拍 |
| 迟到帧闸门不误杀 | **能** | `ScriptedTransport` 支持 `DelayMs`，可注入"刚好卡在物理下界"与"刚好在下界之下"的帧，验证下界判定既不误杀也不放过 |
| 真实串口故障恢复 | **部分** | CRC 错/半帧/乱字节可以在 `SerialTransport` 层用伪流注入；端口消失这类要真机 |
| 控制写入端到端 | 否 | 必须真 PLC |
| 站 6 无响应根因 | 否 | 必须现场（接线/站号/设备本身） |
| D2000/D2001/D2207 语义 | 否 | 必须现场操作面板 |
| ZI6682 分流器倍数 | 否 | 必须现场发电工况 + 独立基准 |
| T1 与 D2206 最终命名 | 否 | 必须现场探头贴附对照 |

**结论**：8 项里有 **2 项可以靠离线对拍显著推进**（块读对拍、迟到帧闸门），
它们正是最需要"在真机上先验一遍"的两项 —— 离线先把逻辑钉死，真机只需要复核。

---

## 5. 建议的下一批工作顺序

1. **P1-6 删死字段**（低风险，一次性）—— 顺带把 `ModeCompatibilityWarning` 这种
   "只被测试读"的模式掐掉。
2. **P2-9 文档对齐**（低风险）—— 验收清单 D 段尤其要紧，它是现场签字用的。
3. **离线对拍两项缺口**（中风险，收益高）—— 块读端到端对拍 + 迟到帧闸门边界注入。
4. **P1-8 站 6 离线状态上屏**（中风险）—— 让"设备没接"和"程序卡住"在界面上可区分。
5. **P1-7 块读是否启用**（高风险）—— 上面那张实测表已经说明：现在不划算，
   等站 6 根因清楚后再评估。
