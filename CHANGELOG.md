# Changelog

## v0.1.0-alpha.11 (2026-09-12 20:17)

### 05D 封存：实验性嵌入路线判定不可行

- **封存结论**：05D 的“实际 Explorer 任务栏父子窗口承载组件”路线判定不可行，本版本将其封存到独立实验分支，主分支不再保留该实现。判定依据是机制层面的实测失败，不是未验证。
- **不可行的组合**：WinUI 内容岛在 `WS_CHILD` 上无法承载透明。组合色刷加一次 GDI 表面绘制在子窗口上无效，绘制前后像素一致；`DwmExtendFrameIntoClientArea` 不能被内嵌子窗口使用，表面保持不透明；亚克力在 `WS_CHILD` 上返回成功但整个进程以 `E_HANDLE` 崩溃，属于会绕过降级契约的生产风险。
- **同一条路线上未被排除的机制**：分层子窗口可用。`WS_EX_LAYERED` 加 `UpdateLayeredWindow` 在同一任务栏子窗口上能逐像素控制 alpha，不透明层、全透明层与半透明层的读数可明确区分。该机制要求丢弃 WinUI 控件树、自行把内容画进位图，代价与收益需另立项评估，本版本不采用，也不把“子窗口不能透明”当作结论。
- **关键时序约束**：分层子窗口必须先挂进任务栏、再首次提交图层。若在顶层阶段提交过一次图层，之后即使 `SetParent` 成功、样式重设加 `SWP_FRAMECHANGED`、`UpdateLayeredWindow` 返回成功，图层也永久不再显示；改为挂载后才首次提交则图层正常。该约束已由四组时序对照确认。
- **位图路径可行性**：`RenderTargetBitmap` 返回预乘 BGRA 且保留 alpha，透明、半透明与抗锯齿边缘像素齐备，可直接交给 `ULW_ALPHA`；物理尺寸为 DIP 尺寸乘窗口 DPI 比（现场 1.50 倍），此前“尺寸无法解释”的记录已更正。
- **保留资产**：05A/05B 的偏好、生命周期、降级与窗口所有权能力不受影响；05A 保持维护者已验收状态。

### 实验性探测工具

- **`--taskbar-surface`**：透明契约回归。断言顶层 MTP 窗口在任务栏之上按契约顺序处理后保持透明；对内嵌子窗口只测量、不断言，避免未来修复反而让该组失败。
- **`--child-material`**：子窗口色刷夹具，校验色刷连接与一次表面绘制。
- **`--layered-child`**：分层子窗口对照探针，用 A–L 组区分“窗口没画”与“图层生效”，含不透明控制组。
- **`--acrylic-child`**：系统材质在子窗口上的行为与崩溃复现。
- **`--render-target`**：`RenderTargetBitmap` 预乘、透明与尺寸换算验证。
- **`--show-surface` / `--show-layered`**：供人直接观察的展示模式，不作为自动判定。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release --no-restore` 通过，共 209 个测试成功，0 个失败，0 个跳过。
- **构建**：`Mtp.sln` 与 `tests/Mtp.Host.WindowTests` Release 构建 0 个警告、0 个错误。
- **真机探针**：分层子窗口 A–L 组、亚克力组与位图组均在本机真实 Windows 复现，关键项至少两次；日志保存在 `tests/Mtp.Host.WindowTests/bin/window-regression/`。
- **人工验收**：本版本不主张任何人工验收结果；探针像素读数与展示模式不能替代维护者肉眼确认。

### 范围边界

- **实验分支**：本版本承载 05D 嵌入实验与配套探测工具，不构成正式兼容承诺；Explorer 窗口类名、窗口树与 `SetParent` 仍属实验性 Windows 适配边界。
- **不承诺**：多屏精细同步、混合 DPI、任意遮挡计算与第三方任务栏兼容均不在范围内。
- **未决项**：`材质与透明实现契约` 与 `SYS-003` 中“子窗口承载不可行/机制已穷尽”的措辞是否修订，待维护者决定；分层子窗口路线是否立项另议。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `tests/Mtp.Host.WindowTests/LayeredChildProbe.cs` | **新增** — 分层子窗口 A–L 时序对照探针 |
| `tests/Mtp.Host.WindowTests/TaskbarSurfaceRegression.cs` | **新增** — 顶层透明契约断言与子窗口只测不断言 |
| `tests/Mtp.Host.WindowTests/RenderTargetProbe.cs` | **新增** — 预乘 BGRA、alpha 与尺寸换算验证 |
| `tests/Mtp.Host.WindowTests/AcrylicChildProbe.cs` | **新增** — 子窗口系统材质行为与崩溃复现 |
| `tests/Mtp.Host.WindowTests/TaskbarSurfaceShowcase.cs` | **新增** — 四种表面组合的人工展示模式 |
| `tests/Mtp.Host.WindowTests/LayeredChildShowcase.cs` | **新增** — 分层子窗口的人工展示模式 |
| `tests/Mtp.Host.WindowTests/Program.cs` | **修改** — 新增六个探针运行入口 |
| `tests/Mtp.Host.WindowTests/README.md` | **修改** — 说明可选用途、依赖与红色运行条件 |
| `CHANGELOG.md` | **修改** — 记录本版本 |
| `CHANGELOG.txt` | **修改** — 记录本版本 |

## v0.1.0-alpha.10 (2026-09-12 18:21)

### 05D 实际嵌入任务栏父子窗口与动态锚点探针

- **父子窗口承载**：承载路线由“MTP 自有顶级窗口”改为实际 Explorer 任务栏子窗口。绑定前保存原始样式与父级，去掉顶级样式位并置 `WS_CHILD`，调用 `SetParent`，绑定后校验实际父句柄；只有样式切换失败、`SetParent` 失败、父句柄不一致、绑定后立即丢失或 Explorer 重建后无法重绑才算嵌入失败，失败时恢复原样式与原父级并回退独立贴靠窗口。
- **嵌入失败定义收窄**：材质、布局、DPI 与输入问题不再伪装成绑定失败，单独保留诊断。新增失败码分类，只有绑定类错误才触发重绑与降级路径。
- **材质方案**：改用带 alpha 的纯色色刷（默认 `0xFFFFFF`、透明度 0.1）。色刷连接后执行一次必要的 GDI 表面绘制，确认 `WS_CHILD` 路径的透明结果，不复用已知在子窗口路径不兼容的顶级窗口 DWM 材质；材质模式默认值同步改为纯色绘制。
- **显隐改为父子同步**：子窗口显隐与父窗口移动由原生父子关系同步，Host 响应尺寸、锚点、目标显示器与 Explorer 重建变化；删除以“前台窗口是否全屏”和自动隐藏收起状态驱动的显隐判定。
- **动态探针**：旧的一次性通知区域位置读取改为动态刷新，任务栏位置、通知区域矩形、父句柄与窗口尺寸变化时重新读取更新，监听相关窗口事件并保留定时兜底。探针只记录任务栏/子窗口关系与锚点变化，不再驱动独立窗口显隐。
- **锚点不再静默回退**：探针放置移除 `fixed_inset` 兜底路径，通知区域锚点不可用时返回 `explorer_probe_tray_anchor_unavailable`，不再默默贴到任务栏右端。
- **05C 清理**：删除 Core 层 `TaskbarVisibilityPolicy` 与 `Win32TaskbarVisibility` 及其测试，仅保留诊断用 `TaskbarVisibility` 枚举；前台全屏判定、自动隐藏收起判定与“暂时隐藏优先于降级”的独立策略退出运行链路。05A/05B 的偏好、生命周期、降级与窗口所有权能力保留。
- **故障恢复**：Explorer 重启、目标显示器变化与绑定失败后的独立降级不遗留子窗口、旧父句柄、事件订阅或迟到回调；通过关系丢失重绑、延迟清理、失败重试与降级回归验证。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release --no-restore` 通过，共 209 个测试成功，0 个失败，0 个跳过。
- **构建与格式**：Release 构建 0 个警告、0 个错误。
- **窗口回归入口**：`tests/Mtp.Host.WindowTests/Run.ps1` 标准 WinUI 窗口回归 9 个原生帧样本通过。
- **子窗口色刷回归**：新增 `ChildMaterialRegression`，绑定父级 fixture 后请求带 alpha 纯色色刷、校验连接状态并执行一次 GDI 表面绘制，进程无崩溃，可选 `--child-pixels` 校验像素混合；证据位于 `.scratch/二期开发/verification/05D/runtime/child-material.log`。
- **单元测试**：`Refresh()` 委托并保持 Embedded 状态，父句柄变化触发重绑路径；`preferEmbedded` 分支与动态锚点放置均有覆盖。
- **人工验收**：真实 Windows 的任务栏自动隐藏收起/展开、全屏中唤出、普通桌面、Alt+Tab、Explorer 重启、输入、透明度、无白边、DPI 与故障恢复仍待维护者人工验收。

### 范围边界

- **维持单屏底部任务栏**：仍是单目标屏幕、底部任务栏与通知区域左缘锚点，不扩展多屏精细同步、混合 DPI、任意遮挡计算或正式第三方任务栏兼容承诺。
- **实验性适配边界**：Explorer 窗口类名、窗口树与 `SetParent` 仍属实验性 Windows 适配边界，未通过真实 Windows 验证前不标记为正式兼容。
- **不新增能力面**：未新增 SDK、Broker、业务动作、浮窗或外部应用进程探测。
- **人工验收未代填**：票据保持 `ready-for-agent`，人工验收项由 Agent 留空。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Platform.Core/TaskbarVisibility.cs` | **新增** — 仅用于诊断保留的旧呈现状态枚举 |
| `tests/Mtp.Host.WindowTests/ChildMaterialRegression.cs` | **新增** — 子窗口带 alpha 色刷与 GDI 表面绘制的原生回归 |
| `src/Mtp.Platform.Core/TaskbarVisibilityPolicy.cs` | **删除** — 05C 独立显隐判定退出运行链路 |
| `src/Mtp.Host/Win32TaskbarVisibility.cs` | **删除** — 前台全屏与自动隐藏状态读取退出 |
| `tests/Mtp.Platform.Core.Tests/TaskbarVisibilityPolicyTests.cs` | **删除** — 随可见性策略一并移除 |
| `tests/Mtp.Platform.Core.Tests/Win32TaskbarVisibilityTests.cs` | **删除** — 随可见性读取器一并移除 |
| `src/Mtp.Host/Win32ExplorerTaskbarEmbedAdapter.cs` | **修改** — `WS_CHILD`/`SetParent` 绑定与父级校验、失败清理、动态 `Refresh`、带 alpha 纯色色刷 |
| `src/Mtp.Host/ExplorerTaskbarProbeController.cs` | **修改** — 新增 `Refresh` 与绑定失败重绑编排 |
| `src/Mtp.Host/ExplorerTaskbarProbePlacement.cs` | **修改** — 移除固定内缩兜底锚点，缺锚点返回结构化失败 |
| `src/Mtp.Host/ExplorerTaskbarProbeReport.cs` | **修改** — 默认材质改为纯色绘制并带 alpha |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml.cs` | **修改** — 新增子窗口组合色刷与释放路径 |
| `src/Mtp.Host/HostDisplayActionController.cs` | **修改** — 新增 `preferEmbedded` 与 `RefreshPresentation` |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 定时器改为刷新嵌入呈现，移除独立环境监视接线 |
| `src/Mtp.Host/TaskbarEnvironmentMonitor.cs` | **修改** — 事件过滤只跟随目标任务栏窗口 |
| `src/Mtp.Host/Win32TaskbarDockEnvironment.cs` | **修改** — 停止读取前台全屏与自动隐藏可见性 |
| `src/Mtp.Host/App.xaml.cs` | **修改** — 以 `preferEmbedded` 启动接线 |
| `tests/Mtp.Host.WindowTests/Program.cs` | **修改** — 接入子窗口材质回归入口 |
| `tests/Mtp.Host.WindowTests/README.md` | **修改** — 说明新增子窗口回归 |
| `tests/Mtp.Platform.Core.Tests/ExplorerTaskbarProbeTests.cs` | **修改** — 覆盖动态刷新与父句柄变化重绑 |
| `tests/Mtp.Platform.Core.Tests/ExplorerTaskbarProbePlacementTests.cs` | **修改** — 对齐动态锚点与缺锚点失败 |
| `tests/Mtp.Platform.Core.Tests/HostDisplayActionTests.cs` | **修改** — 覆盖 `preferEmbedded` 分支 |
| `tests/Mtp.Platform.Core.Tests/Win32TaskbarDockEnvironmentTests.cs` | **修改** — 对齐可见性判定移除 |
| `CHANGELOG.md` | **修改** — 记录本版本 |
| `CHANGELOG.txt` | **修改** — 记录本版本 |

## v0.1.0-alpha.9 (2026-09-12 15:51)

### 05A 单屏右贴靠任务栏组件

- **几何与定位**：新增 Core 层 `TaskbarDockPlacement`，按目标显示器底部任务栏、通知区域左缘锚点和 DIP 换算计算组件位置；空锚点、越界、空间不足、非底部任务栏和无效显示器均返回结构化错误，明确不允许静默回退到任务栏右端。
- **偏好存储**：新增 `TaskbarDockPreferences`，显示开关、目标显示器和 0–64 DIP 右侧间距独立持久化；采用命名互斥锁与同目录原子替换，文件不可读、损坏或提交失败时保留旧值，不破坏当前窗口状态。
- **窗口适配**：新增 `TaskbarDockWindowAdapter` 与 `WinUiTaskbarDockWindow`，复用既有独立窗口接口统一拥有窗口、偏好与任务栏绑定；目标显示器缺失或锚点不可用时回退到工作区右下角独立贴靠并显示可解释状态。
- **会话绑定**：嵌入会话绑定窗口身份、Explorer 进程实例、任务栏句柄、显示器和几何；重复相同重排不新建会话，环境或窗口变化才重新绑定，清理未确认时保留对象并阻止重建以便重试关闭。
- **白边修复**：现象是组件外围出现白色立体边框。根因是窗口创建误用工具窗口 presenter，导致 `WS_DLGFRAME`、`WS_SYSMENU` 与 `WS_EX_WINDOWEDGE` 残留并压缩客户区。修复为显式使用普通 presenter，同时保留 `TOOLWINDOW`/`NOACTIVATE` 原生定位。
- **透明绘制**：新增 `DeferredSurfacePaint`，在连接色刷后的低优先级 UI 队列执行一次 GDI 表面绘制以退出丢 alpha 的合成路径；窗口关闭取消未执行的绘制，绘制失败不改变组件状态或偏好。

### 05C 独立窗口任务栏行为（实验分支）

- **可见性策略**：新增 Core 层 `TaskbarVisibilityPolicy`，区分正常贴靠、全屏、任务栏收起和环境未知；暂时隐藏不写回显示偏好、不清空组件状态，并保留已有原因与诊断。
- **全屏判定**：以目标屏幕前台窗口的客户区几何作为全屏依据，排除桌面/Shell 窗口和带标题栏的普通最大化窗口，保留无边框全屏判定；不查询游戏进程名、不打开其进程、不发送控制消息，普通最大化不再误判为全屏。
- **自动隐藏任务栏**：通过目标屏幕底部自动隐藏任务栏查询结合实时矩形判断收起与展开；收起或动画中缺少可靠锚点时先隐藏组件，恢复时使用当前锚点，不留下点击热区或阻挡唤出区域。
- **刷新与生命周期**：进程外 WinEvent 订阅前台切换与窗口显示/隐藏/位置变化，仅对相关窗口排队并合并重复请求；250 ms UI 定时器作为订阅漏报兜底，显示器设置每秒刷新。退出先使排队刷新失效并解除事件，解除失败保留所有权并提示重试关闭。
- **焦点与层级**：只有首次显示或恢复才请求置顶，重复布局使用 `SWP_NOZORDER`；隐藏通过原生窗口隐藏移除输入表面，恢复复用同一窗口并重新验证无边框表面与延后绘制透明背景，避免抢焦点、闪烁重建和不必要的反复置顶。
- **优先级**：暂时隐藏优先于独立故障降级；环境不确定时先隐藏并保留原因，只有允许呈现且锚点故障时才独立降级。

### 文档与测试基建

- **术语**：`CONTEXT.md` 更新右侧避让锚点与独立贴靠窗口在任务栏显示与隐藏规则下的语义，明确“在任务栏区域呈现”不等于成为 Explorer 子窗口。
- **界面**：Host 主窗口新增目标显示器选择、右侧间距输入和靠右位置提示，原实验探针与材质对照移入可展开区域；主窗口改为可滚动布局。
- **窗口回归入口**：新增 `tests/Mtp.Host.WindowTests` 独立 WinUI 回归程序与 `Run.ps1`，直接实例化生产窗口在屏幕外检查首次显示、跨 Dispatcher 刷新、相同布局、移动缩放与关闭重建；运行前需构建，仅新建测试进程。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release` 通过，共 220 个测试成功，0 个失败，0 个跳过。
- **构建与格式**：Release 构建 0 个警告、0 个错误；`dotnet format --verify-no-changes` 与 `git diff --check` 通过。
- **红绿回归**：恢复旧的每次置顶调用使相对顺序检查失败，修复后通过；未接入隐藏分支时全屏、收起与未知三类用例错误显示降级窗口，接入后通过；仅看客户区会误判自定义标题栏最大化，加入样式排除后普通最大化与无边框全屏均通过。
- **原生窗口证据**：`Run.ps1` 完成多次边框与客户区测量，确认首次隐藏不显示、恢复保持 HWND 与前台焦点、重复布局不提升相对层级、事件解除后无迟到刷新。
- **人工验收**：05A 已由维护者在真实 Windows 确认位置、间距、透明、输入、锚点失败恢复、Explorer 重启与关闭再打开，正式收口；05C 的全屏游戏、视频与 Alt+Tab、自动隐藏任务栏、偏好优先、故障与恢复及生命周期组合仍待维护者验收。

### 范围边界

- **05C 为实验分支**：本版本沿用 MTP 自有透明顶级窗口路线，不把全屏游戏、多屏组件组、混合 DPI 或任意窗口遮挡计算标记为正式兼容承诺。
- **未决事项**：05C 的人工验收项未被 Agent 代填，票据保持待人工验收；Explorer 子窗口实验入口未改动，仍不构成正式支持。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Platform.Core/TaskbarDockPlacement.cs` | **新增** — 底部任务栏与通知区域锚点的纯几何右贴靠计算 |
| `src/Mtp.Platform.Core/TaskbarVisibilityPolicy.cs` | **新增** — 允许呈现、全屏、任务栏收起与环境未知的纯逻辑判定 |
| `src/Mtp.Host/TaskbarDockWindowAdapter.cs` | **新增** — 统一拥有窗口、偏好与任务栏绑定的适配器 |
| `src/Mtp.Host/WinUiTaskbarDockWindow.cs` | **新增** — MTP 自有贴靠窗口的显示、隐藏与定位 |
| `src/Mtp.Host/Win32TaskbarDockEnvironment.cs` | **新增** — 目标任务栏、通知区域与显示器几何读取 |
| `src/Mtp.Host/Win32TaskbarVisibility.cs` | **新增** — 前台全屏几何与自动隐藏任务栏状态判定 |
| `src/Mtp.Host/TaskbarEnvironmentMonitor.cs` | **新增** — WinEvent 订阅、刷新排队与定时兜底 |
| `src/Mtp.Host/TaskbarDockPreferences.cs` | **新增** — 显示、目标显示器与间距的独立持久化 |
| `src/Mtp.Host/DeferredSurfacePaint.cs` | **新增** — 连接色刷后的延后 GDI 表面绘制调度 |
| `src/Mtp.Host/MainWindow.xaml` | **修改** — 新增显示器与间距控件，探针移入可展开区域 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 接入任务栏可见性状态、设置与诊断展示 |
| `src/Mtp.Host/App.xaml.cs` | **修改** — 启动接线任务栏适配器与环境监视 |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml.cs` | **修改** — 配合延后绘制与窗口生命周期调整 |
| `src/Mtp.Host/Win32ExplorerTaskbarEmbedAdapter.cs` | **修改** — 抽离延后绘制并校正 GDI 返回值检查 |
| `CONTEXT.md` | **修改** — 更新锚点与独立贴靠窗口术语语义 |
| `tests/Mtp.Platform.Core.Tests/TaskbarDockPlacementTests.cs` | **新增** — 覆盖 DIP、间距端点、负原点与无效几何 |
| `tests/Mtp.Platform.Core.Tests/TaskbarVisibilityPolicyTests.cs` | **新增** — 覆盖可见性状态组合判定 |
| `tests/Mtp.Platform.Core.Tests/TaskbarDockAdapterTests.cs` | **新增** — 覆盖成功、降级、重排、恢复与关闭失败 |
| `tests/Mtp.Platform.Core.Tests/TaskbarDockPreferenceTests.cs` | **新增** — 覆盖保存失败保留旧值与原子替换 |
| `tests/Mtp.Platform.Core.Tests/TaskbarEnvironmentMonitorTests.cs` | **新增** — 覆盖事件排队、合并、兜底与解除 |
| `tests/Mtp.Platform.Core.Tests/Win32TaskbarDockEnvironmentTests.cs` | **新增** — 覆盖真实显示器读取与降级路径 |
| `tests/Mtp.Platform.Core.Tests/Win32TaskbarVisibilityTests.cs` | **新增** — 覆盖全屏与自动隐藏判定 |
| `tests/Mtp.Platform.Core.Tests/DeferredSurfacePaintTests.cs` | **新增** — 覆盖延后执行、取消与异常处理 |
| `tests/Mtp.Platform.Core.Tests/WindowSurfacePaintIntegrationTests.cs` | **新增** — 覆盖原生 GDI 绘制与句柄校验 |
| `tests/Mtp.Platform.Core.Tests/ExplorerProbeNativeCleanupTests.cs` | **新增** — 覆盖探针原生清理路径 |
| `tests/Mtp.Platform.Core.Tests/ExplorerProbeWindowOwnerTests.cs` | **修改** — 适配探针清理与所有权变化 |
| `tests/Mtp.Host.WindowTests/Mtp.Host.WindowTests.csproj` | **新增** — 独立 WinUI 窗口回归测试工程 |
| `tests/Mtp.Host.WindowTests/Program.cs` | **新增** — 屏幕外实例化生产窗口并校验边框与客户区 |
| `tests/Mtp.Host.WindowTests/WindowTestApplication.xaml` | **新增** — 回归测试宿主应用定义 |
| `tests/Mtp.Host.WindowTests/Run.ps1` | **新增** — 回归测试构建与运行入口 |
| `tests/Mtp.Host.WindowTests/README.md` | **新增** — 回归入口说明与查看方式 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.8 (2026-09-10 23:45)

### 状态完整性修复

- **已校验声明不可变**：现象是调用方可以改写已校验声明的输出集合，或绕过校验直接构造“已校验”对象。根因是三个声明类型以记录和可变集合暴露。修复为密封类、只读集合和内部构造函数，强制校验父子身份关系，并要求只能经 `DeclarationValidator` 创建。
- **偏好读取状态分类**：现象是“文件缺失”“内容损坏”“暂时读不到”被当作同一类，且暂时不可读时会用空偏好覆盖磁盘上的未知旧值。修复为区分 `Loaded / Missing / Invalid / Unavailable`；`Unavailable` 时拒绝写入并保留未知旧偏好。
- **偏好陈旧快照覆盖**：现象是两个 Host 实例或长时间运行后，内存里的旧偏好会覆盖磁盘上的较新内容。修复为提交前重新读取磁盘最新偏好并合并本次改动。
- **偏好原子写入**：现象是写入中断可能留下半份文件。修复为同目录临时文件加原子替换，失败时保留上一份完整文件并清理本次临时文件。
- **载荷上限**：现象是超大 JSON 会被整体读入内存。修复为声明与偏好在完整读取和反序列化前拒绝超过 1 MiB 的载荷，读取端按上限加一字节截断判断，写入端同样受限。
- **偏好写入并发**：同一偏好文件的写入改为使用按完整路径哈希命名的命名互斥量，短时等待后失败返回结构化错误，避免两个 Host 同时写坏文件。

### 窗口与生命周期修复

- **错误在组件隐藏时不可见**：现象是组件关闭后 Host 错误文本一起被隐藏。根因是错误文本位于组件可见性容器内部。修复为把错误文本移出该容器，并让加载结果汇总声明与偏好两类错误。
- **窗口关闭未确认即释放**：现象是关闭调用返回后立刻释放引用，即使窗口尚未真正关闭。修复为只有收到 `Closed` 确认才释放资源与关闭通知，未确认时返回 `dock_window_close_unconfirmed` 并保留重试入口。
- **显示失败后资源所有权丢失**：现象是显示或配置失败后适配器丢掉窗口引用，导致无法重试关闭。修复为失败时按真实 `IsOpen` 保留所有权与组件模型，并新增 `dock_window_closed_during_show` 处理显示期间被关闭的情况。
- **过期关闭事件**：修复为按引用核对关闭事件来源，忽略属于旧窗口的迟到事件。
- **诊断对照窗口所有权**：新增 `TopLevelControlWindowOwner`，只有收到关闭确认才释放；关闭失败保留重试入口、阻止 Host 本次退出，且不先关闭其他显示资源。
- **Explorer 探针生命周期**：原先只有“已嵌入/未嵌入”两态，清理进行中会被误报为已分离。修复为 `Detached / Embedded / CleanupPending` 三态，区分停止探针与 Host 退出两种意图，逐步校验隐藏、恢复父窗口、恢复样式和零句柄关闭确认，失败时保留所有权与可用重试入口，迟到的分离事件先恢复可用承载。

### 逻辑与架构优化

- **统一显示动作入口**：新增 `HostDisplayActionController`，把偏好修改、组件显示模型、独立贴靠窗口和探针诊断统一协调；启动恢复与用户切换走同一入口，UI 不再自行排列这些步骤，消除了两条路径行为漂移。
- **偏好提交接口单一化**：由“读全量再写全量”改为按稳定 ID 提交一次可见性变更，由存储层在锁内完成读取、合并与写入，避免调用方持有过期快照。
- **合成器共享**：现象是反复切换材质会泄漏合成器并导致进程崩溃。修复为进程内共享单个 `Compositor`，材质切换只重建画刷。
- **测试可见性**：新增 `InternalsVisibleTo` 程序集声明，使测试能通过真实内部边界验证失败与恢复路径。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release` 通过，共 149 个测试成功，0 个失败，0 个跳过；相比 alpha.7 新增 55 个测试。
- **构建结果**：Release 配置下 `dotnet build Mtp.sln --configuration Release` 成功，0 个警告，0 个错误；`dotnet format --verify-no-changes` 通过。
- **新增覆盖**：显示动作的显示、隐藏、保存失败、显示失败、关闭失败、启动恢复与退出重试；偏好独占锁恢复、历史稳定 ID 保留、陈旧快照合并、读取状态分类、双向载荷上限、受限读取、超长路径与原子替换失败；窗口初始化、配置、显示、清理、关闭确认与关闭重试；探针三态生命周期与迟到事件；对照窗口所有权；错误文本位于可见性容器之外。
- **静态隔离**：平台核心与公共契约中未引入 WinUI、Win32、P/Invoke、Explorer 或窗口句柄依赖。

### 待人工验收

- **真机项**：真实 Windows 上以隐藏状态启动并制造偏好读取或窗口承载错误，确认主窗口仍显示可复制的结构化错误；反复显示、隐藏和关闭独立贴靠窗口后重启，确认无遗留窗口且偏好按最后一次成功保存恢复；若存在可控的真实关闭失败入口，确认 Host 不把失败窗口报告为已关闭且后续可重试释放。若无法稳定制造关闭失败，将记录为证据缺口，不以测试替身结果代替。

### 范围边界

- **不含内容**：本版本不新增 SDK、Broker、媒体服务、动作回传、完整浮窗、安装更新或完整设置壳。
- **未决事项**：不改变 05A 的任务栏承载路线结论，05A 仍为阻塞状态；Explorer 嵌入仍为实验能力，不是正式支持。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Host/ValidatedDeclaration.cs` | **修改** — 已校验声明改为密封类与只读集合，强制身份关系 |
| `src/Mtp.Host/DeclarationValidator.cs` | **修改** — 增加 1 MiB 载荷上限与可信构造入口 |
| `src/Mtp.Host/DeclarationSource.cs` | **修改** — 按上限有界读取声明文件并去除 BOM |
| `src/Mtp.Host/ComponentDisplayPreferences.cs` | **修改** — 读取状态分类、原子替换、跨进程锁、载荷上限与合并提交 |
| `src/Mtp.Host/HostDisplayController.cs` | **修改** — 接入偏好管理器并汇总加载错误 |
| `src/Mtp.Host/HostDisplayActionController.cs` | **新增** — 统一协调偏好、显示模型、贴靠窗口与探针 |
| `src/Mtp.Host/IndependentDockWindowController.cs` | **修改** — 按真实打开状态保留失败态信息 |
| `src/Mtp.Host/WinUiIndependentDockWindowAdapter.cs` | **修改** — 保留资源至关闭确认并支持关闭重试 |
| `src/Mtp.Host/TopLevelControlWindowOwner.cs` | **新增** — 诊断对照窗口的关闭确认所有权 |
| `src/Mtp.Host/ExplorerTaskbarProbeController.cs` | **修改** — 三态生命周期、分离意图与清理校验 |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml.cs` | **修改** — 共享合成器并统一材质资源释放 |
| `src/Mtp.Host/Win32ExplorerTaskbarEmbedAdapter.cs` | **修改** — 探针资源所有权与清理确认 |
| `src/Mtp.Host/IndependentDockWindow.xaml.cs` | **修改** — 配合关闭确认与资源释放路径 |
| `src/Mtp.Host/MainWindow.xaml` | **修改** — 错误文本移出组件可见性容器 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 改用统一显示动作入口并展示聚合错误 |
| `src/Mtp.Host/App.xaml.cs` | **修改** — 启动接线显示动作控制器与恢复结果 |
| `src/Mtp.Host/Properties/AssemblyInfo.cs` | **新增** — 允许测试访问内部边界 |
| `tests/Mtp.Platform.Core.Tests/HostDisplayActionTests.cs` | **新增** — 覆盖显示、隐藏、失败与退出重试 |
| `tests/Mtp.Platform.Core.Tests/HostErrorPresentationTests.cs` | **新增** — 覆盖组件隐藏时的错误可见性 |
| `tests/Mtp.Platform.Core.Tests/TopLevelControlWindowOwnerTests.cs` | **新增** — 覆盖对照窗口关闭确认 |
| `tests/Mtp.Platform.Core.Tests/ExplorerProbeWindowOwnerTests.cs` | **新增** — 覆盖探针清理与迟到事件 |
| `tests/Mtp.Platform.Core.Tests/WinUiIndependentDockWindowAdapterTests.cs` | **新增** — 覆盖窗口资源初始化、显示与关闭重试 |
| `tests/Mtp.Platform.Core.Tests/DisplayPreferenceTests.cs` | **修改** — 覆盖锁、状态分类、上限与原子替换失败 |
| `tests/Mtp.Platform.Core.Tests/DeclarationLoadingTests.cs` | **修改** — 覆盖声明不可变与载荷上限 |
| `tests/Mtp.Platform.Core.Tests/ExplorerTaskbarProbeTests.cs` | **修改** — 覆盖三态生命周期与恢复 |
| `tests/Mtp.Platform.Core.Tests/IndependentDockWindowTests.cs` | **修改** — 覆盖失败态所有权保留 |
| `tests/Mtp.Platform.Core.Tests/HostDisplayModelTests.cs` | **修改** — 适配不可变声明与错误聚合 |
| `tests/Mtp.Platform.Core.Tests/Mtp.Platform.Core.Tests.csproj` | **修改** — 保持测试项目配置一致 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.7 (2026-09-09 21:46)

### 透明窗口修复

- **根因定位**：通过 `TransparencyLab` 真机对照确认，透明窗口在 DirectFlip/MPO 硬件平面路径下会丢失 alpha；对窗口 DC 执行一次 GDI 绘制后回落到普通 DWM 合成路径，alpha 恢复，透明窗口不再显示为灰色或不透明底。
- **透明承载**：`Win32ExplorerTaskbarEmbedAdapter` 新增一次性窗口表面绘制路径，并将默认探针透明模式调整为 `SolidPaint`；该路径只作用于 MTP 自己的窗口，不修改 Explorer 窗口属性。
- **材质模型**：平台核心新增无 Windows 依赖的 `MaterialSpec`、`MaterialCapabilities`、`MaterialResolution` 和 `MaterialResolver`，统一表达材质能力、请求材质、实际材质与运行时降级原因。
- **材质降级**：材质请求不被静默改写；当前环境不支持请求材质时按亚克力、云母、纯色、无材质顺序选择实际材质，保留原始请求和不透明度，并返回可读的降级原因。
- **语义校正**：明确区分纯色透明度、亚克力浓淡、云母亮度和无材质表面；云母是不透明材质，不会因为不透明度数值变小而透出后方窗口。
- **Host 控制**：主窗口新增材质选择和不透明度控制，显示实际生效材质及降级原因；透明顶级对照窗口和 Explorer 探针共享材质能力探测与应用路径。
- **稳定性**：材质合成器改为进程内共享，避免反复切换材质时重复创建 `Compositor` 导致崩溃；新增窗口句柄、显示器环境、像素结果和跨窗口对照诊断脚本，支持复核透明效果。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release` 通过，共 94 个测试成功，0 个失败，0 个跳过；新增材质解析、能力降级、不透明度约束和材质语义测试。
- **构建结果**：Release 配置下 `dotnet build Mtp.sln --configuration Release` 成功，0 个警告，0 个错误。
- **真实 Windows 验证**：在当前验证机器上确认一次 GDI 窗口表面绘制可恢复透明合成，解决透明窗口灰底/不透明问题；该结论属于实验证据，尚未覆盖不同 GPU 厂商、Windows 版本、HDR、独占全屏和其他显示环境。
- **Explorer 边界**：`WS_CHILD` 仍不支持 DWM 材质和窗口级透明；本修复解决的是 MTP 自有顶级窗口透明问题，不把 Explorer 子窗口嵌入标记为正式兼容能力。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Platform.Core/MaterialSpec.cs` | **新增** — 无 Windows 依赖的材质声明、能力、降级和语义模型 |
| `tests/Mtp.Platform.Core.Tests/MaterialResolverTests.cs` | **新增** — 覆盖材质保留、降级、不透明度约束和语义 |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml.cs` | **修改** — 共享合成器并支持统一材质应用、清理与能力探测 |
| `src/Mtp.Host/Win32ExplorerTaskbarEmbedAdapter.cs` | **修改** — 增加一次性 GDI 表面绘制，恢复透明合成路径并记录诊断步骤 |
| `src/Mtp.Host/ExplorerTaskbarProbeReport.cs` | **修改** — 扩展透明修复与实际材质结果报告 |
| `src/Mtp.Host/MainWindow.xaml` | **修改** — 增加材质选择和不透明度控制 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 接入材质解析、应用结果和降级状态显示 |
| `tools/TransparencyLab/LabWindow.xaml` | **修改** — 更新材质诊断界面 |
| `tools/TransparencyLab/LabWindow.xaml.cs` | **修改** — 增加窗口表面绘制、材质对照和像素/句柄诊断 |
| `tools/TransparencyLab/TransparencyLab.csproj` | **修改** — 配置新增诊断依赖 |
| `tools/TransparencyLab/probe-compare-windows.ps1` | **新增** — 对照窗口诊断脚本 |
| `tools/TransparencyLab/probe-hwnd-identity.ps1` | **新增** — 窗口句柄与身份诊断脚本 |
| `tools/TransparencyLab/probe-lab-pixels.ps1` | **新增** — 透明像素结果诊断脚本 |
| `tools/TransparencyLab/probe-lab-window.ps1` | **新增** — 诊断窗口状态脚本 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.6 (2026-09-09 20:11)

### Explorer 任务栏嵌入探针

- **探针边界**：新增 `ExplorerTaskbarProbeController` 与 `IExplorerTaskbarEmbedAdapter`，探针逻辑与 Windows 适配器分层；Explorer 窗口类名、窗口树与 `SetParent` 只存在于 `Win32ExplorerTaskbarEmbedAdapter` 私有实现，不进入平台核心、公共契约、声明模型或 SDK/Broker 协议。
- **几何计算**：新增 `ExplorerTaskbarProbePlacement`，按任务栏客户区坐标、通知区域左边缘锚点与 DIP 换算计算嵌入矩形，拒绝竖向任务栏、空矩形、零 DPI、空间不足与越界结果。
- **实验记录**：新增 `ExplorerTaskbarProbeReport` 与格式化器，逐步骤记录查找任务栏、测量、DPI、锚点、定位、子窗口化、定位校验与透明应用结果，固定标注“实验性，不代表正式支持”和五项未验证范围。
- **Host 入口**：Host 主窗口新增运行/停止探针、失败注入与透明实验模式选择；探针只接受当前已校验且可见的组件，隐藏或未声明组件在触碰适配器前被拒绝。
- **降级与恢复**：探针失败时清理子窗口、回退独立贴靠窗口并返回「任务栏嵌入当前不可用，已切换独立贴靠」；嵌入窗口被外部销毁时重置状态并恢复独立窗口，不自动重试；探针结果不持久化，不读写显示偏好。

### 透明机制结论与诊断工具

- **子窗口材质结论**：真实 Windows 证据显示 `WS_CHILD` 窗口被 DWM 全面拒绝——`DwmExtendFrameIntoClientArea` 返回 `E_INVALIDARG`、`DwmSetWindowAttribute` 返回 `E_HANDLE`、`DesktopAcrylicController.AddSystemBackdropTarget` 抛空引用；在顶级阶段附加材质后再 `SetParent` 会导致 Host 崩溃。嵌入窗口只能是不透明底，无法融入任务栏。
- **可用透明配方**：MTP 自有顶级窗口经多轮两屏验证，采用默认 presenter 去边框、剥除 `WS_CAPTION/WS_BORDER/WS_DLGFRAME/WS_THICKFRAME`、`DwmExtendFrameIntoClientArea(-1)`、`DesktopAcrylicController` 或 `MicaController`、`DWMSBT_NONE` 后透明稳定。
- **诊断工具**：新增 `tools/TransparencyLab/`（独立解决方案，不在 `Mtp.sln`、不参与主构建与测试），用于隔离变量对照四种材质与显示器信息，配套 `probe-windows.ps1` 与两张诊断截图；新增 `tools/.gitignore` 忽略其构建产物与本地临时输出。

### 文档

- **规划入口**：`AGENTS.md` 的开发规划链接更新为现行的 `Docs/当前决策/分期开发规划.md`。
- **术语语义**：`CONTEXT.md` 更新右侧避让锚点与实际避让锚点在“单屏右贴靠首片”下的含义：首片只接受通知区域左边缘作为嵌入锚点，锚点不可用时回退独立贴靠窗口，不强行贴到任务栏右端。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release` 通过，共 73 个测试成功，0 个失败，0 个跳过。
- **构建结果**：Release 配置下 `dotnet build Mtp.sln --configuration Release` 成功，0 个警告，0 个错误。
- **边界断言**：新增反射测试断言平台核心与公共契约不含任何 P/Invoke 方法；`src` 中 `SetParent`/`DllImport` 仅出现在 Win32 适配器。
- **人工验收**：维护者在真实 Windows 确认探针成功嵌入任务栏、停止探针恢复独立贴靠、失败注入降级、Host 重启恢复；探针结构路径可行，但嵌入窗口无法透明，不能融入任务栏。

### 范围边界

- **实验性能力**：Explorer 任务栏嵌入仍为实验适配器，不构成正式兼容承诺。多屏、DPI 变化、任务栏自动隐藏、Explorer 重启和第三方任务栏工具均未验证。
- **后续决策**：进入 05A 前需由维护者在当前决策文档确认承载路线（不透明嵌入或 MTP 自有透明顶级窗口），05A 当前为 blocked。透明色刷在主屏不透明的根因未定位，亚克力/云母为采信路径。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Host/ExplorerTaskbarProbeController.cs` | **新增** — 探针控制器、适配器接口、状态与结果模型 |
| `src/Mtp.Host/ExplorerTaskbarProbePlacement.cs` | **新增** — 任务栏客户区嵌入矩形与锚点计算 |
| `src/Mtp.Host/ExplorerTaskbarProbeReport.cs` | **新增** — 实验记录模型与状态文本格式化 |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml` | **新增** — 紧凑单行探针窗口布局 |
| `src/Mtp.Host/ExplorerTaskbarProbeWindow.xaml.cs` | **新增** — 探针窗口创建、透明材质与顶级对照模式 |
| `src/Mtp.Host/Win32ExplorerTaskbarEmbedAdapter.cs` | **新增** — Win32 查找、子窗口化、定位与 DWM 诊断适配器 |
| `src/Mtp.Host/App.xaml.cs` | **修改** — 启动时构造探针控制器并注入主窗口 |
| `src/Mtp.Host/MainWindow.xaml` | **修改** — 新增探针运行、停止、失败注入与透明模式控件 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 探针交互、状态文本与独立窗口协作 |
| `tests/Mtp.Platform.Core.Tests/ExplorerTaskbarProbeTests.cs` | **新增** — 覆盖成功、失败降级、拒绝、分离、丢失与报告格式化 |
| `tests/Mtp.Platform.Core.Tests/ExplorerTaskbarProbePlacementTests.cs` | **新增** — 覆盖锚点、DPI、方向、边界与越界拒绝 |
| `tests/Mtp.Platform.Core.Tests/PlatformSkeletonTests.cs` | **修改** — 新增核心与契约不含 P/Invoke 的反射断言 |
| `tools/TransparencyLab/` | **新增** — 独立透明诊断程序（不在 `Mtp.sln`），含源码、脚本与两张诊断截图 |
| `tools/.gitignore` | **新增** — 忽略 tools 下的构建产物与本地临时输出 |
| `AGENTS.md` | **修改** — 开发规划链接更新为现行分期开发规划 |
| `CONTEXT.md` | **修改** — 更新右侧避让锚点在单屏右贴靠首片下的语义 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.5 (2026-09-03 21:36)

### 独立贴靠窗口

- **Host 窗口承载**：新增 Host 拥有的独立顶级 WinUI 贴靠窗口，使用已校验且当前可见的组件模型显示内容；窗口不创建 Explorer 子窗口，也不使用 Explorer 句柄或 `SetParent`。
- **生命周期管理**：新增 `IndependentDockWindowController` 与 `IIndependentDockWindowAdapter`，统一管理窗口显示、关闭、用户关闭通知和结构化错误状态；承载失败或关闭失败时保留 Host 声明、组件模型和显示偏好。
- **受控边界**：隐藏组件、未声明组件和没有可见组件时拒绝创建窗口，返回可解释的结构化错误；窗口适配器与 WinUI 代码仅位于 Host，平台核心、公共契约和 SDK 模型不引入窗口对象或 Win32 依赖。

### 多屏定位修复

- **坐标语义**：新增 `IndependentDockWindowPlacement`，兼容 Windows App SDK 返回的相对工作区和已是屏幕坐标的工作区表示，确保副屏虚拟桌面偏移只应用一次。
- **边界保护**：窗口定位使用屏幕坐标 `MoveAndResize`，每次显示根据 Host 当前所属显示区域重新计算；工作区无效、窗口过大或计算结果越界时返回结构化错误，不静默把窗口放到屏幕外。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release` 通过，共 46 个测试成功，0 个失败，0 个跳过；覆盖承载生命周期、失败状态保留、隐藏/未声明组件拒绝，以及主屏、副屏非零虚拟坐标、相对/绝对工作区和越界防护。
- **构建结果**：Release 配置下 `dotnet build Mtp.sln --configuration Release` 成功，0 个警告，0 个错误。
- **回归护栏**：旧的副屏坐标算法可被定位测试捕获，恢复修复后完整测试通过；`src` 与 `tests` 中仅保留一处 `MoveAndResize` 调用，使用屏幕坐标重载。
- **人工验收**：维护者已在真实 Windows 上确认主屏与副屏非零虚拟坐标显示、任务栏附近位置、窗口关闭与 Host 重启恢复、关闭显示开关，以及承载失败时 Host、声明快照和偏好状态保持正常。

### 范围边界

- **后续议程**：本版本交付 MTP 自有独立贴靠窗口承载，不等同于 Explorer 任务栏嵌入正式支持；Explorer 嵌入仍属于后续实验性探针，正式兼容性必须经过独立的真实 Windows 验证。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Host/App.xaml.cs` | **修改** — 将独立贴靠窗口控制器接入 Host 启动和显示开关生命周期 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 根据组件可见性显示或关闭独立贴靠窗口，并处理承载错误 |
| `src/Mtp.Host/IndependentDockWindow.xaml` | **新增** — 独立贴靠窗口的最小组件布局 |
| `src/Mtp.Host/IndependentDockWindow.xaml.cs` | **新增** — MTP 自有顶级 WinUI 窗口、任务栏附近定位和无激活显示 |
| `src/Mtp.Host/IndependentDockWindowController.cs` | **新增** — 独立窗口生命周期、组件边界和状态错误管理 |
| `src/Mtp.Host/IndependentDockWindowPlacement.cs` | **新增** — 多屏工作区坐标计算与越界保护 |
| `src/Mtp.Host/WinUiIndependentDockWindowAdapter.cs` | **新增** — WinUI 独立贴靠窗口适配器 |
| `tests/Mtp.Platform.Core.Tests/IndependentDockWindowPlacementTests.cs` | **新增** — 覆盖主屏、副屏坐标、工作区模式、窗口尺寸和边界错误 |
| `tests/Mtp.Platform.Core.Tests/IndependentDockWindowTests.cs` | **新增** — 覆盖窗口承载、关闭、失败状态保留和组件拒绝 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.4 (2026-09-03 20:24)

### 组件显示偏好

- **独立偏好模块**：新增 `ComponentDisplayPreferences` 与 `LocalComponentDisplayPreferenceStore`，把组件显示偏好保存为与声明文件独立的本地 JSON（`display-preferences.json`），只记录完整分层稳定 ID 与可见性，不写回声明文件，也不能脱离当前有效声明生成组件。
- **控制器合并**：新增 `HostDisplayController`，Host 启动时先加载并校验有效声明，再把已保存偏好合并到当前组件显示状态；新组件默认隐藏，当前声明中同一稳定 ID 保留既有偏好。
- **状态恢复**：暂时从声明消失的组件在偏好文件中保留，重新声明后按原偏好恢复；偏好文件中的过期 ID 不进入当前显示列表。
- **窗口开关**：Host 主窗口新增“显示组件”开关，切换即时保存偏好并更新组件卡片可见性；保存失败时开关回弹到原状态并显示结构化错误。

### 故障与边界

- **偏好文件故障**：偏好文件缺失或损坏时 Host 以隐藏默认状态启动，并返回 `preference_not_found` 或 `preference_invalid` 结构化错误，不产生幽灵组件；读取或写入失败分别返回 `preference_read_failed`、`preference_write_failed`。
- **幽灵组件防护**：只允许切换当前有效声明中声明的组件；切换未声明组件返回 `component_not_declared`，不写入偏好文件。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release --no-restore` 通过，共 35 个测试成功，0 个失败，0 个跳过。
- **构建结果**：Release 配置下构建成功，0 个警告，0 个错误。
- **人工验收**：真实 Windows 上已由用户确认显示开关切换、隐藏与显示状态的重启恢复、声明文件未被偏好写入污染、移除组件不生成幽灵组件，以及偏好文件损坏后的启动与错误提示（依据 03 票据验收记录）。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Host/ComponentDisplayPreferences.cs` | **新增** — 按稳定 ID 保存可见性偏好的独立模块与本地存储 |
| `src/Mtp.Host/HostDisplayController.cs` | **新增** — 声明加载与偏好合并控制器 |
| `src/Mtp.Host/HostComponentDisplayModel.cs` | **修改** — 显示模型增加可见性，支持按偏好投影 |
| `src/Mtp.Host/MainWindow.xaml` | **修改** — 新增“显示组件”开关并绑定组件卡片可见性 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **修改** — 开关切换调用控制器保存偏好并回退失败 |
| `src/Mtp.Host/App.xaml.cs` | **修改** — 启动时接入偏好存储与显示控制器 |
| `tests/Mtp.Platform.Core.Tests/DisplayPreferenceTests.cs` | **新增** — 覆盖切换保存、重启恢复、声明分离、损坏默认、幽灵防护与写盘回退 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.3 (2026-09-03 19:36)

### Host 显示基线

- **普通 WinUI 窗口**：完成二期 01，Host 从控制台占位启动切换为可显示最小 Host 托管组件的普通 WinUI 窗口，并保持组件文本与状态标记在目标窗口尺寸内稳定呈现。
- **平台边界**：Host 的 WinUI、文件读取和窗口启动逻辑留在 Host 项目；平台核心与公共契约继续不依赖 WinUI、Win32、Windows App SDK、文件系统或窗口对象。
- **DPI 基线**：新增应用清单并启用 `PerMonitorV2` DPI 感知，为后续普通窗口显示和真实 Windows 验证保留基础。

### 本地声明

- **可替换声明来源**：新增 `IDeclarationSource`，将声明获取抽象为 Host 边界；当前实现从 Host 输出目录的固定 `declaration.json` 读取本地 JSON。
- **声明加载**：新增 `HostDeclarationLoader`，统一执行本地读取、完整声明校验和有效快照提交。
- **组件投影**：有效声明经校验后生成固定文本、状态标记和分层稳定 ID 的 Host 受控显示模型。
- **失败保护**：空文件、损坏 JSON、未知字段、重复 ID、缺失入口和本地文件读取失败均返回结构化错误；无效声明不会覆盖最后一次有效声明或当前显示。
- **范围边界**：本版本仍不连接 SDK、Broker、动作、浮窗、媒体服务或真实系统状态；任务栏独立贴靠窗口和 Explorer 嵌入探针不属于本次交付。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release --no-restore` 通过，共 29 个测试成功，0 个失败，0 个跳过。
- **构建结果**：Release 配置下 `dotnet build Mtp.sln --configuration Release --no-restore` 成功，0 个警告，0 个错误。
- **人工验收**：真实 Windows 上的窗口内容、尺寸调整、有效声明修改后的显示、错误声明启动行为仍待人工确认；自动化启动 smoke check 仅证明启动路径，不替代 UI 人工验收。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `AGENTS.md` | **修改** — 补充二期 Host 显示与声明加载相关的执行和验收约束 |
| `src/Mtp.Host/App.xaml` | **新增** — WinUI 应用资源与控件资源入口 |
| `src/Mtp.Host/App.xaml.cs` | **新增** — 启动 Host 窗口并加载本地声明 |
| `src/Mtp.Host/DeclarationSource.cs` | **新增** — 可替换声明来源及本地 JSON 文件读取实现 |
| `src/Mtp.Host/HostDeclarationLoader.cs` | **新增** — 声明读取、校验和有效快照加载流程 |
| `src/Mtp.Host/HostComponentDisplayModel.cs` | **新增** — UI 无关的最小组件显示投影 |
| `src/Mtp.Host/MainWindow.xaml` | **新增** — 普通 WinUI Host 窗口和组件显示布局 |
| `src/Mtp.Host/MainWindow.xaml.cs` | **新增** — 组件显示、分层 ID 和结构化错误状态绑定 |
| `src/Mtp.Host/Mtp.Host.csproj` | **修改** — 配置 WinUI 应用、应用清单和示例声明复制 |
| `src/Mtp.Host/Program.cs` | **删除** — 移除旧的最小控制台入口 |
| `src/Mtp.Host/app.manifest` | **新增** — 配置 Windows 兼容性与 PerMonitorV2 DPI 感知 |
| `src/Mtp.Host/declaration.json` | **新增** — 本地 Host 声明示例 |
| `src/Mtp.Host/DeclarationSnapshotStore.cs` | **修改** — 支持读取失败和无效声明时保留最后有效快照 |
| `tests/Mtp.Platform.Core.Tests/DeclarationLoadingTests.cs` | **新增** — 覆盖本地读取、结构化拒绝、贯通加载和快照保护 |
| `tests/Mtp.Platform.Core.Tests/HostDisplayModelTests.cs` | **新增** — 覆盖最小组件到 Host 显示模型的转换 |
| `tests/Mtp.Platform.Core.Tests/Mtp.Platform.Core.Tests.csproj` | **修改** — 配置 Host 显示与声明加载测试依赖 |

---

## v0.1.0-alpha.2 (2026-09-03 00:17)

### 平台核心

- **领域模型**：新增稳定 ID、层级身份、组件、动作槽位、能力状态、状态快照和结构化结果模型。
- **状态规则**：组件状态存储只接受更高 revision，拒绝相同或更低版本的旧状态覆盖当前状态。
- **声明契约**：新增应用、功能组、组件、任务栏操作浮窗和动作槽位的最小声明 DTO，以及完整声明校验结果。
- **原子快照**：声明只有在整份校验成功后才替换当前快照；无效声明保留上一次有效声明和状态。

### 验证

- **自动化测试**：补充稳定 ID、状态覆盖、能力状态、结构化结果、声明校验、JSON 解析、层级冲突和快照保护测试。
- **构建结果**：Release 构建成功，0 个警告、0 个错误；平台核心测试共 16 个通过。
- **范围边界**：本版本仍未实现 Host UI、Broker、SDK、IPC 会话、心跳、Windows 适配器、Core 功能或安装更新。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `src/Mtp.Platform.Core/` | **新增** — 平台核心领域模型、状态规则和结构化结果 |
| `src/Mtp.Contracts/DeclarationContracts.cs` | **新增** — 最小接入应用声明契约 |
| `src/Mtp.Host/DeclarationValidator.cs` | **新增** — 声明对象和 JSON 校验 |
| `src/Mtp.Host/ValidatedDeclaration.cs` | **新增** — 校验后的 Host 声明结果 |
| `src/Mtp.Host/DeclarationSnapshotStore.cs` | **新增** — 有效声明原子快照存储 |
| `tests/Mtp.Platform.Core.Tests/` | **修改** — 新增平台核心和声明校验测试，共 16 个通过 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.1.0-alpha.1 (2026-09-02 23:30)

### 工程基础

- **C# 工程骨架**：新增可构建的 `Mtp.sln`，包含平台核心、公共契约、最小 Host 和核心测试项目。
- **项目边界**：建立平台核心与公共契约的纯类库边界，Host 和测试项目按规定方向引用，核心不依赖 WinUI、Win32、Windows App SDK、文件系统、SQLite 或命名管道。
- **最小 Host**：新增可启动的 Host 入口，验证工程可以运行。

### 验证

- **构建与测试**：完成 `dotnet restore`、Release 构建和测试验证，构建无警告无错误，2 个测试通过。
- **首张票据**：完成“建立最小 C# 平台骨架”，暂未实现声明校验、组件、Broker、SDK、Windows 适配器或 Core 功能。

### 开发规则

- **开发环境**：明确当前仓库处于开发阶段，不为尚未发布的接口、协议和数据结构添加未经要求的向前兼容层；现行文档明确要求的迁移、旧会话隔离和状态回退规则仍然有效。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `.gitignore` | **修改** — 保持 Docs、.scratch 和参考项目等本地材料不进入公开工程提交 |
| `AGENTS.md` | **修改** — 新增开发阶段与兼容性约束 |
| `Mtp.sln` | **新增** — C# / .NET 解决方案 |
| `src/Mtp.Platform.Core/` | **新增** — 平台核心纯类库 |
| `src/Mtp.Contracts/` | **新增** — 公共契约纯类库 |
| `src/Mtp.Host/` | **新增** — 最小 Host 启动项目 |
| `tests/Mtp.Platform.Core.Tests/` | **新增** — 平台核心测试项目 |
| `CHANGELOG.md` | **修改** — 记录本次开发版本 |
| `CHANGELOG.txt` | **修改** — 记录本次开发版本 |

---

## v0.0.0 (2026-09-01 02:42)

### 文档先行准备

- **平台定位**：建立 MTP（Mo's Taskbar Platform）的领域上下文，明确平台核心、Host、Broker、SDK、Windows 适配器与接入应用之间的职责边界。
- **技术基线**：确定首期采用 C#、.NET 与 WinUI 3，Windows 具体能力通过独立适配器接入。
- **进程边界**：确定 Broker 作为独立 .NET 进程，负责命名管道、票据、会话、心跳、消息转发与旧会话清理。
- **核心约束**：平台核心保持纯 C# 领域逻辑，不依赖 WinUI、Win32、Windows App SDK、文件系统、SQLite 或命名管道。
- **首期范围**：明确首期只支持“应用到一个简单程序”的一层依赖，并规定更新回退、降级与责任边界。

### 仓库初始化

- **公开范围**：初始化 Git 主分支，保留项目约束、领域上下文和公开仓库忽略规则。
- **本地材料隔离**：忽略 `Docs/`、`.scratch/` 和 `参考项目/`，避免设计文档、开发票据与参考源码进入公开仓库。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `.gitignore` | **新增** — 忽略本地票据、设计文档、参考项目和开发环境状态 |
| `AGENTS.md` | **新增** — 记录架构、Windows 集成、界面边界和测试验证约束 |
| `CONTEXT.md` | **新增** — 记录 MTP 领域术语、对象关系、生命周期和产品边界 |
| `CHANGELOG.md` | **新增** — 记录首次仓库初始化和架构准备内容 |
| `CHANGELOG.txt` | **新增** — 提供首次版本的纯文本摘要 |