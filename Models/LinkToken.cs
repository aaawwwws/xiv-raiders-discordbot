namespace RaidDcBot.Models;

public sealed class LinkToken
{
    public Guid TokenId { get; init; }
    public long DiscordUserId { get; init; }
    public long? LodestoneId { get; init; }
    public string? LodestoneUrl { get; init; }
    public string Label { get; init; } = "sub";
    public string Token { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
}
