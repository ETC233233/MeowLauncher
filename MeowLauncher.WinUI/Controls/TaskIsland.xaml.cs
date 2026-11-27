using System.ComponentModel;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace MeowLauncher.WinUI.Controls;

/// <summary>One adaptive entry: quiet when idle, instance progress while working, then a brief result.</summary>
public sealed partial class TaskIsland : UserControl
{
    private enum IslandMode { Idle, Working, Result }
    private IslandMode? _mode;
    private Storyboard? _transition;
    private int _transitionVersion;
    private double _targetWidth = 132;
    private double _workingWidth = 292;
    private readonly TextBlock _measure = new() { FontSize = 13 };
    private string? _arrivalId;
    private readonly DispatcherTimer _arrivalTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _hooked;
    private TaskEntry? _primary;
    private DateTime _holdUntil = DateTime.MinValue;
    private readonly HashSet<string> _celebrated = new();
    private readonly DispatcherTimer _holdTimer = new();

    public FrameworkElement PillElement => Pill;
    public bool IsExpanded => App.MainWindow?.IsActivityCenterOpen == true;

    public TaskIsland()
    {
        InitializeComponent();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); Refresh(); };
        _arrivalTimer.Tick += (_, _) => { _arrivalTimer.Stop(); _arrivalId = null; UpdateCompact(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked) return;
        _hooked = true;
        foreach (var entry in TaskCenter.Entries.Where(e => !e.IsActive)) _celebrated.Add(entry.Id);
        TaskCenter.Changed += OnChanged;
        TaskCenter.NotificationsChanged += OnChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        _hooked = false;
        TaskCenter.Changed -= OnChanged;
        TaskCenter.NotificationsChanged -= OnChanged;
        _holdTimer.Stop(); _arrivalTimer.Stop(); _transition?.Stop(); _mode = null;
        if (_primary is not null) _primary.PropertyChanged -= PrimaryChanged;
        _primary = null;
    }

    private void OnChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);
    private void PrimaryChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateCompact);
    public void Open() => App.MainWindow?.OpenTaskCenter();
    public void Close() => App.MainWindow?.CloseActivityCenter();
    internal void RestoreEntryFocus(bool notifications) => Trigger.Focus(FocusState.Programmatic);
    internal Point ArrivalPoint(UIElement root, TaskEntry incoming)
    {
        var rect = Pill.TransformToVisual(root).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        var width = _primary?.IsActive == true ? _workingWidth : PreferredWidth(incoming);
        return new Point(rect.Right - width + 25, rect.Top + 18);
    }
    internal void BeginArrival(string id)
    {
        _arrivalId = id; _arrivalTimer.Stop(); _arrivalTimer.Start(); UpdateCompact();
    }
    internal void FinishArrival(string id)
    {
        if (_arrivalId != id) return;
        _arrivalId = null; _arrivalTimer.Stop(); UpdateCompact();
    }

    private void Refresh()
    {
        if (!_hooked) return;
        if (_primary is { IsActive: false } finished && finished.Status is "已完成" or "失败" && _celebrated.Add(finished.Id))
        {
            _holdUntil = DateTime.Now.AddMilliseconds(finished.Status == "失败" ? 6000 : 1600);
            _holdTimer.Interval = _holdUntil - DateTime.Now;
            _holdTimer.Stop(); _holdTimer.Start();
        }
        var entries = TaskCenter.Entries.Select(e => new IslandActivity(e.Id, e.IsActive, e.Status is "已完成" or "失败")).ToList();
        var id = IslandSelection.Choose(_primary?.Id, entries, false, DateTime.Now, _holdUntil);
        var selected = TaskCenter.Entries.FirstOrDefault(e => e.Id == id);
        if (!ReferenceEquals(selected, _primary))
        {
            if (_primary is not null) _primary.PropertyChanged -= PrimaryChanged;
            _primary = selected;
            if (_primary?.IsActive == true) _workingWidth = 292;
            _holdUntil = DateTime.MinValue;
            if (_primary is not null) _primary.PropertyChanged += PrimaryChanged;
        }
        UpdateCompact();
    }

    private void UpdateCompact()
    {
        if (!_hooked) return;
        var primary = _primary;
        var active = primary?.IsActive == true;
        var failed = primary?.Status == "失败";
        var mode = primary is null ? IslandMode.Idle : active ? IslandMode.Working : IslandMode.Result;
        // Keep the outgoing labels intact until their fade-out finishes.
        if (primary is not null)
        {
            StateIcon.Glyph = active ? "\uE896" : failed ? "\uE783" : "\uE73E";
            StateIcon.Foreground = (Brush)Application.Current.Resources[failed ? "AppBadBrush" : active ? "AppText2Brush" : "AppAccentBrush"];
            PrimaryIcon.Source = primary.TargetIcon;
            PrimaryIcon.Visibility = active && PrimaryIcon.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            PrimaryIcon.Opacity = primary.Id == _arrivalId ? 0 : 1;
            StateIcon.Visibility = PrimaryIcon.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            TitleText.Text = Summary(primary);
            PercentText.Text = active && !primary!.IsIndeterminate ? $"{primary.Progress:0.#}%" : "";
            PercentText.Visibility = PercentText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var unread = TaskCenter.UnreadCount;
        UnreadDot.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CompactIndeterminate.Visibility = active && primary!.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        CompactFill.Visibility = active && !primary!.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        CompactScale.ScaleX = Math.Clamp(primary?.Progress ?? 0, 0, 100) / 100;
        AutomationProperties.SetName(Trigger, mode == IslandMode.Idle
            ? unread > 0 ? $"任务与通知，{unread} 条未读" : "任务与通知"
            : $"任务与通知，{TitleText.Text} {PercentText.Text}，{unread} 条未读");
        if (active) _workingWidth = Math.Max(_workingWidth, PreferredWidth(primary!));
        var width = mode switch { IslandMode.Working => _workingWidth, IslandMode.Result => PreferredWidth(primary!), _ => 132 };
        SetMode(mode, width);
    }

    private static string Summary(TaskEntry entry)
    {
        var phase = entry.IsActive
            ? entry.Steps.FirstOrDefault(s => s.SpinnerVisibility == Visibility.Visible)?.Title ?? entry.Detail
            : entry.Status;
        if (string.IsNullOrWhiteSpace(phase)) phase = "正在准备…";
        return $"{Views.TaskVisuals.DisplayTitle(entry.Title)} · {phase}";
    }

    private double PreferredWidth(TaskEntry entry)
    {
        _measure.Text = Summary(entry);
        _measure.Measure(new Size(double.PositiveInfinity, 36));
        // Reserve space for the icon, gaps, padding and a stable percentage width.
        return entry.IsActive ? Math.Clamp(Math.Ceiling(_measure.DesiredSize.Width + 116), 292, 336)
            : Math.Clamp(Math.Ceiling(_measure.DesiredSize.Width + 61), 184, 248);
    }

    private void SetMode(IslandMode mode, double width)
    {
        if (_mode == mode && Math.Abs(_targetWidth - width) < .5) return;
        _targetWidth = width;
        var first = _mode is null;
        _mode = mode;
        var version = ++_transitionVersion;
        var fromWidth = Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.Width;
        var idleOpacity = IdleSurface.Opacity;
        var workOpacity = WorkSurface.Opacity;
        _transition?.Stop();
        var idle = mode == IslandMode.Idle;
        Pill.Width = fromWidth;
        IdleSurface.Opacity = idleOpacity; WorkSurface.Opacity = workOpacity;
        IdleSurface.Visibility = WorkSurface.Visibility = Visibility.Visible;
        if (first || !Motion.Enabled)
        {
            Pill.Width = width; IdleSurface.Opacity = idle ? 1 : 0; WorkSurface.Opacity = idle ? 0 : 1;
            IdleSurface.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
            WorkSurface.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        var story = new Storyboard();
        void Animate(DependencyObject target, string property, double from, double to, int duration, int delay = 0)
        {
            var animation = new DoubleAnimation { From = from, To = to, BeginTime = TimeSpan.FromMilliseconds(delay), Duration = TimeSpan.FromMilliseconds(duration),
                EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property); story.Children.Add(animation);
        }
        Animate(Pill, "Width", fromWidth, width, 360);
        Animate(IdleSurface, "Opacity", idleOpacity, idle ? 1 : 0, idle ? 180 : 100, idle ? 100 : 0);
        Animate(WorkSurface, "Opacity", workOpacity, idle ? 0 : 1, idle ? 100 : 180, idle ? 0 : 80);
        story.Completed += (_, _) =>
        {
            if (version != _transitionVersion) return;
            Pill.Width = width;
            IdleSurface.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
            WorkSurface.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        };
        _transition = story; story.Begin();
    }

    private void Trigger_Click(object sender, RoutedEventArgs e) => App.MainWindow?.ToggleUnifiedActivityCenter(_primary is not null);
}
