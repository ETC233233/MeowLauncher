using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System.Power;
using Windows.UI.ViewManagement;

namespace MeowLauncher.WinUI.Controls;

public enum WallpaperPage { Home, List, Settings }

/// <summary>
/// 整窗分层像素壁纸。锚点式构图：台阶贴窗口底边、猫挂在网格右下角外侧、
/// 太阳夹在标题和标签之间；每层用整数倍最近邻素材，Composition 做视差，暗幕按页面分档。
/// </summary>
public sealed partial class WallpaperLayer : UserControl
{
    private const int MasterW = 480, MasterH = 270, MinN = 4, MaxN = 8;
    private const double Bleed = 1.16;
    private static readonly double[][] VeilTable =
    {
        new[] { 0.40, 0.70, 0.85 },   // 深色：主页 / 列表 / 设置
        new[] { 0.80, 0.75, 0.88 },   // 浅色
    };
    private static readonly double[] Amp = { 3, 6, 9 };
    // 近景（猫、台阶）吃的蒙层比例：深色 1.0；浅色 0.5，否则 80% 薄雾下猫几乎看不见
    private double NearVeilShare => ActualTheme == ElementTheme.Light ? 0.5 : 1.0;

    private Compositor? _c;
    private int _n;
    private string _theme = "";
    private double _scale = 1, _bleedDip;
    private bool _active = true, _settings, _frozen;
    private WallpaperPage _page = WallpaperPage.Home;
    private readonly UISettings _ui = new();
    private readonly UIElement[][] _groups;                       // 视差分组：天空+太阳 4 / 远山 8 / 猫+台阶 12
    private readonly Vector3?[] _lastTarget = new Vector3?[3];
    private (Rect Grid, double TitleRight, double TabsLeft)? _anchors;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _anchorTimer;

    private double _contentLeft;

    /// <summary>内容区左边界（侧栏右边），猫不会被压到半透明侧栏底下。</summary>
    public double ContentLeft
    {
        get => _contentLeft;
        set { if (Math.Abs(_contentLeft - value) < 0.5) return; _contentLeft = value; Rebuild(); }
    }

    public WallpaperLayer()
    {
        InitializeComponent();
        _groups = new[]
        {
            new UIElement[] { SkyGroup, SunLayer },
            new UIElement[] { FarLayer },
            new UIElement[] { CatLayer, LedgeLayer },
        };
    }

    // ---- 对外接口 ----
    public void SetSafeArea(Rect grid, double titleRight, double tabsLeft)
    {
        // 越界/取到动画中途值的推送直接丢弃，只留可信的最后一帧（60ms 去抖）。
        var W = ActualWidth;
        if (W > 1 && (grid.X < -1 || grid.X > W || grid.Width < 1 || grid.Right > W + 1)) return;
        _anchors = (grid, titleRight, tabsLeft);
        _anchorTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _anchorTimer.Interval = TimeSpan.FromMilliseconds(60);
        _anchorTimer.IsRepeating = false;
        _anchorTimer.Tick -= OnAnchorTimerTick;
        _anchorTimer.Tick += OnAnchorTimerTick;
        _anchorTimer.Start();
    }

    private void OnAnchorTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    { sender.Stop(); Rebuild(); }

    public void ClearSafeArea() { if (_anchors is null) return; _anchors = null; Rebuild(); }
    public void OnPointer(Point p, Size win)
    { if (!_frozen && _c is not null && win.Width > 1 && win.Height > 1) Aim((p.X - win.Width / 2) / (win.Width / 2), (p.Y - win.Height / 2) / (win.Height / 2)); }
    public void OnPointerLeave() { if (!_frozen) Aim(0, 0); }
    public void SetActive(bool active) { _active = active; ApplyMotion(); }
    public void SetPage(WallpaperPage page)
    {
        var wasHome = _page == WallpaperPage.Home;
        _page = page; _settings = page == WallpaperPage.Settings;
        ApplyVeil(); ApplyMotion();
        // 猫只在首页画。页面切换如果不重建，从设置页回来会有一段时间看不到猫。
        if (wasHome != (page == WallpaperPage.Home)) Rebuild();
    }

    // ---- 生命周期 ----
    private void OnLoaded(object s, RoutedEventArgs e)
    {
        _c = ElementCompositionPreview.GetElementVisual(this).Compositor;
        foreach (var g in _groups) foreach (var el in g) ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        ElementCompositionPreview.SetIsTranslationEnabled(CloudStrip, true);
        PowerManager.EnergySaverStatusChanged += OnPower;
        Rebuild();
    }
    private void OnUnloaded(object s, RoutedEventArgs e) => PowerManager.EnergySaverStatusChanged -= OnPower;
    private void OnPower(object? s, object e) => DispatcherQueue.TryEnqueue(ApplyMotion);
    private void OnSizeChanged(object s, SizeChangedEventArgs e) => Rebuild();
    private void OnThemeChanged(FrameworkElement s, object a) => Rebuild();

    private void Rebuild()
    {
        if (_c is null || XamlRoot is null || ActualWidth < 1 || ActualHeight < 1) return;
        _scale = Math.Max(1, XamlRoot.RasterizationScale);
        double wPx = ActualWidth * _scale, hPx = ActualHeight * _scale;
        var n = Math.Clamp((int)Math.Ceiling(Bleed * Math.Max(wPx / MasterW, hPx / MasterH)), MinN, MaxN);
        var theme = ActualTheme == ElementTheme.Light ? "light" : "dark";
        var reload = n != _n || theme != _theme;
        _n = n; _theme = theme;
        if (reload) { LoadLayers(); Array.Clear(_lastTarget); }
        LayoutLayers(wPx, hPx);
        if (reload) StartAmbient();
        ApplyVeil();
        ApplyMotion();
    }

    /// <summary>当前壁纸主题目录名（dark/light），供页面内的贴图复用。</summary>
    public string ThemeName => string.IsNullOrEmpty(_theme)
        ? (ActualTheme == ElementTheme.Light ? "light" : "dark")
        : _theme;

    /// <summary>当前使用的猫姿势：sit=站着（有身子和尾巴），peek=只探头。</summary>
    private string _catPose = "sit";
    private bool _catHidden;

    /// <summary>继续玩页让猫站起来（整只可见），组件板页保持探头。</summary>
    public void SetCatStanding(bool standing)
    {
        var want = standing ? "sit" : "peek";
        if (want == _catPose) return;
        _catPose = want;
        if (CatLayer is not null) CatLayer.Source = CatImage();
        if (ActualWidth > 0) Rebuild();
    }

    /// <summary>把壁纸里的猫藏起来（负一屏改用账户条上的贴图猫）。</summary>
    public void SetCatHidden(bool hidden)
    {
        if (_catHidden == hidden) return;
        _catHidden = hidden;
        Rebuild();
    }

    private BitmapImage CatImage() => new(new Uri($"ms-appx:///Assets/Wallpaper/{_theme}/cat_{_catPose}_x{_n}.png"));

    private void LoadLayers()
    {
        var dir = $"ms-appx:///Assets/Wallpaper/{_theme}";
        BitmapImage Img(string f) => new(new Uri($"{dir}/{f}"));
        SkyLayer.Source = Img($"sky_x{_n}.png");
        SunLayer.Source = Img($"sun_x{_n}.png");
        FarLayer.Source = Img($"far_x{_n}.png");
        CatLayer.Source = CatImage();
        LedgeLayer.Source = Img($"ledge_x{_n}.png");
        var cloud = Img($"clouds_x{_n}.png"); CloudA.Source = cloud; CloudB.Source = cloud;
        GlowLayer.Source = Img("glow.png");
    }

    private double Snap(double dip) => Math.Round(dip * _scale) / _scale;

    /// <summary>把图层容器裁到内容区左边界，图本身按容器坐标左移同样的距离（视差仍作用在图元素上）。</summary>
    private void ClipToContent(Canvas host, Image img, double W, double H)
    {
        var x = Math.Max(0, Math.Min(ContentLeft, W));
        Canvas.SetLeft(host, x);
        host.Width = Math.Max(1, W - x);
        host.Height = H;
        host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, host.Width, H) };
        Canvas.SetLeft(img, Snap(Canvas.GetLeft(img) - x));
    }
    private static void Put(FrameworkElement el, double x, double y, double w, double h)
    { el.Width = w; el.Height = h; Canvas.SetLeft(el, x); Canvas.SetTop(el, y); }

    private void LayoutLayers(double wPx, double hPx)
    {
        var d = _n / _scale;                                      // 1 个母版像素 = d DIP
        double W = ActualWidth, H = ActualHeight, imgW = MasterW * d;
        var left = Math.Round((wPx - MasterW * _n) / 2) / _scale;
        var top = Math.Round((hPx - MasterH * _n) / 2) / _scale;

        Put(SkyGroup, left, top, imgW, MasterH * d);
        SkyLayer.Width = imgW; SkyLayer.Height = MasterH * d;
        CloudA.Width = CloudB.Width = imgW; CloudA.Height = CloudB.Height = 40 * d;
        Canvas.SetTop(CloudStrip, Snap(40 - top));                // 云带顶部落在窗口 y=40

        var ledgeTop = H - 5.5 * d;
        Put(FarLayer, left, Snap(H - 17 * d), imgW, 20 * d);      // 山脊基准行在第 6 行 → 窗口 y = H − 11d
        Put(LedgeLayer, left, Snap(ledgeTop - d), imgW, 12 * d);  // 台阶第 1 行 → 窗口 y = H − 5.5d
        // 侧栏是不透明面板：山脊和台阶只从内容区左边界开始画，别从玻璃底下透出来。
        ClipToContent(FarHost, FarLayer, W, H);
        ClipToContent(LedgeHost, LedgeLayer, W, H);

        // 底部带只按「窗口右边 + 窗口高」定位：窗口怎么变，猫都完整露头（8 行 = 32 DIP）。
        var showCat = _page == WallpaperPage.Home && !_catHidden;
        if (showCat)
        {
            // 新可爱猫母版 13×14；旧探头猫 12×13
            var catW = _catPose == "sit" ? 13 : 12;
            var catH = _catPose == "sit" ? 14 : 13;
            var catRows = _catPose == "sit" ? 14 : 8;
            var catX = Math.Max(W - 48 - catW * d, ContentLeft + 24);
            Put(CatLayer, Snap(catX), Snap(ledgeTop - catRows * d), catW * d, catH * d);
        }
        var showSun = false;
        if (_anchors is { } a)
        {
            showSun = a.TabsLeft - a.TitleRight >= 24 * d + 32;   // 标题和标签之间要放得下光晕
            if (showSun) Put(SunLayer, Snap((a.TitleRight + a.TabsLeft) / 2 - 12 * d), Snap(84 - 12 * d), 24 * d, 24 * d);
        }
        CatLayer.Visibility = showCat ? Visibility.Visible : Visibility.Collapsed;
        SunLayer.Visibility = showSun ? Visibility.Visible : Visibility.Collapsed;

        GlowLayer.Width = VeilBack.Width = VeilFront.Width = W;
        GlowLayer.Height = VeilBack.Height = VeilFront.Height = H;
        _bleedDip = Math.Max(0, Math.Min((MasterW * _n - wPx) / 2, (MasterH * _n - hPx) / 2) / _scale);
    }

    private void StartAmbient()
    {
        if (_c is null) return;
        var tilePx = MasterW * _n;
        var cloud = _c.CreateScalarKeyFrameAnimation();
        cloud.InsertKeyFrame(0f, 0f);
        cloud.InsertKeyFrame(1f, (float)(-tilePx / _scale), _c.CreateStepEasingFunction(tilePx));
        cloud.Duration = TimeSpan.FromSeconds(tilePx * 0.6);
        cloud.IterationBehavior = AnimationIterationBehavior.Forever;
        ElementCompositionPreview.GetElementVisual(CloudStrip).StartAnimation("Translation.X", cloud);

        var glow = _c.CreateScalarKeyFrameAnimation();
        glow.InsertKeyFrame(0f, 0f); glow.InsertKeyFrame(0.5f, 0.08f); glow.InsertKeyFrame(1f, 0f);
        glow.Duration = TimeSpan.FromSeconds(30); glow.IterationBehavior = AnimationIterationBehavior.Forever;
        ElementCompositionPreview.GetElementVisual(GlowLayer).StartAnimation("Opacity", glow);
    }

    private void Aim(double nx, double ny)
    {
        if (_c is null) return;
        nx = Math.Clamp(nx, -1, 1); ny = Math.Clamp(ny, -1, 1);
        for (var i = 0; i < 3; i++)
        {
            var a = Math.Min(Amp[i], _bleedDip);
            var t = new Vector3((float)(Math.Round(-nx * a * _scale) / _scale), (float)(Math.Round(-ny * a * _scale) / _scale), 0);
            if (_lastTarget[i] == t) continue;                    // 目标已取整到设备像素，没变就不重发
            _lastTarget[i] = t;
            var sp = _c.CreateSpringVector3Animation();
            sp.DampingRatio = 0.9f; sp.Period = TimeSpan.FromMilliseconds(110); sp.FinalValue = t;
            foreach (var el in _groups[i]) ElementCompositionPreview.GetElementVisual(el).StartAnimation("Translation", sp);
        }
    }

    private void ApplyMotion()
    {
        if (_c is null) return;
        var run = _active && !_settings && _ui.AnimationsEnabled && PowerManager.EnergySaverStatus != EnergySaverStatus.On;
        _frozen = !run;
        void Set(UIElement el, string prop)
        {
            var ctl = ElementCompositionPreview.GetElementVisual(el).TryGetAnimationController(prop);
            if (ctl is null) return; if (run) ctl.Resume(); else ctl.Pause();
        }
        foreach (var g in _groups) foreach (var el in g) Set(el, "Translation");
        Set(CloudStrip, "Translation.X"); Set(GlowLayer, "Opacity");
    }

    private void ApplyVeil()   // 渐变交给 XAML 的 OpacityTransition
    {
        var v = VeilTable[ActualTheme == ElementTheme.Light ? 1 : 0][(int)_page];
        var f = v * NearVeilShare;
        VeilBack.Opacity = 1 - (1 - v) / (1 - f);                 // 叠加后天空和远山 = v，猫和台阶 = f
        VeilFront.Opacity = f;
    }

}
