using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed record MemberRegistrationDraftContext(
    string FfxivName,
    string Bio,
    string? XHandle,
    string? BlueskyHandle,
    DateTimeOffset CreatedAt,
    string? StoryProgression = null);

public sealed class MemberRegistrationDraftContextTracker
{
    private readonly ConcurrentDictionary<ulong, MemberRegistrationDraftContext> _contexts = new();

    public void Set(ulong userId, MemberRegistrationDraftContext context)
        => _contexts[userId] = context;

    public bool TryGet(ulong userId, out MemberRegistrationDraftContext context)
        => _contexts.TryGetValue(userId, out context!);

    public bool TryRemove(ulong userId, out MemberRegistrationDraftContext context)
        => _contexts.TryRemove(userId, out context!);

    public void PruneExpired(DateTimeOffset now, TimeSpan timeout)
    {
        foreach (var pair in _contexts)
        {
            if (now - pair.Value.CreatedAt > timeout)
            {
                _contexts.TryRemove(pair.Key, out _);
            }
        }
    }
}
