using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动前的文件体检：缺 jar / 库 / 资源时先拦住，问一句再补全。
/// 补全过程中弹一个带进度条的对话框，不让界面看起来卡死。
/// </summary>
public static class VersionHealth
{
    public static async Task<bool> EnsureReadyAsync(string versionId, XamlRoot? root = null)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;
        root ??= App.MainWindow?.Content?.XamlRoot;
        if (root is null) return true;

        VerifyResult check;
        try
        {
            check = await App.Backend.VerifyVersionAsync(versionId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"verify failed: {ex}");
            return true; // 检查本身出错就别挡着启动
        }

        if (!check.Ok || check.Clean || check.Missing <= 0) return true;

        var detail = new StackPanel { Spacing = 6 };
        detail.Children.Add(new TextBlock
        {
            Text = check.Message,
            TextWrapping = TextWrapping.Wrap,
        });
        detail.Children.Add(new TextBlock
        {
            Text = "可以先补全，补完会自动继续启动。",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });

        var ask = new ContentDialog
        {
            Title = "这个实例的文件不完整",
            Content = detail,
            PrimaryButtonText = "补全并启动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return false;

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
            repair = await App.Backend.RepairVersionAsync(versionId, progress);
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
