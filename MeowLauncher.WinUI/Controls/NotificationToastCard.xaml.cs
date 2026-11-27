using MeowLauncher.WinUI.Services;
using MeowLauncher.WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Controls;

public sealed partial class NotificationToastCard : UserControl
{
    internal TaskNotification Notice { get; }
    private readonly Action? _primaryAction;
    internal event Action? DismissRequested;

    internal NotificationToastCard(TaskNotification notice, string? actionLabel, Action? primaryAction)
    {
        InitializeComponent();
        Notice = notice;
        _primaryAction = primaryAction;
        Category.Text = notice.SourceTask is not null ? "任务 · " + notice.Kind :
            notice.Kind is "已完成" or "失败" or "提示" ? notice.Kind : notice.Kind + " · 刚刚";
        Heading.Text = notice.Title;
        Body.Text = notice.Message;
        Body.Visibility = string.IsNullOrWhiteSpace(notice.Message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, string.IsNullOrEmpty(notice.Message) ? notice.Title : notice.Title + "，" + notice.Message);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        NoticeIcon.Glyph = TaskVisuals.NotificationGlyph(notice.Kind, notice.Title);
        NoticeIcon.Foreground = TaskVisuals.NotificationBrush(notice.Kind);
        if (notice.SourceTask?.TargetIcon is { } image)
        {
            TargetIcon.Source = image;
            TargetIcon.Visibility = Visibility.Visible;
            NoticeIcon.Visibility = Visibility.Collapsed;
        }
        PrimaryButton.Content = notice.InviteOpen ? "接受邀请" : actionLabel;
        RefreshInvite();
    }

    internal void RefreshInvite()
    {
        var invite = Notice.InviteOpen;
        Actions.Visibility = invite || _primaryAction is not null ? Visibility.Visible : Visibility.Collapsed;
        DeclineButton.Visibility = invite ? Visibility.Visible : Visibility.Collapsed;
        DeclineButton.IsEnabled = PrimaryButton.IsEnabled = !Notice.InviteResponding;
        ReplyStatus.Text = Notice.InviteResponding ? "正在回复邀请…" : Notice.ResultText;
        ReplyStatus.Visibility = ReplyStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (Notice.InviteOpen) { await App.Online.RespondToInviteAsync(Notice, true); return; }
        if (_primaryAction is null) return;
        DismissRequested?.Invoke();
        if (Notice.SourceTask is null) TaskCenter.MarkRead(Notice);
        _primaryAction();
    }

    private async void Decline_Click(object sender, RoutedEventArgs e) =>
        await App.Online.RespondToInviteAsync(Notice, false);
}
