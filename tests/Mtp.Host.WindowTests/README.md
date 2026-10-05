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

## 五期实况岛组织

使用 `-Scenario organization -EvidenceDirectory .scratch/五期开发/evidence/06/<新批次>` 串行执行真实 SDK、生产设置和自有内容岛组织回归。Host 的 `--organization-demo` 提供合并/分别显示、共享活动、展开、混排及许可演示。脚本归档 `organization-*` 日志与截图；每次使用新证据目录。自有离屏内容岛不代替真实任务栏和多屏人工确认。
