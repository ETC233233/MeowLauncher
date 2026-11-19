# 第三方组件声明 / Third-Party Notices

MeowLauncher 随包分发或在运行时使用下列第三方组件。各组件的版权归其作者所有，许可证全文见本目录下对应文件（随启动器一起安装在 `Licenses\`）。
MeowLauncher 自身以 GPL-3.0 发布（全文见 `GPL-3.0.txt`），自带美术素材同样适用。
MeowLauncher 是非官方的第三方启动器，与 Mojang、Microsoft、Minecraft 无任何隶属或授权关系。Minecraft 是 Mojang AB / Microsoft 的商标。

## 一、联机组件（按需下载，不随启动器分发）

联机功能所需的联机模块、提权代理和 EasyTier 不包含在启动器安装包里，首次进入「联机」页时由用户同意后下载（约 50 MB）。
下载内容按云端签名清单里的哈希校验，不一致会被丢弃。组件包内自带它的许可证和第三方声明（`Licenses\`），其中包括：

| 组件 | 许可证 | 上游 |
|---|---|---|
| EasyTier 2.6.4 | LGPL-3.0 | https://github.com/EasyTier/EasyTier |
| WinDivert（`Packet.dll`、`WinDivert64.sys`） | LGPL-3.0 或 GPL-2.0（双许可） | https://github.com/basil00/WinDivert |
| Wintun（`wintun.dll`） | WireGuard LLC「Prebuilt Binaries License」 | https://www.wintun.net/ |
| 联机模块 `MeowRoom.Module`、提权代理 `RoomAgent` | MIT | https://github.com/ETC233233/meow-room-module |

这些二进制均原样取自上游官方发布包、未修改；启动器通过独立进程调用 EasyTier，没有与它链接。
源代码获取：EasyTier 与 WinDivert 的源代码可从上游仓库对应版本标签免费获取；联机模块和组件打包脚本的源码在上表仓库与本项目仓库中。

## 二、启动器自带的库与运行时

| 组件 | 许可证 | 许可证文件 | 说明 |
|---|---|---|---|
| Windows App SDK（WinUI 3） | MIT | `WindowsAppSDK-MIT.txt` | 界面框架 |
| GitHub Octicons | MIT | `Octicons-MIT.txt` | 关于页 GitHub 仓库图标（mark-github-24） |
| .NET 运行时 / `System.IO.Pipes.AccessControl` | MIT | `dotnet-runtime-MIT.txt` | 自包含运行时、提权代理的管道权限 |
| BouncyCastle.Cryptography 2.4.0 | MIT | `BouncyCastle-MIT.txt` | 云端组件清单的 Ed25519 验签 |
| Python（内嵌，用于后端） | PSF License | `Python-PSF-LICENSE.txt` | `Backend\python\` |
| skinview3d | MIT | `skinview3d-MIT.txt` | 皮肤 3D 预览（`Assets\skinviewer`） |
| three.js r160 | MIT | `ThreeJS-MIT.txt` | skinview3d 的渲染依赖 |

## 美术素材

启动器自带的猫咪壁纸、图标等美术素材由项目作者借助 Anthropic 的 Claude 模型创作，归项目作者所有，授权方式见项目许可证。

本启动器不内置任何 Mojang / Minecraft 的图标、Logo 或默认皮肤素材；玩家自己的皮肤、披风由 Mojang 官方服务按需获取，仅用于本机显示。

## 三、运行时按需下载（不随包分发）

- **Java 运行时**：用户同意后，从 Azul（Zulu）官方下载到启动器自己的目录，适用 Azul 的许可证（GPLv2 + Classpath Exception）。
- **Minecraft 游戏文件、库、资源**：从 Mojang 官方或 BMCLAPI 镜像下载，适用 Mojang 的 EULA；MeowLauncher 不分发任何 Minecraft 文件。
- **Mod / 整合包**：来自 Modrinth 等平台，版权归各自作者，适用各自许可证。
- **Mod 加载器**（Fabric、Quilt、Forge、NeoForge）：从各自官方渠道下载安装。

## 四、联机服务

联机功能连接 Cloudflare Workers 上的账号 / 房间 / 配置服务；联机组件的下载来源由云端清单指定，可能经过 GitHub 或第三方加速代理。登录使用 Minecraft 玩家证书，不会读取或上传你的 Microsoft / Mojang 令牌。详见联机模块仓库的 SECURITY.md。

## 加载器图标

图标仅用于标识加载器；各项目的名称和商标归其权利人所有。

- Fabric Loader — [官方资源图标](https://github.com/FabricMC/fabric-loader/blob/cb9523bc29ef775c50b6aef5e43bc56927cd8e67/src/main/resources/assets/fabricloader/icon.png)，Apache-2.0，见 `Apache-2.0.txt`。
- Quilt Project — [官网方形图标](https://github.com/QuiltMC/quiltmc.org/blob/main/public/assets/img/logo-square.png)，见官网随附的 `Quilt-Art-NOTICE.md` 和 `Apache-2.0.txt`。
- Minecraft Forge — [官方安装页图标](https://files.minecraftforge.net/static/images/favicon-32x32.png)。
- NeoForged 团队 / Ridanisaurus — [官方狐狸图标](https://github.com/neoforged/.github/blob/main/art/neoforged.ico)，从 ICO 转为 PNG，许可 CC BY 4.0，见 `NeoForged-Art-CC-BY-4.0.txt`。
