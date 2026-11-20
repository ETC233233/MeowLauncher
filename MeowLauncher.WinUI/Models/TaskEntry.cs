using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowLauncher.WinUI.Models;

public class TaskEntry : INotifyPropertyChanged
{
    private string _status = "进行中";
    private string _detail = "";
    private double _progress;
    private bool _isIndeterminate = true;
    private string _speedText = "";
    private string _remainText = "";
    private double _target;
    private DispatcherQueueTimer? _tick;
    private FailureInfo? _failure;
    private ImageSource? _targetIcon;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "\uE7C3";
    public string PageTag { get; set; } = "home";
    public string ResourceKey { get; set; } = "";
    public string TimeText { get; set; } = DateTime.Now.ToString("HH:mm");

    public ImageSource? TargetIcon
    {
        get => _targetIcon;
        set { _targetIcon = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskGlyphVisibility)); }
    }

    public Visibility TaskGlyphVisibility => TargetIcon is null ? Visibility.Visible : Visibility.Collapsed;
    internal string LastStage { get; set; } = "";

    public FailureInfo? Failure
    {
        get => _failure;
        internal set { _failure = value; OnPropertyChanged(nameof(ErrorCodeText)); }
    }

    /// <summary>完整失败信息（可能含安装器多行输出）；卡片只显示第一行，复制诊断用全文。</summary>
    public string FailureMessage { get; set; } = "";

    public string ErrorCodeText => Failure is null ? "" : $"错误码 {Failure.Code}";

    public override string ToString() => Title;

    /// <summary>分步明细：任务系统里每张卡下面那串「✓ 下载依赖库 / ◐ 下载资源文件 26%」。</summary>
    private ObservableCollection<TaskStep> _steps = new();

    /// <summary>步骤清单。整体替换（SetSteps）时换一个全新集合，界面一次画完，不会一条条蹦出来。</summary>
    public ObservableCollection<TaskStep> Steps
    {
        get => _steps;
        private set
        {
            _steps = value;
            OnPropertyChanged();
            OnStepsChanged();
        }
    }

    public void SetSteps(IEnumerable<TaskStep> items)
        => Steps = new ObservableCollection<TaskStep>(items);

    public bool HasSteps => Steps.Count > 0;

    /// <summary>步骤建好/清空后刷新一次「有没有分步明细」。</summary>
    public void OnStepsChanged()
    {
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StepsVisibility));
    }

    /// <summary>分步明细只在进行中显示：装完/失败后那串步骤已经是过去时，留在卡片里只是噪音。</summary>
    public Microsoft.UI.Xaml.Visibility StepsVisibility =>
        IsActive && Steps.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StepsVisibility));
        }
    }

    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    /// <summary>对外的进度：这里做缓动，界面上的条不会因为后端一次报一个大跳就「一下一下」。</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            var v = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);
            if (v < _progress) _progress = v;   // 回退（换任务/重试）直接跳，不做缓动
            _target = v;
            if (Math.Abs(_progress - _target) < 0.2 || !IsActive)
            {
                _progress = v;
                OnPropertyChanged();
                return;
            }
            EnsureTick();
        }
    }

    internal void AdvanceProgress(double value) => Progress = Math.Max(_target, value);

    private void EnsureTick()
    {
        if (_tick is not null) return;
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            _progress = _target;
            OnPropertyChanged();
            return;
        }
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(40);
        _tick.Tick += (_, _) =>
        {
            var step = Math.Max(0.25, (_target - _progress) * 0.22);
            _progress = Math.Min(_target, _progress + step);
            OnPropertyChanged(nameof(Progress));
            if (_progress >= _target - 0.05)
            {
                _progress = _target;
                _tick?.Stop();
                _tick = null;
                OnPropertyChanged(nameof(Progress));
            }
        };
        _tick.Start();
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; OnPropertyChanged(); }
    }

    private double _speedBytes;
    private double _speedTarget;
    private DispatcherQueueTimer? _speedTick;

    /// <summary>后端报的是「最近几秒的平均速度」，直接照抄会看着一跳一跳；这里用 250ms 一帧追着走。</summary>
    public void SetSpeedBytes(double bytesPerSec)
    {
        _speedTarget = bytesPerSec > 0 ? bytesPerSec : 0;
        if (_speedTarget <= 0)
        {
            _speedBytes = 0;
            SpeedText = "";
            _speedTick?.Stop();
            _speedTick = null;
            return;
        }
        EnsureSpeedTick();
    }

    private void EnsureSpeedTick()
    {
        if (_speedTick is not null) return;
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        _speedTick = queue.CreateTimer();
        _speedTick.Interval = TimeSpan.FromMilliseconds(250);
        _speedTick.Tick += (_, _) =>
        {
            if (!IsActive)
            {
                _speedTick?.Stop();
                _speedTick = null;
                return;
            }
            _speedBytes += (_speedTarget - _speedBytes) * 0.35;
            if (Math.Abs(_speedTarget - _speedBytes) < Math.Max(2048, _speedTarget * 0.03)) _speedBytes = _speedTarget;
            SpeedText = _speedBytes >= 1048576
                ? $"{_speedBytes / 1048576:0.0} MB/s"
                : _speedBytes >= 1024 ? $"{_speedBytes / 1024:0} KB/s" : "";
        };
        _speedTick.Start();
    }

    /// <summary>右侧小字：下载速度。空字符串表示不显示。</summary>
    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStats)); }
    }

    /// <summary>右侧小字：剩余文件数。空字符串表示不显示。</summary>
    public string RemainText
    {
        get => _remainText;
        set { _remainText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStats)); }
    }

    private string _live = "";
    /// <summary>外部工具（如安装器）的原始输出，一行，显示在进度条上面最右侧。</summary>
    public string LiveText
    {
        get => _live;
        private set { _live = value; OnPropertyChanged(); }
    }
    public Visibility LiveVisibility => string.IsNullOrWhiteSpace(_live) ? Visibility.Collapsed : Visibility.Visible;

    public void SetLive(string text)
    {
        if (string.Equals(_live, text, System.StringComparison.Ordinal)) return;
        LiveText = text ?? "";
        OnPropertyChanged(nameof(LiveVisibility));
    }

    public bool HasStats => _speedText.Length > 0 || _remainText.Length > 0;

    public bool IsActive => Status == "进行中";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
