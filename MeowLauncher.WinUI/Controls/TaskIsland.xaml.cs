using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 任务与通知灵动岛（类 iOS 实时活动），常驻在标题栏右侧。状态机：
///   Idle（铃铛 + 未读数）──有进行中任务──▶ Compact ──点击/悬停──▶ Expanded
///   Compact/Expanded ──全部结束──▶ Done 或 Failed（各自停留 3.5 s / 8 s）──▶ Idle
///   收到新通知（邀请、对方的回复等）──▶ 自动展开 6 s（邀请 30 s，要等你点接受/拒绝）──▶ 收回
/// 展开卡片 = 进行中的任务 + 最近 4 条通知（邀请带「接受 / 拒绝」）+ 「查看全部」。
/// 规则：悬停时暂停一切自动收起；同一条已结束任务只庆祝一次（_celebrated）；
/// 关闭动画（系统「显示动画」关）时不做缩放/淡入。数据只读自 TaskCenter，不持有任务。
/// 侧栏左下的 TaskDock 已收起（Collapsed），入口统一在这里。
/// </summary>
public sealed partial class TaskIsland : UserControl
{
    private enum IslandState { Hidden, Idle, Compact, Expanded, Done, Failed }

    private const int IncomingInviteMs = 30000;
    private const int IncomingNoticeMs = 6000;
    private readonly DispatcherTimer _incomingTimer = new();
    private DateTime _incomingUntil = DateTime.MinValue;
    private string _lastTopNoteId = "";
    private string _noteSignature = "";   // 哨兵值：不能等于任何真实签名（空通知列表的签名是空串）
    private StackPanel? _taskHost;
    private StackPanel? _noteHost;
    private Button? _allButton;
    private bool IncomingActive => DateTime.Now < _incomingUntil;

    /// <summary>药丸本体，给主窗口算「标题栏穿透区」用（药丸在标题栏里，不穿透的话鼠标事件会被系统当成拖动窗口吃掉）。</summary>
    public FrameworkElement PillElement => Pill;

    private bool _open;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _openStory;
    private double _pillWidth;          // 收起时药丸的自然宽度，收回去时的目标

    private static Microsoft.UI.Xaml.Media.Animation.DoubleAnimation Anim(
        Microsoft.UI.Xaml.Media.Animation.Storyboard sb, DependencyObject target, string path,
        double from, double to, int ms, Microsoft.UI.Xaml.Media.Animation.EasingFunctionBase ease, bool dependent, int beginMs = 0)
    {
        var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease,
            EnableDependentAnimation = dependent, BeginTime = TimeSpan.FromMilliseconds(beginMs),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, path);
        sb.Children.Add(a);
        return a;
    }

    /// <summary>展开后的宽度：药丸左边固定在搜索栏右侧，向右撑开，给窗口按钮留 150 + 一点回弹余量。</summary>
    private double ExpandedWidth()
    {
        var rootWidth = _root?.Size.Width ?? 1120;
        return Math.Max(260, Math.Min(340, rootWidth - 150 - Margin.Left - 16));
    }

    /// <summary>展开：药丸自己向右、向下连续变大（宽度 + 高度 + 内容淡入），宽度带一点点回弹（先冲过一点再稳住）。</summary>
    private void ExpandIsland()
    {
        if (_open) return;
        _open = true;
        _openStory?.Stop();
        if (BodyHost.Visibility != Visibility.Visible && Pill.ActualWidth > 0) _pillWidth = Pill.ActualWidth;   // 完全收起时才记自然宽度

        var fromW = Pill.ActualWidth > 0 ? Pill.ActualWidth : 160;
        var fromH = BodyHost.Visibility == Visibility.Visible ? BodyHost.ActualHeight : 0;
        var toW = ExpandedWidth();
        Body.Width = toW - 2;
        Body.Measure(new Windows.Foundation.Size(toW - 2, double.PositiveInfinity));
        var toH = Body.DesiredSize.Height;

        Pill.MaxWidth = 400;
        Pill.Background = (Brush)Application.Current.Resources["AppPanelBrush"];     // 展开后用不透明底，后面页面的文字不会透出来
        BodyHost.Visibility = Visibility.Visible;
        if (!_ui.AnimationsEnabled)
        {
            Pill.Width = toW; BodyHost.Height = double.NaN; Body.Opacity = 1;
            return;
        }

        Pill.Width = fromW;
        BodyHost.Height = fromH;
        Body.Opacity = fromH > 0 ? Body.Opacity : 0;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Anim(sb, Pill, "Width", fromW, toW, 360,
            new Microsoft.UI.Xaml.Media.Animation.BackEase { Amplitude = 0.35, EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }, true);
        Anim(sb, BodyHost, "Height", fromH, toH, 320,
            new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }, true);
        Anim(sb, Body, "Opacity", Body.Opacity, 1, 200,
            new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }, false, 110);
        sb.Completed += (_, _) =>
        {
            if (!_open) return;
            Pill.Width = toW; BodyHost.Height = double.NaN; Body.Opacity = 1;     // 高度交还给内容，之后列表变化会自己撑开
        };
        _openStory = sb;
        sb.Begin();
    }

    /// <summary>收起：宽度、高度缩回药丸大小，内容先淡出；结束后才 Collapsed。</summary>
    private void CollapseIsland()
    {
        if (!_open) return;
        _open = false;
        _openStory?.Stop();
        void Reset()
        {
            BodyHost.Visibility = Visibility.Collapsed;
            BodyHost.Height = 0;
            Body.Width = double.NaN;
            Body.Opacity = 1;
            Pill.Width = double.NaN;
            Pill.MaxWidth = 232;
            Pill.Background = (Brush)Application.Current.Resources["AppCardBrush"];
        }
        if (!_ui.AnimationsEnabled || BodyHost.Visibility != Visibility.Visible) { Reset(); return; }

        var fromW = Pill.ActualWidth;
        var fromH = BodyHost.ActualHeight;
        var toW = _pillWidth > 0 ? _pillWidth : 160;
        Pill.Width = fromW;
        BodyHost.Height = fromH;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut };
        Anim(sb, Pill, "Width", fromW, toW, 240, ease, true);
        Anim(sb, BodyHost, "Height", fromH, 0, 240, ease, true);
        Anim(sb, Body, "Opacity", Body.Opacity, 0, 110,
            new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn }, false);
        sb.Completed += (_, _) =>
        {
            if (_open || !ReferenceEquals(_openStory, sb)) return;     // 中途又展开了，别把它收掉
            Reset();
        };
        _openStory = sb;
        sb.Begin();
    }

    private const int DoneHoldMs = 3500;
    private const int FailedHoldMs = 8000;
    private const int CollapseDelayMs = 700;

    private readonly UISettings _ui = new();
    private readonly HashSet<string> _celebrated = new();
    private readonly DispatcherTimer _holdTimer = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(CollapseDelayMs) };
    private IslandState _state = IslandState.Hidden;
    private bool _hover;
    private bool _pinnedOpen;
    private bool _hooked;
    // 窗口窄于 1200 DIP 时，紧凑态只留进度环/状态图标（与居中搜索框不相撞）；展开卡片仍显示全部信息。
    private const double NarrowBelowDip = 1200;
    private bool _narrow;
    private Microsoft.UI.Xaml.XamlRoot? _root;

    public TaskIsland()
    {
        InitializeComponent();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); if (!_hover) Settle(); };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_hover && !_pinnedOpen) { Refresh(); } };
        _incomingTimer.Tick += (_, _) => { _incomingTimer.Stop(); if (!_hover && !_pinnedOpen) Refresh(); };
        Loaded += (_, _) =>
        {
            if (_hooked) return;
            _hooked = true;
            TaskCenter.Changed += TaskCenter_Changed;
            _lastTopNoteId = TaskCenter.Notifications.FirstOrDefault()?.Id ?? "";
            TaskCenter.NotificationsChanged += TaskCenter_NotificationsChanged;
            _root = XamlRoot;
            if (_root is not null) _root.Changed += XamlRoot_Changed;
            UpdateNarrow();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!_hooked) return;
            _hooked = false;
            TaskCenter.Changed -= TaskCenter_Changed;
            TaskCenter.NotificationsChanged -= TaskCenter_NotificationsChanged;
            if (_root is not null) _root.Changed -= XamlRoot_Changed;
            _root = null;
            _holdTimer.Stop();
            _collapseTimer.Stop();
            _incomingTimer.Stop();
        };
    }

    private void XamlRoot_Changed(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        if (UpdateNarrow() && Pill.Visibility == Visibility.Visible) Refresh();
    }

    private bool UpdateNarrow()
    {
        var narrow = (_root?.Size.Width ?? double.MaxValue) < NarrowBelowDip;
        if (narrow == _narrow) return false;
        _narrow = narrow;
        return true;
    }

    private void TaskCenter_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void TaskCenter_NotificationsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var top = TaskCenter.Notifications.FirstOrDefault();
        if (top is not null && top.Id != _lastTopNoteId)
        {
            _lastTopNoteId = top.Id;
            // 任务完成 / 失败的通知由「完成 / 失败」状态展示，这里只自动展开和任务无关的通知（邀请、对方的回复等）
            if (top.SourceTaskId.Length == 0) { ShowIncoming(top); return; }
        }
        Refresh();
    });

    /// <summary>有新通知：自动展开一会儿。邀请要等你回复，停得久一些。必须在界面线程调用。</summary>
    public void ShowIncoming(TaskNotification note)
    {
        var ms = note.InviteOpen ? IncomingInviteMs : IncomingNoticeMs;
        _incomingUntil = DateTime.Now.AddMilliseconds(ms);
        _incomingTimer.Stop();
        _incomingTimer.Interval = TimeSpan.FromMilliseconds(ms + 150);
        _incomingTimer.Start();
        Refresh();
    }

    /// <summary>用户展开岛时把通知记为已读（角标清零）。只在有未读时才动，避免互相触发。</summary>
    private static void MarkSeen()
    {
        if (TaskCenter.Notifications.Any(n => !n.IsRead)) TaskCenter.MarkAllRead();
    }

    // ------------------------------------------------------------ 状态

    private void Refresh()
    {
        var active = TaskCenter.Entries.Where(t => t.IsActive).ToList();
        if (active.Count > 0)
        {
            _holdTimer.Stop();
            var wantExpanded = _pinnedOpen || _hover || IncomingActive;
            Show(wantExpanded ? IslandState.Expanded : IslandState.Compact, active[0], active);
            return;
        }

        // 没有进行中任务：找最近一条还没庆祝过的结束任务
        var finished = TaskCenter.Entries.FirstOrDefault(t => !t.IsActive && (t.Status == "已完成" || t.Status == "失败") && !_celebrated.Contains(t.Id));
        if (finished is not null && (_state != IslandState.Hidden || DateTime.Now - ParseTime(finished) < TimeSpan.FromSeconds(30)))
        {
            _celebrated.Add(finished.Id);
            var failed = finished.Status == "失败";
            Show(failed ? IslandState.Failed : IslandState.Done, finished, []);
            _holdTimer.Interval = TimeSpan.FromMilliseconds(failed ? FailedHoldMs : DoneHoldMs);
            _holdTimer.Stop();
            _holdTimer.Start();
            return;
        }

        if (_state is IslandState.Done or IslandState.Failed) return;   // 还在停留期，等计时器
        ShowIdle();
    }

    /// <summary>没有任务：药丸一直在，显示铃铛和未读数；展开（悬停 / 点击 / 有新通知）时给出通知列表。</summary>
    private void ShowIdle()
    {
        var expanded = _pinnedOpen || _hover || IncomingActive;
        Show(expanded ? IslandState.Expanded : IslandState.Idle, null, []);
    }

    private void Settle()
    {
        if (_hover) return;
        _pinnedOpen = false;
        Refresh();
        if (_state is IslandState.Done or IslandState.Failed) ShowIdle();
    }

    private static DateTime ParseTime(TaskEntry entry)
        => DateTime.TryParse(entry.TimeText, out var t) ? DateTime.Today + t.TimeOfDay : DateTime.MinValue;

    // ------------------------------------------------------------ 绘制

    private void Show(IslandState state, TaskEntry? primary, List<TaskEntry> active)
    {
        var wasHidden = Pill.Visibility != Visibility.Visible;
        _state = state;
        Pill.Visibility = Visibility.Visible;

        var idle = primary is null;                       // 没有任务：铃铛 + 未读数
        var isActive = !idle && state is IslandState.Compact or IslandState.Expanded;
        var unread = TaskCenter.Notifications.Count(n => !n.IsRead);
        Ring.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        Ring.IsActive = isActive;
        StateIcon.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
        if (idle)
        {
            StateIcon.Glyph = "\uE7ED";
            StateIcon.Foreground = (Brush)Application.Current.Resources[unread > 0 ? "TintTasksBrush" : "AppText2Brush"];
        }
        else if (!isActive)
        {
            var failed = state == IslandState.Failed;
            StateIcon.Glyph = failed ? "\uE783" : "\uE73E";
            StateIcon.Foreground = (Brush)Application.Current.Resources[failed ? "AppWarnBrush" : "AppAccentBrush"];
        }

        TitleText.Text = idle
            ? (unread > 0 ? $"{unread} 条未读" : "任务与通知")
            : state switch
            {
                IslandState.Done => $"{primary!.Title} · 已完成",
                IslandState.Failed => $"{primary!.Title} · 失败",
                _ => primary!.Title,
            };
        var showPercent = isActive && !primary!.IsIndeterminate;
        // 灵动岛只放「一眼能看完」的东西：有确定进度就给百分比，否则留空只转圈；
        // 细节文案属于任务页的步骤行，不要再往这里塞。
        PercentText.Text = idle ? "" : showPercent ? $"{Math.Round(primary!.Progress)}%" : "";
        PercentText.MaxWidth = showPercent ? 60 : 140;
        Bar.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        Bar.IsIndeterminate = isActive && primary!.IsIndeterminate;
        if (isActive && !primary!.IsIndeterminate) Bar.Value = primary.Progress;

        var more = active.Count - 1;
        MoreChip.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreText.Text = $"+{more}";

        var expanded = state == IslandState.Expanded;
        if (expanded)
        {
            BuildBody(active);
            ExpandIsland();
        }
        else
        {
            CollapseIsland();
        }
        CompactRow.Visibility = Visibility.Visible;
        var iconOnly = false;                   // 搜索栏已经左移，药丸有地方放文字；展开时向右撑开
        TitleText.Visibility = PercentText.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        if (iconOnly) MoreChip.Visibility = Visibility.Collapsed;
        CompactRow.ColumnSpacing = iconOnly ? 0 : 9;
        Pill.MinWidth = iconOnly ? 0 : 160;
        var tip = idle ? TitleText.Text : string.IsNullOrWhiteSpace(primary!.Detail) ? TitleText.Text : $"{TitleText.Text} · {primary.Detail}";
        // 悬停提示只在构造时设置一次，不在这里动：提示框正开着时去改 / 清掉它会抛 0x8000FFFF（灾难性故障）。
        AutomationProperties.SetName(Pill, TitleText.Text);

        if (wasHidden) AnimateIn();
    }

    private void Hide()
    {
        if (Pill.Visibility != Visibility.Visible) { _state = IslandState.Hidden; return; }
        _state = IslandState.Hidden;
        Ring.IsActive = false;
        CollapseIsland();
        Pill.Visibility = Visibility.Collapsed;
    }

    private void AnimateIn()
    {
        if (!_ui.AnimationsEnabled) { PillScale.ScaleX = PillScale.ScaleY = 1; Pill.Opacity = 1; return; }
        PillScale.ScaleX = PillScale.ScaleY = 0.86;
        Pill.Opacity = 0;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        void Add(DependencyObject target, string path, double from, double to)
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(240), EnableDependentAnimation = true,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, path);
            sb.Children.Add(a);
        }
        Add(PillScale, "ScaleX", 0.86, 1);
        Add(PillScale, "ScaleY", 0.86, 1);
        Add(Pill, "Opacity", 0, 1);
        sb.Begin();
    }

    private void BuildBody(List<TaskEntry> active)
    {
        // Body 里固定三块：任务区（每次重建）、通知区（内容变了才重建，免得进度刷新时把正在点的按钮换掉）、查看全部
        if (_taskHost is null || _noteHost is null || _allButton is null || Body.Children.Count != 3)
        {
            Body.Children.Clear();
            _taskHost = new StackPanel { Spacing = 8 };
            _noteHost = new StackPanel { Spacing = 8 };
            _allButton = new Button
            {
                Content = "查看全部", Height = 30, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 11.5,
            };
            _allButton.Click += (_, _) => App.MainWindow?.Navigate("tasks");
            Body.Children.Add(_taskHost);
            Body.Children.Add(_noteHost);
            Body.Children.Add(_allButton);
            _noteSignature = "";
        }

        BuildTasks(active);
        BuildNotes(active.Count > 0);
    }

    private void BuildTasks(List<TaskEntry> active)
    {
        _taskHost!.Children.Clear();
        foreach (var entry in active.Take(3))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock
            {
                Text = entry.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AppTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = "进行中", FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"], TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new ProgressBar
            {
                Height = 3, Minimum = 0, Maximum = 100, Value = entry.Progress, IsIndeterminate = entry.IsIndeterminate,
                Foreground = (Brush)Application.Current.Resources["TintTasksBrush"],
            });
            row.Children.Add(text);

            if (TaskCenter.CanCancel(entry))
            {
                var cancel = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }, Width = 28, Height = 28, Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center, Tag = entry,
                };
                ToolTipService.SetToolTip(cancel, "取消");
                AutomationProperties.SetName(cancel, $"取消 {entry.Title}");
                cancel.Click += (s, e) => { if (((FrameworkElement)s).Tag is TaskEntry t) TaskCenter.Cancel(t); };
                Grid.SetColumn(cancel, 1);
                row.Children.Add(cancel);
            }
            _taskHost.Children.Add(row);
        }
    }

    /// <summary>最近 4 条通知。邀请没回复时带「接受 / 拒绝」，回复后变成结果文字。</summary>
    private void BuildNotes(bool hasTasks)
    {
        var notes = TaskCenter.Notifications.Take(4).ToList();
        var signature = string.Join("|", notes.Select(n => $"{n.Id}:{n.InviteOpen}:{n.ResultText}")) + (hasTasks ? "+t" : "");
        if (signature == _noteSignature) return;
        _noteSignature = signature;

        _noteHost!.Children.Clear();
        if (notes.Count == 0)
        {
            if (!hasTasks)
            {
                _noteHost.Children.Add(new TextBlock
                {
                    Text = "没有进行中的任务，也没有通知", FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
                });
            }
            return;
        }

        if (hasTasks)
        {
            _noteHost.Children.Add(new TextBlock
            {
                Text = "通知", FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
            });
        }

        foreach (var note in notes)
        {
            var card = new StackPanel { Spacing = 4 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock
            {
                Text = note.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250,
            });
            head.Children.Add(new TextBlock
            {
                Text = note.TimeText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
            });
            card.Children.Add(head);
            if (!string.IsNullOrWhiteSpace(note.Message))
            {
                card.Children.Add(new TextBlock
                {
                    Text = note.Message, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                    Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
                });
            }

            if (note.InviteOpen)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
                var accept = new Button { Content = "接受", Height = 30, Padding = new Thickness(16, 0, 16, 0), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                var decline = new Button { Content = "拒绝", Height = 30, Padding = new Thickness(16, 0, 16, 0) };
                async void Respond(bool ok)
                {
                    accept.IsEnabled = decline.IsEnabled = false;
                    try { await App.Online.RespondToInviteAsync(note, ok); }
                    finally { _noteSignature = ""; Refresh(); }
                }
                accept.Click += (_, _) => Respond(true);
                decline.Click += (_, _) => Respond(false);
                buttons.Children.Add(accept);
                buttons.Children.Add(decline);
                card.Children.Add(buttons);
            }
            else if (note.ResultText.Length > 0)
            {
                card.Children.Add(new TextBlock
                {
                    Text = note.ResultText, FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
                });
            }

            var wrap = new Border
            {
                Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Child = card,
                Background = (Brush)Application.Current.Resources["AppChipBrush"],
            };
            _noteHost.Children.Add(wrap);
        }
    }

    // ------------------------------------------------------------ 交互

    private void Pill_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        _collapseTimer.Stop();
        _holdTimer.Stop();
        if (_state is IslandState.Compact or IslandState.Idle) { Refresh(); MarkSeen(); }      // 悬停即展开
    }

    private void Pill_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hover = false;
        if (_state is IslandState.Done or IslandState.Failed)
        {
            _holdTimer.Interval = TimeSpan.FromMilliseconds(1200);
            _holdTimer.Start();
            return;
        }
        _collapseTimer.Stop();
        _collapseTimer.Start();                            // 离开 700 ms 后收起，不会一晃就缩
    }

    private void Pill_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { } src && IsInsideButton(src)) return;   // 取消/查看全部自己处理
        if (_state is IslandState.Done or IslandState.Failed)
        {
            var tag = TaskCenter.Entries.FirstOrDefault(t => !t.IsActive)?.PageTag;
            if (!string.IsNullOrWhiteSpace(tag)) App.MainWindow?.Navigate(tag);
            return;
        }
        _pinnedOpen = !_pinnedOpen && _state is IslandState.Compact or IslandState.Idle;
        Refresh();
        if (_pinnedOpen) MarkSeen();
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (n is Button) return true;
        return false;
    }
}
