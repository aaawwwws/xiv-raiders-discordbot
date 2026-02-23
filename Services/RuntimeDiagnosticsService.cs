using System.Collections.Concurrent;

namespace RaidDcBot.Services;

public sealed class RuntimeDiagnosticsService
{
    private readonly ConcurrentDictionary<string, RuntimeHeartbeat> _heartbeats = new(StringComparer.OrdinalIgnoreCase);

    public void Mark(string key, bool success, string? note = null, DateTimeOffset? nowUtc = null)
    {
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        _heartbeats.AddOrUpdate(
            key,
            _ => new RuntimeHeartbeat(key, timestamp, success, note),
            (_, existing) => existing with
            {
                LastRunAtUtc = timestamp,
                LastSuccess = success,
                LastNote = string.IsNullOrWhiteSpace(note) ? existing.LastNote : note
            });
    }

    public IReadOnlyList<RuntimeHeartbeat> Snapshot()
        => _heartbeats.Values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public RuntimeHeartbeat? Get(string key)
        => _heartbeats.TryGetValue(key, out var value) ? value : null;
}

public sealed record RuntimeHeartbeat(
    string Key,
    DateTimeOffset LastRunAtUtc,
    bool LastSuccess,
    string? LastNote);
