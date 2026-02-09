namespace RaidDcBot.Models;

public sealed class GuildStatsPanel
{
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long DataCenterMessageId { get; init; }
    public long WorldMessageId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
