using System;
using System.Collections.Generic;

namespace RaidDcBot.Models;

public sealed class DiscordOptions
{
    public string Token { get; init; } = string.Empty;
    public ulong GuildId { get; init; }
    public string AdminRoleName { get; init; } = "RaidDC Admin";
    public string MemberRoleName { get; init; } = "メンバー";
    public string ProvisionalRoleName { get; init; } = "仮登録";
    public string RegistrationReviewChannelName { get; init; } = "登録確認";
    public bool AutoCreateDcRoles { get; init; } = true;
    public string LogChannelName { get; init; } = "log";
    public string NotificationChannelName { get; init; } = "notice";
    public bool MentionVoiceNotificationRole { get; init; } = true;
    public string VoiceNotificationRoleName { get; init; } = "通話通知";
    public bool AutoCreateVoiceNotificationRole { get; init; } = true;
    public int MessageCacheSize { get; init; } = 5000;
    public int TokenExpiryMinutes { get; init; } = 30;
    public int LodestoneIssueCooldownSeconds { get; init; } = 15;
    public int LodestoneVerifyMaxAttempts { get; init; } = 8;
    public int LodestoneVerifyWindowMinutes { get; init; } = 10;
    public int LodestoneFetchCooldownMinutes { get; init; } = 30;
    public bool VoiceProfileEmbedEnabled { get; init; } = true;
    public int VoiceProfileMaxBioLength { get; init; } = 250;
    public bool LogIncludeMessageContent { get; init; } = true;
    public string[] LogIgnoreChannelNames { get; init; } = Array.Empty<string>();
    public int CleanupIntervalMinutes { get; init; } = 10;
    public int MessageLogRetentionDays { get; init; } = 0;
    public bool AttachmentArchiveEnabled { get; init; } = true;
    public string AttachmentArchiveRootPath { get; init; } = "data/attachments";
    public int VoiceSessionRetentionDays { get; init; } = 90;
    public int TempVoiceAutoDeleteDelaySeconds { get; init; } = 10;
    public bool RecruitFeatureEnabled { get; init; } = false;
    public string RecruitPostChannelName { get; init; } = string.Empty;
    public string LotteryPostChannelName { get; init; } = "抽選";
    public bool RecruitAutoCloseEnabled { get; init; } = true;
    public bool RecruitDisallowDuplicateJobs { get; init; } = true;
    public int RecruitReminderMinutesBefore { get; init; } = 30;
    public int RecruitScheduleCheckIntervalSeconds { get; init; } = 60;
    public string ActivityRankingChannelName { get; init; } = "ランキング";
    public bool AutoCreateActivityRankingChannel { get; init; } = true;
    public bool DailyActivityRankingEnabled { get; init; } = true;
    public int DailyActivityRankingTopCount { get; init; } = 10;
    public int DailyActivityRankingPostHourJst { get; init; } = 0;
    public int DailyActivityRankingUpdateIntervalMinutes { get; init; } = 30;
    public bool VoiceWeeklyRankingEnabled { get; init; } = true;
    public int VoiceWeeklyRankingTopCount { get; init; } = 10;
    public int VoiceWeeklyRankingRangeDays { get; init; } = 7;
    public int VoiceWeeklyRankingPostDayOfWeekJst { get; init; } = 1;
    public int VoiceWeeklyRankingPostHourJst { get; init; } = 9;
    public bool ActiveRoleEnabled { get; init; } = true;
    public string ActiveRoleName { get; init; } = "アクティブ";
    public bool AutoCreateActiveRole { get; init; } = true;
    public bool InactiveRoleEnabled { get; init; } = true;
    public string InactiveRoleName { get; init; } = "非アクティブ";
    public bool AutoCreateInactiveRole { get; init; } = true;
    public int InactiveRoleDays { get; init; } = 7;
    public int ActivityRoleSyncIntervalMinutes { get; init; } = 30;
    public bool InactiveAutoKickEnabled { get; init; } = true;
    public int InactiveAutoKickDays { get; init; } = 30;
    public string[] ManagementRoleNames { get; init; } = ["管理者", "オフィサー", "宣伝係"];
    public string[] InactiveAutoKickExemptRoleNames { get; init; } = Array.Empty<string>();
    public bool ApplyChannelPermissionOverwritesOnStartup { get; init; }
    public bool ControlChannelMemberCanSend { get; init; }
    public string TempVoiceLobbyName { get; init; } = "VC作成";
    public string TempVoiceCategoryName { get; init; } = string.Empty;
    public string TempVoiceChannelPrefix { get; init; } = "VC-";
    public int TempVoiceUserLimit { get; init; }
    public string ControlChannelName { get; init; } = "bot-control";

    public HashSet<string> BuildManagementRoleNameSet()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = ManagementRoleNames is { Length: > 0 }
            ? ManagementRoleNames
            : InactiveAutoKickExemptRoleNames;

        foreach (var roleName in source ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                names.Add(roleName.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(AdminRoleName))
        {
            names.Add(AdminRoleName.Trim());
        }

        return names;
    }
}
