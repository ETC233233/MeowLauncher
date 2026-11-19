using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowLauncher.WinUI.Services;

/// <summary>读取组件包附带的版本声明，不执行组件中的程序。</summary>
public static class ComponentDetails
{
    public static string EasyTierVersion(string dir)
    {
        try
        {
            // 新包可以直接声明；旧包从随包的第三方声明表读取，不写死版本号。
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "component.json")));
            if (manifest.RootElement.TryGetProperty("easyTierVersion", out var value)
                && value.ValueKind == JsonValueKind.String && Valid(value.GetString()) is { } version)
                return version;

            var notices = Path.Combine(dir, "Licenses", "THIRD-PARTY-NOTICES.md");
            if (File.Exists(notices))
            {
                foreach (var line in File.ReadLines(notices))
                {
                    var columns = line.Split('|');
                    if (columns.Length > 3 && columns[1].Trim().Equals("EasyTier", StringComparison.OrdinalIgnoreCase)
                        && Valid(columns[2].Trim()) is { } declared)
                        return declared;
                }
            }
        }
        catch { }
        return "";
    }

    private static string? Valid(string? value) => value is not null
        && Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$")
        ? value.TrimStart('v') : null;
}
