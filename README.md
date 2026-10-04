# Mo's Taskbar Platform

MTP 是开发中的 Windows 协作式任务栏小组件平台。当前 Host 包含二期开发测试控制台、最小本地组件和内容岛承载；完整产品设置壳、SDK 与 Broker 尚未交付。

## 仓库入口

| 路径 | 用途 |
| --- | --- |
| [src](src/) | 当前平台核心、公共契约、Host Runtime 与 Windows Host 实现。 |
| [tests](tests/README.md) | 纯规则、Windows 适配器、WinUI 进程与历史夹具的验证入口。 |
| [tools](tools/README.md) | 可复用对照工具及历史诊断材料。 |
| [CONTEXT.md](CONTEXT.md) | 项目术语与领域边界。 |
| [AGENTS.md](AGENTS.md) | 开发硬约束及本地规划入口。 |
| [CHANGELOG.md](CHANGELOG.md) / [CHANGELOG.txt](CHANGELOG.txt) | 详细与精简版本记录。 |

`Docs/`、`.scratch/` 和 `参考项目/` 是本地私有材料，不随公开 Git 克隆提供。需要按票据继续开发的 Agent 必须取得对应规划、规格与票据交接包；仅有代码不能代替这些依据。本机票据入口为 `.scratch/README.md`。

## 构建与自动化

在 Windows 上使用 .NET 10；Host 运行需要匹配的 Windows App Runtime。

```powershell
dotnet build Mtp.sln -c Release
dotnet test Mtp.sln -c Release
```

独立 WinUI 回归与对照工具另见测试和工具入口。构建、自动化或截图不等于真实 Windows 人工验收。
