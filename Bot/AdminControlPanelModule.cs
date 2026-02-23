using System.Text;
using System.Security.Cryptography;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

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
    private readonly RecruitmentListService _recruitListService;
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
    private const string RuntimeTuningEditCustomId = "admin_runtime_tuning_edit";
    private const string RuntimeTuningModalCustomId = "admin_runtime_tuning_modal";

    public AdminControlPanelModule(
        Db db,
        IOptions<DiscordOptions> options,
        LotteryCreateDraftContextTracker lotteryCreateDraftTracker,
        GuildAuditLogService audit,
        RuntimeDiagnosticsService runtimeDiagnostics,
        RuntimeTuningService runtimeTuning,
        RecruitmentListService recruitListService)
    {
        _db = db;
        _options = options.Value;
        _lotteryCreateDraftTracker = lotteryCreateDraftTracker;
        _audit = audit;
        _runtimeDiagnostics = runtimeDiagnostics;
        _runtimeTuning = runtimeTuning;
        _recruitListService = recruitListService;
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

        await RespondAsync(
            embed: BuildRuntimeTuningEmbed(),
            components: new ComponentBuilder()
                .WithButton("編集", RuntimeTuningEditCustomId, ButtonStyle.Primary, row: 0)
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

        await RespondWithModalAsync<AdminRuntimeTuningModal>(RuntimeTuningModalCustomId);
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

        var updated = _runtimeTuning.Update(cleanupMinutes, closedHours, canceledHours);
        await _db.UpsertGuildRuntimeTuningAsync(
            Context.Guild.Id,
            updated.CleanupIntervalMinutes,
            updated.RecruitClosedRetentionHours,
            updated.RecruitCanceledRetentionHours);
        await _audit.SendAsync(
            Context.Guild,
            "運用設定更新",
            $"👤 実行者: {Context.User.Mention} ({Context.User})\n⏱️ Cleanup間隔: {updated.CleanupIntervalMinutes}分\n📦 募集保持(終了): {updated.RecruitClosedRetentionHours}h\n📦 募集保持(中断): {updated.RecruitCanceledRetentionHours}h\n💾 DBに保存しました（再起動後も維持）。",
            Color.Blue,
            Context.User);

        await RespondAsync(embed: BuildRuntimeTuningEmbed(), ephemeral: true);
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
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? true;
        if (Context.Interaction is SocketMessageComponent component && component.Message is IUserMessage message)
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }

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
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? true;
        if (Context.Interaction is SocketMessageComponent component && component.Message is IUserMessage message)
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }

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
        var embed = new EmbedBuilder()
            .WithTitle("Bot Health Check")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("総合", overall, true)
            .AddField("自動キック", autoKickEnabled ? "ON" : "OFF", true)
            .AddField("対象Guild", $"{guild.Name} ({guild.Id})", true)
            .AddField("Cleanup間隔", $"{cleanupIntervalMinutes}分", true)
            .AddField("募集Cleanup", recruitCleanupPolicy, true)
            .AddField("修復導線", "ADMINパネルの `パネル再同期` を使用", true)
            .AddField("詳細", string.IsNullOrWhiteSpace(summary) ? "なし" : summary, false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private Embed BuildRuntimeTuningEmbed()
    {
        var tuning = _runtimeTuning.GetSnapshot();
        return new EmbedBuilder()
            .WithTitle("運用設定")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("Cleanup間隔", $"{tuning.CleanupIntervalMinutes}分", true)
            .AddField("募集保持(終了)", $"{tuning.RecruitClosedRetentionHours}時間", true)
            .AddField("募集保持(中断)", $"{tuning.RecruitCanceledRetentionHours}時間", true)
            .AddField("保存", "DB保存（再起動後も維持）", false)
            .Build();
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
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? true;
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

    private static Embed BuildAdminStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        var stateText = approvalRequired ? "必要" : "不要";
        var note = approvalRequired
            ? "申請は登録確認チャンネルで管理者が許可/拒否します。"
            : "申請送信時に即時でメンバーロールを付与します。";
        var autoKickText = inactiveAutoKickEnabled ? "ON" : "OFF";
        var autoKickNote = inactiveAutoKickEnabled
            ? "毎日 00:00 (JST) に30日非アクティブのユーザーを判定して自動キックします。"
            : "30日非アクティブ自動キックは無効です。";

        return new EmbedBuilder()
            .WithTitle("ADMIN設定")
            .WithColor(inactiveAutoKickEnabled ? Color.Orange : Color.Green)
            .WithCurrentTimestamp()
            .AddField("登録確認フロー", stateText, true)
            .AddField("動作", note, false)
            .AddField("30日非アクティブ自動キック", autoKickText, true)
            .AddField("キック動作", autoKickNote, false)
            .Build();
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

    private static string BuildMessageLogSearchLines(IReadOnlyList<MessageLog> rows, int maxRows)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows
            .Take(Math.Clamp(maxRows, 1, 30))
            .Select(row =>
            {
                var time = row.CreatedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
                var content = TruncateSingleLine(string.IsNullOrWhiteSpace(row.Content) ? "(no content)" : row.Content, 60);
                var deleted = row.DeletedAt.HasValue ? " [deleted]" : string.Empty;
                return $"[{time}] <#{ToUlong(row.ChannelId)}> <@{ToUlong(row.AuthorId)}> {content}{deleted}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static string BuildVoiceLogSearchLines(IReadOnlyList<VoiceSession> rows, int maxRows, DateTimeOffset toUtcExclusive)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows
            .Take(Math.Clamp(maxRows, 1, 30))
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

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, Math.Max(1, maxLength - 3)) + "...";
    }

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
}
