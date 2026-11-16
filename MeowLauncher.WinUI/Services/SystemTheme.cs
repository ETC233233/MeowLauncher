using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// Windows 应用深浅色模式：build 17763 起才有 AppsUseLightTheme；
/// 旧系统没有这个值，主题只能锁定在「跟随系统」。
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int MinBuild = 17763;

    /// <summary>系统是否支持应用深浅色模式。</summary>
    public static bool IsSupported =>
        Environment.OSVersion.Version.Build >= MinBuild && ReadAppsUseLightTheme().HasValue;

    /// <summary>当前系统是否深色；读取不到时回退到应用当前主题。</summary>
    public static bool IsDark()
    {
        var light = ReadAppsUseLightTheme();
        if (light.HasValue) return light.Value == 0;
        return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
    }

    private static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            var raw = key?.GetValue("AppsUseLightTheme");
            return raw switch
            {
                int value => value,
                long value => (int)value,
                uint value => (int)value,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
