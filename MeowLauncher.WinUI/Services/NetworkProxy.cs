using System.Net;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 全局网络代理：设置页改一次，HTTP 客户端与 sidecar 子进程都跟着变。
/// system = 不动（.NET 默认读系统代理）；direct = 显式关闭；custom = 用 host:port。
/// </summary>
public static class NetworkProxy
{
    public static AppPreferences? Current { get; private set; }

    /// <summary>当前生效的代理；null 表示交给系统默认。</summary>
    public static IWebProxy? Proxy { get; private set; }

    /// <summary>给子进程（python sidecar）用的环境变量。</summary>
    public static Dictionary<string, string> ChildEnvironment { get; } = new();

    /// <summary>设置页保存后调用：重算代理并让 HTTP 客户端重建。</summary>
    public static void Apply(AppPreferences preferences)
    {
        Current = preferences;
        ChildEnvironment.Clear();

        var mode = preferences.ProxyMode;
        if (string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            Proxy = new WebProxy();
            ChildEnvironment["NO_PROXY"] = "*";
            ChildEnvironment["no_proxy"] = "*";
        }
        else if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(preferences.ProxyHost)
                 && preferences.ProxyPort > 0)
        {
            var bare = new Uri($"http://{preferences.ProxyHost.Trim()}:{preferences.ProxyPort}");
            var user = (preferences.ProxyUser ?? "").Trim();
            var secret = SecretStore.Unprotect(preferences.ProxySecret ?? "");
            var proxy = new WebProxy(bare);
            if (user.Length > 0)
                proxy.Credentials = new NetworkCredential(user, secret);
            Proxy = proxy;

            // 子进程（python sidecar）只认 URL 里的账号密码，这里把同一份凭据编进环境变量。
            var text = user.Length > 0
                ? $"http://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(secret)}@{preferences.ProxyHost.Trim()}:{preferences.ProxyPort}"
                : bare.ToString();
            ChildEnvironment["HTTP_PROXY"] = text;
            ChildEnvironment["HTTPS_PROXY"] = text;
            ChildEnvironment["http_proxy"] = text;
            ChildEnvironment["https_proxy"] = text;
        }
        else
        {
            Proxy = null;   // 跟随系统
        }

        ModrinthClient.ResetProxy();
    }

    /// <summary>设置页顶部那行状态小字用的说明。</summary>
    public static string Describe()
    {
        var prefs = Current;
        if (prefs is null) return "";
        return prefs.ProxyMode switch
        {
            "direct" => "不使用代理",
            "custom" => string.IsNullOrWhiteSpace(prefs.ProxyHost) || prefs.ProxyPort <= 0
                ? "自定义代理未填写，按系统处理"
                : string.IsNullOrWhiteSpace(prefs.ProxyUser)
                    ? $"{prefs.ProxyHost.Trim()}:{prefs.ProxyPort}"
                    : $"{prefs.ProxyUser.Trim()}@{prefs.ProxyHost.Trim()}:{prefs.ProxyPort}",
            _ => "跟随系统",
        };
    }
}
