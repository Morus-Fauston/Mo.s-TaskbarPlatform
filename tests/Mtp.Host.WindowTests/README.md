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
