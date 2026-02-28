namespace RaidDcBot.Models;

public sealed class SsAlbumFavorite
{
    public long Id { get; init; }
    public long GuildId { get; init; }
    public long ViewerUserId { get; init; }
    public long TargetUserId { get; init; }
    public long MessageId { get; init; }
    public long AttachmentId { get; init; }
    public long ChannelId { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? MessageContent { get; init; }
    public string? LocalPath { get; init; }
    public DateTimeOffset PostedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
