using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using MeowLauncher.WinUI.Controls;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace MeowLauncher.WinUI.Views;

public sealed partial class HomePage : Page
{
    // 组件板固定 4 列；B 定稿：内容宽最多 960、格子 228 方块，窄窗 201×165。
    private int _columns = 4;
    private const int MaxRows = 3;
    private const double CellSpacing = 16;
    private const double BoardMaxWidth = 960;
    private const double BoardPageMargin = 24;
    private const double BoardSquareThreshold = 940;
    private const double BoardNarrowRowHeight = 165;
    private int _instanceTierW = 2;
    private int _instanceTierH = 2;

    /// <summary>组件板最多几页（每页 4×3 格），到顶就不再新开页。</summary>
    private const int MaxPages = 6;
    /// <summary>组件板页数：按 4×3 容量算，卡片排不下就新开一页，绝不靠上下滚动。</summary>
    private int PageCount { get; set; } = 2;
    // 错误面板只给这一句：异常原文/后端串只进调试日志。
    private const string LoadErrorMessage = "本机数据没读出来，重试一次。";

    private sealed record WidgetDef(string Id, string Label, int DefaultW, int DefaultH)
    {
        public required IReadOnlyList<(int W, int H)> Sizes { get; init; }
    }

    private static readonly WidgetDef[] Catalog =
    {
        new("instance", "当前实例", 2, 2) { Sizes = new[] { (1, 1), (2, 1), (2, 2) } },
        new("profile", "玩家档案", 2, 2) { Sizes = new[] { (1, 1), (2, 1), (1, 2), (2, 2) } },
        new("clock", "时钟", 1, 1) { Sizes = new[] { (1, 1), (2, 1) } },
        new("status", "服务状态", 1, 1) { Sizes = new[] { (1, 1), (2, 1) } },
        new("recent", "最近启动", 2, 2) { Sizes = new[] { (2, 1), (1, 1), (2, 2) } },
        new("media", "系统媒体", 4, 1) { Sizes = new[] { (1, 1), (2, 1), (4, 1) } },
        new("pinned", "固定版本", 4, 1) { Sizes = new[] { (2, 1), (4, 1) } },
        new("shots", "截图墙", 2, 2) { Sizes = new[] { (1, 1), (2, 1), (2, 2), (4, 2), (4, 3) } },
        new("quick", "快捷操作", 2, 1) { Sizes = new[] { (1, 1), (2, 1), (2, 2) } },
        new("tasks", "任务", 1, 1) { Sizes = new[] { (1, 1), (2, 1), (4, 1) } },
        new("tip", "每日一帖", 2, 2) { Sizes = new[] { (2, 1), (1, 1), (2, 2) } },
    };

    private sealed class Placement
    {
        public required string Id { get; init; }
        public int Page { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    private readonly record struct CellRect(int Row, int Col, int W, int H);

    private readonly Dictionary<string, WidgetDef> _defs = Catalog.ToDictionary(d => d.Id);
    private readonly Dictionary<string, Border> _cards = new();
    private readonly Dictionary<Border, string> _cardIds = new();
    private readonly List<Placement> _layout = new();
    private readonly HashSet<string> _hidden = new();

    private bool _layoutCustomized;
    private bool _statusLoading;

    private readonly MediaService _media = new();
    private readonly DispatcherTimer _clockTimer = new();
    private Window? _hookedWindow;
    private DateTime _lastResume = DateTime.MinValue;
    private readonly DispatcherTimer _toastTimer = new();
    private readonly TranslateTransform _dragTransform = new();

    private Snapshot? _snapshot;
    private bool _mediaStarted;
    private readonly NewsService _news = new();
    private bool _newsStarted;
    private NewsItem? _newsItem;
    private bool _busy;
    private bool _launching;
    private string _launchOverlayVersion = "";
    private bool _layoutLoaded;
    private bool _preferencesApplied;
    private double _boardWidth = -1;
    private double _boardRowHeight = -1;
    private bool _editMode;
    private bool _dragMoved;
    private int _currentPage;
    private int _shotsCount;
    private int _recentWideCap = -1;
    private int _recentSmallCap = -1;
    private int _continueRecentCount = -1;
    private int _continueRecentMeta = -1;
    private double _continueRecentHeight = -1;
    private bool _applyingContinue;
    private bool _continueHasFigure;
    private bool _taskHasQueue;
    private bool _mediaPlaying;

    // ---------- 两屏（负一屏 / 组件板） ----------
    private const string ModeSimple = "simple";
    private const string ModeClassic = "classic";

    private string _homeLayout = ModeSimple;
    private readonly TranslateTransform _continueShift = new();
    private readonly TranslateTransform _boardShift = new();
    private bool _simpleMode;
    private int _screen;                 // 0 = 继续玩（负一屏），1 = 组件板
    private double _screenDragStartX;
    private bool _screenDragging;
    private string _continueSelectedId = "";

    /// <summary>设置页切换首页形态后广播，主页据此即时重建两屏。</summary>
    public static event EventHandler? LayoutPreferenceChanged;

    public static void NotifyLayoutPreferenceChanged() => LayoutPreferenceChanged?.Invoke(null, EventArgs.Empty);

    private Border? _dragCard;
    private Placement? _dragPlacement;
    private Windows.Foundation.Point _dragStart;

    public event EventHandler<string>? NavigateRequested;
    /// <summary>「实例设置」要打开版本详情并滚到启动设置，参数是实例 id。</summary>
    public event EventHandler<string>? InstanceSettingsRequested;
    public event EventHandler? RefreshRequested;

    public HomePage()
    {
        InitializeComponent();
        ContinuePicker.Picked += ContinuePicker_Picked;
        foreach (var def in Catalog)
        {
            var card = (Border)FindName(char.ToUpperInvariant(def.Id[0]) + def.Id[1..] + "Card");
            _cards[def.Id] = card;
            _cardIds[card] = def.Id;
            card.Transitions.Add(new RepositionThemeTransition());
            card.AddHandler(PointerPressedEvent, new PointerEventHandler(Card_PointerPressed), true);
            card.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Grid_PointerCanceled), true);
            // 捕获后事件只送到捕获元素（卡片本身），网格层收不到，所以卡片自己也要接一份。
            card.AddHandler(PointerMovedEvent, new PointerEventHandler(Grid_PointerMoved), true);
            card.AddHandler(PointerReleasedEvent, new PointerEventHandler(Grid_PointerReleased), true);
            card.AddHandler(RightTappedEvent, new RightTappedEventHandler(Card_RightTapped), true);
        }

        // 拖拽的移动/释放跟踪挂在网格层（捕获可能被 ScrollViewer 抢占，网格层最稳）
        WidgetGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(Grid_PointerMoved), true);
        WidgetGrid.AddHandler(PointerReleasedEvent, new PointerEventHandler(Grid_PointerReleased), true);
        WidgetGrid.AddHandler(PointerCanceledEvent, new PointerEventHandler(Grid_PointerCanceled), true);

        ProfileCard.SizeChanged += (_, _) => ResizeCardDecorations();
        ShotsCard.SizeChanged += (_, _) => ResizeCardDecorations();
        ClockCard.SizeChanged += (_, _) => ResizeClockMeta();
        RecentCard.SizeChanged += (_, _) => RecentCard_SizeChanged();

        SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyResponsiveColumns();
            LayoutScreens(false);
            ApplyContinueResponsive();
            PushSafeArea();
        });
        ContinueScreen.RenderTransform = _continueShift;
        BoardScreen.RenderTransform = _boardShift;
        ScreenHost.PointerPressed += ScreenHost_PointerPressed;
        ScreenHost.PointerMoved += ScreenHost_PointerMoved;
        ScreenHost.PointerReleased += ScreenHost_PointerReleased;
        ScreenHost.PointerCanceled += ScreenHost_PointerLost;
        ScreenHost.PointerCaptureLost += ScreenHost_PointerLost;
        KeyDown += HomePage_KeyDown;
        IsTabStop = true;
        _homeLayout = ResolveHomeLayout();
        _screen = ResolveStartScreen();
        LayoutPreferenceChanged += (_, _) => DispatcherQueue.TryEnqueue(ReloadLayoutPreference);

        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) => CloseToast();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
        InstanceSelection.Changed += InstanceSelection_Changed;
    }

    // ---------- 两屏（负一屏 / 组件板） ----------

    private static string ResolveHomeLayout()
    {
        // 正式设置来自 app.json（设置页「首页形态」）。
        var pref = (App.MainWindow?.Preferences?.HomeLayout ?? "").Trim().ToLowerInvariant();
        return pref == ModeClassic ? ModeClassic : ModeSimple;
    }

    private int ResolveStartScreen()
    {
        var start = (App.MainWindow?.Preferences?.HomeStartScreen ?? "continue").Trim().ToLowerInvariant();
        return start == "board" ? 1 : 0;
    }

    private void ReloadLayoutPreference()
    {
        _homeLayout = ResolveHomeLayout();
        _screen = ResolveStartScreen();
        ApplyHomeMode();
    }

    private void ApplyHomeMode()
    {
        _simpleMode = _homeLayout != ModeClassic;
        ContinueScreen.Visibility = _simpleMode ? Visibility.Visible : Visibility.Collapsed;
        ScreenSwitcher.Visibility = _simpleMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_simpleMode) _screen = 1;
        _screen = Math.Clamp(_screen, 0, _simpleMode ? 1 : 1);
        LayoutScreens(false);
        UpdateScreenChrome();
        ApplyContinueResponsive();
    }

    private void LayoutScreens(bool animate)
    {
        var width = ScreenHost.ActualWidth;
        if (width <= 0) return;
        // 两屏都钉成视口宽度并左对齐，位移才是「整屏」而不是按内容宽算。
        ContinueScreen.Width = width;
        BoardScreen.Width = width;
        ContinueScreen.HorizontalAlignment = HorizontalAlignment.Left;
        BoardScreen.HorizontalAlignment = HorizontalAlignment.Left;
        ScreenHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, Math.Max(1, ScreenHost.ActualHeight)),
        };

        if (!_simpleMode)
        {
            _continueShift.X = 0;
            _boardShift.X = 0;
            return;
        }

        var toContinue = -_screen * width;
        var toBoard = _screen == 0 ? width : 0;
        if (!animate)
        {
            _continueShift.X = toContinue;
            _boardShift.X = toBoard;
            return;
        }

        AnimateShift(_continueShift, toContinue);
        var boardShift = AnimateShift(_boardShift, toBoard);
        // 位移动画期间 TransformToVisual 拿到的是中途坐标，锚点会落到屏外；等它停下再推一次。
        boardShift.Completed += (_, _) => DispatcherQueue.TryEnqueue(PushSafeArea);
    }

    private static Storyboard AnimateShift(TranslateTransform transform, double to)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
        return storyboard;
    }

    private void GoToScreen(int index, bool animate)
    {
        if (!_simpleMode) return;
        var next = Math.Clamp(index, 0, 1);
        if (next == _screen && animate) return;
        _screen = next;
        LayoutScreens(animate);
        UpdateScreenChrome();
        PushSafeArea();
    }

    /// <summary>
    /// 把当前屏的卡片边界推给壁纸：猫挂在网格右下角外侧、太阳夹在标题与标签之间。
    /// 用真实元素边界换算，不用公式反推，窗口或边距变了自动跟上。
    /// </summary>
    private bool _pushingSafeArea;

    private void PushSafeArea()
    {
        var win = App.MainWindow;
        if (win is null) return;
        var root = win.RootElement;
        if (root is null) return;
        FrameworkElement host = _screen == 1 || !_simpleMode ? WidgetGrid : MainInstanceCard;
        if (host.ActualWidth < 1 || host.ActualHeight < 1) return;
        // SizeChanged 里读到的 ActualWidth 可能是上一轮的值：先同步一次布局再量。
        if (!_pushingSafeArea) { _pushingSafeArea = true; try { root.UpdateLayout(); } finally { _pushingSafeArea = false; } }
        var g = host.TransformToVisual(root).TransformPoint(new Point(0, 0));
        var titleRight = PageDateText.TransformToVisual(root)
            .TransformPoint(new Point(PageDateText.ActualWidth, 0)).X;
        // 经典布局没有标签条（ScreenSwitcher 是 Collapsed），拿它当锚点会得到 0，
        // 太阳会因此被判成放不下。没有标签时用内容右缘兜底。
        var tabsLeft = _simpleMode
            ? ScreenSwitcher.TransformToVisual(root).TransformPoint(new Point(0, 0)).X
            : g.X + host.ActualWidth;
        win.WallpaperControl.SetCatStanding(_simpleMode);
        win.WallpaperControl.SetCatHidden(_simpleMode && _screen == 0);
        UpdateAccountCat();
        win.WallpaperControl.SetSafeArea(
            new Rect(g.X, g.Y, host.ActualWidth, host.ActualHeight), titleRight, tabsLeft);
    }

    private void UpdateScreenChrome()
    {
        var onBoard = !_simpleMode || _screen == 1;
        // 编辑、翻页、页脚都只属于组件板；负一屏不出现，避免误操作。
        // 保留槽位：只切透明度/命中测试，不折叠。折叠会让页头标签和整页高度在切页时跳。
        LayoutButton.Visibility = Visibility.Visible;
        LayoutButton.Opacity = onBoard ? 1 : 0;
        LayoutButton.IsHitTestVisible = onBoard;
        BoardFooter.Visibility = Visibility.Visible;
        BoardFooter.Opacity = onBoard ? 1 : 0;
        BoardFooter.IsHitTestVisible = onBoard;
        if (!onBoard && _editMode) ToggleLayoutEditing();

        var active = (Brush)Application.Current.Resources["AppTextBrush"];
        TabContinueText.Text = "继续玩";
        TabBoardText.Text = "组件板";
        var onContinue = _simpleMode && _screen == 0;
        var onBoardTab = !_simpleMode || _screen == 1;
        // 与设置 / 获取 / 实例页同一套分段标签：平时透明，选中为浅色胶囊 + 加粗
        var selectedFill = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
        var idleText = (Brush)Application.Current.Resources["AppText2Brush"];
        var clearFill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        TabContinueText.Foreground = onContinue ? active : idleText;
        TabBoardText.Foreground = onBoardTab ? active : idleText;
        TabContinueText.Opacity = 1;
        TabBoardText.Opacity = 1;
        TabContinueText.FontWeight = onContinue ? FontWeights.SemiBold : FontWeights.Normal;
        TabBoardText.FontWeight = onBoardTab ? FontWeights.SemiBold : FontWeights.Normal;
        TabContinue.Background = onContinue ? selectedFill : clearFill;
        TabBoard.Background = onBoardTab ? selectedFill : clearFill;
        TabContinueBar.Visibility = Visibility.Collapsed;
        TabBoardBar.Visibility = Visibility.Collapsed;
        TabContinue.IsEnabled = _simpleMode;
    }

    /// <summary>
    /// 负一屏只在这里决定高度：主卡吃 * 行，最近实例和账户条保持 Auto。
    /// 不再用子元素实际高度反算主卡，避免「测量→改高→再测量」的循环。
    /// </summary>
    private void ApplyContinueResponsive(bool forceRecent = false)
    {
        if (_applyingContinue || MainInstanceCard is null || ContinueStack is null || ContinueScreen is null) return;
        var h = ContinueScreen.ActualHeight;
        var w = ScreenHost.ActualWidth;
        if (h <= 0 || w <= 0) return;

        _applyingContinue = true;
        try
        {
            var tier = h >= 620 ? 2 : h >= 500 ? 1 : 0;
            // 宽度不够时立绘、右侧动作会互相挤：立绘收掉、动作移到标题下方单独一行。
            var narrow = w < 900;
            var roomy = w >= 1040;

            ContinueStack.Margin = new Thickness(0, tier == 2 ? 20 : tier == 1 ? 14 : 10, 0, 0);
            ContinueStack.RowSpacing = tier == 2 ? 18 : tier == 1 ? 12 : 8;
            RecentSection.Spacing = tier == 2 ? 6 : tier == 1 ? 4 : 2;

            MainInstanceCard.MinHeight = tier == 2 ? 240 : tier == 1 ? 200 : 176;
            ContinueContent.Padding = narrow
                ? new Thickness(24, 20, 24, 20)
                : tier == 2
                    ? new Thickness(36, 28, 34, 30)
                    : tier == 1
                        ? new Thickness(28, 20, 28, 22)
                        : new Thickness(20, 14, 20, 16);

            // 太窄才收掉立绘；其余按卡片高度给足尺寸（立绘站在启动键上方，所以能放大）。
            ContinueFigure.Visibility = (w < 640 || !_continueHasFigure)
                ? Visibility.Collapsed
                : Visibility.Visible;
            var figH = tier == 2 ? (roomy && !narrow ? 300 : 262)
                : tier == 1 ? (roomy && !narrow ? 240 : 216)
                : 160;
            ContinueFigure.Height = figH;
            ContinueFigure.Width = Math.Round(figH * 0.55);
            ContinueFigure.Margin = new Thickness(0, 0, narrow ? 20 : 26, 0);

            // 不动：启动按钮永远在右下角，标题/副标题在左下，立绘站在按钮正上方。
            Grid.SetRow(ContinueActions, 1);
            Grid.SetColumn(ContinueActions, 1);
            ContinueActions.HorizontalAlignment = HorizontalAlignment.Right;
            ContinueActions.Margin = new Thickness(24, 0, 0, 0);
            ContinueLaunchButton.Height = tier == 2 ? 52 : tier == 1 ? 46 : 42;
            ContinueSwitchButton.Height = ContinueLaunchButton.Height;
            ContinueAddButton.Height = ContinueLaunchButton.Height;

            ContinueFigure.HorizontalAlignment = HorizontalAlignment.Right;
            ContinueFigure.VerticalAlignment = VerticalAlignment.Bottom;
            ContinueFigure.Margin = new Thickness(0, 0, ContinueFigure.Margin.Right,
                ContinueContent.Padding.Bottom + ContinueLaunchButton.Height + 34);

            ContinueName.FontSize = narrow ? (tier == 2 ? 34 : 27) : (tier == 2 ? 44 : 32);
            ContinueName.MaxWidth = Math.Max(160, w - 300);
            ContinueMeta.MaxWidth = ContinueName.MaxWidth;

            AccountName.MaxWidth = narrow ? 180 : 240;
            AccountActions.Spacing = narrow ? 6 : 8;
            AccountBar.Height = tier == 2 ? 76 : tier == 1 ? 68 : 58;
            AccountBar.Margin = new Thickness(0, 0, 0, tier == 2 ? 16 : tier == 1 ? 10 : 6);
            AccountName.FontSize = tier == 2 ? 16 : tier == 1 ? 15 : 14;
            var avatar = tier == 2 ? 56 : tier == 1 ? 46 : 38;
            AccountHeadBox.Width = avatar;
            AccountHeadBox.Height = avatar;
            AccountHeadHost.Width = avatar;
            AccountHeadHost.Height = avatar;
            AccountHeadFigure.Width = avatar;
            AccountHeadFigure.Height = avatar;
            foreach (var child in AccountActions.Children)
            {
                if (child is Button button)
                {
                    button.Height = tier == 2 ? 36 : tier == 1 ? 32 : 28;
                    button.Padding = tier == 2 ? new Thickness(14, 0, 14, 0) : new Thickness(10, 0, 10, 0);
                }
            }

            var count = tier == 2
                ? (w >= 1080 ? 4 : w >= 900 ? 3 : 2)
                : tier == 1
                    ? (w >= 1080 ? 3 : 2)
                    : (w >= 1000 ? 2 : 1);
            var metaMode = tier == 2 ? 2 : tier == 1 ? 1 : 0;
            var itemHeight = tier == 2 ? 56 : tier == 1 ? 48 : 44;

            if (forceRecent || count != _continueRecentCount || metaMode != _continueRecentMeta || Math.Abs(itemHeight - _continueRecentHeight) > 0.1)
            {
                _continueRecentCount = count;
                _continueRecentMeta = metaMode;
                _continueRecentHeight = itemHeight;
                if (_snapshot is not null) BuildContinueRecent(_snapshot, count, metaMode, itemHeight);
            }

        }
        finally
        {
            _applyingContinue = false;
        }
    }

    private bool _screenPressed;
    private double _screenPressX;
    private uint _screenPointerId;

    private void ScreenHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_simpleMode) return;
        _screenPressed = true;
        _screenDragging = false;
        _screenPointerId = e.Pointer.PointerId;
        _screenPressX = e.GetCurrentPoint(ScreenHost).Position.X;
        _screenDragStartX = _screenPressX - _screen * ScreenHost.ActualWidth;
    }

    private void ScreenHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_simpleMode || !_screenPressed || e.Pointer.PointerId != _screenPointerId) return;
        var width = ScreenHost.ActualWidth;
        if (width <= 0) return;
        var x = e.GetCurrentPoint(ScreenHost).Position.X;
        if (!_screenDragging)
        {
            // 超过 12 px 才算拖动：普通点击按钮不会让两屏晃动，也不会截走指针。
            if (Math.Abs(x - _screenPressX) < 12) return;
            _screenDragging = true;
            try { ScreenHost.CapturePointer(e.Pointer); } catch { }
        }
        var dx = x - _screenDragStartX;
        // 拖动时两屏一起跟手，松手再吸附。
        _continueShift.X = dx - _screen * width;
        _boardShift.X = dx + (_screen == 0 ? width : 0);
    }

    private void ScreenHost_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishScreenDrag();

    private void ScreenHost_PointerLost(object sender, PointerRoutedEventArgs e) => FinishScreenDrag();

    /// <summary>
    /// 松手 / 取消 / 失去捕获都走这里。以前目标屏等于当前屏时 GoToScreen 直接 return，
    /// 两屏就停在拖到一半的位置；现在无论翻不翻页都会动画归位。
    /// </summary>
    private void FinishScreenDrag()
    {
        var wasDragging = _screenDragging;
        _screenPressed = false;
        _screenDragging = false;
        if (!wasDragging || !_simpleMode) return;
        try { ScreenHost.ReleasePointerCaptures(); } catch { }
        var width = ScreenHost.ActualWidth;
        if (width <= 0) { LayoutScreens(false); return; }
        var offset = -_continueShift.X;
        var target = Math.Clamp((int)Math.Round(offset / width), 0, 1);
        if (target == _screen)
        {
            LayoutScreens(true);
            UpdateScreenChrome();
            PushSafeArea();
        }
        else
        {
            GoToScreen(target, true);
        }
    }

    private void HomePage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_simpleMode) return;
        if (e.Key == Windows.System.VirtualKey.Right) { GoToScreen(1, true); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Left) { GoToScreen(0, true); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter && _screen == 0) { LaunchContinue(_continueSelectedId); e.Handled = true; }
    }

    private void TabContinue_Click(object sender, RoutedEventArgs e) => GoToScreen(0, true);

    private void TabBoard_Click(object sender, RoutedEventArgs e) => GoToScreen(1, true);

    /// <summary>
    /// 页头自适应：窗口高时标题 28、上下留白 6/6；窗口矮（≤820 内容高）时标题 24、留白 4/4，
    /// 把垂直空间让给内容区。宽度不参与，避免拖动窗口时字号来回跳。
    /// </summary>
    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = e.NewSize.Height;
        var w = e.NewSize.Width;
        if (h <= 0 || w <= 0) return;
        var tall = h >= 820;
        var narrow = w < 860;
        var title = narrow ? 22.0 : tall ? 28.0 : 24.0;
        if (Math.Abs(PageTitleText.FontSize - title) > 0.1) PageTitleText.FontSize = title;

        // 页头在窄窗口先让日期消失，只留标题、两个 tab 和图标按钮。
        PageDateText.Visibility = w >= 720 ? Visibility.Visible : Visibility.Collapsed;
        ApplyLayoutButtonChrome(w >= 820);
        ScreenSwitcher.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 8, 0);

        var side = narrow ? 16.0 : 24.0;
        var vertical = tall ? 6.0 : 4.0;
        // 顶部留白和其它页一致（标题落在同一个纵向位置）；窗口矮时少留一点
        var top = tall ? 33.0 : 25.0;
        var pad = new Thickness(side, top, side, vertical);
        if (PageRoot.Padding != pad) PageRoot.Padding = pad;
        PushSafeArea();
    }

    /// <summary>
    /// 启动封面优先级：实例自定义图 → 这个实例本次游玩期间挑出的干净截图
    /// → 最近一张游戏截图（文件夹里有图就先用上） → 内置品牌图。
    /// sidecar 的时间窗只用来挑“本次游玩”的截图；没有命中的实例仍要有一张真截图可看。
    /// </summary>
    private ImageSource? ResolveContinueCover(Snapshot snapshot, VersionInfo? version)
    {
        var prefs = App.MainWindow?.Preferences;
        string? custom = null;
        if (version is not null && prefs?.HomeCovers is { } covers)
        {
            if (!covers.TryGetValue(InstanceSelection.Key(version.Root, version.Id), out custom)
                && !covers.TryGetValue(version.Id, out custom))
            {
                custom = covers.FirstOrDefault(kv =>
                    string.Equals(kv.Key, version.Id, StringComparison.OrdinalIgnoreCase)).Value;
            }
        }
        var mode = version is null ? "default" : prefs?.HomeCoverModes.GetValueOrDefault(
            InstanceSelection.Key(version.Root, version.Id), string.IsNullOrEmpty(custom) ? "default" : "custom") ?? "default";
        if (mode != "custom") custom = null;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            var customImage = LoadImage(custom);
            if (customImage is not null) return customImage;
        }

        var picked = "";
        if (mode == "latest" && version is not null && _instanceShots is { } shots
            && string.Equals(shots.Version, version.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_instanceShotsRoot, version.Root, StringComparison.OrdinalIgnoreCase))
            picked = shots.Latest;
        if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
        {
            var shot = LoadImage(picked);
            if (shot is not null) return shot;
        }

        return LoadImage(Path.Combine(AppContext.BaseDirectory, "Assets", "cover-fallback.jpg"))
               ?? LoadImage(Path.Combine(AppContext.BaseDirectory, "Assets", "cover.jpg"));
    }

    private void ApplyContinueSnapshot(Snapshot snapshot)
    {
        var versions = snapshot.Versions.Where(v => v.Valid && !v.Hidden).ToList();
        // 首页选择以偏好里的 HomeSelectedInstance 为准：本页单击切换、版本页切换、
        // 版本页启动成功都会写它，回到首页或重启后都能对上。
        var resolved = InstanceSelection.Resolve(snapshot, App.MainWindow?.Preferences);
        _continueSelectedId = resolved?.Id ?? "";

        var current = resolved ?? snapshot.Current;
        var cover = ResolveContinueCover(snapshot, current);
        ContinueCover.Source = cover;
        if (current is null)
        {
            ContinueName.Text = "还没有实例";
            ContinueMeta.Text = "";
            ContinueMeta.Visibility = Visibility.Collapsed;
            ContinueAgoText.Text = "";
            ContinueAgoText.Visibility = Visibility.Collapsed;
            ContinueSwitchButton.Visibility = Visibility.Collapsed;
            ContinueSettingsButton.Visibility = Visibility.Collapsed;
            ContinueLaunchButton.Visibility = Visibility.Collapsed;
            ContinueAddButton.Visibility = Visibility.Visible;
            ContinueLaunchButton.IsEnabled = false;
            ContinueSettingsButton.IsEnabled = false;
            ApplyContinueLaunchState("");
        }
        else
        {
            ContinueName.Text = current.Id;
            ContinueMeta.Visibility = Visibility.Visible;
            ContinueSwitchButton.Visibility = Visibility.Visible;
            ContinueSettingsButton.Visibility = Visibility.Visible;
            ContinueLaunchButton.Visibility = Visibility.Visible;
            ContinueAddButton.Visibility = Visibility.Collapsed;
            var ago = string.IsNullOrWhiteSpace(current.AgoText) ? "还没启动过" : $"上次游玩 {current.AgoText}";
            ContinueMeta.Text = current.VersionLine;
            ContinueAgoText.Text = ago;
            ContinueAgoText.Visibility = Visibility.Visible;
            ContinueLaunchButton.IsEnabled = true;
            ContinueSettingsButton.IsEnabled = true;
            ApplyContinueLaunchState(current.Id);
        }

        var account = snapshot.Account;
        var hasFigure = account is not null
            && !string.IsNullOrWhiteSpace(account.SkinPath)
            && File.Exists(account.SkinPath);
        _continueHasFigure = hasFigure;
        ContinueFigure.SkinPath = account?.SkinPath ?? "";
        ContinueFigure.CapePath = account?.CapePath ?? "";
        ContinueFigure.Slim = account?.Slim ?? false;
        ContinueFigure.Visibility = hasFigure ? Visibility.Visible : Visibility.Collapsed;

        AccountName.Text = account?.Name ?? "还没有账户";
        AccountType.Text = account?.TypeDisplay ?? "离线账户";
        AccountTypeChip.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;

        // 头像统一用 SkinHeadView（不依赖后端预渲染的头像）；皮肤还没取到时它显示默认头像，不再露出底色块
        AccountHeadHost.Visibility = Visibility.Collapsed;
        AccountHeadFigure.SkinPath = account?.SkinPath ?? "";
        AccountHeadFigure.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;

        ApplyContinueResponsive(forceRecent: true);
    }

    private void BuildContinueRecent(Snapshot snapshot, int count, int metaMode, double itemHeight)
    {
        RecentInstanceRow.Children.Clear();
        RecentInstanceRow.ColumnDefinitions.Clear();

        var list = snapshot.Versions
            .Where(v => v.Valid && !v.Hidden && v.LastPlayed > 0)
            .OrderByDescending(v => v.LastPlayed)
            .Take(count)
            .ToList();

        // 一条记录都没有：像 Win11 开始菜单「推荐的项目」那样只放一段说明文字，不再摆几行「还没有启动记录」的占位。
        if (list.Count == 0)
        {
            RecentInstanceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            RecentInstanceRow.Children.Add(new TextBlock
            {
                Text = "在你使用本启动器进行启动游戏之后，这里才会显示最近启动的实例。",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
                Margin = new Thickness(2, 8, 0, 8),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        for (var i = 0; i < count; i++)
        {
            RecentInstanceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i >= list.Count) continue;            // 记录不够填满的格子留空，不放占位
            var card = BuildRecentInstanceCard(list[i], metaMode, itemHeight);
            Grid.SetColumn(card, i);
            RecentInstanceRow.Children.Add(card);
        }
    }

    private static CornerRadius ResCorner(string key) =>
        Application.Current.Resources.TryGetValue(key, out var raw) && raw is CornerRadius c
            ? c
            : new CornerRadius(16);

    private Border BuildRecentInstanceCard(VersionInfo? version, int metaMode, double itemHeight)
    {
        var isCurrent = InstanceSelection.Same(version, InstanceSelection.Resolve(_snapshot, App.MainWindow?.Preferences));

        // Win11 资源管理器「最近使用」样式：无卡片、无彩色装饰，图标 + 两行文本，悬停才出浅底。
        var card = new Border
        {
            Style = (Style)Resources[isCurrent ? "ContinueRecentItemCurrentStyle" : "ContinueRecentItemStyle"],
            Height = itemHeight,
        };

        var iconSize = itemHeight < 50 ? 28.0 : 32.0;
        var grid = new Grid { ColumnSpacing = itemHeight < 50 ? 10 : 12, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Width = iconSize,
            Height = iconSize,
            CornerRadius = (CornerRadius)Application.Current.Resources["RowCornerRadius"],
            Background = (Brush)Application.Current.Resources["AppChipBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 有实例自己的 icon.png 就用它；没有就统一用 Minecraft 图标（不用字母、不用系统字形）。
        string? iconFile = null;
        try
        {
            if (version is not null && !string.IsNullOrWhiteSpace(version.Path))
            {
                var candidate = System.IO.Path.Combine(version.Path, "icon.png");
                if (System.IO.File.Exists(candidate)) iconFile = candidate;
            }
        }
        catch { }
        // 缺图标时所有实例共用同一张 Minecraft 图标，按圆角裁切。
        icon.Background = null;
        icon.Child = new Image
        {
            Source = iconFile is not null
                ? new BitmapImage(new Uri(iconFile)) { DecodePixelWidth = 64 }
                : Services.InstanceIcon.ForLoader(version?.Loader),
            Stretch = Stretch.UniformToFill,
        };
        icon.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, iconSize, iconSize) };
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = itemHeight < 50 ? 0 : 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = version?.Id ?? "还没有启动记录",
            Style = (Style)Resources["ContinueRecentNameStyle"],
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
        };
        text.Children.Add(name);

        var meta = "—";
        if (version is not null)
        {
            meta = metaMode switch
            {
                2 or 1 => $"{version.VersionLine} · {version.AgoText}",
                _ => string.IsNullOrWhiteSpace(version.AgoText) ? "—" : version.AgoText,
            };
        }
        text.Children.Add(new TextBlock
        {
            Style = (Style)Resources["ContinueRecentMetaStyle"],
            Text = meta,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        if (version is not null)
        {
            AutomationProperties.SetName(card, $"最近实例 {version.Id}");
            AutomationProperties.SetHelpText(card, "单击切换当前实例，双击切换并启动");
            ToolTipService.SetToolTip(card, "单击切换当前实例 · 双击切换并启动");
            card.Tag = version;
            card.Tapped += RecentInstanceCard_Tapped;
            card.DoubleTapped += RecentInstanceCard_DoubleTapped;
        }

        return card;
    }

    private void RecentAll_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, "versions");

    private void RecentInstanceCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: VersionInfo version }) return;
        SelectContinueInstance(version.Id, version.Root);
    }

    private void RecentInstanceCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: VersionInfo version }) return;
        SelectContinueInstance(version.Id, version.Root);
        LaunchContinue(version.Id, version.Root);
    }

    private async void SelectContinueInstance(string id, string root = "")
    {
        var version = _snapshot?.Versions.FirstOrDefault(v =>
            string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)
            && (root.Length == 0 || string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase)));
        if (InstanceSelection.Same(version, _snapshot?.Current)) return;
        if (version is null) return;
        try
        {
            var (ok, message) = await InstanceSelection.SelectAsync(version);   // 乐观更新 + 写后端 + 失败回滚，都在服务里
            if (!ok) { ShowToast(InfoBarSeverity.Warning, "切换失败", message); return; }
            ShowToast(InfoBarSeverity.Success, "已切换", $"当前实例已切到 {id}");
        }
        catch (Exception ex) { App.LogCrash("HomeSelect", ex); }
    }

    /// <summary>还没有实例时的主按钮：去「获取」页下载第一个版本。</summary>
    private void ContinueAdd_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("download");

    private void ContinueSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_continueSelectedId.Length > 0) InstanceSettingsRequested?.Invoke(this, _continueSelectedId);
    }

    // ---- 启动按钮右侧小三角：展开实例列表（和页头胶囊同一套 Picker / 同一条切换路径）
    private void ContinuePickerFlyout_Opening(object? sender, object e) => ContinuePicker.Reload();

    private void ContinuePickerFlyout_Opened(object? sender, object e) => ContinuePicker.FocusFilter();

    private void ContinueManageAll_Click(object sender, RoutedEventArgs e)
    {
        ContinuePickerFlyout.Hide();
        NavigateRequested?.Invoke(this, "versions");
    }

    private async void ContinuePicker_Picked(VersionInfo version)
    {
        ContinuePickerFlyout.Hide();
        var (ok, message) = await InstanceSelection.SelectAsync(version);
        if (!ok) ShowToast(InfoBarSeverity.Warning, "切换失败", message);
    }

    private async void ContinueLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_launching) return;
        if (GameSessions.IsRunning(_continueSelectedId, _snapshot?.Current?.Root ?? ""))
        {
            await StopGameAsync(_continueSelectedId);
            return;
        }

        LaunchContinue(_continueSelectedId);
    }

    private void GameSessions_Changed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyContinueLaunchState(_continueSelectedId);
        });
    }

    /// <summary>首页大卡按钮在「启动」和「结束游戏」之间切换。</summary>
    private void ApplyContinueLaunchState(string versionId)
    {
        var running = GameSessions.IsRunning(versionId, _snapshot?.Current?.Root ?? "");
        ContinueLaunchText.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ContinueStopText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ContinueLaunchIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ContinueStopIcon.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ContinueLaunchButton.Style = (Style)Resources[running ? "StopGameButtonStyle" : "ContinueLaunchButtonStyle"];
        ContinueSwitchButton.Style = ContinueLaunchButton.Style;
        AutomationProperties.SetName(ContinueLaunchButton, running ? "结束当前实例的游戏" : "启动当前实例");
    }

    private void ShowLaunchOverlay(string versionId, string stage)
    {
        _launchOverlayRevision++;
        _launchOverlayVersion = versionId;
        LaunchOverlayName.Text = versionId;
        LaunchCinemaCover.Source = ContinueCover.Source;
        var selected = _snapshot?.Current;
        LaunchCinemaIcon.Source = InstanceIcon.For(selected);
        LaunchOverlayTitle.Text = "正在准备游戏";
        LaunchOverlayStage.Text = stage;
        LaunchOverlay.Visibility = Visibility.Visible;
        LaunchOverlay.Opacity = 0;
        LaunchOverlayRing.IsActive = true;
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, LaunchOverlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        story.Begin();
        LaunchOverlay.UpdateLayout();
        try
        {
            var origin = ContinueTextStack.TransformToVisual(LaunchCinemaHeading).TransformPoint(new Windows.Foundation.Point());
            var motion = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var (property, from) in new[] { ("X", origin.X), ("Y", origin.Y) })
            {
                var move = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = from, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(520)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(move, LaunchTitleTransform);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(move, property);
                motion.Children.Add(move);
            }
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) motion.Begin();
            else { LaunchTitleTransform.X = 0; LaunchTitleTransform.Y = 0; }
        }
        catch { LaunchTitleTransform.X = 0; LaunchTitleTransform.Y = 0; }
    }

    private int _launchOverlayRevision;
    private async void HideLaunchOverlay()
    {
        var revision = ++_launchOverlayRevision;
        _launchOverlayVersion = "";
        var fade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, LaunchOverlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        fade.Children.Add(animation);
        fade.Begin();
        await Task.Delay(230);
        if (revision != _launchOverlayRevision) return;
        LaunchOverlay.Visibility = Visibility.Collapsed;
        LaunchOverlayRing.IsActive = false;
    }

    private void SetLaunchStage(string stage)
    {
        if (LaunchOverlay.Visibility != Visibility.Visible || stage.StartsWith('@')) return;
        var split = stage.IndexOf('|');
        LaunchOverlayStage.Text = split >= 0 ? stage[(split + 1)..] : stage;
    }

    /// <summary>启动后等游戏进程真正出现在运行台账里，最多等 14 秒。</summary>
    private async Task WaitForLaunchOverlayAsync(string versionId)
    {
        SetLaunchStage("游戏进程已启动，正在进入游戏…");
        for (var i = 0; i < 20; i++)
        {
            await GameSessions.RefreshAsync();
            var game = GameSessions.Find(versionId, _snapshot?.Current?.Root ?? "");
            if (game?.Pid > 0)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(game.Pid);
                    if (process.HasExited || process.MainWindowHandle != IntPtr.Zero) break;
                }
                catch (ArgumentException) { break; }
            }
            await Task.Delay(700);
        }
        HideLaunchOverlay();
    }

    private async Task StopGameAsync(string versionId)
    {
        var row = GameSessions.Find(versionId, _snapshot?.Current?.Root ?? "");
        var result = await App.Backend.StopGameAsync(row?.Pid ?? 0, versionId);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已结束游戏" : "结束失败", result.Message);
        await GameSessions.RefreshAsync();
        ApplyContinueLaunchState(versionId);
        if (_snapshot is not null) _ = RefreshAsync(true);
    }

    public async Task<bool> StartInstanceAsync(VersionInfo target)
    {
        if (_launching) return false;
        _snapshot ??= await App.Backend.GetSnapshotAsync();
        if (_snapshot is not null)
        {
            _snapshot.Current = target;
            ApplyContinueSnapshot(_snapshot);
        }
        LaunchContinue(target.Id, target.Root);
        return true;
    }

    private async void LaunchContinue(string versionId, string targetRoot = "")
    {
        if (string.IsNullOrWhiteSpace(versionId) || _launching) return;
        var version = InstanceSelection.Resolve(_snapshot, App.MainWindow?.Preferences);
        if (version?.Id != versionId || (targetRoot.Length > 0 && !string.Equals(version?.Root, targetRoot, StringComparison.OrdinalIgnoreCase)))
            version = _snapshot?.Versions.FirstOrDefault(v =>
                string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase)
                && (targetRoot.Length == 0 || string.Equals(v.Root, targetRoot, StringComparison.OrdinalIgnoreCase)));
        var root = version?.Root ?? "";
        if (GameSessions.IsRunning(versionId, root))
        {
            ShowToast(InfoBarSeverity.Informational, "已经在运行", $"{versionId} 的游戏进程还开着。");
            return;
        }
        var task = TaskCenter.Add($"启动 {versionId}", "正在检查实例文件", "home", targetIcon: InstanceIcon.For(version));
        var progress = new Progress<string>(stage =>
        {
            SetLaunchStage(stage);
            TaskCenter.SetStage(task, stage);
        });
        _launching = true;
        SetLaunchEnabled(false);
        ShowLaunchOverlay(versionId, "正在切换实例…");
        try
        {
            // 首页直接点启动时也要把 selected 落到这个实例，否则版本页的「当前」
            // 与首页大卡会各说各话；启动本身也带上实例所在目录。
            if (version is not null)
            {
                var (switched, switchMessage) = await InstanceSelection.SelectAsync(version);
                if (!switched)
                {
                    TaskCenter.Fail(task, switchMessage);
                    ShowToast(InfoBarSeverity.Warning, "切换失败", switchMessage);
                    return;
                }
            }

            if (!await VersionHealth.EnsureReadyAsync(versionId, XamlRoot, version?.Root ?? "", progress, task.Id))
            {
                if (TaskCenter.IsCancelled(task.Id)) return;
                TaskCenter.Fail(task, "实例文件自动补全未完成。");
                ShowToast(InfoBarSeverity.Error, "启动未完成", "实例文件自动补全未完成。");
                return;
            }

            if (TaskCenter.IsCancelled(task.Id)) return;
            var result = await App.Backend.LaunchAsync(versionId, progress, root: root, taskId: task.Id);
            if (TaskCenter.IsCancelled(task.Id)) return;
            if (result.Ok)
            {
                TaskCenter.SetStage(task, "运行中");
                LaunchWatcher.Track(versionId, task, 0, root);
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                await WaitForLaunchOverlayAsync(versionId);
                if (string.Equals(App.MainWindow?.Preferences.AfterLaunch, "minimize", StringComparison.OrdinalIgnoreCase))
                    App.MainWindow?.MinimizeForGame();
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                TaskCenter.Fail(task, result.Message, result.Failure);
                ShowToast(InfoBarSeverity.Error, "启动失败", result.Message);
            }
            _ = GameSessions.RefreshAsync();
        }
        catch (Exception ex)
        {
            TaskCenter.Fail(task, ex.Message);
            ShowToast(InfoBarSeverity.Error, "启动失败", Diagnostics.FriendlyMessage(ex));
        }
        finally
        {
            HideLaunchOverlay();
            _launching = false;
            SetLaunchEnabled(true);
        }
    }

    private void AccountSkin_Click(object sender, RoutedEventArgs e)
    {
        AccountPage.PendingSubView = "skin";
        NavigateRequested?.Invoke(this, "account");
    }

    private void AccountManage_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, "account");

    // ---------- 生命周期 ----------

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        // MainWindow 在首页构造之后才挂到 App.MainWindow，构造期读不到偏好；
        // 冷启动时在这里补读一次「首页形态 / 启动打开哪一屏」，否则设置里改了也不生效。
        if (!_preferencesApplied)
        {
            _preferencesApplied = true;
            _homeLayout = ResolveHomeLayout();
            _screen = ResolveStartScreen();
        }

        if (!_layoutLoaded)
        {
            _layoutLoaded = true;
            LoadLayoutPreferences();
            _currentPage = 0;
            ApplyResponsiveColumns(force: true);
            UpdatePageIndicator();
            ApplyLayout();
        }
        ApplyHomeMode();
        // 首帧尺寸可能还是 0，测完布局再定一次位移。
        DispatcherQueue.TryEnqueue(() =>
        {
            LayoutScreens(false);
            ApplyContinueResponsive();
            ApplyResponsiveColumns(force: true);
            UpdateScreenChrome();
            PushSafeArea();
        });
        UpdateClock();
        _clockTimer.Start();
        GameSessions.Changed -= GameSessions_Changed;
        GameSessions.Changed += GameSessions_Changed;
        ApplyContinueLaunchState(_continueSelectedId);
        HookWindowLifecycle();
        ShowLoading();
        // 负一屏优先显示内容，组件板仍按原逻辑走三态。
        if (_simpleMode && _screen == 0) HideStates();
        _ = RefreshAsync(true);
        _ = InitializeMediaAsync();
        _ = InitializeNewsAsync();
    }

    private void InstanceSelection_Changed(VersionInfo v) => DispatcherQueue.TryEnqueue(() =>
    {
        _continueSelectedId = v.Id;
        if (_snapshot is not null) ApplyContinueSnapshot(_snapshot);
    });

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _clockTimer.Stop();
        _toastTimer.Stop();
        GameSessions.Changed -= GameSessions_Changed;
        InstanceSelection.Changed -= InstanceSelection_Changed;
        UnhookWindowLifecycle();
    }

    /// <summary>
    /// 机器睡眠/唤醒后 DispatcherTimer 会停摆：唤醒时窗口被重新激活，
    /// 这里挂 Window.Activated + Window.VisibilityChanged，回到前台立刻重算时间并重启计时器。
    /// </summary>
    private void HookWindowLifecycle()
    {
        if (_hookedWindow is not null) return;
        try
        {
            var window = App.MainWindow;
            if (window is null) return;
            window.Activated += MainWindow_Activated;
            window.VisibilityChanged += MainWindow_VisibilityChanged;
            _hookedWindow = window;
        }
        catch
        {
            // 拿不到窗口就退回纯计时器，不影响页面可用。
        }
    }

    private void UnhookWindowLifecycle()
    {
        if (_hookedWindow is null) return;
        try
        {
            _hookedWindow.Activated -= MainWindow_Activated;
            _hookedWindow.VisibilityChanged -= MainWindow_VisibilityChanged;
        }
        catch
        {
        }
        _hookedWindow = null;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        ResumeAfterSleep();
    }

    private void MainWindow_VisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (args.Visible) ResumeAfterSleep();
    }


    /// <summary>按实例 id 稳定映射到 6 个语义色之一（同一实例每次都是同一个颜色）。</summary>
    private static int SemanticTintFor(string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        var sum = 0;
        foreach (var ch in id) sum = (sum * 31 + ch) & 0x7FFFFFF;
        return sum % 6;
    }

    /// <summary>唤醒/回到前台：立刻用 DateTime.Now 重算时钟，并让「N 小时前」这类相对时间重新求值。</summary>
    private void ResumeAfterSleep()
    {
        var now = DateTime.Now;
        if ((now - _lastResume).TotalSeconds < 2) return;
        _lastResume = now;

        try
        {
            _clockTimer.Stop();
            UpdateClock();
            _clockTimer.Start();
        }
        catch
        {
        }

        // AgoText 是「按绝对时间戳现算」的属性，重新写一遍文本即可刷新相对时间。
        if (_snapshot is not null)
        {
            try
            {
                ApplySnapshot(_snapshot);
            }
            catch
            {
            }
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText1.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockDate.Text = now.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
        ClockWeek.Text = now.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"));
        PageDateText.Text = now.ToString("M月d日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    // ---------- 布局模型 ----------

    // 默认排布（B 定稿：4 列 × 3 行、格子 228 方块 / 窄窗 201×165、间距 16）：
    // 第一页：当前实例 2×2、玩家档案 1×2、时钟 1×1、服务状态 1×1、最近启动 2×1、每日一帖 2×1。
    // 第二页：截图墙 4×2 + 任务 4×1。
    private static readonly (string Id, int Page, int Row, int Col, int W, int H)[] DefaultSpots =
    {
        ("instance", 0, 0, 0, 2, 2),
        ("profile", 0, 0, 2, 1, 2),
        ("clock", 0, 0, 3, 1, 1),
        ("status", 0, 1, 3, 1, 1),
        ("recent", 0, 2, 0, 2, 1),
        ("tip", 0, 2, 2, 2, 1),
        ("shots", 1, 0, 0, 4, 2),
        ("tasks", 1, 2, 0, 4, 1),
    };

    private void BuildDefaultLayout() => BuildVariantLayout(DefaultSpots);

    private void BuildVariantLayout((string Id, int Page, int Row, int Col, int W, int H)[] spots)
    {
        _layout.Clear();
        foreach (var s in spots)
        {
            if (_hidden.Contains(s.Id)) continue;
            _layout.Add(new Placement { Id = s.Id, Page = s.Page, Row = s.Row, Col = s.Col, W = s.W, H = s.H });
        }
    }

    /// <summary>当前布局是否正好等于某套内置排布（用来判断「用户有没有自己摆过」）。</summary>
    private bool LayoutMatches((string Id, int Page, int Row, int Col, int W, int H)[] spots)
    {
        var expected = spots.Where(s => !_hidden.Contains(s.Id)).ToList();
        if (_layout.Count != expected.Count) return false;
        foreach (var s in expected)
        {
            var p = _layout.FirstOrDefault(x => x.Id == s.Id);
            if (p is null || p.Page != s.Page || p.Row != s.Row || p.Col != s.Col || p.W != s.W || p.H != s.H)
                return false;
        }
        return true;
    }

    private void LoadLayoutPreferences()
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null)
        {
            BuildDefaultLayout();
            return;
        }

        _hidden.Clear();
        foreach (var id in SplitCsv(prefs.HomeWidgetHidden))
            if (_defs.ContainsKey(id)) _hidden.Add(id);

        var pages = prefs.HomeWidgetPages?.Trim();
        var migrated = false;
        if (string.IsNullOrEmpty(pages) || !TryParsePages(pages))
        {
            BuildLegacyLayout(prefs);
            migrated = true;
        }
        else if (!LayoutFitsBoard())
        {
            // 旧版是 4×6 / 4×5 布局，或旧排布把两页塞进同一页（pinned + media 都放在第 3 行）；
            // 只要越界或总格数超过 4×3，直接按 B 默认重建，避免卡片在第三行叠成一团。
            _hidden.Clear();
            BuildDefaultLayout();
            migrated = true;
        }

        _layout.RemoveAll(x => _hidden.Contains(x.Id));

        // 默认排布里没列出来的组件（时钟 / 服务状态 / 快捷操作）既不在 _layout、也不在 _hidden，
        // 那样页脚的「添加小组件」就再也找不回它们；这里统一补登记一次。
        foreach (var id in _defs.Keys)
            if (!_layout.Exists(x => x.Id == id) && !_hidden.Contains(id))
                _hidden.Add(id);

        if (!NormalizeOverlaps())
        {
            // 旧排布本身已经超出 4×3 的容量（例如 6 张 2×2 都挤在第 1 页），
            // 无法靠「挪一格」修好，整页回到 B 默认。
            _hidden.Clear();
            BuildDefaultLayout();
            migrated = true;
        }

        _layoutCustomized = !LayoutMatches(DefaultSpots);
        if (migrated) SaveLayoutPreferences();
    }

    private void BuildLegacyLayout(AppPreferences prefs)
    {
        // 旧 Order/Sizes 字段已废弃：一律按新默认排布重建（第 1 页铺满），仅沿用「隐藏」状态。
        _layout.Clear();
        foreach (var s in DefaultSpots)
        {
            if (_hidden.Contains(s.Id)) continue;
            _layout.Add(new Placement { Id = s.Id, Page = s.Page, Row = s.Row, Col = s.Col, W = s.W, H = s.H });
        }
    }

    private bool TryParsePages(string text)
    {
        var parsed = new List<Placement>();
        var seen = new HashSet<string>();
        foreach (var pageSeg in text.Split('|'))
        {
            var colon = pageSeg.IndexOf(':');
            if (colon <= 0) return false;
            if (!int.TryParse(pageSeg[..colon], out var page) || page < 0 || page >= PageCount) return false;
            foreach (var entry in pageSeg[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('@');
                if (parts.Length != 2) return false;
                var id = parts[0].Trim();
                if (!_defs.ContainsKey(id) || !seen.Add(id)) return false;
                var rc = parts[1].Split(',');
                if (rc.Length != 4) return false;
                if (!int.TryParse(rc[0], out var row) || !int.TryParse(rc[1], out var col)
                    || !int.TryParse(rc[2], out var w) || !int.TryParse(rc[3], out var h)) return false;
                var def = _defs[id];
                if (!def.Sizes.Contains((w, h))) return false;
                parsed.Add(new Placement
                {
                    Id = id,
                    Page = page,
                    Row = Math.Clamp(row, 0, MaxRows - h),
                    Col = Math.Clamp(col, 0, _columns - w),
                    W = w,
                    H = h,
                });
            }
        }
        _layout.Clear();
        _layout.AddRange(parsed);
        return true;
    }

    private void SaveLayoutPreferences()
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        var layout = _layout;
        prefs.HomeWidgetOrder = string.Join(",", Catalog.Select(d => d.Id));
        prefs.HomeWidgetHidden = string.Join(",", _hidden);
        prefs.HomeWidgetSizes = string.Join(",", layout.Select(x => $"{x.Id}:{x.W}x{x.H}"));
        prefs.HomeWidgetPages = string.Join("|",
            Enumerable.Range(0, PageCount).Select(page =>
            {
                var entries = layout.Where(x => x.Page == page)
                    .OrderBy(x => x.Row).ThenBy(x => x.Col)
                    .Select(x => $"{x.Id}@{x.Row},{x.Col},{x.W},{x.H}");
                return $"{page}:{string.Join(";", entries)}";
            }));
        AppPreferencesStore.Save(prefs);
    }

    private void ApplyLayout()
    {
        var layout = _layout;
        foreach (var (id, card) in _cards)
        {
            var pl = layout.FirstOrDefault(x => x.Id == id && x.Page == _currentPage);
            if (pl is null)
            {
                card.Visibility = Visibility.Collapsed;
                continue;
            }
            ApplyPlacement(card, pl);
        }
        ApplyShotsVisibility();
        UpdateProfileCardMode();
    }

    /// <summary>
    /// 窄窗档（WIDGET-SPEC §8）：4 列需要 4×337+3×14=1390 的内容宽，1120 窗口内容区只有约 864，
    /// 所以列数按可用宽度决定：≥1330 用 4 列，否则 3 列；列宽交给 star 自适应，不写死 337。
    /// </summary>

    private void ContinueScreen_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyContinueResponsive();

    /// <summary>
    /// B 组件板几何：宽档限宽 960、4 列 228 方块；窄档内容区不足 940 时保持 4 列，
    /// 列宽自然收窄、行高固定 165，绝不降成 3 列。
    /// </summary>
    private void ApplyResponsiveColumns(bool force = false)
    {
        if (ActualWidth <= 0) return;
        // 首帧 ScreenHost 还没量好，退到整页宽会把 960 的版心塞进窄内容区，右边溢出。
        if (ScreenHost.ActualWidth <= 0) UpdateLayout();
        var contentWidth = ScreenHost.ActualWidth > 0 ? ScreenHost.ActualWidth : Math.Max(0, ActualWidth - 340);
        var gridWidth = Math.Min(BoardMaxWidth, Math.Max(0, contentWidth - BoardPageMargin * 2));
        if (gridWidth <= 0) return;
        var square = (gridWidth - CellSpacing * 3) / 4;
        // 页头、继续玩、组件板、翻页器共用同一个版心宽度；切标签时左右边缘不跳。
        PageHeader.Width = gridWidth;
        PageHeader.HorizontalAlignment = HorizontalAlignment.Center;
        ContinueStack.Width = gridWidth;
        ContinueStack.HorizontalAlignment = HorizontalAlignment.Center;
        BoardFooter.Width = gridWidth;
        BoardFooter.HorizontalAlignment = HorizontalAlignment.Center;
        // 方形优先：用整页高度扣掉页头/页脚/行距，给 3 行留出空间；矮窗才收到 165。
        var footerHeight = BoardFooter.ActualHeight > 0 ? BoardFooter.ActualHeight : 40;
        var availableHeight = ActualHeight - 12 - 44 - 16 - footerHeight;
        var fitByHeight = availableHeight > 0
            ? (availableHeight - CellSpacing * (MaxRows - 1)) / MaxRows
            : square;
        var rowHeight = Math.Min(square, Math.Max(BoardNarrowRowHeight, fitByHeight));
        if (!force && Math.Abs(gridWidth - _boardWidth) < 0.5 && Math.Abs(rowHeight - _boardRowHeight) < 0.5) return;
        _boardWidth = gridWidth;
        _boardRowHeight = rowHeight;

        ConfigureBoardGrid(WidgetGrid, gridWidth, rowHeight);
        ConfigureBoardGrid(SkeletonLayer, gridWidth, rowHeight);

        ApplyProfileFigureSize();
        if (_currentPage > VisiblePages - 1) _currentPage = VisiblePages - 1;
        UpdatePageIndicator();
        ApplyLayout();
    }

    private static void ConfigureBoardGrid(Grid grid, double width, double rowHeight)
    {
        grid.Width = width;
        grid.HorizontalAlignment = HorizontalAlignment.Center;
        grid.ColumnSpacing = CellSpacing;
        grid.RowSpacing = CellSpacing;
        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Clear();
        for (var i = 0; i < MaxRows; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
    }

    /// <summary>档案立绘：窄档缩小并保留右侧 24 内边距，禁止贴边裁切。</summary>
    private void ApplyProfileFigureSize()
    {
        // 默认窗口下 2×2 卡只有约 399 宽，88 宽的立绘会把文字挤没；按实际页宽收一档。
        var narrow = _columns < 4 || ActualWidth < 1200;
        // 1×2 档案卡的立绘是主体，固定居中放大；不再按右边栏 24 内边距缩到 60。
        ProfileFigure3.Width = 136;
        ProfileFigure3.Height = 260;
        ProfileFigure4.Width = narrow ? 64 : 88;
        ProfileFigure4.Height = narrow ? 132 : 176;
    }

    /// <summary>启动卡 2×2 的主操作：宽档内联在文字行右端，窄档单独一行右对齐。</summary>
    private void ApplyInstanceCta()
    {
        var wide = _columns >= 4;
        InstancePlay4.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        InstancePlay4Narrow.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyPlacement(Border card, Placement pl)
    {
        Grid.SetRow(card, pl.Row);
        Grid.SetColumn(card, pl.Col);
        Grid.SetRowSpan(card, pl.H);
        Grid.SetColumnSpan(card, pl.W);
        card.Visibility = Visibility.Visible;
        SetCardTier(pl.Id, pl.W, pl.H);
    }

    /// <summary>
    /// 尺寸即内容契约（契约 11.1 / WIDGET-SPEC §3）：每档写死显示什么，不做「自适应」。
    /// 每张卡在 XAML 里按档位拆成独立面板（Tier1=1×1、Tier2=2×1、Tier3=1×2、Tier4=2×2），
    /// 这里只负责「同一时刻只显示一个面板」，不再调整字号/颜色——样式由各面板自己决定。
    /// </summary>
    private void SetCardTier(string id, int w, int h)
    {
        switch (id)
        {
            case "instance":
                _instanceTierW = w;
                _instanceTierH = h;
                ShowTier(InstanceTier1, w == 1 && h == 1);
                ShowTier(InstanceTier2, w == 2 && h == 1);
                ShowTier(InstanceTier4, w == 2 && h == 2);
                ApplyInstanceEmptyTier();
                ApplyInstanceCta();
                break;

            case "profile":
                ShowTier(ProfileTier1, w == 1 && h == 1);
                ShowTier(ProfileTier2, w == 2 && h == 1);
                ShowTier(ProfileTier3, w == 1 && h == 2);
                ShowTier(ProfileTier4, w == 2 && h == 2);
                // 面板收起时宽高为 0，切档后补一次数据，避免头像/立绘空白。
                if (_snapshot is not null) ApplyProfile(_snapshot);
                break;

            case "clock":
                ShowTier(ClockText1, w == 1 && h == 1);
                ShowTier(ClockTier2, w == 2 && h == 1);
                break;

            case "status":
                ShowTier(StatusTier1, w == 1 && h == 1);
                ShowTier(StatusTier2, w == 2 && h == 1);
                break;

            case "recent":
                ShowTier(RecentTier1, w == 1 && h == 1);
                ShowTier(RecentTier2, w == 2 && h == 1);
                ShowTier(RecentTier4, w == 2 && h == 2);
                break;

            case "media":
                ShowTier(MediaTier1, w == 1 && h == 1);
                ShowTier(MediaTier2, w == 2 && h == 1);
                // 3 列窄档下 4×1 会被夹到 3，仍用同一条通栏面板（内部三列自适应）。
                ShowTier(MediaTier4, w >= 3 && h == 1);
                break;

            case "pinned":
                // 固定版本只有 4×1 一档：横向的实例条，不做更小尺寸。
                break;

            case "shots":
                ShowTier(ShotsTier1, w == 1 && h == 1);
                ShowTier(ShotsTier2, w == 2 && h == 1);
                ShowTier(ShotsTier4, w == 2 && h == 2);
                ShowTier(ShotsTierWide, w >= 4);
                break;

            case "quick":
                ShowTier(QuickDownload1, w == 1 && h == 1);
                ShowTier(QuickTier2, w == 2 && h == 1);
                ShowTier(QuickTier4, w == 2 && h == 2);
                break;

            case "tasks":
                ShowTier(TasksTier1, w == 1 && h == 1);
                ShowTier(TasksTier2, w == 2 && h == 1);
                ShowTier(TasksTierWide, w >= 4 && h == 1);
                break;

            case "tip":
                ShowTier(TipTier1, w == 1 && h == 1);
                ShowTier(TipTier2, w == 2 && h == 1);
                ShowTier(TipTier4, w == 2 && h == 2);
                break;
        }
    }

    private static void ShowTier(FrameworkElement panel, bool on) =>
        panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyShotsVisibility()
    {
        var pl = _layout.FirstOrDefault(x => x.Id == "shots" && x.Page == _currentPage);
        if (pl is null)
        {
            ShotsCard.Visibility = Visibility.Collapsed;
            return;
        }
        // 0 张截图时不藏卡：第二页只有截图墙 + 任务两张卡，藏掉一张整页就空了。
        ShotsCard.Visibility = Visibility.Visible;
        var empty = _shotsCount == 0;
        ShotsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShotsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 网格几何 ----------

    /// <summary>整页是否落在 4×3 画布内，且总格数不超过 12。旧版 4×6 排布会在这里被识别。</summary>
    private bool LayoutFitsBoard()
    {
        foreach (var page in _layout.GroupBy(x => x.Page))
        {
            var area = 0;
            foreach (var pl in page)
            {
                if (pl.Row < 0 || pl.Col < 0 || pl.W <= 0 || pl.H <= 0) return false;
                if (pl.Row + pl.H > MaxRows || pl.Col + pl.W > _columns) return false;
                area += pl.W * pl.H;
            }
            if (area > MaxRows * _columns) return false;
        }
        return true;
    }

    /// <summary>把重叠卡挪到第一个空位；整页放不下时返回 false，由调用方回退到 B 默认排布。</summary>
    private bool NormalizeOverlaps()
    {
        // 每页独立占位表：以前所有页共用一个 occupied，第 1 页排完会占掉前几行，
        // 第 2 页的卡就被 FindFirstFit 推到第 4/5 行（在 4 行可视区外面），整页看起来是空的。
        foreach (var page in _layout.GroupBy(x => x.Page))
        {
            var occupied = new List<CellRect>();
            foreach (var pl in page)
            {
                if (IntersectsAny(pl, occupied))
                {
                    var spot = FindFirstFit(pl.W, pl.H, occupied);
                    if (spot is null) return false;
                    pl.Row = spot.Value.Row;
                    pl.Col = spot.Value.Col;
                }
                AddRect(occupied, pl);
            }
        }
        return true;
    }

    private bool ReflowOthers(Placement dragged)
    {
        // 先快照当前页所有位置：若任一卡片无处可放（页面满），整体回滚并返回 false，
        // 由调用方把拖拽目标还原，避免两个卡片叠在同一格。
        var snapshot = _layout
            .Where(x => x.Page == _currentPage)
            .Select(x => (Placement: x, Row: x.Row, Col: x.Col))
            .ToList();
        var occupied = new List<CellRect>();
        AddRect(occupied, dragged);
        var ok = true;
        foreach (var pl in _layout.Where(x => x.Page == _currentPage && !ReferenceEquals(x, dragged)))
        {
            if (!IntersectsAny(pl, occupied))
            {
                AddRect(occupied, pl);
            }
            else
            {
                var spot = FindFirstFit(pl.W, pl.H, occupied);
                if (spot is not null)
                {
                    pl.Row = spot.Value.Row;
                    pl.Col = spot.Value.Col;
                }
                else
                {
                    ok = false;
                    break;
                }
                AddRect(occupied, pl);
            }
        }
        if (!ok)
        {
            foreach (var (pl, r, c) in snapshot)
            {
                pl.Row = r;
                pl.Col = c;
            }
            return false;
        }
        foreach (var pl in _layout.Where(x => x.Page == _currentPage))
            ApplyPlacement(_cards[pl.Id], pl);
        return true;
    }

    private (int Row, int Col)? FindFirstFit(int w, int h, List<CellRect> occupied)
    {
        for (var r = 0; r <= MaxRows - h; r++)
            for (var c = 0; c <= _columns - w; c++)
            {
                var ok = true;
                foreach (var o in occupied)
                    if (r < o.Row + o.H && o.Row < r + h && c < o.Col + o.W && o.Col < c + w)
                    {
                        ok = false;
                        break;
                    }
                if (ok) return (r, c);
            }
        return null;
    }

    private static void AddRect(List<CellRect> occupied, Placement pl) =>
        occupied.Add(new CellRect(pl.Row, pl.Col, pl.W, pl.H));

    private static bool IntersectsAny(Placement pl, List<CellRect> occupied)
    {
        foreach (var o in occupied)
            if (pl.Row < o.Row + o.H && o.Row < pl.Row + pl.H && pl.Col < o.Col + o.W && o.Col < pl.Col + pl.W)
                return true;
        return false;
    }

    private void ClampPlacement(Placement pl)
    {
        pl.Row = Math.Clamp(pl.Row, 0, MaxRows - pl.H);
        pl.Col = Math.Clamp(pl.Col, 0, _columns - pl.W);
    }

    // ---------- 编辑态拖拽（Pointer 拖拽 + 让位） ----------

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_editMode || _dragCard is not null) return;
        var card = (Border)sender;
        var pt = e.GetCurrentPoint(card);
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (e.OriginalSource is { } origin && FindButtonUpTree(origin, card)) return;
        var placement = _layout.FirstOrDefault(x => x.Id == _cardIds[card] && x.Page == _currentPage);
        if (placement is null) return;

        e.Handled = true;
        _dragCard = card;
        _dragPlacement = placement;
        _dragMoved = false;
        _dragStart = e.GetCurrentPoint(WidgetGrid).Position;
        _dragTransform.X = 0;
        // 按下即微抬，给出「已抓住」的视觉反馈；合成鼠标（截图工具）没有中间移动事件，
        // 落点改在释放时按指针位置计算（见 Grid_PointerReleased）。
        _dragTransform.Y = -6;
        card.RenderTransform = _dragTransform;
        card.Transitions.Clear();
        Canvas.SetZIndex(card, 10);
        // Grid 的绘制顺序按子元素顺序，Canvas.ZIndex 在 Grid 里不一定被尊重；
        // 把拖拽卡片挪到最后一个子元素，保证它盖在其它卡片上面（行列是显式设置，重排不影响布局）。
        if (WidgetGrid.Children.Remove(card))
            WidgetGrid.Children.Add(card);
        card.CapturePointer(e.Pointer);
    }

    private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_editMode || _dragCard is null || _dragPlacement is null) return;
        var p = e.GetCurrentPoint(WidgetGrid).Position;
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        if (!_dragMoved)
        {
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
            _dragMoved = true;
        }

        e.Handled = true;
        _dragTransform.X = dx;
        _dragTransform.Y = dy;

        var cell = CellFromPoint(dx, dy, _dragPlacement);
        if (cell != (_dragPlacement.Row, _dragPlacement.Col))
        {
            var old = (_dragPlacement.Row, _dragPlacement.Col);
            _dragPlacement.Row = cell.Row;
            _dragPlacement.Col = cell.Col;
            if (!ReflowOthers(_dragPlacement))
            {
                // 目标格非法（无处让位）：还原并回到原格重排。
                _dragPlacement.Row = old.Row;
                _dragPlacement.Col = old.Col;
                ReflowOthers(_dragPlacement);
            }
        }
    }

    private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_editMode || _dragCard is null || _dragPlacement is null) return;
        e.Handled = true;
        // 合成鼠标（截图工具）没有中间移动事件：直接把释放点当落点格，让「拖到哪放哪」在无
        // PointerMoved 的情况下也成立。真实鼠标路径不变（Grid_PointerMoved 里已跟手）。
        if (!_dragMoved)
        {
            var p = e.GetCurrentPoint(WidgetGrid).Position;
            var cell = CellFromPointAbs(p.X, p.Y, _dragPlacement);
            if (cell != (_dragPlacement.Row, _dragPlacement.Col))
            {
                var old = (_dragPlacement.Row, _dragPlacement.Col);
                _dragPlacement.Row = cell.Row;
                _dragPlacement.Col = cell.Col;
                if (ReflowOthers(_dragPlacement))
                    _dragMoved = true;
                else
                {
                    // 目标格非法：还原为原格（布局已在 ReflowOthers 内回滚）。
                    _dragPlacement.Row = old.Row;
                    _dragPlacement.Col = old.Col;
                    ReflowOthers(_dragPlacement);
                }
            }
        }
        EndDrag();
    }

    private void Grid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_editMode || _dragCard is null) return;
        EndDrag();
    }

    private void EndDrag()
    {
        var card = _dragCard;
        var pl = _dragPlacement;
        _dragCard = null;
        _dragPlacement = null;
        if (card is null) return;

        card.ReleasePointerCaptures();
        Canvas.SetZIndex(card, 0);
        card.Transitions.Add(new RepositionThemeTransition());

        if (pl is not null)
        {
            var old = (pl.Row, pl.Col);
            ClampPlacement(pl);
            if (!ReflowOthers(pl))
            {
                // 落点格在 clamp 后仍放不下（页面满）：还原原格重排，保证永不叠卡。
                pl.Row = old.Row;
                pl.Col = old.Col;
                ReflowOthers(pl);
            }
            ApplyPlacement(card, pl);
            if (_dragMoved)
            {
                _layoutCustomized = true;
                SaveLayoutPreferences();
                ShowToast(InfoBarSeverity.Success, "布局已更新", "小组件位置已记录到本地。");
            }
        }

        AnimateDragBack(card);
    }

    private void AnimateDragBack(Border card)
    {
        var sb = new Storyboard();
        var ax = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var ay = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(ax, _dragTransform);
        Storyboard.SetTargetProperty(ax, "X");
        Storyboard.SetTarget(ay, _dragTransform);
        Storyboard.SetTargetProperty(ay, "Y");
        sb.Children.Add(ax);
        sb.Children.Add(ay);
        sb.Begin();
    }

    private (int Row, int Col) CellFromPoint(double dx, double dy, Placement pl) =>
        CellFromPointAbs(_dragStart.X + dx, _dragStart.Y + dy, pl);

    private (int Row, int Col) CellFromPointAbs(double x, double y, Placement pl)
    {
        var colAct = WidgetGrid.ColumnDefinitions[0].ActualWidth;
        var rowAct = WidgetGrid.RowDefinitions[0].ActualHeight;
        var cellW = colAct + CellSpacing;
        var cellH = Math.Max(rowAct, 0) + CellSpacing;
        var cx = x + _dragCard!.ActualWidth / 2.0;
        var cy = y + _dragCard.ActualHeight / 2.0;
        var col = (int)Math.Floor(cx / cellW);
        var row = (int)Math.Floor(cy / cellH);
        return (Math.Clamp(row, 0, MaxRows - pl.H), Math.Clamp(col, 0, _columns - pl.W));
    }

    private void Card_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!_editMode) return;
        var card = (Border)sender;
        var id = _cardIds[card];
        var def = _defs[id];
        e.Handled = true;

        var menu = new MenuFlyout();
        var pl = _layout.FirstOrDefault(x => x.Id == id && x.Page == _currentPage);
        foreach (var size in def.Sizes)
        {
            if (pl is not null && pl.W == size.W && pl.H == size.H) continue;
            var item = new MenuFlyoutItem { Text = $"尺寸 {size.W}×{size.H}" };
            var w = size.W;
            var h = size.H;
            item.Click += (_, _) => ResizeWidget(id, w, h);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var hide = new MenuFlyoutItem { Text = "隐藏" };
        hide.Click += (_, _) => HideWidget(id);
        menu.Items.Add(hide);
        menu.ShowAt(card, e.GetPosition(card));
    }

    private static bool FindButtonUpTree(object source, DependencyObject root)
    {
        var cur = source as DependencyObject;
        while (cur is not null && !ReferenceEquals(cur, root))
        {
            if (cur is ButtonBase) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    // ---------- 编辑态操作 ----------

    private void ToggleLayoutEditing()
    {
        ToggleLayoutEditingCore();
    }

    private void ToggleLayoutEditingCore()
    {
        _editMode = !_editMode;
        LayoutButtonText.Text = _editMode ? "完成" : "编辑";
        LayoutIcon.Glyph = _editMode ? "\uE73E" : "\uE70F";
        ApplyLayoutButtonChrome(ActualWidth >= 820);
        EditHint.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        AddWidgetBtn.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        ResetLayoutBtn.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_editMode)
        {
            SaveLayoutPreferences();
            ShowToast(InfoBarSeverity.Success, "布局已更新", "小组件布局已记录到本地。");
        }
        ApplyLayout();
    }

    private void ResizeWidget(string id, int w, int h)
    {
        var pl = _layout.FirstOrDefault(x => x.Id == id && x.Page == _currentPage);
        if (pl is null) return;
        var old = (pl.W, pl.H);
        pl.W = w;
        pl.H = h;
        ClampPlacement(pl);
        var occupied = _layout.Where(x => x.Page == _currentPage && !ReferenceEquals(x, pl))
            .Select(x => new CellRect(x.Row, x.Col, x.W, x.H)).ToList();
        if (IntersectsAny(pl, occupied))
        {
            var spot = FindFirstFit(w, h, occupied);
            if (spot is not null)
            {
                pl.Row = spot.Value.Row;
                pl.Col = spot.Value.Col;
            }
            else
            {
                pl.W = old.W;
                pl.H = old.H;
                ShowToast(InfoBarSeverity.Warning, "放不下", "当前页没有足够空间，先挪动或隐藏其他卡片。");
                return;
            }
        }
        _layoutCustomized = true;
        ApplyLayout();
        SaveLayoutPreferences();
        ShowToast(InfoBarSeverity.Success, "布局已更新", $"「{_defs[id].Label}」已调整为 {w}×{h}。");
    }

    private void HideWidget(string id)
    {
        if (!_defs.TryGetValue(id, out var def)) return;
        _layout.RemoveAll(x => x.Id == id);
        _hidden.Add(id);
        _layoutCustomized = true;
        ApplyLayout();
        SaveLayoutPreferences();
        ShowToast(InfoBarSeverity.Success, "已隐藏", $"「{def.Label}」已隐藏，可在页脚「添加小组件」找回。");
    }

    private void AddWidget_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        if (_hidden.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "没有可添加的小组件", IsEnabled = false });
        }
        foreach (var id in _hidden)
        {
            var item = new MenuFlyoutItem { Text = _defs[id].Label };
            item.Click += (_, _) => AddWidget(id);
            menu.Items.Add(item);
        }
        menu.ShowAt(AddWidgetBtn, new Windows.Foundation.Point(0, AddWidgetBtn.ActualHeight + 4));
    }

    private void AddWidget(string id)
    {
        var def = _defs[id];
        var occupied = _layout.Where(x => x.Page == _currentPage)
            .Select(x => new CellRect(x.Row, x.Col, x.W, x.H)).ToList();
        var spot = FindFirstFit(def.DefaultW, def.DefaultH, occupied);
        if (spot is null)
        {
            ShowToast(InfoBarSeverity.Warning, "放不下", "当前页没有足够空间。");
            return;
        }
        _hidden.Remove(id);
        var pl = new Placement
        {
            Id = id,
            Page = _currentPage,
            Row = spot.Value.Row,
            Col = spot.Value.Col,
            W = def.DefaultW,
            H = def.DefaultH,
        };
        _layout.Add(pl);
        _layoutCustomized = true;
        ApplyLayout();
        SaveLayoutPreferences();
        ShowToast(InfoBarSeverity.Success, "已添加", $"「{def.Label}」已放回第 {_currentPage + 1} 页。");
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _hidden.Clear();
        _layoutCustomized = false;
        BuildDefaultLayout();
        ApplyLayout();
        SaveLayoutPreferences();
        ShowToast(InfoBarSeverity.Success, "已恢复默认", "小组件已恢复默认排布。");
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e) => ToggleLayoutEditing();

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        var pages = VisiblePages;
        _currentPage = (_currentPage - 1 + pages) % pages;
        UpdatePageIndicator();
        ApplyLayout();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var pages = VisiblePages;
        _currentPage = (_currentPage + 1) % pages;
        UpdatePageIndicator();
        ApplyLayout();
    }

    /// <summary>实际有卡的页数（空页不显示；没有卡时按 1 页算，避免 0/0）。</summary>
    private int VisiblePages
    {
        get
        {
            var layout = _layout;
            var maxPage = layout.Count == 0 ? 0 : layout.Max(x => x.Page);
            return Math.Clamp(maxPage + 1, 1, PageCount);
        }
    }

    private void UpdatePageIndicator()
    {
        if (_currentPage > VisiblePages - 1) _currentPage = VisiblePages - 1;
        var pages = Math.Max(1, VisiblePages);
        if (BoardPager.NumberOfPages != pages) BoardPager.NumberOfPages = pages;
        if (BoardPager.SelectedPageIndex != _currentPage) BoardPager.SelectedPageIndex = _currentPage;
    }

    private void BoardPager_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        var page = sender.SelectedPageIndex;
        if (page < 0 || page >= VisiblePages || page == _currentPage) return;
        _currentPage = page;
        UpdatePageIndicator();
        ApplyLayout();
    }

    private void ApplyLayoutButtonChrome(bool showText)
    {
        var editing = _editMode;
        LayoutButton.Width = editing ? (showText ? 76 : 36) : 36;
        LayoutButton.Padding = editing && showText ? new Thickness(14, 0, 14, 0) : new Thickness(0);
        LayoutButton.Background = editing
            ? ResBrush("AppAccentBrush", Color.FromArgb(255, 47, 163, 90))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LayoutButtonText.Visibility = editing && showText ? Visibility.Visible : Visibility.Collapsed;
        LayoutButtonText.Foreground = ResBrush("AppAccentTextBrush", Microsoft.UI.Colors.White);
    }

    // ---------- 数据刷新 ----------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(true);

    private async Task RefreshAsync(bool force)
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        var firstLoad = _snapshot is null;              // 已有数据时（手动刷新）不盖遮罩，只有首次读取才盖
        if (firstLoad) { HomeLoading.Visibility = Visibility.Visible; HomeLoadingRing.IsActive = true; }
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync(force);
            if (snapshot is null)
            {
                ShowError(LoadErrorMessage);
            }
            else
            {
                _snapshot = snapshot;
                ApplySnapshot(snapshot);
                HideStates();
                // 侧栏账户块由 MainWindow 统一渲染：冷启动时它先跑过一次（那时快照还没回来，
                // 会写成「未登录」），拿到快照后必须再刷一次，否则侧栏和档案卡会不一致。
                if (App.MainWindow is not null)
                    await App.MainWindow.RefreshShellAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Home refresh failed: {ex}");
            ShowError(LoadErrorMessage);
        }
        finally
        {
            HomeLoading.Visibility = Visibility.Collapsed;
            HomeLoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
            _busy = false;
        }
    }

    // ---------- 整板三态：骨架 / 错误 / 正常 ----------

    /// <summary>首次加载：只铺卡位灰块，不显示任何数字。</summary>
    private void ShowLoading()
    {
        SkeletonLayer.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        WidgetGrid.Visibility = Visibility.Collapsed;
    }

    /// <summary>拉快照失败：整板错误态 + 120 字摘要 + 原地重试。</summary>
    private void ShowError(string message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length > 120) text = text[..120] + "…";
        ErrorSummary.Text = string.IsNullOrEmpty(text)
            ? "本机数据暂时读不出来，可以重试一次。"
            : text;
        SkeletonLayer.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        WidgetGrid.Visibility = Visibility.Collapsed;
    }

    private void HideStates()
    {
        SkeletonLayer.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        WidgetGrid.Visibility = Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        ShowLoading();
        await RefreshAsync(true);
    }

    private static Brush ThemeBrush(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush) return brush;
        }
        catch
        {
            // 主题字典里没有这个键：退回透明，绝不硬编码颜色。
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void ApplySnapshot(Snapshot snapshot)
    {
        _snapshot = snapshot;
        var cover = ResolveContinueCover(snapshot, snapshot.Current);

        var current = snapshot.Current;
        var title = current?.Id ?? "没有可启动的实例";
        var sub = current is null
            ? (snapshot.GameDir.Length > 0 ? snapshot.GameDir : "前往版本管理添加一个实例。")
            : $"{current.LoaderDisplay} · {current.AgoText}";
        SetInstanceTexts(title, sub, current is not null, cover);
        ContinueCover.Source = cover;

        // 服务状态：快照只带缓存结果（unknown 表示探测还在后台跑），
        // 首屏先用它渲染，探测结果由 ApplyStatus 之后的异步请求补上。
        ApplyStatus(snapshot.Online);

        var files = snapshot.Shots?.Files ?? new List<string>();
        _shotsCount = snapshot.Shots?.Count ?? files.Count;
        SetShotImages(files);

        ApplyShotsVisibility();
        // 快照里的截图是全局目录的；隔离实例的截图在实例目录里，再按当前实例补一次。
        RefreshInstanceShots(current?.Id ?? "", current?.Root ?? "");
        ApplyProfile(snapshot);
        ApplyRecent(snapshot);
        BuildPinnedSection();
        UpdateTaskCard();
        ApplyContinueSnapshot(snapshot);
    }

    /// <summary>启动卡三档共用一份数据，各面板自己决定排版（1×1 只名字、2×1 封面+版本、2×2 封面铺满+压字）。</summary>
    private void SetInstanceTexts(string title, string sub, bool canLaunch, ImageSource? cover)
    {
        InstanceName1.Text = title;
        InstanceName2.Text = title;
        InstanceName4.Text = title;
        InstanceVersion2.Text = sub;
        InstanceVersion4.Text = sub;
        if (cover is not null)
        {
            InstanceCover2.Source = cover;
            InstanceCover4.Source = cover;
        }
        var opacity = canLaunch ? 1 : 0.45;
        InstancePlay1.Opacity = opacity;
        InstancePlay2.Opacity = opacity;
        InstancePlay4.Opacity = opacity;

        // 没有可启动实例时，真实卡整块换成空态：虚化封面 + 两个动作，不置灰、不假装能启动。
        var empty = !canLaunch;
        InstanceEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            InstanceEmptyCover.Source = cover;
            var hasDir = !string.IsNullOrWhiteSpace(_snapshot?.GameDir)
                && Directory.Exists(_snapshot!.GameDir);
            var emptyTitle = hasDir ? "这个文件夹里还没有实例" : "找不到游戏文件夹";
            var emptySub = hasDir
                ? "下载一个新实例，或换到已有的 .minecraft 文件夹"
                : "上次用的文件夹不在了，重新选一个。";
            InstanceEmptyTitle4.Text = emptyTitle;
            InstanceEmptyTitle2.Text = emptyTitle;
            InstanceEmptySub4.Text = emptySub;
            InstanceEmptySub2.Text = emptySub;
        }
        ApplyInstanceEmptyTier();
    }

    /// <summary>空态也遵守 1×1 / 2×1 / 2×2 的尺寸契约，同一时刻只显示一个面板。</summary>
    private void ApplyInstanceEmptyTier()
    {
        var empty = _snapshot is not null && _snapshot.Current is null;
        ShowTier(InstanceEmpty1, empty && _instanceTierW == 1 && _instanceTierH == 1);
        ShowTier(InstanceEmpty2, empty && _instanceTierW == 2 && _instanceTierH == 1);
        ShowTier(InstanceEmpty4, empty && _instanceTierW == 2 && _instanceTierH == 2);
    }

    // 当前实例自己的截图：同一实例 30 秒内复用结果，避免每次快照刷新都起一次后台进程。
    private ShotsResult? _instanceShots;
    private DateTime _instanceShotsAt;
    private int _instanceShotsRequest;

    private string _instanceShotsRoot = "";
    private async void RefreshInstanceShots(string versionId, string root)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;
        if (_instanceShots is not null
            && string.Equals(_instanceShotsRoot, root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_instanceShots.Version, versionId, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _instanceShotsAt < TimeSpan.FromSeconds(30))
        {
            ApplyInstanceShots(_instanceShots);
            return;
        }
        var request = ++_instanceShotsRequest;
        try
        {
            var result = await App.Backend.GetShotsAsync(versionId, root);
            if (request != _instanceShotsRequest || result is not { Ok: true }) return;
            _instanceShots = result;
            _instanceShotsRoot = root;
            _instanceShotsAt = DateTime.UtcNow;
            ApplyInstanceShots(result);
        }
        catch
        {
            // 读不到就保留快照里的全局截图。
        }
    }

    private void ApplyInstanceShots(ShotsResult result)
    {
        _shotsCount = result.Count;
        SetShotImages(result.Files ?? new List<string>());
        ApplyShotsVisibility();
        if (_snapshot?.Current is { } current && result.Version.Equals(current.Id, StringComparison.OrdinalIgnoreCase)
            && _instanceShotsRoot.Equals(current.Root, StringComparison.OrdinalIgnoreCase))
        {
            var cover = ResolveContinueCover(_snapshot, current);
            ContinueCover.Source = cover;
            if (cover is not null) { InstanceCover2.Source = cover; InstanceCover4.Source = cover; }
        }

    }
    /// <summary>截图墙三档共用：1×1 首图铺满、2×1 四张缩略、2×2 3×2 六格。</summary>
    private void SetShotImages(List<string> files)
    {
        var count = Math.Max(_shotsCount, files.Count);
        ShotsHero1.Source = LoadImage(files.ElementAtOrDefault(0));
        ShotsEmpty1.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotsCount1.Text = count > 0 ? $"{count} 张" : "";

        ShotT2Image1.Source = LoadImage(files.ElementAtOrDefault(0));
        ShotT2Image2.Source = LoadImage(files.ElementAtOrDefault(1));
        ShotT2Image3.Source = LoadImage(files.ElementAtOrDefault(2));
        ShotT2Image4.Source = LoadImage(files.ElementAtOrDefault(3));
        // 空位直接收掉，别留四个灰块（截图只有 2 张时最明显）。
        ShotT2Slot1.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotT2Slot2.Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShotT2Slot3.Visibility = files.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        ShotT2Slot4.Visibility = files.Count > 3 ? Visibility.Visible : Visibility.Collapsed;

        ShotImage1.Source = LoadImage(files.ElementAtOrDefault(0));
        ShotImage2.Source = LoadImage(files.ElementAtOrDefault(1));
        ShotImage3.Source = LoadImage(files.ElementAtOrDefault(2));
        ShotImage4.Source = LoadImage(files.ElementAtOrDefault(3));
        ShotImage5.Source = LoadImage(files.ElementAtOrDefault(4));
        ShotImage6.Source = LoadImage(files.ElementAtOrDefault(5));
        ShotSlot1.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotSlot2.Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShotSlot3.Visibility = files.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        ShotSlot4.Visibility = files.Count > 3 ? Visibility.Visible : Visibility.Collapsed;
        ShotSlot5.Visibility = files.Count > 4 ? Visibility.Visible : Visibility.Collapsed;
        ShotSlot6.Visibility = files.Count > 5 ? Visibility.Visible : Visibility.Collapsed;

        ShotWideHero.Source = LoadImage(files.ElementAtOrDefault(0));
        ShotWideThumb1.Source = LoadImage(files.ElementAtOrDefault(1));
        ShotWideThumb2.Source = LoadImage(files.ElementAtOrDefault(2));
        ApplyShotsWide(count);

    }

    /// <summary>4×N 档：1 张时大图独占，2 张时大图 + 一张竖排缩略，3 张以上大图 + 两张缩略。</summary>
    private void ApplyShotsWide(int count)
    {
        ShotsWideMeta.Text = count > 0 ? $"{count} 张 · 点击查看全部" : string.Empty;
        ShotsWideEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotsWideBody.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) return;

        var hasSide = count > 1;
        ShotWideSide.Visibility = hasSide ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ShotWideHeroSlot, hasSide ? 1 : 2);
        ShotsWideRow2.Height = count > 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ShotWideThumb2Slot.Visibility = count > 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyProfile(Snapshot snapshot)
    {
        var account = snapshot.Account
            ?? snapshot.Accounts.FirstOrDefault(a => a.Active)
            ?? snapshot.Accounts.FirstOrDefault();

        var empty = account is null;
        ProfileEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ProfileTier1.Opacity = empty ? 0 : 1;
        ProfileTier2.Opacity = empty ? 0 : 1;
        ProfileTier3.Opacity = empty ? 0 : 1;
        ProfileTier4.Opacity = empty ? 0 : 1;

        var name = account?.Name ?? "未登录";
        var type = account?.TypeDisplay ?? "还没有账户";
        var uuid = account is null
            ? "点击「账户」添加"
            : string.IsNullOrEmpty(account.Uuid)
                ? "本地档案"
                : $"UUID {account.Uuid[..Math.Min(12, account.Uuid.Length)]}";

        ProfileName1.Text = name;
        ProfileName2.Text = name;
        ProfileName3.Text = name;
        ProfileName4.Text = name;
        ProfileType2.Text = type;
        ProfileType3.Text = type;
        ProfileType4.Text = type;
        ProfileUuid3.Text = uuid;
        ProfileUuid4.Text = uuid;

        SetHead(account, ProfileHeadHost1, ProfileHeadBrush1, ProfileHeadFigure1);
        SetHead(account, ProfileHeadHost2, ProfileHeadBrush2, ProfileHeadFigure2);
        SetHead(account, ProfileHeadHost3, ProfileHeadBrush3, ProfileHeadFigure3);
        SetHead(account, ProfileHeadHost4, ProfileHeadBrush4, ProfileHeadFigure4);

        // 全身像只认真实皮肤；没有皮肤时直接收起，绝不显示占位色块。
        var hasSkin = account is not null && !string.IsNullOrWhiteSpace(account.SkinPath) && File.Exists(account.SkinPath);
        foreach (var figure in new[] { ProfileFigure3, ProfileFigure4 })
        {
            figure.SkinPath = account?.SkinPath;
            figure.CapePath = account?.CapePath ?? "";
            figure.Slim = account?.Slim ?? false;
            figure.Visibility = hasSkin ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>头像：皮肤裁头（SkinHeadView，按物理像素精确绘制）；还没拿到皮肤时显示默认头像，不留黑块。</summary>
    private static void SetHead(AccountInfo? account, Ellipse host, ImageBrush brush, SkinHeadView figure)
    {
        host.Visibility = Visibility.Collapsed;
        figure.SkinPath = account?.SkinPath;
        figure.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStatus(OnlineInfo? online, bool requestRefresh = true)
    {
        var total = online?.Total ?? 0;
        var pending = online is null || online.Unknown || total == 0;
        var reachable = online?.Reachable ?? 0;
        var allOk = !pending && reachable == total;

        var countText = pending ? "" : $"{reachable} / {total}";
        StatusCount1.Text = countText;
        StatusCount2.Text = countText;
        // 数字本身就是数据：常态用文字色，只有不可达才转琥珀，不靠色块/彩点表达状态。
        var countBrush = ThemeBrush(pending || allOk ? "AppTextBrush" : "AppWarnBrush");
        StatusCount1.Foreground = countBrush;
        StatusCount2.Foreground = countBrush;

        var detail = pending
            ? "正在检测服务状态…"
            : allOk
                ? $"全部可达 · 最慢 {online!.Ms} ms"
                : (online!.Items.Where(i => !i.Ok).Select(i => i.Label).Take(2).ToList() is { Count: > 0 } down
                    ? $"{string.Join("、", down)} 不可达"
                    : "部分服务不可达");
        StatusDetail1.Text = pending ? "" : detail;
        StatusDetail2.Text = pending ? "" : detail;
        ToolTipService.SetToolTip(StatusCount2, detail);

        StatusUpdated2.Text = pending ? "检测中…" : $"更新于 {DateTime.Now:HH:mm}";

        if (pending && requestRefresh) _ = RefreshStatusAsync();
    }

    /// <summary>两段式的第二段：真正去探测一次（超时由 sidecar 兜住，≤3s），回来再刷这张卡。</summary>
    private async Task RefreshStatusAsync()
    {
        if (_statusLoading) return;
        _statusLoading = true;
        try
        {
            var status = await App.Backend.GetOnlineStatusAsync();
            if (status is { Unknown: false })
            {
                ApplyStatus(status, requestRefresh: false);
            }
            else if (status is null)
            {
                ToolTipService.SetToolTip(StatusCount2, "服务状态暂时取不到");
                StatusUpdated2.Text = $"更新于 {DateTime.Now:HH:mm}";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MeowLauncher status refresh failed: {ex.Message}");
        }
        finally
        {
            _statusLoading = false;
        }
    }

    /// <summary>任务卡只显示真实队列：空队列给中性文案，有任务才出现进度条与百分比。</summary>
    private void UpdateTaskCard()
    {
        var active = TaskCenter.Entries.Where(x => x.IsActive).ToList();
        _taskHasQueue = active.Count > 0;
        if (!_taskHasQueue)
        {
            TaskCount1.Text = "队列空闲";
            TaskPercentText.Text = "队列空闲";
            TaskProgress.Visibility = Visibility.Collapsed;
            TaskSub.Text = "";
            TaskSub.Visibility = Visibility.Collapsed;
            TaskWideCount.Text = "队列空闲";
            TaskWideSub.Text = "";
            TaskWideProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var entry = active[0];
        TaskCount1.Text = $"{active.Count} 进行";
        TaskPercentText.Text = $"{active.Count} 进行";
        TaskSub.Text = string.IsNullOrWhiteSpace(entry.Detail) ? entry.Title : entry.Detail;
        TaskSub.Visibility = Visibility.Visible;
        TaskWideCount.Text = $"{active.Count} 进行";
        TaskWideSub.Text = string.IsNullOrWhiteSpace(entry.Detail) ? entry.Title : entry.Detail;
        TaskWideProgress.IsIndeterminate = entry.Progress <= 0;
        TaskWideProgress.Visibility = Visibility.Visible;
        TaskProgress.IsIndeterminate = entry.Progress <= 0;
        TaskProgress.Visibility = Visibility.Visible;
        if (entry.Progress > 0)
        {
            TaskProgress.Value = entry.Progress;
            TaskWideProgress.Value = entry.Progress;
        }
    }

    /// <summary>装饰性尺寸全部由 XAML 面板决定（立绘、缩略图在各自档位里写死），这里不再二次缩放。</summary>
    private void ResizeCardDecorations()
    {
    }

    /// <summary>时钟卡在窄格（980 窗口的 1x1）里收掉右侧日期，保证 44 号时间完整可读。</summary>
    private void ResizeClockMeta()
    {
        var w = ClockCard.ActualWidth;
        if (w <= 0) return;
        ClockMeta.Visibility = w < 190 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateProfileCardMode()
    {
        // 玩家档案的四档各有独立面板（Tier1/2/3/4），字号、头像尺寸、立绘显隐都写在 XAML 里；
        // 这里只保留调用点，避免 SetCardTier 之外再出现「按宽度改样式」的第二套逻辑。
    }

    private void RecentCard_SizeChanged()
    {
        if (_snapshot is null) return;
        var h = RecentCard.ActualHeight;
        if (h <= 1) return;
        var wide = Math.Clamp((int)Math.Floor((h - 44) / 30), 1, 5);
        var small = Math.Clamp((int)Math.Floor((h - 40) / 28), 1, 3);
        if (wide == _recentWideCap && small == _recentSmallCap) return;
        ApplyRecent(_snapshot);
    }

    private void ApplyRecent(Snapshot snapshot)
    {
        var cardHeight = RecentCard.ActualHeight;
        var wideCap = cardHeight > 1 ? Math.Clamp((int)Math.Floor((cardHeight - 44) / 30), 1, 5) : 5;
        var smallCap = cardHeight > 1 ? Math.Clamp((int)Math.Floor((cardHeight - 40) / 28), 1, 3) : 2;
        _recentWideCap = wideCap;
        _recentSmallCap = smallCap;

        RecentList.Children.Clear();
        var recent = snapshot.Versions
            .Where(v => v.Valid && !v.Hidden && v.LastPlayed > 0)
            .OrderByDescending(v => v.LastPlayed)
            .Take(smallCap)
            .ToList();

        // 1×1 只放最近一条；2×1 的条数由卡片实际高度算，不再写死。
        var first = recent.FirstOrDefault();
        RecentOneName.Text = first?.Id ?? "还没有启动记录";
        RecentOneAgo.Text = first?.AgoText ?? "";

        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var version in recent)
        {
            var btn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = version.Id,
            };
            AutomationProperties.SetName(btn, $"最近启动 {version.Id}");

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var isCurrent = version.Id == _continueSelectedId;
            var name = new TextBlock
            {
                Text = version.Id,
                FontSize = 13,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ResBrush("AppTextBrush", Color.FromArgb(255, 32, 32, 32)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var sub = new TextBlock
            {
                Text = $"{version.GameVersion} · {version.Loader}",
                FontSize = 11.5,
                Foreground = ResBrush("AppText3Brush", Color.FromArgb(255, 138, 138, 138)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var ago = new TextBlock
            {
                Text = version.AgoText,
                FontSize = 11.5,
                Foreground = ResBrush("AppText3Brush", Color.FromArgb(255, 138, 138, 138)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
            };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(sub, 1);
            Grid.SetColumn(ago, 2);
            grid.Children.Add(name);
            grid.Children.Add(sub);
            grid.Children.Add(ago);

            btn.Content = grid;
            // 悬停用卡片内的浅底，别用系统默认的整块灰。
            btn.Resources["ButtonBackgroundPointerOver"] = ResBrush("AppChipBrush", Color.FromArgb(20, 0, 0, 0));
            btn.Resources["ButtonBackgroundPressed"] = ResBrush("AppChipBrush", Color.FromArgb(30, 0, 0, 0));
            btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Click += Recent_Click;
            RecentList.Children.Add(btn);
        }

        // 2×2 的条数同样按实际高度算；只留名字 + 相对时间。
        RecentList4.Children.Clear();
        var wide = snapshot.Versions
            .Where(v => v.Valid && !v.Hidden && v.LastPlayed > 0)
            .OrderByDescending(v => v.LastPlayed)
            .Take(wideCap)
            .ToList();
        RecentList4.Visibility = wide.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty4.Visibility = wide.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var version in wide)
        {
            var row = new Button
            {
                Height = 30,
                MinHeight = 30,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = version.Id,
            };
            AutomationProperties.SetName(row, $"最近启动 {version.Id}");
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var n = new TextBlock
            {
                Text = version.Id,
                FontSize = 13,
                FontWeight = version.Id == _continueSelectedId ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ResBrush("AppTextBrush", Color.FromArgb(255, 32, 32, 32)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var ago = new TextBlock
            {
                Text = version.AgoText,
                FontSize = 11.5,
                Foreground = ResBrush("AppText3Brush", Color.FromArgb(255, 138, 138, 138)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(n, 0);
            Grid.SetColumn(ago, 1);
            g.Children.Add(n);
            g.Children.Add(ago);
            row.Content = g;
            row.Resources["ButtonBackgroundPointerOver"] = ResBrush("AppChipBrush", Color.FromArgb(20, 0, 0, 0));
            row.Resources["ButtonBackgroundPressed"] = ResBrush("AppChipBrush", Color.FromArgb(30, 0, 0, 0));
            row.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Click += Recent_Click;
            RecentList4.Children.Add(row);
        }
    }


    // ---------- 固定版本 ----------

    private static List<string> ReadPinned()
        => (App.MainWindow?.Preferences?.PinnedVersions ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Take(6).ToList();

    /// <summary>首页「固定版本」：钉住的实例一键启动；没钉时只留一个「固定版本」入口。</summary>
    private void BuildPinnedSection()
    {
        if (PinnedList is null) return;
        PinnedList.Children.Clear();
        if (PinnedCardList is not null) PinnedCardList.Children.Clear();

        var all = _snapshot?.Versions ?? new List<VersionInfo>();
        var hidden = (_snapshot?.HiddenVersionIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinned = ReadPinned().Where(id => !hidden.Contains(id) || all.Any(v => !v.Hidden
            && string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        var shown = 0;

        foreach (var id in pinned)
        {
            var v = all.FirstOrDefault(x => x.Id == id);
            var title = v?.Id ?? id;
            var meta = v is null ? "不在当前目录" : $"{v.GameVersion} · {v.Loader}";

            var btn = new Button
            {
                Width = 208,
                Height = 56,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = (CornerRadius)Application.Current.Resources["RowCornerRadius"],
                Background = ResBrush("AppCardBrush", Color.FromArgb(45, 15, 18, 22)),
                BorderThickness = new Thickness(1),
                BorderBrush = ResBrush("CardStrokeBrush", Color.FromArgb(28, 255, 255, 255)),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = id,
                IsEnabled = v is not null,
            };
            AutomationProperties.SetName(btn, $"固定版本 {title}");
            btn.Resources["ButtonBackgroundPointerOver"] = ResBrush("AppChipBrush", Color.FromArgb(20, 255, 255, 255));
            btn.Resources["ButtonBackgroundPressed"] = ResBrush("AppChipBrush", Color.FromArgb(30, 255, 255, 255));
            btn.Resources["ButtonBorderBrushPointerOver"] = ResBrush("CardStrokeBrush", Color.FromArgb(40, 255, 255, 255));

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = (CornerRadius)Application.Current.Resources["RowCornerRadius"],
                Background = ResBrush("AppChipBrush", Color.FromArgb(28, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.Child = new FontIcon
            {
                Glyph = "",
                FontSize = 14,
                Foreground = ResBrush("AppText2Brush", Color.FromArgb(200, 235, 235, 235)),
            };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResBrush("AppTextBrush", Color.FromArgb(255, 235, 235, 235)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = 11.5,
                Foreground = ResBrush("AppText3Brush", Color.FromArgb(255, 138, 138, 138)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(text, 1);
            grid.Children.Add(icon);
            grid.Children.Add(text);
            btn.Content = grid;
            btn.Click += PinnedLaunch_Click;
            PinnedList.Children.Add(btn);
            shown++;
        }

        // 没有任何固定版本时整段收起：不留空入口、不留空当。
        PinnedSection.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildPinnedCard(pinned, all);
    }

    /// <summary>组件板上的 4×1「固定版本」卡：同一批实例换成单行矮卡；没钉时给一句怎么钉。</summary>
    private void BuildPinnedCard(List<string> pinned, List<VersionInfo> all)
    {
        if (PinnedCardList is null || PinnedCardEmpty is null) return;
        PinnedCardList.Children.Clear();
        foreach (var id in pinned)
            PinnedCardList.Children.Add(MakePinnedChip(id, all.FirstOrDefault(x => x.Id == id)));

        var empty = pinned.Count == 0;
        PinnedCardList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PinnedCardEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button MakePinnedChip(string id, VersionInfo? version)
    {
        var title = version?.Id ?? id;
        var meta = version is null ? "不在当前目录" : $"{version.GameVersion} · {version.Loader}";
        var btn = new Button
        {
            Width = 196,
            Height = 52,
            Padding = new Thickness(10, 0, 12, 0),
            CornerRadius = (CornerRadius)Application.Current.Resources["RowCornerRadius"],
            Background = ResBrush("AppChipBrush", Color.FromArgb(24, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = ResBrush("CardStrokeBrush", Color.FromArgb(28, 255, 255, 255)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = id,
            IsEnabled = version is not null,
        };
        AutomationProperties.SetName(btn, $"固定版本 {title}");
        btn.Resources["ButtonBackgroundPointerOver"] = ResBrush("AppChipBrush", Color.FromArgb(34, 255, 255, 255));
        btn.Resources["ButtonBackgroundPressed"] = ResBrush("AppChipBrush", Color.FromArgb(44, 255, 255, 255));
        btn.Resources["ButtonBorderBrushPointerOver"] = ResBrush("CardStrokeBrush", Color.FromArgb(44, 255, 255, 255));

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标框沿用最近启动的绿：固定版本的本质就是「点一下开机」，不和新闻/媒体的紫橙抢语义。
        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = (CornerRadius)Application.Current.Resources["RowCornerRadius"],
            Background = ResBrush("TintRecentSoftBrush", Color.FromArgb(38, 47, 163, 90)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Child = new FontIcon
        {
            Glyph = "\uE768",
            FontSize = 12,
            Foreground = ResBrush("TintRecentBrush", Color.FromArgb(255, 47, 163, 90)),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResBrush("AppTextBrush", Color.FromArgb(255, 235, 235, 235)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11.5,
            Foreground = ResBrush("AppText3Brush", Color.FromArgb(255, 138, 138, 138)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var go = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            Foreground = ResBrush("AppText3Brush", Color.FromArgb(190, 138, 138, 138)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(go, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(go);
        btn.Content = grid;
        btn.Click += PinnedLaunch_Click;
        return btn;
    }

    private void PinnedMore_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        NavigateRequested?.Invoke(this, "versions");
    }

    private void PinnedLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode || _launching || sender is not Button { Tag: string id }) return;
        LaunchContinue(id);
    }

    // ---------- 启动 ----------

    /// <summary>三档启动卡的「启动中」状态同步：播放图标变淡且不可点，避免重复提交（契约 4）。</summary>
    private void SetLaunchEnabled(bool enabled)
    {
        foreach (var chip in new[] { InstancePlay1, InstancePlay2, InstancePlay4 })
        {
            chip.Opacity = enabled ? 1 : 0.45;
            chip.IsHitTestVisible = enabled;
        }
    }

    private void SetInstanceSub(string text)
    {
        InstanceVersion2.Text = text;
        InstanceVersion4.Text = text;
    }

    private async void Launch_Click(object sender, RoutedEventArgs e) => await LaunchCurrentAsync();

#if DEBUG
    /// <summary>仅调试：让 debug-nav 能在不点鼠标的情况下走一次真实启动链路。</summary>
    public void DebugLaunch() => _ = LaunchCurrentAsync();
#endif

    private Task LaunchCurrentAsync()
    {
        if (!_editMode && !_launching && _snapshot?.Current is { } current)
            LaunchContinue(current.Id, current.Root);
        return Task.CompletedTask;
    }

    private void ShowLaunchFailure(string message, string versionId, FailureInfo failure)
    {
        ShowToast(InfoBarSeverity.Error, $"启动失败 · {failure.Code}", message, "复制诊断",
            () => Diagnostics.Copy(Diagnostics.BuildReport(failure, "启动游戏", message, versionId)), 20);
    }

    // ---------- 系统媒体 ----------

    private async Task InitializeMediaAsync()
    {
        if (_mediaStarted) return;
        _mediaStarted = true;
        _media.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => _ = UpdateMediaAsync());
        await _media.StartAsync();
        await UpdateMediaAsync();
    }

    private async Task UpdateMediaAsync()
    {
        var info = await _media.GetInfoAsync();
        var hasSession = info is not null && info.Title != "没有正在播放";
        MediaTitle.Text = hasSession ? info!.Title : "没有正在播放";
        MediaSub.Text = hasSession ? info!.Artist : "打开网易云 / Spotify";
        MediaTitle1.Text = MediaTitle.Text;
        MediaSub1.Text = MediaSub.Text;
        // 只读进度条：没有可信时间轴时只表达「正在播放」，不编造百分比（契约 11.2 禁假数字）。
        _mediaPlaying = hasSession && info!.IsPlaying;
        MediaProgress.Visibility = _mediaPlaying ? Visibility.Visible : Visibility.Collapsed;

        // 4×1 档：封面 + 标题/副行 + 真实时间轴（系统会话给了 TimelineProperties 才画）。
        MediaTitle4.Text = MediaTitle.Text;
        MediaSub4.Text = MediaSub.Text;
        var cover = hasSession ? info!.Cover : null;
        MediaCover4.Source = cover;
        MediaCover4.Visibility = cover is null ? Visibility.Collapsed : Visibility.Visible;
        MediaCoverIcon4.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;

        var duration = hasSession ? info!.Duration : TimeSpan.Zero;
        var position = hasSession ? info!.Position : TimeSpan.Zero;
        if (duration > TimeSpan.Zero)
        {
            MediaProgress4.Maximum = duration.TotalSeconds;
            MediaProgress4.Value = Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
            MediaTime4.Text = $"{FormatMediaTime(position)} / {FormatMediaTime(duration)}";
            MediaProgress4.Visibility = Visibility.Visible;
            MediaTime4.Visibility = Visibility.Visible;
        }
        else
        {
            // 会话没给可信时间轴：不画进度条，只留「正在播放」。
            MediaProgress4.Maximum = 1;
            MediaProgress4.Value = 0;
            MediaProgress4.Visibility = Visibility.Collapsed;
            MediaTime4.Text = hasSession ? "正在播放" : "";
            MediaTime4.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;
        }
        MediaPlayIcon4.Glyph = _mediaPlaying ? "\uE769" : "\uE768";
    }

    private static string FormatMediaTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";

    private async void MediaPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        await _media.TogglePlayPauseAsync();
        await UpdateMediaAsync();
    }

    private async void MediaPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        await _media.PreviousAsync();
        await UpdateMediaAsync();
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        await _media.NextAsync();
        await UpdateMediaAsync();
    }

    // ---------- 每日一帖（Mojang 官方新闻，只读） ----------

    private async Task InitializeNewsAsync()
    {
        if (_newsStarted) return;
        _newsStarted = true;
        _news.LoadCache();
        ApplyNews();
        _news.Updated += (_, _) => DispatcherQueue.TryEnqueue(ApplyNews);
        await _news.RefreshAsync();
        ApplyNews();
    }

    private void ApplyNews()
    {
        var item = _news.Today;
        if (item is null) return;
        _newsItem = item;
        var title = string.IsNullOrWhiteSpace(item.Title) ? "Mojang 新闻" : item.Title;
        TipTitle4.Text = title;
        TipTitle2.Text = title;
        TipText1.Text = title;
        var tag = string.IsNullOrWhiteSpace(item.Tag) ? "Mojang 新闻" : item.Tag;
        var date = item.Date.ToLocalTime().ToString("M月d日");
        TipTag4.Text = tag;
        TipDate4.Text = date;
        TipMeta2.Text = $"{tag} · {date}";
        TipSub4.Text = item.Summary;
        if (!string.IsNullOrWhiteSpace(item.ImageUrl) && TipImage4.Source is null)
        {
            try
            {
                var image = new BitmapImage(new Uri(item.ImageUrl));
                TipImage4.Source = image;
                TipImage4.Visibility = Visibility.Visible;
                TipImage2.Source = image;
                TipImage2.Visibility = Visibility.Visible;
            }
            catch
            {
                // 图挂了就只留文字，不做占位图。
            }
        }
    }

    // ---------- 导航与跳转 ----------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        NavigateRequested?.Invoke(this, "settings");
    }

    // 有实例时整卡是启动目标；没有实例时只剩空态里的两个动作，1×1 才弹出菜单。
    private void InstanceCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        if (_snapshot is null || _snapshot.Current is null)
        {
            if (_instanceTierW == 1 && _instanceTierH == 1) ShowInstanceEmptyMenu();
            return;
        }
        Launch_Click(this, new RoutedEventArgs());
    }

    private void ShowInstanceEmptyMenu()
    {
        var flyout = new MenuFlyout();
        var download = new MenuFlyoutItem { Text = "下载新实例" };
        download.Click += (_, _) => NavigateRequested?.Invoke(this, "get");
        var pick = new MenuFlyoutItem { Text = "选择已有的 .minecraft 文件夹" };
        pick.Click += (_, _) => _ = PickGameFolderAsync();
        flyout.Items.Add(download);
        flyout.Items.Add(pick);
        flyout.ShowAt(InstanceCard);
    }

    private void InstanceEmptyDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        NavigateRequested?.Invoke(this, "get");
    }

    private async void InstanceEmptyPickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        await PickGameFolderAsync();
    }

    /// <summary>选一个已有的 .minecraft：写进偏好、通知 sidecar 重扫，再刷首页。</summary>
    private async Task PickGameFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is { } window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var dir = GameDirectoryResolver.Normalize(folder.Path);
        if (string.IsNullOrWhiteSpace(dir)) return;

        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.McDirs.RemoveAll(d => string.Equals(
            GameDirectoryResolver.Normalize(d), dir, StringComparison.OrdinalIgnoreCase));
        prefs.McDirs.Insert(0, dir);
        prefs.McDir = dir;
        AppPreferencesStore.Save(prefs);

        await App.Backend.SetGameDirAsync(dir);
        await RefreshAsync(true);
    }

    private void ProfileCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        var account = _snapshot?.Account
            ?? _snapshot?.Accounts.FirstOrDefault(a => a.Active)
            ?? _snapshot?.Accounts.FirstOrDefault();
        if (account is null)
        {
            App.MainWindow?.OpenAccountAdd();
            return;
        }
        NavigateRequested?.Invoke(this, "account");
    }

    private void ProfileEmptyAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        App.MainWindow?.OpenAccountAdd();
    }

    private void MediaCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        // 卡上已经有上一首/播放/下一首；点空白处只在「根本没有会话」时提一句去哪开播放器。
        if (MediaTitle.Text == "没有正在播放")
            ShowToast(InfoBarSeverity.Informational, "系统媒体", "打开网易云 / Spotify 等播放器后再回来。");
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        NavigateRequested?.Invoke(this, "versions");
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (tag == "shots") OpenScreenshots();
            else NavigateRequested?.Invoke(this, tag);
        }
    }

    private void OpenTasks_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        NavigateRequested?.Invoke(this, "tasks");
    }

    private void OpenShots_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        OpenScreenshots();
    }

    private void OpenWiki_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_editMode) return;
        try
        {
            var url = _newsItem?.Link;
            if (string.IsNullOrWhiteSpace(url)) url = "https://minecraft.wiki/";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器不可用时静默忽略。
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode) return;
        var dir = _snapshot?.Current?.Path;
        if (string.IsNullOrEmpty(dir)) dir = _snapshot?.GameDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            ShowToast(InfoBarSeverity.Warning, "目录不存在", "还没有可打开的实例目录。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "打开失败", Diagnostics.FriendlyMessage(ex));
        }
    }

    private void OpenScreenshots()
    {
        if (_editMode) return;
        var gameDir = _snapshot?.GameDir;
        var shotDir = _instanceShots?.Dir ?? (string.IsNullOrEmpty(gameDir) ? "" : Path.Combine(gameDir, "screenshots"));
        if (string.IsNullOrEmpty(shotDir) || !Directory.Exists(shotDir))
        {
            ShowToast(InfoBarSeverity.Warning, "还没有截图", "游戏里按 F2 截图后会出现在这里。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{shotDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "打开失败", Diagnostics.FriendlyMessage(ex));
        }
    }

    // ---------- Toast ----------

    private void CloseToast()
    {
        Toast.IsOpen = false;
        _toastTimer.Stop();
    }

    private void ShowToast(InfoBarSeverity severity, string title, string message,
        string? actionText = null, Action? action = null, int seconds = 4)
    {
        Toast.Severity = severity;
        Toast.Title = title;
        Toast.Message = message;
        if (action is not null)
        {
            var label = actionText ?? "查看";
            var btn = new Button
            {
                Content = label,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(14),
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Background = ResBrush("AppChipBrush", Color.FromArgb(255, 138, 138, 138)),
                Foreground = ResBrush("AppTextBrush", Color.FromArgb(255, 255, 255, 255)),
            };
            AutomationProperties.SetName(btn, label);
            btn.Click += (_, _) => action();
            Toast.ActionButton = btn;
        }
        else
        {
            Toast.ActionButton = null;
        }
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    // ---------- 工具 ----------

    private static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return new BitmapImage(new Uri(path)); }
        catch { return null; }
    }

    private bool RootGridDark()
    {
        return (App.MainWindow?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
    }

    private static Brush ResBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch
        {
            // 主题资源缺失时退回纯色。
        }
        return new SolidColorBrush(fallback);
    }

    private static IEnumerable<string> SplitCsv(string? text)
    {
        return (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ── 账户条上的猫：爬上爬上动画（Claude 的 6 帧状态机）──
    private const int CatLie = 5;
    private const int CatGone = -1;
    private static readonly int[] CatFrameMs = { 120, 90, 90, 90, 110, 400 };
    private int _catFrame = CatGone;
    private int _catTarget = CatGone;
    private DispatcherQueueTimer? _catTimer;
    private DateTime _catSettledAt = DateTime.MinValue;

    private bool CatShouldShow =>
        _simpleMode && _screen == 0 &&
        App.MainWindow is { } w && w.WallpaperControl.Visibility == Visibility.Visible;

    /// <summary>负一屏的猫在账户条上沿：想要就爬上（0→5），不想要就爬下（5→0→隐藏）。</summary>
    private void UpdateAccountCat()
    {
        var want = CatShouldShow;
        if (want)
        {
            if (_catTarget == CatLie) { ApplyAccountCatFrame(); return; }
            _catTarget = CatLie;
            EnsureCatTimer();
            return;
        }
        if (_catFrame <= CatGone) { CollapseAccountCat(); return; }
        // 已经趴好且不满 1 秒，先别下去（防抖动）
        if (_catFrame == CatLie && DateTime.UtcNow - _catSettledAt < TimeSpan.FromSeconds(1) && _catTarget == CatLie) return;
        _catTarget = CatGone;
        EnsureCatTimer();
    }

    /// <summary>离开首页时立刻收起，不播动画。</summary>
    private void HideAccountCatNow()
    {
        _catTarget = CatGone;
        _catFrame = CatGone;
        _catTimer?.Stop();
        CollapseAccountCat();
    }

    private void EnsureCatTimer()
    {
        if (_catTimer is null)
        {
            _catTimer = DispatcherQueue.CreateTimer();
            _catTimer.IsRepeating = false;
            _catTimer.Tick += (_, _) => CatTick();
        }
        CatSchedule();
    }

    private void CatSchedule()
    {
        if (_catTimer is null) return;
        _catTimer.Stop();
        var ms = CatFrameMs[Math.Clamp(_catFrame < 0 ? 0 : _catFrame, 0, CatFrameMs.Length - 1)];
        _catTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _catTimer.Start();
    }

    private void CatTick()
    {
        if (_catFrame == _catTarget)
        {
            _catTimer?.Stop();
            if (_catFrame == CatLie) _catSettledAt = DateTime.UtcNow;
            return;
        }
        _catFrame += _catFrame < _catTarget ? 1 : -1;
        ApplyAccountCatFrame();
        if (_catFrame == _catTarget)
        {
            _catTimer?.Stop();
            if (_catFrame == CatLie) _catSettledAt = DateTime.UtcNow;
            return;
        }
        CatSchedule();
    }

    private void ApplyAccountCatFrame()
    {
        if (_catFrame <= CatGone) { CollapseAccountCat(); return; }
        var scale = Math.Max(1, XamlRoot?.RasterizationScale ?? 1);
        var n = Math.Clamp((int)Math.Round(56 * scale / 10), 4, 8);
        var theme = (App.MainWindow as MainWindow)?.IsDarkTheme == true ? "dark" : "light";
        var file = _catFrame >= CatLie ? $"cat_lie_x{n}.png" : $"cat_climb_x{n}_{_catFrame}.png";
        var uri = $"ms-appx:///Assets/Wallpaper/{theme}/{file}";
        if (AccountCat.Source is not BitmapImage bi || bi.UriSource?.OriginalString != uri)
            AccountCat.Source = new BitmapImage(new Uri(uri));
        var w = 14d * n / scale;
        var h = 10d * n / scale;
        AccountCat.Width = w;
        AccountCat.Height = h;
        AccountCat.Margin = new Thickness(0, -(7d * n / scale), 48, 0);
        AccountCat.Visibility = Visibility.Visible;
    }

    private void CollapseAccountCat()
    {
        AccountCat.Visibility = Visibility.Collapsed;
    }
}
