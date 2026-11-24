using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI.Views;

public sealed partial class OnlinePage : Page, IPageSearchFilter
{
    private readonly ObservableCollection<OnlineItem> _items = new();
    private readonly List<ModrinthProject> _results = new();
    // 第 5 项筛选状态：安装目标实例、游戏版本/加载器覆盖、排序、分页偏移。
    private string _installTo = "";
    private string _gameVersion = "";
    private string _loader = "";
    private string _sort = "relevance";
    private int _offset;
    private bool _loadingMore;
    private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private string _category = "all";
    private int _requestId;
    private bool _ready;
    private ISubPageHost? _host;
    /// <summary>最近一次搜索词（页内搜索框已并入顶栏全局搜索，输入不再来自本页）。</summary>
    private string _query = "";

    public OnlinePage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;
        // ScrollViewer 会把 PointerMoved 标成已处理，这里用 handledEventsToo 订阅一次，保证行悬停生效。
        ResultsList.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(ResultsList_PointerMoved), handledEventsToo: true);
        ResultsList.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler(ResultsList_PointerExited), handledEventsToo: true);
        // 列表换内容时容器会被回收复用，旧行的悬停底色会跟着跑到新行上，这里清一次。
        ResultsList.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer is ListViewItem item) item.Background = null;
        };
        // 数据源由「获取」顶部的「在线资源」按钮切换，这里沿用上次选的源。
        SourceBox.SelectedIndex = PreferredSource is 0 or 1 ? PreferredSource : 0;
        if (PreferredCfSource is 1 or 2) CfSourceBox.SelectedIndex = PreferredCfSource;
        UpdateSourceCategories();
        Loaded += (_, _) =>
        {
            if (_hooked) return;
            _hooked = true;
            InstanceSelection.Changed += InstanceSelection_Changed;
        };
        Unloaded += (_, _) =>
        {
            DetachQueryBox();
            if (!_hooked) return;
            _hooked = false;
            InstanceSelection.Changed -= InstanceSelection_Changed;
        };
    }

    private bool _hooked;

    // 嵌在「获取」里时，搜索框在宿主顶栏（在线资源 / 来源切换的右边）；独立打开时用本页自己的搜索卡。
    private MeowLauncher.WinUI.Controls.PageSearchBox? _externalQuery;
    private MeowLauncher.WinUI.Controls.PageSearchBox ActiveQueryBox => _externalQuery ?? QueryBox;

    public void AttachQueryBox(MeowLauncher.WinUI.Controls.PageSearchBox box)
    {
        DetachQueryBox();
        _externalQuery = box;
        box.QueryChanged += QueryBox_QueryChanged;
        box.QuerySubmitted += QueryBox_QuerySubmitted;
        if (box.Text.Trim() != _query) box.Text = _query;
        SearchCard.Visibility = Visibility.Collapsed;
        PageRoot.RowSpacing = 0;   // 页头和搜索卡都收起了，不留空行间距
    }

    private void DetachQueryBox()
    {
        if (_externalQuery is null) return;
        _externalQuery.QueryChanged -= QueryBox_QueryChanged;
        _externalQuery.QuerySubmitted -= QueryBox_QuerySubmitted;
        _externalQuery = null;
    }


    /// <summary>实例切换器切了当前实例：「跟随实例」的真实值、安装目标、结果都跟着变。</summary>
    private void InstanceSelection_Changed(VersionInfo target) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_ready) return;
        _installTo = target.Id;
        RefreshFollowLabels();
        _offset = 0;
        await SearchAsync(_query);
    });

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (PreferredSource < 0)
        {
            var settings = await App.Backend.GetGeneralSettingsAsync();
            if (settings.Ok && PreferredSource < 0)
                PreferredSource = ResourceSourceIndex(settings.DownloadSource);
        }
        SourceBox.SelectedIndex = PreferredSource == 1 ? 1 : 0;
        await PopulateFiltersAsync();
        _ready = true;
        string query = "";
        if (e.Parameter is TabNav nav)
        {
            query = nav.Query;
            _host = nav.Host;
            if (_host is GetPage page) page.RefreshSourceSelection();
            PageHeader.Visibility = Visibility.Collapsed;
            PageRoot.RowDefinitions[0].Height = new GridLength(0);
            PageRoot.Padding = new Thickness(0);   // 宿主「获取」已有页边距，不再叠一层
        }
        else if (e.Parameter is string text)
        {
            query = text;
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            await SearchAsync(query);
        }
        else
        {
            _sort = SourceBox.SelectedIndex == 1 ? "relevance" : "downloads";
            await SearchAsync("");
        }
    }

    /// <summary>把实例/游戏版本/加载器塞进筛选条；数据来自主窗口快照，不额外发请求。</summary>
    private async Task PopulateFiltersAsync()
    {
        // 页面可能比快照先到：这里主动要一次，别让「安装到」默认成列表里的第一个实例。
        var snapshot = App.MainWindow?.Snapshot
                       ?? await App.Backend.GetSnapshotAsync(false);
        var versions = snapshot?.Versions ?? new List<VersionInfo>();

        var current = snapshot?.Current?.Id ?? snapshot?.Selected ?? "";
        _installTo = string.IsNullOrWhiteSpace(current) ? "" : current;

        GameVersionBox.Items.Clear();
        GameVersionBox.Items.Add(new ComboBoxItem { Content = "跟随实例", Tag = "" });
        // 先用本地实例的版本垫底（只收正式版号、按版本号从新到旧排），再异步换成 Mojang 的完整正式版列表。
        FillGameVersions(versions.Select(v => v.GameVersion));
        _ = LoadReleaseVersionsAsync();
        GameVersionBox.SelectedIndex = 0;

        LoaderBox.Items.Clear();
        LoaderBox.Items.Add(new ComboBoxItem { Content = "跟随实例", Tag = "" });
        foreach (var (label, tag) in new[]
                 {
                     ("Fabric", "fabric"), ("Forge", "forge"), ("NeoForge", "neoforge"), ("Quilt", "quilt"),
                 })
            LoaderBox.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        LoaderBox.SelectedIndex = 0;
        UpdateLoaderVisibility();
        RefreshFollowLabels();

        SortBox.SelectedIndex = 0;
    }

    // 正式版号：1.20.1 / 1.21.11 / 26.1 / 26.3.1。快照、预发布、带加载器后缀的实例版本（如 26.3-Fabric_0.20.1）都不算。
    private static readonly System.Text.RegularExpressions.Regex ReleaseId =
        new(@"^\d+(\.\d+){1,3}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>按版本号数字逐段比较，从新到旧（26.3 > 26.1.1 > 26.1 > 1.21.11 > 1.21.2）。</summary>
    private static int CompareVersionDesc(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length && int.TryParse(x[i], out var p) ? p : -1;
            var yi = i < y.Length && int.TryParse(y[i], out var q) ? q : -1;
            if (xi != yi) return yi.CompareTo(xi);
        }
        return 0;
    }

    /// <summary>重建游戏版本下拉（第一项「跟随 / 不限」保留），尽量保住当前选中的版本，不触发搜索。</summary>
    private void FillGameVersions(IEnumerable<string> source)
    {
        var list = source.Select(v => (v ?? "").Trim())
            .Where(v => ReleaseId.IsMatch(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Sort(CompareVersionDesc);
        var keep = (GameVersionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _relabeling = true;
        try
        {
            while (GameVersionBox.Items.Count > 1) GameVersionBox.Items.RemoveAt(1);
            if (GameVersionBox.Items.Count == 0) GameVersionBox.Items.Add(new ComboBoxItem { Content = "跟随实例", Tag = "" });
            foreach (var gv in list) GameVersionBox.Items.Add(new ComboBoxItem { Content = gv, Tag = gv });
            var index = keep.Length == 0 ? 0 : list.FindIndex(v => v.Equals(keep, StringComparison.OrdinalIgnoreCase)) + 1;
            GameVersionBox.SelectedIndex = Math.Max(0, index);
        }
        finally
        {
            _relabeling = false;
        }
    }

    /// <summary>Mojang 正式版列表（后端有缓存）；拿不到就保留本地实例版本，不报错。</summary>
    private async Task LoadReleaseVersionsAsync()
    {
        try
        {
            var result = await App.Backend.GetMojangVersionsAsync();
            if (result is not { Ok: true } || result.Versions.Count == 0) return;
            var releases = result.Versions
                .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Id);
            DispatcherQueue.TryEnqueue(() =>
            {
                FillGameVersions(releases);
                RefreshFollowLabels();
            });
        }
        catch
        {
            // 离线时只用本地实例的版本。
        }
    }
    private VersionInfo? CurrentInstance => App.MainWindow?.Snapshot?.Current;

    /// <summary>实例的加载器小写标识：fabric / forge / neoforge / quilt；原版或认不出来返回空。</summary>
    private static string LoaderTagOf(VersionInfo? v)
    {
        var raw = (v?.Loader ?? "").Trim().ToLowerInvariant();
        foreach (var tag in new[] { "neoforge", "fabric", "forge", "quilt" })
            if (raw.Contains(tag)) return tag;
        return "";
    }

    private static string LoaderName(string tag) => tag switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => tag,
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// 「跟随实例」真正生效：游戏版本取当前实例的版本；加载器只对 Mod/整合包取实例的加载器。
    /// 以前跟随 = 不传任何条件，等于没有筛选，原版实例也会搜出 Fabric 的 Mod。
    /// </summary>
#if DEBUG
    /// <summary>仅调试：dmod|词|实例id 时忽略实例的游戏版本，免得截图被筛选条件滤空。</summary>
    internal static bool DebugNoFilter;
#endif

    private string EffectiveGameVersion() =>
#if DEBUG
        DebugNoFilter ? "" :
#endif
        !string.IsNullOrWhiteSpace(_gameVersion) ? _gameVersion
        // 整合包装成新实例，不跟随当前实例：没手选就不限版本。
        : _category == "modpack" ? "" : InstanceGameVersion(CurrentInstance);

    /// <summary>实例的 MC 版本，只取开头的正式版号（「26.3-Fabric_0.20.1」→「26.3」）；认不出就不限版本，免得搜索一条都搜不到。</summary>
    private static string InstanceGameVersion(VersionInfo? v)
    {
        var raw = (v?.GameVersion ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\d+(\.\d+){1,3}");
        return m.Success ? m.Value : "";
    }

    private string EffectiveLoader()
    {
        if (!string.IsNullOrWhiteSpace(_loader)) return _loader;
        // 只有 Mod 跟随实例的加载器；整合包会自己带加载器建新实例，没手选就不限。
        return _category == "mod" ? LoaderTagOf(CurrentInstance) : "";
    }

    /// <summary>「跟随实例」后面补上解析出的真实值，别让用户猜它指什么。</summary>
    private void RefreshFollowLabels()
    {
        var cur = CurrentInstance;
        if (GameVersionBox.Items.Count > 0 && GameVersionBox.Items[0] is ComboBoxItem gv)
            gv.Content = _category == "modpack" ? "不限版本" : cur is null || InstanceGameVersion(cur).Length == 0 ? "跟随实例" : $"跟随 {InstanceGameVersion(cur)}";
        if (LoaderBox.Items.Count > 0 && LoaderBox.Items[0] is ComboBoxItem lb)
        {
            var tag = LoaderTagOf(cur);
            var raw = (cur?.Loader ?? "").Trim();
            var name = cur is null ? ""
                : tag.Length > 0 ? LoaderName(tag)
                : raw.Length == 0 || raw.Equals("vanilla", StringComparison.OrdinalIgnoreCase) ? "原版"
                : "未识别";
            lb.Content = _category == "modpack" ? "不限加载器" : name.Length == 0 ? "跟随实例" : $"跟随 {name}";
        }
        // 选中项的文字改了，下拉框上显示的那份不会自己刷新：选中第一项时重选一次（不触发搜索）。
        Reselect(GameVersionBox);
        Reselect(LoaderBox);
        UpdateFilterHint();
    }

    private bool _relabeling;

    private void Reselect(ComboBox box)
    {
        if (box.SelectedIndex != 0) return;
        _relabeling = true;
        try { box.SelectedIndex = -1; box.SelectedIndex = 0; }
        finally { _relabeling = false; }
    }

    private void UpdateFilterHint()
    {
        var cur = CurrentInstance;
        var vanilla = cur is not null && LoaderTagOf(cur).Length == 0;
        FilterHint.Text = vanilla && _category == "mod" && string.IsNullOrWhiteSpace(_loader)
            ? $"{cur!.Id} 是原版或未识别加载器：结果不限加载器，装 Mod 前要先给它装加载器"
            : "";
        FilterHint.Visibility = FilterHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>筛选/排序变化：偏移归零后重新搜（不做按键级联，只有下拉变化才触发）。</summary>
    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _relabeling) return;
        _gameVersion = (GameVersionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _loader = (LoaderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _offset = 0;
        await SearchAsync(_query);
    }

    private async void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance";
        _offset = 0;
        await SearchAsync(_query);
    }

    /// <summary>每页条数：整合包更重，一页少放些。</summary>
    private int PageSize => _category == "all" ? 20 : 60;

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingMore || _offset < PageSize) return;
        _loadingMore = true;
        PrevPageButton.IsEnabled = false;
        try
        {
            _offset -= PageSize;
            await SearchAsync(_query);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingMore) return;
        _loadingMore = true;
        NextPageButton.IsEnabled = false;
        try
        {
            _offset += PageSize;
            await SearchAsync(_query);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void Home_Tapped(object sender, TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _typeTimer;

    private void QueryBox_QueryChanged(object? sender, string text)
    {
        // 联网搜索：停手 450 ms 再发，不要每敲一个字就打一次网络。
        _typeTimer ??= DispatcherQueue.CreateTimer();
        _typeTimer.Stop();
        _typeTimer.Interval = TimeSpan.FromMilliseconds(450);
        _typeTimer.IsRepeating = false;
        _typeTimer.Tick -= TypeTimer_Tick;
        _typeTimer.Tick += TypeTimer_Tick;
        _typeTimer.Start();
    }

    private async void TypeTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await RunQueryAsync(force: false);
    }

    private async void QueryBox_QuerySubmitted(object? sender, string text)
    {
        _typeTimer?.Stop();
        await RunQueryAsync(force: true);
    }

    private async Task RunQueryAsync(bool force)
    {
        if (!_ready) return;
        var q = ActiveQueryBox.Text.Trim();
        if (!force && q == _query) return;
        _offset = 0;
        await SearchAsync(q);
    }

    // 顶栏 Ctrl+K 在本页时聚焦搜索框；程序设置关键词时直接搜。
    public bool TryFocusSearch() => ActiveQueryBox.FocusInput();

    public void ApplySearch(string query)
    {
        _typeTimer?.Stop();
        _offset = 0;
        _ = SearchAsync(query ?? string.Empty);
    }

    /// <summary>当前数据源（0 = Modrinth，1 = CurseForge）。切换入口在「获取」顶部的下载源按钮，切走再回来保持不变。</summary>
    public static int PreferredSource { get; set; } = -1;

    /// <summary>CurseForge 来源（0 MCIM 社区镜像 / 1 MeowAPI / 2 官方），同样由「获取」页的下载源菜单写入。</summary>
    public static int PreferredCfSource { get; set; }

    internal static int ResourceSourceIndex(string source) =>
        source is "priority:curseforge,modrinth" or "only:curseforge" ? 1 : 0;

    /// <summary>由「获取」页的来源切换调用：只改隐藏的 SourceBox，搜索走它的 SelectionChanged。</summary>
    public void SetSource(int index)
    {
        if (index is not (0 or 1) || SourceBox.SelectedIndex == index) return;
        SourceBox.SelectedIndex = index;
    }

    private bool _cfKeyPresent;
    private int _lastCfSource;
    private bool _cfReverting;

    /// <summary>读一次设置里的 CurseForge Key，决定「官方」这一项是否置灰。</summary>
    private async Task RefreshCfKeyAsync()
    {
        try
        {
            var g = await App.Backend.GetGeneralSettingsAsync();
            _cfKeyPresent = !string.IsNullOrWhiteSpace(g.CfApiKey);
        }
        catch { }
        if (CfOfficialItem is not null) CfOfficialItem.Opacity = _cfKeyPresent ? 1 : 0.45;
    }

    private async void CfSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CfSourceBox is null) return;
        if (_cfReverting) { _cfReverting = false; return; }

        if (CfSourceBox.SelectedIndex == 2 && !_cfKeyPresent)
        {
            await RefreshCfKeyAsync();
            if (!_cfKeyPresent)
            {
                await new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "需要 CurseForge API Key",
                    Content = "请前往「设置 › 下载与网络」填写 CurseForge API Key，再选官方源。",
                    CloseButtonText = "知道了",
                }.ShowAsync();
                _cfReverting = true;
                CfSourceBox.SelectedIndex = _lastCfSource is 0 or 1 ? _lastCfSource : 0;
                return;
            }
        }

        _lastCfSource = CfSourceBox.SelectedIndex;
        if (!_ready || SourceBox.SelectedIndex < 1) return;
        _offset = 0;
        await SearchAsync(_query);
    }

    private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 初始化 SourceBox 时，分类按钮可能尚未创建。
        if (CatModpack is null) return;
        UpdateSourceCategories();
        if (!_ready) return;
        UpdateLoaderVisibility();
        RefreshFollowLabels();
        _offset = 0;
        await SearchAsync(_query);
    }

    /// <summary>CurseForge 来源：0 MCIM 社区镜像；1 MeowAPI 自建代理；2 官方直连（需要设置里填 Key）。</summary>
    private static string CfSourceFor(int index) => index switch
    {
        1 => "cfmi",
        2 => "official",
        _ => "mcim",
    };

    private static string CfSourceLabel(int index) => index switch
    {
        1 => "MeowAPI",
        2 => "CurseForge 官方",
        _ => "MCIM",
    };

    private void UpdateSourceCategories()
    {
        var isCurseForge = SourceBox.SelectedIndex >= 1;
        // 来源改由「获取」顶部的 CurseForge 按钮弹菜单选，这个下拉只留作代码载体。
        CfSourceBox.Visibility = Visibility.Collapsed;
        if (isCurseForge) _ = RefreshCfKeyAsync();
        CatAll.Visibility = isCurseForge ? Visibility.Collapsed : Visibility.Visible;
        if (isCurseForge && _category == "all") _category = "mod";
        UpdateCategoryVisuals();
    }

    private async Task SearchAsync(string raw, bool append = false)
    {
        var query = (raw ?? "").Trim();
        _query = query;
        if (ActiveQueryBox.Text.Trim() != query) ActiveQueryBox.Text = query;   // 同步外部带进来的关键词；不触发再次搜索
        var useCurseForge = SourceBox.SelectedIndex >= 1;
        var cfSource = CfSourceFor(CfSourceBox.SelectedIndex);
        var sourceName = useCurseForge ? CfSourceLabel(CfSourceBox.SelectedIndex) : "Modrinth";

        // 两家现在都支持游戏版本/加载器/排序；CurseForge 没有"相关度"，回落成热门。
        UpdateFilterHint();

        var request = ++_requestId;
        SetSearching(true, sourceName);
        ResultCount.Text = $"正在搜索 {sourceName}…";

        try
        {
            // 分类已经交给服务端：Modrinth 用 facets、CurseForge 用 classId，本地只做兜底过滤。
            var limit = PageSize;
            var searchTask = useCurseForge
                ? CurseForgeClient.SearchAsync(query, limit, _category,
                    NullIfEmpty(EffectiveGameVersion()), NullIfEmpty(EffectiveLoader()), _sort, _offset, cfSource)
                : ModrinthClient.SearchAsync(query, limit, _category,
                    NullIfEmpty(EffectiveGameVersion()), NullIfEmpty(EffectiveLoader()), _sort, _offset);
            var done = await Task.WhenAny(searchTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (done != searchTask) throw new TimeoutException("请求超过 15 秒未返回");
            var results = await searchTask;
            if (request != _requestId) return;

            if (!append) _results.Clear();
            foreach (var r in results)
            {
                if (_results.All(x => x.Id != r.Id)) _results.Add(r);
            }
            await RefreshInstalledAsync();
            if (request != _requestId) return;
            ApplyResults(sourceName, query);
            PageLabel.Text = $"第 {_offset / limit + 1} 页";
            PrevPageButton.IsEnabled = _offset > 0;
            PrevPageButton.Visibility = _offset > 0 ? Visibility.Visible : Visibility.Collapsed;
            NextPageButton.IsEnabled = results.Count >= limit;
            NextPageButton.Visibility = results.Count >= limit ? Visibility.Visible : Visibility.Collapsed;
            PageBar.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (request != _requestId) return;
            _results.Clear();
            _items.Clear();
            if (ex is NotSupportedException)
            {
                ResultCount.Text = sourceName;
                ShowEmpty("\uE946", "这个来源需要先选分类", ex.Message + " 相关度排序仍然可用。", retry: false);
                CategoryShortcuts.Visibility = Visibility.Visible;
                return;
            }
            ResultCount.Text = $"{sourceName} · 离线";
            ShowEmpty("\uE783", $"{sourceName} 连接失败",
                $"检查网络或代理后再试。{ClipText(ex.Message, 120)}",
                retry: true);
        }
        finally
        {
            if (request == _requestId) SetSearching(false, sourceName);
        }
    }

    private void ApplyResults(string sourceName, string query)
    {
        // 「全部」只放装进实例的资源；整合包在下面单独的框里看。
        var filtered = _category == "all"
            ? _results.Where(p => !p.IsModpack).ToList()
            : _results.Where(p => string.Equals(p.ProjectType, _category, StringComparison.OrdinalIgnoreCase)).ToList();

        _hoverItem = null;
        _items.Clear();
        foreach (var project in filtered)
            _items.Add(new OnlineItem(project, IsInstalled(project)));

        ResultCount.Text = $"{sourceName} · {_items.Count} 项";
        if (_items.Count == 0)
        {
            ShowEmpty("\uE774", "没有找到结果",
                _category == "all"
                    ? $"「{query}」在 {sourceName} 没有匹配项，换个关键词或切换来源再试。"
                    : $"「{query}」里没有{CategoryName(_category)}，换个关键词再试。",
                retry: false);
            return;
        }

        EmptyHost.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        var listWasHidden = ResultsList.Visibility != Visibility.Visible;
        ResultsList.Visibility = Visibility.Visible;
        // 新一轮搜索（不是「加载更多」追加）出结果时，列表淡入上浮一下。
        if (_offset == 0 || listWasHidden) MeowLauncher.WinUI.Controls.Motion.Enter(ResultsList, 0, 8, 200);
    }

    /// <summary>已装判断：当前实例的 Mod 列表里是否已有同一 project id / 文件名含 slug。</summary>
    private async Task RefreshInstalledAsync()
    {
        _installed.Clear();
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync(true);
            foreach (var pack in snapshot?.Versions ?? [])
                if (pack.PackProject.Length > 0) _installed.Add($"pack:{pack.PackSource}:{pack.PackProject}");
            var versionId = string.IsNullOrWhiteSpace(_installTo)
                ? (snapshot?.Current?.Id ?? snapshot?.Selected ?? "")
                : _installTo;
            if (string.IsNullOrWhiteSpace(versionId)) return;
            var mods = await App.Backend.GetModsAsync(versionId);
            if (mods is null) return;
            foreach (var mod in mods.Mods)
            {
                if (!string.IsNullOrWhiteSpace(mod.ModId)) _installed.Add(mod.ModId);
                if (!string.IsNullOrWhiteSpace(mod.File)) _installed.Add(mod.File);
            }
        }
        catch
        {
            // 本地列表读不到时按「未安装」处理，不影响搜索。
        }
    }

    private bool IsInstalled(ModrinthProject project)
    {
        if (project.IsModpack) return _installed.Contains($"pack:{(SourceBox.SelectedIndex >= 1 ? "curseforge" : "modrinth")}:{project.Id}");
        if (_installed.Count == 0) return false;
        if (!string.IsNullOrWhiteSpace(project.Id) && _installed.Contains(project.Id)) return true;
        if (string.IsNullOrWhiteSpace(project.Slug)) return false;
        foreach (var installed in _installed)
            if (installed.Contains(project.Slug, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void MarkInstalled(ModrinthProject project)
    {
        if (project.IsModpack) _installed.Add($"pack:{(SourceBox.SelectedIndex >= 1 ? "curseforge" : "modrinth")}:{project.Id}");
        if (!project.IsModpack && !string.IsNullOrWhiteSpace(project.Id)) _installed.Add(project.Id);
        ApplyResults(SourceBox.SelectedIndex >= 1 ? "CurseForge" : "Modrinth", _query);
    }

    private bool _typeSyncing;

    /// <summary>顶部「类型」下拉：和左栏分类是同一个状态，互相点亮。</summary>
    private async void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_typeSyncing || TypeBox?.SelectedItem is not ComboBoxItem { Tag: string category }) return;
        // CurseForge 没有「全部类型」，落到 Mod。
        if (category == "all" && SourceBox.SelectedIndex >= 1) category = "mod";
        if (category == _category) { SyncTypeBox(); return; }
        await SwitchCategoryAsync(category);
    }

    private void SyncTypeBox()
    {
        if (TypeBox is null) return;
        var index = _category switch
        {
            "mod" => 1,
            "modpack" => 2,
            "resourcepack" => 3,
            "shader" => 4,
            "datapack" => 5,
            _ => 0,
        };
        if (TypeBox.SelectedIndex == index) return;
        _typeSyncing = true;
        TypeBox.SelectedIndex = index;
        _typeSyncing = false;
    }

    /// <summary>由「获取」页的来源菜单调用：0 MCIM 社区镜像 / 1 MeowAPI / 2 官方。</summary>
    public void SetCfSource(int index)
    {
        if (index is not (0 or 1 or 2) || CfSourceBox is null) return;
        PreferredCfSource = index;
        if (CfSourceBox.SelectedIndex == index) return;
        CfSourceBox.SelectedIndex = index;
    }

    public int CfSourceIndex => CfSourceBox?.SelectedIndex ?? 0;

    private async void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        // Modrinth 可以再点整合包回到「全部」，CurseForge 始终保留具体分类。
        if (category == _category)
        {
            if (category != "modpack" || SourceBox.SelectedIndex >= 1) return;
            category = "all";
        }
        await SwitchCategoryAsync(category);
    }

    private async Task SwitchCategoryAsync(string category)
    {
        _category = category;
        _offset = 0;   // 换分类从第一页开始，不沿用上一个分类「加载更多」之后的偏移
        UpdateCategoryVisuals();
        UpdateLoaderVisibility();
        RefreshFollowLabels();
        await SearchAsync(_query);
    }
    private void UpdateCategoryVisuals()
    {
        foreach (var button in new[] { CatAll, CatMod, CatModpack, CatResourcepack, CatShader, CatDatapack })
        {
            var active = button.Tag as string == _category;
            // 选中 = 品牌绿浅底 + 左侧绿条（固定品牌色，不跟主题强调色走）
            button.Background = active
                ? PagePalette.ThemeBrush("AppBrandSoftBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderBrush = active
                ? PagePalette.ThemeBrush("AppBrandBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = active
                ? PagePalette.ThemeBrush("AppTextBrush")
                : PagePalette.ThemeBrush("AppText2Brush");
            button.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
        SyncTypeBox();
    }

    private async void CategoryShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        await SwitchCategoryAsync(category);
    }


    /// <summary>资源包/光影/数据包没有 forge/fabric 这类分类，加载器下拉直接隐藏（也不再拼 facet）。</summary>
    private void UpdateLoaderVisibility()
    {
        var supported = _category is "mod" or "modpack" or "all";
        LoaderGroup.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported)
        {
            _loader = "";
            if (LoaderBox.SelectedIndex != 0) LoaderBox.SelectedIndex = 0;
        }
    }

    private static string CategoryName(string category) => category switch
    {
        "mod" => "Mod",
        "modpack" => "整合包",
        "resourcepack" => "资源包",
        "shader" => "光影",
        "datapack" => "数据包",
        _ => "结果",
    };

    private void SetSearching(bool searching, string sourceName)
    {
        LoadingPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Text = $"正在搜索 {sourceName}…";
        if (!searching) return;
        EmptyHost.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string glyph, string title, string hint, bool retry)
    {
        CategoryShortcuts.Visibility = Visibility.Collapsed;
        PageBar.Visibility = Visibility.Collapsed;
        EmptyPanel.Glyph = glyph;
        EmptyPanel.Title = title;
        EmptyPanel.Hint = hint;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        EmptyHost.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
    }

    private async void EmptyRetry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            await SearchAsync(_query);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private async void OpenProject_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var url = sender switch
        {
            Button { Tag: ModrinthProject project } => project.PageUrl,
            Button { Tag: string text } => text,
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(url)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { /* 打不开浏览器时不打断页面 */ }
    }

    private async void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        var row = _items.FirstOrDefault(item => item.Id == id);
        var project = row?.Project;
        if (row is null || project is null) return;

        // 不支持的资源类型没有「打开项目页」按钮了，点安装先把详情铺出来，
        // 详细原因和官网入口都在右侧，不会让人点了没反应。
        if (!project.IsInstallable)
        {
            await OpenDetailAsync(project);
            return;
        }

        var source = SourceBox.SelectedIndex >= 1 ? "curseforge" : "modrinth";
        var sourceName = source == "curseforge" ? "CurseForge" : "Modrinth";
        if (project.IsModpack)
        {
            await InstallModpackAsync(project, source, sourceName, button);
            return;
        }
        var snapshot = await App.Backend.GetSnapshotAsync(true);
        var versionId = string.IsNullOrWhiteSpace(_installTo)
                ? (snapshot?.Current?.Id ?? snapshot?.Selected ?? "")
                : _installTo;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            ResultCount.Text = "没有选择实例，先在版本管理里选择要安装到的实例。";
            return;
        }

        var current = snapshot?.Current ?? snapshot?.Versions.FirstOrDefault(v => v.Id == versionId);
        if (!project.IsLoaderCompatibleWith(current?.Loader ?? ""))
        {
            await OpenDetailAsync(project);
            return;
        }

        button.IsEnabled = false;
        TaskEntry? task = null;
        try
        {
            OnlineModInstallPlan? plan = null;
            if (project.InstallKind == "mod")
            {
                var previousStatus = ResultCount.Text;
                var checking = $"正在检查 {project.Title} 的必需前置…";
                ResultCount.Text = checking;
                plan = await App.Backend.ConfirmOnlineModInstallAsync(source, project.Id, "", versionId, XamlRoot);
                if (plan is null)
                {
                    if (ResultCount.Text == checking) ResultCount.Text = previousStatus;
                    return;
                }
            }
            task = TaskCenter.Add($"安装 {project.Title}",
                $"{sourceName} · {project.TypeText} · {versionId}", PageTagFor(project), "\uE7B8", InstanceIcon.ForProject(project, current));
            ResultCount.Text = $"正在安装 {project.Title}…";
            var result = plan is not null
                ? await App.Backend.InstallOnlineFileAsync(source, project.Id, plan.FileId, versionId, project.InstallKind,
                    plan.Confirmation, taskId: task.Id, skipDependencies: plan.SkipDependencies)
                : await App.Backend.InstallOnlineAsync(source, project.Id, versionId, project.InstallKind, taskId: task.Id);
            if (result.Ok)
            {
                TaskCenter.Complete(task, result.Message);
                ResultCount.Text = $"{sourceName} · 已安装 {project.Title}";
                MarkInstalled(project);
            }
            else
            {
                TaskCenter.Fail(task, result.Message, result.Failure);
                ResultCount.Text = $"安装失败：{result.Message}";
            }
        }
        catch (Exception ex)
        {
            if (task is not null) TaskCenter.Fail(task, ex.Message);
            ResultCount.Text = $"安装失败：{ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>整合包：装成一个新实例。不需要先选实例，也不看当前实例的游戏版本 / 加载器。</summary>
    private async Task InstallModpackAsync(ModrinthProject project, string source, string sourceName, Button button)
    {
        button.IsEnabled = false;
        var task = TaskCenter.Add($"安装整合包 {project.Title}", $"{sourceName} · 新实例", PageTagFor(project),
            "\uE8F1", project.IconSource ?? InstanceIcon.ForLoader(null));
        task.ResourceKey = $"pack:{source}:{project.Id}";
        ResultCount.Text = $"正在安装整合包 {project.Title}…";
        try
        {
            var progress = new Progress<string>(stage => TaskCenter.Report(task, stage));
            var result = await App.Backend.InstallOnlineAsync(source, project.Id, "", "modpack", progress, task.Id);
            if (result.Ok)
            {
                task.TargetIcon = InstanceIcon.ForInstalledPack(result);
                TaskCenter.Complete(task, result.Message);
                MarkInstalled(project);
                ResultCount.Text = $"{sourceName} · {result.Message}";
            }
            else
            {
                TaskCenter.Fail(task, result.Message, result.Failure);
                ResultCount.Text = $"安装失败：{result.Message}";
            }
        }
        catch (Exception ex)
        {
            TaskCenter.Fail(task, ex.Message);
            ResultCount.Text = $"安装失败：{ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
    private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OnlineItem item) await OpenDetailAsync(item.Project);
    }

    private ListViewItem? _hoverItem;

    /// <summary>行悬停：ListView 层统一处理，避免 DataTemplate 里的 PointerEntered 在虚拟化容器上不可靠。</summary>
    private void ResultsList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(ResultsList).Position.Y;
        ListViewItem? hit = null;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ResultsList.ContainerFromIndex(i) is not ListViewItem container) continue;
            var top = container.TransformToVisual(ResultsList)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y >= top && y <= top + container.ActualHeight)
            {
                hit = container;
                break;
            }
        }
        SetHoverItem(hit);
    }

    private void ResultsList_PointerExited(object sender, PointerRoutedEventArgs e) => SetHoverItem(null);

    private void SetHoverItem(ListViewItem? item)
    {
        if (ReferenceEquals(item, _hoverItem)) return;
        if (item is null)
        {
            // 指针不在任何行上：把可能残留在回收容器上的悬停底色一并清掉。
            for (var i = 0; i < _items.Count; i++)
                if (ResultsList.ContainerFromIndex(i) is ListViewItem stale) stale.Background = null;
        }
        if (_hoverItem is not null) _hoverItem.Background = null;
        _hoverItem = item;
        if (_hoverItem is not null && Resources.TryGetValue("RowHoverBrush", out var value) && value is Brush brush)
            _hoverItem.Background = brush;
    }

    /// <summary>
    /// 打开「获取 › 在线资源 › 项目名」子页。列表页一直活着，返回后滚动位置、分类、数据源、筛选都还在。
    /// </summary>
#if DEBUG
    /// <summary>截图用：等搜索结果出来，直接打开第一行的详情子页。</summary>
    public async Task DebugOpenFirst(string instance = "")
    {
        if (!string.IsNullOrWhiteSpace(instance)) _installTo = instance;
        for (var i = 0; i < 200 && _items.Count == 0; i++) await Task.Delay(100);
        App.Trace("DebugOpenFirst items=" + _items.Count);
        if (_items.Count > 0) await OpenDetailAsync(_items[0].Project);
    }
#endif

    /// <summary>从别处（比如实例 Mod 行按哈希认出项目）直接打开某个项目的详情子页。</summary>
    public async Task OpenProjectAsync(ModrinthProject project, string instance)
    {
        for (var i = 0; i < 50 && _host is null; i++) await Task.Delay(100);
        if (!string.IsNullOrWhiteSpace(instance)) _installTo = instance;
        await OpenDetailAsync(project);
    }

    private async Task OpenDetailAsync(ModrinthProject project)
    {
        if (_host is null)
        {
            ResultCount.Text = "当前入口不支持打开详情子页。";
            return;
        }
        var source = SourceBox.SelectedIndex >= 1 ? "curseforge" : "modrinth";
        var sourceName = source == "curseforge" ? "CurseForge" : "Modrinth";
        App.Trace("OpenDetail: begin " + project.Title);
        // 只读缓存快照：详情页要的只是「装到哪个实例」，不必为此再跑一次后端。
        var snapshot = App.MainWindow?.Snapshot ?? await App.Backend.GetSnapshotAsync(false);
        var versionId = string.IsNullOrWhiteSpace(_installTo)
            ? (snapshot?.Current?.Id ?? snapshot?.Selected ?? "")
            : _installTo;
        App.Trace("OpenDetail: opening sub page, versionId=" + versionId);
        try
        {
            _host.OpenSubPage(typeof(OnlineDetailPage),
                new OnlineDetailArgs(project, source, sourceName, versionId, _host, MarkInstalled),
                "获取", "在线资源", project.Title);
            App.Trace("OpenDetail: sub page opened");
        }
        catch (Exception ex)
        {
            App.Trace("OpenDetail: FAILED " + ex);
            _host.CloseSubPage();
            ResultCount.Text = "打开详情失败：" + ex.Message;
        }
    }

    private static string PageTagFor(ModrinthProject project) => project.InstallKind switch
    {
        "resourcepack" => "resourcepack",
        "shaderpack" => "shaderpack",
        "modpack" => "versions",
        _ => "mods",
    };

    private static string ClipText(string? message, int limit)
    {
        var text = (message ?? "").Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }
}
