namespace RaidDcBot.Models;

public sealed class MemberContentRoleSetting
{
    public long GuildId { get; init; }
    public string RoleKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public int ButtonStyle { get; init; }
    public int ButtonRow { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
