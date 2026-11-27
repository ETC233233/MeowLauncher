using MeowRoom.Services;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 启动器里的联机服务（真正干活的部分，会用到联机模块的类型，只在联机组件装好之后才会被创建；外面请用 OnlineService）：用联机模块（MeowRoom.Module）完成「Minecraft 登录握手 → 会话票 → 邀请推送」。
///
/// 只在「开发者认证 → 开发者测试功能 → 联机」三层开关都打开时才会运行（见 MainWindow.ApplyOnlineSettingAsync）。
/// 玩家的访问令牌始终留在本机的 Python 后端里，只会发给 Mojang；账号服务拿到的只是一次性挑战码和玩家名。
/// </summary>
internal sealed class OnlineCore
{
    private readonly AccountService _account = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    /// <summary>已知的 Mojang 好友 UUID（32 位小写）。好友页每次读到列表就更新；收到邀请时用它过滤陌生人。</summary>
    private HashSet<string> _friendIds = new();

    /// <summary>好友 UUID → 好友名（来自 Mojang 好友列表）。邀请通知上的名字以它为准：票里的名字是对方启动器自己报的，不可信。</summary>
    private Dictionary<string, string> _friendNames = new();

    public string Status { get; private set; } = "";

    /// <summary>当前联机会话票（给房间服务和提权代理用）。没登录是空串。</summary>
    public string Token => _account.IsLoggedIn ? _account.Session?.Token ?? "" : "";

    /// <summary>自己的正版名（联机登录时记下）。</summary>
    public string MyName { get; private set; } = "";
    public bool IsRunning => _cts is not null;
    public bool IsLoggedIn => _account.IsLoggedIn;

    /// <summary>状态变化（登录成功、连接断开、会话过期……），给日志 / 调试用。</summary>
    public event Action<string>? StatusChanged;

    public OnlineCore()
    {
        _account.Log += SetStatus;
        _account.Invite += OnInvite;
        _account.InviteReply += OnInviteReply;
        _account.SessionExpired += () => SetStatus("联机会话已过期，正在重新登录…");
    }

    public void UpdateFriends(IEnumerable<(string Id, string Name)> friends)
    {
        var list = friends.Select(f => (Id: Normalize(f.Id), f.Name)).Where(f => f.Id.Length == 32).ToList();
        _friendIds = new HashSet<string>(list.Select(f => f.Id));
        _friendNames = list.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Name);
    }

    private string ShownName(string uuid, string claimed) =>
        _friendNames.TryGetValue(uuid, out var n) && n.Length > 0 ? n : claimed;

    /// <summary>开始联机：登录握手并保持推送通道。重复调用无副作用。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            OnlineSwitch.Enabled = true;
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(ct));
        }
    }

    /// <summary>停止联机：断开推送通道、丢掉会话，并把模块的总开关关上（之后模块不再产生任何网络请求）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _account.Logout();
            OnlineSwitch.Enabled = false;
        }
        SetStatus("联机已停止");
    }

    // 已发出、对方还没回复的邀请：同一个人同一个房间 5 分钟内不重复发
    private static readonly TimeSpan InviteHold = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTime At, string Room)> _sentInvites = new();

    /// <summary>这个人是不是刚被邀请过、还在等回复（按钮据此显示「已邀请」）。</summary>
    public bool IsInvitePending(string toUuid, string roomCode = "")
    {
        lock (_sentInvites)
        {
            return _sentInvites.TryGetValue(Normalize(toUuid), out var s)
                && DateTime.UtcNow - s.At < InviteHold && s.Room == roomCode;
        }
    }

    public async Task<(bool Ok, bool Delivered, string Message)> InviteAsync(string toUuid, string roomCode = "")
    {
        var id = Normalize(toUuid);
        if (IsInvitePending(id, roomCode))
        {
            return (true, false, "已经邀请过了，等对方回复就好（5 分钟后可再邀请）。");
        }
        var result = await _account.InviteAsync(id, roomCode);
        if (result.Ok)
        {
            lock (_sentInvites) _sentInvites[id] = (DateTime.UtcNow, roomCode);
        }
        return result;
    }

    /// <summary>回复收到的邀请。接受 / 拒绝都会通知邀请人。</summary>
    public Task<(bool Ok, bool Delivered, string Message)> ReplyInviteAsync(string toUuid, bool accepted) =>
        _account.ReplyInviteAsync(Normalize(toUuid), accepted);

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_account.IsLoggedIn || NeedsRenewal())
                {
                    var ok = await LoginAsync(ct);
                    if (!ok && !_account.IsLoggedIn)
                    {
                        await Task.Delay(backoff, ct);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
                        continue;
                    }
                    if (ok) backoff = TimeSpan.FromSeconds(5);
                }

                // 一直连着；会话过期会返回并清掉会话，下一轮循环自动重新登录。
                // 另外在到期前 10 分钟主动中断推送，回到循环顶部用玩家证书重新登录续票（服务端的票只有 2 小时）。
                using var renew = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_account.Session is { } cur)
                {
                    var left = TimeSpan.FromMilliseconds(cur.ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var wait = left - TimeSpan.FromMinutes(10);
                    renew.CancelAfter(wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait);
                }
                try { await _account.RunPushAsync(renew.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 续票时间到 */ }
                if (!ct.IsCancellationRequested && renew.IsCancellationRequested) continue;
                if (!ct.IsCancellationRequested && _account.IsLoggedIn) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                SetStatus("联机服务出错：" + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
            }
        }
    }

    /// <summary>会话还剩不到 11 分钟：该续票了。</summary>
    private bool NeedsRenewal() =>
        _account.Session is { } s && s.ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < TimeSpan.FromMinutes(11).TotalMilliseconds;

    private async Task<bool> LoginAsync(CancellationToken ct)
    {
        var who = await App.Backend.MojangWhoAsync();
        if (!who.Ok) { SetStatus(who.Message.Length > 0 ? who.Message : "取不到当前正版账号"); return false; }

        var (ok, message) = await _account.LoginAsync(who.Name, async (serverId, _) =>
        {
            var p = await App.Backend.MojangProofAsync(serverId);
            if (!p.Ok) { SetStatus(p.Message); return null; }
            return new LoginProof(p.Uuid, p.PublicKey, p.ExpiresAtMs, p.CertSignature, p.Signature);
        }, ct);
        if (ok) MyName = who.Name;
        SetStatus(ok ? $"已登录联机账号 {who.Name}" : message);
        return ok;
    }

    private void OnInvite(InviteEvent e)
    {
        var from = Normalize(e.FromUuid);
        // 只接受 Mojang 好友的邀请：好友列表还没读到时先放行，并在消息里注明
        var known = _friendIds.Count > 0;
        if (known && !_friendIds.Contains(from))
        {
            SetStatus($"忽略了非好友 {ShownName(from, e.FromName)} 的邀请");
            return;
        }

        var title = $"{ShownName(from, e.FromName)} 邀请你一起玩";
        var message = e.RoomCode.Length > 0 ? $"房间码 {e.RoomCode}" : "来自 MeowLauncher 好友的邀请";
        if (!known) message += "（好友列表还没读取，未确认对方是否是好友）";
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            TaskCenter.PostInvite(title, message, from, e.RoomCode);
        });
    }

    private void OnInviteReply(InviteReplyEvent e)
    {
        var from = Normalize(e.FromUuid);
        var known = _friendIds.Count > 0;
        if (known && !_friendIds.Contains(from)) return;       // 非好友的回复不理
        lock (_sentInvites) _sentInvites.Remove(from);          // 对方回复了，可以再邀请
        var who = ShownName(from, e.FromName);
        var title = e.Accepted ? $"{who} 接受了你的邀请" : $"{who} 拒绝了你的邀请";
        var message = e.Accepted ? "对方已接受，可以一起玩了" : "对方这次不方便";
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            TaskCenter.PostMessage(title, message, "邀请");
        });
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
        App.Trace("Online: " + text);
    }

    private static string Normalize(string uuid) => (uuid ?? "").Replace("-", "").Trim().ToLowerInvariant();
}
