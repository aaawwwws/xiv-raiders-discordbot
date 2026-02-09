namespace RaidDcBot.Models;

public sealed class GuildSettings
{
    public long GuildId { get; init; }
    public long AdminRoleId { get; init; }
    public long MemberRoleId { get; init; }
    public long ProvisionalRoleId { get; init; }
    public long RegistrationChannelId { get; init; }
    public long RegistrationReviewChannelId { get; init; }
    public bool RegistrationApprovalRequired { get; init; }
    public bool InactiveAutoKickEnabled { get; init; }
    public long LogChannelId { get; init; }
    public long ControlChannelId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
