using Microsoft.UI.Xaml.Media;

namespace MeowLauncher.WinUI.Models;

/// <summary>
/// 在线资源列表的一行：列表模式只有「缩略图 / 标题+作者 / 下载量 / 一个动作」，
/// 这里把「是否已装」提前算好，模板里就不用再叠转换器。
/// </summary>
public sealed class OnlineItem
{
    public OnlineItem(ModrinthProject project, bool installed, bool chooseDownloadTarget = false)
    {
        Project = project;
        ChooseDownloadTarget = chooseDownloadTarget;
        IsInstalled = installed && !chooseDownloadTarget;
    }

    public ModrinthProject Project { get; }
    public bool IsInstalled { get; }
    public bool ChooseDownloadTarget { get; }

    public string Id => Project.Id;
    public string Title => Project.Title;
    public string Author => Project.Author;
    /// <summary>列表第二行的一句话简介：去标签、折叠空白，单行显示；没有简介就整行不占位。</summary>
    public string DescriptionLine
    {
        get
        {
            var raw = Project.Description ?? "";
            if (raw.Length == 0) return "";
            var text = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    public Microsoft.UI.Xaml.Visibility DescriptionVisibility =>
        DescriptionLine.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string MetaText => $"下载 {ShortNumber(Project.Downloads)} · {Project.TypeText}";

    /// <summary>ListViewItem 的 UIA 名称取对象 ToString，不重写会读出类型名。</summary>
    public override string ToString() => Title;
    public ImageSource? IconSource => Project.IconSource;

    /// <summary>跟随模式显示安装状态；非跟随模式进入下载与目标选择。</summary>
    public string ActionText => ChooseDownloadTarget ? "下载" : IsInstalled ? "已安装" : "安装";

    public bool ActionEnabled => !IsInstalled;

    /// <summary>未装显示描边「安装」按钮，已装改显示绿色 ✓ 文字（和下载页一致）。</summary>
    public Microsoft.UI.Xaml.Visibility InstallVisibility =>
        IsInstalled ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility InstalledVisibility =>
        IsInstalled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string ActionTooltip => ChooseDownloadTarget
        ? "按筛选条件选择版本，下载时选择目标实例或保存位置"
        : IsInstalled
        ? $"{Project.Title} 已经在当前实例里了"
        : Project.IsModpack
            ? "安装成一个新实例"
        : Project.IsInstallable
            ? "安装到当前实例"
            : "查看详情";

    private static string ShortNumber(long number) => number switch
    {
        >= 100_000_000 => $"{number / 100_000_000d:0.#} 亿",
        >= 10_000 => $"{number / 10_000d:0.#} 万",
        _ => number.ToString(),
    };
}
