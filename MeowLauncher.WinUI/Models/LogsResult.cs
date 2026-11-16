namespace MeowLauncher.WinUI.Models;

public sealed class LogsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Dir { get; set; } = "";
    public string File { get; set; } = "";
    public int Total { get; set; }
    public bool Truncated { get; set; }
    public List<LogFileInfo> Files { get; set; } = new();
    public List<string> Lines { get; set; } = new();
}

public sealed class LogFileInfo
{
    public string Name { get; set; } = "";
    public long Mtime { get; set; }
    public long Size { get; set; }

    public string SizeText => Size switch
    {
        >= 1_048_576 => $"{Size / 1_048_576d:0.#} MB",
        >= 1024 => $"{Size / 1024d:0.#} KB",
        _ => $"{Size} B",
    };

    public string Label => $"{Name} · {SizeText}";

    public override string ToString() => Label;
}
