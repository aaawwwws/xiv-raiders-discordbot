namespace RaidDcBot.Models;

public sealed class ReactionCountByChannel
{
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public int ReactionCount { get; set; }
}
