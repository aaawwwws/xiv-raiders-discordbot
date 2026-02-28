namespace RaidDcBot.Models;

public sealed class AdminModerationTimelineEntry
{
    public int EntryId { get; init; }
    public long TargetUserId { get; init; }
    public long ActorUserId { get; init; }
    public string ActorName { get; init; } = string.Empty;
    public string ActionType { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
