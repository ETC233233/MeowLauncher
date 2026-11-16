using System.Net.Http;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>MCIM 在线资源镜像，域名映射与 UA 约定见 https://www.mcimirror.top/guide/start/getting-started</summary>
internal static class McimClient
{
    internal const string Modrinth = "https://mod.mcimirror.top/modrinth/v2";
    internal static string UserAgent => $"MeowLauncher/{BuildInfo.VersionText}";
    internal static bool PreferOfficial => App.MainWindow?.Preferences.PreferOfficialResources == true;

    internal static bool SetPreferOfficial(bool value)
    {
        var prefs = App.MainWindow?.Preferences;
        if (prefs is null || prefs.PreferOfficialResources == value) return false;
        prefs.PreferOfficialResources = value;
        AppPreferencesStore.Save(prefs);
        return true;
    }

    internal static async Task<JsonDocument> SearchAsync(HttpClient http, string mirrorUrl, string officialUrl,
        string resultsField)
    {
        var first = PreferOfficial ? officialUrl : mirrorUrl;
        var second = first == officialUrl ? mirrorUrl : officialUrl;
        try
        {
            return await ReadAsync(http, first, resultsField, TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await ReadAsync(http, second, resultsField, TimeSpan.FromSeconds(8));
        }
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient http, string url, string resultsField,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // WinUI 的镜像搜索与 Python 下载使用同一个发布版本 UA。
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation.Token);
        if (json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty(resultsField, out var results) && results.ValueKind == JsonValueKind.Array)
            return json;
        json.Dispose();
        throw new JsonException($"资源源响应未包含 {resultsField} 列表。");
    }
}
