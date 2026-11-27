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
    string VersionId, ISubPageHost Host, Action<ModrinthProject> OnInstalled,
    string GameVersion = "", string Loader = "", bool ChooseDownloadTarget = false, string GameRoot = "");

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

        public string Meta
        {
            get
            {
                // 一行只放「从文件名和筛选条件里推不出来」的信息：
                // CurseForge 的 version 常常就是发布日期原串（2026-10-17T03:13:29.870Z），
                // 和后面的日期重复；已经出现在文件名里的版本号也不再写一遍。
                var version = File.Version ?? "";
                if (LooksIsoStamp(version) ||
                    (version.Length > 0 && (File.FileName ?? "").Contains(version, StringComparison.OrdinalIgnoreCase)))
                    version = "";
                return string.Join("  ·  ", new[]
                {
                    GameOnly(),
                    File.Loader,
                    version,
                    File.Date,
                    File.SizeText,
                    File.RequiredText,
                    File.Installed ? "已安装 · " + File.InstalledInstance : "",
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
            }
        }

        /// <summary>游戏版本去掉 CurseForge 的 Client / Server 端标，它们不是版本号。</summary>
        private string GameOnly() => string.Join(", ", (File.GameShort ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.Equals("Client", StringComparison.OrdinalIgnoreCase)
                        && !part.Equals("Server", StringComparison.OrdinalIgnoreCase)));

        private static bool LooksIsoStamp(string value) =>
            value.Length >= 16 && value[4] == '-' && value[7] == '-' && value[10] == 'T';

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
    private OnlineModInstallPlan? _reviewedDeps;
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

    private bool ManualTarget => _args?.ChooseDownloadTarget == true;

    private void ApplyHint()
    {
        var p = _project!;
        var versionId = _args!.VersionId;
        string? hint = null;
        if (ManualTarget)
        {
            DetailHint.Text = "按筛选条件显示版本，下载时选择目标实例或保存位置。";
            DetailHint.Visibility = Visibility.Visible;
            return;
        }
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
            var inst = InstanceSelection.Find(snap, versionId, _args.GameRoot);
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

        if (!(p.IsInstallable || p.IsDatapack) || (!ManualTarget && !p.IsModpack && string.IsNullOrWhiteSpace(a.VersionId)))
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
            files = await App.Backend.GetOnlineFilesAsync(a.Source, p.Id, p.IsModpack ? "" : a.VersionId, p.InstallKind,
                ManualTarget ? a.GameVersion : null, ManualTarget ? a.Loader : null, a.GameRoot);
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
            SetNote(p.IsModpack ? "这个整合包没有可下载的文件。" : $"没有兼容 {(ManualTarget ? a.GameVersion + " / " + a.Loader : a.VersionId)} 的文件：这个项目没有发布支持该游戏版本 / 加载器的文件。", busy: false);
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

    /// <summary>底部「N 个前置」：点开看是哪些前置，勾选决定这次装不装。</summary>
    private async void DepsLink_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is null || _args is null || _project is null) return;
        var a = _args;
        var p = _project;
        _busy = true;
        UpdateBottom();
        try
        {
            var plan = await App.Backend.ConfirmOnlineModInstallAsync(
                a.Source, p.Id, _selected.File.Id, a.VersionId, XamlRoot, gameRoot: a.GameRoot, showAllDependencies: true);
            if (plan is null) return;
            _reviewedDeps = plan;
            App.Trace($"OnlineDetail: deps picked, skip={plan.SkipDependencies.Length}");
        }
        catch (Exception ex)
        {
            App.Trace("OnlineDetail: deps pick failed " + ex.Message);
            SetNote("无法查看前置：" + ex.Message, busy: false);
        }
        finally
        {
            _busy = false;
            UpdateBottom();
        }
    }

    private void File_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_busy && e.ClickedItem is FileRow row) Select(row);
    }

    private void Select(FileRow row)
    {
        if (_selected?.File.Id != row.File.Id) _reviewedDeps = null;
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
        var hasTarget = ManualTarget || p.IsModpack || !string.IsNullOrWhiteSpace(a.VersionId);
        // 与列表页一致：实例加载器不在项目支持列表里（含原版实例装 Mod）时不给装，提示里已经说明原因。
        var snap = App.MainWindow?.Snapshot;
        var inst = InstanceSelection.Find(snap, a.VersionId, a.GameRoot);
        var loaderBlocked = !ManualTarget && !dp && !string.IsNullOrWhiteSpace(a.VersionId) && !p.IsLoaderCompatibleWith(inst?.Loader ?? "");
        if (!ManualTarget && dp && _saves is null && !_savesLoading) _ = LoadSavesAsync();
        SavePanel.Visibility = dp && !ManualTarget ? Visibility.Visible : Visibility.Collapsed;
        var needSave = !ManualTarget && dp && (SelectedSave() is null || (_selected is null && _rows.Count == 0));
        var canInstall = (p.IsInstallable || dp) && !needSave && hasTarget && !_busy && !_loadingFiles && !_installed && !_noFiles && !loaderBlocked && (!ManualTarget || _selected is not null);
        string summary;
        var where = ManualTarget ? "选择实例或下载位置" : p.IsModpack ? "新实例" : dp ? $"{a.VersionId} / {SelectedSave()}" : a.VersionId;
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
        var showDeps = !ManualTarget && required > 0 && !_installed && !dp && !p.IsModpack && p.InstallKind == "mod";
        DepsLink.Content = _busy ? "正在处理前置…" : $"查看 {required} 个前置";
        DepsLink.IsEnabled = !_busy && !_loadingFiles;
        DepsLink.Visibility = showDeps ? Visibility.Visible : Visibility.Collapsed;

        var installingPack = InstallingPack is not null;
        DetailInstall.IsEnabled = installingPack || canInstall || (_project?.IsModpack == true && _installed && !_busy);
        DetailInstall.Content = installingPack ? "查看安装进度" : _installed ? (p.IsModpack ? "查看实例" : "已安装") : _busy ? "处理中…" : ManualTarget ? "下载" : "安装";
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
            _saves = await App.Backend.GetSaveNamesAsync(a.VersionId, a.GameRoot) ?? [];
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
        if (ManualTarget)
        {
            await DownloadToSelectedTargetAsync();
            return;
        }
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
                plan = _reviewedDeps ?? await App.Backend.ConfirmOnlineModInstallAsync(a.Source, p.Id,
                    file?.Id ?? "", target, XamlRoot, gameRoot: a.GameRoot);
                _reviewedDeps = null;
                SetNote("", busy: false);
                if (plan is null) return;
            }
            var snapshot = App.MainWindow.Snapshot;
            var targetInstance = target.Length > 0 ? InstanceSelection.Find(snapshot, target, a.GameRoot) : null;
            task = TaskCenter.Add($"安装 {p.Title}",
                $"{a.SourceName} · {(file is null ? p.TypeText : file.Display)} · {(p.IsModpack ? "新实例" : a.VersionId)}",
                PageTagFor(p), "\uE7B8", InstanceIcon.ForProject(p, targetInstance));
            if (p.IsModpack) task.ResourceKey = $"pack:{a.Source}:{p.Id}";
            var packProgress = p.IsModpack ? new Progress<string>(stage => TaskCenter.Report(task, stage)) : null;
            var result = plan is not null
                ? await App.Backend.InstallOnlineFileAsync(a.Source, p.Id, plan.FileId, target, p.InstallKind,
                    plan.Confirmation, taskId: task.Id, skipDependencies: plan.SkipDependencies, gameRoot: a.GameRoot,
                    dependencyVersions: plan.DependencyVersions)
                : dpInstall
                ? await App.Backend.InstallOnlineDatapackAsync(a.Source, p.Id, file?.Id ?? "", target, SelectedSave() ?? "", task.Id, a.GameRoot)
                : file is null
                ? await App.Backend.InstallOnlineAsync(a.Source, p.Id, target, p.InstallKind, packProgress, task.Id,
                    gameRoot: p.IsModpack ? "" : a.GameRoot)
                : await App.Backend.InstallOnlineFileAsync(a.Source, p.Id, file.Id, target, p.InstallKind, progress: packProgress, taskId: task.Id, gameRoot: a.GameRoot);
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

    private async Task DownloadToSelectedTargetAsync()
    {
        var a = _args!;
        var p = _project!;
        var file = _selected?.File;
        if (file is null) return;
        _busy = true;
        UpdateBottom();
        TaskEntry? task = null;
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync(force: true);
            var choice = await ChooseDownloadTargetAsync(XamlRoot, p, file,
                snapshot?.Versions ?? App.MainWindow.Snapshot?.Versions ?? [], DatapackMode);
            if (choice is null) return;
            var target = choice.Value.Target;
            string folder = "", save = "";
            if (target is null)
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.WindowHandle);
                picker.FileTypeFilter.Add("*");
                var chosen = await picker.PickSingleFolderAsync();
                if (chosen is null) return;
                folder = chosen.Path;
            }
            else if (DatapackMode)
            {
                var saves = await App.Backend.GetSaveNamesAsync(target.Id, target.Root) ?? [];
                var savesBox = new ComboBox { ItemsSource = saves, SelectedIndex = saves.Count > 0 ? 0 : -1,
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                var savesDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "选择存档", Content = saves.Count == 0
                        ? (object)"此实例没有存档，请先创建存档，或选择文件夹仅下载。" : savesBox,
                    PrimaryButtonText = "安装", CloseButtonText = "取消", IsPrimaryButtonEnabled = saves.Count > 0,
                };
                if (await savesDialog.ShowAsync() != ContentDialogResult.Primary) return;
                save = savesBox.SelectedItem as string ?? "";
            }
            OnlineModInstallPlan? plan = null;
            if (target is not null && !DatapackMode && p.InstallKind == "mod")
            {
                plan = await App.Backend.ConfirmOnlineModInstallAsync(a.Source, p.Id, file.Id,
                    target.Id, XamlRoot, gameRoot: target.Root);
                if (plan is null) return;
            }
            task = TaskCenter.Add((target is null ? "下载 " : "安装 ") + p.Title,
                a.SourceName + " · " + file.Display + " · " + (target?.Id ?? folder),
                PageTagFor(p), "\uE7B8", InstanceIcon.ForProject(p, target));
            if (p.IsModpack && target is not null) task.ResourceKey = $"pack:{a.Source}:{p.Id}";
            var progress = new Progress<string>(stage => TaskCenter.Report(task, stage));
            var result = target is null
                ? await App.Backend.DownloadOnlineFileAsync(a.Source, p.Id, file.Id, folder, task.Id)
                : DatapackMode
                ? await App.Backend.InstallOnlineDatapackAsync(a.Source, p.Id, file.Id, target.Id, save, task.Id, target.Root)
                : await App.Backend.InstallOnlineFileAsync(a.Source, p.Id, plan?.FileId ?? file.Id,
                    p.IsModpack ? "" : target.Id, p.InstallKind, plan?.Confirmation ?? "", progress, task.Id,
                    plan?.SkipDependencies, target.Root, plan?.DependencyVersions);
            if (!result.Ok)
            {
                TaskCenter.Fail(task, result.Message, result.Failure);
                SetNote(result.Message, busy: false);
                return;
            }
            TaskCenter.Complete(task, result.Message);
            SetNote(result.Message, busy: false);
            // Saving a standalone file does not mean it is installed in the selected instance.
            if (target is not null && p.IsModpack) await ReadFilesAsync();
            if (target is not null && target.Id.Equals(a.VersionId, StringComparison.OrdinalIgnoreCase)
                && InstanceSelection.SameRoot(target.Root, a.GameRoot)) a.OnInstalled(p);
        }
        catch (Exception ex)
        {
            if (task is not null) TaskCenter.Fail(task, ex.Message);
            SetNote(ex.Message, busy: false);
        }
        finally { _busy = false; UpdateBottom(); }
    }

    // Both paths stay visible even when no installed instance matches this file.
    // Keep VersionInfo itself as the selection so duplicate IDs retain their game roots.
    internal static async Task<(VersionInfo? Target, bool DownloadOnly)?> ChooseDownloadTargetAsync(
        XamlRoot xamlRoot, ModrinthProject project, OnlineFileInfo file,
        IEnumerable<VersionInfo> instances, bool datapack = false)
    {
        var valid = instances.Where(v => v.Valid).ToList();
        var compatible = valid.Where(v => project.IsModpack || CompatibleTarget(v, file, project, datapack)).ToList();
        var install = new RadioButton { Content = project.IsModpack ? "安装为新实例" : "选择实例", GroupName = "ResourceTarget" };
        var download = new RadioButton { Content = "选择下载位置", GroupName = "ResourceTarget" };
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 320,
            PlaceholderText = project.IsModpack ? "选择游戏目录" : "选择安装到的实例" };
        foreach (var instance in compatible.Concat(valid.Except(compatible)))
        {
            // Packs create a new instance in a game directory, never overwrite the chosen instance.
            if (project.IsModpack && box.Items.Cast<ComboBoxItem>().Any(i =>
                string.Equals(((VersionInfo)i.Tag).Root, instance.Root, StringComparison.OrdinalIgnoreCase))) continue;
            var available = compatible.Contains(instance);
            var label = project.IsModpack ? instance.Root : instance.Id + " · " + instance.GameVersion + " · " + instance.LoaderDisplay;
            if (!project.IsModpack && valid.Count(v => v.Id.Equals(instance.Id, StringComparison.OrdinalIgnoreCase)) > 1)
                label += " · " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(instance.Root.TrimEnd('\\', '/')));
            var item = new ComboBoxItem { Content = label + (available ? "" : "（版本或加载器不匹配）"),
                Tag = instance, IsEnabled = available };
            ToolTipService.SetToolTip(item, instance.Root);
            box.Items.Add(item);
        }
        box.SelectedIndex = compatible.Count > 0 ? 0 : -1;
        var requirements = string.Join(" · ", new[] { file.GameShort, datapack || project.InstallKind != "mod" ? "" : file.Loader }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var info = new TextBlock { Text = requirements.Length > 0 ? "所选文件：" + requirements : file.Display,
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)Application.Current.Resources["AppText2Brush"] };
        var installHint = new TextBlock { Text = compatible.Count == 0
                ? "没有与所选文件匹配的实例。可以选择下载位置，仅保存文件。"
                : project.IsModpack ? "将在所选游戏目录中创建新实例。" : "仅可安装到游戏版本与加载器匹配的实例。",
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = info.Foreground };
        var installDetails = new StackPanel { Spacing = 8, Margin = new Thickness(28, 0, 0, 0), Children = { box, installHint } };
        var downloadHint = new TextBlock { Text = "仅下载文件，不安装到实例。", FontSize = 13,
            Foreground = info.Foreground, Margin = new Thickness(28, 0, 0, 0) };
        var content = new StackPanel { Spacing = 12, Children = { info, install, installDetails, download, downloadHint } };
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = "选择实例或下载位置", Content = content,
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        void UpdateMode()
        {
            var installing = install.IsChecked == true;
            installDetails.Visibility = installing || compatible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.Visibility = installing && valid.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            downloadHint.Visibility = installing ? Visibility.Collapsed : Visibility.Visible;
            box.IsEnabled = installing && compatible.Count > 0;
            dialog.PrimaryButtonText = installing ? "安装" : "选择文件夹";
            dialog.IsPrimaryButtonEnabled = !installing || (box.SelectedItem as ComboBoxItem)?.IsEnabled == true;
        }
        install.Checked += (_, _) => UpdateMode();
        download.Checked += (_, _) => UpdateMode();
        box.SelectionChanged += (_, _) => UpdateMode();
        if (compatible.Count > 0) install.IsChecked = true;
        else download.IsChecked = true;
        UpdateMode();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return install.IsChecked == true
            ? (((box.SelectedItem as ComboBoxItem)?.Tag as VersionInfo), false)
            : (null, true);
    }

    private static bool CompatibleTarget(VersionInfo instance, OnlineFileInfo file, ModrinthProject project, bool datapack)
    {
        var games = (file.Game ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (games.Length > 0 && !games.Contains(instance.GameVersion, StringComparer.OrdinalIgnoreCase)) return false;
        if (datapack || project.InstallKind != "mod") return true;
        var loader = instance.Loader.ToLowerInvariant();
        var tag = new[] { "neoforge", "fabric", "forge", "quilt" }.FirstOrDefault(loader.Contains);
        return tag is not null && (file.Loader ?? "").Split(',', StringSplitOptions.TrimEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
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
