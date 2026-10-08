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

## 许可

本项目按 **Apache License 2.0** 授权，协议全文见 [LICENSE](LICENSE)。

```
Copyright 2026 Morus-Fauston

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

该授权适用于本仓库的全部内容，**包括历史提交**。SPDX 标识：`Apache-2.0`。

许可授予的权利（含第 3 条的专利授权与第 6 条的商标除外声明）与义务以 [LICENSE](LICENSE) 全文为准；本节只为便于阅读，不构成对协议的修改。
