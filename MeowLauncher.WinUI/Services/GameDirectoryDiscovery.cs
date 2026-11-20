using System.Diagnostics;

namespace MeowLauncher.WinUI.Services;

/// <summary>有边界的本地发现：常见位置与启动器目录，不递归遍历整块磁盘。</summary>
public static class GameDirectoryDiscovery
{
    public static Task<List<string>> DiscoverAsync(AppPreferences preferences) => Task.Run(() =>
    {
        var found = new HashSet<string>(GameDirectoryResolver.Resolve(preferences), StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 常见游戏目录名，用来发现用户已有的 .minecraft，避免重复下载资源。
        string[] launchers = ["pcl", "bakaxl", "plain craft launcher", "minecraft", "hmcl", "meowlauncher", "prism", "multimc"];
        void Consider(string dir)
        {
            try
            {
                if (Directory.Exists(Path.Combine(dir, "versions"))) found.Add(Path.GetFullPath(dir));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void Scan(string dir, int depth, bool launcher)
        {
            if (watch.ElapsedMilliseconds > 2500 || depth > 3 || !visited.Add(dir)) return;
            Consider(Path.Combine(dir, ".minecraft"));
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir).Take(300))
                {
                    var name = Path.GetFileName(child);
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    if (name.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase)) { Consider(child); continue; }
                    var match = launchers.Any(key => name.Contains(key, StringComparison.OrdinalIgnoreCase));
                    var common = name.Equals("Games", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("游戏", StringComparison.OrdinalIgnoreCase);
                    if (launcher || match || common) Scan(child, depth + 1, launcher || match);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        foreach (var dir in new[] { AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
            Scan(dir, 0, false);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                    Scan(drive.RootDirectory.FullName, 0, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.ToList();
    });
}
