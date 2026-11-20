using System.Linq;
using System.Net.Http;
using System.Text.Json;
using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>CurseForge 搜索由后端按资源源优先级发出，官方 API Key 留在后端。</summary>
public static class CurseForgeClient
{
    // classId 是 CurseForge 的唯一分类入口：Mod=6、整合包=4471、资源包=12、光影=6552、数据包=6945。
    // 搜索接口不会按 projectType 过滤，传错 classId 就会拿到别的类型，所以每个分类必须单独请求。
    private static readonly (string Category, int ClassId)[] Classes =
    {
        ("mod", 6),
        ("modpack", 4471),
        ("resourcepack", 12),
        ("shader", 6552),
        ("datapack", 6945),
    };

    /// <summary>
    /// sortField 取值：2 热门、3 最近更新、4 名称、6 总下载量；CurseForge 没有"相关度"字段，
    /// 相关度就沿用热门（2），搜索词本身仍走 searchFilter。
    /// </summary>
    private static string SortField(string sort) => sort switch
    {
        "downloads" => "6",
        "updated" => "3",
        "newest" => "3",
        "name" => "4",
        _ => "2",
    };

    /// <summary>modLoaderType：1 Forge、4 Fabric、5 Quilt、6 NeoForge。</summary>
    private static string? LoaderId(string? loader) => loader switch
    {
        "forge" => "1",
        "fabric" => "4",
        "quilt" => "5",
        "neoforge" => "6",
        _ => null,
    };

    /// <summary>按分类搜索；category = all 时并发抓全部分类后轮流合并，避免结果被 Mod 淹没。</summary>
    public static async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string query, int limit = 20, string category = "all",
        string? gameVersion = null, string? loader = null, string sort = "relevance", int offset = 0,
        string cfSource = "mcim")
    {
        var targets = category == "all"
            ? Classes
            : Classes.Where(c => c.Category == category).ToArray();
        if (targets.Length == 0) return Array.Empty<ModrinthProject>();

        // 显式排序时要的是「全部分类里下载量最高的 N 条」。真按全局做要每类都取满 limit，
        // 实测 5 个分类各 20 条并发会被 CurseForge 拖到 15 秒超时；而均分请求量只是把
        // 20 条候选重排，名不副实。所以「全部 + 显式排序」直接引导用户选具体分类。
        var explicitSort = sort is not ("relevance" or "");
        if (explicitSort && category == "all")
            throw new NotSupportedException("CurseForge 的排序只在选定分类下生效，请先选 Mod / 整合包 / 资源包 / 光影 / 数据包。");

        var perClass = category == "all"
            ? (explicitSort ? Math.Clamp(limit, 1, 50) : Math.Clamp(limit / targets.Length, 4, 50))
            : Math.Clamp(limit, 1, 50);

        // 单分类失败要报错；all 里某一个分类失败不该拖垮整页结果。
        if (category != "all")
            return await SearchClassAsync(query, targets[0].Category, targets[0].ClassId, perClass, gameVersion, loader, sort, offset, cfSource);

        var attempts = await Task.WhenAll(targets.Select(async t =>
        {
            try { return (Ok: true, Items: await SearchClassAsync(query, t.Category, t.ClassId, perClass, gameVersion, loader, sort, offset, cfSource)); }
            catch { return (Ok: false, Items: (IReadOnlyList<ModrinthProject>)Array.Empty<ModrinthProject>()); }
        }));
        if (attempts.All(a => !a.Ok))
            throw new HttpRequestException("CurseForge 搜索全部失败");
        var lists = attempts.Select(a => a.Items).ToArray();

        // 用户显式选了排序（下载量/更新时间/名称）时，交错合并会把排序抹平，
        // 所以这里直接按排序键全局重排；只有「相关度」保留旧的轮流取法（它的目的就是分类多样性）。
        if (sort is not ("relevance" or ""))
        {
            var flat = lists.SelectMany(l => l);
            var ordered = sort switch
            {
                "downloads" => flat.OrderByDescending(p => p.Downloads),
                "name" => flat.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => flat,
            };
            return ordered.Take(limit).ToArray();
        }

        // 轮流取（Mod、整合包、资源包、光影、数据包各一条），比按下载量排序更能体现分类多样性。
        var merged = new List<ModrinthProject>(limit);
        var cursors = new int[lists.Length];
        while (merged.Count < limit)
        {
            var added = false;
            for (var i = 0; i < lists.Length && merged.Count < limit; i++)
            {
                if (cursors[i] >= lists[i].Count) continue;
                merged.Add(lists[i][cursors[i]++]);
                added = true;
            }
            if (!added) break;
        }
        return merged;
    }

    private static async Task<IReadOnlyList<ModrinthProject>> SearchClassAsync(
        string query, string category, int classId, int limit,
        string? gameVersion = null, string? loader = null, string sort = "relevance", int offset = 0,
        string cfSource = "mcim")
    {
        var parameters = new Dictionary<string, string>
        {
            ["gameId"] = "432",
            ["classId"] = classId.ToString(),
            ["index"] = Math.Max(0, offset).ToString(),
            ["pageSize"] = Math.Clamp(limit, 1, 50).ToString(),
            ["sortField"] = SortField(sort),
            ["cfSource"] = cfSource,
            ["sortOrder"] = "desc",
        };
        if (!string.IsNullOrWhiteSpace(query)) parameters["searchFilter"] = query.Trim();
        if (!string.IsNullOrWhiteSpace(gameVersion)) parameters["gameVersion"] = gameVersion.Trim();
        if (LoaderId(loader) is { } loaderId) parameters["modLoaderType"] = loaderId;
        using var json = await App.Backend.SearchCurseForgeAsync(parameters);

        var result = new List<ModrinthProject>();
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var hit in data.EnumerateArray())
        {
            var id = Long(hit, "id").ToString();
            var slug = Str(hit, "slug");
            var logo = hit.TryGetProperty("logo", out var logoElement) ? Str(logoElement, "thumbnailUrl") : "";
            if (logo.Length == 0 && hit.TryGetProperty("logo", out var logo2)) logo = Str(logo2, "url");
            var website = hit.TryGetProperty("links", out var links) ? Str(links, "websiteUrl") : "";
            if (website.Length == 0 && slug.Length > 0)
                website = $"https://www.curseforge.com/minecraft/{PagePath(category)}/{slug}";
            var author = "";
            if (hit.TryGetProperty("authors", out var authors) &&
                authors.ValueKind == JsonValueKind.Array && authors.GetArrayLength() > 0)
                author = Str(authors[0], "name");

            var project = new ModrinthProject
            {
                Id = id,
                Slug = slug,
                Title = Str(hit, "name"),
                Description = Str(hit, "summary"),
                Author = author,
                ProjectType = category,
                IconUrl = logo,
                PageUrl = website,
                Downloads = Long(hit, "downloadCount"),
            };
            ReadCompatibility(hit, project);
            result.Add(project);
        }

        return result;
    }

    /// <summary>从 latestFilesIndexes / gameVersionLatestFiles 里取游戏版本与加载器，列表页才有「MC 1.20.1 · Forge」可显示。</summary>
    private static void ReadCompatibility(JsonElement hit, ModrinthProject project)
    {
        if (hit.TryGetProperty("latestFilesIndexes", out var indexes) && indexes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in indexes.EnumerateArray())
            {
                var version = Str(item, "gameVersion");
                if (version.Length > 0 && !project.GameVersions.Contains(version))
                    project.GameVersions.Add(version);
                var loader = LoaderName((int)Long(item, "modLoader"));
                if (loader.Length > 0 && !project.Loaders.Contains(loader))
                    project.Loaders.Add(loader);
            }
        }

        if (project.GameVersions.Count == 0 &&
            hit.TryGetProperty("gameVersionLatestFiles", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in legacy.EnumerateArray())
            {
                var version = Str(item, "gameVersion");
                if (version.Length > 0 && !project.GameVersions.Contains(version))
                    project.GameVersions.Add(version);
            }
        }

        project.GameVersions.Sort((a, b) => string.CompareOrdinal(b, a));
    }

    private static string PagePath(string category) => category switch
    {
        "modpack" => "modpacks",
        "resourcepack" => "texture-packs",
        "shader" => "shaders",
        "datapack" => "data-packs",
        _ => "mc-mods",
    };

    private static string LoaderName(int modLoader) => modLoader switch
    {
        1 => "forge",
        4 => "fabric",
        5 => "quilt",
        6 => "neoforge",
        _ => "",
    };

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;
}
