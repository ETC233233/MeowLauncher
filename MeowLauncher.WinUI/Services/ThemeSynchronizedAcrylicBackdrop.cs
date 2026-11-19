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
        base.OnTargetConnected(target, root);
        Sync(target, root);
        _controller = new DesktopAcrylicController { Kind = Thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
        _controller.SetSystemBackdropConfiguration(_configuration);
        ApplyTheme();
        _controller.AddSystemBackdropTarget(target);
        App.Trace($"Acrylic connected: kind={_controller.Kind}, state={_controller.State}");
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        Sync(target, root);
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
        if (_controller is not null)
        {
            _controller.RemoveSystemBackdropTarget(target);
            _controller.Dispose();
            _controller = null;
        }
        _configuration = null;
        base.OnTargetDisconnected(target);
    }
}
