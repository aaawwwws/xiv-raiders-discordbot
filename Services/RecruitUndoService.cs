using System.Collections.Concurrent;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class RecruitUndoService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, RecruitUndoEntry> _entries = new(StringComparer.Ordinal);

    public string Put(ulong guildId, int postId, ulong messageId, ulong actorUserId, RecruitPostStatus fromStatus, RecruitPostStatus toStatus)
    {
        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var entry = new RecruitUndoEntry(
            Token: token,
            GuildId: guildId,
            PostId: postId,
            MessageId: messageId,
            ActorUserId: actorUserId,
            FromStatus: fromStatus,
            ToStatus: toStatus,
            CreatedAtUtc: now,
            ExpiresAtUtc: now.Add(DefaultTtl));
        _entries[token] = entry;
        PruneExpired(now);
        return token;
    }

    public bool TryTake(string token, out RecruitUndoEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!_entries.TryRemove(token, out var found))
        {
            return false;
        }

        if (found.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return false;
        }

        entry = found;
        return true;
    }

    public void PruneExpired(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc < now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }
}

public sealed record RecruitUndoEntry(
    string Token,
    ulong GuildId,
    int PostId,
    ulong MessageId,
    ulong ActorUserId,
    RecruitPostStatus FromStatus,
    RecruitPostStatus ToStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);
