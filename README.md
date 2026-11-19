# MeowLauncher

Hi！这里是 **MeowLauncher** 启动器源码仓库，我们致力于使用 Windows 原生的 Fluent Design 设计语言还原你最喜爱的启动器，让 Minecraft 像系统原生一样融入 Windows 。<br>

![首页](assets/readme/home.png)

> 非官方项目，与 Mojang、Microsoft 无隶属关系。Minecraft 是 Mojang AB 的商标。

## 从何而来

MeowLauncher 本身并非一个企划项目，只是作者与群友在群里戏弄大肥鱼时萌生的一个想法，作者利用其他ai辅助持续迭代半个月后便有了此项目 :P
<br>
*这你扯不扯.jpg*
<br>

## 功能介绍

### ☑️ 基础功能

版本管理、实例配置、版本目录、资下载等等很普遍的启动器必备功能。
<br>

### 💡 特色功能

1. 超强的下载管理系统：经过长时间运行测试后特调的下载系统，符合国内宝宝体质。相比同类型启动器，下载安装新实例速度达**10%以上**，含有加载器的复杂安装场景下最多可提升**50%以上**。
2. 首发连接**Mojang玩家系统**：与26.2快照引入的好友管理系统进行充分接入，实现了启动器的基础玩家系统。
3. **免第三方登录**的联机功能：在官方玩家系统的基础上，通过利用游戏中的聊天签名证书实现了无需任何第三方平台的安全鉴权系统，并通过Cloudflare Worker、自建中转服务器与 Easytier 工具实现了安全稳定的正版玩家联机平台。

> *联机功能仍处于开发者测试中，暂不对外开放。


![获取](assets/readme/get.png)

## 系统要求

受.NET 8.0与WinUI 3的显示，仅支持**Windows 10 1703及以上**的版本。

## 开始用

以下Readme内容待补充，项目仍在持续开发中。
欢迎各位提交issue，或者提出更多有价值的想法:D


## 常见问题

需要 Windows 10 1809 或更新版本、.NET 8 SDK，以及 Windows App Runtime 1.8。首次使用新手引导还需要 WebView2 Runtime。

联机模块源码独立维护；首次构建前放到项目期望的位置：

```powershell
git clone https://github.com/ETC233233/meow-room-module.git _export/meow-room-module
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -t:Rebuild
```

后端需要 Python；便携测试包包含内嵌 Python。运行源码时可通过 `MEOW_PYTHON` 指定解释器。联机组件仍按需下载，默认不会开启。

Debug 预览为 Review 开发构建，不是正式版。启动协议、整合包与取消任务的回归命令：

```powershell
python tools/test_launch_hardening.py
python tools/test_nov19_regressions.py
python tools/test_pack_install_status.py
dotnet run --project tools/task-cancellation-test/TaskCancellationTest.csproj
python tools/selfcheck_backend.py 26.3
```

准备包含内嵌 Python 的 Debug 便携测试包：

```powershell
powershell -File tools/build_debug_preview.ps1 -PythonDir <内嵌Python目录>
```

输出位于 `_export/debug-preview/`，包含 ZIP、SHA-256 校验值和依赖说明。此包需要 .NET 8 Desktop Runtime、Windows App Runtime 1.8 与 WebView2 Runtime；打包不会修改既有发布目录。

## 从源码构建

<!-- 待你写 -->

## 致谢

BMCLAPI 提供 Minecraft 与 Mod 加载器的部分下载服务<br>
MCIM 镜像站提供 Mods 检索加速服务<br>
EasyTier 提供基础联机工具服务<br>
以及每一位参与本项目测试和反馈的小伙伴们

## 许可证

[GPL-3.0](LICENSE)。第三方组件的许可证与声明见发布包里的 `Licenses\`（源码在 `MeowLauncher.WinUI/Licenses/`）。
<br>
联机模块 [meow-room-module](https://github.com/ETC233233/meow-room-module) 单独以 MIT 发布。
