# 已归档：旧 Explorer 探针人工回归流程

2026-10-05 归档。下方是原说明快照，其中的探针按钮和独立贴靠回退已经被当前 Host 内容岛路线替代，不能继续作为当前人工流程。原脚本均保留为 `.ps1.txt` / `.cmd.txt`，避免双击启动失效步骤；本轮没有改写脚本原始内容。

当前操作从 Host 开发控制台进入，参见本地私有 `Docs/使用手册/Host测试平台操作手册.md`。历史报告已迁至 `.scratch/二期开发/evidence/manual-run-时间戳/`，旧、新路径和文件哈希见本地迁移清单。

## 原说明快照

# MTP 人工回归启动器

2026-10-04 Explorer 重启崩溃修复后的单项复验：双击 `Start-ExplorerRetest.cmd`。只检查首次嵌入 → 人工重启 Explorer → 再次嵌入 → 停止恢复；已通过的 05G/05J 和内容岛项目无需重做，受保护目录也不在这次复验中。结果报告会记录本次 Host DLL 哈希。

双击 `Start-ManualRegression.cmd`。它会自动备份每用户偏好、运行现有 Release 测试、启动 Host，并在需要真实 Windows 观察时让维护者输入 `P`（通过）、`F`（失败）或 `U`（未测）。完成后会自动打开本轮 Markdown 报告。

报告生成在 `.scratch/二期开发/verification/manual-run-时间戳/人工回归报告.md`。把报告的“结果”部分发回即可。

如只想做人工操作、不想重复跑自动化，可在仓库根目录执行：

```powershell
& tools/MtpManualRegression/Run-MtpManualRegression.ps1 -SkipAutomatedTests
```

独立内容岛实验可在脚本内选择启动；它没有接入主程序，副屏独立贴靠也仍是当前允许的降级。

启动器检查（不会启动 Host、备份偏好或填写人工结果）：

```powershell
& tools/MtpManualRegression/Test-Launcher.ps1
```

2026-10-03 修复：原批处理的 LF 换行/中文编码组合在 cmd.exe 下复现为 `'l.exe'` 找不到；中文 PowerShell 脚本缺少 BOM，在 Windows PowerShell 5.1 下另有解析失败。启动批处理现采用 ASCII、CRLF，PowerShell 文件采用 UTF-8 BOM、CRLF，并用目录内 `.gitattributes` 固定换行。已验证真实 cmd.exe → Windows PowerShell 5.1 调用、参数转发及非零退出码传播；这仅证明启动链路，不代表人工回归项目通过。
