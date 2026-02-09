using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed record RecruitCommentEditContext(ulong MessageId, DateTimeOffset CreatedAt);

public sealed class RecruitCommentEditContextTracker
{
    private readonly ConcurrentDictionary<ulong, RecruitCommentEditContext> _contexts = new();

    public void Set(ulong userId, RecruitCommentEditContext context)
        => _contexts[userId] = context;

    public bool TryRemove(ulong userId, out RecruitCommentEditContext context)
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
