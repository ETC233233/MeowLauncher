using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace MeowLauncher.WinUI.Services;

/// <summary>启动失败的诊断信息：给用户一个可复制的错误码与上下文，而不是只看到一句异常。</summary>
public static class Diagnostics
{
    public static string ErrorCodeFor(string? message)
    {
        var text = message ?? "";
        if (text.Contains("Java", StringComparison.OrdinalIgnoreCase)
            || text.Contains("JVM", StringComparison.OrdinalIgnoreCase)) return "JAVA-01";
        if (text.Contains("登录") || text.Contains("账号") || text.Contains("账户")) return "ACC-01";
        if (text.Contains("超时") || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "NET-01";
        if (text.Contains("FileNotFound") || text.Contains("不存在") || text.Contains("找不到版本")) return "VER-01";
        if (text.Contains("版本")) return "VER-02";
        return "LAUNCH-01";
    }

    /// <summary>把 .NET 异常翻译成一句用户能懂的话；技术细节仍留在诊断报告里。</summary>
    public static string FriendlyMessage(Exception ex)
    {
        var text = ex.Message ?? "";
        if (ex is TimeoutException
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("超时")) return "等待服务器响应超时，请检查网络或代理后重试。";
        if (ex is System.Net.Http.HttpRequestException) return "连不上网络，请检查网络或代理设置。";
        if (ex is UnauthorizedAccessException) return "没有权限访问这个位置，请换一个目录或检查权限。";
        if (ex is DirectoryNotFoundException || ex is FileNotFoundException) return "找不到需要的文件，它可能已被移动或删除。";
        if (ex is IOException) return "读写文件失败，请检查文件是否被占用、磁盘是否还有空间。";
        if (ex is OperationCanceledException) return "操作已取消。";
        return "操作没有完成，请重试一次。";
    }

    public static string BuildReport(string code, string stage, string message, string versionId, string? lastStage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MeowLauncher 诊断报告");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"错误码：{code}");
        sb.AppendLine($"环节：{stage}");
        if (!string.IsNullOrWhiteSpace(versionId)) sb.AppendLine($"实例：{versionId}");
        if (!string.IsNullOrWhiteSpace(lastStage)) sb.AppendLine($"最后阶段：{lastStage}");
        sb.AppendLine($"说明：{message}");
        sb.AppendLine($"启动器：{VersionText()}");
        sb.AppendLine($"系统：{Environment.OSVersion} · {RuntimeInformation.OSArchitecture}");
        return sb.ToString();
    }

    public static void Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            // 剪贴板被别的进程占用时静默失败，不影响主流程。
        }
    }

    private static string VersionText()
    {
        try
        {
            return BuildInfo.Current.Display;
        }
        catch
        {
            return "?";
        }
    }
}
