using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 跟踪「我启动的实例」什么时候退出：退出时把任务标成已完成、把启动器从任务栏还原，
/// 并在任务与通知里留一条。启动后是否最小化由 AppPreferences.AfterLaunch 决定。
/// </summary>
public static class LaunchWatcher
{
    private sealed record Tracked(string Version, TaskEntry Task, int Pid, DateTime StartedAt, string Root);

    private static readonly List<Tracked> Items = new();
    private static bool _hooked;

    /// <summary>启动成功后调用：登记这个实例，等它退出。</summary>
    public static void Track(string version, TaskEntry task, int pid, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(version)) return;

        var startedAt = DateTime.Now;
        root ??= "";

        // 调用方常常拿不到 pid（java 是 sidecar 的孙进程，启动返回时还没起来）；
        // 这时从 GameSessions 的运行台账里按实例名补一次，仍补不到才退回轮询。
        if (pid <= 0)
        {
            try { pid = GameSessions.Find(version, root)?.Pid ?? 0; } catch { }
        }

        lock (Items)
        {
            Items.RemoveAll(t => string.Equals(t.Version, version, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Root, root, StringComparison.OrdinalIgnoreCase));
            Items.Add(new Tracked(version, task, pid, startedAt, root));
        }
        Hook();
        if (pid > 0)
        {
            MarkSeen(task);
            WatchProcess(version, task, pid, startedAt, root);
        }
        else
        {
            _ = FindPidThenWatchAsync(version, task, startedAt, root);
        }
    }

    /// <summary>已经确认出现在运行台账里的任务；轮询兜底只对这些任务判「已退出」，避免台账还没刷新就误收尾。</summary>
    private static readonly HashSet<TaskEntry> Seen = new();

    private static void MarkSeen(TaskEntry task)
    {
        lock (Items) Seen.Add(task);
    }

    /// <summary>
    /// 启动返回时 java 往往还没进运行台账（台账 4 秒刷新一次）：每秒主动刷新找 pid，找到就盯句柄；
    /// 20 秒都没出现（秒退/起不来）就直接收尾，否则任务会永远停在「游戏运行中」。
    /// </summary>
    private static async Task FindPidThenWatchAsync(string version, TaskEntry task, DateTime startedAt, string root)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            bool tracked;
            lock (Items) tracked = Items.Any(t => ReferenceEquals(t.Task, task));
            if (!tracked) return;
            try { await GameSessions.RefreshAsync(); } catch { }
            var game = GameSessions.Find(version, root);
            if (game is not null && game.Pid > 0)
            {
                MarkSeen(task);
                WatchProcess(version, task, game.Pid, startedAt, root);
                return;
            }
        }
        Finish(version, task, null, startedAt, root);
    }

    /// <summary>
    /// 拿得到 pid 时直接盯进程句柄。
    /// 注意：java 是 sidecar 的孙进程，Process.ExitCode 经常读不到，
    /// 所以退回 OpenProcess + GetExitCodeProcess；仍读不到就按「退出码未知」记完成。
    /// </summary>
    private static void WatchProcess(string version, TaskEntry task, int pid, DateTime startedAt, string root)
    {
        if (pid <= 0) return;
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                int? code = null;
                try { code = proc.ExitCode; } catch { }
                code ??= TryGetExitCode(pid);
                Finish(version, task, code, startedAt, root);
            };
        }
        catch
        {
            // 进程已经退出或拿不到句柄：让 GameSessions 的轮询兜底。
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>进程句柄读退出码（含 STILL_ACTIVE 判定）；拿不到返回 null。</summary>
    private static int? TryGetExitCode(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!GetExitCodeProcess(handle, out var code)) return null;
            return code == 259 ? null : unchecked((int)code); // 259 = STILL_ACTIVE
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void Finish(string version, TaskEntry task, int? exitCode, DateTime startedAt, string root)
    {
        lock (Items)
        {
            if (Items.RemoveAll(t => ReferenceEquals(t.Task, task)) == 0) return;
            Seen.Remove(task);
        }

        // 失败判定：①出现新的崩溃报告；②启动后很短时间内就退出且退出码非 0 / 未知
        //（JVM 参数非法、缺依赖等「秒退」，不会产生 crash-reports）。
        // 玩了一阵后用户关窗得到非 0 码不算失败。
        var crashed = HasCrashReport(version, startedAt, root);
        var elapsed = DateTime.Now - startedAt;
        var quickFail = elapsed < TimeSpan.FromSeconds(30) && (exitCode is null || exitCode != 0);
        var detail = exitCode is null
            ? "游戏已退出（退出码未知）"
            : $"游戏已退出（代码 {exitCode}）";
        if (!crashed && quickFail)
            detail = (exitCode is null ? "游戏启动后立即退出（退出码未知）" : $"游戏启动后立即退出（代码 {exitCode}）") + "，请查看日志";

        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (crashed) TaskCenter.Fail(task, detail + "，检测到崩溃报告");
            else if (quickFail) TaskCenter.Fail(task, detail);
            else TaskCenter.Complete(task, detail);
            App.MainWindow?.RestoreFromGame();
        });
    }

    /// <summary>退出后检查 crash-reports 与 hs_err_pid*.log 是否有比启动更新的文件。</summary>
    private static bool HasCrashReport(string version, DateTime startedAt, string rootHint)
    {
        try
        {
            foreach (var root in CandidateRoots(rootHint))
            {
                var cr = Path.Combine(root, "crash-reports");
                if (Directory.Exists(cr))
                {
                    foreach (var f in Directory.EnumerateFiles(cr))
                    {
                        try { if (File.GetLastWriteTime(f) > startedAt) return true; } catch { }
                    }
                }

                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "hs_err_pid*.log"))
                    {
                        try { if (File.GetLastWriteTime(f) > startedAt) return true; } catch { }
                    }
                }
                catch { }

                var verDir = Path.Combine(root, "versions", version);
                if (Directory.Exists(verDir))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(verDir, "hs_err_pid*.log"))
                        {
                            try { if (File.GetLastWriteTime(f) > startedAt) return true; } catch { }
                        }
                    }
                    catch { }

                    // 隔离实例的 run_dir 就是 versions/<id>，崩溃报告落在它自己的 crash-reports 里。
                    var verCr = Path.Combine(verDir, "crash-reports");
                    if (Directory.Exists(verCr))
                    {
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(verCr))
                            {
                                try { if (File.GetLastWriteTime(f) > startedAt) return true; } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<string> CandidateRoots(string? rootHint)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? home = null;
        try { home = AppPreferencesStore.Load().HomeSelectedRoot; } catch { }
        foreach (var raw in new[] { rootHint, home, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft") })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var norm = Services.GameDirectoryResolver.Normalize(raw);
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
        }
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        GameSessions.Changed += OnChanged;
    }

    private static void OnChanged()
    {
        List<Tracked> exited;
        lock (Items)
        {
            if (Items.Count == 0) return;
            exited = Items.Where(t => Seen.Contains(t.Task) && !GameSessions.IsRunning(t.Version, t.Root)).ToList();
        }

        if (exited.Count == 0) return;
        // 轮询兜底也走 Finish：崩溃报告照旧检查，文案统一「游戏已退出」。
        foreach (var t in exited) Finish(t.Version, t.Task, null, t.StartedAt, t.Root);
    }
}
