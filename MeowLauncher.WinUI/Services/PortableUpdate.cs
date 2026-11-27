using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

// Shared with the independent updater and its integration tests. No WinUI dependencies.
public sealed record PortableFileList(string Version, string[] Files);
public sealed record PortableUpdatePlan(string Target, string Archive, string Envelope, string Version,
    string Channel, int ParentPid, long ParentStarted, string Token);

public static class PortableUpdate
{
    public const string ManifestName = "meow-update.json";
    public const string UpdaterName = "MeowLauncher.Updater.exe";
    private static readonly string[] Required = ["MeowLauncher.WinUI.exe", "MeowLauncher.WinUI.dll",
        "MeowLauncher.WinUI.pri", "Backend/sidecar.py", UpdaterName, "MeowLauncher.Updater.dll"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool SupportsInPlaceUpdate(string root)
    {
        if (!File.Exists(Path.Combine(root, ManifestName)) || !File.Exists(Path.Combine(root, UpdaterName)))
            return false;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (IsUnderDirectory(root, programFiles) || IsUnderDirectory(root, programFilesX86)) return false;

        try
        {
            // A previous portable copy can leave its manifest and helper in an Inno Setup installation.
            return !Directory.EnumerateFiles(root, "unins*.dat")
                .Any(log => File.Exists(Path.ChangeExtension(log, ".exe")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        if (directory.Length == 0) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.StartsWith('/') || relative.EndsWith('/') || relative.Length > 220) throw new InvalidDataException("不安全的更新路径。");
        var parts = relative.Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]))
                throw new InvalidDataException("不安全的更新文件名。");
        }
        // Packages own program assets only. Account/preferences/game data are never update targets.
        if (parts.Length > 1 && parts[0] is not ("Assets" or "Backend" or "Licenses")) throw new InvalidDataException("更新包包含非程序目录。");
        if (parts.Length == 1 && relative != ManifestName && relative != "version.json"
            && !relative.StartsWith("MeowLauncher.", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(relative).ToLowerInvariant() is not (".dll" or ".exe" or ".pdb" or ".dat" or ".bin" or ".txt"))
            throw new InvalidDataException("更新包包含非程序文件。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径越界。");
        for (var dir = new FileInfo(full).Directory; dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新目录不能经过符号链接。");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新文件不能是符号链接。");
        return full;
    }

    public static PortableFileList ReadList(string json, string root)
    {
        var list = JsonSerializer.Deserialize<PortableFileList>(json, JsonOptions) ?? throw new InvalidDataException("缺少更新文件清单。");
        if (list.Files is null || list.Files.Length is 0 or > 30000) throw new InvalidDataException("更新文件清单无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in list.Files)
        {
            SafePath(root, name);
            if (!names.Add(name)) throw new InvalidDataException("更新文件清单重复。");
        }
        if (!names.Contains(ManifestName) || Required.Any(x => !names.Contains(x))) throw new InvalidDataException("更新包缺少必要程序文件。");
        return list;
    }

    public static async Task VerifyAsync(string archive, LauncherPackage package, CancellationToken ct = default)
    {
        if (new FileInfo(archive).Length != package.Size) throw new InvalidDataException("更新包大小不匹配。");
        await using var stream = File.OpenRead(archive);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败。");
    }

    public static PortableFileList Extract(string archive, string stage, string version)
    {
        if (Directory.Exists(stage)) throw new IOException("更新解压目录已存在。");
        Directory.CreateDirectory(stage);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 30000) throw new InvalidDataException("更新包文件过多。");
        var manifest = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("缺少更新文件清单。");
        if (manifest.Length > 4 * 1024 * 1024) throw new InvalidDataException("更新文件清单过大。");
        using var reader = new StreamReader(manifest.Open());
        var list = ReadList(reader.ReadToEnd(), stage);
        if (list.Version != version) throw new InvalidDataException("更新包版本与签名清单不符。");
        var remaining = new HashSet<string>(list.Files, StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !remaining.Remove(entry.FullName))
                throw new InvalidDataException("更新包包含链接、重复或未声明的文件。");
            expanded = checked(expanded + entry.Length);
            if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包解压体积过大。");
            var dest = SafePath(stage, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: false);
        }
        if (remaining.Count != 0) throw new InvalidDataException("更新包内容不完整。");
        return list;
    }

    /// <summary>Move old program files to a retained backup; restore them on replacement or health-check failure.</summary>
    public static async Task ApplyAsync(string target, string stage, string backup, PortableFileList next,
        Func<Task<bool>> health, Action<int>? afterFile = null)
    {
        var oldPath = SafePath(target, ManifestName);
        var old = ReadList(File.ReadAllText(oldPath), target);
        var all = old.Files.Union(next.Files, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in all) { SafePath(target, name); SafePath(backup, name); }
        // Never overwrite an unowned local file, even if the new package happens to use that name.
        var owned = new HashSet<string>(old.Files, StringComparer.OrdinalIgnoreCase);
        if (next.Files.Any(x => !owned.Contains(x) && File.Exists(SafePath(target, x))))
            throw new IOException("更新目标存在未受启动器管理的同名文件。");
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        var installed = new List<string>();
        void Journal(string status) => File.WriteAllText(Path.Combine(backup, "transaction.json"),
            JsonSerializer.Serialize(new { status, target, stage, moved, installed }));
        Journal("preparing");
        try
        {
            foreach (var name in all)
            {
                var source = SafePath(target, name);
                if (!File.Exists(source)) continue;
                var dest = SafePath(backup, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                moved.Add(name); Journal("backing-up");
                File.Move(source, dest);
            }
            foreach (var name in next.Files)
            {
                var dest = SafePath(target, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                installed.Add(name); Journal("installing");
                File.Copy(SafePath(stage, name), dest, overwrite: false);
                afterFile?.Invoke(installed.Count);
            }
            Journal("checking");
            if (!await health()) throw new IOException("新版启动自检未通过，已恢复之前的版本。");
            Journal("complete");
        }
        catch
        {
            var failed = Path.Combine(backup, "failed-new-version");
            foreach (var name in installed.AsEnumerable().Reverse())
            {
                var file = SafePath(target, name);
                if (!File.Exists(file)) continue;
                var dest = SafePath(failed, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
            }
            foreach (var name in moved.AsEnumerable().Reverse())
            {
                var file = SafePath(backup, name);
                if (!File.Exists(file)) continue;
                var dest = SafePath(target, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
            }
            Journal("rolled-back");
            throw;
        }
    }
}
