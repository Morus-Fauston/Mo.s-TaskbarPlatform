# Changelog

## v0.4.0-alpha.5 (2026-10-05 21:31)

### 新增与修复

- **项展开**：以屏幕、应用、功能组、组件及项ID管理临时状态；单项切换与声明目标批量切换独立，新增项按模板初始态。普通项和实况岛共用同一路径。
- **稳定身份**：刷新、重排、活动重组和等价结构重连保留展开；Store逐次记录当前项出现代次，避免UI漏过中间删除或结构变化后错误沿用状态，不保存历史墓碑。
- **受控激活**：固定内部控件消费输入，互斥绑定展开、业务动作或同组面板；输出带来源句柄和会话的唯一意图，旧句柄拒绝。换绑和Host关闭清理状态，迟到偏好写入拒绝。

### 验证与边界

- **自动化**：402项核心和50项真实进程通信通过，格式检查及Debug构建通过，构建零警告错误；复用上一票26项传输证据。
- **Windows**：真实WinUI按钮、Broker恢复与耗尽后手动重试、同HWND及自有资源清理通过；本票交付目标呈现模型，原生动态项控件、宽度和动画由五期接入，实际多屏体验待人工。

---
## v0.4.0-alpha.4 (2026-10-05 21:07)

### 新增与修复

- **活动生命周期**：Host按有效期、提供方结束和完整集合移除活动，过滤失效引用；隐藏时继续更新已有活动，拒绝新增或过期活动重建，计时归零不代表业务结束。
- **显示许可**：实况岛入口默认关闭，合法声明和普通状态仍可接纳；初始提交及后续更新返回完整活动拒绝明细。保存显示偏好成功后同步许可，重启保留许可但不恢复活动实例。
- **通知与隔离**：Host、Broker和SDK分别保留有界最新许可快照，回调不阻塞通信；重连重取当前活动，提供方主动提交。旧回调即使忽略取消也不能向新会话发送请求，异常回调不影响后续通知。

### 验证与边界

- **自动化**：373项Core、26项传输和47项真实通信通过，包含双服务、隐藏到期、旧通知取消与新Host；另有SDK非合作回调及非法准入明细探针。Debug构建零警告错误，格式检查通过。
- **Windows**：当前WinUI按钮、Broker恢复、同HWND和退出清理回归通过，7条既有原生测试类型冲突警告保留；活动完整视觉、项展开和动画按后续票据推进，人工体验仍待确认。

---

## v0.4.0-alpha.3 (2026-10-05 20:38)

### 新增与修复

- **有限恢复**：Host按代次管理Broker和自有服务；新票据经受控登记后发送，健康服务在Broker替换期间保持PID，重连重新获取提供方当前完整快照，不重放离线业务请求。
- **预算与就绪**：服务20秒窗口、最多3份新票据和一次重启，Broker最多3次替换且总窗口25秒；短暂成功不补满预算。初始5秒未确认声明也进入恢复，新会话声明、状态和首心跳齐备才恢复动作。
- **手动入口与清理**：控制台独立提供“重试通信”，分别重置受影响对象预算；取消恢复、旧代结果隔离、SDK凭据读泵释放、清理失败保留所有权均有真实进程验证。
- **隔离修复**：真实浮窗断管回归暴露Host状态确认与Broker登记ACK的双向等待；登记ACK改为容量16的独立发送队列，健康服务500次状态更新保持通过。协议错误不再被SDK吞为普通重连。

### 验证与边界

- **自动化**：357项纯规则、26项传输、42项真实通信全部通过；覆盖20秒窗口、一次重启、手动恢复、未握手超时和恢复中关闭。Debug构建零警告零错误，格式检查通过。
- **Windows**：真实WinUI按钮验证自动替换耗尽后的手动恢复，内容岛HWND不变，退出清理全部自有进程和窗口；原生测试工程7条既有类型冲突警告保留。
- **交付范围**：四期03自动化交付完成，任务栏外观仍待人工；活动许可、原位展开及完整动画由后续已发布票据交付。

---

## v0.4.0-alpha.2 (2026-10-05 19:54)

### 新增与修复

- **独立心跳**：SDK每秒发送递增会话心跳，Broker按本机单调时间每秒检测5秒阈值；静止业务保持连接，重复心跳和普通状态更新不能续命，超时只隔离对应服务。
- **故障事实**：Host区分心跳超时、管道断开与已确认进程退出，保留首因和最后确认值，结束受影响动作并禁用按钮；并发退出另存当前会话/PID/退出码，不改写先前故障。
- **启动容量修复**：16服务实测发现标准输出的同步转异步读取挤占公共线程池，导致握手超时；改为每自有进程两条专用排空线程，上限34条，不缓存输出。清理失败先返回并保留所有权，避免等待存活进程的EOF。
- **测试资源**：真实通信集合最多并行两组，内部多服务与并发动作保持原验证；保留首次启动失败和16服务红灯，不通过加长业务超时规避问题。

### 验证与边界

- **自动化**：355项纯规则、36项真实通信全部通过。16服务约0.8秒内就绪，首尾服务动作确认、17个自有进程清理及清理失败后重试通过。
- **Windows**：真实内容岛在自有服务退出后禁用按钮、保留最后值并保持同一HWND，原生外观和任务栏实际体验继续待人工；不宣称全环境兼容。
- **交付范围**：本票完成心跳与故障隔离，有限重连、Broker重启恢复及新凭据分发继续由四期03实现。

---

## v0.4.0-alpha.1 (2026-10-05 19:33)

### 新增与修复

- **受控动作闭环**：Host按完整槽位身份和类型化参数派发动作，SDK在独立服务内顺序执行业务并回传确认；计数器动作增加10，内容岛真实按钮等待确认后刷新读数。
- **有界等待与隔离**：每应用最多4个未终局动作，全局64；5秒超时和用户取消解除忙碌但保留最后确认值，不自动重发。当前会话迟到成功可更新，重复、旧会话和错误序号不污染状态。
- **并发与传输**：序号分配和有界入队原子排序；取消不截断帧，写失败关闭通道。普通发布响应写出与任务结束存在竞争时曾误报Busy，现改为明确准入和容量1的顺序worker。
- **声明与资源边界**：只派发已确认声明中的动作；声明撤销立即禁用交互，确认状态完整校验并冻结，旧revision不倒退读数；关闭释放待定请求、worker及自有进程。

### 验证与边界

- **自动化**：纯规则339项、真实通信30项全部通过，含双服务动作、10批四并发、业务失败、超时/取消后迟到成功和原始管道重复结果隔离。初次回归的6项失败及修复证据保留。
- **Windows与工程检查**：真实WinUI按钮Invoke触发业务确认，内容岛HWND不变，Host关闭和启动失败清理通过；Debug构建零警告零错误。原生测试工程仍有7项既有Windows App SDK类型冲突警告。
- **人工边界**：任务栏实际点击体验待维护者确认，错误提示窗口仍由五期交付；本票不宣称心跳、重连或完整动画已经完成。

---

## v0.3.0-alpha.5 (2026-10-05 19:04)

### 验收与测试

- **跨票组合**：新增可重跑的同Host动态服务与浮窗服务组合，坏载荷期间另一应用非空动态快照不变；随后活动/项集合更新、移除、清空，并以当前revision获得受控请求Received。
- **结束证据**：记录两个独立会话、最终revision4/123、实际待确认峰值2、冻结快照、组合完成标记和全部自有进程的实际退出码；不把主动清理后的非零进程码冒充自主正常退出。
- **三期卡口**：本轮组合1项、传输26项通过，0失败/跳过，Release构建0警告0错误，格式检查通过；复用已验证Core325、通信21及未变Windows路径证据，不重复长测。
- **交付边界**：三期自动化已达到进入四期条件，原生任务栏体验继续待维护者集中确认。完整实况岛呈现、动态宽度、原位展开和动画仍在五期范围，当前不声称这些能力已完成。
- **证据维护**：测试说明补充各进程夹具与组合命令；历史格式日志缺失明确记录，当前格式结果有独立退出码记录，不补造历史文件。

---

## v0.3.0-alpha.4 (2026-10-05 18:55)

### 新增

- **受控浮窗协议**：SDK支持任务栏组、普通提示、交互提示与已注册事件通道请求；只允许受控选屏和位置偏好，不接收坐标、任意模板或秒数。声明统一校验入口身份、种类、事件关闭策略及节点预算。
- **请求隔离**：SDK在单请求准入后分配逻辑ID与递增序号，Host按当前会话/完整入口/确认状态revision检查，保留单一最高序号防重复及乱序，应用每秒最多10请求且不排队。
- **Host许可与结果**：完整声明确认时登记入口，Host可在首请求前禁用；匹配身份许可跨会话保留。三期成功仅返回Received，不声明已显示。
- **边界清理**：Hello、State和Declare拒绝混入浮窗载荷；断流和Host退出结束在途请求，其他应用状态更新继续推进。

### 验证与边界

- **自动化**：Core325/325、真实通信21/21，0失败/跳过；SDK/raw双服务最终revision123/500、实际待确认峰值2；四类接收、禁用后恢复、重放/归属/混载拒绝及Host退出在途释放均通过。
- **工程检查**：Debug构建0警告0错误，最终格式及差异检查通过；真实窗口和3/5/8秒交互呈现留在五期，本票不增加人工门或原生显示结论。

---

## v0.3.0-alpha.3 (2026-10-05 18:43)

### 新增

- **动态模型**：普通多项组件与实况岛共用预声明项结构，支持预置模板、常态/展开态、标准宽度或有界槽位、初始展开、批量展开目标、组织能力与语义动画。活动ID和项ID分开，岛实例仍由Host组织。
- **强类型读数**：显式计时依据与暂停/调整、确定及不确定进度、已完成计数及当前序号、Host状态标记；序号不自动变成比例，归零或进度完成不结束活动。
- **原子输入**：动态声明共享4096节点预算，每入口16结构，每应用64活动/128项、每项8活动引用，每次未来有效期最长24小时。递归冻结已校验数据，坏字段、重复身份、跨入口引用和越限保留整份旧快照。
- **进程闭环**：新增动态内容服务夹具，同一SDK会话经独立Broker完成新增、暂停/调整、增项、部分移除和清空，验证10类坏状态和非法模板拒绝及所有自有进程退出。

### 验证与边界

- **自动化**：Core299/299（排除并行开发的04请求路由），真实通信17/17，0失败/跳过；Debug构建、最终格式和差异检查通过。首次进程失败暴露预算错误码不一致及尚未接齐的字段校验，修正后相关用例通过，原失败证据保留。
- **范围**：三期03仅交付可供后续消费的动态模型与协议；本票未改变原生窗口路径，复用三期02内容岛刷新/清理证据，不宣称完整实况岛视觉、到期调度或动画已交付。

---

## v0.3.0-alpha.2 (2026-10-05 18:23)

### 新增与修复

- **多应用显示**：Host 按完整应用身份消费最新快照，保留本地组件和其他应用，最多16应用；重复及超预算批次原子拒绝，同批快照不重复重建显示模型。
- **通信隔离**：Broker 仅向 Host 提供实际待确认数量与全局峰值，拒绝服务伪造诊断；繁忙回复不回显业务载荷，慢读客户端不占用 Host 写锁。
- **真实进程夹具**：新增专用探针验证高频1000次、健康500次、六类坏状态/旧会话拒绝及慢读背压；检查原快照不变、最终状态、队列峰值和重复释放后的自有进程退出。

### 验证与边界

- **自动化**：Core 234/234、真实通信15/15，0失败/跳过；双服务各场景实际待确认峰值2，健康服务均达到revision500。Debug构建0警告0错误，格式与差异检查通过。
- **原生回归**：Windows自有父窗口中生产内容岛读数7→16、HWND不变；正常关闭、启动失败及启动取消均完成自有进程/岛/桥清理。专用测试构建保留7个已有Windows App SDK类型冲突警告。
- **交付边界**：三期02自动化完成；不代表任务栏外观、全环境或人工验收通过，三期01待人工项继续保留。

---

## v0.3.0-alpha.1 (2026-10-05 18:06)

### 新增与架构

- **真实进程闭环**：新增独立 Broker、SDK 与计数器服务；Host 启动并监督自有进程，完整声明与初态原子校验后进入显示模型，后续确认读数刷新实际 WinUI 内容岛。以 `--counter-demo` 提供可运行开发入口，组件显示仍由稳定偏好控制。
- **受控通信**：采用当前用户专用管道、长度前缀严格 UTF-8 JSON、1 MiB 单帧和深度32限制；30秒一次性随机票据绑定应用和启动请求，拒绝错误身份、重放、未知版本、坏帧与非法字段。
- **状态隔离**：Host 使用不可变已校验声明与确认快照，校验当前会话、完整入口身份及递增 revision；断流保留读数并标记服务未连接，输入和待确认请求有明确预算。
- **退出可靠性**：区分启动失败与未完成清理，修复失败启动阻止关窗的问题；终止失败时保留自有进程和会话的重试所有权，确认退出后才释放。
- **构建与夹具**：生产和测试从当前构建输出复制 Broker/CounterService，SDK封装标准输入启动协议；新增真实进程安全测试及生产内容岛读取回归。

### 验证与边界

- **自动化**：Core 226、Transport 26、真实通信12、Windows适配器71项通过，均0失败/跳过；Release与Debug构建0警告/0错误，格式与差异检查通过。
- **原生证据**：自有Windows父窗口中，独立服务确认读数7→16进入实际TextBlock且HWND不变；Host关闭清理进程、内容岛和桥窗口，启动失败/初始化取消可正常关窗。Windows专用测试项目保留7个SDK自动初始化类型冲突警告。
- **范围**：三期01自动化交付完成，人工任务栏外观、交互及未覆盖环境仍待维护者集中确认；心跳、有限重连、动作、完整浮窗及实况岛动画继续按后续票据交付，不据本票承诺全环境支持。

---
## v0.2.0 (2026-10-05 00:42)

### 二期期末收口与正式内容岛承载迁移

- **06 期末收口**：二期期末验收票据 06 于 2026-10-05 由维护者确认收口，原话“没啥问题。二期赶紧收口吧。”；11 项 Checklist 全部完成，票据登记 `completed`。首轮（2026-10-04）阶段结论为不通过并保留 4 项，本次是维护者接受遗留与证据边界后的阶段收口，不是 E1 修复复验。
- **E1 未修复保留**：现象是普通 Acrylic 案例的事件日志写入失败；本轮未修复，作为已知遗留留档，维护者接受其不阻塞二期收口。不得将日志缺失或原失败验证写成修复通过，后续排查入口见二期遗留清单。
- **声明契约口径修正**：本票原流程要求“无效声明重启后仍保留最后有效声明”，与 02 已验收的冷启动行为冲突；按 02 原契约更正为“运行中拒绝坏声明保留当前有效快照，坏文件冷启动显示结构化错误、不显示半成品”，且不新增跨重启声明缓存。
- **接受范围**：按“没啥问题”的总体反馈接受当前 Host 使用结果，不再以补交逐项记录作为结票前置；未回传的具体操作、次数与环境不推定已实测。已完成 Acrylic 直接确认与旧票人工记录保留。
- **不自动进入三期**：二期按上述边界完成，不等于全部缺陷消失或全环境兼容；三期仍须单独确认范围并授权建票与开工。

### 05M Host 测试平台重构与内容岛正式迁移

- **正式承载路线落地**：将声明显示链路接入实际内容岛宿主，采用自建 Win32 宿主 + WinUI 内容岛，不再经过旧独立贴靠窗口与 Explorer 探针控制器。无法嵌入时报错且不回退独立贴靠。
- **Host 主窗口改造**：`MainWindow` 只保留导航、绑定与轻量事件转发；显示协调、测试场景、会话状态、采样与证据导出按职责分离，新增 `HostTestController`、`HostConsoleController`、`HostEvidenceIdentity`、`DisplaySelectionBinding` 与 `Islands/` 内容岛宿主层。
- **过期入口清理**：移除与无回退规则冲突的“停止探针并恢复独立贴靠”等正式控制台入口与回退装配；旧“模拟任务栏不可用”统一为明确的测试会话失败注入并提供清除操作。
- **生产代码退役**：独立贴靠窗口、Explorer 嵌入探针与透明绘制实现移出生产；保留价值部分迁入 `tests/Mtp.Host.Legacy.Runtime` 与 `tests/Mtp.Host.Legacy.Windows` 作为历史夹具，`Mtp.sln` 一并调整对应装配。
- **测试平台入口**：提供启动测试、观察结果、停止测试与导出逐项记录的操作入口。测试夹具可显示本地计数、按钮、开关、滑块和 Popup，仅为开发期夹具，不代表 SDK/Broker、业务动作、公开控件模板或完整浮窗体系。
- **独立内容岛预览**：追加宿主内显式可拖动内容岛预览，复用 Host 内容与宿主，支持真实控件、参数应用、系统标题栏拖动、与案例互斥及退出清理，供在不同背景上观察透明样式；不作为失败回退。
- **同一宿主路径**：测试与正常最小组件使用同一 Host 宿主和生命周期服务，不复制“仅供测试”的内容岛冒充实际路径；独立实验 exe 不作为 Host 显示依赖。
- **渲染与边界**：内容由 WinUI 原生渲染，不引入逐帧位图读回和整窗复制；HWND、dispatcher、Explorer 与原生资源留在 Windows 适配器，Runtime/Core/Contracts 保持原有边界。

### 工具与历史归档

- **TransparencyLab 归档**：`tools/TransparencyLab` 整体迁入 `tools/Historical/TransparencyLab`，不再作为当前工具入口。
- **手工回归启动器归档**：旧人工回归启动脚本迁入 `tools/Historical/MtpManualRegression`，正文保留为 `.txt`。
- **历史入口说明**：新增 `tools/README.md` 与 `tools/Historical/README.md`，明确历史工具只作追溯、不作当前执行入口。
- **TaskbarIslandLab 调整**：更新 `README.md`、`Demo.ps1`、`Run.ps1`、配对证据回读脚本与人工验收手册，保持独立性能对照定位。

### 文档与决策

- **AGENTS.md**：新增任务栏组件正式承载路线条款、第三至第五期连续执行例外、测试与验证补充，以及票据正文与证据目录约定。
- **CONTEXT.md**：更新术语与承载路线，随内容岛正式路线同步。
- **决策落点**：记录正式承载路线、当前 Host 测试平台与补验入口、三至五期连续执行三项决策。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `README.md` | **新增** — 仓库根入口说明 |
| `AGENTS.md` | 增正式承载路线、三至五期连续执行例外、测试与验证补充、票据证据目录约定 |
| `CONTEXT.md` | 同步术语与内容岛正式承载路线 |
| `Mtp.sln` | 调整工程装配：移除生产探针与独立贴靠窗口，加入 Host.Runtime 分层与 Legacy 夹具 |
| `src/Mtp.Host.Runtime/HostTestConfiguration.cs` | **新增** — 测试会话配置 |
| `src/Mtp.Host.Runtime/HostTestRun.cs` | **新增** — 测试运行与阶段状态 |
| `src/Mtp.Host.Runtime/IslandDisplaySession.cs` | **新增** — 内容岛显示会话 |
| `src/Mtp.Host.Runtime/TaskbarDockEnvironment.cs` | **新增** — 任务栏停靠环境纯规则 |
| `src/Mtp.Host.Runtime/`（删除 6 个 + 修改 csproj） | **删除** — 旧探针、独立贴靠与透明绘制生产实现退役 |
| `src/Mtp.Host/App.xaml.cs` | 装配内容岛主路径，替代旧探针/贴靠控制器 |
| `src/Mtp.Host/MainWindow.xaml`、`MainWindow.xaml.cs` | 改为普通 WinUI 测试控制台，只保留导航、绑定与轻量事件转发 |
| `src/Mtp.Host/HostTestController.cs`、`HostConsoleController.cs` | **新增** — 测试场景、会话状态与操作入口 |
| `src/Mtp.Host/HostEvidenceIdentity.cs`、`DisplaySelectionBinding.cs` | **新增** — 证据身份标识与目标显示器绑定 |
| `src/Mtp.Host/Islands/`（7 个） | **新增** — 内容岛宿主、内容、显示适配器、生命周期、预览窗口、原生窗口与无激活 Acrylic 背景 |
| `src/Mtp.Host/`（删除 10 个 .cs/.xaml） | **删除** — 探针与独立贴靠窗口从生产移除 |
| `tests/Mtp.Host.Legacy.Runtime/`（7 个） | **新增** — 历史探针与独立贴靠纯逻辑夹具 |
| `tests/Mtp.Host.Legacy.Windows/`（11 个） | **新增** — 历史探针与独立贴靠 Windows 夹具 |
| `tests/Mtp.Host.WindowTests/`（新增 9 个、修改 4 个） | 新增控制台、内容岛、预览、显示器拖动回归与证据校验入口 |
| `tests/Mtp.Host.Windows.Tests/`（新增 1 个、修改 4 个） | 新增内容岛原生测试，调整探针资源与环境监测测试 |
| `tests/Mtp.Platform.Core.Tests/`（新增 2 个、修改 csproj） | 新增 Host 测试运行与内容岛显示策略纯规则测试 |
| `tests/README.md` | 更新测试层与证据边界说明 |
| `tools/Historical/**`（19 个）、`tools/README.md` | **新增** — TransparencyLab 与旧手工回归启动器归档，明确历史工具定位 |
| `tools/TransparencyLab/**`（13 个） | **删除** — 整体迁入 `tools/Historical/TransparencyLab` |
| `tools/.gitignore`、`tools/TaskbarIslandLab/**`（6 个） | 更新忽略规则与独立对照工具入口 |

### 验证

- `dotnet build Mtp.sln -c Release --no-restore`：0 警告、0 错误。
- `dotnet test Mtp.sln -c Release --no-restore`：纯规则 204/204、Windows 71/71，通过且无跳过。
- `TaskbarIslandLab.Tests`：14/14 通过。
- `dotnet build Mtp.sln -c Debug --no-restore` 后 `dotnet format Mtp.sln --verify-no-changes`、`git diff --check` 均通过。

### 验收边界

- 内容岛路线已采纳，但不据此视为实现验收通过；05M 的实际结果与未覆盖项以票据为准。
- 05D、05E 两条实验线保持封存为独立分支，不合并、不重新启用，不修改其历史结论。
- E1（普通 Acrylic 案例事件日志写入失败）保留为未修复遗留，本次收口接受其不阻塞二期。
- 多屏、混合 DPI、其他 Windows/GPU、独占全屏/HDR、长期资源稳定性、外部 UIA 与 Explorer 重启的自动化覆盖仍待人工或专用环境确认，不得标记为已验证。
- 三期范围与实施授权独立确认，本版本不自动启动三期。

---

## v0.1.0-alpha.11 (2026-10-02 02:53)

### 二期 06 验收前最后一轮遗留问题修复

- **输入完整性**：现象是损坏 UTF-8、未知枚举、超限声明或 DTO 载荷可能绕过统一边界；修复为严格 UTF-8 解码、统一声明与 JSON 的 ID/集合/节点预算，以及构造时枚举校验，拒绝结果保留最后一次有效快照。
- **窗口清理恢复**：现象是探针窗口已被销毁后，关闭异常会错误地保留所有权并阻塞后续重建；修复为在异常后复核缓存 HWND 和 `Closed` 状态，确认资源已失效时幂等释放，仍有效时保留重试状态。
- **偏好目录与并发写入**：现象是偏好写入程序目录，且不同进程、会话或目录别名可能竞争不同的锁；修复为统一迁移到每用户 `LocalApplicationData/Mo.s-TaskbarPlatform`，使用目标文件旁固定 `.lock` 文件、临时文件和原子替换，并只在目标缺失时有界导入旧文件。
- **Host 分层**：现象是纯规则测试依赖 Windows Host，导致平台边界和非 Windows 验证不清晰；修复为提取 `Mtp.Host.Runtime`，将窗口和 Windows 原生测试移入独立工程，保持 Core/Contracts 不依赖 WinUI、Win32 或窗口对象。
- **内容岛材质初始化**：现象是自有宿主的 Acrylic 在无激活窗口下无法稳定显示；修复为只对 MTP 自有顶级宿主执行 host-backdrop 初始化，并增加无激活宿主的材质活动适配；Explorer 外部窗口仍不写入、不探测、不重排。
- **验证工具与证据**：补齐材质状态矩阵、进程边界、Popup/peer 生命周期、配对采样回读和人工补验入口，明确自动化状态证据与真实 Windows 外观、性能、Explorer、外屏和 UIA 验收的边界。

### 验证

- `dotnet build Mtp.sln -c Release --no-restore`：0 警告、0 错误。
- `dotnet test Mtp.sln -c Release --no-restore`：纯规则 194/194、Windows 63/63，通过且无跳过。
- `TaskbarIslandLab.Tests`：14/14 通过。
- 材质状态矩阵：8/8 通过；进程边界：7/7 通过；`dotnet format --verify-no-changes` 与 `git diff --check` 通过。
- 维护者已确认当前笔记本自有 Host 默认 Acrylic 可以显示。

### 验收边界

- 05G–05L 的自动化实现和证据材料已完成，但票据仍保留 `awaiting-human`。
- 真实 Explorer 重启、受保护安装目录、跨登录会话、长时间资源稳定性、性能预算、Mica/主题/高对比/焦点/alpha、外屏与混合 DPI、外部 UIA 仍待人工或专用环境确认。
- 二期期末演示票据 06 仍需维护者授权并完成讨论；本版本不把自动化绿色结果写成二期整体通过。

---

## v0.1.0-alpha.10 (2026-10-01 20:20)

### 05F WinUI 内容岛任务栏承载最小验证

- **验证目标**：确认“显式自建 Win32 宿主并直接承载 WinUI 内容岛，能在保持原生呈现与低开销的同时取得所需的任务栏同步”这一路线成立。窗口关系为 MTP 自有父级或显式选择的 Explorer 任务栏 HWND → 同线程创建的 Win32 宿主 H → `DesktopChildSiteBridge` 子 HWND → `DesktopWindowXamlSource` 承载的真实 XAML 树。普通 WinUI `Window` 内部同样使用 DWXS，本次改变的是外层宿主与初始化路径。
- **实现落点**：新增 `TaskbarIslandLab`，分 `Logic`（纯逻辑）、`Windows`（宿主适配器）、`Tests`（专属测试）三层。所有 HWND、Win32 调用、窗口父链与 UIA 诊断留在 Windows 适配器内，不向 Core、Contracts 或接入应用暴露窗口对象。默认入口只创建本进程自有父窗口，不枚举或绑定 Explorer、不抢前台。
- **原生渲染链路**：内容走 WinUI 原生渲染，不含逐帧 `RenderTargetBitmap` / `GetPixels` / `UpdateLayeredWindow`、截图读回或整窗位图复制。诊断截图只作独立取证，不参与显示链路或性能计时。
- **配对性能测量**：提供 `Measure-Pairs.ps1`，以 `empty` / `top-level` / `owned` 三种模式在 0 / 1 / 30 / 60 Hz 下串行采样，记录请求更新数与实际内容更新数、阶段时长、进程 CPU 单核与整机口径、私有内存、工作集与线程句柄变化。30 Hz 可见动态阶段实际运行 600 秒，实际更新约 18,170 次；Explorer 父级三次独立采样单核口径 6.34%–6.74%，整机约 0.26%–0.28%（24 逻辑处理器）。GPU、唤醒/上下文切换与真实呈现帧率按工具契约记为未测。
- **Popup 定位修复**：现象是高于 56 DIP 内容岛的 Popup 在右下屏边只露出一部分。根因是固定偏移 `(200,44)` 与默认根边界约束共同造成裁剪。修复为保留原生 Popup、解除其根边界约束，并以 `+1` 为锚点优先向上弹出。修复前同一最终回归断言在屏边失败，修复后 owned/verify 28/28 通过。
- **脚本入口修复**：现象是 `Check-ProcessBoundaries.ps1` 对 Windows PowerShell 子进程读出错误退出码，把非法参数场景记为脚本检查失败。根因是启动子进程的方式。修复为 `ProcessStartInfo` + `Process.Start`，`Run.ps1` 同类入口一并修正；脚本 BOM 与 JSON UTF-8 读取同时补齐。故意把副本退出码改为 99 后，脚本正确报告 414/415 与退出 1。
- **交付材料**：新增 `人工验收手册.md`、中文菜单 `Demo.ps1` 与双击入口 `Start-Demo.cmd`，每轮自动新建证据目录并复制人工观察表。Explorer 目标仍须显式选择屏幕与父级，重启仍由人执行；工具不自动选择 Explorer、不重启外壳、不标记人工验收完成。

### 验证

- **自动化测试**：Release 配置下 `dotnet test Mtp.sln --configuration Release --no-restore` 通过，共 220 个测试成功，0 个失败，0 个跳过；`TaskbarIslandLab.Tests` 14 个测试全部通过。
- **构建与格式**：Release 构建 0 个警告、0 个错误；主解决方案与工具 / Logic / Tests 四项 `dotnet format --verify-no-changes` 通过，`git diff --check` 通过。
- **真实自有窗口集成**：`Run.ps1` 自有父级夹具通过，19 条断言全部成功，12 条清理记录均为 `Closed` 且错误数组为空，host/bridge/ownedParent 均不再存活。覆盖 XAML 树加载、布局与客户区尺寸、宿主/bridge/DPI 身份、隐藏恢复、尺寸变更、关闭后拒绝更新、过期回调隔离、父关系丢失、父级先销毁、显式重建，以及 host/source/attach 三处初始化中断清理。
- **配对证据回读**：`Verify-PairedEvidence.ps1` 对固定配对矩阵回读 415 项检查全部通过，覆盖 14 次运行、70 个阶段；该检查验证证据一致性，不是单元测试数或性能合格判断。
- **人工验收**：输入/焦点/键盘、主题、全屏、自动隐藏反向等项已确认通过；透明色刷 alpha 1 / 0.5 / 0 三档在笔记本内屏确认不透、半透、全透；Popup 完整显示、按钮关闭、点外关闭与退出无残留复验通过；Explorer 重建取得安全失效与显式新建日志。自动化清单 8/8。

### 未通过、后置与未覆盖

- **材质未通过**：Acrylic 在当前实验下视觉未通过。多轮对照（激活配置、去蓝色背景、Thin/低浓度）均未恢复可见效果，不能把激活策略当成已确认完整根因；Acrylic / Mica 固体底问题列为后续待修复事项，本轮停止参数试错。该结论不修改正式材质契约。
- **已知低优先级限制**：任务栏背景可透出，但 FluentFlyout 在重叠区域时隐时现；FFO 位于 MTP 上方时透明可实现，降为低优先级，不探测或修改第三方进程。
- **待人工保留项**：真实隐藏成本补采暂缓，现有隐藏阶段数据为工具侧采样，不替代人工隐藏成本验收；长期内存与句柄变化、GPU / 唤醒 / 呈现 FPS、外接 4K / 跨屏 / 混合 DPI、UIA 详细取证均保留为未覆盖。内存调查发现 GC 保留提交容量与 ComWrappers 容量增长，未发现窗口或内容对象数量累积，不写成零泄漏。
- **未覆盖范围**：本版本只代表已测单屏场景；多屏组合、混合 DPI、其他 Windows / GPU、独占全屏及 HDR 按实际覆盖填写，不得标记为已验证。

### 范围边界

- **不启用旧实验**：不合并或重新启用 `experiment/taskbar-embed` 与 `codex/05e-taskbar-visibility-probe` 两条已封存分支，不修改其历史结论。
- **单切片**：只做一个选定屏幕、底部任务栏、一个 `260 × 56 DIP` 控件组；不交付多组件布局、多屏正式适配、完整设置壳、业务服务、SDK/Broker 或真实系统动作。
- **不规避**：不改用 WPF、自绘产品 UI 或新的渲染进程规避失败，不通过改变整个 Host 的 DPI 感知模式掩盖跨进程重置。

### 文件变更表

| 文件 | 变更 |
|:-----|:------|
| `tools/TaskbarIslandLab/Program.cs` | **新增** — 实验入口与模式 / 场景 / 参数分发 |
| `tools/TaskbarIslandLab/Windows/IslandHost.cs` | **新增** — 同线程宿主的 DWXS 生命周期、bridge 布局与清理 |
| `tools/TaskbarIslandLab/Windows/NativeWindows.cs` | **新增** — 原生窗口与父链调用 |
| `tools/TaskbarIslandLab/Logic/IslandLifetime.cs` | **新增** — 实例生命周期与失效判定纯逻辑 |
| `tools/TaskbarIslandLab/Logic/LabOptions.cs` | **新增** — 运行选项与参数校验纯逻辑 |
| `tools/TaskbarIslandLab/Logic/Measurement.cs` | **新增** — 阶段计时与更新计数纯逻辑 |
| `tools/TaskbarIslandLab/LabContent.xaml` / `.cs` | **新增** — 真实 WinUI 控件树与期望结构断言 |
| `tools/TaskbarIslandLab/App.xaml` / `app.manifest` | **新增** — 应用定义与清单 |
| `tools/TaskbarIslandLab/RunLog.cs` / `ResourceSampler.cs` | **新增** — 事件日志与资源采样 |
| `tools/TaskbarIslandLab/Tests/LifetimeTests.cs` | **新增** — 生命周期与清理失败保留所有权测试 |
| `tools/TaskbarIslandLab/Tests/MeasurementTests.cs` | **新增** — 阶段计时与计数测试 |
| `tools/TaskbarIslandLab/Tests/OptionsTests.cs` | **新增** — 参数校验测试 |
| `tools/TaskbarIslandLab/Tests/TaskbarIslandLab.Tests.csproj` | **新增** — 工具专属测试工程 |
| `tools/TaskbarIslandLab/Run.ps1` / `Stop.ps1` / `Demo.ps1` / `Start-Demo.cmd` | **新增** — 运行、停止与中文演示入口 |
| `tools/TaskbarIslandLab/Measure-Pairs.ps1` / `Summarize.ps1` | **新增** — 配对采样与汇总 |
| `tools/TaskbarIslandLab/Tests/Check-ProcessBoundaries.ps1` / `Verify-PairedEvidence.ps1` | **新增** — 进程边界与配对证据回读验收脚本 |
| `tools/TaskbarIslandLab/TaskbarIslandLab.csproj` | **新增** — 实验工具工程 |
| `tools/TaskbarIslandLab/Backdrop.html` | **新增** — 透明/不透明对照背景 |
| `tools/TaskbarIslandLab/README.md` | **新增** — 构建、运行、参数与证据边界说明 |
| `tools/TaskbarIslandLab/人工验收手册.md` | **新增** — 逐项操作、判定、失败取证与回传格式 |
| `tools/TaskbarIslandLab/observations.template.md` | **新增** — 人工观察记录模板 |
| `CONTEXT.md` | **修改** — 新增「Windows 原生性」术语 |
| `CHANGELOG.md` | **修改** — 记录本版本 |
| `CHANGELOG.txt` | **修改** — 记录本版本 |

---

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
