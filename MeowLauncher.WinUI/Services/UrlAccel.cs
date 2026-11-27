using System;
using System.Collections.Generic;

namespace MeowLauncher.WinUI.Services
{
    /// <summary>GitHub 下载加速：把 github.com 的地址改写到镜像前缀，开关与镜像都来自设置页。</summary>
    public static class UrlAccel
    {
        public const string DefaultHost = "https://gh-proxy.com/";

        public static bool Enabled { get; set; } = true;

        public static string Host { get; set; } = DefaultHost;

        /// <summary>规范化镜像前缀：补协议、补结尾斜杠；空串表示不加速。</summary>
        public static string Normalize(string? host)
        {
            var h = (host ?? string.Empty).Trim();
            if (h.Length == 0) return string.Empty;
            if (!h.StartsWith("http", StringComparison.OrdinalIgnoreCase)) h = "https://" + h;
            if (!h.EndsWith("/", StringComparison.Ordinal)) h += "/";
            return h;
        }

        public static string Apply(string url)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(url)) return url;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            // ghproxy accelerates files, not GitHub's API, repository pages or arbitrary hosts.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.UserInfo.Length > 0
                || !(u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                     && u.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal)
                     || u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                     || u.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))) return url;
            var host = Normalize(Host);
            if (host.Length == 0) return url;
            if (url.StartsWith(host, StringComparison.OrdinalIgnoreCase)) return url;
            return host + url;
        }

        /// <summary>先试镜像、再试原始地址；没开加速时只有一个原始地址。</summary>
        public static IEnumerable<string> Candidates(string url)
        {
            var accel = Apply(url);
            if (!string.Equals(accel, url, StringComparison.Ordinal)) yield return accel;
            yield return url;
        }
    }
}
