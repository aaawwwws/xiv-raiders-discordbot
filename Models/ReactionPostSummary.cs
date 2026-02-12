namespace RaidDcBot.Models;

public sealed class ReactionPostSummary
{
    public long MessageId { get; set; }
    public long ChannelId { get; set; }
    public int ReactionCount { get; set; }
    public int ReactorCount { get; set; }
    public DateTimeOffset LastReactedAt { get; set; }
    public long? AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorDisplayName { get; set; }
    public string? Content { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
