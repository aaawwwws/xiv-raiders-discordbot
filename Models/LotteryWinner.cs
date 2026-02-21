namespace RaidDcBot.Models;

public sealed class LotteryWinner
{
    public int Id { get; set; }
    public int LotteryId { get; set; }
    public long UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int WinnerOrder { get; set; }
    public long DrawnByUserId { get; set; }
    public DateTimeOffset DrawnAt { get; set; }
}
