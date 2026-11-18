using System.Collections.ObjectModel;
using System.Diagnostics;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace MeowLauncher.WinUI.Views;

public sealed partial class ModsPage : Page, IPageSearchFilter
{
    private readonly ObservableCollection<ModInfo> _items = new();
    private List<ModInfo> _all = new();
    private string _versionId = "";
    private string _query = "";
    private string _dir = "";
    private bool _loading;
    private bool _busy;
    private string _filter = "all";
    private bool _suppressFilter;

    public ModsPage()
    {
        InitializeComponent();
        ModList.ItemsSource = _items;

        // 默认选中「全部」：走控件自身的选中态，不再由代码刷 Background。
        _suppressFilter = true;
        FilterAll.IsChecked = true;
        _suppressFilter = false;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is TabNav)
        {
            PageHeader.Visibility = Visibility.Collapsed;
            PageRoot.RowDefinitions[0].Height = new GridLength(0);
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyHost.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ModList.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await App.Backend.GetSnapshotAsync();
            _versionId = snapshot?.Current?.Id ?? snapshot?.Selected ?? "";
            if (string.IsNullOrWhiteSpace(_versionId))
            {
                ShowError("没有选择实例，请先在版本管理中选一个。");
                return;
            }

            var result = await App.Backend.GetModsAsync(_versionId);
            if (result is null || !result.Ok)
            {
                ShowError(result?.Message ?? "读取 Mod 列表失败。");
                return;
            }

            _versionId = result.Version;
            _dir = result.Dir;
            _all = result.Mods ?? new List<ModInfo>();
            // 检查更新的结果跟着文件名带过来；文件已被换掉/删掉的自然对不上，就不再显示。
            foreach (var mod in _all)
                if (_updates.TryGetValue(mod.File, out var pending)) mod.Update = pending;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ShowError($"读取 Mod 列表失败：{Diagnostics.FriendlyMessage(ex)}");
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        EmptyPanel.Glyph = "\uE783";
        EmptyPanel.Title = "读取 Mod 列表失败";
        EmptyPanel.Hint = ClipText(message);
        RetryButton.Visibility = Visibility.Visible;
        EmptyHost.Visibility = Visibility.Visible;
        ModList.Visibility = Visibility.Collapsed;
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
        var enabled = _all.Count(m => m.Enabled);
        var disabled = _all.Count - enabled;
        FilterEnabled.Visibility = enabled > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterDisabled.Visibility = disabled > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 一个 Mod 都没有时没有可筛的东西，搜索框退回放大镜，不留漏斗按钮。
        QueryBox.IsFilter = _all.Count > 0;

        // 当前筛选如果已经没有任何结果，退回「全部」，避免停在一个空的分段上。
        if ((_filter == "enabled" && enabled == 0) || (_filter == "disabled" && disabled == 0))
        {
            _filter = "all";
            _suppressFilter = true;
            FilterAll.IsChecked = true;
            _suppressFilter = false;
        }
        var updatable = _all.Count(m => m.Update is not null);
        ModCountText.Text = updatable > 0 ? $"{_all.Count} 个 Mod · {updatable} 个可更新" : $"{_all.Count} 个 Mod";

        // 页内搜索框（QueryBox）按名称过滤，再叠加启用状态筛选。
        IEnumerable<ModInfo> filtered = _all;
        if (_query.Length > 0)
        {
            filtered = filtered.Where(m =>
                m.DisplayName.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                m.EnglishName.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                m.ModId.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                m.File.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }
        if (_filter == "enabled") filtered = filtered.Where(m => m.Enabled);
        else if (_filter == "disabled") filtered = filtered.Where(m => !m.Enabled);

        var list = filtered.ToList();
        _items.Clear();
        foreach (var mod in list) _items.Add(mod);
        EmptyPanel.Glyph = "\uE8B7";
        RetryButton.Visibility = Visibility.Collapsed;

        if (list.Count > 0)
        {
            ModList.Visibility = Visibility.Visible;
            EmptyHost.Visibility = Visibility.Collapsed;
            return;
        }

        ModList.Visibility = Visibility.Collapsed;
        EmptyHost.Visibility = Visibility.Visible;
        if (_all.Count == 0)
        {
            EmptyPanel.Title = "这个实例还没有 Mod";
            EmptyPanel.Hint = "可前往在线资源页下载，或将已有mod的拖入此窗口或文件夹。";
        }
        else
        {
            EmptyPanel.Title = _filter == "enabled" ? "没有已启用的 Mod" : "没有已停用的 Mod";
            EmptyPanel.Hint = "把筛选切回「全部」看看完整的 Mod 列表。";
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressFilter) return;
        if (sender is not RadioButton { Tag: string filter }) return;
        if (string.Equals(_filter, filter, StringComparison.Ordinal)) return;
        _filter = filter;
        ApplyFilter();
    }

    /// <summary>组内 ←/→ 切换单选：RadioButton 默认只移焦点，这里补上「选中跟着焦点走」。</summary>
    private void ChipGroup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        if (sender is not Panel panel) return;
        var chips = panel.Children.OfType<RadioButton>().Where(c => c.Visibility == Visibility.Visible).ToList();
        if (chips.Count == 0) return;
        var index = chips.FindIndex(c => c.IsChecked == true);
        if (index < 0) index = 0;
        var next = e.Key == Windows.System.VirtualKey.Left ? index - 1 : index + 1;
        if (next < 0 || next >= chips.Count) return;
        chips[next].IsChecked = true;
        chips[next].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private readonly Dictionary<string, ModUpdate> _updates = new(StringComparer.OrdinalIgnoreCase);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_versionId)) return;
        _busy = true;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesText.Text = "正在检查…";
        try
        {
            var result = await App.Backend.CheckModUpdatesAsync(_versionId);
            if (!result.Ok)
            {
                ShowToast($"检查更新失败：{ClipText(result.Message)}", InfoBarSeverity.Error);
                return;
            }
            _updates.Clear();
            foreach (var u in result.Updates) _updates[u.File] = u;
            foreach (var mod in _all)
                mod.Update = _updates.TryGetValue(mod.File, out var found) ? found : null;
            ApplyFilter();
            ShowToast(result.Updates.Count == 0
                    ? $"已检查 {result.Checked} 个 Mod，都是最新版（只能识别 Modrinth / CurseForge 上有的文件）。"
                    : $"已检查 {result.Checked} 个 Mod，{result.Updates.Count} 个有新版本。",
                InfoBarSeverity.Success);
        }
        finally
        {
            _busy = false;
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesText.Text = "检查更新";
        }
    }

    private async void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string file } button || string.IsNullOrWhiteSpace(file)) return;
        if (!_updates.TryGetValue(file, out var update)) return;

        _busy = true;
        button.IsEnabled = false;
        button.Content = "更新中…";
        try
        {
            var result = await App.Backend.UpdateModAsync(_versionId, update);
            if (result.Ok) _updates.Remove(file);
            ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            await LoadAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>点一行 = 去「获取 › 在线资源」搜这个 Mod（行内的启用 / 更新 / 删除按钮自己处理点击，不会走到这里）。</summary>
    private async void ModList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModInfo mod) return;
        // 先按文件哈希认项目：认得出就直接开详情子页；认不出（自编译、非 Modrinth）再退回按名字搜。
        if (!string.IsNullOrWhiteSpace(mod.File) && !mod.CanMove && !string.IsNullOrWhiteSpace(_versionId))
        {
            try
            {
                var hit = await App.Backend.GetModProjectAsync(_versionId, mod.File);
                if (hit.Ok && hit.ProjectId.Length > 0)
                {
                    var info = await App.Backend.GetProjectInfoAsync(hit.ProjectId);
                    var project = new ModrinthProject
                    {
                        Description = info.Description,
                        Downloads = info.Downloads,
                        Follows = info.Followers,
                        GameVersions = info.GameVersions,
                        Loaders = info.Loaders,
                        Id = hit.ProjectId,
                        Slug = hit.Slug,
                        Title = string.IsNullOrWhiteSpace(hit.Title) ? mod.DisplayName : hit.Title,
                        IconUrl = hit.IconUrl,
                        PageUrl = $"https://modrinth.com/mod/{(hit.Slug.Length > 0 ? hit.Slug : hit.ProjectId)}",
                        ProjectType = info.Ok ? info.ProjectType : "mod",
                    };
                    App.MainWindow?.OpenOnlineProject(project, _versionId);
                    return;
                }
            }
            catch { /* 查不到就按名字搜 */ }
        }
        var query = !string.IsNullOrWhiteSpace(mod.Name) ? mod.Name
            : !string.IsNullOrWhiteSpace(mod.ModId) ? mod.ModId
            : System.IO.Path.GetFileNameWithoutExtension(mod.File ?? "");
        if (string.IsNullOrWhiteSpace(query)) return;
        App.MainWindow?.OpenOnline(query.Trim());
    }

    private async void MoveMisplaced_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string file } button || string.IsNullOrWhiteSpace(file)) return;
        var mod = _all.FirstOrDefault(m => m.File == file);
        if (mod is null || !mod.CanMove) return;

        _busy = true;
        button.IsEnabled = false;
        try
        {
            var result = await App.Backend.MoveMisplacedAsync(file, _versionId, mod.Misplaced);
            ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            await LoadAsync();
        }
        finally
        {
            _busy = false;
            button.IsEnabled = true;
        }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string file } button || string.IsNullOrWhiteSpace(file)) return;

        _busy = true;
        button.IsEnabled = false;
        try
        {
            var result = await App.Backend.ToggleModAsync(file);
            if (!result.Ok && result.Message.Contains("PermissionError", StringComparison.OrdinalIgnoreCase))
            {
                result = ToggleLocally(file);
            }
            ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            await LoadAsync();
        }
        finally
        {
            _busy = false;
            button.IsEnabled = true;
        }
    }

    private BackendResult ToggleLocally(string file)
    {
        try
        {
            var probe = Path.Combine(_dir, ".meow-write-test");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return new BackendResult(false, $"写入测试失败：{ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            var path = Path.Combine(_dir, file);
            if (!File.Exists(path)) return new BackendResult(false, "找不到 Mod 文件。");
            var next = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? path[..^".disabled".Length]
                : path + ".disabled";
            File.Move(path, next);
            return new BackendResult(true, next.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "已停用" : "已启用");
        }
        catch (Exception ex)
        {
            return new BackendResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string file } button || string.IsNullOrWhiteSpace(file)) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移除 Mod",
            Content = $"是否将 {file}移动到回收站？这将不会直接删除它！",
            PrimaryButtonText = "移到回收目录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _busy = true;
        button.IsEnabled = false;
        try
        {
            var result = await App.Backend.DeleteModAsync(file);
            ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            await LoadAsync();
        }
        finally
        {
            _busy = false;
            button.IsEnabled = true;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_versionId))
        {
            ShowToast("没有选择实例，先在版本管理里选一个。", InfoBarSeverity.Warning);
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        foreach (var ext in new[] { ".jar", ".zip" }) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        var imported = 0;
        var lastError = "";
        foreach (var file in files)
        {
            var result = await App.Backend.ImportAssetAsync("mod", _versionId, file.Path);
            if (result.Ok) imported++;
            else lastError = result.Message;
        }

        if (imported == 0)
        {
            ShowToast(string.IsNullOrWhiteSpace(lastError) ? "没有可导入的文件。" : lastError, InfoBarSeverity.Error);
            return;
        }

        ShowToast(imported == files.Count
            ? $"{imported} 个 Mod 已导入。"
            : $"{imported} / {files.Count} 个 Mod 已导入。{lastError}",
            imported == files.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        await LoadAsync();
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowToast($"打开目录失败：{Diagnostics.FriendlyMessage(ex)}", InfoBarSeverity.Error);
        }
    }

    private void Home_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Title = severity == InfoBarSeverity.Error ? "操作失败" : "Mod 管理";
        Toast.Message = ClipText(message);
        Toast.Severity = severity;
        Toast.IsOpen = true;
    }

    private static string ClipText(string? message)
    {
        var text = (message ?? "").Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}

/// <summary>Mod 行的状态语义：启用=good、停用=bad；切换按钮文案跟着状态走。</summary>
public static class ModVisuals
{
    public static Visibility VisibleWhen(bool enabled) =>
        enabled ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HiddenWhen(bool enabled) =>
        enabled ? Visibility.Collapsed : Visibility.Visible;

    public static string ToggleLabel(bool enabled) => enabled ? "停用" : "启用";

    public static string ToggleName(string? name, bool enabled) =>
        (enabled ? "停用 " : "启用 ") + (string.IsNullOrWhiteSpace(name) ? "这个 Mod" : name);
}

/// <summary>
/// 页面级令牌取用：ThemeDictionaries 里的画刷在 C# 里必须按当前主题字典取，
/// 直接 <c>Application.Current.Resources[key]</c> 取不到；Soft 底色由 Tint 令牌色派生。
/// </summary>
internal static class PagePalette
{
    public static Brush ThemeBrush(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            if (resources.TryGetValue(key, out var direct) && direct is Brush directBrush)
                return directBrush;
            foreach (var themeKey in new[] { CurrentThemeKey, "Default" })
            {
                if (resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary)
                    && dictionary is ResourceDictionary theme
                    && theme.TryGetValue(key, out var themed)
                    && themed is Brush themedBrush)
                {
                    return themedBrush;
                }
            }
        }
        // t55：兜底不再硬编码 RGB，取当前主题 AppText2Color，再退系统灰
        foreach (var themeKey in new[] { CurrentThemeKey, "Default" })
        {
            if (resources?.ThemeDictionaries.TryGetValue(themeKey, out var dictionary) == true
                && dictionary is ResourceDictionary theme
                && theme.TryGetValue("AppText2Color", out var themedColor)
                && themedColor is Color fallbackColor)
            {
                return new SolidColorBrush(fallbackColor);
            }
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public static Brush SoftTint(string colorKey, byte alpha = 38)
    {
        if (Application.Current?.Resources.TryGetValue(colorKey, out var value) == true
            && value is Color color)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }
        // t55：兜底不再硬编码 RGB，从主题字典取 AppText2Color 派生 alpha，再退系统灰
        if (Application.Current?.Resources.ThemeDictionaries.TryGetValue(CurrentThemeKey, out var dictionary) == true
            && dictionary is ResourceDictionary theme
            && theme.TryGetValue("AppText2Color", out var themedColor)
            && themedColor is Color fallbackColor)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, fallbackColor.R, fallbackColor.G, fallbackColor.B));
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private static string CurrentThemeKey
    {
        get
        {
            var theme = (App.MainWindow?.Content as FrameworkElement)?.ActualTheme
                        ?? ElementTheme.Default;
            if (theme == ElementTheme.Default)
            {
                theme = Application.Current?.RequestedTheme == ApplicationTheme.Light
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
            }
            return theme == ElementTheme.Light ? "Light" : "Dark";
        }
    }
}
