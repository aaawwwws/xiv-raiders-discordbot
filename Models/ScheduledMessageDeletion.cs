namespace RaidDcBot.Models;

public sealed class ScheduledMessageDeletion
{
    public long Id { get; init; }
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long MessageId { get; init; }
    public long AuthorUserId { get; init; }
    public DateTimeOffset DeleteAt { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
