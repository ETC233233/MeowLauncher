using System.ComponentModel;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 打开 Mod 详情子页要带的东西：项目、来源、安装目标实例，以及装完通知列表页刷新「已安装」的回调。
/// </summary>
public sealed record OnlineDetailArgs(ModrinthProject Project, string Source, string SourceName,
    string VersionId, ISubPageHost Host, Action<ModrinthProject> OnInstalled);

/// <summary>「获取 › 在线资源 › 某某 Mod」子页：简介 + 版本（文件）列表 + 底部固定安装。</summary>
public sealed partial class OnlineDetailPage : Page
{
    private sealed class FileRow : INotifyPropertyChanged
    {
        private bool _selected;

        public FileRow(OnlineFileInfo file)
        {
            File = file;
        }

        public OnlineFileInfo File { get; }
        public string Name => string.IsNullOrWhiteSpace(File.Name)
            ? (string.IsNullOrWhiteSpace(File.FileName) ? "未命名文件" : File.FileName)
            : File.Name;
        public string TypeText => File.TypeText;
        public Brush TypeBrush => File.TypeBrush;

        public string Meta => string.Join("  ·  ", new[]
        {
            File.Version,
            File.Date,
            File.SizeText,
            File.GameShort,
            File.Loader,
            File.RequiredText,
            File.Installed ? "已安装 · " + File.InstalledInstance : "",
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
        public bool Selected { get => _selected; set { _selected = value; Raise(nameof(SelectedVisibility)); } }
        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private OnlineDetailArgs? _args;
    private ModrinthProject? _project;
    private List<FileRow> _rows = [];
    private FileRow? _selected;
    private bool _installed;
    private bool _busy;
    private bool _noFiles;
    private bool _loadingFiles;
    private bool _wasInstallingPack;
    private string _installedInstanceId = "";
    private string _installedRoot = "";
    private TaskEntry? InstallingPack => _project?.IsModpack == true && _args is { } args
        ? TaskCenter.Entries.FirstOrDefault(task => task.IsActive && task.ResourceKey == $"pack:{args.Source}:{_project.Id}") : null;

    public OnlineDetailPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not OnlineDetailArgs args) return;
        _args = args;
        TaskCenter.Changed += PackTask_Changed;
        var p = _project = args.Project;
        _wasInstallingPack = InstallingPack is not null;
        try
        {
            App.Trace("OnlineDetail: navigated " + p.Title);
            DetailIcon.Source = p.IconSource;
            DetailTitle.Text = p.Title;
            DetailAuthor.Text = string.IsNullOrWhiteSpace(p.Author) ? "" : $"{p.Author} · {p.TypeText}";
        DetailAuthor.Visibility = string.IsNullOrWhiteSpace(p.Author) ? Visibility.Collapsed : Visibility.Visible;
            DetailStats.Text = $"{p.DownloadsText} · {args.SourceName}";   // DownloadsText 自带「下载」二字
            DetailDescription.Text = StripMarkup(p.Description);
            ApplyHint();
            UpdateBottom();
            App.Trace("OnlineDetail: header ready, loading files");
            await LoadFilesAsync();
            App.Trace("OnlineDetail: files loaded");
        }
        catch (Exception ex)
        {
            App.Trace("OnlineDetail: FAILED " + ex);
            SetNote("详情页加载出错：" + ex.Message, busy: false);
        }
    }

    private void ApplyHint()
    {
        var p = _project!;
        var versionId = _args!.VersionId;
        string? hint = null;
        if (DatapackMode)
        {
            hint = string.IsNullOrWhiteSpace(versionId)
                ? "先在版本管理里选一个实例，再回来安装。"
                : "这是数据包，不需要加载器；会安装到你选择的存档（saves/<存档名>/datapacks），同名数据包不会被覆盖。";
        }
        else if (!p.IsInstallable)
        {
            hint = "这个类型暂不支持自动安装；可以打开项目页下载后放进实例目录。";
        }
        else if (p.IsModpack)
        {
            hint = "整合包会安装成一个新实例，不会改动当前实例。";
        }
        else if (string.IsNullOrWhiteSpace(versionId))
        {
            hint = "先在版本管理里选一个实例，再回来安装。";
        }
        else
        {
            var snap = App.MainWindow?.Snapshot;
            var inst = snap?.Versions.FirstOrDefault(v => v.Id == versionId) ?? snap?.Current;
            var gameVersion = inst?.GameVersion ?? "";
            if (!string.IsNullOrWhiteSpace(gameVersion) && !p.IsCompatibleWith(gameVersion))
                hint = $"实例 {versionId} 是 {gameVersion}，这个项目没有对应的游戏版本，装了可能不生效。";
            else if (!p.IsLoaderCompatibleWith(inst?.Loader ?? ""))
                hint = $"实例 {versionId} 是 {inst?.LoaderDisplay ?? "未检测到加载器"}，这个项目只支持 {p.LoaderText}。";
        }
        DetailHint.Text = hint ?? "";
        DetailHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadFilesAsync()
    {
        if (_loadingFiles || _busy) return;
        _loadingFiles = true;
        try { await ReadFilesAsync(); }
        finally
        {
            _loadingFiles = false;
            UpdateBottom();
        }
    }

    private async Task ReadFilesAsync()
    {
        var p = _project!;
        var a = _args!;
        FilesRetry.Visibility = Visibility.Collapsed;
        _noFiles = false;
        _rows = [];
        FileList.ItemsSource = null;
        _selected = null;

        if (!(p.IsInstallable || p.IsDatapack) || (!p.IsModpack && string.IsNullOrWhiteSpace(a.VersionId)))
        {
            SetNote("", busy: false);
            UpdateBottom();
            return;
        }

        SetNote("正在读取版本列表…", busy: true);
        UpdateBottom();
        OnlineFilesResult? files = null;
        string error = "";
        try
        {
            files = await App.Backend.GetOnlineFilesAsync(a.Source, p.Id, p.IsModpack ? "" : a.VersionId, p.InstallKind);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (files is not { Ok: true })
        {
            // 列表读不到：仍允许「安装最新版本」，但把原因和重试摆出来。
            SetNote($"版本列表读取失败：{(error.Length > 0 ? error : files?.Message ?? "后台无响应")}", busy: false);
            FilesRetry.Visibility = Visibility.Visible;
            UpdateBottom();
            return;
        }

        if (files.Files.Count == 0)
        {
            _noFiles = true;
            SetNote(p.IsModpack ? "这个整合包没有可下载的文件。" : $"没有兼容 {a.VersionId} 的文件：这个项目没有发布支持该游戏版本 / 加载器的文件。", busy: false);
            UpdateBottom();
            return;
        }

        _rows = files.Files.Select(f => new FileRow(f)).ToList();
        FileList.ItemsSource = _rows;
        SetNote("", busy: false);
        Select(_rows[FirstReleaseIndex(files.Files)]);
    }

    private static int FirstReleaseIndex(IReadOnlyList<OnlineFileInfo> files)
    {
        for (var i = 0; i < files.Count; i++)
            if (string.Equals(files[i].Type, "release", StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private void SetNote(string text, bool busy)
    {
        FilesNote.Text = text;
        FilesNote.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FilesBusy.IsActive = busy;
        FilesBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void File_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_busy && e.ClickedItem is FileRow row) Select(row);
    }

    private void Select(FileRow row)
    {
        if (_selected is not null) _selected.Selected = false;
        _selected = row;
        row.Selected = true;
        if (_project?.IsModpack == true)
        {
            _installed = row.File.Installed;
            _installedInstanceId = row.File.InstalledInstance;
            _installedRoot = row.File.InstalledRoot;
        }
        ApplyHint();
        UpdateBottom();
    }

    private async void FilesRetry_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) await LoadFilesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        TaskCenter.Changed -= PackTask_Changed;
        base.OnNavigatedFrom(e);
    }

    private void PackTask_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var installing = InstallingPack is not null;
        if (_wasInstallingPack && !installing && !_busy) _ = LoadFilesAsync();
        _wasInstallingPack = installing;
        UpdateBottom();
    });

    private void UpdateBottom()
    {
        var p = _project;
        var a = _args;
        if (p is null || a is null) return;
        var dp = DatapackMode;

        // 整合包装成新实例，不需要先选实例。
        var hasTarget = p.IsModpack || !string.IsNullOrWhiteSpace(a.VersionId);
        // 与列表页一致：实例加载器不在项目支持列表里（含原版实例装 Mod）时不给装，提示里已经说明原因。
        var snap = App.MainWindow?.Snapshot;
        var inst = snap?.Versions.FirstOrDefault(v => v.Id == a.VersionId) ?? snap?.Current;
        var loaderBlocked = !dp && !string.IsNullOrWhiteSpace(a.VersionId) && !p.IsLoaderCompatibleWith(inst?.Loader ?? "");
        if (dp && _saves is null && !_savesLoading) _ = LoadSavesAsync();
        SavePanel.Visibility = dp ? Visibility.Visible : Visibility.Collapsed;
        var needSave = dp && (SelectedSave() is null || (_selected is null && _rows.Count == 0));
        var canInstall = (p.IsInstallable || dp) && !needSave && hasTarget && !_busy && !_loadingFiles && !_installed && !_noFiles && !loaderBlocked;
        string summary;
        var where = p.IsModpack ? "新实例" : dp ? $"{a.VersionId} / {SelectedSave()}" : a.VersionId;
        if (_installed) summary = p.IsModpack ? $"{p.Title} 已安装为新实例" : $"{p.Title} 已安装到 {a.VersionId}";
        else if (!(p.IsInstallable || dp)) summary = "暂不支持自动安装";
        else if (needSave) summary = _saves is { Count: 0 } ? "先在游戏里创建一个存档" : "请选择要装进的存档";
        else if (!hasTarget) summary = "还没有选择实例";
        else if (loaderBlocked) summary = "实例加载器不兼容";
        else if (_loadingFiles) summary = "正在读取版本列表…";
        else if (_noFiles) summary = "无兼容版本";
        else if (_selected is not null) summary = $"将安装 {_selected.Name}（{where}）";
        else summary = $"将安装最新版本（{where}）";
        DetailSummary.Text = summary;

        var required = _selected?.File.Required ?? 0;
        DetailDeps.Text = required > 0 && !_installed && !dp && p.InstallKind == "mod"
            ? $"安装时会检查 {required} 个必需前置"
            : "";
        DetailDeps.Visibility = DetailDeps.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var installingPack = InstallingPack is not null;
        DetailInstall.IsEnabled = installingPack || canInstall || (_project?.IsModpack == true && _installed && !_busy);
        DetailInstall.Content = installingPack ? "查看安装进度" : _installed ? (p.IsModpack ? "查看实例" : "已安装") : _busy ? "安装中…" : "安装";
    }

    private List<string>? _saves;
    private bool _savesLoading;

    /// <summary>数据包不需要加载器。项目类型是 datapack，或者选中的文件只带 datapack 加载器（Modrinth 上很多数据包项目的类型是 mod）。</summary>
    private bool DatapackMode => _project is { } p && (p.IsDatapack || FileIsDatapack(_selected?.File));

    private static bool FileIsDatapack(OnlineFileInfo? f)
    {
        var l = (f?.Loader ?? "").ToLowerInvariant();
        if (!l.Contains("datapack")) return false;
        return !(l.Contains("fabric") || l.Contains("forge") || l.Contains("quilt"));
    }

    private string? SelectedSave() => SaveBox.SelectedItem as string;

    private void SaveBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBottom();

    private async Task LoadSavesAsync()
    {
        var p = _project!;
        var a = _args!;
        if (string.IsNullOrWhiteSpace(a.VersionId)) { _saves = []; return; }
        _savesLoading = true;
        try
        {
            _saves = await App.Backend.GetSaveNamesAsync(a.VersionId) ?? [];
        }
        catch { _saves = []; }
        _savesLoading = false;
        SaveBox.ItemsSource = _saves;
        SaveBox.IsEnabled = _saves.Count > 0;
        if (_saves.Count > 0) SaveBox.SelectedIndex = 0;
        UpdateBottom();
    }

    private async void DetailInstall_Click(object sender, RoutedEventArgs e)
    {
        if (InstallingPack is not null)
        {
            App.MainWindow.Navigate("tasks");
            return;
        }
        if (_project?.IsModpack == true && _installed && _installedInstanceId.Length > 0)
        {
            App.MainWindow.OpenInstalledInstance(_installedInstanceId, _installedRoot);
            return;
        }
        if (_project is null || _args is null || _busy || _loadingFiles || _installed) return;
        var p = _project;
        var a = _args;
        _busy = true;
        UpdateBottom();

        var dpInstall = DatapackMode;
        var file = _selected?.File ?? (dpInstall ? _rows.FirstOrDefault()?.File : null);
        var target = p.IsModpack ? "" : a.VersionId;   // 整合包不装进当前实例，后端会新建一个
        TaskEntry? task = null;
        try
        {
            OnlineModInstallPlan? plan = null;
            if (!dpInstall && p.InstallKind == "mod")
            {
                SetNote("正在检查必需前置…", busy: true);
                plan = await App.Backend.ConfirmOnlineModInstallAsync(a.Source, p.Id, file?.Id ?? "", target, XamlRoot);
                SetNote("", busy: false);
                if (plan is null) return;
            }
            var snapshot = App.MainWindow.Snapshot;
            var targetInstance = snapshot?.Current is { } current && current.Id == target
                ? current : snapshot?.Versions.FirstOrDefault(v => v.Id == target);
            task = TaskCenter.Add($"安装 {p.Title}",
                $"{a.SourceName} · {(file is null ? p.TypeText : file.Display)} · {(p.IsModpack ? "新实例" : a.VersionId)}",
                PageTagFor(p), "\uE7B8", InstanceIcon.ForProject(p, targetInstance));
            if (p.IsModpack) task.ResourceKey = $"pack:{a.Source}:{p.Id}";
            var packProgress = p.IsModpack ? new Progress<string>(stage => TaskCenter.Report(task, stage)) : null;
            var result = plan is not null
                ? await App.Backend.InstallOnlineFileAsync(a.Source, p.Id, plan.FileId, target, p.InstallKind, plan.Confirmation, taskId: task.Id)
                : dpInstall
                ? await App.Backend.InstallOnlineDatapackAsync(a.Source, p.Id, file?.Id ?? "", target, SelectedSave() ?? "", task.Id)
                : file is null
                ? await App.Backend.InstallOnlineAsync(a.Source, p.Id, target, p.InstallKind, packProgress, task.Id)
                : await App.Backend.InstallOnlineFileAsync(a.Source, p.Id, file.Id, target, p.InstallKind, progress: packProgress, taskId: task.Id);
            if (result.Ok)
            {
                if (p.IsModpack) task.TargetIcon = InstanceIcon.ForInstalledPack(result);
                TaskCenter.Complete(task, result.Message);
                _installed = true;
                if (p.IsModpack)
                {
                    _installedInstanceId = result.Version;
                    _installedRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(result.InstancePath)) ?? "";
                }
                if (p.IsModpack && _selected is not null)
                {
                    _selected.File.InstalledInstance = result.Version;
                    _selected.File.InstalledRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(result.InstancePath)) ?? "";
                }
                a.OnInstalled(p);
            }
            else
            {
                TaskCenter.Fail(task, result.Message, result.Failure);
                SetNote($"安装失败：{result.Message}", busy: false);
            }
        }
        catch (Exception ex)
        {
            if (task is not null) TaskCenter.Fail(task, ex.Message);
            SetNote($"安装失败：{ex.Message}", busy: false);
        }
        finally
        {
            _busy = false;
            UpdateBottom();
        }
    }

    private async void DetailOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || string.IsNullOrWhiteSpace(_project.PageUrl)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(_project.PageUrl)); }
        catch { /* 浏览器不可用时忽略 */ }
    }

    private static string PageTagFor(ModrinthProject project) => project.InstallKind switch
    {
        "resourcepack" => "resourcepack",
        "shaderpack" => "shaderpack",
        "modpack" => "versions",
        _ => "mods",
    };

    private static string StripMarkup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "暂无简介。";
        var text = System.Text.RegularExpressions.Regex.Replace(raw, "<br\\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "[ \t]+", " ");
        return text.Trim();
    }
}
