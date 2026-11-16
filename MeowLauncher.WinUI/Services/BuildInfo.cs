namespace MeowLauncher.WinUI;

/// <summary>
/// 当前构建的版本信息。常量 Channel / VersionText / BuildId 由 csproj 的 MeowGenBuildInfo 目标生成（MeowBuildInfo.g.cs），
/// 来源是根目录的 version.props 和 build\review-counter.txt；这里是手写的那一半。
/// </summary>
public static partial class BuildInfo
{
    public static Services.MeowVersion Current { get; } = Build();

    private static Services.MeowVersion Build()
    {
        var channel = Channel.ToLowerInvariant() switch
        {
            "beta" => Services.MeowChannel.Beta,
            "release" => Services.MeowChannel.Release,
            _ => Services.MeowChannel.Review,
        };
        // Review 没有正式版本号（虽然将来是 VersionText）；Beta / Release 带版本号
        var core = channel == Services.MeowChannel.Review ? null : System.Version.TryParse(VersionText, out var v) ? v : new System.Version(0, 0, 0);
        return new Services.MeowVersion(channel, core, BuildId);
    }
}
