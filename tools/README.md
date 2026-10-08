# 开发工具入口

| 工具 | 当前用途 |
| --- | --- |
| [TaskbarIslandLab](TaskbarIslandLab/README.md) | 保留的独立内容岛与性能对照工具，不作为 Host 运行依赖。 |
| [Historical](Historical/README.md) | 旧承载路线的诊断源码与已失效的人工回归流程，仅供追溯。 |

当前功能测试从 Host 开发控制台进入，操作手册位于本地私有 `Docs/使用手册/Host测试平台操作手册.md`。不要用旧工具的验收流程代替当前 Host 测试。

工具运行日志、截图和报告留在 `.scratch/<期次>/evidence/`。构建输出留在各项目 `bin/obj`，不提交运行产物。

## 归档说明

本目录**只保留工具源码**。各工具的 `bin/`、`obj/` 是构建产物（`.gitignore` 已忽略 `**/bin/`、`**/obj/`），需要时重新构建即可，不作为长期材料保留。清理用 `pwsh -File tests/Clean-BuildOutput.ps1 -Scope all`。

已从本目录移除的目录（保留源码于他处，便于追溯）：

| 曾用目录 | 现状 |
| --- | --- |
| `TaskbarVisibilityLab/` | 05E「任务栏可见性探针」实验（`v0.1.0-alpha.10`，2026-09-20）的观测工具，提供 observe／baseline／fixture 三模式与每屏 JSONL 观测。该实验**未进入 main**（提交 `03634bd`，属分支 `codex/05e-taskbar-visibility-probe`），因此 main 工作树中只有它的构建残留、没有源码。残留产物已清理；需要时从该分支取源码，不要从产物反推实现。 |

判定"某工具目录是否属于 main"的方法：`git ls-files <目录>` 为空即该目录在 main 上无源码；再用 `git branch -a --contains <提交>` 找它所属的分支。
