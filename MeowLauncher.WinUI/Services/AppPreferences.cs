using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public sealed class AppPreferences
{
    /// <summary>旧版二选一主题；只用于兼容旧配置，实际模式看 ThemeMode。</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>主题模式：System（跟随系统）/ Dark / Light；旧配置没有该字段时默认跟随系统。</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>Acrylic / AcrylicThin / Mica / Solid / Wallpaper（跟随系统壁纸）/ PixelWallpaper（像素壁纸）/ Image</summary>
    public string BackgroundMode { get; set; } = "Acrylic";

    public string WindowMaterial { get; set; } = "";
    public string BackgroundTheme { get; set; } = "";
    public double WallpaperBlur { get; set; } = 20;
    public double WallpaperShade { get; set; } = 0.35;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveWindowMaterial => !string.IsNullOrWhiteSpace(WindowMaterial) ? WindowMaterial
        : BackgroundMode is "Mica" or "Solid" or "AcrylicThin" ? BackgroundMode
        : BackgroundMode is "Wallpaper" or "PixelWallpaper" or "Image" ? "Solid" : "Acrylic";

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveBackgroundTheme => !string.IsNullOrWhiteSpace(BackgroundTheme) ? BackgroundTheme
        : BackgroundMode is "Wallpaper" or "PixelWallpaper" or "Image" ? BackgroundMode : "None";

    public bool NormalizeAppearanceSelection()
    {
        var material = EffectiveWindowMaterial;
        var background = EffectiveBackgroundTheme;
        if (material != "Solid" && background != "None") background = "None";
        var legacy = background == "None" ? material : background;
        var changed = WindowMaterial != material || BackgroundTheme != background || BackgroundMode != legacy;
        WindowMaterial = material;
        BackgroundTheme = background;
        BackgroundMode = legacy;
        return changed;
    }

    /// <summary>自定义背景图的绝对路径；仅 BackgroundMode=Image 时使用。</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>自定义背景缩放方式：UniformToFill / Uniform。</summary>
    public string BackgroundStretch { get; set; } = "UniformToFill";

    /// <summary>旧版全局遮罩，仅保留配置兼容；现由图片背景的 WallpaperShade 取代。</summary>
    public double VeilOpacity { get; set; } = 0.05;

    /// <summary>窗口内容不透明度 0.5 - 1.0，对应旧版“窗口透明度”。</summary>
    public double UiOpacity { get; set; } = 1.0;

    /// <summary>字体缩放 0.85 - 1.25，对应旧版“字体缩放”。</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>主题色来源：wallpaper = 跟随壁纸取色（莫奈），accent = 系统强调色，custom = 自定义。</summary>
    public string ThemeColorMode { get; set; } = "wallpaper";

    /// <summary>自定义主题色 #RRGGBB；仅 ThemeColorMode=custom 时使用。</summary>
    public string ThemeColorHex { get; set; } = "#2FA35A";

    /// <summary>更新渠道：stable（正式）/ beta（尝鲜）；没有更新服务时只影响提示文案。</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>已经弹过「测试版 / 预发行版」提示的记号（见 MeowVersion.NoticeKey），同一记号只弹一次。</summary>
    public string VersionNoticeShown { get; set; } = "";

    /// <summary>已经提示过「有新版本」的版本号，同一个新版本只弹一次。</summary>
    public string UpdateNoticeShown { get; set; } = "";

    /// <summary>已经弹过「本地版本高于最新发行版」提示的本地版本，同一个版本只弹一次。</summary>
    public string AheadNoticeShown { get; set; } = "";

    /// <summary>启动时检查更新。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>网络代理：system（跟随系统）/ direct（不使用代理）/ custom（手动）。</summary>
    /// <summary>启动游戏后启动器自己怎么办：keep=留在前台，minimize=收进任务栏。</summary>
    public string AfterLaunch { get; set; } = "keep";

    public bool PreferOfficialResources { get; set; }

    public string ProxyMode { get; set; } = "system";

    /// <summary>自定义代理主机（ProxyMode=custom 时使用），例如 127.0.0.1。</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>自定义代理端口（ProxyMode=custom 时使用）。</summary>
    public int ProxyPort { get; set; } = 7890;

    /// <summary>自定义代理用户名（可选；留空表示代理不认证）。</summary>
    public string ProxyUser { get; set; } = "";

    /// <summary>自定义代理密码：DPAPI 保护的 base64 密文，明文永不落盘。</summary>
    public string ProxySecret { get; set; } = "";

    /// <summary>任务完成 / 失败时弹出系统通知。</summary>
    public bool NotifyTaskDone { get; set; } = true;

    /// <summary>任务进行中在标题栏显示药丸（TaskIsland 开关）。</summary>
    public bool ShowTaskIsland { get; set; } = true;

    /// <summary>首页小组件顺序，逗号分隔；空 = 使用默认顺序。</summary>
    public string HomeWidgetOrder { get; set; } = "";

    /// <summary>首页被隐藏的小组件 id，逗号分隔。</summary>
    public string HomeWidgetHidden { get; set; } = "";

    /// <summary>首页小组件尺寸覆盖，格式 id:列x行，逗号分隔；空 = 使用默认尺寸。</summary>
    public string HomeWidgetSizes { get; set; } = "";

    /// <summary>首页小组件分页排布，格式 "页码:id@行,列,宽,高;...|下一页..."；空 = 由旧字段迁移。</summary>
    public string HomeWidgetPages { get; set; } = "";

    /// <summary>首页形态：simple = 水平两屏（负一屏 + 组件板），classic = 只显示组件板。</summary>
    public string HomeLayout { get; set; } = "simple";

    /// <summary>固定在首页「固定版本」栏里的实例 id，竖线分隔，最多 6 个。</summary>
    public string PinnedVersions { get; set; } = "";

    /// <summary>打开启动器时停在哪一屏：continue = 继续玩（负一屏），board = 组件板。</summary>
    public string HomeStartScreen { get; set; } = "continue";

    /// <summary>负一屏里用户点选的实例 id（空 = 跟随后端当前实例）。</summary>
    public string HomeSelectedInstance { get; set; } = "";

    /// <summary>与 HomeSelectedInstance 配对：多游戏目录下同名实例靠 Root 区分；空 = 只按 Id。</summary>
    public string HomeSelectedRoot { get; set; } = "";

    /// <summary>用户在 Java 管理中添加的运行时。</summary>
    public List<string> JavaRuntimePaths { get; set; } = new();

    /// <summary>实例目录 → 是否显示启动控制台。</summary>
    public Dictionary<string, bool> InstanceConsoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>实例自定义封面：实例 id → 图片绝对路径；没有就走最新游戏截图/内置保底图。</summary>
    public Dictionary<string, string> HomeCovers { get; set; } = new();

    /// <summary>启动器窗口宽度（设计像素，0 = 用默认 1280）。</summary>
    /// <summary>窗口尺寸自适应（按工作区 55%/72% 与 DPI 计算）。默认开。</summary>
    public bool AutoWindowSize { get; set; } = true;

    /// <summary>忽略系统缩放比例：窗口和界面都按物理像素 1:1 渲染（150% 屏上不再放大 1.5 倍）。默认关。</summary>
    public bool IgnoreSystemDpi { get; set; }

    public int WindowWidth { get; set; }

    /// <summary>启动器窗口高度（设计像素，0 = 用默认 800）。</summary>
    public int WindowHeight { get; set; }

    /// <summary>关闭时是否记住当前窗口尺寸。</summary>
    /// <summary>GitHub 下载加速：开启后 github.com 的下载先走镜像前缀。</summary>
    /// <summary>同时下载的并发数（进程数），默认 3。</summary>
    public int DownloadParallel { get; set; } = 8;

    public bool GithubAccel { get; set; } = true;

    /// <summary>GitHub 加速镜像前缀；留空用内置默认值。</summary>
    public string GithubAccelHost { get; set; } = "";

    public bool RememberWindowSize { get; set; } = true;

    /// <summary>主游戏目录（兼容旧字段与单目录场景）；与 McDirs[0] 保持一致。</summary>
    public string McDir { get; set; } = "";

    /// <summary>已知游戏目录列表，第一个是主目录；空 = 首次使用时用后端报告的目录补齐。</summary>
    public List<string> McDirs { get; set; } = new();

    /// <summary>新手引导是否已完成：全新安装为 false，引导走完写 true，之后启动不再显示。</summary>
    public bool Onboarded { get; set; }

    /// <summary>引导里选的账号类型（microsoft / offline / skip），只用于引导汇总与后续提示。</summary>
    public string OnboardAccountKind { get; set; } = "";

    /// <summary>引导里选的自动更新三开关：检查 / 下载 / 安装。</summary>
    public bool AutoDownloadUpdate { get; set; } = true;

    public bool AutoInstallUpdate { get; set; }

    /// <summary>实例自定义顺序：目录 → 实例 id 列表（只记用户拖过/上下移过的目录）。</summary>
    public Dictionary<string, List<string>> VersionOrder { get; set; } = new();
}

public static class AppPreferencesStore
{
    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher.WinUI");

    public static string FilePath => Path.Combine(FolderPath, "app.json");

    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppPreferences();
            var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new AppPreferences();
            if (preferences.NormalizeAppearanceSelection()) Save(preferences);
            return preferences;
        }
        catch
        {
            try
            {
                var bak = FilePath + ".bak";
                if (File.Exists(bak))
                {
                    var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(bak)) ?? new AppPreferences();
                    if (preferences.NormalizeAppearanceSelection()) Save(preferences);
                    return preferences;
                }
            }
            catch { }
            return new AppPreferences();
        }
    }

    public static void Save(AppPreferences preferences)
    {
        try
        {
            preferences.NormalizeAppearanceSelection();
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");   // 同卷原子替换并留一份备份
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            App.LogCrash("PrefsSave", ex);   // 不再静默吞掉：至少留下日志
        }
    }
}

/// <summary>
/// 游戏目录解析：只认磁盘上真实存在的目录，绝不把猜出来的路径写回配置。
/// 取值顺序：app.json 的 McDirs → app.json 的 McDir → 旧版 settings4.json 的 mcDir →
/// settings4 的 mcDirs → %APPDATA%\.minecraft → 文档下的常见目录。
/// </summary>
public static class GameDirectoryResolver
{
    public const int MaxDirs = 6;

    /// <summary>旧版（Qt / 早期 WinUI）配置，只读，用作迁移候选。</summary>
    public static string LegacySettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", "settings4.json");

    public static List<string> Resolve(AppPreferences? preferences)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? candidate)
        {
            if (result.Count >= MaxDirs) return;
            var full = Normalize(candidate);
            if (full.Length == 0) return;
            if (!Directory.Exists(full)) return;
            if (!seen.Add(full)) return;
            result.Add(full);
        }

        if (preferences is not null)
        {
            foreach (var dir in preferences.McDirs) Consider(dir);
            Consider(preferences.McDir);
            // app.json 里有可用目录就以它为准；旧配置只在这层拿不到东西时参与迁移。
            if (result.Count > 0) return result;
        }

        var legacy = ReadLegacy();
        Consider(legacy.McDir);
        foreach (var dir in legacy.McDirs) Consider(dir);
        if (result.Count > 0) return result;

        // 没有显式选过目录时，用启动器目录下的 .minecraft 作为默认位置。
        // 不再自动接管 %APPDATA%\.minecraft：那会把「没选过目录」伪装成「已经选好」，
        // 首次启动也永远看不到「选择已有文件夹」这个入口。
        var launcherDefault = Path.Combine(AppContext.BaseDirectory, ".minecraft");
        try
        {
            Directory.CreateDirectory(launcherDefault);
            Consider(launcherDefault);
        }
        catch
        {
            // 启动器装在受保护目录时创建可能失败；退回系统里真实存在的老位置。
            Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Consider(Path.Combine(documents, "我的游戏", ".minecraft"));
            Consider(Path.Combine(documents, ".minecraft"));
        }

        return result;
    }

    /// <summary>目录里是否真的有 versions 子目录：用来区分「目录不存在」和「目录没装过游戏」。</summary>
    public static bool HasVersions(string? dir)
    {
        try
        {
            var full = Normalize(dir);
            return full.Length > 0 && Directory.Exists(Path.Combine(full, "versions"));
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(string? dir)
    {
        var value = (dir ?? "").Trim().Trim('"');
        if (value.Length == 0) return "";
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return "";
        }
    }

    private static (string McDir, List<string> McDirs) ReadLegacy()
    {
        try
        {
            if (!File.Exists(LegacySettingsPath)) return ("", new List<string>());
            using var document = JsonDocument.Parse(File.ReadAllText(LegacySettingsPath));
            var root = document.RootElement;
            var mcDir = root.TryGetProperty("mcDir", out var single) && single.ValueKind == JsonValueKind.String
                ? single.GetString() ?? ""
                : "";
            var dirs = new List<string>();
            if (root.TryGetProperty("mcDirs", out var many) && many.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in many.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) dirs.Add(value!);
                }
            }
            return (mcDir, dirs);
        }
        catch
        {
            return ("", new List<string>());
        }
    }
}
