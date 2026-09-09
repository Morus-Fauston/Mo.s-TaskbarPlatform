# Changelog

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