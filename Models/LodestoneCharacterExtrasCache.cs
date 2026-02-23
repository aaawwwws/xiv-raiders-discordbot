namespace RaidDcBot.Models;

public sealed class LodestoneCharacterExtrasCache
{
    public long LodestoneId { get; init; }
    public string? ExtrasJson { get; init; }
    public DateTimeOffset? ExtrasSyncedAt { get; init; }
}
