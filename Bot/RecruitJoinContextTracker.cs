using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RaidDcBot.Bot;

public sealed record RecruitJoinContext(
    ulong MessageId,
    string Position,
    string Job,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? CandidatePositions = null,
    string? RequestedLabel = null,
    IReadOnlyList<string>? CandidateJobs = null);

public sealed class RecruitJoinContextTracker
{
    private readonly ConcurrentDictionary<ulong, RecruitJoinContext> _contexts = new();

    public void Set(ulong userId, RecruitJoinContext context)
        => _contexts[userId] = context;

    public bool TryRemove(ulong userId, out RecruitJoinContext context)
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
