using System.Collections.ObjectModel;
using System.Diagnostics;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace MeowLauncher.WinUI.Views;

public sealed partial class LogsPage : Page
{
    public sealed class LogLine
    {
        public string Text { get; init; } = "";
        public Brush Brush { get; init; } = ResolveDefaultBrush();

        // t55：默认刷从当前主题字典取 AppText2Brush，不再硬编码 RGB（实际每个 LogLine 都带显式 Brush，此值仅兜底）
        private static Brush ResolveDefaultBrush()
        {
            try
            {
                var resources = Application.Current?.Resources;
                if (resources is not null)
                {
                    var theme = (App.MainWindow?.Content as FrameworkElement)?.ActualTheme
                                ?? ElementTheme.Default;
                    var themeKey = theme == ElementTheme.Light ? "Light" : "Default";
                    if (resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary)
                        && dictionary is ResourceDictionary themed
                        && themed.TryGetValue("AppText2Brush", out var themedBrush)
                        && themedBrush is Brush brush)
                    {
                        return brush;
                    }
                }
            }
            catch
            {
                // 主题资源缺失时退系统灰，仅兜底。
            }
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }

    private readonly ObservableCollection<LogLine> _visible = new();
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<LogLine> _all = new();
    private List<string> _raw = new();
    private string _file = "";
    private string _dir = "";
    private bool _suppressFileChange;
    private bool _loading;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _visible;
        _followTimer.Tick += async (_, _) => await LoadAsync(_file, quiet: true);
        Switcher.Switched += (VersionInfo picked) =>
        {
            _ = LoadAsync(string.IsNullOrWhiteSpace(_file) ? null : _file);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string requested && !string.IsNullOrWhiteSpace(requested))
        {
            _file = requested.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                ? requested
                : requested + ".log";
        }
        _ = LoadAsync(string.IsNullOrWhiteSpace(_file) ? null : _file);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _followTimer.Stop();
    }

    private async Task LoadAsync(string? file, bool quiet = false)
    {
        if (_loading) return;
        _loading = true;
        if (!quiet)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            EmptyHost.Visibility = Visibility.Collapsed;
            LogList.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = false;
        }
        try
        {
            var result = await App.Backend.GetLogsAsync(file);
            if (!result.Ok)
            {
                ShowMessage("\uE783", "读取日志失败", result.Message, showRetry: true);
                SummaryText.Text = "读取失败";
                return;
            }

            _file = result.File;
            _dir = result.Dir;
            SummaryText.Text = result.Truncated
                ? $"最近 {result.Lines.Count} 行"
                : $"{result.Lines.Count} 行";

            _suppressFileChange = true;
            FileBox.ItemsSource = result.Files;
            FileBox.SelectedItem = result.Files.FirstOrDefault(f => f.Name == result.File);
            _suppressFileChange = false;

            _raw = result.Lines;
            _all = result.Lines.Select(line => new LogLine { Text = line, Brush = ColorFor(line) }).ToList();
            ApplyFilter();

            if (FollowSwitch.IsChecked == true && _visible.Count > 0)
            {
                LogList.ScrollIntoView(_visible[^1]);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("\uE783", "读取日志失败", Diagnostics.FriendlyMessage(ex), showRetry: true);
            SummaryText.Text = "读取失败";
        }
        finally
        {
            _loading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var mode = FilterBox.SelectedIndex;
        _visible.Clear();
        foreach (var line in _all)
        {
            if (mode == 1 && !IsError(line.Text)) continue;
            if (mode == 2 && !IsWarning(line.Text)) continue;
            _visible.Add(line);
        }

        if (_all.Count == 0)
        {
            ShowMessage("\uE7C3", "这个日志是空的", "换一个文件，或者启动一次游戏再看。", showRetry: false);
            return;
        }
        if (_visible.Count == 0)
        {
            ShowMessage("\uE7C3",
                mode == 1 ? "没有错误行" : "没有警告行",
                "把筛选切回「全部」看看完整日志。",
                showRetry: false);
            return;
        }

        EmptyHost.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        LogList.Visibility = Visibility.Visible;
    }

    private void ShowMessage(string glyph, string title, string hint, bool showRetry)
    {
        EmptyPanel.Glyph = glyph;
        EmptyPanel.Title = title;
        EmptyPanel.Hint = ClipText(hint);
        RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        EmptyHost.Visibility = Visibility.Visible;
        LogList.Visibility = Visibility.Collapsed;
    }

    private static bool IsError(string line) =>
        line.Contains("[错误]", StringComparison.Ordinal)
        || line.Contains("[失败]", StringComparison.Ordinal)
        || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Traceback", StringComparison.Ordinal);

    private static bool IsWarning(string line) =>
        IsError(line)
        || line.Contains("[警告]", StringComparison.Ordinal)
        || line.Contains("[提示]", StringComparison.Ordinal)
        || line.Contains("WARN", StringComparison.OrdinalIgnoreCase);

    /// <summary>级别颜色全部走主题令牌：错误 bad / 警告 warn / 完成 good / 其它 AppText2。</summary>
    private static Brush ColorFor(string line)
    {
        if (IsError(line)) return PagePalette.ThemeBrush("AppBadBrush");
        if (IsWarning(line)) return PagePalette.ThemeBrush("AppWarnBrush");
        if (line.Contains("[完成]", StringComparison.Ordinal)
            || line.Contains("[启动]", StringComparison.Ordinal)
            || line.Contains("[安装]", StringComparison.Ordinal))
        {
            return PagePalette.ThemeBrush("AppGoodBrush");
        }
        return PagePalette.ThemeBrush("AppText2Brush");
    }

    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFileChange) return;
        if (FileBox.SelectedItem is LogFileInfo info) await LoadAsync(info.Name);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_all.Count > 0) ApplyFilter();
    }

    private void Follow_Toggled(object sender, RoutedEventArgs e)
    {
        if (FollowSwitch.IsChecked == true)
        {
            _followTimer.Start();
            if (_visible.Count > 0) LogList.ScrollIntoView(_visible[^1]);
        }
        else
        {
            _followTimer.Stop();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await LoadAsync(string.IsNullOrWhiteSpace(_file) ? null : _file);

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            await LoadAsync(string.IsNullOrWhiteSpace(_file) ? null : _file);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // 打不开资源管理器时忽略。
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
