# MeowLauncher.WinUI

WinUI 3 前端工程。项目总览、构建方式、目录说明都在仓库根目录的 [README](../README.md)，这里只留几份前端自己的设计资料：

- 设计契约（内部文档，未随仓库发布）：8 条契约 + 14 语义色 + 字号令牌
- 界面规范：内部文档，未随仓库发布
- Apple HIG 小组件与布局提取（内部文档，未随仓库发布）

改代码前先了解 WinUI 侧的分层（内部文档「项目结构」）：`Views/` 只放页面与视图逻辑，`Services/` 放后端调用与平台能力，`Controls/` 放可复用控件，`Themes/` 放令牌。

## 快速构建

```powershell
cd MeowLauncher.WinUI
dotnet build MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64
```

输出在 `bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/`。构建前先结束残留的 `MeowLauncher.WinUI` 进程，否则会遇到 `MSB3027` 文件被锁。
