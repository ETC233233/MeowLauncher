namespace MeowLauncher.WinUI.Services;

internal sealed record IslandActivity(string Id, bool Active, bool Finished);

/// <summary>Keep a task's identity stable while it runs or the user is acting on its result.</summary>
internal static class IslandSelection
{
    internal static string? Choose(string? currentId, IReadOnlyList<IslandActivity> entries,
        bool interacting, DateTime now, DateTime holdUntil)
    {
        var current = entries.FirstOrDefault(e => e.Id == currentId);
        if (current is not null && (current.Active || (current.Finished && (interacting || now < holdUntil))))
            return current.Id;
        return entries.FirstOrDefault(e => e.Active)?.Id;
    }
}
