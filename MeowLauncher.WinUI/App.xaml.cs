using Microsoft.UI.Xaml;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Services;

namespace MeowLauncher.WinUI;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

#if DEBUG
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _debugNavTimer;
#endif
    public static BackendClient Backend { get; } = new();

    /// <summary>联机服务（账号握手 + 邀请推送）。三层开关都打开才会 Start，默认不运行。</summary>
    public static Services.OnlineService Online { get; } = new();
    public static Services.RoomService Room { get; } = new();

    public App()
    {
        PrepareWebView2UserDataFolder();
        UnhandledException += (_, e) =>
        {
            LogCrash("XamlUnhandled", e.Exception);
            e.Handled = true;
            try { MainWindow?.ShowCrashToast(); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };
        InitializeComponent();
    }

    /// <summary>
    /// WebView2 默认把浏览器用户数据放在 exe 所在目录（安装版是 Program Files），普通用户没有写权限，
    /// 于是引导页、皮肤立绘等所有 WebView2 都静默初始化失败。这里统一改到 LocalAppData，必须在创建任何 WebView2 之前执行。
    /// </summary>
    private static void PrepareWebView2UserDataFolder()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeowLauncher.WinUI", "WebView2");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dir);
        }
        catch { }
    }

    /// <summary>
    /// 崩溃面包屑（Debug 与 Release 都写）：把关键步骤追加到 %APPDATA%\MeowLauncher\trace.log，
    /// 原生崩溃（0xc000027b）没有托管堆栈，最后一行就是定位线索。文件超过 512 KB 自动轮转，不用环境变量。
    /// </summary>
    private static readonly object TraceLock = new();

    public static void Trace(string message)
    {
        try
        {
            lock (TraceLock)
            {
                var dir = Services.AppPreferencesStore.FolderPath;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "trace.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    var old = Path.Combine(dir, "trace.old.log");
                    File.Move(path, old, overwrite: true);
                }
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] T {message}\n";
                File.AppendAllText(path, line);
#if DEBUG
                File.AppendAllText(Path.Combine(dir, "debug-nav.log"), line);
#endif
            }
        }
        catch { }
    }

    public static void LogCrash(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Services.AppPreferencesStore.FolderPath);
            var path = Path.Combine(Services.AppPreferencesStore.FolderPath, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {source}\n{exception}\n\n");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTimeline.Mark("OnLaunched 开始");
        // 资源管理器的「打开方式」把文件路径作为独立的命令行参数传入。
        var packs = Environment.GetCommandLineArgs().Skip(1)
            .Where(path => Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        MainWindow = new MainWindow(packs);
        StartupTimeline.Mark("MainWindow 构造完成");
        MainWindow.Activate();
        StartupTimeline.Mark("窗口已激活");
        _ = SkinRenderView.PrewarmAsync();

#if DEBUG
        var navFile = Path.Combine(Services.AppPreferencesStore.FolderPath, "debug-nav.txt");
        _debugNavTimer = MainWindow.DispatcherQueue.CreateTimer();
        _debugNavTimer.Interval = TimeSpan.FromMilliseconds(500);
        _debugNavTimer.Tick += (_, _) =>
        {
            try
            {
                if (!File.Exists(navFile)) return;
                var line = File.ReadAllText(navFile);
                File.Delete(navFile);
                MainWindow.DebugGoto(line);
            }
            catch { }
        };
        _debugNavTimer.Start();
#endif
    }
}
