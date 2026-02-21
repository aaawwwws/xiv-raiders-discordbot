using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public enum LotteryCreateMentionMode
{
    None = 0,
    Here = 1,
    Everyone = 2
}

public sealed record LotteryCreateDraftContext(
    DateTimeOffset CloseAtUtc,
    bool AllowDuplicateWinners,
    LotteryCreateMentionMode MentionMode,
    DateTimeOffset CreatedAt);

public sealed class LotteryCreateDraftContextTracker
{
    private readonly ConcurrentDictionary<ulong, LotteryCreateDraftContext> _contexts = new();

    public void Set(ulong userId, LotteryCreateDraftContext context)
        => _contexts[userId] = context;

    public bool TryGet(ulong userId, out LotteryCreateDraftContext context)
        => _contexts.TryGetValue(userId, out context!);

    public bool TryRemove(ulong userId, out LotteryCreateDraftContext context)
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
