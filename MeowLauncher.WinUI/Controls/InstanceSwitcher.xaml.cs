using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeowLauncher.WinUI.Controls;

public sealed partial class InstanceSwitcher : UserControl
{
    private bool _hooked;

    /// <summary>窄窗口时只显示名称，不显示版本·加载器。</summary>
    private bool _compact;
    public bool Compact
    {
        get => _compact;
        set { _compact = value; Refresh(); }
    }

    private string _label = "";

    /// <summary>非空时显示成固定文案按钮（如首页大卡上的「换实例」），不再显示当前实例名与版本。</summary>
    public string Label
    {
        get => _label;
        set { _label = value ?? ""; Refresh(); }
    }

    public double ChipHeight { set => Chip.Height = value; }

    /// <summary>压在封面图上（首页大卡）时用深色半透明底 + 白字，和旁边的「实例设置」按钮同一套。</summary>
    public bool OnCover
    {
        set
        {
            if (!value) return;
            Chip.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x0A, 0x0A, 0x0A));
            Chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            NameText.Foreground = new SolidColorBrush(Colors.White);
            SubText.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            Chevron.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>切换成功后触发（页面据此重载自己的列表）。</summary>
    public event Action<VersionInfo>? Switched;

    /// <summary>切换失败（后端没写成）；页面有自己的 Toast 就订阅它，没订阅时用一个简单对话框兜底。</summary>
    public event Action<string>? Failed;

    public InstanceSwitcher()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_hooked) return;
            _hooked = true;
            App.Backend.SnapshotArrived += Backend_SnapshotArrived;
            InstanceSelection.Changed += InstanceSelection_Changed;
            Picker.Picked += Picker_Picked;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!_hooked) return;
            _hooked = false;
            App.Backend.SnapshotArrived -= Backend_SnapshotArrived;
            InstanceSelection.Changed -= InstanceSelection_Changed;
            Picker.Picked -= Picker_Picked;
        };
    }

    public void Refresh()
    {
        var current = App.MainWindow?.Snapshot?.Current;
        var has = current is not null;
        var fixedLabel = _label.Length > 0;
        Dot.Visibility = has && !fixedLabel ? Visibility.Visible : Visibility.Collapsed;
        if (has && !fixedLabel) IconBrush.ImageSource = InstanceIcon.For(current);
        NameText.Text = fixedLabel ? _label : current?.Id ?? "未选择实例";
        NameText.FontWeight = fixedLabel ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        // 实例名常常就等于版本号（26.3），这时候副标题里再重复一遍很蠢：只留加载器状态。
        SubText.Text = fixedLabel || current is null
            ? ""
            : string.Equals(current.Id, current.GameVersion, StringComparison.OrdinalIgnoreCase)
                ? current.LoaderDisplay
                : $"{current.GameVersion} · {current.LoaderDisplay}";
        SubText.Visibility = fixedLabel || Compact ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Chip, has ? $"当前实例 {current!.Id}，点击切换" : "还没有实例");
    }

    private void Backend_SnapshotArrived(Snapshot _) => DispatcherQueue.TryEnqueue(Refresh);
    private void InstanceSelection_Changed(VersionInfo _) => DispatcherQueue.TryEnqueue(Refresh);

    private void PickFlyout_Opening(object? sender, object e) => Picker.Reload();
    private void PickFlyout_Opened(object? sender, object e) => Picker.FocusFilter();

    private void ManageAll_Click(object sender, RoutedEventArgs e)
    {
        PickFlyout.Hide();
        App.MainWindow?.Navigate("versions");
    }

    private async void Picker_Picked(VersionInfo version)
    {
        try
        {
            PickFlyout.Hide();
            var (ok, message) = await InstanceSelection.SelectAsync(version);
            if (ok) Switched?.Invoke(version);
            else if (Failed is not null) Failed.Invoke(message);
            else await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "切换失败", Content = message, CloseButtonText = "好",
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.LogCrash("InstanceSwitcher", ex);
        }
    }
}
