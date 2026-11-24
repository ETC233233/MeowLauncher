using System.Collections.ObjectModel;
using System.Diagnostics;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly List<VersionGroup> _groups = [];
    private readonly HashSet<string> _collapsedDirs = new(StringComparer.OrdinalIgnoreCase);
    private string _primaryDir = "";
    private bool _suppressDirChange;
    private bool _changingDefaultDir;

    private VersionInfo? _selected;
    // 实例「路径」显示实际生效的游戏目录：隔离开是 versions/<id>，关就是 .minecraft 根。
    // 之前固定显示版本目录，和下面的「未隔离」互相打架。
    private bool _detailIsolated;
    private ListState _listState = ListState.Loading;
    private bool _loading;
    private bool _launching;
    private bool _suppressInstanceSettings;
    private bool _savingSettings;
    private bool _settingsDirty;
    private double _loadedMemory = 4096;
    private bool _loadedMemAuto;
    private bool _loadedJvmAuto = true;
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
        AttachListFilterPanel();
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
            _primaryDir = _dirs.FirstOrDefault() ?? "";
            SyncDirBox();

            if (_dirs.Count == 0)
            {
                _primaryDir = "";
                _versionsByDir.Clear();
                _all = [];
                _snapshot = null;
                ApplyFilter(keepSelection: false);
                return;
            }

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
            UpdateDirectoryActions();
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
                                     && IsSameDir(item.Root, snapshot.Current?.Root is { Length: > 0 } currentRoot
                                         ? currentRoot : snapshot.GameDir);
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

    public async Task OpenVersionAsync(string id, string focus = "", string root = "")
    {
        if (_all.Count == 0) await RefreshAsync();
        var version = _all.FirstOrDefault(v => v.Id == id && (root.Length == 0 || string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase)));
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

    /// <summary>排序 / 筛选面板挂到搜索框右侧的漏斗上。排序选择与首页那个小的实例选择列表共享（InstanceSort）。</summary>
    private void AttachListFilterPanel()
    {
        if (ListFilterPanel.Parent is Panel host) host.Children.Remove(ListFilterPanel);
        QueryBox.AttachFilterPanel(ListFilterPanel);
        SyncSortBox();
    }

    private void SyncSortBox()
    {
        var index = InstanceSort.Mode switch
        {
            InstanceSortMode.Name => 1,
            InstanceSortMode.Created => 2,
            _ => 0,
        };
        if (SortBox.SelectedIndex != index) SortBox.SelectedIndex = index;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = ((SortBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "name" => InstanceSortMode.Name,
            "created" => InstanceSortMode.Created,
            _ => InstanceSortMode.Recent,
        };
        if (InstanceSort.Mode == mode) return;
        InstanceSort.Set(mode);
        ApplyFilter(keepSelection: true);
    }

    private void ListFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
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
        var filtering = OnlyRunningCheck.IsChecked == true
                        || OnlyModdedCheck.IsChecked == true
                        || OnlyLoaderCheck.IsChecked == true;
        foreach (var dir in _dirs)
        {
            if (!_versionsByDir.TryGetValue(dir, out var source)) continue;
            var visible = source.Where(v => v.Valid && !v.Hidden);
            if (OnlyRunningCheck.IsChecked == true)
                visible = visible.Where(v => GameSessions.IsRunning(v.Id, v.Root));
            if (OnlyModdedCheck.IsChecked == true)
                visible = visible.Where(v => v.HasMods);
            if (OnlyLoaderCheck.IsChecked == true)
            {
                visible = visible.Where(v => !string.IsNullOrWhiteSpace(v.Loader)
                    && !v.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase));
            }
            if (_query.Length > 0)
            {
                visible = visible.Where(v =>
                    v.Id.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    (v.Loader?.Contains(_query, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            var visibleList = visible.ToList();
            // 「使用时间」沿用目录里手动拖过的顺序；名称 / 创建时间纯粹按字段排。
            var rows = InstanceSort.Mode == InstanceSortMode.Recent
                ? OrderFor(dir, visibleList).ToList()
                : InstanceSort.Apply(visibleList).ToList();
            if (rows.Count == 0 && (_query.Length > 0 || filtering)) continue;
            var group = new VersionGroup(dir,
                _query.Length > 0 || !_collapsedDirs.Contains(dir), rows);
            _groups.Add(group);
            total += rows.Count;
        }

        SetHoveredRow(null);
        VersionList.ItemsSource = _groups.ToArray();

        if (total == 0 && (_groups.Count == 0 || _query.Length > 0 || filtering))
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
            else if (filtering)
            {
                ListEmpty.Glyph = "\uE71C";
                ListEmpty.Title = "没有符合筛选条件的实例";
                ListEmpty.Hint = "在搜索框右边的漏斗里少勾几条，或换个排序试试。";
                EmptyDownloadButton.Visibility = Visibility.Collapsed;
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

    private void SyncDirBox()
    {
        _suppressDirChange = true;
        try
        {
            DirBox.Items.Clear();
            foreach (var dir in _dirs) DirBox.Items.Add(dir);
            var index = _dirs.FindIndex(d => IsSameDir(d, _primaryDir));
            DirBox.SelectedIndex = index;
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
        if (_suppressDirChange || _loading || _changingDefaultDir) return;
        var index = DirBox.SelectedIndex;
        if (index < 0 || index >= _dirs.Count) return;
        var dir = _dirs[index];
        if (IsSameDir(dir, _primaryDir)) return;
        _changingDefaultDir = true;
        UpdateDirectoryActions();
        try
        {
            if (!await App.Backend.SetGameDirAsync(dir))
            {
                SyncDirBox();
                ShowToast(InfoBarSeverity.Warning, "切换默认目录失败", dir);
                return;
            }

            _primaryDir = dir;
            _dirs.RemoveAll(d => IsSameDir(d, dir));
            _dirs.Insert(0, dir);
            SaveDirs();
            await RefreshAsync();
        }
        finally
        {
            _changingDefaultDir = false;
            UpdateDirectoryActions();
        }
    }

    private void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder }) OpenPath(folder);
    }

    private async void FolderDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _changingDefaultDir || sender is not Button { Tag: string dir }
            || IsSameDir(dir, _primaryDir)) return;
        _changingDefaultDir = true;
        try
        {
            if (!await App.Backend.SetGameDirAsync(dir))
            {
                ShowToast(InfoBarSeverity.Error, "切换默认目录失败", dir);
                return;
            }
            _primaryDir = dir;
            _dirs.RemoveAll(d => IsSameDir(d, dir));
            _dirs.Insert(0, dir);
            SaveDirs();
            await RefreshAsync();
        }
        finally { _changingDefaultDir = false; UpdateDirectoryActions(); }
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

        var index = sender is Button { Tag: string folder } ? _dirs.FindIndex(d => IsSameDir(d, folder)) : -1;
        if (index < 0) return;
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
        group.Items.Move(index, target);
        SaveOrder(group.Folder, group.Items.Select(r => r.Id));
    }

    private FrameworkElement? _hoveredRow;
    private Panel? _hoveredActions;

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetHoveredRow(sender as FrameworkElement);

    private void Row_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        SetHoveredRow(sender as FrameworkElement);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || !ReferenceEquals(row, _hoveredRow)) return;
        var position = e.GetCurrentPoint(row).Position;
        if (position.X < 0 || position.Y < 0 || position.X >= row.ActualWidth || position.Y >= row.ActualHeight)
            SetHoveredRow(null);
    }

    private void VersionListScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) =>
        SetHoveredRow(null);

    private void SetHoveredRow(FrameworkElement? row)
    {
        if (ReferenceEquals(_hoveredRow, row)) return;
        var previous = _hoveredActions;
        _hoveredRow = row;
        _hoveredActions = row is null ? null : Descendants(row).OfType<Panel>().FirstOrDefault(p => p.Tag is "move");
        if (previous is not null) UpdateActionVisibility(previous);
        if (_hoveredActions is not null) UpdateActionVisibility(_hoveredActions);
    }

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

    private void MovePanel_FocusChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Panel panel) UpdateActionVisibility(panel);
    }

    private void UpdateActionVisibility(Panel panel)
    {
        // 键盘焦点仍然能看到操作。鼠标点击产生的焦点不能把按钮留在旧行。
        var visible = ReferenceEquals(panel, _hoveredActions)
            || panel.Children.Any(child => child is Control { FocusState: FocusState.Keyboard });
        panel.Opacity = visible ? 1 : 0;
        panel.IsHitTestVisible = visible;
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

    private void UpdateDirectoryActions()
    {
        var ready = !_loading && !_changingDefaultDir;
        DirBox.IsEnabled = ready && _dirs.Count > 1;
        AddDirButton.IsEnabled = ready;
        RemoveDirButton.IsEnabled = ready && _dirs.Count > 1;
        RescanButton.IsEnabled = ready;
    }

    private void SetListState(ListState state)
    {
        var changed = _listState != state;
        _listState = state;
        LoadingPanel.Visibility = state == ListState.Loading ? Visibility.Visible : Visibility.Collapsed;
        VersionListScroll.Visibility = state == ListState.List ? Visibility.Visible : Visibility.Collapsed;
        UpdateDirectoryActions();
        EmptyPanel.Visibility = state == ListState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == ListState.Error ? Visibility.Visible : Visibility.Collapsed;
        if (changed && IsLoaded)
        {
            FrameworkElement panel = state switch
            {
                ListState.List => VersionListScroll,
                ListState.Empty => EmptyPanel,
                ListState.Error => ErrorPanel,
                _ => LoadingPanel,
            };
            Motion.Enter(panel, fromY: 5, durationMs: 150);
        }
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
    private void VersionGroup_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_query.Length == 0 && sender.IsLoaded && sender.Tag is string root)
            _collapsedDirs.Remove(root);
        if (sender.IsLoaded && sender.Content is FrameworkElement list)
            Motion.Enter(list, fromY: -5, durationMs: 170);
    }

    private void VersionGroup_Collapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        SetHoveredRow(null);
        if (_query.Length == 0 && sender.IsLoaded && sender.Tag is string root)
            _collapsedDirs.Add(root);
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
        var revealing = ListPanel.Visibility != Visibility.Visible;
        _selected = null;
        _settingsDirty = false;
        _saveTimer.Stop();
        SetHoveredRow(null);
        ListPanel.Visibility = Visibility.Visible;
        DetailView.Visibility = Visibility.Collapsed;
        UpdateCrumb(null);
        if (revealing) Motion.Enter(ListPanel, fromY: 6, durationMs: 175);
    }

    private void ShowDetails(VersionInfo? version)
    {
        if (version is null)
        {
            ShowList();
            return;
        }

        var revealing = DetailView.Visibility != Visibility.Visible || !InstanceSelection.Same(_selected, version);
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
        SetHoveredRow(null);
        UpdateCrumb(version);
        if (revealing) Motion.Enter(DetailView, fromY: 8, durationMs: 190);

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
        UpdateDetailPath();
        DetailCreated.Text = Directory.Exists(version.Path)
            ? Directory.GetCreationTime(version.Path).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
        DetailLastPlayed.Text = version.AgoText.Replace(" ", "");
        DetailLaunch.IsEnabled = !_launching;
        UpdateLaunchButtonState(version.Id);
        DetailSwitch.Visibility = version.IsCurrent ? Visibility.Collapsed : Visibility.Visible;
        DetailSwitch.IsEnabled = true;
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
        DirBox.Visibility = Visibility.Collapsed;
    }

    private void BackToInstance_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.Navigate("instance");

    private void CrumbHome_Tapped(object sender, TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");

    private void CrumbRoot_Tapped(object sender, TappedRoutedEventArgs e) => ShowList();

    private string _lastIsolationTag = "follow";

    /// <summary>实例路径 = 实际生效的游戏目录（隔离开：versions/<id>；关：.minecraft 根）。</summary>
    private void UpdateDetailPath()
    {
        if (_selected is null) return;
        var root = _selected.Root;
        DetailPath.Text = string.IsNullOrWhiteSpace(root)
            ? _selected.Path
            : _detailIsolated
                ? System.IO.Path.Combine(root, "versions", _selected.Id)
                : root;
    }

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
        if (_selected is { } selected && GameSessions.IsRunning(selected.Id, selected.Root))
        {
            _suppressInstanceSettings = true;
            SelectIsolation(_lastIsolationTag);
            _suppressInstanceSettings = false;
            ShowToast(InfoBarSeverity.Warning, "实例正在运行", "结束游戏后再迁移版本隔离内容。");
            return;
        }

        // 切换隔离会改变存档/Mod/配置实际读写的目录，先确认；取消就回滚下拉。
        var previous = _lastIsolationTag;
        var next = SelectedIsolation();
        if (next != previous)
        {
            var dialog = new ContentDialog
            {
                Title = next == "on" ? "开启版本隔离？" : next == "off" ? "关闭版本隔离？" : "跟随全局版本隔离？",
                Content = next == "on"
                    ? @"此实例内容将会迁移至 .minecraft\versions\ 下，与非版本隔离的实例分离，无法共享存档和设置。"
                    : next == "off"
                        ? "此实例内容将会迁移至 .minecraft 根目录，迁移存档并与非版本隔离的实例共享，但无法迁移设置。"
                        : "将按全局设置决定此实例的游戏目录，并迁移相应内容。",
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
            InstanceMemAutoSwitch.IsOn = settings.OverrideMemAuto;

            _instanceJavaPath = settings.JavaPath ?? "";
            UpdateInstanceJavaChoices();
            InstanceJvmBox.Text = settings.Jvm;
            InstanceJvmAutoSwitch.IsOn = settings.JvmAuto;
            _loadedJvmAuto = settings.JvmAuto;
            UpdateJvmAutoHint(settings.JvmAuto);
            InstanceGlobalJvm.Text = string.IsNullOrWhiteSpace(settings.GlobalJvm) ? "默认参数" : settings.GlobalJvm;
            SelectIsolation(settings.Isolation);
            _lastIsolationTag = SelectedIsolation();
            InstanceIsolationHint.Text = settings.Isolated
                ? "当前：已隔离"
                : "当前：未隔离";
            _detailIsolated = settings.Isolated;
            UpdateDetailPath();
            InstanceSettingsStatus.Text = settings.Ok
                ? (settings.UseGlobal ? "当前使用全局设置" : "当前使用独立设置")
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
            var extra = await App.Backend.GetLaunchExtraAsync(versionId, version.Root);
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
            prefs.HomeCovers[InstanceSelection.Key(_selected!.Root, id)] = file.Path;
            prefs.HomeCoverModes[InstanceSelection.Key(_selected.Root, id)] = "custom";
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
        if (_selected is not { } version || App.MainWindow?.Preferences is not { } prefs) return;
        prefs.HomeCoverModes[InstanceSelection.Key(version.Root, version.Id)] = "default";
        AppPreferencesStore.Save(prefs);
        UpdateCoverHint();
    }

    private bool _updatingCover;
    private void UpdateCoverHint()
    {
        if (_selected is not { } version || App.MainWindow?.Preferences is not { } prefs) return;
        var key = InstanceSelection.Key(version.Root, version.Id);
        var has = prefs.HomeCovers.ContainsKey(key) || prefs.HomeCovers.ContainsKey(version.Id);
        var mode = prefs.HomeCoverModes.GetValueOrDefault(key, has ? "custom" : "default");
        _updatingCover = true;
        InstanceCoverMode.SelectedIndex = mode == "custom" ? 2 : mode == "latest" ? 1 : 0;
        _updatingCover = false;
        InstanceCoverHint.Text = "";
        InstanceCoverPickButton.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        InstanceCoverResetButton.Visibility = Visibility.Collapsed;
    }

    private async void InstanceCoverMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCover || _suppressInstanceSettings || _selected is not { } version
            || App.MainWindow?.Preferences is not { } prefs
            || InstanceCoverMode.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        var key = InstanceSelection.Key(version.Root, version.Id);
        if (mode == "custom" && !prefs.HomeCovers.ContainsKey(key) && !prefs.HomeCovers.ContainsKey(version.Id))
        {
            await PickInstanceCoverAsync();
            UpdateCoverHint();
            return;
        }
        prefs.HomeCoverModes[key] = mode;
        AppPreferencesStore.Save(prefs);
        UpdateCoverHint();
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
    /// <summary>「手动分配内存」这一行只在关掉自动分配（或跟随全局给出只读摘要）时出现。</summary>
    private void SyncManualMemRow()
    {
        if (InstanceManualMemRow is null) return;
        var show = (InstanceMemoryBox?.Visibility == Visibility.Visible)
                || (InstanceMemorySummary?.Visibility == Visibility.Visible);
        InstanceManualMemRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

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
        var legacyAutomatic = !useGlobal && InstanceMemAutoSwitch.IsOn;
        var readOnly = useGlobal || legacyAutomatic;
        InstanceMemorySummary.Visibility = Visibility.Collapsed;
        var alloc = InstanceMemoryBox.Value;
        if (readOnly)
        {
            var (summary, allocation) = ReadOnlyMemoryState(useGlobal);
            InstanceMemorySummary.Text = summary;
            InstanceMemorySummary.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncManualMemRow();
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

        var running = _selected is not null && GameSessions.IsRunning(_selected.Id, _selected.Root);
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
        if (!(useGlobal ? _globalMemAuto : InstanceMemAutoSwitch.IsOn))
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

    private void InstanceJvmAutoSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateJvmAutoHint(InstanceJvmAutoSwitch.IsOn);
        if (_suppressInstanceSettings) return;
        ScheduleInstanceSave();
    }

    private void InstanceMemAuto_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInstanceSettings || !_instanceSettingsReady) return;
        SetInstanceSettingsEnabled();
        UpdateMemoryBar();
        ScheduleInstanceSave();
    }

    private void UpdateJvmAutoHint(bool on)
    {
        if (InstanceJvmAutoHint is null) return;
        InstanceJvmAutoHint.Text = "";
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
        var editor = Visibility.Visible;
        var summary = Visibility.Collapsed;
        InstanceMemoryBox.IsEnabled = editable && !InstanceMemAutoSwitch.IsOn;
        InstanceJvmBox.IsEnabled = editable;
        InstanceJvmAutoSwitch.IsEnabled = editable;
        InstanceMemAutoSwitch.IsEnabled = editable;
        InstanceAutoApplyLabel.Visibility = Visibility.Collapsed;
        InstanceMemoryBox.Visibility = editable && !InstanceMemAutoSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SyncManualMemRow();
        InstanceJvmBox.Visibility = editor;
        InstanceJvmAutoSwitch.Visibility = editor;
        InstanceJvmAutoHint.Visibility = Visibility.Collapsed;
        // 跟随全局时这一行没有可操作项，整行收起，别留一个孤零零的标签。
        InstanceJvmAutoLabel.Visibility = editor;
        InstanceJavaBox.Visibility = editor;
        InstanceJavaBox.IsEnabled = editable;
        InstanceGlobalJava.Visibility = summary;
        InstanceGlobalJvm.Visibility = summary;
        InstanceGlobalExtra.Visibility = summary;
        ExtraPanel.Visibility = editor;
        ExtraPanel.IsEnabled = editable && _extraReady;
        DimBelowGlobalRow(inherited);
    }

    /// <summary>
    /// 「跟随全局设置」是它下面所有设置的总开关：开启时把后面这些卡整体压暗并禁掉交互，
    /// 而不是把它们藏起来——藏了用户不知道自己在跟随什么。
    /// </summary>
    private void DimBelowGlobalRow(bool dim)
    {
        if (DetailPanel is null || LaunchSettingsSourceRow is null) return;
        var start = DetailPanel.Children.IndexOf(LaunchSettingsSourceRow);
        if (start < 0) return;
        for (var i = start + 1; i < DetailPanel.Children.Count; i++)
        {
            if (DetailPanel.Children[i] is not UIElement el) continue;
            el.Opacity = dim ? 0.45 : 1.0;
            el.IsHitTestVisible = !dim;
        }
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
            var result = await App.Backend.SetLaunchExtraAsync(version.Id, ExtraPanel.Read(), version.Root);
            if (!result.Ok) InstanceSettingsStatus.Text = result.Message;
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
            && InstanceMemAutoSwitch.IsOn == _loadedMemAuto
            && InstanceUseGlobalSwitch.IsOn == _loadedUseGlobal
            && string.Equals(_instanceJavaPath, _loadedJava, StringComparison.Ordinal)
            && string.Equals(InstanceJvmBox.Text ?? "", _loadedJvm, StringComparison.Ordinal)
            && InstanceJvmAutoSwitch.IsOn == _loadedJvmAuto
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
            var savedMemAuto = InstanceMemAutoSwitch.IsOn;
            var savedJava = _instanceJavaPath;
            var savedJvm = InstanceJvmBox.Text ?? "";
            var savedJvmAuto = InstanceJvmAutoSwitch.IsOn;
            var savedIsolation = SelectedIsolation();
            var result = await App.Backend.SetInstanceLaunchSettingsAsync(
                version.Id, (int)memory, savedUseGlobal,
                savedMemAuto,
                savedJava,
                savedJvm,
                savedIsolation,
                version.Root,
                savedJvmAuto);
            if (!ReferenceEquals(_selected, version)) return;
            if (result.Ok)
            {
                // 写成功后把「已加载值」更新为刚写的值，否则改过去再改回原值会被当成没变化而不写盘。
                _loadedMemory = memory;
                _loadedMemAuto = savedMemAuto;
                _loadedUseGlobal = savedUseGlobal;
                _loadedJava = savedJava;
                _loadedJvm = savedJvm;
                _loadedJvmAuto = savedJvmAuto;
                _loadedIsolation = savedIsolation;
                _loadedStatus = savedUseGlobal ? "当前使用全局设置" : "当前使用独立设置";
                try
                {
                    var fresh = await App.Backend.GetInstanceLaunchSettingsAsync(version.Id, version.Root);
                    if (ReferenceEquals(_selected, version) && fresh.Ok)
                    {
                        _globalMemoryMb = fresh.GlobalMem;
                        _globalMemAuto = fresh.GlobalMemAuto;
                        UpdateMemoryBar();
                        InstanceIsolationHint.Text = fresh.Isolated
                            ? "当前：已隔离"
                            : "当前：未隔离";
                    }
                }
                catch { }
            }
            if (!ReferenceEquals(_selected, version)) return;
            InstanceSettingsStatus.Text = _settingsDirty ? "正在应用…"
                : result.Ok ? _loadedStatus : ClipText(result.Message, 80);
            if (!result.Ok)
            {
                _suppressInstanceSettings = true;
                SelectIsolation(_loadedIsolation);
                _lastIsolationTag = _loadedIsolation;
                _suppressInstanceSettings = false;
                ShowToast(InfoBarSeverity.Warning, "设置没有写入", result.Message);
            }
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
        var running = GameSessions.IsRunning(versionId, _selected?.Root ?? "");
        LaunchLabel.Text = running ? "结束游戏" : "启动该版本";
        LaunchGlyph.Glyph = running ? "\uE71A" : "\uE768";
        AutomationProperties.SetName(DetailLaunch, running ? "结束这个实例的游戏" : "启动该版本");
    }

    private async Task StopGameAsync(string versionId)
    {
        var row = GameSessions.Find(versionId, _selected?.Root ?? "");
        var result = await App.Backend.StopGameAsync(row?.Pid ?? 0, versionId);
        ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Ok ? "已结束游戏" : "结束失败", result.Message);
        await GameSessions.RefreshAsync();
        UpdateLaunchButtonState(versionId);
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _launching) return;
        if (GameSessions.IsRunning(_selected.Id, _selected.Root))
        {
            await StopGameAsync(_selected.Id);
            return;
        }

        var target = _selected;
        _launching = true;
        SetLaunchBusy(true);
        try
        {
            await FlushInstanceSettingsAsync();
            await FlushExtraAsync();
            var result = await App.MainWindow.LaunchInstanceAsync(target);
            if (!result.Ok) ShowToast(InfoBarSeverity.Warning, "启动未完成", result.Message);
        }
        catch (Exception ex) { ShowToast(InfoBarSeverity.Error, "启动失败", Diagnostics.FriendlyMessage(ex)); }
        finally { _launching = false; SetLaunchBusy(false); }
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
        if (_selected is not { } target || sender is not Button button) return;
        var main = App.MainWindow;
        var wasCurrent = target.IsCurrent || InstanceSelection.Same(target, InstanceSelection.Resolve(main.Snapshot, main.Preferences));
        button.IsEnabled = false;
        try
        {
            var result = await App.Backend.HideVersionAsync(target.Id, target.Root);
            if (!result.Ok)
            {
                ShowToast(InfoBarSeverity.Warning, "不能隐藏", result.Message);
                return;
            }
            var next = wasCurrent ? await SelectReplacementAsync(target) : null;
            ShowList();
            await main.RefreshShellAsync(force: true);
            await RefreshAsync();
            ShowToast(InfoBarSeverity.Success, "已隐藏", next is null ? result.Message : $"已选中 {next.Id}");
        }
        finally { button.IsEnabled = true; }
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

    private sealed class LoaderEditorHost(ContentDialog dialog) : ISubPageHost
    {
        public void CloseSubPage() => dialog.Hide();
        public void OpenSubPage(Type page, object args, params string[] crumbs) { }
    }

    private void ChangeVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } target || _launching) return;
        if (GameSessions.IsRunning(target.Id, target.Root))
        {
            ShowToast(InfoBarSeverity.Warning, "实例正在运行", "请结束游戏后修改加载器。");
            return;
        }
        var frame = new Frame { Width = 780, Height = 540 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"修改「{target.Id}」的加载器", Content = frame,
            CloseButtonText = "返回", RequestedTheme = ActualTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860d;
        var host = new LoaderEditorHost(dialog);
        var version = new MojangVersionInfo { Id = target.GameVersion, Type = target.Type };
        frame.Navigate(typeof(DownloadDetailPage), new DownloadDetailArgs(version, host,
            (kind, loaderVersion, api, folder, name) => _ = ApplyInstanceLoaderAsync(target, kind, loaderVersion, api), target));
        _ = dialog.ShowAsync();
    }

    private async Task ApplyInstanceLoaderAsync(VersionInfo target, string kind, string loaderVersion, DownloadApiSelection? api)
    {
        var task = TaskCenter.Add($"修改加载器 {target.Id}", "保留实例内容", "download", targetIcon: InstanceIcon.ForLoader(kind));
        try
        {
            var result = await App.Backend.InstallInstanceLoaderAsync(target.Id, kind, loaderVersion, target.Root,
                api?.ProjectId ?? "", api?.VersionId ?? "", new Progress<string>(line => TaskCenter.Report(task, line)), task.Id);
            if (!result.Ok) { TaskCenter.Fail(task, result.Message, result.Failure); return; }
            TaskCenter.Complete(task, result.Message);
            await App.MainWindow.RefreshShellAsync(force: true);
            await RefreshAsync();
            var changed = _all.FirstOrDefault(v => InstanceSelection.Same(v, target));
            if (changed is not null) ShowDetails(changed);
        }
        catch (Exception ex) { TaskCenter.Fail(task, ex.Message); }
    }

    private async void ArchiveVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } target || _launching || sender is not Button button) return;
        if (GameSessions.IsRunning(target.Id, target.Root))
        {
            ShowToast(InfoBarSeverity.Warning, "实例正在运行", "结束游戏后再归档。");
            return;
        }

        var main = App.MainWindow;
        var prefs = main?.Preferences;
        var wasCurrent = target.IsCurrent || InstanceSelection.Same(target, InstanceSelection.Resolve(main?.Snapshot, prefs));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "归档实例",
            Content = wasCurrent
                ? $"将「{target.Id}」移到回收目录，并自动选中下一个可用实例。"
                : $"将「{target.Id}」移到回收目录。",
            PrimaryButtonText = "归档",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        button.IsEnabled = false;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await App.Backend.DeleteVersionAsync(target.Id, target.Root);
            if (!result.Ok)
            {
                ShowToast(InfoBarSeverity.Error, "归档失败", result.Message);
                return;
            }

            var next = wasCurrent && main is not null ? await SelectReplacementAsync(target) : null;

            ShowList();
            if (main is not null) await main.RefreshShellAsync(force: true);
            await RefreshAsync();
            ShowToast(InfoBarSeverity.Success, "已归档", next is null
                ? result.Message
                : $"已选中 {next.Id}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task<VersionInfo?> SelectReplacementAsync(VersionInfo target)
    {
        var prefs = App.MainWindow.Preferences;
        prefs.HomeSelectedInstance = "";
        prefs.HomeSelectedRoot = "";
        AppPreferencesStore.Save(prefs);

        var ordered = _dirs.SelectMany(dir => _versionsByDir.TryGetValue(dir, out var versions)
            ? OrderFor(dir, versions.Where(v => v.Valid && !v.Hidden).ToList()) : []).ToList();
        var index = ordered.FindIndex(v => InstanceSelection.Same(v, target));
        foreach (var candidate in ordered.Skip(index + 1).Concat(ordered.Take(index)))
        {
            if (!Directory.Exists(candidate.Path)) continue;
            var (ok, _) = await InstanceSelection.SelectAsync(candidate);
            if (ok) return candidate;
        }
        return null;
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

    private void OpenGameDir_Click(object sender, RoutedEventArgs e) => OpenPath(_primaryDir);

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
    private void ShowLaunchFailure(string message, string versionId, FailureInfo failure)
    {
        ShowToast(InfoBarSeverity.Error, $"启动失败 · {failure.Code}", message, "复制诊断",
            () => Diagnostics.Copy(Diagnostics.BuildReport(failure, "启动游戏", message, versionId)), 20);
    }
}

/// <summary>列表行：名称 + 一行副信息（版本 · 加载器 · Mod 数 · 最近游玩）。</summary>
public sealed class VersionRow(VersionInfo source, string root)
{
    public VersionInfo Source { get; } = source;
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
public sealed class VersionGroup(string folder, bool isExpanded, IEnumerable<VersionInfo> versions)
{
    public string Folder { get; } = folder;
    public string Title => DirectoryTitle(Folder);
    public bool IsExpanded { get; } = isExpanded;
    public ObservableCollection<VersionRow> Items { get; } = new(versions.Select(v => new VersionRow(v, folder)));
    public string CountText => $"{Items.Count} 个实例";
    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string DefaultText => string.Equals(GameDirectoryResolver.Normalize(Folder),
        GameDirectoryResolver.Normalize(App.MainWindow?.Preferences.McDir), StringComparison.OrdinalIgnoreCase)
        ? "当前默认目录" : "设为默认";

    private static string DirectoryTitle(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(normalized);
        if (string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(normalized));
            if (!string.IsNullOrEmpty(parent)) return parent;
        }
        return string.IsNullOrEmpty(name) ? path : name;
    }
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
