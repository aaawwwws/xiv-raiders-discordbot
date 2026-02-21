namespace RaidDcBot.Models;

public sealed class InquiryTicket
{
    public int Id { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long RequesterUserId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = InquiryTicketStatus.Open;
    public long? AssignedAdminUserId { get; set; }
    public string? AssignedAdminName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public string? TranscriptText { get; set; }
}
