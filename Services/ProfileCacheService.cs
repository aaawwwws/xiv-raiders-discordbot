using System.Collections.Concurrent;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class ProfileCacheEntry
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string? World { get; set; }
    public string? DataCenter { get; set; }
    public string? PortraitUrl { get; set; }
    public IReadOnlyList<LodestoneJobLevel> JobLevels { get; set; } = new List<LodestoneJobLevel>();
    public int MountCount { get; set; }
    public int MinionCount { get; set; }
    public int AchievementCount { get; set; }
    public DateTimeOffset CachedAt { get; set; }
    public DateTimeOffset? LastForceUpdatedAt { get; set; }
}

public sealed class ProfileCacheService
{
    private readonly ConcurrentDictionary<ulong, ProfileCacheEntry> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromHours(1);
    private readonly TimeSpan _forceUpdateCooldown = TimeSpan.FromMinutes(5);

    public ProfileCacheEntry? Get(ulong userId)
    {
        if (_cache.TryGetValue(userId, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CachedAt < _ttl)
            {
                return entry;
            }
            // TTL切れ: 削除
            _cache.TryRemove(userId, out _);
        }
        return null;
    }

    public void Set(ulong userId, ProfileCacheEntry entry)
    {
        entry.CachedAt = DateTimeOffset.UtcNow;
        _cache[userId] = entry;
    }

    public bool CanForceUpdate(ulong userId, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (_cache.TryGetValue(userId, out var entry) && entry.LastForceUpdatedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - entry.LastForceUpdatedAt.Value;
            if (elapsed < _forceUpdateCooldown)
            {
                remaining = _forceUpdateCooldown - elapsed;
                return false;
            }
        }
        return true;
    }

    public void MarkForceUpdated(ulong userId)
    {
        if (_cache.TryGetValue(userId, out var entry))
        {
            entry.LastForceUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Invalidate(ulong userId)
    {
        _cache.TryRemove(userId, out _);
    }
}
