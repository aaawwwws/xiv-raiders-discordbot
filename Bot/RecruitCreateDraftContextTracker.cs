using System.Collections.Concurrent;

namespace RaidDcBot.Bot;

public sealed record RecruitCreateDraftContext(
    ulong GuildId,
    ulong ChannelId,
    ulong TargetChannelId,
    string Title,
    string? DataCenter,
    string? Schedule,
    DateTimeOffset? ScheduleAt,
    string? Note,
    IReadOnlyList<string> Positions,
    IReadOnlyDictionary<string, string> RequiredJobs,
    DateTimeOffset CreatedAt);

public sealed class RecruitCreateDraftContextTracker
{
    private readonly ConcurrentDictionary<ulong, RecruitCreateDraftContext> _contexts = new();

    public void Set(ulong userId, RecruitCreateDraftContext context)
        => _contexts[userId] = context;

    public bool TryGet(ulong userId, out RecruitCreateDraftContext context)
        => _contexts.TryGetValue(userId, out context!);

    public bool TryRemove(ulong userId, out RecruitCreateDraftContext context)
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
