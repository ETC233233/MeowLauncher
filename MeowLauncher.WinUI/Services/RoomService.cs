using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public enum RoomPhase
{
    /// <summary>不在任何房间里。</summary>
    Idle,
    /// <summary>正在向房间服务建房。</summary>
    Creating,
    /// <summary>正在加入（申请中继准入、拉起提权代理、建隧道）。</summary>
    Joining,
    /// <summary>房主：房间已建好，等着选世界、点「开放」。</summary>
    Hosting,
    /// <summary>房主：隧道建立中，地址还没发布出来。</summary>
    Opening,
    /// <summary>房主：地址已发布，好友可以连了。</summary>
    Opened,
    /// <summary>成员：已加入并建好隧道；房主地址发布后会出现在 <see cref="RoomService.HostAddr"/>。</summary>
    Joined,
}

public sealed record RoomPlayer(string Name, bool IsOwner);

/// <summary>本机上正在监听的游戏端口（房主「对局域网开放」之后才会出现）。</summary>
public sealed record GameChoice(int Port, string Label);

/// <summary>
/// 房间服务的门面：不碰联机模块的任何类型，组件没装好时只给「空」答案；装好后由 <see cref="Attach"/> 接上 RoomCore。
/// </summary>
public sealed class RoomService : IDisposable
{
    private RoomCore? _core;

    public RoomPhase Phase => _core?.Phase ?? RoomPhase.Idle;
    public string Code => _core?.Code ?? "";
    public bool IsHost => _core?.IsHost ?? false;
    public string OwnerName => _core?.OwnerName ?? "";
    public string HostAddr => _core?.HostAddr ?? "";
    public string Message => _core?.Message ?? "";
    public DateTime MessageAt => _core?.MessageAt ?? DateTime.MinValue;
    public IReadOnlyList<RoomPlayer> Players => _core?.Players ?? Array.Empty<RoomPlayer>();
    public bool InRoom => _core?.InRoom ?? false;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action? RoomClosed;

    internal void Attach()
    {
        if (_core is not null) return;
        var core = ModuleBinder.NewRoom();
        core.Changed += () => Changed?.Invoke();
        core.Log += s => Log?.Invoke(s);
        core.RoomClosed += () => RoomClosed?.Invoke();
        _core = core;
    }

    public List<GameChoice> ScanGames() => _core?.ScanGames() ?? new List<GameChoice>();
    public Task<bool> CreateAsync() => _core?.CreateAsync() ?? Task.FromResult(false);
    public Task<bool> OpenAsync(int port) => _core?.OpenAsync(port) ?? Task.FromResult(false);
    public Task<bool> JoinAsync(string code) => _core?.JoinAsync(code) ?? Task.FromResult(false);
    public Task CloseAsync() => _core?.CloseAsync() ?? Task.CompletedTask;
    public void Dispose() => _core?.Dispose();
}

/// <summary>
/// 创建 OnlineCore / RoomCore 的地方单独放在不内联的方法里：门面类被 JIT 时不会因为内联而提前去加载联机模块。
/// 只有联机组件的程序集解析器就绪之后才会调用这里。
/// </summary>
internal static class ModuleBinder
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static OnlineCore NewOnline() => new OnlineCore();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static RoomCore NewRoom() => new RoomCore();

    /// <summary>告诉模块联机组件解压在哪（它去那里找 RoomAgent.exe 和 EasyTier）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void SetRoot(string dir) => MeowRoom.Services.AgentPaths.ComponentRoot = dir;
}
