namespace MeowLauncher.WinUI.Services;

/// <summary>已知不可能支持的版本不启动元数据请求；精确支持情况由后端官方清单确认。</summary>
internal static class LoaderCompatibility
{
    internal static bool CanQuery(string game, string kind)
    {
        if (kind is not ("fabric" or "quilt" or "forge" or "neoforge")) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(game, @"^\d+\.\d+(\.\d+)?$"))
        {
            var parts = game.Split('.').Select(int.Parse).ToArray();
            var value = (parts[0], parts[1], parts.Length > 2 ? parts[2] : 0);
            var floor = kind switch { "fabric" or "quilt" => (1, 14, 0), "neoforge" => (1, 20, 1), _ => (1, 1, 0) };
            return value.CompareTo(floor) >= 0;
        }
        return kind is "fabric" or "quilt" or "forge" &&
            !new[] { "old_", "b1.", "a1.", "inf-", "rd-", "c0." }.Any(game.StartsWith);
    }
}
