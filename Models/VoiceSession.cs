namespace RaidDcBot.Models;

public sealed class VoiceSession
{
    public long Id { get; init; }
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public DateTimeOffset JoinedAt { get; init; }
    public DateTimeOffset? LeftAt { get; init; }
    public int? DurationSeconds { get; init; }
}
