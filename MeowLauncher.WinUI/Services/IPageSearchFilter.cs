namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 让顶栏搜索在「本页」作用域下真正过滤当前页面列表。
/// 页面实现它之后，输入框的每次变化都会把 query 交给页面自己处理，
/// 而不是去查全局静态索引（那里没有 Mod、资源包、实例这些条目级数据）。
/// </summary>
public interface IPageSearchFilter
{
    /// <summary>把关键词应用到本页列表；空串表示恢复全部。</summary>
    void ApplySearch(string query);

    /// <summary>Ctrl+K 在本页时把焦点交给本页搜索框；没有本页搜索框则返回 false（调用方退化为全局浮层）。</summary>
    bool TryFocusSearch();
}
