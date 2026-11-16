using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>本机已装好的组件包（来自包里的 component.json）。</summary>
public sealed record ComponentInfo(string Version, int ApiLevel, string Dir);

/// <summary>
/// 联机组件（联机模块 + RoomAgent + EasyTier）：不随启动器分发，首次进入联机页时从云端清单指定的地址下载。
///
/// 信任链：清单在云端签名配置里（Ed25519，公钥在启动器里），清单给出包的 sha256 和大小；
/// 下载地址可以是任何镜像 / 加速代理，下载下来的包哈希对不上一律丢弃，所以镜像不必可信。
/// 兼容：包有「接口级别」（ApiLevel），启动器只认和自己同级别的包，清单里还可以限定适用的启动器版本区间；
/// 模块大改、接口不兼容时，新启动器会去找新级别的包，旧启动器继续用旧级别的包。
/// 更新：新版本装进新目录，下次启动器启动时才切换（已经加载的程序集不能卸载）；首次安装则立刻生效。
/// </summary>
public static class OnlineComponent
{
    /// <summary>这个启动器能对接的模块接口级别。模块接口不兼容地改动时，启动器和模块的级别一起 +1。</summary>
    public const int ApiLevel = ComponentManifest.ApiLevel;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CancellationTokenSource? _installCts;
    private static string _dir = "";
    private static bool _resolverInstalled;

    /// <summary>下载/安装是否正在进行（离开界面也在跑，进度记在任务中心）。</summary>
    public static bool Installing => Gate.CurrentCount == 0;

    /// <summary>取消正在进行的下载/安装。</summary>
    public static void CancelInstall() { try { _installCts?.Cancel(); } catch { } }

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher", "components", "online");

    /// <summary>组件已加载，联机服务可用。</summary>
    public static bool Ready { get; private set; }

    /// <summary>本次已加载的组件；没加载就是 null。</summary>
    public static ComponentInfo? Loaded { get; private set; }

    /// <summary>最近一次没装好 / 没加载成功的原因（给界面看）。</summary>
    public static string LastError { get; private set; } = "";

    public static event Action? ReadyChanged;

    /// <summary>云端有比已加载的更新的版本（每次启动器运行最多检查一次）；没有就是 null。</summary>
    public static ComponentOffer? PendingUpdate { get; private set; }
    public static event Action? UpdateAvailable;
    private static bool _updateChecked;

    /// <summary>后台检查一次组件更新。失败（断网等）静默，下次进联机页再试。</summary>
    public static async Task CheckUpdateAsync()
    {
        if (_updateChecked || !Ready) return;
        _updateChecked = true;
        try
        {
            var (offer, _) = await FetchOfferAsync();
            if (offer is null) { _updateChecked = false; return; }
            if (IsNewer(offer, Loaded)) { PendingUpdate = offer; UpdateAvailable?.Invoke(); }
        }
        catch { _updateChecked = false; }
    }

    /// <summary>手动检查一次组件更新（设置页用，不受「每次运行只查一次」限制）。返回云端适合本启动器的包和错误。</summary>
    public static async Task<(ComponentOffer? Offer, string Error)> CheckNowAsync()
    {
        var (offer, error) = await FetchOfferAsync();
        if (offer is not null)
        {
            var installed = Loaded ?? ReadInstalled();
            PendingUpdate = installed is not null && IsNewer(offer, installed) ? offer : null;
            UpdateAvailable?.Invoke();
        }
        return (offer, error);
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MeowLauncher");
        return c;
    }

    private static string Meta(string key) =>
        typeof(OnlineComponent).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    // ------------------------------------------------------------------ 本机状态

    private static string ActiveFile => Path.Combine(Root, "active.json");

    /// <summary>读本机「当前选中」的组件；没装、坏了都返回 null。</summary>
    public static ComponentInfo? ReadInstalled()
    {
        try
        {
#if DEBUG
            // 仅调试版：直接指向一个已解压的组件目录，方便本地联调（发布版没有这条路）
            var dev = Environment.GetEnvironmentVariable("MEOW_COMPONENT_DIR");
            if (!string.IsNullOrWhiteSpace(dev) && Directory.Exists(dev)) return ReadManifest(dev);
#endif
            if (!File.Exists(ActiveFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ActiveFile));
            var ver = doc.RootElement.GetProperty("version").GetString() ?? "";
            if (!SafeName(ver)) return null;
            var dir = Path.Combine(Root, ver);
            return Directory.Exists(dir) ? ReadManifest(dir) : null;
        }
        catch { return null; }
    }

    private static ComponentInfo? ReadManifest(string dir)
    {
        var f = Path.Combine(dir, "component.json");
        if (!File.Exists(f)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        var r = doc.RootElement;
        return new ComponentInfo(r.GetProperty("version").GetString() ?? "", r.GetProperty("apiLevel").GetInt32(), dir);
    }

    private static bool SafeName(string s) => ComponentManifest.SafeName(s);

    // ------------------------------------------------------------------ 加载

    /// <summary>装好了就加载（装好并且接口级别对得上、关键文件哈希没被改过）。重复调用无副作用。</summary>
    public static bool TryLoad()
    {
        if (Ready) return true;
        var info = ReadInstalled();
        if (info is null) { LastError = "联机组件还没安装。"; return false; }
        if (info.ApiLevel != ApiLevel)
        {
            LastError = $"已安装的联机组件接口级别是 {info.ApiLevel}，这个启动器要 {ApiLevel}，需要重新下载。";
            return false;
        }
        var bad = VerifyInstalled(info.Dir);
        if (bad.Length > 0) { LastError = $"联机组件文件校验失败（{bad}），请重新下载。"; return false; }

        try
        {
            _dir = info.Dir;
            InstallResolver();
            ModuleBinder.SetRoot(info.Dir);       // 告诉模块去哪找 RoomAgent / EasyTier
            App.Online.Attach();
            App.Room.Attach();
        }
        catch (Exception ex)
        {
            LastError = "联机组件加载失败：" + ex.Message;
            App.LogCrash("OnlineComponent.TryLoad", ex);
            return false;
        }
        Loaded = info;
        Ready = true;
        LastError = "";
        ReadyChanged?.Invoke();
        return true;
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            // 只替联机模块解析；其它依赖（BouncyCastle 等）用启动器自带的
            if (_dir.Length == 0 || name.Name is not "MeowRoom.Module") return null;
            var path = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>启动时校验几个最关键的文件（模块、提权代理、EasyTier）和清单里的哈希一致。返回出问题的文件名，没问题返回空串。</summary>
    private static string VerifyInstalled(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "component.json")));
            string[] critical = { "MeowRoom.Module.dll", "RoomAgent.exe", "easytier/easytier-windows-x86_64/easytier-core.exe" };
            var files = doc.RootElement.GetProperty("files").EnumerateArray()
                .ToDictionary(e => (e.GetProperty("path").GetString() ?? "").Replace('\\', '/'),
                              e => e.GetProperty("sha256").GetString() ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var c in critical)
            {
                if (!files.TryGetValue(c, out var want)) return c + "（清单里没有）";
                var path = Path.Combine(dir, c.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return c + "（缺失）";
                if (!string.Equals(HashFile(path), want, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return "";
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static string HashFile(string path) => ComponentPackage.HashFile(path);

    // ------------------------------------------------------------------ 云端清单

    /// <summary>
    /// 从配置入口取已验签的组件清单，挑出适合这个启动器的最新一份。
    /// 返回 (包, 错误)：没有适合的包、验签失败、网络问题都会给出人能看懂的原因。
    /// </summary>
    public static async Task<(ComponentOffer? Offer, string Error)> FetchOfferAsync(CancellationToken ct = default)
    {
#if DEBUG
        // 仅调试版：用本地的清单文件代替云端（不验签），方便本地联调下载 / 校验 / 安装流程
        var dev = Environment.GetEnvironmentVariable("MEOW_COMPONENT_OFFER_FILE");
        if (!string.IsNullOrWhiteSpace(dev) && File.Exists(dev)) return ComponentManifest.PickOffer(File.ReadAllText(dev), BuildInfo.Current.CoreForCompat);
#endif
        var (payload, error) = await SignedConfig.FetchAsync(ct: ct);
        if (payload is null) return (null, error);
        return ComponentManifest.PickOffer(payload, BuildInfo.Current.CoreForCompat);
    }

    /// <summary>云端的包比本机装的新（只比版本号；级别相同才会走到这里）。</summary>
    public static bool IsNewer(ComponentOffer offer, ComponentInfo? installed) =>
        installed is null || installed.ApiLevel != offer.ApiLevel
        || (Version.TryParse(offer.Version, out var a) && Version.TryParse(installed.Version, out var b) && a > b);

    // ------------------------------------------------------------------ 下载安装

    /// <summary>
    /// 下载、校验、解压并选中一个组件包。progress 报告 (0~1, 说明)。
    /// 全程只有「哈希和大小都对得上的包」才会被解压；解压内容还要和包里的 component.json 逐个文件核对。
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(ComponentOffer offer, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct)) return (false, "正在安装中。");
        _installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = _installCts.Token;
        var task = TaskCenter.Add("下载联机组件", $"版本 {offer.Version}，约 {offer.Size / 1048576.0:0} MB", "room", "\uE896");
        var outer = progress;
        progress = outer is null ? null : new Progress<(double Fraction, string Text)>(p =>
        {
            outer.Report(p);
            TaskCenter.Progress(task, p.Fraction * 100, p.Text);
        });
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = Path.Combine(Root, "_download");
            Directory.CreateDirectory(tmp);
            var zip = Path.Combine(tmp, offer.Version + ".zip");

            var (got, why) = await DownloadVerifiedAsync(offer, zip, progress, ct);
            if (!got) { TaskCenter.Fail(task, why); return (false, why); }

            progress?.Report((1, "正在解压和校验…"));
            var staging = Path.Combine(Root, offer.Version + ".staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            var extractErr = await Task.Run(() => ComponentPackage.ExtractAndVerify(zip, staging, offer.Version, offer.ApiLevel), ct);
            if (extractErr.Length > 0)
            {
                try { Directory.Delete(staging, true); } catch { }
                TaskCenter.Fail(task, extractErr);
                return (false, extractErr);
            }

            var final = Path.Combine(Root, offer.Version);
            if (Directory.Exists(final))
            {
                // 同版本重装：旧目录可能正被加载着，先改名挪开
                var aside = final + ".old-" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(final, aside);
            }
            Directory.Move(staging, final);
            var tmpActive = ActiveFile + ".tmp";
            File.WriteAllText(tmpActive, JsonSerializer.Serialize(new { version = offer.Version }));
            File.Move(tmpActive, ActiveFile, overwrite: true);
            try { File.Delete(zip); } catch { }
            CleanOld(offer.Version);

            if (!Ready)
            {
                if (TryLoad()) { TaskCenter.Complete(task, "联机组件已安装。"); return (true, "联机组件已安装。"); }
                TaskCenter.Fail(task, LastError);
                return (false, LastError);
            }
            TaskCenter.Complete(task, "联机组件已更新，重启启动器后生效。");
            return (true, "联机组件已更新，重启启动器后生效。");
        }
        catch (OperationCanceledException) { TaskCenter.Fail(task, "已取消。"); return (false, "已取消。"); }
        catch (Exception ex) { TaskCenter.Fail(task, "安装失败：" + ex.Message); return (false, "安装失败：" + ex.Message); }
        finally
        {
            try { _installCts?.Dispose(); } catch { }
            _installCts = null;
            Gate.Release();
        }
    }

    /// <summary>按清单给的地址顺序逐个试；任何一个来源下来的包都必须哈希、大小对得上，否则换下一个。</summary>
    private static async Task<(bool Ok, string Message)> DownloadVerifiedAsync(
        ComponentOffer offer, string zipPath, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        var last = "没有可用的下载地址。";
        var n = 0;
        // 清单里的地址是 GitHub 直连时国内经常卡住：开关打开时先试镜像，再回落原始地址。
        var sources = new List<string>();
        foreach (var u in offer.Urls) sources.AddRange(UrlAccel.Candidates(u));
        foreach (var url in sources)
        {
            n++;
            var host = new Uri(url).Host;
            var part = zipPath + ".part";
            try
            {
                progress?.Report((0, $"正在连接下载源 {n}/{sources.Count}…"));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                if (!resp.IsSuccessStatusCode) { last = $"下载源 {n} 返回 HTTP {(int)resp.StatusCode}。"; continue; }

                var threads = PreferredThreads();
                var done = await FetchAsync(url, part, offer, threads, progress, ct);
                var hash = await Task.Run(() => ComponentPackage.HashFile(part), ct);
                if (done != offer.Size) { last = $"下载源 {n} 的文件大小不对（{done}/{offer.Size}）。"; TryDelete(part); continue; }
                if (hash != offer.Sha256) { last = $"下载源 {n} 的文件哈希对不上，已丢弃（可能被篡改或缓存了旧版本）。"; TryDelete(part); continue; }
                File.Move(part, zipPath, overwrite: true);
                return (true, "");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = $"下载源 {n} 失败（{(ex is TaskCanceledException ? "超时" : ex.GetType().Name)}）。";
                TryDelete(part);
            }
        }
        return (false, "下载失败：" + last + " 请检查网络后重试。");
    }


    /// <summary>设置里的下载线程数（settings4.json 的 dlThreads），读不到就 8。</summary>
    private static int PreferredThreads()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MeowLauncher", "settings4.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("dlThreads", out var el) && el.TryGetInt32(out var n))
                return Math.Clamp(n, 1, 16);
        }
        catch { }
        return 8;
    }

    /// <summary>先探是否支持 Range：支持就分段多线程，否则退回单流。返回实际字节数。</summary>
    private static async Task<long> FetchAsync(string url, string part, ComponentOffer offer, int threads,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        long total = -1;
        if (threads > 1)
        {
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, url);
                probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var pr = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
                if (pr.StatusCode == System.Net.HttpStatusCode.PartialContent && pr.Content.Headers.ContentRange?.Length is long len)
                    total = len;
            }
            catch { }
        }
        if (threads > 1 && total == offer.Size && total > 2 * 1024 * 1024)
            return await FetchSegmentedAsync(url, part, total, threads, progress, ct);
        return await FetchSingleAsync(url, part, offer, progress, ct);
    }

    /// <summary>分段并发下载：每段一个 Range 请求，直接写进同一个文件的对应偏移。</summary>
    private static async Task<long> FetchSegmentedAsync(string url, string part, long total, int threads,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1);
        fs.SetLength(total);
        var handle = fs.SafeFileHandle;
        long done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = 0L;
        var tasks = new List<Task>(threads);
        for (int i = 0; i < threads; i++)
        {
            long from = total * i / threads;
            long to = total * (i + 1) / threads - 1;
            tasks.Add(Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[96 * 1024];
                long pos = from;
                while (pos <= to)
                {
                    var want = (int)Math.Min(buf.Length, to - pos + 1);
                    var read = await src.ReadAsync(buf.AsMemory(0, want), ct);
                    if (read == 0) break;
                    RandomAccess.Write(handle, buf.AsSpan(0, read), pos);
                    pos += read;
                    var d = Interlocked.Add(ref done, read);
                    if (d - Interlocked.Read(ref lastReport) > total / 200 || d >= total)
                    {
                        Interlocked.Exchange(ref lastReport, d);
                        var speed = d / Math.Max(sw.Elapsed.TotalSeconds, 0.5) / 1048576.0;
                        progress?.Report((Math.Min(0.99, (double)d / total),
                            $"正在下载联机组件 {d / 1048576.0:0.#}/{total / 1048576.0:0.#} MB · {speed:0.0} MB/s · {threads} 线程"));
                    }
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
        return Interlocked.Read(ref done);
    }

    /// <summary>单流下载（服务器不支持 Range 时）。</summary>
    private static async Task<long> FetchSingleAsync(string url, string part, ComponentOffer offer,
        IProgress<(double, string)>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)resp.StatusCode);

        long done = 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(part);
        var buf = new byte[128 * 1024];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(20));
            var read = await src.ReadAsync(buf, stall.Token);
            if (read == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (done > offer.Size + 1024) break;
            var speed = done / Math.Max(sw.Elapsed.TotalSeconds, 0.5) / 1048576.0;
            progress?.Report((Math.Min(0.99, (double)done / offer.Size),
                $"正在下载联机组件 {done / 1048576.0:0.#}/{offer.Size / 1048576.0:0.#} MB · {speed:0.0} MB/s"));
        }
        return done;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>装好新版本后清理旧版本（正在加载的那个、当前选中的不动；删不掉的下次再说）。</summary>
    private static void CleanOld(string keepVersion)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(d);
                if (name == keepVersion || name == "_download" || d.Equals(_dir, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }
    }
}
