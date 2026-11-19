namespace MeowLauncher.WinUI.Services;

/// <summary>Three separable box passes approximate a Gaussian on decoded BGRA pixels.</summary>
public static class WallpaperBlur
{
    public static byte[] Apply(byte[] pixels, int width, int height, int amount)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid BGRA image size.");
        if (amount <= 0) return pixels;
        var radius = Math.Clamp((int)Math.Round(amount / Math.Sqrt(3)), 1, 24);
        var source = (byte[])pixels.Clone();
        var scratch = new byte[source.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Pass(source, scratch, width, height, radius, true);
            Pass(scratch, source, width, height, radius, false);
        }
        return source;
    }

    private static void Pass(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var stride = horizontal ? 4 : width * 4;
        var count = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            var origin = horizontal ? line * width * 4 : line * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += source[origin + Math.Clamp(i, 0, length - 1) * stride + channel];
                for (var i = 0; i < length; i++)
                {
                    target[origin + i * stride + channel] = (byte)((sum + count / 2) / count);
                    sum -= source[origin + Math.Clamp(i - radius, 0, length - 1) * stride + channel];
                    sum += source[origin + Math.Clamp(i + radius + 1, 0, length - 1) * stride + channel];
                }
            }
        }
    }
}
