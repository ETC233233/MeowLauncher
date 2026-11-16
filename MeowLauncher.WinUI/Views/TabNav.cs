namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 子页宿主（获取）：把一个页面作为「子页」整页替换内容区，页头换成 返回箭头 + 面包屑。
/// 关闭后回到打开前的列表，列表页实例一直活着，所以滚动位置和筛选状态原样保留。
/// </summary>
public interface ISubPageHost
{
    /// <summary>打开子页；crumbs 是面包屑各段（如 获取 / 下载版本 / 1.20.1），最后一段是当前页。</summary>
    void OpenSubPage(Type page, object args, params string[] crumbs);

    /// <summary>关闭当前子页，回到列表。</summary>
    void CloseSubPage();
}

/// <summary>
/// 子页宿主（获取 / 实例）传给内部页面的参数。
/// Embedded=true 表示页面被嵌在宿主里，需要把自带的页头（面包屑）收起，避免和宿主的页头重复。
/// Query 只给「在线资源」用，用于把搜索词带进子页。
/// Host 是能打开子页的宿主（只有「获取」提供）。
/// </summary>
public sealed record TabNav(string Query = "", bool Embedded = true, ISubPageHost? Host = null);
