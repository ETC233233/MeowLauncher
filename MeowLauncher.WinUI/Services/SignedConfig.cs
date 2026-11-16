using System.Reflection;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 云端签名配置（config-worker 下发，Ed25519 签名）的取回与验签，联机组件清单和启动器更新信息共用。
/// 入口地址和公钥来自启动器自己的程序集元数据（online.props）。结果只缓存在内存里几分钟，避免两处各发一次请求。
/// </summary>
public static class SignedConfig
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string Payload, DateTime At)? _cache;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeowLauncher");
        return c;
    }

    private static string Meta(string key) =>
        typeof(SignedConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    /// <summary>取已验签的配置 payload（JSON 文本）。返回 (payload, 错误)；失败时 payload 为 null、错误是给人看的原因。</summary>
    public static async Task<(string? Payload, string Error)> FetchAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is { } c && DateTime.UtcNow - c.At < CacheFor) return (c.Payload, "");
        var entry = Meta("MeowConfigEntry");
        if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "这个构建没有配置云端服务入口。");

        await Gate.WaitAsync(ct);
        try
        {
            if (!force && _cache is { } c2 && DateTime.UtcNow - c2.At < CacheFor) return (c2.Payload, "");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var envelope = await Http.GetStringAsync(uri, cts.Token);
            var payload = ComponentManifest.VerifyEnvelope(envelope);
            if (payload is null) return (null, "云端配置验签失败或已过期，请稍后再试。");
            _cache = (payload, DateTime.UtcNow);
            return (payload, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, "连不上云端服务（" + (ex is TaskCanceledException ? "连接超时" : ex.GetType().Name) + "），请检查网络后重试。");
        }
        finally { Gate.Release(); }
    }
}
