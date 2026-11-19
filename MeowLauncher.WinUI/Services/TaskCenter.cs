using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowLauncher.WinUI.Models;

namespace MeowLauncher.WinUI.Services;

/// <summary>任务类型决定取消策略：启动只能在中途取消，安装/下载可以随时中止。</summary>
public enum TaskKind
{
    Other,
    Launch,
    Install,
}

/// <summary>启动四阶段里的单段状态：未开始 / 进行中 / 完成 / 失败。</summary>
public enum LaunchStageState
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>启动四阶段里的一段（准备 / 校验 / 启动 / 进入游戏）。</summary>
public sealed class LaunchStage : INotifyPropertyChanged
{
    private string _detail = "";
    private LaunchStageState _state;

    public string Title { get; set; } = "";

    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailVisibility));
        }
    }

    public LaunchStageState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string Glyph => State switch
    {
        LaunchStageState.Done => "\uE73E",
        LaunchStageState.Active => "\uE895",
        LaunchStageState.Failed => "\uEA39",
        _ => "\uE73B",
    };

    public string StateText => State switch
    {
        LaunchStageState.Done => "完成",
        LaunchStageState.Active => "进行中",
        LaunchStageState.Failed => "失败",
        _ => "等待",
    };

    public Microsoft.UI.Xaml.Visibility DetailVisibility =>
        string.IsNullOrWhiteSpace(Detail) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一次启动任务的轨迹：四阶段、日志尾、日志文件路径。
/// TaskEntry 只带一段 Detail，装不下这些，所以按 taskId 存在任务中心里。
/// </summary>
public sealed class LaunchTrace
{
    public const int MaxLogLines = 400;

    public LaunchTrace(string taskId)
    {
        TaskId = taskId;
        Stages = new[]
        {
            new LaunchStage { Title = "准备" },
            new LaunchStage { Title = "校验" },
            new LaunchStage { Title = "启动" },
            new LaunchStage { Title = "进入游戏" },
        };
    }

    public string TaskId { get; }

    public LaunchStage[] Stages { get; }

    /// <summary>日志尾：后端 stage 通道里以「› 」开头的行。</summary>
    public List<string> Logs { get; } = new();

    /// <summary>启动后游戏日志文件路径，用于「打开日志文件」。</summary>
    public string LogPath { get; set; } = "";

    public int Current { get; private set; } = -1;

    public string LogText => string.Join(Environment.NewLine, Logs);

    public void Append(string line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0) return;
        Logs.Add(text.Length > 200 ? text[..200] + "…" : text);
        if (Logs.Count > MaxLogLines) Logs.RemoveRange(0, Logs.Count - MaxLogLines);
        if (text.StartsWith("日志 ", StringComparison.Ordinal) && text.Length > 3)
        {
            LogPath = text[3..].Trim();
        }
    }

    /// <summary>推进到第 index 段：前面的都算完成，后面的回到未开始。</summary>
    public void Advance(int index, string detail)
    {
        if (index < 0 || index >= Stages.Length) return;
        Current = index;
        for (var i = 0; i < Stages.Length; i++)
        {
            var stage = Stages[i];
            if (i < index)
            {
                stage.State = LaunchStageState.Done;
            }
            else if (i == index)
            {
                stage.State = LaunchStageState.Active;
                stage.Detail = detail ?? "";
            }
            else
            {
                stage.State = LaunchStageState.Pending;
                stage.Detail = "";
            }
        }
    }

    /// <summary>任务结束时收口：成功则四段全打勾，失败则当前段标红。</summary>
    public void Finish(bool ok)
    {
        if (ok)
        {
            foreach (var stage in Stages)
            {
                stage.State = LaunchStageState.Done;
                if (Current < 0) stage.Detail = "";
            }
            Current = Stages.Length - 1;
            return;
        }

        if (Current >= 0 && Current < Stages.Length)
        {
            Stages[Current].State = LaunchStageState.Failed;
        }
    }
}

/// <summary>任务中心里的一次性通知（完成 / 失败 / 已取消），与任务列表分开保存。</summary>
public sealed class TaskNotification : INotifyPropertyChanged
{
    private bool _isRead;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Kind { get; set; } = "已完成";
    public string SourceTaskId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnread));
            OnPropertyChanged(nameof(UnreadVisibility));
        }
    }

    public bool IsUnread => !_isRead;

    // ---- 好友邀请通知：有 InviteFromUuid 就带「接受 / 拒绝」按钮，回复之后按钮收起、显示结果 ----
    private bool _inviteOpen;
    private string _resultText = "";

    /// <summary>邀请人的 UUID；空表示这不是一条可回复的邀请。</summary>
    public string InviteFromUuid { get; set; } = "";

    /// <summary>邀请里带的房间码（6 位数字）；空表示只是「来一起玩」，没有房间。接受后会自动加入这个房间。</summary>
    public string InviteRoomCode { get; set; } = "";

    /// <summary>还没回复（按钮显示）。</summary>
    public bool InviteOpen
    {
        get => _inviteOpen;
        set
        {
            if (_inviteOpen == value) return;
            _inviteOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InviteButtonsVisibility));
        }
    }

    /// <summary>回复的结果或失败原因，显示在按钮位置。</summary>
    public string ResultText
    {
        get => _resultText;
        set
        {
            if (_resultText == value) return;
            _resultText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResultVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility InviteButtonsVisibility =>
        _inviteOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ResultVisibility =>
        _resultText.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility UnreadVisibility =>
        _isRead ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>相对时间：刚刚 / N 分钟前 / N 小时前 / M月d日。</summary>
    public string TimeText
    {
        get
        {
            var span = DateTime.Now - CreatedAt;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
            return CreatedAt.ToString("M月d日");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class TaskCenter
{
    private const int MaxEntries = 40;
    private const int MaxNotifications = 60;

    private static readonly Dictionary<string, TaskKind> Kinds = new();
    private static readonly Dictionary<string, LaunchTrace> Traces = new();
    private static readonly HashSet<string> Finished = new();
    private static readonly TaskCancellationRegistry Cancellations = new();
    public static CancellationToken CancellationFor(string? id) => Cancellations.TokenFor(id);
    public static bool IsCancelled(string? id) => CancellationFor(id).IsCancellationRequested;

    public static ObservableCollection<TaskEntry> Entries { get; } = new();

    /// <summary>独立通知通道；任务结果保留在任务列表，不重复存入通知。</summary>
    public static ObservableCollection<TaskNotification> Notifications { get; } = new();

    public static event EventHandler? Changed;
    public static event EventHandler? NotificationsChanged;
    public static event Action<TaskEntry>? TaskFinished;

    public static TaskEntry Add(string title, string detail, string pageTag = "home", string glyph = "\\uE7C3",
        Microsoft.UI.Xaml.Media.ImageSource? targetIcon = null)
    {
        var entry = new TaskEntry
        {
            Title = title,
            Detail = detail,
            PageTag = pageTag,
            Glyph = glyph,
            TargetIcon = targetIcon,
        };
        Kinds[entry.Id] = KindOf(title);
        Cancellations.Create(entry.Id);
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        Notify();
        return entry;
    }

    public static TaskKind KindOf(string? title)
    {
        var text = (title ?? "").TrimStart();
        if (text.StartsWith("启动", StringComparison.Ordinal)) return TaskKind.Launch;
        if (text.StartsWith("安装", StringComparison.Ordinal)) return TaskKind.Install;
        if (text.StartsWith("下载", StringComparison.Ordinal)) return TaskKind.Install;
        if (text.StartsWith("导入整合包", StringComparison.Ordinal)) return TaskKind.Install;
        return TaskKind.Other;
    }

    public static void Complete(TaskEntry entry, string detail)
    {
        if (Finished.Contains(entry.Id)) return;
        Finished.Add(entry.Id);
        ResetDownloadStats(entry);
        entry.Status = "已完成";
        entry.Detail = detail;
        entry.IsIndeterminate = false;
        entry.Progress = 100;
        TraceFor(entry.Id)?.Finish(true);
        foreach (var step in entry.Steps) { step.State = TaskStepState.Done; step.Pct = 100; }
        Notify();
        TaskFinished?.Invoke(entry);

    }

    public static void Fail(TaskEntry entry, string detail, FailureInfo? failure = null)
    {
        if (Finished.Contains(entry.Id)) return;
        Finished.Add(entry.Id);
        var stage = failure?.Stage;
        if (string.IsNullOrWhiteSpace(stage))
            stage = entry.Steps.LastOrDefault(step => step.State == TaskStepState.Failed)?.Title ?? entry.LastStage;
        var failedStep = entry.Steps.FirstOrDefault(step => step.State == TaskStepState.Active &&
            (stage.Contains(step.Title, StringComparison.OrdinalIgnoreCase) ||
             (step.Title == "下载资源文件" && stage.Contains("资源索引", StringComparison.Ordinal))));
        if (failedStep is not null) failedStep.State = TaskStepState.Failed;
        StopSteps(entry);
        ResetDownloadStats(entry);
        var code = string.IsNullOrWhiteSpace(failure?.Code) ? Diagnostics.ErrorCodeFor(detail, KindOf(entry.Title)) : failure.Code;
        entry.Failure = new FailureInfo(code, failure?.At ?? DateTimeOffset.Now, stage);
        entry.Status = "失败";
        entry.Detail = detail;
        entry.IsIndeterminate = false;
        entry.Progress = 0;
        TraceFor(entry.Id)?.Finish(false);
        Notify();
        TaskFinished?.Invoke(entry);

    }

    /// <summary>只更新当前阶段文案，不动进度条状态（启动这类不确定时长的任务用）。</summary>
    public static void SetStage(TaskEntry entry, string detail) => Report(entry, detail);

    /// <summary>
    /// 后端 stage 通道来的一行：
    /// `序号|短标题|说明`（如 `1|校验|检查 Java 运行时`）推进四阶段，
    /// `› 文本` 追加到日志尾，其余文本仍然只更新详情（兼容旧调用）。
    /// </summary>
    public static void Report(TaskEntry entry, string? line)
    {
        if (Finished.Contains(entry.Id)) return;
        var text = (line ?? "").Trim();
        if (text.Length == 0) return;
        if (text.StartsWith("@phase|", StringComparison.Ordinal))
        {
            var title = text[7..];
            foreach (var step in entry.Steps.Where(s => s.State == TaskStepState.Active))
            { step.State = TaskStepState.Done; step.Pct = 100; }
            entry.Steps.Add(new TaskStep(title) { State = TaskStepState.Active, Pct = -1 });
            entry.OnStepsChanged();
            entry.Detail = entry.LastStage = title;
            entry.IsIndeterminate = true;
            Notify();
            return;
        }
        if (text.StartsWith("@progress|", StringComparison.Ordinal))
        {
            var parts_ = text[10..].Split('|', 2);
            if (double.TryParse(parts_[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                var step = entry.Steps.LastOrDefault();
                if (step is not null) { step.Pct = Math.Clamp(percent, 0, 100); step.Detail = parts_.Length > 1 ? parts_[1] : ""; }
            }
            Notify(); return;
        }
        if (text.StartsWith("@target|", StringComparison.Ordinal))
        {
            entry.TargetIcon = InstanceIcon.ForLoader(text[8..]);
            Notify();
            return;
        }

        if (text.StartsWith("›", StringComparison.Ordinal))
        {
            EnsureTrace(entry.Id).Append(text.TrimStart('›', ' '));
            Notify();
            return;
        }

        var parts = text.Split('|', 3);
        if (parts.Length == 3 && int.TryParse(parts[0], out var index) && index is >= 0 and <= 3)
        {
            EnsureTrace(entry.Id).Advance(index, parts[2]);
            entry.LastStage = parts[2];
            entry.Detail = parts[2];
            Notify();
            return;
        }

        entry.LastStage = text;
        entry.Detail = text;
        Notify();
    }

    /// <summary>取某个任务的启动轨迹（没走过启动流程的任务返回 null）。</summary>
    public static LaunchTrace? TraceFor(string? taskId)
        => string.IsNullOrWhiteSpace(taskId) ? null : Traces.GetValueOrDefault(taskId);

    private static LaunchTrace EnsureTrace(string taskId)
    {
        if (Traces.TryGetValue(taskId, out var trace)) return trace;

        trace = new LaunchTrace(taskId);
        Traces[taskId] = trace;
        while (Traces.Count > MaxEntries)
        {
            var stale = Traces.Keys.FirstOrDefault(key => Entries.All(e => e.Id != key));
            if (stale is null) break;
            Traces.Remove(stale);
        }
        return trace;
    }

    /// <summary>安装/下载的分步清单：任务开始时一次性建好，后面只改每步的状态与百分比。</summary>
    public static void BuildSteps(TaskEntry entry, IReadOnlyList<string> titles)
    {
        if (entry.IsActive) entry.SetSteps(titles.Select(t => new TaskStep(t)));
    }

    /// <summary>更新某一步：0 等待 / 1 进行中 / 2 完成 / 3 失败。</summary>
    public static void Step(TaskEntry entry, int index, int state, double pct, string detail)
    {
        if (!entry.IsActive || index < 0 || index >= entry.Steps.Count) return;
        var step = entry.Steps[index];
        if (step.State is TaskStepState.Done or TaskStepState.Failed) return;
        step.State = state switch
        {
            1 => TaskStepState.Active,
            2 => TaskStepState.Done,
            3 => TaskStepState.Failed,
            _ => TaskStepState.Pending,
        };
        // -1 是「没有百分比」的哨兵（安装器在跑），别被 clamp 成 0 显示成卡住。
        step.Pct = state == 2 ? 100 : (pct < 0 ? -1 : Math.Max(step.Pct, Math.Clamp(pct, 0, 100)));
        step.Detail = detail;
    }

    private static void StopSteps(TaskEntry entry)
    {
        foreach (var step in entry.Steps)
        {
            if (step.State == TaskStepState.Active) step.State = TaskStepState.Stopped;
            if (step.State == TaskStepState.Pending) step.State = TaskStepState.Skipped;
        }
    }

    public static void ResetDownloadStats(TaskEntry entry)
    {
        Stats(entry, 0, "");
        entry.SetLive("");
    }

    /// <summary>速度 / 剩余文件：任务卡右侧那行小字。</summary>
    /// <summary>速度传字节数，由 TaskEntry 自己平滑成「x.x MB/s」；剩余文件直接给文案。</summary>
    public static void Stats(TaskEntry entry, double speedBytes, string remain)
    {
        entry.SetSpeedBytes(speedBytes);
        entry.RemainText = remain;
    }

    public static void Stats(TaskEntry entry, string speed, string remain)
    {
        entry.SpeedText = speed;
        entry.RemainText = remain;
    }

    public static void Progress(TaskEntry entry, double value, string detail)
    {
        if (Finished.Contains(entry.Id)) return;
        entry.AdvanceProgress(Math.Clamp(value, 0, 99));
        entry.IsIndeterminate = false;
        if (!string.IsNullOrWhiteSpace(detail) && detail != entry.Detail) entry.LastStage = detail;
        entry.Detail = detail;
        Notify();
    }

    public static void ClearFinished()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!Entries[i].IsActive) Entries.RemoveAt(i);
        }
        Notify();
    }

    public static int ActiveCount => Entries.Count(x => x.IsActive);

    public static int UnreadCount => Notifications.Count(n => !n.IsRead);

    // ── 取消 ────────────────────────────────────────────────────────────

    /// <summary>游戏进程已经起来之后取消没有意义——按钮置灰，由 ToolTip 说明原因。</summary>
    public static bool CanCancel(TaskEntry? entry)
    {
        if (entry is null || !entry.IsActive) return false;
        var kind = Kinds.GetValueOrDefault(entry.Id, TaskKind.Other);
        if (kind == TaskKind.Launch && LaunchHandedOff(entry.Detail)) return false;
        return true;
    }

    public static string CancelHint(TaskEntry? entry)
    {
        if (entry is null) return "没有可取消的任务。";
        if (!entry.IsActive) return "任务已经结束，没有可取消的操作。";
        var kind = Kinds.GetValueOrDefault(entry.Id, TaskKind.Other);
        if (kind == TaskKind.Launch)
        {
            return LaunchHandedOff(entry.Detail)
                ? "游戏进程已启动，取消不会再关掉它。"
                : "可取消：中止本次启动准备，已经启动的游戏不会被关闭。";
        }
        if (kind == TaskKind.Install)
        {
            return "可取消：中止当前下载/安装，已下载一半的文件会保留，稍后可重试。";
        }
        return "可取消：中止这次后台操作。";
    }

    /// <summary>取消排队或运行中的任务，同时取消关联后台操作。</summary>
    public static bool Cancel(TaskEntry entry)
    {
        if (!CanCancel(entry)) return false;
        var kind = Kinds.GetValueOrDefault(entry.Id, TaskKind.Other);
        Cancellations.Cancel(entry.Id);
        try
        {
            _ = App.Backend?.CancelOperations(kind, EntityOf(entry.Title), entry.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"取消任务失败：{ex.Message}");
        }


        Finished.Add(entry.Id);
        entry.Status = "已取消";
        StopSteps(entry);
        ResetDownloadStats(entry);
        entry.Detail = "后台操作已中止。";
        entry.IsIndeterminate = false;
        entry.Progress = 0;
        TraceFor(entry.Id)?.Finish(false);
        Notify();

        return true;
    }

    public static void Dismiss(TaskEntry entry)
    {
        if (entry.IsActive) return;
        Entries.Remove(entry);
        Kinds.Remove(entry.Id);
        Traces.Remove(entry.Id);
        Notify();
    }

    public static void MarkRead(TaskNotification? notification)
    {
        if (notification is null || notification.IsRead) return;
        notification.IsRead = true;
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void MarkAllRead()
    {
        foreach (var item in Notifications) item.IsRead = true;
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearNotifications()
    {
        Notifications.Clear();
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool LaunchHandedOff(string? detail)
    {
        var text = detail ?? "";
        return text.Contains("进程已启动", StringComparison.Ordinal)
               || text.Contains("正在记录启动时间", StringComparison.Ordinal)
               || text.Contains("已记录启动时间", StringComparison.Ordinal);
    }

    /// <summary>任务标题里「启动 X / 安装 Y」的实体名，用来把取消精确匹配到对应的后端进程。</summary>
    private static string EntityOf(string? title)
    {
        var text = (title ?? "").Trim();
        foreach (var prefix in new[] { "启动 ", "安装 ", "下载 " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text[prefix.Length..].Trim();
            }
        }
        return "";
    }

    /// <summary>发一条和任务无关的通知（比如收到好友邀请）。必须在界面线程调用。</summary>
    public static TaskNotification PostMessage(string title, string message, string kind = "消息")
    {
        var note = new TaskNotification { Title = title, Message = message, Kind = kind };
        Notifications.Insert(0, note);
        while (Notifications.Count > MaxNotifications) Notifications.RemoveAt(Notifications.Count - 1);
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
        return note;
    }

    /// <summary>发一条可回复的好友邀请通知（带接受 / 拒绝按钮）。必须在界面线程调用。</summary>
    public static TaskNotification PostInvite(string title, string message, string fromUuid, string roomCode = "")
    {
        // 同一个人又发来邀请：把还没处理的旧邀请换掉，不堆叠
        for (var i = Notifications.Count - 1; i >= 0; i--)
        {
            if (Notifications[i].InviteOpen && string.Equals(Notifications[i].InviteFromUuid, fromUuid, StringComparison.OrdinalIgnoreCase))
            {
                Notifications.RemoveAt(i);
            }
        }
        var note = new TaskNotification
        {
            Title = title, Message = message, Kind = "邀请", InviteFromUuid = fromUuid, InviteRoomCode = roomCode, InviteOpen = true,
        };
        Notifications.Insert(0, note);
        while (Notifications.Count > MaxNotifications) Notifications.RemoveAt(Notifications.Count - 1);
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
        return note;
    }

    private static void PushNotification(string kind, TaskEntry entry, string message)
    {
        Notifications.Insert(0, new TaskNotification
        {
            Title = entry.Title,
            Message = string.IsNullOrWhiteSpace(message) ? entry.Detail : message,
            Kind = kind,
            SourceTaskId = entry.Id,
        });
        while (Notifications.Count > MaxNotifications) Notifications.RemoveAt(Notifications.Count - 1);
        NotificationsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Notify() => Changed?.Invoke(null, EventArgs.Empty);
}
