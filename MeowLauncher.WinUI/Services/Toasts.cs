using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 系统通知（Windows App SDK AppNotification）。未打包运行时 Register() 会抛，
/// 这里吞掉并记录一次，调用方继续走原来的任务栏闪烁兜底。
/// </summary>
internal static class Toasts
{
    private static bool _registered;
    private static bool _failed;

    public static bool Available => !_failed;

    public static bool TryShow(string title, string body)
    {
        if (_failed) return false;
        try
        {
            if (!_registered)
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Debug.WriteLine($"[Toasts] 系统通知不可用：{ex.GetType().Name} {ex.Message}");
            try
            {
                var log = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MeowLauncher.WinUI", "toast-fail.log");
                System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
            return false;
        }
    }
}
