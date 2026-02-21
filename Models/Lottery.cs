namespace RaidDcBot.Models;

public sealed class Lottery
{
    public int Id { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long? MessageId { get; set; }
    public long? ParticipantRoleId { get; set; }
    public DateTimeOffset? ParticipantRoleDeleteAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PrizeList { get; set; }
    public bool AllowDuplicateWinners { get; set; }
    public int WinnerCount { get; set; }
    public string Status { get; set; } = LotteryStatus.Open;
    public DateTimeOffset CloseAt { get; set; }
    public long CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DrawnAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
}
