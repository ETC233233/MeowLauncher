namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动器更新检查。版本信息来源（按顺序）：① 云端签名配置里的 launcher 字段（国内可达、有签名）；
/// ② 没有这个字段时退回 GitHub Releases（直连，失败再走 gh-proxy）。判断规则见 <see cref="UpdateLogic"/>。
/// Review（开发版）不检查更新。只提示、不自动替换：点「下载」打开发行页。
/// </summary>
public static class UpdateService
{
    /// <summary>启动器源码仓库（owner/name）。仓库公开前链接会 404，关于页照常显示。</summary>
    public const string Repo = "ETC233233/MeowLauncher";
    public static string RepoUrl => "https://github.com/" + Repo;

    /// <summary>联机模块的源码仓库。</summary>
    public const string ModuleRepo = "ETC233233/meow-room-module";
    public static string ModuleRepoUrl => "https://github.com/" + ModuleRepo;

    private static readonly HttpClient Http = CreateHttp();
    private static int _busy;

    public static UpdateResult? Last { get; private set; }
    public static event Action? Changed;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeowLauncher");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>检查一次。正在检查时重复调用，直接返回上一次结果。</summary>
    public static async Task<UpdateResult> CheckAsync(string channelPref, CancellationToken ct = default)
    {
        var cur = BuildInfo.Current;
        if (cur.IsReview) return Publish(UpdateLogic.Evaluate(cur, null, null, channelPref));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Last ?? new UpdateResult(UpdateState.Unknown, null, "正在检查…");
        try
        {
            var (stable, beta, error) = await FetchReleasesAsync(ct);
            if (error.Length > 0) return Publish(new UpdateResult(UpdateState.Unknown, null, error));
            return Publish(UpdateLogic.Evaluate(cur, stable, beta, channelPref));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Publish(new UpdateResult(UpdateState.Unknown, null, "检查更新失败：" + ex.Message)); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static UpdateResult Publish(UpdateResult r)
    {
        Last = r;
        Changed?.Invoke();
        return r;
    }

    private static async Task<(LauncherRelease? Stable, LauncherRelease? Beta, string Error)> FetchReleasesAsync(CancellationToken ct)
    {
#if DEBUG
        // 仅调试版：用本地 JSON 文件当作「云端配置 payload」，方便本地联调
        var dev = Environment.GetEnvironmentVariable("MEOW_UPDATE_FILE");
        if (!string.IsNullOrWhiteSpace(dev) && File.Exists(dev))
        {
            var (ds, db) = UpdateLogic.ParseLauncher(File.ReadAllText(dev));
            return (ds, db, "");
        }
#endif
        // ① 签名配置
        var (payload, cfgError) = await SignedConfig.FetchAsync(ct: ct);
        if (payload is not null)
        {
            var (s, b) = UpdateLogic.ParseLauncher(payload);
            if (s is not null || b is not null) return (s, b, "");
        }

        // ② GitHub Releases：直连，不行再走加速代理。404 表示仓库还没有发行版，不是错误。
        var api = $"https://api.github.com/repos/{Repo}/releases?per_page=20";
        string? lastErr = null;
        foreach (var url in UrlAccel.Candidates(api))
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null, "");
                if (!resp.IsSuccessStatusCode) { lastErr = $"HTTP {(int)resp.StatusCode}"; continue; }
                var (s, b) = UpdateLogic.ParseGitHubReleases(await resp.Content.ReadAsStringAsync(ct));
                return (s, b, "");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastErr = ex is TaskCanceledException ? "连接超时" : ex.GetType().Name; }
        }
        // 两条路都不通：把原因说清楚（云端配置失败的原因优先）
        return (null, null, payload is null && cfgError.Length > 0
            ? "检查更新失败：" + cfgError
            : $"检查更新失败：连不上 GitHub（{lastErr}）。");
    }
}
