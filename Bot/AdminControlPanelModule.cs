using System.Text;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RaidDcBot.Bot;

[Group("adminpanel", "ADMINコントロールパネル")]
public sealed class AdminControlPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly DiscordOptions _options;
    private readonly LotteryCreateDraftContextTracker _lotteryCreateDraftTracker;
    private readonly GuildAuditLogService _audit;
    private readonly RuntimeDiagnosticsService _runtimeDiagnostics;
    private readonly RuntimeTuningService _runtimeTuning;
    private readonly VoiceSessionReconciliationService _voiceSessionReconciliation;
    private readonly RecruitmentListService _recruitListService;
    private readonly UserProfileEmbedService _profileEmbeds;
    private readonly ConcurrentDictionary<string, MemberManageDangerActionContext> _memberDangerContexts = new();
    private readonly ConcurrentDictionary<ulong, MemberManageBulkSelectionContext> _memberBulkSelections = new();
    private readonly ConcurrentDictionary<ulong, MemberManageLookbackPreference> _memberManageLookbackByActor = new();
    private const int LotteryCreateSchedulePageSize = 7;
    private const int LotteryCreateCloseMaxDayOffset = 60;
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private static readonly TimeSpan LotteryCreateDraftTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LotteryDefaultCloseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LotteryMaxCloseDuration = TimeSpan.FromDays(LotteryCreateCloseMaxDayOffset + 1);
    private static readonly TimeSpan LotteryParticipantRoleRetention = TimeSpan.FromHours(24);
    private const int LotteryTestCloseSecondsMax = 86400;
    private const string LotteryCreateCloseDefaultCustomId = "lottery_create_close_default";
    private const string LotteryCreateCloseTestCustomId = "lottery_create_close_test";
    private const string LotteryCreateToggleDuplicateWinnersCustomId = "lottery_create_toggle_duplicate_winners";
    private const string LotteryCreateSetDuplicateOnCustomId = "lottery_create_set_duplicate_on";
    private const string LotteryCreateSetDuplicateOffCustomId = "lottery_create_set_duplicate_off";
    private const string LotteryCreateDuplicateBackCustomId = "lottery_create_duplicate_back";
    private const string LotteryCreateSetMentionNoneCustomId = "lottery_create_set_mention_none";
    private const string LotteryCreateSetMentionHereCustomId = "lottery_create_set_mention_here";
    private const string LotteryCreateSetMentionEveryoneCustomId = "lottery_create_set_mention_everyone";
    private const string LotteryCreateMentionBackCustomId = "lottery_create_mention_back";
    private const string LotteryCreateClosePageCustomIdPrefix = "lottery_create_close_page:";
    private const string LotteryCreateCloseDayPickCustomIdPrefix = "lottery_create_close_day_pick:";
    private const string LotteryCreateCloseHourCustomIdPrefix = "lottery_create_close_hour:";
    private const string LotteryCreateCloseHourSelectCustomId = "lottery_create_close_hour_select";
    private const string LotteryCreateCloseMinuteCustomIdPrefix = "lottery_create_close_minute:";
    private const string LotteryCreateCloseMinuteSelectCustomId = "lottery_create_close_minute_select";
    private const string LotteryCreateTestModalCustomId = "lottery_create_test_modal";
    private const string LotteryClaimToggleCustomId = "lottery_claim_toggle";
    private const string LotteryClaimDeleteCustomId = "lottery_claim_delete";
    private const string LotteryAdminConfirmExecuteCustomIdPrefix = "lottery_admin_confirm_exec:";
    private const string LotteryAdminConfirmCancelCustomId = "lottery_admin_confirm_cancel";
    private const string RuntimeTuningEditCustomId = "admin_runtime_tuning_edit";
    private const string RuntimeTuningModalCustomId = "admin_runtime_tuning_modal";
    private const string RuntimeInactiveKickRetentionEditCustomId = "admin_runtime_inactive_kick_retention_edit";
    private const string RuntimeInactiveKickRetentionModalCustomId = "admin_runtime_inactive_kick_retention_modal";
    private const int VoiceHourlyStatsDefaultDays = 30;
    private const int VoiceHourlyStatsMaxDays = 90;
    private const int OpsKpiDefaultDays = 30;
    private const int OpsKpiMinDays = 14;
    private const int OpsKpiMaxDays = 90;
    private const int OpsKpiRecentWindowDays = 7;
    private const int OpsKpiBaselineWindowDays = 28;
    private const int RecruitKpiDefaultDays = 30;
    private const int RecruitKpiMinDays = 7;
    private const int RecruitKpiMaxDays = 90;
    private const int RegistrationPendingListMaxRows = 30;
    private const string MemberManageSelectCustomIdPrefix = "admin_member_manage_select:";
    private const string MemberManageSelectPrevPageCustomIdPrefix = "admin_member_manage_select_prev_page:";
    private const string MemberManageSelectNextPageCustomIdPrefix = "admin_member_manage_select_next_page:";
    private const int MemberManageSelectPageSize = 25;
    private const string MemberManageRefreshCustomIdPrefix = "admin_member_manage_refresh:";
    private const string MemberManageViewProfileCustomIdPrefix = "admin_member_manage_profile:";
    private const string MemberManageToggleVoiceRoleCustomIdPrefix = "admin_member_manage_voice_role:";
    private const string MemberManageToggleVoiceSettingCustomIdPrefix = "admin_member_manage_voice_setting:";
    private const string MemberManageSetMemberRoleCustomIdPrefix = "admin_member_manage_set_member_role:";
    private const string MemberManageSetProvisionalRoleCustomIdPrefix = "admin_member_manage_set_provisional_role:";
    private const string MemberManageTimeoutOpenCustomIdPrefix = "admin_member_manage_timeout_open:";
    private const string MemberManageTimeoutClearCustomIdPrefix = "admin_member_manage_timeout_clear:";
    private const string MemberManageKickCustomIdPrefix = "admin_member_manage_kick:";
    private const string MemberManageBanCustomIdPrefix = "admin_member_manage_ban:";
    private const string MemberManageStateDangerConfirmCustomIdPrefix = "admin_member_manage_state_confirm:";
    private const string MemberManageStateDangerCancelCustomIdPrefix = "admin_member_manage_state_cancel:";
    private const string MemberManageDangerConfirmCustomIdPrefix = "admin_member_manage_danger_confirm:";
    private const string MemberManageDangerCancelCustomIdPrefix = "admin_member_manage_danger_cancel:";
    private const string MemberManageTimeoutModalCustomIdPrefix = "admin_member_manage_timeout_modal:";
    private const string MemberManageSevenDaySummaryCustomIdPrefix = "admin_member_manage_summary_7d:";
    private const string MemberManagePeriodOpenCustomIdPrefix = "admin_member_manage_period_open:";
    private const string MemberManagePeriodModalCustomIdPrefix = "admin_member_manage_period_modal:";
    private const string MemberManageInactiveListCustomId = "admin_member_manage_inactive_list";
    private const string MemberManageModerationHistoryCustomIdPrefix = "admin_member_manage_moderation_history:";
    private const string MemberManageMessageLogCustomIdPrefix = "admin_member_manage_message_log:";
    private const string MemberManageMessageLogPageCustomIdPrefix = "admin_member_manage_message_log_page:";
    private const string MemberManageMessageCsvCustomIdPrefix = "admin_member_manage_message_csv:";
    private const string MemberManageVoiceLogCustomIdPrefix = "admin_member_manage_voice_log:";
    private const string MemberManageVoiceLogPageCustomIdPrefix = "admin_member_manage_voice_log_page:";
    private const string MemberManageVoiceCsvCustomIdPrefix = "admin_member_manage_voice_csv:";
    private const string MemberManageFreezeToggleCustomIdPrefix = "admin_member_manage_freeze_toggle:";
    private const string MemberManageAddNoteCustomIdPrefix = "admin_member_manage_add_note:";
    private const string MemberManageAddNoteModalCustomIdPrefix = "admin_member_manage_add_note_modal:";
    private const string MemberManageOpenBulkCustomId = "admin_member_manage_open_bulk";
    private const string MemberManageBulkUserSelectCustomId = "admin_member_manage_bulk_user_select";
    private const string MemberManageBulkSetMemberCustomId = "admin_member_manage_bulk_set_member";
    private const string MemberManageBulkSetProvisionalCustomId = "admin_member_manage_bulk_set_provisional";
    private const string MemberManageBulkFreezeOnCustomId = "admin_member_manage_bulk_freeze_on";
    private const string MemberManageBulkFreezeOffCustomId = "admin_member_manage_bulk_freeze_off";
    private const string MemberManageBulkVoiceNotifyOffCustomId = "admin_member_manage_bulk_voice_notify_off";
    private const string MemberManageBulkTimeoutClearCustomId = "admin_member_manage_bulk_timeout_clear";
    private const string MemberManageBulkDangerConfirmCustomIdPrefix = "admin_member_manage_bulk_danger_confirm:";
    private const string MemberManageBulkDangerCancelCustomId = "admin_member_manage_bulk_danger_cancel";
    private const string MemberManageDangerReasonModalCustomIdPrefix = "admin_member_manage_danger_reason_modal:";
    private const string ModerationTimelinePageCustomIdPrefix = "admin_moderation_timeline_page:";
    private const string FreezeRoleName = "凍結";
    private const int MemberManageTimeoutMaxMinutes = 28 * 24 * 60;
    private const int MemberManageLogLookbackDays = 30;
    private const int MemberManageLookbackDaysMin = 1;
    private const int MemberManageLookbackDaysMax = 90;
    private const int MemberManageInactiveListMaxRows = 25;
    private const int MemberManageLogPreviewRows = 30;
    private const int MemberManageCsvMaxRows = 5000;
    private const int MemberManageMessageAlertThreshold24h = 80;
    private const int MemberManageVoiceSessionAlertThreshold24h = 12;
    private const int MemberManageVoiceDurationAlertMinutes24h = 360;
    private const int ModerationTimelinePageSize = 20;
    private static readonly TimeSpan MemberManageContextTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MemberManageLookbackPreferenceTimeout = TimeSpan.FromHours(12);
    private static readonly HttpClient VoiceChartImageHttpClient = CreateVoiceChartImageHttpClient();
    private static readonly FontCollection VoiceChartFontFileCollection = new();
    private static readonly IReadOnlyList<FontFamily> VoiceChartFileFamilies = LoadVoiceChartFileFamilies();

    public AdminControlPanelModule(
        Db db,
        IOptions<DiscordOptions> options,
        LotteryCreateDraftContextTracker lotteryCreateDraftTracker,
        GuildAuditLogService audit,
        RuntimeDiagnosticsService runtimeDiagnostics,
        RuntimeTuningService runtimeTuning,
        VoiceSessionReconciliationService voiceSessionReconciliation,
        RecruitmentListService recruitListService,
        UserProfileEmbedService profileEmbeds)
    {
        _db = db;
        _options = options.Value;
        _lotteryCreateDraftTracker = lotteryCreateDraftTracker;
        _audit = audit;
        _runtimeDiagnostics = runtimeDiagnostics;
        _runtimeTuning = runtimeTuning;
        _voiceSessionReconciliation = voiceSessionReconciliation;
        _recruitListService = recruitListService;
        _profileEmbeds = profileEmbeds;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにADMINコントロールパネルを設置します")]
    public async Task SetupAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var existing = await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = existing is null || existing.MessageId == 0 ? null : ToUlong(existing.MessageId);
        var message = await EnsureAdminControlPanelMessageAsync(channel, existingMessageId);
        await _db.UpsertGuildAdminControlPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        await FollowupAsync($"このチャンネルにADMINコントロールパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ExportMessageCsvCustomId, ignoreGroupNames: true)]
    public async Task ExportMessageCsvAllAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ExportMessageCsvAsync();
    }

    [RequireBotAdmin]
    [SlashCommand("inactive_preview", "次回の自動キック判定で対象になる候補を表示します")]
    public async Task PreviewInactiveKickByCommandAsync([Summary("top", "各カテゴリの表示件数(1-50)")] int top = 20)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await PreviewInactiveKickAsync(Math.Clamp(top, 1, 50));
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.PreviewInactiveKickCustomId, ignoreGroupNames: true)]
    public async Task PreviewInactiveKickByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await PreviewInactiveKickAsync(20);
    }

    [RequireBotAdmin]
    [SlashCommand("log_search", "メッセージ/VCログを期間・ユーザーで検索します")]
    public async Task SearchLogsAsync(
        [Summary("days", "過去何日を対象にするか(1-30)")] int days = 7,
        [Summary("user", "対象ユーザー(未指定で全員)")] IUser? user = null,
        [Summary("limit", "表示件数(1-30)")] int limit = 10)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await SearchLogsInternalAsync(Math.Clamp(days, 1, 30), user?.Id, Math.Clamp(limit, 1, 30));
    }

    [RequireBotAdmin]
    [SlashCommand("ops_kpi", "運用KPI(定着/コホート/曜日時間/急落/依存度)を表示します")]
    public async Task ShowOpsKpiByCommandAsync(
        [Summary("days", "集計期間(日数:14-90)")] int days = OpsKpiDefaultDays)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowOpsKpiAsync(Math.Clamp(days, OpsKpiMinDays, OpsKpiMaxDays));
    }

    [RequireBotAdmin]
    [SlashCommand("recruit_kpi", "募集KPI(作成/充足/中断/平均充足時間)を表示します")]
    public async Task ShowRecruitKpiByCommandAsync(
        [Summary("days", "集計期間(日数:7-90)")] int days = RecruitKpiDefaultDays)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowRecruitKpiAsync(Math.Clamp(days, RecruitKpiMinDays, RecruitKpiMaxDays));
    }

    [RequireBotAdmin]
    [SlashCommand("health", "Botの権限・主要チャンネル・DB接続状態を診断します")]
    public async Task HealthAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowHealthAsync();
    }

    [RequireBotAdmin]
    [SlashCommand("perm_diag", "チャンネル権限を簡易診断します")]
    public async Task PermissionDiagnosticsAsync(
        [Summary("channel", "対象チャンネル（省略時: 現在チャンネル）")] IChannel? channel = null,
        [Summary("user", "確認したいユーザー（省略時: Botのみ）")] SocketGuildUser? user = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if ((channel ?? Context.Channel) is not IGuildChannel guildChannel)
        {
            await RespondAsync("このチャンネルは診断対象にできません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var embed = new EmbedBuilder()
            .WithTitle("権限診断")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象チャンネル", $"<#{guildChannel.Id}> ({guildChannel.Name})", false)
            .AddField("Bot", BuildChannelPermissionDiagnostic(Context.Guild.CurrentUser, guildChannel), false);

        if (user is not null)
        {
            embed.AddField($"ユーザー ({user.DisplayName ?? user.Username})", BuildChannelPermissionDiagnostic(user, guildChannel), false);
        }

        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpenMonitoringCategoryCustomId, ignoreGroupNames: true)]
    public async Task OpenMonitoringCategoryMenuAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "監視/統計メニュー",
            components: AdminControlPanel.BuildMonitoringCategoryComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpenMemberOpsCategoryCustomId, ignoreGroupNames: true)]
    public async Task OpenMemberOpsCategoryMenuAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "メンバー運用メニュー",
            components: AdminControlPanel.BuildMemberOpsCategoryComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpenSettingsCategoryCustomId, ignoreGroupNames: true)]
    public async Task OpenSettingsCategoryMenuAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        await RespondAsync(
            "設定/保守メニュー",
            components: AdminControlPanel.BuildSettingsCategoryComponents(approvalRequired, inactiveAutoKickEnabled),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpenMaintenanceCategoryCustomId, ignoreGroupNames: true)]
    public async Task OpenMaintenanceCategoryMenuAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "補助/エクスポートメニュー",
            components: AdminControlPanel.BuildMaintenanceCategoryComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpenSpecialCategoryCustomId, ignoreGroupNames: true)]
    public async Task OpenSpecialCategoryMenuAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "管理機能メニュー",
            components: AdminControlPanel.BuildSpecialCategoryComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.HealthCheckCustomId, ignoreGroupNames: true)]
    public async Task HealthByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowHealthAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.VoiceSessionIntegrityCustomId, ignoreGroupNames: true)]
    public async Task RepairVoiceSessionIntegrityByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowVoiceSessionIntegrityAsync(repair: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.VoiceHourlyStatsCustomId, ignoreGroupNames: true)]
    public async Task ShowVoiceHourlyStatsByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowVoiceHourlyStatsAsync(VoiceHourlyStatsDefaultDays);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpsKpiCustomId, ignoreGroupNames: true)]
    public async Task ShowOpsKpiByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowOpsKpiAsync(OpsKpiDefaultDays);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.OpsKpiCohortHourlyCustomId, ignoreGroupNames: true)]
    public async Task ShowOpsKpiCohortHourlyByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowOpsKpiAsync(OpsKpiDefaultDays);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.RecruitKpiCustomId, ignoreGroupNames: true)]
    public async Task ShowRecruitKpiByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowRecruitKpiAsync(RecruitKpiDefaultDays);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ModerationTimelineCustomId, ignoreGroupNames: true)]
    public async Task ShowModerationTimelineByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var payload = await BuildModerationTimelinePayloadAsync(Context.Guild, page: 0);
        await FollowupAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(ModerationTimelinePageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowModerationTimelinePageAsync(string pageRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (!int.TryParse(pageRaw, out var page))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var payload = await BuildModerationTimelinePayloadAsync(Context.Guild, page);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = payload.Embed;
            props.Components = payload.Components;
        });
    }

    [RequireBotAdmin]
    [SlashCommand("member_manage", "メンバー管理パネルを表示します")]
    public async Task ShowMemberManageByCommandAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            embed: BuildMemberManageSelectEmbed(),
            components: BuildMemberManageSelectComponents(Context.Guild, page: 0),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.MemberManageCustomId, ignoreGroupNames: true)]
    public async Task ShowMemberManageByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            embed: BuildMemberManageSelectEmbed(),
            components: BuildMemberManageSelectComponents(Context.Guild, page: 0),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSelectCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleMemberManageSelectAsync(string pageRaw, string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (values.Length == 0 || !ulong.TryParse(values[0], out var targetUserId))
        {
            await RespondAsync("ユーザー選択の情報が不正です。", ephemeral: true);
            return;
        }

        var payload = await BuildMemberManageTargetPayloadAsync(Context.Guild, targetUserId, notice: null);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = payload.Embed;
            props.Components = payload.Components;
        });
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSelectPrevPageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageSelectPrevPageAsync(string payloadRaw)
        => await MoveMemberManageSelectPageAsync(payloadRaw, direction: -1);

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSelectNextPageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageSelectNextPageAsync(string payloadRaw)
        => await MoveMemberManageSelectPageAsync(payloadRaw, direction: 1);

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageRefreshCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task RefreshMemberManageTargetAsync(string userIdRaw)
    {
        await UpdateMemberManageTargetPanelAsync(userIdRaw, notice: null);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManagePeriodOpenCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenMemberManagePeriodModalAsync(string targetUserIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var currentDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var modal = new ModalBuilder()
            .WithTitle("参照期間設定")
            .WithCustomId($"{MemberManagePeriodModalCustomIdPrefix}{targetUserIdRaw}")
            .AddTextInput(
                $"参照期間（日 / {MemberManageLookbackDaysMin}-{MemberManageLookbackDaysMax}）",
                "admin_member_manage_period_days",
                value: currentDays.ToString(),
                maxLength: 3);
        await RespondWithModalAsync(modal.Build());
    }

    [RequireBotAdmin]
    [ModalInteraction(MemberManagePeriodModalCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleMemberManagePeriodModalAsync(string targetUserIdRaw, MemberManagePeriodModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.Days?.Trim(), out var days) ||
            days < MemberManageLookbackDaysMin ||
            days > MemberManageLookbackDaysMax)
        {
            await RespondAsync(
                $"期間は {MemberManageLookbackDaysMin}-{MemberManageLookbackDaysMax} 日で入力してください。",
                ephemeral: true);
            return;
        }

        SetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id, days);

        if (TryParseUserId(targetUserIdRaw, out var targetUserId) && targetUserId != 0)
        {
            var payload = await BuildMemberManageTargetPayloadAsync(
                Context.Guild,
                targetUserId,
                $"参照期間を {days}日 に変更しました。");
            await RespondAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
            return;
        }

        await RespondAsync($"参照期間を {days}日 に変更しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageInactiveListCustomId, ignoreGroupNames: true)]
    public async Task ShowMemberManageInactiveListByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowMemberManageInactiveListAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageViewProfileCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageTargetProfileAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        var embed = await _profileEmbeds.BuildProfileEmbedAsync(target, Context.Guild, "メンバープロフィール", Color.Blue);
        await RespondAsync(embed: embed, ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageToggleVoiceRoleCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleMemberManageVoiceRoleAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        IRole? role;
        try
        {
            role = await ResolveOrCreateVoiceNotificationRoleAsync(Context.Guild);
        }
        catch
        {
            await RespondAsync("通話通知ロールの解決に失敗しました。Bot権限を確認してください。", ephemeral: true);
            return;
        }

        if (role is null)
        {
            await RespondAsync("通話通知ロールが見つかりません。", ephemeral: true);
            return;
        }

        if (!TryValidateRoleOperation(Context.Guild, target, role, out var roleReason))
        {
            await RespondAsync($"ロール変更できません: {roleReason}", ephemeral: true);
            return;
        }

        try
        {
            string notice;
            if (target.Roles.Any(existing => existing.Id == role.Id))
            {
                await target.RemoveRoleAsync(role);
                notice = $"{target.Mention} から `{role.Name}` を外しました。";
            }
            else
            {
                await target.AddRoleAsync(role);
                notice = $"{target.Mention} に `{role.Name}` を付与しました。";
            }

            await UpdateMemberManageTargetPanelAsync(userIdRaw, notice);
            await SendMemberManageAuditAsync("メンバー管理: VC通知ロール切替", target.Id, notice);
        }
        catch
        {
            await RespondAsync("ロール変更に失敗しました。Botの権限・ロール階層を確認してください。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageToggleVoiceSettingCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleMemberManageVoiceSettingAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        try
        {
            var enabled = await _db.ToggleVoiceNotificationEnabledAsync(targetUserId);
            var notice = enabled
                ? $"<@{targetUserId}> の「自分のVC通知表示」を ON にしました。"
                : $"<@{targetUserId}> の「自分のVC通知表示」を OFF にしました。";

            await UpdateMemberManageTargetPanelAsync(userIdRaw, notice);
            await SendMemberManageAuditAsync("メンバー管理: VC通知表示切替", targetUserId, notice);
        }
        catch
        {
            await RespondAsync("VC通知表示設定の更新に失敗しました。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSetMemberRoleCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SetMemberRoleByMemberManageAsync(string userIdRaw)
        => await ShowMemberRegistrationStateDangerConfirmAsync(userIdRaw, setMemberRole: true);

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSetProvisionalRoleCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SetProvisionalRoleByMemberManageAsync(string userIdRaw)
        => await ShowMemberRegistrationStateDangerConfirmAsync(userIdRaw, setMemberRole: false);

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageStateDangerCancelCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task CancelMemberRegistrationStateDangerAsync(string userIdRaw)
    {
        await UpdateMemberManageTargetPanelAsync(userIdRaw, "登録状態変更をキャンセルしました。");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageStateDangerConfirmCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExecuteMemberRegistrationStateDangerAsync(string payloadRaw)
    {
        if (!TryParseMemberManageStateDangerPayload(payloadRaw, out var setMemberRole, out var targetUserId))
        {
            await RespondAsync("確認情報が不正です。", ephemeral: true);
            return;
        }

        await SetMemberRegistrationStateAsync(targetUserId.ToString(), setMemberRole);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageTimeoutOpenCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenMemberManageTimeoutModalAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        if (Context.Guild.GetUser(targetUserId) is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<MemberManageTimeoutModal>($"{MemberManageTimeoutModalCustomIdPrefix}{targetUserId}");
    }

    [RequireBotAdmin]
    [ModalInteraction(MemberManageTimeoutModalCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleMemberManageTimeoutModalAsync(string userIdRaw, MemberManageTimeoutModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.TimeoutMinutes, out var timeoutMinutes) ||
            timeoutMinutes < 1 ||
            timeoutMinutes > MemberManageTimeoutMaxMinutes)
        {
            await RespondAsync($"タイムアウト分は 1-{MemberManageTimeoutMaxMinutes} の範囲で入力してください。", ephemeral: true);
            return;
        }

        var timeoutReason = modal.Reason?.Trim();
        if (timeoutMinutes >= 60 && string.IsNullOrWhiteSpace(timeoutReason))
        {
            await RespondAsync("60分以上のタイムアウトは理由を必須で入力してください。", ephemeral: true);
            return;
        }

        if (!TryValidateTimeoutOperation(Context.Guild, target, out var reason))
        {
            await RespondAsync($"タイムアウトを設定できません: {reason}", ephemeral: true);
            return;
        }

        try
        {
            var until = DateTimeOffset.UtcNow.AddMinutes(timeoutMinutes);
            await target.ModifyAsync(props => props.TimedOutUntil = until);
            var note = string.IsNullOrWhiteSpace(timeoutReason)
                ? null
                : TruncateSingleLine(timeoutReason, 120);
            var notice = note is null
                ? $"{target.Mention} を `{timeoutMinutes}` 分タイムアウトしました。"
                : $"{target.Mention} を `{timeoutMinutes}` 分タイムアウトしました。理由: {note}";
            _ = await _db.AddAdminMemberModerationActionAsync(
                Context.Guild.Id,
                target.Id,
                Context.User.Id,
                Context.User.ToString(),
                "timeout_set",
                note,
                $"{timeoutMinutes}分 / 解除予定: {until.ToOffset(JapanTimeOffset):yyyy/MM/dd HH:mm} JST");
            var payload = await BuildMemberManageTargetPayloadAsync(Context.Guild, target.Id, notice);
            await RespondAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
            await SendMemberManageAuditAsync(
                "メンバー管理: タイムアウト設定",
                target.Id,
                $"{target.Mention} を {timeoutMinutes}分タイムアウト\n終了予定: <t:{until.ToUnixTimeSeconds()}:F>\n理由: {(note ?? "(なし)")}");
        }
        catch
        {
            await RespondAsync("タイムアウト設定に失敗しました。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageTimeoutClearCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ClearMemberManageTimeoutAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        if (!TryValidateTimeoutOperation(Context.Guild, target, out var reason))
        {
            await RespondAsync($"タイムアウト解除できません: {reason}", ephemeral: true);
            return;
        }

        try
        {
            await target.ModifyAsync(props => props.TimedOutUntil = null);
            _ = await _db.AddAdminMemberModerationActionAsync(
                Context.Guild.Id,
                target.Id,
                Context.User.Id,
                Context.User.ToString(),
                "timeout_clear",
                detail: "手動解除");
            await UpdateMemberManageTargetPanelAsync(userIdRaw, $"{target.Mention} のタイムアウトを解除しました。");
            await SendMemberManageAuditAsync(
                "メンバー管理: タイムアウト解除",
                target.Id,
                $"{target.Mention} のタイムアウトを解除");
        }
        catch
        {
            await RespondAsync("タイムアウト解除に失敗しました。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageKickCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageKickConfirmAsync(string userIdRaw)
        => await OpenMemberManageDangerReasonModalAsync("kick", userIdRaw);

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBanCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageBanConfirmAsync(string userIdRaw)
        => await OpenMemberManageDangerReasonModalAsync("ban", userIdRaw);

    [RequireBotAdmin]
    [ModalInteraction(MemberManageDangerReasonModalCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleMemberManageDangerReasonModalAsync(string payloadRaw, MemberManageDangerReasonModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseMemberManageDangerPayload(payloadRaw, out var action, out var targetUserId))
        {
            await RespondAsync("対象情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        if (action == "kick" && !TryValidateKickOperation(Context.Guild, target, out var kickReason))
        {
            await RespondAsync($"キックを実行できません: {kickReason}", ephemeral: true);
            return;
        }

        if (action == "ban" && !TryValidateBanOperation(Context.Guild, target, out var banReason))
        {
            await RespondAsync($"BANを実行できません: {banReason}", ephemeral: true);
            return;
        }

        PruneMemberManageDangerContexts();
        var contextId = Guid.NewGuid().ToString("N");
        var actionReason = string.IsNullOrWhiteSpace(modal.Reason)
            ? "(なし)"
            : TruncateSingleLine(modal.Reason.Trim(), 120);
        _memberDangerContexts[contextId] = new MemberManageDangerActionContext(
            Context.Guild.Id,
            Context.User.Id,
            targetUserId,
            action,
            actionReason,
            DateTimeOffset.UtcNow);

        var actionLabel = action == "ban" ? "BAN" : "キック";
        var confirmEmbed = new EmbedBuilder()
            .WithTitle($"メンバー管理: {actionLabel}確認")
            .WithColor(Color.Red)
            .WithCurrentTimestamp()
            .WithDescription($"{target.Mention} を `{actionLabel}` します。\n理由: {actionReason}\nこの操作は取り消せません。")
            .Build();
        var components = new ComponentBuilder()
            .WithButton(
                $"はい、{actionLabel}します",
                $"{MemberManageDangerConfirmCustomIdPrefix}{contextId}",
                ButtonStyle.Danger,
                row: 0)
            .WithButton("キャンセル", $"{MemberManageDangerCancelCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 0)
            .Build();
        await RespondAsync(embed: confirmEmbed, components: components, ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageDangerCancelCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task CancelMemberManageDangerAsync(string userIdRaw)
    {
        await UpdateMemberManageTargetPanelAsync(userIdRaw, "破壊的操作をキャンセルしました。");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageDangerConfirmCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExecuteMemberManageDangerAsync(string contextId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        PruneMemberManageDangerContexts();
        if (!_memberDangerContexts.TryRemove(contextId, out var context))
        {
            await RespondAsync("確認情報が不正です。", ephemeral: true);
            return;
        }

        if (context.GuildId != Context.Guild.Id || context.ActorUserId != Context.User.Id)
        {
            await RespondAsync("この確認操作は実行できません。", ephemeral: true);
            return;
        }

        var action = context.Action;
        var targetUserId = context.TargetUserId;
        var actionReason = context.Reason;

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            var notFoundEmbed = BuildMemberManageResultEmbed("対象ユーザーが見つかりません。");
            await component.UpdateAsync(props =>
            {
                props.Content = null;
                props.Embed = notFoundEmbed;
                props.Components = BuildMemberManageSelectComponents(Context.Guild, page: 0);
            });
            return;
        }

        try
        {
            if (action == "kick")
            {
                if (!TryValidateKickOperation(Context.Guild, target, out var kickReason))
                {
                    await RespondAsync($"キックを実行できません: {kickReason}", ephemeral: true);
                    return;
                }

                await target.KickAsync($"admin_member_manage by {Context.User.Username} ({Context.User.Id}) / {actionReason}");
                _ = await _db.AddAdminMemberModerationActionAsync(
                    Context.Guild.Id,
                    target.Id,
                    Context.User.Id,
                    Context.User.ToString(),
                    "kick",
                    actionReason);
                var resultEmbed = BuildMemberManageResultEmbed($"{target.Username} をキックしました。");
                await component.UpdateAsync(props =>
                {
                    props.Content = null;
                    props.Embed = resultEmbed;
                    props.Components = BuildMemberManageSelectComponents(Context.Guild, page: 0);
                });
                await SendMemberManageAuditAsync(
                    "メンバー管理: キック",
                    target.Id,
                    $"{target.Mention} をキック\n理由: {actionReason}");
                return;
            }

            if (action == "ban")
            {
                if (!TryValidateBanOperation(Context.Guild, target, out var banReason))
                {
                    await RespondAsync($"BANを実行できません: {banReason}", ephemeral: true);
                    return;
                }

                await Context.Guild.AddBanAsync(target.Id, pruneDays: 0, reason: $"admin_member_manage by {Context.User.Username} ({Context.User.Id}) / {actionReason}");
                _ = await _db.AddAdminMemberModerationActionAsync(
                    Context.Guild.Id,
                    target.Id,
                    Context.User.Id,
                    Context.User.ToString(),
                    "ban",
                    actionReason);
                var resultEmbed = BuildMemberManageResultEmbed($"{target.Username} をBANしました。");
                await component.UpdateAsync(props =>
                {
                    props.Content = null;
                    props.Embed = resultEmbed;
                    props.Components = BuildMemberManageSelectComponents(Context.Guild, page: 0);
                });
                await SendMemberManageAuditAsync(
                    "メンバー管理: BAN",
                    target.Id,
                    $"{target.Mention} をBAN\n理由: {actionReason}");
                return;
            }

            await RespondAsync("操作種別が不正です。", ephemeral: true);
        }
        catch
        {
            await RespondAsync("破壊的操作の実行に失敗しました。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageSevenDaySummaryCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageSevenDaySummaryAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);
        var messageCountTask = _db.CountMessagesByUserAsync(
            Context.Guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive,
            includeDeleted: false);
        var reactionCountTask = _db.CountReactionsByUserAsync(
            Context.Guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive);
        var voiceSummaryTask = _db.SummarizeVoiceByUserAsync(
            Context.Guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive);
        var messagePodiumTask = _db.GetActivityRankingMetricPodiumCountsByUserAsync(
            Context.Guild.Id,
            targetUserId,
            VoiceStatsService.RankingMetricMessageCount);
        var voicePodiumTask = _db.GetActivityRankingMetricPodiumCountsByUserAsync(
            Context.Guild.Id,
            targetUserId,
            VoiceStatsService.RankingMetricVoiceDuration);
        await Task.WhenAll(messageCountTask, reactionCountTask, voiceSummaryTask, messagePodiumTask, voicePodiumTask);

        var messageCount = messageCountTask.Result;
        var reactionCount = reactionCountTask.Result;
        var (voiceSessionCount, voiceDurationSeconds) = voiceSummaryTask.Result;
        var voiceDurationMinutes = (int)Math.Round(Math.Max(0L, voiceDurationSeconds) / 60d, MidpointRounding.AwayFromZero);
        var averageSessionMinutes = voiceSessionCount <= 0
            ? 0
            : (int)Math.Round((voiceDurationSeconds / (double)voiceSessionCount) / 60d, MidpointRounding.AwayFromZero);

        var messagePodium = messagePodiumTask.Result;
        var voicePodium = voicePodiumTask.Result;

        var embed = new EmbedBuilder()
            .WithTitle("メンバー期間サマリ")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象", $"<@{targetUserId}>", true)
            .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
            .AddField("発言数", $"{messageCount}件", true)
            .AddField("リアクション", $"{reactionCount}件", true)
            .AddField("VC入退出", $"{voiceSessionCount}回", true)
            .AddField("VC滞在", $"{voiceDurationMinutes}分", true)
            .AddField("VC平均滞在", voiceSessionCount <= 0 ? "0分" : $"{averageSessionMinutes}分/回", true)
            .AddField("発言数TOP獲得(累計)", BuildPodiumCountLine(messagePodium), true)
            .AddField("VC滞在TOP獲得(累計)", BuildPodiumCountLine(voicePodium), true)
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: 期間サマリ参照",
            targetUserId,
            $"対象: <@{targetUserId}> / 期間: {lookbackDays}日 / 発言: {messageCount}件 / リアクション: {reactionCount}件 / VC入退出: {voiceSessionCount}回 / VC滞在: {voiceDurationMinutes}分");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageModerationHistoryCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageModerationHistoryAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var rows = await _db.ListAdminMemberModerationActionsAsync(
            Context.Guild.Id,
            targetUserId,
            maxRows: 20);

        var embed = new EmbedBuilder()
            .WithTitle("メンバー処分履歴")
            .WithColor(Color.DarkRed)
            .WithCurrentTimestamp()
            .AddField("対象", $"<@{targetUserId}>", true)
            .AddField("取得件数", $"{rows.Count}件", true)
            .AddField("最新20件", BuildMemberModerationHistorySummary(rows), false)
            .Build();
        await FollowupAsync(embed: embed, ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: 処分履歴参照",
            targetUserId,
            $"対象: <@{targetUserId}> / 件数: {rows.Count}");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageMessageLogCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageMessageLogsAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var payload = await BuildMemberManageMessageLogPayloadAsync(Context.Guild, targetUserId, page: 0, lookbackDays);
        await FollowupAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: 発言ログ参照",
            targetUserId,
            $"対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageMessageLogPageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageMessageLogsPageAsync(string payloadRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (!TryParseMemberManageLogPagePayload(payloadRaw, out var targetUserId, out var page))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var nextPayload = await BuildMemberManageMessageLogPayloadAsync(Context.Guild, targetUserId, page, lookbackDays);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = nextPayload.Embed;
            props.Components = nextPayload.Components;
        });
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageMessageCsvCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExportMemberManageMessageCsvAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);
        var rows = await _db.ListMessageLogsForUserExportAsync(
            Context.Guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive,
            maxRows: MemberManageCsvMaxRows,
            includeDeleted: true);
        if (rows.Count == 0)
        {
            await FollowupAsync("対象ユーザーの発言ログがありません。", ephemeral: true);
            return;
        }

        var csv = BuildMessageLogCsv(rows);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes, writable: false);
        var fileName = $"member_messages_{Context.Guild.Id}_{targetUserId}_{toUtcExclusive:yyyyMMddHHmm}.csv";
        await FollowupWithFileAsync(
            stream,
            fileName,
            text: $"発言CSVを出力しました。対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日 / 件数: {rows.Count}",
            ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: 発言CSV出力",
            targetUserId,
            $"対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日 / 件数: {rows.Count}");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageVoiceLogCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageVoiceLogsAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var payload = await BuildMemberManageVoiceLogPayloadAsync(Context.Guild, targetUserId, page: 0, lookbackDays);
        await FollowupAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: VC入退出ログ参照",
            targetUserId,
            $"対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageVoiceLogPageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ShowMemberManageVoiceLogsPageAsync(string payloadRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (!TryParseMemberManageLogPagePayload(payloadRaw, out var targetUserId, out var page))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var nextPayload = await BuildMemberManageVoiceLogPayloadAsync(Context.Guild, targetUserId, page, lookbackDays);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = nextPayload.Embed;
            props.Components = nextPayload.Components;
        });
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageVoiceCsvCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExportMemberManageVoiceCsvAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var lookbackDays = GetMemberManageLookbackDays(Context.Guild.Id, Context.User.Id);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);
        var rows = await _db.ListVoiceSessionsForUserExportAsync(
            Context.Guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive,
            maxRows: MemberManageCsvMaxRows);
        if (rows.Count == 0)
        {
            await FollowupAsync("対象ユーザーのVCログがありません。", ephemeral: true);
            return;
        }

        var csv = BuildVoiceSessionCsv(rows, toUtcExclusive);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes, writable: false);
        var fileName = $"member_voice_{Context.Guild.Id}_{targetUserId}_{toUtcExclusive:yyyyMMddHHmm}.csv";
        await FollowupWithFileAsync(
            stream,
            fileName,
            text: $"VCログCSVを出力しました。対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日 / 件数: {rows.Count}",
            ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: VCログCSV出力",
            targetUserId,
            $"対象: <@{targetUserId}> / 期間: 直近{lookbackDays}日 / 件数: {rows.Count}");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageFreezeToggleCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleMemberManageFreezeAsync(string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        try
        {
            var freezeRole = await EnsureFreezeRoleAsync(Context.Guild);
            if (freezeRole is null)
            {
                await RespondAsync("凍結ロールを解決できませんでした。", ephemeral: true);
                return;
            }

            if (!TryValidateRoleOperation(Context.Guild, target, freezeRole, out var roleReason))
            {
                await RespondAsync($"凍結状態を変更できません: {roleReason}", ephemeral: true);
                return;
            }

            string notice;
            string actionType;
            if (target.Roles.Any(role => role.Id == freezeRole.Id))
            {
                await target.RemoveRoleAsync(freezeRole);
                notice = $"{target.Mention} の凍結を解除しました。";
                actionType = "freeze_off";
            }
            else
            {
                await target.AddRoleAsync(freezeRole);
                notice = $"{target.Mention} を凍結しました（投稿/VC参加を制限）。";
                actionType = "freeze_on";
            }

            _ = await _db.AddAdminMemberModerationActionAsync(
                Context.Guild.Id,
                target.Id,
                Context.User.Id,
                Context.User.ToString(),
                actionType);
            await UpdateMemberManageTargetPanelAsync(userIdRaw, notice);
            await SendMemberManageAuditAsync("メンバー管理: 凍結切替", targetUserId, notice);
        }
        catch
        {
            await RespondAsync("凍結状態の更新に失敗しました。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageAddNoteCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenMemberManageNoteModalAsync(string userIdRaw)
    {
        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<MemberManageNoteModal>($"{MemberManageAddNoteModalCustomIdPrefix}{targetUserId}");
    }

    [RequireBotAdmin]
    [ModalInteraction(MemberManageAddNoteModalCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleMemberManageNoteModalAsync(string userIdRaw, MemberManageNoteModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(modal.Note))
        {
            await RespondAsync("コメントを入力してください。", ephemeral: true);
            return;
        }

        var note = modal.Note.Trim();
        await _db.AddAdminMemberNoteAsync(
            Context.Guild.Id,
            targetUserId,
            Context.User.Id,
            Context.User.ToString(),
            note);

        var payload = await BuildMemberManageTargetPayloadAsync(
            Context.Guild,
            targetUserId,
            $"管理者メモを追加しました。\n{TruncateSingleLine(note, 80)}");
        await RespondAsync(embed: payload.Embed, components: payload.Components, ephemeral: true);
        await SendMemberManageAuditAsync("メンバー管理: 管理者メモ追加", targetUserId, $"対象: <@{targetUserId}> / 内容: {TruncateSingleLine(note, 120)}");
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageOpenBulkCustomId, ignoreGroupNames: true)]
    public async Task OpenMemberManageBulkPanelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            embed: BuildMemberManageBulkEmbed(null, "対象を選択してください。"),
            components: BuildMemberManageBulkComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkUserSelectCustomId, ignoreGroupNames: true)]
    public async Task HandleMemberManageBulkSelectAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        var ids = values
            .Select(raw => ulong.TryParse(raw, out var id) ? id : 0UL)
            .Where(id => id != 0)
            .Distinct()
            .Take(25)
            .ToArray();
        if (ids.Length == 0)
        {
            await RespondAsync("一括対象を1人以上選択してください。", ephemeral: true);
            return;
        }

        _memberBulkSelections[Context.User.Id] = new MemberManageBulkSelectionContext(Context.Guild.Id, ids, DateTimeOffset.UtcNow);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = BuildMemberManageBulkEmbed(ids, $"対象を {ids.Length} 人保存しました。");
            props.Components = BuildMemberManageBulkComponents();
        });
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkSetMemberCustomId, ignoreGroupNames: true)]
    public async Task BulkSetMemberRoleAsync() => await ShowMemberManageBulkDangerConfirmAsync("set_member", "登録済みにする");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkSetProvisionalCustomId, ignoreGroupNames: true)]
    public async Task BulkSetProvisionalRoleAsync() => await ShowMemberManageBulkDangerConfirmAsync("set_provisional", "仮登録にする");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkFreezeOnCustomId, ignoreGroupNames: true)]
    public async Task BulkFreezeOnAsync() => await ShowMemberManageBulkDangerConfirmAsync("freeze_on", "凍結ON");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkFreezeOffCustomId, ignoreGroupNames: true)]
    public async Task BulkFreezeOffAsync() => await ShowMemberManageBulkDangerConfirmAsync("freeze_off", "凍結OFF");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkVoiceNotifyOffCustomId, ignoreGroupNames: true)]
    public async Task BulkVoiceNotifyOffAsync() => await ShowMemberManageBulkDangerConfirmAsync("voice_notify_off", "VC通知表示OFF");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkTimeoutClearCustomId, ignoreGroupNames: true)]
    public async Task BulkTimeoutClearAsync() => await ShowMemberManageBulkDangerConfirmAsync("timeout_clear", "タイムアウト解除");

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkDangerConfirmCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExecuteMemberManageBulkDangerAsync(string action)
    {
        await ExecuteMemberManageBulkOperationAsync(action);
    }

    [RequireBotAdmin]
    [ComponentInteraction(MemberManageBulkDangerCancelCustomId, ignoreGroupNames: true)]
    public async Task CancelMemberManageBulkDangerAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneMemberManageBulkSelections();
        if (!_memberBulkSelections.TryGetValue(Context.User.Id, out var selection) ||
            selection.GuildId != Context.Guild.Id)
        {
            await RespondAsync("一括対象が未選択です。", ephemeral: true);
            return;
        }

        await RespondAsync(
            embed: BuildMemberManageBulkEmbed(selection.TargetUserIds, "一括操作をキャンセルしました。"),
            components: BuildMemberManageBulkComponents(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.RegistrationPendingListCustomId, ignoreGroupNames: true)]
    public async Task ShowRegistrationPendingListByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowRegistrationPendingListAsync(RegistrationPendingListMaxRows);
    }

    [RequireBotAdmin]
    [SlashCommand("integrity", "募集/抽選/お問い合わせの参照整合性を確認します")]
    public async Task IntegrityCheckByCommandAsync(
        [Summary("repair", "可能な範囲で簡易修復する")] bool repair = false)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowIntegrityCheckAsync(repair);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.IntegrityCheckCustomId, ignoreGroupNames: true)]
    public async Task IntegrityCheckByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowIntegrityCheckAsync(repair: false);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.RuntimeTuningCustomId, ignoreGroupNames: true)]
    public async Task ShowRuntimeTuningPanelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var embed = await BuildRuntimeTuningEmbedAsync(Context.Guild.Id);
        await RespondAsync(
            embed: embed,
            components: new ComponentBuilder()
                .WithButton("編集", RuntimeTuningEditCustomId, ButtonStyle.Primary, row: 0)
                .WithButton("保持日数編集", RuntimeInactiveKickRetentionEditCustomId, ButtonStyle.Secondary, row: 0)
                .Build(),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(RuntimeTuningEditCustomId, ignoreGroupNames: true)]
    public async Task ShowRuntimeTuningModalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var tuning = _runtimeTuning.GetSnapshot();
        var modal = new ModalBuilder()
            .WithTitle("運用設定")
            .WithCustomId(RuntimeTuningModalCustomId)
            .AddTextInput(
                "Cleanup間隔（分 / 1-720）",
                "admin_runtime_cleanup_interval_minutes",
                value: tuning.CleanupIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 3)
            .AddTextInput(
                "募集保持: 終了後（時間 / 1-720）",
                "admin_runtime_recruit_closed_retention_hours",
                value: tuning.RecruitClosedRetentionHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 3)
            .AddTextInput(
                "募集保持: 中断後（時間 / 1-720）",
                "admin_runtime_recruit_canceled_retention_hours",
                value: tuning.RecruitCanceledRetentionHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 3)
            .AddTextInput(
                "メッセージ保持（日 / 0-3650, 0=無効）",
                "admin_runtime_message_log_retention_days",
                value: tuning.MessageLogRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 4)
            .AddTextInput(
                "VC保持（日 / 0-3650, 0=無効）",
                "admin_runtime_voice_session_retention_days",
                value: tuning.VoiceSessionRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 4);
        await RespondWithModalAsync(modal.Build());
    }

    [RequireBotAdmin]
    [ComponentInteraction(RuntimeInactiveKickRetentionEditCustomId, ignoreGroupNames: true)]
    public async Task ShowInactiveKickRetentionModalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var retentionDays = await ResolveInactiveKickDataRetentionDaysAsync(Context.Guild.Id);
        var modal = new ModalBuilder()
            .WithTitle("自動キック後データ保持")
            .WithCustomId(RuntimeInactiveKickRetentionModalCustomId)
            .AddTextInput(
                "保持日数（日 / 1-30）",
                "admin_runtime_inactive_kick_user_data_retention_days",
                value: retentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                maxLength: 2);
        await RespondWithModalAsync(modal.Build());
    }

    [RequireBotAdmin]
    [ModalInteraction(RuntimeTuningModalCustomId, ignoreGroupNames: true)]
    public async Task HandleRuntimeTuningModalAsync(AdminRuntimeTuningModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.CleanupIntervalMinutes, out var cleanupMinutes))
        {
            await RespondAsync("Cleanup間隔(分)は数値で入力してください。", ephemeral: true);
            return;
        }
        if (!int.TryParse(modal.RecruitClosedRetentionHours, out var closedHours))
        {
            await RespondAsync("終了後保持時間(h)は数値で入力してください。", ephemeral: true);
            return;
        }
        if (!int.TryParse(modal.RecruitCanceledRetentionHours, out var canceledHours))
        {
            await RespondAsync("中断後保持時間(h)は数値で入力してください。", ephemeral: true);
            return;
        }
        if (!int.TryParse(modal.MessageLogRetentionDays, out var messageRetentionDays))
        {
            await RespondAsync("メッセージ保持日数は数値で入力してください。", ephemeral: true);
            return;
        }
        if (!int.TryParse(modal.VoiceSessionRetentionDays, out var voiceRetentionDays))
        {
            await RespondAsync("VC保持日数は数値で入力してください。", ephemeral: true);
            return;
        }
        if (messageRetentionDays is < 0 or > 3650)
        {
            await RespondAsync("メッセージ保持日数は 0-3650 の範囲で入力してください。", ephemeral: true);
            return;
        }
        if (voiceRetentionDays is < 0 or > 3650)
        {
            await RespondAsync("VC保持日数は 0-3650 の範囲で入力してください。", ephemeral: true);
            return;
        }
        var reactionRetentionDays = Math.Max(messageRetentionDays, Math.Max(0, _options.InactiveAutoKickDays));

        var updated = _runtimeTuning.Update(
            cleanupMinutes,
            closedHours,
            canceledHours,
            messageRetentionDays,
            voiceRetentionDays,
            reactionRetentionDays);
        await _db.UpsertGuildRuntimeTuningAsync(
            Context.Guild.Id,
            updated.CleanupIntervalMinutes,
            updated.RecruitClosedRetentionHours,
            updated.RecruitCanceledRetentionHours,
            updated.MessageLogRetentionDays,
            updated.VoiceSessionRetentionDays,
            updated.ReactionLogRetentionDays);
        await _audit.SendAsync(
            Context.Guild,
            "運用設定更新",
            $"👤 実行者: {Context.User.Mention} ({Context.User})\n⏱️ Cleanup間隔: {updated.CleanupIntervalMinutes}分\n📦 募集保持(終了): {updated.RecruitClosedRetentionHours}h\n📦 募集保持(中断): {updated.RecruitCanceledRetentionHours}h\n🧾 メッセージ保持: {updated.MessageLogRetentionDays}日\n🎤 VC保持: {updated.VoiceSessionRetentionDays}日\n😀 リアクション保持: {updated.ReactionLogRetentionDays}日（自動算出）\n💾 DBに保存しました（再起動後も維持）。",
            Color.Blue,
            Context.User);

        await RespondAsync(embed: await BuildRuntimeTuningEmbedAsync(Context.Guild.Id), ephemeral: true);
    }

    [RequireBotAdmin]
    [ModalInteraction(RuntimeInactiveKickRetentionModalCustomId, ignoreGroupNames: true)]
    public async Task HandleInactiveKickRetentionModalAsync(AdminInactiveKickRetentionModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.RetentionDays, out var retentionDays))
        {
            await RespondAsync("保持日数は数値で入力してください。", ephemeral: true);
            return;
        }

        if (retentionDays is < 1 or > 30)
        {
            await RespondAsync("保持日数は 1-30 の範囲で入力してください。", ephemeral: true);
            return;
        }

        var updatedDays = await _db.UpsertGuildInactiveKickUserDataRetentionDaysAsync(Context.Guild.Id, retentionDays);
        await _audit.SendAsync(
            Context.Guild,
            "運用設定更新（自動キック後データ保持）",
            $"👤 実行者: {Context.User.Mention} ({Context.User})\n🗓️ 自動キック後ユーザーデータ保持: {updatedDays}日\n💾 DBに保存しました（再起動後も維持）。",
            Color.Blue,
            Context.User);

        await RespondAsync(embed: await BuildRuntimeTuningEmbedAsync(Context.Guild.Id), ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.LotteryCustomId, ignoreGroupNames: true)]
    public async Task LotteryByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await ShowLotteryManagePanelAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.ManageRefreshCustomId, ignoreGroupNames: true)]
    public async Task RefreshLotteryManagePanelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await ShowLotteryManagePanelAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.ManageControlCustomId, ignoreGroupNames: true)]
    public async Task ShowLotteryManageSelectionAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var lotteries = await _db.ListRecentLotteriesAsync(Context.Guild.Id, 25);
        await RespondAsync(
            embed: LotteryPanel.BuildManagementSelectionEmbed(lotteries),
            components: LotteryPanel.BuildManagementSelectionComponents(lotteries),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.ManagePickCustomId, ignoreGroupNames: true)]
    public async Task HandleLotteryManagePickAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 } || !int.TryParse(values[0], out var lotteryId) || lotteryId <= 0)
        {
            await RespondAsync("抽選選択が不正です。", ephemeral: true);
            return;
        }

        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await RespondAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        await ShowLotteryAdminDetailAsync(lottery, "対象の抽選を選択しました。");
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.ManageBackCustomId, ignoreGroupNames: true)]
    public async Task BackToLotteryManageRootAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await ShowLotteryManagePanelAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.ManageCreateCustomId, ignoreGroupNames: true)]
    public async Task ShowLotteryCreateModalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        _lotteryCreateDraftTracker.Set(Context.User.Id, new LotteryCreateDraftContext(
            nowUtc.Add(LotteryDefaultCloseDuration),
            AllowDuplicateWinners: false,
            MentionMode: LotteryCreateMentionMode.None,
            nowUtc));
        await ShowLotteryCreateCloseDatePageAsync(0, "抽選の締切日を選択してください。");
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseDefaultCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateCloseDefaultAsync()
        => SelectLotteryCreateCloseDefaultInternalAsync();

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateToggleDuplicateWinnersCustomId, ignoreGroupNames: true)]
    public async Task ToggleLotteryCreateDuplicateWinnersAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var draftContext = GetOrCreateLotteryCreateDraftContext(nowUtc);
        var updated = draftContext with
        {
            AllowDuplicateWinners = !draftContext.AllowDuplicateWinners,
            CreatedAt = nowUtc
        };
        _lotteryCreateDraftTracker.Set(Context.User.Id, updated);
        var label = updated.AllowDuplicateWinners ? "許可" : "不可";
        await ShowLotteryCreateCloseDatePageAsync(0, $"重複当選を `{label}` に設定しました。");
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateSetDuplicateOnCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateDuplicateOnAsync()
        => SelectLotteryCreateDuplicateInternalAsync(allowDuplicateWinners: true);

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateSetDuplicateOffCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateDuplicateOffAsync()
        => SelectLotteryCreateDuplicateInternalAsync(allowDuplicateWinners: false);

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateDuplicateBackCustomId, ignoreGroupNames: true)]
    public Task BackToLotteryCreateCloseDateAsync()
        => ShowLotteryCreateCloseDatePageAsync(0, "抽選の締切日を選択してください。");

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateSetMentionNoneCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateMentionNoneAsync()
        => SelectLotteryCreateMentionInternalAsync(LotteryCreateMentionMode.None);

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateSetMentionHereCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateMentionHereAsync()
        => SelectLotteryCreateMentionInternalAsync(LotteryCreateMentionMode.Here);

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateSetMentionEveryoneCustomId, ignoreGroupNames: true)]
    public Task SelectLotteryCreateMentionEveryoneAsync()
        => SelectLotteryCreateMentionInternalAsync(LotteryCreateMentionMode.Everyone);

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateMentionBackCustomId, ignoreGroupNames: true)]
    public Task BackToLotteryCreateDuplicateAsync()
        => BackToLotteryCreateDuplicateInternalAsync();

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseTestCustomId, ignoreGroupNames: true)]
    public async Task ShowLotteryCreateTestModalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        _ = GetOrCreateLotteryCreateDraftContext(DateTimeOffset.UtcNow);
        await RespondWithModalAsync<LotteryCreateTestModal>(LotteryCreateTestModalCustomId);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateClosePageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectLotteryCreateClosePageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        var maxPage = LotteryCreateCloseMaxDayOffset / LotteryCreateSchedulePageSize;
        if (pageIndex < 0 || pageIndex > maxPage)
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        await ShowLotteryCreateCloseDatePageAsync(pageIndex);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseDayPickCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectLotteryCreateCloseDayPickAsync(string dayOffsetRaw)
    {
        if (!int.TryParse(dayOffsetRaw, out var dayOffset) ||
            dayOffset is < 0 or > LotteryCreateCloseMaxDayOffset)
        {
            await RespondAsync("日付の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectLotteryCreateCloseDayInternalAsync(dayOffset);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseHourCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectLotteryCreateCloseHourAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            dayOffset is < 0 or > LotteryCreateCloseMaxDayOffset ||
            hour is < 0 or > 23)
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectLotteryCreateCloseHourInternalAsync(dayOffset, hour);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseHourSelectCustomId, ignoreGroupNames: true)]
    public async Task SelectLotteryCreateCloseHourSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectLotteryCreateCloseHourAsync(values[0]);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseMinuteCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectLotteryCreateCloseMinuteAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            !int.TryParse(parts[2], out var minute) ||
            dayOffset is < 0 or > LotteryCreateCloseMaxDayOffset ||
            hour is < 0 or > 23 ||
            (minute != 0 && minute != 10 && minute != 20 && minute != 30 && minute != 40 && minute != 50))
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectLotteryCreateCloseMinuteInternalAsync(dayOffset, hour, minute);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryCreateCloseMinuteSelectCustomId, ignoreGroupNames: true)]
    public async Task SelectLotteryCreateCloseMinuteSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectLotteryCreateCloseMinuteAsync(values[0]);
    }

    [RequireBotAdmin]
    [ModalInteraction(LotteryPanel.CreateModalCustomId, ignoreGroupNames: true)]
    public async Task HandleLotteryCreateModalAsync(LotteryCreateModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var channelName = string.IsNullOrWhiteSpace(_options.LotteryPostChannelName)
            ? "抽選"
            : _options.LotteryPostChannelName.Trim();
        var channel = FindTextChannelByName(Context.Guild, channelName);
        if (channel is null)
        {
            await RespondAsync($"抽選投稿先チャンネルが見つかりません（Discord:LotteryPostChannelName=`{channelName}`）。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        PruneLotteryCreateDraftContexts();
        if (!_lotteryCreateDraftTracker.TryGet(Context.User.Id, out var draftContext))
        {
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        if (nowUtc - draftContext.CreatedAt > LotteryCreateDraftTimeout)
        {
            _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync(
                "締切日時の選択が期限切れです。もう一度選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        var closeAt = draftContext.CloseAtUtc;
        if (closeAt <= nowUtc || closeAt - nowUtc > LotteryMaxCloseDuration)
        {
            _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync(
                "締切日時が不正です。もう一度選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }
        var allowDuplicateWinners = draftContext.AllowDuplicateWinners;
        var mentionMode = draftContext.MentionMode;

        var title = modal.LotteryTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("タイトルを入力してください。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.WinnerCount, out var winnerCount) || winnerCount is < 1 or > 99)
        {
            await RespondAsync("当選人数は 1-99 の整数で入力してください。", ephemeral: true);
            return;
        }

        var prizeList = NormalizeLotteryPrizeList(modal.PrizeList, winnerCount, out var prizeError);
        if (prizeError is not null)
        {
            await RespondAsync(prizeError, ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var description = modal.Description?.Trim();
        var creatorName = Context.User.GlobalName ?? Context.User.Username;
        var lottery = await _db.CreateLotteryAsync(
            Context.Guild.Id,
            channel.Id,
            title,
            description,
            prizeList,
            allowDuplicateWinners,
            winnerCount,
            closeAt,
            Context.User.Id,
            creatorName,
            nowUtc);

        var mentionSettings = BuildLotteryPostMentionSettings(mentionMode);
        IUserMessage posted;
        if (mentionSettings.SuppressNotification)
        {
            posted = await channel.SendMessageAsync(
                text: mentionSettings.MentionText,
                embed: LotteryPanel.BuildLotteryEmbed(lottery, Array.Empty<LotteryEntry>(), Array.Empty<LotteryWinner>(), nowUtc),
                components: LotteryPanel.BuildLotteryComponents(lottery, 0, 0, nowUtc),
                flags: MessageFlags.SuppressNotification,
                allowedMentions: mentionSettings.AllowedMentions);
        }
        else
        {
            posted = await channel.SendMessageAsync(
                text: mentionSettings.MentionText,
                embed: LotteryPanel.BuildLotteryEmbed(lottery, Array.Empty<LotteryEntry>(), Array.Empty<LotteryWinner>(), nowUtc),
                components: LotteryPanel.BuildLotteryComponents(lottery, 0, 0, nowUtc),
                allowedMentions: mentionSettings.AllowedMentions);
        }
        await _db.UpdateLotteryMessageIdAsync(lottery.Id, posted.Id);
        _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);

        var link = $"https://discord.com/channels/{Context.Guild.Id}/{channel.Id}/{posted.Id}";
        await FollowupAsync(
            $"抽選を作成しました。#{lottery.Id} / 締切: <t:{closeAt.ToUnixTimeSeconds()}:F> / [メッセージを開く]({link})",
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ModalInteraction(LotteryCreateTestModalCustomId, ignoreGroupNames: true)]
    public async Task HandleLotteryCreateTestModalAsync(LotteryCreateTestModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var channelName = string.IsNullOrWhiteSpace(_options.LotteryPostChannelName)
            ? "抽選"
            : _options.LotteryPostChannelName.Trim();
        var channel = FindTextChannelByName(Context.Guild, channelName);
        if (channel is null)
        {
            await RespondAsync($"抽選投稿先チャンネルが見つかりません（Discord:LotteryPostChannelName=`{channelName}`）。", ephemeral: true);
            return;
        }

        var title = modal.LotteryTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("タイトルを入力してください。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.WinnerCount, out var winnerCount) || winnerCount is < 1 or > 99)
        {
            await RespondAsync("当選人数は 1-99 の整数で入力してください。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.CloseSeconds, out var closeInSeconds) ||
            closeInSeconds is < 1 or > LotteryTestCloseSecondsMax)
        {
            await RespondAsync($"締切まで秒（テスト用）は 1-{LotteryTestCloseSecondsMax} の整数で入力してください。", ephemeral: true);
            return;
        }

        var prizeList = NormalizeLotteryPrizeList(modal.PrizeList, winnerCount, out var prizeError);
        if (prizeError is not null)
        {
            await RespondAsync(prizeError, ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var nowUtc = DateTimeOffset.UtcNow;
        PruneLotteryCreateDraftContexts();
        var hasDraft =
            _lotteryCreateDraftTracker.TryGet(Context.User.Id, out var draftContext) &&
            nowUtc - draftContext.CreatedAt <= LotteryCreateDraftTimeout;
        var allowDuplicateWinners = hasDraft && draftContext.AllowDuplicateWinners;
        var mentionMode = hasDraft ? draftContext.MentionMode : LotteryCreateMentionMode.None;
        var closeAt = nowUtc.AddSeconds(closeInSeconds);
        var description = modal.Description?.Trim();
        var creatorName = Context.User.GlobalName ?? Context.User.Username;
        var lottery = await _db.CreateLotteryAsync(
            Context.Guild.Id,
            channel.Id,
            title,
            description,
            prizeList,
            allowDuplicateWinners,
            winnerCount,
            closeAt,
            Context.User.Id,
            creatorName,
            nowUtc);

        var mentionSettings = BuildLotteryPostMentionSettings(mentionMode);
        IUserMessage posted;
        if (mentionSettings.SuppressNotification)
        {
            posted = await channel.SendMessageAsync(
                text: mentionSettings.MentionText,
                embed: LotteryPanel.BuildLotteryEmbed(lottery, Array.Empty<LotteryEntry>(), Array.Empty<LotteryWinner>(), nowUtc),
                components: LotteryPanel.BuildLotteryComponents(lottery, 0, 0, nowUtc),
                flags: MessageFlags.SuppressNotification,
                allowedMentions: mentionSettings.AllowedMentions);
        }
        else
        {
            posted = await channel.SendMessageAsync(
                text: mentionSettings.MentionText,
                embed: LotteryPanel.BuildLotteryEmbed(lottery, Array.Empty<LotteryEntry>(), Array.Empty<LotteryWinner>(), nowUtc),
                components: LotteryPanel.BuildLotteryComponents(lottery, 0, 0, nowUtc),
                allowedMentions: mentionSettings.AllowedMentions);
        }
        await _db.UpdateLotteryMessageIdAsync(lottery.Id, posted.Id);
        _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);

        var link = $"https://discord.com/channels/{Context.Guild.Id}/{channel.Id}/{posted.Id}";
        await FollowupAsync(
            $"テスト抽選を作成しました。#{lottery.Id} / 締切: <t:{closeAt.ToUnixTimeSeconds()}:F> / [メッセージを開く]({link})",
            ephemeral: true);
    }

    [ComponentInteraction("lottery_join:*", ignoreGroupNames: true)]
    public async Task JoinLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User.IsBot)
        {
            await RespondAsync("Botは応募できません。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!LotteryStatus.IsOpen(lottery.Status))
        {
            await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
            await FollowupAsync("この抽選は応募を締め切っています。", ephemeral: true);
            return;
        }

        var displayName = Context.User.GlobalName ?? Context.User.Username;
        var added = await _db.AddLotteryEntryAsync(lottery.Id, Context.User.Id, displayName, DateTimeOffset.UtcNow);
        if (Context.User is SocketGuildUser guildUser)
        {
            var role = await EnsureLotteryParticipantRoleAsync(lottery);
            if (role is not null)
            {
                await TryAddRoleAsync(guildUser, role);
            }
        }

        if (added)
        {
            await _audit.SendAsync(
                Context.Guild,
                "抽選応募",
                $"抽選: #{lottery.Id} {lottery.Title}\n締切: <t:{lottery.CloseAt.ToUnixTimeSeconds()}:F>",
                Color.Green,
                Context.User);
        }

        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await FollowupAsync(added ? "応募しました。" : "すでに応募済みです。", ephemeral: true);
    }

    [ComponentInteraction("lottery_leave:*", ignoreGroupNames: true)]
    public async Task LeaveLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!LotteryStatus.IsOpen(lottery.Status))
        {
            await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
            await FollowupAsync("この抽選はすでに締切済みです。", ephemeral: true);
            return;
        }

        var removed = await _db.RemoveLotteryEntryAsync(lottery.Id, Context.User.Id);
        if (removed &&
            Context.User is SocketGuildUser guildUser &&
            lottery.ParticipantRoleId.HasValue &&
            lottery.ParticipantRoleId.Value != 0)
        {
            var role = Context.Guild.GetRole(ToUlong(lottery.ParticipantRoleId.Value));
            if (role is not null)
            {
                await TryRemoveRoleAsync(guildUser, role);
            }
        }

        if (removed)
        {
            await _audit.SendAsync(
                Context.Guild,
                "抽選辞退",
                $"抽選: #{lottery.Id} {lottery.Title}\n締切: <t:{lottery.CloseAt.ToUnixTimeSeconds()}:F>",
                Color.Orange,
                Context.User);
        }

        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await FollowupAsync(removed ? "応募を取り消しました。" : "応募していません。", ephemeral: true);
    }

    [ComponentInteraction(LotteryPanel.ClaimPrizeCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ClaimLotteryPrizeAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User.IsBot)
        {
            await RespondAsync("Botは操作できません。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        var winners = await _db.ListLotteryWinnersAsync(lottery.Id);
        if (winners.Count == 0)
        {
            await FollowupAsync("まだ当選者が確定していません。", ephemeral: true);
            return;
        }

        var userDbId = ToDbId(Context.User.Id);
        if (!winners.Any(winner => winner.UserId == userDbId))
        {
            await FollowupAsync("当選者のみ操作できます。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser winnerUser)
        {
            await FollowupAsync("ユーザー情報の取得に失敗しました。", ephemeral: true);
            return;
        }

        var existingClaim = await _db.GetLotteryClaimChannelAsync(lottery.Id, winnerUser.Id);
        var claimChannel = await EnsureLotteryClaimChannelAsync(lottery, winnerUser);
        if (claimChannel is null)
        {
            await FollowupAsync("受け取りチャンネルの作成に失敗しました。管理者に連絡してください。", ephemeral: true);
            return;
        }

        if (existingClaim is null)
        {
            await _audit.SendAsync(
                Context.Guild,
                "景品受け取りチャンネル作成",
                $"抽選: #{lottery.Id} {lottery.Title}\n当選者: {winnerUser.Mention}\nチャンネル: <#{claimChannel.Id}>",
                Color.Green,
                Context.User);
        }

        await FollowupAsync($"受け取り対応チャンネルを用意しました。<#{claimChannel.Id}>", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryClaimToggleCustomId, ignoreGroupNames: true)]
    public async Task ToggleLotteryClaimChannelAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser actor)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var context = await ResolveLotteryClaimContextAsync();
        if (context is null)
        {
            await FollowupAsync("このチャンネルは景品受け取りチャンネルではありません。", ephemeral: true);
            return;
        }
        var claimContext = context.Value;

        var nextClosed = !claimContext.Claim.IsClosed;
        var nowUtc = DateTimeOffset.UtcNow;
        var updated = await _db.UpdateLotteryClaimChannelClosedStateAsync(claimContext.Claim.Id, nextClosed, nowUtc);
        if (!updated)
        {
            await FollowupAsync("状態更新に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var current = await _db.GetLotteryClaimChannelByChannelIdAsync(claimContext.Channel.Id);
        if (current is null)
        {
            await FollowupAsync("状態更新後の情報取得に失敗しました。", ephemeral: true);
            return;
        }

        var winnerUserId = ToUlong(current.UserId);
        await ApplyLotteryClaimPermissionsAsync(claimContext.Channel, claimContext.Guild, winnerUserId, current.IsClosed);
        var winnerUser = claimContext.Guild.GetUser(winnerUserId);

        await EnsureLotteryClaimControlMessageAsync(claimContext.Channel, current, claimContext.Lottery, winnerUser, announceCreated: false);
        await FollowupAsync(nextClosed ? "受け取りチャンネルをクローズしました。" : "受け取りチャンネルを再開しました。", ephemeral: true);

        await _audit.SendAsync(
            claimContext.Guild,
            nextClosed ? "景品受け取りクローズ" : "景品受け取り再開",
            $"抽選: #{claimContext.Lottery.Id} {claimContext.Lottery.Title}\nチャンネル: <#{claimContext.Channel.Id}>",
            nextClosed ? Color.DarkGrey : Color.Green,
            actor);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryClaimDeleteCustomId, ignoreGroupNames: true)]
    public async Task DeleteLotteryClaimChannelAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser actor)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var context = await ResolveLotteryClaimContextAsync();
        if (context is null)
        {
            await FollowupAsync("このチャンネルは景品受け取りチャンネルではありません。", ephemeral: true);
            return;
        }
        var claimContext = context.Value;

        if (!claimContext.Claim.IsClosed)
        {
            await FollowupAsync("チャンネル削除はクローズ状態でのみ実行できます。", ephemeral: true);
            return;
        }

        await FollowupAsync("受け取りチャンネルを削除します。", ephemeral: true);

        var winners = await _db.ListLotteryWinnersAsync(claimContext.Lottery.Id);
        var prizeText = BuildLotteryClaimPrizeText(claimContext.Lottery, winners, claimContext.Claim.UserId);
        var userId = ToUlong(claimContext.Claim.UserId);
        var thisLotteryWinCount = winners.Count(winner => winner.UserId == claimContext.Claim.UserId);
        var totalWinCount = await _db.CountLotteryWinsForGuildUserAsync(ToUlong(claimContext.Lottery.GuildId), userId);
        await _audit.SendAsync(
            claimContext.Guild,
            "景品受け取り完了",
            $"抽選: #{claimContext.Lottery.Id} {claimContext.Lottery.Title}\n当選者: <@{userId}>\n当選回数: この抽選 {thisLotteryWinCount}回 / 累計 {totalWinCount}回\n受け取り景品:\n{prizeText}\nチャンネル: <#{claimContext.Channel.Id}>",
            Color.DarkGrey,
            actor);
        await claimContext.Channel.DeleteAsync(new RequestOptions
        {
            AuditLogReason = $"Lottery claim channel deleted. LotteryId={claimContext.Lottery.Id} ClaimId={claimContext.Claim.Id}"
        });
        await _db.DeleteLotteryClaimChannelAsync(claimContext.Claim.Id);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryPanel.AdminConfirmActionCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmLotteryAdminActionAsync(string value)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryAdminConfirmAction(value, out var action, out var lotteryId))
        {
            await RespondAsync("抽選操作の確認情報が不正です。", ephemeral: true);
            return;
        }

        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await RespondAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        var (actionLabel, actionDescription, confirmStyle) = action switch
        {
            "draw" => ("抽選実行", "当選者を確定し、結果投稿と景品受け取り導線を更新します。", ButtonStyle.Primary),
            "redraw" => ("再抽選", "既存の当選者情報を新しい結果で上書きします。", ButtonStyle.Danger),
            "close" => ("締切", "応募受付を終了し、抽選結果表示を締切状態に更新します。", ButtonStyle.Danger),
            "cancel" => ("中止", "抽選を中止状態に変更し、参加導線を停止します。", ButtonStyle.Danger),
            _ => ("操作", "この操作を実行します。", ButtonStyle.Secondary)
        };

        var title = string.IsNullOrWhiteSpace(lottery.Title) ? "(no title)" : lottery.Title.Trim();
        await RespondAsync(
            $"本当に「{actionLabel}」しますか？\n対象: #{lottery.Id} {title}\n{actionDescription}",
            components: BuildLotteryAdminConfirmComponents(action, lottery.Id, actionLabel, confirmStyle),
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryAdminConfirmExecuteCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ExecuteConfirmedLotteryAdminActionAsync(string value)
    {
        if (!TryParseLotteryAdminConfirmAction(value, out var action, out var lotteryId))
        {
            await RespondAsync("抽選操作の確認情報が不正です。", ephemeral: true);
            return;
        }

        var rawId = lotteryId.ToString();
        switch (action)
        {
            case "draw":
                await DrawLotteryAsync(rawId);
                return;
            case "redraw":
                await RedrawLotteryAsync(rawId);
                return;
            case "close":
                await CloseLotteryAsync(rawId);
                return;
            case "cancel":
                await CancelLotteryAsync(rawId);
                return;
            default:
                await RespondAsync("未対応の抽選操作です。", ephemeral: true);
                return;
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(LotteryAdminConfirmCancelCustomId, ignoreGroupNames: true)]
    public Task CancelLotteryAdminConfirmAsync()
        => RespondAsync("抽選操作を中止しました。", ephemeral: true);

    [RequireBotAdmin]
    [ComponentInteraction("lottery_refresh:*", ignoreGroupNames: true)]
    public async Task RefreshLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await TryUpdateLotteryAdminDetailMessageAsync(lottery);
        await FollowupAsync("抽選表示を更新しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction("lottery_draw:*", ignoreGroupNames: true)]
    public async Task DrawLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (LotteryStatus.IsCanceled(lottery.Status))
        {
            await FollowupAsync("中止済みの抽選は実行できません。", ephemeral: true);
            return;
        }

        var previousStatus = LotteryStatus.Normalize(lottery.Status);
        var entries = (await _db.ListLotteryEntriesAsync(lottery.Id)).ToList();
        if (entries.Count == 0)
        {
            await FollowupAsync("応募者がいないため抽選できません。", ephemeral: true);
            return;
        }

        var drawCount = lottery.AllowDuplicateWinners
            ? Math.Max(lottery.WinnerCount, 1)
            : Math.Min(Math.Max(lottery.WinnerCount, 1), entries.Count);
        var picked = PickLotteryWinners(entries, drawCount, lottery.AllowDuplicateWinners);
        var nowUtc = DateTimeOffset.UtcNow;
        await _db.ReplaceLotteryWinnersAsync(lottery.Id, picked, Context.User.Id, nowUtc);
        await _db.UpdateLotteryStatusAsync(lottery.Id, LotteryStatus.Drawn, nowUtc);
        lottery = await GetLotteryForCurrentGuildAsync(lottery.Id) ?? lottery;
        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await TryUpdateLotteryAdminDetailMessageAsync(lottery);
        await ScheduleLotteryParticipantRoleCleanupAsync(lottery, "抽選が終了しました。");
        await PostLotteryResultEmbedAsync(lottery);
        await SendLotteryStatusTransitionAuditAsync(
            lottery,
            previousStatus,
            LotteryStatus.Normalize(lottery.Status),
            "手動（抽選実行）",
            $"当選者数: {picked.Count}");

        var winnerMentions = string.Join(", ", picked.Select(item => $"<@{ToUlong(item.UserId)}>"));
        await FollowupAsync($"抽選しました。当選者: {winnerMentions}", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction("lottery_redraw:*", ignoreGroupNames: true)]
    public async Task RedrawLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!LotteryStatus.IsDrawn(lottery.Status))
        {
            await FollowupAsync("再抽選は抽選済みの募集でのみ実行できます。", ephemeral: true);
            return;
        }

        var previousStatus = LotteryStatus.Normalize(lottery.Status);
        var entries = (await _db.ListLotteryEntriesAsync(lottery.Id)).ToList();
        if (entries.Count == 0)
        {
            await FollowupAsync("応募者がいないため再抽選できません。", ephemeral: true);
            return;
        }

        var existingWinners = await _db.ListLotteryWinnersAsync(lottery.Id);
        var excludedIds = existingWinners.Select(winner => winner.UserId).ToHashSet();
        var redrawCandidates = entries.Where(entry => !excludedIds.Contains(entry.UserId)).ToList();
        List<LotteryEntry> source;
        int drawCount;
        if (lottery.AllowDuplicateWinners)
        {
            source = redrawCandidates.Count > 0 ? redrawCandidates : entries;
            drawCount = Math.Max(lottery.WinnerCount, 1);
        }
        else
        {
            if (redrawCandidates.Count == 0)
            {
                await FollowupAsync("再抽選候補がいません（全応募者が当選済み）。", ephemeral: true);
                return;
            }

            source = redrawCandidates;
            drawCount = Math.Min(Math.Max(lottery.WinnerCount, 1), redrawCandidates.Count);
        }

        var picked = PickLotteryWinners(source, drawCount, lottery.AllowDuplicateWinners);
        var nowUtc = DateTimeOffset.UtcNow;
        await _db.ReplaceLotteryWinnersAsync(lottery.Id, picked, Context.User.Id, nowUtc);
        await _db.UpdateLotteryStatusAsync(lottery.Id, LotteryStatus.Drawn, nowUtc);
        lottery = await GetLotteryForCurrentGuildAsync(lottery.Id) ?? lottery;
        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await TryUpdateLotteryAdminDetailMessageAsync(lottery);
        await PostLotteryResultEmbedAsync(lottery);
        await SendLotteryStatusTransitionAuditAsync(
            lottery,
            previousStatus,
            LotteryStatus.Normalize(lottery.Status),
            "手動（再抽選）",
            $"当選者数: {picked.Count}");

        var winnerMentions = string.Join(", ", picked.Select(item => $"<@{ToUlong(item.UserId)}>"));
        await FollowupAsync($"再抽選しました。新当選者: {winnerMentions}", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction("lottery_close:*", ignoreGroupNames: true)]
    public async Task CloseLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        var previousStatus = LotteryStatus.Normalize(lottery.Status);
        if (!LotteryStatus.IsOpen(lottery.Status))
        {
            await FollowupAsync("すでに締切済みです。", ephemeral: true);
            return;
        }

        await _db.UpdateLotteryStatusAsync(lottery.Id, LotteryStatus.Closed, DateTimeOffset.UtcNow);
        lottery = await GetLotteryForCurrentGuildAsync(lottery.Id) ?? lottery;
        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await TryUpdateLotteryAdminDetailMessageAsync(lottery);
        await ScheduleLotteryParticipantRoleCleanupAsync(lottery, "抽選が終了しました。");
        await PostLotteryResultEmbedAsync(lottery);
        await SendLotteryStatusTransitionAuditAsync(
            lottery,
            previousStatus,
            LotteryStatus.Normalize(lottery.Status),
            "手動（締切）");
        await FollowupAsync("抽選を締め切りました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction("lottery_cancel:*", ignoreGroupNames: true)]
    public async Task CancelLotteryAsync(string lotteryIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseLotteryId(lotteryIdRaw, out var lotteryId))
        {
            await RespondAsync("抽選情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var lottery = await GetLotteryForCurrentGuildAsync(lotteryId);
        if (lottery is null)
        {
            await FollowupAsync("対象の抽選が見つかりませんでした。", ephemeral: true);
            return;
        }

        var previousStatus = LotteryStatus.Normalize(lottery.Status);
        if (LotteryStatus.IsCanceled(lottery.Status))
        {
            await FollowupAsync("すでに中止済みです。", ephemeral: true);
            return;
        }

        await _db.UpdateLotteryStatusAsync(lottery.Id, LotteryStatus.Canceled, DateTimeOffset.UtcNow);
        lottery = await GetLotteryForCurrentGuildAsync(lottery.Id) ?? lottery;
        await RefreshLotteryMessageAsync(lottery, GetKnownLotteryMessage());
        await TryUpdateLotteryAdminDetailMessageAsync(lottery);
        await ScheduleLotteryParticipantRoleCleanupAsync(lottery, "抽選は中止されました。");
        await PostLotteryResultEmbedAsync(lottery);
        await SendLotteryStatusTransitionAuditAsync(
            lottery,
            previousStatus,
            LotteryStatus.Normalize(lottery.Status),
            "手動（中止）");
        await FollowupAsync("抽選を中止しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.InquiryDashboardCustomId, ignoreGroupNames: true)]
    public async Task ShowInquiryDashboardByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var channel = await ResolveOrCreateInquiryManagementChannelAsync(Context.Guild);
        if (channel is null)
        {
            await FollowupAsync("お問い合わせ管理チャンネルの作成に失敗しました。", ephemeral: true);
            return;
        }

        var message = await EnsureInquiryDashboardMessageAsync(channel);
        await FollowupAsync($"お問い合わせ管理パネルを <#{channel.Id}> に表示しました。(message: {message.Id})", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(InquiryManagementPanel.RefreshCustomId, ignoreGroupNames: true)]
    public async Task RefreshInquiryDashboardAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        if (Context.Interaction is SocketMessageComponent component &&
            component.Message is IUserMessage source &&
            source.Author.Id == Context.Client.CurrentUser.Id &&
            InquiryManagementPanel.LooksLikeDashboardMessage(source))
        {
            var payload = await BuildInquiryDashboardPayloadAsync(Context.Guild.Id);
            await source.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = payload.Embed;
                props.Components = payload.Components;
            });
            await FollowupAsync("お問い合わせ管理パネルを更新しました。", ephemeral: true);
            return;
        }

        var channel = await ResolveOrCreateInquiryManagementChannelAsync(Context.Guild);
        if (channel is null)
        {
            await FollowupAsync("お問い合わせ管理チャンネルの作成に失敗しました。", ephemeral: true);
            return;
        }

        var message = await EnsureInquiryDashboardMessageAsync(channel);
        await FollowupAsync($"お問い合わせ管理パネルを更新しました。<#{channel.Id}> / {message.Id}", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(InquiryManagementPanel.PickCustomId, ignoreGroupNames: true)]
    public async Task ShowInquiryDetailByPickAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 } || !int.TryParse(values[0], out var ticketId))
        {
            await RespondAsync("問い合わせ選択が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowInquiryTicketDetailAsync(ticketId);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ToggleRegistrationApprovalCustomId, ignoreGroupNames: true)]
    public async Task ToggleRegistrationApprovalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        if (settings is null)
        {
            await FollowupAsync("サーバー設定が未初期化です。Botを再起動してから再試行してください。", ephemeral: true);
            return;
        }

        var previousValue = settings.RegistrationApprovalRequired;
        var updated = await _db.UpdateGuildRegistrationApprovalRequiredAsync(
            Context.Guild.Id,
            !settings.RegistrationApprovalRequired);
        if (!updated)
        {
            await FollowupAsync("設定更新に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var refreshedSettings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = refreshedSettings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        if (Context.Interaction is SocketMessageComponent component &&
            component.Message is IUserMessage message &&
            LooksLikeAdminControlPanelMessage(message))
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }
        await RefreshStoredAdminControlPanelStateAsync(approvalRequired, inactiveAutoKickEnabled);

        await FollowupAsync(
            approvalRequired
                ? "登録確認フローを「必要」に変更しました。"
                : "登録確認フローを「不要」に変更しました（申請送信時に即時メンバー付与）。",
            ephemeral: true);

        await _audit.SendAsync(
            Context.Guild,
            "設定変更: 登録確認フロー",
            $"旧値: {(previousValue ? "必要" : "不要")}\n新値: {(approvalRequired ? "必要" : "不要")}",
            Color.Teal,
            Context.User);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ToggleInactiveAutoKickCustomId, ignoreGroupNames: true)]
    public async Task ToggleInactiveAutoKickAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        if (settings is null)
        {
            await FollowupAsync("サーバー設定が未初期化です。Botを再起動してから再試行してください。", ephemeral: true);
            return;
        }

        var previousValue = settings.InactiveAutoKickEnabled;
        var updated = await _db.UpdateGuildInactiveAutoKickEnabledAsync(
            Context.Guild.Id,
            !settings.InactiveAutoKickEnabled);
        if (!updated)
        {
            await FollowupAsync("設定更新に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var refreshedSettings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = refreshedSettings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        if (Context.Interaction is SocketMessageComponent component &&
            component.Message is IUserMessage message &&
            LooksLikeAdminControlPanelMessage(message))
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }
        await RefreshStoredAdminControlPanelStateAsync(approvalRequired, inactiveAutoKickEnabled);

        await FollowupAsync(
            inactiveAutoKickEnabled
                ? "30日非アクティブ自動キックを「ON」に変更しました。"
                : "30日非アクティブ自動キックを「OFF」に変更しました。",
            ephemeral: true);

        await _audit.SendAsync(
            Context.Guild,
            "設定変更: 30日非アクティブ自動キック",
            $"旧値: {(previousValue ? "ON" : "OFF")}\n新値: {(inactiveAutoKickEnabled ? "ON" : "OFF")}",
            Color.Teal,
            Context.User);
    }

    private async Task ExportMessageCsvAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = DateTimeOffset.UnixEpoch;
        var rows = await _db.ListMessageLogsForExportAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive);

        if (rows.Count == 0)
        {
            await FollowupAsync("メッセージログはありません。", ephemeral: true);
            return;
        }

        var csv = BuildMessageLogCsv(rows);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes, writable: false);
        var fileName = $"message_logs_{Context.Guild.Id}_all_to_{toUtcExclusive:yyyyMMddHHmm}.csv";
        await FollowupWithFileAsync(stream, fileName, text: $"CSVを出力しました。期間: 全期間 / 件数: {rows.Count}", ephemeral: true);
    }

    private async Task SearchLogsInternalAsync(int days, ulong? userId, int limit)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-days);
        var readLimit = Math.Clamp(limit, 1, 30);
        var messageTask = _db.ListRecentMessageLogsAsync(
            Context.Guild.Id,
            fromUtcInclusive,
            toUtcExclusive,
            userId,
            maxRows: readLimit,
            includeDeleted: true);
        var voiceTask = _db.ListRecentVoiceSessionsAsync(
            Context.Guild.Id,
            fromUtcInclusive,
            toUtcExclusive,
            userId,
            maxRows: readLimit);

        await Task.WhenAll(messageTask, voiceTask);
        var messageRows = messageTask.Result;
        var voiceRows = voiceTask.Result;
        var targetText = userId.HasValue ? $"<@{userId.Value}>" : "全員";

        var embed = new EmbedBuilder()
            .WithTitle("ログ検索結果")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象", targetText, true)
            .AddField("期間", $"{days}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", true)
            .AddField("取得件数", $"メッセージ: {messageRows.Count} / VC: {voiceRows.Count}", true)
            .AddField($"メッセージ(最新{readLimit}件)", BuildMessageLogSearchLines(messageRows, readLimit), false)
            .AddField($"VCセッション(最新{readLimit}件)", BuildVoiceLogSearchLines(voiceRows, readLimit, toUtcExclusive), false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowOpsKpiAsync(int days)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        var lookbackDays = Math.Clamp(days, OpsKpiMinDays, OpsKpiMaxDays);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);

        var lookbackVoiceHourlyTask = _db.ListVoiceHourlyActivityAsync(guild.Id, fromUtcInclusive, toUtcExclusive);
        var lookbackVoiceWeekdayHourlyTask = _db.ListVoiceWeekdayHourlyActivityAsync(guild.Id, fromUtcInclusive, toUtcExclusive);
        var lookbackMessageWeekdayHourlyTask = _db.ListMessageWeekdayHourlyActivityAsync(guild.Id, fromUtcInclusive, toUtcExclusive);
        var lookbackVoiceChannelsTask = _db.ListVoiceChannelDurationSummaryAsync(guild.Id, fromUtcInclusive, toUtcExclusive, topCount: 5);
        var lookbackMessageRowsTask = _db.ListMessageCountsByChannelAsync(guild.Id, fromUtcInclusive, toUtcExclusive);
        var lookbackVoiceTopUsersTask = _db.ListVoiceDurationSummaryAsync(guild.Id, fromUtcInclusive, toUtcExclusive, topCount: 5);

        var recentFromUtc = toUtcExclusive.AddDays(-OpsKpiRecentWindowDays);
        var baselineFromUtc = recentFromUtc.AddDays(-OpsKpiBaselineWindowDays);
        var recentMessageRowsTask = _db.ListMessageCountsByChannelAsync(guild.Id, recentFromUtc, toUtcExclusive);
        var baselineMessageRowsTask = _db.ListMessageCountsByChannelAsync(guild.Id, baselineFromUtc, recentFromUtc);
        var recentReactionRowsTask = _db.ListReactionCountsByChannelAsync(guild.Id, recentFromUtc, toUtcExclusive);
        var baselineReactionRowsTask = _db.ListReactionCountsByChannelAsync(guild.Id, baselineFromUtc, recentFromUtc);
        var recentVoiceRowsTask = _db.ListVoiceHourlyActivityAsync(guild.Id, recentFromUtc, toUtcExclusive);
        var baselineVoiceRowsTask = _db.ListVoiceHourlyActivityAsync(guild.Id, baselineFromUtc, recentFromUtc);

        var memberSnapshotIncomplete = false;
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch
        {
            memberSnapshotIncomplete = true;
        }

        var members = guild.Users
            .Where(user => !user.IsBot)
            .Select(user => new OpsMemberJoin(user.Id, user.JoinedAt ?? user.CreatedAt))
            .ToArray();

        var joined30FromUtc = toUtcExclusive.AddDays(-30);
        var joined60FromUtc = toUtcExclusive.AddDays(-60);
        var joined7CutoffUtc = toUtcExclusive.AddDays(-7);
        var joined30CutoffUtc = toUtcExclusive.AddDays(-30);
        var cohortWindowFromUtc = toUtcExclusive.AddDays(-84);

        var joinedLast30 = members
            .Where(member => member.JoinedAt >= joined30FromUtc)
            .ToArray();
        var cohortMembers = members
            .Where(member => member.JoinedAt >= cohortWindowFromUtc)
            .ToArray();
        var retention7Cohort = joinedLast30
            .Where(member => member.JoinedAt <= joined7CutoffUtc)
            .ToArray();
        var retention30Cohort = members
            .Where(member => member.JoinedAt >= joined60FromUtc && member.JoinedAt < joined30CutoffUtc)
            .ToArray();

        var timelineUserIds = joinedLast30
            .Select(member => member.UserId)
            .Concat(retention30Cohort.Select(member => member.UserId))
            .Concat(cohortMembers.Select(member => member.UserId))
            .Distinct()
            .ToArray();
        var timelineTask = _db.ListUserActivityTimelinesAsync(guild.Id, timelineUserIds);

        await Task.WhenAll(
            lookbackVoiceHourlyTask,
            lookbackVoiceWeekdayHourlyTask,
            lookbackMessageWeekdayHourlyTask,
            lookbackVoiceChannelsTask,
            lookbackMessageRowsTask,
            lookbackVoiceTopUsersTask,
            recentMessageRowsTask,
            baselineMessageRowsTask,
            recentReactionRowsTask,
            baselineReactionRowsTask,
            recentVoiceRowsTask,
            baselineVoiceRowsTask,
            timelineTask);

        var timelineByUser = timelineTask.Result
            .ToDictionary(row => ToUlong(row.UserId));
        var firstMessage24h = joinedLast30.Count(member =>
            timelineByUser.TryGetValue(member.UserId, out var row) &&
            IsFirstActivityWithin(row.FirstMessageAt, member.JoinedAt, TimeSpan.FromHours(24)));
        var firstVoice72h = joinedLast30.Count(member =>
            timelineByUser.TryGetValue(member.UserId, out var row) &&
            IsFirstActivityWithin(row.FirstVoiceAt, member.JoinedAt, TimeSpan.FromHours(72)));
        var retained7 = retention7Cohort.Count(member =>
            timelineByUser.TryGetValue(member.UserId, out var row) &&
            row.LastActivityAt.HasValue &&
            row.LastActivityAt.Value >= member.JoinedAt.AddDays(7));
        var retained30 = retention30Cohort.Count(member =>
            timelineByUser.TryGetValue(member.UserId, out var row) &&
            row.LastActivityAt.HasValue &&
            row.LastActivityAt.Value >= member.JoinedAt.AddDays(30));

        var retentionLines = new List<string>
        {
            $"新規参加(直近30日): {joinedLast30.Length:N0}人",
            $"初回発言24h率: {firstMessage24h:N0}/{joinedLast30.Length:N0} ({FormatRatePercent(firstMessage24h, joinedLast30.Length)})",
            $"初回VC72h率: {firstVoice72h:N0}/{joinedLast30.Length:N0} ({FormatRatePercent(firstVoice72h, joinedLast30.Length)})",
            $"7日継続率(加入7-30日): {retained7:N0}/{retention7Cohort.Length:N0} ({FormatRatePercent(retained7, retention7Cohort.Length)})",
            $"30日継続率(加入30-60日): {retained30:N0}/{retention30Cohort.Length:N0} ({FormatRatePercent(retained30, retention30Cohort.Length)})",
            "継続判定: メッセージ/VC/リアクションの最終活動時刻"
        };
        if (memberSnapshotIncomplete)
        {
            retentionLines.Add("注記: メンバー一覧の完全取得に失敗したため、人数は少なめに出る場合があります。");
        }

        var cohortLines = BuildCohortRetentionLines(cohortMembers, timelineByUser, toUtcExclusive, maxRows: 6);
        if (memberSnapshotIncomplete)
        {
            cohortLines.Add("注記: メンバー一覧の完全取得に失敗したため、人数は少なめに出る場合があります。");
        }

        var lookbackVoiceHourly = BuildHourlyVoiceActivity(lookbackVoiceHourlyTask.Result);
        var totalVoiceSeconds = Math.Max(0L, lookbackVoiceHourly.Sum(row => row.TotalSeconds));
        var topHours = lookbackVoiceHourly
            .Where(row => row.TotalSeconds > 0)
            .OrderByDescending(row => row.TotalSeconds)
            .ThenBy(row => row.Hour)
            .Take(3)
            .ToArray();
        var topHourSeconds = topHours.Sum(row => row.TotalSeconds);
        var topHourShare = totalVoiceSeconds <= 0 ? 0d : topHourSeconds / (double)totalVoiceSeconds;
        var topHourText = topHours.Length == 0
            ? "-"
            : string.Join(" / ", topHours.Select(row =>
                $"{FormatHourRangeLabel(row.Hour)} {FormatDuration((int)Math.Min(int.MaxValue, row.TotalSeconds))}"));

        var topChannels = lookbackVoiceChannelsTask.Result
            .Take(3)
            .ToArray();
        var topChannelSeconds = topChannels.Sum(row => Math.Max(0L, row.DurationSeconds));
        var topChannelShare = totalVoiceSeconds <= 0 ? 0d : topChannelSeconds / (double)totalVoiceSeconds;
        var topChannelText = topChannels.Length == 0
            ? "-"
            : string.Join(" / ", topChannels.Select(row =>
                $"{FormatChannelMention(guild, row.ChannelId)} {FormatDuration((int)Math.Min(int.MaxValue, row.DurationSeconds))}"));

        var vcLines = totalVoiceSeconds <= 0
            ? new List<string> { $"期間({lookbackDays}日)にVCログがありません。" }
            : new List<string>
            {
                $"VC稼働時間({lookbackDays}日): {FormatDuration((int)Math.Min(int.MaxValue, totalVoiceSeconds))}",
                $"上位3時間帯占有率: {FormatPercent(topHourShare)}",
                $"上位時間帯: {topHourText}",
                $"上位3VC占有率: {FormatPercent(topChannelShare)}",
                $"上位VC: {topChannelText}"
            };

        var activeTimeLines = BuildWeekdayHourActivityLines(
            lookbackMessageWeekdayHourlyTask.Result,
            lookbackVoiceWeekdayHourlyTask.Result);

        var baselineBucketCount = Math.Max(1, OpsKpiBaselineWindowDays / OpsKpiRecentWindowDays);
        var recentMessageTotal = recentMessageRowsTask.Result.Sum(row => (long)row.MessageCount);
        var baselineMessageAvg = baselineMessageRowsTask.Result.Sum(row => (long)row.MessageCount) / (double)baselineBucketCount;
        var recentReactionTotal = recentReactionRowsTask.Result.Sum(row => (long)row.ReactionCount);
        var baselineReactionAvg = baselineReactionRowsTask.Result.Sum(row => (long)row.ReactionCount) / (double)baselineBucketCount;
        var recentVoiceTotal = recentVoiceRowsTask.Result.Sum(row => Math.Max(0L, row.TotalSeconds));
        var baselineVoiceAvg = baselineVoiceRowsTask.Result.Sum(row => Math.Max(0L, row.TotalSeconds)) / (double)baselineBucketCount;

        var (messageTrendLine, messageAlert) = BuildDropTrendLine("発言", recentMessageTotal, baselineMessageAvg);
        var (reactionTrendLine, reactionAlert) = BuildDropTrendLine("リアクション", recentReactionTotal, baselineReactionAvg);
        var (voiceTrendLine, voiceAlert) = BuildDropTrendLine("VC滞在(秒)", recentVoiceTotal, baselineVoiceAvg);
        var alertCount = (messageAlert ? 1 : 0) + (reactionAlert ? 1 : 0) + (voiceAlert ? 1 : 0);
        var dropSummary = alertCount switch
        {
            0 => "✅ 急落なし",
            1 => "⚠️ 一部で急落傾向あり",
            _ => "🚨 急落注意（複数指標が30%以上低下）"
        };
        var alertLines = new List<string>
        {
            $"判定窓: 直近{OpsKpiRecentWindowDays}日 vs 直前{OpsKpiBaselineWindowDays}日平均",
            dropSummary,
            messageTrendLine,
            reactionTrendLine,
            voiceTrendLine
        };

        var lookbackMessageRows = lookbackMessageRowsTask.Result;
        var totalMessages = lookbackMessageRows.Sum(row => (long)row.MessageCount);
        var messageTopUsers = lookbackMessageRows
            .GroupBy(row => row.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Sum(row => (long)row.MessageCount) })
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.UserId)
            .Take(5)
            .ToArray();
        var topMessageTotal = messageTopUsers.Sum(row => row.Count);
        var messageTopShare = totalMessages <= 0 ? 0d : topMessageTotal / (double)totalMessages;
        var messageTopLine = messageTopUsers.Length == 0
            ? "-"
            : string.Join(" / ", messageTopUsers.Select(row => $"<@{ToUlong(row.UserId)}> {row.Count:N0}件"));

        var voiceTopUsers = lookbackVoiceTopUsersTask.Result;
        var topVoiceTotal = voiceTopUsers.Sum(row => Math.Max(0L, row.DurationSeconds));
        var voiceTopShare = totalVoiceSeconds <= 0 ? 0d : topVoiceTotal / (double)totalVoiceSeconds;
        var voiceTopLine = voiceTopUsers.Count == 0
            ? "-"
            : string.Join(" / ", voiceTopUsers.Select(row =>
                $"<@{ToUlong(row.UserId)}> {FormatDuration((int)Math.Min(int.MaxValue, Math.Max(0L, row.DurationSeconds)))}"));

        var dependencyLines = new List<string>
        {
            $"発言TOP5占有率: {FormatPercent(messageTopShare)} (全{totalMessages:N0}件)",
            $"VCTOP5占有率: {FormatPercent(voiceTopShare)} (全{FormatDuration((int)Math.Min(int.MaxValue, totalVoiceSeconds))})",
            $"発言TOP5: {messageTopLine}",
            $"VCTOP5: {voiceTopLine}"
        };

        var embedColor = alertCount >= 2 ? Color.Red : alertCount == 1 ? Color.Orange : Color.Blue;
        var embed = new EmbedBuilder()
            .WithTitle("運用KPIレポート (1/2/3/4/5/6)")
            .WithColor(embedColor)
            .WithCurrentTimestamp()
            .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
            .AddField("1) 新規定着", JoinWithinLimit(retentionLines, 980), false)
            .AddField("2) VC稼働偏り", JoinWithinLimit(vcLines, 980), false)
            .AddField("3) 新規定着率コホート", JoinWithinLimit(cohortLines, 980), false)
            .AddField("4) 曜日×時間アクティブ", JoinWithinLimit(activeTimeLines, 980), false)
            .AddField("5) 活動急落アラート", JoinWithinLimit(alertLines, 980), false)
            .AddField("6) コアメンバー依存", JoinWithinLimit(dependencyLines, 980), false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowRecruitKpiAsync(int days)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var lookbackDays = Math.Clamp(days, RecruitKpiMinDays, RecruitKpiMaxDays);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);
        var summaries = await _db.ListRecruitPostSummariesAsync(Context.Guild.Id);
        var rows = summaries
            .Where(row => row.CreatedAt >= fromUtcInclusive && row.CreatedAt < toUtcExclusive)
            .ToArray();

        if (rows.Length == 0)
        {
            var noDataEmbed = new EmbedBuilder()
                .WithTitle("募集KPI")
                .WithColor(Color.DarkGrey)
                .WithCurrentTimestamp()
                .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
                .AddField("結果", "対象期間に募集データがありません。", false)
                .Build();

            await FollowupAsync(embed: noDataEmbed, ephemeral: true);
            return;
        }

        var total = rows.Length;
        var openCount = rows.Count(row => row.Status == RecruitPostStatus.Open);
        var fullCount = rows.Count(row => row.Status == RecruitPostStatus.Full);
        var closedCount = rows.Count(row => row.Status == RecruitPostStatus.Closed);
        var canceledCount = rows.Count(row => row.Status == RecruitPostStatus.Canceled);
        var inProgressCount = openCount + fullCount;

        var totalSlots = rows.Sum(row => Math.Max(0, row.SlotCount));
        var totalFilled = rows.Sum(row => Math.Clamp(row.FilledCount, 0, Math.Max(0, row.SlotCount)));
        var slotFillRate = totalSlots <= 0 ? 0d : totalFilled / (double)totalSlots;

        var filledRows = rows
            .Where(row => row.SlotCount > 0 && row.FilledCount >= row.SlotCount && row.Status != RecruitPostStatus.Canceled)
            .ToArray();
        var fullyFilledCount = filledRows.Length;
        var fullyFilledRate = fullyFilledCount / (double)total;
        var cancelRate = canceledCount / (double)total;
        var partialCloseCount = rows.Count(row =>
            row.Status == RecruitPostStatus.Closed &&
            row.SlotCount > 0 &&
            row.FilledCount < row.SlotCount);

        var avgFillSeconds = filledRows.Length == 0
            ? 0d
            : filledRows.Average(row => Math.Max(0d, (row.UpdatedAt - row.CreatedAt).TotalSeconds));
        var medianFillSeconds = filledRows.Length == 0
            ? 0
            : (int)Math.Round(filledRows
                .Select(row => Math.Max(0d, (row.UpdatedAt - row.CreatedAt).TotalSeconds))
                .OrderBy(seconds => seconds)
                .ElementAt(filledRows.Length / 2));

        var topCreators = rows
            .GroupBy(row => row.CreatorUserId)
            .Select(group => new
            {
                CreatorUserId = group.Key,
                Count = group.Count()
            })
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.CreatorUserId)
            .Take(3)
            .Select(row => $"<@{ToUlong(row.CreatorUserId)}> {row.Count}件")
            .ToArray();

        var throughputLines = new List<string>
        {
            $"作成数: {total:N0}件",
            $"進行中: {inProgressCount:N0}件 / 終了: {closedCount:N0}件 / 中断: {canceledCount:N0}件",
            $"満員到達(件数): {fullyFilledCount:N0}/{total:N0} ({FormatPercent(fullyFilledRate)})",
            $"充足率(スロット): {totalFilled:N0}/{totalSlots:N0} ({FormatPercent(slotFillRate)})",
            $"平均充足時間(近似): {(filledRows.Length == 0 ? "-" : FormatDuration((int)Math.Min(int.MaxValue, Math.Round(avgFillSeconds))))}",
            $"中央値(近似): {(filledRows.Length == 0 ? "-" : FormatDuration(medianFillSeconds))}"
        };

        var riskLines = new List<string>
        {
            $"中断率: {canceledCount:N0}/{total:N0} ({FormatPercent(cancelRate)})",
            $"未充足クローズ: {partialCloseCount:N0}件",
            $"主催上位3名: {(topCreators.Length == 0 ? "-" : string.Join(" / ", topCreators))}",
            "充足時間は `updated_at - created_at` の近似値です。"
        };

        var color = cancelRate >= 0.4 ? Color.Red : cancelRate >= 0.2 ? Color.Orange : Color.Blue;
        var embed = new EmbedBuilder()
            .WithTitle("募集KPI")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
            .AddField("募集の流量/充足", JoinWithinLimit(throughputLines, 980), false)
            .AddField("運用リスク", JoinWithinLimit(riskLines, 980), false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowVoiceHourlyStatsAsync(int days)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var lookbackDays = Math.Clamp(days, 1, VoiceHourlyStatsMaxDays);
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-lookbackDays);
        var currentHourJst = toUtcExclusive.ToOffset(JapanTimeOffset).Hour;
        var rows = await _db.ListVoiceHourlyActivityAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive);
        var weekdayRows = await _db.ListVoiceWeekdayHourlyActivityAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive);
        var hourRegulars = await _db.ListVoiceTopUsersByHourAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive, currentHourJst, topCount: 4);
        var regularChartItems = await BuildVoiceRegularChartItemsAsync(hourRegulars);
        var hourly = BuildHourlyVoiceActivity(rows);
        var weekdayHourSeconds = BuildWeekdayHourSecondsMatrix(weekdayRows);
        var totalSeconds = hourly.Sum(x => x.TotalSeconds);

        if (totalSeconds <= 0)
        {
            var noDataEmbed = new EmbedBuilder()
                .WithTitle("VC時間帯統計")
                .WithColor(Color.DarkGrey)
                .WithCurrentTimestamp()
                .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
                .AddField("結果", "VCログがないため集計できませんでした。", false)
                .Build();

            await FollowupAsync(embed: noDataEmbed, ephemeral: true);
            return;
        }

        var peakByTime = hourly
            .OrderByDescending(x => x.TotalSeconds)
            .ThenByDescending(x => x.ActiveUsers)
            .ThenBy(x => x.Hour)
            .First();
        var peakByUsers = hourly
            .OrderByDescending(x => x.ActiveUsers)
            .ThenByDescending(x => x.TotalSeconds)
            .ThenBy(x => x.Hour)
            .First();
        var topTimeHours = hourly
            .Where(x => x.TotalSeconds > 0)
            .OrderByDescending(x => x.TotalSeconds)
            .ThenBy(x => x.Hour)
            .Take(3)
            .Select(x => $"{FormatHourRangeLabel(x.Hour)} ({FormatDuration((int)Math.Min(int.MaxValue, x.TotalSeconds))})")
            .ToArray();
        var topUserHours = hourly
            .Where(x => x.ActiveUsers > 0)
            .OrderByDescending(x => x.ActiveUsers)
            .ThenByDescending(x => x.TotalSeconds)
            .ThenBy(x => x.Hour)
            .Take(3)
            .Select(x => $"{FormatHourRangeLabel(x.Hour)} ({x.ActiveUsers}人)")
            .ToArray();
        var hotSlots = weekdayRows
            .Where(x => x.TotalSeconds > 0)
            .OrderByDescending(x => x.TotalSeconds)
            .ThenBy(x => x.Weekday)
            .ThenBy(x => x.Hour)
            .Take(3)
            .Select(x => $"{FormatWeekdayHourLabel(x.Weekday, x.Hour)} ({FormatDuration((int)Math.Min(int.MaxValue, x.TotalSeconds))})")
            .ToArray();
        var avgDailySeconds = totalSeconds / Math.Max(1, lookbackDays);
        var summaryLines = new List<string>
        {
            $"🕘 VC滞在ピーク: {FormatHourRangeLabel(peakByTime.Hour)} ({FormatDuration((int)Math.Min(int.MaxValue, peakByTime.TotalSeconds))})",
            $"👥 参加人数ピーク: {FormatHourRangeLabel(peakByUsers.Hour)} ({peakByUsers.ActiveUsers}人)",
            $"📈 よく集まる時間帯(滞在): {(topTimeHours.Length == 0 ? "-" : string.Join(", ", topTimeHours))}",
            $"🧩 よく集まる時間帯(人数): {(topUserHours.Length == 0 ? "-" : string.Join(", ", topUserHours))}",
            $"🗓️ 混雑スロット(曜日×時間): {(hotSlots.Length == 0 ? "-" : string.Join(", ", hotSlots))}",
            BuildHourRegularsLine(hourRegulars, currentHourJst),
            $"⏱️ 1日あたりVC稼働時間: {FormatDuration((int)Math.Min(int.MaxValue, avgDailySeconds))}"
        };
        var chartPng = await BuildVoiceHourlyChartPngAsync(hourly, weekdayHourSeconds, lookbackDays, currentHourJst, regularChartItems);
        var chartFileName = $"voice_hourly_{Context.Guild.Id}_{toUtcExclusive:yyyyMMddHHmm}.png";

        var embedBuilder = new EmbedBuilder()
            .WithTitle("VC時間帯統計")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("期間", $"{lookbackDays}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", false)
            .AddField("サマリー", JoinWithinLimit(summaryLines, 980), false);

        if (chartPng is not null && chartPng.Length > 0)
        {
            embedBuilder.WithImageUrl($"attachment://{chartFileName}");
            var embed = embedBuilder.Build();
            await using var stream = new MemoryStream(chartPng, writable: false);
            await FollowupWithFileAsync(stream, chartFileName, embed: embed, ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embedBuilder.Build(), ephemeral: true);
    }

    private async Task ShowVoiceSessionIntegrityAsync(bool repair)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        var nowUtc = DateTimeOffset.UtcNow;
        var result = await _voiceSessionReconciliation.ReconcileGuildAsync(guild, repair, nowUtc);
        var hasIssue = result.HasPlannedActions || result.LongOpenSessionCount > 0;
        var hasChange = result.HasAppliedChanges;
        var color = hasIssue
            ? (hasChange ? Color.Orange : Color.Red)
            : Color.Green;

        var lines = new List<string>
        {
            $"Openセッション: {result.OpenSessionCount:N0}",
            $"実VC参加者: {result.CurrentVoiceUserCount:N0}",
            $"要修復(重複): {result.DuplicateOpenSessionCount:N0}",
            $"要修復(退出漏れ): {result.StaleSessionCount:N0}",
            $"要修復(チャンネル不一致): {result.MismatchedChannelSessionCount:N0}",
            $"要修復(開始漏れ): {result.MissingOpenSessionCount:N0}",
            $"24h超Open警告: {result.LongOpenSessionCount:N0}",
            $"適用(重複クローズ): {result.ClosedDuplicateSessions:N0}",
            $"適用(退出漏れクローズ): {result.ClosedStaleSessions:N0}",
            $"適用(不一致クローズ): {result.ClosedMismatchedSessions:N0}",
            $"適用(開始漏れ補完): {result.StartedMissingSessions:N0}"
        };

        var status = hasIssue
            ? (hasChange ? "修復を適用しました。" : "要修復項目があります。")
            : "異常は検出されませんでした。";

        var embed = new EmbedBuilder()
            .WithTitle("VCセッション整合性")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("状態", status, false)
            .AddField("集計", JoinWithinLimit(lines, 980), false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowHealthAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        var checks = new List<HealthCheckItem>();
        GuildSettings? settings = null;

        try
        {
            settings = await _db.GetGuildSettingsAsync(guild.Id);
            checks.Add(HealthCheckItem.Ok("DB接続", "OK"));
        }
        catch (Exception ex)
        {
            checks.Add(HealthCheckItem.Error("DB接続", $"失敗: {ex.Message}"));
        }

        var botUser = guild.CurrentUser;
        var missingPermissions = new List<string>();
        if (!botUser.GuildPermissions.ManageRoles) missingPermissions.Add("ManageRoles");
        if (!botUser.GuildPermissions.ManageChannels) missingPermissions.Add("ManageChannels");
        if (!botUser.GuildPermissions.ManageNicknames) missingPermissions.Add("ManageNicknames");
        if (!botUser.GuildPermissions.ReadMessageHistory) missingPermissions.Add("ReadMessageHistory");
        if (!botUser.GuildPermissions.SendMessages) missingPermissions.Add("SendMessages");
        var autoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        if (autoKickEnabled && !botUser.GuildPermissions.KickMembers) missingPermissions.Add("KickMembers");

        if (missingPermissions.Count == 0)
        {
            checks.Add(HealthCheckItem.Ok("権限", "主要権限は満たしています。"));
        }
        else
        {
            checks.Add(HealthCheckItem.Error("権限", $"不足: {string.Join(", ", missingPermissions)}"));
        }

        var logChannel = FindTextChannelByName(guild, _options.LogChannelName);
        checks.Add(logChannel is null
            ? HealthCheckItem.Warn("logチャンネル", $"未検出: {_options.LogChannelName}")
            : HealthCheckItem.Ok("logチャンネル", $"OK: <#{logChannel.Id}>"));

        var notifyChannel = FindTextChannelByName(guild, _options.NotificationChannelName);
        checks.Add(notifyChannel is null
            ? HealthCheckItem.Warn("通知チャンネル", $"未検出: {_options.NotificationChannelName}")
            : HealthCheckItem.Ok("通知チャンネル", $"OK: <#{notifyChannel.Id}>"));

        var controlChannel = FindTextChannelByName(guild, _options.ControlChannelName);
        checks.Add(controlChannel is null
            ? HealthCheckItem.Warn("コントロールチャンネル", $"未検出: {_options.ControlChannelName}")
            : HealthCheckItem.Ok("コントロールチャンネル", $"OK: <#{controlChannel.Id}>"));

        var adminRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.AdminRoleName, StringComparison.OrdinalIgnoreCase));
        checks.Add(adminRole is null
            ? HealthCheckItem.Warn("管理者ロール", $"未検出: {_options.AdminRoleName}")
            : HealthCheckItem.Ok("管理者ロール", $"OK: <@&{adminRole.Id}>"));

        var memberRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));
        checks.Add(memberRole is null
            ? HealthCheckItem.Warn("メンバーロール", $"未検出: {_options.MemberRoleName}")
            : HealthCheckItem.Ok("メンバーロール", $"OK: <@&{memberRole.Id}>"));

        if (settings is not null)
        {
            checks.Add(settings.LogChannelId == 0
                ? HealthCheckItem.Warn("設定LogChannelId", "未設定")
                : HealthCheckItem.Ok("設定LogChannelId", settings.LogChannelId.ToString()));
            checks.Add(settings.ControlChannelId == 0
                ? HealthCheckItem.Warn("設定ControlChannelId", "未設定")
                : HealthCheckItem.Ok("設定ControlChannelId", settings.ControlChannelId.ToString()));
        }

        var startupReady = _runtimeDiagnostics.Get("startup.ready.end");
        checks.Add(startupReady is null
            ? HealthCheckItem.Warn("起動復旧確認", "まだ記録がありません（起動直後の可能性）")
            : HealthCheckItem.Ok("起動復旧確認", FormatHeartbeatSummary(startupReady)));

        checks.Add(BuildLoopHealthItem("定期処理/Cleanup", "loop.cleanup"));
        if (_options.RecruitFeatureEnabled)
        {
            checks.Add(BuildLoopHealthItem("定期処理/募集Schedule", "loop.recruit_schedule"));
        }
        checks.Add(BuildLoopHealthItem("定期処理/ActivityRole", "loop.activity_role_sync"));
        checks.Add(BuildLoopHealthItem("定期処理/VC整合性", "loop.voice_session_reconcile"));

        var hasError = checks.Any(x => x.Level == "ERROR");
        var hasWarn = checks.Any(x => x.Level == "WARN");
        var overall = hasError ? "ERROR" : hasWarn ? "WARN" : "OK";
        var color = hasError ? Color.Red : hasWarn ? Color.Orange : Color.Green;
        var summaryLines = checks.Select(x => $"[{x.Level}] {x.Name}: {x.Message}");
        var summary = JoinWithinLimit(summaryLines, 950);
        var tuning = _runtimeTuning.GetSnapshot();
        var cleanupIntervalMinutes = tuning.CleanupIntervalMinutes;
        var recruitCleanupPolicy = _options.RecruitFeatureEnabled
            ? $"終了=開始+{tuning.RecruitClosedRetentionHours}h / 中断=中断+{tuning.RecruitCanceledRetentionHours}h"
            : "募集機能OFF";
        var logRetentionPolicy = $"msg:{(tuning.MessageLogRetentionDays <= 0 ? "OFF" : $"{tuning.MessageLogRetentionDays}d")} / vc:{(tuning.VoiceSessionRetentionDays <= 0 ? "OFF" : $"{tuning.VoiceSessionRetentionDays}d")} / react:{(tuning.ReactionLogRetentionDays <= 0 ? "OFF" : $"{tuning.ReactionLogRetentionDays}d")}";
        var embed = new EmbedBuilder()
            .WithTitle("Bot Health Check")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("総合", overall, true)
            .AddField("自動キック", autoKickEnabled ? "ON" : "OFF", true)
            .AddField("対象Guild", $"{guild.Name} ({guild.Id})", true)
            .AddField("Cleanup間隔", $"{cleanupIntervalMinutes}分", true)
            .AddField("募集Cleanup", recruitCleanupPolicy, true)
            .AddField("ログ保持", logRetentionPolicy, true)
            .AddField("修復導線", "ADMINパネルの `パネル再同期` を使用", true)
            .AddField("詳細", string.IsNullOrWhiteSpace(summary) ? "なし" : summary, false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task<Embed> BuildRuntimeTuningEmbedAsync(ulong guildId)
    {
        var tuning = _runtimeTuning.GetSnapshot();
        var inactiveKickRetentionDays = await ResolveInactiveKickDataRetentionDaysAsync(guildId);
        return new EmbedBuilder()
            .WithTitle("運用設定")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("Cleanup間隔", $"{tuning.CleanupIntervalMinutes}分", true)
            .AddField("募集保持(終了)", $"{tuning.RecruitClosedRetentionHours}時間", true)
            .AddField("募集保持(中断)", $"{tuning.RecruitCanceledRetentionHours}時間", true)
            .AddField("メッセージ保持", tuning.MessageLogRetentionDays <= 0 ? "無効" : $"{tuning.MessageLogRetentionDays}日", true)
            .AddField("VC保持", tuning.VoiceSessionRetentionDays <= 0 ? "無効" : $"{tuning.VoiceSessionRetentionDays}日", true)
            .AddField("リアクション保持", tuning.ReactionLogRetentionDays <= 0 ? "無効" : $"{tuning.ReactionLogRetentionDays}日", true)
            .AddField("自動キック後ユーザーデータ保持", $"{inactiveKickRetentionDays}日", true)
            .AddField("保存", "DB保存（再起動後も維持）", false)
            .Build();
    }

    private async Task<int> ResolveInactiveKickDataRetentionDaysAsync(ulong guildId)
    {
        var settings = await _db.GetGuildSettingsAsync(guildId);
        if (settings?.InactiveAutoKickUserDataRetentionDays is int perGuild)
        {
            return Math.Clamp(perGuild, 1, 30);
        }

        return Math.Clamp(_options.InactiveAutoKickUserDataRetentionDays <= 0 ? 7 : _options.InactiveAutoKickUserDataRetentionDays, 1, 30);
    }

    private async Task ShowIntegrityCheckAsync(bool repair)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        var lines = new List<string>();
        var errors = 0;
        var warns = 0;
        var repaired = 0;

        var recruitSummaries = await _db.ListRecruitPostSummariesAsync(guild.Id);
        var recruitPosts = new List<RecruitPost>();
        foreach (var summary in recruitSummaries)
        {
            var post = await _db.GetRecruitPostByMessageIdAsync(ToUlong(summary.MessageId));
            if (post is not null)
            {
                recruitPosts.Add(post);
            }
        }

        var staleRecruitThreadRefs = 0;
        foreach (var post in recruitPosts.Where(p => p.ThreadChannelId.HasValue && p.ThreadChannelId.Value != 0))
        {
            var threadId = ToUlong(post.ThreadChannelId!.Value);
            var exists = guild.GetThreadChannel(threadId) is not null;
            if (!exists)
            {
                try
                {
                    exists = (await Context.Client.Rest.GetChannelAsync(threadId)) is IThreadChannel;
                }
                catch
                {
                    exists = false;
                }
            }

            if (exists)
            {
                continue;
            }

            staleRecruitThreadRefs++;
            if (repair)
            {
                await _db.UpdateRecruitThreadAsync(post.Id, null, null);
                repaired++;
            }
        }

        if (staleRecruitThreadRefs > 0)
        {
            warns++;
            lines.Add($"[WARN] 募集スレッド参照切れ: {staleRecruitThreadRefs}件{(repair ? "（DB参照を解除）" : string.Empty)}");
        }
        else
        {
            lines.Add("[OK] 募集スレッド参照切れ: なし");
        }

        var missingRecruitMessages = 0;
        foreach (var post in recruitPosts.Take(100))
        {
            var channel = guild.GetTextChannel(ToUlong(post.ChannelId));
            if (channel is null)
            {
                missingRecruitMessages++;
                continue;
            }

            try
            {
                var message = await channel.GetMessageAsync(ToUlong(post.MessageId));
                if (message is null)
                {
                    missingRecruitMessages++;
                }
            }
            catch
            {
                missingRecruitMessages++;
            }
        }

        if (missingRecruitMessages > 0)
        {
            warns++;
            lines.Add($"[WARN] 募集メッセージ参照切れ: {missingRecruitMessages}件（先頭100件確認）");
        }
        else
        {
            lines.Add("[OK] 募集メッセージ参照切れ: なし（確認範囲内）");
        }

        var inquiryTickets = await _db.ListInquiryTicketsAsync(guild.Id, includeClosed: false, maxRows: 200);
        var missingInquiryChannels = inquiryTickets.Count(ticket => guild.GetTextChannel(ToUlong(ticket.ChannelId)) is null);
        if (missingInquiryChannels > 0)
        {
            warns++;
            lines.Add($"[WARN] お問い合わせチャンネル参照切れ: {missingInquiryChannels}件");
        }
        else
        {
            lines.Add("[OK] お問い合わせチャンネル参照切れ: なし");
        }

        var lotteries = await _db.ListRecentLotteriesAsync(guild.Id, 100);
        var missingLotteryChannels = 0;
        foreach (var lottery in lotteries)
        {
            var channelId = ToUlong(lottery.ChannelId);
            if (!lottery.MessageId.HasValue || lottery.MessageId.Value == 0)
            {
                missingLotteryChannels++;
                continue;
            }

            var messageId = ToUlong(lottery.MessageId.Value);
            var channel = guild.GetTextChannel(channelId);
            if (channel is null)
            {
                missingLotteryChannels++;
                continue;
            }

            try
            {
                var msg = await channel.GetMessageAsync(messageId);
                if (msg is null)
                {
                    missingLotteryChannels++;
                }
            }
            catch
            {
                missingLotteryChannels++;
            }
        }
        if (missingLotteryChannels > 0)
        {
            warns++;
            lines.Add($"[WARN] 抽選メッセージ/チャンネル参照切れ: {missingLotteryChannels}件（直近100件）");
        }
        else
        {
            lines.Add("[OK] 抽選メッセージ/チャンネル参照切れ: なし（確認範囲内）");
        }

        if (repair)
        {
            try
            {
                if (Context.Channel is IMessageChannel fallbackChannel)
                {
                    await _recruitListService.RefreshAsync(guild, fallbackChannel);
                    lines.Add("[OK] 募集一覧を再生成しました。");
                }
            }
            catch (Exception ex)
            {
                warns++;
                lines.Add($"[WARN] 募集一覧再生成失敗: {ex.Message}");
            }
        }

        var color = errors > 0 ? Color.Red : warns > 0 ? Color.Orange : Color.Green;
        var embed = new EmbedBuilder()
            .WithTitle(repair ? "整合性チェック（簡易修復あり）" : "整合性チェック")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("結果", errors > 0 ? "ERROR" : warns > 0 ? "WARN" : "OK", true)
            .AddField("募集件数", recruitPosts.Count.ToString(), true)
            .AddField("修復数", repaired.ToString(), true)
            .AddField("詳細", JoinWithinLimit(lines, 1000), false)
            .AddField("修復の範囲", "現在は `募集スレッド参照切れのDB解除` と `募集一覧再生成` のみ自動対応", false)
            .Build();

        if (repair)
        {
            await _audit.SendAsync(
                guild,
                "整合性チェック（簡易修復）",
                $"👤 実行者: {Context.User.Mention} ({Context.User})\n🛠️ 修復数: {repaired}\n⚠️ 警告: {warns}\n❌ エラー: {errors}",
                color,
                Context.User);
        }

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private HealthCheckItem BuildLoopHealthItem(string name, string key)
    {
        var heartbeat = _runtimeDiagnostics.Get(key);
        if (heartbeat is null)
        {
            return HealthCheckItem.Warn(name, "記録なし");
        }

        var age = DateTimeOffset.UtcNow - heartbeat.LastRunAtUtc;
        if (age > TimeSpan.FromHours(1))
        {
            return HealthCheckItem.Warn(name, $"{FormatHeartbeatSummary(heartbeat)} / 遅延の可能性");
        }

        return heartbeat.LastSuccess
            ? HealthCheckItem.Ok(name, FormatHeartbeatSummary(heartbeat))
            : HealthCheckItem.Error(name, FormatHeartbeatSummary(heartbeat));
    }

    private static string FormatHeartbeatSummary(RuntimeHeartbeat heartbeat)
    {
        var ts = $"<t:{heartbeat.LastRunAtUtc.ToUnixTimeSeconds()}:R>";
        return string.IsNullOrWhiteSpace(heartbeat.LastNote) ? ts : $"{ts} ({heartbeat.LastNote})";
    }

    private async Task PreviewInactiveKickAsync(int top)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch
        {
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        var kickDays = Math.Max(1, _options.InactiveAutoKickDays);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        var todayJst = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset);
        var evaluationBaseJst = nowJst.Hour == 0 ? todayJst : todayJst.AddDays(1);
        var evaluationBaseUtc = evaluationBaseJst.ToUniversalTime();
        var kickCutoffUtc = evaluationBaseUtc.AddDays(-kickDays);

        var activeUserIds = await _db.ListActiveUserIdsSinceAsync(guild.Id, kickCutoffUtc);
        var exemptRoleNames = BuildInactiveKickExemptRoleNameSet(_options);

        var kickTargets = new List<SocketGuildUser>();
        var exemptUsers = new List<SocketGuildUser>();
        var permissionSkippedUsers = new List<SocketGuildUser>();
        var eligibleUsers = 0;
        var currentBotUser = guild.CurrentUser;

        foreach (var user in guild.Users.ToArray())
        {
            if (user.IsBot || user.Id == guild.OwnerId)
            {
                continue;
            }

            eligibleUsers++;
            var joinedAt = user.JoinedAt ?? user.CreatedAt;
            if (joinedAt > kickCutoffUtc || activeUserIds.Contains(user.Id))
            {
                continue;
            }

            if (IsInactiveKickExemptUser(user, exemptRoleNames))
            {
                exemptUsers.Add(user);
                continue;
            }

            if (CanKickMember(currentBotUser, user, guild.OwnerId))
            {
                kickTargets.Add(user);
                continue;
            }

            permissionSkippedUsers.Add(user);
        }

        var color = inactiveAutoKickEnabled ? Color.Orange : Color.DarkGrey;
        var stateText = inactiveAutoKickEnabled ? "ON" : "OFF";
        var embed = new EmbedBuilder()
            .WithTitle("自動キック候補プレビュー")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("設定", $"自動キック: **{stateText}** / 日数: **{kickDays}日**", false)
            .AddField("次回判定(JST)", $"{evaluationBaseJst:yyyy/MM/dd HH:mm}", true)
            .AddField("活動判定", "メッセージ / VC参加 / リアクション", true)
            .AddField("判定基準", $"{kickCutoffUtc.ToOffset(JapanTimeOffset):yyyy/MM/dd HH:mm} 以降の活動なし", true)
            .AddField("キック対象", $"{kickTargets.Count}人", true)
            .AddField("除外(管理者/除外ロール)", $"{exemptUsers.Count}人", true)
            .AddField("権限不足でスキップ", $"{permissionSkippedUsers.Count}人", true)
            .AddField($"対象一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(kickTargets, top), false)
            .AddField($"除外一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(exemptUsers, top), false)
            .AddField($"権限不足一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(permissionSkippedUsers, top), false)
            .WithFooter($"判定対象メンバー: {eligibleUsers}人 (Bot/オーナー除外)")
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowMemberManageInactiveListAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch
        {
        }

        var lookbackDays = GetMemberManageLookbackDays(guild.Id, Context.User.Id);
        var nowUtc = DateTimeOffset.UtcNow;
        var cutoffUtc = nowUtc.AddDays(-lookbackDays);
        var members = guild.Users
            .Where(user => !user.IsBot)
            .ToArray();
        var userIds = members
            .Select(user => user.Id)
            .ToArray();
        var timelineByUser = (await _db.ListUserActivityTimelinesAsync(guild.Id, userIds))
            .ToDictionary(row => ToUlong(row.UserId));

        var inactiveRows = members
            .Select(user =>
            {
                timelineByUser.TryGetValue(user.Id, out var timeline);
                var joinedAt = user.JoinedAt ?? user.CreatedAt;
                var lastActivityAt = timeline?.LastActivityAt;
                var isInactive = joinedAt <= cutoffUtc &&
                                 (!lastActivityAt.HasValue || lastActivityAt.Value < cutoffUtc);
                return new MemberManageInactiveRow(user, timeline, joinedAt, isInactive);
            })
            .Where(row => row.IsInactive)
            .OrderBy(row => row.Timeline?.LastActivityAt ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.JoinedAt)
            .ThenBy(row => row.User.Id)
            .ToArray();

        var embed = new EmbedBuilder()
            .WithTitle("メンバー管理: 最近活動していないメンバー")
            .WithColor(Color.DarkGrey)
            .WithCurrentTimestamp()
            .AddField("参照期間", $"直近{lookbackDays}日", true)
            .AddField("判定基準", $"{cutoffUtc.ToOffset(JapanTimeOffset):yyyy/MM/dd HH:mm} JST 以降の活動なし", false)
            .AddField("対象人数", $"{inactiveRows.Length}人", true)
            .AddField(
                $"一覧(最大 {MemberManageInactiveListMaxRows} 件)",
                BuildMemberManageInactiveListSection(inactiveRows, MemberManageInactiveListMaxRows),
                false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowRegistrationPendingListAsync(int maxRows)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch
        {
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var provisionalRole = ResolveProvisionalRole(guild, settings);
        if (provisionalRole is null)
        {
            await FollowupAsync("仮登録ロールが見つからないため、保留リストを作成できません。", ephemeral: true);
            return;
        }

        var memberRole = ResolveMemberRole(guild, settings);
        var pendingUsers = guild.Users
            .Where(user =>
                !user.IsBot &&
                user.Roles.Any(role => role.Id == provisionalRole.Id) &&
                (memberRole is null || user.Roles.All(role => role.Id != memberRole.Id)))
            .OrderBy(user => user.JoinedAt ?? user.CreatedAt)
            .ThenBy(user => user.Id)
            .ToList();

        var rows = Math.Clamp(maxRows, 1, 50);
        var listText = BuildRegistrationPendingListSection(pendingUsers, rows);
        var reviewChannelText = settings is not null && settings.RegistrationReviewChannelId != 0
            ? $"<#{ToUlong(settings.RegistrationReviewChannelId)}>"
            : "(未設定)";

        var embed = new EmbedBuilder()
            .WithTitle("登録申請 保留リスト")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .AddField("保留件数", $"{pendingUsers.Count}人", true)
            .AddField("仮登録ロール", $"<@&{provisionalRole.Id}>", true)
            .AddField("登録確認チャンネル", reviewChannelText, true)
            .AddField($"対象一覧(最大 {rows} 件)", listText, false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private static string BuildChannelPermissionDiagnostic(SocketGuildUser target, IGuildChannel channel)
    {
        try
        {
            var p = target.GetPermissions(channel);
            var lines = new[]
            {
                $"ViewChannel: {(p.ViewChannel ? "OK" : "NG")}",
                $"ReadMessageHistory: {(p.ReadMessageHistory ? "OK" : "NG")}",
                $"SendMessages: {(p.SendMessages ? "OK" : "NG")}",
                $"UseApplicationCommands: {(p.UseApplicationCommands ? "OK" : "NG")}",
                $"AttachFiles: {(p.AttachFiles ? "OK" : "NG")}",
                $"ManageChannels: {(p.ManageChannel ? "OK" : "NG")}"
            };
            return string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            return $"取得失敗: {ex.Message}";
        }
    }

    private async Task ShowInquiryTicketDetailAsync(int ticketId)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var ticket = await _db.GetInquiryTicketByIdAsync(ticketId);
        if (ticket is null || ticket.GuildId != ToDbId(Context.Guild.Id))
        {
            await FollowupAsync("対象のお問い合わせが見つかりませんでした。", ephemeral: true);
            return;
        }

        var requester = $"<@{ToUlong(ticket.RequesterUserId)}>";
        var channelText = $"<#{ToUlong(ticket.ChannelId)}>";
        var statusLabel = InquiryTicketStatus.IsClosed(ticket.Status) ? "クローズ" : "オープン";
        var transcript = string.IsNullOrWhiteSpace(ticket.TranscriptText) ? "未保存" : "保存済み";
        var embed = new EmbedBuilder()
            .WithTitle($"お問い合わせ詳細 #{ticket.Id}")
            .WithColor(InquiryTicketStatus.IsClosed(ticket.Status) ? Color.DarkGrey : Color.Orange)
            .WithCurrentTimestamp()
            .AddField("状態", statusLabel, true)
            .AddField("投稿者", requester, true)
            .AddField("件名", string.IsNullOrWhiteSpace(ticket.Subject) ? "未指定" : ticket.Subject, false)
            .AddField("チャンネル", channelText, true)
            .AddField("最終更新", $"<t:{ticket.LastActivityAt.ToUnixTimeSeconds()}:R>", true)
            .AddField("作成", $"<t:{ticket.CreatedAt.ToUnixTimeSeconds()}:F>", true)
            .AddField("トランスクリプト", transcript, true)
            .Build();

        var payload = await BuildInquiryDashboardPayloadAsync(Context.Guild.Id);
        await FollowupAsync(embed: embed, components: payload.Components, ephemeral: true);
    }

    private async Task<IUserMessage> EnsureInquiryDashboardMessageAsync(ITextChannel channel)
    {
        if (Context.Guild is null)
        {
            throw new InvalidOperationException("Guild is required.");
        }

        var payload = await BuildInquiryDashboardPayloadAsync(Context.Guild.Id);
        var recent = await channel.GetMessagesAsync(50).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                InquiryManagementPanel.LooksLikeDashboardMessage(message));

        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = payload.Embed;
                props.Components = payload.Components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            embed: payload.Embed,
            components: payload.Components,
            flags: MessageFlags.SuppressNotification);
    }

    private async Task ShowLotteryManagePanelAsync(string? prompt = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var recent = await _db.ListRecentLotteriesAsync(Context.Guild.Id, 10);
        var channelName = string.IsNullOrWhiteSpace(_options.LotteryPostChannelName)
            ? "抽選"
            : _options.LotteryPostChannelName.Trim();
        var configuredChannel = FindTextChannelByName(Context.Guild, channelName);
        var channelText = configuredChannel is null
            ? $"送信先: `{channelName}`（未検出）"
            : $"送信先: <#{configuredChannel.Id}> (`{channelName}`)";
        var content = string.IsNullOrWhiteSpace(prompt)
            ? channelText
            : $"{prompt}\n{channelText}";
        await RespondAsync(
            text: content,
            embed: LotteryPanel.BuildManagementEmbed(recent),
            components: BuildLotteryManageComponents(),
            ephemeral: true);
    }

    private async Task ShowLotteryAdminDetailAsync(Lottery lottery, string? prompt = null)
    {
        var entries = await _db.ListLotteryEntriesAsync(lottery.Id);
        var winners = await _db.ListLotteryWinnersAsync(lottery.Id);
        var nowUtc = DateTimeOffset.UtcNow;
        await RespondAsync(
            text: prompt,
            embed: LotteryPanel.BuildLotteryAdminEmbed(lottery, entries, winners, nowUtc),
            components: LotteryPanel.BuildLotteryAdminComponents(lottery, entries.Count, winners.Count, nowUtc),
            ephemeral: true);
    }

    private static MessageComponent BuildLotteryManageComponents()
    {
        return new ComponentBuilder()
            .WithButton("抽選を作成", LotteryPanel.ManageCreateCustomId, ButtonStyle.Success, row: 0)
            .WithButton("管理", LotteryPanel.ManageControlCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("再表示", LotteryPanel.ManageRefreshCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    private void PruneLotteryCreateDraftContexts()
    {
        _lotteryCreateDraftTracker.PruneExpired(DateTimeOffset.UtcNow, LotteryCreateDraftTimeout);
    }

    private LotteryCreateDraftContext GetOrCreateLotteryCreateDraftContext(DateTimeOffset nowUtc)
    {
        PruneLotteryCreateDraftContexts();
        if (_lotteryCreateDraftTracker.TryGet(Context.User.Id, out var current) &&
            nowUtc - current.CreatedAt <= LotteryCreateDraftTimeout)
        {
            return current;
        }

        var created = new LotteryCreateDraftContext(
            nowUtc.Add(LotteryDefaultCloseDuration),
            AllowDuplicateWinners: false,
            MentionMode: LotteryCreateMentionMode.None,
            nowUtc);
        _lotteryCreateDraftTracker.Set(Context.User.Id, created);
        return created;
    }

    private async Task ShowLotteryCreateCloseDatePageAsync(int pageIndex, string? prompt = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondAsync(
            prompt ?? "抽選の締切日を選択してください。",
            components: BuildLotteryCreateCloseDateSelectComponents(pageIndex),
            ephemeral: true);
    }

    private async Task SelectLotteryCreateCloseDefaultInternalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var draftContext = GetOrCreateLotteryCreateDraftContext(nowUtc);
        var closeAtUtc = nowUtc.Add(LotteryDefaultCloseDuration);
        _lotteryCreateDraftTracker.Set(Context.User.Id, draftContext with
        {
            CloseAtUtc = closeAtUtc,
            CreatedAt = nowUtc
        });
        await ShowLotteryCreateDuplicateSelectAsync(closeAtUtc, draftContext.AllowDuplicateWinners);
    }

    private async Task SelectLotteryCreateCloseDayInternalAsync(int dayOffset)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneLotteryCreateDraftContexts();
        await RespondAsync(
            "締切時間（時）を選択してください。",
            components: BuildLotteryCreateCloseHourSelectComponents(dayOffset),
            ephemeral: true);
    }

    private async Task SelectLotteryCreateCloseHourInternalAsync(int dayOffset, int hour)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneLotteryCreateDraftContexts();
        await RespondAsync(
            "締切時間（分）を選択してください。",
            components: BuildLotteryCreateCloseMinuteSelectComponents(dayOffset, hour),
            ephemeral: true);
    }

    private async Task SelectLotteryCreateCloseMinuteInternalAsync(int dayOffset, int hour, int minute)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var draftContext = GetOrCreateLotteryCreateDraftContext(nowUtc);
        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        var targetDate = nowJst.Date.AddDays(dayOffset);
        var closeJst = new DateTimeOffset(
            targetDate.Year,
            targetDate.Month,
            targetDate.Day,
            hour,
            minute,
            0,
            JapanTimeOffset);
        var closeAtUtc = closeJst.ToUniversalTime();
        if (closeAtUtc <= nowUtc)
        {
            var pageIndex = dayOffset / LotteryCreateSchedulePageSize;
            await RespondAsync(
                "過去の時刻は選択できません。日付を選び直してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(pageIndex),
                ephemeral: true);
            return;
        }

        if (closeAtUtc - nowUtc > LotteryMaxCloseDuration)
        {
            await RespondAsync(
                $"締切日時は{LotteryCreateCloseMaxDayOffset}日以内で選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        _lotteryCreateDraftTracker.Set(Context.User.Id, draftContext with
        {
            CloseAtUtc = closeAtUtc,
            CreatedAt = nowUtc
        });
        await ShowLotteryCreateDuplicateSelectAsync(closeAtUtc, draftContext.AllowDuplicateWinners);
    }

    private async Task ShowLotteryCreateDuplicateSelectAsync(DateTimeOffset closeAtUtc, bool allowDuplicateWinners)
    {
        await RespondAsync(
            $"締切日時: <t:{closeAtUtc.ToUnixTimeSeconds()}:F>\n重複当選を選択してください。",
            components: BuildLotteryCreateDuplicateSelectComponents(allowDuplicateWinners),
            ephemeral: true);
    }

    private async Task BackToLotteryCreateDuplicateInternalAsync()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        PruneLotteryCreateDraftContexts();
        if (!_lotteryCreateDraftTracker.TryGet(Context.User.Id, out var draftContext) ||
            nowUtc - draftContext.CreatedAt > LotteryCreateDraftTimeout)
        {
            _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        await ShowLotteryCreateDuplicateSelectAsync(draftContext.CloseAtUtc, draftContext.AllowDuplicateWinners);
    }

    private async Task SelectLotteryCreateDuplicateInternalAsync(bool allowDuplicateWinners)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        PruneLotteryCreateDraftContexts();
        if (!_lotteryCreateDraftTracker.TryGet(Context.User.Id, out var draftContext) ||
            nowUtc - draftContext.CreatedAt > LotteryCreateDraftTimeout)
        {
            _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        if (draftContext.CloseAtUtc <= nowUtc || draftContext.CloseAtUtc - nowUtc > LotteryMaxCloseDuration)
        {
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        _lotteryCreateDraftTracker.Set(Context.User.Id, draftContext with
        {
            AllowDuplicateWinners = allowDuplicateWinners,
            CreatedAt = nowUtc
        });
        await ShowLotteryCreateMentionSelectAsync(draftContext.CloseAtUtc, draftContext.MentionMode);
    }

    private async Task ShowLotteryCreateMentionSelectAsync(DateTimeOffset closeAtUtc, LotteryCreateMentionMode mentionMode)
    {
        await RespondAsync(
            $"締切日時: <t:{closeAtUtc.ToUnixTimeSeconds()}:F>\n通知メンションを選択してください。\n現在: {ToLotteryCreateMentionLabel(mentionMode)}",
            components: BuildLotteryCreateMentionSelectComponents(mentionMode),
            ephemeral: true);
    }

    private async Task SelectLotteryCreateMentionInternalAsync(LotteryCreateMentionMode mentionMode)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        PruneLotteryCreateDraftContexts();
        if (!_lotteryCreateDraftTracker.TryGet(Context.User.Id, out var draftContext) ||
            nowUtc - draftContext.CreatedAt > LotteryCreateDraftTimeout)
        {
            _lotteryCreateDraftTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        if (draftContext.CloseAtUtc <= nowUtc || draftContext.CloseAtUtc - nowUtc > LotteryMaxCloseDuration)
        {
            await RespondAsync(
                "締切日時を先に選択してください。",
                components: BuildLotteryCreateCloseDateSelectComponents(),
                ephemeral: true);
            return;
        }

        _lotteryCreateDraftTracker.Set(Context.User.Id, draftContext with
        {
            MentionMode = mentionMode,
            CreatedAt = nowUtc
        });
        await RespondWithModalAsync<LotteryCreateModal>(LotteryPanel.CreateModalCustomId);
    }

    private static MessageComponent BuildLotteryCreateCloseDateSelectComponents(int pageIndex = 0)
    {
        var maxPage = LotteryCreateCloseMaxDayOffset / LotteryCreateSchedulePageSize;
        var clampedPage = Math.Clamp(pageIndex, 0, maxPage);
        var startOffset = clampedPage * LotteryCreateSchedulePageSize;
        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);
        var builder = new ComponentBuilder()
            .WithButton("30分後", LotteryCreateCloseDefaultCustomId, ButtonStyle.Success, row: 0)
            .WithButton("テスト", LotteryCreateCloseTestCustomId, ButtonStyle.Primary, row: 0);

        for (var i = 0; i < LotteryCreateSchedulePageSize; i++)
        {
            var dayOffset = startOffset + i;
            if (dayOffset > LotteryCreateCloseMaxDayOffset)
            {
                break;
            }

            var date = nowJst.Date.AddDays(dayOffset);
            var label = dayOffset switch
            {
                0 => $"今日 {date:MM/dd}",
                1 => $"明日 {date:MM/dd}",
                2 => $"明後日 {date:MM/dd}",
                _ => $"{date:MM/dd}"
            };

            var row = i < 4 ? 1 : 2;
            builder.WithButton(
                label,
                $"{LotteryCreateCloseDayPickCustomIdPrefix}{dayOffset}",
                ButtonStyle.Primary,
                row: row);
        }

        builder
            .WithButton("前へ", $"{LotteryCreateClosePageCustomIdPrefix}{clampedPage - 1}", ButtonStyle.Secondary, disabled: clampedPage <= 0, row: 3)
            .WithButton("次へ", $"{LotteryCreateClosePageCustomIdPrefix}{clampedPage + 1}", ButtonStyle.Secondary, disabled: clampedPage >= maxPage, row: 3)
            .WithButton("戻る", LotteryPanel.ManageRefreshCustomId, ButtonStyle.Secondary, row: 3);

        return builder.Build();
    }

    private static MessageComponent BuildLotteryCreateCloseHourSelectComponents(int dayOffset)
    {
        var backPage = dayOffset / LotteryCreateSchedulePageSize;
        var menu = new SelectMenuBuilder()
            .WithCustomId(LotteryCreateCloseHourSelectCustomId)
            .WithPlaceholder("時間（時）を選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        for (var hour = 0; hour < 24; hour++)
        {
            var label = hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            menu.AddOption(label, $"{dayOffset}|{hour}");
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("日付に戻る", $"{LotteryCreateClosePageCustomIdPrefix}{backPage}", ButtonStyle.Secondary, row: 1)
            .WithButton("戻る", LotteryPanel.ManageRefreshCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildLotteryCreateDuplicateSelectComponents(bool allowDuplicateWinners)
    {
        return new ComponentBuilder()
            .WithButton(
                "重複当選: ON",
                LotteryCreateSetDuplicateOnCustomId,
                allowDuplicateWinners ? ButtonStyle.Success : ButtonStyle.Secondary,
                row: 0)
            .WithButton(
                "重複当選: OFF",
                LotteryCreateSetDuplicateOffCustomId,
                allowDuplicateWinners ? ButtonStyle.Secondary : ButtonStyle.Primary,
                row: 0)
            .WithButton("日付選択に戻る", LotteryCreateDuplicateBackCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildLotteryCreateMentionSelectComponents(LotteryCreateMentionMode mentionMode)
    {
        return new ComponentBuilder()
            .WithButton(
                "メンションなし",
                LotteryCreateSetMentionNoneCustomId,
                mentionMode == LotteryCreateMentionMode.None ? ButtonStyle.Primary : ButtonStyle.Secondary,
                row: 0)
            .WithButton(
                "@here",
                LotteryCreateSetMentionHereCustomId,
                mentionMode == LotteryCreateMentionMode.Here ? ButtonStyle.Primary : ButtonStyle.Secondary,
                row: 0)
            .WithButton(
                "@everyone",
                LotteryCreateSetMentionEveryoneCustomId,
                mentionMode == LotteryCreateMentionMode.Everyone ? ButtonStyle.Primary : ButtonStyle.Secondary,
                row: 0)
            .WithButton("重複選択に戻る", LotteryCreateMentionBackCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static string ToLotteryCreateMentionLabel(LotteryCreateMentionMode mentionMode)
    {
        return mentionMode switch
        {
            LotteryCreateMentionMode.Here => "@here",
            LotteryCreateMentionMode.Everyone => "@everyone",
            _ => "メンションなし"
        };
    }

    private static (string? MentionText, AllowedMentions AllowedMentions, bool SuppressNotification) BuildLotteryPostMentionSettings(LotteryCreateMentionMode mentionMode)
    {
        return mentionMode switch
        {
            LotteryCreateMentionMode.Here => (
                "@here",
                new AllowedMentions { AllowedTypes = AllowedMentionTypes.Everyone },
                false),
            LotteryCreateMentionMode.Everyone => (
                "@everyone",
                new AllowedMentions { AllowedTypes = AllowedMentionTypes.Everyone },
                false),
            _ => (null, AllowedMentions.None, true)
        };
    }

    private static MessageComponent BuildLotteryCreateCloseMinuteSelectComponents(int dayOffset, int hour)
    {
        var backPage = dayOffset / LotteryCreateSchedulePageSize;
        var menu = new SelectMenuBuilder()
            .WithCustomId(LotteryCreateCloseMinuteSelectCustomId)
            .WithPlaceholder($"分を選択（{hour:00}時）")
            .WithMinValues(1)
            .WithMaxValues(1)
            .AddOption("00", $"{dayOffset}|{hour}|0")
            .AddOption("10", $"{dayOffset}|{hour}|10")
            .AddOption("20", $"{dayOffset}|{hour}|20")
            .AddOption("30", $"{dayOffset}|{hour}|30")
            .AddOption("40", $"{dayOffset}|{hour}|40")
            .AddOption("50", $"{dayOffset}|{hour}|50");

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("時間に戻る", $"{LotteryCreateCloseDayPickCustomIdPrefix}{dayOffset}", ButtonStyle.Secondary, row: 1)
            .WithButton("日付に戻る", $"{LotteryCreateClosePageCustomIdPrefix}{backPage}", ButtonStyle.Secondary, row: 1)
            .WithButton("戻る", LotteryPanel.ManageRefreshCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private async Task<Lottery?> GetLotteryForCurrentGuildAsync(int lotteryId)
    {
        if (Context.Guild is null)
        {
            return null;
        }

        var lottery = await _db.GetLotteryByIdAsync(lotteryId);
        if (lottery is null || lottery.GuildId != ToDbId(Context.Guild.Id))
        {
            return null;
        }

        return await EnsureLotteryStatusByDeadlineAsync(lottery);
    }

    private Task<Lottery> EnsureLotteryStatusByDeadlineAsync(Lottery lottery)
    {
        if (LotteryStatus.IsOpen(lottery.Status) && lottery.CloseAt <= DateTimeOffset.UtcNow)
        {
            // Keep DB status OPEN until the scheduler processes auto-close+auto-draw.
            // Here we only reflect CLOSED in UI/interaction checks to block further entries.
            lottery.Status = LotteryStatus.Closed;
        }

        return Task.FromResult(lottery);
    }

    private async Task RefreshLotteryMessageAsync(Lottery lottery, IUserMessage? knownMessage = null)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var current = await _db.GetLotteryByIdAsync(lottery.Id) ?? lottery;
        current = await EnsureLotteryStatusByDeadlineAsync(current);

        var entries = await _db.ListLotteryEntriesAsync(current.Id);
        var winners = await _db.ListLotteryWinnersAsync(current.Id);
        var nowUtc = DateTimeOffset.UtcNow;
        var embed = LotteryPanel.BuildLotteryEmbed(current, entries, winners, nowUtc);
        var components = LotteryPanel.BuildLotteryComponents(current, entries.Count, winners.Count, nowUtc);

        var currentMessageId = current.MessageId.HasValue ? ToUlong(current.MessageId.Value) : 0;
        if (currentMessageId == 0)
        {
            return;
        }

        if (knownMessage is not null && knownMessage.Id == currentMessageId)
        {
            try
            {
                await knownMessage.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                    props.Components = components;
                });
            }
            catch (Discord.Net.HttpException ex) when (IsUnknownMessage(ex))
            {
                await SendLotteryApiReferenceErrorAuditAsync(current, knownMessage.Channel.Id, knownMessage.Id, "lottery_message_modify");
            }

            return;
        }

        var channel = Context.Guild.GetTextChannel(ToUlong(current.ChannelId));
        if (channel is null)
        {
            return;
        }

        var message = await channel.GetMessageAsync(currentMessageId) as IUserMessage;
        if (message is null)
        {
            return;
        }

        try
        {
            await message.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
                props.Components = components;
            });
        }
        catch (Discord.Net.HttpException ex) when (IsUnknownMessage(ex))
        {
            await SendLotteryApiReferenceErrorAuditAsync(current, channel.Id, message.Id, "lottery_message_modify");
        }
    }

    private async Task TryUpdateLotteryAdminDetailMessageAsync(Lottery lottery)
    {
        if (Context.Interaction is not SocketMessageComponent component ||
            component.Message is not IUserMessage source)
        {
            return;
        }

        if (lottery.MessageId.HasValue && source.Id == ToUlong(lottery.MessageId.Value))
        {
            return;
        }

        var current = await _db.GetLotteryByIdAsync(lottery.Id) ?? lottery;
        current = await EnsureLotteryStatusByDeadlineAsync(current);
        var entries = await _db.ListLotteryEntriesAsync(current.Id);
        var winners = await _db.ListLotteryWinnersAsync(current.Id);
        var nowUtc = DateTimeOffset.UtcNow;

        try
        {
            await source.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = LotteryPanel.BuildLotteryAdminEmbed(current, entries, winners, nowUtc);
                props.Components = LotteryPanel.BuildLotteryAdminComponents(current, entries.Count, winners.Count, nowUtc);
            });
        }
        catch (Discord.Net.HttpException ex) when (IsUnknownMessage(ex))
        {
            await SendLotteryApiReferenceErrorAuditAsync(current, (Context.Channel as SocketTextChannel)?.Id ?? 0UL, source.Id, "lottery_admin_detail_modify");
        }
    }

    private async Task<ITextChannel?> EnsureLotteryClaimChannelAsync(Lottery lottery, SocketGuildUser winnerUser)
    {
        if (Context.Guild is null)
        {
            return null;
        }

        var claim = await _db.GetLotteryClaimChannelAsync(lottery.Id, winnerUser.Id);
        if (claim is not null)
        {
            var existingChannel = Context.Guild.GetTextChannel(ToUlong(claim.ChannelId));
            if (existingChannel is not null)
            {
                await ApplyLotteryClaimPermissionsAsync(existingChannel, Context.Guild, winnerUser.Id, claim.IsClosed);
                await EnsureLotteryClaimControlMessageAsync(existingChannel, claim, lottery, winnerUser, announceCreated: false);
                return existingChannel;
            }
        }

        var channelName = BuildLotteryClaimChannelName(lottery.Id, winnerUser.Id);
        var created = await Context.Guild.CreateTextChannelAsync(channelName, props =>
        {
            props.Topic = $"抽選景品受け取り: lottery#{lottery.Id} winner:{winnerUser.Id}";
        });
        await ApplyLotteryClaimPermissionsAsync(created, Context.Guild, winnerUser.Id, closed: false);
        await _db.UpsertLotteryClaimChannelIdAsync(lottery.Id, winnerUser.Id, created.Id, DateTimeOffset.UtcNow);
        claim = await _db.GetLotteryClaimChannelAsync(lottery.Id, winnerUser.Id);
        if (claim is not null)
        {
            await EnsureLotteryClaimControlMessageAsync(created, claim, lottery, winnerUser, announceCreated: true);
        }

        return created;
    }

    private async Task<(SocketGuild Guild, SocketTextChannel Channel, LotteryClaimChannel Claim, Lottery Lottery)?> ResolveLotteryClaimContextAsync()
    {
        if (Context.Guild is null || Context.Channel is not SocketTextChannel channel)
        {
            return null;
        }

        var claim = await _db.GetLotteryClaimChannelByChannelIdAsync(channel.Id);
        if (claim is null || claim.LotteryId <= 0 || claim.UserId == 0)
        {
            return null;
        }

        var lottery = await _db.GetLotteryByIdAsync(claim.LotteryId);
        if (lottery is null || lottery.GuildId != ToDbId(Context.Guild.Id))
        {
            return null;
        }

        return (Context.Guild, channel, claim, lottery);
    }

    private async Task EnsureLotteryClaimControlMessageAsync(
        ITextChannel channel,
        LotteryClaimChannel claim,
        Lottery lottery,
        SocketGuildUser? winnerUser,
        bool announceCreated)
    {
        IUserMessage? controlMessage = null;
        if (claim.ControlMessageId.HasValue && claim.ControlMessageId.Value != 0)
        {
            try
            {
                controlMessage = await channel.GetMessageAsync(ToUlong(claim.ControlMessageId.Value)) as IUserMessage;
            }
            catch
            {
            }
        }

        var winners = await _db.ListLotteryWinnersAsync(lottery.Id);
        var prizeText = BuildLotteryClaimPrizeText(lottery, winners, claim.UserId);
        var embed = BuildLotteryClaimControlEmbed(claim, lottery, prizeText);
        var components = BuildLotteryClaimControlComponents(claim.IsClosed);
        if (controlMessage is not null)
        {
            await controlMessage.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
                props.Components = components;
            });
            return;
        }

        var managerRoles = Context.Guild is null ? Array.Empty<IRole>() : ResolveLotteryClaimManagerRoles(Context.Guild);
        var mentionText = string.Empty;
        AllowedMentions? allowedMentions = null;
        if (announceCreated && winnerUser is not null)
        {
            mentionText = managerRoles.Count == 0
                ? winnerUser.Mention
                : $"{string.Join(' ', managerRoles.Select(role => role.Mention))} {winnerUser.Mention}";
            allowedMentions = new AllowedMentions
            {
                UserIds = new List<ulong> { winnerUser.Id },
                RoleIds = managerRoles.Select(role => role.Id).ToList()
            };
        }

        var posted = await channel.SendMessageAsync(
            text: string.IsNullOrWhiteSpace(mentionText) ? null : $"{mentionText}\n景品受け取り対応チャンネルです。",
            embed: embed,
            components: components,
            allowedMentions: allowedMentions ?? AllowedMentions.None);
        await _db.UpdateLotteryClaimChannelControlMessageIdAsync(claim.Id, posted.Id);
    }

    private async Task ApplyLotteryClaimPermissionsAsync(IGuildChannel channel, SocketGuild guild, ulong winnerUserId, bool closed)
    {
        var denyEveryone = new OverwritePermissions(viewChannel: PermValue.Deny);
        var participantPermissions = closed
            ? BuildLotteryClaimReadOnlyPermissions()
            : BuildLotteryClaimParticipantPermissions();
        var managerPermissions = BuildLotteryClaimManagerPermissions();
        var tasks = new List<Task>
        {
            channel.AddPermissionOverwriteAsync(guild.EveryoneRole, denyEveryone)
        };
        IGuildUser? winnerUser = guild.GetUser(winnerUserId);
        if (winnerUser is null)
        {
            try
            {
                winnerUser = await Context.Client.Rest.GetGuildUserAsync(guild.Id, winnerUserId);
            }
            catch
            {
            }
        }
        if (winnerUser is not null)
        {
            tasks.Add(channel.AddPermissionOverwriteAsync(winnerUser, participantPermissions));
        }

        foreach (var role in ResolveLotteryClaimManagerRoles(guild))
        {
            tasks.Add(channel.AddPermissionOverwriteAsync(role, managerPermissions));
        }

        await Task.WhenAll(tasks);
    }

    private IReadOnlyList<IRole> ResolveLotteryClaimManagerRoles(SocketGuild guild)
    {
        var roleNameSet = _options.BuildManagementRoleNameSet();
        if (roleNameSet.Count == 0)
        {
            return Array.Empty<IRole>();
        }

        return guild.Roles
            .Where(role => roleNameSet.Contains(role.Name))
            .Cast<IRole>()
            .ToList();
    }

    private static OverwritePermissions BuildLotteryClaimParticipantPermissions()
    {
        return new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Allow,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Allow,
            addReactions: PermValue.Allow,
            useApplicationCommands: PermValue.Allow);
    }

    private static OverwritePermissions BuildLotteryClaimReadOnlyPermissions()
    {
        return new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Deny,
            attachFiles: PermValue.Deny,
            embedLinks: PermValue.Deny,
            addReactions: PermValue.Deny,
            useApplicationCommands: PermValue.Allow);
    }

    private static OverwritePermissions BuildLotteryClaimManagerPermissions()
    {
        return new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Allow,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Allow,
            addReactions: PermValue.Allow,
            manageMessages: PermValue.Allow,
            manageChannel: PermValue.Allow,
            useApplicationCommands: PermValue.Allow);
    }

    private static Embed BuildLotteryClaimControlEmbed(LotteryClaimChannel claim, Lottery lottery, string claimPrizeText)
    {
        var statusText = claim.IsClosed ? "クローズ" : "オープン";
        var color = claim.IsClosed ? Color.DarkGrey : Color.Green;
        var builder = new EmbedBuilder()
            .WithTitle($"景品受け取り管理 #{claim.Id}")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("状態", statusText, true)
            .AddField("抽選", $"#{lottery.Id} {lottery.Title}", false)
            .AddField("当選者", $"<@{ToUlong(claim.UserId)}>", false)
            .AddField("当選景品", claimPrizeText, false)
            .AddField("作成", $"<t:{claim.CreatedAt.ToUnixTimeSeconds()}:F>", true);
        if (claim.ClosedAt.HasValue)
        {
            builder.AddField("クローズ", $"<t:{claim.ClosedAt.Value.ToUnixTimeSeconds()}:F>", true);
        }

        builder.AddField("操作", "管理者はクローズ/再開できます。削除はクローズ時のみ実行可能です。", false);
        return builder.Build();
    }

    private static MessageComponent BuildLotteryClaimControlComponents(bool isClosed)
    {
        return new ComponentBuilder()
            .WithButton(
                isClosed ? "再開" : "クローズ",
                LotteryClaimToggleCustomId,
                isClosed ? ButtonStyle.Success : ButtonStyle.Danger,
                row: 0)
            .WithButton(
                "チャンネル削除",
                LotteryClaimDeleteCustomId,
                ButtonStyle.Danger,
                disabled: !isClosed,
                row: 0)
            .Build();
    }

    private static string BuildLotteryClaimPrizeText(Lottery lottery, IReadOnlyList<LotteryWinner> winners, long targetUserId)
    {
        if (targetUserId == 0 || winners.Count == 0)
        {
            return "なし";
        }

        var prizes = ParseLotteryPrizeList(lottery.PrizeList);
        var matched = winners
            .OrderBy(winner => winner.WinnerOrder)
            .ThenBy(winner => winner.Id)
            .Select((winner, index) => new { Winner = winner, Index = index })
            .Where(x => x.Winner.UserId == targetUserId)
            .Select(x => x.Index < prizes.Count ? prizes[x.Index] : "景品")
            .ToList();
        if (matched.Count == 0)
        {
            return "なし";
        }

        return JoinWithinLimit(matched.Select((item, index) => $"{index + 1}. {item}"), 900);
    }

    private static IReadOnlyList<string> ParseLotteryPrizeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var normalized = raw
            .Replace('、', ',')
            .Replace('，', ',');
        return normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static string BuildLotteryClaimChannelName(int lotteryId, ulong userId)
    {
        var name = $"lottery-{lotteryId}-{userId}";
        return name.Length <= 95 ? name : name.Substring(0, 95);
    }

    private static bool TryParseLotteryId(string raw, out int lotteryId)
        => int.TryParse(raw, out lotteryId) && lotteryId > 0;

    private static bool TryParseLotteryAdminConfirmAction(string raw, out string action, out int lotteryId)
    {
        action = string.Empty;
        lotteryId = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out lotteryId) || lotteryId <= 0)
        {
            return false;
        }

        action = parts[0].Trim().ToLowerInvariant();
        return action is "draw" or "redraw" or "close" or "cancel";
    }

    private static bool TryParseMemberManageDangerPayload(string raw, out string action, out ulong userId)
    {
        action = string.Empty;
        userId = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !ulong.TryParse(parts[1], out userId) || userId == 0)
        {
            return false;
        }

        action = parts[0].Trim().ToLowerInvariant();
        return action is "kick" or "ban";
    }

    private static bool TryValidateRoleOperation(SocketGuild guild, SocketGuildUser targetUser, IRole role, out string reason)
    {
        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            reason = "Botユーザーを取得できませんでした。";
            return false;
        }

        if (!botUser.GuildPermissions.ManageRoles)
        {
            reason = "Botに「ロールの管理」権限がありません。";
            return false;
        }

        if (role.Position >= botUser.Hierarchy)
        {
            reason = $"Botのロール位置が `{role.Name}` 以下です。";
            return false;
        }

        if (targetUser.Id == guild.OwnerId)
        {
            reason = "サーバーオーナーのロールは変更できません。";
            return false;
        }

        if (targetUser.Id != botUser.Id && targetUser.Hierarchy >= botUser.Hierarchy)
        {
            reason = "対象ユーザーのロール階層がBot以上です。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateKickOperation(SocketGuild guild, SocketGuildUser targetUser, out string reason)
    {
        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            reason = "Botユーザーを取得できませんでした。";
            return false;
        }

        if (!botUser.GuildPermissions.KickMembers)
        {
            reason = "Botに「メンバーのキック」権限がありません。";
            return false;
        }

        if (targetUser.Id == guild.OwnerId)
        {
            reason = "サーバーオーナーはキックできません。";
            return false;
        }

        if (targetUser.Id == botUser.Id)
        {
            reason = "Bot自身はキックできません。";
            return false;
        }

        if (targetUser.Hierarchy >= botUser.Hierarchy)
        {
            reason = "対象ユーザーのロール階層がBot以上です。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateBanOperation(SocketGuild guild, SocketGuildUser targetUser, out string reason)
    {
        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            reason = "Botユーザーを取得できませんでした。";
            return false;
        }

        if (!botUser.GuildPermissions.BanMembers)
        {
            reason = "Botに「メンバーのBAN」権限がありません。";
            return false;
        }

        if (targetUser.Id == guild.OwnerId)
        {
            reason = "サーバーオーナーはBANできません。";
            return false;
        }

        if (targetUser.Id == botUser.Id)
        {
            reason = "Bot自身はBANできません。";
            return false;
        }

        if (targetUser.Hierarchy >= botUser.Hierarchy)
        {
            reason = "対象ユーザーのロール階層がBot以上です。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateTimeoutOperation(SocketGuild guild, SocketGuildUser targetUser, out string reason)
    {
        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            reason = "Botユーザーを取得できませんでした。";
            return false;
        }

        if (!botUser.GuildPermissions.ModerateMembers)
        {
            reason = "Botに「メンバーをタイムアウト」権限がありません。";
            return false;
        }

        if (targetUser.Id == guild.OwnerId)
        {
            reason = "サーバーオーナーはタイムアウトできません。";
            return false;
        }

        if (targetUser.Id == botUser.Id)
        {
            reason = "Bot自身はタイムアウトできません。";
            return false;
        }

        if (targetUser.Hierarchy >= botUser.Hierarchy)
        {
            reason = "対象ユーザーのロール階層がBot以上です。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static MessageComponent BuildLotteryAdminConfirmComponents(string action, int lotteryId, string actionLabel, ButtonStyle confirmStyle)
    {
        return new ComponentBuilder()
            .WithButton($"はい、{actionLabel}します", $"{LotteryAdminConfirmExecuteCustomIdPrefix}{action}|{lotteryId}", confirmStyle, row: 0)
            .WithButton("いいえ", LotteryAdminConfirmCancelCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    private async Task<IRole?> EnsureLotteryParticipantRoleAsync(Lottery lottery)
    {
        if (Context.Guild is null)
        {
            return null;
        }

        if (lottery.ParticipantRoleId.HasValue && lottery.ParticipantRoleId.Value != 0)
        {
            var existing = Context.Guild.GetRole(ToUlong(lottery.ParticipantRoleId.Value));
            if (existing is not null)
            {
                return existing;
            }
        }

        try
        {
            var role = await Context.Guild.CreateRoleAsync(
                $"抽選参加-{lottery.Id}",
                GuildPermissions.None,
                color: new Color(46, 204, 113),
                isHoisted: false,
                isMentionable: true);
            await _db.UpdateLotteryParticipantRoleIdAsync(lottery.Id, role.Id);
            lottery.ParticipantRoleId = ToDbId(role.Id);
            return role;
        }
        catch
        {
            return null;
        }
    }

    private async Task ScheduleLotteryParticipantRoleCleanupAsync(Lottery lottery, string message)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var current = await _db.GetLotteryByIdAsync(lottery.Id) ?? lottery;
        if (!current.ParticipantRoleId.HasValue || current.ParticipantRoleId.Value == 0)
        {
            return;
        }

        if (current.ParticipantRoleDeleteAt.HasValue)
        {
            return;
        }

        var role = Context.Guild.GetRole(ToUlong(current.ParticipantRoleId.Value));
        if (role is null)
        {
            await _db.UpdateLotteryParticipantRoleIdAsync(current.Id, null);
            return;
        }

        var deleteAt = DateTimeOffset.UtcNow.Add(LotteryParticipantRoleRetention);

        var channel = Context.Guild.GetTextChannel(ToUlong(current.ChannelId));
        if (channel is not null)
        {
            try
            {
                await channel.SendMessageAsync($"{role.Mention} {message}");
            }
            catch
            {
            }
        }

        await _db.UpdateLotteryParticipantRoleDeleteAtAsync(current.Id, deleteAt);
    }

    private async Task PostLotteryResultEmbedAsync(Lottery lottery)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var channel = Context.Guild.GetTextChannel(ToUlong(lottery.ChannelId));
        if (channel is null)
        {
            return;
        }

        var entries = await _db.ListLotteryEntriesAsync(lottery.Id);
        var winners = await _db.ListLotteryWinnersAsync(lottery.Id);
        var nowUtc = DateTimeOffset.UtcNow;
        var embed = LotteryPanel.BuildLotteryResultEmbed(lottery, entries, winners, nowUtc);
        var winnerAnnouncement = LotteryPanel.BuildLotteryWinnerAnnouncementText(lottery, winners);
        var mentionUserIds = winners
            .Select(winner => ToUlong(winner.UserId))
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        var allowedMentions = mentionUserIds.Count == 0
            ? AllowedMentions.None
            : new AllowedMentions { UserIds = mentionUserIds };
        var resultComponents = LotteryPanel.BuildLotteryResultComponents(lottery, winners.Count);

        try
        {
            await channel.SendMessageAsync(
                text: winnerAnnouncement,
                embed: embed,
                components: resultComponents,
                allowedMentions: allowedMentions);
        }
        catch
        {
        }
    }

    private static async Task TryAddRoleAsync(SocketGuildUser user, IRole role)
    {
        try
        {
            if (!user.Roles.Any(existing => existing.Id == role.Id))
            {
                await user.AddRoleAsync(role);
            }
        }
        catch
        {
        }
    }

    private static async Task TryRemoveRoleAsync(SocketGuildUser user, IRole role)
    {
        try
        {
            if (user.Roles.Any(existing => existing.Id == role.Id))
            {
                await user.RemoveRoleAsync(role);
            }
        }
        catch
        {
        }
    }

    private async Task SendLotteryStatusTransitionAuditAsync(
        Lottery lottery,
        string beforeStatus,
        string afterStatus,
        string trigger,
        string? note = null)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var channelId = ToUlong(lottery.ChannelId);
        var messageId = lottery.MessageId.HasValue ? ToUlong(lottery.MessageId.Value) : 0;
        var lines = new List<string>
        {
            $"🎯 抽選: #{lottery.Id} {lottery.Title}",
            $"🔄 状態: {FormatLotteryStatusLabel(beforeStatus)} -> {FormatLotteryStatusLabel(afterStatus)}",
            $"🧭 トリガー: {trigger}",
            $"💬 チャンネル: <#{channelId}>"
        };
        if (messageId != 0)
        {
            lines.Add($"🔗 メッセージ: https://discord.com/channels/{Context.Guild.Id}/{channelId}/{messageId}");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add($"📝 備考: {note}");
        }

        await _audit.SendAsync(Context.Guild, "抽選状態遷移", string.Join('\n', lines), Color.Orange, Context.User);
    }

    private async Task SendLotteryApiReferenceErrorAuditAsync(Lottery lottery, ulong channelId, ulong messageId, string operation)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var description = string.Join('\n', new[]
        {
            $"🎯 抽選: #{lottery.Id} {lottery.Title}",
            $"🧭 操作: {operation}",
            $"🆔 guild/channel/message/user: {Context.Guild.Id}/{channelId}/{messageId}/{Context.User.Id}",
            "❌ Error: Unknown Message / Unknown Channel"
        });
        await _audit.SendAsync(Context.Guild, "Discord API参照切れ", description, Color.Red, Context.User);
    }

    private static bool IsUnknownMessage(Discord.Net.HttpException ex)
        => ex.DiscordCode == DiscordErrorCode.UnknownMessage;

    private static string FormatLotteryStatusLabel(string? status)
    {
        var normalized = LotteryStatus.Normalize(status);
        return normalized switch
        {
            LotteryStatus.Open => "OPEN(受付中)",
            LotteryStatus.Closed => "CLOSED(締切)",
            LotteryStatus.Drawn => "DRAWN(抽選済み)",
            LotteryStatus.Canceled => "CANCELED(中止)",
            _ => normalized
        };
    }

    private IUserMessage? GetKnownLotteryMessage()
    {
        return Context.Interaction is SocketMessageComponent component
            ? component.Message as IUserMessage
            : null;
    }

    private static List<LotteryEntry> PickLotteryWinners(
        IReadOnlyList<LotteryEntry> source,
        int count,
        bool allowDuplicateWinners)
    {
        if (source.Count == 0 || count <= 0)
        {
            return new List<LotteryEntry>();
        }

        var take = allowDuplicateWinners
            ? count
            : Math.Min(count, source.Count);
        if (take <= source.Count)
        {
            var pool = source.ToList();
            var unique = new List<LotteryEntry>(take);
            for (var i = 0; i < take; i++)
            {
                var index = RandomNumberGenerator.GetInt32(pool.Count);
                unique.Add(pool[index]);
                pool.RemoveAt(index);
            }

            return unique;
        }

        var duplicated = new List<LotteryEntry>(take);
        for (var i = 0; i < take; i++)
        {
            var index = RandomNumberGenerator.GetInt32(source.Count);
            duplicated.Add(source[index]);
        }

        return duplicated;
    }

    private async Task<(Embed Embed, MessageComponent Components, IReadOnlyList<InquiryTicket> Tickets)> BuildInquiryDashboardPayloadAsync(ulong guildId)
    {
        var tickets = await _db.ListInquiryTicketsAsync(guildId, includeClosed: false, maxRows: 25);
        return (
            InquiryManagementPanel.BuildSummaryEmbed(tickets),
            InquiryManagementPanel.BuildComponents(tickets),
            tickets);
    }

    private async Task<ITextChannel?> ResolveOrCreateInquiryManagementChannelAsync(SocketGuild guild)
    {
        var existing = guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, InquiryManagementPanel.ChannelName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ApplyInquiryManagementChannelPermissionsAsync(guild, existing);
            return existing;
        }

        var category = guild.CategoryChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, "お問い合わせ", StringComparison.OrdinalIgnoreCase));
        var created = await guild.CreateTextChannelAsync(InquiryManagementPanel.ChannelName, props =>
        {
            props.Topic = "お問い合わせ管理パネル";
            if (category is not null)
            {
                props.CategoryId = category.Id;
            }
        });

        await ApplyInquiryManagementChannelPermissionsAsync(guild, created);
        return created;
    }

    private async Task ApplyInquiryManagementChannelPermissionsAsync(SocketGuild guild, ITextChannel channel)
    {
        var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
        var adminRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
        var denyEveryone = new OverwritePermissions(viewChannel: PermValue.Deny);
        await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, denyEveryone);

        if (adminRole is not null)
        {
            var allowAdmin = new OverwritePermissions(
                viewChannel: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                sendMessages: PermValue.Allow,
                manageMessages: PermValue.Allow,
                useApplicationCommands: PermValue.Allow);
            await channel.AddPermissionOverwriteAsync(adminRole, allowAdmin);
        }
    }

    private async Task<IUserMessage> EnsureAdminControlPanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch
            {
            }
        }

        var settings = Context.Guild is null
            ? null
            : await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled);
                props.Components = AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled);
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            embed: BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled),
            components: AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled),
            flags: MessageFlags.SuppressNotification);
    }

    private static Task ApplyAdminControlPanelStateAsync(IUserMessage message, bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        var embed = BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled);
        var components = AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled);
        return message.ModifyAsync(props =>
        {
            props.Content = null;
            props.Embed = embed;
            props.Components = components;
        });
    }

    private async Task RefreshStoredAdminControlPanelStateAsync(bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var panel = await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id);
        if (panel is null || panel.ChannelId == 0 || panel.MessageId == 0)
        {
            return;
        }

        var channel = Context.Guild.GetTextChannel(ToUlong(panel.ChannelId));
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }
        catch
        {
        }
    }

    private static bool LooksLikeAdminControlPanelMessage(IUserMessage message)
    {
        foreach (var embed in message.Embeds)
        {
            if (string.Equals(embed.Title, "ADMIN設定パネル", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Embed BuildAdminStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled)
        => AdminControlPanel.BuildStatusEmbed(approvalRequired, inactiveAutoKickEnabled);

    private int GetMemberManageLookbackDays(ulong guildId, ulong actorUserId)
    {
        PruneMemberManageLookbackPreferences();
        if (_memberManageLookbackByActor.TryGetValue(actorUserId, out var preference) &&
            preference.GuildId == guildId &&
            preference.Days >= MemberManageLookbackDaysMin &&
            preference.Days <= MemberManageLookbackDaysMax)
        {
            return preference.Days;
        }

        return Math.Clamp(MemberManageLogLookbackDays, MemberManageLookbackDaysMin, MemberManageLookbackDaysMax);
    }

    private void SetMemberManageLookbackDays(ulong guildId, ulong actorUserId, int days)
    {
        var clampedDays = Math.Clamp(days, MemberManageLookbackDaysMin, MemberManageLookbackDaysMax);
        _memberManageLookbackByActor[actorUserId] = new MemberManageLookbackPreference(
            guildId,
            clampedDays,
            DateTimeOffset.UtcNow);
        PruneMemberManageLookbackPreferences();
    }

    private void PruneMemberManageLookbackPreferences()
    {
        var threshold = DateTimeOffset.UtcNow - MemberManageLookbackPreferenceTimeout;
        foreach (var pair in _memberManageLookbackByActor)
        {
            if (pair.Value.UpdatedAt >= threshold)
            {
                continue;
            }

            _memberManageLookbackByActor.TryRemove(pair.Key, out _);
        }
    }

    private async Task UpdateMemberManageTargetPanelAsync(string userIdRaw, string? notice)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var payload = await BuildMemberManageTargetPayloadAsync(Context.Guild, targetUserId, notice);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = payload.Embed;
            props.Components = payload.Components;
        });
    }

    private async Task MoveMemberManageSelectPageAsync(string payloadRaw, int direction)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はパネルから実行してください。", ephemeral: true);
            return;
        }

        if (!TryParseMemberManageSelectPagePayload(payloadRaw, out var currentPage, out var currentTargetUserId))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var users = ListMemberManageSelectableUsers(Context.Guild);
        var totalPages = Math.Max(1, (int)Math.Ceiling(users.Count / (double)MemberManageSelectPageSize));
        var nextPage = Math.Clamp(currentPage + direction, 0, totalPages - 1);

        if (currentTargetUserId != 0 && Context.Guild.GetUser(currentTargetUserId) is not null)
        {
            var payload = await BuildMemberManageTargetPayloadAsync(
                Context.Guild,
                currentTargetUserId,
                notice: null,
                forcedSelectPage: nextPage);
            await component.UpdateAsync(props =>
            {
                props.Content = null;
                props.Embed = payload.Embed;
                props.Components = payload.Components;
            });
            return;
        }

        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = BuildMemberManageSelectEmbed();
            props.Components = BuildMemberManageSelectComponents(Context.Guild, nextPage);
        });
    }

    private static Embed BuildMemberManageSelectEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("メンバー管理")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .WithDescription("下のセレクトから対象メンバーを選んでください。")
            .AddField("操作例", "プロフィール / 期間サマリ / 処分履歴 / タイムアウト / キック / BAN / ログ出力", false)
            .Build();
    }

    private static MessageComponent BuildMemberManageSelectComponents(SocketGuild guild, int page, ulong currentTargetUserId = 0)
        => BuildMemberManageSelectComponentBuilder(guild, page, currentTargetUserId, pagerRow: 1).Build();

    private static ComponentBuilder BuildMemberManageSelectComponentBuilder(
        SocketGuild guild,
        int page,
        ulong currentTargetUserId,
        int pagerRow)
    {
        _ = page;
        _ = currentTargetUserId;
        _ = pagerRow;
        var users = ListMemberManageSelectableUsers(guild);
        if (users.Count == 0)
        {
            var emptySelect = new SelectMenuBuilder
            {
                CustomId = $"{MemberManageSelectCustomIdPrefix}0",
                Type = ComponentType.UserSelect,
                Placeholder = "表示できるメンバーがいません",
                MinValues = 1,
                MaxValues = 1,
                IsDisabled = true
            };

            return new ComponentBuilder()
                .WithSelectMenu(emptySelect, row: 0)
                .WithButton("期間設定", $"{MemberManagePeriodOpenCustomIdPrefix}{currentTargetUserId}", ButtonStyle.Secondary, row: 1)
                .WithButton("非アクティブ一覧", MemberManageInactiveListCustomId, ButtonStyle.Secondary, row: 1);
        }

        var select = new SelectMenuBuilder
        {
            CustomId = $"{MemberManageSelectCustomIdPrefix}0",
            Type = ComponentType.UserSelect,
            Placeholder = "管理対象メンバーを検索して選択",
            MinValues = 1,
            MaxValues = 1,
            IsDisabled = false
        };
        return new ComponentBuilder()
            .WithSelectMenu(select, row: 0)
            .WithButton("期間設定", $"{MemberManagePeriodOpenCustomIdPrefix}{currentTargetUserId}", ButtonStyle.Secondary, row: 1)
            .WithButton("非アクティブ一覧", MemberManageInactiveListCustomId, ButtonStyle.Secondary, row: 1);
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildMemberManageTargetPayloadAsync(
        SocketGuild guild,
        ulong targetUserId,
        string? notice,
        int? forcedSelectPage = null)
    {
        var target = guild.GetUser(targetUserId);
        if (target is null)
        {
            var notFoundEmbed = new EmbedBuilder()
                .WithTitle("メンバー管理")
                .WithColor(Color.Orange)
                .WithCurrentTimestamp()
                .WithDescription("対象ユーザーが見つかりません。再選択してください。")
                .Build();
            return (notFoundEmbed, BuildMemberManageSelectComponents(guild, page: 0));
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var memberRole = ResolveMemberRole(guild, settings);
        var provisionalRole = ResolveProvisionalRole(guild, settings);
        var memberState = "未登録";
        if (memberRole is not null && target.Roles.Any(role => role.Id == memberRole.Id))
        {
            memberState = "登録済み";
        }
        else if (provisionalRole is not null && target.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            memberState = "仮登録";
        }

        var timedOutUntil = target.TimedOutUntil;
        var timeoutState = timedOutUntil.HasValue && timedOutUntil.Value > DateTimeOffset.UtcNow
            ? $"ON (<t:{timedOutUntil.Value.ToUnixTimeSeconds()}:F>)"
            : "OFF";
        var nowUtc = DateTimeOffset.UtcNow;
        var lookbackDays = GetMemberManageLookbackDays(guild.Id, Context.User.Id);
        var lookback24h = nowUtc.AddHours(-24);
        var lookbackFromUtc = nowUtc.AddDays(-lookbackDays);
        var messageCount24hTask = _db.CountMessagesByUserAsync(guild.Id, target.Id, lookback24h, nowUtc, includeDeleted: false);
        var reactionCount24hTask = _db.CountReactionsByUserAsync(guild.Id, target.Id, lookback24h, nowUtc);
        var voice24hTask = _db.SummarizeVoiceByUserAsync(guild.Id, target.Id, lookback24h, nowUtc);
        var messageCountLookbackTask = _db.CountMessagesByUserAsync(guild.Id, target.Id, lookbackFromUtc, nowUtc, includeDeleted: false);
        var reactionCountLookbackTask = _db.CountReactionsByUserAsync(guild.Id, target.Id, lookbackFromUtc, nowUtc);
        var voiceLookbackTask = _db.SummarizeVoiceByUserAsync(guild.Id, target.Id, lookbackFromUtc, nowUtc);
        var messageHourlyLookbackTask = _db.ListMessageHourlyActivityByUserAsync(guild.Id, target.Id, lookbackFromUtc, nowUtc);
        var voiceHourlyLookbackTask = _db.ListVoiceHourlyActivityByUserAsync(guild.Id, target.Id, lookbackFromUtc, nowUtc);
        var timelineTask = _db.ListUserActivityTimelinesAsync(guild.Id, new[] { target.Id });
        await Task.WhenAll(
            messageCount24hTask,
            reactionCount24hTask,
            voice24hTask,
            messageCountLookbackTask,
            reactionCountLookbackTask,
            voiceLookbackTask,
            messageHourlyLookbackTask,
            voiceHourlyLookbackTask,
            timelineTask);

        var messageCount24h = messageCount24hTask.Result;
        var reactionCount24h = reactionCount24hTask.Result;
        var (voiceSessionCount24h, voiceDurationSeconds24h) = voice24hTask.Result;
        var messageCountLookback = messageCountLookbackTask.Result;
        var reactionCountLookback = reactionCountLookbackTask.Result;
        var (voiceSessionCountLookback, voiceDurationSecondsLookback) = voiceLookbackTask.Result;
        var messageHourlyLookback = messageHourlyLookbackTask.Result;
        var voiceHourlyLookback = voiceHourlyLookbackTask.Result;
        var voiceDurationMinutesLookback = (int)Math.Round(Math.Max(0L, voiceDurationSecondsLookback) / 60d, MidpointRounding.AwayFromZero);
        var activityTimeline = timelineTask.Result.FirstOrDefault();

        var alertLines = new List<string>();
        if (messageCount24h >= MemberManageMessageAlertThreshold24h)
        {
            alertLines.Add($"⚠️ 24h発言数: {messageCount24h}件 (閾値 {MemberManageMessageAlertThreshold24h})");
        }

        if (voiceSessionCount24h >= MemberManageVoiceSessionAlertThreshold24h)
        {
            alertLines.Add($"⚠️ 24hVC入退出: {voiceSessionCount24h}回 (閾値 {MemberManageVoiceSessionAlertThreshold24h})");
        }

        var voiceDurationMinutes24h = (int)Math.Round(Math.Max(0L, voiceDurationSeconds24h) / 60d, MidpointRounding.AwayFromZero);
        if (voiceDurationMinutes24h >= MemberManageVoiceDurationAlertMinutes24h)
        {
            alertLines.Add($"⚠️ 24hVC滞在: {voiceDurationMinutes24h}分 (閾値 {MemberManageVoiceDurationAlertMinutes24h}分)");
        }

        var notes = await _db.ListAdminMemberNotesAsync(guild.Id, target.Id, maxRows: 5);
        var roleText = target.Roles
            .Where(role => role.Id != guild.EveryoneRole.Id)
            .OrderByDescending(role => role.Position)
            .Select(role => role.Mention)
            .ToArray();
        var roleLines = roleText.Length == 0 ? "なし" : JoinWithinLimit(roleText, 980);

        var embed = new EmbedBuilder()
            .WithTitle("メンバー管理")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .WithAuthor(target.DisplayName, target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl())
            .AddField("対象", $"{target.Mention}\nID: `{target.Id}`", false)
            .AddField("登録状態", memberState, true)
            .AddField("タイムアウト", timeoutState, true)
            .AddField("参加日", target.JoinedAt.HasValue ? $"<t:{target.JoinedAt.Value.ToUnixTimeSeconds()}:F>" : "不明", true)
            .AddField("現在のロール", roleLines, false);

        var activitySummary = $"24h発言: {messageCount24h}件 / 24hリアクション: {reactionCount24h}件 / 24hVC入退出: {voiceSessionCount24h}回 / 24hVC滞在: {voiceDurationMinutes24h}分";
        embed.AddField("しきい値監視(24h)", activitySummary, false);
        embed.AddField(
            $"期間集計({lookbackDays}日)",
            $"発言: {messageCountLookback}件 / リアクション: {reactionCountLookback}件 / VC入室: {voiceSessionCountLookback}回 / VC滞在: {voiceDurationMinutesLookback}分",
            false);
        embed.AddField(
            $"よくいる時間帯({lookbackDays}日/JST)",
            BuildMemberManageFrequentHoursSummary(messageHourlyLookback, voiceHourlyLookback),
            false);
        embed.AddField("最終活動", BuildMemberManageLastActivitySummary(activityTimeline), false);

        if (alertLines.Count > 0)
        {
            embed.AddField("アラート", JoinWithinLimit(alertLines, 980), false);
            embed.AddField("推奨対応フロー", "1) タイムアウト設定\n2) 管理者メモ追記\n3) 再発時はキック/BANを検討", false);
        }

        if (timedOutUntil.HasValue && timedOutUntil.Value > DateTimeOffset.UtcNow)
        {
            embed.AddField(
                "解除フロー",
                $"解除予定: <t:{timedOutUntil.Value.ToUnixTimeSeconds()}:F>\n解除時は「タイムアウト解除」ボタンで復帰確認し、必要なら管理者メモに結果を残してください。",
                false);
        }

        embed.AddField("管理者メモ", BuildAdminMemberNotesSummary(notes), false);

        if (!string.IsNullOrWhiteSpace(notice))
        {
            embed.AddField("実行結果", notice, false);
        }

        var users = ListMemberManageSelectableUsers(guild);
        var resolvedSelectPage = forcedSelectPage ?? ResolveMemberManageSelectPage(users, targetUserId);
        var components = BuildMemberManageTargetComponents(guild, targetUserId, resolvedSelectPage);
        return (embed.Build(), components);
    }

    private static MessageComponent BuildMemberManageTargetComponents(SocketGuild guild, ulong targetUserId, int selectPage)
    {
        var builder = BuildMemberManageSelectComponentBuilder(guild, selectPage, currentTargetUserId: targetUserId, pagerRow: 1);
        return builder
            .WithButton("プロフィール", $"{MemberManageViewProfileCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 2)
            .WithButton("発言ログ", $"{MemberManageMessageLogCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 2)
            .WithButton("期間サマリ", $"{MemberManageSevenDaySummaryCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 2)
            .WithButton("処分履歴", $"{MemberManageModerationHistoryCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 2)
            .WithButton("更新", $"{MemberManageRefreshCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 2)
            .WithButton("登録済みにする", $"{MemberManageSetMemberRoleCustomIdPrefix}{targetUserId}", ButtonStyle.Success, row: 3)
            .WithButton("仮登録にする", $"{MemberManageSetProvisionalRoleCustomIdPrefix}{targetUserId}", ButtonStyle.Primary, row: 3)
            .WithButton("管理者メモ追加", $"{MemberManageAddNoteCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 3)
            .WithButton("タイムアウト設定", $"{MemberManageTimeoutOpenCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 3)
            .WithButton("タイムアウト解除", $"{MemberManageTimeoutClearCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 3)
            .WithButton("発言CSV", $"{MemberManageMessageCsvCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 4)
            .WithButton("VC入退出ログ", $"{MemberManageVoiceLogCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 4)
            .WithButton("VCログCSV", $"{MemberManageVoiceCsvCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 4)
            .WithButton("キック", $"{MemberManageKickCustomIdPrefix}{targetUserId}", ButtonStyle.Danger, row: 4)
            .WithButton("BAN", $"{MemberManageBanCustomIdPrefix}{targetUserId}", ButtonStyle.Danger, row: 4)
            .Build();
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildModerationTimelinePayloadAsync(SocketGuild guild, int page)
    {
        var totalCount = await _db.CountAdminModerationTimelineEntriesAsync(guild.Id);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)ModerationTimelinePageSize));
        var clampedPage = Math.Clamp(page, 0, totalPages - 1);
        var offsetRows = clampedPage * ModerationTimelinePageSize;
        var rows = await _db.ListAdminModerationTimelineAsync(guild.Id, offsetRows, ModerationTimelinePageSize);
        var pageStart = totalCount == 0 ? 0 : offsetRows + 1;
        var pageEnd = totalCount == 0 ? 0 : Math.Min(totalCount, offsetRows + rows.Count);

        var embed = new EmbedBuilder()
            .WithTitle("モデレーション時系列")
            .WithColor(Color.DarkRed)
            .WithCurrentTimestamp()
            .AddField("対象", "処分 / タイムアウト / ロール変更 / 管理者メモ", false)
            .AddField("件数", $"{totalCount}件", true)
            .AddField("表示", $"{pageStart}-{pageEnd}/{totalCount}", true)
            .AddField(
                $"履歴 (ページ {clampedPage + 1}/{totalPages})",
                BuildModerationTimelineSummary(rows),
                false)
            .Build();

        var components = BuildModerationTimelineComponents(clampedPage, totalPages);
        return (embed, components);
    }

    private static MessageComponent BuildModerationTimelineComponents(int currentPage, int totalPages)
    {
        var clampedTotalPages = Math.Max(1, totalPages);
        var clampedPage = Math.Clamp(currentPage, 0, clampedTotalPages - 1);
        return new ComponentBuilder()
            .WithButton(
                "前へ",
                $"{ModerationTimelinePageCustomIdPrefix}{clampedPage - 1}",
                ButtonStyle.Secondary,
                disabled: clampedPage <= 0,
                row: 0)
            .WithButton(
                $"{clampedPage + 1}/{clampedTotalPages}",
                "admin_moderation_timeline_page_indicator",
                ButtonStyle.Secondary,
                disabled: true,
                row: 0)
            .WithButton(
                "次へ",
                $"{ModerationTimelinePageCustomIdPrefix}{clampedPage + 1}",
                ButtonStyle.Secondary,
                disabled: clampedPage >= clampedTotalPages - 1,
                row: 0)
            .Build();
    }

    private static string BuildModerationTimelineSummary(IReadOnlyList<AdminModerationTimelineEntry> rows)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows.Select(row =>
        {
            var time = row.CreatedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
            var action = ResolveModerationActionLabel(row.ActionType);
            var actorName = string.IsNullOrWhiteSpace(row.ActorName) ? row.ActorUserId.ToString() : row.ActorName;
            var actor = row.ActorUserId == 0 ? actorName : $"<@{ToUlong(row.ActorUserId)}>";
            var detail = string.IsNullOrWhiteSpace(row.Detail) ? null : TruncateSingleLine(row.Detail, 48);
            var reason = string.IsNullOrWhiteSpace(row.Reason) ? null : TruncateSingleLine(row.Reason, 32);

            var suffixParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                suffixParts.Add(detail);
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                suffixParts.Add($"理由: {reason}");
            }

            var suffix = suffixParts.Count == 0 ? string.Empty : $" / {string.Join(" / ", suffixParts)}";
            return $"[{time}] <@{ToUlong(row.TargetUserId)}> {action} by {actor}{suffix}";
        });
        return JoinWithinLimit(lines, 980);
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildMemberManageMessageLogPayloadAsync(
        SocketGuild guild,
        ulong targetUserId,
        int page,
        int lookbackDays)
    {
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var days = Math.Clamp(lookbackDays, MemberManageLookbackDaysMin, MemberManageLookbackDaysMax);
        var fromUtcInclusive = toUtcExclusive.AddDays(-days);
        var rows = await _db.ListMessageLogsForUserExportAsync(
            guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive,
            maxRows: MemberManageCsvMaxRows,
            includeDeleted: true);

        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)MemberManageLogPreviewRows));
        var clampedPage = Math.Clamp(page, 0, totalPages - 1);
        var pageStart = rows.Count == 0 ? 0 : (clampedPage * MemberManageLogPreviewRows) + 1;
        var pageEnd = rows.Count == 0 ? 0 : Math.Min(rows.Count, (clampedPage + 1) * MemberManageLogPreviewRows);

        var embed = new EmbedBuilder()
            .WithTitle("メンバー発言ログ")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象", $"<@{targetUserId}>", true)
            .AddField("期間", $"直近{days}日", true)
            .AddField("取得件数", $"{rows.Count}件", true)
            .AddField(
                $"メッセージ({pageStart}-{pageEnd}/{rows.Count})",
                BuildMessageLogSearchLines(rows, MemberManageLogPreviewRows, page: clampedPage),
                false)
            .Build();
        var components = BuildMemberManageLogPagerComponents(
            MemberManageMessageLogPageCustomIdPrefix,
            targetUserId,
            clampedPage,
            totalPages);
        return (embed, components);
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildMemberManageVoiceLogPayloadAsync(
        SocketGuild guild,
        ulong targetUserId,
        int page,
        int lookbackDays)
    {
        var toUtcExclusive = DateTimeOffset.UtcNow;
        var days = Math.Clamp(lookbackDays, MemberManageLookbackDaysMin, MemberManageLookbackDaysMax);
        var fromUtcInclusive = toUtcExclusive.AddDays(-days);
        var rows = await _db.ListVoiceSessionsForUserExportAsync(
            guild.Id,
            targetUserId,
            fromUtcInclusive,
            toUtcExclusive,
            maxRows: MemberManageCsvMaxRows);

        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)MemberManageLogPreviewRows));
        var clampedPage = Math.Clamp(page, 0, totalPages - 1);
        var pageStart = rows.Count == 0 ? 0 : (clampedPage * MemberManageLogPreviewRows) + 1;
        var pageEnd = rows.Count == 0 ? 0 : Math.Min(rows.Count, (clampedPage + 1) * MemberManageLogPreviewRows);

        var embed = new EmbedBuilder()
            .WithTitle("メンバーVC入退出ログ")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象", $"<@{targetUserId}>", true)
            .AddField("期間", $"直近{days}日", true)
            .AddField("取得件数", $"{rows.Count}件", true)
            .AddField(
                $"VCセッション({pageStart}-{pageEnd}/{rows.Count})",
                BuildVoiceLogSearchLines(rows, MemberManageLogPreviewRows, toUtcExclusive, page: clampedPage),
                false)
            .Build();
        var components = BuildMemberManageLogPagerComponents(
            MemberManageVoiceLogPageCustomIdPrefix,
            targetUserId,
            clampedPage,
            totalPages);
        return (embed, components);
    }

    private static MessageComponent BuildMemberManageLogPagerComponents(
        string customIdPrefix,
        ulong targetUserId,
        int currentPage,
        int totalPages)
    {
        var clampedTotalPages = Math.Max(1, totalPages);
        var clampedPage = Math.Clamp(currentPage, 0, clampedTotalPages - 1);

        return new ComponentBuilder()
            .WithButton(
                "前へ",
                $"{customIdPrefix}{targetUserId}:{clampedPage - 1}",
                ButtonStyle.Secondary,
                disabled: clampedPage <= 0,
                row: 0)
            .WithButton(
                $"{clampedPage + 1}/{clampedTotalPages}",
                "admin_member_manage_log_page_indicator",
                ButtonStyle.Secondary,
                disabled: true,
                row: 0)
            .WithButton(
                "次へ",
                $"{customIdPrefix}{targetUserId}:{clampedPage + 1}",
                ButtonStyle.Secondary,
                disabled: clampedPage >= clampedTotalPages - 1,
                row: 0)
            .Build();
    }

    private async Task ShowMemberRegistrationStateDangerConfirmAsync(string userIdRaw, bool setMemberRole)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var memberRole = ResolveMemberRole(Context.Guild, settings);
        var provisionalRole = ResolveProvisionalRole(Context.Guild, settings);

        if (setMemberRole && memberRole is null)
        {
            await RespondAsync("メンバーロールが見つかりません。", ephemeral: true);
            return;
        }

        if (!setMemberRole && provisionalRole is null)
        {
            await RespondAsync("仮登録ロールが見つかりません。", ephemeral: true);
            return;
        }

        var actionLabel = setMemberRole ? "登録済みにする" : "仮登録にする";
        var detail = setMemberRole
            ? $"付与: {(memberRole is null ? "-" : $"`{memberRole.Name}`")} / 解除: {(provisionalRole is null ? "-" : $"`{provisionalRole.Name}`")}"
            : $"付与: {(provisionalRole is null ? "-" : $"`{provisionalRole.Name}`")} / 解除: {(memberRole is null ? "-" : $"`{memberRole.Name}`")}";

        var embed = new EmbedBuilder()
            .WithTitle($"メンバー管理: {actionLabel}確認")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .WithDescription($"{target.Mention} の登録状態を変更します。")
            .AddField("変更内容", detail, false)
            .AddField("注意", "この操作はロール剥奪/付与を行います。", false)
            .Build();
        var payload = $"{(setMemberRole ? "member" : "provisional")}:{targetUserId}";
        var components = new ComponentBuilder()
            .WithButton(
                $"はい、{actionLabel}",
                $"{MemberManageStateDangerConfirmCustomIdPrefix}{payload}",
                ButtonStyle.Danger,
                row: 0)
            .WithButton("キャンセル", $"{MemberManageStateDangerCancelCustomIdPrefix}{targetUserId}", ButtonStyle.Secondary, row: 0)
            .Build();
        await RespondAsync(embed: embed, components: components, ephemeral: true);
    }

    private static bool TryParseMemberManageStateDangerPayload(string payloadRaw, out bool setMemberRole, out ulong targetUserId)
    {
        setMemberRole = false;
        targetUserId = 0;
        if (string.IsNullOrWhiteSpace(payloadRaw))
        {
            return false;
        }

        var parts = payloadRaw.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !ulong.TryParse(parts[1], out targetUserId))
        {
            return false;
        }

        setMemberRole = parts[0] switch
        {
            "member" => true,
            "provisional" => false,
            _ => setMemberRole
        };
        return parts[0] is "member" or "provisional";
    }

    private async Task SetMemberRegistrationStateAsync(string userIdRaw, bool setMemberRole)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var memberRole = ResolveMemberRole(Context.Guild, settings);
        var provisionalRole = ResolveProvisionalRole(Context.Guild, settings);
        if (setMemberRole && memberRole is null)
        {
            await RespondAsync("メンバーロールが見つかりません。", ephemeral: true);
            return;
        }

        if (!setMemberRole && provisionalRole is null)
        {
            await RespondAsync("仮登録ロールが見つかりません。", ephemeral: true);
            return;
        }

        try
        {
            var notices = new List<string>();

            if (setMemberRole)
            {
                if (provisionalRole is not null && target.Roles.Any(role => role.Id == provisionalRole.Id))
                {
                    if (!TryValidateRoleOperation(Context.Guild, target, provisionalRole, out var removeReason))
                    {
                        await RespondAsync($"仮登録ロール解除に失敗: {removeReason}", ephemeral: true);
                        return;
                    }

                    await target.RemoveRoleAsync(provisionalRole);
                    notices.Add($"`{provisionalRole.Name}` を解除");
                }

                if (memberRole is not null && !target.Roles.Any(role => role.Id == memberRole.Id))
                {
                    if (!TryValidateRoleOperation(Context.Guild, target, memberRole, out var grantReason))
                    {
                        await RespondAsync($"メンバーロール付与に失敗: {grantReason}", ephemeral: true);
                        return;
                    }

                    await target.AddRoleAsync(memberRole);
                    notices.Add($"`{memberRole.Name}` を付与");
                }
            }
            else
            {
                if (memberRole is not null && target.Roles.Any(role => role.Id == memberRole.Id))
                {
                    if (!TryValidateRoleOperation(Context.Guild, target, memberRole, out var removeReason))
                    {
                        await RespondAsync($"メンバーロール解除に失敗: {removeReason}", ephemeral: true);
                        return;
                    }

                    await target.RemoveRoleAsync(memberRole);
                    notices.Add($"`{memberRole.Name}` を解除");
                }

                if (provisionalRole is not null && !target.Roles.Any(role => role.Id == provisionalRole.Id))
                {
                    if (!TryValidateRoleOperation(Context.Guild, target, provisionalRole, out var grantReason))
                    {
                        await RespondAsync($"仮登録ロール付与に失敗: {grantReason}", ephemeral: true);
                        return;
                    }

                    await target.AddRoleAsync(provisionalRole);
                    notices.Add($"`{provisionalRole.Name}` を付与");
                }
            }

            var notice = notices.Count == 0
                ? $"{target.Mention} の登録状態に変更はありません。"
                : $"{target.Mention} の登録状態を更新しました。\n- {string.Join("\n- ", notices)}";
            if (notices.Count > 0)
            {
                _ = await _db.AddAdminMemberModerationActionAsync(
                    Context.Guild.Id,
                    target.Id,
                    Context.User.Id,
                    Context.User.ToString(),
                    setMemberRole ? "role_set_member" : "role_set_provisional",
                    detail: string.Join(" / ", notices.Select(x => TruncateSingleLine(x, 80))));
            }
            await UpdateMemberManageTargetPanelAsync(userIdRaw, notice);
            await SendMemberManageAuditAsync("メンバー管理: 登録状態変更", target.Id, notice);
        }
        catch
        {
            await RespondAsync("登録状態の更新に失敗しました。", ephemeral: true);
        }
    }

    private async Task OpenMemberManageDangerReasonModalAsync(string action, string userIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser actor || !actor.GuildPermissions.Administrator)
        {
            await RespondAsync("キック/BANはDiscordの管理者権限を持つ管理者のみ実行できます。", ephemeral: true);
            return;
        }

        if (!TryParseUserId(userIdRaw, out var targetUserId))
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(targetUserId);
        if (target is null)
        {
            await RespondAsync("対象ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        var actionLabel = action == "ban" ? "BAN" : action == "kick" ? "キック" : string.Empty;
        if (string.IsNullOrEmpty(actionLabel))
        {
            await RespondAsync("操作種別が不正です。", ephemeral: true);
            return;
        }

        if (action == "kick" && !TryValidateKickOperation(Context.Guild, target, out var kickReason))
        {
            await RespondAsync($"キックを実行できません: {kickReason}", ephemeral: true);
            return;
        }

        if (action == "ban" && !TryValidateBanOperation(Context.Guild, target, out var banReason))
        {
            await RespondAsync($"BANを実行できません: {banReason}", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<MemberManageDangerReasonModal>($"{MemberManageDangerReasonModalCustomIdPrefix}{action}|{targetUserId}");
    }

    private static Embed BuildMemberManageResultEmbed(string message)
    {
        return new EmbedBuilder()
            .WithTitle("メンバー管理")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .WithDescription(message)
            .Build();
    }

    private static string BuildAdminMemberNotesSummary(IReadOnlyList<AdminMemberNote> notes)
    {
        if (notes.Count == 0)
        {
            return "なし";
        }

        var lines = notes
            .Select(note =>
            {
                var createdJst = note.CreatedAt.ToOffset(JapanTimeOffset);
                var body = TruncateSingleLine(note.Note, 80);
                return $"[{createdJst:MM/dd HH:mm}] {note.AuthorName}: {body}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static string BuildPodiumCountLine(ActivityRankingPodiumCount count)
    {
        return $"🥇 {count.FirstCount}回 / 🥈 {count.SecondCount}回 / 🥉 {count.ThirdCount}回";
    }

    private static string BuildMemberModerationHistorySummary(IReadOnlyList<AdminMemberModerationAction> rows)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows.Select(row =>
        {
            var time = row.CreatedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
            var action = ResolveModerationActionLabel(row.ActionType);
            var actor = string.IsNullOrWhiteSpace(row.ActorName) ? row.ActorUserId.ToString() : row.ActorName;
            var detail = string.IsNullOrWhiteSpace(row.Detail) ? null : TruncateSingleLine(row.Detail, 42);
            var reason = string.IsNullOrWhiteSpace(row.Reason) ? null : TruncateSingleLine(row.Reason, 42);
            var suffix = string.Empty;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                suffix += $" / {detail}";
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                suffix += $" / 理由: {reason}";
            }

            return $"[{time}] {action} by {actor}{suffix}";
        });
        return JoinWithinLimit(lines, 980);
    }

    private static string ResolveModerationActionLabel(string? actionType)
    {
        return actionType?.Trim().ToLowerInvariant() switch
        {
            "timeout_set" => "タイムアウト設定",
            "timeout_clear" => "タイムアウト解除",
            "kick" => "キック",
            "ban" => "BAN",
            "role_set_member" => "登録済みに変更",
            "role_set_provisional" => "仮登録に変更",
            "freeze_on" => "凍結ON",
            "freeze_off" => "凍結OFF",
            "voice_notify_off" => "VC通知表示OFF",
            "admin_note" => "管理者メモ",
            _ => "操作"
        };
    }

    private void PruneMemberManageDangerContexts()
    {
        var threshold = DateTimeOffset.UtcNow - MemberManageContextTimeout;
        foreach (var pair in _memberDangerContexts)
        {
            if (pair.Value.CreatedAt < threshold)
            {
                _memberDangerContexts.TryRemove(pair.Key, out _);
            }
        }
    }

    private void PruneMemberManageBulkSelections()
    {
        var threshold = DateTimeOffset.UtcNow - MemberManageContextTimeout;
        foreach (var pair in _memberBulkSelections)
        {
            if (pair.Value.CreatedAt < threshold)
            {
                _memberBulkSelections.TryRemove(pair.Key, out _);
            }
        }
    }

    private Task SendMemberManageAuditAsync(string title, ulong targetUserId, string detail)
    {
        return Task.CompletedTask;
    }

    private async Task<IRole?> EnsureFreezeRoleAsync(SocketGuild guild)
    {
        IRole? freezeRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, FreezeRoleName, StringComparison.OrdinalIgnoreCase));
        if (freezeRole is null)
        {
            freezeRole = await guild.CreateRoleAsync(
                FreezeRoleName,
                GuildPermissions.None,
                color: Color.DarkGrey,
                isHoisted: false,
                isMentionable: false);
        }

        await ApplyFreezeRoleOverwritesAsync(guild, freezeRole);
        return freezeRole;
    }

    private static async Task ApplyFreezeRoleOverwritesAsync(SocketGuild guild, IRole freezeRole)
    {
        var deny = new OverwritePermissions(
            sendMessages: PermValue.Deny,
            sendMessagesInThreads: PermValue.Deny,
            createPublicThreads: PermValue.Deny,
            createPrivateThreads: PermValue.Deny,
            addReactions: PermValue.Deny,
            connect: PermValue.Deny,
            speak: PermValue.Deny,
            stream: PermValue.Deny,
            useApplicationCommands: PermValue.Deny);

        foreach (var channel in guild.Channels)
        {
            try
            {
                await channel.AddPermissionOverwriteAsync(freezeRole, deny);
            }
            catch
            {
            }
        }
    }

    private static Embed BuildMemberManageBulkEmbed(IReadOnlyList<ulong>? targetIds, string status)
    {
        var targetLine = targetIds is not { Count: > 0 }
            ? "未選択"
            : JoinWithinLimit(targetIds.Select(id => $"<@{id}>"), 950);

        return new EmbedBuilder()
            .WithTitle("メンバー管理: 一括操作")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .AddField("対象", targetLine, false)
            .AddField("状態", status, false)
            .Build();
    }

    private static MessageComponent BuildMemberManageBulkComponents()
    {
        var select = new SelectMenuBuilder
        {
            CustomId = MemberManageBulkUserSelectCustomId,
            Type = ComponentType.UserSelect,
            Placeholder = "一括操作の対象を選択 (最大25)",
            MinValues = 1,
            MaxValues = 25,
            IsDisabled = false
        };

        return new ComponentBuilder()
            .WithSelectMenu(select, row: 0)
            .WithButton("登録済みにする", MemberManageBulkSetMemberCustomId, ButtonStyle.Success, row: 1)
            .WithButton("仮登録にする", MemberManageBulkSetProvisionalCustomId, ButtonStyle.Primary, row: 1)
            .WithButton("凍結ON", MemberManageBulkFreezeOnCustomId, ButtonStyle.Danger, row: 1)
            .WithButton("凍結OFF", MemberManageBulkFreezeOffCustomId, ButtonStyle.Success, row: 1)
            .WithButton("VC通知表示OFF", MemberManageBulkVoiceNotifyOffCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("タイムアウト解除", MemberManageBulkTimeoutClearCustomId, ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private async Task ShowMemberManageBulkDangerConfirmAsync(string action, string operationLabel)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneMemberManageBulkSelections();
        if (!_memberBulkSelections.TryGetValue(Context.User.Id, out var selection) ||
            selection.GuildId != Context.Guild.Id ||
            selection.TargetUserIds.Count == 0)
        {
            await RespondAsync("一括対象が未選択です。先にユーザーを選択してください。", ephemeral: true);
            return;
        }

        var confirmEmbed = new EmbedBuilder()
            .WithTitle("メンバー管理: 一括操作確認")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .WithDescription($"`{operationLabel}` を {selection.TargetUserIds.Count} 人に実行します。")
            .AddField("注意", "この操作は大量変更になります。最終確認後に実行されます。", false)
            .Build();
        var components = new ComponentBuilder()
            .WithButton(
                $"はい、{operationLabel}",
                $"{MemberManageBulkDangerConfirmCustomIdPrefix}{action}",
                ButtonStyle.Danger,
                row: 0)
            .WithButton("キャンセル", MemberManageBulkDangerCancelCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
        await RespondAsync(embed: confirmEmbed, components: components, ephemeral: true);
    }

    private async Task ExecuteMemberManageBulkOperationAsync(string action)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryResolveMemberManageBulkOperation(action, out var operationLabel, out var operation))
        {
            await RespondAsync("操作種別が不正です。", ephemeral: true);
            return;
        }

        PruneMemberManageBulkSelections();
        if (!_memberBulkSelections.TryGetValue(Context.User.Id, out var selection) ||
            selection.GuildId != Context.Guild.Id ||
            selection.TargetUserIds.Count == 0)
        {
            await RespondAsync("一括対象が未選択です。先にユーザーを選択してください。", ephemeral: true);
            return;
        }

        var success = 0;
        var failed = 0;
        var skipped = 0;
        var actionType = ResolveBulkModerationActionType(action);
        foreach (var targetUserId in selection.TargetUserIds)
        {
            var target = Context.Guild.GetUser(targetUserId);
            if (target is null)
            {
                skipped++;
                continue;
            }

            try
            {
                var updated = await operation(Context.Guild, target);
                if (updated)
                {
                    success++;
                    if (!string.IsNullOrWhiteSpace(actionType))
                    {
                        _ = await _db.AddAdminMemberModerationActionAsync(
                            Context.Guild.Id,
                            target.Id,
                            Context.User.Id,
                            Context.User.ToString(),
                            actionType,
                            detail: "一括操作");
                    }
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                failed++;
            }
        }

        var status = $"{operationLabel} 実行: 成功 {success} / 失敗 {failed} / 変更なし {skipped}";
        await RespondAsync(
            embed: BuildMemberManageBulkEmbed(selection.TargetUserIds, status),
            components: BuildMemberManageBulkComponents(),
            ephemeral: true);
        await SendMemberManageAuditAsync(
            "メンバー管理: 一括操作",
            0,
            $"{operationLabel} / 対象{selection.TargetUserIds.Count}人 / 成功{success} / 失敗{failed} / 変更なし{skipped}");
    }

    private bool TryResolveMemberManageBulkOperation(
        string action,
        out string operationLabel,
        out Func<SocketGuild, SocketGuildUser, Task<bool>> operation)
    {
        operationLabel = string.Empty;
        operation = BulkSetMemberRoleInternalAsync;

        switch (action)
        {
            case "set_member":
                operationLabel = "登録済みにする";
                operation = BulkSetMemberRoleInternalAsync;
                return true;
            case "set_provisional":
                operationLabel = "仮登録にする";
                operation = BulkSetProvisionalRoleInternalAsync;
                return true;
            case "freeze_on":
                operationLabel = "凍結ON";
                operation = BulkFreezeOnInternalAsync;
                return true;
            case "freeze_off":
                operationLabel = "凍結OFF";
                operation = BulkFreezeOffInternalAsync;
                return true;
            case "voice_notify_off":
                operationLabel = "VC通知表示OFF";
                operation = BulkVoiceNotifyOffInternalAsync;
                return true;
            case "timeout_clear":
                operationLabel = "タイムアウト解除";
                operation = BulkTimeoutClearInternalAsync;
                return true;
            default:
                return false;
        }
    }

    private static string ResolveBulkModerationActionType(string action)
    {
        return action switch
        {
            "set_member" => "role_set_member",
            "set_provisional" => "role_set_provisional",
            "freeze_on" => "freeze_on",
            "freeze_off" => "freeze_off",
            "voice_notify_off" => "voice_notify_off",
            "timeout_clear" => "timeout_clear",
            _ => string.Empty
        };
    }

    private async Task<bool> BulkSetMemberRoleInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var memberRole = ResolveMemberRole(guild, settings);
        var provisionalRole = ResolveProvisionalRole(guild, settings);
        if (memberRole is null)
        {
            return false;
        }

        var changed = false;
        if (provisionalRole is not null && target.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            if (!TryValidateRoleOperation(guild, target, provisionalRole, out _))
            {
                return false;
            }

            await target.RemoveRoleAsync(provisionalRole);
            changed = true;
        }

        if (!target.Roles.Any(role => role.Id == memberRole.Id))
        {
            if (!TryValidateRoleOperation(guild, target, memberRole, out _))
            {
                return false;
            }

            await target.AddRoleAsync(memberRole);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> BulkSetProvisionalRoleInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var memberRole = ResolveMemberRole(guild, settings);
        var provisionalRole = ResolveProvisionalRole(guild, settings);
        if (provisionalRole is null)
        {
            return false;
        }

        var changed = false;
        if (memberRole is not null && target.Roles.Any(role => role.Id == memberRole.Id))
        {
            if (!TryValidateRoleOperation(guild, target, memberRole, out _))
            {
                return false;
            }

            await target.RemoveRoleAsync(memberRole);
            changed = true;
        }

        if (!target.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            if (!TryValidateRoleOperation(guild, target, provisionalRole, out _))
            {
                return false;
            }

            await target.AddRoleAsync(provisionalRole);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> BulkFreezeOnInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        var freezeRole = await EnsureFreezeRoleAsync(guild);
        if (freezeRole is null)
        {
            return false;
        }

        if (target.Roles.Any(role => role.Id == freezeRole.Id))
        {
            return false;
        }

        if (!TryValidateRoleOperation(guild, target, freezeRole, out _))
        {
            return false;
        }

        await target.AddRoleAsync(freezeRole);
        return true;
    }

    private async Task<bool> BulkFreezeOffInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        var freezeRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, FreezeRoleName, StringComparison.OrdinalIgnoreCase));
        if (freezeRole is null)
        {
            return false;
        }

        if (!target.Roles.Any(role => role.Id == freezeRole.Id))
        {
            return false;
        }

        if (!TryValidateRoleOperation(guild, target, freezeRole, out _))
        {
            return false;
        }

        await target.RemoveRoleAsync(freezeRole);
        return true;
    }

    private async Task<bool> BulkVoiceNotifyOffInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        var current = await _db.GetVoiceNotificationEnabledAsync(target.Id);
        if (!current)
        {
            return false;
        }

        await _db.SetVoiceNotificationEnabledAsync(target.Id, enabled: false);
        return true;
    }

    private async Task<bool> BulkTimeoutClearInternalAsync(SocketGuild guild, SocketGuildUser target)
    {
        if (target.TimedOutUntil is not DateTimeOffset timeout || timeout <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (!TryValidateTimeoutOperation(guild, target, out _))
        {
            return false;
        }

        await target.ModifyAsync(props => props.TimedOutUntil = null);
        return true;
    }

    private async Task<IRole?> ResolveOrCreateVoiceNotificationRoleAsync(SocketGuild guild)
    {
        var roleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName)
            ? "通話通知"
            : _options.VoiceNotificationRoleName;
        var existing = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        if (!_options.AutoCreateVoiceNotificationRole)
        {
            return null;
        }

        return await guild.CreateRoleAsync(
            roleName,
            GuildPermissions.None,
            color: new Color(52, 152, 219),
            isHoisted: false,
            isMentionable: true);
    }

    private static List<SocketGuildUser> ListMemberManageSelectableUsers(SocketGuild guild)
    {
        return guild.Users
            .OrderBy(
                user => string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Id)
            .ToList();
    }

    private static int ResolveMemberManageSelectPage(IReadOnlyList<SocketGuildUser> users, ulong targetUserId)
    {
        if (users.Count == 0 || targetUserId == 0)
        {
            return 0;
        }

        var index = -1;
        for (var i = 0; i < users.Count; i++)
        {
            if (users[i].Id == targetUserId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return 0;
        }

        return index / MemberManageSelectPageSize;
    }

    private static bool TryParseMemberManageSelectPagePayload(string raw, out int page, out ulong targetUserId)
    {
        page = 0;
        targetUserId = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out page) || page < 0)
        {
            return false;
        }

        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return true;
        }

        return ulong.TryParse(parts[1], out targetUserId);
    }

    private static bool TryParseMemberManageLogPagePayload(string raw, out ulong targetUserId, out int page)
    {
        targetUserId = 0;
        page = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        return ulong.TryParse(parts[0], out targetUserId) &&
               targetUserId != 0 &&
               int.TryParse(parts[1], out page);
    }

    private static bool TryParseUserId(string raw, out ulong userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var token = raw.Trim();
        if (token.StartsWith("<@", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
        {
            token = token[2..^1];
            if (token.StartsWith("!", StringComparison.Ordinal))
            {
                token = token[1..];
            }
        }

        return ulong.TryParse(token, out userId) && userId != 0;
    }

    private static string BuildMessageLogCsv(IReadOnlyList<MessageLog> rows)
    {
        var sb = new StringBuilder(capacity: Math.Max(1024, rows.Count * 128));
        sb.AppendLine("created_at_utc,message_id,channel_id,author_id,author_name,author_display_name,content,attachment_count,embed_count,attachments_json,embeds_json,edited_at_utc,deleted_at_utc");

        foreach (var row in rows)
        {
            var displayName = string.IsNullOrWhiteSpace(row.AuthorDisplayName) ? row.AuthorName : row.AuthorDisplayName;
            sb.Append(ToCsvCell(row.CreatedAt.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.MessageId.ToString())).Append(',')
              .Append(ToCsvCell(row.ChannelId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorName)).Append(',')
              .Append(ToCsvCell(displayName)).Append(',')
              .Append(ToCsvCell(row.Content)).Append(',')
              .Append(ToCsvCell(row.AttachmentCount.ToString())).Append(',')
              .Append(ToCsvCell(row.EmbedCount.ToString())).Append(',')
              .Append(ToCsvCell(row.AttachmentsJson)).Append(',')
              .Append(ToCsvCell(row.EmbedsJson)).Append(',')
              .Append(ToCsvCell(row.EditedAt?.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.DeletedAt?.ToUniversalTime().ToString("O")))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildVoiceSessionCsv(IReadOnlyList<VoiceSession> rows, DateTimeOffset toUtcExclusive)
    {
        var sb = new StringBuilder(capacity: Math.Max(1024, rows.Count * 96));
        sb.AppendLine("joined_at_utc,left_at_utc,guild_id,channel_id,user_id,user_name,duration_seconds");

        foreach (var row in rows)
        {
            var durationSeconds = row.DurationSeconds
                ?? (int)Math.Max(0, (toUtcExclusive - row.JoinedAt).TotalSeconds);

            sb.Append(ToCsvCell(row.JoinedAt.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.LeftAt?.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.GuildId.ToString())).Append(',')
              .Append(ToCsvCell(row.ChannelId.ToString())).Append(',')
              .Append(ToCsvCell(row.UserId.ToString())).Append(',')
              .Append(ToCsvCell(row.UserName)).Append(',')
              .Append(ToCsvCell(Math.Max(0, durationSeconds).ToString()))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string ToCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string BuildMessageLogSearchLines(IReadOnlyList<MessageLog> rows, int maxRows, int page = 0)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var takeCount = Math.Clamp(maxRows, 1, 30);
        var skipCount = Math.Max(0, page) * takeCount;
        var lines = rows
            .Skip(skipCount)
            .Take(takeCount)
            .Select(row =>
            {
                var time = row.CreatedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
                var content = TruncateSingleLine(string.IsNullOrWhiteSpace(row.Content) ? "(no content)" : row.Content, 60);
                var deleted = row.DeletedAt.HasValue ? " [deleted]" : string.Empty;
                return $"[{time}] <#{ToUlong(row.ChannelId)}> <@{ToUlong(row.AuthorId)}> {content}{deleted}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static string BuildVoiceLogSearchLines(IReadOnlyList<VoiceSession> rows, int maxRows, DateTimeOffset toUtcExclusive, int page = 0)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var takeCount = Math.Clamp(maxRows, 1, 30);
        var skipCount = Math.Max(0, page) * takeCount;
        var lines = rows
            .Skip(skipCount)
            .Take(takeCount)
            .Select(row =>
            {
                var joined = row.JoinedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
                var left = row.LeftAt?.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm") ?? "進行中";
                var durationSeconds = row.DurationSeconds
                    ?? (int)Math.Max(0, (toUtcExclusive - row.JoinedAt).TotalSeconds);
                var durationText = FormatDuration(durationSeconds);
                return $"[{joined}] <#{ToUlong(row.ChannelId)}> <@{ToUlong(row.UserId)}> {left} / {durationText}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static IReadOnlyList<VoiceHourlyActivity> BuildHourlyVoiceActivity(IReadOnlyList<VoiceHourlyActivity> rows)
    {
        var map = Enumerable.Range(0, 24)
            .Select(hour => new VoiceHourlyActivity
            {
                Hour = hour,
                TotalSeconds = 0,
                SessionCount = 0,
                ActiveUsers = 0
            })
            .ToDictionary(item => item.Hour, item => item);

        foreach (var row in rows)
        {
            if (!map.ContainsKey(row.Hour))
            {
                continue;
            }

            map[row.Hour] = new VoiceHourlyActivity
            {
                Hour = row.Hour,
                TotalSeconds = Math.Max(0, row.TotalSeconds),
                SessionCount = Math.Max(0, row.SessionCount),
                ActiveUsers = Math.Max(0, row.ActiveUsers)
            };
        }

        return map.Values
            .OrderBy(item => item.Hour)
            .ToArray();
    }

    private static double[,] BuildWeekdayHourSecondsMatrix(IReadOnlyList<VoiceWeekdayHourlyActivity> rows)
    {
        var matrix = new double[7, 24];
        foreach (var row in rows)
        {
            if (row.Weekday < 0 || row.Weekday >= 7 || row.Hour < 0 || row.Hour >= 24)
            {
                continue;
            }

            matrix[row.Weekday, row.Hour] = Math.Max(0, row.TotalSeconds);
        }

        return matrix;
    }

    private async Task<IReadOnlyList<VoiceRegularChartItem>> BuildVoiceRegularChartItemsAsync(IReadOnlyList<VoiceUserHourSummary> rows)
    {
        if (Context.Guild is null || rows.Count == 0)
        {
            return Array.Empty<VoiceRegularChartItem>();
        }

        var targets = rows.Take(4).ToArray();
        var tasks = targets.Select(BuildVoiceRegularChartItemAsync).ToArray();
        var items = await Task.WhenAll(tasks);
        return items;
    }

    private async Task<VoiceRegularChartItem> BuildVoiceRegularChartItemAsync(VoiceUserHourSummary row)
    {
        var userId = ToUlong(row.UserId);
        var durationSeconds = Math.Max(0, row.DurationSeconds);
        try
        {
            if (Context.Guild is null)
            {
                return new VoiceRegularChartItem($"User {userId}", durationSeconds, null);
            }

            IGuildUser? guildUser = Context.Guild.GetUser(userId);
            if (guildUser is null)
            {
                guildUser = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, userId);
            }

            var displayName = guildUser?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = guildUser?.Username;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = $"ユーザー {userId}";
            }

            var avatarUrl = guildUser?.GetDisplayAvatarUrl(ImageFormat.Png, 128) ?? guildUser?.GetDefaultAvatarUrl();
            var avatarBytes = await TryDownloadImageBytesAsync(avatarUrl);
            return new VoiceRegularChartItem(displayName, durationSeconds, avatarBytes);
        }
        catch
        {
            return new VoiceRegularChartItem($"ユーザー {userId}", durationSeconds, null);
        }
    }

    private static async Task<byte[]?> BuildVoiceHourlyChartPngAsync(
        IReadOnlyList<VoiceHourlyActivity> rows,
        double[,] weekdayHourSeconds,
        int lookbackDays,
        int highlightHourJst,
        IReadOnlyList<VoiceRegularChartItem> regularItems)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        const int width = 1600;
        const int height = 1920;
        const float marginX = 70f;
        var panelWidth = width - (marginX * 2f);
        var cardsY = 130f;
        var cardsHeight = 150f;
        var usersPanelY = 320f;
        var panelHeight = 300f;
        var vcTimePanelY = 670f;
        var heatmapPanelY = 1020f;
        var heatmapPanelHeight = 410f;
        var regularPanelY = 1480f;
        var regularPanelHeight = 390f;
        var maxUsers = Math.Max(1, rows.Max(x => x.ActiveUsers));
        var maxHours = Math.Max(1.0, rows.Max(x => x.TotalSeconds / 3600.0));

        using var image = new SixLabors.ImageSharp.Image<Rgba32>(width, height, new Rgba32(246, 248, 251, 255));
        image.Mutate(ctx =>
        {
            var titleFont = ResolveChartFont(48, FontStyle.Bold);
            var subFont = ResolveChartFont(24, FontStyle.Regular);
            var axisFont = ResolveChartFont(20, FontStyle.Regular);
            var captionFont = ResolveChartFont(24, FontStyle.Bold);
            var infoFont = ResolveChartFont(19, FontStyle.Regular);

            ctx.DrawText("VC時間帯ダッシュボード", titleFont, SixLabors.ImageSharp.Color.Black, new SixLabors.ImageSharp.PointF(marginX, 44));
            ctx.DrawText($"直近{lookbackDays}日 / JST / 注目時間: {highlightHourJst:00}:00", subFont, SixLabors.ImageSharp.Color.FromRgb(78, 83, 91), new SixLabors.ImageSharp.PointF(marginX + 2f, 98));

            DrawVoiceMetricCards(
                ctx,
                rows,
                highlightHourJst,
                new SixLabors.ImageSharp.RectangleF(marginX, cardsY, panelWidth, cardsHeight),
                captionFont,
                infoFont);

            DrawHourlyBarPanel(
                ctx,
                rows,
                new SixLabors.ImageSharp.RectangleF(marginX, usersPanelY, panelWidth, panelHeight),
                valueSelector: row => row.ActiveUsers,
                maxValue: maxUsers,
                barColor: new Rgba32(46, 134, 222, 255),
                panelTitle: "時間帯別アクティブ人数",
                peakLabelPrefix: "最多人数",
                highlightHourJst: highlightHourJst,
                axisFont: axisFont,
                captionFont: captionFont,
                valueFormatter: value => $"{value:0}人");

            DrawHourlyBarPanel(
                ctx,
                rows,
                new SixLabors.ImageSharp.RectangleF(marginX, vcTimePanelY, panelWidth, panelHeight),
                valueSelector: row => row.TotalSeconds / 3600.0,
                maxValue: maxHours,
                barColor: new Rgba32(239, 143, 47, 255),
                panelTitle: "時間帯別VC滞在時間 (時間)",
                peakLabelPrefix: "最長VC滞在",
                highlightHourJst: highlightHourJst,
                axisFont: axisFont,
                captionFont: captionFont,
                valueFormatter: value => $"{value:0.#}時間");

            DrawWeekdayHeatmapPanel(
                ctx,
                weekdayHourSeconds,
                new SixLabors.ImageSharp.RectangleF(marginX, heatmapPanelY, panelWidth, heatmapPanelHeight),
                axisFont,
                captionFont);

            DrawHourRegularsPanel(
                ctx,
                regularItems,
                highlightHourJst,
                new SixLabors.ImageSharp.RectangleF(marginX, regularPanelY, panelWidth, regularPanelHeight),
                axisFont,
                captionFont);
        });

        using var stream = new MemoryStream();
        await image.SaveAsync(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static void DrawVoiceMetricCards(
        IImageProcessingContext ctx,
        IReadOnlyList<VoiceHourlyActivity> rows,
        int highlightHourJst,
        SixLabors.ImageSharp.RectangleF area,
        Font titleFont,
        Font valueFont)
    {
        var safeRows = rows.Count == 24 ? rows : Enumerable.Range(0, 24)
            .Select(i => i < rows.Count ? rows[i] : new VoiceHourlyActivity { Hour = i })
            .ToArray();
        var byUsers = safeRows
            .OrderByDescending(x => x.ActiveUsers)
            .ThenBy(x => x.Hour)
            .First();
        var byTime = safeRows
            .OrderByDescending(x => x.TotalSeconds)
            .ThenBy(x => x.Hour)
            .First();
        var focus = safeRows[Math.Clamp(highlightHourJst, 0, 23)];

        var cardGap = 16f;
        var cardWidth = (area.Width - (cardGap * 4f)) / 3f;
        var cardHeight = area.Height - 12f;
        var titleColor = new Rgba32(56, 61, 70, 255);
        var valueColor = new Rgba32(18, 23, 31, 255);
        var borderColor = new Rgba32(196, 204, 216, 255);

        var cards = new[]
        {
            ("最多人数", $"{byUsers.Hour:00}:00  ({byUsers.ActiveUsers}人)"),
            ("最長VC滞在", $"{byTime.Hour:00}:00  ({FormatDurationJa((int)Math.Min(int.MaxValue, byTime.TotalSeconds))})"),
            ("注目時間", $"{focus.Hour:00}:00  ({focus.ActiveUsers}人 / {FormatDurationJa((int)Math.Min(int.MaxValue, focus.TotalSeconds))})")
        };

        for (var i = 0; i < cards.Length; i++)
        {
            var x = area.X + cardGap + (i * (cardWidth + cardGap));
            var y = area.Y;
            var cardRect = new SixLabors.ImageSharp.RectangleF(x, y, cardWidth, cardHeight);
            ctx.Fill(SixLabors.ImageSharp.Color.White, cardRect);
            ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromPixel(borderColor), 1.2f), cardRect);
            ctx.DrawText(cards[i].Item1, titleFont, titleColor, new SixLabors.ImageSharp.PointF(x + 14f, y + 16f));
            ctx.DrawText(cards[i].Item2, valueFont, valueColor, new SixLabors.ImageSharp.PointF(x + 14f, y + 64f));
        }
    }

    private static void DrawHourlyBarPanel(
        IImageProcessingContext ctx,
        IReadOnlyList<VoiceHourlyActivity> rows,
        SixLabors.ImageSharp.RectangleF area,
        Func<VoiceHourlyActivity, double> valueSelector,
        double maxValue,
        Rgba32 barColor,
        string panelTitle,
        string peakLabelPrefix,
        int highlightHourJst,
        Font axisFont,
        Font captionFont,
        Func<double, string> valueFormatter)
    {
        var gridColor = new Rgba32(223, 228, 236, 255);
        var axisColor = new Rgba32(131, 138, 149, 255);
        var labelColor = new Rgba32(58, 64, 74, 255);
        var titleColor = new Rgba32(27, 31, 36, 255);
        var panelBg = new Rgba32(255, 255, 255, 255);
        var marginLeft = 74f;
        var marginBottom = 54f;
        var marginTop = 56f;
        var marginRight = 16f;
        var plot = new SixLabors.ImageSharp.RectangleF(
            area.X + marginLeft,
            area.Y + marginTop,
            area.Width - marginLeft - marginRight,
            area.Height - marginTop - marginBottom);

        ctx.Fill(SixLabors.ImageSharp.Color.FromPixel(panelBg), area);
        ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromPixel(axisColor), 1.2f), area);
        ctx.DrawText(panelTitle, captionFont, titleColor, new SixLabors.ImageSharp.PointF(area.X + 14f, area.Y + 12f));

        var peak = rows
            .Select((row, hour) => new { Hour = hour, Value = Math.Max(0.0, valueSelector(row)) })
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Hour)
            .First();
        ctx.DrawText(
            $"{peakLabelPrefix}: {peak.Hour:00}:00 ({valueFormatter(peak.Value)})",
            axisFont,
            labelColor,
            new SixLabors.ImageSharp.PointF(area.X + area.Width - 460f, area.Y + 18f));

        var slotWidth = plot.Width / 24f;
        var focusHour = Math.Clamp(highlightHourJst, 0, 23);
        ctx.Fill(SixLabors.ImageSharp.Color.White, plot);
        ctx.Draw(Pens.Solid(axisColor, 1.2f), plot);
        var focusX = plot.Left + (slotWidth * focusHour);
        ctx.Fill(
            SixLabors.ImageSharp.Color.FromRgba(201, 228, 255, 90),
            new SixLabors.ImageSharp.RectangleF(focusX + 1f, plot.Top + 1f, Math.Max(2f, slotWidth - 2f), plot.Height - 2f));

        for (var i = 0; i <= 4; i++)
        {
            var ratio = i / 4f;
            var y = plot.Bottom - (plot.Height * ratio);
            ctx.DrawLine(Pens.Solid(gridColor, 1f), new SixLabors.ImageSharp.PointF(plot.Left, y), new SixLabors.ImageSharp.PointF(plot.Right, y));
            var valueLabel = valueFormatter(maxValue * ratio);
            ctx.DrawText(valueLabel, axisFont, labelColor, new SixLabors.ImageSharp.PointF(area.X + 10f, y - 10f));
        }

        for (var hour = 0; hour < 24; hour += 2)
        {
            var x = plot.Left + (hour * slotWidth);
            ctx.DrawLine(Pens.Solid(gridColor, 0.8f), new SixLabors.ImageSharp.PointF(x, plot.Top), new SixLabors.ImageSharp.PointF(x, plot.Bottom));
        }

        var barWidth = Math.Max(8f, slotWidth * 0.62f);
        var safeMax = Math.Max(1e-9, maxValue);
        for (var hour = 0; hour < 24; hour++)
        {
            var row = rows[hour];
            var value = Math.Max(0.0, valueSelector(row));
            var ratio = Math.Clamp(value / safeMax, 0.0, 1.0);
            var barHeight = (float)(plot.Height * ratio);
            var barX = plot.Left + (hour * slotWidth) + ((slotWidth - barWidth) / 2f);
            var barY = plot.Bottom - barHeight;
            if (barHeight > 0.5f)
            {
                var drawColor = hour == focusHour
                    ? new Rgba32(33, 97, 178, 255)
                    : barColor;
                ctx.Fill(SixLabors.ImageSharp.Color.FromPixel(drawColor), new SixLabors.ImageSharp.RectangleF(barX, barY, barWidth, barHeight));
            }

            if (hour % 3 == 0)
            {
                ctx.DrawText($"{hour:00}", axisFont, labelColor, new SixLabors.ImageSharp.PointF(barX - 4f, plot.Bottom + 10f));
            }
        }
    }

    private static void DrawWeekdayHeatmapPanel(
        IImageProcessingContext ctx,
        double[,] weekdayHourSeconds,
        SixLabors.ImageSharp.RectangleF area,
        Font axisFont,
        Font captionFont)
    {
        var dayLabels = new[] { "月", "火", "水", "木", "金", "土", "日" };
        var titleColor = new Rgba32(27, 31, 36, 255);
        var labelColor = new Rgba32(58, 64, 74, 255);
        var borderColor = new Rgba32(131, 138, 149, 255);
        var marginLeft = 90f;
        var marginTop = 58f;
        var marginBottom = 74f;
        var marginRight = 28f;
        var plot = new SixLabors.ImageSharp.RectangleF(
            area.X + marginLeft,
            area.Y + marginTop,
            area.Width - marginLeft - marginRight,
            area.Height - marginTop - marginBottom);

        ctx.Fill(SixLabors.ImageSharp.Color.White, area);
        ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromPixel(borderColor), 1.2f), area);
        ctx.DrawText("曜日 x 時間帯 ヒートマップ (VC滞在)", captionFont, titleColor, new SixLabors.ImageSharp.PointF(area.X + 14f, area.Y + 12f));
        ctx.DrawText("濃い色ほどVC滞在が長い", axisFont, labelColor, new SixLabors.ImageSharp.PointF(area.X + area.Width - 320f, area.Y + 18f));

        ctx.Fill(SixLabors.ImageSharp.Color.White, plot);
        ctx.Draw(Pens.Solid(borderColor, 1f), plot);

        const int dayCount = 7;
        const int hourCount = 24;
        var maxValue = 0.0;
        for (var day = 0; day < dayCount; day++)
        {
            for (var hour = 0; hour < hourCount; hour++)
            {
                if (weekdayHourSeconds[day, hour] > maxValue)
                {
                    maxValue = weekdayHourSeconds[day, hour];
                }
            }
        }

        var cellWidth = plot.Width / hourCount;
        var cellHeight = plot.Height / dayCount;
        for (var day = 0; day < dayCount; day++)
        {
            var y = plot.Y + (day * cellHeight);
            ctx.DrawText(dayLabels[day], axisFont, labelColor, new SixLabors.ImageSharp.PointF(area.X + 16f, y + (cellHeight * 0.24f)));

            for (var hour = 0; hour < hourCount; hour++)
            {
                var x = plot.X + (hour * cellWidth);
                var value = weekdayHourSeconds[day, hour];
                var ratio = maxValue <= 0 ? 0.0 : Math.Clamp(value / maxValue, 0.0, 1.0);
                var color = ToHeatColor(ratio);
                var cell = new SixLabors.ImageSharp.RectangleF(x + 1f, y + 1f, Math.Max(1f, cellWidth - 2f), Math.Max(1f, cellHeight - 2f));
                ctx.Fill(SixLabors.ImageSharp.Color.FromPixel(color), cell);
            }
        }

        for (var hour = 0; hour < hourCount; hour += 3)
        {
            var x = plot.X + (hour * cellWidth);
            ctx.DrawText($"{hour:00}", axisFont, labelColor, new SixLabors.ImageSharp.PointF(x + 1f, plot.Bottom + 10f));
        }

        DrawHeatLegend(
            ctx,
            new SixLabors.ImageSharp.RectangleF(plot.Right - 250f, plot.Bottom + 36f, 220f, 14f),
            axisFont);
    }

    private static void DrawHourRegularsPanel(
        IImageProcessingContext ctx,
        IReadOnlyList<VoiceRegularChartItem> regularItems,
        int hourJst,
        SixLabors.ImageSharp.RectangleF area,
        Font axisFont,
        Font captionFont)
    {
        var titleColor = new Rgba32(27, 31, 36, 255);
        var labelColor = new Rgba32(58, 64, 74, 255);
        var borderColor = new Rgba32(131, 138, 149, 255);
        var cardBorderColor = new Rgba32(202, 209, 220, 255);
        var marginLeft = 20f;
        var marginTop = 58f;
        var marginBottom = 24f;
        var marginRight = 20f;
        var plot = new SixLabors.ImageSharp.RectangleF(
            area.X + marginLeft,
            area.Y + marginTop,
            area.Width - marginLeft - marginRight,
            area.Height - marginTop - marginBottom);

        ctx.Fill(SixLabors.ImageSharp.Color.White, area);
        ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromPixel(borderColor), 1.2f), area);
        ctx.DrawText($"この時間帯の常連 ({FormatHourRangeLabel(hourJst)} JST)", captionFont, titleColor, new SixLabors.ImageSharp.PointF(area.X + 14f, area.Y + 12f));
        ctx.DrawText("注目時間内のVC滞在時間 上位ユーザー", axisFont, labelColor, new SixLabors.ImageSharp.PointF(area.X + 16f, area.Y + 38f));
        ctx.Fill(SixLabors.ImageSharp.Color.White, plot);
        ctx.Draw(Pens.Solid(borderColor, 1f), plot);

        if (regularItems.Count == 0)
        {
            ctx.DrawText("この時間帯のデータはありません。", axisFont, labelColor, new SixLabors.ImageSharp.PointF(plot.X + 18f, plot.Y + 22f));
            return;
        }

        var gap = 16f;
        var cols = 2;
        var rows = 2;
        var cardWidth = (plot.Width - (gap * (cols + 1))) / cols;
        var cardHeight = (plot.Height - (gap * (rows + 1))) / rows;
        var avatarSize = 78f;
        var shownItems = regularItems.Take(4).ToArray();

        for (var i = 0; i < shownItems.Length; i++)
        {
            var item = shownItems[i];
            var col = i % cols;
            var row = i / cols;
            var cardX = plot.X + gap + (col * (cardWidth + gap));
            var cardY = plot.Y + gap + (row * (cardHeight + gap));
            var cardRect = new SixLabors.ImageSharp.RectangleF(cardX, cardY, cardWidth, cardHeight);
            ctx.Fill(SixLabors.ImageSharp.Color.FromRgb(251, 252, 254), cardRect);
            ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromPixel(cardBorderColor), 1f), cardRect);

            var rankLabel = $"#{i + 1}";
            ctx.DrawText(rankLabel, axisFont, titleColor, new SixLabors.ImageSharp.PointF(cardX + 10f, cardY + 8f));

            var avatarX = cardX + 12f;
            var avatarY = cardY + 34f;
            var avatarRect = new SixLabors.ImageSharp.RectangleF(avatarX, avatarY, avatarSize, avatarSize);
            var avatarDrawn = false;

            if (item.AvatarBytes is { Length: > 0 })
            {
                try
                {
                    using var avatar = SixLabors.ImageSharp.Image.Load<Rgba32>(item.AvatarBytes);
                    avatar.Mutate(a => a.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size((int)Math.Round(avatarSize), (int)Math.Round(avatarSize)),
                        Mode = ResizeMode.Crop
                    }));
                    ctx.DrawImage(avatar, new SixLabors.ImageSharp.Point((int)Math.Round(avatarRect.X), (int)Math.Round(avatarRect.Y)), 1f);
                    avatarDrawn = true;
                }
                catch
                {
                    avatarDrawn = false;
                }
            }

            if (!avatarDrawn)
            {
                ctx.Fill(SixLabors.ImageSharp.Color.FromRgb(228, 233, 242), avatarRect);
                ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromRgb(174, 183, 198), 1f), avatarRect);
                ctx.DrawText("なし", axisFont, labelColor, new SixLabors.ImageSharp.PointF(avatarRect.X + 20f, avatarRect.Y + (avatarSize / 2f) - 10f));
            }

            var textX = avatarRect.Right + 14f;
            var nameText = TruncateSingleLine(item.DisplayName, 24);
            var durationText = FormatDurationJa((int)Math.Min(int.MaxValue, item.DurationSeconds));
            ctx.DrawText(nameText, axisFont, titleColor, new SixLabors.ImageSharp.PointF(textX, avatarRect.Y + 10f));
            ctx.DrawText(durationText, axisFont, labelColor, new SixLabors.ImageSharp.PointF(textX, avatarRect.Y + 42f));
            ctx.DrawText("この時間の滞在時間", axisFont, labelColor, new SixLabors.ImageSharp.PointF(textX, avatarRect.Y + 72f));
        }
    }

    private static void DrawHeatLegend(
        IImageProcessingContext ctx,
        SixLabors.ImageSharp.RectangleF area,
        Font axisFont)
    {
        for (var i = 0; i < (int)Math.Max(10f, area.Width); i++)
        {
            var ratio = i / Math.Max(1f, area.Width - 1f);
            var color = ToHeatColor(ratio);
            var x = area.X + i;
            ctx.Fill(SixLabors.ImageSharp.Color.FromPixel(color), new SixLabors.ImageSharp.RectangleF(x, area.Y, 1f, area.Height));
        }

        ctx.Draw(Pens.Solid(SixLabors.ImageSharp.Color.FromRgb(131, 138, 149), 1f), area);
        ctx.DrawText("低", axisFont, SixLabors.ImageSharp.Color.FromRgb(58, 64, 74), new SixLabors.ImageSharp.PointF(area.X, area.Y - 24f));
        ctx.DrawText("高", axisFont, SixLabors.ImageSharp.Color.FromRgb(58, 64, 74), new SixLabors.ImageSharp.PointF(area.Right - 24f, area.Y - 24f));
    }

    private static async Task<byte[]?> TryDownloadImageBytesAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        try
        {
            using var response = await VoiceChartImageHttpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > (2 * 1024 * 1024))
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    private static Rgba32 ToHeatColor(double ratio)
    {
        var r = Math.Clamp(ratio, 0.0, 1.0);
        var low = new Rgba32(236, 244, 255, 255);
        var mid = new Rgba32(126, 182, 255, 255);
        var high = new Rgba32(16, 92, 192, 255);
        return r < 0.5
            ? Lerp(low, mid, r * 2.0)
            : Lerp(mid, high, (r - 0.5) * 2.0);
    }

    private static Rgba32 Lerp(Rgba32 from, Rgba32 to, double ratio)
    {
        var t = Math.Clamp(ratio, 0.0, 1.0);
        byte Blend(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t));
        return new Rgba32(
            Blend(from.R, to.R),
            Blend(from.G, to.G),
            Blend(from.B, to.B),
            Blend(from.A, to.A));
    }

    private static Font ResolveChartFont(float size, FontStyle style)
    {
        var preferredExactJapanese = new[]
        {
            "Yu Gothic UI",
            "Meiryo",
            "BIZ UDPGothic",
            "Noto Sans CJK JP",
            "Noto Sans JP",
            "Source Han Sans JP",
            "IPAexGothic",
            "IPAGothic",
            "TakaoPGothic",
            "Hiragino Sans",
            "MS Gothic"
        };

        // CentOS での family 名差分（weight/suffix 付き）にも対応するため、
        // まず厳密一致、次に部分一致で日本語向けフォントを優先的に探す。
        var preferredContains = new[]
        {
            "Noto Sans CJK JP",
            "Noto Sans JP",
            "Noto Sans CJ JP",
            "Noto Sans CJ",
            "Noto Serif CJK JP",
            "Noto Serif JP",
            "Source Han Sans",
            "Source Han Serif",
            "IPAexGothic",
            "IPAGothic",
            "Takao",
            "VL Gothic",
            "WenQuanYi",
            "Droid Sans Fallback",
            "Yu Gothic",
            "Meiryo",
            "Hiragino",
            "MS Gothic"
        };

        static bool TryCreate(FontFamily family, float targetSize, FontStyle targetStyle, out Font font)
        {
            try
            {
                font = family.CreateFont(targetSize, targetStyle);
                if (CanRenderChartText(font))
                {
                    return true;
                }
            }
            catch
            {
                // Try regular style below.
            }

            try
            {
                font = family.CreateFont(targetSize, FontStyle.Regular);
                if (CanRenderChartText(font))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore and fallback to next family.
            }

            font = default!;
            return false;
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var familyName in preferredExactJapanese)
        {
            if (!SystemFonts.TryGet(familyName, out var family))
            {
                continue;
            }

            if (!tried.Add(family.Name))
            {
                continue;
            }

            if (TryCreate(family, size, style, out var preferredFont))
            {
                return preferredFont;
            }
        }

        foreach (var pattern in preferredContains)
        {
            var family = SystemFonts.Collection.Families.FirstOrDefault(candidate =>
                candidate.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(family.Name) || !tried.Add(family.Name))
            {
                continue;
            }

            if (TryCreate(family, size, style, out var containsFont))
            {
                return containsFont;
            }
        }

        foreach (var family in VoiceChartFileFamilies)
        {
            if (!tried.Add(family.Name))
            {
                continue;
            }

            if (TryCreate(family, size, style, out var fileFont))
            {
                return fileFont;
            }
        }

        var preferredGeneric = new[]
        {
            "Segoe UI",
            "Arial Unicode MS",
            "Arial",
            "Liberation Sans",
            "DejaVu Sans"
        };
        foreach (var familyName in preferredGeneric)
        {
            if (!SystemFonts.TryGet(familyName, out var family) || !tried.Add(family.Name))
            {
                continue;
            }

            if (TryCreate(family, size, style, out var genericFont))
            {
                return genericFont;
            }
        }

        foreach (var family in SystemFonts.Collection.Families)
        {
            if (!tried.Add(family.Name))
            {
                continue;
            }

            if (TryCreate(family, size, style, out var fallbackFont))
            {
                return fallbackFont;
            }
        }

        throw new InvalidOperationException("No usable system fonts available.");
    }

    private static IReadOnlyList<FontFamily> LoadVoiceChartFileFamilies()
    {
        var loaded = new List<FontFamily>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = new[]
        {
            "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc",
            "/usr/share/fonts/opentype/ipaexfont-gothic/ipaexg.ttf",
            "/usr/share/fonts/ipa-gothic/ipag.ttf",
            "/usr/share/fonts/vlgothic/VL-Gothic-Regular.ttf"
        };

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var family = VoiceChartFontFileCollection.Add(path);
                if (seen.Add(family.Name))
                {
                    loaded.Add(family);
                }
            }
            catch
            {
                // Skip broken/unreadable font files.
            }
        }

        return loaded;
    }

    private static bool CanRenderChartText(Font font)
    {
        try
        {
            _ = TextMeasurer.MeasureBounds("VC時間帯統計 123", new TextOptions(font));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateVoiceChartImageHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    private static string FormatHourRangeLabel(int hour)
        => $"{Math.Clamp(hour, 0, 23):00}:00-{Math.Clamp(hour, 0, 23):00}:59";

    private static string FormatWeekdayHourLabel(int weekday, int hour)
    {
        var days = new[] { "月", "火", "水", "木", "金", "土", "日" };
        var normalizedWeekday = Math.Clamp(weekday, 0, 6);
        var normalizedHour = Math.Clamp(hour, 0, 23);
        return $"{days[normalizedWeekday]} {normalizedHour:00}:00";
    }

    private static string BuildHourRegularsLine(IReadOnlyList<VoiceUserHourSummary> rows, int hourJst)
    {
        if (rows.Count == 0)
        {
            return $"🧑 この時間帯の常連({FormatHourRangeLabel(hourJst)}): まだデータなし";
        }

        var parts = rows
            .Take(4)
            .Select((row, index) =>
            {
                var userId = ToUlong(row.UserId);
                var badge = RankBadge(index + 1);
                return $"{badge} <@{userId}> {FormatDuration((int)Math.Min(int.MaxValue, Math.Max(0, row.DurationSeconds)))}";
            })
            .ToArray();

        return $"🧑 この時間帯の常連({FormatHourRangeLabel(hourJst)}): {string.Join(" / ", parts)}";
    }

    private static List<string> BuildCohortRetentionLines(
        IReadOnlyList<OpsMemberJoin> members,
        IReadOnlyDictionary<ulong, UserActivityTimeline> timelineByUser,
        DateTimeOffset nowUtc,
        int maxRows)
    {
        if (members.Count == 0)
        {
            return new List<string> { "直近12週の新規参加がありません。" };
        }

        var maxCohorts = Math.Clamp(maxRows, 1, 12);
        var cohorts = members
            .GroupBy(member => GetCohortWeekStartJst(member.JoinedAt))
            .OrderByDescending(group => group.Key)
            .Take(maxCohorts)
            .ToArray();

        var lines = new List<string>(cohorts.Length + 1)
        {
            "D7/D30=加入後7日/30日までに活動が確認できた割合"
        };

        foreach (var cohort in cohorts)
        {
            var membersInWeek = cohort.ToArray();
            var d7Eligible = membersInWeek
                .Where(member => member.JoinedAt <= nowUtc.AddDays(-7))
                .ToArray();
            var d30Eligible = membersInWeek
                .Where(member => member.JoinedAt <= nowUtc.AddDays(-30))
                .ToArray();

            var d7Retained = d7Eligible.Count(member =>
                timelineByUser.TryGetValue(member.UserId, out var row) &&
                row.LastActivityAt.HasValue &&
                row.LastActivityAt.Value >= member.JoinedAt.AddDays(7));
            var d30Retained = d30Eligible.Count(member =>
                timelineByUser.TryGetValue(member.UserId, out var row) &&
                row.LastActivityAt.HasValue &&
                row.LastActivityAt.Value >= member.JoinedAt.AddDays(30));

            lines.Add(
                $"{FormatCohortWeekLabel(cohort.Key)}: 参加{membersInWeek.Length:N0}人 / D7 {FormatCohortRateCell(d7Retained, d7Eligible.Length)} / D30 {FormatCohortRateCell(d30Retained, d30Eligible.Length)}");
        }

        return lines;
    }

    private static List<string> BuildWeekdayHourActivityLines(
        IReadOnlyList<MessageWeekdayHourlyActivity> messageRows,
        IReadOnlyList<VoiceWeekdayHourlyActivity> voiceRows)
    {
        if (messageRows.Count == 0 && voiceRows.Count == 0)
        {
            return new List<string> { "対象期間にメッセージ/VCの時間帯データがありません。" };
        }

        var lines = new List<string>(capacity: 4);
        if (messageRows.Count > 0)
        {
            var messagePeak = messageRows
                .OrderByDescending(row => row.MessageCount)
                .ThenByDescending(row => row.ActiveUsers)
                .ThenBy(row => row.Weekday)
                .ThenBy(row => row.Hour)
                .First();
            lines.Add($"発言ピーク: {FormatWeekdayHourLabel(messagePeak.Weekday, messagePeak.Hour)} ({messagePeak.MessageCount:N0}件 / {messagePeak.ActiveUsers:N0}人)");
        }
        else
        {
            lines.Add("発言ピーク: データなし");
        }

        if (voiceRows.Count > 0)
        {
            var voicePeak = voiceRows
                .OrderByDescending(row => row.TotalSeconds)
                .ThenByDescending(row => row.ActiveUsers)
                .ThenBy(row => row.Weekday)
                .ThenBy(row => row.Hour)
                .First();
            lines.Add($"VCピーク: {FormatWeekdayHourLabel(voicePeak.Weekday, voicePeak.Hour)} ({FormatDuration((int)Math.Min(int.MaxValue, Math.Max(0L, voicePeak.TotalSeconds)))} / {voicePeak.ActiveUsers:N0}人)");
        }
        else
        {
            lines.Add("VCピーク: データなし");
        }

        var slotMap = new Dictionary<(int Weekday, int Hour), (long MessageCount, long VoiceSeconds)>();
        foreach (var row in messageRows)
        {
            if (row.Weekday is < 0 or > 6 || row.Hour is < 0 or > 23)
            {
                continue;
            }

            var key = (row.Weekday, row.Hour);
            slotMap.TryGetValue(key, out var current);
            slotMap[key] = (current.MessageCount + Math.Max(0L, row.MessageCount), current.VoiceSeconds);
        }

        foreach (var row in voiceRows)
        {
            if (row.Weekday is < 0 or > 6 || row.Hour is < 0 or > 23)
            {
                continue;
            }

            var key = (row.Weekday, row.Hour);
            slotMap.TryGetValue(key, out var current);
            slotMap[key] = (current.MessageCount, current.VoiceSeconds + Math.Max(0L, row.TotalSeconds));
        }

        var hotSlots = slotMap
            .Where(kv => kv.Value.MessageCount > 0 || kv.Value.VoiceSeconds > 0)
            .OrderByDescending(kv => kv.Value.VoiceSeconds + (kv.Value.MessageCount * 60L))
            .ThenByDescending(kv => kv.Value.VoiceSeconds)
            .ThenByDescending(kv => kv.Value.MessageCount)
            .ThenBy(kv => kv.Key.Weekday)
            .ThenBy(kv => kv.Key.Hour)
            .Take(3)
            .Select(kv =>
            {
                var voiceText = FormatDuration((int)Math.Min(int.MaxValue, Math.Max(0L, kv.Value.VoiceSeconds)));
                return $"{FormatWeekdayHourLabel(kv.Key.Weekday, kv.Key.Hour)} (発言{kv.Value.MessageCount:N0}/VC{voiceText})";
            })
            .ToArray();

        lines.Add($"複合ホット3: {(hotSlots.Length == 0 ? "データなし" : string.Join(" / ", hotSlots))}");
        lines.Add("複合指数: VC秒 + 発言件数x60秒");
        return lines;
    }

    private static DateTime GetCohortWeekStartJst(DateTimeOffset joinedAt)
    {
        var jstDate = joinedAt.ToOffset(JapanTimeOffset).Date;
        var dayOffset = ((int)jstDate.DayOfWeek + 6) % 7;
        return jstDate.AddDays(-dayOffset);
    }

    private static string FormatCohortWeekLabel(DateTime weekStartJst)
        => $"{weekStartJst:MM/dd}週";

    private static string FormatCohortRateCell(int retained, int eligible)
    {
        if (eligible <= 0)
        {
            return "-";
        }

        return $"{retained:N0}/{eligible:N0} ({FormatRatePercent(retained, eligible)})";
    }

    private static string RankBadge(int rank)
        => rank switch
        {
            1 => "🥇",
            2 => "🥈",
            3 => "🥉",
            _ => "⭐"
        };

    private static bool IsFirstActivityWithin(DateTimeOffset? firstActivityAt, DateTimeOffset joinedAt, TimeSpan window)
    {
        if (!firstActivityAt.HasValue)
        {
            return false;
        }

        var value = firstActivityAt.Value;
        return value >= joinedAt && value <= joinedAt + window;
    }

    private static string FormatRatePercent(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return "-";
        }

        return FormatPercent(numerator / (double)denominator);
    }

    private static string FormatPercent(double ratio)
    {
        var safe = double.IsFinite(ratio) ? ratio : 0d;
        return $"{Math.Max(0d, safe) * 100d:0.#}%";
    }

    private static (string Line, bool IsAlert) BuildDropTrendLine(string label, long recentValue, double baselineAverage, double alertThresholdRatio = 0.70)
    {
        if (baselineAverage <= 0)
        {
            return ($"ℹ️ {label}: {recentValue:N0} (比較データ不足)", false);
        }

        var ratio = recentValue / baselineAverage;
        var isAlert = ratio < alertThresholdRatio;
        var icon = isAlert ? "⚠️" : "✅";
        return ($"{icon} {label}: {recentValue:N0} / 基準平均{baselineAverage:0.#} ({FormatPercent(ratio)})", isAlert);
    }

    private static string FormatChannelMention(SocketGuild guild, long channelId)
    {
        var normalizedChannelId = ToUlong(channelId);
        var channel = guild.GetChannel(normalizedChannelId);
        return channel is null ? $"VC:{normalizedChannelId}" : $"<#{normalizedChannelId}>";
    }

    private static string JoinWithinLimit(IEnumerable<string> lines, int maxLength)
    {
        var sb = new StringBuilder(capacity: Math.Max(128, maxLength));
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (sb.Length > 0 && sb.Length + 1 + line.Length > maxLength)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static ITextChannel? FindTextChannelByName(SocketGuild guild, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDuration(int durationSeconds)
    {
        var seconds = Math.Max(0, durationSeconds);
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;
        if (hours > 0)
        {
            return $"{hours}h{minutes:00}m";
        }

        if (minutes > 0)
        {
            return $"{minutes}m{secs:00}s";
        }

        return $"{secs}s";
    }

    private static string FormatDurationJa(int durationSeconds)
    {
        var seconds = Math.Max(0, durationSeconds);
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;
        if (hours > 0)
        {
            return $"{hours}時間{minutes:00}分";
        }

        if (minutes > 0)
        {
            return $"{minutes}分{secs:00}秒";
        }

        return $"{secs}秒";
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, Math.Max(1, maxLength - 3)) + "...";
    }

    private static string BuildMemberManageFrequentHoursSummary(
        IReadOnlyList<MessageHourlyActivity> messageRows,
        IReadOnlyList<VoiceHourlyActivity> voiceRows)
    {
        var topMessageHours = messageRows
            .Where(row => row.MessageCount > 0)
            .OrderByDescending(row => row.MessageCount)
            .ThenBy(row => row.Hour)
            .Take(3)
            .Select(row => $"{FormatHourRangeLabel(row.Hour)} ({row.MessageCount}件)")
            .ToArray();

        var topVoiceHours = voiceRows
            .Where(row => row.TotalSeconds > 0)
            .OrderByDescending(row => row.TotalSeconds)
            .ThenBy(row => row.Hour)
            .Take(3)
            .Select(row => $"{FormatHourRangeLabel(row.Hour)} ({FormatDuration((int)Math.Min(int.MaxValue, row.TotalSeconds))})")
            .ToArray();

        var voiceLine = topVoiceHours.Length == 0
            ? "🎙️ VC滞在: データなし"
            : $"🎙️ VC滞在: {string.Join(", ", topVoiceHours)}";
        var messageLine = topMessageHours.Length == 0
            ? "💬 発言: データなし"
            : $"💬 発言: {string.Join(", ", topMessageHours)}";
        return $"{voiceLine}\n{messageLine}";
    }

    private static string BuildMemberManageLastActivitySummary(UserActivityTimeline? timeline)
    {
        var lastMessage = FormatOptionalActivity(timeline?.LastMessageAt);
        var lastReaction = FormatOptionalActivity(timeline?.LastReactionAt);
        var lastVoiceJoin = FormatOptionalActivity(timeline?.LastVoiceJoinedAt);
        var lastOverall = FormatOptionalActivity(timeline?.LastActivityAt);
        return string.Join('\n', new[]
        {
            $"発言: {lastMessage}",
            $"リアクション: {lastReaction}",
            $"VC入室: {lastVoiceJoin}",
            $"総合: {lastOverall}"
        });
    }

    private static string BuildMemberManageInactiveListSection(
        IReadOnlyList<MemberManageInactiveRow> rows,
        int maxRows)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var limit = Math.Clamp(maxRows, 1, 50);
        var sb = new StringBuilder(capacity: 900);
        var shown = 0;
        foreach (var row in rows)
        {
            if (shown >= limit)
            {
                break;
            }

            var line = $"{shown + 1}. <@{row.User.Id}> / 総合:{FormatOptionalActivity(row.Timeline?.LastActivityAt)} / 発:{FormatOptionalActivity(row.Timeline?.LastMessageAt)} / リ:{FormatOptionalActivity(row.Timeline?.LastReactionAt)} / VC入:{FormatOptionalActivity(row.Timeline?.LastVoiceJoinedAt)}";
            if (sb.Length + line.Length + 1 > 980)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            shown++;
        }

        if (rows.Count > shown)
        {
            var suffix = $"...他 {rows.Count - shown} 件";
            if (sb.Length + suffix.Length + 1 <= 1000)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(suffix);
            }
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static string FormatOptionalActivity(DateTimeOffset? activityAt)
        => activityAt.HasValue ? $"<t:{activityAt.Value.ToUnixTimeSeconds()}:R>" : "なし";

    private static string BuildInactiveKickPreviewSection(IReadOnlyList<SocketGuildUser> users, int maxRows)
    {
        if (users.Count == 0)
        {
            return "なし";
        }

        var rows = Math.Clamp(maxRows, 1, 50);
        var sb = new StringBuilder(capacity: 512);
        var shown = 0;
        foreach (var user in users
                     .OrderBy(x => x.JoinedAt ?? x.CreatedAt)
                     .ThenBy(x => x.Id))
        {
            if (shown >= rows)
            {
                break;
            }

            var joinedJst = (user.JoinedAt ?? user.CreatedAt).ToOffset(JapanTimeOffset);
            var line = $"<@{user.Id}> / 参加:{joinedJst:yyyy/MM/dd}";
            if (sb.Length + line.Length + 1 > 980)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            shown++;
        }

        if (users.Count > shown)
        {
            var suffix = $"...他 {users.Count - shown} 件";
            if (sb.Length + suffix.Length + 1 <= 1000)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(suffix);
            }
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static string BuildRegistrationPendingListSection(IReadOnlyList<SocketGuildUser> users, int maxRows)
    {
        if (users.Count == 0)
        {
            return "なし";
        }

        var rows = Math.Clamp(maxRows, 1, 50);
        var sb = new StringBuilder(capacity: 512);
        var shown = 0;
        foreach (var user in users)
        {
            if (shown >= rows)
            {
                break;
            }

            var joinedJst = (user.JoinedAt ?? user.CreatedAt).ToOffset(JapanTimeOffset);
            var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
            var line = $"{shown + 1}. <@{user.Id}> / 参加:{joinedJst:yyyy/MM/dd} / {TruncateSingleLine(displayName, 28)}";
            if (sb.Length + line.Length + 1 > 980)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            shown++;
        }

        if (users.Count > shown)
        {
            var suffix = $"...他 {users.Count - shown} 件";
            if (sb.Length + suffix.Length + 1 <= 1000)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(suffix);
            }
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static HashSet<string> BuildInactiveKickExemptRoleNameSet(DiscordOptions options)
    {
        return options.BuildManagementRoleNameSet();
    }

    private static bool IsInactiveKickExemptUser(SocketGuildUser user, IReadOnlySet<string> exemptRoleNames)
    {
        if (user.GuildPermissions.Administrator)
        {
            return true;
        }

        if (exemptRoleNames.Count == 0)
        {
            return false;
        }

        foreach (var role in user.Roles)
        {
            if (exemptRoleNames.Contains(role.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanKickMember(SocketGuildUser? botUser, SocketGuildUser targetUser, ulong guildOwnerId)
    {
        if (botUser is null)
        {
            return false;
        }

        if (targetUser.Id == guildOwnerId || targetUser.Id == botUser.Id)
        {
            return false;
        }

        if (!botUser.GuildPermissions.KickMembers)
        {
            return false;
        }

        return botUser.Hierarchy > targetUser.Hierarchy;
    }

    private IRole? ResolveMemberRole(SocketGuild guild, GuildSettings? settings)
    {
        if (settings is not null && settings.MemberRoleId != 0)
        {
            var byId = guild.GetRole(ToUlong(settings.MemberRoleId));
            if (byId is not null)
            {
                return byId;
            }
        }

        return guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));
    }

    private IRole? ResolveProvisionalRole(SocketGuild guild, GuildSettings? settings)
    {
        if (settings is not null && settings.ProvisionalRoleId != 0)
        {
            var byId = guild.GetRole(ToUlong(settings.ProvisionalRoleId));
            if (byId is not null)
            {
                return byId;
            }
        }

        return guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.ProvisionalRoleName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record OpsMemberJoin(ulong UserId, DateTimeOffset JoinedAt);
    private sealed record VoiceRegularChartItem(string DisplayName, long DurationSeconds, byte[]? AvatarBytes);
    private sealed record MemberManageDangerActionContext(
        ulong GuildId,
        ulong ActorUserId,
        ulong TargetUserId,
        string Action,
        string Reason,
        DateTimeOffset CreatedAt);
    private sealed record MemberManageLookbackPreference(
        ulong GuildId,
        int Days,
        DateTimeOffset UpdatedAt);
    private sealed record MemberManageInactiveRow(
        SocketGuildUser User,
        UserActivityTimeline? Timeline,
        DateTimeOffset JoinedAt,
        bool IsInactive);
    private sealed record MemberManageBulkSelectionContext(
        ulong GuildId,
        IReadOnlyList<ulong> TargetUserIds,
        DateTimeOffset CreatedAt);

    private static string? NormalizeLotteryPrizeList(string? raw, int winnerCount, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "景品を入力してください。";
            return null;
        }

        var normalizedRaw = raw
            .Replace('、', ',')
            .Replace('，', ',');
        var items = normalizedRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (items.Count == 0)
        {
            error = "景品を入力してください。";
            return null;
        }

        if (items.Count != winnerCount)
        {
            error = $"景品数は当選人数（{winnerCount}）と同じ件数にしてください。";
            return null;
        }

        return string.Join(", ", items);
    }

    private sealed record HealthCheckItem(string Level, string Name, string Message)
    {
        public static HealthCheckItem Ok(string name, string message) => new("OK", name, message);
        public static HealthCheckItem Warn(string name, string message) => new("WARN", name, message);
        public static HealthCheckItem Error(string name, string message) => new("ERROR", name, message);
    }

private static ulong ToUlong(long value) => unchecked((ulong)value);
private static long ToDbId(ulong value) => unchecked((long)value);
}

public sealed class LotteryCreateModal : IModal
{
    public string Title => "抽選を作成";

    [InputLabel("タイトル")]
    [ModalTextInput("lottery_create_title", maxLength: 80)]
    public string LotteryTitle { get; set; } = string.Empty;

    [InputLabel("当選人数（1-99）")]
    [ModalTextInput("lottery_create_winner_count", maxLength: 2)]
    public string WinnerCount { get; set; } = string.Empty;

    [InputLabel("景品（,区切り・当選人数分）")]
    [ModalTextInput("lottery_create_prize_list", maxLength: 300)]
    public string PrizeList { get; set; } = string.Empty;

    [InputLabel("説明（任意）")]
    [ModalTextInput("lottery_create_description", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Description { get; set; }
}

public sealed class LotteryCreateTestModal : IModal
{
    public string Title => "テスト抽選を作成";

    [InputLabel("タイトル")]
    [ModalTextInput("lottery_create_test_title", maxLength: 80)]
    public string LotteryTitle { get; set; } = string.Empty;

    [InputLabel("当選人数（1-99）")]
    [ModalTextInput("lottery_create_test_winner_count", maxLength: 2)]
    public string WinnerCount { get; set; } = string.Empty;

    [InputLabel("景品（,区切り・当選人数分）")]
    [ModalTextInput("lottery_create_test_prize_list", maxLength: 300)]
    public string PrizeList { get; set; } = string.Empty;

    [InputLabel("締切まで秒（1-86400）")]
    [ModalTextInput("lottery_create_test_close_seconds", maxLength: 5)]
    public string CloseSeconds { get; set; } = string.Empty;

    [InputLabel("説明（任意）")]
    [ModalTextInput("lottery_create_test_description", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Description { get; set; }
}

public sealed class AdminRuntimeTuningModal : IModal
{
    public string Title => "運用設定";

    [InputLabel("Cleanup間隔（分 / 1-720）")]
    [ModalTextInput("admin_runtime_cleanup_interval_minutes", maxLength: 3)]
    public string CleanupIntervalMinutes { get; set; } = "10";

    [InputLabel("募集保持: 終了後（時間 / 1-720）")]
    [ModalTextInput("admin_runtime_recruit_closed_retention_hours", maxLength: 3)]
    public string RecruitClosedRetentionHours { get; set; } = "24";

    [InputLabel("募集保持: 中断後（時間 / 1-720）")]
    [ModalTextInput("admin_runtime_recruit_canceled_retention_hours", maxLength: 3)]
    public string RecruitCanceledRetentionHours { get; set; } = "24";

    [InputLabel("メッセージ保持（日 / 0-3650, 0=無効）")]
    [ModalTextInput("admin_runtime_message_log_retention_days", maxLength: 4)]
    public string MessageLogRetentionDays { get; set; } = "90";

    [InputLabel("VC保持（日 / 0-3650, 0=無効）")]
    [ModalTextInput("admin_runtime_voice_session_retention_days", maxLength: 4)]
    public string VoiceSessionRetentionDays { get; set; } = "90";
}

public sealed class AdminInactiveKickRetentionModal : IModal
{
    public string Title => "自動キック後データ保持";

    [InputLabel("保持日数（日 / 1-30）")]
    [ModalTextInput("admin_runtime_inactive_kick_user_data_retention_days", maxLength: 2)]
    public string RetentionDays { get; set; } = "7";
}

public sealed class MemberManageTimeoutModal : IModal
{
    public string Title => "タイムアウト設定";

    [InputLabel("タイムアウト分（1-40320）")]
    [ModalTextInput("admin_member_manage_timeout_minutes", maxLength: 5)]
    public string TimeoutMinutes { get; set; } = "60";

    [InputLabel("理由（任意）")]
    [ModalTextInput("admin_member_manage_timeout_reason", TextInputStyle.Paragraph, maxLength: 120)]
    [RequiredInput(false)]
    public string? Reason { get; set; }
}

public sealed class MemberManageDangerReasonModal : IModal
{
    public string Title => "理由入力（任意）";

    [InputLabel("理由（任意）")]
    [ModalTextInput("admin_member_manage_danger_reason", TextInputStyle.Paragraph, maxLength: 120)]
    [RequiredInput(false)]
    public string? Reason { get; set; }
}

public sealed class MemberManageNoteModal : IModal
{
    public string Title => "管理者メモ追加";

    [InputLabel("メモ（管理者のみ閲覧）")]
    [ModalTextInput("admin_member_manage_note_value", TextInputStyle.Paragraph, maxLength: 300)]
    public string Note { get; set; } = string.Empty;
}

public sealed class MemberManagePeriodModal : IModal
{
    public string Title => "参照期間設定";

    [InputLabel("参照期間（日）")]
    [ModalTextInput("admin_member_manage_period_days", maxLength: 3)]
    public string Days { get; set; } = "30";
}
