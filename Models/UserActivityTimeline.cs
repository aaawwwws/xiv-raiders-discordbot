namespace RaidDcBot.Models;

public sealed class UserActivityTimeline
{
    public long UserId { get; set; }
    public DateTimeOffset? FirstMessageAt { get; set; }
    public DateTimeOffset? FirstVoiceAt { get; set; }
    public DateTimeOffset? FirstReactionAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset? LastVoiceAt { get; set; }
    public DateTimeOffset? LastVoiceJoinedAt { get; set; }
    public DateTimeOffset? LastReactionAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
}
