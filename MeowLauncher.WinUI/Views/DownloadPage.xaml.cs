using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
            Icon = VanillaIcon;
            Meta = showType ? $"{source.TypeText} · {source.DateText}" : source.DateText;
        }

        public MojangVersionInfo Source { get; }
        public string Id => Source.Id;
        public string Meta { get; }
        public ImageSource Icon { get; }
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

        public Visibility SelectedVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public override string ToString() => Title;
    }

    /// <summary>左栏的一个大版本（或一年快照 / 一类远古版），右栏显示它的全部版本。</summary>
    private sealed record NavGroup(string Cat, string Key, string Title, string Sub, string Heading,
        List<MojangVersionInfo> Items);

    // ------------------------------------------------------------------ 图标

    private static ImageSource? _vanillaIcon;
    private static ImageSource VanillaIcon => _vanillaIcon ??= InstanceIcon.ForLoader("vanilla");

    // ------------------------------------------------------------------ 状态

    private readonly List<MojangVersionInfo> _all = [];
    private List<NavGroup> _groups = [];
    private List<DownloadRow> _rows = [];
    // 列表只绑一次集合，之后原地增删/移动：重新赋值 ItemsSource 会让列表整体重建，
    // 视觉上就是「一直刷新页面」，滚动位置和动画也跟着跳。
    private readonly ObservableCollection<NavRow> _navItems = new();
    private readonly ObservableCollection<DownloadRow> _rowItems = new();

    // 左栏的分类 / 选中状态在页面实例之间记住：离开再回来保持原样。
    private static string _selCat = "release";
    private static readonly Dictionary<string, string> _selKeyByCat = new(StringComparer.Ordinal);

    private bool _loading;
    private string _query = "";
    private ISubPageHost? _host;

    private bool _sourceReady;
    private GeneralSettings? _general;
    internal const string BmclApiUrl = "https://bmclapi2.bangbang93.com";
    internal const string AutoMirror = "auto";
    internal const string OfficialMirror = "official";
    internal const string MeowMirror = "meow";

    internal static bool IsGitHubMirror(string? mirror) =>
        Uri.TryCreate(mirror?.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.ToLowerInvariant() is "gh-proxy.com" or "gh-proxy.org";

    // 设置里没写渠道（或写的就是 auto）时，走智能分配。
    internal static bool IsAutoMirror(string? mirror)
    {
        var value = mirror?.Trim() ?? "";
        return value.Length == 0 || value.Equals(AutoMirror, StringComparison.OrdinalIgnoreCase);
    }

    // 设置里存的是渠道名（auto / official / meow）或具体镜像地址。
    // 智能分配是独立开关，不占下拉位置，所以下拉只有四个具体渠道。
    internal static int GameMirrorIndex(string? mirror)
    {
        var value = mirror?.Trim() ?? "";
        if (IsAutoMirror(value) || IsGitHubMirror(value)) return 0;
        if (value.Equals(OfficialMirror, StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.Contains("bmcl", StringComparison.OrdinalIgnoreCase)) return 1;
        if (value.Equals(MeowMirror, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
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
        Loaded += (_, _) =>
        {
            if (NavList.ItemsSource is null) NavList.ItemsSource = _navItems;
            if (VersionList.ItemsSource is null) VersionList.ItemsSource = _rowItems;
        };
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

    private async Task LoadAsync(bool force = false)
    {
        if (_loading) return;
        _loading = true;
        // 已经有列表时重进页面不再切回整页 Loading：只把状态条文字换掉，
        // 否则每次进来都像「页面重新刷新了一遍」。
        if (_all.Count == 0) SetState(PageState.Loading);
        try
        {
            var result = await App.Backend.GetMojangVersionsAsync(force);
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
            if (force && !string.IsNullOrEmpty(result.Message))
                ShowStatus(InfoBarSeverity.Warning, "暂时无法刷新", result.Message);
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
            // 读不到状态就当作都未安装，不阻塞列表。
        }
    }

    private async Task LoadSourceAsync()
    {
        try
        {
            var g = await App.Backend.GetGeneralSettingsAsync();
            if (!g.Ok) return;
            _general = g;
            _sourceReady = false;
            SelectSourceFor(SourceBox, g.Mirror);
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

    // 下载页是安装源的唯一入口：智能分配 + 四个具体渠道。
    private static void SelectSourceFor(ComboBox box, string? mirror)
    {
        box.SelectedIndex = IsAutoMirror(mirror) ? 0 : GameMirrorIndex(mirror) + 1;
    }

    private static string DescribeSource(string? mirror)
    {
        if (IsAutoMirror(mirror)) return "联网时实测官方与镜像的速度，清单、库、资源文件各选更快的那一个。";
        return GameMirrorIndex(mirror) switch
        {
            0 => "之后的下载全部走 Mojang 官方源。",
            1 => "之后的下载优先走 BMCLAPI，失败再回官方。",
            2 => "资源文件走自建 CDN，其余走 Mojang 官方源。",
            _ => "之后的下载走你填写的自定义镜像。",
        };
    }

    private async Task<string?> PromptCustomMirrorAsync(string current)
    {
        var box = new TextBox { Text = current ?? "", PlaceholderText = "https://", Height = 34 };
        var dialog = new ContentDialog
        {
            Title = "自定义下载镜像",
            Content = box,
            PrimaryButtonText = "使用这个镜像",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sourceReady || _general is null) return;
        if (SourceBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var current = _general.Mirror ?? "";
        string mirror;
        switch (tag)
        {
            case "auto": mirror = AutoMirror; break;
            case "official": mirror = OfficialMirror; break;
            case "bmclapi": mirror = BmclApiUrl; break;
            case "meow": mirror = MeowMirror; break;
            case "custom":
                var typed = await PromptCustomMirrorAsync(IsAutoMirror(current) ? "" : current);
                if (string.IsNullOrWhiteSpace(typed))
                {
                    _sourceReady = false;
                    SelectSourceFor(SourceBox, current);
                    _sourceReady = true;
                    return;
                }
                mirror = typed;
                break;
            default:
                return;
        }
        if (string.Equals(mirror, current, StringComparison.OrdinalIgnoreCase)) return;

        var g = _general;
        var result = await App.Backend.SetGeneralSettingsAsync(g.DownloadSource, mirror, g.DlThreads, g.CfApiKey, g.JavaPath);
        if (result.Ok)
        {
            _general = g with { Mirror = mirror };
            ShowStatus(InfoBarSeverity.Success, "下载源已切换", DescribeSource(mirror));
        }
        else
        {
            _sourceReady = false;
            SelectSourceFor(SourceBox, g.Mirror);
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

    private PageSearchBox? _filterBox;
    private bool _syncingFilter;

    /// <summary>「获取」宿主把顶栏筛选框交进来；本页不再自带搜索框。</summary>
    public void AttachQueryBox(PageSearchBox box)
    {
        if (!ReferenceEquals(_filterBox, box))
        {
            if (_filterBox is not null) _filterBox.QueryChanged -= OnFilterChanged;
            _filterBox = box;
            box.QueryChanged += OnFilterChanged;
        }
        SyncFilterBox();
    }

    private void SyncFilterBox()
    {
        if (_filterBox is null) return;
        _syncingFilter = true;
        _filterBox.Text = _query;
        _syncingFilter = false;
    }

    private void OnFilterChanged(object? sender, string text)
    {
        if (_syncingFilter) return;
        _query = text;
        ApplyFilter(resetList: true, resetNav: true);
    }

    public bool TryFocusSearch() => _filterBox?.FocusInput() ?? false;

    public void ApplySearch(string query)
    {
        _query = query ?? "";
        SyncFilterBox();
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
            nav.Add(new NavRow
            {
                Key = "search", Title = "搜索结果", Sub = $"跨正式版 / 快照 / 远古版",
                CountText = items.Count.ToString(), Selected = true, Icon = VanillaIcon,
            });
            ListTitle.Text = "搜索结果";
            ListCount.Text = $"{items.Count} 个版本包含「{q}」";
            RightLoading.Visibility = Visibility.Collapsed;
            _ = LoadCoverAsync("");
            _ = LoadNotesAsync("");
        }
        else
        {
            EnsureSelection();
            PaintCategories();
            var selKey = _selKeyByCat[_selCat];
            foreach (var g in _groups.Where(g => g.Cat == _selCat))
            {
                nav.Add(new NavRow
                {
                    Key = g.Key, Title = g.Title, Sub = g.Sub,
                    CountText = g.Items.Count.ToString(), Selected = g.Key == selKey,
                    Icon = VanillaIcon,
                });
            }

            var sel = _groups.First(g => g.Key == selKey);
            items = sel.Items;
            ListTitle.Text = sel.Heading;
            ListCount.Text = $"{items.Count} 个版本";
            // 只有正式版（26.3 这种）查更新内容；封面：正式版用组名，快照取组内最新版本，远古用 Beta/Alpha/Classic 整组页
            var formal = System.Text.RegularExpressions.Regex.IsMatch(sel.Title, @"^\d+\.\d+$");
            var coverVer = formal
                ? sel.Title
                : sel.Cat == "ancient"
                    ? (sel.Key.EndsWith(":beta", StringComparison.Ordinal) ? "Beta"
                        : sel.Key.EndsWith(":alpha", StringComparison.Ordinal) ? "Alpha" : "Classic")
                    : (sel.Items.Count > 0 ? sel.Items[0].Id : "");
            if (!formal)
            {
                _notesFor = null;
                NotesHost.Children.Clear();
                NotesHost.Visibility = Visibility.Collapsed;
            }

            var seq = ++_infoSeq;
            var coverTask = LoadCoverAsync(coverVer, sel.Heading);
            var notesTask = formal ? LoadNotesAsync(coverVer) : Task.CompletedTask;
            RightLoading.Visibility = coverVer.Length > 0 || formal ? Visibility.Visible : Visibility.Collapsed;
            _ = HideRightLoadingAsync(seq, coverTask, notesTask);
        }

        SyncRows(_navItems, nav);
        if (navOffset > 0)
            DispatcherQueue.TryEnqueue(() => FindScrollViewer(NavList)?.ChangeView(null, navOffset, null, true));

        if (resetList || VersionList.ItemsSource is null || _rowItems.Count != items.Count)
        {
            // 同一个版本复用原来的行对象，只让它的文字变；换了分类/搜索才整批换。
            var old = _rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
            _rows = items.Select(v => old.TryGetValue(v.Id, out var keep) ? keep : new DownloadRow(v, searching || v.Type == "snapshot")).ToList();
            SyncRows(_rowItems, _rows);
            if (resetList) FindScrollViewer(VersionList)?.ChangeView(null, 0, null, true);
        }

        SetState(PageState.List);
    }

    private string? _coverFor;
    private int _infoSeq;

    /// <summary>整栏加载层：等封面和更新内容都完成（或确认没有）后一次性收起，避免切组时一部分一部分地冒出来。</summary>
    private async Task HideRightLoadingAsync(int seq, Task a, Task b)
    {
        try { await Task.WhenAll(a, b); } catch { }
        if (seq != _infoSeq) return;
        RightLoading.Visibility = Visibility.Collapsed;
    }

    /// <summary>右栏标题下那张宣传图：只跟选中的大版本走，取不到就整块收起。</summary>
    private async Task LoadCoverAsync(string versionId, string heading = "")
    {
        if (versionId == _coverFor) return;
        _coverFor = versionId;
        if (string.IsNullOrEmpty(versionId))
        {
            CoverBrush.ImageSource = null;
            CoverCard.Visibility = Visibility.Collapsed;
            UpdateListHeader();
            return;
        }

        // 本地缓存优先：维基的图跟着版本走，命中就不用等后端冷启动
        var cached = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MeowLauncher", "version-notes", "cover-" + versionId + ".jpg");
        string? path = System.IO.File.Exists(cached) ? cached : null;

        if (path is null)
        {
            try { path = await App.Backend.GetVersionCoverAsync(versionId); }
            catch { }
            if (_coverFor != versionId) return;
        }

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            CoverBrush.ImageSource = null;
            CoverCard.Visibility = Visibility.Collapsed;
            UpdateListHeader();
            return;
        }

        CoverTitle.Text = heading;
        CoverSub.Text = "Minecraft Java 版";
        CoverBrush.ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        CoverCard.Visibility = Visibility.Visible;
        UpdateListHeader();
    }

    /// <summary>头图和更新内容都空着的时候，把原来的标题行亮出来，免得右栏一片空。</summary>
    private void UpdateListHeader()
    {
        var hasContent = CoverCard.Visibility == Visibility.Visible || NotesHost.Visibility == Visibility.Visible;
        ListHeader.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? _notesFor;

    /// <summary>右栏头图下面的更新日志：取中文 Minecraft Wiki 的新内容 / 更改 / 修复，取不到就整块收起。</summary>
    private async Task LoadNotesAsync(string versionId)
    {
        if (versionId == _notesFor) return;
        _notesFor = versionId;
        NotesHost.Children.Clear();
        NotesHost.Visibility = Visibility.Collapsed;
        NotesLoading.Visibility = Visibility.Collapsed;
        UpdateListHeader();
        if (string.IsNullOrEmpty(versionId)) return;

        // 本地有缓存就直接出，不闪加载条；第一次联网才显示占位
        var cached = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MeowLauncher", "version-notes",
            Regex.Replace(versionId, "[^0-9A-Za-z._-]", "_") + ".json");
        if (!System.IO.File.Exists(cached)) NotesLoading.Visibility = Visibility.Visible;

        VersionNotes? notes = null;
        try { notes = await App.Backend.GetVersionNotesAsync(versionId); }
        catch { }
        finally { NotesLoading.Visibility = Visibility.Collapsed; }
        if (_notesFor != versionId) return;
        if (notes?.Sections is null || notes.Sections.Count == 0) return;

        foreach (var sec in notes.Sections)
        {
            AddNoteText(sec.Title, "NoteSecStyle");
            foreach (var sub in sec.Subs)
            {
                if (!string.IsNullOrWhiteSpace(sub.Name)) AddNoteText(sub.Name, "NoteSubStyle");
                var shown = 0;
                foreach (var entry in sub.Entries)
                {
                    if (shown >= 10) break;
                    shown++;
                    var body = entry.Points.Count > 0
                        ? entry.Name + "：" + string.Join("；", entry.Points)
                        : entry.Name;
                    AddNoteText(body, "NoteEntryStyle");
                }
                if (sub.Entries.Count > shown) AddNoteText($"另有 {sub.Entries.Count - shown} 项…", "NoteEntryStyle");
            }
        }
        NotesHost.Visibility = NotesHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateListHeader();
    }

    private void AddNoteText(string text, string styleKey)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var tb = new TextBlock { Text = text };
        if (Resources.TryGetValue(styleKey, out var st) && st is Style s) tb.Style = s;
        NotesHost.Children.Add(tb);
    }

    /// <summary>原地对齐集合：多删、缺插、错位移动，已存在的实例原样保留（不重建控件）。</summary>
    private static void SyncRows<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
    {
        var keep = new HashSet<T>(wanted);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = -1;
            for (var j = i; j < target.Count; j++)
                if (EqualityComparer<T>.Default.Equals(target[j], item)) { at = j; break; }
            if (at < 0) target.Insert(i, item);
            else if (at != i) target.Move(at, i);
        }
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
        await LoadAsync(force: true);
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
            (kind, loaderVersion, api, folder, instanceName) =>
                _ = InstallAsync(v, kind, loaderVersion, api, folder, instanceName));
        _host.OpenSubPage(typeof(DownloadDetailPage), args, "获取", "下载版本", v.Id);
    }

    /// <summary>任务标题就用即将装出来的那个实例名（原版＝版本号，带加载器则把加载器和版本拼进去）。</summary>
    private static string DefaultInstanceName(string versionId, string kind, string loaderVersion)
    {
        var ver = (loaderVersion ?? "").Trim();
        if (kind == "vanilla" || ver.Length == 0) return versionId;
        return kind switch
        {
            "fabric" => $"{versionId}-Fabric_{ver}",
            "quilt" => $"{versionId}-Quilt_{ver}",
            "forge" => $"{versionId}-forge-{ver}",
            "neoforge" => $"{versionId}-neoforge-{ver}",
            _ => versionId,
        };
    }

    private static string InstallTaskDetail(string kind) =>
        kind == "vanilla" ? "原版" : $"{LoaderDisplayName(kind)} 加载器";

    private static string LoaderDisplayName(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => "原版",
    };

    private async Task InstallAsync(MojangVersionInfo v, string kind, string loaderVersion,
        DownloadApiSelection? api, string folder, string instanceName)
    {
        if (Directory.Exists(Path.Combine(folder, "versions", instanceName))) return;

        var task = TaskCenter.Add(
            string.IsNullOrWhiteSpace(instanceName) ? DefaultInstanceName(v.Id, kind, loaderVersion) : instanceName,
            InstallTaskDetail(kind), "download", targetIcon: InstanceIcon.ForLoader(kind));
        var progress = new Progress<string>(line =>
        {
            if (!task.IsActive) return;
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
                var f = raw[6..].Split('|', 4);
                if (f.Length >= 4 && int.TryParse(f[0], out var idx) && int.TryParse(f[1], out var state)
                    && TryReadProgress(f[2], out var spct))
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
                var f = raw[6..].Split('|', 4);
                if (f.Length >= 4 && TryReadProgress(f[0], out var overall))
                {
                    var speed = TryReadProgress(f[1], out var sp) ? sp : 0;
                    var remain = int.TryParse(f[2], out var rm) ? rm : 0;
                    TaskCenter.Stats(task, speed, remain > 0 ? $"{remain:N0} 个文件" : "");
                    TaskCenter.Progress(task, overall, task.Detail);
                    // 当前文件放任务卡右侧那行，灵动岛只留百分比
                    task.SetLive(System.IO.Path.GetFileName(f[3].Replace('\\', '/')));
                }
                return;
            }
            var parts = raw.Split('|', 2);
            if (parts.Length == 2 && TryReadProgress(parts[0], out var pct) && pct > 0)
            {
                TaskCenter.ResetDownloadStats(task);
                TaskCenter.Progress(task, pct, parts[1]);
            }
            else
            {
                TaskCenter.Report(task, parts.Length == 2 ? parts[1] : raw);
            }
        });
        BackendResult result;
        try
        {
            result = await App.Backend.InstallVersionAsync(v.Id, kind, loaderVersion, progress, api is not null,
                instanceName, api?.VersionId ?? "", folder, api?.ProjectId ?? "", task.Id);
        }
        catch (Exception ex)
        {
            TaskCenter.Fail(task, ex.Message);
            return;
        }
        if (result.Ok)
        {
            TaskCenter.Complete(task, result.Message ?? "安装完成");
            await SyncInstalledAsync();
        }
        else TaskCenter.Fail(task, result.Message, result.Failure);
    }

    private static bool TryReadProgress(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

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

    private static void ShowStatus(InfoBarSeverity severity, string title, string message) =>
        TaskCenter.PostFeedback(severity, title, message);
}
