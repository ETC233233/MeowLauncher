using System.Text.Json.Serialization;
using MeowLauncher.WinUI.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowLauncher.WinUI.Models;

public sealed class ModInfo
{
    public string File { get; set; } = "";
    public string Name { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Size { get; set; } = "";
    public bool Enabled { get; set; }
    public string Description { get; set; } = "";
    public string Error { get; set; } = "";

    /// <summary>后端认出的「放错地方」类型：resourcepack / shaderpack / datapack；空 = 正常 Mod。</summary>
    public string Misplaced { get; set; } = "";

    /// <summary>资源包 / 光影包能一键移到实例对应文件夹；数据包要选存档，这里不给按钮。</summary>
    [JsonIgnore] public bool CanMove => Misplaced is "resourcepack" or "shaderpack";
    [JsonIgnore] public string MoveText => Misplaced == "shaderpack" ? "移到光影" : "移到资源包";
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility MoveVisibility =>
        CanMove ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility ToggleVisibility =>
        CanMove ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>sidecar 从 jar 里抽出的图标路径；没有图标时为空。</summary>
    [JsonPropertyName("icon")]
    public string IconPath { get; set; } = "";

    public ImageSource? IconSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconPath) || !System.IO.File.Exists(IconPath)) return null;
            try { return new BitmapImage(new Uri(IconPath)); }
            catch { return null; }
        }
    }

    /// <summary>mcmod 缓存里的中文名（可能为空）。</summary>
    [JsonPropertyName("cnName")]
    public string CnName { get; set; } = "";

    /// <summary>检查更新后由页面写入；空 = 没有可更新版本（或还没检查）。</summary>
    [JsonIgnore] public ModUpdate? Update { get; set; }
    [JsonIgnore] public string UpdateText => Update is null ? "" : $"→ {Update.Latest}";
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility UpdateVisibility =>
        Update is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string EnglishName => string.IsNullOrWhiteSpace(Name) ? File : Name;

    public string DisplayName => string.IsNullOrWhiteSpace(CnName) ? EnglishName : CnName;
    public string StatusText => Enabled ? "已启用" : "已停用";

    /// <summary>ListViewItem 的 UIA 名称取对象 ToString，不重写就会读出类型名。</summary>
    public override string ToString() => DisplayName;
    public string StatusGlyph => Enabled ? "\uE73E" : "\uE71A";
    public string Subtitle
    {
        get
        {
            var bits = new List<string>();
            // 标题已经显示的英文名不再重复；只有中文名和英文名确实不同才补在副行。
            if (!string.IsNullOrWhiteSpace(CnName)
                && !string.IsNullOrWhiteSpace(EnglishName)
                && !string.Equals(CnName.Trim(), EnglishName.Trim(), StringComparison.OrdinalIgnoreCase))
                bits.Add(EnglishName);
            // sidecar 偶尔把模板字面量当版本号吐回来（如 "v${file.jarVersion}"），
            // 直接上屏就是一条假版本。含 ${ / {{ 的一律回退成「版本未知」。
            var version = Version?.Trim() ?? "";
            if (version.Contains("${", StringComparison.Ordinal) || version.Contains("{{", StringComparison.Ordinal))
                bits.Add("版本未知");
            else if (version.Length > 0)
                bits.Add("v" + version);
            if (!string.IsNullOrWhiteSpace(Authors)) bits.Add(Authors);
            if (!string.IsNullOrWhiteSpace(Size)) bits.Add(Size);
            return string.Join(" · ", bits);
        }
    }
    public string Detail
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Error)) return Error;
            if (!string.IsNullOrWhiteSpace(Description)) return Description.Replace('\n', ' ').Trim();
            return string.IsNullOrWhiteSpace(ModId) ? File : ModId;
        }
    }
}

public sealed class ModsResult
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Dir { get; set; } = "";
    public List<ModInfo> Mods { get; set; } = new();
    public string Message { get; set; } = "";
}
