using System.Net.Http;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public sealed partial class BackendClient
{
    internal async Task<JsonDocument> SearchCurseForgeAsync(Dictionary<string, string> parameters)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(parameters));
        var result = await RunAsync($"--curseforge-search {encoded}", TimeSpan.FromSeconds(14));
        if (!result.Success) throw new HttpRequestException(result.Error);
        var json = JsonDocument.Parse(result.Output.Trim());
        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return json;
        var message = json.RootElement.TryGetProperty("message", out var error)
            ? error.GetString() : null;
        json.Dispose();
        throw new HttpRequestException(message ?? "CurseForge 缺少搜索结果列表。");
    }
}
