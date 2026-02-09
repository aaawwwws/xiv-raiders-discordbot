namespace RaidDcBot.Models;

public sealed class DiscordUserProfile
{
    public long DiscordUserId { get; init; }
    public string? DisplayName { get; init; }
    public string? DiscordName { get; init; }
    public string? FfxivName { get; init; }
    public string? FfxivWorld { get; init; }
    public string? FfxivDataCenter { get; init; }
    public string? XHandle { get; init; }
    public string? BlueskyHandle { get; init; }
    public string? Bio { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
