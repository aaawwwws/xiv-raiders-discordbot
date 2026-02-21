using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed record RecruitPostEditContext(
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    string? Title,
    string? DataCenter,
    string? Schedule,
    DateTimeOffset? ScheduleAt,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed class RecruitPostEditContextTracker
{
    private readonly ConcurrentDictionary<ulong, RecruitPostEditContext> _contexts = new();

    public void Set(ulong userId, RecruitPostEditContext context)
        => _contexts[userId] = context;

    public bool TryGet(ulong userId, out RecruitPostEditContext context)
        => _contexts.TryGetValue(userId, out context!);

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
