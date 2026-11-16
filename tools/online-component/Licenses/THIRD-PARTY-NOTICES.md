# 联机组件的第三方声明 / Third-Party Notices (online component)

本包由 MeowLauncher 的联机组件打包而成，包含下列第三方二进制，均**原样取自上游官方发布包，未修改**。

| 组件 | 版本 | 文件 | 许可证 | 许可证文件 | 上游 |
|---|---|---|---|---|---|
| EasyTier | 2.6.4 | `easytier/easytier-windows-x86_64/easytier-core.exe` | LGPL-3.0 | `EasyTier-LGPL-3.0.txt`、`GPL-3.0.txt` | https://github.com/EasyTier/EasyTier |
| WinDivert | 随 EasyTier 2.6.4 发布包 | `Packet.dll`、`WinDivert64.sys` | LGPL-3.0 或 GPL-2.0（双许可，按 LGPL-3.0 使用） | `WinDivert-LICENSE.txt` | https://github.com/basil00/WinDivert |
| Wintun | 随 EasyTier 2.6.4 发布包 | `wintun.dll` | WireGuard LLC「Prebuilt Binaries License」 | `Wintun-Prebuilt-Binaries-License.txt` | https://www.wintun.net/ |

- 联机模块（`MeowRoom.Module.dll`）和提权代理（`RoomAgent.exe`）是 MIT 许可，源码：https://github.com/ETC233233/meow-room-module 。包内的 .NET 运行时文件是 MIT 许可（https://github.com/dotnet/runtime ），BouncyCastle 是 MIT 许可。
- 启动器通过独立进程调用 EasyTier，没有与它链接。运行前提权代理会按云端签名清单校验这些文件的 SHA-256。
- 对应源代码：EasyTier 2.6.4 与 WinDivert 的源代码可从上表上游仓库的对应版本标签免费获取。
- 依 LGPL，你有权用自己编译的兼容版本替换这些库；Wintun 仅可随使用其公开 API 的软件（EasyTier）一起分发，不得单独分发、修改或反编译。
- MeowLauncher 与 Mojang、Microsoft、Minecraft、WireGuard 无隶属关系。
