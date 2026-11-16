using System.IO;
using MeowLauncher.WinUI.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 实例图标：实例目录里有自己的 icon.png 就用它；否则按加载器给一枚徽标（Fabric / Quilt / Forge / NeoForge，
/// 原版和认不出的用默认图标）。徽标是我们自己画的简化图形（tools/make_loader_icons.py）。
/// 主页、实例管理、切换列表共用这一处，保证同一个实例在各处长得一样。
/// </summary>
public static class InstanceIcon
{

    public static string? FilePathFor(VersionInfo? version)
    {
        try
        {
            if (version is null || string.IsNullOrWhiteSpace(version.Path)) return null;
            var candidate = Path.Combine(version.Path, "icon.png");
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource For(VersionInfo? version)
    {
        var file = FilePathFor(version);
        return file is not null
            ? new BitmapImage(new Uri(file)) { DecodePixelWidth = 64 }
            : ForLoader(version?.Loader);
    }

    /// <summary>加载器对应的徽标资源；neoforge 要先于 forge 判断（名字里含 forge）。</summary>
    public static string LoaderAsset(string? loader)
    {
        var l = (loader ?? "").Trim().ToLowerInvariant();
        if (l.Contains("neoforge")) return "ms-appx:///Assets/Icons/loaders/neoforge.png";
        if (l.Contains("forge")) return "ms-appx:///Assets/Icons/loaders/forge.png";
        if (l.Contains("fabric")) return "ms-appx:///Assets/Icons/loaders/fabric.png";
        if (l.Contains("quilt")) return "ms-appx:///Assets/Icons/loaders/quilt.png";
        return "ms-appx:///Assets/Icons/loaders/vanilla.png";   // 原版：自绘草方块
    }

    public static ImageSource ForLoader(string? loader) =>
        new BitmapImage(new Uri(LoaderAsset(loader))) { DecodePixelWidth = 64 };
}
