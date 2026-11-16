namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 联机服务的门面。联机模块（MeowRoom.Module）不再随启动器分发，而是首次进入联机页时下载（见 OnlineComponent）。
/// 所以这个类本身不能碰模块的任何类型：组件没装好时它只返回「空」答案，装好后由 <see cref="Attach"/> 接上真正的实现 OnlineCore。
/// 其余代码一律通过它访问联机服务。
/// </summary>
public sealed class OnlineService
{
    private OnlineCore? _core;

    /// <summary>联机组件已装好并接上。</summary>
    public bool Available => _core is not null;

    public string Status => _core?.Status ?? "";
    public string Token => _core?.Token ?? "";
    public string MyName => _core?.MyName ?? "";
    public bool IsRunning => _core?.IsRunning ?? false;
    public bool IsLoggedIn => _core?.IsLoggedIn ?? false;

    public event Action<string>? StatusChanged;

    /// <summary>联机组件加载成功后调用一次：创建真正的实现。</summary>
    internal void Attach()
    {
        if (_core is not null) return;
        var core = ModuleBinder.NewOnline();
        core.StatusChanged += s => StatusChanged?.Invoke(s);
        _core = core;
    }

    public void UpdateFriends(IEnumerable<(string Id, string Name)> friends) => _core?.UpdateFriends(friends);
    public void Start() => _core?.Start();
    public void Stop() => _core?.Stop();
    public bool IsInvitePending(string toUuid, string roomCode = "") => _core?.IsInvitePending(toUuid, roomCode) ?? false;

    public Task<(bool Ok, bool Delivered, string Message)> InviteAsync(string toUuid, string roomCode = "") =>
        _core is null ? Task.FromResult((false, false, "联机组件还没安装，先打开「联机」页下载。")) : _core.InviteAsync(toUuid, roomCode);

    public Task<(bool Ok, bool Delivered, string Message)> ReplyInviteAsync(string toUuid, bool accepted) =>
        _core is null ? Task.FromResult((false, false, "联机组件还没安装。")) : _core.ReplyInviteAsync(toUuid, accepted);

    /// <summary>
    /// 回复一条邀请通知（通知页和弹窗共用）：成功就收起按钮并写结果；邀请已过期也收起；网络问题保留按钮可重试。
    /// 必须在界面线程调用。返回 true 表示这条邀请已经处理完（不用再显示按钮）。
    /// </summary>
    public async Task<bool> RespondToInviteAsync(TaskNotification note, bool accepted)
    {
        if (note.InviteFromUuid.Length == 0 || !note.InviteOpen) return false;
        var (ok, _, message) = await ReplyInviteAsync(note.InviteFromUuid, accepted);
        if (ok)
        {
            var join = accepted && System.Text.RegularExpressions.Regex.IsMatch(note.InviteRoomCode, "^\\d{6}$");
            note.InviteOpen = false;
            note.ResultText = !accepted ? "已拒绝，已通知对方" : join ? "已接受，正在加入房间…" : "已接受，已通知对方";
            TaskCenter.MarkRead(note);
            if (join)
            {
                // 邀请带了房间码：接受就是加入。切到联机页看进度；加入时会弹一次管理员授权（建虚拟网卡）
                App.MainWindow?.Navigate("room");
                _ = App.Room.JoinAsync(note.InviteRoomCode);
            }
            return true;
        }
        if (message.Contains("没有来自", StringComparison.Ordinal))
        {
            note.InviteOpen = false;                       // 已经回复过或已过期，按钮不再有意义
            note.ResultText = "这个邀请已过期或已处理";
            return true;
        }
        note.ResultText = message;                         // 网络问题等：保留按钮，可以重试
        return false;
    }
}
