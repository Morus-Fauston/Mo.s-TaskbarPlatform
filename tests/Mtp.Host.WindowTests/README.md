# WinUI Window Regression

Run on Windows with an interactive desktop and the Host Windows App SDK prerequisites:

```powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1
```

构建输出仍在 `bin/window-regression/`。`Run.ps1` 将每轮新产生的日志、截图与报告归档到独立批次目录，包括失败运行留下的材料。输出中的归档路径是本轮长期引用入口。

**证据落脚点**（五期15 起）：`-EvidenceDirectory <路径>` 精确指定批次目录；`-EvidenceRoot <根路径>` 只给根，批次子目录自动生成为 `<场景>-<时间戳>-<随机>`；两者都不给时落到中性暂存区 `.scratch/evidence-staging/`。此前默认值写死 `.scratch/二期开发/evidence/HostWindowRegression/`，导致不管在做哪期、只要忘了传路径就落进二期目录，现已改为不再硬编码期次。暂存区是 scratch 位置：确认为要长期引用的证据后，再归档到 `.scratch/<期次>/evidence/<票号>/<批次>/`。

二进制身份副本按场景分流：`combination-stability` 只写 `binary-hashes.json` 与 `binary-identity-redirect.txt`，二进制本体入共享哈希池 `.scratch/tool-cache/binary-pool/`（按 SHA256 取回）；其他场景仍把本体存到 `binary-identity/`，因为 `Verify-HostEvidence.ps1 -CapturedBinaryDirectory` 要从该目录重新哈希匹配报告身份。

2026-10-05 之前的已有日志、console 和 display-selection 材料已迁至 `.scratch/二期开发/evidence/HostWindowRegression/before-layout-20261005/`。原始报告中的采集时路径与二进制身份保留，迁移后仍按报告身份核对，不能因后续重新构建而把旧报告当成新证据。

### 本项目构建输出布局（五期15）

`bin/` 与 `obj/` 是 `.gitignore` 忽略的构建产物，**可整体删除，下次构建自动重建**。本项目的输出容易堆成多个平级目录，因为存在两种布局：

| 构建方式 | 输出布局 |
|---|---|
| 走 `Run.ps1`（内部 `-p:OutDir=`） | `bin/window-regression/`（**扁平**，固定一个目录） |
| 直接 `dotnet build` | `bin/<Configuration>/<TFM>/`（标准布局，如 `bin/Debug/net10.0`） |

再叠加"给 `-OutputDirectory` 传场景名"（历史上出现过 `acrylic-composition`、`phase06-acceptance`、`preview-regression` 等），同一个 `bin/` 下就会并排多套命名。TFM 迁移也会留残留：`Mtp.Host` 早期是 `net10.0`，改用 WinUI 后是 `net10.0-windows10.0.26100.0`，旧目录不会自动清理。

清理约定与脚本用法见[测试层 README 的"构建产物与目录整洁"](../README.md#构建产物与目录整洁)：统一走 `Run.ps1` 固定输出到 `bin/window-regression`，清理用 `tests/Clean-BuildOutput.ps1`。

该独立进程通过反射实例化 `Mtp.Host.Legacy.Windows` 中的历史 05A/05C 贴靠窗口，在屏幕外显示它，并跨 dispatcher 轮次、重复布局、缩放、隐藏、恢复与重建检查原生窗口样式与完整客户区矩形。它校验 NOACTIVATE 与 TOOLWINDOW 被保留、隐藏保持窗口标识、恢复不夺取前台焦点，且重复布局不会把该窗口抬到另一个置顶窗口之上。一个原生测试窗口发出 WinEvent，用于验证实际的事件订阅、UI 派发延迟与关闭清理。失败时进程以非零码退出，并把 `window-tests.log` 写入它自己的隔离构建输出。它还记录当前任务栏／显示环境的只读快照及其读取耗时。它同样在屏幕外实例化生产 MainWindow（使用隔离测试输出目录中的全新偏好）。真实 ComboBox 选择与弹出事件覆盖贴靠窗口关闭／打开两种状态下的反复目标切换、每次选择只提交一次偏好、保存失败回滚，以及缺失目标仅在弹出关闭后才被重新协调（含生产的一秒刷新定时器）。显示选择用例现在使用测试自有原生父窗口之下的生产内容岛。它不启动 Host 应用的 App、不读写用户偏好、不修改 Explorer，也不关闭已存在的 Host。运行器同时要求进程退出码为零并出现完成标记。它与 `dotnet test Mtp.sln` 分开（后者不初始化 WinUI 应用）。

这些原生测量不替代对透明度、输入或任务栏位置的人工验收。

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

### 证据与二进制身份（五期15）

组合场景会反复运行（每次改动跑冒烟、每个大版本跑长跑）。为记录"这批证据由哪个二进制产生"，
运行器原本把完整二进制身份集复制进**每个**批次目录，于是副本线性累积：实测 14+15 票共
2861 个二进制文件、1274 MB，而**唯一哈希只有 179 个**。

因此 `combination-stability` 改为**内容寻址**：

- 二进制入共享池 `.scratch/tool-cache/binary-pool/`（池内缺失才复制，可用 `-BinaryPoolDirectory` 覆盖）；
- 批次只写 `binary-hashes.json`（`Path`/`Hash`/`PoolFile`）与 `binary-identity-redirect.txt`；
- 池内文件命名为 `<sha256 前 16 位>_<原文件名>`，**按哈希即可取回，不依赖索引**。

单批次证据目录由约 **3322 KB 降至约 105 KB（−96.8%）**。

**其他场景保持原行为**：仍把本体复制到证据的 `binary-identity/`，因为
`Verify-HostEvidence.ps1 -CapturedBinaryDirectory` 要从该目录重新哈希以匹配报告身份。
哈希清单、日志与 jsonl 原始采样在任何场景下都不参与去重。
