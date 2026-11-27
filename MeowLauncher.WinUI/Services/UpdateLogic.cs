using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>一个已发行的启动器版本（来自签名配置或 GitHub Releases）。</summary>
public sealed record LauncherRelease(string Version, string Url, string Notes, string Date)
{
    public MeowVersion? Parsed => MeowVersion.Parse(Version);
    // Only ParseLauncher (the verified configuration path) attaches an installable package.
    public LauncherPackage? Package { get; init; }
}

public sealed record LauncherPackage(string Format, string Platform, long Size, string Sha256, string[] Urls);

public enum UpdateState
{
    /// <summary>开发版（Review）：不做更新检查。</summary>
    Review,
    /// <summary>已是最新。</summary>
    UpToDate,
    /// <summary>有新版本。</summary>
    Available,
    /// <summary>本地版本高于云端最新发行版（或云端还没有任何发行版）：无法检测此版本的更新。</summary>
    Ahead,
    /// <summary>没查到（网络问题等），Message 里是原因。</summary>
    Unknown,
}

public sealed record UpdateResult(UpdateState State, LauncherRelease? Latest, string Message);

/// <summary>
/// 检查更新的判断逻辑。不依赖界面和网络，方便单独测试（tools/online-component/test）。
/// 规则见内部文档「版本号方案」：同版本号 Beta &lt; Release；Beta 之间按迭代次数比；Review 不检查。
/// </summary>
public static class UpdateLogic
{
    /// <summary>本地版本高于云端最新发行版（含云端还没有任何发行版）时的提示——用户指定的文案。</summary>
    public const string AheadMessage = "当前使用的是非现发行的测试版本，无法检测此版本的更新，请保管好您的账号数据。";

    /// <summary>
    /// 比较本地版本和云端发行版。stable 通道只看正式版；beta 通道看 Beta + 正式版，取最新的那个。
    /// </summary>
    public static UpdateResult Evaluate(MeowVersion current, LauncherRelease? stable, LauncherRelease? beta, string channelPref)
    {
        if (current.IsReview) return new UpdateResult(UpdateState.Review, null, current.NoticeText);

        var candidates = new List<(LauncherRelease Rel, MeowVersion Ver)>();
        if (stable?.Parsed is { IsReview: false } sv) candidates.Add((stable, sv));
        if (channelPref == "beta" && beta?.Parsed is { IsReview: false } bv) candidates.Add((beta, bv));

        if (candidates.Count == 0) return new UpdateResult(UpdateState.Ahead, null, AheadMessage);

        var best = candidates[0];
        foreach (var c in candidates.Skip(1)) if (c.Ver.CompareTo(best.Ver) > 0) best = c;

        var cmp = current.CompareTo(best.Ver);
        if (cmp < 0) return new UpdateResult(UpdateState.Available, best.Rel, $"有新版本 {best.Ver.Display}");
        if (cmp == 0) return new UpdateResult(UpdateState.UpToDate, best.Rel, "已是最新版本");
        return new UpdateResult(UpdateState.Ahead, best.Rel, AheadMessage);
    }

    /// <summary>
    /// 从已验签的配置 payload 里取 <c>launcher: { stable: {version,url,notes,date}, beta: {...} }</c>。
    /// 版本号不合法、链接不是 https 的条目一律丢弃。
    /// </summary>
    public static (LauncherRelease? Stable, LauncherRelease? Beta) ParseLauncher(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("launcher", out var l) || l.ValueKind != JsonValueKind.Object) return (null, null);
            return (ParseOne(l, "stable"), ParseOne(l, "beta"));
        }
        catch { return (null, null); }
    }

    private static LauncherRelease? ParseOne(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object) return null;
        string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var version = S("version");
        var parsed = MeowVersion.Parse(version);
        if (parsed is null || parsed.IsReview) return null;
        var url = S("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) url = "";
        return new LauncherRelease(version, url, S("notes"), S("date")) { Package = ParsePackage(e) };
    }

    private static LauncherPackage? ParsePackage(JsonElement release)
    {
        try
        {
            if (!release.TryGetProperty("package", out var p)) return null;
            var format = p.GetProperty("format").GetString();
            var platform = p.GetProperty("platform").GetString();
            var size = p.GetProperty("size").GetInt64();
            var hash = p.GetProperty("sha256").GetString() ?? "";
            var urls = p.GetProperty("urls").EnumerateArray().Select(x => x.GetString() ?? "")
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (format != "portable-v1" || platform != "win-x64" || size is <= 0 or > 2L * 1024 * 1024 * 1024
                || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || urls.Length == 0) return null;
            return new LauncherPackage(format, platform, size, hash.ToLowerInvariant(), urls);
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 GitHub Releases 列表（<c>/repos/{owner}/{repo}/releases</c> 的 JSON）里挑出最新的正式版和 Beta：
    /// 带 prerelease 标记的算 Beta，草稿跳过；tag 要能按版本号解析。
    /// </summary>
    public static (LauncherRelease? Stable, LauncherRelease? Beta) ParseGitHubReleases(string json)
    {
        LauncherRelease? stable = null, beta = null;
        MeowVersion? sv = null, bv = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (null, null);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                var tag = r.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var ver = MeowVersion.Parse(tag);
                if (ver is null || ver.IsReview) continue;
                var url = r.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) url = "";
                var notes = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                var date = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                var rel = new LauncherRelease(tag.TrimStart('v', 'V'), url, notes, date);
                var pre = r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (pre || ver.Channel == MeowChannel.Beta)
                {
                    if (bv is null || ver.CompareTo(bv) > 0) { beta = rel; bv = ver; }
                }
                else if (sv is null || ver.CompareTo(sv) > 0) { stable = rel; sv = ver; }
            }
        }
        catch { return (null, null); }
        return (stable, beta);
    }
}
