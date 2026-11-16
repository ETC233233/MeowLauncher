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
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        var snapshot = App.MainWindow?.Snapshot;
        var usable = snapshot?.Versions.Where(v => v.Valid && !v.Hidden).ToList() ?? [];
        var multiRoot = usable.Select(v => GameDirectoryResolver.Normalize(v.Root))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        var current = snapshot?.Current;

        _all = usable
            .OrderByDescending(v => InstanceSelection.Same(v, current))
            .ThenByDescending(v => v.LastPlayed)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var parts = new List<string> { v.GameVersion, v.LoaderDisplay, v.AgoText };
                if (multiRoot) parts.Add(Path.GetFileName(v.Root.TrimEnd('\\', '/')));
                return new InstanceRow
                {
                    Source = v,
                    Sub = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))),
                    IsCurrent = InstanceSelection.Same(v, current),
                    Badge = GameSessions.IsRunning(v.Id) ? "运行中" : "",
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
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InstanceRow row) Picked?.Invoke(row.Source);
    }

    public void FocusFilter() => FilterBox.Focus(FocusState.Programmatic);
}
