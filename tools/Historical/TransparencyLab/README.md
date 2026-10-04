# TransparencyLab 历史诊断工具

用途：保留二期早期普通 WinUI 顶级窗口的透明合成、DWM 状态及像素诊断。当前任务栏 Host 使用内容岛，本工具不证明当前 Host 外观或任务栏行为。

源码保留在本目录，不进入 `Mtp.sln`。如需复现原工具，可从仓库根目录构建：

```powershell
dotnet build tools/Historical/TransparencyLab/TransparencyLab.csproj -c Release
```

原 `shots/TransparencyLab-0.png` 和 `shots/TransparencyLab-1.png` 内容相同，均完整保留在本地 `.scratch/二期开发/evidence/05/TransparencyLab-shots/`；历史截图不继续进入公开 Git。未来探针在本目录生成的 `shots/` 同样不跟踪。

本工具没有作为当前 Host 的操作入口；当前流程见工具总入口及本地 Host 操作手册。
