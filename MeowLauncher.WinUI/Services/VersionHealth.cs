using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动前的文件体检：缺 jar / 库 / 资源时先拦住，自动补全。
/// 补全过程中弹一个带进度条的对话框，不让界面看起来卡死。
/// </summary>
public static class VersionHealth
{
    public static async Task<bool> EnsureReadyAsync(string versionId, XamlRoot? root = null, string gameRoot = "", IProgress<string>? stageProgress = null, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;
        root ??= App.MainWindow?.Content?.XamlRoot;
        if (root is null) return true;

        VerifyResult check;
        try
        {
            check = await App.Backend.VerifyVersionAsync(versionId, gameRoot, taskId);
        }
        catch (Exception ex)
        {
            if (TaskCenter.IsCancelled(taskId)) return false;
            Debug.WriteLine($"verify failed: {ex}");
            await new ContentDialog { Title = "无法验证实例文件", Content = "文件检查未完成，已停止启动。请检查目录权限后重试。", CloseButtonText = "返回", XamlRoot = root }.ShowAsync();
            return false;
        }
        if (TaskCenter.IsCancelled(taskId)) return false;
        if (!check.Ok)
        {
            await new ContentDialog { Title = "无法验证实例文件", Content = check.Message, CloseButtonText = "返回", XamlRoot = root }.ShowAsync();
            return false;
        }
        if (check.Clean && check.Missing <= 0) return true;

        if (stageProgress is not null)
        {
            stageProgress.Report("正在自动补全缺失文件…");
            BackendResult outcome;
            try { outcome = await App.Backend.RepairVersionAsync(versionId, stageProgress, gameRoot, taskId); }
            catch (Exception ex) { outcome = new BackendResult(false, Diagnostics.FriendlyMessage(ex)); }
            if (TaskCenter.IsCancelled(taskId)) return false;
            if (outcome.Ok) return true;
            await new ContentDialog
            {
                Title = "补全没有完成", Content = outcome.Message, CloseButtonText = "返回", XamlRoot = root,
            }.ShowAsync();
            return false;
        }

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 4 };
        var label = new TextBlock { Text = "正在检查缺哪些文件…", TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 14, MinWidth = 360 };
        body.Children.Add(label);
        body.Children.Add(bar);

        var progressDialog = new ContentDialog
        {
            Title = "正在补全文件",
            Content = body,
            XamlRoot = root,
        };
        var showTask = progressDialog.ShowAsync();

        var progress = new Progress<string>(stage =>
        {
            var text = stage ?? "";
            var split = text.IndexOf('|');
            if (split > 0 && int.TryParse(text[..split], out var pct))
            {
                if (pct > 0) bar.Value = pct;
                var rest = text[(split + 1)..];
                if (!string.IsNullOrWhiteSpace(rest)) label.Text = rest;
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                label.Text = text;
            }
        });

        BackendResult repair;
        try
        {
            repair = await App.Backend.RepairVersionAsync(versionId, progress, gameRoot, taskId);
        }
        catch (Exception ex)
        {
            repair = new BackendResult(false, Diagnostics.FriendlyMessage(ex));
        }
        finally
        {
            progressDialog.Hide();
            try { await showTask; } catch { }
        }

        if (TaskCenter.IsCancelled(taskId)) return false;
        if (repair.Ok) return true;

        await new ContentDialog
        {
            Title = "补全没有完成",
            Content = new TextBlock { Text = repair.Message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = root,
        }.ShowAsync();
        return false;
    }
}
