using System.ComponentModel;
using System.Diagnostics;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI.Text;

namespace MeowLauncher.WinUI.Views;

public sealed partial class VersionsPage : Page, IPageSearchFilter
{
    private enum ListState
    {
        Loading,
        List,
        Empty,
        Error,
    }

    private enum StageState
    {
        Pending,
        Active,
        Done,
    }

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Snapshot? _snapshot;
    private List<VersionInfo> _all = [];
    private string _query = "";

    // 多目录：_dirs 是列表顺序（第一个是主目录），_versionsByDir 是每个目录扫到的实例。
    private readonly List<string> _dirs = [];
    private readonly Dictionary<string, List<VersionInfo>> _versionsByDir = new(StringComparer.OrdinalIgnoreCase);
    private readonly CollectionViewSource _groupsView = new() { IsSourceGrouped = true };
    private readonly List<VersionGroup> _groups = [];
    private readonly HashSet<string> _collapsedDirs = new(StringComparer.OrdinalIgnoreCase);
    private string _primaryDir = "";
    private bool _suppressDirChange;

    /// <summary>按目录分组后的列表源（组头 + 行都由 x:Bind 直接取）。</summary>
    public CollectionViewSource GroupsView => _groupsView;
    private VersionInfo? _selected;
    private ListState _listState = ListState.Loading;
    private bool _loading;
    private bool _launching;
    private bool _suppressInstanceSettings;
    private bool _savingSettings;
    private bool _settingsDirty;
    private double _loadedMemory = 4096;
    private bool _loadedMemAuto;
    private int? _globalMemoryMb;
    private bool _globalMemAuto;
    private bool _instanceSettingsReady;
    private bool _extraReady;
    private bool _loadedUseGlobal = true;
    private string _loadedJava = "";
    private string _instanceJavaPath = "";
    private IReadOnlyList<JavaInfo> _javaRuntimes = [];
    private bool _javaRuntimesLoaded;
    private bool _updatingJavaChoices;
    private bool _refreshingJavaRuntimes;
    private string _loadedJvm = "";
    private string _loadedIsolation = "follow";
    private string _loadedStatus = "";

    public string? SelectedVersionId => _selected?.Id;

    public VersionsPage()
    {
        InitializeComponent();
        Loaded += VersionsPage_Loaded;
        Loaded += (_, _) => ApplyCompact(App.MainWindow?.IsCompact ?? false);
        ExtraPanel.Changed += ExtraPanel_Changed;
        if (App.MainWindow is { } shell) shell.CompactChanged += (_, _) => ApplyCompact(shell.IsCompact);
        Unloaded += (_, _) =>
        {
            _toastTimer.Stop();
            _saveTimer.Stop();
            GameSessions.Changed -= GameSessions_Changed;
        };
        GameSessions.Changed -= GameSessions_Changed;
        GameSessions.Changed += GameSessions_Changed;
        _toastTimer.Tick += (_, _) =>
        {
            Toast.IsOpen = false;
            _toastTimer.Stop();
        };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await FlushInstanceSettingsAsync();
        };
    }

    private async void VersionsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    /// <summary>

    // ------------------------------------------------------------------ 列表 / 三态

    private const int ScanBudgetMs = 8000;
    private const int SnapshotBudgetMs = 8000;

    /// <summary>
    /// 目录解析只查本地文件（毫秒级）：一个可用目录都没有时直接进空态，不再显示扫描转圈。
    /// 每个目录走 --scan-dir（实测约 0.2s/个），超过 8s 的目录不再等；快照另跑并限时 8s，
    /// 只用来补「当前实例」，拿不到也不影响列表。
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        _saveTimer.Stop();
        SetListState(ListState.Loading);
        try
        {
            var resolved = GameDirectoryResolver.Resolve(App.MainWindow?.Preferences);
            _dirs.Clear();
            _dirs.AddRange(resolved);
            SyncDirBox();

            if (_dirs.Count == 0)
            {
                _primaryDir = "";
                _versionsByDir.Clear();
                _all = [];
                _snapshot = null;
                GameDirText.Text = "还没有配置游戏目录";
                ApplyFilter(keepSelection: false);
                return;
            }

            if (!_dirs.Any(d => IsSameDir(d, _primaryDir))) _primaryDir = _dirs[0];
            GameDirText.Text = _primaryDir;

            var snapshotTask = WithTimeout(App.Backend.GetSnapshotAsync(force: true), SnapshotBudgetMs);
            var scans = _dirs.Select(ScanDirSafeAsync).ToList();
            await Task.WhenAny(Task.WhenAll(scans), Task.Delay(ScanBudgetMs));

            _versionsByDir.Clear();
            foreach (var scan in scans.Where(s => s.IsCompletedSuccessfully))
            {
                var (dir, list) = scan.Result;
                _versionsByDir[dir] = list;
            }

            RebuildFromScans();
            ApplyFilter(keepSelection: true);
            _ = FinishRefreshAsync(scans, snapshotTask);
        }
        catch (Exception ex)
        {
            _all = [];
            ShowError("扫描本地实例失败。", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>后台补齐：等快照（当前实例）与还没扫完的目录，有变化才重画一次。</summary>
    private async Task FinishRefreshAsync(
        List<Task<(string Dir, List<VersionInfo> Versions)>> scans,
        Task<Snapshot?> snapshotTask)
    {
        try
        {
            var changed = false;
            var snapshot = await snapshotTask;
            if (snapshot is not null)
            {
                _snapshot = snapshot;
                var currentId = !string.IsNullOrWhiteSpace(snapshot.Current?.Id) ? snapshot.Current!.Id : snapshot.Selected;
                foreach (var item in _all)
                {
                    item.IsCurrent = !string.IsNullOrWhiteSpace(currentId)
                                     && string.Equals(item.Id, currentId, StringComparison.OrdinalIgnoreCase)
                                     && IsSameDir(item.Root, _primaryDir);
                }
                changed = true;
            }

            var pending = scans.Where(s => !s.IsCompleted).ToList();
            if (pending.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ScanBudgetMs));
                foreach (var scan in pending.Where(s => s.IsCompletedSuccessfully))
                {
                    var (dir, list) = scan.Result;
                    _versionsByDir[dir] = list;
                    changed = true;
                }
            }

            if (!changed) return;
            RebuildFromScans();
            ApplyFilter(keepSelection: true);
        }
        catch
        {
            // 后台补齐失败不影响已经渲染出来的列表。
        }
    }

    private async Task<(string Dir, List<VersionInfo> Versions)> ScanDirSafeAsync(string dir)
    {
        try
        {
            var result = await App.Backend.ScanDirAsync(dir);
            var list = result?.Versions ?? [];
            foreach (var version in list) version.Root = dir;
            return (dir, list);
        }
        catch
        {
            return (dir, []);
        }
    }

    private void RebuildFromScans()
    {
        var merged = new List<VersionInfo>();
        foreach (var dir in _dirs)
        {
            if (!_versionsByDir.TryGetValue(dir, out var list)) continue;
            foreach (var version in list)
            {
                if (string.IsNullOrWhiteSpace(version.Root)) version.Root = dir;
                merged.Add(version);
            }
        }
        _all = merged.Where(v => v.Valid && !v.Hidden).ToList();
    }

    private static async Task<T?> WithTimeout<T>(Task<T?> task, int milliseconds) where T : class
    {
        var finished = await Task.WhenAny(task, Task.Delay(milliseconds));
        if (finished != task) return null;
        try
        {
            return await task;
        }
        catch
        {
            return null;
        }
    }

    public async Task OpenVersionAsync(string id, string focus)
    {
        if (_all.Count == 0) await RefreshAsync();
        var version = _all.FirstOrDefault(v => v.Id == id);
#if DEBUG
        try
        {
            var navLog = System.IO.Path.Combine(Services.AppPreferencesStore.FolderPath, "debug-nav.log");
            System.IO.File.AppendAllText(navLog, $"[{DateTime.Now:HH:mm:ss.fff}] open-version id='{id}' found={(version is null ? "null" : version.Id)} total={_all.Count} sample={string.Join("|", _all.Where(v => v.Id.Contains("1.10.2") || v.Id.Contains("OptiFine")).Select(v => v.Id))}\n");
        }
        catch { }
#endif
        if (version is null)
        {
            ShowToast(InfoBarSeverity.Warning, "没有找到实例", id);
            return;
        }

        ShowDetails(version);
        if (focus is not ("java" or "memory" or "launch")) return;

        await Task.Delay(80);
        LaunchSettingsSourceRow.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.18,
        });
        await Task.Delay(180);
        if (focus == "java")
            (InstanceJavaBox.IsEnabled ? (Control)InstanceJavaBox : InstanceUseGlobalSwitch).Focus(FocusState.Programmatic);
        else if (focus == "memory")
            (InstanceMemoryBox.Visibility == Visibility.Visible ? (Control)InstanceMemoryBox : InstanceUseGlobalSwitch).Focus(FocusState.Programmatic);
        else
        {
            DetailLaunch.Focus(FocusState.Programmatic);
#if DEBUG
            // 调试钩子：debug-nav 的 version|<id>|launch 直接触发「启动该版本」，
            // 否则这条分支只会聚焦按钮、不会真的启动，隔离模式没法取证。
            await Task.Delay(120);
            Launch_Click(DetailLaunch, new RoutedEventArgs());
#endif
        }
    }

    /// <summary>窄窗口档：页头标题 28 → 22，行改单行 44 高；宽档原样。1440/1120 两档不受影响。</summary>
    private void ApplyCompact(bool compact)
    {
        CrumbRoot.FontSize = compact ? 22 : 28;
        CrumbCurrent.FontSize = compact ? 22 : 28;
        if (VersionRowText.CompactRows == compact) return;
        VersionRowText.CompactRows = compact;
        ApplyFilter(keepSelection: true);
    }

    private void QueryBox_QueryChanged(object? sender, string text)
    {
        _query = (text ?? string.Empty).Trim();
        ApplyFilter(true);
    }

    public bool TryFocusSearch() => QueryBox.FocusInput();

    public void ApplySearch(string query)
    {
        QueryBox.Text = query ?? string.Empty;
        _query = (query ?? string.Empty).Trim();
        ApplyFilter(true);
    }

    private void ApplyFilter(bool keepSelection)
    {
        // 页内搜索框（QueryBox）按实例名等过滤，再叠加目录与隐藏状态筛选。
        _groups.Clear();
        var total = 0;
        foreach (var dir in _dirs)
        {
            if (!_versionsByDir.TryGetValue(dir, out var source)) continue;
            var visible = source.Where(v => v.Valid && !v.Hidden);
            if (_query.Length > 0)
            {
                visible = visible.Where(v =>
                    v.Id.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    (v.Loader?.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            var visibleList = visible.ToList();
            var rows = OrderFor(dir, visibleList).ToList();
            if (rows.Count == 0) continue;
            var group = new VersionGroup(dir) { IsPrimary = IsSameDir(dir, _primaryDir) };
            foreach (var version in rows) group.AddRow(new VersionRow(version, dir));
            group.SetExpanded(_query.Length > 0 || !_collapsedDirs.Contains(dir));
            _groups.Add(group);
            total += rows.Count;
        }

        _groupsView.Source = _groups;

        if (total == 0)
        {
            var hasVersionsFolder = _dirs.Any(GameDirectoryResolver.HasVersions);
            EmptyOpenDirButton.Visibility = _dirs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyDownloadButton.Visibility = Visibility.Visible;
            if (_dirs.Count == 0)
            {
                ListEmpty.Glyph = "\uE81E";
                ListEmpty.Title = "还没有配置游戏目录";
                ListEmpty.Hint = "选一个装过游戏的 .minecraft 目录，实例就会出现在这里。";
            }
            else if (!hasVersionsFolder)
            {
                ListEmpty.Glyph = "\uE8B7";
                ListEmpty.Title = "这个目录里没有 versions 文件夹";
                ListEmpty.Hint = "换一个装过游戏的 .minecraft 目录，或先在下载页装一个版本。";
            }
            else if (_all.Count == 0)
            {
                ListEmpty.Glyph = "\uE81E";
                ListEmpty.Title = "还没有本地实例";
                ListEmpty.Hint = "去下载页装一个版本，或添加另一个游戏目录。";
            }
            else
            {
                ListEmpty.Glyph = "\uE81E";
                ListEmpty.Title = "没有可用的实例";
                ListEmpty.Hint = "隐藏或无效的实例不会出现在列表里。";
                EmptyDownloadButton.Visibility = Visibility.Collapsed;
            }
            SetListState(ListState.Empty);
            if (keepSelection && _selected is not null) ShowList();
            return;
        }

        SetListState(ListState.List);

        if (keepSelection && _selected is { } selected)
        {
            var keep = _all.FirstOrDefault(v => v.Id == selected.Id && IsSameDir(v.Root, selected.Root));
            if (keep is not null && _groups.Any(g => IsSameDir(g.Folder, keep.Root)))
            {
                ShowDetails(keep);
                return;
            }
        }

        if (_selected is null) ShowList();
    }

    // ------------------------------------------------------------------ 多目录

    private static string NormalizeDir(string? dir)
    {
        var value = (dir ?? "").Trim();
        if (value.Length == 0) return "";
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static bool IsSameDir(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(NormalizeDir(a), NormalizeDir(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>目录列表 = 磁盘上真实存在的候选目录（见 GameDirectoryResolver），最多 6 个。</summary>
    private void ResolveDirs(string primary)
    {
        var resolved = GameDirectoryResolver.Resolve(App.MainWindow?.Preferences);
        var normalized = GameDirectoryResolver.Normalize(primary);
        _dirs.Clear();
        if (normalized.Length > 0 && Directory.Exists(normalized) && !resolved.Any(d => IsSameDir(d, normalized)))
        {
            _dirs.Add(normalized);
        }
        foreach (var dir in resolved)
        {
            if (_dirs.Count >= GameDirectoryResolver.MaxDirs) break;
            if (_dirs.Any(d => IsSameDir(d, dir))) continue;
            _dirs.Add(dir);
        }
        SyncDirBox();
    }

    private void SyncDirBox()
    {
        _suppressDirChange = true;
        try
        {
            DirBox.Items.Clear();
            foreach (var dir in _dirs) DirBox.Items.Add(dir);
            var index = _dirs.FindIndex(d => IsSameDir(d, _primaryDir));
            DirBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressDirChange = false;
        }
    }

    /// <summary>只持久化真实存在的目录，避免把猜测出来的路径写回 app.json。</summary>
    private void SaveDirs()
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        var valid = _dirs.Where(Directory.Exists).ToList();
        prefs.McDirs = valid;
        prefs.McDir = valid.FirstOrDefault(d => IsSameDir(d, _primaryDir)) ?? valid.FirstOrDefault() ?? "";
        AppPreferencesStore.Save(prefs);
    }

    private async void DirBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDirChange) return;
        var index = DirBox.SelectedIndex;
        if (index < 0 || index >= _dirs.Count) return;
        var dir = _dirs[index];
        if (IsSameDir(dir, _primaryDir)) return;
        _primaryDir = dir;
        _dirs.RemoveAll(d => IsSameDir(d, dir));
        _dirs.Insert(0, dir);
        SaveDirs();
        await App.Backend.SetGameDirAsync(dir);
        await RefreshAsync();
    }

    private async void AddDir_Click(object sender, RoutedEventArgs e)
    {
        if (_dirs.Count >= 6)
        {
            ShowToast(InfoBarSeverity.Warning, "目录最多 6 个", "为避免扫描目录时间过长，请避免加入过多目录导致性能下降。");
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is { } window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var dir = NormalizeDir(folder.Path);
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (_dirs.Any(d => IsSameDir(d, dir)))
        {
            ShowToast(InfoBarSeverity.Informational, "这个目录已经在列表里", dir);
            return;
        }

        _dirs.Add(dir);
        SaveDirs();
        ShowToast(InfoBarSeverity.Success, "已添加游戏目录", dir);
        await RefreshAsync();
    }

    private async void RemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (_dirs.Count <= 1)
        {
            ShowToast(InfoBarSeverity.Warning, "至少保留一个目录", "移除只影响列表，不会删除磁盘上的实例。");
            return;
        }

        var index = DirBox.SelectedIndex >= 0 && DirBox.SelectedIndex < _dirs.Count ? DirBox.SelectedIndex : 0;
        var removed = _dirs[index];
        _dirs.RemoveAt(index);
        if (IsSameDir(removed, _primaryDir))
        {
            _primaryDir = _dirs[0];
            await App.Backend.SetGameDirAsync(_primaryDir);
        }
        SaveDirs();
        ShowToast(InfoBarSeverity.Informational, "已从列表移除", removed);
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ 自定义顺序

    private List<string> OrderFor(string dir) =>
        App.MainWindow?.Preferences.VersionOrder.TryGetValue(dir, out var ids) == true ? ids : [];

    private void SaveOrder(string dir, IEnumerable<string> ids)
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;
        prefs.VersionOrder[dir] = ids.ToList();
        AppPreferencesStore.Save(prefs);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveRow(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveRow(sender, 1);

    private void MoveRow(object sender, int delta)
    {
        if (sender is not Button { Tag: VersionRow row }) return;
        var group = _groups.FirstOrDefault(g => IsSameDir(g.Folder, row.Root));
        if (group is null) return;
        var list = group.Items.ToList();
        var index = list.FindIndex(r => string.Equals(r.Id, row.Id, StringComparison.Ordinal));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;
        (list[index], list[target]) = (list[target], list[index]);
        group.Items.Clear();
        foreach (var item in list) group.Items.Add(item);
        SaveOrder(group.Folder, list.Select(r => r.Id));
    }

    // 上移/下移只在行悬停或聚焦时出现：始终占位（Opacity 切换），箭头与文字不会跳动。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
    private object? _hoverRow;

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer?.Stop();
        SetMoveVisible(sender, true);
    }

    private void Row_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer?.Stop();
        SetMoveVisible(sender, true);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited 会从子元素冒泡上来；稍等一下，若指针仍在行内会收到 PointerMoved 取消收起。
        _hoverTimer ??= DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(120);
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick -= HoverTimer_Tick;
        _hoverTimer.Tick += HoverTimer_Tick;
        _hoverTimer.Stop();
        _hoverRow = sender;
        _hoverTimer.Start();
    }

    private void HoverTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args) =>
        SetMoveVisible(_hoverRow, false);


    /// <summary>固定/取消固定到首页「固定版本」栏（最多 6 个）。</summary>
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionInfo info) return;
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null) return;

        var list = (prefs.PinnedVersions ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

        if (list.Contains(info.Id)) list.Remove(info.Id);
        else
        {
            list.Add(info.Id);
            if (list.Count > 6) list.RemoveAt(0);
        }

        prefs.PinnedVersions = string.Join("|", list);
        AppPreferencesStore.Save(prefs);
        ShowToast(InfoBarSeverity.Success, list.Contains(info.Id) ? "已固定到首页" : "已取消固定", info.Id);
    }

    private void MovePanel_GotFocus(object sender, RoutedEventArgs e) => SetMoveVisible(sender, true);

    private void MovePanel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Panel panel && panel.Children.Any(c => c is Control { FocusState: not FocusState.Unfocused }))
            return;
        SetMoveVisible(sender, false);
    }

    private static void SetMoveVisible(object? sender, bool visible)
    {
        if (sender is FrameworkElement { Tag: "move" } self)
        {
            SetOne(self, visible);
            return;
        }

        if (sender is not DependencyObject root) return;
        foreach (var move in Descendants(root).OfType<FrameworkElement>())
        {
            if (move.Tag is "move") SetOne(move, visible);
        }

        static void SetOne(FrameworkElement element, bool show)
        {
            var opacity = show ? 1d : 0d;
            if (Math.Abs(element.Opacity - opacity) > 0.001) element.Opacity = opacity;
            if (element.IsHitTestVisible != show) element.IsHitTestVisible = show;
        }
    }

    /// <summary>行模板里的上下移按钮在第二层 Grid 里，按可视树递归找。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private List<VersionInfo> OrderFor(string dir, List<VersionInfo> visible)
    {
        var order = OrderFor(dir);
        if (order.Count == 0) return visible;
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) ranks[order[i]] = i;
        return visible
            .OrderBy(v => ranks.TryGetValue(v.Id, out var rank) ? rank : int.MaxValue)
            .ThenByDescending(v => v.LastPlayed)
            .ThenByDescending(v => v.Mtime)
            .ToList();
    }

    private void SetListState(ListState state)
    {
        _listState = state;
        LoadingPanel.Visibility = state == ListState.Loading ? Visibility.Visible : Visibility.Collapsed;
        VersionList.Visibility = state == ListState.List ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == ListState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == ListState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string title, string summary)
    {
        ErrorTitle.Text = title;
        ErrorSummary.Text = ClipText(summary, 120);
        SetListState(ListState.Error);
    }

    private static string ClipText(string? text, int max)
    {
        var value = (text ?? "").Trim().ReplaceLineEndings(" ");
        return value.Length <= max ? value : value[..max] + "…";
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void EmptyDownload_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.Navigate("download");

    private void EmptyChangeDir_Click(object sender, RoutedEventArgs e) => AddDir_Click(sender, e);
    private void VersionGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string root }) return;
        var group = _groups.FirstOrDefault(g => string.Equals(g.Root, root, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;
        var expand = !group.IsExpanded;
        group.SetExpanded(expand);
        if (expand) _collapsedDirs.Remove(root);
        else _collapsedDirs.Add(root);
    }

    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionRow row)
        {
            ShowDetails(row.Source);
        }
    }

    // ------------------------------------------------------------------ 列表 / 详情切换

    private void ShowList()
    {
        _selected = null;
        _settingsDirty = false;
        _saveTimer.Stop();
        VersionList.SelectedItem = null;
        ListPanel.Visibility = Visibility.Visible;
        DetailView.Visibility = Visibility.Collapsed;
        OpenDirButton.Visibility = Visibility.Visible;
        UpdateCrumb(null);
    }

    private void ShowDetails(VersionInfo? version)
    {
        if (version is null)
        {
            ShowList();
            return;
        }

        _selected = version;
        _instanceSettingsReady = _extraReady = false;
        _extraDirty = false;
        _extraTimer?.Stop();
        InstanceUseGlobalSwitch.IsEnabled = InstanceIsolationBox.IsEnabled = false;
        InstanceSettingsStatus.Text = "正在读取启动设置…";
        _globalMemoryMb = null;
        SetInstanceSettingsEnabled();
        _settingsDirty = false;
        _saveTimer.Stop();
        ListPanel.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        OpenDirButton.Visibility = Visibility.Collapsed;
        UpdateCrumb(version);

        var instanceKey = version.Path.ToLowerInvariant();
        InstanceConsoleSwitch.Toggled -= InstanceConsole_Toggled;
        InstanceConsoleSwitch.IsOn = App.MainWindow?.Preferences.InstanceConsoles?.GetValueOrDefault(instanceKey) == true;
        InstanceConsoleSwitch.Toggled += InstanceConsole_Toggled;

        DetailCurrentPill.Visibility = version.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        DetailGameVersion.Text = $"{TypeLabel(version.Type)} {version.GameVersion}";
        DetailLoader.Text = version.LoaderDisplay;
        DetailModsRow.Visibility = string.IsNullOrWhiteSpace(version.Loader)
            || string.Equals(version.Loader, "vanilla", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed : Visibility.Visible;
        DetailMods.Text = version.Mods.ToString();
        DetailPath.Text = version.Path;
        DetailCreated.Text = Directory.Exists(version.Path)
            ? Directory.GetCreationTime(version.Path).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
        DetailLastPlayed.Text = version.AgoText.Replace(" ", "");
        DetailLaunch.IsEnabled = !_launching;
        UpdateLaunchButtonState(version.Id);
        DetailSwitch.Visibility = version.IsCurrent ? Visibility.Collapsed : Visibility.Visible;
        DetailSwitch.IsEnabled = true;
        HideVersionButton.IsEnabled = !version.IsCurrent;
        HideHintText.Visibility = version.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        DeleteHintText.Visibility = version.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        if (!_launching) LaunchStages.Visibility = Visibility.Collapsed;
        _ = LoadInstanceSettingsAsync(version);
    }

    private void InstanceConsole_Toggled(object sender, RoutedEventArgs e)
    {
        var preferences = App.MainWindow?.Preferences;
        if (preferences is null || _selected is not { } version) return;

        var key = version.Path.ToLowerInvariant();
        preferences.InstanceConsoles ??= new(StringComparer.OrdinalIgnoreCase);
        if (InstanceConsoleSwitch.IsOn) preferences.InstanceConsoles[key] = true;
        else preferences.InstanceConsoles.Remove(key);
        AppPreferencesStore.Save(preferences);
    }

    private static string TypeLabel(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_alpha" or "old_beta" => "远古版",
            null or "" => "未知类型",
            _ => type,
        };

    private void UpdateCrumb(VersionInfo? version)
    {
        var hasDetail = version is not null;
        CrumbRoot.FontWeight = new FontWeight { Weight = hasDetail ? (ushort)400 : (ushort)600 };
        CrumbRoot.Opacity = hasDetail ? 0.72 : 1;
        CrumbSeparator.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        CrumbCurrent.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        CrumbCurrent.Text = version?.Id ?? "";
        BackToInstanceButton.Visibility = hasDetail ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackToInstance_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.Navigate("instance");

    private void CrumbHome_Tapped(object sender, TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");

    private void CrumbRoot_Tapped(object sender, TappedRoutedEventArgs e) => ShowList();

    private string _lastIsolationTag = "follow";

    private void SelectIsolation(string? tag)
    {
        var want = string.IsNullOrWhiteSpace(tag) ? "follow" : tag;
        for (var i = 0; i < InstanceIsolationBox.Items.Count; i++)
        {
            if (InstanceIsolationBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            {
                InstanceIsolationBox.SelectedIndex = i;
                return;
            }
        }
        InstanceIsolationBox.SelectedIndex = 0;
    }

    private string SelectedIsolation() =>
        InstanceIsolationBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "follow";

    private async void InstanceIsolation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInstanceSettings) return;

        // 切换隔离会改变存档/Mod/配置实际读写的目录，先确认；取消就回滚下拉。
        var previous = _lastIsolationTag;
        var next = SelectedIsolation();
        if (next != previous)
        {
            var dialog = new ContentDialog
            {
                Title = "切换版本隔离？",
                Content = "会改变存档、Mod 和配置文件所在的目录。已有文件不会被移动；"
                          + "隔离关闭后游戏将读写游戏根目录（.minecraft）下的存档与 Mod。",
                PrimaryButtonText = "切换",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            if (!confirmed)
            {
                _suppressInstanceSettings = true;
                SelectIsolation(previous);
                _suppressInstanceSettings = false;
                return;
            }
            _lastIsolationTag = next;
        }

        _settingsDirty = true;
        ScheduleInstanceSave();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    // ------------------------------------------------------------------ 启动设置（自动应用）

    private async Task LoadInstanceSettingsAsync(VersionInfo version)
    {
        var versionId = version.Id;
        var settings = await App.Backend.GetInstanceLaunchSettingsAsync(versionId, version.Root);
        if (!ReferenceEquals(_selected, version)) return;
        _suppressInstanceSettings = true;
        try
        {
            InstanceUseGlobalSwitch.IsOn = settings.UseGlobal;
            InstanceMemoryBox.Value = settings.OverrideMem;

            _instanceJavaPath = settings.JavaPath ?? "";
            UpdateInstanceJavaChoices();
            InstanceJvmBox.Text = settings.Jvm;
            InstanceGlobalJvm.Text = string.IsNullOrWhiteSpace(settings.GlobalJvm) ? "默认参数" : settings.GlobalJvm;
            SelectIsolation(settings.Isolation);
            _lastIsolationTag = SelectedIsolation();
            InstanceIsolationHint.Text = settings.Isolated
                ? "当前：已隔离（versions/" + versionId + "）"
                : "当前：未隔离（游戏根目录）";
            InstanceSettingsStatus.Text = settings.Ok
                ? (settings.UseGlobal ? "当前跟随全局默认" : "当前使用实例覆盖")
                : settings.Message;
            _loadedMemory = settings.OverrideMem;
            _loadedMemAuto = settings.OverrideMemAuto;
            _globalMemoryMb = settings.Ok ? settings.GlobalMem : null;
            _globalMemAuto = settings.GlobalMemAuto;
            _loadedUseGlobal = settings.UseGlobal;
            _loadedJava = settings.JavaPath ?? "";
            _loadedJvm = settings.Jvm ?? "";
            _loadedIsolation = SelectedIsolation();
            _loadedStatus = InstanceSettingsStatus.Text;
            UpdateMemoryBar();
            UpdateCoverHint();
            var extra = await App.Backend.GetLaunchExtraAsync(versionId);
            if (ReferenceEquals(_selected, version) && extra.Ok)
            {
                ExtraPanel.Load(extra.Extra);
                _extraReady = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_selected, version))
            {
                _suppressInstanceSettings = false;
                _settingsDirty = false;
                _instanceSettingsReady = settings.Ok;
                InstanceUseGlobalSwitch.IsEnabled = InstanceIsolationBox.IsEnabled = settings.Ok;
                SetInstanceSettingsEnabled();
                if (settings.Ok && !settings.UseGlobal) _ = RefreshInstanceJavaChoicesAsync();
            }
        }
    }

    // ------------------------------------------------------------------ 封面（从首页卡搬进来）
    private void InstanceCoverPick_Click(object sender, RoutedEventArgs e) => _ = PickInstanceCoverAsync();

    private async Task PickInstanceCoverAsync()
    {
        var id = _selected?.Id ?? "";
        if (string.IsNullOrWhiteSpace(id)) return;
        var main = App.MainWindow;
        if (main is null) return;
        try
        {
            if (main.WindowHandle == IntPtr.Zero)
            {
                ShowToast(InfoBarSeverity.Error, "封面没有换成功", "窗口还没有准备好，过一秒再试。");
                return;
            }

            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, main.WindowHandle);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            {
                ShowToast(InfoBarSeverity.Error, "封面没有换成功", "这个文件读不到本地路径，换一张图片试试。");
                return;
            }

            var prefs = main.Preferences;
            if (prefs is null) return;
            prefs.HomeCovers ??= new Dictionary<string, string>();
            prefs.HomeCovers[id] = file.Path;
            AppPreferencesStore.Save(prefs);
            UpdateCoverHint();
            ShowToast(InfoBarSeverity.Success, "封面已更新", "首页这个实例的卡会换成这张图。");
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "封面没有换成功", ex.Message);
        }
    }

    private void InstanceCoverReset_Click(object sender, RoutedEventArgs e)
    {
        var id = _selected?.Id ?? "";
        var prefs = App.MainWindow?.Preferences;
        if (string.IsNullOrWhiteSpace(id) || prefs?.HomeCovers is null || prefs.HomeCovers.Count == 0) return;

        var removed = prefs.HomeCovers.Remove(id);
        if (!removed)
        {
            var key = prefs.HomeCovers.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
            if (key is not null) removed = prefs.HomeCovers.Remove(key);
        }
        if (!removed) return;

        AppPreferencesStore.Save(prefs);
        UpdateCoverHint();
        ShowToast(InfoBarSeverity.Success, "已恢复默认封面", "没有自定义封面时，会先挑这个实例的截图。");
    }

    /// <summary>只有真的设过自定义封面才写一行；没设就留空，别让「跟随截图」变成常驻说明。</summary>
    private void UpdateCoverHint()
    {
        var id = _selected?.Id ?? "";
        var covers = App.MainWindow?.Preferences?.HomeCovers;
        var has = !string.IsNullOrWhiteSpace(id) && covers is not null
                  && (covers.ContainsKey(id)
                      || covers.Keys.Any(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)));
        InstanceCoverHint.Text = has ? "已使用自定义封面" : "";
        InstanceCoverResetButton.IsEnabled = has;
    }

    private void InstanceUseGlobal_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInstanceSettings || !_instanceSettingsReady) return;
        SetInstanceSettingsEnabled();
        UpdateMemoryBar();
        if (!InstanceUseGlobalSwitch.IsOn) _ = RefreshInstanceJavaChoicesAsync();
        ScheduleInstanceSave();
    }

    private void InstanceMemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateMemoryBar();
        if (_suppressInstanceSettings) return;
        ScheduleInstanceSave();
    }

    private static string GbText(double mb) => $"{mb / 1024d:0.#} GB";

    /// <summary>
    /// 内存条：整条 = 本机总内存；其他占用 = 总 − 当前可用；预估分配 = 输入框里的值。
    /// 上面一排大数字和三段同色；条上的把手可以直接拖出分配量；分配超过剩余时变警示色并说明。
    /// </summary>
    private void UpdateMemoryBar()
    {
        // XAML 解析时 NumberBox 的 Value="4096" 会立刻触发 ValueChanged，那时条形里的控件还没创建（为 null）。
        // 这里不挡的话，整页会抛 XamlParseException（Failed to assign to property NumberBox.Value）。
        if (MemBarHost is null || MemBar is null || MemTicks is null || MemSegAlloc is null || MemWarn is null
            || MemHandle is null || MemStatOther is null || MemStatAlloc is null || MemStatFree is null
            || MemStatTotal is null || MemDotAlloc is null || MemDotFree is null
            || InstanceUseGlobalSwitch is null || InstanceMemoryBox is null || InstanceMemorySummary is null
            || MemCapOther is null || MemCapAlloc is null || MemFreeStat is null)
            return;
        var useGlobal = InstanceUseGlobalSwitch.IsOn;
        var legacyAutomatic = !useGlobal && _loadedMemAuto
            && Math.Abs(InstanceMemoryBox.Value - _loadedMemory) < 0.5;
        var readOnly = useGlobal || legacyAutomatic;
        InstanceMemorySummary.Visibility = Visibility.Collapsed;
        var alloc = InstanceMemoryBox.Value;
        if (readOnly)
        {
            var (summary, allocation) = ReadOnlyMemoryState(useGlobal);
            InstanceMemorySummary.Text = summary;
            InstanceMemorySummary.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (allocation is not { } mb)
            {
                MemBarHost.Visibility = Visibility.Collapsed;
                return;
            }
            alloc = mb;
        }

        var (total, available) = HostMemory.Read();
        if (_dragMem) { total = _dragTotal; available = _dragTotal - _dragOther; }
        if (total <= 0)
        {
            MemBarHost.Visibility = Visibility.Collapsed;   // 读不到本机内存就不画一根没依据的条
            return;
        }
        MemBar.Height = readOnly ? 10 : 24;
        MemBarHost.Visibility = Visibility.Visible;

        var other = Math.Max(0, total - available);
        var free = Math.Max(0, total - other);
        if (double.IsNaN(alloc) || alloc < 0) alloc = 0;

        var running = _selected is not null && GameSessions.IsRunning(_selected.Id);
        var twoSegments = running;

        var over = !twoSegments && alloc > free;
        var shownAlloc = twoSegments ? 0 : Math.Min(alloc, free);
        var rest = twoSegments ? free : Math.Max(0, free - shownAlloc);

        MemColOther.Width = new GridLength(Math.Max(other, 0.0001), GridUnitType.Star);
        MemColAlloc.Width = new GridLength(Math.Max(shownAlloc, 0.0001), GridUnitType.Star);
        MemColFree.Width = new GridLength(Math.Max(rest, 0.0001), GridUnitType.Star);
        MemSegOther.Margin = (shownAlloc > 0 || rest > 0) ? new Thickness(0, 0, 2, 0) : new Thickness(0);
        MemSegAlloc.Margin = (shownAlloc > 0 && rest > 0) ? new Thickness(0, 0, 2, 0) : new Thickness(0);
        var allocBrush = PagePalette.ThemeBrush(over ? "AppWarnBrush" : "AppBrandBrush");
        MemSegAlloc.Background = allocBrush;
        MemDotAlloc.Background = allocBrush;

        MemCapOther.Text = readOnly ? "已占用内存" : "其他程序已用";
        MemCapAlloc.Text = readOnly
            ? (_loadedMemAuto ? "智能分配内存" : "固定分配内存")
            : "本实例分配";
        MemFreeStat.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        MemTicks.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        MemStatOther.Text = GbText(other);
        MemStatAlloc.Text = GbText(alloc);
        MemStatAlloc.Foreground = PagePalette.ThemeBrush(over ? "AppWarnBrush" : "AppTextBrush");
        MemStatFree.Text = over ? "不够" : GbText(rest);
        MemStatFree.Foreground = PagePalette.ThemeBrush(over ? "AppWarnBrush" : "AppTextBrush");
        MemStatTotal.Text = GbText(total);

        var barPx = MemBar.ActualWidth;

        // 把手：贴在分配段右缘，只有「自定义覆盖且没在运行」才出现。
        MemHandle.Visibility = readOnly || twoSegments || barPx <= 0 ? Visibility.Collapsed : Visibility.Visible;
        if (MemHandle.Visibility == Visibility.Visible)
            Canvas.SetLeft(MemHandle, Math.Max(0, Math.Min(barPx - 8, barPx * (other + shownAlloc) / total - 4)));

        AutomationProperties.SetName(MemBar,
            $"内存分配条：{MemCapOther.Text} {GbText(other)}，{MemCapAlloc.Text} {GbText(alloc)}，本机总内存 {GbText(total)}");

        if (readOnly && !running)
        {
            MemWarn.Visibility = Visibility.Collapsed;
            return;
        }

        if (running)
        {
            MemWarn.Foreground = PagePalette.ThemeBrush("AppText3Brush");
            MemWarn.Text = $"游戏运行中：游戏已用内存计入「{MemCapOther.Text}」。";
        }
        else if (over)
        {
            MemWarn.Foreground = PagePalette.ThemeBrush("AppWarnBrush");
            MemWarn.Text = $"比当前可用内存多 {GbText(alloc - free)}，启动时可能卡顿或失败。";
        }
        else if (rest < 1024)
        {
            MemWarn.Foreground = PagePalette.ThemeBrush("AppWarnBrush");
            MemWarn.Text = $"偏紧：分配后电脑只剩 {GbText(rest)}，开着别的程序时可能卡。";
        }
        else
        {
            MemWarn.Foreground = PagePalette.ThemeBrush("AppText3Brush");
            MemWarn.Text = $"余量充足：分配后电脑还剩 {GbText(rest)}。拖动条上的把手可以直接调整。";
        }
        MemWarn.Visibility = Visibility.Visible;

        BuildMemoryTicks(total, barPx);
    }

    private void MemBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMemoryBar();

    private bool _dragMem;
    private readonly Dictionary<(string Root, string Id), MemEstimate> _memEstimates = new();
    private readonly HashSet<(string Root, string Id)> _memEstimating = new();

    private (string Summary, double? AllocationMb) ReadOnlyMemoryState(bool useGlobal)
    {
        double? fallback = useGlobal ? _globalMemoryMb : _loadedMemory;
        if (fallback is null || _selected is not { } version)
            return ("正在读取全局设置…", null);
        if (!(useGlobal ? _globalMemAuto : _loadedMemAuto))
            return ($"固定 · {fallback:0} MB", fallback);
        if (_memEstimates.TryGetValue((version.Root, version.Id), out var estimate))
        {
            if (!estimate.Ok || estimate.Mem <= 0)
                return ($"估算不可用 · 使用备用值 {fallback:0} MB", fallback);
            return ("", estimate.Mem);
        }
        _ = LoadMemEstimateAsync(version);
        return ("正在估算内存…", null);
    }

    private async Task LoadMemEstimateAsync(VersionInfo version)
    {
        var key = (version.Root, version.Id);
        if (!_memEstimating.Add(key)) return;
        try { _memEstimates[key] = await App.Backend.GetMemEstimateAsync(version.Id, version.Root); }
        catch { _memEstimates[key] = new MemEstimate(false, 0, 0, "", false, ""); }
        finally { _memEstimating.Remove(key); }
        if (_selected is { } selected && (selected.Root, selected.Id) == key)
            UpdateMemoryBar();
    }
    private double _dragTotal, _dragOther;   // 按下时定格：拖动中不再读实时内存，免得「其他占用」跟着跳
    private double _ticksTotal, _ticksPx;     // 刻度只在总内存或条宽变了才重建，拖动时不闪

    private void MemHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (MemHandle.Visibility != Visibility.Visible) return;
        var (t0, a0) = HostMemory.Read();
        _dragTotal = t0;
        _dragOther = Math.Max(0, t0 - a0);
        _dragMem = t0 > 0 && MemHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MemHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragMem) return;
        var total = _dragTotal;
        var barPx = MemBar.ActualWidth;
        if (total <= 0 || barPx <= 0) return;
        var other = _dragOther;
        var free = Math.Max(0, total - other);
        var x = e.GetCurrentPoint(MemBar).Position.X;
        var mb = x / barPx * total - other;
        mb = Math.Max(1024, Math.Min(Math.Max(1024, free), mb));
        mb = Math.Round(mb / 512d) * 512d;
        if (Math.Abs(mb - InstanceMemoryBox.Value) > 0.5) InstanceMemoryBox.Value = mb;   // 走和输入框同一条保存路径
        e.Handled = true;
    }

    private void MemHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragMem = false;
        MemHandle.ReleasePointerCapture(e.Pointer);
    }

    private void MemHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragMem = false;

    /// <summary>条下方的刻度：按本机总内存挑步长（总内存 ≤ 5 格），最后一格带 GB。</summary>
    private void BuildMemoryTicks(double totalMb, double barPx)
    {
        if (barPx <= 0 || totalMb <= 0) return;
        if (Math.Abs(totalMb - _ticksTotal) < 0.5 && Math.Abs(barPx - _ticksPx) < 0.5) return;
        _ticksTotal = totalMb;
        _ticksPx = barPx;
        MemTicks.Children.Clear();
        var totalGb = totalMb / 1024d;
        var step = 64;
        foreach (var s in new[] { 1, 2, 4, 8, 16, 32, 64 })
            if (totalGb / s <= 5) { step = s; break; }

        var ticks = new List<int>();
        for (var g = 0; g <= totalGb + 0.001; g += step) ticks.Add(g);
        var mark = PagePalette.ThemeBrush("AppText3Brush");
        for (var i = 0; i < ticks.Count; i++)
        {
            var g = ticks[i];
            var x = Math.Min(barPx - 1, barPx * g / totalGb);
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 4, Fill = mark, Opacity = 0.6 };
            Canvas.SetLeft(line, x);
            MemTicks.Children.Add(line);

            var label = new TextBlock
            {
                Text = i == ticks.Count - 1 && ticks.Count > 1 ? $"{g} GB" : $"{g}",
                FontSize = 10.5,
                Foreground = mark,
            };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = label.DesiredSize.Width;
            Canvas.SetLeft(label, g == 0 ? 0 : Math.Max(0, Math.Min(x - w / 2, barPx - w)));
            Canvas.SetTop(label, 5);
            MemTicks.Children.Add(label);
        }
    }

    private void UpdateInstanceJavaChoices()
    {
        _updatingJavaChoices = true;
        try { JavaGuide.SetRuntimeChoices(InstanceJavaBox, _javaRuntimes, _instanceJavaPath); }
        finally { _updatingJavaChoices = false; }
    }

    private async Task RefreshInstanceJavaChoicesAsync(bool force = false)
    {
        if (_refreshingJavaRuntimes || (!force && _javaRuntimesLoaded)) return;
        _refreshingJavaRuntimes = true;
        try
        {
            var scan = await App.Backend.GetJavaScanAsync();
            if (!scan.Ok) return;
            _javaRuntimes = scan.Items;
            _javaRuntimesLoaded = true;
            if (_selected is not null) UpdateInstanceJavaChoices();
        }
        finally { _refreshingJavaRuntimes = false; }
    }

    private async void InstanceJavaBox_DropDownOpened(object sender, object e) =>
        await RefreshInstanceJavaChoicesAsync();

    private async void InstanceJavaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInstanceSettings || _updatingJavaChoices || _selected is null
            || !InstanceJavaBox.IsEnabled || InstanceUseGlobalSwitch.IsOn
            || InstanceJavaBox.SelectedItem is not ComboBoxItem { Tag: string value }) return;

        if (value == JavaGuide.ManageRuntimeTag)
        {
            InstanceJavaBox.IsDropDownOpen = false;
            UpdateInstanceJavaChoices();
            await JavaGuide.ManageRuntimesAsync();
            await RefreshInstanceJavaChoicesAsync(force: true);
            return;
        }

        if (string.Equals(value, _instanceJavaPath, StringComparison.OrdinalIgnoreCase)) return;
        _instanceJavaPath = value;
        ScheduleInstanceSave();
    }

    private void InstanceJvmBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInstanceSettings) return;
        ScheduleInstanceSave();
    }

    private void SetInstanceSettingsEnabled()
    {
        var inherited = InstanceUseGlobalSwitch.IsOn;
        var editable = _instanceSettingsReady && !inherited;
        var editor = editable ? Visibility.Visible : Visibility.Collapsed;
        var summary = _instanceSettingsReady && inherited ? Visibility.Visible : Visibility.Collapsed;
        InstanceAutoApplyLabel.Visibility = editor;
        InstanceMemoryBox.Visibility = editor;
        InstanceJvmBox.Visibility = editor;
        InstanceJavaBox.Visibility = editor;
        InstanceJavaBox.IsEnabled = editable;
        InstanceGlobalJava.Visibility = summary;
        InstanceGlobalJvm.Visibility = summary;
        InstanceGlobalExtra.Visibility = summary;
        ExtraPanel.Visibility = editor;
        ExtraPanel.IsEnabled = editable && _extraReady;
    }

    // ---- 启动附加设置（DESIGN-1110 第 4 项，scope=当前实例）
    private bool _extraDirty;
    private bool _savingExtra;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _extraTimer;

    private void ExtraPanel_Changed()
    {
        if (_suppressInstanceSettings || _selected is null || !ExtraPanel.IsEnabled) return;
        _extraDirty = true;
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
        if (!_extraDirty || _selected is null) return;
        if (_savingExtra || _savingSettings)
        {
            _extraTimer?.Start();
            return;
        }
        var version = _selected;
        _extraDirty = false;
        _savingExtra = true;
        try
        {
            await App.Backend.SetLaunchExtraAsync(version.Id, ExtraPanel.Read());
        }
        catch
        {
            // 附加设置写失败不该把整页弄成异常状态，静默即可
        }
        finally
        {
            _savingExtra = false;
        }
    }

    private void ScheduleInstanceSave()
    {
        if (_selected is null || !_instanceSettingsReady) return;
        _settingsDirty = true;
        InstanceSettingsStatus.Text = "正在应用…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task FlushInstanceSettingsAsync()
    {
        if (!_settingsDirty || _selected is null) return;
        if (_savingSettings || _savingExtra)
        {
            _saveTimer.Start();
            return;
        }
        var version = _selected;
        var memory = InstanceMemoryBox.Value;
        if (double.IsNaN(memory) || memory < 1024)
        {
            InstanceSettingsStatus.Text = "内存至少要 1024 MB";
            return;
        }

        // 打开详情时控件回填也会触发一次变更信号；值没变就别写后端，只把状态文案还原。
        var sameValues = Math.Abs(memory - _loadedMemory) < 0.5
            && InstanceUseGlobalSwitch.IsOn == _loadedUseGlobal
            && string.Equals(_instanceJavaPath, _loadedJava, StringComparison.Ordinal)
            && string.Equals(InstanceJvmBox.Text ?? "", _loadedJvm, StringComparison.Ordinal)
            && string.Equals(SelectedIsolation(), _loadedIsolation, StringComparison.Ordinal);
        if (sameValues)
        {
            _settingsDirty = false;
            InstanceSettingsStatus.Text = _loadedStatus;
            return;
        }

        _settingsDirty = false;
        _savingSettings = true;
        try
        {
            var savedUseGlobal = InstanceUseGlobalSwitch.IsOn;
            // A manually edited value must not inherit the global automatic mode.
            var savedMemAuto = _loadedMemAuto && Math.Abs(memory - _loadedMemory) < 0.5;
            var savedJava = _instanceJavaPath;
            var savedJvm = InstanceJvmBox.Text ?? "";
            var savedIsolation = SelectedIsolation();
            var result = await App.Backend.SetInstanceLaunchSettingsAsync(
                version.Id, (int)memory, savedUseGlobal,
                savedMemAuto,
                savedJava,
                savedJvm,
                savedIsolation,
                version.Root);
            if (!ReferenceEquals(_selected, version)) return;
            if (result.Ok)
            {
                // 写成功后把「已加载值」更新为刚写的值，否则改过去再改回原值会被当成没变化而不写盘。
                _loadedMemory = memory;
                _loadedMemAuto = savedMemAuto;
                _loadedUseGlobal = savedUseGlobal;
                _loadedJava = savedJava;
                _loadedJvm = savedJvm;
                _loadedIsolation = savedIsolation;
                _loadedStatus = savedUseGlobal ? "当前跟随全局默认" : "当前使用实例覆盖";
                try
                {
                    var fresh = await App.Backend.GetInstanceLaunchSettingsAsync(version.Id, version.Root);
                    if (ReferenceEquals(_selected, version) && fresh.Ok)
                    {
                        _globalMemoryMb = fresh.GlobalMem;
                        _globalMemAuto = fresh.GlobalMemAuto;
                        UpdateMemoryBar();
                        InstanceIsolationHint.Text = fresh.Isolated
                            ? "当前：已隔离（versions/" + version.Id + "）"
                            : "当前：未隔离（游戏根目录）";
                    }
                }
                catch { }
            }
            if (!ReferenceEquals(_selected, version)) return;
            InstanceSettingsStatus.Text = _settingsDirty ? "正在应用…"
                : result.Ok ? _loadedStatus : ClipText(result.Message, 80);
            if (!result.Ok)
                ShowToast(InfoBarSeverity.Warning, "设置没有写入", result.Message);
        }
        catch (Exception ex)
        {
            if (_selected?.Id == version.Id)
                InstanceSettingsStatus.Text = ClipText(ex.Message, 80);
        }
        finally
        {
            _savingSettings = false;
        }
    }

    // ------------------------------------------------------------------ 启动 + 四行阶段

    private async void SwitchInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.IsCurrent) return;
        var target = _selected;
        DetailSwitch.IsEnabled = false;
        var (ok, message) = await InstanceSelection.SelectAsync(target);
        DetailSwitch.IsEnabled = true;
        if (!ok)
        {
            ShowToast(InfoBarSeverity.Warning, "切换失败", message);
            return;
        }

        foreach (var item in _all)
        {
            item.IsCurrent = string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase)
                             && (string.IsNullOrWhiteSpace(target.Root) || string.IsNullOrWhiteSpace(item.Root)
                                 || IsSameDir(item.Root, target.Root));
        }
        target.IsCurrent = true;

        ApplyFilter(keepSelection: true);
        ShowDetails(target);
        ShowToast(InfoBarSeverity.Success, "已切换", $"当前实例已切到 {target.Id}");
    }

    private void GameSessions_Changed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateLaunchButtonState(_selected?.Id ?? "");
            UpdateMemoryBar();   // 运行 / 结束会改变「已用」是否含本实例
        });
    }

    /// <summary>详情页按钮跟着运行状态切换：启动该版本 / 结束游戏。</summary>
    private void UpdateLaunchButtonState(string versionId)
    {
        if (_launching) return;
        var running = GameSessions.IsRunning(versionId);
        LaunchLabel.Text = running ? "结束游戏" : "启动该版本";
        LaunchGlyph.Glyph = running ? "\uE71A" : "\uE768";
        AutomationProperties.SetName(DetailLaunch, running ? "结束这个实例的游戏" : "启动该版本");
    }

    private async Task StopGameAsync(string versionId)
    {
        var row = GameSessions.Find(versionId);
        var result = await App.Backend.StopGameAsync(row?.Pid ?? 0, versionId);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已结束游戏" : "结束失败", result.Message);
        await GameSessions.RefreshAsync();
        UpdateLaunchButtonState(versionId);
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _launching) return;
        if (GameSessions.IsRunning(_selected.Id))
        {
            await StopGameAsync(_selected.Id);
            return;
        }

        ShowToast(InfoBarSeverity.Informational, "检查文件", "正在检查实例文件是否完整…");
        if (!await VersionHealth.EnsureReadyAsync(_selected.Id, XamlRoot))
        {
            ShowToast(InfoBarSeverity.Informational, "已取消启动", "文件不完整，先补全再启动。");
            return;
        }

        _launching = true;
        SetLaunchBusy(true);
        StartStages();
        var target = _selected;
        var task = TaskCenter.Add($"启动 {target.Id}", "正在准备资源与 Java", "versions", "\uE768");
        var stageProgress = new Progress<string>(stage =>
        {
            TaskCenter.SetStage(task, stage);
            AdvanceStage(stage);
        });
        ShowToast(InfoBarSeverity.Informational, "正在启动", $"正在准备 {target.Id} 的资源和 Java…");
        try
        {
            var result = await App.Backend.LaunchAsync(target.Id, stageProgress, target.Root);
            if (result.Ok)
            {
                // 启动成功即视为切到这个实例，首页与版本页的「当前」保持一致。
                await InstanceSelection.SelectAsync(target);

                TaskCenter.SetStage(task, "运行中");
                Services.LaunchWatcher.Track(target.Id, task, 0, target.Root);
                if (string.Equals(App.MainWindow?.Preferences.AfterLaunch, "minimize", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Delay(900).ContinueWith(_ => App.MainWindow?.DispatcherQueue.TryEnqueue(
                        () => App.MainWindow?.MinimizeForGame()));
                }
                CompleteStages();
                _ = GameSessions.RefreshAsync();
                ShowToast(InfoBarSeverity.Success, "启动成功", result.Message);
                await Task.Delay(1200);
                LaunchStages.Visibility = Visibility.Collapsed;
                await RefreshAsync();
            }
            else
            {
                TaskCenter.Fail(task, result.Message);
                FailStage();
                ShowLaunchFailure(result.Message, _selected.Id);
            }
        }
        catch (Exception ex)
        {
            FailStage();
            ShowLaunchFailure(ex.Message, _selected.Id);
        }
        finally
        {
            _launching = false;
            SetLaunchBusy(false);
        }
    }

    private void SetLaunchBusy(bool busy)
    {
        DetailLaunch.IsEnabled = !busy;
        LaunchRing.IsActive = busy;
        LaunchRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LaunchGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) LaunchLabel.Text = "正在启动…";
        else UpdateLaunchButtonState(_selected?.Id ?? "");
    }

    private static readonly string[] StageNames =
    [
        "读取启动配置",
        "检查 Java 运行时",
        "组装启动参数",
        "启动游戏进程",
    ];

    private void StartStages()
    {
        LaunchStages.Visibility = Visibility.Visible;
        LaunchStageRing.IsActive = true;
        LaunchStageRing.Visibility = Visibility.Visible;
        LaunchStageTitle.Text = "正在启动…";
        for (var i = 0; i < StageNames.Length; i++) SetStageVisual(i, StageState.Pending);
    }

    private static int StageIndexFor(string stage)
    {
        if (stage.Contains("已启动", StringComparison.Ordinal) || stage.Contains("记录启动时间", StringComparison.Ordinal))
            return StageNames.Length;
        for (var i = 0; i < StageNames.Length; i++)
        {
            if (stage.Contains(StageNames[i], StringComparison.Ordinal)) return i;
        }
        if (stage.Contains("Java", StringComparison.OrdinalIgnoreCase)) return 1;
        if (stage.Contains("组装", StringComparison.Ordinal)) return 2;
        return -1;
    }

    private void AdvanceStage(string stage)
    {
        var index = StageIndexFor(stage);
        if (index < 0) return;
        LaunchStageTitle.Text = stage;
        for (var i = 0; i < StageNames.Length; i++)
        {
            SetStageVisual(i, i < index ? StageState.Done : i == index ? StageState.Active : StageState.Pending);
        }
    }

    private void CompleteStages()
    {
        LaunchStageTitle.Text = "游戏进程已启动";
        LaunchStageRing.IsActive = false;
        LaunchStageRing.Visibility = Visibility.Collapsed;
        for (var i = 0; i < StageNames.Length; i++) SetStageVisual(i, StageState.Done);
    }

    private void FailStage()
    {
        LaunchStageTitle.Text = "启动中断，可以查看日志再试";
        LaunchStageRing.IsActive = false;
        LaunchStageRing.Visibility = Visibility.Collapsed;
    }

    private void SetStageVisual(int index, StageState state)
    {
        var icon = index switch
        {
            0 => Stage1Icon,
            1 => Stage2Icon,
            2 => Stage3Icon,
            _ => Stage4Icon,
        };
        var row = index switch
        {
            0 => StageRow1,
            1 => StageRow2,
            2 => StageRow3,
            _ => StageRow4,
        };
        icon.Glyph = state switch
        {
            StageState.Done => "\uE73E",
            StageState.Active => "\uE895",
            _ => "\uE73B",
        };
        icon.Foreground = state switch
        {
            StageState.Done => BrushOf("AppGoodBrush"),
            StageState.Active => BrushOf("TintRecentBrush"),
            _ => BrushOf("AppText3Brush"),
        };
        row.Opacity = state == StageState.Pending ? 0.45 : 1;
    }

    private static Brush BrushOf(string key)
    {
        try
        {
            var resources = Application.Current?.Resources;
            if (resources is not null)
            {
                if (resources.TryGetValue(key, out var direct) && direct is Brush directBrush)
                    return directBrush;
                // t55：ThemeDictionaries 里的画刷要走当前主题字典取
                var theme = (App.MainWindow?.Content as FrameworkElement)?.ActualTheme
                            ?? ElementTheme.Default;
                var themeKey = theme == ElementTheme.Light ? "Light" : "Default";
                if (resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary)
                    && dictionary is ResourceDictionary themed
                    && themed.TryGetValue(key, out var themedBrush)
                    && themedBrush is Brush brush)
                {
                    return brush;
                }
            }
        }
        catch
        {
            // 主题资源缺失时退回默认前景色，不影响启动流程。
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    // ------------------------------------------------------------------ 快捷入口

    private async void HideVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (_selected.IsCurrent)
        {
            ShowToast(InfoBarSeverity.Warning, "当前实例不能隐藏", "无法隐藏正在使用的实例，请切换后再隐藏。");
            return;
        }

        var result = await App.Backend.HideVersionAsync(_selected.Id, _selected.Root);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            result.Ok ? "已隐藏" : "不能隐藏", result.Message);
        if (!result.Ok) return;

        await App.MainWindow.RefreshShellAsync(force: true);
        await RefreshAsync();
        ShowList();
    }

    /// <summary>行内重命名：实例名和 versions 下的文件夹一起改，不用先选中这一行。</summary>
    private async void RenameRow_Click(object sender, RoutedEventArgs e)
    {
        if (_launching || sender is not FrameworkElement { Tag: VersionRow row }) return;
        if (row.Id.Length == 0) return;
        var input = new TextBox
        {
            Text = row.Id,
            PlaceholderText = "新的实例名",
            MaxLength = 64,
            SelectionStart = row.Id.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名实例",
            Content = input,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var oldId = row.Id;
        var newId = input.Text.Trim();
        if (newId.Length == 0 || string.Equals(newId, oldId, StringComparison.Ordinal)) return;
        var result = await App.Backend.RenameVersionAsync(oldId, newId, row.Root);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已重命名" : "重命名失败", result.Message);
        if (!result.Ok) return;
        await App.MainWindow.RefreshShellAsync(force: true);
        await RefreshAsync();
        ShowList();
    }

    private async void RenameVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _launching) return;
        var input = new TextBox
        {
            Text = _selected.Id,
            PlaceholderText = "新的实例名",
            MaxLength = 64,
            SelectionStart = _selected.Id.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名实例",
            Content = input,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var oldId = _selected.Id;
        var newId = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(newId) || newId == oldId) return;

        var result = await App.Backend.RenameVersionAsync(oldId, newId, _selected.Root);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已重命名" : "重命名失败", result.Message);
        if (!result.Ok) return;

        await App.MainWindow.RefreshShellAsync(force: true);
        await RefreshAsync();
        var renamed = _all.FirstOrDefault(v => string.Equals(v.Id, newId, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null) ShowDetails(renamed);
        else ShowList();
    }

    private async void ChangeVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _launching) return;
        var target = _selected;
        var loader = target.Loader?.Trim() ?? "";
        if (loader.Equals("Forge", StringComparison.OrdinalIgnoreCase)
            || loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase)
            || loader.Equals("modded", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast(InfoBarSeverity.Warning, "暂不支持修改版本",
                $"{target.LoaderDisplay} 实例的安装器会生成绑定自身 id 的补丁文件，不能直接换版本。请新建目标版本的实例，再拷贝存档和 Mod。");
            return;
        }

        var list = await App.Backend.GetMojangVersionsAsync();
        if (list is null || !list.Ok || list.Versions.Count == 0)
        {
            ShowToast(InfoBarSeverity.Error, "取不到版本列表", list?.Message ?? "请检查网络后重试。");
            return;
        }

        var releases = list.Versions.Where(v => v.Type == "release").ToList();
        var combo = new ComboBox
        {
            ItemsSource = releases.Select(v => v.Id).ToList(),
            SelectedItem = releases.Any(v => v.Id == target.GameVersion) ? target.GameVersion : null,
            PlaceholderText = "选择目标 Minecraft 版本",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var note = new TextBlock
        {
            Text = $"当前：Minecraft {target.GameVersion} · {target.LoaderDisplay}\n"
                   + "存档、Mod 和实例设置都会保留，但 Mod 与存档可能和新版本不兼容（降级尤其可能损坏存档）。"
                   + "旧版本文件会先备份，保存后重启启动器生效。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(combo);
        panel.Children.Add(note);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"修改「{target.Id}」的版本",
            Content = panel,
            PrimaryButtonText = "修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newMc = combo.SelectedItem as string ?? "";
        if (string.IsNullOrWhiteSpace(newMc) || newMc == target.GameVersion) return;

        ShowToast(InfoBarSeverity.Informational, "正在修改版本", $"正在准备 Minecraft {newMc}，需要联网下载，请稍候…");
        var result = await App.Backend.ChangeVersionAsync(target.Id, newMc, target.Root);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已修改版本" : "修改版本失败", result.Message);
        if (!result.Ok) return;

        await App.MainWindow.RefreshShellAsync(force: true);
        await RefreshAsync();
        var changed = _all.FirstOrDefault(v => string.Equals(v.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        if (changed is not null) ShowDetails(changed);
        else ShowList();
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _launching) return;
        if (_selected.IsCurrent)
        {
            ShowToast(InfoBarSeverity.Warning, "当前实例不能删除", "无法删除正在使用的实例，请切换后再进行删除。");
            return;
        }

        var target = _selected;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除实例",
            Content = $"是否将「{target.Id}」移动到回收站？这将不会直接删除它！",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await App.Backend.DeleteVersionAsync(target.Id, target.Root);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已删除" : "删除失败", result.Message);
        if (!result.Ok) return;

        await App.MainWindow.RefreshShellAsync(force: true);
        await RefreshAsync();
        ShowList();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            ShowToast(InfoBarSeverity.Warning, "还没有选择实例", "先在列表里点一个版本。");
            return;
        }
        App.MainWindow.NavigateLogs(_selected.Id);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var path = _selected?.Path ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowToast(InfoBarSeverity.Warning, "没有可复制的路径", "先选择实例。");
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ShowToast(InfoBarSeverity.Success, "已复制实例路径", path);
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "复制失败", Diagnostics.FriendlyMessage(ex));
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _selected?.Path ?? _snapshot?.GameDir;
        OpenPath(path);
    }

    private void OpenGameDir_Click(object sender, RoutedEventArgs e) => OpenPath(_snapshot?.GameDir);

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowToast(InfoBarSeverity.Warning, "目录不存在", path ?? "还没有配置游戏目录。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "打开失败", Diagnostics.FriendlyMessage(ex));
        }
    }

    // ------------------------------------------------------------------ Toast

    private void ShowToast(InfoBarSeverity severity, string title, string message,
        string? actionText = null, Action? action = null, int seconds = 4)
    {
        Toast.Severity = severity;
        Toast.Title = title;
        Toast.Message = message;
        if (action is null)
        {
            Toast.ActionButton = null;
        }
        else
        {
            var button = new Button
            {
                Content = actionText ?? "查看",
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(14),
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Background = BrushOf("ChipHoverBrush"),
                Foreground = BrushOf("AppTextBrush"),
            };
            AutomationProperties.SetName(button, actionText ?? "查看");
            var captured = action;
            button.Click += (_, _) => captured();
            Toast.ActionButton = button;
        }
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    /// <summary>启动失败统一出口：错误码 + 可复制的诊断报告。</summary>
    private void ShowLaunchFailure(string message, string versionId)
    {
        var code = Diagnostics.ErrorCodeFor(message);
        ShowToast(InfoBarSeverity.Error, $"启动失败 · {code}", message, "复制诊断",
            () => Diagnostics.Copy(Diagnostics.BuildReport(code, "启动游戏", message, versionId, "")), 20);
    }
}

/// <summary>列表行：名称 + 一行副信息（版本 · 加载器 · Mod 数 · 最近游玩）。</summary>
public sealed class VersionRow(VersionInfo source, string root)
{
    public static readonly VersionRow Placeholder = new(new VersionInfo { Id = "" }, "");
    public VersionInfo Source { get; } = source;
    public Visibility RowVisibility => Source.Id.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string Root { get; } = root;
    public string Id => Source.Id;

    /// <summary>ListViewItem 的 UIA 名称取对象 ToString。</summary>
    public override string ToString() => Id;
    public string Subtitle => VersionRowText.Subtitle(Source);
    public Microsoft.UI.Xaml.Media.ImageSource IconSource => MeowLauncher.WinUI.Services.InstanceIcon.For(Source);
    public Visibility DotVisibility => Source.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentVisibility => DotVisibility;

    /// <summary>窄窗口档：行变单行（只留标题 + 行尾动作），高度 56 → 44。</summary>
    public Visibility SubtitleVisibility => VersionRowText.CompactRows ? Visibility.Collapsed : Visibility.Visible;
    public double RowMinHeight => VersionRowText.CompactRows ? 44 : 56;
    public Thickness RowPadding => VersionRowText.CompactRows ? new Thickness(12, 4, 12, 4) : new Thickness(12, 10, 12, 10);
}

/// <summary>目录分组：组头显示目录名与实例数，组内保持用户自定义顺序；可折叠。</summary>
public sealed class VersionGroup(string root) : IEnumerable<VersionRow>, INotifyPropertyChanged
{
    private readonly List<VersionRow> _rows = [];

    public string Root { get; } = root;
    public string Folder { get; } = root;
    public string Title { get; } = root;
    public int TotalCount => _rows.Count;
    public string CountText => $"{TotalCount} 个实例";

    public override string ToString() => Root;
    public bool IsExpanded { get; private set; } = true;

    /// <summary>主目录＝新实例默认装到这里，组头挂一个「默认」胶囊。</summary>
    public bool IsPrimary { get; set; }

    public Visibility DefaultVisibility => IsPrimary ? Visibility.Visible : Visibility.Collapsed;
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public System.Collections.ObjectModel.ObservableCollection<VersionRow> Items { get; } = [];

    public void AddRow(VersionRow row)
    {
        _rows.Add(row);
        if (IsExpanded) Items.Add(row);
    }

    public void SetExpanded(bool expanded)
    {
        if (expanded == IsExpanded && Items.Count > 0) return;
        IsExpanded = expanded;
        Items.Clear();
        if (expanded)
        {
            foreach (var row in _rows) Items.Add(row);
        }
        else
        {
            Items.Add(VersionRow.Placeholder);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
    }

    // ListView 的分组必须能枚举自身，否则组头下面不会出任何行。
    public IEnumerator<VersionRow> GetEnumerator() => Items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class VersionRowText
{
    /// <summary>窄窗口档标记：行模板据此切单行。改动后需要重建行（ApplyFilter）。</summary>
    public static bool CompactRows { get; set; }

    public static string Subtitle(VersionInfo version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(version.GameVersion)
            && !string.Equals(version.GameVersion, version.Id, StringComparison.OrdinalIgnoreCase)) parts.Add(version.GameVersion);
        var loader = version.LoaderDisplay;
        parts.Add(string.IsNullOrWhiteSpace(loader) || loader == "未检测到加载器" ? "原版" : loader);
        if (version.Mods > 0) parts.Add($"{version.Mods} 个 Mod");
        parts.Add(version.AgoText);
        return string.Join(" · ", parts);
    }
}
