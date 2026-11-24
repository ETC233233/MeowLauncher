using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Controls;

public sealed class InstanceRow
{
    public required VersionInfo Source { get; init; }
    public string Name => Source.Id;
    public string Sub { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool IsCurrent { get; init; }
    public Microsoft.UI.Xaml.Media.ImageSource IconSource => InstanceIcon.For(Source);
    public Visibility DotVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class InstancePicker : UserControl
{
    private List<InstanceRow> _all = [];

    /// <summary>用户点了某一行（尚未写后端；调用方决定是否走 InstanceSelection）。</summary>
    public event Action<VersionInfo>? Picked;

    public InstancePicker()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            InstanceSort.Changed -= OnSortChanged;
            InstanceSort.Changed += OnSortChanged;
        };
        Unloaded += (_, _) => InstanceSort.Changed -= OnSortChanged;
    }

    public void Reload()
    {
        var snapshot = App.MainWindow?.Snapshot;
        var usable = snapshot?.Versions.Where(v => v.Valid && !v.Hidden).ToList() ?? [];
        var current = snapshot?.Current;

        // 排序跟实例管理页同步（使用时间 / 名称 / 创建时间）；选中的那条靠滚动定位，不顶到最前面。
        _all = InstanceSort.Apply(usable)
            .Select(v =>
            {
                var parts = new List<string> { v.GameVersion, v.LoaderDisplay, v.AgoText };
                return new InstanceRow
                {
                    Source = v,
                    Sub = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))),
                    IsCurrent = InstanceSelection.Same(v, current),
                    Badge = GameSessions.IsRunning(v.Id, v.Root) ? "运行中" : "",
                };
            })
            .ToList();
        Apply();
    }

    private void Apply()
    {
        var tokens = (FilterBox.Text ?? "").Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant()).ToList();
        var rows = tokens.Count == 0
            ? _all
            : _all.Where(r =>
            {
                var hay = $"{r.Name} {r.Sub} {r.Source.Loader}".ToLowerInvariant();
                return tokens.All(hay.Contains);
            }).ToList();
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollToCurrent(rows);
    }

    /// <summary>打开列表时把当前实例滚进视野（它就是「大概位置」），不改变排序。</summary>
    private void ScrollToCurrent(List<InstanceRow> rows)
    {
        var focus = rows.FirstOrDefault(r => r.IsCurrent);
        if (focus is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => List.ScrollIntoView(focus, ScrollIntoViewAlignment.Leading));
    }

    private void OnSortChanged(object? sender, EventArgs e) => Reload();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InstanceRow row) Picked?.Invoke(row.Source);
    }

    public void FocusFilter() => FilterBox.Focus(FocusState.Programmatic);
}
