using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 「实例」页：Mod 管理 / 资源包 / 光影 / 存档 / 截图 五个同级入口，
/// 内容直接复用 ModsPage 与 AssetsPage，只把它们的页头收起来。
/// </summary>
public sealed partial class InstancePage : Page, IPageSearchFilter
{
    private string _tab = "mods";
    private bool _ready;
    private AssetsPage? _assets;
    private string? _pendingKind;

    public InstancePage()
    {
        InitializeComponent();
        InnerFrame.Navigated += (_, _) =>
        {
            if (InnerFrame.Content is AssetsPage assets)
                AttachAssets(assets);
        };

        Switcher.Switched += switched =>
        {
            var content = InnerFrame.Content?.GetType();
            if (content is not null)
            {
                InnerFrame.Navigate(content, new TabNav(), new SuppressNavigationTransitionInfo());
                MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 0, 8, 200);
            }
            _ = RefreshAssetCountsAsync();   // 不阻塞切页，显式丢弃返回值
        };
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            UpdateTabVisuals();
            Show(_tab);
            // 徽章不能等 AssetsPage 懒加载：停在 Mod 管理时它根本不会加载，四个数字会一直是 0。
            _ = RefreshAssetCountsAsync();
        };
    }

    public string CurrentTab => _tab;

    // 顶栏「本页」搜索转给里面的 Mod 管理 / 资源页；不转的话实例页的搜索条什么都过滤不了。
    public void ApplySearch(string query)
    {
        if (InnerFrame.Content is IPageSearchFilter inner) inner.ApplySearch(query);
    }

    public bool TryFocusSearch() => InnerFrame.Content is IPageSearchFilter inner && inner.TryFocusSearch();

    /// <summary>由 MainWindow 调用：切到指定子页。兼容旧的 "assets" 别名。</summary>
    public void ShowTab(string tab)
    {
        if (tab == "assets") tab = "resourcepack";
        if (tab is not ("mods" or "resourcepack" or "shaderpack" or "world" or "screenshot"))
            tab = "mods";
        _tab = tab;
        UpdateTabVisuals();
        if (!_ready) return;
        Show(tab);
    }

    private void Show(string tab)
    {
        if (tab == "mods")
        {
            if (InnerFrame.Content is not ModsPage)
            {
                var switching = InnerFrame.Content is not null;
                InnerFrame.Navigate(typeof(ModsPage), new TabNav(), new SuppressNavigationTransitionInfo());
                if (switching) MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 0, 8, 200);
            }
            return;
        }

        _pendingKind = tab;
        if (InnerFrame.Content is not AssetsPage)
        {
            var switching = InnerFrame.Content is not null;
            InnerFrame.Navigate(typeof(AssetsPage), new TabNav(), new SuppressNavigationTransitionInfo());
            if (switching) MeowLauncher.WinUI.Controls.Motion.Enter(InnerFrame, 0, 8, 200);
        }
        else if (InnerFrame.Content is AssetsPage assets)
        {
            AttachAssets(assets);
        }
    }

    /// <summary>把五种资源入口接到内嵌的 AssetsPage 上，并同步计数。</summary>
    private void AttachAssets(AssetsPage page)
    {
        if (!ReferenceEquals(_assets, page))
        {
            if (_assets is not null) _assets.CountsChanged -= Assets_CountsChanged;
            _assets = page;
            page.CountsChanged += Assets_CountsChanged;
        }

        if (_pendingKind is not null)
        {
            var kind = _pendingKind;
            _pendingKind = null;
            page.SelectKind(kind);
        }

        ApplyAssetCounts(page.Counts);
    }

    private void Assets_CountsChanged(object? sender, AssetsCounts counts) => ApplyAssetCounts(counts);

    private void ApplyAssetCounts(AssetsCounts counts)
    {
        AssetPacksText.Text = $"资源包 {counts.Packs}";
        AssetShadersText.Text = $"光影 {counts.Shaders}";
        AssetWorldsText.Text = $"存档 {counts.Worlds}";
        AssetShotsText.Text = $"截图 {counts.Shots}";
    }

    private int _countRequest;

    /// <summary>页面自己拉一次资源包 / 光影 / 存档 / 截图数量（目录与 AssetsPage 相同：实例真实运行目录）。</summary>
    private async Task RefreshAssetCountsAsync()
    {
        var request = ++_countRequest;
        try
        {
            var snapshot = App.MainWindow?.Snapshot ?? await App.Backend.GetSnapshotAsync();
            var vid = snapshot?.Current?.Id ?? snapshot?.Selected ?? "";
            if (string.IsNullOrWhiteSpace(vid)) return;
            var result = await App.Backend.GetPacksAsync(vid);
            if (request != _countRequest || result is not { Ok: true }) return;
            var gameDir = snapshot?.GameDir ?? "";
            var savesDir = !string.IsNullOrWhiteSpace(result.SavesDir) ? result.SavesDir
                : gameDir.Length > 0 ? Path.Combine(gameDir, "saves") : "";
            var shotsDir = !string.IsNullOrWhiteSpace(result.ScreenshotsDir) ? result.ScreenshotsDir
                : gameDir.Length > 0 ? Path.Combine(gameDir, "screenshots") : "";
            var packs = result.Packs.Count(x => x.Kind == "resourcepack");
            var shaders = result.Packs.Count(x => x.Kind == "shaderpack");
            var (worlds, shots) = await Task.Run(() => (CountDirs(savesDir), CountShots(shotsDir)));
            if (request != _countRequest) return;
            ApplyAssetCounts(new AssetsCounts(packs, shaders, worlds, shots));
        }
        catch
        {
            // 数不出来就保持现状，资源页加载后会再给一次准确数字。
        }
    }

    private static int CountDirs(string dir)
    {
        try { return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) ? 0 : Directory.EnumerateDirectories(dir).Count(); }
        catch { return 0; }
    }

    private static int CountShots(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir).Count(f =>
                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }
    private void AllInstances_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Navigate("versions");

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag })
        {
            ShowTab(tag);
            App.MainWindow?.NotifySubTab(tag);
        }
    }

    private void UpdateTabVisuals()
    {
        var selected = (Brush)Application.Current.Resources["AppNavSelectedBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var text = PagePalette.ThemeBrush("AppTextBrush");
        var text2 = PagePalette.ThemeBrush("AppText2Brush");
        Paint(TabMods, _tab == "mods");
        Paint(TabResourcepack, _tab == "resourcepack");
        Paint(TabShaderpack, _tab == "shaderpack");
        Paint(TabWorld, _tab == "world");
        Paint(TabScreenshot, _tab == "screenshot");

        void Paint(ToggleButton button, bool on)
        {
            button.IsChecked = on;
            button.Background = on ? selected : clear;
            button.Foreground = on ? text : text2;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
