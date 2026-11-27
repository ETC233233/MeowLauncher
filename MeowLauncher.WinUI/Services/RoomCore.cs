using System.Text.Json;
using MeowRoom.Services;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动器里的联机房间服务（真正干活的部分，用到联机模块的类型，只在联机组件装好后才会被创建；外面请用 RoomService）：用联机模块（RoomApiClient / RoomSocket / AgentClient / GameInfo）把
/// 「建房 → 开放 → 好友加入 → 连隧道 → 关房」串起来。
///
/// 登录用的是联机账号服务签发的会话票（App.Online.Token），房间服务通过服务绑定认它，不需要验证码。
/// 提权只发生在 RoomAgent（启动器本体不提权），只有「开放」和「加入」时才会弹一次 UAC。
/// 状态变化通过 <see cref="Changed"/> 通知；事件可能在后台线程触发，界面要自己切回界面线程。
/// </summary>
internal sealed class RoomCore : IDisposable
{
    private readonly RoomApiClient _api;
    private readonly RoomSocket _socket;
    private readonly AgentClient _agent = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _roomCts;

    private string _secret = "";
    private bool _agentUsed;                // 拉起过提权代理才需要去停它（没拉起过就不发 stop，免得日志里冒出「联机模块还没连上」）
    private List<RoomPlayer> _players = new();

    public RoomPhase Phase { get; private set; } = RoomPhase.Idle;
    public string Code { get; private set; } = "";
    public bool IsHost { get; private set; }
    public string OwnerName { get; private set; } = "";
    public string HostAddr { get; private set; } = "";

    /// <summary>最近一条给人看的提示（成功或失败原因）。</summary>
    public string Message { get; private set; } = "";

    /// <summary>这条提示是什么时候写的。没在房间里时，过了一会儿的旧提示（比如「还没登录好」）界面不再显示。</summary>
    public DateTime MessageAt { get; private set; } = DateTime.MinValue;

    public IReadOnlyList<RoomPlayer> Players => _players;
    public bool InRoom => Phase != RoomPhase.Idle;

    /// <summary>任何状态变化（含推送来的成员 / 地址变化）。</summary>
    public event Action? Changed;

    /// <summary>调试日志（隧道、提权、实时通道的过程）。</summary>
    public event Action<string>? Log;

    /// <summary>房间被房主关掉了（成员端收到）。</summary>
    public event Action? RoomClosed;

    public RoomCore()
    {
        _api = new RoomApiClient(() => App.Online.Token);
        _socket = new RoomSocket(() => App.Online.Token);

        _api.Log += s => Log?.Invoke(s);
        _socket.Log += s => Log?.Invoke(s);
        _agent.Log += s => Log?.Invoke(s);

        _api.SessionExpired += () => Fail("联机登录过期了，启动器正在自动重新登录，稍后再试一次。");
        _socket.Unauthorized += () => Fail("联机登录过期了，请重新进入房间。");
        _socket.State += OnRoomState;
        _socket.Closed += () =>
        {
            StopAgent();                        // 房主关了房间：本机的隧道也要跟着停，不留后台提权进程在跑
            Reset("房主关闭了房间。");
            RoomClosed?.Invoke();
        };
        // 隧道里发布 / 收到的房主地址（加入端等房主发布，房主端是自己发布的）
        _agent.Addr += addr => ApplyAddr(addr);
    }

    // ------------------------------------------------------------------ 扫描本机的游戏端口

    /// <summary>
    /// 本机正在监听的 java 端口，带上日志里能读到的玩家 / 版本 / 世界。扫端口不需要管理员，
    /// 但会读系统的 TCP 表，别在界面线程里同步调。
    /// </summary>
    public List<GameChoice> ScanGames()
    {
        var found = McPortScanner.ListenPorts();           // pid → 端口
        var result = new List<GameChoice>();
        var described = new HashSet<int>();

        // 日志里有更完整的身份信息；日志可能过期，所以只用来给「真的在监听」的端口补充说明，不凭它新增候选
        foreach (var s in GameInfo.Discover())
        {
            if (!found.ContainsValue(s.Port) || !described.Add(s.Port)) continue;
            var parts = new List<string>();
            if (s.Player.Length > 0) parts.Add(s.Player);
            if (s.Version.Length > 0) parts.Add(s.Version);
            if (s.World.Length > 0) parts.Add("世界 " + s.World);
            parts.Add("端口 " + s.Port);
            result.Add(new GameChoice(s.Port, string.Join(" · ", parts)));
        }
        foreach (var kv in found)
            if (!described.Contains(kv.Value)) result.Add(new GameChoice(kv.Value, $"java #{kv.Key} · 端口 {kv.Value}"));
        return result;
    }

    // ------------------------------------------------------------------ 房主：建房

    public async Task<bool> CreateAsync()
    {
        if (!CanStart(out var why)) { Say(why); return false; }
        SetPhase(RoomPhase.Creating, "正在创建房间…");

        var (status, body) = await _api.CallAsync(HttpMethod.Post, "/api/room/create", new { });
        if (status != 200)
        {
            SetPhase(RoomPhase.Idle, ErrorText(status, body, "创建房间失败"));
            return false;
        }

        lock (_gate)
        {
            Code = Str(body, "roomCode");
            _secret = Str(body, "secret");
            IsHost = true;
            OwnerName = App.Online.MyName;
            HostAddr = "";
            _players = new List<RoomPlayer> { new(OwnerName, true) };
        }
        StartRoomLoops();
        SetPhase(RoomPhase.Hosting, "房间已创建。先在游戏里「对局域网开放」，再回来点「开放」。");
        return true;
    }

    // ------------------------------------------------------------------ 房主：开放世界

    public async Task<bool> OpenAsync(int port)
    {
        if (Phase != RoomPhase.Hosting) { Say("先创建房间再开放。"); return false; }
        if (!NetworkAvailability.CanStartRequest) { Say(NetworkAvailability.OfflineMessage); return false; }
        if (!McPortScanner.IsListening(port))
        {
            Say($"端口 {port} 现在没有在监听，先在游戏里点「对局域网开放」。");
            return false;
        }

        SetPhase(RoomPhase.Opening, "正在建立隧道（会弹出一次管理员授权）…");
        if (!await EnsureRelayAsync()) { SetPhase(RoomPhase.Hosting, "中继准入失败，稍后再试。"); return false; }

        // 房间服务地址 / 中继 / 组件哈希都由提权代理自己拉取并验签，这里只传房间、密钥、端口和票
        var ok = await _agent.StartAsync(Code, _secret, expose: true, token: App.Online.Token, port: port);
        _agentUsed = _agentUsed || ok;
        if (!ok) { SetPhase(RoomPhase.Hosting, "联机模块没起来：没有拿到管理员权限，或组件不完整。"); return false; }

        Say("隧道启动中，地址出来了会自动显示。");
        return true;
    }

    // ------------------------------------------------------------------ 成员：加入

    public async Task<bool> JoinAsync(string code)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) { Say("房间码是 6 位数字。"); return false; }
        if (!CanStart(out var why)) { Say(why); return false; }
        SetPhase(RoomPhase.Joining, "正在加入房间…");

        var (status, body) = await _api.CallAsync(HttpMethod.Post, "/api/room/join", new { roomCode = code });
        if (status != 200)
        {
            SetPhase(RoomPhase.Idle, status == 404 ? "房间不存在，或房主已经关掉了。" : ErrorText(status, body, "加入房间失败"));
            return false;
        }

        lock (_gate)
        {
            Code = Str(body, "roomCode");
            _secret = Str(body, "secret");
            IsHost = false;
            HostAddr = "";
            OwnerName = "";
            _players = (body.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Select(p => p.GetString() ?? "").Where(n => n.Length > 0).ToList()
                : new List<string>()).Select(n => new RoomPlayer(n, false)).ToList();
        }
        StartRoomLoops();

        if (!await EnsureRelayAsync()) { await LeaveSilentlyAsync(); Say("中继准入失败，已退出房间。"); return false; }
        var ok = await _agent.StartAsync(Code, _secret, expose: false, token: App.Online.Token);
        _agentUsed = _agentUsed || ok;
        if (!ok) { await LeaveSilentlyAsync(); Say("联机模块没起来：没有拿到管理员权限，或组件不完整。已退出房间。"); return false; }

        SetPhase(RoomPhase.Joined, "已加入，等房主开放游戏。");
        return true;
    }

    // ------------------------------------------------------------------ 关房 / 离开

    /// <summary>房主关房，成员离开。无论服务端回什么，本地都会清干净（隧道停掉、实时通道断开）。</summary>
    public async Task CloseAsync()
    {
        if (!InRoom) return;
        var code = Code;
        var host = IsHost;
        try
        {
            StopAgent();
            await _api.CallAsync(HttpMethod.Post, host ? "/api/room/close" : "/api/room/leave", new { roomCode = code }, quiet: true);
        }
        catch { /* 网络问题：本地照样清理，房间 6 小时后服务端会自己收掉 */ }
        Reset(host ? "房间已关闭。" : "已离开房间。");
    }

    private async Task LeaveSilentlyAsync()
    {
        try { StopAgent(); await _api.CallAsync(HttpMethod.Post, "/api/room/leave", new { roomCode = Code }, quiet: true); } catch { }
        Reset("");
    }

    // ------------------------------------------------------------------ 内部

    private void StopAgent()
    {
        if (!_agentUsed) return;
        _agentUsed = false;
        _agent.Stop();
    }

    private bool CanStart(out string why)
    {
        why = "";
        if (!NetworkAvailability.CanStartRequest) { why = NetworkAvailability.OfflineMessage; return false; }
        if (InRoom && Phase != RoomPhase.Creating && Phase != RoomPhase.Joining) { why = "你已经在一个房间里了，先关闭或离开它。"; return false; }
        if (Phase is RoomPhase.Creating or RoomPhase.Joining) { why = "正在处理，请稍等。"; return false; }
        if (!App.Online.IsLoggedIn) { why = "联机账号还没登录好，稍等几秒再试。"; return false; }
        return true;
    }

    /// <summary>向房间服务申请中继准入（只有房间成员有资格）。被明确拒绝返回 false；闸门没部署（503）按直连处理，不挡人。</summary>
    private async Task<bool> EnsureRelayAsync()
    {
        var (code, body) = await _api.CallAsync(HttpMethod.Post, "/api/relay/open", new { roomCode = Code });
        if (code is >= 200 and < 300) return true;
        if (code is 401 or 403) { Log?.Invoke("中继准入被拒绝：" + Str(body, "error")); return false; }
        Log?.Invoke($"中继准入未启用（HTTP {code}）{Str(body, "error")}，按直连处理");
        return true;
    }

    private void StartRoomLoops()
    {
        _roomCts?.Cancel();
        var cts = new CancellationTokenSource();
        _roomCts = cts;
        var code = Code;
        _ = Task.Run(() => _socket.RunAsync(code, cts.Token));
        _ = Task.Run(() => RelayRenewLoopAsync(cts.Token));
    }

    /// <summary>中继许可只有 10 分钟：房间里每 4 分钟续一次，免得隧道重连时被闸门拒绝。</summary>
    private async Task RelayRenewLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(4), ct);
                if (Phase is RoomPhase.Opened or RoomPhase.Joined or RoomPhase.Opening) await EnsureRelayAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>实时通道推来的房间状态：成员、房主、房主发布的地址。</summary>
    private void OnRoomState(JsonElement room)
    {
        lock (_gate)
        {
            var owner = Str(room, "owner");
            if (owner.Length > 0) OwnerName = owner;
            if (room.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array)
                _players = ps.EnumerateArray().Select(p => p.GetString() ?? "").Where(n => n.Length > 0)
                    .Select(n => new RoomPlayer(n, n == OwnerName)).ToList();
            if (room.TryGetProperty("addrs", out var addrs) && addrs.ValueKind == JsonValueKind.Object
                && OwnerName.Length > 0 && addrs.TryGetProperty(OwnerName, out var a) && a.ValueKind == JsonValueKind.String)
            {
                var addr = a.GetString() ?? "";
                if (addr.Length > 0) HostAddr = addr;
            }
        }
        AfterAddrOrMembers();
    }

    private void ApplyAddr(string addr)
    {
        // 隧道代理发来的地址消息：成员端用它显示房主地址；房主端的地址以服务端推送为准
        if (IsHost) return;
        lock (_gate) { if (addr.Length > 0) HostAddr = addr; }
        AfterAddrOrMembers();
    }

    private void AfterAddrOrMembers()
    {
        if (IsHost && Phase == RoomPhase.Opening && HostAddr.Length > 0)
        {
            Phase = RoomPhase.Opened;
            Message = "房间已开放，好友现在可以加入了。";
        }
        Changed?.Invoke();
    }

    private void SetPhase(RoomPhase phase, string message)
    {
        Phase = phase;
        if (message.Length > 0) { Message = message; MessageAt = DateTime.Now; }
        Changed?.Invoke();
    }

    private void Say(string message)
    {
        Message = message;
        MessageAt = DateTime.Now;
        Changed?.Invoke();
    }

    private void Fail(string message)
    {
        Message = message;
        MessageAt = DateTime.Now;
        Changed?.Invoke();
    }

    private void Reset(string message)
    {
        _roomCts?.Cancel();
        _roomCts = null;
        lock (_gate)
        {
            Code = "";
            _secret = "";
            IsHost = false;
            OwnerName = "";
            HostAddr = "";
            _players = new List<RoomPlayer>();
        }
        Phase = RoomPhase.Idle;
        if (message.Length > 0) { Message = message; MessageAt = DateTime.Now; }
        Changed?.Invoke();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ErrorText(int status, JsonElement body, string fallback)
    {
        var err = Str(body, "error");
        if (status == 0) return err.Length > 0 ? err : "联机服务暂时不可用。";
        if (status == 401) return "联机登录过期了，启动器正在自动重新登录，稍后再试一次。";
        if (status == 409 && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("needCaptcha", out _))
            return "房间服务要求人机验证，说明它没认出你的联机登录。请先确认联机账号已登录。";
        if (status == 429) return err.Length > 0 ? err : "操作太频繁，请稍后再试。";
        return err.Length > 0 ? $"{fallback}：{err}" : $"{fallback}（HTTP {status}）";
    }

    public void Dispose()
    {
        _roomCts?.Cancel();
        try { StopAgent(); } catch { }
        _agent.Dispose();
        _api.Dispose();
    }
}
