using System.Diagnostics;
using System.Text.Json;
using MeowLauncher.WinUI.Services;

if (args.Length != 1) return 2;
var planPath = Path.GetFullPath(args[0]);
var work = Path.GetDirectoryName(planPath)!;
var status = Path.Combine(work, "result.json");
PortableUpdatePlan? plan = null;
Process? child = null;
var parentExited = false;
try
{
    plan = JsonSerializer.Deserialize<PortableUpdatePlan>(File.ReadAllText(planPath)) ?? throw new InvalidDataException("无效更新计划。");
    var payload = ComponentManifest.VerifyEnvelope(File.ReadAllText(plan.Envelope)) ?? throw new InvalidDataException("更新清单验签失败或已过期。");
    var (stable, beta) = UpdateLogic.ParseLauncher(payload);
    var release = plan.Channel == "beta" ? beta : stable;
    if (release?.Version != plan.Version || release.Package is null) throw new InvalidDataException("更新包不在签名清单中。");
    await PortableUpdate.VerifyAsync(plan.Archive, release.Package);
    using (var parent = Process.GetProcessById(plan.ParentPid))
    {
        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted
            || !string.Equals(Path.GetDirectoryName(parent.MainModule!.FileName), Path.TrimEndingDirectorySeparator(plan.Target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新目标与启动器进程不符。");
        // Validate/extract before announcing readiness, so the launcher can stay open on failure.
        var next = PortableUpdate.Extract(plan.Archive, Path.Combine(work, "expanded"), plan.Version);
        if (File.Exists(Path.Combine(work, "abort"))) throw new OperationCanceledException("更新已取消。");
        File.WriteAllText(Path.Combine(work, "ready"), plan.Token);
        for (var i = 0; !parent.HasExited; i++)
        {
            if (i >= 600 || File.Exists(Path.Combine(work, "abort"))) throw new OperationCanceledException("启动器未退出，更新已取消。");
            await Task.Delay(100);
        }
        parentExited = true;
        // Sidecars exit normally; never terminate a running game or unrelated Python process.
        await Task.Delay(1500);
        var backup = Path.Combine(plan.Target, "_archive", "updates", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + plan.Token);
        async Task<bool> Health()
        {
            var start = new ProcessStartInfo(Path.Combine(plan.Target, "MeowLauncher.WinUI.exe")) { UseShellExecute = false, WorkingDirectory = plan.Target };
            start.Environment["MEOW_UPDATE_HEALTH_FILE"] = Path.Combine(work, "healthy");
            start.Environment["MEOW_UPDATE_HEALTH_TOKEN"] = plan.Token;
            start.Environment["MEOW_UPDATE_EXPECTED_VERSION"] = plan.Version;
            child = Process.Start(start) ?? throw new IOException("无法启动新版。");
            var healthy = false;
            try
            {
                for (var i = 0; i < 120; i++)
                {
                    await Task.Delay(1000);
                    if (File.Exists(Path.Combine(work, "healthy")) && File.ReadAllText(Path.Combine(work, "healthy")) == plan.Token)
                    {
                        await Task.Delay(5000);
                        healthy = !child.HasExited;
                        return healthy;
                    }
                    if (child.HasExited) return false;
                }
                return false;
            }
            finally
            {
                if (!healthy && !child.HasExited)
                { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        await PortableUpdate.ApplyAsync(plan.Target, Path.Combine(work, "expanded"), backup, next, Health);
        File.WriteAllText(status, JsonSerializer.Serialize(new { ok = true, version = plan.Version, backup }));
    }
    return 0;
}
catch (Exception ex)
{
    File.WriteAllText(status, JsonSerializer.Serialize(new { ok = false, message = ex.Message }));
    // ApplyAsync has restored the old files. Show its result on the next ordinary launch.
    if (plan is not null && parentExited)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(plan.Target, "MeowLauncher.WinUI.exe")) { WorkingDirectory = plan.Target, UseShellExecute = true }); } catch { }
    }
    return 1;
}
