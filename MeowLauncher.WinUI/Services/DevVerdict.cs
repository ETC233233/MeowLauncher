using System.Text;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 联网开发者验证：用正版账号的玩家证书向账号服务证明「我是这个 UUID」，服务端回答是不是平台登记的开发者
/// （<c>POST /auth/dev-status</c>，只回答、不签发会话票，非开发者也拿不到任何联机能力）。
/// 结果写到 %APPDATA%\MeowLauncher\dev-verdict.json（{uuid, developer, verifiedAt}），后端 sidecar 的
/// <c>_dev_unlocked()</c> 读它决定「开发者测试」是否出现；7 天内离线也认，过期且连不上就当作非开发者。
/// 注意：这只决定界面入口是否出现，**不是安全边界**——联机接口只认会话票，会话票只发给开发者（服务端拦）。
/// </summary>
public static class DevVerdict
{
    private static readonly HttpClient Http = CreateHttp();
    private static int _busy;

    /// <summary>最近一次没问成的原因（给排查用，不含任何账号信息）。</summary>
    public static string LastWhy { get; private set; } = "";

    private static bool? Fail(string why)
    {
        LastWhy = why;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dev-verdict.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {why}\n");
        }
        catch { }
        return null;
    }

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowLauncher", "dev-verdict.json");

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeowLauncher");
        return c;
    }

    /// <summary>上一次验证的结果（没有 / 坏了 / 不是当前账号的，返回 null）。</summary>
    public static (string Uuid, bool Developer, DateTimeOffset VerifiedAt)? ReadCached()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(FilePath));
            var r = doc.RootElement;
            return (r.GetProperty("uuid").GetString() ?? "", r.GetProperty("developer").GetBoolean(),
                DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("verifiedAt").GetInt64()));
        }
        catch { return null; }
    }

    /// <summary>缓存还新鲜（1 天内）且是当前账号：启动时不用再问一次。</summary>
    public static bool IsFresh(string currentUuid)
    {
        var c = ReadCached();
        return c is { } v && string.Equals(v.Uuid, currentUuid, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - v.VerifiedAt < TimeSpan.FromDays(1);
    }

    /// <summary>
    /// 问一次服务器。返回 true / false = 是 / 不是开发者；null = 没问成（没有正版账号、没网、服务不可用……），
    /// 此时保留旧的结果不动。成功后会把结果写盘并刷新联机开关。
    /// </summary>
    public static async Task<bool?> RefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        try
        {
            if (!NetworkAvailability.CanStartRequest) return Fail(NetworkAvailability.OfflineMessage);
            var who = await App.Backend.MojangWhoAsync(forVerdict: true);
            if (!who.Ok || string.IsNullOrWhiteSpace(who.Uuid)) return Fail("取不到正版账号：" + who.Message);
            var uuid = who.Uuid.Replace("-", "").ToLowerInvariant();

            var (payload, cfgError) = await SignedConfig.FetchAsync(ct: ct);
            if (payload is null) return Fail("取不到云端配置：" + cfgError);
            string? baseUrl;
            using (var doc = JsonDocument.Parse(payload))
                baseUrl = doc.RootElement.TryGetProperty("accountBase", out var ab) ? ab.GetString()?.TrimEnd('/') : null;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var bu) || bu.Scheme != Uri.UriSchemeHttps) return Fail("配置里没有合法的 accountBase");

            using var chRes = await Http.GetAsync(baseUrl + "/auth/challenge", ct);
            if (!chRes.IsSuccessStatusCode) return Fail("取挑战码失败 HTTP " + (int)chRes.StatusCode);
            using var chDoc = JsonDocument.Parse(await chRes.Content.ReadAsStringAsync(ct));
            var serverId = chDoc.RootElement.GetProperty("serverId").GetString() ?? "";

            var proof = await App.Backend.MojangProofAsync(serverId, forVerdict: true);
            if (!proof.Ok) return Fail("生成证明失败：" + proof.Message);

            var body = JsonSerializer.Serialize(new
            {
                serverId, uuid = proof.Uuid, name = proof.Name.Length > 0 ? proof.Name : who.Name,
                publicKey = proof.PublicKey, expiresAtMs = proof.ExpiresAtMs,
                certSignature = proof.CertSignature, signature = proof.Signature,
            });
            using var res = await Http.PostAsync(baseUrl + "/auth/dev-status",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) return Fail("dev-status 失败 HTTP " + (int)res.StatusCode);
            using var vDoc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var dev = vDoc.RootElement.TryGetProperty("developer", out var d) && d.ValueKind == JsonValueKind.True;

            Save(uuid, dev);
            return dev;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Fail("异常：" + ex.GetType().Name + " " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static void Save(string uuid, bool developer)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new { uuid, developer, verifiedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        }
        catch { }
    }
}
