namespace MeowLauncher.WinUI.Models;

public sealed class FriendInfo
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Online { get; set; }
    public bool Joinable { get; set; }

    /// <summary>好友上报在线时带了 MeowLauncher 标记（官方客户端的 joinInfo.value 恒为 null）。标记可伪造，只用于分类显示。</summary>
    public bool Meow { get; set; }

    /// <summary>头像文件（皮肤渲染出的透明头像 PNG）。取不到时为空，界面退回首字母。</summary>
    public string HeadPath { get; set; } = "";

    public string StatusText => !Online ? "离线" : Meow ? "MeowLauncher 在线" : "在线";
}

public sealed class FriendsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<FriendInfo> Friends { get; set; } = new();
    public List<FriendInfo> Incoming { get; set; } = new();
    public List<FriendInfo> Outgoing { get; set; } = new();
    public bool Empty { get; set; }
    public string PresenceError { get; set; } = "";

    /// <summary>没有上报在线状态的原因："online-disabled"（联机功能没开）、"game-running"（游戏在运行，避免覆盖）、"disabled"（关了显示在线）、空串=正常上报。</summary>
    public string PresenceSkipped { get; set; } = "";
}

/// <summary>联机握手用的玩家身份（名字 + UUID）。不含任何令牌。</summary>
public sealed class MojangIdentity
{
    public bool Ok { get; set; }
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>联机登录的身份证明（Mojang 玩家证书 + 对挑战码的签名）。不含令牌和私钥。</summary>
public sealed class MojangProof
{
    public bool Ok { get; set; }
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public long ExpiresAtMs { get; set; }
    public string CertSignature { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>单独上报在线状态（心跳 / 退出）的结果。</summary>
public sealed class PresenceReport
{
    public bool Ok { get; set; }
    public string Skipped { get; set; } = "";
    public string Error { get; set; } = "";
    public int MeowOnline { get; set; }
}

/// <summary>
/// 开发者测试功能的三层开关：认证（Unlocked）→ 开发者测试功能总开关（DevFeatures）→ 联机功能（OnlineEnabled）。
/// 第一版发布不带联机，所以默认全部关闭，「开发者测试」入口也隐藏；通过开发者认证后才出现。
/// </summary>
public sealed class DevSettings
{
    public bool Ok { get; set; }

    /// <summary>已通过开发者认证：设置里才会出现「开发者测试」入口。</summary>
    public bool Unlocked { get; set; }

    /// <summary>「开发者测试功能」总开关。关着的时候联机开关不可用。</summary>
    public bool DevFeatures { get; set; }

    /// <summary>联机功能开关（用户在子页里的选择）。</summary>
    public bool OnlineEnabled { get; set; }

    public bool ShareOnline { get; set; } = true;

    /// <summary>联机功能此刻是否真正生效（三层都开 / 环境变量强制启用）。</summary>
    public bool OnlineActive { get; set; }

    /// <summary>环境变量 MEOW_ONLINE 强制启用了联机（测试用）。</summary>
    public bool EnvOverride { get; set; }
}
