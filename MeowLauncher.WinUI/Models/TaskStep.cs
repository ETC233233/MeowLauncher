using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MeowLauncher.WinUI.Models;

/// <summary>一条安装/下载任务里的单个步骤（对应 PCL 任务管理里那种一行一行的小条目）。</summary>
public enum TaskStepState
{
    Pending,
    Active,
    Done,
    Failed,
    Stopped,
    Skipped,
}

public sealed class TaskStep : INotifyPropertyChanged
{
    private TaskStepState _state;
    private double _pct;
    private string _detail = "";

    public TaskStep(string title) => Title = title;

    public string Title { get; }

    public TaskStepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(PctText));
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public double Pct
    {
        get => _pct;
        set
        {
            if (Math.Abs(_pct - value) < 0.05) return;
            _pct = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PctText));
        }
    }

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

    /// <summary>已完成打勾、进行中半环、等待一个小点、失败叉号。</summary>
    public string Glyph => State switch
    {
        TaskStepState.Done => "\uE73E",
        TaskStepState.Active => "\uE895",
        TaskStepState.Failed => "\uEA39",
        _ => "\uE73B",
    };

    /// <summary>完成的行压暗、进行中的最亮、等待的更淡。</summary>
    public double Opacity => State switch
    {
        TaskStepState.Done => 0.62,
        TaskStepState.Active => 1.0,
        TaskStepState.Failed => 1.0,
        _ => 0.38,
    };

    /// <summary>有副文案才显示，避免空出一个 8px 的洞。</summary>
    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(_detail) ? Visibility.Collapsed : Visibility.Visible;

    public string PctText => State switch
    {
        TaskStepState.Done => "完成",
        TaskStepState.Active => _pct < 0 ? "进行中" : $"{Math.Min(99, Math.Floor(_pct)):0}%",
        TaskStepState.Failed => "失败",
        TaskStepState.Stopped => "已中止",
        TaskStepState.Skipped => "未执行",
        _ => "等待",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
