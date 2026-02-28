namespace RaidDcBot.Models;

public sealed class AdminMemberNote
{
    public int Id { get; init; }
    public long GuildId { get; init; }
    public long TargetUserId { get; init; }
    public long AuthorUserId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
