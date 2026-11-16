using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 绘画式的猫猫 logo：把 logo 切成 32×32 的像素块，按「铺底色 → 画脸 → 点睛」的顺序一块块上色，
/// 画完后放大缩小地「呼吸」。用作冷启动遮罩。可见时才跑，不可见自动停。
/// </summary>
public sealed partial class CatLogoPainter : UserControl
{
    private const int Grid = 32;
    private const double Cell = 3;                       // 32 × 3 = 96
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Rectangle[] _order = Array.Empty<Rectangle>();
    private int _shown;
    private int _hold;
    private bool _loaded;

    public CatLogoPainter()
    {
        InitializeComponent();
        _timer.Tick += Tick;
        Loaded += async (_, _) => { await EnsureBuiltAsync(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task EnsureBuiltAsync()
    {
        if (_loaded) return;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "default_icon.png");
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var w = (int)decoder.PixelWidth; var h = (int)decoder.PixelHeight;
            var data = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

            var cells = new List<(Rectangle Rect, double Key)>();
            var rnd = new Random(7);
            for (var gy = 0; gy < Grid; gy++)
            for (var gx = 0; gx < Grid; gx++)
            {
                // 取块中心像素当这一块的颜色（像素画，不做平均，边缘保持锐利）
                var px = Math.Min(w - 1, (int)((gx + 0.5) * w / Grid));
                var py = Math.Min(h - 1, (int)((gy + 0.5) * h / Grid));
                var i = (py * w + px) * 4;
                byte b = data[i], g = data[i + 1], r = data[i + 2], a = data[i + 3];
                if (a < 96) continue;
                // 顺序：绿底(0) → 奶油色脸(1) → 其它深色细节(2)；同层内从左上往右下扫，带一点随机抖动像在涂
                var lum = (r * 299 + g * 587 + b * 114) / 1000;
                var layer = (g > r + 25 && lum < 200) ? 0 : (lum >= 200 ? 1 : 2);
                var key = layer * 100 + (gx + gy) * 1.2 + rnd.NextDouble() * 10;
                var rect = new Rectangle
                {
                    Width = Cell + 0.5, Height = Cell + 0.5,                 // 多 0.5 px 盖住缝
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
                    Opacity = 0, IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, gx * Cell); Canvas.SetTop(rect, gy * Cell);
                Paint.Children.Add(rect);
                cells.Add((rect, key));
            }
            _order = cells.OrderBy(c => c.Key).Select(c => c.Rect).ToArray();
            _loaded = true;
        }
        catch
        {
            Fallback.Visibility = Visibility.Visible;       // 读不到就显示静态 logo
            _timer.Stop();
        }
    }

    private void Tick(object? sender, object e)
    {
        if (!_loaded || _order.Length == 0) return;
        if (_shown < _order.Length)
        {
            var step = Math.Max(1, _order.Length / 70);      // 约 1.1 秒画完
            for (var k = 0; k < step && _shown < _order.Length; k++) _order[_shown++].Opacity = 1;
            return;
        }
        // 画完后不再擦掉：一直一放一缩地「呼吸」，直到遮罩消失
        _hold++;
        var s = 1.0 + 0.09 * Math.Sin(_hold * 2 * Math.PI / 90.0);   // 周期 90 帧 ≈ 1.5 秒
        Pulse.ScaleX = s; Pulse.ScaleY = s;
    }
}
