using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;

namespace MeowLauncher.WinUI;

public sealed partial class MainWindow
{
    private bool _refreshingAnnouncements;

    private void StartAnnouncementRefresh()
    {
        var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(15);
        timer.Tick += async (_, _) => await RefreshAnnouncementsAsync(token);
        Closed += (_, _) =>
        {
            timer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        };
        timer.Start();
        _ = RefreshAnnouncementsAsync(token);
    }

    private async Task RefreshAnnouncementsAsync(CancellationToken token)
    {
        if (_refreshingAnnouncements || token.IsCancellationRequested || !CanShowAnnouncements()) return;
        _refreshingAnnouncements = true;
        try
        {
            TaskCenter.PruneExpiredAnnouncements(DateTimeOffset.UtcNow);
            var (payload, _) = await SignedConfig.FetchAsync(ct: token);
            if (payload is not null && !token.IsCancellationRequested && CanShowAnnouncements())
                TaskCenter.SyncAnnouncements(LauncherAnnouncement.ReadActive(payload, DateTimeOffset.UtcNow), _preferences);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { App.Trace("Announcements: " + ex.GetType().Name); }
        finally { _refreshingAnnouncements = false; }
    }

    private bool CanShowAnnouncements() => OnboardingHost.Visibility != Visibility.Visible
        && OnboardingSketch.Visibility != Visibility.Visible;
}
