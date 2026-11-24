using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

/// <summary>「此版本更新」里的一条：方块 / 物品 / 生物名 + 若干要点。</summary>
public sealed class NotesEntry
{
    public string Name { get; set; } = "";
    public List<string> Points { get; set; } = new();
}

public sealed class NotesSub
{
    public string Name { get; set; } = "";
    public List<NotesEntry> Entries { get; set; } = new();
}

public sealed class NotesSection
{
    public string Title { get; set; } = "";
    public List<NotesSub> Subs { get; set; } = new();
}

/// <summary>后端 --version-notes 的输出（中文 Minecraft Wiki 的「新内容 / 更改 / 修复」）。</summary>
/// <summary>后端 --version-cover 的输出：Cover 是本地缓存图片的绝对路径。</summary>
public sealed class VersionCover
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Cover { get; set; } = "";
}

public sealed class VersionNotes
{
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public bool Stale { get; set; }
    public List<NotesSection> Sections { get; set; } = new();
}

public sealed partial class BackendClient
{
    /// <summary>读某个正式版的更新日志；后端按版本缓存 3 天，失败时用旧缓存（Stale=true）。</summary>
    public async Task<VersionNotes?> GetVersionNotesAsync(string versionId, bool refresh = false)
    {
        var arguments = $"--version-notes \"{versionId}\"" + (refresh ? " --refresh" : "");
        var result = await RunAsync(arguments, TimeSpan.FromSeconds(30));
        if (!result.Success) return null;

        try
        {
            return JsonSerializer.Deserialize<VersionNotes>(result.Output.Trim(), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>版本页的宣传图（后端下到本地缓存），返回本地绝对路径；取不到是 null。</summary>
    public async Task<string?> GetVersionCoverAsync(string versionId)
    {
        var result = await RunAsync($"--version-cover \"{versionId}\"", TimeSpan.FromSeconds(25));
        if (!result.Success) return null;

        try
        {
            var cover = JsonSerializer.Deserialize<VersionCover>(result.Output.Trim(), JsonOptions);
            return cover is { Ok: true } && !string.IsNullOrWhiteSpace(cover.Cover) ? cover.Cover : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
