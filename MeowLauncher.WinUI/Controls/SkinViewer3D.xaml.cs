using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 用开源 skinview3d（WebView2 内嵌本地页面）渲染 3D 皮肤，支持 classic/slim、披风与拖拽旋转。
/// 资源全部落在 Assets/skinviewer（本地 vendor，运行时不联网）；初始化失败或 3 秒未就绪时只显示提示，不再回退到旧渲染器。
/// </summary>
public sealed partial class SkinViewer3D : UserControl
{
    private const string VirtualHost = "meowskin";
    private static readonly Uri PageUri = new($"https://{VirtualHost}/index.html");

    /// <summary>皮肤 PNG 的本地绝对路径（与账户页同来源，sidecar 的 skin_*.png）。</summary>
    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath), typeof(string), typeof(SkinViewer3D),
        new PropertyMetadata(null, (d, _) => ((SkinViewer3D)d).QueuePush()));

    /// <summary>true = 纤细（Alex）3 像素手臂。</summary>
    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(
        nameof(Slim), typeof(bool), typeof(SkinViewer3D),
        new PropertyMetadata(false, (d, _) => ((SkinViewer3D)d).QueuePush()));

    /// <summary>披风图片地址（http(s) 或本地文件路径）；为空则不显示披风。</summary>
    public static readonly DependencyProperty CapeUrlProperty = DependencyProperty.Register(
        nameof(CapeUrl), typeof(string), typeof(SkinViewer3D),
        new PropertyMetadata(string.Empty, (d, _) => ((SkinViewer3D)d).QueuePush()));

    /// <summary>初始视角（度），0 = 正面，默认 -30。</summary>
    public static readonly DependencyProperty YawProperty = DependencyProperty.Register(
        nameof(Yaw), typeof(double), typeof(SkinViewer3D),
        new PropertyMetadata(25d, (d, _) => ((SkinViewer3D)d).ApplyYaw()));

    private bool _initialized;
    private bool _ready;
    private bool _failed;
    private bool _pushing;
    private string? _cachedSkinUri;
    private string _cachedSkinPath = string.Empty;

    public SkinViewer3D()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _ = 0;
    }

    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    public bool Slim
    {
        get => (bool)GetValue(SlimProperty);
        set => SetValue(SlimProperty, value);
    }

    public string CapeUrl
    {
        get => (string)GetValue(CapeUrlProperty);
        set => SetValue(CapeUrlProperty, value);
    }

    public double Yaw
    {
        get => (double)GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    /// <summary>true 表示当前由 WebView2 渲染。</summary>
    public bool IsUsingWebView => _ready && !_failed;

    /// <summary>改完皮肤/披风后强制把当前属性再下发一次，不重建 WebView。</summary>
    public void ForceRefresh()
    {
        if (_ready && !_failed) _ = PushAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            if (_ready) QueuePush();
            return;
        }

        _initialized = true;
        try
        {
            var env = await SkinRenderView.PrewarmAsync();
            if (env is not null) await Web.EnsureCoreWebView2Async(env);
            else await Web.EnsureCoreWebView2Async();

            var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "skinviewer");
            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                Fail("本地渲染资源缺失");
                return;
            }

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            try { Web.DefaultBackgroundColor = StageBackdropColor(); } catch { /* 旧运行时忽略 */ }
            try { Root.ActualThemeChanged += (_, _) => { try { Web.DefaultBackgroundColor = StageBackdropColor(); } catch { /* ignore */ } }; } catch { /* ignore */ }

            Web.Source = PageUri;
            Web.Visibility = Visibility.Visible;

            // 3 秒没收到 ready 就显示错误，避免白屏
            _ = WatchdogAsync();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task WatchdogAsync()
    {
        await Task.Delay(3000);
        if (!_ready && !_failed)
        {
            Fail("WebView2 3 秒内未就绪");
        }
    }

    /// <summary>
    /// WebView2 的面画不出圆角、也做不到真透明，所以用「卡片底色 + 壁纸底色」按卡片自身透明度混出一个不透明底，
    /// 让 3D 视口看起来就是卡片的一部分，而不是一块突兀的深色方块。
    /// </summary>
    private Windows.UI.Color StageBackdropColor()
    {
        var light = Root.ActualTheme == ElementTheme.Light;
        var baseColor = light
            ? Windows.UI.Color.FromArgb(255, 0xF2, 0xF3, 0xF6)
            : Windows.UI.Color.FromArgb(255, 0x0B, 0x0D, 0x11);

        try
        {
            if (Application.Current.Resources.TryGetValue("AppCardBrush", out var raw)
                && raw is SolidColorBrush card && card.Color.A > 0 && card.Color.A < 255)
            {
                var c = card.Color;
                double a = c.A / 255.0;
                return Windows.UI.Color.FromArgb(
                    255,
                    (byte)Math.Round(c.R * a + baseColor.R * (1 - a)),
                    (byte)Math.Round(c.G * a + baseColor.G * (1 - a)),
                    (byte)Math.Round(c.B * a + baseColor.B * (1 - a)));
            }
        }
        catch
        {
            // 资源字典取不到就退回基准色
        }

        return baseColor;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        if (message.Contains("\"ready\"", StringComparison.Ordinal))
        {
            _ready = true;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            QueuePush();
            return;
        }

        if (message.Contains("\"error\"", StringComparison.Ordinal))
        {
            // 渲染器自己报错：不立刻回退，只要曾经 ready 过就继续（可能只是这一张图失败）
            if (!_ready)
            {
                Fail("skinview3d 初始化失败");
            }
        }
    }

    private void QueuePush()
    {
        if (!_ready || _failed) return;
        _ = PushAsync();
    }

    private async Task PushAsync()
    {
        if (_pushing) return;
        var path = SkinPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // 没有皮肤文件时保持空白 canvas，不覆盖回退逻辑
            return;
        }

        _pushing = true;
        try
        {
            if (!string.Equals(_cachedSkinPath, path, StringComparison.OrdinalIgnoreCase) || _cachedSkinUri is null)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                _cachedSkinUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
                _cachedSkinPath = path;
            }

            var cape = await ResolveCapeAsync();
            var script = new StringBuilder()
                .Append("window.setSkin('").Append(_cachedSkinUri).Append('\'')
                .Append(", ").Append(Slim ? "true" : "false")
                .Append(", '").Append(Escape(cape)).Append('\'')
                .Append(", ").Append(Yaw.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                .Append(");")
                .ToString();

            await Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // 单次下发失败不切回退；下一次属性变化会重试
        }
        finally
        {
            _pushing = false;
        }
    }

    private async Task<string> ResolveCapeAsync()
    {
        var cape = CapeUrl;
        if (string.IsNullOrWhiteSpace(cape)) return string.Empty;

        if (!cape.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(cape)) return string.Empty;
            var bytes = await File.ReadAllBytesAsync(cape);
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }

        // 远程披风图：先抓成本地 data URI，避免 WebView 里的跨域/离线问题
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var bytes = await http.GetByteArrayAsync(cape);
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private void ApplyYaw()
    {
        if (_ready && !_failed)
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync(
                $"window.setYaw({Yaw.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)});");
        }
    }

    private void Fail(string reason)
    {
        if (_failed) return;
        _failed = true;
        _ready = false;
        System.Diagnostics.Debug.WriteLine($"[SkinViewer3D] 3D 预览不可用：{reason}");

        try { Web.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Visible;
    }
}
