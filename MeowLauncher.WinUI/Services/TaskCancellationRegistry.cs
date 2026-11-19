using System.Collections.Concurrent;

namespace MeowLauncher.WinUI.Services;

/// <summary>Cancellation exists before a worker starts, independently of its process ID.</summary>
internal sealed class TaskCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();
    public void Create(string id) => _sources.TryAdd(id, new CancellationTokenSource());
    public CancellationToken TokenFor(string? id) => id is not null && _sources.TryGetValue(id, out var source)
        ? source.Token : CancellationToken.None;
    public bool Cancel(string id)
    {
        if (!_sources.TryGetValue(id, out var source)) return false;
        source.Cancel();
        return true;
    }
}
