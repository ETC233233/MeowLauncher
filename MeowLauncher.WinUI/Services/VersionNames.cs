using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 大版本的官方中文代号（1.21 → 棘巧试炼 …），数据来自 MC 百科，由后端抓取后缓存在
/// %LOCALAPPDATA%\MeowLauncher.WinUI\version-names.json。读取只碰这个小文件，抓取在后台进程里做，
/// 缓存超过 7 天才刷新；读不到 / 抓不到就返回 null，调用方退回自带的硬编码表。
/// </summary>
public static class VersionNames
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly Regex MajorOnly = new(@"^\d+\.\d+$", RegexOptions.Compiled);
    private static Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private static DateTimeOffset _fetched = DateTimeOffset.MinValue;
    private static bool _loaded;
    private static int _refreshStarted;

    /// <summary>后台刷新写入了新内容（在后台线程触发，订阅方自己切回 UI 线程）。</summary>
    public static event Action? Changed;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeowLauncher.WinUI", "version-names.json");

    /// <summary>某个大版本（如 "1.21"）的中文代号；没有就返回 null。</summary>
    public static string? Get(string major)
    {
        if (!_loaded)
        {
            _loaded = true;
            Reload();
        }
        return _names.TryGetValue(major, out var name) && name.Length > 0 ? name : null;
    }

    private static bool Reload()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in versions.EnumerateObject())
                {
                    if (!MajorOnly.IsMatch(item.Name)) continue;
                    if (item.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!item.Value.TryGetProperty("codename", out var code)) continue;
                    var text = code.GetString() ?? "";
                    if (text.Length > 0) names[item.Name] = text;
                }
            }
            var fetched = root.TryGetProperty("fetched", out var f) && f.TryGetInt64(out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : DateTimeOffset.MinValue;
            var changed = names.Count != _names.Count || names.Any(kv => !_names.TryGetValue(kv.Key, out var old) || old != kv.Value);
            _names = names;
            _fetched = fetched;
            return changed;
        }
        catch
        {
            return false;   // 文件损坏 / 被占用：当作没有，下次刷新会覆盖
        }
    }

    /// <summary>缓存没有或超过 7 天才在后台抓一次；整个应用进程里最多触发一次。</summary>
    public static void RefreshInBackground()
    {
        if (!_loaded)
        {
            _loaded = true;
            Reload();
        }
        if (_names.Count > 0 && DateTimeOffset.UtcNow - _fetched < MaxAge) return;
        if (Interlocked.Exchange(ref _refreshStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await App.Backend.RefreshVersionNamesAsync();
                if (result.Ok && Reload()) Changed?.Invoke();
            }
            catch
            {
                // 抓不到就算了：左栏继续用硬编码表
            }
        });
    }
}
