using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 联机组件的下载 / 更新页。这个页面本身不碰联机模块（模块还没下载），只用 <see cref="OnlineComponent"/>。
/// 流程：已装好就直接加载并进房间页；没装就读云端签名清单 → 显示版本和大小 → 用户点「下载并安装」→ 校验 → 加载 → 进房间页。
/// 更新模式（参数 "update"）：只检查有没有新版本；新版本装好后要重启启动器才生效（已加载的程序集不能卸载）。
/// </summary>
public sealed partial class OnlineSetupPage : Page
{
    private bool _update;
    private ComponentOffer? _offer;
    private CancellationTokenSource? _cts;
    private bool _installing;

    public OnlineSetupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _update = e.Parameter as string == "update";
        _ = BeginAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面不再中断下载：安装跑在后台，进度记在「任务与通知」。
    }

    private async Task BeginAsync()
    {
        string? broken = null;
        if (!_update)
        {
            if (OnlineComponent.TryLoad()) { Done(); return; }
            // 装过但坏了 / 级别不对：把原因告诉用户，然后走重新下载
            if (OnlineComponent.ReadInstalled() is not null) broken = OnlineComponent.LastError;
        }

        ShowChecking();
        var (offer, error) = await OnlineComponent.FetchOfferAsync();
        if (offer is null) { ShowError("没能获取联机组件", error, retryLabel: "重试"); return; }
        _offer = offer;

        if (_update && !OnlineComponent.IsNewer(offer, OnlineComponent.Loaded))
        {
            TitleText.Text = "联机组件已是最新";
            BodyText.Text = $"当前版本 {OnlineComponent.Loaded?.Version ?? "—"}，没有可用的更新。";
            Show(progress: false, primary: null, secondary: "返回联机页");
            return;
        }

        var mb = offer.Size / 1048576.0;
        TitleText.Text = _update ? $"联机组件有新版本 {offer.Version}" : "需要先下载联机组件";
        BodyText.Text = _update
            ? $"当前版本 {OnlineComponent.Loaded?.Version ?? "—"}，最新版本 {offer.Version}（约 {mb:0} MB）。更新装好后，重启启动器生效。"
            : $"首次使用联机需要下载联机组件（约 {mb:0} MB，版本 {offer.Version}），包含联机模块、提权代理和 EasyTier。";
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(offer.Notes)) notes.Add(offer.Notes);
        if (offer.Required && _update) notes.Add("这个版本是必须更新的。");
        if (!string.IsNullOrEmpty(broken)) notes.Add("已安装的组件不可用：" + broken);
        DetailText.Text = string.Join("\n", notes);
        Show(progress: false, primary: _update ? "下载并更新" : "下载并安装", secondary: _update ? "返回联机页" : null);
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_offer is null) { _ = BeginAsync(); return; }          // 「重试」
        _installing = true;
        _cts = new CancellationTokenSource();
        TitleText.Text = _update ? "正在更新联机组件" : "正在下载联机组件";
        BodyText.Text = "下载在后台进行，可以离开这个页面，进度在「任务与通知」里看。";
        DetailText.Text = "";
        Bar.IsIndeterminate = true;
        Show(progress: true, primary: null, secondary: "取消");

        var progress = new Progress<(double Fraction, string Text)>(p =>
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
            ProgressText.Text = p.Text;
        });

        var (ok, message) = await OnlineComponent.InstallAsync(_offer, progress, _cts.Token);
        _installing = false;
        if (ok && OnlineComponent.Ready && !_update) { Done(); return; }
        if (ok)
        {
            TitleText.Text = "联机组件已更新";
            BodyText.Text = message;
            Show(progress: false, primary: null, secondary: "返回联机页");
            return;
        }
        _offer = null;                                            // 让「重试」重新取清单
        ShowError("没有装好", message, retryLabel: "重试");
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) { OnlineComponent.CancelInstall(); return; }
        if (_update) App.MainWindow?.Navigate("room", recordHistory: false);
    }

    private void Done()
    {
        // 组件已加载：按联机开关把账号握手 / 推送通道跑起来，然后进房间页
        _ = App.MainWindow?.ApplyOnlineSettingAsync();
        App.MainWindow?.Navigate("room", recordHistory: false);
    }

    private void ShowChecking()
    {
        TitleText.Text = "正在检查联机组件…";
        BodyText.Text = "正在读取云端的组件清单。";
        DetailText.Text = "";
        Bar.IsIndeterminate = true;
        ProgressText.Text = "";
        Show(progress: true, primary: null, secondary: null);
    }

    private void ShowError(string title, string message, string retryLabel)
    {
        TitleText.Text = title;
        BodyText.Text = message;
        DetailText.Text = "";
        Show(progress: false, primary: retryLabel, secondary: null);
    }

    private void Show(bool progress, string? primary, string? secondary)
    {
        ProgressPanel.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        if (primary is not null) PrimaryButton.Content = primary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        if (secondary is not null) SecondaryButton.Content = secondary;
    }
}
