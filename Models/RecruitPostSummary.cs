namespace RaidDcBot.Models;

public sealed class RecruitPostSummary
{
    public int Id { get; set; }
    public long ChannelId { get; set; }
    public long MessageId { get; set; }
    public long CreatorUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? DataCenter { get; set; }
    public string? ScheduleText { get; set; }
    public DateTimeOffset? ScheduleAt { get; set; }
    public RecruitPostStatus Status { get; set; }
    public int FilledCount { get; set; }
    public int SlotCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
