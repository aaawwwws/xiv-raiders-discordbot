using System.Collections.Concurrent;

namespace RaidDcBot.Services;

public sealed class LodestoneRateLimitService
{
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _issueAt = new();
    private readonly ConcurrentDictionary<ulong, VerifyWindowState> _verifyStates = new();

    public bool TryIssue(ulong userId, DateTimeOffset now, int cooldownSeconds, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (cooldownSeconds <= 0)
        {
            _issueAt[userId] = now;
            return true;
        }

        var cooldown = TimeSpan.FromSeconds(cooldownSeconds);
        while (true)
        {
            if (!_issueAt.TryGetValue(userId, out var last))
            {
                if (_issueAt.TryAdd(userId, now))
                {
                    return true;
                }

                continue;
            }

            var elapsed = now - last;
            if (elapsed >= cooldown)
            {
                if (_issueAt.TryUpdate(userId, now, last))
                {
                    return true;
                }

                continue;
            }

            retryAfter = cooldown - elapsed;
            return false;
        }
    }

    public bool TryVerify(ulong userId, DateTimeOffset now, int maxAttempts, int windowMinutes, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (maxAttempts <= 0 || windowMinutes <= 0)
        {
            return true;
        }

        var window = TimeSpan.FromMinutes(windowMinutes);
        var state = _verifyStates.GetOrAdd(userId, _ => new VerifyWindowState(now));
        lock (state.Sync)
        {
            if (now - state.WindowStart >= window)
            {
                state.WindowStart = now;
                state.Count = 1;
                return true;
            }

            if (state.Count >= maxAttempts)
            {
                retryAfter = (state.WindowStart + window) - now;
                if (retryAfter < TimeSpan.Zero)
                {
                    retryAfter = TimeSpan.Zero;
                }

                return false;
            }

            state.Count++;
            return true;
        }
    }

    public void Prune(DateTimeOffset now, int verifyWindowMinutes)
    {
        var issueThreshold = now - TimeSpan.FromHours(1);
        foreach (var pair in _issueAt)
        {
            if (pair.Value < issueThreshold)
            {
                _issueAt.TryRemove(pair.Key, out _);
            }
        }

        var verifyWindow = verifyWindowMinutes > 0
            ? TimeSpan.FromMinutes(verifyWindowMinutes)
            : TimeSpan.FromMinutes(10);
        var verifyThreshold = now - (verifyWindow * 2);
        foreach (var pair in _verifyStates)
        {
            var state = pair.Value;
            lock (state.Sync)
            {
                if (state.WindowStart < verifyThreshold)
                {
                    _verifyStates.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private sealed class VerifyWindowState
    {
        public VerifyWindowState(DateTimeOffset start)
        {
            WindowStart = start;
            Count = 1;
        }

        public object Sync { get; } = new();
        public DateTimeOffset WindowStart { get; set; }
        public int Count { get; set; }
    }
}
