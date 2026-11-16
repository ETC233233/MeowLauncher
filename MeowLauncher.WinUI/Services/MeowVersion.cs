using System.Text.RegularExpressions;

namespace MeowLauncher.WinUI.Services;

/// <summary>版本档位：Review 开发版（不发布）、Beta 测试版、Release 正式版。</summary>
public enum MeowChannel
{
    Review,
    Beta,
    Release,
}

/// <summary>
/// MeowLauncher 的版本号（三档，见内部文档「版本号方案」）。不依赖界面和 App，方便单独测试。
///
/// 构建号 = &lt;年序&gt;&lt;月日&gt;-&lt;迭代次数&gt;，例如 <c>01113-7829</c>：0 = 项目第 0 年（2026），1113 = 11 月 13 日，7829 = 迭代次数（只增不减）。
///
/// 三档的写法：
/// <list type="bullet">
/// <item><b>Review</b>：<c>Review 01113-7829</c>。没有正式版本号，不发布，不做更新检查。</item>
/// <item><b>Beta</b>：<c>1.0.0-01113-7829</c>（版本号 + 构建号）。发行前同一个版本号下可以有多个 Beta。</item>
/// <item><b>Release</b>：<c>1.0.0</c>，「功能版本」是它晋升前的构建号 <c>01113-7829</c>（<c>1.0.0+01113-7829</c>）。</item>
/// </list>
///
/// 版本号 <c>主.次.修订</c>：修订 = 小幅更新（修问题）；次版本 = 大幅更新（如开放联机模块）；主版本 = 颠覆性更新（重构、彻底迁移）。
/// </summary>
public sealed record MeowVersion(MeowChannel Channel, Version? Core, string BuildId)
{
    /// <summary>构建号里「年序」的起点：2026 年是第 0 年。</summary>
    public const int FirstYear = 2026;

    private static readonly Regex BuildShape = new(@"^(?<year>\d+?)(?<md>\d{4})-(?<n>\d+)$", RegexOptions.Compiled);

    public bool IsReview => Channel == MeowChannel.Review;

    /// <summary>构建号里的迭代次数（只增不减）；构建号不合法返回 -1。</summary>
    public long Iteration => TryBuild(BuildId, out _, out _, out var n) ? n : -1;

    /// <summary>组装构建号：<paramref name="date"/> 当天、第 <paramref name="iteration"/> 次迭代。</summary>
    public static string MakeBuildId(DateTime date, long iteration) =>
        $"{date.Year - FirstYear}{date:MMdd}-{iteration}";

    /// <summary>界面上显示的完整写法：<c>Review 01113-7829</c> / <c>1.0.0-01113-7829</c> / <c>1.0.0</c>。</summary>
    public string Display => Channel switch
    {
        MeowChannel.Review => $"Review {BuildId}",
        MeowChannel.Beta => $"{Core}-{BuildId}",
        _ => Core?.ToString() ?? "",
    };

    /// <summary>标题栏等窄位置用的短写法（Review 不带「Review」前缀以外的东西）。</summary>
    public string Short => Display;

    /// <summary>「功能版本」：正式版显示它晋升前的构建号；其它档位没有这一行。</summary>
    public string FunctionVersion => Channel == MeowChannel.Release ? BuildId : "";

    /// <summary>该档位常驻提示的文案（关于页、首次启动的通知）；正式版没有。</summary>
    public string NoticeText => Channel switch
    {
        MeowChannel.Review => "当前使用的是非现发行的测试版本，无法检测此版本的更新，请保管好您的账号数据。",
        MeowChannel.Beta => "您正在使用MeowLauncher的预发行版本，此版本可能包含bug和不稳定内容，欢迎向我们的仓库提出issue反馈！",
        _ => "",
    };

    /// <summary>「这条提示显示过了吗」的记号：Review 每台机器一次；每个新的 Beta 构建各一次；正式版没有提示。</summary>
    public string NoticeKey => Channel switch
    {
        MeowChannel.Review => "review",
        MeowChannel.Beta => "beta|" + BuildId,
        _ => "",
    };

    /// <summary>档位的中文名。</summary>
    public string ChannelName => Channel switch
    {
        MeowChannel.Review => "开发版",
        MeowChannel.Beta => "测试版",
        _ => "正式版",
    };

    /// <summary>
    /// 解析各种写法：<c>Review 01113-7829</c>、<c>1.0.0-01113-7829</c>、<c>1.0.0</c>、<c>1.0.0+01113-7829</c>。
    /// 不认识的返回 null。
    /// </summary>
    public static MeowVersion? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("Review", StringComparison.OrdinalIgnoreCase))
        {
            var id = s["Review".Length..].Trim();
            return TryBuild(id, out _, out _, out _) ? new MeowVersion(MeowChannel.Review, null, id) : null;
        }
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        var plus = s.IndexOf('+');
        if (plus > 0)
        {
            var core = s[..plus];
            var id = s[(plus + 1)..];
            return Version.TryParse(core, out var v) && TryBuild(id, out _, out _, out _)
                ? new MeowVersion(MeowChannel.Release, Normalize(v), id) : null;
        }
        var dash = s.IndexOf('-');
        if (dash > 0)
        {
            var core = s[..dash];
            var id = s[(dash + 1)..];
            return Version.TryParse(core, out var v) && TryBuild(id, out _, out _, out _)
                ? new MeowVersion(MeowChannel.Beta, Normalize(v), id) : null;
        }
        return Version.TryParse(s, out var rel) ? new MeowVersion(MeowChannel.Release, Normalize(rel), "") : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool TryBuild(string id, out int year, out int monthDay, out long n)
    {
        year = 0; monthDay = 0; n = 0;
        var m = BuildShape.Match(id ?? "");
        return m.Success
            && int.TryParse(m.Groups["year"].Value, out year)
            && int.TryParse(m.Groups["md"].Value, out monthDay)
            && long.TryParse(m.Groups["n"].Value, out n);
    }

    /// <summary>
    /// 更新检查用的比较：返回负数 = this 比 other 旧，0 = 同一个，正数 = 更新。
    /// Review 没有版本号，不能参与比较（调用前先判断 <see cref="IsReview"/>），这里把它当作最旧。
    /// 规则：先比版本号数字；同一版本号下 Beta &lt; Release；两个 Beta 按迭代次数比（数字比较，不是字符串比较）。
    /// </summary>
    public int CompareTo(MeowVersion other)
    {
        if (IsReview || other.IsReview)
        {
            if (IsReview && other.IsReview) return Iteration.CompareTo(other.Iteration);
            return IsReview ? -1 : 1;
        }
        var c = Core!.CompareTo(other.Core!);
        if (c != 0) return c;
        if (Channel != other.Channel) return Channel == MeowChannel.Beta ? -1 : 1;
        return Channel == MeowChannel.Beta ? Iteration.CompareTo(other.Iteration) : 0;
    }

    /// <summary>
    /// 兼容判断（联机组件的 minLauncher / maxLauncher）用的版本：只取版本号数字部分，忽略构建号；
    /// Review 没有版本号，返回 null（调用方跳过区间判断，只看接口级别）。
    /// </summary>
    public Version? CoreForCompat => IsReview ? null : Core;
}
