namespace RaidDcBot.Models;

public sealed class RecruitPost
{
    public int Id { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long MessageId { get; set; }
    public long? ThreadChannelId { get; set; }
    public long? ThreadMessageId { get; set; }
    public RecruitPostStatus Status { get; set; } = RecruitPostStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public long CreatorUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? DataCenter { get; set; }
    public string? ScheduleText { get; set; }
    public DateTimeOffset? ScheduleAt { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public string? Note { get; set; }
    public bool DisallowDuplicateJobs { get; set; } = true;
    public string? HostFlexSpec { get; set; }
    public string? HostFlexJobsSpec { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
