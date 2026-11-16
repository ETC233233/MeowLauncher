using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 2D 皮肤头像：从 64x64（或 64x32 legacy）皮肤 PNG 裁出头层 8x8@(8,8) 与帽层 8x8@(40,8)，
/// 按控件物理像素放大成一张透明头像。没有 3D 盒模型，也不创建 WebView2。
/// 拿不到真实皮肤时整个控件保持空白，不显示占位色块。
/// </summary>
public sealed partial class SkinHeadView : UserControl
{
    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath), typeof(string), typeof(SkinHeadView),
        new PropertyMetadata(null, OnSkinPathChanged));

    private bool _busy;
    private bool _pending;
    private string? _lastKey;

    /// <summary>没有皮肤文件时是否改用自带的默认头像（猫）。默认开：永远有头像，不再露出占位色块。</summary>
    public bool UseDefaultWhenMissing { get; set; } = true;

    private static string DefaultSkinPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "skinviewer", "demo", "default_classic.png");

    public SkinHeadView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RefreshAsync();
        SizeChanged += (_, _) => _ = RefreshAsync();
        Unloaded += (_, _) =>
        {
            HeadImage.Source = null;
            _lastKey = null;
        };
    }

    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    private static void OnSkinPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkinHeadView view)
        {
            view._lastKey = null;
            if (view.IsLoaded) _ = view.RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        var path = SkinPath;
        if ((string.IsNullOrWhiteSpace(path) || !File.Exists(path)) && UseDefaultWhenMissing && File.Exists(DefaultSkinPath))
            path = DefaultSkinPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || ActualWidth < 1 || ActualHeight < 1)
        {
            HeadImage.Visibility = Visibility.Collapsed;
            HeadImage.Source = null;
            _lastKey = null;
            return;
        }

        var width = ActualWidth;
        var raster0 = XamlRoot?.RasterizationScale ?? 1.0;
        var px = SnapToHeadPixels(width * raster0);
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + px;
        if (string.Equals(key, _lastKey, StringComparison.Ordinal)) return;

        if (_busy)
        {
            _pending = true;
            return;
        }

        _busy = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var image = await BuildAsync(bytes, px);
            if (image is null)
            {
                HeadImage.Visibility = Visibility.Collapsed;
                HeadImage.Source = null;
                _lastKey = null;
                return;
            }

            HeadImage.Width = px / raster0;              // 位图多少物理像素，就占多少物理像素
            HeadImage.Height = px / raster0;
            HeadImage.Source = image;
            HeadImage.Visibility = Visibility.Visible;
            _lastKey = key;
        }
        catch
        {
            HeadImage.Visibility = Visibility.Collapsed;
            HeadImage.Source = null;
            _lastKey = null;
        }
        finally
        {
            _busy = false;
            if (_pending)
            {
                _pending = false;
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>
    /// 把目标物理像素尺寸取成 8 的整数倍：8×8 的皮肤像素各自占同样多的物理像素。
    /// 以前 50pt@150% 得到 75px，8 列被分成 9/10/9/10…，头像看着一边胖一边瘦、边缘发毛。
    /// </summary>
    private static int SnapToHeadPixels(double devicePixels)
        => Math.Max(8, (int)Math.Round(devicePixels / 8.0) * 8);

    private async Task<SoftwareBitmapSource?> BuildAsync(byte[] bytes, int size)
    {
        using var stream = new InMemoryRandomAccessStream();
        var write = stream.AsStreamForWrite();
        await write.WriteAsync(bytes);
        await write.FlushAsync();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth != 64 || (decoder.PixelHeight != 64 && decoder.PixelHeight != 32))
            return null;

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var source = data.DetachPixelData();
        var sourceW = (int)decoder.PixelWidth;
        var sourceH = (int)decoder.PixelHeight;

        // 立体方块头：取皮肤的正面/右面/顶面，按固定角度（yaw −32°、pitch +18°）投影成一个立方体，
        // 帽层按 1.09 倍放大叠在外面。头像只有 40–50pt，用 WebView2 那套 3D 渲染器太重，这里直接软件光栅化。
        var bgra = new byte[size * size * 4];
        var depth = new double[size * size];
        for (var i = 0; i < depth.Length; i++) depth[i] = double.NegativeInfinity;
        var (scale, offX, offY) = Projection(size);
        RenderCube(source, sourceW, sourceH, bgra, depth, size, scale, offX, offY, 0.5, HeadFaces);
        RenderCube(source, sourceW, sourceH, bgra, depth, size, scale, offX, offY, 0.545, HatFaces);

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bgra.AsBuffer(), BitmapPixelFormat.Bgra8, size, size,
            BitmapAlphaMode.Premultiplied);
        var result = new SoftwareBitmapSource();
        await result.SetBitmapAsync(bitmap);
        return result;
    }

    private enum CubeFace { Front, Right, Top, Left, Back, Bottom }

    private static readonly (CubeFace Face, int Ox, int Oy)[] HeadFaces =
    {
        (CubeFace.Front, 8, 8), (CubeFace.Right, 0, 8), (CubeFace.Top, 8, 0),
        (CubeFace.Left, 16, 8), (CubeFace.Back, 24, 8), (CubeFace.Bottom, 16, 0),
    };

    private static readonly (CubeFace Face, int Ox, int Oy)[] HatFaces =
    {
        (CubeFace.Front, 40, 8), (CubeFace.Right, 32, 8), (CubeFace.Top, 40, 0),
        (CubeFace.Left, 48, 8), (CubeFace.Back, 56, 8), (CubeFace.Bottom, 48, 0),
    };

    /// <summary>帽层外扩后的半边长，用来算整张头像的包围盒，所以头层和帽层同心、不会被裁掉一角。</summary>
    private const double OuterHalf = 0.545;

    private static readonly double CosYaw = Math.Cos(-44 * Math.PI / 180);
    private static readonly double SinYaw = Math.Sin(-44 * Math.PI / 180);
    private static readonly double CosPitch = Math.Cos(14 * Math.PI / 180);
    private static readonly double SinPitch = Math.Sin(14 * Math.PI / 180);

    /// <summary>各面明暗：只靠皮肤底色分不出立体感，按面的朝向压一点亮度，方块感才出来。</summary>
    private static double FaceShade(CubeFace face) => face switch
    {
        CubeFace.Front => 1.0,
        CubeFace.Right => 0.72,
        CubeFace.Top => 1.14,
        CubeFace.Left => 0.86,
        CubeFace.Back => 0.92,
        _ => 0.62,
    };

    private static (double X, double Y, double Z) Rotate(double x, double y, double z)
    {
        var x1 = x * CosYaw + z * SinYaw;
        var z1 = -x * SinYaw + z * CosYaw;
        return (x1, y * CosPitch - z1 * SinPitch, y * SinPitch + z1 * CosPitch);
    }

    private static (double Scale, double OffX, double OffY) Projection(int size)
    {
        var minX = double.MaxValue; var maxX = double.MinValue;
        var minY = double.MaxValue; var maxY = double.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var r = Rotate(
                (i & 1) == 0 ? -OuterHalf : OuterHalf,
                (i & 2) == 0 ? -OuterHalf : OuterHalf,
                (i & 4) == 0 ? -OuterHalf : OuterHalf);
            minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.X);
            minY = Math.Min(minY, r.Y); maxY = Math.Max(maxY, r.Y);
        }

        var scale = size * 1.02 / Math.Max(maxX - minX, maxY - minY);
        return (scale,
            size / 2.0 - (minX + maxX) / 2 * scale,
            size / 2.0 + (minY + maxY) / 2 * scale);
    }

    private static (double X, double Y, double Z) FacePoint(CubeFace face, double u, double v, double half)
    {
        var a = (u - 0.5) * 2 * half;
        var b = (0.5 - v) * 2 * half;
        return face switch
        {
            CubeFace.Front => (a, b, half),
            CubeFace.Right => (half, b, -a),
            CubeFace.Top => (a, half, -b),
            CubeFace.Left => (-half, b, a),
            CubeFace.Back => (-a, b, -half),
            _ => (a, -half, b),
        };
    }

    private static (double X, double Y, double Z) FaceNormal(CubeFace face) => face switch
    {
        CubeFace.Front => (0, 0, 1),
        CubeFace.Right => (1, 0, 0),
        CubeFace.Top => (0, 1, 0),
        CubeFace.Left => (-1, 0, 0),
        CubeFace.Back => (0, 0, -1),
        _ => (0, -1, 0),
    };

    private static (double X, double Y, double Z) ProjectPoint(
        (double X, double Y, double Z) p, double scale, double offX, double offY)
        => (offX + p.X * scale, offY - p.Y * scale, p.Z);

    private static (double R, double G, double B, double A) Sample(
        byte[] source, int w, int h, int x, int y)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return (0, 0, 0, 0);
        var i = (y * w + x) * 4;
        return (source[i + 2] / 255.0, source[i + 1] / 255.0, source[i] / 255.0, source[i + 3] / 255.0);
    }

    /// <summary>把一个立方体（6 面各 8×8 皮肤像素）按当前投影画进 bgra，像素按预乘 alpha 叠加。</summary>
    private static void RenderCube(byte[] source, int sourceW, int sourceH,
        byte[] bgra, double[] depth, int size, double scale, double offX, double offY,
        double half, (CubeFace Face, int Ox, int Oy)[] faces)
    {
        foreach (var (face, ox, oy) in faces)
        {
            var n = FaceNormal(face);
            if (Rotate(n.X, n.Y, n.Z).Z <= 0.0001) continue; // 背面不画
            var shade = FaceShade(face);

            for (var ty = 0; ty < 8; ty++)
            {
                for (var tx = 0; tx < 8; tx++)
                {
                    var (r, g, b, a) = Sample(source, sourceW, sourceH, ox + tx, oy + ty);
                    if (a <= 0) continue;
                    r = Math.Min(1, r * shade);
                    g = Math.Min(1, g * shade);
                    b = Math.Min(1, b * shade);

                    var p00 = ProjectPoint(FacePoint(face, tx / 8.0, ty / 8.0, half), scale, offX, offY);
                    var p10 = ProjectPoint(FacePoint(face, (tx + 1) / 8.0, ty / 8.0, half), scale, offX, offY);
                    var p01 = ProjectPoint(FacePoint(face, tx / 8.0, (ty + 1) / 8.0, half), scale, offX, offY);
                    var p11 = ProjectPoint(FacePoint(face, (tx + 1) / 8.0, (ty + 1) / 8.0, half), scale, offX, offY);

                    var e1x = p10.X - p00.X; var e1y = p10.Y - p00.Y; var e1z = p10.Z - p00.Z;
                    var e2x = p01.X - p00.X; var e2y = p01.Y - p00.Y; var e2z = p01.Z - p00.Z;
                    var det = e1x * e2y - e1y * e2x;
                    if (Math.Abs(det) < 1e-9) continue;
                    var inv = 1 / det;

                    var minX = Math.Min(Math.Min(p00.X, p10.X), Math.Min(p01.X, p11.X));
                    var maxX = Math.Max(Math.Max(p00.X, p10.X), Math.Max(p01.X, p11.X));
                    var minY = Math.Min(Math.Min(p00.Y, p10.Y), Math.Min(p01.Y, p11.Y));
                    var maxY = Math.Max(Math.Max(p00.Y, p10.Y), Math.Max(p01.Y, p11.Y));

                    var x0 = Math.Max(0, (int)Math.Floor(minX));
                    var x1 = Math.Min(size - 1, (int)Math.Ceiling(maxX));
                    var y0 = Math.Max(0, (int)Math.Floor(minY));
                    var y1 = Math.Min(size - 1, (int)Math.Ceiling(maxY));

                    for (var py = y0; py <= y1; py++)
                    {
                        var dy = py + 0.5 - p00.Y;
                        var row = py * size;
                        for (var px = x0; px <= x1; px++)
                        {
                            var dx = px + 0.5 - p00.X;
                            var u = (dx * e2y - dy * e2x) * inv;
                            var v = (e1x * dy - e1y * dx) * inv;
                            if (u < 0 || u > 1 || v < 0 || v > 1) continue;

                            var z = p00.Z + u * e1z + v * e2z;
                            var di = row + px;
                            if (z < depth[di]) continue;
                            depth[di] = z;

                            var o = di * 4;
                            var keep = 1 - a;
                            bgra[o] = (byte)Math.Clamp(Math.Round(b * a * 255 + bgra[o] * keep), 0, 255);
                            bgra[o + 1] = (byte)Math.Clamp(Math.Round(g * a * 255 + bgra[o + 1] * keep), 0, 255);
                            bgra[o + 2] = (byte)Math.Clamp(Math.Round(r * a * 255 + bgra[o + 2] * keep), 0, 255);
                            bgra[o + 3] = (byte)Math.Clamp(Math.Round(a * 255 + bgra[o + 3] * keep), 0, 255);
                        }
                    }
                }
            }
        }
    }

    private static void Blend(byte[] source, int sourceW, int sourceH,
        int sx, int sy, double[] canvas, int dx, int dy)
    {
        if (sx < 0 || sy < 0 || sx >= sourceW || sy >= sourceH) return;
        var si = (sy * sourceW + sx) * 4;
        var b = source[si] / 255.0;
        var g = source[si + 1] / 255.0;
        var r = source[si + 2] / 255.0;
        var a = source[si + 3] / 255.0;
        if (a <= 0) return;

        var di = (dy * 8 + dx) * 4;
        var da = canvas[di + 3];
        var outA = a + da * (1 - a);
        if (outA <= 0) return;
        canvas[di] = (r * a + canvas[di] * da * (1 - a)) / outA;
        canvas[di + 1] = (g * a + canvas[di + 1] * da * (1 - a)) / outA;
        canvas[di + 2] = (b * a + canvas[di + 2] * da * (1 - a)) / outA;
        canvas[di + 3] = outA;
    }
}
