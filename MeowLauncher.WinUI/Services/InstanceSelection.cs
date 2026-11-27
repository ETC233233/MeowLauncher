using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 「当前实例」唯一的切换入口。首页、版本页、搜索、实例页顶栏都走这里：
/// 先乐观更新快照与偏好并通知界面，再写后端，失败整体回滚。
/// 以前这段逻辑在 HomePage.SelectContinueInstance 和 VersionsPage.SwitchInstance_Click 各写一份，
/// 且只按 Id 比较，多游戏目录下同名实例会互相顶掉。
/// </summary>
public static class InstanceSelection
{
    /// <summary>当前实例变化（含回滚）。在调用方所在线程触发，调用方应在 UI 线程。</summary>
    public static event Action<VersionInfo>? Changed;

    public static string Key(string root, string id) =>
        Path.Combine(GameDirectoryResolver.Normalize(root), "versions", id).ToLowerInvariant();

    public static bool Same(VersionInfo? a, VersionInfo? b)
    {
        if (a is null || b is null) return false;
        if (!string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return false;
        return SameRoot(a.Root, b.Root);
    }

    public static bool SameRoot(string? a, string? b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(GameDirectoryResolver.Normalize(a)),
            Path.TrimEndingDirectorySeparator(GameDirectoryResolver.Normalize(b)), StringComparison.OrdinalIgnoreCase);

    public static VersionInfo? Find(Snapshot? snapshot, string id, string root)
    {
        if (snapshot is null) return null;
        var current = snapshot.Current;
        if (id.Length == 0 && root.Length == 0 && current is not null) return current;
        if (id.Length == 0) id = snapshot.Selected;
        if (id.Length == 0) return null;
        bool Matches(VersionInfo entry) => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            && (root.Length == 0 || SameRoot(entry.Root, root));
        if (current is not null && Matches(current)) return current;
        var matches = snapshot.Versions.Where(Matches).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static async Task<(bool Ok, string Message)> SelectAsync(VersionInfo target)
    {
        var main = App.MainWindow;
        var snapshot = main?.Snapshot;
        var prefs = main?.Preferences;
        if (Same(snapshot?.Current, target)) return (true, "");

        var prevCurrent = snapshot?.Current;
        var prevSelected = snapshot?.Selected ?? "";
        var prevId = prefs?.HomeSelectedInstance ?? "";
        var prevRoot = prefs?.HomeSelectedRoot ?? "";

        Apply(snapshot, prefs, target.Id, target.Root, target);
        Changed?.Invoke(target);

        var result = await App.Backend.SelectVersionAsync(target.Id, target.Root);
        if (result.Ok) return (true, "");

        // 后端没写成：整体退回，避免「看着切了、重开又变回去」。
        if (snapshot is not null)
        {
            foreach (var v in snapshot.Versions) v.IsCurrent = Same(v, prevCurrent);
            snapshot.Selected = prevSelected;
            snapshot.Current = prevCurrent;
        }
        if (prefs is not null)
        {
            prefs.HomeSelectedInstance = prevId;
            prefs.HomeSelectedRoot = prevRoot;
            AppPreferencesStore.Save(prefs);
        }
        if (prevCurrent is not null) Changed?.Invoke(prevCurrent);
        return (false, result.Message);
    }

    private static void Apply(Snapshot? snapshot, AppPreferences? prefs, string id, string root, VersionInfo target)
    {
        if (snapshot is not null)
        {
            foreach (var v in snapshot.Versions) v.IsCurrent = Same(v, target);
            snapshot.Selected = id;
            snapshot.Current = target;
        }
        if (prefs is not null)
        {
            prefs.HomeSelectedInstance = id;
            prefs.HomeSelectedRoot = root;
            AppPreferencesStore.Save(prefs);
        }
    }

    /// <summary>按「偏好里选的实例 → 后端当前实例 → 第一个可用实例」解析，Id 与 Root 同时匹配优先。</summary>
    public static VersionInfo? Resolve(Snapshot? snapshot, AppPreferences? prefs)
    {
        if (snapshot is null) return null;
        var usable = snapshot.Versions.Where(v => v.Valid && !v.Hidden).ToList();
        var id = prefs?.HomeSelectedInstance ?? "";
        var root = GameDirectoryResolver.Normalize(prefs?.HomeSelectedRoot);
        if (id.Length > 0)
        {
            var exact = usable.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)
                && (root.Length == 0 || string.Equals(GameDirectoryResolver.Normalize(v.Root), root, StringComparison.OrdinalIgnoreCase)));
            if (exact is not null) return exact;
            var byId = usable.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }
        return snapshot.Current is { Valid: true, Hidden: false } c ? c : usable.FirstOrDefault();
    }
}
