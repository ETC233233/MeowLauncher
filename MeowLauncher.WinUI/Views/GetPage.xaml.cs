using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowLauncher.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 「获取」页：顶部两个子页胶囊（下载版本 / 在线资源），
/// 内容直接复用 DownloadPage 与 OnlinePage，只把它们的页头收起来。
/// </summary>
public sealed partial class GetPage : Page, IPageSearchFilter, ISubPageHost
{
    private string _tab = "download";
    private string _pendingQuery = "";
    private bool _ready;
    private bool _subOpen;
    private bool _importingPack;

    public GetPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            Show(_tab);
        };
    }

    public string CurrentTab => _tab;

    /// <summary>由 MainWindow 调用：切到指定子页；query 只对在线资源生效。</summary>
    public void ShowTab(string tab, string? query = null)
    {
        if (tab is not ("download" or "online")) tab = "download";
        if (!string.IsNullOrWhiteSpace(query)) _pendingQuery = query;
        if (_subOpen) CloseSubPageCore(popHistory: true);
        _tab = tab;
        UpdateTabVisuals();
        if (!_ready) return;
        Show(tab);
    }

    private void Show(string tab)
    {
        var target = tab == "online" ? typeof(OnlinePage) : typeof(DownloadPage);
        var query = tab == "online" ? _pendingQuery : "";
        if (tab == "online") _pendingQuery = "";
        if (InnerFrame.Content?.GetType() == target) return;
        var switching = InnerFrame.Content is not null;   // 首次载入由整页的滑入负责，只有标签互切才补进场
        InnerFrame.Navigate(target, new TabNav(query, true, this), switching ? MeowLauncher.WinUI.Controls.Motion.Sibling(tab == "online") : MeowLauncher.WinUI.Controls.Motion.PageRefresh());
        // 在线资源的搜索框在本页顶栏（来源切换右边），交给新建的 OnlinePage 使用。
        if (InnerFrame.Content is OnlinePage online) online.AttachQueryBox(OnlineQueryBox);
        if (InnerFrame.Content is DownloadPage download) download.AttachQueryBox(DownloadQueryBox);
    }

    private static bool IsPackFile(StorageFile file) =>
        file.FileType.Equals(".mrpack", StringComparison.OrdinalIgnoreCase)
        || file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private void Pack_DragOver(object sender, DragEventArgs e)
    {
        if (_importingPack || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入整合包（.mrpack 或 .zip）";
        e.Handled = true;
    }

    private async void Pack_Drop(object sender, DragEventArgs e)
    {
        if (_importingPack || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var deferral = e.GetDeferral();
        List<StorageFile> files;
        try
        {
            files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Where(IsPackFile).ToList();
        }
        catch (Exception ex)
        {
            ShowPackNotice(InfoBarSeverity.Error, "无法读取拖入的文件", ex.Message);
            return;
        }
        finally { deferral.Complete(); }
        await ImportPacksAsync(files.Select(file => file.Path).ToArray());
    }

    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        if (_importingPack || App.MainWindow is not { } main) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, main.WindowHandle);
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        try
        {
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await ImportPacksAsync(files.Select(file => file.Path).ToArray());
        }
        catch (Exception ex) { ShowPackNotice(InfoBarSeverity.Error, "无法打开文件", ex.Message); }
    }

    internal async Task ImportPacksAsync(IReadOnlyList<string> files)
    {
        if (_importingPack || App.MainWindow is not { } main) return;
        if (files.Count == 0)
        {
            ShowPackNotice(InfoBarSeverity.Warning, "没有整合包", "请拖入 Modrinth .mrpack 或 CurseForge 整合包 .zip。");
            return;
        }
        var dirs = GameDirectoryResolver.Resolve(main.Preferences);
        if (dirs.Count == 0)
        {
            ShowPackNotice(InfoBarSeverity.Warning, "没有游戏目录", "请先在全部实例中添加游戏目录。");
            return;
        }

        _importingPack = true;
        ImportPackButton.IsEnabled = false;
        try
        {
            var directory = new ComboBox { Header = "安装目录", ItemsSource = dirs, SelectedIndex = 0,
                                           HorizontalAlignment = HorizontalAlignment.Stretch };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = files.Count == 1 ? Path.GetFileName(files[0]) : $"将新建 {files.Count} 个整合包实例",
                                               TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(directory);
            var dialog = new ContentDialog
            {
                XamlRoot = main.Content.XamlRoot,
                Title = "导入整合包",
                Content = panel,
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || directory.SelectedItem is not string root)
                return;

            var installed = 0;
            var error = "";
            foreach (var file in files)
            {
                var task = TaskCenter.Add($"导入整合包 {Path.GetFileNameWithoutExtension(file)}", Path.GetFileName(file),
                    "versions", "\uE8B5", InstanceIcon.ForLoader(null));
                try
                {
                    var progress = new Progress<string>(stage => TaskCenter.Report(task, stage));
                    var result = await App.Backend.ImportModpackAsync(file, root, progress, task.Id);
                    if (result.Ok)
                    {
                        installed++;
                        task.TargetIcon = InstanceIcon.ForInstalledPack(result);
                        TaskCenter.Complete(task, result.Message);
                    }
                    else
                    {
                        error = result.Message;
                        TaskCenter.Fail(task, error, result.Failure);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    TaskCenter.Fail(task, error);
                }
            }
            if (installed > 0) await main.RefreshShellAsync(force: true);
        }
        catch (Exception ex)
        {
            ShowPackNotice(InfoBarSeverity.Error, "无法导入整合包", ex.Message);
        }
        finally
        {
            _importingPack = false;
            ImportPackButton.IsEnabled = true;
        }
    }

    private static void ShowPackNotice(InfoBarSeverity severity, string title, string message) =>
        TaskCenter.PostFeedback(severity, title, message);

    // 「获取」只是个壳：本页过滤和 Ctrl+K 都转给里面的 DownloadPage / OnlinePage。
    public void ApplySearch(string query)
    {
        if (_subOpen) CloseSubPageCore(popHistory: true);
        if (InnerFrame.Content is IPageSearchFilter inner) inner.ApplySearch(query);
    }

    public bool TryFocusSearch()
    {
        if (_subOpen) CloseSubPageCore(popHistory: true);
        return InnerFrame.Content is IPageSearchFilter inner && inner.TryFocusSearch();
    }

    // ------------------------------------------------------------------ 子页

    public void OpenSubPage(Type page, object args, params string[] crumbs)
    {
        BuildCrumbs(crumbs);
        var first = !_subOpen;
        _subOpen = true;
        ApplyHeaderMode();
        // 详情是下一级页面，使用系统 DrillIn 过渡。
        try
        {
            SubFrame.Navigate(page, args, MeowLauncher.WinUI.Controls.Motion.Enabled ? new DrillInNavigationTransitionInfo() : new SuppressNavigationTransitionInfo());
        }
        catch (Exception ex)
        {
            // 子页构造失败（常见是 XAML 解析错误）：别留一个空白窗口，退回列表并把错误交给调用方。
            App.Trace("OpenSubPage FAILED " + page.Name + ": " + ex);
            CloseSubPageCore(popHistory: true);
            throw;
        }
        MeowLauncher.WinUI.Controls.Motion.Enter(SubHeader, 0, 6, 200);
        // Alt+Left 关闭子页，和面包屑返回一致。
        if (first) App.MainWindow?.PushSubPage(() => CloseSubPageCore(popHistory: false));
    }

    public void CloseSubPage() => CloseSubPageCore(popHistory: true);

    private void CloseSubPageCore(bool popHistory)
    {
        if (!_subOpen) return;
        _subOpen = false;
        SubFrame.Content = null;
        ApplyHeaderMode();
        MeowLauncher.WinUI.Controls.Motion.Enter(TitleRow, 0, 6, 200);
        MeowLauncher.WinUI.Controls.Motion.Enter(TabsRow, 30, 6, 200);
        MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 60, 8, 220);
        if (popHistory) App.MainWindow?.PopSubPage();
    }

    /// <summary>子页开着时显示面包屑并收起胶囊和列表，内容区换成子页。</summary>
    private void ApplyHeaderMode()
    {
        var sub = _subOpen;
        TitleRow.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        SubHeader.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        TabsRow.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        InnerFrame.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        SubFrame.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCrumbs(string[] crumbs)
    {
        CrumbPanel.Children.Clear();
        for (var i = 0; i < crumbs.Length; i++)
        {
            var last = i == crumbs.Length - 1;
            if (i > 0)
            {
                CrumbPanel.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = PagePalette.ThemeBrush("AppText3Brush"),
                });
            }
            var text = new TextBlock
            {
                Text = crumbs[i],
                FontSize = 28,
                // 和「实例」「账户」同一套：前面的层级常规字重 + 0.72 不透明度，当前页半粗。
                FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = last ? 1 : 0.72,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = PagePalette.ThemeBrush("AppTextBrush"),
            };
            if (last)
            {
                text.MaxWidth = 460;
            }
            else
            {
                // 前面的段都能点：回到列表。
                text.Tapped += (_, _) => CloseSubPage();
                ToolTipService.SetToolTip(text, "返回列表");
            }
            CrumbPanel.Children.Add(text);
        }
    }

#if DEBUG
    public async Task DebugOpenDetail(string id)
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not DownloadPage; i++) await Task.Delay(100);
        if (InnerFrame.Content is DownloadPage d) await d.DebugOpenDetail(id);
    }
#endif

#if DEBUG
    public async Task DebugOpenFirstMod(string instance = "")
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not OnlinePage; i++) await Task.Delay(100);
        if (InnerFrame.Content is OnlinePage o) await o.DebugOpenFirst(instance);
    }
#endif

    public async Task OpenProjectAsync(MeowLauncher.WinUI.Models.ModrinthProject project, string instance, string root)
    {
        for (var i = 0; i < 50 && InnerFrame.Content is not OnlinePage; i++) await Task.Delay(100);
        if (InnerFrame.Content is OnlinePage o) await o.OpenProjectAsync(project, instance, root);
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag })
        {
            ShowTab(tag);
            App.MainWindow?.NotifySubTab(tag);
        }
    }

    internal void RefreshSourceSelection() => UpdateTabVisuals();

    private void UpdateTabVisuals()
    {
        var selected = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var text = PagePalette.ThemeBrush("AppTextBrush");
        var text2 = PagePalette.ThemeBrush("AppText2Brush");
        Paint(TabDownload, _tab == "download");
        Paint(TabOnline, _tab == "online");
        OnlineHeaderActions.Visibility = _tab == "online" ? Visibility.Visible : Visibility.Collapsed;
        ImportPackButton.Visibility = _tab == "download" ? Visibility.Visible : Visibility.Collapsed;

        // 来源切换跟着「在线资源」走：选中时从按钮右边滑出来，切到下载版本就收起。
        var showStrip = _tab == "online";
        var stripWasHidden = SourceStrip.Visibility != Visibility.Visible;
        SourceStrip.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        OnlineQueryBox.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        DownloadQueryBox.Visibility = _tab == "download" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceLabel();
        if (showStrip && stripWasHidden) MeowLauncher.WinUI.Controls.Motion.EnterX(SourceStrip, 40, -14, 220);

        void Paint(ToggleButton button, bool on)
        {
            button.IsChecked = on;
            button.Background = on ? selected : clear;
            button.Foreground = on ? text : text2;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>把「来源 + 镜像」的组合写成按钮上的小字，并勾中菜单里对应的那一项。</summary>
    private void UpdateSourceLabel()
    {
        if (OptModrinthMcim is null) return;
        var isCurseForge = OnlinePage.PreferredSource == 1;
        var cf = (InnerFrame.Content as OnlinePage)?.CfSourceIndex ?? OnlinePage.PreferredCfSource;
        var official = Services.McimClient.PreferOfficial;

        OptModrinthMcim.IsChecked = !isCurseForge && !official;
        OptModrinthOfficial.IsChecked = !isCurseForge && official;
        OptCfMcim.IsChecked = isCurseForge && cf == 0;
        OptCfMeow.IsChecked = isCurseForge && cf == 1;
        OptCfOfficial.IsChecked = isCurseForge && cf == 2;

        // 两个胶囊的选中态（绿底 + 粗体）由这里刷，别丢。
        var selected = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var text = PagePalette.ThemeBrush("AppTextBrush");
        var text2 = PagePalette.ThemeBrush("AppText2Brush");
        PaintSource(SrcModrinth, !isCurseForge);
        PaintSource(SrcCurseForge, isCurseForge);

        void PaintSource(ToggleButton button, bool on)
        {
            button.IsChecked = on;
            button.Background = on ? selected : clear;
            button.Foreground = on ? text : text2;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>来源菜单：m0/m1 = Modrinth 的 MCIM/官方，c0/c1/c2 = CurseForge 的 MCIM/MeowAPI/官方。</summary>
    private async void SourceOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag } || tag.Length < 2) return;
        if (tag[0] == 'm')
        {
            Services.McimClient.SetPreferOfficial(tag == "m1");
            SetSourceIndex(0);
        }
        else
        {
            await ApplyCfSourceAsync(tag[1] - '0');
        }
        UpdateTabVisuals();
    }

    private async Task ApplyCfSourceAsync(int index)
    {
        // 官方直连需要 Key：没有就直接拦下，别让用户选完才发现搜不出东西。
        if (index == 2)
        {
            try
            {
                var g = await App.Backend.GetGeneralSettingsAsync();
                if (string.IsNullOrWhiteSpace(g.CfApiKey))
                {
                    await new Microsoft.UI.Xaml.Controls.ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "需要 CurseForge API Key",
                        Content = "请前往「设置 › 下载与网络」填写 CurseForge API Key，再选官方源。",
                        CloseButtonText = "知道了",
                    }.ShowAsync();
                    return;
                }
            }
            catch { }
        }
        SetSourceIndex(1);
        (InnerFrame.Content as OnlinePage)?.SetCfSource(index);
        OnlinePage.PreferredCfSource = index;
    }

    /// <summary>点 Modrinth / CurseForge：先切过去，再把镜像/来源的单选菜单弹出来。</summary>
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        SetSourceIndex(index);
        UpdateTabVisuals();
        if (sender is FrameworkElement element)
            FlyoutBase.ShowAttachedFlyout(element);
    }

    private void SetSourceIndex(int index)
    {
        OnlinePage.PreferredSource = index;
        if (InnerFrame.Content is OnlinePage online) online.SetSource(index);
    }

    private void Home_Tapped(object sender, TappedRoutedEventArgs e) =>
        App.MainWindow.Navigate("home");
}
