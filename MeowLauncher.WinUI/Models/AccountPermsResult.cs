namespace MeowLauncher.WinUI.Models;

/// <summary>GET /player/attributes 的整理结果：好友请求权限、聊天过滤与多人游戏权限。</summary>
public sealed class AccountPermsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

    /// <summary>ENABLED / DISABLED；空串表示接口没给。</summary>
    public string Friends { get; set; } = "";
    public string AcceptInvites { get; set; } = "";
    public bool ProfanityFilterOn { get; set; }

    public Dictionary<string, bool> Privileges { get; set; } = new();
    public bool Banned { get; set; }

    public string FriendsText => Friends switch
    {
        "ENABLED" => "允许",
        "DISABLED" => "不允许",
        _ => "未知",
    };

    public string AcceptInvitesText => AcceptInvites switch
    {
        "ENABLED" => "允许",
        "DISABLED" => "不允许",
        _ => "未知",
    };
}
