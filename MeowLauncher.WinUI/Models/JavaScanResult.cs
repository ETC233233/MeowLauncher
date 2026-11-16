namespace MeowLauncher.WinUI.Models;

public sealed class JavaInfo
{
    public string Path { get; set; } = "";
    public int Major { get; set; }
    public string Version { get; set; } = "";

    public string Label => Major > 0
        ? (string.IsNullOrWhiteSpace(Version) ? $"Java {Major}" : $"Java {Major} · {Version}")
        : "Java · 版本未知";
    public string Detail => Path;

    public override string ToString() => Label;
}

public sealed class JavaScanResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Recommended { get; set; } = "";
    public List<JavaInfo> Items { get; set; } = new();
}
