# 启动链路对照检查 · 2026-11-20

对照用户提供的教程与当前实际调用入口；本轮未修改启动实现、未启动游戏、未读取真实令牌。

教程（搜索索引返回了正文；直接打开页面失败）：https://zh.minecraft.wiki/w/Tutorial:编写启动器
微软桌面 OAuth 文档：https://learn.microsoft.com/en-us/windows/apps/develop/security/oauth2

## 最高优先级：正版会话没有接入启动入口

- `MeowLauncher.WinUI/Backend/sidecar.py:3686` 的 `launch_version` 直接取 `active_account`，向 `LaunchOptions` 传缓存 UUID/access_token。
- 已有 `_mc_access_token`、`_mc_refresh_session`，但启动入口没有使用；刷新助手也没有同步重新取得档案/UUID。
- 主登录入口 `ms_poll` 取得 Minecraft token 和 profile 后保存，没有单独调用 `entitlements`。
- MC 登录返回 `expires_in`，主账户记录只保存 `ts`，刷新助手使用固定 20 小时阈值。
- 备用 `ml2/auth.py:396 login_with_pkce_code` 把 `entitlements()` 的列表返回值当字典调用 `.get("items")`，异常被捕获后把 owns_game 设为 False。
- 应统一会话处理：验证/刷新令牌，获取权威档案，处理所有权结果，区分网络失败、登录过期与没有游戏；成功后才传给启动核心。

## 文件存在不等于完整

- `sidecar.py:3466 _version_file_gaps` 仅检查存在性；资源索引解析失败转为空对象，结果可能“没有缺失”。
- `Services/VersionHealth.cs:20` 检查报错或 `Ok=false` 时放行。
- `ml2/net.py:1650` 普通库下载使用二元组，没有传元数据 SHA-1；客户端下载 `:1678` 只传 size；索引 `:1576` 未传 SHA-1。
- 已有通用下载器支持 SHA-1，资源对象下载也在使用，不代表所有下载调用都已接上校验。
- 需要：大小/哈希校验、损坏索引重新获取、明确修复失败，不能让损坏索引伪装为资源完整。

## Classpath 和 natives 不完整

- `ml2/core.py:402 libraries` 同一依赖同时有 artifact 和适用 native classifier 时，追加 native 后 continue，漏掉普通 artifact。
- `:456 extract_natives` 不读取 `extract.exclude`，只跳过 META-INF；全部文件按 basename 平铺。
- 原生库解压异常通常被吞掉或仅记警告，仍继续运行 Java。
- 需要独立收集普通 classpath 与原生依赖，按规则解压，遇到关键原生库损坏应修复或明确停止。

## 规则与启动参数

- `core.py:199 rule_allows` 操作系统版本写死为 `10.0.26300`；未知架构条件会错误放行，`sys.machine` 也不是实际架构获取接口。
- `:593 has_custom_resolution` 固定 False，因此设置 width/height 后没有进入对应的参数规则。
- `:537 clientid`、`:538 auth_xuid` 固定为 0；缺少旧版 user_properties 占位符处理。
- `:580 extra_jvm.split()` 不能保留引号包裹的带空格参数。
- 参数替换之后没有检查残留 `${...}`。

## 老版资源映射

- 当前实现有 assets/objects，但没有发现按资源索引 virtual/map_to_resources 标志构建旧版映射的逻辑。
- `game_assets` 直接填 assets 根目录。不能因此宣称所有旧版资源加载已兼容。
- 需要按目标版本与索引标志准备 virtual/legacy 或对应游戏 resources 目录，而非对所有版本无条件复制。

## Java

- 自动找 Java、缺失安装和手动选择已存在。
- 启动入口只校验用户指定 Java 文件是否存在，没有在该分支验证实际大版本/架构兼容性。
- JSON 不声明 javaVersion 时，自动路径要求手动选择，缺少旧版版本范围的兼容兜底策略。

## 已有的基础能力

版本清单与 JSON 读取、版本继承合并、旧 minecraftArguments 与新 arguments、依赖/客户端/资源下载、普通离线 UUID、基本内存/JVM 参数、主类启动、隔离目录、日志重定向、进程退出与崩溃报告跟踪均已有。问题主要是分支不完整和入口未串联，不能用“存在函数”替代“实际启动路径已调用”。

## 实际复现证据

通过隔离临时目录、合成版本 JSON/native JAR，以及仅构造命令（不执行 Java）确认六项：

1. artifact 和 native 同条目时漏普通 JAR。
2. extract.exclude 未执行。
3. 自定义分辨率参数未生成。
4. user_properties 留有未替换占位符。
5. x64 Python 下 aarch64 条件被允许。
6. 损坏客户端文件与无效资源索引未报告缺失/损坏。

输出：`_archive/launcher-audit-1120-repro.json`。其他项目为代码调用与分支检查结果，没有用真实账户/网络完整登录或真实游戏启动验证。

## 文档适用边界

教程提供的启动步骤适合作为检查清单，但授权码流不是仅服务端可用。微软当前文档明确支持桌面公共客户端使用 PKCE、不包含 client_secret。仓库已有 PKCE 助手；此次确认的是其备用分支解析错误，不是缺少整个 PKCE 实现。
