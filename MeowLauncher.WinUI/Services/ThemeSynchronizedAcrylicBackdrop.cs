using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowLauncher.WinUI.Services;

/// <summary>Windows acrylic presets, synchronized with the window's resolved theme.</summary>
public sealed class ThemeSynchronizedAcrylicBackdrop : SystemBackdrop
{
    private SystemBackdropConfiguration? _configuration;
    private DesktopAcrylicController? _controller;
    private bool _dark;
    public bool Thin { get; init; }

    public bool Dark
    {
        get => _dark;
        set { _dark = value; ApplyTheme(); }
    }

    private void ApplyTheme()
    {
        if (_configuration is not null)
            _configuration.Theme = Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // 窗口要在关掉 / 重建 XamlRoot 时 XAML 仍会回调这里，参数可能已经失效；
        // 抛出去会变成 XamlUnhandled（crash.log 里那条「参数错误。target」），所以全部兜住。
        try
        {
            base.OnTargetConnected(target, root);
            if (target is null || root is null) return;
            Sync(target, root);
            _controller = new DesktopAcrylicController { Kind = Thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
            _controller.SetSystemBackdropConfiguration(_configuration);
            ApplyTheme();
            _controller.AddSystemBackdropTarget(target);
            App.Trace($"Acrylic connected: kind={_controller.Kind}, state={_controller.State}");
        }
        catch (Exception ex)
        {
            App.Trace($"Acrylic connect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // 同上：拆窗口 / 切主题时这个回调可能在 target 已失效后被调用，
        // GetDefaultSystemBackdropConfiguration 会抛 ArgumentException（参数 target），
        // 在原生回调里抛出去就是 0xc000027b 崩溃（crash.log 里反复出现）。
        if (target is null || root is null || _controller is null) return;
        try
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, root);
            Sync(target, root);
        }
        catch (Exception ex)
        {
            App.Trace($"Acrylic config sync ignored: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    private void Sync(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // Use XAML's live configuration so activation and accessibility keep
        // updating. Only the theme is explicitly synchronized with MainWindow.
        var configuration = GetDefaultSystemBackdropConfiguration(target, root);
        var changed = !ReferenceEquals(_configuration, configuration);
        _configuration = configuration;
        ApplyTheme();
        if (changed) _controller?.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        // 关窗口时 target 可能已经被原生侧释放，RemoveSystemBackdropTarget 会抛
        // 无参数名的 ArgumentException（crash.log 里那条没有堆栈的「参数错误。」）。
        try
        {
            if (_controller is not null)
            {
                try { _controller.RemoveSystemBackdropTarget(target); } catch (Exception) { }
                _controller.Dispose();
                _controller = null;
            }
        }
        catch (Exception ex)
        {
            App.Trace($"Acrylic disconnect ignored: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            _configuration = null;
            try { base.OnTargetDisconnected(target); } catch (Exception) { }
        }
    }
}
