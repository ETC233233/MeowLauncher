using System.Diagnostics;
using MeowLauncher.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI.Views;

public sealed partial class AssetsPage : Page, IPageSearchFilter
{
    private readonly List<PackInfo> _all = [];
    private string _dir = "";
    private string _gameDir = "";
    private string _savesDir = "";
    private string _shotsDir = "";
    private string _versionId = "";
    private string _kind = "resourcepack";
    private string _query = "";
    private bool _loading;
    private bool _suppressKind;
    private int _packCount, _shaderCount, _worldCount, _shotCount;

    /// <summary>宿主页（InstancePage）用来在外层显示同一套筛选与计数。</summary>
    public event EventHandler<AssetsCounts>? CountsChanged;

    public string CurrentKind => _kind;

    public AssetsCounts Counts => new(_packCount, _shaderCount, _worldCount, _shotCount);

    public AssetsPage()
    {
        InitializeComponent();
        _suppressKind = true;
        TabPacks.IsChecked = true;
        _suppressKind = false;

        Loaded += (_, _) => _ = LoadAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is TabNav)
        {
            PageHeader.Visibility = Visibility.Collapsed;
            PageRoot.RowDefinitions[0].Height = new GridLength(0);
            // 筛选胶囊移到 InstancePage 的 tab 行，内嵌时不再重复显示。
            FiltersPanel.Visibility = Visibility.Collapsed;
            // 没有筛选胶囊了就别留着漏斗按钮：搜索框退回放大镜。
            QueryBox.IsFilter = false;
            PageRoot.RowDefinitions[1].Height = new GridLength(0);
        }
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        PackList.Visibility = Visibility.Collapsed;
        EmptyHost.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync();
            var vid = snapshot?.Current?.Id ?? snapshot?.Selected ?? "";
            _versionId = vid;
            _gameDir = snapshot?.Current?.Root ?? snapshot?.GameDir ?? "";
            var result = await App.Backend.GetPacksAsync(vid);
            if (result is null || !result.Ok)
            {
                ShowError(result?.Dir ?? "后台无响应");
                return;
            }

            _all.Clear();
            _dir = result.Dir ?? "";
            if (result.Packs is { Count: > 0 }) _all.AddRange(result.Packs);

            // 存档 / 截图跟着实例走：开了版本隔离就在实例目录里；后端没给就退回全局目录。
            _savesDir = string.IsNullOrWhiteSpace(result.SavesDir) ? Path.Combine(_gameDir, "saves") : result.SavesDir;
            _shotsDir = string.IsNullOrWhiteSpace(result.ScreenshotsDir) ? Path.Combine(_gameDir, "screenshots") : result.ScreenshotsDir;
            var worlds = AddWorlds(_savesDir);
            var shots = AddShots(_shotsDir);
            var packs = _all.Count(x => x.Kind == "resourcepack");
            var shaders = _all.Count(x => x.Kind == "shaderpack");
            _packCount = packs;
            _shaderCount = shaders;
            _worldCount = worlds;
            _shotCount = shots;
            TabPacks.Content = $"资源包 {packs}";
            TabShaders.Content = $"光影 {shaders}";
            TabWorlds.Content = $"存档 {worlds}";
            TabShots.Content = $"截图 {shots}";
            NotifyCounts();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ShowError(Diagnostics.FriendlyMessage(ex));
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>存档在该实例的 saves 目录（隔离实例在实例目录里）；只列目录，不读 level.dat，避免每次刷新扫大文件。</summary>
    private int AddWorlds(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return 0;
        if (!Directory.Exists(root)) return 0;

        var rows = new List<(DateTime Stamp, PackInfo Info)>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var level = Path.Combine(dir, "level.dat");
                var stamp = File.Exists(level) ? File.GetLastWriteTime(level) : Directory.GetLastWriteTime(dir);
                var icon = Path.Combine(dir, "icon.png");
                rows.Add((stamp, new PackInfo
                {
                    Kind = "world",
                    Name = name,
                    Description = $"最后游玩 {AgoText(stamp)}",
                    Path = dir,
                    IconPath = File.Exists(icon) ? icon : "",
                }));
            }
            catch
            {
                // 单个存档读不到不影响其它行。
            }
        }

        var ordered = rows.OrderByDescending(x => x.Stamp).Select(x => x.Info).ToList();
        _all.AddRange(ordered);
        return ordered.Count;
    }

    /// <summary>截图在该实例的 screenshots 目录，按修改时间倒序。</summary>
    private int AddShots(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return 0;
        if (!Directory.Exists(root)) return 0;

        var rows = new List<(DateTime Stamp, PackInfo Info)>();
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new FileInfo(file);
                rows.Add((info.LastWriteTime, new PackInfo
                {
                    Kind = "screenshot",
                    Name = info.Name,
                    Description = $"截屏 · {AgoText(info.LastWriteTime)}",
                    Path = file,
                    IconPath = file,
                    Size = info.Length,
                }));
            }
            catch
            {
                // 跳过被占用或刚被删除的文件。
            }
        }

        var ordered = rows.OrderByDescending(x => x.Stamp).Select(x => x.Info).ToList();
        _all.AddRange(ordered);
        return ordered.Count;
    }

    private static string AgoText(DateTime local)
    {
        var span = DateTime.Now - local;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return local.ToString("M月d日");
    }

    private void ShowError(string message)
    {
        EmptyPanel.Glyph = "\uE783";
        EmptyPanel.Title = "读取实例内容失败";
        EmptyPanel.Hint = ClipText(message);
        RetryButton.Visibility = Visibility.Visible;
        EmptyHost.Visibility = Visibility.Visible;
        PackList.Visibility = Visibility.Collapsed;
    }

    private void QueryBox_QueryChanged(object? sender, string text)
    {
        _query = (text ?? string.Empty).Trim();
        ApplyFilter();
    }

    public bool TryFocusSearch() => QueryBox.FocusInput();

    public void ApplySearch(string query)
    {
        QueryBox.Text = query ?? string.Empty;
        _query = (query ?? string.Empty).Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // 页内搜索框（QueryBox）按名称过滤，再按资源包/光影/存档/截图的当前页签筛选。
        IEnumerable<PackInfo> query = _all.Where(p => p.Kind == _kind);
        if (_query.Length > 0)
        {
            query = query.Where(p =>
                p.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                p.Path.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = query.ToList();
        PackList.ItemsSource = filtered;
        RetryButton.Visibility = Visibility.Collapsed;
        var folderLabel = _kind switch
        {
            "world" => "打开存档目录",
            "screenshot" => "打开截图目录",
            _ => "打开资源目录",
        };
        OpenFolderText.Text = folderLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OpenFolderButton, folderLabel);
        ImportButton.Visibility = _kind == "screenshot" ? Visibility.Collapsed : Visibility.Visible;

        if (filtered.Count > 0)
        {
            PackList.Visibility = Visibility.Visible;
            EmptyHost.Visibility = Visibility.Collapsed;
            return;
        }

        PackList.Visibility = Visibility.Collapsed;
        EmptyHost.Visibility = Visibility.Visible;
        switch (_kind)
        {
            case "world":
                EmptyPanel.Glyph = "\uE8B7";
                EmptyPanel.Title = "这个游戏还没有存档";
                EmptyPanel.Hint = "进入游戏创建一个世界，回来刷新就能看到。";
                break;
            case "screenshot":
                EmptyPanel.Glyph = "\uE722";
                EmptyPanel.Title = "这个游戏还没有截图";
                EmptyPanel.Hint = "在游戏里按 F2 截图，回来刷新就能看到。";
                break;
            default:
                var isShader = _kind == "shaderpack";
                EmptyPanel.Glyph = "\uE7B8";
                EmptyPanel.Title = isShader ? "这个实例还没有光影" : "这个实例还没有资源包";
                EmptyPanel.Hint = isShader
                    ? "把光影包放进实例目录的 shaderpacks，再回来刷新。"
                    : "把资源包放进实例目录的 resourcepacks，再回来刷新。";
                break;
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressKind) return;
        if (sender is not RadioButton { Tag: string kind }) return;
        if (string.Equals(_kind, kind, StringComparison.Ordinal)) return;
        _kind = kind;
        ApplyFilter();
        NotifyCounts();
    }

    /// <summary>外层筛选胶囊调用：切换资源类型但不重复切换内层页签状态。</summary>
    public void SelectKind(string kind)
    {
        if (kind is not ("resourcepack" or "shaderpack" or "world" or "screenshot")) return;
        if (string.Equals(_kind, kind, StringComparison.Ordinal)) return;
        _kind = kind;
        _suppressKind = true;
        (kind switch
        {
            "shaderpack" => TabShaders,
            "world" => TabWorlds,
            "screenshot" => TabShots,
            _ => TabPacks,
        }).IsChecked = true;
        _suppressKind = false;
        ApplyFilter();
        NotifyCounts();
    }

    private void NotifyCounts() => CountsChanged?.Invoke(this, Counts);

    /// <summary>组内 ←/→ 切换单选：RadioButton 默认只移焦点，这里补上「选中跟着焦点走」。</summary>
    private void ChipGroup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        if (sender is not Panel panel) return;
        var chips = panel.Children.OfType<RadioButton>().ToList();
        if (chips.Count == 0) return;
        var index = chips.FindIndex(c => c.IsChecked == true);
        if (index < 0) index = 0;
        var next = e.Key == Windows.System.VirtualKey.Left ? index - 1 : index + 1;
        if (next < 0 || next >= chips.Count) return;
        chips[next].IsChecked = true;
        chips[next].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            await LoadAsync();
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_versionId))
        {
            ShowToast(InfoBarSeverity.Warning, "没有选择实例", "先在版本管理里选一个实例，再导入内容。");
            return;
        }

        ImportButton.IsEnabled = false;
        try
        {
            if (_kind == "world")
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                };
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, App.MainWindow.WindowHandle);
                folderPicker.FileTypeFilter.Add("*");
                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder is null) return;

                var result = await App.Backend.ImportAssetAsync("world", _versionId, folder.Path, _gameDir);
                ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                    result.Ok ? "已导入存档" : "导入失败", result.Message);
                if (result.Ok) await LoadAsync();
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.WindowHandle);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".zip");
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            var imported = 0;
            var lastError = "";
            foreach (var file in files)
            {
                var result = await App.Backend.ImportAssetAsync(_kind, _versionId, file.Path, _gameDir);
                if (result.Ok) imported++;
                else lastError = result.Message;
            }

            if (imported > 0)
            {
                ShowToast(InfoBarSeverity.Success, "已导入",
                    imported == files.Count ? $"{imported} 个文件已导入。" : $"{imported} / {files.Count} 个文件已导入。{lastError}");
                await LoadAsync();
            }
            else
            {
                ShowToast(InfoBarSeverity.Error, "导入失败", string.IsNullOrWhiteSpace(lastError) ? "没有可导入的文件。" : lastError);
            }
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "导入失败", Diagnostics.FriendlyMessage(ex));
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private static void ShowToast(InfoBarSeverity severity, string title, string message) =>
        TaskCenter.PostFeedback(severity, title, message);

    /// <summary>存档行「导入数据包」：选本地 .zip/.jar，逐个装进这个存档的 datapacks；重名的跳过并说明。</summary>
    private async void ImportDatapack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string save } button || string.IsNullOrWhiteSpace(save)) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".jar");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        button.IsEnabled = false;
        try
        {
            var ok = 0;
            var lastError = "";
            foreach (var file in files)
            {
                var result = await App.Backend.InstallDatapackAsync(_versionId, save, file.Path);
                if (result.Ok) ok++;
                else lastError = result.Message;
            }
            if (ok == files.Count)
                ShowToast(InfoBarSeverity.Success, "已导入数据包", $"{ok} 个数据包已装进「{save}」，下次进这个存档生效。");
            else if (ok > 0)
                ShowToast(InfoBarSeverity.Warning, "部分导入", $"{ok} / {files.Count} 个已装进「{save}」。{lastError}");
            else
                ShowToast(InfoBarSeverity.Error, "导入失败", lastError);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _kind switch
        {
            "world" => _savesDir,
            "screenshot" => _shotsDir,
            "shaderpack" => Path.Combine(_dir, "shaderpacks"),
            "resourcepack" => Path.Combine(_dir, "resourcepacks"),
            _ => _dir,
        };
        if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder) && !string.IsNullOrWhiteSpace(_dir)) folder = _dir;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        OpenInExplorer(folder);
    }

    private void PackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PackInfo item) return;
        if (item.Kind == "world")
        {
            OpenInExplorer(item.Path);
            return;
        }
        if (item.Kind != "screenshot" || !File.Exists(item.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch
        {
            // 没有图片查看器时保持列表可继续操作。
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 目录不可用时静默忽略。
        }
    }

    private void Home_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => App.MainWindow.Navigate("home");

    private static string ClipText(string? message)
    {
        var text = (message ?? "").Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}

/// <summary>资源包 / 光影 / 存档 / 截图的计数，供外层筛选胶囊显示。</summary>
public sealed record AssetsCounts(int Packs, int Shaders, int Worlds, int Shots);

/// <summary>列表行的可见性帮手：x:Bind 只认方法调用，不认内联三元。</summary>
public static class PackVisuals
{
    /// <summary>说明列为空时整行收起，不留一行空白。</summary>
    public static Visibility VisibleWhenText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
