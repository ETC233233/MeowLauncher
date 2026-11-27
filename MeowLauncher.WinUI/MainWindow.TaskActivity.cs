using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using MeowLauncher.WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace MeowLauncher.WinUI;

public sealed partial class MainWindow
{
#if DEBUG
    private TaskEntry? _islandDemoTask;
#endif
    private Border? _activityDrawer;
    private TasksPage? _activityPage;
    private Grid? _activityTabHost;
    private TranslateTransform? _activityIndicatorMove;
    private Storyboard? _activityIndicatorStory;
    private double _activityIndicatorTarget = double.NaN;
    private RadioButton? _activityTasksTab, _activityNotificationsTab;
    private Button? _activityReadButton, _activityClearButton;
    private bool _activityNotifications, _activitySwitching;
    public bool IsActivityCenterOpen => _activityDrawer is not null;
    private Point _activityOrigin;
    private DateTime _activityClickAt = DateTime.MinValue;
    private readonly HashSet<string> _knownActivities = new();
    private bool _arrivalUsed;

    private void InitializeActivityInteractions()
    {
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(RootGrid).Position;
            _activityOrigin = point; _activityClickAt = DateTime.Now; _arrivalUsed = false;
            if (_activityDrawer is { } drawer)
            {
                var rect = drawer.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, drawer.ActualWidth, drawer.ActualHeight));
                var entryRect = TaskIsland.PillElement.TransformToVisual(RootGrid).TransformBounds(
                    new Rect(0, 0, TaskIsland.PillElement.ActualWidth, TaskIsland.PillElement.ActualHeight));
                if (!rect.Contains(point) && !entryRect.Contains(point)) CloseActivityCenter(false);
            }
        }), true);
        foreach (var entry in TaskCenter.Entries) _knownActivities.Add(entry.Id);
        TaskCenter.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var entry in TaskCenter.Entries.Where(e => e.IsActive))
                if (_knownActivities.Add(entry.Id)) AnimateTaskArrival(entry);
            UpdateActivityHeader();
        });
        TaskCenter.NotificationsChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateActivityHeader);
        GameSessions.Changed += () => DispatcherQueue.TryEnqueue(UpdateActivityHeader);
        RootGrid.SizeChanged += (_, _) =>
        {
            if (_activityDrawer is { } drawer) drawer.MaxWidth = Math.Max(0, RootGrid.ActualWidth - 24);
        };
    }

    internal void AnimateTaskArrival(TaskEntry entry)
    {
        var source = entry.TargetIcon;
        if (source is null || _arrivalUsed || !Motion.Enabled || DateTime.Now - _activityClickAt > TimeSpan.FromSeconds(2.5) || _activityOrigin.Y < 60) return;
        _arrivalUsed = true;
        var target = TaskIsland.ArrivalPoint(RootGrid, entry);
        TaskIsland.BeginArrival(entry.Id);
        var image = new Image { Source = source, Width = 28, Height = 28, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(_activityOrigin.X - 14, _activityOrigin.Y - 14, 0, 0), IsHitTestVisible = false };
        Grid.SetRowSpan(image, 2); Grid.SetColumnSpan(image, 2); Canvas.SetZIndex(image, 80);
        var transform = new CompositeTransform(); image.RenderTransform = transform; image.RenderTransformOrigin = new Point(.5, .5);
        RootGrid.Children.Add(image);
        var story = new Storyboard();
        void Animate(DependencyObject element, string property, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(460), EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(animation, element); Storyboard.SetTargetProperty(animation, property); story.Children.Add(animation);
        }
        Animate(transform, "TranslateX", 0, target.X - _activityOrigin.X);
        var endY = target.Y - _activityOrigin.Y;
        var arc = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        arc.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        arc.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), Value = endY * .65 - 28,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        arc.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(460)), Value = endY,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        Storyboard.SetTarget(arc, transform); Storyboard.SetTargetProperty(arc, "TranslateY"); story.Children.Add(arc);
        Animate(transform, "ScaleX", 1, 22.0 / 28); Animate(transform, "ScaleY", 1, 22.0 / 28);
        story.Completed += (_, _) => { RootGrid.Children.Remove(image); TaskIsland.FinishArrival(entry.Id); };
        story.Begin();
    }

    public void ToggleUnifiedActivityCenter(bool hasTask)
    {
        if (IsActivityCenterOpen) CloseActivityCenter();
        else OpenActivityCenter(!hasTask && (TaskCenter.UnreadCount > 0 || _activityNotifications));
    }

    public void OpenTaskCenter() => OpenActivityCenter(false);
    public void OpenNotificationCenter() => OpenActivityCenter(true);
    private void OpenActivityCenter(bool notifications)
    {
        if (OnboardingHost.Visibility == Visibility.Visible) return;
        ClearNoticePopups();
        if (_activityDrawer is not null) { SetActivitySection(notifications); return; }
        var page = new TasksPage();
        page.ConfigureActivityDrawer(notifications);
        _activityPage = page;
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(18, 7, 12, 0), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var tabStyle = (Style)page.Resources["TaskSectionTabStyle"];
        _activityTasksTab = new RadioButton { Content = "任务", GroupName = "ActivityDrawerSection", Style = tabStyle, FontSize = 14 };
        _activityNotificationsTab = new RadioButton { Content = "通知", GroupName = "ActivityDrawerSection", Style = tabStyle, FontSize = 14 };
        AutomationProperties.SetName(_activityTasksTab, "任务");
        AutomationProperties.SetName(_activityNotificationsTab, "通知");
        _activityTasksTab.Checked += (_, _) => { if (!_activitySwitching) SetActivitySection(false); };
        _activityNotificationsTab.Checked += (_, _) => { if (!_activitySwitching) SetActivitySection(true); };
        tabs.Children.Add(_activityTasksTab); tabs.Children.Add(_activityNotificationsTab);
        tabs.KeyDown += (_, e) =>
        {
            if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
            SetActivitySection(!_activityNotifications);
            (_activityNotifications ? _activityNotificationsTab : _activityTasksTab)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        var tabHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _activityTabHost = tabHost;
        tabHost.Children.Add(tabs);
        _activityIndicatorMove = new TranslateTransform();
        var indicator = new Border { Width = 24, Height = 3, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = (Brush)Application.Current.Resources["AppAccentBrush"], RenderTransform = _activityIndicatorMove, IsHitTestVisible = false };
        tabHost.Children.Add(indicator);
        tabHost.Loaded += (_, _) => { if (ReferenceEquals(_activityTabHost, tabHost)) UpdateActivityIndicator(false); };
        tabHost.SizeChanged += (_, _) => { if (ReferenceEquals(_activityTabHost, tabHost)) UpdateActivityIndicator(true); };
        header.Children.Add(tabHost);
        var close = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Width = 32, Height = 32,
            Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(close, "关闭任务与通知");
        close.Click += (_, _) => CloseActivityCenter();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        var headerBorder = new Border { Child = header, BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"], BorderThickness = new Thickness(0, 0, 0, 1) };
        panel.Children.Add(headerBorder);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(18, 10, 18, 6), HorizontalAlignment = HorizontalAlignment.Right };
        Button Action(string label) => new() { Content = label, FontSize = 12, Padding = new Thickness(4, 3, 4, 3),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), MinHeight = 28 };
        _activityReadButton = Action("全部已读"); _activityReadButton.Click += (_, _) => TaskCenter.MarkAllRead();
        _activityClearButton = Action("清除已完成"); _activityClearButton.Click += (_, _) =>
        {
            if (_activityNotifications) TaskCenter.ClearNotifications(); else TaskCenter.ClearFinished();
        };
        actions.Children.Add(_activityReadButton); actions.Children.Add(_activityClearButton);
        Grid.SetRow(actions, 1); panel.Children.Add(actions);
        page.Margin = new Thickness(18, 0, 18, 12);
        Grid.SetRow(page, 2); panel.Children.Add(page);
        _activityDrawer = new Border
        {
            Width = 418, MaxWidth = Math.Max(0, RootGrid.ActualWidth - 24), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 54, 12, 12),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel,
        };
        Grid.SetRowSpan(_activityDrawer, 2); Grid.SetColumnSpan(_activityDrawer, 2); Canvas.SetZIndex(_activityDrawer, 50);
        RootGrid.Children.Add(_activityDrawer);
        SetActivitySection(notifications);
        Motion.EnterX(_activityDrawer, 0, 32, 220);
        (_activityNotifications ? _activityNotificationsTab : _activityTasksTab)?.Focus(FocusState.Programmatic);
    }

    private void SetActivitySection(bool notifications)
    {
        if (_activityPage is null) return;
        _activityNotifications = notifications;
        _activitySwitching = true;
        if (_activityTasksTab is not null) _activityTasksTab.IsChecked = !notifications;
        if (_activityNotificationsTab is not null) _activityNotificationsTab.IsChecked = notifications;
        _activitySwitching = false;
        _activityPage.ShowActivitySection(notifications);
        UpdateActivityHeader();
        UpdateActivityIndicator(true);
    }

    private void UpdateActivityIndicator(bool animate)
    {
        var host = _activityTabHost;
        var move = _activityIndicatorMove;
        var selected = _activityNotifications ? _activityNotificationsTab : _activityTasksTab;
        if (host is null || move is null || selected is null || selected.ActualWidth <= 0) return;
        var target = selected.TransformToVisual(host).TransformPoint(new Point(selected.ActualWidth / 2 - 12, 0)).X;
        if (!double.IsNaN(_activityIndicatorTarget) && Math.Abs(_activityIndicatorTarget - target) < .5) return;
        var first = double.IsNaN(_activityIndicatorTarget);
        _activityIndicatorTarget = target;
        var from = move.X;
        _activityIndicatorStory?.Stop();
        move.X = target;
        if (!animate || first || !Motion.Enabled) return;
        var story = new Storyboard();
        var animation = new DoubleAnimation { From = from, To = target, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, move); Storyboard.SetTargetProperty(animation, "X"); story.Children.Add(animation);
        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_activityIndicatorStory, story)) return;
            move.X = target; story.Stop(); _activityIndicatorStory = null;
        };
        _activityIndicatorStory = story; story.Begin();
    }

    private void UpdateActivityHeader()
    {
        if (_activityDrawer is null) return;
        var tasks = TaskCenter.Entries.Count + GameSessions.Running.Count;
        var notifications = TaskCenter.Notifications.Count;
        var taskText = tasks > 0 ? $"任务 {tasks}" : "任务";
        var notificationText = notifications > 0 ? $"通知 {notifications}" : "通知";
        if (_activityTasksTab is not null && !Equals(_activityTasksTab.Content, taskText)) _activityTasksTab.Content = taskText;
        if (_activityNotificationsTab is not null && !Equals(_activityNotificationsTab.Content, notificationText)) _activityNotificationsTab.Content = notificationText;
        if (_activityReadButton is not null) _activityReadButton.Visibility = _activityNotifications && TaskCenter.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_activityClearButton is not null)
        {
            _activityClearButton.Content = _activityNotifications ? "清空通知" : "清除已完成";
            _activityClearButton.Visibility = (_activityNotifications ? notifications > 0 : TaskCenter.Entries.Any(e => !e.IsActive)) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void CloseActivityCenter(bool restoreFocus = true)
    {
        if (_activityDrawer is not { } drawer) return;
        var notifications = _activityNotifications;
        _activityDrawer = null; _activityPage = null;
        _activityIndicatorStory?.Stop(); _activityIndicatorStory = null;
        _activityTabHost = null; _activityIndicatorMove = null; _activityIndicatorTarget = double.NaN;
        _activityTasksTab = _activityNotificationsTab = null;
        _activityReadButton = _activityClearButton = null;
        if (restoreFocus) TaskIsland.RestoreEntryFocus(notifications);
        drawer.IsHitTestVisible = false;
        if (!Motion.Enabled) { RootGrid.Children.Remove(drawer); return; }
        var slide = drawer.RenderTransform as TranslateTransform ?? new TranslateTransform();
        drawer.RenderTransform = slide;
        var story = new Storyboard();
        var move = new DoubleAnimation { From = slide.X, To = 32, Duration = TimeSpan.FromMilliseconds(170),
            EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation { From = drawer.Opacity, To = 0, Duration = TimeSpan.FromMilliseconds(170), EnableDependentAnimation = true };
        Storyboard.SetTarget(move, slide); Storyboard.SetTargetProperty(move, "X");
        Storyboard.SetTarget(fade, drawer); Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(move); story.Children.Add(fade);
        story.Completed += (_, _) => RootGrid.Children.Remove(drawer);
        story.Begin();
    }
}
