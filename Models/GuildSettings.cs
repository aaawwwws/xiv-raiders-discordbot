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
    public bool SleepKickEnabled { get; init; }
    public bool SleepKickAutoDetectEnabled { get; init; }
    public bool SleepKickAdminConfirmationRequired { get; init; }
    public int? InactiveAutoKickUserDataRetentionDays { get; init; }
    public long LogChannelId { get; init; }
    public long ControlChannelId { get; init; }
    public int? RuntimeCleanupIntervalMinutes { get; init; }
    public int? RuntimeRecruitClosedRetentionHours { get; init; }
    public int? RuntimeRecruitCanceledRetentionHours { get; init; }
    public int? RuntimeMessageLogRetentionDays { get; init; }
    public int? RuntimeVoiceSessionRetentionDays { get; init; }
    public int? RuntimeReactionLogRetentionDays { get; init; }
    public DateTimeOffset? RuntimeTuningUpdatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
