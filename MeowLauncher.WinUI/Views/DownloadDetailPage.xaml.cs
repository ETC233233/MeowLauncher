using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MeowLauncher.WinUI.Views;

/// <summary>打开下载子页要带的东西：版本、宿主（关闭用）、开始安装的回调（由列表页负责装，列表页一直活着）。</summary>
public sealed record DownloadDetailArgs(MojangVersionInfo Version, ISubPageHost Host,
    Action<string, string, DownloadApiSelection?, string, string> StartInstall, VersionInfo? Target = null);

public sealed record DownloadApiSelection(string ProjectId, string VersionId);

/// <summary>
/// 「获取 › 下载版本 › x.y.z」子页：选加载器（整行列表）+ 底部固定「开始下载」。
/// 页头（返回箭头 + 面包屑）由「获取」宿主画。
/// </summary>
public sealed partial class DownloadDetailPage : Page
{
    /// <summary>安装页里的一行：原版 / Fabric / Forge / NeoForge / Quilt。</summary>
    private sealed class LoaderRow : INotifyPropertyChanged
    {
        private string _base;
        private string? _extra;
        private bool _enabled;
        private bool _selected;
        private bool _installed;
        private bool _failed;
        private string _installedNote = "";

        public LoaderRow(string kind, string name, string status, bool enabled)
        {
            Kind = kind;
            Name = name;
            Icon = InstanceIcon.ForLoader(kind);
            _base = status;
            _enabled = enabled;
        }

        public string Kind { get; }
        public string Name { get; }
        public Microsoft.UI.Xaml.Media.ImageSource Icon { get; }
        public bool Visible { get; set; }
        // 已安装就一定写「已安装」（版本号拿不到时只写这两个字），不再退回「可以添加 · N 个版本」。
        public string Status => _failed
            ? _base
            : _extra ?? (_installed ? (_installedNote.Length > 0 ? $"已安装 · {_installedNote}" : "已安装") : _base);
        public Visibility ChevronVisibility => _installed ? Visibility.Collapsed : Visibility.Visible;
        public bool Enabled => _enabled;
        public double RowOpacity => _enabled ? 1.0 : 0.55;
        public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InstalledVisibility => _installed ? Visibility.Visible : Visibility.Collapsed;
        public override string ToString() => Name;

        public bool Selected { get => _selected; set { _selected = value; Raise(nameof(SelectedVisibility)); } }
        public bool Installed
        {
            get => _installed;
            set
            {
                _installed = value;
                Raise(nameof(InstalledVisibility));
                Raise(nameof(ChevronVisibility));
                Raise(nameof(Status));
            }
        }

        /// <summary>已安装的具体加载器版本（如 0.15.11），显示在状态行里。</summary>
        public string InstalledNote { get => _installedNote; set { _installedNote = value; Raise(nameof(Status)); } }

        /// <summary>查询失败（超时/网络）：状态栏改成「版本查询超时，请重试」，不再显示「已安装」。</summary>
        public bool Failed
        {
            get => _failed;
            set
            {
                if (_failed == value) return;
                _failed = value;
                Raise(nameof(Status));
            }
        }

        public void SetBase(string status, bool enabled)
        {
            _base = status;
            _enabled = enabled;
            Raise(nameof(Status));
            Raise(nameof(Enabled));
            Raise(nameof(RowOpacity));
        }

        public void SetExtra(string? extra)
        {
            if (_extra == extra) return;
            _extra = extra;
            Raise(nameof(Status));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class LoaderChoice
    {
        public LoaderChoice(string version, bool recommended) { Version = version; Recommended = recommended; }
        public string Version { get; }
        public bool Recommended { get; }
        public override string ToString() => Recommended ? $"{Version}（推荐）" : Version;
    }

    private sealed record ApiChoice(string ProjectId, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly ApiChoice FabricApi = new("P7dR8mSH", "Fabric API");
    private static readonly ApiChoice QuiltedApi = new("qvIfYCYJ", "Quilted Fabric API (QFAPI) / Quilt Standard Libraries (QSL)");
    private static readonly ApiChoice NoApi = new("", "不安装");

    private string _apiLoader = "";
    private int _apiRequest;
    private string _appliedLoader = "";

    private static readonly Dictionary<string, LoaderSupportResult> _supportCache = new(StringComparer.Ordinal);

    private DownloadDetailArgs? _args;
    private MojangVersionInfo? _version;
    private LoaderSupportResult? _support;

    /// <summary>每个加载器单独回来的结果：不能等四种都查完再放行版本下拉，否则先回来的 Forge 会被判成「版本还没选到」。</summary>
    private readonly Dictionary<string, LoaderKindInfo> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoaderRow> _loaderRows = [];
    // 列表只绑一次，逐行回来时只改行内文字：重建 ItemsSource 会让整块列表闪一下，
    // 四种加载器各自异步返回时就是连续闪四次。
    private readonly ObservableCollection<LoaderRow> _visibleRows = new();
    private string _selKind = "vanilla";
    private string _suggestedName = "";
    private bool _settingName;

    public DownloadDetailPage()
    {
        InitializeComponent();
        DetailLoaderList.ItemsSource = _visibleRows;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not DownloadDetailArgs args) return;
        _args = args;
        var v = _version = args.Version;

        DetailTitle.Text = v.Id;
        _settingName = true;
        InstanceName.Text = _suggestedName = v.Id;
        _settingName = false;
        DetailMeta.Text = $"{v.TypeText} · {v.DateText}";
        _selKind = "vanilla";
        _appliedLoader = "";
        ConfigureApi("");
        _loaderRows.Clear();
        var dirs = GameDirectoryResolver.Resolve(App.MainWindow?.Preferences ?? AppPreferencesStore.Load());
        DetailDir.ItemsSource = dirs;
        DetailDir.SelectedIndex = dirs.Count > 0 ? 0 : -1;
        if (args.Target is { } target)
        {
            if (!dirs.Contains(target.Root)) dirs.Add(target.Root);
            DetailDir.ItemsSource = dirs;
            DetailDir.SelectedItem = target.Root;
            DetailDir.IsEnabled = false;
            InstanceName.Text = target.Id;
            InstanceName.IsReadOnly = true;
            DetailInstall.Content = "应用加载器";
        }

        SetLoaderNote("");
        _loaderRows.Add(new LoaderRow("vanilla", "原版", "不带 Mod 加载器", true) { Visible = true });
        foreach (var kind in new[] { "fabric", "forge", "neoforge", "quilt" })
            _loaderRows.Add(new LoaderRow(kind, LoaderLabel(kind), "正在查询…", false) { Visible = true });
        RefreshLoaderList();

        DetailAncientNote.Visibility = v.Type is "old_beta" or "old_alpha" ? Visibility.Visible : Visibility.Collapsed;
        ApplyLoaderSelection();
        _ = RefreshInstalledKindsAsync(v);
        await LoadSupportAsync(v);
    }

    private void RefreshLoaderList() =>
        SyncRows(_visibleRows, _loaderRows.Where(r => r.Visible).ToList());

    /// <summary>原地对齐：多删、缺插、错位移动，已存在的行对象原样保留（只更新文字）。</summary>
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

    private static string LoaderLabel(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "原版",
    };

    private string SelectedInstallDir => DetailDir.SelectedItem as string ?? "";

    private async void DetailDir_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_version is { } version && _loaderRows.Count > 0)
            await RefreshInstalledKindsAsync(version);
    }

    private async Task RefreshInstalledKindsAsync(MojangVersionInfo version)
    {
        var folder = SelectedInstallDir;
        foreach (var row in _loaderRows)
        {
            row.InstalledNote = "";
            row.Installed = false;
        }
        UpdateSummary();
        if (folder.Length == 0) return;

        try
        {
            var snap = await App.Backend.ScanDirAsync(folder);
            if (snap is null || _version != version || SelectedInstallDir != folder) return;
            var versions = snap.Versions.Where(v => v.Valid && string.Equals(
                v.GameVersion, version.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var row in _loaderRows)
            {
                var installed = row.Kind == "vanilla"
                    ? versions.FirstOrDefault(v => string.Equals(v.Id, version.Id, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(v.Loader))
                    : versions.FirstOrDefault(v => row.Kind == KindOfLoader(v.Loader));
                row.InstalledNote = installed?.LoaderVersion?.Trim() ?? "";
                row.Installed = installed is not null;
            }
        }
        catch
        {
            // 已安装标记只用于展示，实际安装前会再次检查目标文件夹。
        }
    }

    private static string KindOfLoader(string? raw)
    {
        var loader = raw ?? "";
        foreach (var kind in new[] { "neoforge", "fabric", "quilt", "forge" })
            if (loader.Contains(kind, StringComparison.OrdinalIgnoreCase)) return kind;
        return "";
    }

    private bool AllLoadersOk(LoaderSupportResult support) => FailedLoaders(support).Count == 0;

    /// <summary>版本列表没查到的加载器名（网络 / 超时）。已安装的行状态栏写的是「已安装」，失败只能靠底部这行说明。</summary>
    private List<string> FailedLoaders(LoaderSupportResult support) =>
        _loaderRows.Skip(1)
            .Where(r => support.Kinds.TryGetValue(r.Kind, out var k) && !k.Ok)
            .Select(r => r.Name)
            .ToList();

    /// <summary>四种加载器各自一个请求，谁先回来先显示谁；单个失败只影响它自己的行。</summary>
    private async Task LoadSupportAsync(MojangVersionInfo v)
    {
        DetailLoaderRetry.Visibility = Visibility.Collapsed;
        _kinds.Clear();
        var kinds = new Dictionary<string, LoaderKindInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _loaderRows.Skip(1))
        {
            row.Failed = false;
            row.Visible = true;
            row.SetBase("正在查询…", false);
        }
        RefreshLoaderList();
        ApplyLoaderSelection();
        SetLoaderNote("");

        if (_supportCache.TryGetValue(v.Id, out var cached))
        {
            foreach (var kv in cached.Kinds)
            {
                kinds[kv.Key] = kv.Value;
                _kinds[kv.Key] = kv.Value;
            }
            foreach (var row in _loaderRows.Skip(1))
                ApplyKindToRow(row, kinds.TryGetValue(row.Kind, out var info) ? info : null);
            _support = cached;
            RefreshLoaderList();
            ApplyLoaderSelection();
            RefreshLoaderNote();
            return;
        }

        var jobs = _loaderRows.Skip(1).Select(row => LoadKindAsync(v, row, kinds)).ToArray();
        await Task.WhenAll(jobs);
        if (_version is null || _version.Id != v.Id) return;

        _support = new LoaderSupportResult(true, "", kinds);
        // 查清楚了但确实没有可用版本的加载器才收起；失败的留着让用户重试。
        foreach (var row in _loaderRows.Skip(1))
            if (kinds.TryGetValue(row.Kind, out var info) && info.Ok && info.Versions.Count == 0)
                row.Visible = false;
        if (kinds.Count > 0 && kinds.Values.All(k => k.Ok))
            _supportCache[v.Id] = _support;

        RefreshLoaderList();
        ApplyLoaderSelection();
        RefreshLoaderNote();
    }

    private async Task LoadKindAsync(MojangVersionInfo v, LoaderRow row, Dictionary<string, LoaderKindInfo> kinds)
    {
        var info = await App.Backend.GetLoaderKindAsync(v.Id, row.Kind);
        if (_version is null || _version.Id != v.Id) return;
        kinds[row.Kind] = info;
        _kinds[row.Kind] = info;
        ApplyKindToRow(row, info);
        RefreshLoaderList();
        if (row.Kind == _selKind) ApplyLoaderSelection();
    }

    /// <summary>把单个加载器的查询结果写到它那一行。</summary>
    private void ApplyKindToRow(LoaderRow row, LoaderKindInfo? info)
    {
        if (info is null)
        {
            row.Failed = true;
            row.SetBase("版本查询超时，请重试", true);
            row.Visible = true;
            return;
        }
        if (!info.Ok)
        {
            row.Failed = true;
            row.SetBase("版本查询超时，请重试", true);
            row.Visible = true;
        }
        else if (info.Versions.Count > 0)
        {
            row.Failed = false;
            row.SetBase($"可以添加 · {info.Versions.Count} 个版本", true);
            row.Visible = true;
        }
        else
        {
            row.Failed = false;
            row.SetBase("无可用版本", false);
        }
    }

    private void SetLoaderNote(string text, bool busy = false, bool retry = false)
    {
        DetailLoaderNote.Text = text;
        DetailLoaderNoteRow.Visibility = text.Length > 0 || busy || retry ? Visibility.Visible : Visibility.Collapsed;
        DetailLoaderBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DetailLoaderBusy.IsActive = busy;
        DetailLoaderRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>列表下方那一行说明：由当前查询结果决定，选中行的失败提示在 ApplyLoaderSelection 里覆盖它。</summary>
    private void RefreshLoaderNote()
    {
        if (_support is null) return;
        if (!_support.Ok)
            SetLoaderNote("读取加载器清单失败，暂时只能装原版。", retry: true);
        else if (FailedLoaders(_support) is { Count: > 0 })
            SetLoaderNote("加载失败的加载器可以点它那一行重试。", retry: true);
        else if (!_loaderRows.Skip(1).Any(r => r.Visible))
            SetLoaderNote("此版本没有可用的加载器，只能安装原版。");
        else
            SetLoaderNote("");
    }

    private async Task RetryKindAsync(MojangVersionInfo v, LoaderRow row)
    {
        _supportCache.Remove(v.Id);
        var kinds = _support is null
            ? new Dictionary<string, LoaderKindInfo>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LoaderKindInfo>(_support.Kinds, StringComparer.OrdinalIgnoreCase);
        await LoadKindAsync(v, row, kinds);
        if (_version is null || _version.Id != v.Id) return;
        _support = new LoaderSupportResult(true, "", kinds);
        RefreshLoaderList();
        ApplyLoaderSelection();
        RefreshLoaderNote();
    }

    private void DetailLoaderRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_version is null) return;
        _supportCache.Remove(_version.Id);
        _ = LoadSupportAsync(_version);
    }

    private void DetailLoaderVersion_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void DetailLoader_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LoaderRow row) return;
        // 查询失败的行：点一下只重查这一个加载器。
        if (row.Failed && _version is not null)
        {
            row.SetBase("正在查询…", false);
            row.Failed = false;
            _ = RetryKindAsync(_version, row);
            return;
        }
        if (!row.Enabled) return;
        _selKind = row.Kind;
        ApplyLoaderSelection();
    }

    /// <summary>按当前选中的加载器刷新行高亮、版本下拉、前置勾选和概要。</summary>
    private void ApplyLoaderSelection()
    {
        if (_version is null) return;
        foreach (var r in _loaderRows)
        {
            r.Selected = r.Kind == _selKind;
            r.SetExtra(null);
        }
        RefreshLoaderNote();

        var kind = _selKind;
        var changed = kind != _appliedLoader;
        var previousVersion = changed ? null : (DetailLoaderVersion.SelectedItem as LoaderChoice)?.Version;
        _appliedLoader = kind;
        DetailIconBrush.ImageSource = _loaderRows.FirstOrDefault(row => row.Kind == kind)?.Icon;
        if (changed && IsLoaded) Motion.Enter(DetailIcon, fromY: 4, durationMs: 155);
        DetailLoaderVersion.ItemsSource = null;

        if (kind == "vanilla")
        {
            DetailLoaderVersionCard.Visibility = Visibility.Collapsed;
            ConfigureApi("");
            UpdateSummary();
            return;
        }

        DetailLoaderVersionLabel.Text = $"{LoaderLabel(kind)} 版本";
        LoaderKindInfo? info = _kinds.TryGetValue(kind, out var live)
            ? live
            : (_support is not null && _support.Kinds.TryGetValue(kind, out var i) ? i : null);
        if (info is null || !info.Ok || info.Versions.Count == 0)
        {
            DetailLoaderVersionCard.Visibility = Visibility.Collapsed;
            DetailLoaderVersion.IsEnabled = false;
            ConfigureApi("");
            if (info is not null && !info.Ok)
                SetLoaderNote($"{LoaderLabel(kind)} 的版本清单没有读到（{ShortReason(info.Message)}）。", retry: true);
            UpdateSummary();
            return;
        }

        var choices = info.Versions
            .Select(v => new LoaderChoice(v, v == info.Recommended))
            .ToList();
        var showingOptions = changed || DetailLoaderVersionCard.Visibility != Visibility.Visible;
        DetailLoaderVersionCard.Visibility = Visibility.Visible;
        DetailLoaderVersion.ItemsSource = choices;
        // 默认：后端判定的稳定版；没有稳定版标记就取最新，不乱贴「推荐」。
        var previousIndex = choices.FindIndex(choice => choice.Version == previousVersion);
        DetailLoaderVersion.SelectedIndex = previousIndex >= 0 ? previousIndex : Math.Max(0, choices.FindIndex(c => c.Recommended));
        DetailLoaderVersion.IsEnabled = true;

        ConfigureApi(kind is "fabric" or "quilt" ? kind : "");
        if (showingOptions && IsLoaded) Motion.Enter(DetailLoaderVersionCard, fromY: 6, durationMs: 175);
        UpdateSummary();
    }

    private string SelectedApiProject => DetailPrereqRow.Visibility == Visibility.Visible
        ? (DetailPrereq.SelectedItem as ApiChoice)?.ProjectId ?? ""
        : "";

    private void ConfigureApi(string loader)
    {
        if (_apiLoader == loader) return;
        _apiLoader = loader;
        _apiRequest++;
        DetailPrereqRow.Visibility = loader.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailPrereqVersion.ItemsSource = null;
        DetailPrereq.ItemsSource = loader switch
        {
            "fabric" => new[] { FabricApi, NoApi },
            "quilt" => new[] { QuiltedApi, FabricApi, NoApi },
            _ => null,
        };
        if (loader.Length > 0) DetailPrereq.SelectedIndex = 0;
    }

    private async void DetailPrereq_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await LoadApiVersionsAsync();

    private async void DetailPrereqRetry_Click(object sender, RoutedEventArgs e) =>
        await LoadApiVersionsAsync();

    private void DetailPrereqVersion_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSummary();

    private async Task LoadApiVersionsAsync()
    {
        var request = ++_apiRequest;
        var project = SelectedApiProject;
        DetailPrereqVersion.ItemsSource = null;
        DetailPrereqVersion.IsEnabled = false;
        DetailPrereqRetry.Visibility = Visibility.Collapsed;
        DetailPrereqVersions.Visibility = project.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (project.Length == 0 || _version is null)
        {
            UpdateSummary();
            return;
        }

        DetailPrereqVersion.PlaceholderText = "正在查询版本…";
        UpdateSummary();
        var versions = await App.Backend.GetApiVersionsAsync(_version.Id, project);
        if (request != _apiRequest) return;
        if (versions is null)
        {
            DetailPrereqVersion.PlaceholderText = "版本查询失败";
            DetailPrereqRetry.Visibility = Visibility.Visible;
        }
        else if (versions.Count == 0)
        {
            DetailPrereqVersion.PlaceholderText = "没有适配版本";
        }
        else
        {
            DetailPrereqVersion.ItemsSource = versions;
            DetailPrereqVersion.SelectedIndex = 0;
            DetailPrereqVersion.IsEnabled = true;
        }
        UpdateSummary();
    }

    private static string ShortReason(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "原因未知";
        var line = message.Trim().Split('\n')[0];
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    private void UpdateSummary()
    {
        if (_version is null) return;
        if (SelectedInstallDir.Length == 0)
        {
            DetailSummary.Text = "请先在实例管理中添加游戏目录。";
            DetailInstall.IsEnabled = false;
            return;
        }
        var kind = _selKind;
        var loaderVersion = (DetailLoaderVersion.SelectedItem as LoaderChoice)?.Version ?? "";
        var proposed = kind == "vanilla" ? _version.Id : $"{_version.Id}-{LoaderLabel(kind)}_{loaderVersion}";
        if (_args?.Target is null && InstanceName.Text == _suggestedName)
        {
            _settingName = true;
            InstanceName.Text = proposed;
            _settingName = false;
        }
        _suggestedName = proposed;
        if (!ValidateInstanceName()) { DetailInstall.IsEnabled = false; return; }
        if (kind == "vanilla")
        {
            DetailSummary.Text = _args?.Target is { } t ? $"{t.Id} · 保留实例内容，仅修改加载器" : $"将安装 {_version.Id}（原版）";
            DetailInstall.IsEnabled = true;
            return;
        }
        var row = _loaderRows.FirstOrDefault(r => r.Kind == kind);
        if (DetailLoaderVersion.SelectedItem is LoaderChoice choice)
        {
            DetailSummary.Text = $"将安装 {_version.Id} + {LoaderLabel(kind)} {choice.Version}";
            row?.SetExtra($"将添加 {choice.Version}");
            DetailInstall.IsEnabled = SelectedApiProject.Length == 0 || DetailPrereqVersion.SelectedItem is ApiRelease;
        }
        else
        {
            DetailSummary.Text = $"{LoaderLabel(kind)} 版本还没选到，暂时不能安装";
            row?.SetExtra(null);
            DetailInstall.IsEnabled = false;
        }
    }

    private void InstanceName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName && _version is not null) UpdateSummary();
    }

    private bool ValidateInstanceName()
    {
        var name = InstanceName.Text.Trim();
        var error = "";
        if (name.Length == 0) error = "请输入实例名称";
        else if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                 || name.EndsWith('.') || name is "." or "..") error = "实例名称包含无效字符。";
        else if (_args?.Target is null && SelectedInstallDir.Length > 0 && System.IO.Directory.Exists(
                     System.IO.Path.Combine(SelectedInstallDir, "versions", name)))
            error = "实例名称与现有版本重复，请更换实例名称或选择其他游戏目录安装。";
        InstanceNameError.Text = error;
        InstanceNameError.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return error.Length == 0;
    }

    private void DetailInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_args is null || _version is null) return;
        var folder = SelectedInstallDir;
        if (folder.Length == 0) return;
        var kind = _selKind;
        var loaderVersion = (DetailLoaderVersion.SelectedItem as LoaderChoice)?.Version ?? "";
        if (kind != "vanilla" && loaderVersion.Length == 0) return;

        if (!ValidateInstanceName()) return;
        DownloadApiSelection? api = null;
        var project = SelectedApiProject;
        if (project.Length > 0)
        {
            if (DetailPrereqVersion.SelectedItem is not ApiRelease release) return;
            api = new DownloadApiSelection(project, release.Id);
        }
        // 安装交给列表页（它一直活着，进度走任务中心），子页关掉回到列表。
        _args.StartInstall(kind, loaderVersion, api, folder, InstanceName.Text.Trim());
        _args.Host.CloseSubPage();
    }
}
