using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 宿主用法：
///   panel.Load(extra);                 // 回填，不触发 Changed
///   panel.Changed += () => 防抖 600ms 后 SetLaunchExtraAsync(scope, panel.Read());
///   panel.IsEnabled = !useGlobal;     // 实例页"跟随全局"时整块变灰
/// </summary>
public sealed partial class LaunchExtraPanel : UserControl
{
    private bool _loading;
    public event Action? Changed;

    public LaunchExtraPanel() => InitializeComponent();

    public void Load(LaunchExtra x)
    {
        _loading = true;
        try
        {
            FullscreenSwitch.IsOn = x.Fullscreen;
            WidthBox.Value = x.Width > 0 ? x.Width : double.NaN;
            HeightBox.Value = x.Height > 0 ? x.Height : double.NaN;
            WidthBox.IsEnabled = HeightBox.IsEnabled = !x.Fullscreen;
            ServerBox.Text = x.Server;
            GameArgsBox.Text = x.GameArgs;
            EnvBox.Text = x.Env;
            WrapperBox.Text = x.Wrapper;
            PriorityBox.SelectedIndex = x.Priority switch { "below" => 0, "above" => 2, _ => 1 };
        }
        finally { _loading = false; }
    }

    public LaunchExtra Read() => new(
        double.IsNaN(WidthBox.Value) ? 0 : (int)WidthBox.Value,
        double.IsNaN(HeightBox.Value) ? 0 : (int)HeightBox.Value,
        FullscreenSwitch.IsOn,
        (ServerBox.Text ?? "").Trim(), (GameArgsBox.Text ?? "").Trim(),
        (EnvBox.Text ?? "").Trim(), (WrapperBox.Text ?? "").Trim(),
        (PriorityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal");

    private void Raise()
    {
        if (_loading) return;
        WidthBox.IsEnabled = HeightBox.IsEnabled = !FullscreenSwitch.IsOn;
        Changed?.Invoke();
    }

    private void AnyChanged(object sender, RoutedEventArgs e) => Raise();
    private void AnyChanged2(NumberBox sender, NumberBoxValueChangedEventArgs e) => Raise();
    private void AnyChanged3(object sender, TextChangedEventArgs e) => Raise();
    private void AnyChanged4(object sender, SelectionChangedEventArgs e) => Raise();
}
