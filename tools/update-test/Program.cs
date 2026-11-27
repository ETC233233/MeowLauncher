using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MeowLauncher.WinUI.Services;

var root = Path.GetFullPath(Path.Combine("_archive", "update-test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
async Task Reject(Func<Task> action, string name)
{ try { await action(); } catch { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
var names = new[] { "MeowLauncher.WinUI.exe", "MeowLauncher.WinUI.dll", "MeowLauncher.WinUI.pri", "Backend/sidecar.py",
    "MeowLauncher.Updater.exe", "MeowLauncher.Updater.dll", PortableUpdate.ManifestName };
var old = new PortableFileList("1.0.0", names.Concat(["MeowLauncher.obsolete.dll"]).ToArray());
var next = new PortableFileList("1.0.1", names);
string NewTarget(string name)
{
    var target = Path.Combine(root, name);
    foreach (var file in old.Files)
    {
        var f = PortableUpdate.SafePath(target, file); Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, file == PortableUpdate.ManifestName ? JsonSerializer.Serialize(old) : "old");
    }
    Directory.CreateDirectory(Path.Combine(target, ".minecraft", "saves"));
    File.WriteAllText(Path.Combine(target, ".minecraft", "saves", "keep"), "save");
    File.WriteAllText(Path.Combine(target, "app.json"), "account/preferences");
    return target;
}
string Zip(string name, string? evil = null)
{
    var file = Path.Combine(root, name + ".zip");
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var path in next.Files)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(path == PortableUpdate.ManifestName ? JsonSerializer.Serialize(next) : "new");
    }
    if (evil is not null) { using var writer = new StreamWriter(zip.CreateEntry(evil).Open()); writer.Write("evil"); }
    return file;
}
var archive = Zip("valid");
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
var package = new LauncherPackage("portable-v1", "win-x64", new FileInfo(archive).Length, hash, ["https://example.com/p.zip"]);
await PortableUpdate.VerifyAsync(archive, package); Check(true, "exact size and SHA-256");
await Reject(() => PortableUpdate.VerifyAsync(archive, package with { Sha256 = new string('0', 64) }), "corrupt hash rejected");
await Reject(() => PortableUpdate.VerifyAsync(archive, package with { Size = 1 }), "truncated/oversized download rejected");
foreach (var path in new[] { "../escape", "Assets/../escape", "Assets/a:stream", "Assets/CON.txt", ".minecraft/saves/x", "app.json" })
    await Reject(() => Task.Run(() => PortableUpdate.SafePath(root, path)), "unsafe path rejected " + path);
await Reject(() => Task.Run(() => PortableUpdate.Extract(Zip("extra", "Assets/extra"), Path.Combine(root, "bad-extra"), "1.0.1")), "undeclared entry rejected");
await Reject(() => Task.Run(() => PortableUpdate.Extract(archive, Path.Combine(root, "bad-version"), "1.0.2")), "wrong version rejected");
await Reject(() => Task.Run(() => PortableUpdate.Extract(Zip("duplicate", "MEOWLAUNCHER.WINUI.EXE"), Path.Combine(root, "bad-dupe"), "1.0.1")), "case-colliding duplicate rejected");
var stage = Path.Combine(root, "expanded"); PortableUpdate.Extract(archive, stage, "1.0.1");
var target = NewTarget("success");
await PortableUpdate.ApplyAsync(target, stage, Path.Combine(root, "backup-success"), next, () => Task.FromResult(true));
Check(File.ReadAllText(Path.Combine(target, names[0])) == "new", "successful replacement");
Check(!File.Exists(Path.Combine(target, "MeowLauncher.obsolete.dll")), "obsolete owned file archived");
Check(File.ReadAllText(Path.Combine(target, "app.json")) == "account/preferences" && File.ReadAllText(Path.Combine(target, ".minecraft/saves/keep")) == "save", "preferences and game data preserved");
foreach (var fail in new[] { "replace", "health" })
{
    var dst = NewTarget(fail);
    await Reject(() => PortableUpdate.ApplyAsync(dst, stage, Path.Combine(root, "backup-" + fail), next,
        () => Task.FromResult(false), fail == "replace" ? n => { if (n == 2) throw new IOException("injected write failure"); } : null), fail + " failure reported");
    Check(File.ReadAllText(Path.Combine(dst, names[0])) == "old" && File.Exists(Path.Combine(dst, "MeowLauncher.obsolete.dll")), fail + " restores old program");
}
var parsed = UpdateLogic.ParseLauncher(JsonSerializer.Serialize(new { launcher = new { beta = new { version = "1.0.1-01126-2000", package = new { format = package.Format, platform = package.Platform, size = package.Size, sha256 = package.Sha256, urls = package.Urls } } } }));
Check(parsed.Beta?.Package is not null, "signed config package parsed");
Check(UpdateLogic.ParseGitHubReleases("[{\"tag_name\":\"v1.0.1\",\"html_url\":\"https://github.com/o/r/releases/tag/v1.0.1\",\"package\":{}}]").Stable?.Package is null, "GitHub fallback cannot authorize in-place install");
Check(UrlAccel.Apply("https://api.github.com/repos/o/r/releases") == "https://api.github.com/repos/o/r/releases", "GitHub API never wrapped in ghproxy");
Check(UrlAccel.Apply("https://github.com/o/r/releases/download/v1/p.zip").StartsWith(UrlAccel.DefaultHost), "release file acceleration retained");
Check(UrlAccel.Apply("https://evil.example/github.com/p.zip") == "https://evil.example/github.com/p.zip", "lookalike host not accelerated");
Console.WriteLine($"ALL PASS ({passed}); retained evidence: {root}");
