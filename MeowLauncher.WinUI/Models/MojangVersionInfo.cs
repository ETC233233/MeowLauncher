using Microsoft.UI.Xaml;

namespace MeowLauncher.WinUI.Models;

public sealed class MojangVersionInfo
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string ReleaseTime { get; set; } = "";
    public bool Installed { get; set; }

    /// <summary>当前最新的正式版，用来打「最新版本」标记。</summary>
    public bool IsLatest { get; set; }

    public Visibility LatestVisibility => IsLatest ? Visibility.Visible : Visibility.Collapsed;

    public string TypeText => Type switch
    {
        "release" => "正式版",
        "snapshot" when Id.Contains("-pre", StringComparison.OrdinalIgnoreCase) => "预发布",
        "snapshot" when Id.Contains("-rc", StringComparison.OrdinalIgnoreCase) => "候选版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string DateText
    {
        get
        {
            if (DateTimeOffset.TryParse(ReleaseTime, out var time))
                return time.ToLocalTime().ToString("yyyy-MM-dd");
            return ReleaseTime;
        }
    }

    public string InstallText => Installed ? "已安装" : "安装";
    public bool InstallEnabled => !Installed;
}

public sealed class MojangVersionsResult
{
    public bool Ok { get; set; }
    public List<MojangVersionInfo> Versions { get; set; } = [];
    public string Message { get; set; } = "";
}
