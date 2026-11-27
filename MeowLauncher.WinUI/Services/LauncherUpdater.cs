using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace MeowLauncher.WinUI.Services;

/// <summary>Portable updates: verified signed metadata -> common downloader -> separate transactional updater.</summary>
public static class LauncherUpdater
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool Busy => Gate.CurrentCount == 0;
    public static string Message { get; private set; } = "";
    public static LauncherRelease? Prepared { get; private set; }
    private static string _archive = "";
    public static event Action? Changed;
    public static bool Supported => PortableUpdate.SupportsInPlaceUpdate(AppContext.BaseDirectory);
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher", "updates");

    private static void State(string text) { Message = text; Changed?.Invoke(); }

    public static async Task PrepareAsync(LauncherRelease release)
    {
        if (!Supported || release.Package is null || !await Gate.WaitAsync(0)) return;
        var task = TaskCenter.Add($"下载启动器更新 {release.Version}", "正在获取签名清单", "settings");
        TaskCenter.BuildSteps(task, ["下载更新包", "校验更新包"]);
        try
        {
            Prepared = null;
            State("正在下载更新…");
            Directory.CreateDirectory(Root);
            var archive = Path.Combine(Root, release.Package.Sha256 + ".zip");
            var cached = false;
            if (File.Exists(archive))
            {
                try { await PortableUpdate.VerifyAsync(archive, release.Package); cached = true; }
                catch { File.Move(archive, archive + ".rejected-" + Guid.NewGuid().ToString("N")); }
            }
            if (!cached)
            {
                var progress = new Progress<string>(line =>
                {
                    TaskCenter.Report(task, line);
                    if (line.StartsWith("@progress|"))
                    {
                        var parts = line.Split('|');
                        if (double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                            TaskCenter.Progress(task, pct, "正在下载启动器更新");
                    }
                });
                var result = await App.Backend.DownloadLauncherAsync(release.Package, archive, progress, task.Id);
                if (!result.Ok) throw new IOException(result.Message);
            }
            TaskCenter.CancellationFor(task.Id).ThrowIfCancellationRequested();
            State("正在校验更新包…");
            await PortableUpdate.VerifyAsync(archive, release.Package, TaskCenter.CancellationFor(task.Id));
            _archive = archive;
            Prepared = release;
            TaskCenter.Complete(task, "更新已准备好，可重启安装");
            State($"{release.Version} 已准备好，重启后安装。");
        }
        catch (Exception ex)
        {
            TaskCenter.Fail(task, ex.Message);
            State("下载更新失败：" + ex.Message);
        }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    public static async Task InstallAsync()
    {
        if (Prepared?.Package is not { } package || !Supported || !await Gate.WaitAsync(0)) return;
        try
        {
            if (TaskCenter.ActiveCount > 0 || GameSessions.Running.Count > 0 || App.Backend.HasRunningOperations)
                throw new IOException("请等待当前任务完成后再重启安装。");
            State("正在准备重启…");
            // Refresh the short-lived signature; cached ZIPs are usable only if still offered with the same hash.
            var (payload, error) = await SignedConfig.FetchAsync(force: true);
            if (payload is null) throw new IOException(error);
            var (stable, beta) = UpdateLogic.ParseLauncher(payload);
            var release = new[] { stable, beta }.FirstOrDefault(x => x?.Version == Prepared.Version && x.Package?.Sha256 == package.Sha256);
            if (release is null || SignedConfig.VerifiedEnvelope is null) throw new IOException("更新清单已变化，请重新检查更新。");
            await PortableUpdate.VerifyAsync(_archive, package);
            // Check permissions while the launcher can still report an error; no automatic elevation.
            var probe = Path.Combine(AppContext.BaseDirectory, "MeowLauncher.update-write-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(probe, "update permission check"); }
            catch (UnauthorizedAccessException)
            {
                throw new IOException("当前目录不可写。请从发行页下载对应版本，或将便携版放在有写入权限的目录。");
            }
            var work = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            File.Move(probe, Path.Combine(work, "write-check"));
            var helper = Path.Combine(work, "helper");
            Directory.CreateDirectory(helper);
            // Copy the runtime beside the helper, so none of the installed program files remain locked.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                var name = Path.GetFileName(file);
                if (Path.GetExtension(name).ToLowerInvariant() is ".dll" or ".json" or ".dat" or ".bin"
                    || name == PortableUpdate.UpdaterName) File.Copy(file, Path.Combine(helper, name));
            }
            var envelope = Path.Combine(work, "envelope.json");
            await File.WriteAllTextAsync(envelope, SignedConfig.VerifiedEnvelope);
            using var current = Process.GetCurrentProcess();
            var plan = new PortableUpdatePlan(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), _archive, envelope,
                release.Version, release == beta ? "beta" : "stable", current.Id, current.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"));
            var planPath = Path.Combine(work, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
            var start = new ProcessStartInfo(Path.Combine(helper, PortableUpdate.UpdaterName))
            { WorkingDirectory = helper, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(planPath);
            using var updater = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
            for (var i = 0; i < 300; i++)
            {
                await Task.Delay(100);
                if (File.Exists(Path.Combine(work, "ready")))
                {
                    State("正在重启安装更新…");
                    Application.Current.Exit();
                    return;
                }
                if (updater.HasExited) throw new IOException(File.Exists(Path.Combine(work, "result.json"))
                    ? File.ReadAllText(Path.Combine(work, "result.json")) : "更新程序未能启动。");
            }
            await File.WriteAllTextAsync(Path.Combine(work, "abort"), plan.Token);
            throw new IOException("更新程序准备超时，启动器保持运行。");
        }
        catch (Exception ex) { State("安装更新失败：" + ex.Message); }
        finally { Gate.Release(); Changed?.Invoke(); }
    }

    public static void ReportHealthy()
    {
        var file = Environment.GetEnvironmentVariable("MEOW_UPDATE_HEALTH_FILE");
        var token = Environment.GetEnvironmentVariable("MEOW_UPDATE_HEALTH_TOKEN");
        var expected = Environment.GetEnvironmentVariable("MEOW_UPDATE_EXPECTED_VERSION");
        if (file is null || token is null || expected != BuildInfo.Current.Display) return;
        try
        {
            var full = Path.GetFullPath(file);
            if (full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(full, token);
        }
        catch { }
    }

    public static void ReportPreviousResult()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root).OrderByDescending(Directory.GetCreationTimeUtc).Take(10))
            {
                var result = Path.Combine(dir, "result.json");
                if (!File.Exists(result) || File.Exists(Path.Combine(dir, "reported"))) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(result));
                var ok = doc.RootElement.GetProperty("ok").GetBoolean();
                if (!ok) TaskCenter.PostMessage("启动器更新未完成", doc.RootElement.GetProperty("message").GetString() ?? "请重新检查更新。", "更新");
                File.WriteAllText(Path.Combine(dir, "reported"), DateTime.UtcNow.ToString("O"));
            }
        }
        catch { }
    }
}
