using System.Collections.ObjectModel;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using MeowLauncher.WinUI.Controls;
using Windows.System;

namespace MeowLauncher.WinUI.Views;

public sealed partial class TasksPage : Page
{
    private bool _showNotifications;
    private bool _drawerMode;
    private Storyboard? _sectionTransition;
    private bool _suppressSection;

    public ObservableCollection<TaskEntry> ActiveTasks { get; } = new();
    public ObservableCollection<TaskEntry> DoneTasks { get; } = new();
    public ObservableCollection<TaskEntry> FailedTasks { get; } = new();
    public ObservableCollection<TaskNotification> Notifications => TaskCenter.Notifications;
    public ObservableCollection<RunningGame> RunningGames { get; } = new();

    public TasksPage()
    {
        InitializeComponent();
        _suppressSection = true;
        TasksTab.IsChecked = true;
        _suppressSection = false;
        Loaded += (_, _) =>
        {
            TaskCenter.Changed += OnTasksChanged;
            TaskCenter.NotificationsChanged += OnNotificationsChanged;
            GameSessions.Changed += OnGamesChanged;
            _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            ResetSectionTransition();
            TaskCenter.Changed -= OnTasksChanged;
            TaskCenter.NotificationsChanged -= OnNotificationsChanged;
            GameSessions.Changed -= OnGamesChanged;
        };
    }

    public void ConfigureActivityDrawer(bool notifications)
    {
        _drawerMode = true;
        ActiveSectionHeader.Visibility = Visibility.Collapsed;
        SetSection(notifications);
        PageHeader.Visibility = Visibility.Collapsed;
        PageRoot.RowDefinitions[0].Height = new GridLength(0);
        PageRoot.RowSpacing = 0;
        PageRoot.Padding = new Thickness(0);
    }

    public void ShowActivitySection(bool notifications) => SetSection(notifications);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshAsync();
    }

    private void OnTasksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void OnNotificationsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void OnGamesChanged() => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>进入页面先摆加载态，顺手做一次后端自检；失败就走错误面板，重试原地重发。</summary>
    private async Task RefreshAsync()
    {
        if (_drawerMode) { Refresh(); ShowContent(PageState.Content); return; }
        ShowContent(PageState.Loading);
        await Task.Delay(120);

        var check = App.Backend?.CheckBackend() ?? (false, "后端尚未初始化。");
        if (!check.Ok)
        {
            ShowError("任务队列暂时不可用", check.Message);
            return;
        }

        Refresh();
        ShowContent(PageState.Content);
    }

    private void Refresh()
    {
        // 只做增删/换位，不 Clear 再重建：重建会让每行重新加载，进度条会从 0 重跑一遍（一闪一闪）。
        var activeNow = new List<TaskEntry>();
        var failedNow = new List<TaskEntry>();
        var doneNow = new List<TaskEntry>();
        foreach (var entry in TaskCenter.Entries)
        {
            if (entry.IsActive) activeNow.Add(entry);
            else if (entry.Status == "失败") failedNow.Add(entry);
            else doneNow.Add(entry);
        }
        SyncList(ActiveTasks, activeNow);
        SyncList(FailedTasks, failedNow);
        SyncList(DoneTasks, doneNow);

        RunningGames.Clear();
        foreach (var row in GameSessions.Running) RunningGames.Add(row);
        RunningSection.Visibility = RunningGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunningCountText.Text = RunningGames.Count.ToString();

        var active = ActiveTasks.Count;
        var total = TaskCenter.Entries.Count;
        var unread = TaskCenter.UnreadCount;
        SummaryText.Text = active > 0
            ? $"{active} 个进行中"
            : RunningGames.Count > 0
                ? $"{RunningGames.Count} 个游戏运行中"
                : "队列空闲";
        TasksTab.Content = total > 0 ? $"任务 {total}" : "任务";
        ActiveSection.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneSection.Visibility = DoneTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FailedSection.Visibility = FailedTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveCountText.Text = active.ToString();
        DoneCountText.Text = DoneTasks.Count.ToString();
        FailedCountText.Text = FailedTasks.Count.ToString();

        var empty = total == 0 && RunningGames.Count == 0;
        EmptyHost.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        GroupsScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionVisibility();

        // 通知通道
        NotificationsTab.Content = Notifications.Count > 0 ? $"通知 {Notifications.Count}" : "通知";
        NotificationEmptyHost.Visibility = Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsScroll.Visibility = Notifications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionVisibility();
    }

    /// <summary>把集合同步成目标列表：多删、缺插、错位移动，已存在的元素原样保留。</summary>
    private static void SyncList(ObservableCollection<TaskEntry> target, List<TaskEntry> wanted)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var index = target.IndexOf(item);
            if (index < 0) target.Insert(i, item);
            else if (index != i) target.Move(index, i);
        }
    }

    // ── 通道切换 ────────────────────────────────────────────────────────

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressSection) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        var notifications = string.Equals(tag, "notifications", StringComparison.Ordinal);
        if (_showNotifications == notifications) return;
        SetSection(notifications);
    }

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

    private void SetSection(bool notifications)
    {
        var changed = _showNotifications != notifications;
        if (changed) ResetSectionTransition();
        _showNotifications = notifications;
        if (notifications != (NotificationsTab.IsChecked == true))
        {
            _suppressSection = true;
            NotificationsTab.IsChecked = notifications;
            TasksTab.IsChecked = !notifications;
            _suppressSection = false;
        }
        TaskSection.Visibility = notifications ? Visibility.Collapsed : Visibility.Visible;
        NotificationSection.Visibility = notifications ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionVisibility();
        if (changed && _drawerMode && IsLoaded && Motion.Enabled)
        {
            var incoming = notifications ? NotificationSection : TaskSection;
            var slide = new TranslateTransform { X = notifications ? 12 : -12 };
            incoming.RenderTransform = slide;
            incoming.Opacity = 0;
            var story = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180), EnableDependentAnimation = true };
            var move = new DoubleAnimation { From = slide.X, To = 0, Duration = TimeSpan.FromMilliseconds(180), EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, incoming); Storyboard.SetTargetProperty(fade, "Opacity");
            Storyboard.SetTarget(move, slide); Storyboard.SetTargetProperty(move, "X");
            story.Children.Add(fade); story.Children.Add(move);
            story.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_sectionTransition, story)) return;
                incoming.Opacity = 1; slide.X = 0;
                story.Stop(); _sectionTransition = null;
            };
            _sectionTransition = story; story.Begin();
        }
    }

    private void ResetSectionTransition()
    {
        _sectionTransition?.Stop(); _sectionTransition = null;
        foreach (var section in new FrameworkElement[] { TaskSection, NotificationSection })
        {
            section.Opacity = 1;
            if (section.RenderTransform is TranslateTransform slide) slide.X = 0;
        }
    }

    /// <summary>「当前无事可做」的操作按钮整颗隐藏（没有已完成任务就没有「清空已完成」），不留一个灰着的假按钮。</summary>
    private void UpdateActionVisibility()
    {
        var hasDone = TaskCenter.Entries.Any(x => !x.IsActive);
        var hasNotices = TaskCenter.Notifications.Count > 0;
        var hasUnread = TaskCenter.UnreadCount > 0;
        ClearButton.Visibility = !_showNotifications && hasDone ? Visibility.Visible : Visibility.Collapsed;
        ClearNotificationsButton.Visibility = _showNotifications && hasNotices ? Visibility.Visible : Visibility.Collapsed;
        MarkAllReadButton.Visibility = _showNotifications && hasUnread ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 状态切换 ────────────────────────────────────────────────────────

    private enum PageState
    {
        Loading,
        Content,
        Error,
    }

    private void ShowContent(PageState state)
    {
        LoadingPanel.Visibility = state == PageState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state == PageState.Error ? Visibility.Visible : Visibility.Collapsed;

        var content = state == PageState.Content ? Visibility.Visible : Visibility.Collapsed;
        TaskSection.Visibility = content == Visibility.Visible && !_showNotifications ? Visibility.Visible : Visibility.Collapsed;
        NotificationSection.Visibility = content == Visibility.Visible && _showNotifications ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string title, string message)
    {
        System.Diagnostics.Debug.WriteLine($"Tasks page error: {message}");
        ErrorTitle.Text = title;
        ErrorSummary.Text = "任务队列没有响应，重试一次。";
        ShowContent(PageState.Error);
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    // ── 操作 ────────────────────────────────────────────────────────────

    private void Clear_Click(object sender, RoutedEventArgs e) => TaskCenter.ClearFinished();
    private void DismissTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && TaskCenter.Entries.FirstOrDefault(x => x.Id == id) is { } entry)
            TaskCenter.Dismiss(entry);
    }


    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var entry = TaskCenter.Entries.FirstOrDefault(item => item.Id == id);
        if (entry is null) return;

        if (!TaskCenter.CanCancel(entry))
        {
            ShowToast(InfoBarSeverity.Informational, "这个任务不能取消", TaskCenter.CancelHint(entry));
            return;
        }

        // The row and entry already show the cancelled state; don't cover them with a second banner.
        TaskCenter.Cancel(entry);
    }

    private async void EndGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var pid = button.Tag switch
        {
            int value => value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0,
        };
        if (pid <= 0) return;

        var row = GameSessions.Running.FirstOrDefault(item => item.Pid == pid);
        button.IsEnabled = false;
        try
        {
            var result = await App.Backend.StopGameAsync(pid, row?.Version ?? "");
            ShowToast(result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Ok ? "已结束游戏" : "结束失败", result.Message);
            await GameSessions.RefreshAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            ShowToast(InfoBarSeverity.Error, "结束失败", Diagnostics.FriendlyMessage(ex));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        TaskCenter.MarkRead(TaskCenter.Notifications.FirstOrDefault(item => item.Id == id));
    }

    private void InviteAccept_Click(object sender, RoutedEventArgs e) => _ = ReplyInviteAsync(sender, accepted: true);

    private void InviteDecline_Click(object sender, RoutedEventArgs e) => _ = ReplyInviteAsync(sender, accepted: false);

    private static async Task ReplyInviteAsync(object sender, bool accepted)
    {
        if (sender is not Button { Tag: string id }) return;
        var note = TaskCenter.Notifications.FirstOrDefault(item => item.Id == id);
        if (note is not null) await App.Online.RespondToInviteAsync(note, accepted);
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        TaskCenter.MarkAllRead();
        ShowToast(InfoBarSeverity.Success, "通知", "全部标为已读。");
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        var count = TaskCenter.Notifications.Count;
        TaskCenter.ClearNotifications();
        ShowToast(InfoBarSeverity.Success, "通知", count == 0 ? "本来就没有通知。" : $"已清空 {count} 条通知。");
    }

    private void Notification_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender); node = VisualTreeHelper.GetParent(node))
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
        if (sender is Border { Tag: string id })
        {
            TaskCenter.MarkRead(TaskCenter.Notifications.FirstOrDefault(item => item.Id == id));
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var entry = TaskCenter.Entries.FirstOrDefault(item => item.Id == id);
        if (entry is not null) CopyTaskFailure(entry);
    }

    private void Notification_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string id })
        {
            TaskCenter.MarkRead(TaskCenter.Notifications.FirstOrDefault(item => item.Id == id));
        }
    }

    private void CopyTaskFailure(TaskEntry entry)
    {
        if (entry.Failure is not { } failure) return;
        Diagnostics.Copy(Diagnostics.BuildReport(failure, entry.Title,
            string.IsNullOrWhiteSpace(entry.FailureMessage) ? entry.Detail : entry.FailureMessage));
        ShowToast(InfoBarSeverity.Success, "诊断信息", $"已复制错误码 {failure.Code} 与任务上下文，粘贴到反馈里即可。");
    }

    private static void ShowToast(InfoBarSeverity severity, string title, string message) =>
        TaskCenter.PostFeedback(severity, title, message, addToHistory: false);
}

/// <summary>任务行的类型/状态视觉：图标按任务类型（启动/下载/安装），颜色按状态（进行中蓝、完成绿、失败红）。</summary>
public static class TaskVisuals
{
    public static string StatsText(string speed, string remain) => string.Join(" · ", new[] { speed, remain }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public static string TaskMeta(string title, string status, string time) => $"{KindLabel(title)} · {status} · {time}";

    public static Visibility DeterminateVisibility(bool indeterminate) => indeterminate ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IndeterminateVisibility(bool indeterminate) => indeterminate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>自绘进度条的横向缩放（0-1）：走 RenderTransform，没有内置动画，按上报值直接到位。</summary>
    public static double ProgressScale(double progress) =>
        double.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 100) / 100.0;

    public static Visibility TaskDetailVisibility(string status, bool indeterminate) =>
        status != "进行中" ? Visibility.Visible : Visibility.Collapsed;

    public static string IconFor(string? title)
    {
        var text = title ?? "";
        if (text.StartsWith("启动", StringComparison.Ordinal)) return "\uE768";
        if (text.Contains("下载", StringComparison.Ordinal)) return "\uE896";
        if (text.StartsWith("安装", StringComparison.Ordinal)) return "\uE7B8";
        return "\uE823";
    }

    /// <summary>行标题去掉「启动 / 安装 / 下载」前缀：动作由左侧图标表达，避免「启动 X 启动」的重复。</summary>
    public static string DisplayTitle(string? title)
    {
        var text = (title ?? "").Trim();
        foreach (var prefix in new[] { "启动 ", "安装 ", "下载 " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
                return text[prefix.Length..].Trim();
        }
        return text;
    }

    public static string DotTime(string? time) =>
        string.IsNullOrWhiteSpace(time) ? "" : "· " + time;

    public static string KindLabel(string? title)
    {
        var text = title ?? "";
        if (text.StartsWith("启动", StringComparison.Ordinal)) return "启动";
        if (text.Contains("下载", StringComparison.Ordinal)) return "下载";
        if (text.StartsWith("安装", StringComparison.Ordinal)) return "安装";
        return "任务";
    }

    public static Visibility ActiveVisibility(string? status) =>
        status == "进行中" ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>大号总进度数字：一位小数，够看又不至于乱跳。</summary>
    public static string PercentNumber(double progress) =>
        double.IsNaN(progress) || progress <= 0 ? "0" : progress.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>有速度/剩余文件数据时才显示这一行。</summary>
    public static Visibility HasStatsVisibility(bool has) =>
        has ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility FinishedVisibility(string? status) =>
        status == "进行中" ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility FailedVisibility(string? status) =>
        status == "失败" ? Visibility.Visible : Visibility.Collapsed;

    // ── 取消入口 ────────────────────────────────────────────────────────

    public static Visibility CancelVisibility(string? status) =>
        status == "进行中" ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>启动任务在「进程已启动」之后不给取消——按钮置灰，ToolTip 解释原因。</summary>
    public static bool CancelEnabled(string? status, string? detail) =>
        status == "进行中" && !LaunchHandedOff(detail);

    public static string CancelHint(string? status, string? detail)
    {
        if (status != "进行中") return "任务已经结束，没有可取消的操作。";
        return LaunchHandedOff(detail)
            ? "游戏进程已启动，取消不会再关掉它。"
            : "可取消：中止这次后台操作，已经启动的游戏不会被关闭。";
    }

    private static bool LaunchHandedOff(string? detail)
    {
        var text = detail ?? "";
        return text.Contains("进程已启动", StringComparison.Ordinal)
               || text.Contains("正在记录启动时间", StringComparison.Ordinal)
               || text.Contains("已记录启动时间", StringComparison.Ordinal);
    }

    // ── 通知通道 ────────────────────────────────────────────────────────

    public static string NotificationGlyph(string? kind, string? title)
    {
        if (kind == "公告") return "\uE789";
        if (kind is "账户" or "账号" || (title?.Contains("账户", StringComparison.Ordinal) ?? false) || (title?.Contains("账号", StringComparison.Ordinal) ?? false))
            return "\uE77B";
        return kind switch
        {
            "邀请" => ((char)Symbol.AddFriend).ToString(),
            "更新" => ((char)Symbol.Sync).ToString(),
            "失败" => "\uE7BA",
            "已取消" => "\uE711",
            "已完成" => "\uE73E",
            _ => "\uEA8F",
        };
    }

    public static Brush NotificationBrush(string? kind) => kind switch
    {
        "失败" => PagePalette.ThemeBrush("AppBadBrush"),
        "已完成" => PagePalette.ThemeBrush("AppGoodBrush"),
        "已取消" => PagePalette.ThemeBrush("AppText3Brush"),
        _ => PagePalette.ThemeBrush("AppText2Brush"),
    };

    public static Brush NotificationBackground(bool isRead) =>
        isRead ? PagePalette.ThemeBrush("AppCardBrush") : PagePalette.SoftTint("TintTasksColor");

    public static Brush StatusBrush(string? status) => status switch
    {
        "进行中" => PagePalette.ThemeBrush("TintTasksBrush"),
        "已完成" => PagePalette.ThemeBrush("AppGoodBrush"),
        "失败" => PagePalette.ThemeBrush("AppBadBrush"),
        "已取消" => PagePalette.ThemeBrush("AppText3Brush"),
        _ => PagePalette.ThemeBrush("AppText2Brush"),
    };

    public static string StageText(string? detail)
    {
        var text = (detail ?? "").Trim();
        if (text.Length == 0) return "准备中";
        return text.Length > 100 ? text[..100] + "…" : text;
    }

    /// <summary>进度文案写清在做什么：正在下载 42% / 正在启动…，不再用「进行中」这种空话。</summary>
    public static string ProgressText(string? title, double progress, bool indeterminate)
    {
        var verb = KindLabel(title) switch
        {
            "启动" => "启动",
            "下载" => "下载",
            "安装" => "安装",
            _ => "处理",
        };
        return indeterminate || progress <= 0 ? $"正在{verb}…" : $"正在{verb} {progress:0}%";
    }

    /// <summary>侧栏概览面板的第二行：进行中给进度，结束给结果。</summary>
    public static string PanelStatusText(TaskEntry entry) => entry.IsActive
        ? ProgressText(entry.Title, entry.Progress, entry.IsIndeterminate)
        : entry.Status;

}
