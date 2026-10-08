# WinUI Window Regression

Run on Windows with an interactive desktop and the Host Windows App SDK prerequisites:

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1
```

构建输出仍在 `bin/window-regression/`。`Run.ps1` 将每轮新产生的日志、截图与报告归档到本地 `.scratch/二期开发/evidence/HostWindowRegression/run-时间戳-唯一标识/`，包括失败运行留下的材料；只另存 Host DLL/EXE 的身份副本到 `binary-identity/`，不复制全部运行库。可用 `-EvidenceDirectory` 指定新的独立批次目录。输出中的归档路径是本轮长期引用入口。

2026-10-05 之前的已有日志、console 和 display-selection 材料已迁至 `.scratch/二期开发/evidence/HostWindowRegression/before-layout-20261005/`。原始报告中的采集时路径与二进制身份保留，迁移后仍按报告身份核对，不能因后续重新构建而把旧报告当成新证据。

This separate process instantiates the historical 05A/05C dock from `Mtp.Host.Legacy.Windows` through reflection, shows it offscreen,
and checks native frame styles and the full client rectangle across dispatcher turns, repeated
layout, resizing, hiding, restoration, and recreation. It checks that NOACTIVATE and TOOLWINDOW
are retained, hiding preserves window identity, restoration keeps foreground focus, and repeated
layout does not raise the dock over another topmost window. A native test window emits a WinEvent
to verify the actual event subscription, UI dispatch latency, and shutdown cleanup.
The process exits nonzero on failure and writes `window-tests.log` in its isolated build output.
It also logs a read-only snapshot of the current taskbar/display environment and its read duration.
It also instantiates the production MainWindow offscreen with fresh preferences in the isolated
test output directory. Real ComboBox selection and popup events cover repeated target changes
with the dock closed/open, one preference commit per selection, failed-save rollback, and a missing
target reconciled only after popup closure (including the production one-second refresh timer).
The display selection cases now use the production content island beneath an offscreen, test-owned native parent.
It does not launch the Host application's App, read/write the user's preferences, modify Explorer,
or close an existing Host. The runner requires both a zero process exit code and a completion marker.
Keep it separate from `dotnet test Mtp.sln`, which does not initialize a WinUI app.

These native measurements do not replace human acceptance of transparency, input, or taskbar placement.
## Explorer 旧探针清理回归（2026-10-04）

`ExplorerProbeCleanupRegression` 在同一 WinUI 进程内使用测试自有原生父窗口与旧探针窗口，交替执行父级强制销毁、正常 Detach，并复用旧适配器四轮。原实现第一轮在 `Window.Close` 发生 `0xc0000005`；此前修复同时检查失效句柄并在 MTP 窗口的原生销毁通知中完成 WinUI 关闭。05M 将这些源码及已有修复移到测试专用项目，生产 Host 不再依赖它们。此历史回归不操作 Explorer，不代替新内容岛的人工验证。

## 05M 当前 Host 控制台与内容岛

`HostConsoleRegression` 直接使用生产 MainWindow、协调器、报告层和内容岛宿主，仅把 Explorer 目标换成测试自有父窗口。覆盖：

- 原生初始化/绑定失败、父级销毁、迟到事件、重复清理与新建。
- 各分区真实 WinUI 控件调用，显隐/偏好、模拟/清除/重试、互斥、Popup、停止、关闭 Host。
- 15 个材质/alpha/主题组合，0/1/30/60 Hz 负载、长测预设接线、短时限自动停止。
- 原始报告回读、证据目录打开目标、历史错误保留、测试结束遵守最新偏好。
- `IslandPreviewRegression` 覆盖独立预览：声明隐藏时仍可显式打开，真实按钮/开关/滑块与 Popup、材质参数、重复打开、原生移动不重建、客户区尺寸、标题栏关闭/重开、迟到关闭隔离、与任务栏案例双向互斥，以及 Host 关闭时清理预览和 Popup。`preview-controls.png` 只证明控件树呈现，不证明桌面透明效果或手动拖动体感。
- `PreviewStartupRegression` 在连接的屏幕上首次打开 none/Acrylic 预览，在任何强制布局、截图或移动前检查 `XamlRoot.IsHostVisible`、加载及尺寸；再在屏幕间原生移动，检查 DPI 与可见性。`preview-startup.jsonl` 记录原生与 XAML 状态和 Acrylic 自有父级初始化 HRESULT。即便控件树已加载，`IsHostVisible=false` 仍判失败；Acrylic API 返回成功不等于人类确认透明。
- 常规及最小窗口布局截图。控制台最小客户布局约束为 520×540 DIP，过小窗口请求会恢复至可操作尺寸。截图仅检查控制台布局，不证明任务栏材质或输入。

输出中的 `console-*` 是自动化夹具证据，所有人工结果保持未测。真实隐藏/恢复按钮调用仅验证记录入口，不是人类观察。

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Verify-HostEvidence.ps1 -EvidenceRoot '<console-本轮目录>'
```

回读已归档报告时增加 `-CapturedBinaryDirectory '<本轮归档目录>/binary-identity'`，仍严格匹配报告内的 DLL/EXE 哈希，避免后续构建覆盖原路径后失去身份依据。迁移时保存的旧身份副本只对应迁移时当前二进制，不自动匹配更早的报告。

独立脚本核对报告与原始样本、CPU 两种口径、二进制哈希及缺失指标原因。测试不运行完整 30 分钟，不替维护者处置 05K/05L。

## 五期实况岛组织与任务栏浮窗

实况岛组织通过生产设置页与独立 SDK 样例验证，使用 `-Scenario organization`。任务栏浮窗使用 `-Scenario flyout` 验证自有多窗口、前台与原始输入，`-Scenario flyout-production` 验证真实 SDK 请求、动作确认和会话恢复。`-Scenario flyout-scheduling` 对窗口应用跨越动画截止时刻、原生关闭异常重试、退场键盘禁用和待发请求取消做有界回归；它使用自有窗口焦点，须与其他桌面回归串行。

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario organization -EvidenceDirectory .scratch/五期开发/evidence/06/<新批次>
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario flyout -EvidenceDirectory .scratch/五期开发/evidence/07/<新批次>
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario flyout-production -EvidenceDirectory .scratch/五期开发/evidence/07/<另一新批次>
```

共用桌面与构建输出必须串行。证据目录不得复用；原始失败保留，只有实际完成标记和全部断言成功才通过。普通 WinUI 自有窗口与离屏内容岛证据不代替真实 Explorer 任务栏、实际多屏或人工体验确认。Host 演示入口分别为 `--organization-demo`、`--flyout-demo`，各自启动受控模拟业务，不接管系统音量或其他应用窗口。

## 普通短提示与设置（五期08）

`Run.ps1 -Scenario hint -EvidenceDirectory <新的独立目录>` 串行执行普通WinUI提示、可替换时钟、关联组上/下/限高布局、实际帧等宽与连续转向、减少动画、Close异常资源隔离、跨进程SendInput和真实设置控件。独立目标进程必须记录Press/Release/Click各一次并exit0；不得把透明样式检查单独当作穿透证明。总runner上限60秒；单动画段8秒/256帧。`-Scenario hint-window`只复核窗口边界及保存实际原生和内容截图，便于定向视觉检查。

`-Scenario flyout-production`在既有真实SDK/Broker/Host链路中补验notice请求、同HWND刷新、八方位及覆盖许可、隐藏准入、关联动作失败重计和组关闭。此夹具的内容岛与组位于自有离屏父窗口，屏幕几何显式注入；它证明生产通信/控件/窗口接线，不承诺Explorer或多屏支持。真实普通提示及跨进程点击使用前一场景。原生测量、截图、自动化交互均不代替维护者人工验收。

## 可交互短提示（五期09）

启动 `Mtp.Host.exe --interactive-hint-demo`，在设置显示 `counter/main/controls`，点击请求提示后使用模拟滑块、展开和失败按钮；不写系统音量或亮度。提示默认5秒，悬停、拖动捕获和显式键盘交互暂停，最后交互结束重新计时；确认值由SDK提供方返回，拒绝时回退。

`Run.ps1 -Scenario interactive-hint -EvidenceDirectory <新的独立目录>` 验证真实跨进程空白穿透、裁剪控件命中、拖出区域捕获、键盘、双窗口清理、计时和关联动画。`-Scenario interactive-hint-production` 验证真实SDK/Broker动作、请求、设置、展开、失败与重连；其内容岛使用离屏自有父窗口，不能代替真实Explorer验收。前景是唯一交互控件来源，背景共享确认快照；本地peer断言不等于人类读屏验收。

## 事件组与优先级（五期10）

`Mtp.Host.exe --event-demo` 启动独立SDK模拟事件提供方。显示counter/main/controls后，发送事件更新event0，持续事件打开event1，下一通道轮转event2至event11；组内确认显示SDK确认次数，演示失败产生所属组关联提示，详情/返回只访问声明面板。所有数据为模拟业务。

`Run.ps1 -Scenario event -EvidenceDirectory <新目录>` 串行验证真实WinUI组、8秒空闲及5秒保护、1/5/10上限与降额收敛、同通道更新、关闭失败隔离、关联提示定位/动画及Host自有窗口优先级。替换判断前同步核对实际鼠标/焦点，不等待下一计时器采样。`-Scenario event-settings`使用真实ComboBox/NumberBox/Toggle自动化接口及独立进程回读，覆盖八位置、数量、显示开关和保存失败复位。`-Scenario event-production`走真实SDK/Broker/Host请求、确认/失败/设置/有限重连；使用自有离屏内容岛，不能代替Explorer或多屏人工验收。

## 五期恢复与组合交付

`-Scenario visual-environment` 检查真实 WinUI 控件在模拟密度／文字比例变化下的原位重测和固定 32 DIP 组高。`-Scenario environment-recovery` 使用生产 Controller／Adapter 与自有 Win32 父窗口，验证初始化失败、有限手动重试、父级失效及恢复、隐藏偏好回读、UISettings 回调合并和订阅清理。它们不修改 Explorer 或系统文字比例。

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario combination-stability -EvidenceDirectory .scratch/五期开发/evidence/12/<新批次>
```

组合场景在同一 Host 中使用 7 个独立 SDK 服务、独立 Broker 和自有离屏内容岛，执行 50 轮业务组合；第 10／20／30／40 轮分别触发 Broker 恢复、自有父窗口重建、SDK 恢复和减少动画切换。580 秒取消业务，50 轮及全部清理必须在 600 秒内完成才通过；外部 runner 620 秒防挂只终止测试进程并记失败，不保证卡死时全部子进程退出。超时或轮数不足都不记通过。

### 分层入口（五期15）

同一夹具、同一套断言、同一套错误分类，只用 `-CombinationProfile` 选择跑多少：

```powershell
# 冒烟层：3 轮、约 20-25 秒，进日常
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario combination-stability -CombinationProfile smoke -EvidenceDirectory <新批次>
# 长跑层：50 轮、约 5-6 分钟，每个大版本跑一次
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario combination-stability -CombinationProfile soak -EvidenceDirectory <新批次>
```

| | 冒烟层 `smoke` | 长跑层 `soak` |
|---|---|---|
| 轮数 / 预算 | 3 轮 / 180 秒 | 50 轮 / 600 秒 |
| 实测耗时 | 约 21 秒 | 约 300 秒（含显式等待点） |
| 故障注入 | 无（表为空） | 第 10/20/30/40 轮 |
| 资源趋势 | **不适用**（10 轮预热 + 至少 5 个有效样本，3 轮不可能出趋势） | 全量平台化判据 |
| 覆盖 | 岛加载、主题、业务项组合、动作与模板调用、任务栏组请求与 panel→child→back 导航、短提示、交互提示（owner 组／滑块／展开）、事件窗创建与确认、反向展开、计时暂停恢复与批量展开、组织合并拆分、一次性收尾断言 | 冒烟层全部内容 **+** 累积效应、资源趋势、四类故障恢复 |

单轮覆盖不到：资源趋势（一轮无趋势）、故障恢复（注入点在第 10/20/30/40 轮）、累积效应。故两层共存，不用单轮取代 50 轮。

### 错误分类与退出码（五期15）

所有失败路径归入四类之一，退出码可区分——用来回答"这是软件问题、夹具问题、环境问题还是资源问题"：

| 退出码 | 分类 | 含义 | 是否重试 |
|---:|---|---|---|
| 0 | passed | 全部轮次完成、断言通过、清理已验证 | — |
| 10 | `software-failure` | 生产未遵守五期契约（组未到 panel、动作未确认、组未释放…） | 否 |
| 11 | `fixture-failure` | 本夹具自身错误 | **否**（14 票曾因重试掩盖真实缺陷） |
| 12 | `environment-failure` | 机器干扰（真实桌面输入、服务重启慢） | 是，且有界 |
| 13 | `resource-failure` | 资源天花板或超出预算 | 否 |
| 14 | `unclassified-failure` | 分类器缺陷，按夹具处理 | 否 |

分类规则、退出码契约与全部重试点都写入每轮的 `policy.json`。当前重试点共 4 个，全部归环境类且有界：`taskbar-panel-restore`（20s）、`interactive-owner-group`（20s）、`event-confirm`（20s）、`invoke-com-retry`（2s）。

`MTP_COMBINATION_INJECT_FAILURE=software|fixture|environment|resource` 可注入受控反例，验证各类确实可被触发；`-InjectFailure` 是它的 runner 开关，注入轮次的退出码与分类由 runner 校验。

### 桌面输入隔离（五期15）

`-IsolateInput`（环境变量 `MTP_COMBINATION_ISOLATE_INPUT=1`，**默认关闭**）显式暂停生产的真实输入订阅。默认关闭时运行测量的是"产品原样"，包括契约要求的"组外输入关闭组"。隔离状态与重新施加次数写入 `policy.json` 与结果摘要（`desktopIsolation` / `inputPauses`），报告可区分"已隔离"与"未隔离"。

隔离必须**反复施加**，不能只在轮首做一次：生产在每次创建浮窗组时会自行 `TryStart` 观察器，单次 `TryStop` 会在该轮下一个组出现时被静默撤销（`evidence/15` 实测：只暂停一次时，开关记录为已隔离但仍有 17 次外部输入关闭）。

### 资源趋势判据（五期15，依维护者 2026-10-07 裁决）

资源趋势改为**平台化 + 绝对值报告**双判据，不再以 `handleDelta ≤ 256` 一票否决：

- **平台化**（主判据）：后段（观测窗口后半）句柄与私有内存的最小二乘斜率 ≤ 1/轮 ⇒ 判"无持续增长"；
- **绝对值报告**：`handleDelta / tailHandleSpan / privateBytesDelta` 与总内存如实报告，`256 / 32 / 128 MB` 保留为**初始参考值**，不单独决定红绿。

功能断言、清理断言与故障恢复保持原有严格度，未因该裁决放宽。

对照基线（同为 50 轮存活条件）：

| 臂 | 轮次 | 模式 | `handleDelta` | 后段斜率 | 平台化 | `privateDelta` |
|---|---:|---|---:|---:|---|---:|
| 软件侧 idle 臂（无业务动作） | 50/50 | isolated | **3** | **0/轮** | **是** | 3.3 MB |
| 正常臂（五期15） | 50/50 | **live** | 537 | 14/轮 | 否 | 51 MB |
| 正常臂（14 票 run26） | 50/50 | live | 512 | 未测（旧口径） | 未测 | 104 MB |

增长主体是夹具每轮真实创建的业务浮窗与 peer，不是生产泄漏：同一夹具、同一存活轮数，去掉每轮业务动作后 handleDelta 由 537 降到 3。趋势未平台化时报告如实记录，但**不据此判红**——功能、清理与预算判据保持原严格度。

`combination-stability-*` 保留策略、逐轮结果、CPU／句柄／内存趋势、队列峰值和资源清理回读；CPU 为累计进程时间，无通过阈值。句柄／私有内存按 PID 世代丢弃前 10 次预热采样，至少五次有效采样才判定；**平台化与绝对值报告**取代原来的 256 句柄／128 MiB 净增长硬门与末五次波动门，不跨 PID 比较。通过还要求全部记录的自有 PID、HWND、实际组计时器及 Host 生命周期资源清零。源文件与本批测试／Host／Broker／SDK 二进制哈希独立归档。共享构建及桌面测试须串行，失败批次不得覆盖。自有窗口自动化结果不替代真实任务栏、多屏／DPI、Explorer、材质、读屏和动画观感的人类验收。

### CPU 与内存口径（五期15）

报告里的 CPU 是**进程累计 CPU 时间**。引用时**必须同时给出两种口径**，否则相差逻辑处理器数倍：

| 口径 | 定义 | 与什么对齐 |
|---|---|---|
| 单核百分比 | `ΔCPU时间 / 挂钟 × 100%` | 负载本身有多重 |
| 全核百分比 | 单核 ÷ 逻辑处理器数 | **任务管理器**显示值 |

实测（9 进程合计 = Host + Broker + 7 个 SDK 服务）：idle 臂 20.08 s / 29.8 s = 单核 67.4%、全核（24）**2.81%**；
正常臂 153.01 s / 283.6 s = 单核 54.0%、全核 **2.25%**。这两个数是**夹具跑测试时**的负载，**不是待机占用**。

夹具只采 `Process.TotalProcessorTime`，**未按 P 核/E 核分别采样**，因此不能断言负载落在哪类核上
（本机为 24 核混合架构）。内存绝对值（idle 臂第 50 轮）：9 进程私有合计 246.5 MB，其中 **Host 自身 126.7 MB**、
Broker 约 34 MB、7 个 SDK 服务合计约 125 MB；真实单应用部署约 175 MB。两条臂 50 轮内存净变化均为负
（预热后回落），与"平台化"判定一致。待机占用、真实 Explorer 任务栏嵌入下的占用、小时级稳态均**未测**。
