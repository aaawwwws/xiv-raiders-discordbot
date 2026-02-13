namespace RaidDcBot.Models;

public sealed class ReactionReceivedTopPost
{
    public long UserId { get; set; }
    public long ChannelId { get; set; }
    public long MessageId { get; set; }
    public int ReactionCount { get; set; }
}
