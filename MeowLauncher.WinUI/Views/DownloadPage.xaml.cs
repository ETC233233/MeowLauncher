using System.ComponentModel;
using System.Text.RegularExpressions;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;

namespace MeowLauncher.WinUI.Views;

public sealed partial class DownloadPage : Page, IPageSearchFilter
{
    private enum PageState
    {
        Loading,
        List,
        Empty,
        Error,
    }

    // ------------------------------------------------------------------ 行模型

    /// <summary>右栏一行 = 一个 Mojang 版本。</summary>
    private sealed class DownloadRow : INotifyPropertyChanged
    {
        public DownloadRow(MojangVersionInfo source, bool showType)
        {
            Source = source;
            var (icon, stretch) = IconFor(source.Id);
            Icon = icon;
            IconStretch = stretch;
            Meta = showType ? $"{source.TypeText} · {source.DateText}" : source.DateText;
        }

        public MojangVersionInfo Source { get; }
        public string Id => Source.Id;
        public string Meta { get; }
        public ImageSource Icon { get; }
        public Stretch IconStretch { get; }
        public Visibility LatestVisibility => Source.IsLatest ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstalledVisibility => Source.Installed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>ListViewItem 的 UIA 名称取对象 ToString。</summary>
        public override string ToString() => Id;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>安装完成后只刷新「已安装」标记，不重建列表（滚动位置、筛选都不动）。</summary>
        public void Refresh() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstalledVisibility)));
    }

    /// <summary>左栏一行 = 一个大版本（或一年快照 / 一类远古版）。</summary>
    private sealed class NavRow
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string CountText { get; init; } = "";
        public bool Selected { get; init; }
        public ImageSource? Icon { get; init; }
        public Stretch IconStretch { get; init; } = Stretch.Uniform;

        public Visibility SelectedVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public override string ToString() => Title;
    }

    /// <summary>左栏的一个大版本（或一年快照 / 一类远古版），右栏显示它的全部版本。</summary>
    private sealed record NavGroup(string Cat, string Key, string Title, string Sub, string Heading,
        List<MojangVersionInfo> Items);

    // ------------------------------------------------------------------ 图标

    private static readonly Dictionary<string, string> UpdateBanners = new(StringComparer.Ordinal)
    {
        ["1.21"] = "u121.jpg", ["1.20"] = "u120.jpg", ["1.19"] = "u119.jpg",
        ["1.18"] = "u118.jpg", ["1.17"] = "u117.jpg", ["1.16"] = "u116.jpg",
        ["1.15"] = "u115.jpg", ["1.14"] = "u114.jpg", ["1.13"] = "u113.jpg",
        ["1.12"] = "u112.jpg", ["1.11"] = "u111.jpg", ["1.10"] = "u110.jpg",
        ["1.9"] = "u19.jpg", ["1.8"] = "u18.jpg", ["1.7"] = "u17.jpg",
    };

    private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.Ordinal);

    /// <summary>有该大版本的更新横幅就用横幅（居中裁成方块），没有就用 default_icon。按大版本缓存。</summary>
    internal static (ImageSource Icon, Stretch Stretch) IconFor(string id)
    {
        var major = MajorOf(id);
        if (UpdateBanners.TryGetValue(major, out var file))
        {
            if (_iconCache.TryGetValue(major, out var cached)) return (cached, Stretch.UniformToFill);
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "updates", file);
            if (File.Exists(path))
            {
                var img = new BitmapImage { DecodePixelWidth = 96, UriSource = new Uri(path) };
                _iconCache[major] = img;
                return (img, Stretch.UniformToFill);
            }
        }
        if (!_iconCache.TryGetValue("logo", out var logo))
        {
            logo = new BitmapImage(new Uri("ms-appx:///Assets/default_icon.png"));
            _iconCache["logo"] = logo;
        }
        return (logo, Stretch.Uniform);
    }

    // ------------------------------------------------------------------ 状态

    private readonly List<MojangVersionInfo> _all = [];
    private List<NavGroup> _groups = [];
    private List<DownloadRow> _rows = [];

    // 左栏的分类 / 选中状态在页面实例之间记住：离开再回来保持原样。
    private static string _selCat = "release";
    private static readonly Dictionary<string, string> _selKeyByCat = new(StringComparer.Ordinal);

    private bool _loading;
    private string _query = "";
    private ISubPageHost? _host;

    private bool _sourceReady;
    private GeneralSettings? _general;
    internal const string BmclApiUrl = "https://bmclapi2.bangbang93.com";

    internal static bool IsGitHubMirror(string? mirror) =>
        Uri.TryCreate(mirror?.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.ToLowerInvariant() is "gh-proxy.com" or "gh-proxy.org";

    internal static int GameMirrorIndex(string? mirror)
    {
        if (string.IsNullOrWhiteSpace(mirror) || IsGitHubMirror(mirror)) return 0;
        return mirror.Contains("bmcl", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    public DownloadPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            VersionNames.Changed += OnVersionNamesChanged;
            VersionNames.RefreshInBackground();
        };
        Unloaded += (_, _) => VersionNames.Changed -= OnVersionNamesChanged;
        Loaded += (_, _) => _ = LoadAsync();
        Loaded += (_, _) => _ = LoadSourceAsync();
        Loaded += (_, _) => ApplyCompact(App.MainWindow?.IsCompact ?? false);
        if (App.MainWindow is { } shell) shell.CompactChanged += (_, _) => ApplyCompact(shell.IsCompact);
    }

    /// <summary>后台抓到了新的代号：重建左栏分组，保留当前选中项和列表滚动。</summary>
    private void OnVersionNamesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_all.Count == 0 || _loading) return;
        BuildGroups();
        ApplyFilter();
    });

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is TabNav nav)
        {
            _host = nav.Host;
            PageHeader.Visibility = Visibility.Collapsed;
            HeaderRow.Height = new GridLength(0);
            // 「获取」宿主已经给了 48 的页边距，这里再叠一层会让内容比标题缩进 96。
            PageRoot.Padding = new Thickness(0);
        }
    }

    private void ApplyCompact(bool compact)
    {
        PageTitle.FontSize = compact ? 22 : 28;
        NavColumn.Width = new GridLength(compact ? 184 : 224);
    }

    // ------------------------------------------------------------------ 读取

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        SetState(PageState.Loading);
        try
        {
            var result = await App.Backend.GetMojangVersionsAsync();
            _all.Clear();
            if (result?.Versions is { Count: > 0 })
            {
                _all.AddRange(result.Versions);
                var latest = _all.FirstOrDefault(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase));
                if (latest is not null) latest.IsLatest = true;
            }

            if (result is null || !result.Ok)
            {
                ShowError("读取版本清单失败。", result?.Message ?? "无法读取 Mojang 版本清单。");
                return;
            }

            if (_all.Count == 0)
            {
                ShowEmpty();
                return;
            }

            BuildGroups();
            ApplyFilter(resetList: true, resetNav: true);
        }
        catch (Exception ex)
        {
            _all.Clear();
            ShowError("读取版本清单失败。", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>装完之后只同步「已安装」标记：列表不重建，滚动位置、分类、搜索都原样。</summary>
    private async Task SyncInstalledAsync()
    {
        try
        {
            var result = await App.Backend.GetMojangVersionsAsync();
            if (result is not { Ok: true }) return;
            var map = result.Versions.GroupBy(v => v.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Installed, StringComparer.Ordinal);
            foreach (var v in _all)
                if (map.TryGetValue(v.Id, out var installed)) v.Installed = installed;
            foreach (var row in _rows) row.Refresh();
        }
        catch
        {
            // 同步失败只是标记没刷新，下次进页面会重新读。
        }
    }

    // ------------------------------------------------------------------ 下载源

    private async Task LoadSourceAsync()
    {
        try
        {
            var g = await App.Backend.GetGeneralSettingsAsync();
            if (!g.Ok) return;
            _general = g;
            _sourceReady = false;
            var index = GameMirrorIndex(g.Mirror);
            SourceBox.SelectedIndex = index == 2 ? -1 : index;
            SourceBox.PlaceholderText = "自定义镜像";
        }
        catch
        {
            // 读不到就保持下拉未选中，不阻塞版本列表。
        }
        finally
        {
            _sourceReady = true;
        }
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sourceReady || _general is null) return;
        if (SourceBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var mirror = tag == "bmclapi" ? BmclApiUrl : "";
        if (string.Equals(mirror, _general.Mirror, StringComparison.OrdinalIgnoreCase)) return;

        var g = _general;
        var result = await App.Backend.SetGeneralSettingsAsync(g.DownloadSource, mirror, g.DlThreads, g.CfApiKey, g.JavaPath);
        if (result.Ok)
        {
            _general = g with { Mirror = mirror };
            ShowStatus(InfoBarSeverity.Success, "下载源已切换",
                mirror.Length == 0 ? "之后的下载走官方源。" : "之后的下载优先走 BMCLAPI，失败再回官方。");
        }
        else
        {
            _sourceReady = false;
            var index = GameMirrorIndex(_general.Mirror);
            SourceBox.SelectedIndex = index == 2 ? -1 : index;
            _sourceReady = true;
            ShowStatus(InfoBarSeverity.Error, "没能切换下载源", result.Message ?? "保存失败，请重试一次。");
        }
    }

    // ------------------------------------------------------------------ 分组（左栏）

    /// <summary>
    /// 按「重磅更新」大版本分组。
    /// 这张表只是兜底：实际显示优先取 <see cref="VersionNames"/>（MC 百科抓取、本地缓存 7 天），抓不到才用这里。
    /// 26.4 还没有官方名称，留空，左栏会写「最新 x · 年月」。
    /// </summary>
    private static readonly (string Maj, string Name)[] UpdateDefs =
    {
        ("26.4", ""), ("26.3", "奔赴荒野"), ("26.2", "混沌立方"), ("26.1", "小鬼当家"),
        ("1.21", "棘巧试炼"), ("1.20", "足迹与故事"), ("1.19", "荒野更新"),
        ("1.18", "洞穴与山崖 第二部分"), ("1.17", "洞穴与山崖 第一部分"),
        ("1.16", "下界更新"), ("1.15", "嗡嗡蜂群"), ("1.14", "村庄与掠夺"),
        ("1.13", "海洋更新"), ("1.12", "多彩世界"), ("1.11", "探索更新"),
        ("1.10", "霜炙更新"), ("1.9", "战斗更新"), ("1.8", "缤纷更新"),
        ("1.7", "改变世界的更新"),
    };

    internal static string MajorOf(string id)
    {
        var m = Regex.Match(id ?? "", @"^(\d+\.\d+)");
        return m.Success ? m.Value : "";
    }

    private static string MajorCaption(string maj, string name) =>
        string.IsNullOrEmpty(name) ? maj : $"{maj} · {name}";

    private static bool IsAncient(MojangVersionInfo version) =>
        version.Type is "old_alpha" or "old_beta";

    private static readonly DateTimeOffset ModernSnapshotSince = new(2019, 4, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly Regex ModernSnapshotId = new(
        @"^(\d{2}w\d{2}[a-z]\w*|\d+\.\d+(\.\d+)?-(pre|rc)\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>旧格式快照：早于 1.14（2019-04-23）发布的，或 id 不是现行「yyWwwa / x.y-preN / -rcN」写法的（RV-Pre、Combat Test、Shareware 等）。</summary>
    private static bool IsOldFormatSnapshot(MojangVersionInfo v)
    {
        if (!string.Equals(v.Type, "snapshot", StringComparison.OrdinalIgnoreCase)) return false;
        if (!ModernSnapshotId.IsMatch(v.Id)) return true;
        return DateTimeOffset.TryParse(v.ReleaseTime, out var t) && t < ModernSnapshotSince;
    }

    private static string AncientKindOf(string id)
    {
        var first = id.Length > 0 ? char.ToLowerInvariant(id[0]) : ' ';
        return first switch { 'b' => "beta", 'a' => "alpha", _ => "classic" };
    }

    private static string YearOf(MojangVersionInfo v) =>
        DateTimeOffset.TryParse(v.ReleaseTime, out var t) ? t.ToLocalTime().Year.ToString() : "更早";

    /// <summary>没有官方代号的组，副标题写该组最新的版本号和年月，保持每行两行结构一致。</summary>
    private static string LatestSub(List<MojangVersionInfo> ordered)
    {
        var v = ordered[0];
        var date = DateTimeOffset.TryParse(v.ReleaseTime, out var t) ? t.ToLocalTime().ToString("yyyy-MM") : "";
        return date.Length > 0 ? $"最新 {v.Id} · {date}" : $"最新 {v.Id}";
    }

    private void BuildGroups()
    {
        var groups = new List<NavGroup>();

        var buckets = UpdateDefs.ToDictionary(x => x.Maj, _ => new List<MojangVersionInfo>(), StringComparer.Ordinal);
        var newer = new List<MojangVersionInfo>();
        var legacy = new List<MojangVersionInfo>();
        var snapshots = new List<MojangVersionInfo>();
        var ancient = new List<MojangVersionInfo>();
        foreach (var v in _all)
        {
            if (IsAncient(v)) { ancient.Add(v); continue; }
            if (string.Equals(v.Type, "snapshot", StringComparison.OrdinalIgnoreCase)) { snapshots.Add(v); continue; }

            var maj = MajorOf(v.Id);
            if (buckets.TryGetValue(maj, out var list)) list.Add(v);
            else if (maj.Length > 0 && maj[0] == '1' && double.TryParse(maj, System.Globalization.CultureInfo.InvariantCulture, out var numeric) && numeric < 1.7)
                legacy.Add(v);
            else
                newer.Add(v);   // 清单里出现了表里还没登记的新大版本，放最前面
        }

        void Add(string cat, string key, string title, string sub, string heading, IEnumerable<MojangVersionInfo> items)
        {
            var ordered = items.OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal).ToList();
            if (ordered.Count == 0) return;
            if (sub.Length == 0) sub = LatestSub(ordered);
            groups.Add(new NavGroup(cat, key, title, sub, heading, ordered));
        }

        Add("release", "rel:newer", "较新版本", "", "较新的正式版", newer);
        foreach (var (maj, fallback) in UpdateDefs)
        {
            var name = VersionNames.Get(maj) ?? fallback;   // 缓存里有 MC 百科的代号就用它，没有退回硬编码表
            Add("release", "rel:" + maj, maj, name, MajorCaption(maj, name), buckets[maj]);
        }
        Add("release", "rel:legacy", "1.0 – 1.6.4", "古早正式版", "古早正式版（1.0–1.6.4）", legacy);

        foreach (var year in snapshots.Where(v => !IsOldFormatSnapshot(v))
                     .GroupBy(YearOf).OrderByDescending(g => g.Key, StringComparer.Ordinal))
            Add("snapshot", "snap:" + year.Key, year.Key + " 年", "", year.Key + " 年的快照与预发布", year);
        Add("snapshot", "snap:old", "旧格式快照", "1.14 之前及特殊测试版", "旧格式快照", snapshots.Where(IsOldFormatSnapshot));

        Add("ancient", "anc:beta", "Beta", "b1.x", "远古 Beta 版（b1.x）", ancient.Where(v => AncientKindOf(v.Id) == "beta"));
        Add("ancient", "anc:alpha", "Alpha", "a1.x", "远古 Alpha 版（a1.x）", ancient.Where(v => AncientKindOf(v.Id) == "alpha"));
        Add("ancient", "anc:classic", "Classic / Infdev", "更早的测试版", "远古 Classic / Infdev 版", ancient.Where(v => AncientKindOf(v.Id) == "classic"));

        _groups = groups;
    }

    /// <summary>当前分类没有任何大版本时退到有内容的分类；分类内选中的大版本不存在时回到它的第一项。</summary>
    private void EnsureSelection()
    {
        if (!_groups.Any(g => g.Cat == _selCat))
            _selCat = _groups.FirstOrDefault()?.Cat ?? "release";
        if (!_selKeyByCat.TryGetValue(_selCat, out var key) || !_groups.Any(g => g.Key == key))
        {
            key = _groups.FirstOrDefault(g => g.Cat == _selCat)?.Key ?? "";
            _selKeyByCat[_selCat] = key;
        }
    }

    // ------------------------------------------------------------------ 搜索 / 过滤

    private void QueryBox_QueryChanged(object? sender, string text)
    {
        _query = text;
        ApplyFilter(resetList: true, resetNav: true);
    }

    public bool TryFocusSearch() => QueryBox.FocusInput();

    public void ApplySearch(string query)
    {
        _query = query ?? "";
        QueryBox.Text = _query;
        ApplyFilter(resetList: true, resetNav: true);
    }

#if DEBUG
    /// <summary>截图用：等清单加载完，按版本号直接打开安装子页。</summary>
    public async Task DebugOpenDetail(string id)
    {
        for (var i = 0; i < 100 && _all.Count == 0; i++) await Task.Delay(100);
        var v = _all.FirstOrDefault(x => x.Id == id);
        if (v is not null) OpenDetail(v);
        App.Trace("DebugOpenDetail " + id + " found=" + (v is not null));
    }
#endif

    private void Cat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || tag == _selCat) return;
        _selCat = tag;
        ApplyFilter(resetList: true, resetNav: true);
    }

    private void PaintCategories()
    {
        var on = PagePalette.ThemeBrush("AppBrandSoftBrush");
        var text = PagePalette.ThemeBrush("AppTextBrush");
        var text2 = PagePalette.ThemeBrush("AppText2Brush");
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Paint(CatRelease, _selCat == "release");
        Paint(CatSnapshot, _selCat == "snapshot");
        Paint(CatAncient, _selCat == "ancient");

        CatReleaseCount.Text = CountOf("release");
        CatSnapshotCount.Text = CountOf("snapshot");
        CatAncientCount.Text = CountOf("ancient");

        string CountOf(string cat) => _groups.Where(g => g.Cat == cat).Sum(g => g.Items.Count).ToString();

        void Paint(Button button, bool selected)
        {
            button.Background = selected ? on : clear;
            button.Foreground = selected ? text : text2;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>重建左栏 + 右栏。搜索时左栏只剩「搜索结果」，右栏跨所有分类。</summary>
    private void ApplyFilter(bool resetList = false, bool resetNav = false)
    {
        if (_all.Count == 0) return;

        var navScroll = FindScrollViewer(NavList);
        var navOffset = resetNav ? 0 : navScroll?.VerticalOffset ?? 0;

        var q = _query.Trim();
        var searching = q.Length > 0;
        var nav = new List<NavRow>();
        List<MojangVersionInfo> items;
        CatBar.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;

        if (searching)
        {
            items = _all
                .Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal)
                .ToList();
            if (items.Count == 0)
            {
                ShowEmpty();
                return;
            }
            var (logo, logoStretch) = IconFor("");
            nav.Add(new NavRow
            {
                Key = "search", Title = "搜索结果", Sub = $"跨正式版 / 快照 / 远古版",
                CountText = items.Count.ToString(), Selected = true, Icon = logo, IconStretch = logoStretch,
            });
            ListTitle.Text = "搜索结果";
            ListCount.Text = $"{items.Count} 个版本包含「{q}」";
        }
        else
        {
            EnsureSelection();
            PaintCategories();
            var selKey = _selKeyByCat[_selCat];
            foreach (var g in _groups.Where(g => g.Cat == _selCat))
            {
                var (icon, stretch) = IconFor(g.Items[0].Id);
                nav.Add(new NavRow
                {
                    Key = g.Key, Title = g.Title, Sub = g.Sub,
                    CountText = g.Items.Count.ToString(), Selected = g.Key == selKey,
                    Icon = icon, IconStretch = stretch,
                });
            }

            var sel = _groups.First(g => g.Key == selKey);
            items = sel.Items;
            ListTitle.Text = sel.Heading;
            ListCount.Text = $"{items.Count} 个版本";
        }

        NavList.ItemsSource = nav;
        if (navOffset > 0)
            DispatcherQueue.TryEnqueue(() => FindScrollViewer(NavList)?.ChangeView(null, navOffset, null, true));

        if (resetList || VersionList.ItemsSource is null)
        {
            _rows = items.Select(v => new DownloadRow(v, searching || v.Type == "snapshot")).ToList();
            VersionList.ItemsSource = _rows;
            FindScrollViewer(VersionList)?.ChangeView(null, 0, null, true);
        }

        SetState(PageState.List);
    }

    private void Nav_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NavRow row || row.Key == "search") return;
        if (_selKeyByCat.TryGetValue(_selCat, out var cur) && cur == row.Key) return;
        _selKeyByCat[_selCat] = row.Key;
        ApplyFilter(resetList: true);
    }

    // ------------------------------------------------------------------ 页面状态

    private void SetState(PageState state)
    {
        LoadingPanel.Visibility = state == PageState.Loading ? Visibility.Visible : Visibility.Collapsed;
        BrowseGrid.Visibility = state == PageState.List ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == PageState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == PageState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string title, string message)
    {
        ErrorTitle.Text = title;
        ErrorSummary.Text = ClipText(message, 120);
        SetState(PageState.Error);
    }

    private static string ClipText(string? text, int max)
    {
        var value = (text ?? "").Trim().ReplaceLineEndings(" ");
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>空结果一律给出下一步：重新读取 / 清除搜索，而不是只写「暂无」。</summary>
    private void ShowEmpty()
    {
        if (_all.Count == 0)
        {
            EmptyText.Title = "还没有版本清单";
            EmptyText.Hint = "网络不通或缓存为空时会这样，重新读取一次试试。";
            EmptyAction.Content = "重新读取清单";
            EmptyAction.Tag = "reload";
        }
        else
        {
            EmptyText.Title = $"没有匹配「{_query.Trim()}」的版本";
            EmptyText.Hint = "搜索范围是所有分类，可以换一个更短的版本号。";
            EmptyAction.Content = "清除搜索";
            EmptyAction.Tag = "clear";
        }
        SetState(PageState.Empty);
    }

    private async void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "clear": ApplySearch(""); break;
            default: await LoadAsync(); break;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await LoadAsync();
    }

    // ------------------------------------------------------------------ 子页 / 安装

    private void Version_RowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DownloadRow row) OpenDetail(row.Source);
    }

    /// <summary>点版本行：整页换成「获取 › 下载版本 › x.y.z」子页；列表页留在后面，返回时原样还在。</summary>
    private void OpenDetail(MojangVersionInfo v)
    {
        if (_host is null) return;
        var args = new DownloadDetailArgs(v, _host,
            (kind, loaderVersion, withPrereq, prereqVersion) =>
                _ = InstallAsync(v, kind, loaderVersion, withPrereq, prereqVersion));
        _host.OpenSubPage(typeof(DownloadDetailPage), args, "获取", "下载版本", v.Id);
    }

    private async Task InstallAsync(MojangVersionInfo v, string kind, string loaderVersion, bool withPrereq,
        string prereqVersion = "")
    {
        // 目标文件夹已经存在时先要一个新实例名：绝不悄悄覆盖别人已有的实例。
        var instanceName = await AskInstanceNameIfConflictAsync(v, kind, loaderVersion);
        if (instanceName is null) return;

        var task = TaskCenter.Add($"安装 {v.Id}", kind == "vanilla" ? "原版" : $"{kind} 加载器", "download");
        var progress = new Progress<string>(line =>
        {
            var raw = line ?? "";
            // 结构化事件：@steps 步骤名 / @step 单步状态 / @stat 速度与剩余文件；其余仍是旧的「百分比|文案」。
            if (raw.StartsWith("@steps|", StringComparison.Ordinal))
            {
                var names = raw[7..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Length > 0) TaskCenter.BuildSteps(task, names);
                return;
            }
            if (raw.StartsWith("@step|", StringComparison.Ordinal))
            {
                var f = raw[6..].Split('|');
                if (f.Length >= 4 && int.TryParse(f[0], out var idx) && int.TryParse(f[1], out var state)
                    && double.TryParse(f[2], out var spct))
                {
                    TaskCenter.Step(task, idx, state, spct, f.Length > 3 ? f[3] : "");
                }
                return;
            }
            if (raw.StartsWith("@live|", StringComparison.Ordinal))
            {
                task.SetLive(raw[6..]);
                return;
            }
            if (raw.StartsWith("@stat|", StringComparison.Ordinal))
            {
                var f = raw[6..].Split('|');
                if (f.Length >= 4 && double.TryParse(f[0], out var overall))
                {
                    var speed = double.TryParse(f[1], out var sp) ? sp : 0;
                    var remain = int.TryParse(f[2], out var rm) ? rm : 0;
                    TaskCenter.Stats(task, speed, remain > 0 ? $"{remain:N0} 个文件" : "");
                    TaskCenter.Progress(task, overall, task.Detail);
                    // 当前文件放任务卡右侧那行，灵动岛只留百分比
                    if (f.Length >= 4 && f[3].Length > 0)
                    {
                        task.SetLive(System.IO.Path.GetFileName(f[3].Replace('\\', '/')));
                    }
                }
                return;
            }
            var parts = raw.Split('|', 2);
            if (parts.Length == 2 && double.TryParse(parts[0], out var pct) && pct > 0)
            {
                TaskCenter.Progress(task, pct, parts[1]);
            }
            else
            {
                TaskCenter.Report(task, parts.Length == 2 ? parts[1] : raw);
            }
        });
        var result = await App.Backend.InstallVersionAsync(v.Id, kind, loaderVersion, progress, withPrereq,
            instanceName ?? "", prereqVersion);
        if (result.Ok)
        {
            TaskCenter.Complete(task, "安装完成");
            await SyncInstalledAsync();
            await OfferSwitchAsync(string.IsNullOrWhiteSpace(result.Version) ? v.Id : result.Version);
        }
        else TaskCenter.Fail(task, result.Message ?? "安装失败");
    }

    /// <summary>这次安装会占用的实例文件夹名；Forge/NeoForge 由安装器决定，这里返回空。</summary>
    private static string ExpectedInstanceId(MojangVersionInfo v, string kind, string loaderVersion) => kind switch
    {
        "vanilla" => v.Id,
        "fabric" => loaderVersion.Length > 0 ? $"{v.Id}-Fabric_{loaderVersion}" : "",
        "quilt" => loaderVersion.Length > 0 ? $"{v.Id}-Quilt_{loaderVersion}" : "",
        _ => "",
    };

    private static string SuggestFreeName(string baseName, HashSet<string> known)
    {
        for (var i = 2; i < 200; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!known.Contains(candidate)) return candidate;
        }
        return $"{baseName}-{DateTime.Now:HHmmss}";
    }

    /// <summary>返回 ""＝没有冲突直接装；返回新名字＝用这个名字装；返回 null＝用户取消。</summary>
    private async Task<string?> AskInstanceNameIfConflictAsync(MojangVersionInfo v, string kind, string loaderVersion)
    {
        var expected = ExpectedInstanceId(v, kind, loaderVersion);
        if (expected.Length == 0) return "";
        var snapshot = await App.Backend.GetSnapshotAsync();
        var ids = snapshot?.Versions.Select(x => x.Id).ToList() ?? new List<string>();
        var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!known.Contains(expected)) return "";

        var box = new TextBox
        {
            Text = SuggestFreeName(expected, known),
            PlaceholderText = "新的实例名称",
            MaxLength = 64,
            SelectionStart = 64,
        };
        var hint = new TextBlock
        {
            Text = $"已经有一个叫「{expected}」的实例文件夹了。换个名字就能并装一个新实例，原来的不受影响。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(box);
        stack.Children.Add(hint);
        var invalid = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        while (true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "实例名已被占用",
                Content = stack,
                PrimaryButtonText = "用这个名字安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var name = box.Text.Trim();
            if (name.Length == 0) { hint.Text = "名字不能为空。"; continue; }
            if (name.IndexOfAny(invalid) >= 0) { hint.Text = "名字里不能有 \\ / : * ? \" < > |"; continue; }
            if (known.Contains(name)) { hint.Text = $"已经有叫「{name}」的实例了，换一个。"; continue; }
            var dir = Path.Combine(GameDirectoryResolver.Resolve(AppPreferencesStore.Load()).FirstOrDefault() ?? "", "versions", name);
            if (dir.Length > 0 && Directory.Exists(dir)) { hint.Text = $"游戏目录里已经有「{name}」文件夹了，换一个。"; continue; }
            return name;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>装完在底部提示一下，并给一个「切换到它」：新实例不会自己变成当前实例。</summary>
    private async Task OfferSwitchAsync(string versionId)
    {
        var snapshot = await App.Backend.GetSnapshotAsync(true);
        var target = snapshot?.Versions.FirstOrDefault(x => string.Equals(x.Id, versionId, StringComparison.OrdinalIgnoreCase));
        var isCurrent = target is not null && string.Equals(snapshot?.Current?.Id, target.Id, StringComparison.OrdinalIgnoreCase);
        ShowStatus(InfoBarSeverity.Success, "安装完成", isCurrent ? $"{versionId} 已安装，它就是当前实例。" : $"已安装 {versionId}。");
        if (target is null || isCurrent) return;
        var button = new Button { Content = "切换到这个实例" };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var (ok, message) = await InstanceSelection.SelectAsync(target);
            ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "已切换" : "切换失败", ok ? $"当前实例：{target.Id}" : message);
        };
        StatusBar.ActionButton = button;
    }
    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.ActionButton = null;
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
