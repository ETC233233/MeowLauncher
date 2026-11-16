using System.Net.Http;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public sealed record NewsItem(
    string Title,
    string Summary,
    string Tag,
    DateTimeOffset Date,
    string Link,
    string ImageUrl);

/// <summary>
/// 每日一帖的真实数据源：Mojang 官方启动器内容接口（公开只读，和官方启动器同一份 JSON）。
/// 只取带标题、摘要、配图、原文链接的条目，按日期倒序；缓存 6 小时，断网时继续显示上一次的内容，
/// 接口挂了就什么都不变——不编造任何文案。
/// </summary>
public sealed class NewsService
{
    private const string NewsUrl = "https://launchercontent.mojang.com/v2/news.json";
    private const string ImageRoot = "https://launchercontent.mojang.com";
    private static readonly HttpClient Http = CreateHttp();

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher");

    private static readonly string CacheFile = Path.Combine(CacheDir, "news-cache.json");

    private readonly List<NewsItem> _items = new();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private bool _loading;

    public IReadOnlyList<NewsItem> Items => _items;

    public event EventHandler? Updated;

    /// <summary>按天轮换取一条：同一天看到的永远是同一条，跨天才换。</summary>
    public NewsItem? Today
        => _items.Count == 0 ? null : _items[(DateTimeOffset.Now.DayOfYear - 1) % _items.Count];

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            if (!doc.RootElement.TryGetProperty("fetchedAt", out var fetched)
                || !DateTimeOffset.TryParse(fetched.GetString(), out var at)) return;
            if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var items = new List<NewsItem>();
            foreach (var e in arr.EnumerateArray()) items.Add(ReadItem(e));
            if (items.Count == 0) return;
            _items.Clear();
            _items.AddRange(items.Where(x => x.Title.Length > 0));
            _fetchedAt = at;
        }
        catch
        {
            // 缓存坏了就当没有，下一次联网重取。
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_loading) return;
        if (!force && _items.Count > 0 && DateTimeOffset.Now - _fetchedAt < TimeSpan.FromHours(6)) return;
        _loading = true;
        try
        {
            using var response = await Http.GetAsync(NewsUrl);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) return;

            var items = new List<NewsItem>();
            var titles = new HashSet<string>();
            foreach (var e in entries.EnumerateArray())
            {
                var item = ReadItem(e);
                if (item.Title.Length == 0) continue;
                // 没有原文链接的条目点了没反应，直接不要。
                if (!item.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (!titles.Add(item.Title)) continue;
                items.Add(item);
            }
            if (items.Count == 0) return;

            items.Sort((a, b) => b.Date.CompareTo(a.Date));
            _items.Clear();
            _items.AddRange(items.Take(40));
            _fetchedAt = DateTimeOffset.Now;
            SaveCache();
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 离线或接口变更：保留旧内容，不弹错、不留假数据。
        }
        finally
        {
            _loading = false;
        }
    }

    private static NewsItem ReadItem(JsonElement e)
    {
        var title = ReadString(e, "title");
        var summary = ReadString(e, "text");
        var tag = ReadString(e, "tag");
        var link = ReadString(e, "readMoreLink");
        var date = DateTimeOffset.MinValue;
        var rawDate = ReadString(e, "date");
        if (rawDate.Length > 0
            && DateTimeOffset.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            date = parsed;
        return new NewsItem(title, summary, tag.Length == 0 ? "News" : tag, date, link, ReadImageUrl(e));
    }

    private static string ReadImageUrl(JsonElement e)
    {
        foreach (var key in new[] { "newsPageImage", "playPageImage", "image" })
            if (e.TryGetProperty(key, out var image) && image.ValueKind == JsonValueKind.Object)
            {
                var url = ReadString(image, "url");
                if (url.Length == 0) continue;
                return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : ImageRoot + url;
            }
        return "";
    }

    private static string ReadString(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var payload = new
            {
                fetchedAt = _fetchedAt.ToString("O"),
                items = _items.Select(x => new
                {
                    title = x.Title,
                    text = x.Summary,
                    tag = x.Tag,
                    date = x.Date.ToString("O"),
                    readMoreLink = x.Link,
                    image = new { url = x.ImageUrl },
                }),
            };
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(payload));
        }
        catch
        {
            // 缓存写不进去不影响界面。
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // Mojang 内容接口对 UA 没有要求，但带上自己的名字，别伪装成浏览器。
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "MeowLauncher/1.0 (personal use)");
        return client;
    }
}
