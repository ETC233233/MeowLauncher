using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 透明皮肤立绘：本地 skinview3d（WebView2 内嵌 Assets/skinviewer）离屏渲染透明 PNG。
/// 缓存命中时完全不启动 WebView2；首次渲染后把 PNG 落到 LocalAppData，之后直接秒开。
/// </summary>
public sealed partial class SkinRenderView : UserControl
{
    private const string VirtualHost = "meowskin";
    private const string RendererVersion = "v4";
    private static readonly Uri PageUri = new($"https://{VirtualHost}/index.html");
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeowLauncher", "SkinCache");
    private static readonly ConcurrentDictionary<string, string> DataUriCache = new(StringComparer.OrdinalIgnoreCase);
    private static Task<CoreWebView2Environment?>? _environmentTask;

    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath), typeof(string), typeof(SkinRenderView),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty CapePathProperty = DependencyProperty.Register(
        nameof(CapePath), typeof(string), typeof(SkinRenderView),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(
        nameof(Slim), typeof(bool), typeof(SkinRenderView),
        new PropertyMetadata(false, OnVisualChanged));

    /// <summary>初始视角（度），0 = 正面，25 = 与 xgamingserver 皮肤页一致的 3/4 角度。</summary>
    public static readonly DependencyProperty YawProperty = DependencyProperty.Register(
        nameof(Yaw), typeof(double), typeof(SkinRenderView),
        new PropertyMetadata(25d, OnVisualChanged));

    private bool _loaded;
    private bool _starting;
    private bool _startPending;
    private bool _webReady;
    private Task? _webInitTask;
    private TaskCompletionSource<bool>? _readyTcs;
    private TaskCompletionSource<string>? _pngTcs;
    private int _requestId;
    private bool _rendering;
    private bool _renderAgain;
    private DispatcherQueueTimer? _resizeTimer;

    public SkinRenderView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            QueueStart();
        };
        SizeChanged += (_, _) => QueueStart();
    }

    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    public string? CapePath
    {
        get => (string?)GetValue(CapePathProperty);
        set => SetValue(CapePathProperty, value);
    }

    public bool Slim
    {
        get => (bool)GetValue(SlimProperty);
        set => SetValue(SlimProperty, value);
    }

    public double Yaw
    {
        get => (double)GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    /// <summary>提前创建 WebView2 环境，减少第一张立绘的冷启动开销。</summary>
    public static Task<CoreWebView2Environment?> PrewarmAsync()
    {
        return _environmentTask ??= CreateEnvironmentAsync();
    }

    private static async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            return await CoreWebView2Environment.CreateAsync();
        }
        catch
        {
            return null;
        }
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkinRenderView view) view.QueueStart();
    }

    private void QueueStart()
    {
        if (!_loaded) return;
        if (_resizeTimer is null)
        {
            try
            {
                _resizeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _resizeTimer.Interval = TimeSpan.FromMilliseconds(220);
                _resizeTimer.IsRepeating = false;
                _resizeTimer.Tick += (_, _) => _ = TryStartAsync();
            }
            catch
            {
                _ = TryStartAsync();
                return;
            }
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async Task TryStartAsync()
    {
        if (!_loaded || Visibility != Visibility.Visible || ActualWidth < 1 || ActualHeight < 1)
            return;
        if (_starting)
        {
            _startPending = true;
            return;
        }

        _starting = true;
        try
        {
            var path = SkinPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                HideAll();
                return;
            }

            var (width, height) = GetRenderSize();
            var key = CacheKey(path, width, height);
            if (await TryShowCacheAsync(key)) return;
            await RenderAsync(width, height, key);
        }
        finally
        {
            _starting = false;
            if (_startPending)
            {
                _startPending = false;
                _ = TryStartAsync();
            }
        }
    }

    private (int Width, int Height) GetRenderSize()
    {
        var raster = XamlRoot?.RasterizationScale ?? 1.0;
        var width = Math.Clamp((int)Math.Round(Math.Max(ActualWidth, 1) * raster), 64, 1024);
        var height = Math.Clamp((int)Math.Round(Math.Max(ActualHeight, 1) * raster), 64, 1536);
        return (width, height);
    }

    private string CacheKey(string path, int width, int height)
    {
        var cape = CapePath;
        var raw = string.Join("|",
            RendererVersion,
            path,
            File.GetLastWriteTimeUtc(path).Ticks,
            new FileInfo(path).Length,
            string.IsNullOrWhiteSpace(cape) ? "" : cape,
            string.IsNullOrWhiteSpace(cape) || !File.Exists(cape) ? 0 : File.GetLastWriteTimeUtc(cape).Ticks,
            Slim ? "slim" : "classic",
            Yaw.ToString("0.##", CultureInfo.InvariantCulture),
            width,
            height);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private async Task<bool> TryShowCacheAsync(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var file = Path.Combine(CacheDirectory, key + ".png");
        if (!File.Exists(file)) return false;

        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            await ShowBytesAsync(bytes);
            Rendered.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch
        {
            try { File.Delete(file); } catch { /* ignore */ }
            return false;
        }
    }

    private async Task RenderAsync(int width, int height, string key)
    {
        if (_rendering)
        {
            _renderAgain = true;
            return;
        }

        var path = SkinPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        _rendering = true;
        try
        {
            Rendered.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;

            await EnsureWebAsync();
            if (!_webReady)
            {
                ShowError();
                return;
            }

            var skinData = await ReadDataUriAsync(path);
            if (string.IsNullOrEmpty(skinData))
            {
                ShowError();
                return;
            }

            var capePath = CapePath;
            var capeData = string.Empty;
            if (!string.IsNullOrWhiteSpace(capePath) && File.Exists(capePath))
                capeData = await ReadDataUriAsync(capePath);

            var spec = new
            {
                skin = skinData,
                slim = Slim,
                cape = capeData,
                yaw = Yaw,
                width,
                height,
                pixelRatio = 1
            };
            var json = System.Text.Json.JsonSerializer.Serialize(spec);
            var id = ++_requestId;
            _pngTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Web.CoreWebView2.ExecuteScriptAsync($"window.renderPng({json}, {id});");
            var dataUrl = await _pngTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
            var bytes = DecodeDataUrl(dataUrl);
            if (bytes is null)
            {
                ShowError();
                return;
            }

            await SaveCacheAsync(key, bytes);
            await ShowBytesAsync(bytes);
            Rendered.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ShowError();
        }
        finally
        {
            _rendering = false;
            if (_renderAgain)
            {
                _renderAgain = false;
                _ = TryStartAsync();
            }
        }
    }

    private Task EnsureWebAsync()
    {
        return _webInitTask ??= InitWebCoreAsync();
    }

    private async Task InitWebCoreAsync()
    {
        try
        {
            var env = await PrewarmAsync();
            if (env is not null) await Web.EnsureCoreWebView2Async(env);
            else await Web.EnsureCoreWebView2Async();

            var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "skinviewer");
            if (!File.Exists(Path.Combine(folder, "index.html")))
                return;

            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;

            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Web.Source = PageUri;

            await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
            _webReady = true;
        }
        catch
        {
            _webReady = false;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch { return; }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            if (type == "ready")
            {
                _readyTcs?.TrySetResult(true);
                return;
            }

            if (type == "png" && _pngTcs is not null)
            {
                if (root.TryGetProperty("id", out var idProp) && idProp.GetInt32() != _requestId) return;
                if (root.TryGetProperty("data", out var dataProp))
                {
                    var data = dataProp.GetString();
                    if (!string.IsNullOrEmpty(data)) _pngTcs.TrySetResult(data);
                }
            }
        }
        catch
        {
            // 页面消息格式异常时忽略，下一次属性变化会重试
        }
    }

    private static async Task<string> ReadDataUriAsync(string path)
    {
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length;
        if (DataUriCache.TryGetValue(key, out var cached)) return cached;

        var bytes = await File.ReadAllBytesAsync(path);
        var data = "data:image/png;base64," + Convert.ToBase64String(bytes);
        DataUriCache[key] = data;
        return data;
    }

    private static byte[]? DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return null;
        try { return Convert.FromBase64String(dataUrl[(comma + 1)..]); }
        catch { return null; }
    }

    private static async Task SaveCacheAsync(string key, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(Path.Combine(CacheDirectory, key + ".png"), bytes);
        }
        catch
        {
            // 缓存写失败不影响本次显示
        }
    }

    private async Task ShowBytesAsync(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        var write = stream.AsStreamForWrite();
        await write.WriteAsync(bytes);
        await write.FlushAsync();
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        Rendered.Source = image;
    }

    private void ShowError()
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        Rendered.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideAll()
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        Rendered.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
