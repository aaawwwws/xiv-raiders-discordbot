namespace RaidDcBot.Models;

public sealed class MessageCountByChannel
{
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public int MessageCount { get; set; }
}
