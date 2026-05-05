namespace RaidDcBot.Models;

public sealed class GuildStoryStatsPanel
{
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long MessageId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
