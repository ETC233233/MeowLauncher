namespace MeowLauncher.WinUI.Services;

/// <summary>设置项目录：搜索、跳转、以后加新设置时只改这一处。Focus 必须在 SettingsPage.FocusSection 里有对应控件。</summary>
public sealed record SettingEntry(string Focus, string Section, string Title, string Subtitle, string Glyph, string Keywords);

public static class SettingsCatalog
{
    public static readonly SettingEntry[] All =
    [
        new("startscreen", "launch", "打开时显示哪一屏", "继续玩 / 组件板", "", "首页 启动时 负一屏 继续玩 组件板 默认页"),
        new("launch", "java", "额外 JVM 参数", "跟随全局的实例使用", "", "jvm 参数 启动参数 -xx g1gc 优化"),
        new("theme", "appearance", "主题", "跟随系统 / 深色 / 浅色", "", "主题 深色 浅色 夜间 暗色 dark light system"),
        new("themecolor", "appearance", "主题色", "跟随壁纸 / 系统强调色 / 自定义", "", "主题色 强调色 颜色 莫奈 accent"),
        new("background", "appearance", "窗口材质与背景", "亚克力、云母、纯色、像素壁纸、自定义图片", "", "背景 材质 亚克力 acrylic 云母 mica 壁纸 像素 透明 图片"),
        new("veil", "appearance", "图片模糊与遮罩", "跟随壁纸 / 自定义图片", "", "模糊 壁纸 图片 遮罩 对比 暗"),
        new("opacity", "appearance", "界面不透明度", "50–100%", "", "不透明度 透明度 透明"),
        new("fontscale", "appearance", "字体缩放", "85–125%", "", "字体 字号 缩放 大小"),
        new("network", "network", "资源搜索源", "Modrinth / CurseForge", "", "下载源 资源 搜索源 modrinth curseforge"),
        new("mcim", "network", "在线资源镜像优先级", "MCIM 优先 / 官方优先", "", "mcim 镜像 资源 加速 modrinth curseforge"),
        new("afterlaunch", "java", "启动游戏后", "留在前台 / 最小化到任务栏", "", "启动后 启动游戏 最小化 收起来 minimize"),
        new("proxy", "network", "网络代理", "跟随系统 / 不使用 / 自定义", "", "代理 proxy 网络 翻墙 socks http 端口"),
        new("mirror", "network", "下载镜像", "留空使用官方", "", "镜像 下载 mirror 加速 国内 bmclapi"),
        new("threads", "network", "下载线程", "并发数", "", "线程 并发 下载速度"),
        new("memory", "java", "最大内存", "自动 / 手动", "", "内存上限 分配 mem 自动内存 ram"),
        new("javamanage", "java", "Java 管理", "检测或添加 Java 运行时", "", "java 运行时 添加 检测 jdk"),
        new("window", "appearance", "窗口尺寸", "自适应 / 固定 / 自定义", "", "窗口 尺寸 大小 分辨率 自适应"),
        new("remember", "appearance", "记住窗口尺寸", "关闭时保存", "", "记住 窗口 尺寸"),
        new("hidden", "launch", "已隐藏的实例", "取消隐藏", "", "隐藏 实例 恢复 取消隐藏"),
        new("notifydone", "notify", "任务完成时提醒", "在启动器里提示", "", "通知 提醒 任务 完成 提示"),
        new("taskisland", "notify", "显示任务药丸", "内容区顶部", "", "任务 药丸 灵动岛 显示 隐藏"),
        new("updatechannel", "update", "更新渠道", "正式 / 尝鲜", "", "更新 渠道 尝鲜 beta 正式"),
        new("checkupdate", "update", "启动时检查更新", "打开时检查一次", "", "更新 检查 启动"),
        new("about", "about", "关于", "版本与运行环境", "", "关于 版本 信息 about"),
    ];
}
