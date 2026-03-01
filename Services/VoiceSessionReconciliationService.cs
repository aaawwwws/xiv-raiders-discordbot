using System.Collections.Concurrent;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public readonly record struct VoiceSessionReconcileResult(
    int OpenSessionCount,
    int CurrentVoiceUserCount,
    int DuplicateOpenSessionCount,
    int StaleSessionCount,
    int MismatchedChannelSessionCount,
    int MissingOpenSessionCount,
    int ClosedDuplicateSessions,
    int ClosedStaleSessions,
    int ClosedMismatchedSessions,
    int StartedMissingSessions,
    int LongOpenSessionCount)
{
    public int PlannedCloseCount => DuplicateOpenSessionCount + StaleSessionCount + MismatchedChannelSessionCount;
    public bool HasPlannedActions => PlannedCloseCount > 0 || MissingOpenSessionCount > 0;
    public bool HasAppliedChanges => ClosedDuplicateSessions > 0 || ClosedStaleSessions > 0 || ClosedMismatchedSessions > 0 || StartedMissingSessions > 0;
}

public sealed class VoiceSessionReconciliationService
{
    private static readonly TimeSpan LongOpenSessionThreshold = TimeSpan.FromHours(24);

    private readonly Db _db;
    private readonly ILogger<VoiceSessionReconciliationService> _logger;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();

    public VoiceSessionReconciliationService(Db db, ILogger<VoiceSessionReconciliationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<VoiceSessionReconcileResult> ReconcileGuildAsync(
        SocketGuild guild,
        bool repair,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var syncLock = _guildLocks.GetOrAdd(guild.Id, static _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            var openSessions = await _db.ListOpenVoiceSessionsByGuildAsync(guild.Id);
            var currentPresence = BuildCurrentVoicePresence(guild);
            var openByUser = openSessions
                .GroupBy(session => ToUlong(session.UserId))
                .Where(group => group.Key != 0)
                .ToArray();

            var duplicateSessions = new List<VoiceSession>();
            var staleSessions = new List<VoiceSession>();
            var mismatchedSessions = new List<VoiceSession>();
            var usersWithValidOpenSession = new HashSet<ulong>();

            foreach (var group in openByUser)
            {
                var ordered = group
                    .OrderByDescending(session => session.JoinedAt)
                    .ThenByDescending(session => session.Id)
                    .ToArray();
                if (ordered.Length == 0)
                {
                    continue;
                }

                var primary = ordered[0];
                if (ordered.Length > 1)
                {
                    duplicateSessions.AddRange(ordered.Skip(1));
                }

                if (!currentPresence.TryGetValue(group.Key, out var current))
                {
                    staleSessions.Add(primary);
                    continue;
                }

                var openChannelId = ToUlong(primary.ChannelId);
                if (openChannelId == current.ChannelId)
                {
                    usersWithValidOpenSession.Add(group.Key);
                    continue;
                }

                mismatchedSessions.Add(primary);
            }

            var missingSessions = currentPresence
                .Where(pair => !usersWithValidOpenSession.Contains(pair.Key))
                .ToArray();

            var longOpenSessionCount = openSessions.Count(session =>
                session.JoinedAt != default &&
                nowUtc - session.JoinedAt >= LongOpenSessionThreshold);

            var result = new VoiceSessionReconcileResult(
                OpenSessionCount: openSessions.Count,
                CurrentVoiceUserCount: currentPresence.Count,
                DuplicateOpenSessionCount: duplicateSessions.Count,
                StaleSessionCount: staleSessions.Count,
                MismatchedChannelSessionCount: mismatchedSessions.Count,
                MissingOpenSessionCount: missingSessions.Length,
                ClosedDuplicateSessions: 0,
                ClosedStaleSessions: 0,
                ClosedMismatchedSessions: 0,
                StartedMissingSessions: 0,
                LongOpenSessionCount: longOpenSessionCount);

            if (!repair)
            {
                return result;
            }

            var closedDuplicate = await CloseSessionsByIdAsync(duplicateSessions, nowUtc);
            var closedStale = await CloseSessionsByIdAsync(staleSessions, nowUtc);
            var closedMismatched = await CloseSessionsByIdAsync(mismatchedSessions, nowUtc);
            var startedMissing = 0;

            foreach (var (userId, presence) in missingSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = guild.GetUser(userId);
                if (user is null)
                {
                    continue;
                }

                try
                {
                    await _db.StartVoiceSessionAsync(
                        guild.Id,
                        presence.ChannelId,
                        user.Id,
                        user.ToString() ?? user.Username ?? $"u:{user.Id}",
                        nowUtc);
                    startedMissing++;
                }
                catch (Exception ex)
                {
                    var open = await _db.GetOpenVoiceSessionAsync(guild.Id, user.Id);
                    if (open is null)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to start missing voice session during reconciliation. GuildId={GuildId}, UserId={UserId}, ChannelId={ChannelId}",
                            guild.Id,
                            user.Id,
                            presence.ChannelId);
                    }
                }
            }

            return result with
            {
                ClosedDuplicateSessions = closedDuplicate,
                ClosedStaleSessions = closedStale,
                ClosedMismatchedSessions = closedMismatched,
                StartedMissingSessions = startedMissing
            };
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task<int> CloseSessionsByIdAsync(IReadOnlyCollection<VoiceSession> sessions, DateTimeOffset leftAt)
    {
        if (sessions.Count == 0)
        {
            return 0;
        }

        var closed = 0;
        var uniqueSessionIds = sessions
            .Select(session => session.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        foreach (var sessionId in uniqueSessionIds)
        {
            var ended = await _db.EndVoiceSessionByIdAsync(sessionId, leftAt);
            if (ended is not null)
            {
                closed++;
            }
        }

        return closed;
    }

    private static Dictionary<ulong, VoicePresence> BuildCurrentVoicePresence(SocketGuild guild)
    {
        var result = new Dictionary<ulong, VoicePresence>();
        foreach (var channel in guild.VoiceChannels)
        {
            foreach (var member in channel.ConnectedUsers.Where(member => !member.IsBot))
            {
                result[member.Id] = new VoicePresence(channel.Id);
            }
        }

        return result;
    }

    private static ulong ToUlong(long value)
        => value <= 0 ? 0UL : unchecked((ulong)value);

    private readonly record struct VoicePresence(ulong ChannelId);
}
