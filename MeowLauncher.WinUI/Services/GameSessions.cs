using Microsoft.UI.Dispatching;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 正在运行的游戏进程：4 秒轮询一次 sidecar 的 --running 台账，
/// 让首页、版本页的按钮在「启动」和「结束游戏」之间跟着进程状态走。
/// </summary>
public static class GameSessions
{
    private static readonly object Gate = new();
    private static List<RunningGame> _running = new();
    private static DispatcherQueue? _queue;
    private static DispatcherQueueTimer? _timer;
    private static bool _refreshing;

    /// <summary>运行集合发生变化时在 UI 线程触发。</summary>
    public static event Action? Changed;

    public static IReadOnlyList<RunningGame> Running
    {
        get
        {
            lock (Gate) return _running.ToList();
        }
    }

    public static RunningGame? Find(string versionId, string root = "")
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;
        lock (Gate)
        {
            return _running.FirstOrDefault(row =>
                string.Equals(row.Version, versionId, StringComparison.OrdinalIgnoreCase)
                && (root.Length == 0 || row.Root.Length == 0 || string.Equals(GameDirectoryResolver.Normalize(row.Root), GameDirectoryResolver.Normalize(root), StringComparison.OrdinalIgnoreCase)));
        }
    }

    public static bool IsRunning(string versionId, string root = "") => Find(versionId, root) is not null;

    public static void Initialize(DispatcherQueue queue)
    {
        _queue = queue;
        if (_timer is not null) return;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(4);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public static async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var result = await App.Backend.GetRunningAsync();
            if (!result.Ok) return;
            var next = result.Running ?? new List<RunningGame>();
            bool changed;
            lock (Gate)
            {
                changed = !SameList(_running, next);
                _running = next;
            }

            if (!changed) return;
            if (_queue is not null) _queue.TryEnqueue(() => Changed?.Invoke());
            else Changed?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GameSessions refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static bool SameList(List<RunningGame> a, List<RunningGame> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Pid != b[i].Pid || !string.Equals(a[i].Version, b[i].Version, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
