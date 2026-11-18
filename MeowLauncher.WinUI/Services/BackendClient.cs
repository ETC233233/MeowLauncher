using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

public sealed partial class BackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private Snapshot? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>正在跑的后端进程：任务页的「取消」按任务类型 + 实体名挑一个杀。</summary>
    private static readonly object RunningGate = new();
    private static readonly Dictionary<string, RunningOperation> Running = new();

    /// <summary>最近一次后端调用失败的原因，任务页据此显示错误面板。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 一次完整快照解析成功后触发。外壳（侧栏账户块、任务坞）订阅它，
    /// 这样账户状态不依赖某个具体页面是否被打开过。
    /// </summary>
    public event Action<Snapshot>? SnapshotArrived;

    public string ProjectRoot { get; }
    public string SidecarPath { get; }
    public string PythonPath { get; }

    public BackendClient()
    {
        ProjectRoot = FindProjectRoot();
        SidecarPath = Path.Combine(AppContext.BaseDirectory, "Backend", "sidecar.py");
        StartupTimeline.Mark("BackendClient 构造开始（检测 Python）");
        Python = ProbePython();
        PythonPath = Python.Found ? Python.Path : "python.exe";
        StartupTimeline.Mark("Python 检测完成");
        // 预取首个快照：和窗口构造并行跑，首页第一次要快照时直接接上（只接一次，之后照常）
        if (Python.Found) _prefetch = GetSnapshotAsync(force: true);
    }

    private Task<Snapshot?>? _prefetch;

    public async Task<Snapshot?> GetSnapshotAsync(bool force = false)
    {
        var pre = System.Threading.Interlocked.Exchange(ref _prefetch, null);
        if (pre is not null && !force) return await pre;
        if (!force && _cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(4))
        {
            return _cached;
        }

        var result = await RunAsync("--snapshot", TimeSpan.FromSeconds(25));
        if (!result.Success)
        {
            LastError = result.Error;
            Debug.WriteLine($"MeowLauncher backend snapshot failed: {result.Error}");
            return _cached;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(result.Output.Trim(), JsonOptions);
            if (snapshot is { Ok: true })
            {
                _cached = snapshot;
                _cachedAt = DateTime.UtcNow;
                StartupTimeline.Mark("首个快照到达（后端首次返回）");
                StartupTimeline.Flush();
                SnapshotArrived?.Invoke(snapshot);
                return snapshot;
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"MeowLauncher snapshot JSON error: {ex.Message}");
        }

        return _cached;
    }

    /// <summary>
    /// 两段式的第二段：只探测 Mojang 各服务可达性（sidecar 内部单次 2s、整轮 3s 预算）。
    /// 首屏不等它——快照先渲染本机数据，这张卡等它回来再刷。
    /// </summary>
    public async Task<OnlineInfo?> GetOnlineStatusAsync()
    {
        var result = await RunAsync("--status", TimeSpan.FromSeconds(8));
        if (!result.Success)
        {
            Debug.WriteLine($"MeowLauncher backend status failed: {result.Error}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OnlineInfo>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"MeowLauncher status JSON error: {ex.Message}");
            return null;
        }
    }

    /// <summary>扫描单个游戏目录（多目录版本管理用）；返回的 Snapshot 只填 GameDir 与 Versions。</summary>
    public async Task<Snapshot?> ScanDirAsync(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var result = await RunAsync($"--scan-dir \"{dir}\"", TimeSpan.FromSeconds(30));
        if (!result.Success)
        {
            Debug.WriteLine($"MeowLauncher scan-dir failed: {dir} / {result.Error}");
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(result.Output.Trim(), JsonOptions);
            return snapshot is { Ok: true } ? snapshot : null;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"MeowLauncher scan-dir JSON error: {ex.Message}");
            return null;
        }
    }

    public async Task<BackendResult> LaunchAsync(string versionId, IProgress<string>? progress = null, string root = "")
    {
        var first = await LaunchOnceAsync(versionId, progress, root);
        if (first.NeedJava < 0)
        {
            var path = await JavaGuide.ChooseRuntimeAsync(first.Message);
            if (path is null) return first;
            return await LaunchOnceAsync(versionId, progress, root, path);
        }
        if (first.NeedJava > 0 && await JavaGuide.OfferInstallAsync(first.NeedJava))
        {
            return await LaunchOnceAsync(versionId, progress, root);
        }
        return first;
    }

    public async Task<(bool Ok, string Message)> InstallJavaAsync(int major, IProgress<string>? progress = null)
    {
        var args = $"--java-install {major}" + (progress is null ? "" : " --progress");
        var result = await RunAsync(args, TimeSpan.FromMinutes(15), progress is null ? null : stage => progress.Report(stage));
        if (!result.Success) return (false, result.Error);
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var r = doc.RootElement;
            var ok = r.TryGetProperty("ok", out var o) && o.GetBoolean();
            var msg = r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return (ok, msg);
        }
        catch (Exception ex)
        {
            return (false, $"无法解析下载结果：{ex.Message}");
        }
    }

    private async Task<BackendResult> LaunchOnceAsync(string versionId, IProgress<string>? progress, string root,
        string? javaOnce = null)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return new BackendResult(false, "没有选择要启动的版本。");
        }

        var arguments = $"--launch \"{versionId}\"" + (progress is null ? "" : " --progress");
        if (javaOnce is not null)
            arguments += " --java-once " + Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(new { javaPath = javaOnce }));
        var result = progress is null
            ? await RunAsync(arguments, TimeSpan.FromSeconds(90), null, root)
            : await RunAsync(arguments, TimeSpan.FromSeconds(90), stage => progress.Report(stage), root);
        return ParseActionResult(result);
    }

    public async Task<ModsResult?> GetModsAsync(string versionId)
    {
        var result = await RunAsync($"--mods \"{versionId}\"", TimeSpan.FromSeconds(45));
        if (!result.Success)
        {
            return new ModsResult { Ok = false, Message = result.Error };
        }

        try
        {
            return JsonSerializer.Deserialize<ModsResult>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ModsResult { Ok = false, Message = $"解析 Mod 列表失败：{ex.Message}" };
        }
    }

    public async Task<BackendResult> ToggleModAsync(string file)
    {
        var result = await RunAsync($"--toggle-mod \"{file}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    /// <summary>检查某个实例里已启用 Mod 的更新；Ok=false 时 Message 是失败原因。</summary>
    public async Task<ModUpdatesResult> CheckModUpdatesAsync(string versionId)
    {
        var result = await RunAsync($"--check-mod-updates \"{versionId}\"", TimeSpan.FromSeconds(90));
        if (!result.Success)
            return new ModUpdatesResult(false, string.IsNullOrWhiteSpace(result.Error) ? "后端没有响应。" : result.Error, 0, new List<ModUpdate>());
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string Str(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var list = new List<ModUpdate>();
            if (root.TryGetProperty("updates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var u in arr.EnumerateArray())
                    list.Add(new ModUpdate(Str(u, "file"), Str(u, "name"), Str(u, "current"), Str(u, "latest"),
                        Str(u, "download"), Str(u, "filename"), Str(u, "source")));
            var checkedCount = root.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            return new ModUpdatesResult(ok, ok ? "" : Str(root, "message"), checkedCount, list);
        }
        catch (JsonException)
        {
            return new ModUpdatesResult(false, "更新检查结果解析失败。", 0, new List<ModUpdate>());
        }
    }

    public async Task<BackendResult> UpdateModAsync(string versionId, ModUpdate update)
    {
        var result = await RunAsync(
            $"--update-mod \"{update.File}\" \"{update.Download}\" \"{update.Filename}\" \"{versionId}\"",
            TimeSpan.FromMinutes(5));
        return ParseActionResult(result);
    }

    /// <summary>把误放在 mods 里的资源包 / 光影包移到实例对应文件夹（kind = resourcepack / shaderpack）。</summary>
    public async Task<BackendResult> MoveMisplacedAsync(string file, string versionId, string kind)
    {
        var result = await RunAsync($"--move-misplaced \"{file}\" \"{versionId}\" {kind}", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    /// <summary>把本地 .zip/.jar 数据包装进某个存档的 datapacks（重名拒绝，不覆盖）。</summary>
    public async Task<BackendResult> InstallDatapackAsync(string versionId, string save, string source)
    {
        var result = await RunAsync($"--datapack-install \"{versionId}\" \"{save}\" \"{source}\"", TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    /// <summary>下载在线项目里选中的文件，直接装进指定存档的 datapacks。</summary>
    public async Task<BackendResult> InstallOnlineDatapackAsync(
        string source, string projectId, string fileId, string versionId, string save, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(save))
            return new BackendResult(false, "没有选择存档。");

        var result = await RunAsync(
            $"--install-online-datapack {source} {projectId} {fileId} \"{versionId}\" \"{save}\"",
            TimeSpan.FromMinutes(10), taskId: taskId);
        return ParseActionResult(result);
    }

    public async Task<BackendResult> DeleteModAsync(string file)
    {
        var result = await RunAsync($"--delete-mod \"{file}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    /// <summary>导入本地资源：kind = mod / resourcepack / shaderpack / world，后端负责建目录和重名处理。</summary>
    public async Task<BackendResult> ImportAssetAsync(string kind, string versionId, string source, string gameDir = "")
    {
        if (string.IsNullOrWhiteSpace(source)) return new BackendResult(false, "没有选择文件。");
        var result = await RunAsync(
            $"--import-asset \"{kind}\" \"{versionId}\" \"{source}\"",
            TimeSpan.FromMinutes(10), gameDir: gameDir);
        return ParseActionResult(result);
    }

    public async Task<BackendResult> AddOfflineAccountAsync(string name, bool slim = false)
    {
        var variant = slim ? "slim" : "classic";
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { name, variant }));
        var result = await RunAsync($"--add-offline-json {data}", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<MinecraftProfile> GetMinecraftProfileAsync()
    {
        var result = await RunAsync("--mc-profile", TimeSpan.FromSeconds(40));
        if (!result.Success) return new MinecraftProfile { Ok = false, Message = result.Error };
        try
        {
            return JsonSerializer.Deserialize<MinecraftProfile>(result.Output.Trim(), JsonOptions)
                   ?? new MinecraftProfile { Ok = false, Message = "档案数据为空。" };
        }
        catch (JsonException ex)
        {
            return new MinecraftProfile { Ok = false, Message = $"解析档案失败：{ex.Message}" };
        }
    }

    public async Task<FriendsResult> GetFriendsAsync()
    {
        var result = await RunAsync("--friends", TimeSpan.FromSeconds(60));
        if (!result.Success)
        {
            return new FriendsResult { Ok = false, Message = result.Error };
        }

        try
        {
            return JsonSerializer.Deserialize<FriendsResult>(result.Output.Trim(), JsonOptions)
                   ?? new FriendsResult { Ok = false, Message = "好友列表为空。" };
        }
        catch (JsonException ex)
        {
            return new FriendsResult { Ok = false, Message = $"解析好友列表失败：{ex.Message}" };
        }
    }

    // ---- 只读后端调用的「合并同类请求」：同一个读取正在进行时，后来的调用直接等同一个结果，
    // 不再各自拉起一个 Python 进程（设置页被连续点击时，每次都会开一批读取）。读取结束后不缓存，下次重新读。
    private readonly ConcurrentDictionary<string, object> _shared = new();

    private Task<T> Shared<T>(string key, Func<Task<T>> start)
    {
        var lazy = (Lazy<Task<T>>)_shared.GetOrAdd(key, _ => new Lazy<Task<T>>(() => RunSharedAsync(key, start)));
        return lazy.Value;
    }

    private async Task<T> RunSharedAsync<T>(string key, Func<Task<T>> start)
    {
        try { return await start(); }
        finally { _shared.TryRemove(key, out _); }
    }

    public Task<DevSettings> GetDevSettingsAsync() => Shared("GetDevSettingsAsync", GetDevSettingsCoreAsync);

    private async Task<DevSettings> GetDevSettingsCoreAsync()
    {
        var result = await RunAsync("--dev-get", TimeSpan.FromSeconds(15));
        if (!result.Success) return new DevSettings { Ok = false };
        try { return JsonSerializer.Deserialize<DevSettings>(result.Output.Trim(), JsonOptions) ?? new DevSettings(); }
        catch (JsonException) { return new DevSettings(); }
    }

    public async Task<DevSettings> SetDevSettingsAsync(bool devFeatures, bool online, bool share)
    {
        var result = await RunAsync($"--dev-set {(devFeatures ? 1 : 0)} {(online ? 1 : 0)} {(share ? 1 : 0)}", TimeSpan.FromSeconds(15));
        if (!result.Success) return new DevSettings { Ok = false };
        try { return JsonSerializer.Deserialize<DevSettings>(result.Output.Trim(), JsonOptions) ?? new DevSettings(); }
        catch (JsonException) { return new DevSettings(); }
    }

    /// <summary>联机握手前取当前正版账号的玩家名和 UUID（不含令牌）。</summary>
    public async Task<MojangIdentity> MojangWhoAsync(bool forVerdict = false)
    {
        var result = await RunAsync(forVerdict ? "--mojang-who --verdict" : "--mojang-who", TimeSpan.FromSeconds(15));
        if (!result.Success) return new MojangIdentity { Ok = false, Message = result.Error };
        try { return JsonSerializer.Deserialize<MojangIdentity>(result.Output.Trim(), JsonOptions) ?? new MojangIdentity(); }
        catch (JsonException ex) { return new MojangIdentity { Ok = false, Message = ex.Message }; }
    }

    /// <summary>Minecraft 登录握手第 2 步：后端用玩家自己的令牌对 Mojang 做 join；令牌不会出现在返回值里。</summary>
    public async Task<MojangIdentity> MojangJoinAsync(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return new MojangIdentity { Ok = false, Message = "没有挑战码" };
        var result = await RunAsync($"--mojang-join {serverId}", TimeSpan.FromSeconds(40));
        if (!result.Success) return new MojangIdentity { Ok = false, Message = result.Error };
        try { return JsonSerializer.Deserialize<MojangIdentity>(result.Output.Trim(), JsonOptions) ?? new MojangIdentity(); }
        catch (JsonException ex) { return new MojangIdentity { Ok = false, Message = ex.Message }; }
    }

    /// <summary>联机登录：后端向 Mojang 申请玩家证书并对挑战码签名；令牌和私钥都不会出现在返回值里。</summary>
    public async Task<MojangProof> MojangProofAsync(string serverId, bool forVerdict = false)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return new MojangProof { Ok = false, Message = "没有挑战码" };
        var result = await RunAsync($"--mojang-proof {serverId}" + (forVerdict ? " --verdict" : ""), TimeSpan.FromSeconds(40));
        if (!result.Success) return new MojangProof { Ok = false, Message = result.Error };
        try { return JsonSerializer.Deserialize<MojangProof>(result.Output.Trim(), JsonOptions) ?? new MojangProof(); }
        catch (JsonException ex) { return new MojangProof { Ok = false, Message = ex.Message }; }
    }

    /// <summary>单独上报在线状态：ONLINE 用于心跳，OFFLINE 用于退出。游戏在运行或关了「显示在线」时后端会跳过（Skipped 里有原因）。</summary>
    public async Task<PresenceReport> ReportPresenceAsync(string status, TimeSpan? timeout = null)
    {
        var result = await RunAsync($"--presence {status}", timeout ?? TimeSpan.FromSeconds(30));
        if (!result.Success) return new PresenceReport { Ok = false, Error = result.Error };
        try
        {
            return JsonSerializer.Deserialize<PresenceReport>(result.Output.Trim(), JsonOptions)
                   ?? new PresenceReport { Ok = false, Error = "上报结果为空。" };
        }
        catch (JsonException ex)
        {
            return new PresenceReport { Ok = false, Error = $"解析上报结果失败：{ex.Message}" };
        }
    }

    public async Task<BackendResult> UpdateFriendAsync(string action, string name, string profileId)
    {
        var result = await RunAsync(
            $"--friend-action {action} \"{name}\" \"{profileId}\"",
            TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    public async Task<AccountPermsResult> GetAccountPermsAsync()
    {
        var result = await RunAsync("--account-perms", TimeSpan.FromSeconds(40));
        if (!result.Success) return new AccountPermsResult { Ok = false, Message = result.Error };
        try
        {
            return JsonSerializer.Deserialize<AccountPermsResult>(result.Output.Trim(), JsonOptions)
                   ?? new AccountPermsResult { Ok = false, Message = "账户权限数据为空。" };
        }
        catch (JsonException ex)
        {
            return new AccountPermsResult { Ok = false, Message = $"解析账户权限失败：{ex.Message}" };
        }
    }

    public async Task<AccountPermsResult> SetAccountPermAsync(string kind, string value)
    {
        var result = await RunAsync($"--set-account-perm {kind} {value}", TimeSpan.FromSeconds(40));
        if (!result.Success) return new AccountPermsResult { Ok = false, Message = result.Error };
        try
        {
            return JsonSerializer.Deserialize<AccountPermsResult>(result.Output.Trim(), JsonOptions)
                   ?? new AccountPermsResult { Ok = false, Message = "账户权限数据为空。" };
        }
        catch (JsonException ex)
        {
            return new AccountPermsResult { Ok = false, Message = $"解析账户权限失败：{ex.Message}" };
        }
    }

    public async Task<BackendResult> RenameMinecraftAsync(string name)
    {
        var result = await RunAsync($"--mc-rename \"{name}\"", TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> SetCapeAsync(string capeId)
    {
        var result = await RunAsync($"--mc-cape \"{capeId}\"", TimeSpan.FromSeconds(40));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> SetSkinVariantAsync(string variant)
    {
        var result = await RunAsync($"--mc-skin-variant {variant}", TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> ResetSkinAsync()
    {
        var result = await RunAsync("--mc-reset-skin", TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> UploadSkinAsync(string path, string variant)
    {
        var result = await RunAsync($"--mc-upload-skin \"{path}\" {variant}", TimeSpan.FromSeconds(90));
        return ParseActionResult(result);
    }

    /// <summary>离线账户选一张本地 PNG：复制进启动器目录并写回 settings，不动原文件。</summary>
    public async Task<BackendResult> SetLocalSkinAsync(string key, string path)
    {
        var result = await RunAsync($"--set-local-skin \"{key}\" \"{path}\"", TimeSpan.FromSeconds(60));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> ClearLocalSkinAsync(string key)
    {
        var result = await RunAsync($"--clear-local-skin \"{key}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> SetLocalSkinVariantAsync(string key, string variant)
    {
        var result = await RunAsync($"--set-local-skin-variant \"{key}\" {variant}", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> DeleteAccountAsync(string key)
    {
        var result = await RunAsync($"--delete-account \"{key}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> ReorderAccountsAsync(IEnumerable<string> keys)
    {
        var order = string.Join(";", keys);
        var result = await RunAsync($"--reorder-accounts \"{order}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<BackendResult> UseAccountAsync(string key)
    {
        var result = await RunAsync($"--use-account \"{key}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public Task<LaunchDefaultsResult> GetLaunchDefaultsAsync() => Shared("GetLaunchDefaultsAsync", GetLaunchDefaultsCoreAsync);

    private async Task<LaunchDefaultsResult> GetLaunchDefaultsCoreAsync()
    {
        var result = await RunAsync("--launch-defaults", TimeSpan.FromSeconds(20));
        if (!result.Success)
        {
            return new LaunchDefaultsResult(false, 4096, true, "", result.Error);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            int mem = root.TryGetProperty("mem", out var memEl) && memEl.TryGetInt32(out var m) ? m : 4096;
            bool memAuto = root.TryGetProperty("memAuto", out var autoEl) && autoEl.GetBoolean();
            string jvm = root.TryGetProperty("jvm", out var jvmEl) ? jvmEl.GetString() ?? "" : "";
            string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            return new LaunchDefaultsResult(ok, mem, memAuto, jvm, message);
        }
        catch (JsonException)
        {
            return new LaunchDefaultsResult(false, 4096, true, "", "启动默认数据格式异常");
        }
    }

    public async Task<BackendResult> SetLaunchDefaultsAsync(int mem, bool memAuto, string jvm)
    {
        var payload = JsonSerializer.Serialize(new { mem, memAuto, jvm });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var result = await RunAsync($"--set-launch-defaults {encoded}", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public Task<GeneralSettings> GetGeneralSettingsAsync() => Shared("GetGeneralSettingsAsync", GetGeneralSettingsCoreAsync);

    private async Task<GeneralSettings> GetGeneralSettingsCoreAsync()
    {
        var result = await RunAsync("--general-settings", TimeSpan.FromSeconds(20));
        if (!result.Success)
            return new GeneralSettings(false, "priority:modrinth,curseforge", "", 16, "", "", result.Error);

        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string source = root.TryGetProperty("downloadSource", out var srcEl)
                ? srcEl.GetString() ?? "" : "priority:modrinth,curseforge";
            string mirror = root.TryGetProperty("mirror", out var mirrorEl) ? mirrorEl.GetString() ?? "" : "";
            int threads = root.TryGetProperty("dlThreads", out var threadsEl) && threadsEl.TryGetInt32(out var t) ? t : 16;
            string cfKey = root.TryGetProperty("cfApiKey", out var cfEl) ? cfEl.GetString() ?? "" : "";
            string javaPath = root.TryGetProperty("javaPath", out var javaEl) ? javaEl.GetString() ?? "" : "";
            string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            return new GeneralSettings(ok, source, mirror, threads, cfKey, javaPath, message);
        }
        catch (JsonException)
        {
            return new GeneralSettings(false, "priority:modrinth,curseforge", "", 16, "", "", "网络设置数据格式异常");
        }
    }

    public async Task<BackendResult> SetGeneralSettingsAsync(string downloadSource, string mirror,
        int dlThreads, string cfApiKey, string javaPath)
    {
        var payload = JsonSerializer.Serialize(new { downloadSource, mirror, dlThreads, cfApiKey, javaPath });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var result = await RunAsync($"--set-general-settings {encoded}", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    public async Task<InstanceLaunchSettings> GetInstanceLaunchSettingsAsync(string versionId, string gameRoot = "")
    {
        var result = await RunAsync($"--instance-settings \"{versionId}\"", TimeSpan.FromSeconds(20), gameDir: gameRoot);
        if (!result.Success)
        {
            return new InstanceLaunchSettings(false, 4096, true, true, "", "", result.Error);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            int mem = root.TryGetProperty("mem", out var memEl) && memEl.TryGetInt32(out var m) ? m : 4096;
            bool useGlobal = !root.TryGetProperty("useGlobal", out var globalEl) || globalEl.GetBoolean();
            bool memAuto = !root.TryGetProperty("memAuto", out var autoEl) || autoEl.GetBoolean();
            string javaPath = root.TryGetProperty("javaPath", out var javaEl) ? javaEl.GetString() ?? "" : "";
            string jvm = root.TryGetProperty("jvm", out var jvmEl) ? jvmEl.GetString() ?? "" : "";
            string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            string isolation = root.TryGetProperty("isolation", out var isoEl) ? isoEl.GetString() ?? "follow" : "follow";
            bool isolated = root.TryGetProperty("isolated", out var isoOn) && isoOn.ValueKind == JsonValueKind.True;
            string isoDefault = root.TryGetProperty("isolationDefault", out var isoDefEl) ? isoDefEl.GetString() ?? "auto" : "auto";
            return new InstanceLaunchSettings(ok, mem, useGlobal, memAuto, javaPath, jvm, message,
                isolation, isolated, isoDefault)
            {
                OverrideMem = root.TryGetProperty("overrideMem", out var localMem) ? localMem.GetInt32() : mem,
                OverrideMemAuto = root.TryGetProperty("overrideMemAuto", out var localAuto) ? localAuto.GetBoolean() : !useGlobal && memAuto,
                GlobalMem = root.TryGetProperty("globalMem", out var globalMem) ? globalMem.GetInt32() : mem,
                GlobalMemAuto = root.TryGetProperty("globalMemAuto", out var globalAuto) ? globalAuto.GetBoolean() : useGlobal && memAuto,
                GlobalJvm = root.TryGetProperty("globalJvm", out var globalJvm) ? globalJvm.GetString() ?? "" : "",
                JvmAuto = !root.TryGetProperty("jvmAuto", out var jvmAutoEl) || jvmAutoEl.ValueKind != JsonValueKind.False,
                GlobalJvmAuto = !root.TryGetProperty("globalJvmAuto", out var gjvmAutoEl) || gjvmAutoEl.ValueKind != JsonValueKind.False,
            };
        }
        catch (JsonException)
        {
            return new InstanceLaunchSettings(false, 4096, true, true, "", "", "实例启动设置数据格式异常");
        }
    }

    public Task<BackendResult> SetInstanceLaunchSettingsAsync(
        string versionId, int mem, bool useGlobal, bool memAuto, string javaPath, string jvm,
        string isolation = "follow", string root = "", bool jvmAuto = true) =>
        SaveInstanceSettingsPayloadAsync(versionId, new { mem, useGlobal, memAuto, javaPath, jvm, jvmAuto, isolation }, root);

    private async Task<BackendResult> SaveInstanceSettingsPayloadAsync(string versionId, object values, string root)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(values));
        var result = await RunAsync($"--set-instance-settings \"{versionId}\" {encoded}", TimeSpan.FromSeconds(20), gameDir: root);
        return ParseActionResult(result);
    }

    public async Task<MicrosoftStartResult> StartMicrosoftAsync()
    {
        var result = await RunAsync("--ms-start", TimeSpan.FromSeconds(40));
        if (!result.Success)
        {
            return new MicrosoftStartResult(false, result.Error, "", "", "", 5, 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            string code = root.TryGetProperty("user_code", out var codeEl) ? codeEl.GetString() ?? "" : "";
            string device = root.TryGetProperty("device_code", out var devEl) ? devEl.GetString() ?? "" : "";
            string uri = root.TryGetProperty("verification_uri", out var uriEl) ? uriEl.GetString() ?? "" : "";
            int interval = root.TryGetProperty("interval", out var intEl) ? intEl.GetInt32() : 5;
            int expires = root.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 900;
            return new MicrosoftStartResult(ok, message, code, device, uri, Math.Max(3, interval), expires);
        }
        catch (Exception ex)
        {
            return new MicrosoftStartResult(false, $"解析设备码失败：{ex.Message}", "", "", "", 5, 0);
        }
    }

    public async Task<MicrosoftPollResult> PollMicrosoftAsync(string deviceCode, IProgress<string>? progress = null)
    {
        var result = await RunAsync($"--ms-poll \"{deviceCode}\"" + (progress is null ? "" : " --progress"),
            TimeSpan.FromSeconds(40), progress is null ? null : progress.Report);
        if (!result.Success)
        {
            return new MicrosoftPollResult(false, false, result.Error, "");
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            bool pending = root.TryGetProperty("pending", out var pendEl) && pendEl.GetBoolean();
            string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            return new MicrosoftPollResult(ok, pending, message, name);
        }
        catch (Exception ex)
        {
            return new MicrosoftPollResult(false, false, $"解析登录结果失败：{ex.Message}", "");
        }
    }

    public async Task<LogsResult> GetLogsAsync(string? file = null)
    {
        var args = string.IsNullOrWhiteSpace(file) ? "--logs" : $"--logs \"{file}\"";
        var result = await RunAsync(args, TimeSpan.FromSeconds(25));
        if (!result.Success)
        {
            return new LogsResult { Ok = false, Message = result.Error };
        }

        try
        {
            return JsonSerializer.Deserialize<LogsResult>(result.Output.Trim(), JsonOptions)
                   ?? new LogsResult { Ok = false, Message = "日志数据为空。" };
        }
        catch (JsonException ex)
        {
            return new LogsResult { Ok = false, Message = $"解析日志失败：{ex.Message}" };
        }
    }

    /// <summary>让后端抓一次 MC 百科的版本代号并写缓存（缓存 7 天内后端自己会跳过）。</summary>
    public async Task<BackendResult> RefreshVersionNamesAsync(bool force = false)
    {
        var result = await RunAsync(force ? "--refresh-version-names --force" : "--refresh-version-names",
            TimeSpan.FromSeconds(90));
        if (!result.Success) return new BackendResult(false, result.Error);
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return new BackendResult(ok, message);
        }
        catch (JsonException ex)
        {
            return new BackendResult(false, $"解析版本代号结果失败：{ex.Message}");
        }
    }

    public async Task<MojangVersionsResult?> GetMojangVersionsAsync()
    {
        var result = await RunAsync("--mojang-versions", TimeSpan.FromSeconds(45));
        if (!result.Success)
        {
            return new MojangVersionsResult { Ok = false, Message = result.Error };
        }

        try
        {
            return JsonSerializer.Deserialize<MojangVersionsResult>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new MojangVersionsResult { Ok = false, Message = $"解析 Mojang 版本列表失败：{ex.Message}" };
        }
    }

    public async Task<BackendResult> ImportModpackAsync(string path, string gameDir, IProgress<string> progress, string? taskId = null)
    {
        var result = await RunAsync($"--import-modpack \"{path}\" --progress", TimeSpan.FromMinutes(45),
            progress.Report, gameDir, taskId);
        return ParseActionResult(result);
    }

    public async Task<BackendResult> InstallOnlineAsync(string source, string projectId, string versionId, string kind = "mod",
        IProgress<string>? progress = null, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
        {
            return new BackendResult(false, "没有指定在线资源。");
        }

        var pack = kind == "modpack";
        var result = await RunAsync(
            $"--install-online {source} {projectId} \"{versionId}\" {kind}" + (progress is null ? "" : " --progress"),
            TimeSpan.FromMinutes(pack ? 45 : 10), progress is null ? null : progress.Report, pack ? DefaultPackRoot() : "", taskId);
        return ParseActionResult(result);
    }

    public async Task<OnlineFilesResult?> GetOnlineFilesAsync(string source, string projectId, string versionId, string kind = "mod")
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
            return new OnlineFilesResult { Ok = false, Message = "没有指定在线资源。" };

        var result = await RunAsync(
            $"--online-files {source} {projectId} \"{versionId}\" {kind}",
            TimeSpan.FromSeconds(45));
        if (!result.Success) return new OnlineFilesResult { Ok = false, Message = result.Error };
        try
        {
            return JsonSerializer.Deserialize<OnlineFilesResult>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new OnlineFilesResult { Ok = false, Message = $"解析文件列表失败：{ex.Message}" };
        }
    }

    public async Task<BackendResult> InstallOnlineFileAsync(
        string source, string projectId, string fileId, string versionId, string kind = "mod",
        string confirmation = "", IProgress<string>? progress = null, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(fileId))
            return new BackendResult(false, "没有指定在线资源文件。");

        var args = $"--install-online-file {source} {projectId} {fileId} \"{versionId}\" {kind}";
        if (confirmation.Length > 0) args += $" --confirmed-dependencies={confirmation}";
        if (progress is not null) args += " --progress";
        var pack = kind == "modpack";
        var result = await RunAsync(args, TimeSpan.FromMinutes(pack ? 45 : 10),
            progress is null ? null : progress.Report, pack ? DefaultPackRoot() : "", taskId);
        return ParseActionResult(result);
    }

    private static string DefaultPackRoot() =>
        GameDirectoryResolver.Resolve(App.MainWindow?.Preferences).FirstOrDefault() ?? "";

    public async Task<OnlineModInstallPlan?> ConfirmOnlineModInstallAsync(
        string source, string projectId, string fileId, string versionId, Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        var args = string.IsNullOrWhiteSpace(fileId)
            ? $"--install-online {source} {projectId} \"{versionId}\" mod"
            : $"--install-online-file {source} {projectId} {fileId} \"{versionId}\" mod";
        var result = await RunAsync(args + " --plan-online-mod", TimeSpan.FromMinutes(2));
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var plan = JsonSerializer.Deserialize<OnlineModInstallPlan>(result.Output.Trim(), JsonOptions);
        if (plan is not { Ok: true, Confirmation.Length: > 0, FileId.Length: > 0 })
            throw new InvalidOperationException(plan?.Message ?? "读取前置列表失败。");
        if (plan.Dependencies is not { Length: > 0 }) return plan;

        var list = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = $"以下前置将和 Mod 一起安装到 {versionId}。\n\n"
                   + string.Join("\n", plan.Dependencies.Select(name => $"• {name}")),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "安装必需前置",
            Content = new Microsoft.UI.Xaml.Controls.ScrollViewer { MaxHeight = 300, Content = list },
            PrimaryButtonText = "一起安装",
            CloseButtonText = "取消",
        };
        return await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ? plan : null;
    }

    public async Task<BackendResult> InstallVersionAsync(string versionId, string loader,
        string loaderVersion = "", IProgress<string>? progress = null, bool withPrereq = false,
        string instanceName = "", string prereqVersion = "", string gameDir = "", string prereqProject = "", string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return new BackendResult(false, "没有指定要安装的版本。");
        }

        var args = $"--install-version \"{versionId}\" {loader}";
        if (!string.IsNullOrWhiteSpace(loaderVersion)) args += $" {loaderVersion}";
        if (withPrereq) args += " --prereq";
        if (!string.IsNullOrWhiteSpace(prereqProject)) args += $" --prereq-project {prereqProject}";
        if (!string.IsNullOrWhiteSpace(prereqVersion)) args += $" --prereq-version \"{prereqVersion}\"";
        if (!string.IsNullOrWhiteSpace(instanceName)) args += $" --name \"{instanceName}\"";
        if (progress is not null) args += " --progress";
        var result = progress is null
            ? await RunAsync(args, TimeSpan.FromMinutes(30), gameDir: gameDir, taskId: taskId)
            : await RunAsync(args, TimeSpan.FromMinutes(30), stage => progress.Report(stage), gameDir, taskId);
        return ParseActionResult(result);
    }

    public async Task<IReadOnlyList<ApiRelease>?> GetApiVersionsAsync(string mcVersion, string projectId)
    {
        if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(projectId)) return null;
        var result = await RunAsync($"--fabric-api-versions \"{mcVersion}\" {projectId}", TimeSpan.FromSeconds(40));
        if (!result.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                return items.Deserialize<List<ApiRelease>>(JsonOptions);
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>列某个游戏版本可用的加载器版本（新→旧）；失败时返回空列表。</summary>
    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(string mcVersion, string kind)
    {
        if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(kind))
            return Array.Empty<string>();

        var result = await RunAsync($"--loader-versions \"{mcVersion}\" {kind}", TimeSpan.FromSeconds(75));
        if (!result.Success) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            if (!doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var item in versions.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    list.Add(text);
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>解析 {ok,message,versions,recommended} 结构。</summary>
    private static LoaderKindInfo ParseKind(JsonElement el)
    {
        var kOk = el.TryGetProperty("ok", out var kOkEl) && kOkEl.ValueKind == JsonValueKind.True;
        var kMsg = el.TryGetProperty("message", out var kMsgEl) && kMsgEl.ValueKind == JsonValueKind.String
            ? kMsgEl.GetString() ?? "" : "";
        var rec = el.TryGetProperty("recommended", out var recEl) && recEl.ValueKind == JsonValueKind.String
            ? recEl.GetString() ?? "" : "";
        var list = new List<string>();
        if (el.TryGetProperty("versions", out var vEl) && vEl.ValueKind == JsonValueKind.Array)
            foreach (var item in vEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    list.Add(text);
        return new LoaderKindInfo(kOk, kMsg, list, rec);
    }

    /// <summary>只查一个加载器（界面里各加载器独立请求、独立超时）。ok=false 表示查询失败，不等于不支持。</summary>
    public async Task<LoaderKindInfo> GetLoaderKindAsync(string mcVersion, string kind)
    {
        if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(kind))
            return new LoaderKindInfo(false, "没有指定版本。", Array.Empty<string>(), "");
        var result = await RunAsync($"--loader-versions \"{mcVersion}\" {kind}", TimeSpan.FromSeconds(20));
        if (!result.Success)
            return new LoaderKindInfo(false, string.IsNullOrWhiteSpace(result.Error) ? "后台没有响应。" : result.Error,
                Array.Empty<string>(), "");
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            return ParseKind(doc.RootElement);
        }
        catch (JsonException)
        {
            return new LoaderKindInfo(false, "清单解析失败。", Array.Empty<string>(), "");
        }
    }

    /// <summary>
    /// 一次查完某个游戏版本下四种加载器的可用构建。每种带 Ok：false 是查询失败（不等于不支持），
    /// Ok 且 Versions 为空才是该版本真的没有这个加载器。Recommended 是后端判定的稳定版（可能为空）。
    /// </summary>
    public async Task<LoaderSupportResult> GetLoaderSupportAsync(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            return new LoaderSupportResult(false, "没有指定游戏版本。", new Dictionary<string, LoaderKindInfo>());

        var result = await RunAsync($"--loader-support \"{mcVersion}\"", TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new LoaderSupportResult(false, string.IsNullOrWhiteSpace(result.Error) ? "后端没有响应。" : result.Error,
                new Dictionary<string, LoaderKindInfo>());
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? "" : "";
            var kinds = new Dictionary<string, LoaderKindInfo>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("kinds", out var kindsEl) && kindsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in kindsEl.EnumerateObject())
                    kinds[prop.Name] = ParseKind(prop.Value);
            }
            return new LoaderSupportResult(ok, message, kinds);
        }
        catch (JsonException)
        {
            return new LoaderSupportResult(false, "加载器清单解析失败。", new Dictionary<string, LoaderKindInfo>());
        }
    }

    public Task<JavaScanResult> GetJavaScanAsync() => Shared("GetJavaScanAsync", GetJavaScanCoreAsync);

    private async Task<JavaScanResult> GetJavaScanCoreAsync()
    {
        var result = await RunAsync("--java-scan", TimeSpan.FromSeconds(60));
        if (!result.Success) return new JavaScanResult { Ok = false, Message = result.Error };
        try
        {
            return JsonSerializer.Deserialize<JavaScanResult>(result.Output.Trim(), JsonOptions)
                   ?? new JavaScanResult { Ok = false, Message = "Java 扫描结果为空。" };
        }
        catch (JsonException ex)
        {
            return new JavaScanResult { Ok = false, Message = $"解析 Java 列表失败：{ex.Message}" };
        }
    }

    public Task<HiddenVersionsResult?> GetHiddenVersionsAsync() => Shared("GetHiddenVersionsAsync", GetHiddenVersionsCoreAsync);

    private async Task<HiddenVersionsResult?> GetHiddenVersionsCoreAsync()
    {
        var result = await RunAsync("--hidden-versions", TimeSpan.FromSeconds(20));
        if (!result.Success) return null;
        try
        {
            return JsonSerializer.Deserialize<HiddenVersionsResult>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<BackendResult> HideVersionAsync(string versionId, string root = "")
    {
        var result = await RunAsync($"--hide-version \"{versionId}\"", TimeSpan.FromSeconds(20), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>切换主游戏目录：sidecar 会把它记进 settings4.json（mcDir 与 mcDirs 列表）。</summary>
    public async Task<bool> SetGameDirAsync(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var result = await RunAsync($"--set-dir \"{dir}\"", TimeSpan.FromSeconds(20));
        if (!result.Success)
        {
            Debug.WriteLine($"MeowLauncher set-dir failed: {dir} / {result.Error}");
            return false;
        }

        return true;
    }

    public async Task<BackendResult> UnhideVersionAsync(string versionId, string root = "")
    {
        var result = await RunAsync($"--unhide-version \"{versionId}\" \"{root}\"", TimeSpan.FromSeconds(20));
        return ParseActionResult(result);
    }

    /// <summary>切换当前实例：写 settings4.json 的 selected，并让游戏目录跟随该实例。</summary>
    public async Task<BackendResult> SelectVersionAsync(string versionId, string root = "")
    {
        if (string.IsNullOrWhiteSpace(versionId)) return new BackendResult(false, "没有指定实例。");
        var result = await RunAsync($"--select-version \"{versionId}\"", TimeSpan.FromSeconds(20), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>改实例名：文件夹、同名 json 与 settings4 里的引用一起改，返回新 id。</summary>
    public async Task<BackendResult> RenameVersionAsync(string oldId, string newId, string root = "")
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId))
            return new BackendResult(false, "实例名不能为空。");
        var result = await RunAsync($"--rename-version \"{oldId}\" \"{newId}\"", TimeSpan.FromSeconds(30), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>修改实例的 Minecraft 版本（加载器种类不变，只支持原版 / Fabric / Quilt）；需要联网下载，旧文件先备份。</summary>
    public async Task<BackendResult> ChangeVersionAsync(string versionId, string newMcVersion,
        string root = "", IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(versionId) || string.IsNullOrWhiteSpace(newMcVersion))
            return new BackendResult(false, "没有指定实例或目标版本。");
        var args = $"--change-version \"{versionId}\" \"{newMcVersion}\"";
        if (progress is not null) args += " --progress";
        var result = progress is null
            ? await RunAsync(args, TimeSpan.FromMinutes(30), gameDir: root)
            : await RunAsync(args, TimeSpan.FromMinutes(30), stage => progress.Report(stage), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>删实例：后端把它移到 %APPDATA%\MeowLauncher\trash，不直接抹盘。</summary>
    public async Task<BackendResult> DeleteVersionAsync(string versionId, string root = "")
    {
        if (string.IsNullOrWhiteSpace(versionId)) return new BackendResult(false, "没有指定实例。");
        var result = await RunAsync($"--delete-version \"{versionId}\"", TimeSpan.FromSeconds(60), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>当前正在跑的游戏进程（pid + 实例 + 启动时间 + 日志路径）。</summary>
    public async Task<RunningResult> GetRunningAsync()
    {
        var result = await RunAsync("--running", TimeSpan.FromSeconds(15));
        if (!result.Success) return new RunningResult(false, result.Error, new List<RunningGame>());
        try
        {
            return JsonSerializer.Deserialize<RunningResult>(result.Output.Trim(), JsonOptions)
                   ?? new RunningResult(false, "正在运行的实例数据为空。", new List<RunningGame>());
        }
        catch (JsonException ex)
        {
            return new RunningResult(false, $"解析运行状态失败：{ex.Message}", new List<RunningGame>());
        }
    }

    /// <summary>结束游戏：传 pid 精确结束某个进程，不传则按实例名结束。</summary>
    public async Task<BackendResult> StopGameAsync(int pid = 0, string versionId = "")
    {
        if (pid <= 0 && string.IsNullOrWhiteSpace(versionId))
            return new BackendResult(false, "没有指定要结束的游戏。");
        var arg = pid > 0 ? pid.ToString() : $"\"{versionId}\"";
        var result = await RunAsync($"--stop {arg}", TimeSpan.FromSeconds(30));
        return ParseActionResult(result);
    }

    /// <summary>启动前检查：客户端 jar、库、资源是否齐。</summary>
    public async Task<VerifyResult> VerifyVersionAsync(string versionId, string root = "")
    {
        if (string.IsNullOrWhiteSpace(versionId))
            return new VerifyResult(false, "没有指定实例。", true, 0, 0, 0, 0, null);
        var result = await RunAsync($"--verify-version \"{versionId}\"", TimeSpan.FromMinutes(2), gameDir: root);
        if (!result.Success)
            return new VerifyResult(false, result.Error, true, 0, 0, 0, 0, null);
        try
        {
            var parsed = JsonSerializer.Deserialize<VerifyResult>(result.Output.Trim(), JsonOptions);
            return parsed ?? new VerifyResult(false, "检查结果为空。", true, 0, 0, 0, 0, null);
        }
        catch (JsonException ex)
        {
            return new VerifyResult(false, $"解析检查结果失败：{ex.Message}", true, 0, 0, 0, 0, null);
        }
    }

    /// <summary>补齐缺失的客户端 jar / 库 / 资源。</summary>
    public async Task<BackendResult> RepairVersionAsync(string versionId,
        IProgress<string>? progress = null, string root = "")
    {
        if (string.IsNullOrWhiteSpace(versionId)) return new BackendResult(false, "没有指定实例。");
        var args = $"--repair-version \"{versionId}\"";
        if (progress is not null) args += " --progress";
        var result = progress is null
            ? await RunAsync(args, TimeSpan.FromMinutes(30), gameDir: root)
            : await RunAsync(args, TimeSpan.FromMinutes(30), stage => progress.Report(stage), gameDir: root);
        return ParseActionResult(result);
    }

    /// <summary>某个实例自己的截图统计（隔离实例读实例目录）；失败返回 null，调用方保留原来的数据。</summary>
    public async Task<ShotsResult?> GetShotsAsync(string versionId)
    {
        var result = await RunAsync($"--shots \"{versionId}\"", TimeSpan.FromSeconds(15));
        if (!result.Success) return null;
        try { return JsonSerializer.Deserialize<ShotsResult>(result.Output.Trim(), JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>--saves：该实例实际使用的 saves 目录里的存档名（隔离实例看 versions/<id>/saves）；失败返回 null。</summary>
    public async Task<List<string>?> GetSaveNamesAsync(string versionId)
    {
        var result = await RunAsync($"--saves \"{versionId}\"", TimeSpan.FromSeconds(25));
        if (!result.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            if (!doc.RootElement.TryGetProperty("saves", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            var names = new List<string>();
            foreach (var s in arr.EnumerateArray())
                if (s.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) names.Add(name);
            return names;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>--mod-project：按文件 sha1 去 Modrinth 查这个 jar 属于哪个项目（只读）。查不到 Ok=false。</summary>
    public async Task<ModProjectLookup> GetModProjectAsync(string versionId, string file)
    {
        var result = await RunAsync($"--mod-project \"{versionId}\" \"{file}\"", TimeSpan.FromSeconds(30));
        var text = (result.Output ?? "").Trim();
        if (text.Length == 0) return new ModProjectLookup(false, result.Error ?? "后台无响应", "", "", "", "");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ModProjectLookup(ok, S("message"), S("projectId"), S("slug"), S("title"), S("iconUrl"));
        }
        catch (JsonException ex) { return new ModProjectLookup(false, ex.Message, "", "", "", ""); }
    }

    /// <summary>--project：按项目 id / slug 取简介、下载量等（只读），用来补全从哈希认出来的项目。</summary>
    public async Task<ProjectInfo> GetProjectInfoAsync(string idOrSlug)
    {
        var result = await RunAsync($"--project \"{idOrSlug}\"", TimeSpan.FromSeconds(25));
        var empty = new ProjectInfo(false, "", 0, 0, "mod", [], []);
        var text = (result.Output ?? "").Trim();
        if (text.Length == 0) return empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True)) return empty;
            string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long N(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            List<string> L(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList() : [];
            var type = S("projectType");
            return new ProjectInfo(true, S("description"), N("downloads"), N("followers"), type.Length > 0 ? type : "mod", L("gameVersions"), L("loaders"));
        }
        catch (JsonException) { return empty; }
    }

    /// <summary>--mem-estimate：按 Mod 数量、加载器和本机内存估一个建议内存（MB，只读）。</summary>
    public async Task<MemEstimate> GetMemEstimateAsync(string versionId, string gameRoot = "")
    {
        var none = new MemEstimate(false, 0, 0, "", false, "");
        var result = await RunAsync($"--mem-estimate \"{versionId}\"", TimeSpan.FromSeconds(25), gameDir: gameRoot);
        var text = (result.Output ?? "").Trim();
        if (text.Length == 0) return none;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True)) return none;
            int I(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var capped = root.TryGetProperty("capped", out var c) && c.ValueKind == JsonValueKind.True;
            return new MemEstimate(true, I("mem"), I("mods"), S("loader"), capped, S("reason"));
        }
        catch (JsonException) { return none; }
    }

    public async Task<PacksResult?> GetPacksAsync(string versionId)
    {
        var result = await RunAsync($"--packs \"{versionId}\"", TimeSpan.FromSeconds(25));
        if (!result.Success)
        {
            return new PacksResult { Ok = false, Dir = result.Error };
        }

        try
        {
            return JsonSerializer.Deserialize<PacksResult>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new PacksResult { Ok = false, Dir = $"解析资源包列表失败：{ex.Message}" };
        }
    }

    private static BackendResult ParseActionResult(RunResult result)
    {
        if (result.Success) return ParseActionResult(result.Output);
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            var parsed = ParseActionResult(result.Output);
            if (parsed.Failure is not null) return parsed;
        }
        return new BackendResult(false, result.Error);
    }

    private static BackendResult ParseActionResult(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : ok ? "已处理。" : "操作失败。";
            var version = root.TryGetProperty("installedVersion", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() ?? ""
                : root.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString() ?? "" : "";
            var needJava = root.TryGetProperty("needJava", out var j) && j.ValueKind == JsonValueKind.Number ? j.GetInt32() : 0;
            FailureInfo? failure = null;
            if (!ok && root.TryGetProperty("errorAt", out var at) && at.ValueKind == JsonValueKind.String
                && at.TryGetDateTimeOffset(out var occurred))
            {
                var code = root.TryGetProperty("errorCode", out var c) ? c.GetString() ?? "" : "";
                var stage = root.TryGetProperty("errorStage", out var s) ? s.GetString() ?? "" : "";
                failure = new FailureInfo(code, occurred, stage);
            }
            var path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var loader = root.TryGetProperty("loader", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
            return new BackendResult(ok, message) { Version = version, NeedJava = needJava, Failure = failure,
                InstancePath = path, Loader = loader };
        }
        catch (Exception ex)
        {
            return new BackendResult(false, $"无法解析后台结果：{ex.Message}");
        }
    }

    /// <summary>
    /// 在线程池线程上跑后台引擎。别在界面线程上直接跑：Process.WaitForExitAsync 开头会同步调用 HasExited，
    /// 里面是对进程句柄的 WaitOne，在 STA（界面）线程上这个等待会泵消息；而这些调用常常就发生在页面的
    /// OnLoaded / OnNavigatedTo 里，XAML 正处在不可重入的状态，被泵进来的消息会让 WinUI 以 0x8000FFFF（灾难性故障）
    /// 直接崩掉（两份崩溃转储的界面线程都停在这里）。onStage 现在会在后台线程回调，调用方用 Progress&lt;T&gt; 回到界面线程。
    /// </summary>
    private Task<RunResult> RunAsync(string arguments, TimeSpan timeout, Action<string>? onStage = null, string gameDir = "",
        string? taskId = null) => Task.Run(() => RunCoreAsync(arguments, timeout, onStage, gameDir, taskId));

    private async Task<RunResult> RunCoreAsync(string arguments, TimeSpan timeout, Action<string>? onStage, string gameDir, string? taskId)
    {
#if DEBUG
        // 仅调试版：MEOW_TEST_SLOW_MS=4000 让每个后端调用先等 4 秒，用来在「加载中」的瞬间截图，检查每个页面有没有加载提示
        if (int.TryParse(Environment.GetEnvironmentVariable("MEOW_TEST_SLOW_MS"), out var slowMs) && slowMs > 0)
            await Task.Delay(slowMs);
#endif
        if (!File.Exists(PythonPath))
        {
            return new RunResult(false, "", Python.Message);
        }

        if (!File.Exists(SidecarPath))
        {
            return new RunResult(false, "", $"找不到 sidecar：{SidecarPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = PythonPath,
            WorkingDirectory = ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(SidecarPath);

        if (!string.IsNullOrWhiteSpace(gameDir))
        {
            psi.Environment["MEOW_MC_DIR"] = gameDir;
        }

        foreach (var kv in NetworkProxy.ChildEnvironment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }
        psi.Environment["MEOW_HTTP_USER_AGENT"] = McimClient.UserAgent;
        psi.Environment["MEOW_MCIM_ORDER"] = McimClient.PreferOfficial ? "official" : "mirror";

        foreach (var arg in SplitArguments(arguments))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new RunResult(false, "", $"无法启动 sidecar：{ex.Message}");
        }

        var operationId = taskId ?? Guid.NewGuid().ToString("N");
        RegisterOperation(operationId, process, arguments);

        var stderr = process.StandardError.ReadToEndAsync();
        var stdoutBuilder = new StringBuilder();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            if (onStage is null)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(cts.Token);
                stdoutBuilder.Append(await stdout);
            }
            else
            {
                // 逐行读取：sidecar 会把阶段输出成 {"stage": "..."}，其余行才是最终结果。
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    var stage = TryReadStage(trimmed);
                    if (stage is not null)
                    {
                        onStage(stage);
                        continue;
                    }
                    stdoutBuilder.AppendLine(trimmed);
                }
                await process.WaitForExitAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            UnregisterOperation(operationId);
            return new RunResult(false, "", "后台引擎响应超时。");
        }

        var output = stdoutBuilder.ToString().Trim();
        var error = (await stderr).Trim();
        UnregisterOperation(operationId);

        if (process.ExitCode != 0)
        {
            return new RunResult(false, output, SidecarError(output, error, process.ExitCode));
        }

        return new RunResult(true, output, error);
    }

    /// <summary>sidecar 的异常走 stdout 的 {"ok":false,"message":...}，stderr 常为空；不能只报退出码。</summary>
    private static string SidecarError(string output, string stderr, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(stderr)) return stderr;
        var text = (output ?? "").Trim();
        if (text.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var value = message.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value!;
                }
            }
            catch (JsonException)
            {
                // 不是 JSON 就原样带回去，至少比「退出码 1」有用。
            }
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        return $"sidecar 退出码 {exitCode}";
    }

    private static void RegisterOperation(string id, Process process, string arguments)
    {
        lock (RunningGate)
        {
            Running[id] = new RunningOperation(process, arguments, DateTime.UtcNow);
        }
    }

    private static void UnregisterOperation(string id)
    {
        lock (RunningGate)
        {
            Running.Remove(id);
        }
    }

    /// <summary>
    /// 取消后端操作：按任务类型挑出对应的 sidecar 进程，实体名能匹配上时优先匹配，
    /// 进程树整棵杀掉（sidecar 自己再派生的 python 子进程一并结束）。返回被杀掉的进程数。
    /// </summary>
    public int CancelOperations(TaskKind kind, string? entity = null, string? taskId = null)
    {
        if (kind == TaskKind.Install)
        {
            RunningOperation? operation;
            lock (RunningGate) operation = taskId is null ? null : Running.GetValueOrDefault(taskId);
            if (operation is null) return 0;
            operation.Process.Kill(entireProcessTree: true);
            return 1;
        }
        string[] flags = kind switch
        {
            TaskKind.Launch => new[] { "--launch" },
            _ => Array.Empty<string>(),
        };
        if (flags.Length == 0) return 0;

        List<KeyValuePair<string, RunningOperation>> targets;
        lock (RunningGate)
        {
            foreach (var dead in Running.Where(pair => pair.Value.Process.HasExited).Select(pair => pair.Key).ToList())
            {
                Running.Remove(dead);
            }

            var candidates = Running
                .Where(pair => flags.Any(flag => pair.Value.Arguments.Contains(flag, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(entity))
            {
                var exact = candidates
                    .Where(pair => pair.Value.Arguments.Contains(entity!, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exact.Count > 0) candidates = exact;
            }

            targets = candidates.OrderByDescending(pair => pair.Value.StartedAt).Take(1).ToList();
            foreach (var pair in targets) Running.Remove(pair.Key);
        }

        var killed = 0;
        foreach (var pair in targets)
        {
            try
            {
                pair.Value.Process.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"取消后端进程失败：{ex.Message}");
            }
        }
        return killed;
    }

    /// <summary>快速自检：Python / sidecar 是否就位，以及最近一次后端错误。不启动进程，页面上秒回。</summary>
    public (bool Ok, string Message) CheckBackend()
    {
        if (!File.Exists(PythonPath)) return (false, $"找不到 Python：{PythonPath}");
        if (!File.Exists(SidecarPath)) return (false, $"找不到后端脚本：{SidecarPath}");
        if (!string.IsNullOrWhiteSpace(LastError)) return (false, LastError);
        return (true, "后端就绪");
    }

    private sealed record RunningOperation(Process Process, string Arguments, DateTime StartedAt);

    private static string? TryReadStage(string line)
    {
        if (!line.StartsWith('{') || !line.Contains("\"stage\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("stage", out var stage) &&
                stage.ValueKind == JsonValueKind.String)
            {
                return stage.GetString();
            }
        }
        catch (JsonException)
        {
            // 不是阶段行，交给调用方当普通输出处理。
        }
        return null;
    }

    private static IEnumerable<string> SplitArguments(string raw)
    {
        var current = new StringBuilder();
        var quoted = false;
        var hasArgument = false;
        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                hasArgument = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (hasArgument)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasArgument = false;
                }
                continue;
            }

            current.Append(ch);
            hasArgument = true;
        }

        if (hasArgument) yield return current.ToString();
    }

    private static string FindProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MEOW_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ml2", "core.py")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        // 发布包里没有仓库根：ml2 随程序放在 Backend\ml2，工作目录用程序目录。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Backend", "ml2", "core.py")))
        {
            return AppContext.BaseDirectory;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>Python 检测结果：找到的解释器；没找到时带上检查过的位置，界面据此提示。</summary>
    public sealed record PythonProbe(bool Found, string Path, string Version, IReadOnlyList<string> Checked)
    {
        public string Message => Found ? "" :
            "未找到可用的 Python（需要 3.9 或更新），后端功能暂时不可用。\n\n已检查：\n"
            + string.Join("\n", Checked.Select(c => "· " + c))
            + "\n\n安装 Python 3.9 以上版本后重新打开即可；也可以设置环境变量 MEOW_PYTHON 指向 python.exe。";
    }

    public PythonProbe Python { get; }

    /// <summary>检测顺序：MEOW_PYTHON → 随程序内嵌 → PATH → 常见安装位置。</summary>
    private static PythonProbe ProbePython()
    {
        var tried = new List<string>();
        if (Environment.GetEnvironmentVariable("MEOW_NO_PYTHON") == "1")
        {
            return new PythonProbe(false, "", "", new[] { "（诊断模式 MEOW_NO_PYTHON=1：已跳过检测）" });
        }

        var cands = new List<(string Label, string Path)>();
        var env = Environment.GetEnvironmentVariable("MEOW_PYTHON");
        if (!string.IsNullOrWhiteSpace(env)) cands.Add(("MEOW_PYTHON", env.Trim()));
        else tried.Add("环境变量 MEOW_PYTHON：未设置");

        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { @"Backend\python\python.exe", @"python\python.exe", @"runtime\python\python.exe" })
        {
            cands.Add(("内嵌", Path.Combine(baseDir, rel)));
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var pathHits = 0;
        foreach (var dir in pathDirs)
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), "python.exe");
                if (File.Exists(p)) { pathHits++; cands.Add(("PATH", p)); }
            }
            catch { }
        }
        if (pathHits == 0) tried.Add($"PATH：{pathDirs.Length} 个目录里都没有 python.exe");

        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"C:\",
        };
        var common = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.EnumerateDirectories(root, "Python3*").OrderByDescending(x => x))
                {
                    cands.Add(("常见位置", Path.Combine(d, "python.exe")));
                }
            }
            catch { }
        }
        if (!cands.Any(c => c.Label == "常见位置"))
        {
            tried.Add(@"常见安装位置：%LOCALAPPDATA%\Programs\Python、Program Files、C:\ 下没有 Python3* 目录");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, path) in cands)
        {
            if (!seen.Add(path)) continue;
            try
            {
                if (!File.Exists(path)) { if (label is "MEOW_PYTHON") tried.Add($"{label}：{path}（文件不存在）"); continue; }
                if (new FileInfo(path).Length == 0)
                {
                    tried.Add($"{label}：{path}（Microsoft Store 占位程序，不是真正的 Python）");
                    continue;
                }
                // 冷启动时每多起一个进程都可能要几秒（杀毒软件扫描、磁盘缓存冷），而这一步发生在窗口出现之前：
                // 内嵌 Python 是我们自己随包放的，只要文件在就信它（版本从 python3NN.dll 的文件名读）；
                // 系统 Python 的检测结果按「路径 + 修改时间 + 大小」缓存下来，下次不用再起进程验证。
                var ver = label == "内嵌" ? EmbeddedVersion(path) : null;
                ver ??= CachedVersion(path);
                if (ver is null)
                {
                    ver = TryRunPython(path);
                    if (ver is not null) SaveCachedVersion(path, ver);
                }
                if (ver is null) { tried.Add($"{label}：{path}（无法运行）"); continue; }
                if (Version.TryParse(ver, out var v) && (v.Major < 3 || (v.Major == 3 && v.Minor < 9)))
                {
                    tried.Add($"{label}：{path}（版本 {ver} 过低，需要 3.9 或更新）");
                    continue;
                }
                return new PythonProbe(true, path, ver, tried);
            }
            catch (Exception ex) { tried.Add($"{label}：{path}（{ex.Message}）"); }
        }

        return new PythonProbe(false, "", "", tried);
    }

    /// <summary>内嵌 Python：从同目录的 python3NN.dll 文件名读出版本（例如 python315.dll → 3.15），读不到返回 null（再走真正运行验证）。</summary>
    private static string? EmbeddedVersion(string exe)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(exe);
            if (dir is null) return null;
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "python3*.dll"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(f);        // python315
                var digits = name["python".Length..];                                // 315
                if (digits.Length >= 2 && digits.All(char.IsDigit))
                    return $"{digits[0]}.{digits[1..]}";
            }
        }
        catch { }
        return null;
    }

    private static string PythonCacheFile =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher", "python-cache.json");

    private static string PythonKey(string exe)
    {
        var fi = new System.IO.FileInfo(exe);
        return $"{exe}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
    }

    private static string? CachedVersion(string exe)
    {
        try
        {
            if (!System.IO.File.Exists(PythonCacheFile)) return null;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(PythonCacheFile));
            var r = doc.RootElement;
            return r.GetProperty("key").GetString() == PythonKey(exe) ? r.GetProperty("version").GetString() : null;
        }
        catch { return null; }
    }

    private static void SaveCachedVersion(string exe, string version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PythonCacheFile)!);
            System.IO.File.WriteAllText(PythonCacheFile, JsonSerializer.Serialize(new { key = PythonKey(exe), version }));
        }
        catch { }
    }

    private static string? TryRunPython(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys;print('%d.%d' % sys.version_info[:2])");
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? p.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch { return null; }
    }
    private sealed record RunResult(bool Success, string Output, string Error);
}

public sealed record OnlineModInstallPlan(bool Ok, string? Message, string FileId, string Confirmation,
    string[]? Dependencies);

public sealed record BackendResult(bool Ok, string Message)
{
    public string InstancePath { get; init; } = "";
    public string Loader { get; init; } = "";
    public FailureInfo? Failure { get; init; }
    /// <summary>后台结果里的 version / installedVersion（安装版本、整合包时是新实例 id）；没有就是空串。</summary>
    public string Version { get; init; } = "";
    /// <summary>&gt;0：启动失败的原因是缺这个大版本的 Java，界面可引导下载。</summary>
    public int NeedJava { get; init; }
}
public sealed record ModUpdate(string File, string Name, string Current, string Latest,
    string Download, string Filename, string Source);
public sealed record MemEstimate(bool Ok, int Mem, int Mods, string Loader, bool Capped, string Reason);
public sealed record ApiRelease(string Id, string Version)
{
    public override string ToString() => Version;
}

public sealed record ModProjectLookup(bool Ok, string Message, string ProjectId, string Slug, string Title, string IconUrl);
public sealed record ProjectInfo(bool Ok, string Description, long Downloads, long Followers, string ProjectType,
    List<string> GameVersions, List<string> Loaders);
public sealed record ModUpdatesResult(bool Ok, string Message, int Checked, List<ModUpdate> Updates);
public sealed record LoaderKindInfo(bool Ok, string Message, IReadOnlyList<string> Versions, string Recommended);
public sealed record LoaderSupportResult(bool Ok, string Message, IReadOnlyDictionary<string, LoaderKindInfo> Kinds);
public sealed record VerifySample(string Kind, string Name);
public sealed record VerifyResult(bool Ok, string Message, bool Clean, int Missing,
    int MissingLibraries, int MissingAssets, int MissingClient, List<VerifySample>? Samples);
public sealed class RunningGame
{
    public int Pid { get; set; }
    public string Version { get; set; } = "";
    public long StartedAt { get; set; }
    public string Log { get; set; } = "";
}
public sealed record RunningResult(bool Ok, string Message, List<RunningGame> Running);
public sealed record LaunchDefaultsResult(bool Ok, int Mem, bool MemAuto, string Jvm, string Message);
public sealed record GeneralSettings(bool Ok, string DownloadSource, string Mirror, int DlThreads,
    string CfApiKey, string JavaPath, string Message);
public sealed record InstanceLaunchSettings(bool Ok, int Mem, bool UseGlobal, bool MemAuto,
    string JavaPath, string Jvm, string Message, string Isolation = "follow", bool Isolated = false,
    string IsolationDefault = "auto")
{
    public int OverrideMem { get; init; } = 4096;
    public bool OverrideMemAuto { get; init; }
    public int GlobalMem { get; init; } = 4096;
    public bool GlobalMemAuto { get; init; }
    public string GlobalJvm { get; init; } = "";
    public bool JvmAuto { get; init; } = true;
    public bool GlobalJvmAuto { get; init; } = true;
}

public sealed record MicrosoftStartResult(
    bool Ok, string Message, string UserCode, string DeviceCode,
    string VerificationUri, int IntervalSeconds, int ExpiresInSeconds);

public sealed record MicrosoftPollResult(bool Ok, bool Pending, string Message, string Name);

public sealed record OnlineFileInfo(
    string Id, string Name, string Version, string Game, string Loader, string FileName,
    string Type = "release", string Date = "", long Size = 0, int Required = 0)
{
    /// <summary>发布类型：release/beta/alpha，用来在文件行上给色标。</summary>
    public string TypeText => Type switch
    {
        "alpha" => "Alpha",
        "beta" => "Beta",
        _ => "正式版",
    };

    public string SizeText => Size <= 0
        ? ""
        : Size >= 1024 * 1024
            ? $"{Size / 1024d / 1024d:0.#} MB"
            : $"{Size / 1024d:0} KB";

    /// <summary>游戏版本太多时会撑爆一行：只显示前 3 个，其余折成 +N。</summary>
    public string GameShort
    {
        get
        {
            var parts = (Game ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length <= 3) return string.Join(", ", parts);
            return string.Join(", ", parts.Take(3)) + $" +{parts.Length - 3}";
        }
    }

    /// <summary>文件行第二行的依赖提示，没有前置时为空。</summary>
    public string RequiredText => Required > 0 ? $"需要 {Required} 个前置" : "";

    /// <summary>右侧那一列：CurseForge 的 Version 就是文件名，去掉扩展名更好读。</summary>
    public string RightText
    {
        get
        {
            // 右侧只放「这个文件支持的游戏版本」，短且不和文件名里的版本号重复；
            // version_number 已经出现在文件名里，再放一次会被对话框宽度裁掉（只剩「0.」）。
            var first = (GameShort ?? "").Split(',')[0].Trim();
            if (first.Length > 0)
            {
                // 快照版本号形如 26.4-snapshot-7，整串放右侧会被裁掉，折成「快照」更好读。
                if (first.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                    || first.Contains("pre", StringComparison.OrdinalIgnoreCase))
                    return "快照";
                return first;
            }
            return string.IsNullOrWhiteSpace(Version) ? "" : Version;
        }
    }

    /// <summary>发布类型色标：正式=品牌绿、测试=橙、其他=灰。</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush TypeBrush => Type switch
    {
        "release" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0x2F, 0xA3, 0x5A)),
        "beta" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0xC9, 0x8B, 0x00)),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0x80, 0x80, 0x80)),
    };

    public string Display => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(Name) ? "未命名文件" : Name,
        string.IsNullOrWhiteSpace(Version) ? "" : Version,
        TypeText,
        string.IsNullOrWhiteSpace(GameShort) ? "" : GameShort,
        string.IsNullOrWhiteSpace(Loader) ? "" : Loader,
        string.IsNullOrWhiteSpace(Date) ? "" : Date,
        SizeText,
        Required > 0 ? $"需要 {Required} 个前置" : "",
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed class OnlineFilesResult
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public List<OnlineFileInfo> Files { get; set; } = new();
    public string Message { get; set; } = "";
}
