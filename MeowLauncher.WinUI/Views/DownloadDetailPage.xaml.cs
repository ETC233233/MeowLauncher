using System.ComponentModel;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MeowLauncher.WinUI.Views;

/// <summary>打开下载子页要带的东西：版本、宿主（关闭用）、开始安装的回调（由列表页负责装，列表页一直活着）。</summary>
public sealed record DownloadDetailArgs(MojangVersionInfo Version, ISubPageHost Host,
    Action<string, string, bool, string> StartInstall);

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
            _base = status;
            _enabled = enabled;
        }

        public string Kind { get; }
        public string Name { get; }
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

    private static readonly Dictionary<string, LoaderSupportResult> _supportCache = new(StringComparer.Ordinal);

    private DownloadDetailArgs? _args;
    private MojangVersionInfo? _version;
    private LoaderSupportResult? _support;

    /// <summary>每个加载器单独回来的结果：不能等四种都查完再放行版本下拉，否则先回来的 Forge 会被判成「版本还没选到」。</summary>
    private readonly Dictionary<string, LoaderKindInfo> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoaderRow> _loaderRows = [];
    private string _selKind = "vanilla";

    public DownloadDetailPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not DownloadDetailArgs args) return;
        _args = args;
        var v = _version = args.Version;

        DetailTitle.Text = v.Id;
        DetailMeta.Text = $"{v.TypeText} · {v.DateText}";
        var (icon, stretch) = DownloadPage.IconFor(v.Id);
        DetailIconBrush.ImageSource = icon;
        DetailIconBrush.Stretch = stretch;
        try
        {
            var prefs = AppPreferencesStore.Load();
            var dirs = GameDirectoryResolver.Resolve(prefs);
            DetailDir.Text = dirs.Count > 0 ? "安装到 " + dirs[0] : "未设置游戏目录";
        }
        catch { DetailDir.Text = "未设置游戏目录"; }

        SetLoaderNote("");
        _loaderRows.Add(new LoaderRow("vanilla", "原版", "不带 Mod 加载器", true) { Visible = true, Installed = v.Installed });
        foreach (var kind in new[] { "fabric", "forge", "neoforge", "quilt" })
            _loaderRows.Add(new LoaderRow(kind, LoaderLabel(kind), "正在查询…", false) { Visible = true });
        RefreshLoaderList();

        DetailAncientNote.Visibility = v.Type is "old_beta" or "old_alpha" ? Visibility.Visible : Visibility.Collapsed;
        ApplyLoaderSelection();
        _ = LoadInstalledKindsAsync(v);
        await LoadSupportAsync(v);
    }

    private void RefreshLoaderList() =>
        DetailLoaderList.ItemsSource = _loaderRows.Where(r => r.Visible).ToList();

    private static string LoaderLabel(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "原版",
    };

    /// <summary>同一个 Minecraft 版本下已经装过哪些加载器（来自实例列表），给对应行打「已安装」。</summary>
    private async Task LoadInstalledKindsAsync(MojangVersionInfo v)
    {
        try
        {
            var snap = await App.Backend.GetSnapshotAsync();
            if (snap is null || _version is null || _version.Id != v.Id) return;
            // 只认「同一个 MC 版本 + 该加载器」的实例；原版实例（loader 为空）不算任何加载器。
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var x in snap.Versions)
            {
                if (!string.Equals(x.GameVersion, v.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var kind = KindOfLoader(x.Loader);
                if (kind.Length > 0 && !found.ContainsKey(kind)) found[kind] = (x.LoaderVersion ?? "").Trim();
            }
            foreach (var row in _loaderRows.Skip(1))
            {
                var has = found.TryGetValue(row.Kind, out var ver);
                row.InstalledNote = has ? ver ?? "" : "";
                row.Installed = has;
            }
        }
        catch
        {
            // 读不到实例列表就不打标记，不影响安装。
        }
    }

    /// <summary>实例的加载器字符串 → fabric / forge / neoforge / quilt；认不出（含原版、modded）返回空。neoforge 要先判，它包含 "forge"。</summary>
    private static string KindOfLoader(string? raw)
    {
        var t = (raw ?? "").Trim().ToLowerInvariant();
        foreach (var k in new[] { "neoforge", "fabric", "quilt", "forge" })
            if (t.Contains(k)) return k;
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
        DetailLoaderVersion.ItemsSource = null;

        if (kind == "vanilla")
        {
            DetailLoaderVersionCard.Visibility = Visibility.Collapsed;
            DetailPrereqRow.Visibility = Visibility.Collapsed;
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
            DetailPrereqRow.Visibility = Visibility.Collapsed;
            if (info is not null && !info.Ok)
                SetLoaderNote($"{LoaderLabel(kind)} 的版本清单没有读到（{ShortReason(info.Message)}）。", retry: true);
            UpdateSummary();
            return;
        }

        var choices = info.Versions
            .Select(v => new LoaderChoice(v, v == info.Recommended))
            .ToList();
        DetailLoaderVersionCard.Visibility = Visibility.Visible;
        DetailLoaderVersion.ItemsSource = choices;
        // 默认：后端判定的稳定版；没有稳定版标记就取最新，不乱贴「推荐」。
        var defaultIndex = Math.Max(0, choices.FindIndex(c => c.Recommended));
        DetailLoaderVersion.SelectedIndex = defaultIndex;
        DetailLoaderVersion.IsEnabled = true;

        // 前置（Fabric API）默认带上，但允许用户自己挑版本、或者干脆不装。
        var needPrereq = kind is "fabric" or "quilt";
        DetailPrereqRow.Visibility = needPrereq ? Visibility.Visible : Visibility.Collapsed;
        DetailPrereq.Content = kind == "quilt" ? "一起安装前置（Quilted Fabric API）" : "一起安装前置（Fabric API）";
        _ = LoadPrereqVersionsAsync(needPrereq);
        UpdateSummary();
    }

    /// <summary>按当前游戏版本拉 Fabric API 的可用版本，填进前置那一行的下拉。</summary>
    private async Task LoadPrereqVersionsAsync(bool needed)
    {
        DetailPrereqVersion.ItemsSource = null;
        DetailPrereqVersion.IsEnabled = false;
        DetailPrereqVersion.PlaceholderText = "前置版本";
        if (!needed || _version is null) return;

        var mc = _version.Id;
        var list = await App.Backend.GetFabricApiVersionsAsync(mc);
        // 等待期间可能又切到别的加载器了，回来先确认还需要它。
        if (DetailPrereqRow.Visibility != Visibility.Visible || _selKind is not ("fabric" or "quilt")) return;

        DetailPrereqVersion.ItemsSource = list;
        DetailPrereqVersion.IsEnabled = list.Count > 0;
        if (list.Count > 0) DetailPrereqVersion.SelectedIndex = 0;
        else DetailPrereqVersion.PlaceholderText = "没有找到适配版本";
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
        var kind = _selKind;
        if (kind == "vanilla")
        {
            DetailSummary.Text = $"将安装 {_version.Id}（原版）";
            DetailInstall.IsEnabled = true;
            return;
        }
        var row = _loaderRows.FirstOrDefault(r => r.Kind == kind);
        if (DetailLoaderVersion.SelectedItem is LoaderChoice choice)
        {
            DetailSummary.Text = $"将安装 {_version.Id} + {LoaderLabel(kind)} {choice.Version}";
            row?.SetExtra($"将添加 {choice.Version}");
            DetailInstall.IsEnabled = true;
        }
        else
        {
            DetailSummary.Text = $"{LoaderLabel(kind)} 版本还没选到，暂时不能安装";
            row?.SetExtra(null);
            DetailInstall.IsEnabled = false;
        }
    }

    private async void DetailInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_args is null || _version is null) return;
        var kind = _selKind;
        var loaderVersion = (DetailLoaderVersion.SelectedItem as LoaderChoice)?.Version ?? "";
        if (kind != "vanilla" && loaderVersion.Length == 0) return;

        // 已经装过的先问一句：原版是重新下载校验；加载器同版本会重写那个实例的版本文件，不同版本会另建一个实例。
        var row = _loaderRows.FirstOrDefault(r => r.Kind == kind);
        if (row?.Installed == true)
        {
            var same = kind != "vanilla" && string.Equals(row.InstalledNote, loaderVersion, StringComparison.OrdinalIgnoreCase);
            var text = kind == "vanilla"
                ? $"{_version.Id} 原版已经装过了。再装一次会重新下载并校验游戏文件，存档和设置不受影响。"
                : same
                    ? $"{_version.Id} + {LoaderLabel(kind)} {loaderVersion} 已经装过了。再装一次会重写这个实例的版本文件。"
                    : $"{_version.Id} 已经有 {LoaderLabel(kind)} 实例（{(row.InstalledNote.Length > 0 ? row.InstalledNote : "已安装")}）。装 {loaderVersion} 会另建一个实例，原来的不受影响。";
            var dialog = new ContentDialog
            {
                Title = "已经安装过",
                Content = text,
                PrimaryButtonText = same || kind == "vanilla" ? "重新安装" : "继续安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var withPrereq = DetailPrereqRow.Visibility == Visibility.Visible && DetailPrereq.IsChecked == true;
        var prereqVersion = withPrereq ? DetailPrereqVersion.SelectedItem as string ?? "" : "";
        // 安装交给列表页（它一直活着，进度走任务中心），子页关掉回到列表。
        _args.StartInstall(kind, loaderVersion, withPrereq, prereqVersion);
        _args.Host.CloseSubPage();
    }
}
