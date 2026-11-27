using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using MeowLauncher.WinUI.Views;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MeowLauncher.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, ToggleButton> _navButtons;
    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private bool _shellLoaded;
    private bool _shellAccountSettled;
    private bool _dark = true;
    private string _themeMode = "System";
    private bool _highContrast;
    private readonly UISettings _uiSettings = new();
    private readonly AppPreferences _preferences = AppPreferencesStore.Load();
    private OverlappedPresenter? _presenter;
    private bool _suppressSearch;
    private double _appliedFontScale = -1;
    private string _themeColorSignature = "";
    private string _pendingSettingsFocus = "";
    private string _pendingOnlineQuery = "";
    private string? _pendingLogsFile;
    private List<SearchHit> _hits = [];
    /// <summary>当前实际页面标签（含宿主子页），顶栏搜索的作用域文案与「本页」范围跟着它走。</summary>
    private string _currentTag = "home";

    /// <summary>一层页面历史（只记 tag 与少量参数）；侧栏/快捷键属于根导航，不进历史。</summary>
    private readonly List<NavEntry> _navHistory = [];
    private NavEntry? _currentEntry;
    private bool _restoringHistory;

    private sealed record NavEntry(string Tag, string SettingsFocus, string OnlineQuery, string LogsFile, Action? Back = null);

    public Snapshot? Snapshot { get; private set; }
    public string CurrentSection { get; private set; } = "home";
    public bool IsDarkTheme => _dark;
    private int _lastNotificationCount;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>任务完成时如果窗口不在前台，闪一下任务栏图标（设置页「任务完成时在启动器里提醒」）。</summary>
    private void MaybeFlashOnTaskDone(bool taskFinished = false)
    {
        var count = TaskCenter.Notifications.Count;
        var added = count > _lastNotificationCount;
        _lastNotificationCount = count;
        if ((!added && !taskFinished) || !_preferences.NotifyTaskDone) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (GetForegroundWindow() == hwnd) return;
            // 不再弹 Windows 系统通知：它不会自动消失、也不显眼。通知统一显示在右上角的灵动岛里，
            // 窗口不在前台时只闪任务栏，切回来就能在岛里看到。（Services/Toasts.cs 保留，暂不使用。）
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG
                uCount = 3,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }

    /// <summary>设置页「显示任务药丸」开关。</summary>
    public void SetTaskIslandVisible(bool visible) =>
        TaskIsland.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public AppPreferences Preferences => _preferences;
    public IntPtr WindowHandle { get; private set; }
    public string BackgroundMode => _preferences.BackgroundMode;
    public double VeilOpacity => _preferences.VeilOpacity;
    public double UiOpacity => _preferences.UiOpacity;
    public double FontScale => _preferences.FontScale;

    /// <summary>给页面推锚点用：整窗根元素与壁纸图层。</summary>
    public FrameworkElement RootElement => RootGrid;
    public WallpaperLayer WallpaperControl => Wallpaper;

    public void ApplyTheme(bool dark, bool persist) => SetThemeMode(dark ? "Dark" : "Light", persist);

    /// <summary>设置主题模式：System / Dark / Light。旧系统不支持应用深浅色时强制跟随系统。</summary>
    public void SetThemeMode(string mode, bool persist)
    {
        var normalized = NormalizeThemeMode(mode);
        if (!SystemTheme.IsSupported) normalized = "System";
        _themeMode = normalized;
        _preferences.ThemeMode = normalized;
        _dark = string.Equals(normalized, "System", StringComparison.OrdinalIgnoreCase)
            ? SystemTheme.IsDark()
            : string.Equals(normalized, "Dark", StringComparison.OrdinalIgnoreCase);
        _preferences.Theme = _dark ? "Dark" : "Light";
        if (persist) AppPreferencesStore.Save(_preferences);
        ApplyResolvedTheme();
    }

    public string ThemeMode => _themeMode;

    public bool SystemThemeSupported => SystemTheme.IsSupported;

    private static string NormalizeThemeMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "System",
        };

    private void ApplyResolvedTheme()
    {
        RootGrid.RequestedTheme = _highContrast ? ElementTheme.Default
            : _dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyTitleBarTheme(_dark);
        RefreshAppearance(false);
    }

    private void OnSystemColorValuesChanged(UISettings sender, object args)
    {
        if (!string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase)) return;
        DispatcherQueue.TryEnqueue(ApplySystemThemeIfNeeded);
    }

    private void ApplySystemThemeIfNeeded()
    {
        if (!string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase)) return;
        var dark = SystemTheme.IsDark();
        if (dark == _dark) return;
        _dark = dark;
        _preferences.Theme = dark ? "Dark" : "Light";
        ApplyResolvedTheme();
    }

    private readonly AccessibilitySettings _access = new();
    private string _backdropKind = "?";

    /// <summary>只在材质种类真的变化时才换 SystemBackdrop——每换一次 DWM 会黑一帧。</summary>
    private void ApplyBackdrop(string kind)
    {
        if (_backdropKind == kind)
        {
            if (SystemBackdrop is ThemeSynchronizedAcrylicBackdrop acrylic) acrylic.Dark = _dark;
            return;
        }
        _backdropKind = kind;
        SystemBackdrop = kind switch
        {
            "mica" => new MicaBackdrop(),
            "acrylic" => new ThemeSynchronizedAcrylicBackdrop { Dark = _dark },
            "acrylic-thin" => new ThemeSynchronizedAcrylicBackdrop { Dark = _dark, Thin = true },
            _ => null,
        };
    }

    /// <summary>按偏好重画窗口背景、遮罩、内容不透明度与字体缩放。</summary>
    public void RefreshAppearance(bool persist)
    {
        if (_preferences.NormalizeAppearanceSelection()) AppPreferencesStore.Save(_preferences);
        var material = _preferences.EffectiveWindowMaterial;
        var background = _preferences.EffectiveBackgroundTheme;
        var mode = background == "None" ? material : background;

        string? imagePath = null;
        if (string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase))
            imagePath = _preferences.BackgroundImage;
        else if (string.Equals(mode, "Wallpaper", StringComparison.OrdinalIgnoreCase))
            imagePath = SystemWallpaperPath();

        var imageTheme = mode is "Wallpaper" or "Image";
        // A bitmap can fail asynchronously even when the file exists. Keep an
        // opaque plane beneath it so loading/invalid/transparent images never
        // turn a background theme into a transparent window.
        BackgroundImageBase.Visibility = imageTheme ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageBase.Background = new SolidColorBrush(_dark
            ? Color.FromArgb(255, 11, 13, 16) : Color.FromArgb(255, 245, 246, 248));
        var hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
        var imageKey = imageTheme ? (imagePath ?? "") : "";
        if (imageKey.Length > 0 && hasImage) imageKey += "|" + File.GetLastWriteTimeUtc(imagePath!).Ticks;
        var blur = imageTheme ? (int)Math.Clamp(_preferences.WallpaperBlur, 0, 40) : 0;
        imageKey += "|blur=" + blur;
        if (!imageTheme || !hasImage)
        {
            _backgroundImageGeneration++;
            _backgroundImageKey = "";
            BackgroundImageHost.Source = null;
            BackgroundImageHost.Visibility = Visibility.Collapsed;
        }
        else if (_backgroundImageKey != imageKey)
        {
            _backgroundImageKey = imageKey;
            _ = LoadBackgroundImageAsync(imagePath!, ++_backgroundImageGeneration, blur);
        }

        // 像素壁纸是一个独立主题：只有选它时才画，和亚克力/云母互斥。不要再加环境变量旁路，否则会和设置打架。
        var wallpaperEnabled = string.Equals(mode, "PixelWallpaper", StringComparison.OrdinalIgnoreCase);
        // 高对比度下壁纸只是噪声：只换蒙层色不够，整层收起。
        bool highContrast;
        try { highContrast = _access.HighContrast; } catch { highContrast = false; }
        Wallpaper.Visibility = wallpaperEnabled && !highContrast
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 壁纸收起时把动画一起停掉：Collapsed 不会停 Composition 动画，猫的 DispatcherQueueTimer 也照跑。
        Wallpaper.SetActive(Wallpaper.Visibility == Visibility.Visible);
        if (Wallpaper.Visibility != Visibility.Visible) Wallpaper.SetCatHidden(true);
        // 壁纸自带色彩与暗幕时不再叠主题染色，否则整窗会被品牌绿洗平、远山看不出层次。
        ThemeTintLayer.Visibility = Wallpaper.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 壁纸开时侧栏留洞，用一层很淡的色罩保证文字对比度（Claude 给定值：深 20%、浅 40%）
        try
        {
            if (Wallpaper.Visibility == Visibility.Visible)
                // 壁纸主题下侧栏是半透明面板（不是玻璃）：压在像素画上仍要读得清
                // 浅色不要用深色面板（会发灰、深字压不住），改用高不透明度的白
                SidebarRoot.Background = new SolidColorBrush(_dark
                    ? Color.FromArgb(0x59, 0x0A, 0x0F, 0x0D)
                    : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
            else
                SidebarRoot.Background = new SolidColorBrush(_dark
                    ? Color.FromArgb(0x0A, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        }
        catch { }

        if (Wallpaper.Visibility == Visibility.Visible)
        {
            // 壁纸主题：不用系统材质，壁纸自己带蒙层
            ApplyBackdrop("none");
            RootVeil.Background = new SolidColorBrush(Colors.Transparent);
        }
        else if (hasImage)
        {
            // 自定义背景铺底：去掉系统材质，只留一层可调的暗色遮罩保证正文对比度。
            ApplyBackdrop("none");
            var alpha = (byte)Math.Round(Math.Clamp(_preferences.WallpaperShade, 0, 0.8) * 255);
            RootVeil.Background = new SolidColorBrush(_dark
                ? Color.FromArgb(alpha, 0, 0, 0)
                : Color.FromArgb(alpha, 255, 255, 255));
        }
        else if (string.Equals(material, "Solid", StringComparison.OrdinalIgnoreCase))
        {
            ApplyBackdrop("none");
            RootVeil.Background = new SolidColorBrush(
                _dark ? Color.FromArgb(255, 11, 13, 16) : Color.FromArgb(255, 245, 246, 248));
        }
        else
        {
            ApplyBackdrop(string.Equals(material, "Mica", StringComparison.OrdinalIgnoreCase) ? "mica"
                : string.Equals(material, "AcrylicThin", StringComparison.OrdinalIgnoreCase) ? "acrylic-thin" : "acrylic");

            RootVeil.Background = new SolidColorBrush(Colors.Transparent);
        }

        var opacity = Math.Clamp(_preferences.UiOpacity, 0.5, 1.0);
        TitleBarRoot.Opacity = opacity;
        SidebarRoot.Opacity = opacity;
        ContentHost.Opacity = opacity;

        // 字体缩放遍历整棵可视化树，只在比例真的变化时才重跑，拖动遮罩/透明度不至于掉帧。
        if (Math.Abs(_appliedFontScale - _preferences.FontScale) > 0.001)
        {
            _appliedFontScale = _preferences.FontScale;
            UiScale.Apply(RootGrid, _preferences.FontScale);
        }
        SyncThemeColor();

        if (persist) AppPreferencesStore.Save(_preferences);
    }

    public void SetBackgroundMode(string mode, string? imagePath, bool persist)
    {
        SetAppearanceSelection(mode is "Wallpaper" or "PixelWallpaper" or "Image" ? null : mode,
            mode is "Wallpaper" or "PixelWallpaper" or "Image" ? mode : null, imagePath, persist);
    }

    private string _backgroundImageKey = "";
    private int _backgroundImageGeneration;

    private async Task LoadBackgroundImageAsync(string path, int generation, int blur)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            Microsoft.UI.Xaml.Media.ImageSource bitmap;
            if (blur > 0)
            {
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var scale = Math.Min(1.0, 1280.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                var width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
                var height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
                var transform = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = width, ScaledHeight = height };
                var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
                if (generation != _backgroundImageGeneration) return;
                // Width/height below are the transformed pixel array dimensions.
                // Desktop wallpapers have no EXIF rotation; ignoring it keeps custom
                // wallpaper files consistent with the transform dimensions as well.
                var pixels = data.DetachPixelData();
                var result = await Task.Run(() => Services.WallpaperBlur.Apply(pixels, (int)width, (int)height, blur));
                if (generation != _backgroundImageGeneration) return;
                var writable = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap((int)width, (int)height);
                using var output = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(writable.PixelBuffer);
                await output.WriteAsync(result);
                writable.Invalidate();
                bitmap = writable;
            }
            else
            {
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                bitmap = image;
            }
            if (generation != _backgroundImageGeneration) return;
            BackgroundImageHost.Source = bitmap;
            BackgroundImageHost.Visibility = Visibility.Visible;
            App.Trace($"Background image loaded: blur={blur}");
        }
        catch (Exception ex)
        {
            if (generation != _backgroundImageGeneration) return;
            BackgroundImageHost.Source = null;
            BackgroundImageHost.Visibility = Visibility.Collapsed;
            App.Trace("Background image failed: " + ex.GetType().Name);
            TaskCenter.PostMessage("背景图片加载失败", "暂时使用纯色背景，请重新选择背景主题或图片。", "提示");
        }
    }

    public void SetAppearanceSelection(string? material, string? background, string? imagePath, bool persist)
    {
        var currentMaterial = _preferences.EffectiveWindowMaterial;
        var currentBackground = _preferences.EffectiveBackgroundTheme;
        _preferences.WindowMaterial = material ?? currentMaterial;
        _preferences.BackgroundTheme = background ?? currentBackground;
        _preferences.BackgroundMode = _preferences.BackgroundTheme == "None"
            ? _preferences.WindowMaterial : _preferences.BackgroundTheme;
        if (imagePath is not null) _preferences.BackgroundImage = imagePath;
        RefreshAppearance(persist);
    }

    public void SetVeilOpacity(double veil, bool persist)
    {
        _preferences.VeilOpacity = Math.Round(Math.Clamp(veil, 0.0, 0.12), 2);
        RefreshAppearance(persist);
    }

    public void SetUiOpacity(double opacity, bool persist)
    {
        _preferences.UiOpacity = Math.Round(Math.Clamp(opacity, 0.5, 1.0), 2);
        RefreshAppearance(persist);
    }

    /// <summary>主题色随「模式 / 自定义色 / 背景图 / 明暗」变化重算，其他外观滑杆不会触发重新解码壁纸。</summary>
    private string ThemeColorSignature => string.Join("|",
        _preferences.ThemeColorMode, _preferences.ThemeColorHex,
        _preferences.BackgroundMode, _preferences.BackgroundImage, _dark ? "D" : "L");

    private void SyncThemeColor()
    {
        if (ThemeColorSignature == _themeColorSignature) return;
        _themeColorSignature = ThemeColorSignature;
        _ = ThemeColorService.ApplyAsync(_preferences, _dark);
    }

    /// <summary>设置页改完主题色立刻重算（不等去抖），并回写色块。</summary>
    public async Task RefreshThemeColorAsync()
    {
        _themeColorSignature = ThemeColorSignature;
        await ThemeColorService.ApplyAsync(_preferences, _dark);
    }

    public void SetFontScale(double scale, bool persist)
    {
        _preferences.FontScale = Math.Round(Math.Clamp(scale, 0.85, 1.25), 2);
        RefreshAppearance(persist);
    }

    /// <summary>读取当前系统壁纸路径，供“跟随壁纸”背景模式使用。</summary>
    public static string? SystemWallpaperPath()
    {
        // 读取壁纸必须用 SPI_GETDESKWALLPAPER(0x0073)。之前写成 0x0014 = SPI_SETDESKWALLPAPER，
        // 传空缓冲区等于「把桌面壁纸设成空」，会把用户当前会话的壁纸清掉（桌面变黑）。
        const uint SPI_GETDESKWALLPAPER = 0x0073;
        var buffer = new System.Text.StringBuilder(1024);
        return SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0) ? buffer.ToString() : null;
    }

        // 窄窗口模式：设计宽 < 1000 时侧栏收成 64px 图标栏，指针移入或 Ctrl+B 覆盖展开。
        private const double CompactWidthThreshold = 1000;
        // 首启默认画布 = 设计规范的最小画布 1120×760（150% DPI 下物理 1680×1140）；
        // 用户拉大过就按偏好的尺寸恢复，只有小于最小值时才回落到默认。
        private const int DefaultDesignWidth = 1120;
        private const int DefaultDesignHeight = 760;
        private const double AdaptiveWidthRatio = 0.66;
        private const double AdaptiveHeightRatio = 0.80;
        private const int AdaptiveMaxWidth = 1600;
        private const int AdaptiveMaxHeight = 1000;
        private const int MinDesignWidth = 1120;
        private const int MinDesignHeight = 760;
        private const double SidebarRailWidth = 64;
        private const double SidebarFullWidth = 218;

        private bool _sidebarAnimatedOnce;
        private Storyboard? _sidebarStory;

        private bool _compact;

        /// <summary>导航按钮的原始内容（图标 + 文字），窄档切图标-only 时用它还原。</summary>
        private readonly Dictionary<string, object?> _navContentSnapshot = new();

        /// <summary>悬停展开态的光标轮询（兜底收起）。</summary>
        private readonly DispatcherTimer _hoverWatch = new();

        // ---- 在线状态（向 Mojang 好友系统上报，带 MeowLauncher 标记）----
        // 服务端 10 分钟没收到更新就算离线，所以 4 分钟报一次；退出时主动报 OFFLINE。
        // 游戏在运行、或 settings4.json 的 shareOnline=false 时，后端会跳过，不会覆盖游戏里的状态。
        private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromMinutes(4) };
        private bool _presenceBusy;

        // 联机功能要「开发者认证 → 开发者测试功能 → 联机」三层都开才生效（默认全关，第一版发布不带联机）：
        // 没生效的时候这里什么都不做，也不会向 Mojang 上报任何状态。
        private bool _presenceHooked;

        /// <summary>
        /// 开发版 / 预发行版第一次启动时，用任务与通知（灵动岛）提示一次；同一个记号只提示一次（见 MeowVersion.NoticeKey）。
        /// 正式版没有提示。关于页里这段文字是常驻的。
        /// </summary>
        /// <summary>
        /// 联网开发者验证的**复查**：只在联机已经生效（解锁且开着）的时候做——启动时不会对所有人联网验证（隐私）。
        /// 联机在用的话，缓存超过 1 天就再问一次账号服务，被取消资格能及时失效；结果变了就刷新联机开关。
        /// 第一次解锁走「设置→关于连点版本号 7 次」的主动触发。
        /// </summary>
        private async Task StartupDevVerdictAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                if (!OnlineActive) return;                       // 没在用联机：什么都不发
                var who = await App.Backend.MojangWhoAsync(forVerdict: true);
                if (!who.Ok || string.IsNullOrWhiteSpace(who.Uuid)) return;
                if (DevVerdict.IsFresh(who.Uuid.Replace("-", "").ToLowerInvariant())) return;
                var before = DevVerdict.ReadCached()?.Developer;
                var now = await DevVerdict.RefreshAsync();
                if (now is not null && now != before) await ApplyOnlineSettingAsync();
            }
            catch { /* 没网 / 没账号：静默，沿用上次的结果（7 天内有效） */ }
        }

        /// <summary>联机是否处于开启状态（开发者认证 → 开发者测试 → 联机，三层都开）；设置页据此决定要不要显示联机组件。</summary>
        public bool OnlineActive { get; private set; }

        /// <summary>
        /// 启动 10 秒后后台检查一次更新（尊重「启动时检查更新」开关；Review 开发版不检查）。
        /// 有新版本：通知一次（同一个新版本只通知一次）；Release 版本高于云端最新发行版：提示一次。
        /// </summary>
        private async Task StartupUpdateCheckAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                var cur = BuildInfo.Current;
                if (cur.IsReview || !_preferences.CheckUpdatesOnStartup) return;
                var r = await UpdateService.CheckAsync(_preferences.UpdateChannel);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (r.State == UpdateState.Available && r.Latest is { } l
                        && !string.Equals(_preferences.UpdateNoticeShown, l.Version, StringComparison.Ordinal))
                    {
                        _preferences.UpdateNoticeShown = l.Version;
                        AppPreferencesStore.Save(_preferences);
                        TaskCenter.PostMessage($"有新版本 {l.Version}", "在 设置 → 更新 里查看更新内容并下载。", "更新");
                    }
                    else if (r.State == UpdateState.Ahead && cur.Channel == MeowChannel.Release
                        && !string.Equals(_preferences.AheadNoticeShown, cur.Display, StringComparison.Ordinal))
                    {
                        _preferences.AheadNoticeShown = cur.Display;
                        AppPreferencesStore.Save(_preferences);
                        TaskCenter.PostMessage("版本提示", r.Message, "提示");
                    }
                });
            }
            catch { /* 没网等：静默，设置页里手动检查能看到原因 */ }
        }

        private void ShowVersionNoticeOnce()
        {
            var cur = BuildInfo.Current;
            var key = cur.NoticeKey;
            if (key.Length == 0 || string.Equals(_preferences.VersionNoticeShown, key, StringComparison.Ordinal)) return;
            _preferences.VersionNoticeShown = key;
            AppPreferencesStore.Save(_preferences);
            DispatcherQueue.TryEnqueue(() =>
                TaskCenter.PostMessage(cur.IsReview ? "当前是开发版本" : "您正在使用预发行版本", cur.NoticeText, "提示"));
        }

        private void StartPresence()
        {
            if (!_presenceHooked)
            {
                _presenceTimer.Tick += async (_, _) => await PresenceTickAsync();
                _presenceHooked = true;
            }
            _ = ApplyOnlineSettingAsync();
        }

        /// <summary>读取联机开关并让心跳跟上：开了就启动并立刻报一次，关了就停掉。设置页切换开关后会调用它。</summary>
        public async Task ApplyOnlineSettingAsync()
        {
            DevSettings settings;
            try { settings = await App.Backend.GetDevSettingsAsync(); }
            catch { return; }

            // 侧栏的「联机」入口只在联机开着时出现；关掉时如果正停在房间页，退回主页
            OnlineActive = settings.Ok && settings.OnlineActive;
            NavRoom.Visibility = settings.Ok && settings.OnlineActive ? Visibility.Visible : Visibility.Collapsed;
            if (NavRoom.Visibility == Visibility.Collapsed && CurrentSection == "room") Navigate("home", recordHistory: false, clearHistory: true);

            if (settings.Ok && settings.OnlineActive)
            {
                if (!_presenceTimer.IsEnabled) _presenceTimer.Start();
                await PresenceTickAsync();
                // 联机组件装过就加载并启动；没装过就不启动（首次进「联机」页时下载）
                if (OnlineComponent.Ready || (OnlineComponent.ReadInstalled() is not null && OnlineComponent.TryLoad()))
                {
                    App.Online.Start();      // 账号握手 + 邀请推送通道
                }
            }
            else
            {
                _presenceTimer.Stop();
                App.Online.Stop();
            }
        }

        private async Task PresenceTickAsync()
        {
            if (_presenceBusy) return;
            _presenceBusy = true;
            try
            {
                var report = await App.Backend.ReportPresenceAsync("ONLINE");
                if (report.Skipped == "online-disabled") _presenceTimer.Stop();
            }
            catch { /* 没登录正版账号、网络不通等：静默，下一轮再试 */ }
            finally { _presenceBusy = false; }
        }

        private void StopPresence()
        {
            var wasRunning = _presenceTimer.IsEnabled;
            _presenceTimer.Stop();
            if (!wasRunning) return;   // 联机没开 / 没上报过：退出时不需要报离线，也就不会碰网络
            try
            {
                // 退出时最多等 8 秒把状态改回离线；超时就算了（服务端 10 分钟后会自己判离线）
                Task.Run(() => App.Backend.ReportPresenceAsync("OFFLINE", TimeSpan.FromSeconds(7))).Wait(TimeSpan.FromSeconds(8));
            }
            catch { }
        }

        /// <summary>上次见到的 DPI 缩放；用来只在缩放变化时重算自适应尺寸。</summary>
        private double _lastRasterScale = 1.0;
        private bool _sidebarPinned;
        private bool _sidebarExpanded;

        /// <summary>当前是否窄窗口档（设计宽 &lt; 1000）。页面用它切单行行卡/2 列组件板。</summary>
        public bool IsCompact => _compact;

        /// <summary>窄/宽档切换时触发，页面据此重排。</summary>
        public event EventHandler? CompactChanged;

    private readonly string[] _startupPackPaths;

    public MainWindow(string[] startupPackPaths)
    {
        _startupPackPaths = startupPackPaths;
        // 启动就把 GitHub 加速状态交给 UrlAccel，组件包/更新下载才能立刻用上。
        UrlAccel.Enabled = _preferences.GithubAccel;
        Views.SettingsPage.ApplyDownloadParallel(_preferences.DownloadParallel);
        UrlAccel.Host = string.IsNullOrWhiteSpace(_preferences.GithubAccelHost) ? UrlAccel.DefaultHost : _preferences.GithubAccelHost;

        NetworkProxy.Apply(_preferences);
        InitializeComponent();
        InstallScaleHost();
        AppVersionText.Text = BuildInfo.Current.Short;       // Review 01113-7829 / 1.0.0-01113-7829 / 1.0.0
        SyncThemeColor();
        // 窄档展开时侧栏要浮在内容上（列宽固定 64、不挤页面）：把它移到视觉树末尾，
        // 只依赖 Canvas.ZIndex 在部分面板实现里不够稳。
        // 侧栏始终自己限定宽度，不跨内容列；窄档展开时靠超出列宽覆盖内容。
        SidebarRoot.Width = SidebarFullWidth;
        SidebarRoot.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.Children.Remove(SidebarRoot);
        RootGrid.Children.Add(SidebarRoot);
        ContentHost.SizeChanged += ContentHost_SizeChanged;
        SearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(SearchBox_KeyDown), true);
        // AutoSuggestBox 内部的 TextBox 会把 Enter 吃掉，冒泡阶段收不到；预览阶段先拦一次。
        SearchBox.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(SearchBox_PreviewKeyDown), true);
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        UpdateSearchScopeText();

        // 侧栏任务入口：底部一行与悬浮面板都跟任务中心实时对齐，不需要重拉快照。
        TaskCenter.Changed += (_, _) => UpdateTaskDock();
        TaskCenter.NotificationsChanged += (_, _) => UpdateTaskDock();
        _lastNotificationCount = TaskCenter.Notifications.Count;
        TaskCenter.NotificationsChanged += (_, _) => MaybeFlashOnTaskDone();
        TaskCenter.TaskFinished += _ => MaybeFlashOnTaskDone(taskFinished: true);
        UpdateTaskDock();

        // 四组导航（玩 / 获取 / 实例 / 我的）。「下载版本 + 在线资源」合并进「获取」，
        // 「Mod 管理 + 实例资源」合并进「实例」；旧的 download/online/mods/assets
        // 标签仍由 Navigate 接受并路由到对应宿主页的指定子页。
        _navButtons = new Dictionary<string, ToggleButton>
        {
            ["home"] = NavHome,
            ["get"] = NavGet,
            ["versions"] = NavVersions,
            ["instance"] = NavInstance,
            ["account"] = NavAccount,
            ["room"] = NavRoom,
            ["settings"] = NavSettings,
        };

        // 悬停预览 + 按压反馈。VSM 在本运行时不可靠（见 AppTheme.xaml NavToggleStyle 注释），
        // 全部用指针事件驱动；退出悬停时按当前选中态还原。
        foreach (var (_, button) in _navButtons)
        {
            button.PointerEntered += (_, _) =>
            {
                if (button.IsChecked != true)
                {
                    button.Background = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
                }
            };
            button.PointerExited += (_, _) =>
            {
                if (button.IsChecked != true)
                {
                    button.Background = TransparentBrush;
                }
            };
            button.PointerPressed += (_, _) => button.Opacity = 0.78;
            button.PointerReleased += (_, _) => button.Opacity = 1.0;
            button.PointerCaptureLost += (_, _) => button.Opacity = 1.0;

            // 焦点环由模板的 FocusRing 画，这里只负责切 FocusStates；鼠标点进来的焦点不画环，
            // 与 Fluent 一致（只有键盘 Tab/F6 才显示）。
            button.GotFocus += (_, _) => VisualStateManager.GoToState(
                button, button.FocusState == FocusState.Keyboard ? "Focused" : "Unfocused", false);
            button.LostFocus += (_, _) => VisualStateManager.GoToState(button, "Unfocused", false);
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarHeight();   // 最小化/最大化/关闭撑满我们的 48 高标题栏
        TaskIsland.LayoutUpdated += (_, _) => UpdateIslandPassthrough();
        Title = "MeowLauncher";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowHandle = hwnd;
        var scale = SizeScale();
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "AppIcon.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { }
        UpdateTitleBarIcon(scale);
        var area = DisplayArea.Primary.WorkArea;
        var (adaptiveWidth, adaptiveHeight) = ComputeAdaptiveDesignSize(scale, area);
        var designWidth = _preferences.AutoWindowSize
            ? adaptiveWidth
            : _preferences.WindowWidth >= MinDesignWidth ? _preferences.WindowWidth : DefaultDesignWidth;
        var designHeight = _preferences.AutoWindowSize
            ? adaptiveHeight
            : _preferences.WindowHeight >= MinDesignHeight ? _preferences.WindowHeight : DefaultDesignHeight;
        _lastRasterScale = GetDpiForWindow(hwnd) / 96.0;
        // 设计尺寸指的是**客户区**（布局用的 DIP 宽高）；外框 = 客户区 + 系统边框，边框大小要实测，
        // 不能拿「设计 × DPI」直接当外框（以前这样做，150% 缩放下客户区只有 1111 DIP，低于 1120 的下限）。
        var width = (int)Math.Round(designWidth * scale);
        var height = (int)Math.Round(designHeight * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X, area.Y, width, height));      // 先落到目标显示器，边框才按它的 DPI 算
        var (frameW, frameH) = ClientFrameDelta();
        width = Math.Min(width, area.Width - frameW);                               // 屏幕放不下就压到可用区，保持客户区口径
        height = Math.Min(height, area.Height - frameH);
        AppWindow.Resize(new SizeInt32(width + frameW, height + frameH));      // 外框 = 客户区 + 实测边框（ResizeClient 在自绘标题栏下高度口径不同，不用）
        var x = area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2);
        var y = area.Y + Math.Max(0, (area.Height - AppWindow.Size.Height) / 2);
        AppWindow.Move(new PointInt32(x, y));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // PreferredMinimum* 是外框尺寸：客户区下限 + 边框
            presenter.PreferredMinimumWidth = (int)Math.Round(MinDesignWidth * scale) + frameW;
            presenter.PreferredMinimumHeight = (int)Math.Round(MinDesignHeight * scale) + frameH;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenterRef)
        {
            _presenter = presenterRef;
        }

        Closed += (_, _) =>
        {
            _onboardingLogin?.Cancel();
            RememberWindowSize();
            StopPresence();
        };

        RootGrid.SizeChanged += (_, _) =>
        {
            Wallpaper.ContentLeft = SidebarRoot.ActualWidth;
            ApplyCompactMode();
        };
        RootGrid.Loaded += (_, _) =>
        {
            if (RootGrid.XamlRoot is { } root) root.Changed += XamlRoot_Changed;
        };
        SidebarRoot.PointerEntered += (_, _) => { if (_compact && !_sidebarExpanded) SetSidebarExpanded(true); };
        SidebarRoot.PointerExited += (_, _) => { if (_compact && !_sidebarPinned) SetSidebarExpanded(false); };
        // 兜底：指针移到内容区就收起。PointerExited 在侧栏突然变宽/布局重排时偶发丢事件，
        // 只靠它会让侧栏一直停在展开态（880 档肉眼可见：内容被挤窄）。
        _hoverWatch.Interval = TimeSpan.FromMilliseconds(160);
        _hoverWatch.Tick += (_, _) => WatchHoverState();
        ApplyCompactMode();


        // 高对比度时把 RequestedTheme 交回系统，让 ThemeDictionaries 的 HighContrast 变体生效。
        try { _highContrast = _access.HighContrast; } catch { _highContrast = false; }
        _themeMode = NormalizeThemeMode(_preferences.ThemeMode);
        if (!SystemTheme.IsSupported) _themeMode = "System";
        _dark = string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase)
            ? SystemTheme.IsDark()
            : string.Equals(_themeMode, "Dark", StringComparison.OrdinalIgnoreCase);
        ApplyResolvedTheme();
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
        // 订阅可能抛 COMException 0x80070490（部分系统/用户配置下没有高对比度服务），失败就不跟随，别让它带走启动。
        try { _access.HighContrastChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { ApplyResolvedTheme(); RefreshAppearance(false); }); } catch { }

        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.SizeChanged += (_, _) => UpdateSearchLayout();
        ContentFrame.Navigated += ContentFrame_Navigated;
        RootGrid.PointerMoved += OnWallpaperPointerMoved;
        RootGrid.PointerExited += OnWallpaperPointerExited;
        Activated += OnWindowActivated;
        SizeChanged += (_, _) => MaybeClearMinimizedForGame();
        if (_preferences.Onboarded)
            Navigate("home", recordHistory: false, clearHistory: true);
        else
        {
            // Decide before activation: never reveal the shell underneath first-run setup.
            OnboardingHost.Visibility = Visibility.Visible;
            StartupMask.Visibility = Visibility.Collapsed;
            _startupMaskGone = true;
            SearchBox.Visibility = Visibility.Collapsed;
            TaskIsland.Visibility = Visibility.Collapsed;
            SidebarRoot.Visibility = Visibility.Collapsed;
            SetOnboardingChrome(true);
        }
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1200);
            
        });

    }

    private bool _startupMaskGone;

    /// <summary>冷启动遮罩淡出并移除（只执行一次）。</summary>
    private async void HideStartupMask()
    {
        if (_startupMaskGone) return;
        _startupMaskGone = true;
        StartupBar.Width = StartupMask.ActualWidth;      // 补满
        await Task.Delay(250);
        StartupMask.Opacity = 0;
        await Task.Delay(400);
        StartupMask.Visibility = Visibility.Collapsed;
        await ImportStartupPacksAsync();
    }

    private bool _startupPacksHandled;
    private async Task ImportStartupPacksAsync()
    {
        if (_startupPacksHandled || !_preferences.Onboarded) return;
        _startupPacksHandled = true;
        if (_startupPackPaths.Length > 0 && App.Backend.Python.Found)
        {
            Navigate("download");
            if (ContentFrame.Content is GetPage getPage)
                await getPage.ImportPacksAsync(_startupPackPaths);
        }
    }

    /// <summary>全新安装才走一遍新手引导；走完（或本来就有标记）不再出现。
    /// 启动前判断完成状态，在主窗口内承载 Assets/Onboarding/oobe.html。</summary>
    private bool _onboardingOpening;

    public void OpenOnboarding() => _ = ShowOnboardingIfNeededAsync(force: true);

    private async Task ShowOnboardingIfNeededAsync(bool force = false)
    {
        if (_onboardingOpening || _onboardingFinishing) return;
        if (_onboardingWeb is not null)
        {
            OnboardingHost.Visibility = Visibility.Visible;
            return;
        }
        if (_preferences.Onboarded && !force) return;
        _onboardingOpening = true;
        try
        {
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Onboarding");
            if (!System.IO.File.Exists(System.IO.Path.Combine(folder, "oobe.html")))
            {
                App.Trace("Onboarding: oobe.html missing");
                CloseOnboardingWindow();
                return;
            }

            var web = new Microsoft.UI.Xaml.Controls.WebView2 { Opacity = 0 };
            _onboardingWeb = web;
            OnboardingLoading.Visibility = Visibility.Visible;
            OnboardingHost.Children.Insert(0, web);
            OnboardingHost.Visibility = Visibility.Visible;
            SearchBox.Visibility = Visibility.Collapsed;
            TaskIsland.Visibility = Visibility.Collapsed;
            SidebarRoot.Visibility = Visibility.Collapsed;
            SetOnboardingChrome(true);
            await web.EnsureCoreWebView2Async();
            var core = web.CoreWebView2;
            // EnsureCoreWebView2Async 返回后 CoreWebView2 仍可能短暂为 null（WebView2 已知行为），
            // 直接取值会在下一行抛 NullReferenceException，用户看到的是「点了没反应」。
            for (var attempt = 0; core is null && attempt < 50; attempt++)
            {
                await Task.Delay(100);
                core = web.CoreWebView2;
            }
            if (core is null)
            {
                App.Trace("Onboarding: CoreWebView2 未就绪（WebView2 运行时或用户数据目录不可用）");
                CloseOnboardingWindow();
                ShowOnboardingFailure(new InvalidOperationException("WebView2 初始化未完成"));
                return;
            }
            core.SetVirtualHostNameToFolderMapping("onboarding.meow", folder,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("skinviewer.meow",
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "skinviewer"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived -= Onboarding_WebMessage;
            core.WebMessageReceived += Onboarding_WebMessage;
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('error',function(e){window.__meowPageError=String(e.message)+' @'+e.lineno;});");
            core.NavigationCompleted += async (_, navigation) =>
            {
                try
                {
                    var state = await core.ExecuteScriptAsync("JSON.stringify({w:innerWidth,h:innerHeight,error:window.__meowPageError||'',card:(function(){var c=document.querySelector('.split');if(!c)return null;var r=c.getBoundingClientRect();return {width:r.width,height:r.height,top:r.top,display:getComputedStyle(c).display};})(),stage:document.getElementById('stage')?.childElementCount})");
                    App.Trace("Onboarding document: " + state);
                    await Task.Delay(200);
                    if (ReferenceEquals(_onboardingWeb, web))
                    {
                        web.Opacity = 1;
                        OnboardingLoading.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex) { App.Trace("Onboarding document check: " + ex.GetType().Name); }
            };
            _onboardingDirectories = await GameDirectoryDiscovery.DiscoverAsync(_preferences);
            // 网页只负责画，数据全部由宿主给：主题 / 主题色 / 目录 / 账户 / 更新开关
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildOnboardingBootstrap() + "\n" + OnboardingBridgeScript);
            web.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            core.Navigate("https://onboarding.meow/oobe.html");
            App.Trace("Onboarding: shown");
        }
        catch (Exception ex)
        {
            CloseOnboardingWindow();
            App.Trace("ShowOnboarding: " + ex);
            ShowOnboardingFailure(ex);
        }
        finally { _onboardingOpening = false; }
    }

    private void CloseOnboardingWindow()
    {
        var web = _onboardingWeb;
        _onboardingWeb = null;
        _onboardingLogin?.Cancel();
        if (web?.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= Onboarding_WebMessage;
            core.Stop();
        }
        if (web is not null) OnboardingHost.Children.Remove(web);
        OnboardingHost.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        TaskIsland.Visibility = Visibility.Visible;
        SidebarRoot.Visibility = Visibility.Visible;
        SetOnboardingChrome(false);
        UpdateSearchLayout();
    }

    /// <summary>引导打不开时给一句看得见的话：安装到 Program Files 的机器最常见原因是 WebView2 运行时缺失或用户数据目录不可写。</summary>
    private void ShowOnboardingFailure(Exception ex)
    {
        try
        {
            if (CrashToast is null) return;
            var code = ex is System.Runtime.InteropServices.COMException com && com.HResult != 0
                ? $"（0x{com.HResult:X8}）"
                : "";
            CrashToastTitle.Text = "新手引导没能打开";
            CrashToastBody.Text = $"WebView2 初始化失败{code}。请安装 Microsoft Edge WebView2 运行时后，在设置里点「开始新手引导」重试。";
            CrashToast.Visibility = Visibility.Visible;
            _crashToastTimer ??= DispatcherQueue.CreateTimer();
            _crashToastTimer.Stop();
            _crashToastTimer.Interval = TimeSpan.FromSeconds(12);
            _crashToastTimer.IsRepeating = false;
            _crashToastTimer.Tick -= CrashToastTimer_Tick;
            _crashToastTimer.Tick += CrashToastTimer_Tick;
            _crashToastTimer.Start();
        }
        catch { }
    }

    private void SetOnboardingChrome(bool onboarding)
    {
        SearchHost.Visibility = onboarding ? Visibility.Collapsed : Visibility.Visible;
        MenuToggleButton.Visibility = onboarding ? Visibility.Collapsed : Visibility.Visible;
        AppVersionText.Visibility = onboarding ? Visibility.Collapsed : Visibility.Visible;
        UpdateBackButton(onboarding);
        TitleBrandHost.Margin = new Thickness(onboarding ? 14 : 52, 0, 0, 0);
        TitleBarRoot.Background = onboarding
            ? new SolidColorBrush(_dark ? Color.FromArgb(255, 12, 18, 32) : Color.FromArgb(255, 248, 251, 255))
            : TransparentBrush;
        if (onboarding) OnboardingHost.Background = TitleBarRoot.Background;
    }

    /// <summary>宿主桥：只认底栏按钮的点击，网页本身保持原样，一个像素都不动。</summary>
    private const string OnboardingBridgeScript = """
(function () {
  document.addEventListener('click', function (e) {
    var t = e.target;
    var next = t && t.closest ? t.closest('#next') : null;
    if (!next) return;
    var label = (next.textContent || '').trim();
    if (label === '进入启动器') {
      e.preventDefault();
      e.stopPropagation();
      try { window.chrome.webview.postMessage({ type: 'finish' }); } catch (err) { }
    }
  }, true);
})();
""";

    /// <summary>把启动器的真实状态喂给引导网页（window.__OOBE__）：网页照设计稿画，不改一个像素。</summary>
    private List<string>? _onboardingDirectories;
    private string BuildOnboardingBootstrap()
    {
        var own = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", ".minecraft");
        var found = new List<(string Path, int Versions, DateTime Stamp)>();
        try
        {
            foreach (var dir in _onboardingDirectories ?? GameDirectoryResolver.Resolve(_preferences))
            {
                var norm = GameDirectoryResolver.Normalize(dir);
                if (norm.Length == 0) continue;
                if (string.Equals(norm, own, StringComparison.OrdinalIgnoreCase)) continue;
                var versions = System.IO.Path.Combine(norm, "versions");
                var has = System.IO.Directory.Exists(versions);
                found.Add((norm, has ? CountVersionFolders(versions) : 0, has ? SafeWriteTime(versions) : DateTime.MinValue));
            }
        }
        catch (Exception ex) { App.Trace("Onboarding dirs: " + ex.GetType().Name); }

        // 只给最近动过版本的那个目录挂「最近使用」，扫到多个目录时标签不重复
        var recentPath = "";
        var newest = DateTime.MinValue;
        foreach (var d in found)
        {
            if (d.Versions <= 0) continue;
            if (d.Stamp <= newest) continue;
            newest = d.Stamp;
            recentPath = d.Path;
        }

        var dirs = new List<object>();
        foreach (var d in found)
        {
            dirs.Add(new
            {
                path = d.Path,
                versions = d.Versions,
                recent = d.Versions > 0 && string.Equals(d.Path, recentPath, StringComparison.OrdinalIgnoreCase),
            });
        }

        var theme = NormalizeThemeMode(_preferences.ThemeMode).ToLowerInvariant();   // system / dark / light
        var accountName = AccountName?.Text ?? "";
        if (accountName is "未登录" || accountName.StartsWith("正在读取", StringComparison.Ordinal)) accountName = "";
        var payload = new
        {
            theme,
            accentHex = ThemeColorService.CurrentHex,
            defaultDir = own,
            selectedDir = _preferences.McDir,
            dirs,
            account = new { name = Snapshot?.Account?.Name ?? accountName,
                kind = Snapshot?.Account?.Type ?? _preferences.OnboardAccountKind },
            updates = new
            {
                check = _preferences.CheckUpdatesOnStartup,
                download = _preferences.AutoDownloadUpdate,
                install = _preferences.AutoInstallUpdate,
            },
        };
        return "window.__OOBE__ = " + System.Text.Json.JsonSerializer.Serialize(payload) + ";";
    }

    private static int CountVersionFolders(string versionsDir)
    {
        try { return System.IO.Directory.GetDirectories(versionsDir).Length; }
        catch { return 0; }
    }

    private static DateTime SafeWriteTime(string dir)
    {
        try { return System.IO.Directory.GetLastWriteTime(dir); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>网页里的选择直接落到真实偏好：主题 / 主题色 / 默认目录 / 更新开关 / 完成。</summary>
    private readonly System.Threading.SemaphoreSlim _onboardingMessages = new(1, 1);
    private System.Threading.CancellationTokenSource? _onboardingLogin;
    private Task? _onboardingLoginTask;
    private Uri? _onboardingVerificationUri;
    private Microsoft.UI.Xaml.Controls.WebView2? _onboardingWeb;

    private async Task PushOnboardingAsync(object message)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var literal = System.Text.Json.JsonSerializer.Serialize(json);
        var core = _onboardingWeb?.CoreWebView2;
        if (core is null) return;
        await core.ExecuteScriptAsync(
            "window.__OOBE_PUSH__ && window.__OOBE_PUSH__(JSON.parse(" + literal + ")); ");
    }

    private static async Task<string> OnboardingImageAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return "";
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return "";
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (System.IO.IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private async Task LoginOnboardingAsync(string kind, string name, bool slim)
    {
        using var cancellation = new System.Threading.CancellationTokenSource();
        _onboardingLogin = cancellation;
        var token = cancellation.Token;
        try
        {
            await PushOnboardingAsync(new { type = "account", state = "working" });
            if (kind == "offline")
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("请输入玩家名");
                var result = await App.Backend.AddOfflineAccountAsync(name, slim);
                if (!result.Ok) throw new InvalidOperationException(result.Message);
            }
            else
            {
                var start = await App.Backend.StartMicrosoftAsync();
                token.ThrowIfCancellationRequested();
                if (!start.Ok) throw new InvalidOperationException(start.Message);
                if (!Uri.TryCreate(start.VerificationUri, UriKind.Absolute, out var url) || url.Scheme != "https")
                    throw new InvalidOperationException("Microsoft 授权地址无效。");
                _onboardingVerificationUri = url;
                var copied = false;
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(start.UserCode);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    copied = true;
                }
                catch (Exception ex) { App.Trace("Onboarding clipboard unavailable: " + ex.GetType().Name); }
                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, start.ExpiresInSeconds));
                await PushOnboardingAsync(new { type = "account", state = "code", code = start.UserCode,
                    url = url.AbsoluteUri, copied, expiresInSeconds = start.ExpiresInSeconds, expiresAt = deadline });
                await Windows.System.Launcher.LaunchUriAsync(url);
                var exchangeFinished = false;
                var exchangeProgress = new Progress<string>(async phase =>
                {
                    if (phase != "authorized" || token.IsCancellationRequested || exchangeFinished) return;
                    try { await PushOnboardingAsync(new { type = "account", state = "working" }); }
                    catch (Exception ex) { App.Trace("Onboarding exchange push: " + ex.GetType().Name); }
                });
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, start.IntervalSeconds)), token);
                    if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("验证码已过期，请重新获取。");
                    var poll = await App.Backend.PollMicrosoftAsync(start.DeviceCode, exchangeProgress);
                    token.ThrowIfCancellationRequested();
                    if (poll.Pending) continue;
                    exchangeFinished = true;
                    if (!poll.Ok) throw new InvalidOperationException(poll.Message);
                    name = poll.Name;
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            var snapshot = await App.Backend.GetSnapshotAsync(force: true);
            var account = snapshot?.Account;
            if (account is null || account.Type != kind || account.Name != name)
                throw new InvalidOperationException("账户信息读取失败，请重新登录。");
            _preferences.OnboardAccountKind = account.Type;
            AppPreferencesStore.Save(_preferences);
            var profile = new
            {
                name = account.Name, kind = account.Type, typeDisplay = account.TypeDisplay,
                uuid = Guid.TryParse(account.Uuid, out var uuid) ? uuid.ToString("D") : account.Uuid,
                variant = account.Variant,
                head = await OnboardingHeadAsync(account),
                skin = await OnboardingImageAsync(account.SkinPath),
                cape = await OnboardingImageAsync(account.CapePath),
            };
            token.ThrowIfCancellationRequested();
            await PushOnboardingAsync(new { type = "account", state = "ok", name = account.Name, profile });
            await RefreshShellAsync(force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                try { await PushOnboardingAsync(new { type = "account", state = "error", message = ex.Message }); }
                catch (Exception pushError) { App.Trace("Onboarding account push: " + pushError.GetType().Name); }
            }
        }
        finally
        {
            _onboardingVerificationUri = null;
            _onboardingLogin = null;
        }
    }

    private void Onboarding_WebMessage(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var origin) || origin.Host != "onboarding.meow") return;
        var raw = e.WebMessageAsJson;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await _onboardingMessages.WaitAsync();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "account-login":
                    {
                        if (_onboardingLoginTask is { IsCompleted: false } || _onboardingFinishing) break;
                        var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : "";
                        if (kind is not ("microsoft" or "offline")) break;
                        var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                        var slim = root.TryGetProperty("slim", out var slimValue) && slimValue.ValueKind == System.Text.Json.JsonValueKind.True;
                        _onboardingLoginTask = LoginOnboardingAsync(kind, name.Trim(), slim);
                        break;
                    }
                    case "account-open":
                        if (_onboardingVerificationUri is { } verification) await Windows.System.Launcher.LaunchUriAsync(verification);
                        break;
                    case "account-copy":
                    {
                        var code = root.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? "" : "";
                        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        package.SetText(code);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                        await PushOnboardingAsync(new { type = "account-copy", ok = true });
                        break;
                    }
                    case "account-cancel":
                        _onboardingLogin?.Cancel();
                        if (_onboardingLoginTask is { } login) await login;
                        break;
                    case "theme":
                    {
                        var mode = (root.TryGetProperty("value", out var v) ? v.GetString() : "") switch
                        {
                            "dark" => "Dark",
                            "light" => "Light",
                            "system" => "System",
                            _ => null,
                        };
                        if (mode is null) return;
                        SetThemeMode(mode, persist: true);      // 里面会 Save
                        SetOnboardingChrome(true);
                        App.Trace("Onboarding: theme " + mode);
                        break;
                    }
                    case "accent":
                    {
                        var hex = (root.TryGetProperty("hex", out var h) ? h.GetString() : "") ?? "";
                        if (hex.Length < 7 || hex[0] != '#') return;
                        _preferences.ThemeColorMode = ThemeColorService.ModeCustom;
                        _preferences.ThemeColorHex = hex.ToUpperInvariant();
                        await ThemeColorService.ApplyAsync(_preferences, IsDarkTheme);
                        AppPreferencesStore.Save(_preferences);
                        App.Trace("Onboarding: accent " + hex);
                        break;
                    }
                    case "dir":
                    {
                        var path = (root.TryGetProperty("path", out var p) ? p.GetString() : "") ?? "";
                        await SetOnboardingDirectoryAsync(path);
                        break;
                    }
                    case "dir-browse":
                    {
                        var picker = new Windows.Storage.Pickers.FolderPicker();
                        picker.FileTypeFilter.Add("*");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker,
                            WindowHandle);
                        var folder = await picker.PickSingleFolderAsync();
                        if (folder is not null) await SetOnboardingDirectoryAsync(folder.Path);
                        break;
                    }
                    case "update":
                    {
                        var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
                        var on = root.TryGetProperty("value", out var val)
                                 && val.ValueKind == System.Text.Json.JsonValueKind.True;
                        switch (key)
                        {
                            case "check": _preferences.CheckUpdatesOnStartup = on; break;
                            case "download": _preferences.AutoDownloadUpdate = on; break;
                            case "install": _preferences.AutoInstallUpdate = on; break;
                            default: return;
                        }
                        AppPreferencesStore.Save(_preferences);
                        break;
                    }
                    case "skip":
                    case "finish":
                        await FinishOnboardingAsync();
                        break;
                }
            }
            catch (Exception ex) { App.Trace("Onboarding_WebMessage: " + ex.GetType().Name + " " + ex.Message); }
            finally { _onboardingMessages.Release(); }
        });
    }

    private bool _onboardingFinishing;

    private async Task SetOnboardingDirectoryAsync(string path)
    {
        try
        {
            var norm = GameDirectoryResolver.Normalize(path);
            if (norm.Length == 0) throw new System.IO.IOException("请选择有效的游戏目录。");
            System.IO.Directory.CreateDirectory(norm);
            if (!await App.Backend.SetGameDirAsync(norm))
                throw new System.IO.IOException("游戏目录未能保存，请检查目录权限后重试。");
            _preferences.McDirs = new[] { norm }.Concat(_preferences.McDirs)
                .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(GameDirectoryResolver.MaxDirs).ToList();
            _preferences.McDir = norm;
            AppPreferencesStore.Save(_preferences);
            await PushOnboardingAsync(new { type = "dir", path = norm, versions = CountVersionFolders(System.IO.Path.Combine(norm, "versions")) });
        }
        catch (Exception ex)
        {
            await PushOnboardingAsync(new { type = "dir", error = ex.Message });
        }
    }

    private static async Task<string> OnboardingHeadAsync(AccountInfo account)
    {
        var head = await OnboardingImageAsync(account.HeadPath);
        if (head.Length > 0) return head;
        // Embedded Python may not contain Pillow. Use Windows' PNG decoder as a
        // fallback so an offline account always has a head from its default skin.
        try
        {
            if (!System.IO.File.Exists(account.SkinPath)) return "";
            using var input = System.IO.File.OpenRead(account.SkinPath);
            using var source = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(input);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(source);
            var unit = decoder.PixelWidth / 64;
            if (unit == 0 || decoder.PixelHeight < 16 * unit) return "";
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = 8 * unit, Y = 8 * unit, Width = 8 * unit, Height = 8 * unit }
            };
            var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight, transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                8 * unit, 8 * unit, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
            output.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            var bytes = new byte[(int)output.Size]; reader.ReadBytes(bytes);
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) { App.Trace("Onboarding head: " + ex.GetType().Name); return ""; }
    }

    /// <summary>点「进入启动器」：收起引导层 → 主界面手绘线稿一笔一笔画出顶栏/侧栏/卡片框 → 整层淡出，露出真实界面。</summary>
    private async Task FinishOnboardingAsync()
    {
        if (_onboardingFinishing) return;
        _onboardingFinishing = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_preferences.McDir))
                await SetOnboardingDirectoryAsync(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", ".minecraft"));
            if (string.IsNullOrWhiteSpace(_preferences.McDir))
                throw new System.IO.IOException("请先选择有效的游戏目录。");
            _preferences.Onboarded = true;
            AppPreferencesStore.Save(_preferences);
            var saved = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(
                await System.IO.File.ReadAllTextAsync(AppPreferencesStore.FilePath));
            if (System.Text.Json.JsonSerializer.Serialize(saved) != System.Text.Json.JsonSerializer.Serialize(_preferences))
                throw new System.IO.IOException("引导设置尚未保存，请重试。");
            _onboardingLogin?.Cancel();

            // 线稿层先在主界面里铺好，再收起引导层，中间不留一帧空白
            OnboardingSketch.Visibility = Visibility.Visible;
            OnboardingSketch.Opacity = 1;
            Activate();                             // 主界面先顶到前面
            await Task.Delay(80);
            CloseOnboardingWindow();                // 原窗口内的引导层退场
            await InitializeShellAsync();
            await ImportStartupPacksAsync();
            if (ContentFrame.Content is SettingsPage settings) settings.RefreshPreferences();
            await Task.Delay(60);

            if (OnboardingSketch.Resources["SketchStoryboard"] is Microsoft.UI.Xaml.Media.Animation.Storyboard sketch)
            {
                sketch.Stop();                      // 复位所有线条，重复跑也能从头画
                sketch.Begin();
            }

            _ = RefreshShellAsync(force: true);     // 引导里改过主题/目录/账户，外壳在画线期间后台重拉
            await Task.Delay(1900);                 // 最后一条线收在 1.94s

            OnboardingSketch.Opacity = 0;           // 整层淡出，露出真实界面
            await Task.Delay(520);
            OnboardingSketch.Visibility = Visibility.Collapsed;
            OnboardingSketch.Opacity = 1;
            App.Trace("Onboarding: finished");
        }
        catch (Exception ex)
        {
            _preferences.Onboarded = false;
            App.Trace("FinishOnboarding: " + ex.GetType().Name + " " + ex.Message);
            ShowCrashToast();
        }
        finally { _onboardingFinishing = false; }
    }

    // ---- 界面缩放：DPI 只管清晰度，界面大小是我们自己的（偏好「忽略系统缩放」）----
    private Grid? _scaleHost;

    private double SystemScale() => WindowHandle == IntPtr.Zero ? 1.0 : GetDpiForWindow(WindowHandle) / 96.0;

    /// <summary>1 个设计像素 = 多少物理像素：忽略系统缩放时恒为 1，否则 = 系统 DPI 比例。</summary>
    public double SizeScale() => _preferences.IgnoreSystemDpi ? 1.0 : SystemScale();

    /// <summary>把 RootGrid 放进一层宿主：缩放时给 RootGrid 一个放大的布局尺寸再整体反向缩小。</summary>
    private void InstallScaleHost()
    {
        try
        {
            _scaleHost = new Grid();
            var root = RootGrid;
            Content = null;
            _scaleHost.Children.Add(root);
            Content = _scaleHost;
            _scaleHost.SizeChanged += (_, _) => ApplyContentScale();
        }
        catch (Exception ex) { App.Trace("InstallScaleHost: " + ex.GetType().Name); _scaleHost = null; }
    }

    /// <summary>内容缩放因子 f = 我们的像素比 / 系统 DPI。f≠1 时布局尺寸 = 窗口 / f，再整体缩 f 倍。</summary>
    public void ApplyContentScale()
    {
        if (_scaleHost is null) return;
        var f = SizeScale() / SystemScale();
        if (Math.Abs(f - 1.0) < 0.001)
        {
            RootGrid.Width = double.NaN; RootGrid.Height = double.NaN;
            RootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootGrid.VerticalAlignment = VerticalAlignment.Stretch;
            RootGrid.RenderTransform = null;
            return;
        }
        var w = _scaleHost.ActualWidth; var h = _scaleHost.ActualHeight;
        if (w <= 0 || h <= 0) return;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = VerticalAlignment.Top;
        RootGrid.Width = w / f; RootGrid.Height = h / f;
        RootGrid.RenderTransform = new ScaleTransform { ScaleX = f, ScaleY = f };
    }

    /// <summary>系统三键高度要和我们的 48 高标题栏对齐：Standard=32、Tall=48 个系统 DIP，挑更接近的一档。</summary>
    private void ApplyTitleBarHeight()
    {
        try
        {
            var dpi = SystemScale();
            var ours = 48.0 * SizeScale();
            var tall = Math.Abs(48.0 * dpi - ours) <= Math.Abs(32.0 * dpi - ours);
            AppWindow.TitleBar.PreferredHeightOption = tall
                ? Microsoft.UI.Windowing.TitleBarHeightOption.Tall
                : Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
        }
        catch { }
    }

    /// <summary>设置里切换「忽略系统缩放」后立即生效：内容缩放、标题栏、窗口大小、最小尺寸。</summary>
    public void ApplyDpiMode()
    {
        ApplyContentScale();
        ApplyTitleBarHeight();
        try
        {
            var scale = SizeScale();
            var (frameW, frameH) = ClientFrameDelta();
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.PreferredMinimumWidth = (int)Math.Round(MinDesignWidth * scale) + frameW;
                p.PreferredMinimumHeight = (int)Math.Round(MinDesignHeight * scale) + frameH;
            }
            if (_preferences.AutoWindowSize) ApplyAdaptiveWindowSize();
            else ApplyWindowSize(_preferences.WindowWidth >= MinDesignWidth ? _preferences.WindowWidth : DefaultDesignWidth,
                _preferences.WindowHeight >= MinDesignHeight ? _preferences.WindowHeight : DefaultDesignHeight, persist: false);
            ApplyContentScale();
            UpdateIslandPassthrough();
        }
        catch (Exception ex) { App.Trace("ApplyDpiMode: " + ex.GetType().Name); }
    }

    /// <summary>系统边框占的物理像素（外框 − 客户区）。</summary>
    private (int W, int H) ClientFrameDelta()
    {
        try
        {
            var o = AppWindow.Size; var c = AppWindow.ClientSize;
            return (Math.Max(0, o.Width - c.Width), Math.Max(0, o.Height - c.Height));
        }
        catch { return (0, 0); }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSearchLayout();
        RefreshAppearance(false);
#if DEBUG
        try
        {
            var dpi = GetDpiForWindow(WindowHandle);
            App.Trace($"[size] dpi={dpi} ({dpi / 96.0:0.##}x) outer={AppWindow.Size.Width}x{AppWindow.Size.Height}px client={AppWindow.ClientSize.Width}x{AppWindow.ClientSize.Height}px "
                + $"grid={RootGrid.ActualWidth:0.#}x{RootGrid.ActualHeight:0.#}dip xamlScale={RootGrid.XamlRoot?.RasterizationScale:0.##} lastRaster={_lastRasterScale:0.##}");
        }
        catch { }
#endif
        if (_shellLoaded || _onboardingOpening) return;
        if (!_preferences.Onboarded)
        {
            await ShowOnboardingIfNeededAsync();
            if (_onboardingWeb is not null) return;
        }
        await InitializeShellAsync();
    }

    private async Task InitializeShellAsync()
    {
        if (_shellLoaded) return;
        _shellLoaded = true;
        GameSessions.Initialize(DispatcherQueue);
        if (ContentFrame.Content is null)
            Navigate("home", recordHistory: false, clearHistory: true);
        StartPresence();
        ShowVersionNoticeOnce();
        _ = StartupUpdateCheckAsync();
        _ = StartupDevVerdictAsync();
        _ = ShowPythonMissingAsync();
        _ = Task.Run(async () =>
        {
            // 进度条 + 阶段文字：按时间慢慢往右填，停在 92%，等真正读完才补满
            for (var i = 1; i <= 23 && !_startupMaskGone; i++)
            {
                var n = i;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_startupMaskGone) return;
                    StartupBar.Width = StartupMask.ActualWidth * Math.Min(0.92, n * 0.04);
                    StartupText.Text = n < 4 ? "正在读取实例和账户…" : n < 10 ? "正在启动后台服务…" : "正在初始化MeowLauncher，初次加载可能需要更长时间......";
                });
                await Task.Delay(1000);
            }
        });
        _ = Task.Run(async () => { await Task.Delay(25000); DispatcherQueue.TryEnqueue(HideStartupMask); });   // 兜底：最多盖 25 秒
        // 启动时不要把焦点留在搜索框上：S 开头的空搜索框会一直描着强调色边。
        (ContentFrame.Content as Page)?.Focus(FocusState.Programmatic);
        // 快照可能比外壳晚到（冷启动直接进版本页/设置页时尤其明显）：
        // 订阅后端事件 + 有限次补拉，账户块不再等 HomePage 先跑一遍。
        App.Backend.SnapshotArrived -= Backend_SnapshotArrived;
        App.Backend.SnapshotArrived += Backend_SnapshotArrived;
        await RefreshShellAsync();
        _ = SettleShellAccountAsync();
    }

    /// <summary>启动时没找到可用 Python：弹一次说明，列出检查过的位置，而不是让后端功能静默失败。</summary>
    private async Task ShowPythonMissingAsync()
    {
        if (App.Backend.Python.Found) return;
        try
        {
            await Task.Delay(500);
            var dialog = new ContentDialog
            {
                Title = "未找到 Python",
                Content = new ScrollViewer
                {
                    MaxHeight = 340,
                    Content = new TextBlock { Text = App.Backend.Python.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
                CloseButtonText = "知道了",
                XamlRoot = RootGrid.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch { }
    }
    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var wallpaperPage = e.SourcePageType == typeof(HomePage) ? WallpaperPage.Home
            : e.SourcePageType == typeof(SettingsPage) ? WallpaperPage.Settings
            : WallpaperPage.List;
        Wallpaper.SetPage(wallpaperPage);
        // 非主页时猫和太阳没有可信锚点，直接清掉，避免停在上一页的位置。
        if (wallpaperPage != WallpaperPage.Home) Wallpaper.ClearSafeArea();

        if (Math.Abs(_preferences.FontScale - 1.0) < 0.001) return;
        // 页面刚 Navigate 时还没有真实模板；延迟一拍再套字号，避免空树。
        DispatcherQueue.TryEnqueue(() => UiScale.Apply(ContentFrame, _preferences.FontScale));
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(120);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UiScale.Apply(ContentFrame, _preferences.FontScale);
        timer.Start();
    }


    private void UpdateTitleBarIcon(double scale)
    {
        var size = scale >= 1.75 ? 64 : scale >= 1.4 ? 48 : scale >= 1.15 ? 40 : 32;
        try
        {
            TitleBarIcon.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Icons/png/AppIcon_{size}.png"));
            // 按设备像素选整数图，显示尺寸也换算，保证 1:1 不被系统再缩一次。
            var dip = size / Math.Max(1, scale);
            TitleBarIcon.Width = dip;
            TitleBarIcon.Height = dip;
        }
        catch { }
    }

    private void OnWallpaperPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Wallpaper.Visibility != Visibility.Visible) return;
        var p = e.GetCurrentPoint(RootGrid).Position;
        Wallpaper.OnPointer(new Point(p.X, p.Y), new Size(RootGrid.ActualWidth, RootGrid.ActualHeight));
    }

    private void OnWallpaperPointerExited(object sender, PointerRoutedEventArgs e) => Wallpaper.OnPointerLeave();

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) =>
        Wallpaper.SetActive(args.WindowActivationState != WindowActivationState.Deactivated);

    public async Task RefreshShellAsync(bool force = false)
    {
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync(force);
            if (snapshot is not null) ApplyShellSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shell refresh failed: {ex}");
        }
    }

    private void Backend_SnapshotArrived(Snapshot snapshot)
    {
        // 快照可能由任意页面触发（首页、版本页、设置页……）。外壳自己订阅，
        // 这样侧栏账户块不再依赖 HomePage 是否被打开过。
        DispatcherQueue.TryEnqueue(() => ApplyShellSnapshot(snapshot));
    }

    private void ApplyShellSnapshot(Snapshot snapshot)
    {
        var account = snapshot.Account;
        var settled = account is not null || snapshot.Versions.Count > 0 || snapshot.Current is not null;
        Snapshot = snapshot;

        // 后端偶发返回「没有账户」的空快照时，不能把已经显示出来的账户盖回「未登录」。
        if (account is null && _shellAccountSettled)
        {
            UpdateTaskDock();
            return;
        }

        if (settled) _shellAccountSettled = true;
        HideStartupMask();
        AccountName.Text = string.IsNullOrWhiteSpace(account?.Name) ? "未登录" : account.Name;
        AccountType.Text = account?.TypeDisplay ?? "还没有账户";
        // 与账户页同一条头像链：皮肤大头裁切 → 首字母兜底，不用皮肤展开图。
        Views.AccountPage.FillAvatarShared(AccountAvatar, snapshot.Account, 44);

        // 实例就绪与否不再是侧栏入口的职责：那一行只报进行中任务 / 未读通知。
        UpdateTaskDock();
    }

    /// <summary>
    /// 冷启动时后端第一个快照可能还没就绪（或临时失败）。在拿到可用账户信息前
    /// 做有限次补拉，保证直接进版本管理/设置时侧栏也不会一直停在「未登录」。
    /// </summary>
    private async Task SettleShellAccountAsync()
    {
        for (var attempt = 0; attempt < 12 && !_shellAccountSettled; attempt++)
        {
            await Task.Delay(attempt == 0 ? 600 : 1000);
            if (_shellAccountSettled) return;
            await RefreshShellAsync(force: true);
        }
        HideStartupMask();
        // 12 次补拉都没拿到：别一直写「正在读取」，如实说读不到
        if (!_shellAccountSettled && AccountName.Text == "正在读取账户…")
        {
            AccountName.Text = "未登录";
            AccountType.Text = "读取账户超时";
        }
    }

    private void UpdateSearchLayout()
    {
        if (SearchHost is null || SearchTriggerHint is null) return;
        var available = Math.Max(0, RootGrid.ActualWidth);
        if (available <= 0) return;

        // 搜索栏靠左（品牌区右边），灵动岛紧挨在它右边，展开时向右撑开、占满到窗口按钮前的空位。
        var narrow = available < 1000;
        SearchTriggerHint.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        _searchHostBaseWidth = narrow ? 42 : (available >= 1280 ? 320 : 280);
        // 搜索框在整个窗口里水平居中；窗口太窄放不下时退到品牌区右边（300）
        var searchLeft = Math.Max(300.0, (available - _searchHostBaseWidth) / 2);
        SearchHost.HorizontalAlignment = HorizontalAlignment.Left;
        SearchHost.Margin = new Thickness(searchLeft, 0, 0, 0);
        if (!_spotlightOpen) SearchHost.Width = _searchHostBaseWidth;
        if (TaskIsland is not null) TaskIsland.Margin = new Thickness(searchLeft + _searchHostBaseWidth + 12, 6, 0, 0);
    }

    /// <summary>各页搜索按钮共用：打开顶栏搜索并做放大聚焦动画（宽 320→560、180ms ease-out，输入框发光一圈）。</summary>
    public void FocusGlobalSearch()
    {
        UpdateSearchScopeText();
        OpenSpotlight();
    }

    /// <summary>宿主页打开子页时保留 Alt+Left 返回历史。</summary>
    public void PushSubPage(Action back)
    {
        if (_currentEntry is null) return;
        _navHistory.Add(_currentEntry with { Back = back });
        UpdateBackButton();
    }

    /// <summary>宿主页通过面包屑关闭子页时移除对应的返回项。</summary>
    public void PopSubPage()
    {
        for (var i = _navHistory.Count - 1; i >= 0; i--)
        {
            if (_navHistory[i].Back is null) continue;
            _navHistory.RemoveAt(i);
            break;
        }
        UpdateBackButton();
    }

    /// <summary>宿主页子 tab 切换时同步顶栏搜索作用域（GetPage / InstancePage 调用）。</summary>
    public void NotifySubTab(string tag)
    {
        if (_currentTag == tag) return;
        _currentTag = tag;
        // 端内切 tab 不改 Frame，但历史里要记住用户最后停在哪一页。
        if (_currentEntry is not null) _currentEntry = _currentEntry with { Tag = tag };
        UpdateSearchScopeText();
    }

    /// <summary>顶栏只做全局搜索（跨页跳转），文字固定；「本页」过滤在各页自己的搜索框里（Ctrl+K 聚焦）。</summary>
    private void UpdateSearchScopeText()
    {
        if (SearchTriggerHint is not null) SearchTriggerHint.Text = "搜索全部  Ctrl+Shift+K";
        if (SearchBox is not null) SearchBox.PlaceholderText = "搜索页面、设置、实例、Mod…";
    }

    private void OpenSpotlight_Click(object sender, RoutedEventArgs e) => OpenSpotlight();

    private double _searchHostBaseWidth = 320;
    private bool _spotlightOpen;

    /// <summary>浮层宽度：最大 760，窗口窄时两侧各让出 230 DIP，不盖住左侧品牌区和右侧窗口按钮。</summary>
    private double SpotlightWidth => Math.Clamp((RootGrid?.ActualWidth ?? 1120) - 80, 440, 760);

    private void OpenSpotlight()
    {
        if (OnboardingHost.Visibility == Visibility.Visible) return;
        _spotlightOpen = true;
        // 浮层盖在标题栏搜索按钮上方时，按钮自己的提示文字会透出来，打开期间让它不可见。
        SearchTrigger.Opacity = 0;
        SpotlightCard.Width = SpotlightWidth;
        SpotlightResultsHost.Width = SpotlightWidth;
        if (SpotlightLayer.Visibility == Visibility.Visible)
        {
            SearchBox.Focus(FocusState.Programmatic);
            return;
        }

        SpotlightLayer.Visibility = Visibility.Visible;
        SpotlightLayer.Opacity = 1;
        SpotlightBackdrop.IsHitTestVisible = true;
        SpotlightBackdrop.Opacity = 1;
        SpotlightCard.Opacity = 1;
        SpotlightTransform.ScaleX = 1;
        SpotlightTransform.ScaleY = 1;
        SpotlightTransform.TranslateY = 0;

        var sb = new Storyboard();
        AddAnimation(sb, SpotlightCard, "Opacity", 0.82, 1, 180);
        AddAnimation(sb, SpotlightTransform, "ScaleX", 0.96, 1, 220);
        AddAnimation(sb, SpotlightTransform, "ScaleY", 0.96, 1, 220);
        AddAnimation(sb, SpotlightTransform, "TranslateY", -12, 0, 220);
        sb.Begin();

        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            RefreshSearch();
            SearchBox.IsSuggestionListOpen = false;
        });

        // 等待浮出动画与布局完成后再抢一次焦点；只靠首帧 Focus 在
        // Ctrl+K 路径下会被页面按钮抢走，出现“浮层已开但打字无效”。
        var focusTimer = DispatcherQueue.CreateTimer();
        focusTimer.Interval = TimeSpan.FromMilliseconds(260);
        focusTimer.IsRepeating = false;
        focusTimer.Tick += (_, _) =>
        {
            if (SpotlightLayer.Visibility == Visibility.Visible)
                SearchBox.Focus(FocusState.Keyboard);
        };
        focusTimer.Start();
    }

    private void CloseSpotlight()
    {
        _spotlightOpen = false;
        SearchTrigger.Opacity = 1;
        SearchHost.Width = _searchHostBaseWidth;
        if (SpotlightLayer.Visibility != Visibility.Visible) return;

        // 复位放大聚焦动画留下的宽度与发光描边，回到主题默认样式。
        SpotlightCard.Width = SpotlightWidth;
        SpotlightCard.ClearValue(Border.BorderBrushProperty);

        // 点了结果后立刻不再拦截点击；遮罩的淡出只是动画，不能让它挡住后面的页面或标题栏。
        SpotlightBackdrop.IsHitTestVisible = false;
        _suppressSearch = true;
        SearchBox.Text = "";
        SearchBox.ItemsSource = null;
        SearchBox.IsSuggestionListOpen = false;
        SpotlightResults.ItemsSource = null;
        SpotlightResults.Visibility = Visibility.Collapsed;
        SpotlightResultsHost.Visibility = Visibility.Collapsed;
        _suppressSearch = false;

        var sb = new Storyboard();
        AddAnimation(sb, SpotlightBackdrop, "Opacity", SpotlightBackdrop.Opacity, 0, 130);
        AddAnimation(sb, SpotlightCard, "Opacity", SpotlightCard.Opacity, 0, 130);
        AddAnimation(sb, SpotlightTransform, "ScaleX", SpotlightTransform.ScaleX, 0.97, 150);
        AddAnimation(sb, SpotlightTransform, "ScaleY", SpotlightTransform.ScaleY, 0.97, 150);
        AddAnimation(sb, SpotlightTransform, "TranslateY", SpotlightTransform.TranslateY, -8, 150);
        sb.Completed += (_, _) => SpotlightLayer.Visibility = Visibility.Collapsed;
        sb.Begin();

        var hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = TimeSpan.FromMilliseconds(220);
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => SpotlightLayer.Visibility = Visibility.Collapsed;
        hideTimer.Start();
    }

    private static void AddAnimation(Storyboard sb, DependencyObject target, string path,
        double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        sb.Children.Add(anim);
    }

    private void CloseSpotlight_Tapped(object sender, TappedRoutedEventArgs e) => CloseSpotlight();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSpotlight();
            return;
        }

        if (SpotlightResultsHost.Visibility != Visibility.Visible || _hits.Count == 0) return;
        if (e.Key == Windows.System.VirtualKey.Down)
        {
            SpotlightResults.SelectedIndex = Math.Min(_hits.Count - 1, SpotlightResults.SelectedIndex + 1);
            SpotlightResults.ScrollIntoView(SpotlightResults.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Up)
        {
            SpotlightResults.SelectedIndex = Math.Max(0, SpotlightResults.SelectedIndex - 1);
            SpotlightResults.ScrollIntoView(SpotlightResults.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // 以界面上看到的第一行为准：选中项 > 列表第 0 项 > 兜底第一个可执行项。
            var hit = SpotlightResults.SelectedItem as SearchHit
                      ?? (SpotlightResults.Items.Count > 0 ? SpotlightResults.Items[0] as SearchHit : null)
                      ?? _hits.FirstOrDefault(h => h.IsActionable);
            if (hit is null) return;
            e.Handled = true;
            CommitSearchHit(hit);
        }
    }

    // ---- 标题栏穿透区：灵动岛不是按钮，系统会把它当成可拖动的标题栏，悬停 / 点击都进不来 ----
    private string _islandPassKey = "";

    private void UpdateIslandPassthrough()
    {
        try
        {
            if (RootGrid.XamlRoot is null) return;
            var s = SizeScale();       // 内容缩放后：RootGrid 内坐标 × 实际物理像素比
            var rects = new List<Windows.Graphics.RectInt32>();
            foreach (var el in new FrameworkElement[] { TaskIsland.PillElement })
            {
                if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;
                var b = el.TransformToVisual(RootGrid).TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
                rects.Add(new Windows.Graphics.RectInt32((int)Math.Floor(b.X * s), (int)Math.Floor(b.Y * s),
                    (int)Math.Ceiling(b.Width * s), (int)Math.Ceiling(b.Height * s)));
            }
            if (rects.Count == 0) return;
            var key = string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
            if (key == _islandPassKey) return;
            _islandPassKey = key;
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex)
        {
            App.Trace("IslandPassthrough failed: " + ex.GetType().Name);
        }
    }

    // ---- 好友邀请弹窗 ----
    private DispatcherQueueTimer? _inviteToastTimer;
    private TaskNotification? _inviteToastNote;

    /// <summary>收到邀请 / 回复时在右上角弹出提示；窗口不在前台时任务栏图标闪烁。必须在界面线程调用。</summary>
    public void ShowInviteToast(TaskNotification note)
    {
        // 通知现在由右上角的灵动岛自己展开（TaskIsland 订阅了 NotificationsChanged）；这里只负责窗口不在前台时闪任务栏。
        FlashTaskbarIfBackground();
    }

    private void FlashTaskbarIfBackground()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (GetForegroundWindow() == hwnd) return;
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG
                uCount = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }

    /// <summary>旧的右上角邀请弹窗（已被灵动岛取代，保留代码备查，不再调用）。</summary>
    private void ShowInviteToastLegacy(TaskNotification note)
    {
        if (InviteToast is null) return;
        _inviteToastNote = note;
        InviteToastTitle.Text = note.Title;
        InviteToastBody.Text = note.Message;
        InviteToastButtons.Visibility = note.InviteOpen ? Visibility.Visible : Visibility.Collapsed;
        InviteToastAccept.IsEnabled = InviteToastDecline.IsEnabled = true;
        PlayInviteToastIn();
        RestartInviteToastTimer(note.InviteOpen ? 30 : 8);

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (GetForegroundWindow() != hwnd)
            {
                var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 0x0000000F /* FLASHW_ALL | FLASHW_TIMERNOFG */, uCount = 5, dwTimeout = 0 };
                FlashWindowEx(ref fi);
            }
        }
        catch { /* 闪烁失败不影响弹窗 */ }
    }

    private void RestartInviteToastTimer(int seconds)
    {
        _inviteToastTimer ??= DispatcherQueue.CreateTimer();
        _inviteToastTimer.Stop();
        _inviteToastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _inviteToastTimer.IsRepeating = false;
        _inviteToastTimer.Tick -= InviteToastTimer_Tick;
        _inviteToastTimer.Tick += InviteToastTimer_Tick;
        _inviteToastTimer.Start();
    }

    private Storyboard? _inviteToastStory;

    /// <summary>滑入 + 淡入（260 ms，先快后慢）。同一个 Storyboard 里每个属性只打一条动画。</summary>
    private void PlayInviteToastIn()
    {
        _inviteToastStory?.Stop();
        InviteToast.Opacity = 0;
        InviteToastShift.X = 48;
        InviteToast.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation { From = 48, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(260)), EasingFunction = ease };
        Storyboard.SetTarget(slide, InviteToastShift);
        Storyboard.SetTargetProperty(slide, "X");
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)) };
        Storyboard.SetTarget(fade, InviteToast);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var story = new Storyboard();
        story.Children.Add(slide);
        story.Children.Add(fade);
        story.Completed += (_, _) => { InviteToast.Opacity = 1; InviteToastShift.X = 0; };
        _inviteToastStory = story;
        story.Begin();
    }

    /// <summary>淡出 + 往右滑出（180 ms），结束后再 Collapsed，不让它挡住后面的点击。</summary>
    private void HideInviteToast()
    {
        if (InviteToast is null || InviteToast.Visibility != Visibility.Visible) return;
        _inviteToastStory?.Stop();

        var slide = new DoubleAnimation { From = InviteToastShift.X, To = 36, Duration = new Duration(TimeSpan.FromMilliseconds(180)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(slide, InviteToastShift);
        Storyboard.SetTargetProperty(slide, "X");
        var fade = new DoubleAnimation { From = InviteToast.Opacity, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(160)) };
        Storyboard.SetTarget(fade, InviteToast);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var story = new Storyboard();
        story.Children.Add(slide);
        story.Children.Add(fade);
        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_inviteToastStory, story)) return;      // 中途又弹了新的，别把它收掉
            InviteToast.Visibility = Visibility.Collapsed;
            InviteToast.Opacity = 1;
            InviteToastShift.X = 0;
        };
        _inviteToastStory = story;
        story.Begin();
    }

    private void InviteToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        HideInviteToast();
    }

    private void InviteToastClose_Click(object sender, RoutedEventArgs e)
    {
        _inviteToastTimer?.Stop();
        HideInviteToast();
    }

    private async void InviteToastAccept_Click(object sender, RoutedEventArgs e) => await RespondFromToastAsync(true);

    private async void InviteToastDecline_Click(object sender, RoutedEventArgs e) => await RespondFromToastAsync(false);

    private async Task RespondFromToastAsync(bool accepted)
    {
        var note = _inviteToastNote;
        if (note is null) return;
        InviteToastAccept.IsEnabled = InviteToastDecline.IsEnabled = false;
        _inviteToastTimer?.Stop();
        var done = await App.Online.RespondToInviteAsync(note, accepted);
        InviteToastBody.Text = note.ResultText.Length > 0 ? note.ResultText : note.Message;
        if (done)
        {
            InviteToastButtons.Visibility = Visibility.Collapsed;
            RestartInviteToastTimer(3);
        }
        else
        {
            InviteToastAccept.IsEnabled = InviteToastDecline.IsEnabled = true;   // 网络问题：留着按钮重试
            RestartInviteToastTimer(30);
        }
    }

    private DispatcherQueueTimer? _crashToastTimer;
    private readonly Queue<DateTime> _crashTimes = new();

    /// <summary>XAML 未处理异常被吞掉之后给一句可见的反馈；10 秒内第三次升级文案。</summary>
    public void ShowCrashToast()
    {
        if (CrashToast is null) return;
        var now = DateTime.Now;
        _crashTimes.Enqueue(now);
        while (_crashTimes.Count > 0 && (now - _crashTimes.Peek()).TotalSeconds > 10) _crashTimes.Dequeue();

        var escalated = _crashTimes.Count >= 3;
        CrashToastTitle.Text = escalated ? "连续出错了" : "这一步没有成功";
        CrashToastBody.Text = escalated
            ? "短时间内出了好几次错，已记录。建议保存手头的事后重启启动器。"
            : "已记录错误，可以继续使用。如果反复出现，请打开日志目录后反馈。";
        CrashToast.Visibility = Visibility.Visible;

        _crashToastTimer ??= DispatcherQueue.CreateTimer();
        _crashToastTimer.Stop();
        _crashToastTimer.Interval = TimeSpan.FromSeconds(8);
        _crashToastTimer.IsRepeating = false;
        _crashToastTimer.Tick -= CrashToastTimer_Tick;
        _crashToastTimer.Tick += CrashToastTimer_Tick;
        _crashToastTimer.Start();
    }

    private void CrashToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (CrashToast is not null) CrashToast.Visibility = Visibility.Collapsed;
    }

    private void CrashToastOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { OpenExternal(AppPreferencesStore.FolderPath); } catch { }
        if (CrashToast is not null) CrashToast.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        // 以界面上看到的第一行为准：选中项 > 列表第 0 项 > 兜底第一个可执行项。
            var hit = SpotlightResults.SelectedItem as SearchHit
                      ?? (SpotlightResults.Items.Count > 0 ? SpotlightResults.Items[0] as SearchHit : null)
                      ?? _hits.FirstOrDefault(h => h.IsActionable);
        if (hit is null) return;
        e.Handled = true;
        CommitSearchHit(hit);
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => RefreshSearch();

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSearch) return;
        RefreshSearch();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F6)
        {
            // F6 在「侧栏 ↔ 内容区」之间循环焦点，纯键盘用户不用一路 Tab。
            e.Handled = true;
            CycleShellFocus();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape &&
            SpotlightLayer.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CloseSpotlight();
        }
    }

    /// <summary>F6：焦点在侧栏就进内容区，否则回到侧栏第一个导航项。</summary>
    private void CycleShellFocus()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        var inSidebar = false;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, NavScroll))
            {
                inSidebar = true;
                break;
            }
        }

        if (inSidebar)
        {
            ContentFrame.Focus(FocusState.Keyboard);
        }
        else
        {
            NavHome.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>Ctrl+1…6：按侧栏顺序跳转，加速键挂在各导航项上，ToolTip 里标了组合键。</summary>
    private void NavAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var tag = sender.Key switch
        {
            Windows.System.VirtualKey.Number1 => "home",
            Windows.System.VirtualKey.Number2 => "get",
            Windows.System.VirtualKey.Number3 => "instance",
            Windows.System.VirtualKey.Number4 => "account",
            Windows.System.VirtualKey.Number5 => "settings",
            _ => null,
        };
        if (tag is null) return;
        args.Handled = true;
        Navigate(tag, recordHistory: false, clearHistory: true);
    }

    private void RefreshSearch()
    {
        if (SearchBox is null || ContentFrame is null || SpotlightResults is null) return;
        var query = SearchBox.Text ?? "";
        var contextVersion = (ContentFrame.Content as VersionsPage)?.SelectedVersionId;
        _hits = SearchService.Query(Snapshot, _currentTag, query, true, contextVersion).ToList();
        SpotlightResults.ItemsSource = _hits;
        SpotlightResults.Visibility = Visibility.Visible;   // CloseSpotlight 会把列表收起，这里必须还原，否则关过一次后结果再也出不来
        var hostWasVisible = SpotlightResultsHost.Visibility == Visibility.Visible;
        SpotlightResultsHost.Visibility = query.Trim().Length > 0 && _hits.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 结果面板从无到有时淡入一下；边打字边刷新的那几次不重播，免得闪。
        if (!hostWasVisible && SpotlightResultsHost.Visibility == Visibility.Visible)
            MeowLauncher.WinUI.Controls.Motion.Enter(SpotlightResultsHost, 0, 6, 160);
        SpotlightResults.SelectedIndex = -1;
        SearchBox.IsSuggestionListOpen = false;
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SearchHit hit) return;
        CommitSearchHit(hit);
    }

    private void SpotlightResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchHit hit) CommitSearchHit(hit);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchHit hit)
        {
            CommitSearchHit(hit);
            return;
        }

        RefreshSearch();
        var first = _hits.FirstOrDefault(h => h.IsActionable);
        if (first is not null)
        {
            CommitSearchHit(first);
            return;
        }

        var query = (args.QueryText ?? SearchBox.Text ?? "").Trim();
        if (query.Length == 0) return;
        OpenExternal($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}");
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        // 当前页自己能过滤时，Ctrl+K 只聚焦本页搜索框；全局浮层留给 Ctrl+Shift+K 和标题栏触发器。
        if (ContentFrame?.Content is IPageSearchFilter pageFilter && pageFilter.TryFocusSearch()) return;
        OpenSpotlight();
    }

    private void GlobalSearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenSpotlight();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (SpotlightLayer.Visibility == Visibility.Visible)
        {
            args.Handled = true;
            CloseSpotlight();
        }
    }

    private async void CommitSearchHit(SearchHit hit)
    {
        if (!hit.IsActionable) return;               // 「没有找到…」占位行：点了没反应是对的，也不关浮层
        try { await CommitSearchHitCoreAsync(hit); }
        catch (Exception ex) { App.LogCrash("SearchCommit", ex); }
    }

    private async Task CommitSearchHitCoreAsync(SearchHit hit)
    {
        CloseSpotlight();
        ContentFrame.Focus(FocusState.Programmatic);
        await Task.Yield();
        await Task.Delay(120);

        switch (hit.Action)
        {
            case SearchActionKind.Navigate:
                if (!string.IsNullOrWhiteSpace(hit.Focus)) _pendingSettingsFocus = hit.Focus;
                Navigate(hit.PageTag);
                break;
            case SearchActionKind.Version:
                Navigate("versions");
                if (ContentFrame?.Content is VersionsPage page)
                    await page.OpenVersionAsync(hit.VersionId, hit.Focus);
                break;
            case SearchActionKind.PickInstance:
                await ShowInstancePickerAsync();
                break;
            case SearchActionKind.Online:
                _pendingOnlineQuery = hit.Focus;
                Navigate("online");
                break;
            case SearchActionKind.External:
                OpenExternal(hit.Url);
                break;
        }
    }

    private async Task ShowInstancePickerAsync()
    {
        if (Snapshot is null || Snapshot.Versions.Count == 0)
        {
            Navigate("versions");
            return;
        }

        var picker = new Controls.InstancePicker();
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "切换实例",
            Content = picker,
            SecondaryButtonText = "全局设置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
        };
        VersionInfo? chosen = null;
        picker.Picked += v => { chosen = v; dialog.Hide(); };

        var result = await dialog.ShowAsync();
        if (chosen is not null)
        {
            var (ok, message) = await InstanceSelection.SelectAsync(chosen);
            if (!ok)
            {
                await new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                    Title = "切换失败", Content = message, CloseButtonText = "好" }.ShowAsync();
                return;
            }
            Navigate("versions");
            if (ContentFrame.Content is VersionsPage page) await page.OpenVersionAsync(chosen.Id, "");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Navigate("settings");
        }
    }

    private static async void OpenExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            // 通过 shell broker 打开，避免提升权限的进程把 Edge 一起提权。
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            // 外部浏览器打不开时不打断主流程。
        }
    }

    private static string FocusLabel(string focus) => focus switch
    {
        "theme" => "主题",
        "background" => "背景与材质",
        "java" => "Java 版本",
        "memory" => "内存分配",
        "jvm" => "JVM 参数",
        "download" => "下载源",
        "curseforge" => "CurseForge Key",
        "about" => "关于",
        _ => "设置",
    };

    /// <summary>把当前窗口尺寸换算回设计像素存进偏好；关闭时调用。</summary>
    private void SidebarAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_compact) return;
        _sidebarPinned = !_sidebarPinned;
        SetSidebarExpanded(_sidebarPinned || _sidebarExpanded);
    }


    /// <summary>顶栏左端的菜单伸缩键：常显开合侧栏。</summary>
    private void MenuToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarPinned = !_sidebarPinned;
        SetSidebarExpanded(_sidebarPinned);
    }

    private void ApplyCompactMode()
    {
        var width = RootGrid.ActualWidth;
        if (width <= 0) return;
        var compact = width < CompactWidthThreshold;
        if (compact == _compact) return;

        _compact = compact;
        if (compact)
        {
            _sidebarPinned = false;
            SetSidebarExpanded(false);
        }
        else
        {
            _sidebarPinned = false;
            _sidebarExpanded = true;
            SidebarColumn.Width = new GridLength(SidebarFullWidth);
            SidebarRoot.Width = SidebarFullWidth;
            SidebarRoot.Padding = new Thickness(12, 10, 12, 12);
            SidebarRoot.HorizontalAlignment = HorizontalAlignment.Left;
            Canvas.SetZIndex(SidebarRoot, 0);
            SetNavLabelsVisible(true);
        }

        CompactChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窄档展开/收起：收起时列宽 64、侧栏本体也只占 64；展开时侧栏本体宽 218 覆盖在内容上（列宽不变，不挤内容）。</summary>
    /// <summary>侧栏开合：60fps 的宽度动画（180ms CubicEase），宽窗与窄窗都走同一条路径。</summary>
    private void SetSidebarExpanded(bool expanded)
    {
        _sidebarExpanded = expanded;

        // 窄档展开时列宽保持 64，侧栏浮在内容上，页面不会跟着左右重排；
        // 宽档收起/展开才真正让内容区让位。
        SidebarColumn.Width = new GridLength(_compact ? SidebarRailWidth : (expanded ? SidebarFullWidth : SidebarRailWidth));
        SidebarRoot.Padding = expanded ? new Thickness(12, 10, 12, 12) : new Thickness(8, 10, 8, 10);
        SidebarRoot.HorizontalAlignment = HorizontalAlignment.Left;
        Canvas.SetZIndex(SidebarRoot, 2);
        SetNavLabelsVisible(expanded);
        SetGroupTitles(expanded);
        AnimateSidebarWidth(expanded ? SidebarFullWidth : SidebarRailWidth);

        if (expanded && _compact && !_sidebarPinned) _hoverWatch.Start();
        else _hoverWatch.Stop();
    }

    /// <summary>
    /// 侧栏收起成 64 图标栏时，分组标题「实例 / 我的」放不下，用一条横杠占位（居中、弱化），
    /// 展开时恢复文字——这是占位符，不是内容隐藏。
    /// </summary>
    private void SetGroupTitles(bool expanded)
    {
        SetGroupTitle(GroupInstanceTitle, GroupInstanceTitleRule, "实例", expanded);
        SetGroupTitle(GroupMineTitle, GroupMineTitleRule, "我的", expanded);
    }

    /// <summary>
    /// 分组标题与占位线放在同一个 16 高、同外边距的槽里，两者 Geometry 完全一致；
    /// 展开显示小标题文字，收起成图标栏时把文字换成长短合适的横线。
    /// </summary>
    private static void SetGroupTitle(TextBlock title, FrameworkElement rule, string text, bool expanded)
    {
        if (title is null) return;
        title.Text = text;
        title.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (rule is not null) rule.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把侧栏宽度补间到目标值；启动时第一次直接跳到位，避免开窗闪一下。</summary>
    private void AnimateSidebarWidth(double target)
    {
        // 先停掉上一次补间：被打断的动画会按 HoldEnd 继续把 Width 锁在旧值，
        // 于是出现「文字已收起、栏宽还是 218」的卡死态。
        _sidebarStory?.Stop();
        _sidebarStory = null;

        var current = SidebarRoot.Width;
        if (double.IsNaN(current) || current <= 0) current = target;

        if (!_sidebarAnimatedOnce || Math.Abs(current - target) < 0.5)
        {
            _sidebarAnimatedOnce = true;
            SidebarRoot.Width = target;
            return;
        }

        // 基值先写目标值，动画只负责过渡；FillBehavior=Stop 结束后自动落回基值。
        SidebarRoot.Width = target;
        var anim = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, SidebarRoot);
        Storyboard.SetTargetProperty(anim, "Width");
        _sidebarStory = new Storyboard();
        _sidebarStory.Children.Add(anim);
        _sidebarStory.Completed += (_, _) => { SidebarRoot.Width = target; _sidebarStory = null; };
        _sidebarStory.Begin();
    }

    /// <summary>
    /// 悬停展开的收起判定：直接读系统光标位置与侧栏屏幕矩形比对。
    /// PointerExited 在「侧栏展开导致自身重排」时会漏事件，之前就卡在展开态。
    /// </summary>
    private void WatchHoverState()
    {
        if (!_compact || !_sidebarExpanded || _sidebarPinned)
        {
            _hoverWatch.Stop();
            return;
        }

        if (!GetCursorPos(out var point) || WindowHandle == IntPtr.Zero) return;
        // 屏幕坐标 → 客户区坐标 → 设计像素，再和侧栏在当前窗口里的矩形比。
        var client = new CursorPoint { X = point.X, Y = point.Y };
        if (!ScreenToClient(WindowHandle, ref client)) return;
        var scale = SizeScale();
        var x = client.X / scale;
        var y = client.Y / scale;
        var topLeft = SidebarRoot.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
        var left = topLeft.X;
        var right = left + SidebarRoot.ActualWidth;
        var bottom = topLeft.Y + SidebarRoot.ActualHeight;
        if (x < left || x > right - 1 || y < topLeft.Y || y > bottom)
        {
            SetSidebarExpanded(false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

	private void SetNavLabelsVisible(bool visible)
	{
		Visibility visibility = ((!visible) ? Visibility.Collapsed : Visibility.Visible);
		foreach (var (key, toggleButton2) in _navButtons)
		{
			if (!_navContentSnapshot.TryGetValue(key, out object? value))
			{
				value = toggleButton2.Content;
				_navContentSnapshot[key] = value;
			}
			if (visible)
			{
				toggleButton2.Content = value;
				toggleButton2.HorizontalContentAlignment = HorizontalAlignment.Left;
				continue;
			}
			string glyph = (value as StackPanel)?.Children.OfType<FontIcon>().FirstOrDefault()?.Glyph ?? string.Empty;
			toggleButton2.Content = new FontIcon
			{
				Glyph = glyph,
				FontSize = 15.0
			};
			toggleButton2.HorizontalContentAlignment = HorizontalAlignment.Center;
		}
		GroupInstanceTitle.Visibility = visibility;
		GroupMineTitle.Visibility = visibility;
		GroupInstanceTitleRule.Visibility = Visibility.Collapsed;
		GroupMineTitleRule.Visibility = Visibility.Collapsed;
		AccountName.Visibility = visibility;
		AccountType.Visibility = visibility;
		TaskDockBody.Visibility = visibility;
		AccountButton.Padding = (visible ? new Thickness(9.0, 7.0, 9.0, 7.0) : new Thickness(6.0));
		AccountButton.HorizontalContentAlignment = ((!visible) ? HorizontalAlignment.Center : HorizontalAlignment.Stretch);
		TaskDock.Padding = (visible ? new Thickness(11.0, 10.0, 11.0, 10.0) : new Thickness(8.0));
		TaskDock.HorizontalContentAlignment = ((!visible) ? HorizontalAlignment.Center : HorizontalAlignment.Stretch);
	}

	private static (int Width, int Height) ComputeAdaptiveDesignSize(double scale, RectInt32 area)
	{
		int value = (int)Math.Round((double)area.Width * 0.66 / scale);
		int value2 = (int)Math.Round((double)area.Height * 0.8 / scale);
		return (Width: Math.Clamp(value, 1120, 1600), Height: Math.Clamp(value2, 760, 1000));
	}

	public void ApplyAdaptiveWindowSize()
	{
		if (WindowHandle == IntPtr.Zero)
		{
			return;
		}
		try
		{
			double dpi = (double)GetDpiForWindow(WindowHandle) / 96.0;
			double num = SizeScale();
			RectInt32 workArea = DisplayArea.Primary.WorkArea;
			(int Width, int Height) tuple = ComputeAdaptiveDesignSize(num, workArea);
			int item = tuple.Width;
			int item2 = tuple.Height;
			int num2 = Math.Min((int)((double)item * num), workArea.Width - 24);
			int num3 = Math.Min((int)((double)item2 * num), workArea.Height - 24);
			PointInt32 position = base.AppWindow.Position;
			SizeInt32 size = base.AppWindow.Size;
			double num4 = (double)position.X + (double)size.Width / 2.0;
			double num5 = (double)position.Y + (double)size.Height / 2.0;
			int x = (int)Math.Round(num4 - (double)num2 / 2.0);
			int y = (int)Math.Round(num5 - (double)num3 / 2.0);
			_lastRasterScale = dpi;
			base.AppWindow.MoveAndResize(new RectInt32(x, y, num2, num3));
		}
		catch
		{
		}
	}

	private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
	{
		ApplyContentScale();
		ApplyTitleBarHeight();
		if (_preferences.AutoWindowSize)
		{
			double rasterizationScale = sender.RasterizationScale;
			if (!(Math.Abs(rasterizationScale - _lastRasterScale) < 0.001))
			{
				ApplyAdaptiveWindowSize();
			}
		}
	}

	public bool RememberWindowSizePreference
	{
		get => _preferences.RememberWindowSize;
		set
		{
			_preferences.RememberWindowSize = value;
			if (value) RememberWindowSize();
			AppPreferencesStore.Save(_preferences);
		}
	}

	/// <summary>是否由我们主动最小化过（游戏退出时只在这种情况还原）。</summary>
	public bool MinimizedForGame { get; private set; }

	/// <summary>启动游戏后按偏好收进任务栏（AfterLaunch=minimize 时用）。</summary>
	public void MinimizeForGame()
	{
		try
		{
			_presenter?.Minimize();
			MinimizedForGame = true;
		}
		catch { }
	}

	/// <summary>游戏退出后把窗口还原到前台；不是我们最小化的就不动用户窗口。</summary>
	/// <summary>
	/// 用户自己把启动器从任务栏拉回来、或它被系统还原时清位：
	/// 否则游戏退出时会再 Activate 一次，无端抢焦点。
	/// </summary>
	private void MaybeClearMinimizedForGame()
	{
	    if (!MinimizedForGame) return;
	    try
	    {
	        if (_presenter is null || _presenter.State != OverlappedPresenterState.Minimized) MinimizedForGame = false;
	    }
	    catch { }
	}

	public void RestoreFromGame()
	{
		if (!MinimizedForGame) return;
		MinimizedForGame = false;
		var previous = GetForegroundWindow();
		try
		{
			_presenter?.Restore();
			Activate();
			// 游戏是我们拉起来的、窗口也是我们最小化的，但退出时不该把用户正在用的窗口顶掉：
			// 还原后把前台还给原来那个窗口（SetForegroundWindow 由同一进程调用，通常能生效）。
			var self = WindowHandle;
			if (previous != IntPtr.Zero && previous != self) SetForegroundWindow(previous);
		}
		catch { }
	}

	public void RememberWindowSize()
	{
		if (!_preferences.RememberWindowSize || _preferences.AutoWindowSize || WindowHandle == IntPtr.Zero)
		{
			return;
		}
		try
		{
			double num = SizeScale();
			SizeInt32 size = base.AppWindow.ClientSize;
			int num2 = (int)Math.Round((double)size.Width / num);
			int num3 = (int)Math.Round((double)size.Height / num);
			if (num2 >= 1120 - 1 && num3 >= 760 - 1)
			{
				_preferences.WindowWidth = num2;
				_preferences.WindowHeight = num3;
				AppPreferencesStore.Save(_preferences);
			}
		}
		catch
		{
		}
	}

	public void ApplyWindowSize(int designWidth, int designHeight, bool persist)
	{
		int num = Math.Max(1120, designWidth);
		int num2 = Math.Max(760, designHeight);
		double num3 = SizeScale();
		RectInt32 workArea = DisplayArea.GetFromWindowId(base.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
		var (frameW, frameH) = ClientFrameDelta();
		int num4 = Math.Min((int)Math.Round((double)num * num3), workArea.Width - frameW);
		int num5 = Math.Min((int)Math.Round((double)num2 * num3), workArea.Height - frameH);
		base.AppWindow.Resize(new SizeInt32(num4 + frameW, num5 + frameH));
		base.AppWindow.Move(new PointInt32(workArea.X + Math.Max(0, (workArea.Width - base.AppWindow.Size.Width) / 2), workArea.Y + Math.Max(0, (workArea.Height - base.AppWindow.Size.Height) / 2)));
		_preferences.WindowWidth = num;
		_preferences.WindowHeight = num2;
		if (persist)
		{
			AppPreferencesStore.Save(_preferences);
		}
	}

#if DEBUG
	/// <summary>仅调试：debug-nav.txt 一行 "settings|afterlaunch" / "settings|proxy" / "version|<实例id>|launch" / "versions" / "home"。</summary>
	public async void DebugGoto(string line)
	{
		var log = System.IO.Path.Combine(Services.AppPreferencesStore.FolderPath, "debug-nav.log");
		void L(string m) { try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n"); } catch { } }
		try
		{
			L("goto '" + line + "'");
			var p = line.Trim().Split('|');
			switch (p[0])
			{
			case "settings":
				_pendingSettingsFocus = p.Length > 1 ? p[1] : "";
				L("before Navigate, frame=" + (ContentFrame?.Content?.GetType().Name ?? "null"));
				Navigate("settings");
				L("after Navigate, frame=" + (ContentFrame?.Content?.GetType().Name ?? "null"));
				break;
			case "dsearch":
				// dsearch|文字：进下载页并按版本号过滤（验证空结果态用，截图工具不能输入文字）
				Navigate("download");
				for (var i = 0; i < 30 && ContentFrame?.Content is not GetPage; i++) await Task.Delay(100);
				await Task.Delay(1200);
				if (ContentFrame?.Content is GetPage gp)
				{
					gp.ApplySearch(p.Length > 1 ? p[1] : "");
					L("dsearch applied: " + (p.Length > 1 ? p[1] : ""));
				}
				break;
			case "dtasks":
				// dtasks：把任务胶囊显示出来并展开浮层（截图/调试动画用）
				TaskDock.Visibility = Visibility.Visible;
				UpdateTaskDock();
				await Task.Delay(120);
				TaskFlyout.ShowAt(TaskDock);
				L("dtasks opened");
				break;
			case "dtaskdemo":
				// dtaskdemo：塞几条假任务进任务中心（只用于截图看样式）
				{
					var t1 = TaskCenter.Add("26.1.2", "正在准备…", "download", "\uE896");
					TaskCenter.BuildSteps(t1, new[] { "下载依赖库", "下载游戏主文件", "下载资源文件", "写入实例配置" });
					TaskCenter.Step(t1, 0, 2, 100, "115/115");
					TaskCenter.Step(t1, 1, 1, 62, "23.4 MB / 38.1 MB");
					TaskCenter.Stats(t1, 15.2 * 1024 * 1024, "剩余 4,120 个文件");
					TaskCenter.Progress(t1, 58.4, "正在下载游戏主文件 23.4 MB / 38.1 MB");
					t1.SetLive("Downloading library from https://bmclapi2.bangbang93.com/maven/…");

					var t2 = TaskCenter.Add("1.20.1-forge-47.4.29", "安装完成", "download", "\uE896");
					TaskCenter.BuildSteps(t2, new[] { "下载依赖库", "下载游戏主文件", "下载资源文件", "下载 Forge 安装文件", "安装加载器", "写入实例配置" });
					for (var i = 0; i < 6; i++) TaskCenter.Step(t2, i, 2, 100, "");
					TaskCenter.Progress(t2, 100, "安装完成");
					TaskCenter.Complete(t2, "安装完成");

					var t3 = TaskCenter.Add("1.21.11-neoforge-21.11.48", "连接被重置（ECONNRESET）", "download", "\uE896");
					TaskCenter.BuildSteps(t3, new[] { "下载依赖库", "下载游戏主文件", "下载资源文件", "写入实例配置" });
					TaskCenter.Step(t3, 0, 2, 100, "");
					TaskCenter.Step(t3, 1, 3, 0, "连接被重置");
					TaskCenter.Progress(t3, 31.5, "下载游戏主文件失败：连接被重置（ECONNRESET）");
					TaskCenter.Fail(t3, "下载游戏主文件失败：连接被重置（ECONNRESET）", null);

					Navigate("tasks");
					await Task.Delay(400);
					L("dtaskdemo seeded");
				}
				break;
			case "ddetail":
				// ddetail|版本号：进下载页并直接打开该版本的安装设置页（截图用）
				Navigate("download");
				for (var i = 0; i < 30 && ContentFrame?.Content is not GetPage; i++) await Task.Delay(100);
				if (ContentFrame?.Content is GetPage gd) await gd.DebugOpenDetail(p.Length > 1 ? p[1] : "");
				break;
			case "dmod":
				// dmod|搜索词[|实例id]：进在线资源，等结果出来后直接打开第一行的详情子页（截图用）；带实例id时不按游戏版本筛选，并把它当安装目标
				Navigate("download");
				for (var i = 0; i < 30 && ContentFrame?.Content is not GetPage; i++) await Task.Delay(100);
				if (ContentFrame?.Content is GetPage gm)
				{
					OnlinePage.DebugNoFilter = p.Length > 2 && !string.IsNullOrWhiteSpace(p[2]);
					gm.ShowTab("online", p.Length > 1 ? p[1] : "");
					await gm.DebugOpenFirstMod(p.Length > 2 ? p[2] : "");
				}
				break;
			case "theme":
				// theme|dark / theme|light / theme|system：只切换当前运行的主题，不落盘
				SetThemeMode(p.Length > 1 ? p[1] switch { "dark" => "Dark", "light" => "Light", _ => "System" } : "System", persist: false);
				L("theme -> " + _themeMode);
				break;
			case "account":
				// account|bottom：进账户页并滚到底（看「其他账户」「添加账户」）；account|top 回顶部
				Navigate("account");
				for (var i = 0; i < 30 && ContentFrame?.Content is not AccountPage; i++) await Task.Delay(100);
				await Task.Delay(600);
				if (ContentFrame?.Content is AccountPage acc)
				{
					var sv = acc.FindName("RootView") as Microsoft.UI.Xaml.Controls.ScrollViewer;
					if (p.Length > 1 && (p[1] == "collapsed" || p[1] == "expanded"))
					{
						acc.DebugSetInfoExpanded(p[1] == "expanded");
						L("account info -> " + p[1]);
					}
					else
					sv?.ChangeView(null, p.Length > 1 && p[1] == "bottom" ? double.MaxValue : 0, null, true);
					L("account scrolled: " + (p.Length > 1 ? p[1] : "top"));
				}
				break;
			case "launch":
				Navigate("home");
				await Task.Delay(900);
				if (ContentFrame.Content is HomePage home) home.DebugLaunch();
				L("launch requested");
				break;
			case "instsettings":
				// instsettings 或 instsettings|实例id：进「实例」页，打开该实例（缺省 = 当前实例）的详情，
				// 并滚到启动设置区、聚焦内存框（内存条在这里）。截图用。
				{
					var wantId = p.Length > 1 ? p[1] : "";
					if (wantId.Length == 0)
						wantId = Snapshot?.Current?.Id ?? Snapshot?.Selected ?? Snapshot?.Versions.FirstOrDefault()?.Id ?? "";
					L("instsettings: wantId='" + wantId + "' snapshot=" + (Snapshot is null ? "null" : Snapshot.Versions.Count + " versions"));
					Navigate("versions");
					for (var i = 0; i < 50 && ContentFrame?.Content is not VersionsPage; i++) await Task.Delay(100);
					L("instsettings: page=" + (ContentFrame?.Content?.GetType().Name ?? "null"));
					if (ContentFrame?.Content is VersionsPage ip)
					{
						await Task.Delay(600);   // 等实例列表加载完再按 id 打开
						await ip.OpenVersionAsync(wantId, "memory");
						L("instsettings: opened " + wantId);
					}
					else L("instsettings: versions page not ready");
				}
				break;
			case "version":
				Navigate("versions");
				for (var i = 0; i < 30 && ContentFrame?.Content is not VersionsPage; i++) await Task.Delay(100);
				L("frame=" + (ContentFrame?.Content?.GetType().Name ?? "null"));
				if (ContentFrame?.Content is VersionsPage page)
				{
					await page.OpenVersionAsync(p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : "launch");
					L("version opened: " + (p.Length > 1 ? p[1] : ""));
				}
				else L("version page not ready");
				break;
			default:
				Navigate(p[0]);
				break;
			}
			L("done");
		}
		catch (Exception ex)
		{
			L("EX " + ex.GetType().Name + ": " + ex.Message);
		}
	}
#endif

    public async Task<(bool Ok, string Message)> LaunchInstanceAsync(VersionInfo target)
    {
        var selected = await InstanceSelection.SelectAsync(target);
        if (!selected.Ok) return selected;
        Navigate("home");
        if (ContentFrame?.Content is not HomePage home) return (false, "启动页面没有准备好。");
        return await home.StartInstanceAsync(target) ? (true, "") : (false, "已有实例正在启动，请稍候。");
    }

	/// <summary>带着搜索词进「获取 › 在线资源」（实例页的 Mod 行点击用）。</summary>
	public void OpenOnline(string query)
	{
		_pendingOnlineQuery = query ?? "";
		Navigate("online");
	}

	public async void OpenOnlineProject(Models.ModrinthProject project, string instance)
	{
		Navigate("download");
		for (var i = 0; i < 30 && ContentFrame?.Content is not GetPage; i++) await Task.Delay(100);
		if (ContentFrame?.Content is GetPage gp)
		{
			gp.ShowTab("online", "");
			await gp.OpenProjectAsync(project, instance);
		}
	}

	public void NavigateLogs(string? file)
	{
		_pendingLogsFile = (string.IsNullOrWhiteSpace(file) ? null : file);
		Navigate("logs");
	}

	public void Navigate(string tag, bool recordHistory = true, bool clearHistory = false)
	{
        if (OnboardingHost.Visibility == Visibility.Visible) return;
		// 初始化尚未完成时不进入导航；后续统一使用已确认有效的页面容器。
		if (ContentFrame is not { } frame) return;
		// 已经在设置页、也没有要定位的分区：什么都不用做。连续点「设置」时每点一次都会新建一页并重新读一批后端数据。
		if (string.Equals(tag, "settings", StringComparison.Ordinal) && string.IsNullOrEmpty(_pendingSettingsFocus)
			&& frame.Content is SettingsPage)
		{
			return;
		}
		// 换整页时，旧页里还开着的子页随页面一起没了，它们的返回项不能留在历史里。
		_navHistory.RemoveAll(entry => entry.Back is not null);
		if (string.Equals(tag, "home", StringComparison.OrdinalIgnoreCase))
		{
			clearHistory = true;
			recordHistory = false;
		}
		if (clearHistory)
		{
			_navHistory.Clear();
		}
		if (recordHistory && !_restoringHistory)
		{
			NavEntry? currentEntry = _currentEntry;
			if (currentEntry is not null && !string.Equals(currentEntry.Tag, tag, StringComparison.Ordinal))
			{
				_navHistory.Add(currentEntry);
			}
		}
		_currentTag = tag;
		UpdateSearchScopeText();
		if (1 == 0)
		{
		}
		string key;
		switch (tag)
		{
		case "download":
		case "online":
			key = "get";
			break;
		case "roomupdate":
			key = "room";
			break;
		case "mods":
		case "assets":
		case "resourcepack":
		case "shaderpack":
		case "world":
		case "screenshot":
			key = "instance";
			break;
		default:
			key = tag;
			break;
		}
		if (1 == 0)
		{
		}
		string text = key;
		string currentSection = CurrentSection;
		CurrentSection = text;
		string pendingSettingsFocus = _pendingSettingsFocus;
		_pendingSettingsFocus = "";
		string pendingOnlineQuery = _pendingOnlineQuery;
		_pendingOnlineQuery = "";
		string text2 = _pendingLogsFile ?? "";
		NavigationTransitionInfo infoOverride = MeowLauncher.WinUI.Controls.Motion.PageRefresh();
		switch (text)
		{
		case "home":
			frame.Navigate(typeof(HomePage), null, infoOverride);
			break;
		case "versions":
			frame.Navigate(typeof(VersionsPage), null, infoOverride);
			break;
		case "settings":
			// Settings starts its entrance after its first rendered layout.
			frame.Navigate(typeof(SettingsPage), pendingSettingsFocus, new SuppressNavigationTransitionInfo());
			break;
		case "get":
			frame.Navigate(typeof(GetPage), null, infoOverride);
			break;
		case "instance":
			frame.Navigate(typeof(InstancePage), null, infoOverride);
			break;
		case "account":
			frame.Navigate(typeof(AccountPage), null, infoOverride);
			break;
		case "room":
			// 联机组件（模块 + RoomAgent + EasyTier）不随启动器分发：没装好就先进下载页，装好后由那一页再跳回来
			if (string.Equals(tag, "roomupdate", StringComparison.Ordinal))
			{
				frame.Navigate(typeof(OnlineSetupPage), "update", infoOverride);
			}
			else if (OnlineComponent.Ready || OnlineComponent.TryLoad())
			{
				frame.Navigate(typeof(RoomPage), null, infoOverride);
			}
			else
			{
				frame.Navigate(typeof(OnlineSetupPage), null, infoOverride);
			}
			break;
		case "tasks":
			frame.Navigate(typeof(TasksPage), null, infoOverride);
			break;
		case "logs":
			frame.Navigate(typeof(LogsPage), text2, infoOverride);
			_pendingLogsFile = "";
			break;
		default:
			frame.Navigate(typeof(HomePage), null, infoOverride);
			break;
		}
		if (frame.Content is GetPage getPage)
		{
			bool flag = ((tag == "download" || tag == "online") ? true : false);
			string tab = (flag ? tag : getPage.CurrentTab);
			getPage.ShowTab(tab, (tag == "online") ? pendingOnlineQuery : null);
		}
		else if (frame.Content is InstancePage instancePage)
		{
			InstancePage instancePage2 = instancePage;
			bool flag;
			switch (tag)
			{
			case "mods":
			case "assets":
			case "resourcepack":
			case "shaderpack":
			case "world":
			case "screenshot":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			instancePage2.ShowTab(flag ? tag : instancePage.CurrentTab);
		}
		foreach (KeyValuePair<string, ToggleButton> navButton in _navButtons)
		{
			navButton.Deconstruct(out key, out var value);
			string text3 = key;
			ToggleButton toggleButton = value;
			bool flag2 = text3 == text;
			toggleButton.IsChecked = flag2;
			toggleButton.Background = (flag2 ? ((Brush)Application.Current.Resources["AppNavSelectedBrush"]) : TransparentBrush);
		}
		if (frame.Content is HomePage homePage)
		{
			homePage.NavigateRequested += delegate(object? _, string target)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					Navigate(target);
				});
			};
			homePage.InstanceSettingsRequested += delegate(object? _, string id)
			{
				base.DispatcherQueue.TryEnqueue(delegate
				{
					OpenInstanceSettings(id);
				});
			};
			homePage.RefreshRequested += async delegate
			{
				await RefreshShellAsync(force: true);
			};
		}
		_currentEntry = new NavEntry(tag, pendingSettingsFocus, pendingOnlineQuery, text2);
		UpdateBackButton();
	}

	public void OpenAccountAdd()
	{
		Navigate("account");
		if (ContentFrame.Content is AccountPage accountPage)
		{
			accountPage.OpenAddAccountMenu();
			return;
		}
		base.DispatcherQueue.TryEnqueue(delegate
		{
			if (ContentFrame.Content is AccountPage accountPage2)
			{
				accountPage2.OpenAddAccountMenu();
			}
		});
	}

    public void OpenInstalledInstance(string id, string root) => OpenInstanceSettings(id, root);

	private async void OpenInstanceSettings(string id, string root = "")
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return;
		}
		if (!(ContentFrame.Content is VersionsPage))
		{
			Navigate("versions");
		}
		for (int i = 0; i < 20; i++)
		{
			if (ContentFrame.Content is VersionsPage)
			{
				break;
			}
			await Task.Delay(50);
		}
		object content = ContentFrame.Content;
		if (content is VersionsPage page)
		{
			await page.OpenVersionAsync(id, "", root);
		}
	}

	private void GoBack()
	{
		if (_navHistory.Count == 0)
		{
			return;
		}
		List<NavEntry> navHistory = _navHistory;
		NavEntry navEntry = navHistory[navHistory.Count - 1];
		_navHistory.RemoveAt(_navHistory.Count - 1);
		if (navEntry.Back is not null)
		{
			// 子页（下载版本 / Mod 详情）的返回：只关子页，不换整页。
			navEntry.Back();
			UpdateBackButton();
			return;
		}
		_restoringHistory = true;
		try
		{
			if (navEntry.Tag == "logs")
			{
				_pendingLogsFile = navEntry.LogsFile;
			}
			else if (navEntry.Tag == "settings")
			{
				_pendingSettingsFocus = navEntry.SettingsFocus;
			}
			else if (navEntry.Tag == "online")
			{
				_pendingOnlineQuery = navEntry.OnlineQuery;
			}
			Navigate(navEntry.Tag, recordHistory: false);
		}
		finally
		{
			_restoringHistory = false;
		}
	}

	private void Back_Click(object sender, RoutedEventArgs e)
	{
		GoBack();
	}

	private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = true;
		GoBack();
	}

	private void UpdateBackButton(bool onboarding = false)
	{
		if ((object)BackButton != null)
		{
			BackButton.Visibility = !onboarding && _navHistory.Count > 0 && _navHistory[^1].Back is null
				? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private static int SectionOrder(string tag)
	{
		if (1 == 0)
		{
		}
		int result = ((!(tag == "home")) ? 1 : 0);
		if (1 == 0)
		{
		}
		return result;
	}

	private void Nav_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleButton { Tag: string tag })
		{
			Navigate(tag, recordHistory: false, clearHistory: true);
		}
	}

	private void AccountButton_Click(object sender, RoutedEventArgs e)
	{
		Navigate("account", recordHistory: false, clearHistory: true);
	}

	private void TaskFlyout_Opening(object sender, object e)
	{
		BuildTaskFlyout();
	}

	/// <summary>
	/// 展开动画：先淡入标题与摘要，再逐行淡入正文里的每一条文字。
	/// 只动文字本身（TextBlock）与底部按钮——若同时动外层容器会叠成两层淡入，文字会显得慢半拍。
	/// </summary>
	private void TaskFlyout_Opened(object? sender, object e)
	{
		if ((object)TaskFlyoutRoot == null)
		{
			return;
		}
		TaskFlyoutRoot.UpdateLayout();

		// 本体先动：从胶囊方向轻微上浮 + 由 0.97 放大到 1，只动几何不动透明度，
		// 这样下面的逐条文字仍然是自己淡入，不会叠成两层。
		var rootVisual = ElementCompositionPreview.GetElementVisual(TaskFlyoutRoot);
		var rootCompositor = rootVisual.Compositor;
		rootVisual.CenterPoint = new System.Numerics.Vector3(
			(float)TaskFlyoutRoot.ActualWidth / 2f, (float)TaskFlyoutRoot.ActualHeight, 0f);

		var rise = rootCompositor.CreateVector3KeyFrameAnimation();
		rise.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, 10f, 0f));
		rise.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
		rise.Duration = TimeSpan.FromMilliseconds(240);
		rise.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

		var zoom = rootCompositor.CreateVector3KeyFrameAnimation();
		zoom.InsertKeyFrame(0f, new System.Numerics.Vector3(0.97f, 0.97f, 1f));
		zoom.InsertKeyFrame(1f, System.Numerics.Vector3.One);
		zoom.Duration = TimeSpan.FromMilliseconds(240);
		zoom.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

		rootVisual.StartAnimation("Offset", rise);
		rootVisual.StartAnimation("Scale", zoom);

		var texts = new List<UIElement>();
		CollectFlyoutTexts(TaskFlyoutRoot, texts);
		if (ViewAllTaskButton != null)
		{
			texts.Add(ViewAllTaskButton);
		}

		var compositor = ElementCompositionPreview.GetElementVisual(TaskFlyoutRoot).Compositor;
		for (var i = 0; i < texts.Count; i++)
		{
			var visual = ElementCompositionPreview.GetElementVisual(texts[i]);
			var delay = TimeSpan.FromMilliseconds(i * 22);

			var fade = compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(0f, 0f);
			fade.InsertKeyFrame(1f, 1f);
			fade.Duration = TimeSpan.FromMilliseconds(190);
			fade.DelayTime = delay;
			fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

			var slide = compositor.CreateVector3KeyFrameAnimation();
			slide.InsertKeyFrame(0f, new System.Numerics.Vector3(-7f, -3f, 0f));
			slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
			slide.Duration = TimeSpan.FromMilliseconds(230);
			slide.DelayTime = delay;
			slide.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

			visual.StartAnimation("Opacity", fade);
			visual.StartAnimation("Offset", slide);
		}
	}

	/// <summary>按视觉树顺序收集浮层里的文字块（标题、摘要、分组名、每行的主副文案）。</summary>
	private static void CollectFlyoutTexts(DependencyObject node, List<UIElement> into)
	{
		var count = VisualTreeHelper.GetChildrenCount(node);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(node, i);
			if (child is TextBlock block)
			{
				if (!string.IsNullOrEmpty(block.Text))
				{
					into.Add(block);
				}
				continue;
			}
			CollectFlyoutTexts(child, into);
		}
	}

	private void UpdateTaskDock()
	{
		if ((object)TaskDockText != null)
		{
			int activeCount = TaskCenter.ActiveCount;
			int unreadCount = TaskCenter.UnreadCount;
			TaskDockText.Text = ((activeCount > 0) ? $"{activeCount} 个进行中" : ((unreadCount > 0) ? $"{unreadCount} 条未读通知" : "没有正在进行的任务"));
		}
	}

	private void BuildTaskFlyout()
	{
		TaskFlyoutBody.Children.Clear();
		List<TaskEntry> list = TaskCenter.Entries.Where((TaskEntry item) => item.Status == "进行中").Take(3).ToList();
		List<TaskEntry> list2 = TaskCenter.Entries.Where((TaskEntry item) => item.Status != "进行中").Take(3).ToList();
		TaskFlyoutSummary.Text = ((list.Count == 0) ? "队列空闲" : ((list2.Count > 0) ? $"{list.Count} 个进行中 · {list2.Count} 条结束" : $"{list.Count} 个进行中"));
		if (list.Count == 0 && list2.Count == 0)
		{
			TaskFlyoutBody.Children.Add(new TextBlock
			{
				Text = "暂无消息",
				FontSize = 12.5,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 18.0, 0.0, 18.0),
				Foreground = ThemeBrush("AppText3Brush")
			});
		}
		else
		{
			AddFlyoutSection("进行中", list);
			AddFlyoutSection("最近结束", list2);
		}
	}

	private void AddFlyoutSection(string label, List<TaskEntry> items)
	{
		if (items.Count == 0)
		{
			return;
		}
		if (TaskFlyoutBody.Children.Count > 0)
		{
			TaskFlyoutBody.Children.Add(new Border
			{
				Height = 6.0
			});
		}
		TaskFlyoutBody.Children.Add(new TextBlock
		{
			Text = label,
			FontSize = 11.5,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
			Foreground = ThemeBrush("AppText3Brush")
		});
		foreach (TaskEntry item in items)
		{
			TaskFlyoutBody.Children.Add(BuildFlyoutRow(item));
		}
	}

	private FrameworkElement BuildFlyoutRow(TaskEntry item)
	{
		Grid grid = new Grid
		{
			Padding = new Thickness(0.0, 7.0, 0.0, 7.0),
			ColumnSpacing = 10.0
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(26.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Border border = new Border
		{
			Width = 26.0,
			Height = 26.0,
			CornerRadius = new CornerRadius(13.0),
			Background = ThemeBrush("TintTasksSoftBrush"),
			VerticalAlignment = VerticalAlignment.Top
		};
		border.Child = new FontIcon
		{
			Glyph = TaskVisuals.IconFor(item.Title),
			FontSize = 13.0,
			Foreground = ThemeBrush("TintTasksBrush")
		};
		grid.Children.Add(border);
		StackPanel stackPanel = new StackPanel
		{
			Spacing = 1.0
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = item.Title,
			FontSize = 13.0,
			MaxLines = 1,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Foreground = ThemeBrush("AppTextBrush")
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = TaskVisuals.PanelStatusText(item),
			FontSize = 11.5,
			MaxLines = 1,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Foreground = (item.IsActive ? ThemeBrush("TintTasksBrush") : TaskVisuals.StatusBrush(item.Status))
		});
		Grid.SetColumn(stackPanel, 1);
		grid.Children.Add(stackPanel);
		TextBlock textBlock = new TextBlock
		{
			Text = RelativeTime(item.TimeText),
			FontSize = 11.5,
			VerticalAlignment = VerticalAlignment.Top,
			Foreground = ThemeBrush("AppText3Brush")
		};
		Grid.SetColumn(textBlock, 2);
		grid.Children.Add(textBlock);
		return grid;
	}

	private static Brush ThemeBrush(string key)
	{
		return (Brush)Application.Current.Resources[key];
	}

	private static string RelativeTime(string stamp)
	{
		if (!DateTime.TryParseExact(stamp, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
		{
			return stamp;
		}
		DateTime dateTime = DateTime.Today.Add(result.TimeOfDay);
		if (dateTime > DateTime.Now)
		{
			dateTime = dateTime.AddDays(-1.0);
		}
		TimeSpan timeSpan = DateTime.Now - dateTime;
		if (timeSpan.TotalMinutes < 1.0)
		{
			return "刚刚";
		}
		if (timeSpan.TotalMinutes < 60.0)
		{
			return $"{(int)timeSpan.TotalMinutes} 分钟前";
		}
		if (timeSpan.TotalHours < 24.0)
		{
			return $"{(int)timeSpan.TotalHours} 小时前";
		}
		return dateTime.ToString("M月d日");
	}

	private void ViewAllTasks_Click(object sender, RoutedEventArgs e)
	{
		TaskFlyout.Hide();
		Navigate("tasks");
	}

	private void ThemeButton_Click(object sender, RoutedEventArgs e)
	{
		ApplyTheme(!_dark, persist: true);
	}

	private void ApplyTitleBarTheme(bool dark)
	{
		AppWindowTitleBar titleBar = base.AppWindow.TitleBar;
		titleBar.ButtonBackgroundColor = Colors.Transparent;
		titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
		titleBar.ButtonForegroundColor = (dark ? Colors.White : Colors.Black);
		titleBar.ButtonInactiveForegroundColor = (dark ? Color.FromArgb(160, byte.MaxValue, byte.MaxValue, byte.MaxValue) : Color.FromArgb(160, 0, 0, 0));
		titleBar.ButtonHoverBackgroundColor = (dark ? Color.FromArgb(28, byte.MaxValue, byte.MaxValue, byte.MaxValue) : Color.FromArgb(20, 0, 0, 0));
		titleBar.ButtonHoverForegroundColor = (dark ? Colors.White : Colors.Black);
	}

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

	private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		ContentHost.Clip = new RectangleGeometry
		{
			Rect = new Rect(0.0, 0.0, Math.Max(1.0, e.NewSize.Width), Math.Max(1.0, e.NewSize.Height))
		};
	}
}
