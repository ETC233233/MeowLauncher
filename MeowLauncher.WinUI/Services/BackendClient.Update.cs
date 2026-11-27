using System.Text;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

public sealed partial class BackendClient
{
    public async Task<BackendResult> DownloadLauncherAsync(LauncherPackage package, string path,
        IProgress<string> progress, string taskId)
    {
        var urls = package.Urls.SelectMany(UrlAccel.Candidates).Distinct(StringComparer.Ordinal).ToArray();
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { urls, size = package.Size, sha256 = package.Sha256, path })));
        var result = await RunAsync($"--launcher-download {data} --progress", TimeSpan.FromMinutes(40), progress.Report, taskId: taskId);
        return ParseActionResult(result);
    }

    public bool HasRunningOperations { get { lock (RunningGate) return Running.Count > 0; } }
}
