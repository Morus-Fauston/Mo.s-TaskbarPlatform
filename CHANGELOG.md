# Changelog

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