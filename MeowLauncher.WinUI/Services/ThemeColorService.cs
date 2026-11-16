using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 主题色：系统强调色 / 跟随壁纸取色（莫奈式，从画面里挑最有存在感的一簇颜色）/ 自定义。
/// 只改三支强调色画刷和一支染色渐变的颜色值——引用它们的地方会即时重画，
/// 不重建资源字典，所以换主题色不会让整窗闪一下。
/// </summary>
public static class ThemeColorService
{
    public const string ModeAccent = "accent";
    public const string ModeWallpaper = "wallpaper";
    public const string ModeCustom = "custom";

    private static readonly Color Fallback = Color.FromArgb(255, 0x2F, 0xA3, 0x5A);

    public static Color Current { get; private set; } = Fallback;

    public static string CurrentHex => $"#{Current.R:X2}{Current.G:X2}{Current.B:X2}";

    /// <summary>主题色落到画刷之后触发（取色器色块、状态文案跟着刷）。</summary>
    public static event Action<Color>? Changed;

    public static async Task<Color> ApplyAsync(AppPreferences? preferences, bool dark)
    {
        Color color;
        try
        {
            color = preferences is null ? Fallback : await ResolveAsync(preferences);
        }
        catch
        {
            color = Fallback;
        }
        Apply(color, dark);
        return color;
    }

    public static async Task<Color> ResolveAsync(AppPreferences preferences)
    {
        var mode = (preferences.ThemeColorMode ?? "").Trim().ToLowerInvariant();
        if (mode == ModeAccent) return SystemAccent();
        if (mode == ModeCustom) return ParseHex(preferences.ThemeColorHex) ?? Fallback;

        var source = ResolveSource(preferences);
        if (source is null) return SystemAccent();
        var picked = await PickAsync(source);
        return picked ?? SystemAccent();
    }

    /// <summary>把颜色写成画刷颜色；同一批画刷实例被 XAML 引用，改色即重画。</summary>
    public static void Apply(Color color, bool dark)
    {
        color = Tame(color, dark);
        Current = color;
        var ink = Luminance(color) > 0.62
            ? Color.FromArgb(255, 0x12, 0x14, 0x1A)
            : Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
        MutateBrush("AppAccentBrush", color);
        MutateBrush("AppAccentSoftBrush", WithAlpha(color, dark ? (byte)0x25 : (byte)0x1F));
        MutateBrush("AppAccentTextBrush", ink);
        // 启动按钮固定品牌绿：早期跟随壁纸取色，壁纸偏红时「启动」会变成像删除的危险色。
        var brand = Color.FromArgb(255, 0x2F, 0xA3, 0x5A);
        MutateStops("AppLaunchBrush", brand, Shade(brand, 0.84));
        MutateStops("AppTintBrush",
            WithAlpha(color, dark ? (byte)0x22 : (byte)0x16),
            WithAlpha(color, 0));
        Changed?.Invoke(color);
    }

    public static Color? ParseHex(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('#');
        if (text.Length == 8) text = text[2..];
        if (text.Length != 6) return null;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) return null;
        return Color.FromArgb(255,
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    /// <summary>壁纸取色的输入：优先用户自选的背景图，否则系统壁纸。</summary>
    private static string? ResolveSource(AppPreferences preferences)
    {
        var custom = preferences.BackgroundImage;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        var wallpaper = SystemWallpaperPath();
        return !string.IsNullOrWhiteSpace(wallpaper) && File.Exists(wallpaper) ? wallpaper : null;
    }

    private static Color SystemAccent()
    {
        try { return new UISettings().GetColorValue(UIColorType.Accent); }
        catch { return Fallback; }
    }

    private static async Task<Color?> PickAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            const uint side = 72;
            var transform = new BitmapTransform
            {
                ScaledWidth = side,
                ScaledHeight = side,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return PickFromPixels(pixels.DetachPixelData());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>24 个色相桶按「饱和度² × 明度居中」加权，挑出画面里最像主色的一簇。</summary>
    private static Color? PickFromPixels(byte[] bgra)
    {
        var weight = new double[24];
        var sumR = new double[24];
        var sumG = new double[24];
        var sumB = new double[24];

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 160) continue;
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            ToHsl(r, g, b, out var h, out var s, out var l);
            // 近黑近白没有色相，算进去只会把结果拖成灰。
            if (l < 0.10 || l > 0.95) continue;
            var w = s * s * (1.0 - Math.Abs(l - 0.55) * 1.5);
            if (w <= 0) continue;
            var bucket = Math.Clamp((int)(h * 24.0), 0, 23);
            weight[bucket] += w;
            sumR[bucket] += r * w;
            sumG[bucket] += g * w;
            sumB[bucket] += b * w;
        }

        var best = 0;
        for (var i = 1; i < 24; i++)
            if (weight[i] > weight[best]) best = i;
        if (weight[best] < 0.8) return null;
        return Color.FromArgb(255,
            (byte)Math.Round(sumR[best] / weight[best]),
            (byte)Math.Round(sumG[best] / weight[best]),
            (byte)Math.Round(sumB[best] / weight[best]));
    }

    /// <summary>收进可读区间：深色主题偏亮、浅色主题偏深，饱和不过火，免得变成荧光色。</summary>
    private static Color Tame(Color color, bool dark)
    {
        ToHsl(color.R, color.G, color.B, out var h, out var s, out var l);
        var target = dark ? 0.58 : 0.44;
        l = Math.Clamp((l + target) / 2.0, dark ? 0.40 : 0.32, dark ? 0.72 : 0.56);
        s = Math.Clamp(s, 0.34, 0.76);
        return FromHsl(h, s, l);
    }

    private static void MutateBrush(string key, Color color)
    {
        if (Find(key) is SolidColorBrush brush) brush.Color = color;
        else Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void MutateStops(string key, params Color[] colors)
    {
        if (Find(key) is not LinearGradientBrush gradient) return;
        for (var i = 0; i < colors.Length && i < gradient.GradientStops.Count; i++)
            gradient.GradientStops[i].Color = colors[i];
    }

    private static object? Find(string key)
    {
        var resources = Application.Current.Resources;
        if (TryFind(resources, key, out var value)) return value;
        foreach (var dictionary in resources.MergedDictionaries)
            if (TryFind(dictionary, key, out value)) return value;
        return null;
    }

    private static bool TryFind(ResourceDictionary dictionary, string key, out object? value)
    {
        if (dictionary.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        foreach (var merged in dictionary.MergedDictionaries)
            if (TryFind(merged, key, out value)) return true;
        value = null;
        return false;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Shade(Color color, double factor) => Color.FromArgb(255,
        (byte)Math.Round(color.R * factor),
        (byte)Math.Round(color.G * factor),
        (byte)Math.Round(color.B * factor));

    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static void ToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        l = (max + min) / 2.0;
        var delta = max - min;
        if (delta <= 0.0001)
        {
            h = 0;
            s = 0;
            return;
        }
        s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
        h = max == rn
            ? ((gn - bn) / delta + (gn < bn ? 6.0 : 0.0)) / 6.0
            : max == gn
                ? ((bn - rn) / delta + 2.0) / 6.0
                : ((rn - gn) / delta + 4.0) / 6.0;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0.0001)
        {
            var grey = (byte)Math.Round(l * 255);
            return Color.FromArgb(255, grey, grey, grey);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb(255,
            (byte)Math.Round(Hue(p, q, h + 1.0 / 3.0) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - 1.0 / 3.0) * 255));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint flags);

    /// <summary>当前系统壁纸路径；取不到返回 null（此时退回系统强调色）。</summary>
    public static string? SystemWallpaperPath()
    {
        // 读取壁纸必须用 SPI_GETDESKWALLPAPER(0x0073)。之前写成 0x0014 = SPI_SETDESKWALLPAPER，
        // 传空缓冲区等于「把桌面壁纸设成空」，会把用户当前会话的壁纸清掉（桌面变黑）。
        const uint SPI_GETDESKWALLPAPER = 0x0073;
        var buffer = new System.Text.StringBuilder(1024);
        return SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0) ? buffer.ToString() : null;
    }
}
