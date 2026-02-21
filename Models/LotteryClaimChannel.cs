namespace RaidDcBot.Models;

public sealed class LotteryClaimChannel
{
    public int Id { get; set; }
    public int LotteryId { get; set; }
    public long UserId { get; set; }
    public long ChannelId { get; set; }
    public long? ControlMessageId { get; set; }
    public bool IsClosed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
