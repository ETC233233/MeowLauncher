using System.Reflection;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace MeowLauncher.WinUI.Services;

/// <summary>云端配置里的一个组件包：哪个接口级别、哪个版本、适用哪些启动器版本、去哪下载、哈希是多少。</summary>
public sealed record ComponentOffer(
    int ApiLevel, string Version, string MinLauncher, string MaxLauncher, bool Required,
    long Size, string Sha256, string[] Urls, string Notes);

/// <summary>
/// 云端组件清单的验签与挑包逻辑。不依赖界面和 App，方便单独测试。
/// </summary>
public static class ComponentManifest
{
    /// <summary>这个启动器能对接的模块接口级别。模块接口不兼容地改动时，启动器和模块的级别一起 +1。</summary>
    public const int ApiLevel = 1;

    private static string Meta(string key) =>
        typeof(ComponentManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    internal static bool SafeName(string s) =>
        s.Length is > 0 and <= 40 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !s.StartsWith('.');

    /// <summary>信封 {payload, sig, kid}：先用公钥验 Ed25519（对 payload 字符串的 UTF-8 字节），再检查有效期。</summary>
    internal static string? VerifyEnvelope(string envelope, Func<string, byte[]?>? keyLookup = null, long? nowUnix = null)
    {
        try
        {
            keyLookup ??= FindKey;
            using var doc = JsonDocument.Parse(envelope);
            var root = doc.RootElement;
            var payload = root.GetProperty("payload").GetString() ?? "";
            var sig = Convert.FromBase64String(root.GetProperty("sig").GetString() ?? "");
            var pk = keyLookup(root.GetProperty("kid").GetString() ?? "");
            if (pk is null || pk.Length != 32 || sig.Length != 64) return null;
            var msg = Encoding.UTF8.GetBytes(payload);
            if (!Ed25519.Verify(sig, 0, pk, 0, msg, 0, msg.Length)) return null;

            using var p = JsonDocument.Parse(payload);
            var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var issued = p.RootElement.GetProperty("issuedAt").GetInt64();
            var expires = p.RootElement.GetProperty("expiresAt").GetInt64();
            if (issued > now + 300 || expires < now - 300) return null;
            return payload;
        }
        catch { return null; }
    }

    private static byte[]? FindKey(string kid)
    {
        foreach (var part in Meta("MeowConfigKeys").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf(':');
            if (i <= 0 || part[..i] != kid) continue;
            try { var k = Convert.FromBase64String(part[(i + 1)..]); return k.Length == 32 ? k : null; }
            catch { return null; }
        }
        return null;
    }

    /// <summary>从已验签的 payload 里挑：接口级别相同、启动器版本在区间内、版本最高的一个。</summary>
    /// <param name="launcher">启动器的版本号数字部分；null（Review 开发版没有版本号）表示不做启动器版本区间判断，只看接口级别。</param>
    internal static (ComponentOffer? Offer, string Error) PickOffer(string payload, Version? launcher)
    {
        var offers = new List<ComponentOffer>();
        var sawOtherLevel = false;
        var sawOutOfRange = false;
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("components", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var o = ParseOffer(e);
                if (o is null) continue;
                if (o.ApiLevel != ApiLevel) { sawOtherLevel = true; continue; }
                if (launcher is not null && !InRange(launcher, o.MinLauncher, o.MaxLauncher)) { sawOutOfRange = true; continue; }
                offers.Add(o);
            }
        }
        var best = offers.OrderByDescending(o => Version.TryParse(o.Version, out var v) ? v : new Version(0, 0)).FirstOrDefault();
        if (best is not null) return (best, "");
        if (sawOutOfRange) return (null, "当前启动器版本暂时没有可用的联机组件，请更新启动器后再试。");
        if (sawOtherLevel) return (null, "云端的联机组件已经升级，需要更新启动器后才能使用。");
        return (null, "云端还没有发布联机组件。");
    }

    private static ComponentOffer? ParseOffer(JsonElement e)
    {
        try
        {
            var ver = e.GetProperty("version").GetString() ?? "";
            var sha = (e.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            var size = e.GetProperty("size").GetInt64();
            if (!SafeName(ver) || !Version.TryParse(ver, out _) || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
            if (size is <= 0 or > 400L * 1024 * 1024) return null;
            var urls = e.GetProperty("urls").EnumerateArray().Select(u => u.GetString() ?? "")
                .Where(IsAllowedUrl).ToArray();
            if (urls.Length == 0) return null;
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new ComponentOffer(
                e.GetProperty("apiLevel").GetInt32(), ver, S("minLauncher"), S("maxLauncher"),
                e.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True, size, sha, urls, S("notes"));
        }
        catch { return null; }
    }

    private static bool IsAllowedUrl(string u)
    {
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uu)) return false;
        if (uu.Scheme == Uri.UriSchemeHttps) return true;
#if DEBUG
        return uu.Scheme == Uri.UriSchemeHttp && uu.IsLoopback;       // 仅调试版：本机 http 服务器
#else
        return false;
#endif
    }

    private static bool InRange(Version v, string min, string max)
    {
        if (min.Length > 0 && Version.TryParse(min, out var lo) && v < lo) return false;
        if (max.Length > 0 && Version.TryParse(max, out var hi) && v > hi) return false;
        return true;
    }
}
