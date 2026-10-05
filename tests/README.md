# 测试层与证据边界

当前源码与历史夹具暂保留现有项目分层；Host 拆分另行处理。长期运行证据统一从本地 `.scratch/二期开发/evidence/` 查找，票据状态仍以票据头部为准。历史诊断与过时人工启动器见 [tools/Historical](../tools/Historical/README.md)，不作为当前验收入口。

## 纯规则与文件/进程边界

~~~powershell
dotnet test tests/Mtp.Platform.Core.Tests/Mtp.Platform.Core.Tests.csproj -c Release
~~~

目标为普通 net10.0，引用 Core、Contracts、Host.Runtime 及测试专用 Legacy.Runtime，不构建 WinUI。覆盖声明校验、内容岛恢复策略、测试报告、历史贴靠策略、偏好文件和两个独立 .NET 进程并发写入。进程夹具只写测试创建的临时目录。架构测试阻止 Windows TFM/WinUI 依赖重新进入该层。此轮在 Windows 执行，不将目标框架可移植性写成已经在 Linux/macOS 实跑。

## SDK / Broker 真实进程通信

```powershell
dotnet test tests/Mtp.Transport.Tests/Mtp.Transport.Tests.csproj -c Release
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release
```

通信测试构建并复制本配置的 Broker 和 CounterService，不依赖先前 Release 输出。每个用例使用随机当前用户管道与自有独立进程，验证初始声明/确认状态、票据拒绝、坏帧、取消及清理；不修改用户偏好。MultiApplicationTests 使用专用 ProcessProbe 验证双应用高频、坏状态、旧会话与慢读隔离，TRX记录最终revision、实际队列峰值及PID退出。传输测试覆盖严格 JSON/UTF-8、1 MiB 边界和分段读取。当前切片不包含自动重连或业务动作。

动态内容由 `DynamicContentProcessTests` 验证活动与重复项同会话增减、预置模板、计时/进度/计数字段和拒绝后旧快照保留。受控请求由 `FlyoutProcessTests` 验证 SDK 四类请求、Host 许可、旧会话/序号及混载拒绝；`Received` 仅表示协议接收，不代表已经创建窗口。两者均使用专用独立进程，未扩大计数器业务。

三期跨票组合入口（同一 Host 和两个独立服务）：

```powershell
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release --filter FullyQualifiedName~PhaseThreeCombinationTests
```

长期证据按各期保存至 `.scratch/<期次>/evidence/<票号>/`，TRX必须使用不同文件名或不同目录，避免多项目结果覆盖。三期验收不等于完整实况岛视觉、动作/心跳/恢复或浮窗动画交付。

开发期计数器入口：构建 Host 后以 `Mtp.Host.exe --counter-demo` 启动；控制台出现 `counter / main / counter` 后开启该组件显示。默认显示仍关闭，确认读数来自独立服务。退出 Host 清理本次自有进程；嵌入失败保留错误，不建立独立贴靠回退。独立服务不应手工拼接命令行票据。

## Windows 适配器

~~~powershell
dotnet test tests/Mtp.Host.Windows.Tests/Mtp.Host.Windows.Tests.csproj -c Release
~~~

仅 Windows：自有隐藏 HWND、适配器失败、Explorer 探针资源所有权、显示器采集、原生事件和目录 junction 锁。不会重启或改写 Explorer，不代表任务栏嵌入人工验收。

## WinUI 进程与实验工具

- [WinUI Window Regression](Mtp.Host.WindowTests/README.md)：当前 Host 控制台、内容岛、材质组合、负载/停止/证据，以及历史离屏窗口回归；与可见外观人工验收分开。
- [TaskbarIslandLab](../tools/TaskbarIslandLab/README.md)：独立工具逻辑、owned/verify、配对采样；不进入正式 Host 路径。
- 全量主线：dotnet test Mtp.sln -c Release；dotnet build Mtp.sln -c Release。
- 格式检查：先 dotnet build Mtp.sln -c Debug，再 dotnet format Mtp.sln --verify-no-changes --no-restore，保证 WinUI 设计时引用元数据存在。

真实启动、外观、点击热区、任务栏、多屏、DPI、自动隐藏与 Explorer 重启由维护者执行和确认；自动化通过不能勾选这些人工项。
