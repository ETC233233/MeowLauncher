using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 「获取」页：顶部两个子页胶囊（下载版本 / 在线资源），
/// 内容直接复用 DownloadPage 与 OnlinePage，只把它们的页头收起来。
/// </summary>
public sealed partial class GetPage : Page, IPageSearchFilter, ISubPageHost
{
    private string _tab = "download";
    private string _pendingQuery = "";
    private bool _ready;
    private bool _subOpen;

    public GetPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            Show(_tab);
        };
    }

    public string CurrentTab => _tab;

    /// <summary>由 MainWindow 调用：切到指定子页；query 只对在线资源生效。</summary>
    public void ShowTab(string tab, string? query = null)
    {
        if (tab is not ("download" or "online")) tab = "download";
        if (!string.IsNullOrWhiteSpace(query)) _pendingQuery = query;
        if (_subOpen) CloseSubPageCore(popHistory: true);
        _tab = tab;
        UpdateTabVisuals();
        if (!_ready) return;
        Show(tab);
    }

    private void Show(string tab)
    {
        var target = tab == "online" ? typeof(OnlinePage) : typeof(DownloadPage);
        var query = tab == "online" ? _pendingQuery : "";
        if (tab == "online") _pendingQuery = "";
        if (InnerFrame.Content?.GetType() == target) return;
        var switching = InnerFrame.Content is not null;   // 首次载入由整页的滑入负责，只有标签互切才补进场
        InnerFrame.Navigate(target, new TabNav(query, true, this), new SuppressNavigationTransitionInfo());
        // 在线资源的搜索框在本页顶栏（来源切换右边），交给新建的 OnlinePage 使用。
        if (InnerFrame.Content is OnlinePage online) online.AttachQueryBox(OnlineQueryBox);
        if (switching) MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 0, 8, 200);
    }

    // 「获取」只是个壳：本页过滤和 Ctrl+K 都转给里面的 DownloadPage / OnlinePage。
    public void ApplySearch(string query)
    {
        if (_subOpen) CloseSubPageCore(popHistory: true);
        if (InnerFrame.Content is IPageSearchFilter inner) inner.ApplySearch(query);
    }

    public bool TryFocusSearch()
    {
        if (_subOpen) CloseSubPageCore(popHistory: true);
        return InnerFrame.Content is IPageSearchFilter inner && inner.TryFocusSearch();
    }

    // ------------------------------------------------------------------ 子页

    public void OpenSubPage(Type page, object args, params string[] crumbs)
    {
        BuildCrumbs(crumbs);
        var first = !_subOpen;
        _subOpen = true;
        ApplyHeaderMode();
        // 不用 DrillIn：它的缩放动画会让截图/视觉上子页比列表窄一圈，整页替换直接出现更干净。
        try
        {
            SubFrame.Navigate(page, args, new SuppressNavigationTransitionInfo());
        }
        catch (Exception ex)
        {
            // 子页构造失败（常见是 XAML 解析错误）：别留一个空白窗口，退回列表并把错误交给调用方。
            App.Trace("OpenSubPage FAILED " + page.Name + ": " + ex);
            CloseSubPageCore(popHistory: true);
            throw;
        }
        MeowLauncher.WinUI.Controls.Motion.Enter(SubHeader, 0, 6, 200);
        MeowLauncher.WinUI.Controls.Motion.Enter(SubFrame, 40, 10, 240);
        // 标题栏返回键 / Alt+Left 走同一条历史：只关子页，不换整页。
        if (first) App.MainWindow?.PushSubPage(() => CloseSubPageCore(popHistory: false));
    }

    public void CloseSubPage() => CloseSubPageCore(popHistory: true);

    private void CloseSubPageCore(bool popHistory)
    {
        if (!_subOpen) return;
        _subOpen = false;
        SubFrame.Content = null;
        ApplyHeaderMode();
        MeowLauncher.WinUI.Controls.Motion.Enter(TitleRow, 0, 6, 200);
        MeowLauncher.WinUI.Controls.Motion.Enter(TabsRow, 30, 6, 200);
        MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 60, 8, 220);
        if (popHistory) App.MainWindow?.PopSubPage();
    }

    private void SubBack_Click(object sender, RoutedEventArgs e) => CloseSubPage();

    /// <summary>子页开着时：页头 = 返回箭头 + 面包屑，胶囊和列表收起（保留实例），内容区换成子页。</summary>
    private void ApplyHeaderMode()
    {
        var sub = _subOpen;
        TitleRow.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        SubHeader.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        TabsRow.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        InnerFrame.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        SubFrame.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCrumbs(string[] crumbs)
    {
        CrumbPanel.Children.Clear();
        for (var i = 0; i < crumbs.Length; i++)
        {
            var last = i == crumbs.Length - 1;
            if (i > 0)
            {
                CrumbPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = PagePalette.ThemeBrush("AppText3Brush"),
                });
            }
            var text = new TextBlock
            {
                Text = crumbs[i],
                FontSize = 28,
                // 和「实例」「账户」同一套：前面的层级常规字重 + 0.72 不透明度，当前页半粗。
                FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = last ? 1 : 0.72,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = PagePalette.ThemeBrush("AppTextBrush"),
            };
            if (last)
            {
                text.MaxWidth = 460;
            }
            else
            {
                // 前面的段都能点：回到列表。
                text.Tapped += (_, _) => CloseSubPage();
                ToolTipService.SetToolTip(text, "返回列表");
            }
            CrumbPanel.Children.Add(text);
        }
    }

#if DEBUG
    public async Task DebugOpenDetail(string id)
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not DownloadPage; i++) await Task.Delay(100);
        if (InnerFrame.Content is DownloadPage d) await d.DebugOpenDetail(id);
    }
#endif

#if DEBUG
    public async Task DebugOpenFirstMod(string instance = "")
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not OnlinePage; i++) await Task.Delay(100);
        if (InnerFrame.Content is OnlinePage o) await o.DebugOpenFirst(instance);
    }
#endif

    public async Task OpenProjectAsync(MeowLauncher.WinUI.Models.ModrinthProject project, string instance)
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not OnlinePage; i++) await Task.Delay(100);
        if (InnerFrame.Content is OnlinePage o) await o.OpenProjectAsync(project, instance);
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag })
        {
            ShowTab(tag);
            App.MainWindow?.NotifySubTab(tag);
        }
    }

    internal void RefreshSourceSelection() => UpdateTabVisuals();

    private void UpdateTabVisuals()
    {
        var selected = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var text = PagePalette.ThemeBrush("AppTextBrush");
        var text2 = PagePalette.ThemeBrush("AppText2Brush");
        Paint(TabDownload, _tab == "download");
        Paint(TabOnline, _tab == "online");
        OnlineHeaderActions.Visibility = _tab == "online" ? Visibility.Visible : Visibility.Collapsed;
        ResourceMirrorBox.SelectedIndex = McimClient.PreferOfficial ? 1 : 0;

        // 来源切换跟着「在线资源」走：选中时从按钮右边滑出来，切到下载版本就收起。
        var showStrip = _tab == "online";
        var stripWasHidden = SourceStrip.Visibility != Visibility.Visible;
        SourceStrip.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        OnlineQueryBox.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        PaintSource(SrcModrinth, OnlinePage.PreferredSource != 1);
        PaintSource(SrcCurseForge, OnlinePage.PreferredSource == 1);
        if (showStrip && stripWasHidden) MeowLauncher.WinUI.Controls.Motion.EnterX(SourceStrip, 40, -14, 220);

        void PaintSource(ToggleButton button, bool on)
        {
            button.IsChecked = on;
            button.Background = on ? selected : clear;
            button.Foreground = on ? text : text2;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void Paint(ToggleButton button, bool on)
        {
            button.IsChecked = on;
            button.Background = on ? selected : clear;
            button.Foreground = on ? text : text2;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>点 Modrinth / CurseForge：记下选择，交给当前的在线资源页重新搜。</summary>
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        OnlinePage.PreferredSource = index;
        if (InnerFrame.Content is OnlinePage online) online.SetSource(index);
        UpdateTabVisuals();
    }

    private void ResourceMirror_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceMirrorBox.SelectedIndex is not (0 or 1)) return;
        if (McimClient.SetPreferOfficial(ResourceMirrorBox.SelectedIndex == 1) &&
            InnerFrame.Content is OnlinePage online)
            online.ApplySearch(OnlineQueryBox.Text);
    }

    private void Home_Tapped(object sender, TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");
}
