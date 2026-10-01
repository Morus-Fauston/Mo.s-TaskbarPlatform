# Taskbar Island Lab — 05F

独立 WinUI 内容岛实验工具。正式 Host、Core、Contracts、生产偏好和声明均不接入本工具。运行实验不表示透明、任务栏同步、输入或性能已通过人工验收。

## 人工验收快速入口

双击 [Start-Demo.cmd](Start-Demo.cmd)，或在仓库根目录执行 `& tools/TaskbarIslandLab/Demo.ps1`。中文菜单提供真实 WinUI 演示、透明/材质对照、普通顶级窗口和由维护者显式选择的任务栏模式；每轮自动新建证据目录与人工观察表。停止当前实例用终端 Q、右键 +1 → 关闭实验，或 Stop.ps1。

逐项操作、通过标准、失败取证与回传格式见 [人工验收手册](人工验收手册.md)。先做透明/输入等关口，再做任务栏和性能；不会自动选择 Explorer、重启外壳或标记人工验收完成。演示菜单只是现有实验的启动器，0 Hz 为默认交互配置；原生界面与测量实现不变。

## 构建与自动化

在仓库根目录、Windows PowerShell 5.1 或 PowerShell 7 中执行。使用仓库现行的 .NET 10、Windows App SDK `2.4.0`、Windows SDK BuildTools `10.0.28000.2270`；运行机器必须安装匹配的 Windows App Runtime。实际 OS、.NET 和加载的 WinUI/WindowsAppRuntime DLL 版本记录在 `events.jsonl`，配对脚本另记录 SDK、GPU/驱动清单、分支与提交。GPU 清单不是使用率测量。

```powershell
dotnet restore tools/TaskbarIslandLab/TaskbarIslandLab.csproj
dotnet restore tools/TaskbarIslandLab/Tests/TaskbarIslandLab.Tests.csproj
dotnet build tools/TaskbarIslandLab/TaskbarIslandLab.csproj --configuration Release --no-restore
dotnet test tools/TaskbarIslandLab/Tests/TaskbarIslandLab.Tests.csproj --configuration Release --no-restore
dotnet build tools/TaskbarIslandLab/Logic/TaskbarIslandLab.Logic.csproj --configuration Debug --no-restore
dotnet format tools/TaskbarIslandLab/TaskbarIslandLab.csproj --no-restore --verify-no-changes
dotnet format tools/TaskbarIslandLab/Logic/TaskbarIslandLab.Logic.csproj --no-restore --verify-no-changes
dotnet format tools/TaskbarIslandLab/Tests/TaskbarIslandLab.Tests.csproj --no-restore --verify-no-changes
& tools/TaskbarIslandLab/Run.ps1 -OutputDirectory '.scratch/二期开发/verification/05F/owned-check'
```

默认运行 `owned / verify`：只创建本进程父窗口与本线程 Win32 H，经 H 初始化 DWXS；不枚举或绑定 Explorer。使用非激活显示请求，自动检查前台 HWND 是否被自身显示步骤改变。正常约数秒完成，内部超时默认 45 秒；外部 watchdog 另给启动和退出时间。自有窗口会短暂显示；这属于自动化夹具，不是 Agent 进行人工视觉验收。

真实集成断言覆盖 XAML 控件树已加载、`260 × 56 DIP` 内容、宿主和 bridge 客户区、原始 DPI、尺寸变化、隐藏并恢复、创建销毁、父关系丢失、父级先销毁、显式重建、旧 token 拒绝更新，以及 host/source/attach 三处初始化中断后的清理。144 DPI 对应 `390 × 84 px`。纯逻辑测试另覆盖清理失败时保留所有权、阻止重建及重试关闭。它们均不能证明真实 Explorer 输入、外观或同步成立。

工具不在主解决方案中，所以还须执行票据要求的主线检查：

```powershell
dotnet build Mtp.sln --configuration Release --no-restore
dotnet test Mtp.sln --configuration Release --no-restore
dotnet format Mtp.sln --no-restore --verify-no-changes
git diff --check
```

## 参数与退出

验收脚本位于本工具的 Tests 目录，兼容 Windows PowerShell 5.1 与 PowerShell 7。Debug Logic 构建用于满足 dotnet format 的默认设计时依赖。以下命令从仓库根目录执行，每次换用新输出目录：

~~~powershell
& tools/TaskbarIslandLab/Tests/Check-ProcessBoundaries.ps1 -OutputDirectory '.scratch/二期开发/verification/05F/process-check'
& tools/TaskbarIslandLab/Tests/Verify-PairedEvidence.ps1 -PairedDirectory '.scratch/二期开发/verification/05F/current-paired-run' -OutputDirectory '.scratch/二期开发/verification/05F/paired-readback'
~~~

前者检查非法参数退出码、Stop.ps1 清理和 Run.ps1 的成功/失败入口；后者回读 2026-09-28 的固定配对矩阵（none、alpha 0.5、144 DPI、14 次运行），不会重新启动性能采样，也不修改原数据。415 项检查是证据一致性检查，不是单元测试数或性能合格判断。旧的临时脚本及失败输出保留为历史证据，后续使用上述入口。

`Run.ps1` 参数映射为可执行程序的 `--name value` 参数。参数必须成对、唯一；未知参数、无效数值和隐式 Explorer 请求退出 `2`。输出目录必须新建，不覆盖已有日志。

| 参数 | 范围 / 含义 |
| --- | --- |
| `-Mode` | `owned`（自有父级）、`top-level`（普通 WinUI Window 同内容）、`empty`（无窗口/内容，仅相同应用运行时与计时调度）、`explorer`（仅显式人工选择） |
| `-Scenario` | `verify`（仅 owned）、`measure`、`manual`、`list-targets`（仅 explorer 的只读候选列表） |
| `-Material` / `-Alpha` | `none/acrylic/mica`；`0/0.5/1`。Alpha 只改变 XAML 根色刷，不代表合成表面透明已成立。Mica 是不透明材质。 |
| `-Theme` | `system/light/dark`，默认跟随系统。控件使用系统 Fluent 资源。 |
| `-Hz` | `0/1/30/60`，是请求更新配置，不是呈现帧率。 |
| `-VisibleSeconds` / `-HiddenSeconds` | 性能可见动态阶段 / 隐藏继续更新阶段；默认 `600/20` 秒。 |
| `-TimeoutSeconds` | verify 默认 45 秒；manual 默认 1800 秒。measure 时限由阶段总时长决定。 |
| `-X` / `-Y` | 自有/顶级模式下为屏幕像素坐标，默认 `80/80`；Explorer 的 X 为父客户区横坐标，Y 固定为 0。本票不做产品布局。 |
| `-Monitor` / `-ParentHwnd` | Explorer 模式必须同时提供，从只读候选列表逐字复制设备名和 HWND。 |
| `-Diagnostics` | 打开每次更新的 JSON 记录，仅用于单独诊断成本对照，正式性能数据默认关闭。 |
| `-OutputDirectory` | 本轮唯一证据目录；含 `launcher.json`、`events.jsonl`、`summary.json`。 |

可执行程序：`tools/TaskbarIslandLab/bin/Release/net10.0-windows10.0.26100.0/TaskbarIslandLab.exe`。只接受当前开发契约，不提供旧实验兼容入口。

停止只针对指定实验目录：

```powershell
& tools/TaskbarIslandLab/Stop.ps1 -OutputDirectory '.scratch/二期开发/verification/05F/owned-check'
```

该命令写入 `stop.request`，应用约 250 ms 内检查并清理。也可右键 `+1` 按钮，选择“关闭实验”。正常顺序：失效化回调 → 关闭 Popup/Flyout/动画 → 清理内容与事件 → Dispose DWXS（其内部拥有并关闭 bridge）→ 销毁 H → 销毁自有父窗口。Explorer 从不被销毁、关闭或重启。

父 HWND 已先销毁时，系统可能已关闭底层内容岛；此时跳过 DWXS 的内容/材质 setter，只做 Dispose。初次开发曾在失效后设置 `Content = null` 触发原生访问冲突，回归夹具固定覆盖了这一真实故障。工具保留异常、清理顺序及所有权结果。

| 应用退出码 | 含义 |
| --- | --- |
| `0` | 指定自动化完成，或 manual 正常停止；不表示人工验收通过。 |
| `1` | 初始化、集成断言或运行失败。 |
| `2` | 参数或应用启动边界失败。 |
| `3` | 父关系、HWND、DPI 等失效，停止本实例；没有独立窗口成功替代。 |
| `4` | 应用内清理失败；查看日志，不能声称干净退出。 |

原生崩溃保留系统原始退出码。外部 watchdog 先请求正常退出，必要时只终止本次 `Process` 对象，写 `forcedTermination=true`；这种结果不是清理成功。`Run.ps1` 遇到非零应用码、缺 summary、提前停止的测量或未完成的 verify 会抛错，不以仅存活或进程退出代替通过。

## 性能采样

2026-10-01 Popup 定位修复：原固定右下偏移及根边界裁剪已改为以 +1 为锚点、优先向上、允许越出内容岛的原生 Popup。默认 owned/verify 增加屏边布局、关闭按钮调用、隐藏及原生窗口清理回归；修复前后记录见 [Popup 修复验证](../../.scratch/二期开发/verification/05F/popup-fix-20261001/验证记录.md)。真实 Explorer 外观和自动隐藏残留仍由维护者单项复验，步骤见人工验收手册 C 节。本次新二进制未重跑长时性能，旧性能证据保留原身份。

先用同一环境、无高频日志、无屏幕录制跑自有父级与顶级窗口。长时配对工具依次启动独立进程，不同时运行两种承载，也不会启动 Explorer 实例：

```powershell
& tools/TaskbarIslandLab/Measure-Pairs.ps1 -OutputDirectory '.scratch/二期开发/verification/05F/paired-run'
```

脚本覆盖 empty、top-level、owned 的静态/1/30/60 Hz；30 Hz 的 top-level、owned **可见动态阶段各至少 600 秒**。其他可见阶段默认 20 秒，可用 `-ShortSeconds 5` 做最小基线；隐藏阶段默认 20 秒，最少 2 秒。empty 的短阶段是关闭实验内容的调度/进程基线，时间长度与长时配对不同，必须保留这一差异。短阶段不用于证明长期资源稳定。

每次按预热 5 秒 → 静态空闲 10 秒 → 可见动态 → 隐藏但数据继续更新 → 恢复 10 秒执行。阶段以实际单调时钟下限推进，因此长阶段不会因 timer 边界提前而少于 10 分钟。empty 不创建控件，仍保留相同计时与 dispatcher 请求负载，其实际内容更新数为 0。

单独运行与汇总：

```powershell
& tools/TaskbarIslandLab/Run.ps1 -Mode owned -Scenario measure -Hz 30 -VisibleSeconds 600 -HiddenSeconds 20 -OutputDirectory '.scratch/二期开发/verification/05F/owned-30'
& tools/TaskbarIslandLab/Run.ps1 -Mode top-level -Scenario measure -Hz 30 -VisibleSeconds 600 -HiddenSeconds 20 -OutputDirectory '.scratch/二期开发/verification/05F/window-30'
& tools/TaskbarIslandLab/Summarize.ps1 -InputDirectory '.scratch/二期开发/verification/05F/paired-run'
```

`events.jsonl` 保留每秒的资源快照；`summary.json` 分阶段汇总；`phases.csv` 供比较。计时器的请求数与 UI 线程实际更新数分别记录，繁忙时合并待执行更新，不伪造帧数。呈现帧率未测，不能用请求频率或文字更新数代替。

CPU 主口径为 `Δ进程 CPU 时间 / Δ墙钟 × 100%`（单逻辑核心等效），整机口径再除逻辑处理器数。`AverageProcessCpuMsPerUpdate` 是进程总成本的平均估计，含调度、XAML、采样和日志，不是一次渲染的精确耗时。原始数据同时包含私有内存、工作集、线程与句柄变化。观察持续增长时，先区分预热/GC/运行时懒加载和持续泄漏，不靠终点差或单个短阶段直接下结论。

GPU 使用/引擎、DWM/Explorer 增量、唤醒/上下文切换未启用可靠的 ETW/计数器采集，明确记录“未测及原因”；不填 0，也不声称 GPU 零开销。另报采样与 JSON 写入的墙钟开销，脚本末尾串行运行短时 diagnostics off/on 对照。日志开销已包含在进程成本中；这不是从性能结果扣除开销的校准公式。

没有正式 CPU/GPU 预算。历史位图路线的数据不作为本次测量或合格阈值，最终是否适合常驻由维护者判断。机器仍由用户正常使用，系统竞争、遮挡和其他负载可能影响结果；须记录实际条件。

## Explorer 人工入口

以下命令由维护者执行。第一步仅列出当前任务栏候选，不绑定：

```powershell
& tools/TaskbarIslandLab/Run.ps1 -Mode explorer -Scenario list-targets -OutputDirectory '.scratch/二期开发/verification/05F/targets'
Get-Content -LiteralPath '.scratch/二期开发/verification/05F/targets/targets.json'
```

从 `targets.json` 选择**一个**底部任务栏，复制 `monitor` 和 `window.hwnd`。下面的 `0x123456` 必须替换为实际值：

```powershell
$targetMonitor = '\\.\DISPLAY1'
$targetParent = '0x123456'
& tools/TaskbarIslandLab/Run.ps1 -Mode explorer -Scenario manual -Monitor $targetMonitor -ParentHwnd $targetParent -Material none -Alpha 0.5 -X 80 -TimeoutSeconds 1800 -OutputDirectory '.scratch/二期开发/verification/05F/explorer-manual'
```

启动前会校验 HWND 存活、类名、Explorer 进程、对应屏幕及底部任务栏。H 先以隐藏的本线程 Win32 窗口创建，用 H 初始化 DWXS，再调整 H 的子窗口样式并绑定所选父级；DWXS 从不直接以 Explorer HWND 初始化。记录进程/线程、父链、样式、DPI awareness 及实际尺寸。初始化时 H 保持隐藏，避免桥创建中途直接显示。

父级隐藏、移动和裁剪不等于绑定失败；窗口、父关系或 DPI awareness/缩放失效时停止该实例，不抢 Z 序、不调整整个进程的 DPI 模式。显式实验失败没有自动回退的“成功嵌入”。句柄失效后需重新列举目标并显式启动新实例。

同内容固定高度 56 DIP；真实任务栏可能比它矮，裁剪需记录，不为了通过测试偷改尺寸。X 只是诊断位置，维护者应选无其他操作冲突的位置；工具不识别、重排或协调第三方任务栏工具。

## 人工第一道关口

先复制 `observations.template.md` 到本轮证据目录并填写。每步都记录“通过/失败/未知”、实际结果和证据文件；任一关键反例出现，停止扩大范围。下列清单都仍需人类确认。

1. **环境与对照**：记录 Windows build、GPU/驱动、所选屏幕、DPI/缩放、刷新率、HDR、深浅主题、透明效果与自动隐藏设置、SDK/Runtime、分支和提交。先用 `-Mode owned -Scenario manual` 和 `-Mode top-level -Scenario manual`，再使用 Explorer 模式；每次使用新目录、相同材质、alpha、尺寸和 Hz。所有模式共用 `LabContent.xaml`。
2. **透明正反例**：在能区分透明与不透明的动态彩色背景前，分别启动 `-Material none -Alpha 0`、`0.5`、`1`；不可用同色背景自证透明。可由维护者在浏览器打开本目录 `Backdrop.html`，放到目标屏幕并用 F11 覆盖/恢复。先确认 alpha=1 的不透明反例可识别，再比较 0/0.5。保留截图或录制及背景条件。诊断图像不参与显示链路或性能计时。
3. **材质和系统变化**：单独启动 `-Material acrylic`、`-Material mica`，与 none 对照；实际 Desktop Acrylic 应能取到外部背景，应用内模糊不算。Mica 不透明不能判为透明通过。切换系统深浅主题和透明效果开关，观察控件与根表面；API 日志只报告挂载成功或失败，不代表实际材质成立。
4. **控件、焦点与热区**：操作 `+1`、开关和滑块；观察原生悬停、按下、焦点及计数文字的淡入动画。点击后用 Tab/Shift+Tab 循环、方向键操作控件。初次显示、隐藏恢复时观察原前台应用是否保持焦点。分别点击透明空白和控件区域，记录空白是否阻断底层点击。无激活与键盘可用性分开判断；工具未通过禁用输入来伪造无激活。
5. **UIA**：用维护者的 Accessibility Insights 或 Inspect 查找“实验计数”“增加本地计数”“本地开关”“本地滑块”，核对名称、控件类型、可访问操作与焦点。若工具不可用，记未知；原生 XAML 控件的存在不能代替 UIA 验收。
6. **关键任务栏反例**：先做 F11 覆盖/恢复、全屏时唤出任务栏、自动隐藏中途反向三组；再检查 Alt+Tab、Win 键、父级移动与裁剪。记录提前/延后、残影、全屏上独立浮出和隐藏后热区。需要测延迟时保留同步录制并注明捕获刷新率、误差；不把“没看出延迟”写成零延迟，也不继承 05E 的阈值。
7. **Popup / Flyout**：右键 `+1` 打开 MenuFlyout，再点“切换最小 Popup”；观察越界、裁剪、焦点、独立浮出 HWND，以及任务栏隐藏和实例关闭后的残留。其余浮出窗口只属于实验的诊断夹具，不是 MTP 正式浮窗体系。工具在失效/关闭时释放它们；轮询的逻辑可见性不能保证合成动画中的同步，须记录真实结果。
8. **父级重建**：由维护者在自己选择的安全时机重启 Explorer/重建任务栏，确认旧实例以失效原因结束、没有孤儿窗口/热区；重新列举并显式创建实例。不适合重启时保留未知。Agent 和脚本均不会重启 Explorer。
9. **Explorer 性能**：只有核心视觉/行为成立后，使用相同参数运行以下人工采样。观察 `events.jsonl` 的 `phase-begin`；进入隐藏阶段时由人类操作任务栏隐藏，恢复阶段时恢复。Explorer 模式从不修改父级显隐，阶段名是操作提示，不是可见性事实。记录人类操作时间与偏差；若未按阶段操作，该阶段不能充作隐藏成本证据。

```powershell
& tools/TaskbarIslandLab/Run.ps1 -Mode explorer -Scenario measure -Monitor $targetMonitor -ParentHwnd $targetParent -Material none -Alpha 0.5 -Hz 30 -VisibleSeconds 600 -HiddenSeconds 20 -OutputDirectory '.scratch/二期开发/verification/05F/explorer-30'
# 在另一终端查看阶段，或通过 Stop.ps1 请求退出：
Get-Content -LiteralPath '.scratch/二期开发/verification/05F/explorer-30/events.jsonl' -Wait
```

10. **维护者结论**：共同审核原生性与成本，给出继续、否决或另行立项的结论。未测多屏、混合 DPI、其他 Windows/GPU、HDR 和独占全屏等保留未知。本工具不实现 1/4/8 组件、多屏压力、正式恢复策略或新渲染进程。

## 实现范围与证据边界

代码由本仓库实验独立实现，未复制外部项目代码或私人资料。API 使用依据为当前 SDK 元数据与仓库 SYS-006 所引用的公开 API 生命周期；研究中的建议不自动作为已验证事实。

- `Logic/`：参数边界、生命周期和度量定义；无 WinUI/Win32 依赖。
- `Windows/`：自有 HWND、DWXS、父级选择与身份/DPI 检查；所有原生调用集中于此。
- `LabContent.xaml`：三种承载共用真实 XAML 控件树，没有 bitmap readback、截图显示或逐帧整窗复制。
- `Program.cs`：应用生命周期、自动化夹具、请求与实际更新计数、采样及输出。
- `Tests/`：工具专属纯逻辑测试，不改平台核心的领域契约。

原始日志保存在 `.scratch/二期开发/verification/05F/`。最终人工验收前，票据保持 `ready-for-agent` 并注明“自动化完成，待人工验收”；不自动采纳为正式任务栏承载路线。
