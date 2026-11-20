using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace MeowLauncher.WinUI.Services;

public sealed record FailureInfo(string Code, DateTimeOffset At, string Stage)
{
    public string TimeText => At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
}

/// <summary>启动失败的诊断信息：给用户一个可复制的错误码与上下文，而不是只看到一句异常。</summary>
public static class Diagnostics
{
    public static string ErrorCodeFor(string? message, TaskKind kind = TaskKind.Other)
    {
        var text = message ?? "";
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Has("WinError 32") || Has("WinError 33") || Has("被占用")) return "FILE-IN-USE";
        if (Has("WinError 112") || Has("磁盘空间不足")) return "FILE-DISK-FULL";
        if (Has("权限") || Has("拒绝访问") || Has("PermissionError") || Has("access denied") || Has("无法写入")) return "FILE-ACCESS";
        if (Has("超时") || Has("timeout") || Has("timed out")) return "NET-TIMEOUT";
        if (Has("下载失败") || Has("连接失败") || Has("连不上网络")) return "NET-DOWNLOAD";
        if (Has("不完整") || Has("校验失败") || Has("hash mismatch") || Has("sha1 mismatch")) return "FILE-INTEGRITY";
        if (Has("BadZipFile") || Has("整合包格式") || Has("整合包清单") || Has("不是有效的 Modrinth 整合包")) return "PACK-INVALID";
        if (Has("FileNotFound") || Has("DirectoryNotFound") || Has("找不到要导入")) return "FILE-MISSING";
        if (Has("已经有叫") || Has("FileExistsError")) return "FILE-EXISTS";
        if (Has("Java") || Has("JVM")) return "JAVA-RUNTIME";
        if (Has("登录") || Has("账号") || Has("账户")) return "ACCOUNT-SIGNIN";
        if (Has("找不到版本") || Has("找不到 Mojang 版本") || Has("没有指定要安装的版本")) return "VERSION-MISSING";
        if (Has("检测到崩溃报告")) return "GAME-CRASH";
        if (Has("退出码") && kind == TaskKind.Launch) return "GAME-EXIT";
        return kind switch
        {
            TaskKind.Launch => "LAUNCH-UNEXPECTED",
            TaskKind.Install => "INSTALL-UNEXPECTED",
            _ => "TASK-UNEXPECTED",
        };
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

    public static string BuildReport(FailureInfo failure, string task, string message, string versionId = "")
    {
        message = Trim(message, 4000);
        return BuildReportCore(failure, task, message, versionId);
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "\n…（已截断）";

    private static string BuildReportCore(FailureInfo failure, string task, string message, string versionId = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("MeowLauncher 诊断报告");
        sb.AppendLine($"发生时间：{failure.TimeText}");
        sb.AppendLine($"错误码：{failure.Code}");
        sb.AppendLine($"任务：{task}");
        if (!string.IsNullOrWhiteSpace(versionId)) sb.AppendLine($"实例：{versionId}");
        if (!string.IsNullOrWhiteSpace(failure.Stage)) sb.AppendLine($"失败环节：{failure.Stage}");
        if (message.Contains('\n'))
        {
            // 多行说明（例如安装器原始输出）单独成块，别和前面的键值挤一行。
            sb.AppendLine("说明：");
            foreach (var line in message.Replace("\r", "").Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                sb.AppendLine("  " + line);
            }
        }
        else
        {
            sb.AppendLine($"说明：{message}");
        }
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
