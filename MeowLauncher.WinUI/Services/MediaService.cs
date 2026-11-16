using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;

namespace MeowLauncher.WinUI.Services;

/// <summary>系统媒体会话的只读快照：封面、播放状态、进度都由 Windows 提供，没有就留空。</summary>
public sealed record MediaInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    ImageSource? Cover = null);

public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            HookSession(_manager.GetCurrentSession());
            _ = RefreshAsync();
        }
        catch
        {
            // 未授权或系统媒体会话不可用时保持静态卡，不影响启动器。
        }
    }

    public async Task<MediaInfo?> GetInfoAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null) return null;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var status = session.GetPlaybackInfo().PlaybackStatus;
            var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // 时长/位置来自会话时间轴；结束时间小于零的会话表示拿不到，按 0 处理，不猜。
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            var position = timeline.Position;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;

            ImageSource? cover = null;
            try
            {
                if (properties.Thumbnail is not null)
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync();
                    if (stream is not null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream);
                        cover = bitmap;
                    }
                }
            }
            catch
            {
                // 拿不到封面就用图标兜底。
            }

            return new MediaInfo(
                string.IsNullOrWhiteSpace(properties.Title) ? "没有正在播放" : properties.Title,
                string.IsNullOrWhiteSpace(properties.Artist) ? "系统媒体" : properties.Artist,
                playing,
                position,
                duration,
                cover);
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null) return;
        try { await session.TryTogglePlayPauseAsync(); } catch { }
        _ = RefreshAsync();
    }

    public async Task NextAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null) return;
        try { await session.TrySkipNextAsync(); } catch { }
        _ = RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null) return;
        try { await session.TrySkipPreviousAsync(); } catch { }
        _ = RefreshAsync();
    }

    private void Manager_CurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        HookSession(sender.GetCurrentSession());
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RefreshAsync();
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        }

        _session = session;
        if (_session is null) return;
        _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
    }

    private void Session_MediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private void Session_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private Task RefreshAsync() => Task.CompletedTask;
}
