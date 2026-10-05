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

五期受控模板入口：构建后运行 `Mtp.Host.exe --template-demo`，开启计数器组件显示。模板使用受控 PNG、文本、按钮、图标按钮、开关与滑块，普通值变化保留原控件；滑块 0–80 可确认，95 用于演示失败回退。此示例不接入系统音量或其他六期能力。

~~~powershell
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario template -EvidenceDirectory .scratch/五期开发/evidence/01/<新批次>
~~~

该场景运行真实自有内容岛、独立 Broker 与服务，验证图像解码预算、原生控件动作确认/回退、同 HWND 与控件刷新、深浅主题背景、旧会话拒绝及隐藏/退出清理。`template-light.png`/`template-dark.png` 是 RenderTargetBitmap 呈现证据，场景显式使用不透明 Host 背景，避免透明 PNG 查看器误合成；不代替任务栏、实际焦点与读屏人工验收。

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

## 四期动作闭环

计数器示例的受控动作将读数增加10，自动tick增加1；读数只在服务确认后更新。内容岛的“执行”按钮等待期间禁用并显示“等待确认”，确认失败保留最后值；完整错误提示窗口由五期提供。

~~~powershell
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release --filter FullyQualifiedName~ActionProcessTests
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario broker -EvidenceDirectory .scratch/四期开发/evidence/01/<新批次>
~~~

动作夹具包含真实双服务、业务失败、4个未终局请求预算、5秒超时、取消后迟到结果、旧会话/错序号/重复结果和并发发送。原生回归通过实际WinUI按钮AutomationPeer调用Click链，检查协议确认后的值以及同一HWND和退出清理；这不是人工点击或动画外观验收。

四期心跳与故障隔离验证入口：

~~~powershell
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release --filter FullyQualifiedName~HeartbeatProcessTests
~~~

SDK每秒发送独立心跳，Broker以接收端单调时钟每秒检查5秒阈值；业务静止不等于失联，重复序号和业务State不续命。故障保留首因及最后确认值；进程退出另记当前自有PID/会话/退出码事实。原生broker场景也检查终止自有计数器后按钮禁用、读数保留、HWND不变。

部分通信测试夹具仍共享testhost线程池（Windows继承标准管道的异步封装会占用worker），测试集合最多并行2组；每组内部的双服务、4动作并发和故障隔离仍真实并行。不要通过扩大业务超时掩盖测试夹具资源竞争。

Host产品进程的stdout/stderr使用每进程两条专用同步排空线程，最多34条，避免挤占协议线程池；16服务上限由HeartbeatProcessTests的One_host_starts_all_sixteen测试覆盖。清理失败保留资源所有权并立即返回，不等待仍存活进程的输出EOF。

## 四期有限恢复

Host按应用20秒窗口、最多3份新票据和一次自有服务重启恢复，Broker自动替换最多3次且总窗口25秒；短暂成功不补满预算，手动重试才开启对应对象的新预算。初始声明5秒未确认也进入恢复。Broker故障先保留健康服务，由SDK新凭据读泵重新取当前快照；离线业务请求不缓存、不重放。恢复声明与首个心跳就绪后才解锁交互。

~~~powershell
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release --filter 'FullyQualifiedName~RecoveryProcessTests|FullyQualifiedName~InitialServiceRecoveryTests'
powershell -ExecutionPolicy Bypass -File tests/Mtp.Host.WindowTests/Run.ps1 -Scenario broker -EvidenceDirectory .scratch/四期开发/evidence/03/<新批次>
~~~

恢复夹具验证真实Broker新代次、健康PID保留、当前快照重取、SDK关闭、20秒窗口/一次重启/手动应用恢复及恢复中关闭。原生broker场景覆盖3次自动替换、预算耗尽后的实际“重试通信”按钮、独立应用重启预算和同一HWND；仅操作测试自有进程，不代替人工桌面验收。

## 四期活动生命周期与显示许可

~~~powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/Mtp.Platform.Core.Tests/Mtp.Platform.Core.Tests.csproj -nr:false --filter 'FullyQualifiedName~ActivityLifecycleTests|FullyQualifiedName~ActivityDisplayPreferenceTests'
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release -nr:false --filter FullyQualifiedName~ActivityProcessTests
~~~

新实况岛入口默认关闭。合法声明仍建立连接，但未获许可的新活动返回 AcceptedWithActivityRejections 明细；SDK 的 InitialPublicationResult 与 PublishAsync 均可读取。许可通知由声明提供方可选实现 IDisplayPermissionObserver 接收，提供方主动提交仍在进行的活动，SDK 不重放此前拒绝的创建。首次回调如需 SDK 引用，提供方应等待其在 Connect 返回后显式绑定的信号。

ActivityProcessTests 使用真实双服务验证关闭期间已有活动更新、新建拒绝、共享活动引用过滤、隐藏期间结束/到期、Broker 新会话重新确认和旧回调取消；新 Host 不恢复运行期实例。偏好持久化和保存失败由公开显示控制器边界验证。窗口外观与实际多屏仍待人工。

## 四期按项展开

~~~powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/Mtp.Platform.Core.Tests/Mtp.Platform.Core.Tests.csproj -nr:false --filter 'FullyQualifiedName~ItemPresentationTests|FullyQualifiedName~ItemActivationTests|FullyQualifiedName~ItemDisplayIntegrationTests'
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Release -nr:false --filter FullyQualifiedName~ItemExpansionProcessTests
~~~

HostDisplayController拥有当前Store的ItemPresentationController和ItemActivationRouter，相同Store刷新保留状态，换绑或关闭清理。ItemPresentationController按屏幕/应用/功能组/组件/项ID管理本地展开；Store每次合法提交维护出现代次，避免UI未观察到中间移除后错误保留状态。业务与面板意图附当前SessionId和Origin，五期消费方派发前仍需核对。此入口产出目标呈现，不宣称原生动态宽度或动画完成。

ItemExpansionProcessTests复用真实动态与活动服务，验证两屏普通项/岛项共用路径、批量后新增不继承、刷新保留、删除/到期清理与Host重建。完整多屏交互与原生控件由五期提供后集中人工验收。

## 四期组合与生命周期诊断

~~~powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/Mtp.Communication.Tests/Mtp.Communication.Tests.csproj -c Debug -nr:false --filter FullyQualifiedName~PhaseFourCombinationTests
~~~

同一Host三个服务运行两轮：各轮业务20秒、清理5秒，成功总计50秒以内；Broker恢复步骤8秒。失败时独立5秒安全收尾，不延长通过阈值。覆盖项展开、真实动作超时/晚到、健康服务动作、许可关闭/恢复、Broker新会话、活动到期和旧句柄隔离。

HostBrokerSession.GetLifecycleSnapshot和SdkClient.GetLifecycleSnapshot读取实际拥有任务与队列，最多保存当前及最后清理代次。测试在Host仍保持凭据stdin时显式释放SDK，核对心跳/许可/恢复任务和队列，然后重复释放Host并确认全部自有PID退出、关闭后动作拒绝。保留的业务快照不算活连接；不把非合作提供方任务或未读取的Broker内部计数伪称归零。
