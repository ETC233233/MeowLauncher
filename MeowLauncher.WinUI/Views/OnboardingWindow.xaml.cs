using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeowLauncher.WinUI.Views;

/// <summary>新手引导独立窗口：只负责窗口壳与尺寸，网页内容和消息全部由 MainWindow 驱动。</summary>
public sealed partial class OnboardingWindow : Window
{
    public OnboardingWindow(double scale)
    {
        InitializeComponent();
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            // 设计稿卡片固定 1010x540，四周留白后按 DPI 换算成物理像素
            var width = (int)Math.Round(1180 * scale);
            var height = (int)Math.Round(780 * scale);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var x = area.X + Math.Max(0, (area.Width - width) / 2);
            var y = area.Y + Math.Max(0, (area.Height - height) / 2);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            App.Trace("OnboardingWindow: " + ex.GetType().Name + " " + ex.Message);
        }
    }
}
