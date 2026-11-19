# 启动链路修复 · 2026-11-20

对应 `LAUNCH-AUDIT-1120.md`，本轮补齐启动入口，不改变主页启动动画。

- 正版账户在 `sidecar.launch_version` 先调用统一会话校验。检查 Java 所有权和权威玩家档案；过期或 401 后刷新 MSA → Xbox Live → XSTS → Minecraft；保存轮换的刷新令牌、有效期、玩家名和 UUID。网络、登录过期、无所有权、无档案分别返回错误。离线账户走独立流程。启动核心实际接收刷新后的身份，未通过校验不会创建游戏进程。
- 初次设备码登录复用同一会话逻辑；备用授权码 / PKCE 分支修正所有权列表解析，网络异常不再伪装成所有权结果。
- 文件检查按元数据大小和 SHA-1 校验客户端、库、原生库、资源索引、资源对象。索引缺失、无效 JSON、无效对象哈希均不会当作完整。先修索引，再重新收集并修复对象；下载后再次复核。相同资源哈希只修一次。
- 安装时库、客户端和索引均向通用下载器传递 SHA-1 / size。保留已有的资源失败重试，并预先建立哈希前缀目录，避免并发建目录的偶发失败。
- 同一依赖的普通 artifact 与 native classifier 独立选择；classpath 包含普通 JAR，原生库按排除规则解压、保留相对路径、校验 ZIP CRC，越界或损坏时停止。不会在首次 Popen 失败后悄悄丢弃包装命令和日志重试。
- OS 名称、系统版本、架构和 feature 条件按运行环境求值。自定义分辨率生效；clientid / auth_xuid / user_properties 等占位符补齐，残留未知占位符明确报错。额外 JVM、游戏参数和包装命令使用 Windows 引号规则，重复的游戏参数不再逐词去重。
- 旧索引的 virtual / map_to_resources 按标志建立映射，隔离实例的 resources 位于其真实游戏目录中。
- Java 先读官方 javaVersion，旧版按版本范围兜底；检查所选运行时实际大版本和架构，不能运行或不匹配时停止。javaw 无 release 文件时用同目录 java.exe 探测。日志配置文件损坏时按哈希重下，使用元数据声明的 logging argument。

## 执行证据

```powershell
python tools/test_launch_hardening.py
# 45 tests, OK；合成账户/临时文件，不登录真实账号、不运行 Java 游戏进程
python tools/test_nov19_regressions.py
# 15 tests, OK
python tools/selfcheck_backend.py 26.3
# 联网自检 10 项全部 PASS；首次沙盒内尝试网络被 WinError 10013 拦截，获准外部执行后通过
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -t:Rebuild -v:q
# 0 错误、3 个既有警告（SkinHeadView.Projection / PRI263）
python MeowLauncher.WinUI/Backend/sidecar.py --verify-version 26.3
python MeowLauncher.WinUI/Backend/sidecar.py --repair-version 26.3 --progress
python MeowLauncher.WinUI/Backend/sidecar.py --verify-version 26.3
# 实际实例复核：clean=true, missing=0
```

日志：`_archive/launch-hardening-tests.log`、`launch-hardening-previous-tests.log`、`launch-hardening-selfcheck.log`、`launch-hardening-build.log`、`launch-hardening-real-repair.log`、`launch-hardening-real-file-check-after.json`。

重要源码修改前备份：`_archive/bak/launch-hardening-20261120-035129/`。

边界：没有使用真实账户测试令牌过期续期，没有真启动游戏或逐个版本运行验证。网络自检和真实资源修复通过，不等同于所有版本均可启动。

## 同轮界面调整

- 实例设置拆成独立设置行卡：封面、控制台、跟随全局、隔离、内存策略、内存分配、Java、JVM 策略和额外参数；内存条铺满卡片。
- 去掉“自动置顶”和隔离状态的括号说明；实例选择器、主页启动卡和最近实例副标题不再追加目录路径。目录分组和实例信息仍展示路径，实例身份仍以目录 + ID 区分。
- 权限卡状态区为空时折叠，不留下底部空行。
- 分段单选模板的选中底板覆盖整个控件，文字内边距只作用于内容，不再把底板压到文字宽度。
- 添加好友表单移至好友列表和申请列表下方，使用 6px 圆角、32px 控件。
- 首次运行在主页创建前检测 Onboarded，在主窗口中承载原网页，不再创建 OnboardingWindow。完成并持久化后才初始化主页、后台外壳和首次导入任务；关于页重开引导使用同一窗口。

界面源码修改前分别备份至 `_archive/bak/instance-settings-cards-*`、`instance-subtitle-*`、`account-onboarding-*`。
