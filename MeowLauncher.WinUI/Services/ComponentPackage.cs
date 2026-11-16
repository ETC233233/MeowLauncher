using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 组件包的解压与逐文件核对。不依赖界面和 App，方便单独测试（tools/online-component/test）。
/// 这些文件之后会被提权代理执行，所以任何一点对不上（路径越界、缺文件、多文件、哈希不对）都整包拒绝。
/// </summary>
public static class ComponentPackage
{
    private static readonly System.Text.RegularExpressions.Regex RuntimeFileName =
        new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,100}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>解压到 staging，并和包里的 component.json 逐个文件核对；有多余文件、缺文件、哈希不对都算失败。返回错误（空串=成功）。</summary>
    internal static string ExtractAndVerify(string zipPath, string staging, string version, int apiLevel, string? runtimeSourceDir = null)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(staging);
        long total = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains("../") || name.Contains(':')) return "组件包里有不安全的路径，已拒绝。";
                var dest = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "组件包里有不安全的路径，已拒绝。";
                total += entry.Length;
                if (total > 600L * 1024 * 1024) return "组件包解压后过大，已拒绝。";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: false);
            }
        }

        var manifestPath = Path.Combine(staging, "component.json");
        if (!File.Exists(manifestPath)) return "组件包缺少 component.json。";
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var r = doc.RootElement;
        if (r.GetProperty("version").GetString() != version || r.GetProperty("apiLevel").GetInt32() != apiLevel)
            return "组件包的版本和清单不一致，已拒绝。";

        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in r.GetProperty("files").EnumerateArray())
            listed[(f.GetProperty("path").GetString() ?? "").Replace('\\', '/')] = (f.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();

        foreach (var (rel, want) in listed)
        {
            var path = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return $"组件包缺少文件：{rel}";
            if (HashFile(path) != want) return $"组件包里的文件校验失败：{rel}";
        }
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (rel.Equals("component.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!listed.ContainsKey(rel)) return $"组件包里有清单之外的文件：{rel}";
        }

        // 包里不带 .NET 运行时：清单里的 runtimeFromLauncher 是文件名，从启动器自己的目录复制过来（本地复制，不联网）。
        // 启动器目录是我们自己安装的，是这条信任链的起点；文件名必须是纯文件名，不能和包里的文件重名。
        if (r.TryGetProperty("runtimeFromLauncher", out var rt) && rt.ValueKind == JsonValueKind.Array)
        {
            var src = runtimeSourceDir ?? AppContext.BaseDirectory;
            foreach (var n in rt.EnumerateArray())
            {
                var name = n.GetString() ?? "";
                if (!RuntimeFileName.IsMatch(name) || listed.ContainsKey(name)) return $"组件包的运行时清单里有不合法的文件名：{name}";
                var from = LocateRuntimeFile(name, src);
                if (from is null) return $"找不到 .NET 运行时文件 {name}：请先安装 .NET 8 桌面运行时，或重新安装启动器。";
                File.Copy(from, Path.Combine(staging, name), overwrite: false);
            }
        }
        return "";
    }

    /// <summary>
    /// 启动器改成框架依赖发布后，自身目录里不再带 .NET 运行时；运行时文件按以下顺序找：
    /// ①启动器目录（老的自包含包仍然可用）②本机安装的 Microsoft.NETCore.App / Microsoft.WindowsDesktop.App。
    /// mscordaccore 的文件名带补丁号，按同前缀取唯一一个。
    /// </summary>
    private static string? LocateRuntimeFile(string name, string baseDir)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(mscordaccore_amd64_amd64_)[0-9.]+\.dll$");
        foreach (var dir in RuntimeSearchDirs(baseDir))
        {
            var direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            if (m.Success)
            {
                var alt = Directory.GetFiles(dir, m.Groups[1].Value + "*.dll");
                if (alt.Length == 1) return alt[0];
            }
        }
        return null;
    }

    private static IEnumerable<string> RuntimeSearchDirs(string baseDir)
    {
        yield return baseDir;

        var roots = new List<string>();
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) roots.Add(dotnetRoot);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));

        foreach (var root in roots)
        {
            foreach (var shared in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            {
                var dir = Path.Combine(root, "shared", shared);
                if (!Directory.Exists(dir)) continue;
                foreach (var v in Directory.GetDirectories(dir).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return v;
            }

            // hostfxr.dll 不在 shared 里，单独装了一层 host\fxr\<版本>
            var fxr = Path.Combine(root, "host", "fxr");
            if (Directory.Exists(fxr))
            {
                foreach (var v in Directory.GetDirectories(fxr).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return v;
            }
        }
    }
}
