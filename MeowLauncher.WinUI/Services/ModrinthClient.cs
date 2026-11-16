using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

public static class ModrinthClient
{
    private static HttpClient Http = CreateClient();

    /// <summary>代理设置变化后重建连接（ConnectCallback 里显式走 IPv4，代理也要重新挂）。</summary>
    internal static void ResetProxy() => Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectIPv4Async,
        };
        if (NetworkProxy.Proxy is { } proxy) handler.Proxy = proxy;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return client;
    }

    private static async ValueTask<Stream> ConnectIPv4Async(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.First();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string query, int limit = 20, string category = "all",
        string? gameVersion = null, string? loader = null,
        string sort = "relevance", int offset = 0)
    {
        var q = Uri.EscapeDataString((query ?? "").Trim());
        var index = sort is "downloads" or "updated" or "newest" or "follows" ? sort : "relevance";
        var path = $"/search?query={q}&limit={limit}&offset={offset}&index={index}";
        // Modrinth 的 project_type 既能搜也能过滤；服务端 facets 比拿回混合结果再本地筛准得多。
        // 同一维度内是 OR、不同维度之间是 AND，所以每个 facet 组各自包一层数组。
        // Modrinth 的 facets 是「外层数组 = AND，内层数组 = OR」：
        //   [["project_type:mod"],["versions:1.12.2"],["categories:forge"]]
        // 之前每组多包了一层方括号，单条件时碰巧合法，一叠加就 400 Bad Request。
        var groups = new List<string>();
        if (category != "all") groups.Add("[\"project_type:" + category + "\"]");
        if (!string.IsNullOrWhiteSpace(gameVersion)) groups.Add("[\"versions:" + gameVersion.Trim() + "\"]");
        // 加载器 category 只对 Mod / 整合包存在。资源包、光影、数据包没有 forge/fabric 这类分类，
        // 硬拼进去会一条结果都没有，所以这里按分类判断，而不只是把界面上的下拉藏起来。
        if (!string.IsNullOrWhiteSpace(loader))
        {
            // Modrinth 的加载器分类全小写：forge / fabric / neoforge / quilt。
            // 「全部」下选了加载器也要生效：加载器分类只属于 Mod / 整合包，所以先把
            // project_type 收到这两类再叠分类，否则这条筛选会被静默丢掉（看起来就是「点了没用」）。
            var key = loader.Trim().ToLowerInvariant();
            if (category == "all") groups.Add("[\"project_type:mod\",\"project_type:modpack\"]");
            groups.Add("[\"categories:" + key + "\"]");
        }
        if (groups.Count > 0) path += "&facets=" + Uri.EscapeDataString("[" + string.Join(",", groups) + "]");
        using var json = await McimClient.SearchAsync(Http, McimClient.Modrinth + path,
            "https://api.modrinth.com/v2" + path, "hits");

        var result = new List<ModrinthProject>();
        if (!json.RootElement.TryGetProperty("hits", out var hits) ||
            hits.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var hit in hits.EnumerateArray())
        {
            var slug = Str(hit, "slug");
            var type = Str(hit, "project_type");
            if (type.Length == 0) type = "mod";
            result.Add(new ModrinthProject
            {
                Id = Str(hit, "project_id"),
                Slug = slug,
                Title = Str(hit, "title"),
                Description = Str(hit, "description"),
                Author = Str(hit, "author"),
                ProjectType = type,
                IconUrl = Str(hit, "icon_url"),
                PageUrl = slug.Length == 0 ? "" : $"https://modrinth.com/{type}/{slug}",
                Downloads = Long(hit, "downloads"),
                Follows = Long(hit, "follows"),
                GameVersions = Strings(hit, "versions"),
                Loaders = Strings(hit, "categories").Where(IsLoaderKey).ToList(),
            });
        }

        return result;
    }

    private static readonly HashSet<string> LoaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "liteloader", "rift",
    };

    private static bool IsLoaderKey(string value) => LoaderKeys.Contains(value);

    private static List<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text!);
        }
        return list;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;
}
