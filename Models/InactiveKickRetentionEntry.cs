namespace RaidDcBot.Models;

public sealed class InactiveKickRetentionEntry
{
    public long GuildId { get; init; }
    public long UserId { get; init; }
    public DateTimeOffset DeleteAfter { get; init; }
    public DateTimeOffset? WarningNotifiedAt { get; init; }
    public DateTimeOffset? FinalWarningNotifiedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
