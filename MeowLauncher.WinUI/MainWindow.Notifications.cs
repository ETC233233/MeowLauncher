using System.ComponentModel;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace MeowLauncher.WinUI;

public sealed partial class MainWindow
{
    private const double NoticeInset = 16;
    private const double NoticeGap = 8;
    private const double NoticeWidth = 364;
    private readonly List<ActiveNotice> _visibleNotices = [];
    private readonly RectangleGeometry _noticeClip = new();
    private bool _noticeLayoutQueued;

    private sealed class ActiveNotice(NotificationToastCard card, DispatcherQueueTimer timer, TimeSpan duration)
    {
        public NotificationToastCard Card { get; } = card;
        public DispatcherQueueTimer Timer { get; } = timer;
        public TranslateTransform Move { get; } = new();
        public Storyboard? Horizontal { get; set; }
        public Storyboard? Vertical { get; set; }
        public double Top { get; set; } = double.NaN;
        public double Height { get; set; }
        public bool PointerOver { get; set; }
        public TimeSpan Remaining { get; set; } = duration;
        public DateTimeOffset Deadline { get; set; }
        public PropertyChangedEventHandler? NoticeChanged { get; set; }
    }

    private void InitializeNotifications()
    {
        NotificationLayer.Clip = _noticeClip;
        NotificationLayer.SizeChanged += (_, _) => RefreshNoticeLayout();
        TaskCenter.NotificationRaised += OnNotificationRaised;
        TaskCenter.NotificationsChanged += OnNotificationHistoryChanged;
        Closed += (_, _) =>
        {
            TaskCenter.NotificationRaised -= OnNotificationRaised;
            TaskCenter.NotificationsChanged -= OnNotificationHistoryChanged;
            ClearNoticePopups();
        };
    }

    private void OnNotificationRaised(TaskNotification notice)
    {
        var previewVersion = _notificationPreviewVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (notice.IsPreview && previewVersion != _notificationPreviewVersion) return;
            if (!notice.IsPreview)
            {
                if (notice.SourceTask is not null && !_preferences.NotifyTaskDone) return;
                if (notice.SourceTask is not null || notice.Kind == "邀请") FlashTaskbarIfBackground();
            }
            if (OnboardingHost.Visibility == Visibility.Visible || NotificationLayer.XamlRoot is null) return;
            if (HasNoticeHistory(notice) && !TaskCenter.Notifications.Contains(notice)) return;
            if (IsActivityCenterOpen && (notice.SourceTask is not null
                ? !_activityNotifications : _activityNotifications && HasNoticeHistory(notice))) return;
            ShowNoticePopup(notice);
        });
    }

    private static bool HasNoticeHistory(TaskNotification notice) => notice.SourceTask is null && !notice.IsTransient;

    private void OnNotificationHistoryChanged(object? sender, EventArgs e)
    {
        var changed = false;
        foreach (var item in _visibleNotices.ToArray())
        {
            if (!HasNoticeHistory(item.Card.Notice) || TaskCenter.Notifications.Contains(item.Card.Notice)) continue;
            RetireNotice(item);
            changed = true;
        }
        if (changed) QueueNoticeLayout();
    }

    private void ShowNoticePopup(TaskNotification notice)
    {
        var width = Math.Min(NoticeWidth, NotificationLayer.ActualWidth - NoticeInset * 2);
        if (width <= 0) return;
        string? actionLabel = null;
        Action? action = null;
        if (notice.SourceTask is { } task)
        {
            actionLabel = "查看任务";
            action = () =>
            {
                OpenTaskCenter();
                if (TaskCenter.Entries.Contains(task)) _activityPage?.OpenDetail(task);
            };
        }
        else if (notice.Kind == "更新")
        {
            actionLabel = "查看更新";
            action = () =>
            {
                _pendingSettingsFocus = "updatechannel";
                Navigate("settings");
            };
        }

        var duration = TimeSpan.FromSeconds(notice.InviteOpen ? 30
            : notice.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning ? 9 : 6);
        if (notice.IsPreview && !notice.InviteOpen) duration = TimeSpan.FromSeconds(15);
        var card = new NotificationToastCard(notice, actionLabel, action) { Width = width };
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        var item = new ActiveNotice(card, timer, duration);
        card.RenderTransform = item.Move;
        var slide = Motion.Enabled;
        item.Move.X = slide ? width + NoticeInset * 2 : 0;
        card.DismissRequested += () => DismissNotice(item);
        card.PointerEntered += (_, _) => { item.PointerOver = true; PauseNoticeTimer(item); };
        card.PointerExited += (_, _) => { item.PointerOver = false; ResumeNoticeTimer(item); };
        card.GotFocus += (_, _) => PauseNoticeTimer(item);
        card.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() => ResumeNoticeTimer(item));
        card.SizeChanged += (_, _) => QueueNoticeLayout();
        timer.Tick += (_, _) => DismissNotice(item);
        if (notice.InviteOpen)
        {
            item.NoticeChanged = (_, e) =>
            {
                if (e.PropertyName is not (nameof(TaskNotification.InviteOpen) or nameof(TaskNotification.InviteResponding)
                    or nameof(TaskNotification.ResultText))) return;
                card.RefreshInvite();
                if (notice.InviteResponding) PauseNoticeTimer(item);
                else
                {
                    if (e.PropertyName == nameof(TaskNotification.InviteResponding))
                        item.Remaining = TimeSpan.FromSeconds(notice.InviteOpen ? 10 : 4);
                    ResumeNoticeTimer(item);
                }
                QueueNoticeLayout();
            };
            notice.PropertyChanged += item.NoticeChanged;
        }

        Canvas.SetZIndex(card, 2);
        _visibleNotices.Add(item);
        NotificationLayer.Children.Add(card);
        RefreshNoticeLayout();
        if (slide) item.Horizontal = Slide(item.Move, "X", item.Move.X, 0, 270, EasingMode.EaseOut);
        ResumeNoticeTimer(item);
    }

    private void QueueNoticeLayout()
    {
        if (_noticeLayoutQueued) return;
        _noticeLayoutQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _noticeLayoutQueued = false;
            RefreshNoticeLayout();
        });
    }

    private void RefreshNoticeLayout()
    {
        var width = NotificationLayer.ActualWidth;
        var height = NotificationLayer.ActualHeight;
        _noticeClip.Rect = new Rect(0, 0, width, height);
        if (_visibleNotices.Count == 0 || width <= NoticeInset * 2 || height <= NoticeInset * 2) return;
        var cardWidth = Math.Min(NoticeWidth, width - NoticeInset * 2);
        foreach (var item in _visibleNotices)
        {
            if (item.Card.Width != cardWidth) item.Card.Width = cardWidth;
            item.Card.Measure(new Size(cardWidth, double.PositiveInfinity));
            item.Height = item.Card.DesiredSize.Height;
        }
        while (_visibleNotices.Count > 1 &&
               _visibleNotices.Sum(item => item.Height) + NoticeGap * (_visibleNotices.Count - 1) > height - NoticeInset * 2)
            RetireNotice(_visibleNotices[0]);

        var bottom = height - NoticeInset;
        for (var index = _visibleNotices.Count - 1; index >= 0; index--)
        {
            var item = _visibleNotices[index];
            var top = Math.Max(0, bottom - item.Height);
            Canvas.SetLeft(item.Card, width - cardWidth - NoticeInset);
            if (double.IsNaN(item.Top) || Math.Abs(item.Top - top) >= 0.5)
            {
                var offset = double.IsNaN(item.Top) ? 0 : item.Top + item.Move.Y - top;
                item.Vertical?.Stop();
                item.Top = top;
                Canvas.SetTop(item.Card, top);
                item.Move.Y = 0;
                if (Motion.Enabled && Math.Abs(offset) >= 0.5)
                    item.Vertical = Slide(item.Move, "Y", offset, 0, 200, EasingMode.EaseOut);
            }
            bottom = top - NoticeGap;
        }
    }

    private void DismissNotice(ActiveNotice item)
    {
        RetireNotice(item);
        RefreshNoticeLayout();
    }

    private void RetireNotice(ActiveNotice item)
    {
        if (!_visibleNotices.Remove(item)) return;
        var from = item.Move.X;
        var top = double.IsNaN(item.Top) ? Canvas.GetTop(item.Card) : item.Top + item.Move.Y;
        StopNotice(item);
        if (!Motion.Enabled || double.IsNaN(top))
        {
            NotificationLayer.Children.Remove(item.Card);
            return;
        }
        item.Card.IsHitTestVisible = false;
        item.Card.IsEnabled = false;
        item.Move.Y = 0;
        Canvas.SetTop(item.Card, top);
        Canvas.SetZIndex(item.Card, 1);
        Slide(item.Move, "X", from, item.Card.Width + NoticeInset * 2, 175, EasingMode.EaseIn,
            () => NotificationLayer.Children.Remove(item.Card));
    }

    private static Storyboard Slide(TranslateTransform transform, string property, double from, double to,
        int milliseconds, EasingMode easing, Action? complete = null)
    {
        if (property == "X") transform.X = to;
        else transform.Y = to;
        var movement = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = easing },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(movement, transform);
        Storyboard.SetTargetProperty(movement, property);
        var story = new Storyboard();
        story.Children.Add(movement);
        if (complete is not null) story.Completed += (_, _) => complete();
        story.Begin();
        return story;
    }

    private static void StopNotice(ActiveNotice item)
    {
        item.Timer.Stop();
        item.Horizontal?.Stop();
        item.Vertical?.Stop();
        if (item.NoticeChanged is not null) item.Card.Notice.PropertyChanged -= item.NoticeChanged;
    }

    private void ClearNoticePopups()
    {
        foreach (var item in _visibleNotices) StopNotice(item);
        _visibleNotices.Clear();
        NotificationLayer.Children.Clear();
    }

    private static void PauseNoticeTimer(ActiveNotice item)
    {
        if (!item.Timer.IsRunning) return;
        item.Timer.Stop();
        var left = item.Deadline - DateTimeOffset.UtcNow;
        item.Remaining = left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    private void ResumeNoticeTimer(ActiveNotice item)
    {
        if (!_visibleNotices.Contains(item) || item.PointerOver || item.Card.Notice.InviteResponding
            || item.Card.XamlRoot is not { } root) return;
        for (var node = FocusManager.GetFocusedElement(root) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, item.Card)) return;
        item.Timer.Stop();
        item.Timer.Interval = item.Remaining;
        item.Deadline = DateTimeOffset.UtcNow + item.Remaining;
        item.Timer.Start();
    }
}
