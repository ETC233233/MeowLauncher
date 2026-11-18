using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowLauncher.WinUI.Models;

public sealed class Snapshot
{
    public bool Ok { get; set; }
    public AccountInfo? Account { get; set; }
    public string GameDir { get; set; } = "";
    public VersionInfo? Current { get; set; }
    public List<VersionInfo> Versions { get; set; } = new();
    public MemoryInfo? Memory { get; set; }
    public ShotInfo? Shots { get; set; }
    public OnlineInfo? Online { get; set; }
    public string Ago { get; set; } = "还没启动过";
    public string Selected { get; set; } = "";
    public Dictionary<string, long> LastPlayed { get; set; } = new();
    public List<AccountInfo> Accounts { get; set; } = new();
}

public sealed class AccountInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string SkinPath { get; set; } = "";

    /// <summary>当前激活披风的本地贴图缓存；没有披风时为空。</summary>
    public string CapePath { get; set; } = "";

    /// <summary>皮肤模型：slim（Alex 细臂）或 classic（Steve 宽臂）。</summary>
    public string Variant { get; set; } = "classic";

    /// <summary>离线账户当前用的是内置默认皮肤，不是用户选的本机皮肤。</summary>
    public bool DefaultSkin { get; set; }

    public bool Slim => Variant.Equals("slim", StringComparison.OrdinalIgnoreCase);

    /// <summary>由 sidecar 按 3D 头盒投影生成的 128×128 透明头像（官方皮肤按 UUID 拉取并缓存）。</summary>
    [JsonPropertyName("head")]
    public string HeadPath { get; set; } = "";

    public ImageSource? HeadSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HeadPath) || !System.IO.File.Exists(HeadPath)) return null;
            try { return new BitmapImage(new Uri(HeadPath)); }
            catch { return null; }
        }
    }

    public bool Active { get; set; }

    public string Key => $"{Uuid}|{Name}";

    public string TypeDisplay => Type.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
        ? "正版账户"
        : "离线账户";
}

public sealed class VersionInfo
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>实例所在的游戏目录（多目录时用于分组、启动与打开目录）。</summary>
    public string Root { get; set; } = "";

    public string Loader { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public string Type { get; set; } = "";
    public int Mods { get; set; }
    public bool HasMods { get; set; }
    public long Mtime { get; set; }
    public long LastPlayed { get; set; }
    public bool Valid { get; set; } = true;
    public bool Hidden { get; set; }

    /// <summary>由页面在加载快照时标记，用于在列表里标出当前实例。</summary>
    public bool IsCurrent { get; set; }

    public string GameVersion => string.IsNullOrWhiteSpace(BaseVersion)
        ? Id.Split('-')[0]
        : BaseVersion;

    public string LoaderDisplay
    {
        get
        {
            var raw = Loader?.Trim() ?? "";
            if (raw.Length == 0 || raw.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                return "原版";
            if (raw.Equals("modded", StringComparison.OrdinalIgnoreCase))
                return "未识别加载器";

            return string.IsNullOrWhiteSpace(LoaderVersion) ? raw : $"{raw} {LoaderVersion}";
        }
    }

    public string Subtitle => string.Equals(GameVersion, Id, StringComparison.OrdinalIgnoreCase) ? LoaderDisplay : $"{GameVersion} · {LoaderDisplay}";

    /// <summary>版本信息一行：`Release 1.12.2 Forge 14.23.5.2855`（类型 游戏版本 加载器版本）。</summary>
    public string VersionLine
    {
        get
        {
            var parts = new List<string>();
            var type = (Type ?? "").Trim();
            if (type.Length > 0)
            {
                parts.Add(type.ToLowerInvariant() switch
                {
                    "release" => "Release",
                    "snapshot" => "Snapshot",
                    "old_beta" => "Beta",
                    "old_alpha" => "Alpha",
                    _ => type,
                });
            }
            parts.Add(GameVersion);
            parts.Add(LoaderDisplay);
            return string.Join(" ", parts);
        }
    }

    /// <summary>控件（ComboBox / ListViewItem）直接用对象时显示版本号，而不是类型名。</summary>
    public override string ToString() => Id;

    public string ModText => Mods <= 0 ? "无 Mod" : $"{Mods} 个 Mod";

    public string AgoText
    {
        get
        {
            if (LastPlayed <= 0) return "尚未启动过";
            var ts = LastPlayed > 10_000_000_000 ? LastPlayed / 1000 : LastPlayed;
            var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
            if (span.TotalMinutes < 1) return "刚刚启动过";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
        }
    }
}

public sealed class MemoryInfo
{
    public double Total { get; set; }
    public double Used { get; set; }
    public int Pct { get; set; }
}

/// <summary>sidecar --shots <实例>：该实例真实运行目录（隔离实例在 versions/<id>）下的截图统计。</summary>
public sealed class ShotsResult
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Dir { get; set; } = "";
    public int Count { get; set; }
    public string Latest { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

public sealed class ShotInfo
{
    public int Count { get; set; }
    public List<string> Files { get; set; } = new();

    /// <summary>按实例本次游玩时间窗挑出的干净截图，键是实例 id。</summary>
    public Dictionary<string, string> Covers { get; set; } = new();

    /// <summary>最新一张非 F3 调试界面的截图；仅作没有实例截图时的视觉兜底。</summary>
    public string Latest { get; set; } = "";
}

public sealed class OnlineInfo
{
    public bool Ok { get; set; }

    /// <summary>最慢一项的往返毫秒数（逐服务探测里的最大值）。</summary>
    public int Ms { get; set; }

    /// <summary>可达服务数 / 探测总数，由 sidecar 逐服务探测得到，前端不做估算。</summary>
    public int Reachable { get; set; }
    public int Total { get; set; }

    /// <summary>逐服务可达性（版本清单 / 资源文件 / 会话 / 账户 / 皮肤）。</summary>
    public List<OnlineService> Items { get; set; } = new();

    public string? Error { get; set; }

    /// <summary>true = 这一份只是占位：探测还在后台跑，界面该显示「检测中」而不是「不可达」。</summary>
    public bool Unknown { get; set; }

    /// <summary>整轮探测耗时（毫秒），用于「更新于 …」旁边的响应时间标注。</summary>
    public int Elapsed { get; set; }
}

public sealed class OnlineService
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Ok { get; set; }
    public int Ms { get; set; }
}
