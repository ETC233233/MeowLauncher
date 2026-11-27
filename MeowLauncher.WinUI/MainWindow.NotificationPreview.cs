using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI;

public sealed partial class MainWindow
{
    private readonly List<TaskEntry> _notificationPreviewTasks = [];
    private int _notificationPreviewVersion;

    internal void PlayNotificationPreview(string scenario)
    {
        CloseActivityCenter(false);
        ClearNotificationPreview();

        switch (scenario)
        {
            case "tasks":
                var completed = AddPreviewTask("安装 Fabulously Optimized");
                TaskCenter.Complete(completed, "整合包已安装，可以开始游玩。");
                var failed = AddPreviewTask("下载 NeoForge");
                TaskCenter.Fail(failed, "下载资源文件时连接超时。");
                break;
            case "pages":
                TaskCenter.PreviewNotice("账户", "已切换到所选账户。", "已完成", InfoBarSeverity.Success);
                TaskCenter.PreviewNotice("Mod 管理", "已导入 3 个 Mod。", "已完成", InfoBarSeverity.Success);
                TaskCenter.PreviewNotice("目录不存在", "请在实例设置里重新选择游戏目录。", "提示", InfoBarSeverity.Warning);
                break;
            case "settings":
                TaskCenter.PreviewNotice("日志已导出", "诊断日志已保存到你选择的文件夹。", "已完成", InfoBarSeverity.Success);
                TaskCenter.PreviewNotice("下载源已切换", "已切换到官方源优先。", "已完成", InfoBarSeverity.Success);
                TaskCenter.PreviewNotice("导入失败", "无法读取选中的资源包文件。", "失败", InfoBarSeverity.Error);
                break;
            case "updates":
                TaskCenter.PreviewNotice("有可用的启动器更新", "可在关于页查看版本信息。", "更新");
                TaskCenter.PreviewNotice("橘猫邀请你一起玩", "生存存档 · 好友邀请", "邀请", invite: true);
                break;
            case "announcement":
                TaskCenter.PreviewNotice("联机维护公告（演示）",
                    "预计 20:00–20:30 进行维护。期间暂时无法创建或加入房间，单人游戏可照常启动。",
                    "公告", addToHistory: true);
                break;
        }
    }

    internal void ClearNotificationPreview()
    {
        _notificationPreviewVersion++;
        foreach (var item in _visibleNotices.Where(item => item.Card.Notice.IsPreview).ToArray())
            RetireNotice(item);
        TaskCenter.ClearNoticePreviews();
        foreach (var task in _notificationPreviewTasks) TaskCenter.Dismiss(task);
        _notificationPreviewTasks.Clear();
        RefreshNoticeLayout();
    }

    private TaskEntry AddPreviewTask(string title)
    {
        var task = TaskCenter.Add(title, "通知演示", "download");
        task.IsNotificationPreview = true;
        _notificationPreviewTasks.Add(task);
        return task;
    }
}
