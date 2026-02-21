namespace RaidDcBot.Models;

public sealed class RecruitSlot
{
    public int Id { get; set; }
    public int PostId { get; set; }
    public string Position { get; set; } = string.Empty;
    public string? RequiredJob { get; set; }
    public string? FlexSpec { get; set; }
    public string? FlexJobsSpec { get; set; }
    public string? Job { get; set; }
    public long? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Comment { get; set; }
    public bool IsClosed { get; set; }
    public long? ThreadMessageId { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
