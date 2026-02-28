namespace RaidDcBot.Models;

public sealed class GuildMemberMainCharacter
{
    public long DiscordUserId { get; init; }
    public long LodestoneId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
    public string? DataCenter { get; init; }
    public string LodestoneUrl { get; init; } = string.Empty;
}
