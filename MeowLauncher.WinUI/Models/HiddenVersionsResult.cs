namespace MeowLauncher.WinUI.Models;

public sealed class HiddenVersionInfo
{
    public string Id { get; set; } = "";
    public string Root { get; set; } = "";
    public long Time { get; set; }

    public override string ToString() => Id;

    public string SinceText
    {
        get
        {
            if (Time <= 0) return "隐藏时间未知";
            var ts = Time > 10_000_000_000 ? Time / 1000 : Time;
            var when = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            return $"隐藏于 {when:M月d日 HH:mm}";
        }
    }
}

public sealed class HiddenVersionsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<HiddenVersionInfo> Versions { get; set; } = new();
}
