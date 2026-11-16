namespace MeowLauncher.WinUI.Models;

public enum SearchActionKind
{
    Navigate,
    Version,
    PickInstance,
    Online,
    External,
    /// <summary>占位行（如「没有找到…」）：显示但不可执行，回车和点击都跳过。</summary>
    None
}

public sealed class SearchHit
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Group { get; init; } = "";
    public string GroupHeader { get; set; } = "";
    public bool ShowGroupHeader { get; set; }
    public string Glyph { get; init; } = "";
    public SearchActionKind Action { get; init; }
    public string PageTag { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string Focus { get; init; } = "";
    public string Url { get; init; } = "";
    public string Keywords { get; init; } = "";

    public bool IsActionable => Action != SearchActionKind.None;

    public override string ToString() => Title;
}
