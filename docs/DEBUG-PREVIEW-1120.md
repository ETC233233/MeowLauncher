# MeowLauncher · Debug 预览

本轮保持 Review 开发版通道，不创建正式版或公开 Release。

## 本轮变化

- 补齐启动前正版会话、所有权、UUID 和令牌刷新；补强 SHA-1、原生依赖、启动规则、参数、旧版资源映射和 Java 检查。细节见 [启动链路修复](LAUNCH-FIXES-1120.md)。
- 实例设置拆为独立 Win11 风格设置行；控件靠右，内存条占满卡片宽度。目录和隔离提示精简，实例副标题不再追加路径。
- 权限卡无状态信息时折叠反馈行；添加好友移到好友和申请列表下方；任务与通知选中底板覆盖整项。
- 新手引导在主页加载前判断，在同一主窗口承载，完成并保存后才进入启动器；初次 WebView2 初始化期间显示短加载状态。
- 标题栏搜索、任务入口改为紧凑矩形；引导中只留启动器标识和窗口按钮。Spotlight 独立显示在标题栏下，不再压住顶栏，弹层采用实色背景。
- 联机组件版本和操作并入联机实验区域；版本号作为标题下的副文本。GitHub 加速说明改为“建议国内玩家保持开启”。
- 在线整合包记录来源、项目和文件版本，重新打开可查看已安装实例，进行中可回到安装进度；安装阶段根据实际执行的整合包、Minecraft 和加载器操作生成。
- 取消信号贯穿排队、文件检查、补全、下载、启动和联机组件下载；后端按任务 ID 精确取消。已结束的任务可单条移除，任务结果不再重复进入通知列表。两个通道均显示数量，0 不显示。

## 检查

```powershell
python tools/test_launch_hardening.py              # 45 项
python tools/test_nov19_regressions.py             # 15 项
python tools/test_pack_install_status.py           # 9 项
dotnet run --project tools/task-cancellation-test/TaskCancellationTest.csproj # 8 项
python tools/selfcheck_backend.py 26.3             # 联网自检 10 项
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -t:Rebuild
```

本轮回归使用合成账户与隔离文件；真实实例 26.3 文件检查 / 修复后的复核为 `clean=true, missing=0`。未完成真实账号续期、游戏内启动、两台电脑联机及干净 Windows 环境的安装测试。

构建保留 3 个既有警告：`SkinHeadView.Projection` 成员隐藏（两次编译）和 `PRI263` 图标中性资源提示。没有把这些警告视为已修复。

## 生成测试包

```powershell
powershell -File tools/build_debug_preview.ps1 -PythonDir <内嵌Python目录>
```

生成 `_export/debug-preview/MeowLauncher-Review-<构建号>-Debug-win-x64.zip` 和 SHA-256 文件；不覆盖已有包。包中有内嵌 Python、引导资源、许可证、调试符号和 `BUILD-INFO.json`。

测试机需要 .NET 8 Desktop Runtime、Windows App Runtime 1.8 和 WebView2 Runtime。便携包应解压到单独目录运行，保留 `Backend`、`Assets`、`Licenses` 和 `.pri` 文件。
