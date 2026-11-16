using System.Text;
using System.Text.Json;
using MeowLauncher.WinUI.Services;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

var failed = 0;
void T(string name, bool ok, string info = "")
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (info.Length > 0 ? " | " + info : ""));
    if (!ok) failed++;
}

// 临时密钥对
var rng = new SecureRandom();
var sk = new byte[32]; Ed25519.GeneratePrivateKey(rng, sk);
var pk = new byte[32]; Ed25519.GeneratePublicKey(sk, 0, pk, 0);
var sk2 = new byte[32]; Ed25519.GeneratePrivateKey(rng, sk2);
var pk2 = new byte[32]; Ed25519.GeneratePublicKey(sk2, 0, pk2, 0);

string Env(string payload, byte[] priv, byte[] pub, string kid = "k1")
{
    var msg = Encoding.UTF8.GetBytes(payload);
    var sig = new byte[64];
    Ed25519.Sign(priv, 0, pub, 0, null, msg, 0, msg.Length, sig, 0);
    return JsonSerializer.Serialize(new { payload, sig = Convert.ToBase64String(sig), kid });
}
Func<string, byte[]?> keys = kid => kid == "k1" ? pk : null;
const long now = 1_800_000_000;

string Payload(long issued, long expires, string components) =>
    "{\"version\":1,\"issuedAt\":" + issued + ",\"expiresAt\":" + expires + ",\"components\":" + components + "}";
string Comp(int level, string ver, string min = "", string max = "", string sha = "", long size = 1000, string url = "https://example.com/a.zip") =>
    "{\"apiLevel\":" + level + ",\"version\":\"" + ver + "\",\"minLauncher\":\"" + min + "\",\"maxLauncher\":\"" + max +
    "\",\"required\":false,\"size\":" + size + ",\"sha256\":\"" + (sha.Length > 0 ? sha : new string('a', 64)) + "\",\"urls\":[\"" + url + "\"]}";

var good = Payload(now, now + 3600, "[" + Comp(1, "1.0.0") + "]");

// ---- 验签
T("valid signature accepted", ComponentManifest.VerifyEnvelope(Env(good, sk, pk), keys, now) == good);
T("tampered payload rejected", ComponentManifest.VerifyEnvelope(Env(good, sk, pk).Replace("1.0.0", "9.9.9"), keys, now) is null);
T("signature by other key rejected", ComponentManifest.VerifyEnvelope(Env(good, sk2, pk2), keys, now) is null);
T("unknown kid rejected", ComponentManifest.VerifyEnvelope(Env(good, sk, pk, "zz"), keys, now) is null);
T("expired payload rejected", ComponentManifest.VerifyEnvelope(Env(Payload(now - 7200, now - 3600, "[]"), sk, pk), keys, now) is null);
T("payload issued in the future rejected", ComponentManifest.VerifyEnvelope(Env(Payload(now + 3600, now + 7200, "[]"), sk, pk), keys, now) is null);
T("clock skew within 5 minutes tolerated", ComponentManifest.VerifyEnvelope(Env(Payload(now + 200, now + 3600, "[]"), sk, pk), keys, now) is not null);
T("garbage envelope rejected", ComponentManifest.VerifyEnvelope("not json", keys, now) is null);
T("short signature rejected", ComponentManifest.VerifyEnvelope("{\"payload\":\"{}\",\"sig\":\"AAAA\",\"kid\":\"k1\"}", keys, now) is null);
T("no key configured rejected", ComponentManifest.VerifyEnvelope(Env(good, sk, pk), _ => null, now) is null);

// ---- 挑包
var L5 = new Version(5, 0, 0);
var (o1, e1) = ComponentManifest.PickOffer(good, L5);
T("picks matching offer", o1?.Version == "1.0.0" && e1 == "");

var multi = Payload(now, now + 3600, "[" + Comp(1, "1.0.0") + "," + Comp(1, "1.2.0") + "," + Comp(1, "1.10.0") + "," + Comp(2, "2.0.0") + "]");
T("picks highest version (numeric, 1.10.0 > 1.2.0)", ComponentManifest.PickOffer(multi, L5).Offer?.Version == "1.10.0");

var onlyL2 = Payload(now, now + 3600, "[" + Comp(2, "2.0.0") + "]");
var (o2, e2) = ComponentManifest.PickOffer(onlyL2, L5);
T("other api level only -> none, asks to update launcher", o2 is null && e2.Contains("更新启动器"), e2);

var ranged = Payload(now, now + 3600, "[" + Comp(1, "1.1.0", min: "5.1.0") + "]");
var (o3, e3) = ComponentManifest.PickOffer(ranged, L5);
T("launcher below minLauncher -> none", o3 is null && e3.Contains("更新启动器"), e3);
T("launcher inside range -> picked", ComponentManifest.PickOffer(ranged, new Version(5, 1, 0)).Offer is not null);
var capped = Payload(now, now + 3600, "[" + Comp(1, "1.0.0", max: "5.9.9") + "]");
T("launcher above maxLauncher -> none", ComponentManifest.PickOffer(capped, new Version(6, 0, 0)).Offer is null);
T("old launcher still gets old level while new level exists",
    ComponentManifest.PickOffer(Payload(now, now + 3600, "[" + Comp(1, "1.5.0", max: "5.9.9") + "," + Comp(2, "2.0.0", min: "6.0.0") + "]"), new Version(5, 2, 0)).Offer?.Version == "1.5.0");
T("empty list -> none", ComponentManifest.PickOffer(Payload(now, now + 3600, "[]"), L5).Offer is null);
T("no components field -> none", ComponentManifest.PickOffer("{\"version\":1,\"issuedAt\":1,\"expiresAt\":2}", L5).Offer is null);

// ---- 畸形条目一律丢弃
bool Rejected(string comp) => ComponentManifest.PickOffer(Payload(now, now + 3600, "[" + comp + "]"), L5).Offer is null;
T("version with path traversal rejected", Rejected(Comp(1, "../1.0.0")));
T("version not numeric rejected", Rejected(Comp(1, "latest")));
T("sha256 wrong length rejected", Rejected(Comp(1, "1.0.0", sha: "abc")));
T("sha256 non-hex rejected", Rejected(Comp(1, "1.0.0", sha: new string('z', 64))));
T("size zero rejected", Rejected(Comp(1, "1.0.0", size: 0)));
T("size over 400 MB rejected", Rejected(Comp(1, "1.0.0", size: 500L * 1024 * 1024)));
T("plain http url (non-loopback) rejected", Rejected(Comp(1, "1.0.0", url: "http://example.com/a.zip")));
T("file:// url rejected", Rejected(Comp(1, "1.0.0", url: "file:///C:/a.zip")));
T("ftp url rejected", Rejected(Comp(1, "1.0.0", url: "ftp://example.com/a.zip")));

// ---- SafeName
T("SafeName accepts 1.0.0", ComponentManifest.SafeName("1.0.0"));
T("SafeName rejects ..", !ComponentManifest.SafeName(".."));
T("SafeName rejects slash", !ComponentManifest.SafeName("1/0"));
T("SafeName rejects backslash", !ComponentManifest.SafeName("1\0"));
T("SafeName rejects empty", !ComponentManifest.SafeName(""));

// ---- 组件包解压核对
var tmp = Path.Combine(Path.GetTempPath(), "mt-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

// files: 实际放进 zip 的 (名字, 内容)；listed: 写进 component.json 的 (路径, 哈希)；version/api 写进 component.json
string MakeZip(string name, (string Name, byte[] Data)[] files, (string Path, string Hash)[] listed, string version = "1.0.0", int api = 1, bool withManifest = true)
{
    var zipPath = Path.Combine(tmp, name + ".zip");
    using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
    {
        foreach (var f in files)
        {
            var e = z.CreateEntry(f.Name);
            using var s = e.Open(); s.Write(f.Data);
        }
        if (withManifest)
        {
            var json = JsonSerializer.Serialize(new { version, apiLevel = api, files = listed.Select(l => new { path = l.Path, sha256 = l.Hash }) });
            var e = z.CreateEntry("component.json");
            using var s = e.Open(); s.Write(Encoding.UTF8.GetBytes(json));
        }
    }
    return zipPath;
}
string Extract(string zipPath, string version = "1.0.0", int api = 1)
{
    var dest = Path.Combine(tmp, "out-" + Guid.NewGuid().ToString("N")[..6]);
    return ComponentPackage.ExtractAndVerify(zipPath, dest, version, api);
}
var A = Encoding.UTF8.GetBytes("aaaa"); var B = Encoding.UTF8.GetBytes("bbbbbbbb");

T("good package accepted",
    Extract(MakeZip("good", new[] { ("RoomAgent.exe", A), ("easytier/core.exe", B) }, new[] { ("RoomAgent.exe", Sha(A)), ("easytier/core.exe", Sha(B)) })) == "");
T("backslash entry names accepted (PowerShell 5.1 zips)",
    Extract(MakeZip("bs", new[] { ("easytier\\core.exe", B) }, new[] { ("easytier/core.exe", Sha(B)) })) == "");
T("zip-slip ../ rejected", Extract(MakeZip("slip", new[] { ("../evil.dll", A) }, new[] { ("../evil.dll", Sha(A)) })).Length > 0);
T("nested ../ rejected", Extract(MakeZip("slip2", new[] { ("a/../../evil.dll", A) }, new[] { ("a/../../evil.dll", Sha(A)) })).Length > 0);
T("absolute path rejected", Extract(MakeZip("abs", new[] { ("/evil.dll", A) }, new[] { ("/evil.dll", Sha(A)) })).Length > 0);
T("drive letter path rejected", Extract(MakeZip("drv", new[] { ("C:/evil.dll", A) }, new[] { ("C:/evil.dll", Sha(A)) })).Length > 0);
T("alternate data stream name rejected", Extract(MakeZip("ads", new[] { ("a.dll:evil", A) }, new[] { ("a.dll:evil", Sha(A)) })).Length > 0);
T("file not listed in manifest rejected",
    Extract(MakeZip("extra", new[] { ("RoomAgent.exe", A), ("extra.dll", B) }, new[] { ("RoomAgent.exe", Sha(A)) })).Contains("清单之外"));
T("file listed but missing rejected",
    Extract(MakeZip("missing", new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)), ("gone.dll", Sha(B)) })).Contains("缺少"));
T("wrong hash rejected",
    Extract(MakeZip("hash", new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(B)) })).Contains("校验失败"));
T("no component.json rejected",
    Extract(MakeZip("nomf", new[] { ("RoomAgent.exe", A) }, Array.Empty<(string, string)>(), withManifest: false)).Contains("component.json"));
T("version mismatch rejected",
    Extract(MakeZip("ver", new[] { ("a.dll", A) }, new[] { ("a.dll", Sha(A)) }, version: "9.9.9")).Contains("不一致"));
T("api level mismatch rejected",
    Extract(MakeZip("api", new[] { ("a.dll", A) }, new[] { ("a.dll", Sha(A)) }, api: 2)).Contains("不一致"));
T("manifest path escaping staging rejected",
    Extract(MakeZip("mfesc", new[] { ("a.dll", A) }, new[] { ("../x.dll", Sha(A)), ("a.dll", Sha(A)) })).Length > 0);
// ---- 运行时从启动器目录复制
var launcherDir = Path.Combine(tmp, "launcher"); Directory.CreateDirectory(launcherDir);
File.WriteAllBytes(Path.Combine(launcherDir, "System.Private.CoreLib.dll"), B);
string MakeZipRt(string name, string[] runtime, (string Name, byte[] Data)[] files, (string Path, string Hash)[] listed)
{
    var zipPath = Path.Combine(tmp, name + ".zip");
    using var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
    foreach (var f in files) { using var s2 = z.CreateEntry(f.Name).Open(); s2.Write(f.Data); }
    var json = JsonSerializer.Serialize(new { version = "1.0.0", apiLevel = 1, runtimeFromLauncher = runtime, files = listed.Select(l => new { path = l.Path, sha256 = l.Hash }) });
    using var s3 = z.CreateEntry("component.json").Open(); s3.Write(Encoding.UTF8.GetBytes(json));
    return zipPath;
}
string ExtractRt(string zipPath, out string dest)
{
    dest = Path.Combine(tmp, "rt-" + Guid.NewGuid().ToString("N")[..6]);
    return ComponentPackage.ExtractAndVerify(zipPath, dest, "1.0.0", 1, launcherDir);
}
var rtOk = ExtractRt(MakeZipRt("rt1", new[] { "System.Private.CoreLib.dll" }, new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)) }), out var rtDest);
T("runtime file copied from launcher dir", rtOk == "" && File.Exists(Path.Combine(rtDest, "System.Private.CoreLib.dll")) && File.ReadAllBytes(Path.Combine(rtDest, "System.Private.CoreLib.dll")).SequenceEqual(B), rtOk);
T("runtime file missing in launcher dir rejected",
    ExtractRt(MakeZipRt("rt2", new[] { "nope.dll" }, new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)) }), out _).Contains("不匹配"));
T("runtime name with path traversal rejected",
    ExtractRt(MakeZipRt("rt3", new[] { "../evil.dll" }, new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)) }), out _).Contains("不合法"));
T("runtime name with slash rejected",
    ExtractRt(MakeZipRt("rt4", new[] { "sub/x.dll" }, new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)) }), out _).Contains("不合法"));
T("runtime name clashing with package file rejected",
    ExtractRt(MakeZipRt("rt5", new[] { "RoomAgent.exe" }, new[] { ("RoomAgent.exe", A) }, new[] { ("RoomAgent.exe", Sha(A)) }), out _).Contains("不合法"));
try { Directory.Delete(tmp, true); } catch { }

// ---- 版本号（三档）
MeowVersion? V(string t) => MeowVersion.Parse(t);
T("parse Review", V("Review 01113-7829") is { Channel: MeowChannel.Review, Core: null, BuildId: "01113-7829" });
T("parse Beta", V("1.0.0-01113-7829") is { Channel: MeowChannel.Beta } b && b.Core == new Version(1, 0, 0) && b.BuildId == "01113-7829");
T("parse Release", V("1.0.0") is { Channel: MeowChannel.Release } r && r.Core == new Version(1, 0, 0));
T("parse Release with function version (+)", V("1.0.0+01113-7829") is { Channel: MeowChannel.Release, BuildId: "01113-7829" });
T("parse tag with leading v", V("v1.2.3") is { Channel: MeowChannel.Release } t && t.Core == new Version(1, 2, 3));
T("parse garbage is null", V("hello") is null && V("") is null && V("Review xyz") is null && V("1.0.0-abc") is null);
T("year index >= 10 parses (101113-5)", V("1.0.0-101113-5") is { } y && y.Iteration == 5);
T("display Review", V("Review 01113-7829")!.Display == "Review 01113-7829");
T("display Beta", V("1.0.0-01113-7829")!.Display == "1.0.0-01113-7829");
T("display Release", V("1.0.0")!.Display == "1.0.0" && V("1.0.0")!.FunctionVersion == "");
T("Release promoted from build shows function version", V("1.0.0+01113-7829")!.FunctionVersion == "01113-7829" && V("1.0.0+01113-7829")!.Display == "1.0.0");
T("MakeBuildId 2026-11-13 #7829 = 01113-7829", MeowVersion.MakeBuildId(new DateTime(2026, 11, 13), 7829) == "01113-7829");
T("MakeBuildId 2027-01-05 #12 = 10105-12", MeowVersion.MakeBuildId(new DateTime(2027, 1, 5), 12) == "10105-12");
T("Beta < Release of same version", V("1.0.0-01113-7829")!.CompareTo(V("1.0.0")!) < 0 && V("1.0.0")!.CompareTo(V("1.0.0-01113-7829")!) > 0);
T("Beta iteration compared numerically (9999 < 10000)", V("1.0.0-01120-9999")!.CompareTo(V("1.0.0-01121-10000")!) < 0);
T("same Beta equal", V("1.0.0-01113-7829")!.CompareTo(V("1.0.0-01113-7829")!) == 0);
T("1.0.1 > 1.0.0", V("1.0.1")!.CompareTo(V("1.0.0")!) > 0);
T("1.1.0 > 1.0.9", V("1.1.0")!.CompareTo(V("1.0.9")!) > 0);
T("2.0.0 > 1.9.9", V("2.0.0")!.CompareTo(V("1.9.9")!) > 0);
T("Beta of newer version > Release of older", V("1.1.0-01201-8000")!.CompareTo(V("1.0.9")!) > 0);
T("Review counts as oldest", V("Review 01113-7829")!.CompareTo(V("1.0.0-01113-7829")!) < 0);
T("Review compat version is null (skip range)", V("Review 01113-7829")!.CoreForCompat is null);
T("Beta/Release compat version ignores build id", V("1.0.0-01113-7829")!.CoreForCompat == new Version(1, 0, 0) && V("1.0.0")!.CoreForCompat == new Version(1, 0, 0));
T("notice: Review text", V("Review 01113-7829")!.NoticeText.StartsWith("当前使用的是非现发行的测试版本"));
T("notice: Beta text", V("1.0.0-01113-7829")!.NoticeText == "您正在使用MeowLauncher的预发行版本，此版本可能包含bug和不稳定内容，欢迎向我们的仓库提出issue反馈！");
T("notice: Release none", V("1.0.0")!.NoticeText == "" && V("1.0.0")!.NoticeKey == "");
T("notice key: Review once per machine, Beta per build", V("Review 01113-7829")!.NoticeKey == "review" && V("Review 01114-1")!.NoticeKey == "review" && V("1.0.0-01113-7829")!.NoticeKey != V("1.0.0-01113-7830")!.NoticeKey);
T("manifest range skipped for Review (null launcher)",
    ComponentManifest.PickOffer(Payload(now, now + 3600, "[" + Comp(1, "1.1.0", min: "9.0.0") + "]"), null).Offer is not null);

// ---- 检查更新逻辑
LauncherRelease R(string v) => new(v, "https://example.com/r", "", "");
UpdateResult E(string cur, string? stable, string? beta = null, string ch = "stable") =>
    UpdateLogic.Evaluate(MeowVersion.Parse(cur)!, stable is null ? null : R(stable), beta is null ? null : R(beta), ch);
T("update: Review never checks", E("Review 01113-896", "1.0.0").State == UpdateState.Review && E("Review 01113-896", "1.0.0").Message.StartsWith("当前使用的是非现发行"));
T("update: Release 1.0.0 vs latest 1.0.1 -> available", E("1.0.0", "1.0.1") is { State: UpdateState.Available } u1 && u1.Latest!.Version == "1.0.1");
T("update: Release 1.0.0 vs latest 1.0.0 -> up to date", E("1.0.0", "1.0.0").State == UpdateState.UpToDate);
T("update: Release 1.0.1 vs latest 1.0.0 -> ahead + user message", E("1.0.1", "1.0.0") is { State: UpdateState.Ahead } a1 && a1.Message == "当前使用的是非现发行的测试版本，无法检测此版本的更新，请保管好您的账号数据。");
T("update: no release published at all -> ahead", E("1.0.0", null).State == UpdateState.Ahead);
T("update: Beta 1.0.0-x vs Release 1.0.0 -> available", E("1.0.0-01113-896", "1.0.0").State == UpdateState.Available);
T("update: Beta 1.0.0-x vs older Release 0.9.0 -> ahead", E("1.0.0-01113-896", "0.9.0").State == UpdateState.Ahead);
T("update: stable channel ignores newer beta", E("1.0.0", "1.0.0", "1.1.0-01201-9000", "stable").State == UpdateState.UpToDate);
T("update: beta channel sees newer beta", E("1.0.0", "1.0.0", "1.1.0-01201-9000", "beta") is { State: UpdateState.Available } b1 && b1.Latest!.Version == "1.1.0-01201-9000");
T("update: beta channel, older beta than release ignored", E("1.0.0", "1.0.0", "0.9.0-01101-100", "beta").State == UpdateState.UpToDate);
T("update: Beta build 7829 vs beta 7830 same version -> available", E("1.0.0-01113-7829", null, "1.0.0-01114-7830", "beta").State == UpdateState.Available);
T("update: unparsable latest ignored -> ahead", E("1.0.0", "banana").State == UpdateState.Ahead);
var pl = "{\"launcher\":{\"stable\":{\"version\":\"1.0.0\",\"url\":\"https://example.com/s\",\"notes\":\"n\",\"date\":\"2026-11-20\"},\"beta\":{\"version\":\"1.1.0-01201-9000\",\"url\":\"http://bad\"}}}";
var (ps, pb) = UpdateLogic.ParseLauncher(pl);
T("parse launcher: stable fields", ps is { Version: "1.0.0", Url: "https://example.com/s", Notes: "n" });
T("parse launcher: non-https url dropped, version kept", pb is { Version: "1.1.0-01201-9000", Url: "" });
T("parse launcher: missing field -> none", UpdateLogic.ParseLauncher("{\"version\":1}") is (null, null) && UpdateLogic.ParseLauncher("nope") is (null, null));
T("parse launcher: Review version rejected", UpdateLogic.ParseLauncher("{\"launcher\":{\"stable\":{\"version\":\"Review 01113-1\"}}}").Stable is null);
var gh = "[{\"tag_name\":\"v1.0.0\",\"prerelease\":false,\"draft\":false,\"html_url\":\"https://github.com/o/r/releases/tag/v1.0.0\",\"body\":\"hi\",\"published_at\":\"2026-11-20T00:00:00Z\"},"
       + "{\"tag_name\":\"v1.1.0-01201-9000\",\"prerelease\":true,\"html_url\":\"https://github.com/o/r/releases/tag/x\"},"
       + "{\"tag_name\":\"v1.0.1\",\"prerelease\":false,\"draft\":true,\"html_url\":\"https://github.com/o/r/releases/tag/d\"},"
       + "{\"tag_name\":\"weird\",\"prerelease\":false,\"html_url\":\"https://github.com/o/r/releases/tag/w\"}]";
var (gs, gb) = UpdateLogic.ParseGitHubReleases(gh);
T("github: newest stable picked, draft and unparsable skipped", gs is { Version: "1.0.0" });
T("github: prerelease becomes beta", gb is { Version: "1.1.0-01201-9000" });
T("github: not an array -> none", UpdateLogic.ParseGitHubReleases("{\"message\":\"Not Found\"}") is (null, null));

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;
