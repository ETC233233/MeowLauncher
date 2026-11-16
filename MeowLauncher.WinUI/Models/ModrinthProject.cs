using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowLauncher.WinUI.Models;

public sealed class ModrinthProject
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string ProjectType { get; init; } = "mod";
    public string IconUrl { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public bool IsInstallable => ProjectType is "mod" or "modpack" or "resourcepack" or "shader";

    /// <summary>数据包要装进某个存档的 datapacks 目录，不是实例级资源；还没有「选存档」的步骤，先不做一键安装。</summary>
    public bool IsDatapack => ProjectType is "datapack";

    /// <summary>整合包装成新实例，和「往当前实例里加资源」是两回事：不看当前实例、不打已安装标记。</summary>
    public bool IsModpack => ProjectType is "modpack";

    /// <summary>后台安装类型：mod / modpack / resourcepack / shaderpack / datapack。</summary>
    public string InstallKind => ProjectType switch
    {
        "shader" => "shaderpack",
        "modpack" => "modpack",
        "resourcepack" => "resourcepack",
        "datapack" => "datapack",
        _ => "mod",
    };

    public override string ToString() => Title;
    public long Downloads { get; init; }
    public long Follows { get; init; }

    /// <summary>搜索结果里带的游戏版本（新→旧），供兼容性提示用。</summary>
    public List<string> GameVersions { get; init; } = new();

    /// <summary>搜索结果里带的加载器分类（fabric/forge/neoforge/quilt…）。</summary>
    public List<string> Loaders { get; init; } = new();

    /// <summary>用于提示文案的加载器列表，例如「Fabric / NeoForge」。</summary>
    public string LoaderText => Loaders.Count == 0
        ? "未标注加载器"
        : string.Join(" / ", Loaders.Select(PrettyLoader));

    public string CompatText
    {
        get
        {
            var versions = GameVersions.Count == 0
                ? "版本未知"
                : string.Join(" · ", GameVersions.Take(2).Select(v => $"MC {v}"));
            var loaders = Loaders.Count == 0 ? "" : " · " + string.Join(" / ", Loaders.Select(PrettyLoader));
            return versions + loaders;
        }
    }

    public bool IsCompatibleWith(string gameVersion) =>
        GameVersions.Count == 0
        || string.IsNullOrWhiteSpace(gameVersion)
        || GameVersions.Any(v => string.Equals(v, gameVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>资源包/光影/数据包与加载器无关；整合包会自己新建带加载器的实例，也不看当前实例；只有 Mod 要求实例加载器在项目支持列表里。</summary>
    public bool IsLoaderCompatibleWith(string loader)
    {
        if (ProjectType is "resourcepack" or "shader" or "datapack" or "modpack") return true;
        if (Loaders.Count == 0) return true;

        var wanted = (loader ?? "").Trim().ToLowerInvariant();
        if (wanted.Length == 0 || wanted is "vanilla" or "modded" or "原版") return false;
        return Loaders.Any(item => string.Equals(item, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrettyLoader(string key) => key.ToLowerInvariant() switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "liteloader" => "LiteLoader",
        "rift" => "Rift",
        _ => key,
    };

    public string DownloadsText => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000d:0.#}M 下载",
        >= 1_000 => $"{Downloads / 1_000d:0.#}K 下载",
        _ => $"{Downloads} 下载",
    };

    public string TypeText => ProjectType switch
    {
        "mod" => "Mod",
        "modpack" => "整合包",
        "resourcepack" => "资源包",
        "shader" => "光影",
        "datapack" => "数据包",
        _ => "项目",
    };

    public ImageSource? IconSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconUrl)) return null;
            try { return new BitmapImage(new Uri(IconUrl)); }
            catch { return null; }
        }
    }
}
