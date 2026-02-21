namespace RaidDcBot.Models;

public sealed class LotteryEntry
{
    public int Id { get; set; }
    public int LotteryId { get; set; }
    public long UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
