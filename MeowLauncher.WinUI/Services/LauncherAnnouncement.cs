using System.Globalization;
using System.Text.Json;

namespace MeowLauncher.WinUI.Services;

internal sealed record LauncherAnnouncement(string Id, string Title, string Message, DateTimeOffset PublishedAt,
    DateTimeOffset? ExpiresAt, bool Popup)
{
    internal static IReadOnlyList<LauncherAnnouncement> ReadActive(string payload, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("announcements", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        var announcements = new List<LauncherAnnouncement>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadText(item, "id");
            var title = ReadText(item, "title");
            var message = ReadText(item, "message");
            if (id.Length is 0 or > 64 || title.Length is 0 or > 100 || message.Length is 0 or > 2000
                || !ReadDate(item, "publishedAt", out var published) || published > now)
                continue;
            DateTimeOffset? expiresAt = null;
            if (item.TryGetProperty("expiresAt", out _))
            {
                if (!ReadDate(item, "expiresAt", out var expires) || expires <= now) continue;
                expiresAt = expires;
            }
            var popup = item.TryGetProperty("popup", out var setting) && setting.ValueKind == JsonValueKind.True;
            announcements.Add(new LauncherAnnouncement(id, title, message, published, expiresAt, popup));
        }
        return announcements.OrderByDescending(item => item.PublishedAt).DistinctBy(item => item.Id).Take(10).ToArray();
    }

    private static string ReadText(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static bool ReadDate(JsonElement item, string name, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(ReadText(item, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out date);
}

public sealed record AnnouncementReceipt(DateTimeOffset PublishedAt, bool Read = false, bool Dismissed = false);
