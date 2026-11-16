using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowLauncher.WinUI.Models;

public sealed class PackInfo
{
    public string Kind { get; set; } = "resourcepack";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Format { get; set; }
    public long Size { get; set; }
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>sidecar 从 zip / 文件夹里取出的 pack.png；没有则为空。</summary>
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

    public bool IsShader => Kind == "shaderpack";

    /// <summary>存档行才有「导入数据包」：数据包要装进具体存档的 datapacks 文件夹。</summary>
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility DatapackVisibility =>
        Kind == "world" ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string KindText => IsShader ? "光影" : "资源包";

    /// <summary>ListViewItem 的 UIA 名称取对象 ToString。</summary>
    public override string ToString() => Name;
    public string StatusText => Enabled ? "已启用" : "已停用";
    public string SizeText => Size <= 0 ? "" : Size switch
    {
        >= 1024L * 1024 * 1024 => $"{Size / 1024d / 1024d / 1024d:0.##} GB",
        >= 1024L * 1024 => $"{Size / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{Size / 1024d:0.#} KB",
        _ => $"{Size} B",
    };
    /// <summary>列表副行：只有真的写了描述才显示；pack_format 是给排查用的，不上屏。</summary>
    public string Detail => string.IsNullOrWhiteSpace(Description) ? "" : Description.Trim();
}

public sealed class PacksResult
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Dir { get; set; } = "";
    /// <summary>该实例真实的存档 / 截图目录（开了版本隔离时在实例目录里，不是全局 saves）。</summary>
    public string SavesDir { get; set; } = "";
    public string ScreenshotsDir { get; set; } = "";
    public List<PackInfo> Packs { get; set; } = [];
}
