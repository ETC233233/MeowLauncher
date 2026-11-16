using MeowLauncher.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MeowLauncher.WinUI.Services;

/// <summary>实例 Java 选择、运行时管理及缺失版本的下载引导。</summary>
public static class JavaGuide
{
    internal const string ManageRuntimeTag = "java-management";

    internal static void SetRuntimeChoices(ComboBox choices, IReadOnlyList<JavaInfo> runtimes, string? currentPath = null)
    {
        choices.Items.Clear();
        if (currentPath is not null)
            choices.Items.Add(new ComboBoxItem { Content = "按实例版本自动选择", Tag = "" });

        foreach (var runtime in runtimes)
        {
            var item = new ComboBoxItem { Content = runtime.Label, Tag = runtime.Path };
            ToolTipService.SetToolTip(item, runtime.Path);
            choices.Items.Add(item);
        }
        if (currentPath is null) return;

        var selected = choices.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, currentPath, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = "此实例已指定的 Java", Tag = currentPath };
            ToolTipService.SetToolTip(selected, currentPath);
            choices.Items.Add(selected);
        }
        choices.Items.Add(new ComboBoxItem { Content = "Java 管理…", Tag = ManageRuntimeTag });
        choices.SelectedItem = selected;
    }

    public static async Task<string?> ChooseRuntimeAsync(string message)
    {
        var root = App.MainWindow?.Content?.XamlRoot;
        if (root is null) return null;

        while (true)
        {
            var scan = await App.Backend.GetJavaScanAsync();
            var choices = new ComboBox
            {
                IsEditable = false,
                PlaceholderText = "选择已添加或检测到的 Java",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = scan.Ok && scan.Items.Count > 0,
            };
            SetRuntimeChoices(choices, scan.Items);
            var currentHint = scan.Ok && scan.Items.Count == 0 ? "没有可选的 Java，请先打开 Java 管理。"
                : scan.Ok ? "" : scan.Message;
            var hint = new TextBlock { Text = currentHint, TextWrapping = TextWrapping.Wrap };
            var body = new StackPanel { Spacing = 8, MinWidth = 360 };
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(choices);
            body.Children.Add(hint);
            var dialog = new ContentDialog
            {
                Title = "选择本次启动的 Java",
                Content = body,
                PrimaryButtonText = "仅本次使用",
                SecondaryButtonText = "Java 管理",
                CloseButtonText = "取消",
                IsPrimaryButtonEnabled = false,
                XamlRoot = root,
            };
            choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem is ComboBoxItem;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (choices.SelectedItem is ComboBoxItem { Tag: string path } && File.Exists(path)) return;
                args.Cancel = true;
                hint.Text = "此 Java 已不可用，请打开 Java 管理重新添加。";
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                return (choices.SelectedItem as ComboBoxItem)?.Tag as string;
            if (result != ContentDialogResult.Secondary) return null;
            await ManageRuntimesAsync();
        }
    }

    internal static async Task ManageRuntimesAsync()
    {
        if (App.MainWindow is not { } main || main.Content?.XamlRoot is not { } root) return;

        var add = new Button { Content = "添加 Java…" };
        var refresh = new Button { Content = "重新检测" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(refresh);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var entries = new StackPanel { Spacing = 10 };
        var list = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = entries,
        };
        var body = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 480 };
        body.Children.Add(actions);
        body.Children.Add(status);
        body.Children.Add(list);

        void SetBusy(bool busy) => add.IsEnabled = refresh.IsEnabled = !busy;
        async Task RefreshAsync()
        {
            status.Text = "正在检测 Java…";
            var scan = await App.Backend.GetJavaScanAsync();
            entries.Children.Clear();
            status.Text = !scan.Ok ? scan.Message
                : scan.Items.Count == 0 ? "尚无可选的 Java。" : $"可用 Java · {scan.Items.Count} 个";
            foreach (var runtime in scan.Items)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = runtime.Label });
                row.Children.Add(new TextBlock { Text = runtime.Path, TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
                entries.Children.Add(row);
            }
        }

        add.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, main.WindowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                var executable = file.Name.ToLowerInvariant();
                if (executable is not ("java.exe" or "javaw.exe") || !File.Exists(file.Path))
                {
                    status.Text = "请选择 Java 安装目录中的 java.exe 或 javaw.exe。";
                    return;
                }
                var path = file.Path;
                if (executable == "javaw.exe" && Path.GetDirectoryName(path) is { } folder)
                {
                    var console = Path.Combine(folder, "java.exe");
                    if (File.Exists(console)) path = console;
                }
                var registered = main.Preferences.JavaRuntimePaths ??= new();
                if (!registered.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    registered.Add(path);
                    AppPreferencesStore.Save(main.Preferences);
                    if (AppPreferencesStore.Load().JavaRuntimePaths?.Contains(path, StringComparer.OrdinalIgnoreCase) != true)
                    {
                        registered.Remove(path);
                        status.Text = "无法保存 Java，请重试。";
                        return;
                    }
                }
                await RefreshAsync();
            }
            catch (Exception ex) { status.Text = Diagnostics.FriendlyMessage(ex); }
            finally { SetBusy(false); }
        };
        refresh.Click += async (_, _) =>
        {
            SetBusy(true);
            try { await RefreshAsync(); }
            catch (Exception ex) { status.Text = Diagnostics.FriendlyMessage(ex); }
            finally { SetBusy(false); }
        };

        var dialog = new ContentDialog { Title = "Java 管理", Content = body, CloseButtonText = "关闭", XamlRoot = root };
        SetBusy(true);
        dialog.Opened += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { status.Text = Diagnostics.FriendlyMessage(ex); }
            finally { SetBusy(false); }
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> OfferInstallAsync(int major)
    {
        var root = App.MainWindow?.Content?.XamlRoot;
        if (root is null || major <= 0) return false;

        var detail = new StackPanel { Spacing = 8, MinWidth = 360 };
        detail.Children.Add(new TextBlock
        {
            Text = $"这个版本需要 Java {major}，电脑上没有找到。",
            TextWrapping = TextWrapping.Wrap,
        });
        detail.Children.Add(new TextBlock
        {
            Text = $"可以让启动器下载一份（Azul Zulu，约 40~60 MB），装到系统默认位置 C:\\Program Files\\Zulu\\zulu-{major}，会弹一次管理员确认。下载完会自动继续启动。",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        var ask = new ContentDialog
        {
            Title = $"需要 Java {major}",
            Content = detail,
            PrimaryButtonText = "下载并启动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return false;

        var bar = new ProgressBar { IsIndeterminate = true, Height = 4 };
        var label = new TextBlock { Text = "正在准备下载…", TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 14, MinWidth = 360 };
        body.Children.Add(label);
        body.Children.Add(bar);
        var progressDialog = new ContentDialog { Title = $"正在下载 Java {major}", Content = body, XamlRoot = root };
        var showTask = progressDialog.ShowAsync();

        var progress = new Progress<string>(stage =>
        {
            var text = stage ?? "";
            label.Text = text;
            var dot = text.IndexOf('%');
            if (dot > 0)
            {
                var start = text.LastIndexOf(' ', dot) + 1;
                var s = text.LastIndexOf('·', dot);
                if (s >= 0) start = s + 1;
                if (int.TryParse(text.AsSpan(start, dot - start).Trim(), out var pct))
                {
                    bar.IsIndeterminate = false;
                    bar.Value = pct;
                }
            }
        });

        var (ok, message) = await App.Backend.InstallJavaAsync(major, progress);
        progressDialog.Hide();
        try { await showTask; } catch { }
        if (ok) return true;

        await new ContentDialog
        {
            Title = "Java 下载失败",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 360 },
            CloseButtonText = "知道了",
            XamlRoot = root,
        }.ShowAsync();
        return false;
    }
}
