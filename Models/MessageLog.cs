namespace RaidDcBot.Models;

public sealed class MessageLog
{
    public long MessageId { get; init; }
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long AuthorId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public string? AuthorDisplayName { get; init; }
    public string Content { get; init; } = string.Empty;
    public int AttachmentCount { get; init; }
    public int EmbedCount { get; init; }
    public string? AttachmentsJson { get; init; }
    public string? EmbedsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}
