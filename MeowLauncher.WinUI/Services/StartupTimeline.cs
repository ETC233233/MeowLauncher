using System.Diagnostics;
using System.Text;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动计时：记录「进程启动 → 各个阶段」的毫秒数，写到 %LOCALAPPDATA%\MeowLauncher\startup.log（只留最近一次启动）。
/// 用来在真实的冷启动环境里拿数字（哪一步慢），再决定优化哪里。不含任何个人数据，失败静默。
/// </summary>
public static class StartupTimeline
{
    private static readonly object Gate = new();
    private static readonly List<(string Label, double Ms)> Marks = new();
    private static readonly DateTime ProcessStart = SafeStart();
    private static bool _flushed;

    private static DateTime SafeStart()
    {
        try { return Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; }
    }

    /// <summary>记一个阶段。第一次调用之后的所有阶段都按「距进程启动多少毫秒」记录。</summary>
    public static void Mark(string label)
    {
        lock (Gate)
        {
            if (_flushed) return;
            Marks.Add((label, (DateTime.Now - ProcessStart).TotalMilliseconds));
        }
    }

    /// <summary>写盘（首页数据到齐后调用一次）。</summary>
    public static void Flush()
    {
        try
        {
            lock (Gate)
            {
                if (_flushed) return;
                _flushed = true;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowLauncher");
                Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine($"启动时间 {ProcessStart:yyyy-MM-dd HH:mm:ss}  版本 {BuildInfo.Current.Display}");
                double prev = 0;
                foreach (var (label, ms) in Marks)
                {
                    sb.AppendLine($"{ms,8:0} ms  (+{ms - prev,6:0})  {label}");
                    prev = ms;
                }
                File.WriteAllText(Path.Combine(dir, "startup.log"), sb.ToString());
            }
        }
        catch { }
    }
}
