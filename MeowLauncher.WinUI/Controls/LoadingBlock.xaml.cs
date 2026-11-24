using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 统一的加载态：转圈 + 正在做什么；等超过 3 秒后再补一行「已等待 N 秒」，让玩家知道没卡死。
/// 可见时才转、才计时；页面照旧用 Visibility 切换即可。
/// </summary>
public sealed partial class LoadingBlock : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _since;

    public LoadingBlock()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateWait();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LoadingBlock),
        new PropertyMetadata("", (d, e) => { if (d is LoadingBlock b) b.MainText.Text = e.NewValue as string ?? ""; }));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(LoadingBlock),
        new PropertyMetadata(null, (d, e) => { if (d is LoadingBlock b && e.NewValue is Brush br) b.Ring.Foreground = br; }));

    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    private void Sync()
    {
        if (Visibility == Visibility.Visible && IsLoaded) Start(); else Stop();
    }

    private void Start()
    {
        if (Ring.IsActive) return;
        _since = DateTime.UtcNow;
        WaitText.Visibility = Visibility.Collapsed;
        Ring.IsActive = true;
        _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        Ring.IsActive = false;
        WaitText.Visibility = Visibility.Collapsed;
    }

    private void UpdateWait()
    {
        // 等待秒数已经去掉：转圈本身足够表达「还在处理」，不再报数。
        WaitText.Visibility = Visibility.Collapsed;
    }
}
