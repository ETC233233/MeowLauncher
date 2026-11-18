using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using MeowLauncher.WinUI.Services;
using Windows.UI;

namespace MeowLauncher.WinUI.Views;

/// <summary>首次启动的新手引导：全窗覆盖，走完写 Onboarded，之后不再出现。</summary>
public sealed partial class OnboardingView : UserControl
{
    private readonly AppPreferences _prefs = App.MainWindow?.Preferences ?? AppPreferencesStore.Load();
    private readonly List<string> _dirs = new();
    private string _ownDir = "";
    private string _primary = "";
    private int _screen = 1;
    private string _theme = "System";
    private string _accent = "#0F6CBD";
    private string _accountKind = "";
    private string _accountName = "";
    private bool _busy;

    /// <summary>引导结束（完成或跳过）时触发，由主窗负责播放淡出动画。</summary>
    public event Action? Finished;

    public OnboardingView()
    {
        InitializeComponent();
        LoadDirs();
        UpdateScreen(1, animate: false);
        ApplyThemeButtons();
        ApplyAccentSwatch();
    }

    // ------------------------------------------------------------------ 目录

    private void LoadDirs()
    {
        _ownDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", ".minecraft");
        _dirs.Clear();
        foreach (var dir in GameDirectoryResolver.Resolve(_prefs))
        {
            var norm = GameDirectoryResolver.Normalize(dir);
            if (norm.Length == 0) continue;
            if (string.Equals(norm, _ownDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (_dirs.Any(d => string.Equals(d, norm, StringComparison.OrdinalIgnoreCase))) continue;
            _dirs.Add(norm);
        }
        if (_primary.Length == 0) _primary = _dirs.FirstOrDefault() ?? _ownDir;
        BuildDirList();
    }

    /// <summary>「找到的目录」数量决定正文三态：有外部目录 / 只剩自有目录 / 一个都没有。</summary>
    private void BuildDirList()
    {
        DirList.Children.Clear();
        var others = _dirs.Count;
        DirLead.Text = others > 0
            ? "我们在你的电脑上找到了其他启动器的游戏目录，但它有时不是完全的，可能需要你手动添加未列出的目录以在 MeowLauncher 管理你原有的实例。\n建议使用我们默认的启动器版本目录，方便区分其他启动器的实例文件夹。"
            : _allRemoved
                ? "我们未在你的电脑上找到任何 Minecraft 游戏目录，因此我们为你自动创建了受 MeowLauncher 管理的默认目录。\n如果确实存在我们未发现的其他启动器的版本目录，这需要你手动添加这些文件夹。"
                : "我们未在你的电脑上找到受 MeowLauncher 管理以外的其他启动器游戏目录，但这往往很普遍，所以可能需要你手动添加其他启动器的版本目录。\n建议使用我们默认的启动器版本目录，方便与其他启动器的实例文件夹区分开。";

        // 自有目录永远排第一
        DirList.Children.Add(BuildRow(_ownDir, "由启动器创建 · 全新安装", "推荐", removable: false));
        foreach (var dir in _dirs)
        {
            var label = System.IO.Directory.Exists(System.IO.Path.Combine(dir, "versions")) ? "最近使用" : "";
            DirList.Children.Add(BuildRow(dir, VersionCountText(dir), label, removable: true));
        }
    }

    private bool _allRemoved;

    private static string VersionCountText(string dir)
    {
        try
        {
            var versions = System.IO.Path.Combine(dir, "versions");
            if (!System.IO.Directory.Exists(versions)) return "目录里还没有版本";
            var count = System.IO.Directory.GetDirectories(versions).Length;
            return count == 0 ? "目录里还没有版本" : count + " 个版本";
        }
        catch { return ""; }
    }

    private UIElement BuildRow(string dir, string sub, string badge, bool removable)
    {
        var grid = new Grid { Tag = dir, Height = 56, Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (removable) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selected = string.Equals(dir, _primary, StringComparison.OrdinalIgnoreCase);
        var dot = new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = selected ? 4 : 1.4,
            Stroke = (Brush)Application.Current.Resources["AppAccentBrush"],
            Fill = selected ? (Brush)Application.Current.Resources["AppAccentBrush"] : new SolidColorBrush(Colors.Transparent),
        };
        // 选中时用「实心点 + 外圈」近似单选圆点
        if (selected) { dot.Width = 12; dot.Height = 12; dot.StrokeThickness = 0; }
        else dot.Width = 14;

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = dir,
            FontSize = 13.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
        });
        texts.Children.Add(new TextBlock
        {
            Text = sub,
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(dot);
        grid.Children.Add(texts);

        if (badge.Length > 0)
        {
            var b = new TextBlock
            {
                Text = badge,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
            };
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }

        if (removable)
        {
            var del = new Button
            {
                Content = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = dir,
            };
            ToolTipService.SetToolTip(del, "移除");
            del.Click += (s, _) =>
            {
                if (s is Button { Tag: string target })
                {
                    _dirs.RemoveAll(d => string.Equals(d, target, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(_primary, target, StringComparison.OrdinalIgnoreCase)) _primary = _ownDir;
                    if (_dirs.Count == 0) _allRemoved = true;
                    BuildDirList();
                }
            };
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);
        }

        var btn = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Background = (Brush)Application.Current.Resources["AppChipBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = selected
                ? (Brush)Application.Current.Resources["AppAccentBrush"]
                : (Brush)Application.Current.Resources["AppStrokeBrush"],
            Tag = dir,
        };
        btn.Click += (s, _) =>
        {
            if (s is Button { Tag: string picked })
            {
                _primary = picked;
                _allRemoved = false;
                BuildDirList();
            }
        };
        return btn;
    }

    private async void AddDir_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window) return;
        if (_dirs.Count >= GameDirectoryResolver.MaxDirs - 1)
        {
            DirLead.Text = "目录最多 6 个，先移除一个再添加吧。";
            return;
        }
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var dir = GameDirectoryResolver.Normalize(folder.Path);
        if (dir.Length == 0) return;
        if (_dirs.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
        {
            DirLead.Text = "这个目录已经在列表里。";
            return;
        }
        _dirs.Add(dir);
        _primary = dir;
        _allRemoved = false;
        BuildDirList();
    }

    // ------------------------------------------------------------------ 账号

    private void OptMs_Click(object sender, RoutedEventArgs e)
    {
        _accountKind = "microsoft";
        MsPanel.Visibility = Visibility.Visible;
        OfflinePanel.Visibility = Visibility.Collapsed;
        PaintOptions();
        MsCode.Text = "点下面的按钮，会在浏览器里打开 Microsoft 登录页。";
    }

    private void OptOffline_Click(object sender, RoutedEventArgs e)
    {
        _accountKind = "offline";
        MsPanel.Visibility = Visibility.Collapsed;
        OfflinePanel.Visibility = Visibility.Visible;
        PaintOptions();
    }

    private void PaintOptions()
    {
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var stroke = (Brush)Application.Current.Resources["AppStrokeBrush"];
        OptMs.BorderBrush = _accountKind == "microsoft" ? accent : stroke;
        OptOffline.BorderBrush = _accountKind == "offline" ? accent : stroke;
        OptMs.BorderThickness = new Thickness(_accountKind == "microsoft" ? 2 : 1);
        OptOffline.BorderThickness = new Thickness(_accountKind == "offline" ? 2 : 1);
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        MsLoginBtn.IsEnabled = false;
        try
        {
            var start = await App.Backend.StartMicrosoftAsync();
            if (!start.Ok)
            {
                MsCode.Text = "打开登录页失败：" + start.Message;
                return;
            }
            MsCode.Text = "已在浏览器里打开登录页，验证码：" + start.UserCode + "（请勿关闭窗口，登录完成后会自动继续）";
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(start.VerificationUri)); } catch { }
            var deadline = DateTime.Now.AddSeconds(Math.Max(60, start.ExpiresInSeconds));
            while (DateTime.Now < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, start.IntervalSeconds)));
                var poll = await App.Backend.PollMicrosoftAsync(start.DeviceCode);
                if (poll.Ok)
                {
                    _accountKind = "microsoft";
                    _accountName = poll.Name;
                    MsCode.Text = "已登录：" + poll.Name;
                    return;
                }
                if (!poll.Pending && poll.Message.Length > 0)
                {
                    MsCode.Text = poll.Message;
                    return;
                }
            }
            MsCode.Text = "登录超时了，可以点上面的按钮再试一次，或先跳过。";
        }
        finally
        {
            MsLoginBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private async void OfflineCreate_Click(object sender, RoutedEventArgs e)
    {
        var name = (OfflineName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            MsCode.Text = "先填一个玩家名。";
            return;
        }
        if (_busy) return;
        _busy = true;
        try
        {
            var result = await App.Backend.AddOfflineAccountAsync(name, false);
            if (result.Ok)
            {
                _accountKind = "offline";
                _accountName = name;
                MsCode.Text = "离线账户已创建：" + name;
            }
            else MsCode.Text = result.Message;
        }
        finally { _busy = false; }
    }

    private void SkipAccount_Click(object sender, RoutedEventArgs e)
    {
        _accountKind = "skip";
        _accountName = "";
        Next_Click(sender, e);
    }

    // ------------------------------------------------------------------ 外观

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => PickTheme("Light");
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => PickTheme("Dark");
    private void ThemeAuto_Click(object sender, RoutedEventArgs e) => PickTheme("System");

    private void PickTheme(string mode)
    {
        _theme = mode;
        App.MainWindow?.SetThemeMode(mode, false);
        ApplyThemeButtons();
    }

    private void ApplyThemeButtons()
    {
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var stroke = (Brush)Application.Current.Resources["AppStrokeBrush"];
        void Paint(Button b, bool on, Brush normalBorder)
        {
            b.BorderBrush = on ? accent : normalBorder;
            b.BorderThickness = new Thickness(on ? 2 : 1);
        }
        Paint(ThemeLight, _theme == "Light", stroke);
        Paint(ThemeDark, _theme == "Dark", stroke);
        Paint(ThemeAuto, _theme == "System", stroke);
    }

    private void Accent_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not Ellipse { Tag: string hex }) return;
        _accent = hex;
        try
        {
            var c = hex.TrimStart('#');
            var color = ColorHelper.FromArgb(255,
                Convert.ToByte(c.Substring(0, 2), 16),
                Convert.ToByte(c.Substring(2, 2), 16),
                Convert.ToByte(c.Substring(4, 2), 16));
            ThemeColorService.Apply(color, App.MainWindow?.IsDarkTheme ?? true);
        }
        catch { }
        ApplyAccentSwatch();
    }

    private void ApplyAccentSwatch()
    {
        var items = new FrameworkElement[] { Sw0, Sw1, Sw2, Sw3, Sw4, Sw5, Sw6, Sw7 };
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is not Ellipse dot) continue;
            var hex = dot.Tag as string ?? "";
            var on = string.Equals(hex, _accent, StringComparison.OrdinalIgnoreCase);
            dot.StrokeThickness = on ? 2 : 0;
            dot.Stroke = on ? (Brush)Application.Current.Resources["AppTextBrush"] : null;
            dot.Width = on ? 30 : 28;
            dot.Height = on ? 30 : 28;
        }
    }

    // ------------------------------------------------------------------ 流程

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_screen >= 6)
        {
            Complete();
            return;
        }
        UpdateScreen(_screen + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_screen <= 1) return;
        UpdateScreen(_screen - 1);
    }

    private void UpdateScreen(int n, bool animate = true)
    {
        _screen = n;
        App.Trace("Onboarding: screen " + n);
        var screens = new FrameworkElement[] { Scr1, Scr2, Scr3, Scr4, Scr5, Scr6 };
        var arts = new FrameworkElement[] { Art1, Art2, Art3, Art4, Art5, Art6 };
        for (var i = 0; i < 6; i++)
        {
            screens[i].Visibility = i == n - 1 ? Visibility.Visible : Visibility.Collapsed;
            arts[i].Visibility = i == n - 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        BackBtn.Visibility = n == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = n switch
        {
            1 => "开始",
            _ when n == 6 => "进入启动器",
            _ => "下一步",
        };
        if (n == 4) LoadDirs();
        if (n == 6) BuildSummary();

        if (!animate) return;
        FadeIn(arts[n - 1]);
        FadeIn(screens[n - 1]);
    }

    private static void FadeIn(FrameworkElement host)
    {
        host.Opacity = 0;
        var story = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(190)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, host);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        story.Begin();
    }

    private void BuildSummary()
    {
        Summary.Children.Clear();
        AddSummaryRow("账号", _accountName.Length > 0 ? _accountName : "稍后再说");
        AddSummaryRow("账号类型", _accountKind switch
        {
            "microsoft" => "正版账号",
            "offline" => "离线账户",
            _ => "未登录",
        });
        AddSummaryRow("外观", _theme switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随系统" });
        AddSummaryRow("主题色", AccentName(_accent));
        AddSummaryRow("默认游戏目录", _primary.Length > 0 ? _primary : _ownDir);
    }

    private void AddSummaryRow(string label, string value)
    {
        var grid = new Grid { Padding = new Thickness(0, 13, 0, 13), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
        });
        var v = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        var border = new Border
        {
            Child = grid,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)Application.Current.Resources["AppStrokeBrush"],
        };
        Summary.Children.Add(border);
    }

    private static string AccentName(string hex) => hex.ToUpperInvariant() switch
    {
        "#0F6CBD" => "蓝",
        "#4F52B2" => "靛",
        "#8764B8" => "紫",
        "#C239B3" => "品红",
        "#C4314B" => "红",
        "#CA5010" => "橙",
        "#107C10" => "绿",
        "#038387" => "青",
        _ => "自定义",
    };

    // ------------------------------------------------------------------ 收尾

    private async void Complete()
    {
        try
        {
            _prefs.Onboarded = true;
            _prefs.OnboardAccountKind = _accountKind;
            _prefs.CheckUpdatesOnStartup = TgCheck.IsOn;
            _prefs.AutoDownloadUpdate = TgDownload.IsOn;
            _prefs.AutoInstallUpdate = TgInstall.IsOn;
            _prefs.ThemeMode = _theme;
            _prefs.ThemeColorMode = ThemeColorService.ModeCustom;
            _prefs.ThemeColorHex = _accent;
            var ordered = new List<string> { _primary.Length > 0 ? _primary : _ownDir };
            foreach (var dir in _dirs)
                if (!string.Equals(dir, ordered[0], StringComparison.OrdinalIgnoreCase)) ordered.Add(dir);
            _prefs.McDirs = ordered;
            _prefs.McDir = ordered[0];
            AppPreferencesStore.Save(_prefs);
            try { await App.Backend.SetGameDirAsync(ordered[0]); } catch { }
        }
        finally
        {
            if (Finished is null) Visibility = Visibility.Collapsed;
            else Finished();
        }
    }
}
