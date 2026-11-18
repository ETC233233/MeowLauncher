using System.Diagnostics;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 设置页（契约 5）：七组设置全部自动保存，没有保存按钮、没有只读路径陈列卡。
/// 立即生效的偏好（主题/材质/遮罩/不透明度/字体/窗口）直接写 app.json；
/// 走后端的组（启动默认/下载与网络）走 650ms 去抖，状态统一显示在页头。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private bool _loaded;

    /// <summary>页面在可视树里时为 true。异步读取回来时如果页面已经被切走（多次点击设置会连续新建页面），就不再碰它的控件。</summary>
    private bool _alive;
    private bool _suppress;
    private string _focus = "";
    private string _section = "launch";

    private DispatcherTimer? _appearanceTimer;
    private DispatcherTimer? _launchTimer;
    private DispatcherTimer? _networkTimer;
    private DispatcherTimer? _statusTimer;


    /// <summary>CurseForge Key 已从 UI 移除；仅缓存后端现有值，保存时原样回传，避免把用户已有的 Key 清空。</summary>
    private string _cfApiKey = "";
    private string _legacyGlobalJavaPath = "";
    private string _savedGameMirror = "";
    private string _savedResourceSource = "priority:modrinth,curseforge";

    public SettingsPage()
    {
        App.Trace("Settings ctor: before InitializeComponent");
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        App.Trace("Settings ctor: after InitializeComponent");
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _alive = false;
            CancelSectionEntrance();
            UpdateService.Changed -= OnUpdateChanged;
            OnlineComponent.UpdateAvailable -= OnUpdateChanged;
            _wallpaperEffectsTimer?.Stop();
            FlushPending();
        };
        ExtraPanel.Changed += ExtraPanel_Changed;

    }

    // ---- 启动附加设置（DESIGN-1110 第 4 项，scope=global）
    private bool _savingExtra;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _extraTimer;

    private void ExtraPanel_Changed()
    {
        if (_extraTimer is null)
        {
            _extraTimer = DispatcherQueue.CreateTimer();
            _extraTimer.Interval = TimeSpan.FromMilliseconds(700);
            _extraTimer.IsRepeating = false;
            _extraTimer.Tick += (_, _) => _ = FlushExtraAsync();
        }
        _extraTimer.Stop();
        _extraTimer.Start();
    }

    private async Task FlushExtraAsync()
    {
        if (_savingExtra) return;
        _savingExtra = true;
        try
        {
            await App.Backend.SetLaunchExtraAsync("global", ExtraPanel.Read());
        }
        catch
        {
            // 静默：写失败不该打断设置页
        }
        finally
        {
            _savingExtra = false;
        }
    }

    private async Task LoadExtraAsync()
    {
        try
        {
            App.Trace("LoadExtra: start");
            var r = await App.Backend.GetLaunchExtraAsync("global");
            if (!_alive) return;
            if (r.Ok) ExtraPanel.Load(r.Extra);
        }
        catch
        {
            // 读不到就保持默认值
        }
    }

    /// <summary>关于卡：版本取程序集（csproj 里的 &lt;Version&gt;），运行环境取运行时版本。</summary>
    private void FillAboutInfo()
    {
        var cur = BuildInfo.Current;
        AboutRuntimeText.Text = $"运行环境 .NET {Environment.Version.Major}";
        AboutSummaryText.Text = $"Minecraft 启动器 · {cur.ChannelName}";
        // 正式版多一行「功能版本」（它晋升前的开发构建号）；开发版 / 测试版常驻提示
        AboutBuildText.Text = cur.Channel == MeowChannel.Release ? $"功能版本 {cur.FunctionVersion}" : "";
        AboutBuildText.Visibility = AboutBuildText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AboutNoticeText.Text = cur.NoticeText;
        AboutNoticeText.Visibility = AboutNoticeText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _alive = true;
        UpdateService.Changed += OnUpdateChanged;
        OnlineComponent.UpdateAvailable += OnUpdateChanged;
        if (_loaded)
        {
            RefreshPreferences();
            ShowSection(_section);
            _ = LoadAllAsync();
            return;
        }
        _loaded = true;
        App.Trace("Settings OnLoaded: start");
        FillAboutInfo();
        App.Trace("Settings OnLoaded: FillAboutInfo ok");
        _ = LoadExtraAsync();
        SyncAppearance();
        App.Trace("Settings OnLoaded: SyncAppearance ok");
        SyncNotifyAndUpdate();
        SyncHomeStartScreen();
        _ = SyncDevAsync();
        App.Trace("Settings OnLoaded: SyncNotify/HomeStart ok");
        _ready = true;
        UpdateValueTexts();
        App.Trace("Settings OnLoaded: UpdateValueTexts ok");
        ShowSection(_section);
        App.Trace("Settings OnLoaded: ShowSection ok");
        _ = LoadAllAsync();
        App.Trace("Settings OnLoaded: end");
    }

    public void RefreshPreferences()
    {
        if (!_alive) return;
        SyncAppearance();
        SyncNotifyAndUpdate();
        SyncHomeStartScreen();
        UpdateValueTexts();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Trace("Settings OnNavigatedTo param=" + e.Parameter);
        if (e.Parameter is string focus) _focus = focus;
        DispatcherQueue.TryEnqueue(() => FocusSection(_focus));
    }

    /// <summary>搜索跳转：切到对应分类并把焦点放到目标控件上（theme/background/network/about/launch/window）。</summary>
    public void FocusSection(string focus)
    {
        App.Trace("Settings FocusSection(" + focus + ")");
        FrameworkElement? target = focus switch
        {
            "theme" => ThemeBox,
            "themecolor" => ThemeColorBox,
            "background" => BackgroundBox,
            "veil" => BackgroundThemeBox,
            "opacity" => OpacitySlider,
            "fontscale" => FontScaleSlider,
            "network" => DownloadSourceBox,
            "mcim" => ResourceMirrorBox,
            "proxy" => ProxyModeBox,
            "afterlaunch" => AfterLaunchBox,
            "mirror" => GameDownloadSourceBox,
            "threads" => DownloadThreadsBox,
            "memory" => MemoryBox,
            "javamanage" => ManageJavaButton,
            "launch" => JvmBox,
            "startscreen" => HomeStartScreenBox,
            "window" => AutoSizeChip,
            "remember" => RememberWindowSwitch,
            "hidden" => HiddenList,
            "notifydone" => NotifyTaskDoneSwitch,
            "taskisland" => ShowTaskIslandSwitch,
            "updatechannel" => UpdateChannelBox,
            "checkupdate" => CheckUpdatesSwitch,
            "about" => AboutCard,
            _ => null,
        };
        // 焦点 key 与分类的兜底映射：目录里没注册的 key 也要能落到正确分类，
        // 否则「跳到某行」只会把窗口停在默认分类上。
        var section = focus switch
        {
            "afterlaunch" or "java" or "memory" or "javamanage" => "java",
            "network" or "proxy" or "mirror" or "threads" => "network",
            "notifydone" or "taskisland" => "notify",
            "updatechannel" or "checkupdate" => "update",
            "hidden" => "launch",
            "about" => "about",
            _ => "",
        };
        if (section.Length == 0)
            section = SettingsCatalog.All.FirstOrDefault(s => s.Focus == focus)?.Section ?? "";
        if (section.Length > 0) ShowSection(section);
        App.Trace("Settings FocusSection: section=" + section + " target=" + (target?.Name ?? "null"));
        if (target is null) return;
        // 分类刚显示、布局还没跑，等一拍再滚动并聚焦
        DispatcherQueue.TryEnqueue(() =>
        {
            App.Trace("Settings Focus: StartBringIntoView");
            target.StartBringIntoView();
            App.Trace("Settings Focus: Focus()");
            if (target is Control control) control.Focus(FocusState.Programmatic);
            App.Trace("Settings Focus: done");
        });
    }

    // ------------------------------------------------------------------ //
    // 一级分类导航（顶部 5 个标签）
    // ------------------------------------------------------------------ //
    private static readonly string[] SectionNames = ["launch", "appearance", "java", "network", "about", "dev"];

    // 「开发者测试」分类：通过开发者认证后才出现（第一版发布不带联机，默认隐藏）
    private bool _devUnlocked;

    // 与首页组件板同一个版心：宽 1126 时列宽 860，窗口再宽到 1440+ 才放到 960。
    private void PageRootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 首页是「内容宽 - 2×24 后再居中」，这里对齐同一个左边：页边距 24 + 版心内缩 24。
        var target = Math.Max(320, Math.Min(960, e.NewSize.Width - 96));
        if (Math.Abs(ContentColumn.Width - target) > 0.5) ContentColumn.Width = target;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        ShowSection(key);
        SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void ShowSection(string key)
    {
        key = key switch
        {
            "notify" => "launch",
            "update" or "advanced" => "about",
            _ => key,
        };
        if (!SectionNames.Contains(key)) return;
        if (key == "dev" && !_devUnlocked) return;   // 没认证：这个分类不存在
        CancelSectionEntrance();
        if (CardFor(_section) is { } outgoing) Motion.AttachLayout(outgoing, false);
        foreach (var panel in SectionMotionPanels(_section)) Motion.AttachVisibility(panel, false);
        _section = key;
        if (key == "network") ResourceMirrorBox.SelectedIndex = McimClient.PreferOfficial ? 1 : 0;
        foreach (var name in SectionNames)
        {
            if (CardFor(name) is { } card)
                card.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;
        }
        var isGeneral = key == "launch";
        HiddenVersionsCard.Visibility = NotifyCard.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavSelected();
        if (CardFor(key) is { } shown) FadeIn(shown);
    }

    private FrameworkElement[] SectionMotionPanels(string key) => key switch
    {
        "appearance" => new FrameworkElement[] { AcrylicKindRow, WallpaperEffectsPanel, BackgroundImageRow, CustomSizeRow },
        "network" => new FrameworkElement[] { GithubAccelHostRow, ProxyCustomRow, ProxyAuthRow },
        "dev" => new FrameworkElement[] { DevFeaturesPanel },
        _ => Array.Empty<FrameworkElement>(),
    };

    private FrameworkElement? CardFor(string key) => key switch
    {
        "launch" => LaunchCard,
        "appearance" => AppearanceCard,
        "java" => JavaCard,
        "network" => NetworkCard,
        "dev" => DevCard,
        "about" => AboutCard,
        _ => null,
    };

    private Button? NavFor(string key) => key switch
    {
        "launch" => NavLaunch,
        "appearance" => NavAppearance,
        "java" => NavJava,
        "network" => NavNetwork,
        "dev" => NavDev,
        "about" => NavAbout,
        _ => null,
    };

    private void UpdateNavSelected()
    {
        foreach (var name in SectionNames)
        {
            var on = name == _section;
            if (NavFor(name) is not { } button) continue;
            button.Background = on
                ? (Brush)Application.Current.Resources["AppNavSelectedBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (button.Content is not StackPanel content) continue;
            foreach (var child in content.Children)
            {
                if (child is FontIcon icon) icon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (child is TextBlock text) text.Opacity = on ? 1 : 0.6;
            }
        }
    }

    private Storyboard? _sectionEntranceAnimation;
    private EventHandler<object>? _sectionRender;
    private FrameworkElement? _enteringSection;
    private int _sectionEntranceVersion;

    private void CancelSectionEntrance()
    {
        _sectionEntranceVersion++;
        _sectionEntranceAnimation?.Stop();
        _sectionEntranceAnimation = null;
        if (_sectionRender is not null)
        {
            CompositionTarget.Rendering -= _sectionRender;
            _sectionRender = null;
        }
        if (_enteringSection is { } previous)
        {
            previous.Opacity = 1;
            if (previous.RenderTransform is TranslateTransform shift) shift.Y = 0;
        }
        _enteringSection = null;
    }

    /// <summary>等布局与首帧完成后才启动，避免冷初始化耗尽动画时钟。</summary>
    private void FadeIn(FrameworkElement element)
    {
        CancelSectionEntrance();
        if (!Motion.Enabled) { element.Opacity = 1; return; }
        var version = _sectionEntranceVersion;
        var elapsed = Stopwatch.StartNew();
        var frames = 0;
        _enteringSection = element;
        element.Opacity = 0;
        _sectionRender = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= _sectionRender;
            _sectionRender = null;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_alive || version != _sectionEntranceVersion || element.Visibility != Visibility.Visible) return;
                // Initial layout has settled. Only now enable movement of
                // neighbours; otherwise first-load positions animate from zero.
                Motion.AttachLayout(element);
                foreach (var panel in SectionMotionPanels(_section)) Motion.AttachVisibility(panel);
                if (Motion.Enabled) Motion.Enter(element, fromY: 8, durationMs: 167, onCreated: animation =>
                {
                    _sectionEntranceAnimation = animation;
                    animation.Completed += (_, _) =>
                    {
                        if (version != _sectionEntranceVersion) return;
                        animation.Stop();
                        element.Opacity = 1;
                        if (element.RenderTransform is TranslateTransform shift) shift.Y = 0;
                        _sectionEntranceAnimation = null;
                        _enteringSection = null;
                    };
                });
                else { element.Opacity = 1; _enteringSection = null; }
                App.Trace($"Settings entrance: after layout/render {elapsed.ElapsedMilliseconds}ms, section={_section}");
            });
        };
        CompositionTarget.Rendering += _sectionRender;
    }

    // ------------------------------------------------------------------ //
    // 加载
    // ------------------------------------------------------------------ //
    private async Task LoadAllAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 读取期间在页头右上角给一句提示（读得很快就不会闪：延迟 0.3 秒才出现，读完立即撤掉）
        var loadDone = false;
        var hintShown = false;
        _ = Task.Delay(300).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (_alive && !loadDone) { AutoSaveStatus.Text = "正在读取设置…"; AutoSaveBox.Visibility = Visibility.Visible; hintShown = true; }
        }));
        try { await Task.WhenAll(LoadGeneralAsync(), LoadLaunchAsync()); }
        finally
        {
            loadDone = true;
            if (hintShown) { AutoSaveStatus.Text = ""; AutoSaveBox.Visibility = Visibility.Collapsed; }
        }
        App.Trace($"Settings load: general+launch done in {watch.ElapsedMilliseconds} ms (alive={_alive})");
        if (!_alive) return;
        await LoadHiddenAsync();
        App.Trace($"Settings load: hidden versions done at {watch.ElapsedMilliseconds} ms (alive={_alive})");
    }

    private void SyncAppearance()
    {
        var main = App.MainWindow;
        if (main is null) return;
        var prefs = main.Preferences;

        _suppress = true;
        ThemeBox.SelectedIndex = main.ThemeMode switch
        {
            var mode when string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) => 2,
            var mode when string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 0,
        };
        App.Trace("Sync#1");
        ThemeBox.IsEnabled = main.SystemThemeSupported;
        App.Trace("Sync#2");
        ToolTipService.SetToolTip(ThemeBox, main.SystemThemeSupported
            ? "跟随系统时会随 Windows 的深浅色模式切换"
            : "当前 Windows 版本没有深浅色应用模式，主题固定跟随系统。");
        BackgroundBox.SelectedIndex = BackgroundIndex(prefs.EffectiveWindowMaterial);
        AcrylicKindBox.SelectedIndex = string.Equals(prefs.EffectiveWindowMaterial, "AcrylicThin", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        BackgroundThemeBox.SelectedIndex = BackgroundThemeIndex(prefs.EffectiveBackgroundTheme);
        WallpaperBlurSlider.Value = Math.Clamp(prefs.WallpaperBlur, 0, 40);
        WallpaperShadeSlider.Value = Math.Clamp(prefs.WallpaperShade, 0, 0.8);
        UpdateWallpaperEffectLabels();
        SyncBackgroundControls();
        BackgroundImageValue.Text = ImageLabel(prefs.BackgroundImage);
        App.Trace("Sync#4");
        App.Trace("Sync#5");
        OpacitySlider.Value = Math.Clamp(prefs.UiOpacity, 0.5, 1);
        App.Trace("Sync#6");
        FontScaleSlider.Value = Math.Clamp(prefs.FontScale, 0.85, 1.25);
        App.Trace("Sync#7");
        ThemeColorBox.SelectedIndex = ThemeColorIndex(prefs.ThemeColorMode);
        App.Trace("Sync#8");
        UpdateThemeColorSwatch();
        App.Trace("Sync#9");

        var (width, height) = (prefs.WindowWidth > 0 ? prefs.WindowWidth : 880,
            prefs.WindowHeight > 0 ? prefs.WindowHeight : 620);
        AutoSizeChip.IsChecked = prefs.AutoWindowSize;
        App.Trace("Sync#10");
        FixedSizeChip.IsChecked = !prefs.AutoWindowSize;
        App.Trace("Sync#11");
        CustomSizeRow.Visibility = prefs.AutoWindowSize ? Visibility.Collapsed : Visibility.Visible;
        App.Trace("Sync#12");
        WindowWidthBox.Value = Math.Max(880, width);
        App.Trace("Sync#13");
        WindowHeightBox.Value = Math.Max(620, height);
        App.Trace("Sync#14");
        RememberWindowSwitch.IsOn = prefs.RememberWindowSize;
        IgnoreDpiSwitch.IsOn = prefs.IgnoreSystemDpi;
        GithubAccelSwitch.IsOn = prefs.GithubAccel;
        GithubAccelHostBox.Text = prefs.GithubAccelHost;
        GithubAccelHostRow.Visibility = prefs.GithubAccel ? Visibility.Visible : Visibility.Collapsed;
        SyncUrlAccel(prefs);
        App.Trace("Sync#15");
        _suppress = false;
    }

    private static int BackgroundIndex(string mode) => mode switch
    {
        "Mica" => 1,
        "Solid" => 2,
        _ => 0,
    };

    private static int BackgroundThemeIndex(string theme) => theme switch
    {
        "PixelWallpaper" => 1,
        "Wallpaper" => 2,
        "Image" => 3,
        _ => 0,
    };

    private void SyncBackgroundControls()
    {
        var background = SelectedTag(BackgroundThemeBox);
        var useMaterial = background == "None";
        BackgroundBox.IsEnabled = useMaterial;
        BackgroundThemeBox.IsEnabled = SelectedTag(BackgroundBox) == "Solid";
        AcrylicKindRow.Visibility = useMaterial && SelectedTag(BackgroundBox) == "Acrylic" ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageRow.Visibility = background == "Image" ? Visibility.Visible : Visibility.Collapsed;
        WallpaperEffectsPanel.Visibility = background is "Wallpaper" or "Image" ? Visibility.Visible : Visibility.Collapsed;
    }

    private DispatcherTimer? _wallpaperEffectsTimer;

    private void UpdateWallpaperEffectLabels()
    {
        WallpaperBlurValue.Text = $"{WallpaperBlurSlider.Value:0}";
        WallpaperShadeValue.Text = $"{WallpaperShadeSlider.Value:P0}";
    }

    private void WallpaperEffects_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress || !_ready || App.MainWindow is not { } main) return;
        UpdateWallpaperEffectLabels();
        main.Preferences.WallpaperBlur = Math.Round(WallpaperBlurSlider.Value);
        main.Preferences.WallpaperShade = Math.Round(WallpaperShadeSlider.Value, 2);
        _wallpaperEffectsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _wallpaperEffectsTimer.Tick -= ApplyWallpaperEffects;
        _wallpaperEffectsTimer.Tick += ApplyWallpaperEffects;
        _wallpaperEffectsTimer.Stop();
        _wallpaperEffectsTimer.Start();
        ArmAppearanceSave();
    }

    private void ApplyWallpaperEffects(object? sender, object e)
    {
        _wallpaperEffectsTimer?.Stop();
        App.MainWindow?.RefreshAppearance(false);
    }

    private static string ImageLabel(string path) =>
        string.IsNullOrWhiteSpace(path) ? "未选择" : System.IO.Path.GetFileName(path);

    private async Task LoadLaunchAsync()
    {
        var launch = await App.Backend.GetLaunchDefaultsAsync();
        if (!_alive) return;
        if (!launch.Ok)
        {
            LaunchStatusText.Text = string.IsNullOrWhiteSpace(launch.Message) ? "读取启动默认失败。" : launch.Message;
            return;
        }

        _suppress = true;
        MemoryBox.Value = Math.Clamp(launch.Mem > 0 ? launch.Mem : 4096, 1024, 65536);
        MemAutoSwitch.IsOn = launch.MemAuto;
        MemoryBox.IsEnabled = !launch.MemAuto;
        JvmBox.Text = launch.Jvm;
        _suppress = false;
        LaunchStatusText.Text = "";
    }

    private async Task LoadGeneralAsync()
    {
        var general = await App.Backend.GetGeneralSettingsAsync();
        if (!_alive) return;
        if (!general.Ok)
        {
            NetworkStatusText.Text = string.IsNullOrWhiteSpace(general.Message)
                ? "读取下载与网络设置失败。"
                : general.Message;
            return;
        }

        _suppress = true;
        _savedResourceSource = general.DownloadSource;
        DownloadSourceBox.SelectedIndex = OnlinePage.ResourceSourceIndex(_savedResourceSource);
        LoadProxyFromPreferences();
        LoadAfterLaunch();
        var sourceIndex = DownloadPage.GameMirrorIndex(general.Mirror);
        _savedGameMirror = general.Mirror;
        MirrorBox.Text = sourceIndex == 2 ? general.Mirror : "";
        GameDownloadSourceBox.SelectedIndex = sourceIndex;
        GameMirrorRow.Visibility = sourceIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        GameMirrorNotice.Visibility = DownloadPage.IsGitHubMirror(general.Mirror)
            ? Visibility.Visible : Visibility.Collapsed;
        _cfApiKey = general.CfApiKey ?? "";
        DownloadThreadsBox.Value = Math.Clamp(general.DlThreads, 1, 32);
        DownloadParallelBox.Value = Math.Clamp(App.MainWindow?.Preferences?.DownloadParallel ?? 8, 1, 16);
        _suppress = false;
        _legacyGlobalJavaPath = general.JavaPath;
        NetworkStatusText.Text = "";
    }

    private void SelectTag(ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private async Task LoadHiddenAsync()
    {
        var hidden = await App.Backend.GetHiddenVersionsAsync();
        if (!_alive) return;
        if (hidden is null || !hidden.Ok)
        {
            HiddenList.ItemsSource = null;
            HiddenEmpty.Title = "已隐藏版本读取失败";
            HiddenEmpty.Hint = string.IsNullOrWhiteSpace(hidden?.Message) ? "稍后重试。" : hidden!.Message;
            HiddenEmpty.Visibility = Visibility.Visible;
            HiddenCountValue.Text = "读取失败";
            return;
        }

        HiddenEmpty.Title = "没有已隐藏的版本";
        HiddenEmpty.Hint = "";
        HiddenList.ItemsSource = hidden.Versions;
        HiddenEmpty.Visibility = hidden.Versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenCountValue.Text = $"{hidden.Versions.Count} 个";
    }

    // ------------------------------------------------------------------ //
    // 右侧当前值
    // ------------------------------------------------------------------ //
    /// <summary>启动器首页形态（设置里只暴露「打开时显示哪一屏」）。</summary>
    private void SyncHomeStartScreen()
    {
        var prefs = App.MainWindow?.Preferences;
        var start = (prefs?.HomeStartScreen ?? "continue").Trim().ToLowerInvariant();
        _suppress = true;
        try
        {
            HomeStartScreenBox.SelectedIndex = start == "board" ? 1 : 0;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void HomeStartScreen_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        var tag = (HomeStartScreenBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "continue";
        prefs.HomeStartScreen = tag == "board" ? "board" : "continue";
        AppPreferencesStore.Save(prefs);
        HomePage.NotifyLayoutPreferenceChanged();
        MarkSaved();
    }

    private void UpdateValueTexts()
    {
        UpdateAppearanceTexts();
        UpdateWindowTexts();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppPreferencesStore.FolderPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AutoSaveStatus.Text = $"打不开配置目录：{Diagnostics.FriendlyMessage(ex)}";
            AutoSaveBox.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------ //
    // 主题色：系统强调色 / 壁纸取色（莫奈）/ 自定义色
    // ------------------------------------------------------------------ //
    private static int ThemeColorIndex(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "accent" => 1,
        "custom" => 2,
        _ => 0,
    };

    private void UpdateThemeColorSwatch()
    {
        var color = ThemeColorService.Current;
        ThemeColorSwatch.Background = new SolidColorBrush(color);
        ThemeColorValueText.Text = ThemeColorService.CurrentHex;
        ToolTipService.SetToolTip(ThemeColorSwatch, $"当前主题色 {ThemeColorService.CurrentHex}，点击更换");
    }

    private async void ThemeColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var main = App.MainWindow;
        if (main is null) return;
        main.Preferences.ThemeColorMode = SelectedTag(ThemeColorBox);
        await main.RefreshThemeColorAsync();
        UpdateThemeColorSwatch();
        ArmAppearanceSave();
    }

    private void ThemeColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new ColorPicker
        {
            Color = ThemeColorService.Current,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            MinWidth = 320,
        };
        picker.ColorChanged += (_, args) => ApplyCustomThemeColor(args.NewColor);
        var flyout = new Flyout { Content = picker };
        flyout.ShowAt(ThemeColorSwatch);
    }

    /// <summary>取色器里拖动时即时换色；松手后的落盘交给去抖，避免每帧写文件。</summary>
    private void ApplyCustomThemeColor(Color color)
    {
        var main = App.MainWindow;
        if (main is null) return;
        main.Preferences.ThemeColorMode = ThemeColorService.ModeCustom;
        main.Preferences.ThemeColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ThemeColorService.Apply(color, main.IsDarkTheme);
        _suppress = true;
        ThemeColorBox.SelectedIndex = 2;
        _suppress = false;
        UpdateThemeColorSwatch();
        ArmAppearanceSave();
    }

    private void UpdateAppearanceTexts()
    {
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
        FontScaleValueText.Text = $"{FontScaleSlider.Value * 100:0}%";
    }

    private void UpdateWindowTexts()
    {
        // 窗口尺寸不再在行尾重复显示：下拉本身带预设数值，自定义尺寸下方的两个输入框就是真值。
    }



    // ------------------------------------------------------------------ //
    // 保存状态（页头唯一状态位）
    // ------------------------------------------------------------------ //
    private void MarkSaved()
    {
        AutoSaveStatus.Text = $"已自动保存 {DateTime.Now:HH:mm:ss}";
        AutoSaveBox.Visibility = Visibility.Visible;
        _statusTimer ??= CreateTimer(TimeSpan.FromSeconds(4), () =>
        {
            AutoSaveStatus.Text = "";
            AutoSaveBox.Visibility = Visibility.Collapsed;      // 平时不占位：只在刚保存后的几秒里显示时间
        });
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private void FlushPending()
    {
        if (!_ready) return;
        if (_launchTimer is { IsEnabled: true })
        {
            _launchTimer.Stop();
            _ = SaveLaunchAsync();
        }
        if (_networkTimer is { IsEnabled: true })
        {
            _networkTimer.Stop();
            _ = SaveNetworkAsync();
        }
        if (_appearanceTimer is { IsEnabled: true })
        {
            _appearanceTimer.Stop();
            App.MainWindow?.RefreshAppearance(persist: true);
        }
    }

    // ------------------------------------------------------------------ //
    // 外观 / 界面 / 窗口（本地偏好，立即生效 + 短去抖落盘）
    // ------------------------------------------------------------------ //
    private void ArmAppearanceSave()
    {
        _appearanceTimer ??= CreateTimer(TimeSpan.FromMilliseconds(400), () =>
        {
            App.MainWindow?.RefreshAppearance(persist: true);
            MarkSaved();
        });
        _appearanceTimer.Stop();
        _appearanceTimer.Start();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var main = App.MainWindow;
        if (main is null) return;

        var mode = SelectedTag(ThemeBox) switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System",
        };
        main.SetThemeMode(mode, persist: true);
        if (!main.SystemThemeSupported)
        {
            _suppress = true;
            ThemeBox.SelectedIndex = 0;
            _suppress = false;
        }
        MarkSaved();
    }

    private void Background_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var mode = SelectedTag(BackgroundBox);
        if (mode == "Acrylic" && SelectedTag(AcrylicKindBox) == "thin") mode = "AcrylicThin";
        SyncBackgroundControls();
        App.MainWindow?.SetAppearanceSelection(mode, SelectedTag(BackgroundThemeBox), null, persist: true);
        MarkSaved();
    }

    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var main = App.MainWindow;
        if (main is null) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, main.WindowHandle);
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
            picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        main.SetAppearanceSelection(null, "Image", file.Path, persist: true);
        _suppress = true;
        BackgroundThemeBox.SelectedIndex = 3;
        _suppress = false;
        BackgroundImageRow.Visibility = Visibility.Visible;
        BackgroundImageValue.Text = file.Name;
        MarkSaved();
    }


    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
        App.MainWindow?.SetUiOpacity(OpacitySlider.Value, persist: false);
        ArmAppearanceSave();
    }

    private void FontScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        FontScaleValueText.Text = $"{FontScaleSlider.Value * 100:0}%";
        App.MainWindow?.SetFontScale(FontScaleSlider.Value, persist: false);
        ArmAppearanceSave();
    }

    private async void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        var main = App.MainWindow;
        if (main is null) return;

        var dialog = new ContentDialog
        {
            Title = "恢复默认外观",
            Content = "主题、窗口材质、背景主题、图片模糊与遮罩、界面不透明度和字体缩放都会回到默认值。",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var defaults = new AppPreferences();
        _suppress = true;
        ThemeBox.SelectedIndex = 0;
        BackgroundBox.SelectedIndex = 0;
        BackgroundThemeBox.SelectedIndex = 0;
        AcrylicKindBox.SelectedIndex = 0;
        BackgroundBox.IsEnabled = true;
        AcrylicKindRow.Visibility = Visibility.Visible;
        WallpaperEffectsPanel.Visibility = Visibility.Collapsed;
        WallpaperBlurSlider.Value = defaults.WallpaperBlur;
        WallpaperShadeSlider.Value = defaults.WallpaperShade;
        main.Preferences.WallpaperBlur = defaults.WallpaperBlur;
        main.Preferences.WallpaperShade = defaults.WallpaperShade;
        UpdateWallpaperEffectLabels();
        OpacitySlider.Value = defaults.UiOpacity;
        FontScaleSlider.Value = defaults.FontScale;
        ThemeColorBox.SelectedIndex = 0;
        _suppress = false;

        main.SetThemeMode(defaults.ThemeMode, persist: false);
        main.SetAppearanceSelection("Acrylic", "None", "", persist: false);
        main.SetUiOpacity(defaults.UiOpacity, persist: false);
        main.SetFontScale(defaults.FontScale, persist: false);
        main.Preferences.ThemeColorMode = defaults.ThemeColorMode;
        main.Preferences.ThemeColorHex = defaults.ThemeColorHex;
        await main.RefreshThemeColorAsync();
        UpdateThemeColorSwatch();
        main.RefreshAppearance(persist: true);

        BackgroundImageRow.Visibility = Visibility.Collapsed;
        BackgroundImageValue.Text = "未选择";
        UpdateAppearanceTexts();
        AppearanceStatusText.Text = "已恢复默认外观";
        MarkSaved();
    }

    /// <summary>窗口尺寸模式：自适应＝按工作区与 DPI 算；固定＝用下面的自定义尺寸。</summary>
    private void WindowMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        var auto = AutoSizeChip.IsChecked == true;
        CustomSizeRow.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        prefs.AutoWindowSize = auto;
        AppPreferencesStore.Save(prefs);
        if (auto) App.MainWindow?.ApplyAdaptiveWindowSize();
        UpdateWindowTexts();
    }

    private void WindowSize_Changed(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        UpdateWindowTexts();
        _appearanceTimer ??= CreateTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            var width = double.IsNaN(WindowWidthBox.Value) ? 1440 : (int)WindowWidthBox.Value;
            var height = double.IsNaN(WindowHeightBox.Value) ? 900 : (int)WindowHeightBox.Value;
            App.MainWindow?.ApplyWindowSize(width, height, persist: true);
            MarkSaved();
        });
        _appearanceTimer.Stop();
        _appearanceTimer.Start();
    }

    /// <summary>忽略系统缩放：界面按物理像素 1:1 渲染，立即生效。</summary>
    private void IgnoreDpi_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.IgnoreSystemDpi = IgnoreDpiSwitch.IsOn;
        AppPreferencesStore.Save(prefs);
        App.MainWindow?.ApplyDpiMode();
        MarkSaved();
    }

    /// <summary>GitHub 加速开关：只影响 GitHub 下载（组件包、更新），改完立即生效。</summary>
    private void GithubAccel_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.GithubAccel = GithubAccelSwitch.IsOn;
        GithubAccelHostRow.Visibility = GithubAccelSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        AppPreferencesStore.Save(prefs);
        SyncUrlAccel(prefs);
        MarkSaved();
    }

    private void GithubAccelHost_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.GithubAccelHost = GithubAccelHostBox.Text.Trim();
        AppPreferencesStore.Save(prefs);
        SyncUrlAccel(prefs);
        MarkSaved();
    }

    /// <summary>把设置里的加速开关同步给真正发请求的 UrlAccel。</summary>
    private static void SyncUrlAccel(AppPreferences prefs)
    {
        UrlAccel.Enabled = prefs.GithubAccel;
        UrlAccel.Host = string.IsNullOrWhiteSpace(prefs.GithubAccelHost) ? UrlAccel.DefaultHost : prefs.GithubAccelHost;
    }

    private void RememberWindow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        App.MainWindow!.RememberWindowSizePreference = RememberWindowSwitch.IsOn;
        UpdateWindowTexts();
        MarkSaved();
    }

    // ------------------------------------------------------------------ //
    // 启动默认（后端）
    // ------------------------------------------------------------------ //
    private void ArmLaunchSave()
    {
        _launchTimer ??= CreateTimer(TimeSpan.FromMilliseconds(650), () => _ = SaveLaunchAsync());
        _launchTimer.Stop();
        _launchTimer.Start();
    }

    private async void ManageJava_Click(object sender, RoutedEventArgs e) =>
        await JavaGuide.ManageRuntimesAsync();

    private void LaunchMemory_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        ArmLaunchSave();
    }

    private void LaunchJvm_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        ArmLaunchSave();
    }

    private void LaunchField_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        MemoryBox.IsEnabled = !MemAutoSwitch.IsOn;
        ArmLaunchSave();
    }

    private async Task SaveLaunchAsync()
    {
        if (!_ready) return;
        var mem = double.IsNaN(MemoryBox.Value) ? 4096 : (int)Math.Round(MemoryBox.Value);
        var auto = MemAutoSwitch.IsOn;
        var jvm = JvmBox.Text.Trim();
        var result = await App.Backend.SetLaunchDefaultsAsync(mem, auto, jvm);
        if (!result.Ok)
        {
            LaunchStatusText.Text = string.IsNullOrWhiteSpace(result.Message) ? "保存失败。" : result.Message;
            return;
        }

        LaunchStatusText.Text = "";
        MarkSaved();
    }

    private async void ResetLaunch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "恢复默认启动参数",
            Content = "内存上限、自动估算与额外 JVM 参数都会回到默认值。",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _suppress = true;
        MemoryBox.Value = 4096;
        MemAutoSwitch.IsOn = false;
        MemoryBox.IsEnabled = true;
        JvmBox.Text = "";
        _suppress = false;
        await SaveLaunchAsync();
        LaunchStatusText.Text = "已恢复默认启动参数";
    }

    // ------------------------------------------------------------------ //
    // 下载与网络（后端）
    // ------------------------------------------------------------------ //
    private void ArmNetworkSave()
    {
        _networkTimer ??= CreateTimer(TimeSpan.FromMilliseconds(650), () => _ = SaveNetworkAsync());
        _networkTimer.Stop();
        _networkTimer.Start();
    }

    private void DownloadSource_Changed(object sender, SelectionChangedEventArgs e) => NetworkEdited();

    private void ResourceMirror_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceMirrorBox.SelectedIndex is not (0 or 1)) return;
        if (McimClient.SetPreferOfficial(ResourceMirrorBox.SelectedIndex == 1)) MarkSaved();
    }

    private void GameDownloadSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        GameMirrorRow.Visibility = GameDownloadSourceBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        GameMirrorNotice.Visibility = Visibility.Collapsed;
        NetworkEdited();
    }

    private void GameMirror_Changed(object sender, TextChangedEventArgs e) => NetworkEdited();

    private void NetworkThreads_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => NetworkEdited();

    private void DownloadParallel_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        var value = double.IsNaN(sender.Value) ? 8 : (int)Math.Round(sender.Value);
        prefs.DownloadParallel = Math.Clamp(value, 1, 16);
        AppPreferencesStore.Save(prefs);
        ApplyDownloadParallel(prefs.DownloadParallel);
        MarkSaved();
    }

    /// <summary>并发数靠环境变量传给 sidecar 子进程（ml2/net.py 的 _proc_count 读 MEOW_DL_PROCS）。</summary>
    internal static void ApplyDownloadParallel(int parallel)
        => Environment.SetEnvironmentVariable("MEOW_DL_PROCS", Math.Clamp(parallel, 1, 16).ToString());

    private void NetworkEdited()
    {
        if (_suppress || !_ready) return;
        ArmNetworkSave();
    }

    private void LoadAfterLaunch()
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        _suppress = true;
        SelectTag(AfterLaunchBox, string.IsNullOrWhiteSpace(prefs.AfterLaunch) ? "keep" : prefs.AfterLaunch);
        _suppress = false;
    }

    private void AfterLaunch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.AfterLaunch = string.IsNullOrWhiteSpace(SelectedTag(AfterLaunchBox)) ? "keep" : SelectedTag(AfterLaunchBox);
        AppPreferencesStore.Save(prefs);
        MarkSaved();
    }

    private void Proxy_Changed(object sender, SelectionChangedEventArgs e) => SaveProxy();

    private void ProxyText_Changed(object sender, TextChangedEventArgs e) => SaveProxy();

    private void ProxyPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => SaveProxy();

    private void ProxyUser_Changed(object sender, TextChangedEventArgs e) => SaveProxy();

    private void ProxyPass_Changed(object sender, RoutedEventArgs e) => SaveProxy();

    private void LoadProxyFromPreferences()
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        _suppress = true;
        SelectTag(ProxyModeBox, string.IsNullOrWhiteSpace(prefs.ProxyMode) ? "system" : prefs.ProxyMode);
        ProxyHostBox.Text = prefs.ProxyHost ?? "";
        ProxyPortBox.Value = prefs.ProxyPort <= 0 ? 7890 : prefs.ProxyPort;
        ProxyUserBox.Text = prefs.ProxyUser ?? "";
        ProxyPassBox.Password = SecretStore.Unprotect(prefs.ProxySecret ?? "");
        var custom = string.Equals(SelectedTag(ProxyModeBox), "custom", StringComparison.OrdinalIgnoreCase);
        ProxyCustomRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ProxyAuthRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _suppress = false;
    }

    private void SaveProxy()
    {
        if (_suppress || !_ready) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.ProxyMode = string.IsNullOrWhiteSpace(SelectedTag(ProxyModeBox)) ? "system" : SelectedTag(ProxyModeBox);
        prefs.ProxyHost = ProxyHostBox.Text.Trim();
        prefs.ProxyPort = double.IsNaN(ProxyPortBox.Value) ? 0 : (int)Math.Round(ProxyPortBox.Value);
        prefs.ProxyUser = ProxyUserBox.Text.Trim();
        var plain = ProxyPassBox.Password ?? "";
        // 明文只活在内存里，落盘一律是 DPAPI 密文；密码没变（能解出同一份明文）就不重算密文。
        if (SecretStore.Unprotect(prefs.ProxySecret ?? "") != plain)
            prefs.ProxySecret = plain.Length == 0 ? "" : SecretStore.Protect(plain);
        ProxyCustomRow.Visibility = prefs.ProxyMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        ProxyAuthRow.Visibility = prefs.ProxyMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        NetworkProxy.Apply(prefs);
        AppPreferencesStore.Save(prefs);
        NetworkStatusText.Text = NetworkProxy.Describe();
        MarkSaved();
    }

    private async Task SaveNetworkAsync()
    {
        if (!_ready) return;
        var source = SelectedTag(DownloadSourceBox);
        var savedIndex = OnlinePage.ResourceSourceIndex(_savedResourceSource);
        var selectedIndex = OnlinePage.ResourceSourceIndex(source);
        if (savedIndex == selectedIndex) source = _savedResourceSource;
        var threads = double.IsNaN(DownloadThreadsBox.Value) ? 16 : (int)Math.Round(DownloadThreadsBox.Value);
        var mirror = GameDownloadSourceBox.SelectedIndex switch
        {
            1 => DownloadPage.BmclApiUrl,
            2 => MirrorBox.Text.Trim(),
            _ => "",
        };
        var invalidCustomMirror = GameDownloadSourceBox.SelectedIndex == 2
            && (!Uri.TryCreate(mirror, UriKind.Absolute, out var address)
                || address.Scheme is not ("http" or "https")
                || string.IsNullOrEmpty(address.Host)
                || DownloadPage.IsGitHubMirror(mirror));
        if (invalidCustomMirror) mirror = _savedGameMirror;
        var result = await App.Backend.SetGeneralSettingsAsync(source, mirror, threads,
            _cfApiKey, _legacyGlobalJavaPath);
        if (!result.Ok)
        {
            NetworkStatusText.Text = string.IsNullOrWhiteSpace(result.Message) ? "保存失败。" : result.Message;
            return;
        }

        _savedGameMirror = mirror;
        _savedResourceSource = source;
        if (savedIndex != selectedIndex)
        {
            OnlinePage.PreferredSource = selectedIndex;
        }
        NetworkStatusText.Text = invalidCustomMirror
            ? "请输入有效的游戏镜像地址。GitHub 加速地址请填在 GitHub 镜像。" : "";
        if (!invalidCustomMirror) MarkSaved();
    }

    private async void Unhide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HiddenVersionInfo item } || string.IsNullOrWhiteSpace(item.Id)) return;
        var result = await App.Backend.UnhideVersionAsync(item.Id, item.Root);
        if (!result.Ok)
        {
            NetworkStatusText.Text = string.IsNullOrWhiteSpace(result.Message) ? "恢复失败。" : result.Message;
            return;
        }

        await LoadHiddenAsync();
        _ = App.MainWindow.RefreshShellAsync(force: true);
        MarkSaved();
    }

    // 多目录管理入口：设置页只留一个跳转，目录的增删与排序都在版本管理页完成。
    private void ManageGameDirs_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("versions");

    private void Home_Tapped(object sender, TappedRoutedEventArgs e) => App.MainWindow?.Navigate("home");

    private static string SelectedTag(ComboBox box) =>
        box.SelectedItem is ComboBoxItem item ? item.Tag as string ?? "" : "";

    // ---- 开发者测试：认证 → 开发者测试功能总开关 → 联机功能，三层都开才生效（默认全关）----
    // 设置存在 settings4.json 的 devUnlocked / devFeatures / onlineEnabled / shareOnline。
    private async Task SyncDevAsync()
    {
        DevSettings s;
        try
        {
            App.Trace("SyncDev: start");
            s = await App.Backend.GetDevSettingsAsync();
            App.Trace($"SyncDev: ok={s.Ok} unlocked={s.Unlocked} dev={s.DevFeatures} online={s.OnlineEnabled} env={s.EnvOverride}");
        }
        catch (Exception ex)
        {
            App.Trace("SyncDev: backend call failed: " + ex);
            return;
        }
        if (!_alive || !s.Ok) return;
        _savedDev = s;
        ApplyDevSettings(s);
    }

    private DevSettings? _savedDev;
    private bool _savingDev;

    private void ApplyDevSettings(DevSettings state)
    {
        _devUnlocked = state.Unlocked;
        var suppress = _suppress;
        _suppress = true;
        try
        {
            NavDev.Visibility = state.Unlocked ? Visibility.Visible : Visibility.Collapsed;
            DevFeaturesSwitch.IsOn = state.DevFeatures;
            OnlineEnabledSwitch.IsOn = state.OnlineEnabled;
            ShareOnlineSwitch.IsOn = state.ShareOnline;
            OnlineEnvHint.Visibility = state.EnvOverride ? Visibility.Visible : Visibility.Collapsed;
            UpdateDevControls(state.EnvOverride);
        }
        finally { _suppress = suppress; }
    }

    private void UpdateDevControls(bool forced)
    {
        DevFeaturesPanel.Visibility = DevFeaturesSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        DevFeaturesSwitch.IsEnabled = !_savingDev && !forced;
        OnlineEnabledSwitch.IsEnabled = !_savingDev && !forced && DevFeaturesSwitch.IsOn;
        ShareOnlineSwitch.IsEnabled = !_savingDev && DevFeaturesSwitch.IsOn && OnlineEnabledSwitch.IsOn;
    }

    private async Task SaveDevAsync()
    {
        if (_suppress || !_ready || _savingDev || _savedDev is not { } previous) return;
        var dev = DevFeaturesSwitch.IsOn;
        var online = dev && OnlineEnabledSwitch.IsOn;
        var share = ShareOnlineSwitch.IsOn;
        _savingDev = true;
        // 立即响应总开关，保存结果回来后再确认状态。
        UpdateDevControls(previous.EnvOverride);
        try
        {
            if (previous.DevFeatures && previous.OnlineEnabled && previous.ShareOnline && (!online || !share))
                await App.Backend.ReportPresenceAsync("OFFLINE");
            var saved = await App.Backend.SetDevSettingsAsync(dev, online, share);
            if (!saved.Ok)
            {
                if (_alive) ShowDevSaveError();
                return;
            }
            _savedDev = saved;
            if (_alive) MarkSaved();
            if (App.MainWindow is { } main) await main.ApplyOnlineSettingAsync();
        }
        catch (Exception ex)
        {
            App.Trace("SaveDev: " + ex);
            if (_alive) ShowDevSaveError();
        }
        finally
        {
            _savingDev = false;
            if (_alive) ApplyDevSettings(_savedDev ?? previous);
        }
    }

    private void ShowDevSaveError()
    {
        Toast.Title = "开发者测试设置保存失败";
        Toast.Message = "已恢复上一次保存的设置。";
        Toast.Severity = InfoBarSeverity.Error;
        Toast.IsOpen = true;
    }

    private async void DevFeaturesSwitch_Toggled(object sender, RoutedEventArgs e) => await SaveDevAsync();
    private async void OnlineEnabledSwitch_Toggled(object sender, RoutedEventArgs e) => await SaveDevAsync();
    private async void ShareOnlineSwitch_Toggled(object sender, RoutedEventArgs e) => await SaveDevAsync();

    private void NotifyTaskDoneSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        App.MainWindow!.Preferences.NotifyTaskDone = NotifyTaskDoneSwitch.IsOn;
        MarkSaved();
    }

    private void ShowTaskIslandSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        App.MainWindow!.Preferences.ShowTaskIsland = ShowTaskIslandSwitch.IsOn;
        App.MainWindow!.SetTaskIslandVisible(ShowTaskIslandSwitch.IsOn);
        MarkSaved();
    }

    // ---- 检查更新（启动器）与联机组件版本

    private void RenderUpdate()
    {
        var cur = BuildInfo.Current;
        UpdateCurrentText.Text = $"{cur.Display}（{cur.ChannelName}）";
        var r = UpdateService.Last;
        UpdateCheckButton.IsEnabled = !cur.IsReview;
        UpdateOpenButton.Visibility = Visibility.Collapsed;
        UpdateOpenButton.Tag = null;
        string latest = "—", note = "";
        if (cur.IsReview) note = cur.NoticeText;
        else if (r is not null)
        {
            switch (r.State)
            {
                case UpdateState.Available:
                    latest = r.Latest!.Version;
                    note = r.Message;
                    if (r.Latest.Url.Length > 0) { UpdateOpenButton.Tag = r.Latest.Url; UpdateOpenButton.Visibility = Visibility.Visible; }
                    break;
                case UpdateState.UpToDate: latest = r.Latest?.Version ?? "—"; note = r.Message; break;
                case UpdateState.Ahead: latest = r.Latest?.Version ?? "暂无发行版"; note = r.Message; break;
                default: note = r.Message; break;
            }
        }
        UpdateLatestText.Text = latest;
        UpdateStatusText.Text = note;
        UpdateStatusText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        AboutRepoText.Text = UpdateService.RepoUrl.Replace("https://", "");
        AboutRepoButton.Tag = UpdateService.RepoUrl;
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateCheckButton.Content = "检查中…";
        try { await UpdateService.CheckAsync(App.MainWindow!.Preferences.UpdateChannel); }
        finally
        {
            if (_alive) { UpdateCheckButton.Content = "检查更新"; RenderUpdate(); }
        }
    }

    private void UpdateOpen_Click(object sender, RoutedEventArgs e) => OpenHttps((sender as Button)?.Tag as string);

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenHttps((sender as Button)?.Tag as string);

    private static void OpenHttps(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        _ = Windows.System.Launcher.LaunchUriAsync(u);
    }

    /// <summary>联机组件只在联机开着时显示（第一版联机整套默认隐藏）。</summary>
    private void RenderComponent()
    {
        var online = App.MainWindow?.OnlineActive == true;
        ComponentSection.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        AboutComponentRow.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        if (!online) return;

        var info = OnlineComponent.Loaded ?? OnlineComponent.ReadInstalled();
        var pending = OnlineComponent.PendingUpdate;
        var text = info is null ? "未安装" : $"v{info.Version} · 接口级别 {info.ApiLevel}";
        if (pending is not null) text += $" · 有新版本 v{pending.Version}";
        ComponentStatusText.Text = text;
        ComponentActionButton.Content = info is null ? "下载" : "更新";
        ComponentActionButton.Visibility = info is null || pending is not null ? Visibility.Visible : Visibility.Collapsed;
        AboutComponentText.Text = info is null ? "未安装" : $"v{info.Version} · 接口级别 {info.ApiLevel}";
        AboutComponentButton.Tag = UpdateService.ModuleRepoUrl;
    }

    private async void ComponentCheck_Click(object sender, RoutedEventArgs e)
    {
        ComponentCheckButton.IsEnabled = false;
        ComponentCheckButton.Content = "检查中…";
        string note;
        try
        {
            var (offer, error) = await OnlineComponent.CheckNowAsync();
            var info = OnlineComponent.Loaded ?? OnlineComponent.ReadInstalled();
            note = offer is null ? error
                : OnlineComponent.IsNewer(offer, info) ? $"云端最新版本 v{offer.Version}，可以更新。" : "已是最新版本。";
        }
        catch (Exception ex) { note = "检查失败：" + ex.Message; }
        finally { if (_alive) { ComponentCheckButton.IsEnabled = true; ComponentCheckButton.Content = "检查更新"; } }
        if (!_alive) return;
        ComponentNoteText.Text = note;
        ComponentNoteText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderComponent();
    }

    private void ComponentAction_Click(object sender, RoutedEventArgs e)
    {
        var installed = (OnlineComponent.Loaded ?? OnlineComponent.ReadInstalled()) is not null;
        App.MainWindow?.Navigate(installed ? "roomupdate" : "room");
    }

    private int _versionTaps;
    private DateTime _lastVersionTap = DateTime.MinValue;

    /// <summary>连点版本号 7 次：主动向服务器验证开发者身份（这是第一次解锁的唯一入口；启动时不会自动联网验证）。</summary>
    private async void AboutVersion_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        _versionTaps = (now - _lastVersionTap).TotalSeconds > 3 ? 1 : _versionTaps + 1;
        _lastVersionTap = now;
        if (_versionTaps < 7) return;
        _versionTaps = 0;

        AboutNoticeText.Text = "正在向服务器验证开发者身份…";
        AboutNoticeText.Visibility = Visibility.Visible;
        var result = await DevVerdict.RefreshAsync();
        if (!_alive) return;
        AboutNoticeText.Text = result switch
        {
            true => "验证通过：这个账号是开发者，「开发者测试」已解锁。",
            false => "验证完成：这个账号不是开发者。",
            _ => "没验证成功（需要先登录正版账号并能连上服务器），稍后再试。",
        };
        if (result is not null) await App.MainWindow.ApplyOnlineSettingAsync();
    }

    private void OnUpdateChanged() => DispatcherQueue.TryEnqueue(() => { if (_alive) { RenderUpdate(); RenderComponent(); } });

    private void CheckUpdatesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_ready) return;
        App.MainWindow!.Preferences.CheckUpdatesOnStartup = CheckUpdatesSwitch.IsOn;
        MarkSaved();
    }

    private void UpdateChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_ready) return;
        var tag = (UpdateChannelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "stable";
        App.MainWindow!.Preferences.UpdateChannel = tag;
        MarkSaved();
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", "logs");
        OpenFolder(dir);
    }

    private void OpenOnboarding_Click(object sender, RoutedEventArgs e) => App.MainWindow.OpenOnboarding();

    private void OpenLicenses_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Licenses");
        if (System.IO.Directory.Exists(dir)) OpenFolder(dir);
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = new List<string>
            {
                $"MeowLauncher {BuildInfo.Current.Display}（{BuildInfo.Current.ChannelName}）",
                $".NET {Environment.Version}",
                $"OS {Environment.OSVersion}",
                $"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                "",
                "== crash.log 末尾 ==",
            };
            var crash = System.IO.Path.Combine(AppPreferencesStore.FolderPath, "crash.log");
            if (System.IO.File.Exists(crash))
            {
                var all = System.IO.File.ReadAllLines(crash);
                lines.AddRange(all.TakeLast(Math.Min(120, all.Length)));
            }
            else lines.Add("（没有崩溃记录）");

            var outFile = System.IO.Path.Combine(AppPreferencesStore.FolderPath,
                $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            System.IO.Directory.CreateDirectory(AppPreferencesStore.FolderPath);
            System.IO.File.WriteAllLines(outFile, lines);
            Toast.Title = "诊断包已导出";
            Toast.Message = outFile;
            Toast.Severity = InfoBarSeverity.Success;
            Toast.IsOpen = true;
        }
        catch (Exception ex)
        {
            Toast.Title = "导出失败";
            Toast.Message = ex.Message;
            Toast.Severity = InfoBarSeverity.Error;
            Toast.IsOpen = true;
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Services.AppPreferencesStore.FolderPath;
            var removed = 0;
            foreach (var name in new[] { "skins", "thumbs", "cache" })
            {
                var dir = System.IO.Path.Combine(folder, name);
                if (!System.IO.Directory.Exists(dir)) continue;
                removed += System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories).Length;
                System.IO.Directory.Delete(dir, recursive: true);
            }
            Toast.Title = removed > 0 ? "缓存已清理" : "没有需要清理的缓存";
            Toast.Message = removed > 0 ? $"共删除 {removed} 个文件" : "头像与缩略图目录本来就是空的";
            Toast.Severity = InfoBarSeverity.Success;
            Toast.IsOpen = true;
        }
        catch (Exception ex)
        {
            Toast.Title = "清理失败";
            Toast.Message = ex.Message;
            Toast.Severity = InfoBarSeverity.Error;
            Toast.IsOpen = true;
        }
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "恢复默认设置",
            Content = "所有外观、启动、下载与通知设置都会回到出厂值。实例与账号不受影响，重启后生效。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.AppPreferencesStore.Save(new Services.AppPreferences());
        Toast.Title = "已恢复默认设置";
        Toast.Message = "重启启动器后生效";
        Toast.Severity = InfoBarSeverity.Warning;
        Toast.IsOpen = true;
    }

    private void SyncNotifyAndUpdate()
    {
        if (App.MainWindow is not { } main) return;
        var prefs = main.Preferences;
        _suppress = true;
        NotifyTaskDoneSwitch.IsOn = prefs.NotifyTaskDone;
        ShowTaskIslandSwitch.IsOn = prefs.ShowTaskIsland;
        CheckUpdatesSwitch.IsOn = prefs.CheckUpdatesOnStartup;
        UpdateChannelBox.SelectedIndex = prefs.UpdateChannel == "beta" ? 1 : 0;
        RenderUpdate();
        RenderComponent();
        _suppress = false;
    }
}
