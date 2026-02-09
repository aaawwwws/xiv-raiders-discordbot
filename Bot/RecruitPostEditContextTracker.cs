using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed record RecruitPostEditContext(ulong MessageId, DateTimeOffset CreatedAt);

public sealed class RecruitPostEditContextTracker
{
    private readonly ConcurrentDictionary<ulong, RecruitPostEditContext> _contexts = new();

    public void Set(ulong userId, RecruitPostEditContext context)
        => _contexts[userId] = context;

    public bool TryRemove(ulong userId, out RecruitPostEditContext context)
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
