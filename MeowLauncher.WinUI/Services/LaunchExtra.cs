using System.Text;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public sealed record LaunchExtra(
    int Width = 0, int Height = 0, bool Fullscreen = false,
    string Server = "", string GameArgs = "", string Env = "", string Wrapper = "",
    string Priority = "normal");

public sealed partial class BackendClient
{
    // 放进 BackendClient 的 partial 里；如果 BackendClient 不是 partial，就把两个方法粘进现有类即可。
    public async Task<(bool Ok, LaunchExtra Extra, string Message)> GetLaunchExtraAsync(string scope)
    {
        var result = await RunAsync($"--launch-extra \"{scope}\"", TimeSpan.FromSeconds(20));
        if (!result.Success) return (false, new LaunchExtra(), result.Error);
        try
        {
            using var doc = JsonDocument.Parse(result.Output.Trim());
            var r = doc.RootElement;
            int I(string k) => r.TryGetProperty(k, out var e) && e.TryGetInt32(out var v) ? v : 0;
            string S(string k) => r.TryGetProperty(k, out var e) ? e.GetString() ?? "" : "";
            bool B(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.True;
            return (true, new LaunchExtra(I("width"), I("height"), B("fullscreen"),
                S("server"), S("gameArgs"), S("env"), S("wrapper"),
                string.IsNullOrEmpty(S("priority")) ? "normal" : S("priority")), "");
        }
        catch (JsonException) { return (false, new LaunchExtra(), "启动附加设置数据格式异常"); }
    }

    public async Task<BackendResult> SetLaunchExtraAsync(string scope, LaunchExtra x)
    {
        var payload = JsonSerializer.Serialize(new
        {
            width = x.Width, height = x.Height, fullscreen = x.Fullscreen, server = x.Server,
            gameArgs = x.GameArgs, env = x.Env, wrapper = x.Wrapper, priority = x.Priority,
        });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var result = await RunAsync($"--set-launch-extra \"{scope}\" {encoded}", TimeSpan.FromSeconds(20));
        return result.Success ? ParseActionResult(result.Output) : new BackendResult(false, result.Error);
    }
}
