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
    public static string? VerifiedEnvelope { get; private set; }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4), Proxy = NetworkProxy.Proxy };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
            if (!NetworkAvailability.CanStartRequest) return (null, NetworkAvailability.OfflineMessage);
            // Both entries are compiled into the client: CF must not be reachable just to discover its fallback.
            var entries = new[] { entry }.Concat(Meta("MeowConfigFallbacks").Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var u) && u.Scheme == "https")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
            async Task<(string? Payload, string? Envelope)> FetchOne(string address, int delay)
            {
                try
                {
                    await Task.Delay(delay, race.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var request = new HttpRequestMessage(HttpMethod.Get, address);
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var bytes = new MemoryStream();
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        if (bytes.Length + read > 512 * 1024) return (null, null);
                        bytes.Write(buffer, 0, read);
                    }
                    var envelope = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                    return (ComponentManifest.VerifyEnvelope(envelope), envelope);
                }
                catch { return (null, null); }
            }
            var tasks = entries.Select((x, i) => FetchOne(x, i * 400)).ToList();
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                var result = await done;
                if (result.Payload is null) continue;
                race.Cancel();
                VerifiedEnvelope = result.Envelope;
                _cache = (result.Payload, DateTime.UtcNow);
                return (result.Payload, "");
            }
            ct.ThrowIfCancellationRequested();
            return (null, "云端配置连接失败、验签失败或已过期，请稍后再试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, "连不上云端服务（" + (ex is TaskCanceledException ? "连接超时" : ex.GetType().Name) + "），请检查网络后重试。");
        }
        finally { Gate.Release(); }
    }
}
