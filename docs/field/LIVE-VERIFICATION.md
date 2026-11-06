# 实跑验证记录（用桌面控制插件驱动真实程序）

> 目的：无头测试只能证明"代码逻辑自洽"，证明不了"程序在真机上用起来是对的"。
> 这份记录是**启动真实 exe、用 UI Automation 逐步操作、再拿运行产物做判据**的结果。
> 每一轮都留可复核的数字。

## 方法

1. 在 `test-output\ui-live\` 放一套干净运行时（exe + 配置 + SQLite），不预置 `settings.json`。
2. 用 `cmd /c start` 完全分离启动（**不要用会等就绪的启动器：它超时后会终止进程**）。
3. `desktop_windows` → `desktop_snapshot` 读控件树，`desktop_act` 点按/勾选/输入。
4. 判据优先用产物（会话日志、SQLite、审计文件），其次才是界面文字。

### 环境注意事项

- **窗口可能被系统放到屏幕外**（实测出现过 y=1958，屏幕高 1600）。放到屏幕外会让
  UI Automation 枚举变慢甚至超时。用 `desktop_windows -move` 先挪回屏幕内。
- 每读一次控件树约 26–28 秒（这个程序的控件量大）。控件超过约 1500 个之后，
  单次读取会超过 30 秒上限，此时改用 `query` 按名字/类型定位，或改用进程内驱动。
- 沙箱里进程可能无权在 `%TEMP%` 下建目录 ⇒ 测试的 scratch 目录必须建在 `--root` 下。

## 已验证通过（真实 GUI 驱动）

| 项目 | 判据 | 结果 |
|---|---|---|
| 启动与首屏 | 控件树可读 | 状态栏 `○ 链路 · 未连接` `⏸ 采集 · 已停止` `│` `▤ 记录 · 待机` `☁ 云端 · 未启用`；右侧 `点表已载入 · 4 台设备 / 37 个测点` |
| 连接 | 按钮变「断开连接」；端口/刷新被锁定 | 通过 |
| 每轮行数 | 会话日志逐轮计数 | **27 轮，每轮 37 行，0 个 SKIP** |
| 落库完整性 | SQLite | `999 行 / 37 个测点 / 27 轮`（999 = 37×27，三项吻合） |
| 卡片取值 | 单元素读取 | T1 显示 `24`；温度卡 7 张均有值 |
| 设备分母 | 状态栏 | `● 链路 · 模拟 · 4/4 设备` |
| 门禁安全 | 运行控制页 | `门禁 主机温度设定 尚未现场确认（confirmed=…）` |
| 零写入 | 审计 | 无 `control-events.jsonl`；`control_events` 表 0 行 |
| 搜索定位 | 卡片相对窗口的位置 | 搜 `0006H` 后卡片从窗口外 y=1724 → 窗口内 y=601 |

## 本轮发现并修复

### ① 搜索"找到了"但没滚到（`DashboardForm.FocusCard`）

- 现象：真实程序里搜 `0006H`，卡片仍在窗口外（y=1724，窗口只到 1314），界面毫无反应。
- 两个原因：
  1. 找视口用 `FindControl` 在整棵 `TabControl` 里搜 `AutoScroll` 面板，会命中**别的页面**；
     滚动了也看不见。改为由 `OverviewPageBuilder.Build` 直接返回视口引用。
  2. 定位公式 `card.Parent.Top + card.Top` 漏掉 `section` 在 `stack` 里的偏移，
     越靠后的分组偏得越多。
- 修复：用屏幕坐标换算 —— `PointToClient(card.PointToScreen())` 得到视口内位置，
  加上当前滚动量得到内容坐标，再设 `AutoScrollPosition`（setter 收正值）。
- 回归：新增断言"搜索必须把末尾分组的卡片滚进可视区（y ∈ [0, 视口高)）"。

### ② 随包配置被判为无效（`ControlConfig.Validate`）

- 现象：运行控制页门禁显示 `门禁 mode 只能为 disabled 或 prev…`。
- 原因：0.8.0 把随包 `mode` 改成 `live`，也加了 `WritesEnabled` 接受 live/preview，
  但 `Validate` 的模式白名单没跟着改 ⇒ 配置进 `config_error`。
  写入确实被挡住了（安全），但提示词与随包文件自相矛盾，现场会以为程序坏了。
- 修复：白名单改为 `disabled / live / preview`，并回报实际收到的值。
- 回归：`mode=live` 必须通过校验、`preview` 仍通过、未知 mode 被拒、随包配置能通过校验。

### ③ 测试在受限环境假失败

- 现象：存储套件报 `UnauthorizedAccessException`（无法在 `%TEMP%` 下建目录），
  掩盖真正的缺陷；另外收尾的 `Directory.Delete` 因 SQLite 句柄未及时释放而抛错。
- 修复：scratch 目录改建在 `--root` 下（符合"产物留在仓库内"的约定）；
  收尾删除改为带 GC 的重试且不抛（断言已跑完，清理失败不该决定成败）。

## 未结：数据记录页持续吃 CPU

**现象（可复现）**

| 状态 | 5 秒内 CPU | 内存 |
|---|---|---|
| 全新启动，**未进**数据记录页 | **+0.02 s（≈0.4% 单核）** | 63 MB |
| **进过**数据记录页之后 | **+2.3 s（≈77% 单核，持续）** | 868 MB |

进程 `Responding=true`（消息泵活着），所以不是死锁，是**空转**。
访问一次「数据记录」页之后就一直保持，直到关程序。

**已排除**

- `StartCount`：开头就是 `if(disposed||following||countFilter==null)return;`，
  跟随模式下根本不跑汇总计数。
- `SetToNow`：有 `updatingFollowTime` 保护，改 `to.Value` 不会触发 `MarkDirty`。

**下一步（进程内驱动，不依赖 UI Automation）**

在测试进程里 `new DashboardForm(...)` → `form.MainNavigation.SelectedIndex=3` →
让消息泵跑 5 秒 → 测 `Process.TotalProcessorTime` 增量与数据库读操作计数增量。
按结果二分：若换到其它页就不再烧 CPU，锁定在 `HistoryPage`；
再用"空数据目录"复跑，区分"页面逻辑空转"与"数据量导致的重复渲染"。

**目前影响**：功能不受影响（数据能查、能导出），但现场让程序停在数据记录页
会持续占一个 CPU 核并吃掉近 1 GB 内存，笔记本上会明显发热、掉电快。
