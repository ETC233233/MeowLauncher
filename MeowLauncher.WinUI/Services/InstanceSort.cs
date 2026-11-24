using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>实例列表的排序方式：使用时间（最近游玩） / 名称（版本文件夹名） / 创建时间（文件夹时间）。</summary>
public enum InstanceSortMode
{
    Recent,
    Name,
    Created,
}

/// <summary>
/// 实例排序的全局选择：实例管理页（VersionsPage）负责切换，首页那个小的实例选择列表（InstancePicker）跟随同一个选择。
/// </summary>
public static class InstanceSort
{
    public static InstanceSortMode Mode { get; private set; } = InstanceSortMode.Recent;

    /// <summary>排序方式变了：已显示的列表自己重排。</summary>
    public static event EventHandler? Changed;

    public static void Set(InstanceSortMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<VersionInfo> Apply(IEnumerable<VersionInfo> items) => Mode switch
    {
        InstanceSortMode.Name => items
            .OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        InstanceSortMode.Created => items
            .OrderByDescending(v => v.Mtime)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        _ => items
            .OrderByDescending(v => v.LastPlayed)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };
}
