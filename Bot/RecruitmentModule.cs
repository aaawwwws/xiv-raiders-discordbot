using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("recruit", "募集パネル")]
public sealed class RecruitmentModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly ILogger<RecruitmentModule> _logger;
    private readonly UserProfileEmbedService _profileEmbeds;
    private readonly RecruitJoinContextTracker _joinTracker;
    private readonly RecruitCommentEditContextTracker _commentEditTracker;
    private readonly RecruitCreateDraftContextTracker _createDraftTracker;
    private readonly RecruitPostEditContextTracker _postEditTracker;
    private readonly RecruitmentListService _listService;
    private readonly GuildAuditLogService _audit;
    private readonly DiscordOptions _options;
    private static readonly TimeSpan CreateDraftTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InteractionContextTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private const string RecruitScheduleFormat = "yyyy/MM/dd HH:mm";
    private const int CreateSchedulePageSize = 7;
    private const int CreateScheduleMaxDayOffset = 60;
    private const int ManageSelectionPageSize = 20;

    public RecruitmentModule(
        Db db,
        ILogger<RecruitmentModule> logger,
        UserProfileEmbedService profileEmbeds,
        RecruitJoinContextTracker joinTracker,
        RecruitCommentEditContextTracker commentEditTracker,
        RecruitCreateDraftContextTracker createDraftTracker,
        RecruitPostEditContextTracker postEditTracker,
        RecruitmentListService listService,
        GuildAuditLogService audit,
        IOptions<DiscordOptions> options)
    {
        _db = db;
        _logger = logger;
        _profileEmbeds = profileEmbeds;
        _joinTracker = joinTracker;
        _commentEditTracker = commentEditTracker;
        _createDraftTracker = createDraftTracker;
        _postEditTracker = postEditTracker;
        _listService = listService;
        _audit = audit;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルに募集パネルを設置します")]
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

        var existing = await _db.GetGuildRecruitPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = existing is null ? null : ToUlong(existing.MessageId);
        var message = await EnsurePanelMessageAsync(channel, existingMessageId);

        await _db.UpsertGuildRecruitPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        if (!TryResolveRecruitTargetChannel(Context.Guild, channel, _options.RecruitPostChannelName, out var listTargetChannel, out var listTargetError))
        {
            await FollowupAsync(listTargetError ?? "募集一覧の投稿先チャンネル指定が不正です。", ephemeral: true);
            return;
        }

        var listPanel = await _db.GetGuildRecruitListPanelAsync(Context.Guild.Id);
        ulong? listMessageId = listPanel is null ? null : ToUlong(listPanel.MessageId);
        var listMessage = await _listService.EnsureListMessageAsync(Context.Guild, listTargetChannel, listMessageId);
        if (listMessage is not null)
        {
            await _db.UpsertGuildRecruitListPanelAsync(Context.Guild.Id, listTargetChannel.Id, listMessage.Id);
        }
        await FollowupAsync($"このチャンネルに募集パネルを設置しました。募集一覧は <#{listTargetChannel.Id}> に表示します。", ephemeral: true);
    }

    [SlashCommand("cancel", "主催者/管理ロールが募集を中断します（URL/ID指定）")]
    public Task CancelByAdminAsync(
        [Summary("message", "募集メッセージのURLまたはID")] string messageIdOrUrl)
    {
        var userMessage = IsRecruitCancelPrivilegedUser()
            ? "管理ロールにより募集を中断しました。"
            : "募集を中断しました。";
        return HandleManageSelectionAsync(messageIdOrUrl, RecruitPostStatus.Canceled, userMessage);
    }

    [RequireBotAdmin]
    [SlashCommand("close", "管理者が募集を終了します（URL/ID指定）")]
    public Task CloseByAdminAsync(
        [Summary("message", "募集メッセージのURLまたはID")] string messageIdOrUrl)
        => HandleManageSelectionAsync(messageIdOrUrl, RecruitPostStatus.Closed, "管理者により募集を終了しました。", requireCreator: false);

    [RequireBotAdmin]
    [SlashCommand("reopen", "管理者が募集を再開します（URL/ID指定）")]
    public Task ReopenByAdminAsync(
        [Summary("message", "募集メッセージのURLまたはID")] string messageIdOrUrl)
        => HandleReopenAsync(messageIdOrUrl, requireCreator: false);

    [RequireBotAdmin]
    [SlashCommand("cancel_all", "管理者が募集中の全募集を中断し、募集チャンネルの発言を全削除します")]
    public async Task CancelAllByAdminAsync()
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel currentChannel)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var summaries = (await _db.ListRecruitPostSummariesAsync(Context.Guild.Id))
            .Where(post => post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
            .ToList();
        if (summaries.Count == 0)
        {
            await FollowupAsync("中断対象の募集はありません。", ephemeral: true);
            return;
        }

        var canceledCount = 0;
        var skippedCount = 0;
        var syncWarningCount = 0;
        var deletedMessageCount = 0;
        var deletedThreadCount = 0;

        foreach (var summary in summaries)
        {
            var post = await _db.GetRecruitPostByMessageIdAsync(ToUlong(summary.MessageId));
            if (post is null || post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
            {
                skippedCount++;
                continue;
            }

            try
            {
                var previousStatus = post.Status;
                await _db.UpdateRecruitPostStatusAsync(post.Id, RecruitPostStatus.Canceled);
                post.Status = RecruitPostStatus.Canceled;
                canceledCount++;
                await SendRecruitStatusTransitionAuditAsync(post, previousStatus, RecruitPostStatus.Canceled, "手動（一括中断）");
            }
            catch (Exception ex)
            {
                skippedCount++;
                _logger.LogWarning(ex, "Failed to cancel recruit post {PostId} in cancel_all.", summary.Id);
                continue;
            }

            try
            {
                await UpdateRecruitMessageAsync(post);
            }
            catch (Exception ex)
            {
                syncWarningCount++;
                _logger.LogWarning(ex, "Failed to refresh recruit message for post {PostId} in cancel_all.", post.Id);
            }

            try
            {
                var (handled, deleted) = await DeleteRecruitThreadForCancelAllAsync(post);
                if (deleted)
                {
                    deletedThreadCount++;
                }

                if (!handled)
                {
                    syncWarningCount++;
                }
            }
            catch (Exception ex)
            {
                syncWarningCount++;
                _logger.LogWarning(ex, "Failed to remove recruit thread for post {PostId} in cancel_all.", post.Id);
            }

        }

        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);

        if (!TryResolveRecruitTargetChannel(Context.Guild, currentChannel, _options.RecruitPostChannelName, out var recruitChannel, out var channelResolveError))
        {
            syncWarningCount++;
            _logger.LogWarning("Failed to resolve recruit channel in cancel_all. Error={Error}", channelResolveError ?? "(null)");
        }
        else
        {
            try
            {
                deletedMessageCount = await DeleteAllChannelMessagesAsync(recruitChannel);
            }
            catch (Exception ex)
            {
                syncWarningCount++;
                _logger.LogWarning(ex, "Failed to delete all messages in recruit channel {ChannelId} during cancel_all.", recruitChannel.Id);
            }
        }

        await _audit.SendAsync(
            Context.Guild,
            "募集管理: 一括中断",
            $"👤 実行者: {Context.User.Mention} ({Context.User})\n🛠️ 操作: 募集中の全募集を中断 + 募集チャンネル全削除 + 募集スレッド削除\n📊 結果: 中断 {canceledCount} 件 / スキップ {skippedCount} 件 / 削除 {deletedMessageCount} 件 / スレッド削除 {deletedThreadCount} 件",
            Color.Orange,
            Context.User);

        var warningSuffix = syncWarningCount > 0
            ? $"\n（処理中に {syncWarningCount} 件の警告。ログを確認してください）"
            : string.Empty;
        await FollowupAsync($"募集中の募集を一括中断し、募集チャンネル発言を全削除しました。中断: {canceledCount} 件 / スキップ: {skippedCount} 件 / 削除: {deletedMessageCount} 件 / スレッド削除: {deletedThreadCount} 件{warningSuffix}", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("template_save", "募集テンプレートを保存または更新します")]
    public async Task SaveTemplateAsync(
        [Summary("name", "テンプレート名（英数字/記号）")] string name,
        [Summary("title", "募集タイトル")] string title,
        [Summary("dc", "開催DC")] string dataCenter,
        [Summary("positions", "ジョブ指定（任意）")] string? positions = null,
        [Summary("note", "コメント（任意）")] string? note = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var key = NormalizeTemplateKey(name);
        if (string.IsNullOrWhiteSpace(key))
        {
            await RespondAsync("テンプレート名を入力してください。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitDataCenter(dataCenter, out var normalizedDc, out var dcError))
        {
            await RespondAsync(dcError ?? "開催DCの入力が不正です。", ephemeral: true);
            return;
        }

        if (!TryParseRecruitSlotsForCreate(positions, out _, out _, out var positionError))
        {
            await RespondAsync(positionError ?? "ジョブ指定の入力が不正です。", ephemeral: true);
            return;
        }

        await _db.UpsertRecruitTemplateAsync(Context.Guild.Id, key, title, normalizedDc, note, positions);
        await RespondAsync($"テンプレート `{key}` を保存しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("template_delete", "募集テンプレートを削除します")]
    public async Task DeleteTemplateAsync([Summary("name", "テンプレート名")] string name)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var key = NormalizeTemplateKey(name);
        if (string.IsNullOrWhiteSpace(key))
        {
            await RespondAsync("テンプレート名を入力してください。", ephemeral: true);
            return;
        }

        var deleted = await _db.DeleteRecruitTemplateAsync(Context.Guild.Id, key);
        await RespondAsync(
            deleted ? $"テンプレート `{key}` を削除しました。" : $"テンプレート `{key}` は見つかりませんでした。",
            ephemeral: true);
    }

    [SlashCommand("template_list", "募集テンプレート一覧を表示します")]
    public async Task ListTemplatesAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var templates = await _db.ListRecruitTemplatesAsync(Context.Guild.Id);
        if (templates.Count == 0)
        {
            await RespondAsync("テンプレートはまだありません。", ephemeral: true);
            return;
        }

        var lines = templates
            .Take(20)
            .Select(x => $"`{x.TemplateKey}` | {x.Title} | {x.DataCenter ?? "未指定"}")
            .ToList();
        if (templates.Count > 20)
        {
            lines.Add($"...他 {templates.Count - 20} 件");
        }

        await RespondAsync(string.Join('\n', lines), ephemeral: true);
    }

    [SlashCommand("template_use", "募集テンプレートを使って募集を作成します")]
    public async Task UseTemplateAsync(
        [Summary("name", "テンプレート名")] string name,
        [Summary("schedule", "開始日時（例: 2026/02/09 21:00 / 21:00 / 明日21:00 / 今）")] string schedule)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var key = NormalizeTemplateKey(name);
        if (string.IsNullOrWhiteSpace(key))
        {
            await RespondAsync("テンプレート名を入力してください。", ephemeral: true);
            return;
        }

        var template = await _db.GetRecruitTemplateAsync(Context.Guild.Id, key);
        if (template is null)
        {
            await RespondAsync($"テンプレート `{key}` が見つかりません。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitSchedule(schedule, out var normalizedSchedule, out var scheduleAt, out var scheduleError))
        {
            await RespondAsync(scheduleError ?? "開始日時の入力が不正です。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitDataCenter(template.DataCenter, out var dataCenter, out var dataCenterError))
        {
            await RespondAsync(dataCenterError ?? "テンプレートの開催DCが不正です。", ephemeral: true);
            return;
        }

        if (!TryParseRecruitSlotsForCreate(template.PositionsText, out var positions, out var requiredJobs, out var positionError))
        {
            await RespondAsync(positionError ?? "テンプレートのジョブ指定が不正です。", ephemeral: true);
            return;
        }

        if (!TryResolveRecruitTargetChannel(Context.Guild, channel, _options.RecruitPostChannelName, out var targetChannel, out var targetError))
        {
            await RespondAsync(targetError ?? "投稿先チャンネルの指定が不正です。", ephemeral: true);
            return;
        }

        _createDraftTracker.Set(Context.User.Id, new RecruitCreateDraftContext(
            Context.Guild.Id,
            channel.Id,
            targetChannel.Id,
            template.Title,
            dataCenter,
            normalizedSchedule,
            scheduleAt,
            template.Note,
            positions.ToArray(),
            requiredJobs,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RecruitPreFilledFlexSlot>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RecruitPreFilledMember>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            null,
            _options.RecruitDisallowDuplicateJobs,
            null,
            null,
            DateTimeOffset.UtcNow));

        await RespondAsync(
            $"テンプレ `{key}` を読み込みました。主催ポジションを選択してください。",
            components: BuildCreateHostSelectComponents(positions),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_create", ignoreGroupNames: true)]
    public async Task ShowCreateModalAsync()
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        var channelName = _options.RecruitPostChannelName;
        if (!TryResolveRecruitTargetChannel(Context.Guild, channel, channelName, out var targetChannel, out var targetChannelError))
        {
            await RespondAsync(targetChannelError ?? "投稿先チャンネルの指定が不正です。", ephemeral: true);
            return;
        }

        _createDraftTracker.Set(Context.User.Id, new RecruitCreateDraftContext(
            Context.Guild.Id,
            channel.Id,
            targetChannel.Id,
            string.Empty,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RecruitPreFilledFlexSlot>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, RecruitPreFilledMember>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            null,
            _options.RecruitDisallowDuplicateJobs,
            null,
            null,
            DateTimeOffset.UtcNow));

        await RespondAsync(
            "募集タイプを選択してください。ジョブ重複設定もここで変更できます。",
            components: BuildCreatePartySizeSelectComponents(_options.RecruitDisallowDuplicateJobs),
            ephemeral: true);
    }

    [ModalInteraction("recruit_create_modal", ignoreGroupNames: true)]
    public async Task HandleCreateModalAsync(RecruitCreateModal modal)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();

        var title = modal.RecruitTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("募集タイトルを入力してください。", ephemeral: true);
            return;
        }

        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集作成を開始してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.Positions.Count == 0)
        {
            await RespondAsync("募集タイプを先に選択してください。", components: BuildCreatePartySizeSelectComponents(draft.DisallowDuplicateJobs), ephemeral: true);
            return;
        }

        var note = modal.Note?.Trim();
        _createDraftTracker.Set(Context.User.Id, draft with
        {
            Title = title,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await RespondAsync(
            $"募集タイプを設定しました。タイトルを `{title}` で保存しました。\n開催DCを選択してください。",
            components: BuildCreateDataCenterSelectComponents(),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_create_party_full", ignoreGroupNames: true)]
    public Task SelectCreatePartyFullAsync()
        => SelectCreatePartySizeAsync(RecruitmentRules.Positions);

    [ComponentInteraction("recruit_create_party_light", ignoreGroupNames: true)]
    public Task SelectCreatePartyLightAsync()
        => SelectCreatePartySizeAsync(RecruitmentRules.LightPartyPositions);

    [ComponentInteraction("recruit_create_dup_on", ignoreGroupNames: true)]
    public Task SelectCreateDuplicateJobsOnAsync()
        => SelectCreateDuplicateSettingAsync(true);

    [ComponentInteraction("recruit_create_dup_off", ignoreGroupNames: true)]
    public Task SelectCreateDuplicateJobsOffAsync()
        => SelectCreateDuplicateSettingAsync(false);

    [ComponentInteraction("recruit_create_dc_meteor", ignoreGroupNames: true)]
    public Task SelectCreateDcMeteorAsync() => SelectCreateDataCenterAsync("Meteor");

    [ComponentInteraction("recruit_create_dc_gaia", ignoreGroupNames: true)]
    public Task SelectCreateDcGaiaAsync() => SelectCreateDataCenterAsync("Gaia");

    [ComponentInteraction("recruit_create_dc_mana", ignoreGroupNames: true)]
    public Task SelectCreateDcManaAsync() => SelectCreateDataCenterAsync("Mana");

    [ComponentInteraction("recruit_create_dc_elemental", ignoreGroupNames: true)]
    public Task SelectCreateDcElementalAsync() => SelectCreateDataCenterAsync("Elemental");

    [ComponentInteraction("recruit_create_schedule_now", ignoreGroupNames: true)]
    public Task SelectCreateScheduleNowAsync() => SelectCreateScheduleNowInternalAsync();

    [ComponentInteraction("recruit_create_schedule_quick:*", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleQuickAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            !int.TryParse(parts[2], out var minute) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleMinuteInternalAsync(dayOffset, hour, minute);
    }

    [ComponentInteraction("recruit_create_schedule_day_0", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay0Async() => SelectCreateScheduleDayInternalAsync(0);

    [ComponentInteraction("recruit_create_schedule_day_1", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay1Async() => SelectCreateScheduleDayInternalAsync(1);

    [ComponentInteraction("recruit_create_schedule_day_2", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay2Async() => SelectCreateScheduleDayInternalAsync(2);

    [ComponentInteraction("recruit_create_schedule_day_3", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay3Async() => SelectCreateScheduleDayInternalAsync(3);

    [ComponentInteraction("recruit_create_schedule_day_4", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay4Async() => SelectCreateScheduleDayInternalAsync(4);

    [ComponentInteraction("recruit_create_schedule_day_5", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay5Async() => SelectCreateScheduleDayInternalAsync(5);

    [ComponentInteraction("recruit_create_schedule_day_6", ignoreGroupNames: true)]
    public Task SelectCreateScheduleDay6Async() => SelectCreateScheduleDayInternalAsync(6);

    [ComponentInteraction("recruit_create_schedule_page:*", ignoreGroupNames: true)]
    public async Task SelectCreateSchedulePageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        var maxPage = CreateScheduleMaxDayOffset / CreateSchedulePageSize;
        if (pageIndex < 0 || pageIndex > maxPage)
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        await ShowCreateScheduleDatePageAsync(pageIndex);
    }

    [ComponentInteraction("recruit_create_schedule_day_pick:*", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleDayPickAsync(string dayOffsetRaw)
    {
        if (!int.TryParse(dayOffsetRaw, out var dayOffset) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset)
        {
            await RespondAsync("日付の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleDayInternalAsync(dayOffset);
    }

    [ComponentInteraction("recruit_create_schedule_hour:*", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleHourAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23)
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleHourInternalAsync(dayOffset, hour);
    }

    [ComponentInteraction("recruit_create_schedule_hour_select", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleHourSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleHourAsync(values[0]);
    }

    [ComponentInteraction("recruit_create_schedule_minute:*", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleMinuteAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            !int.TryParse(parts[2], out var minute) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23 ||
            (minute != 0 && minute != 10 && minute != 20 && minute != 30 && minute != 40 && minute != 50))
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleMinuteInternalAsync(dayOffset, hour, minute);
    }

    [ComponentInteraction("recruit_create_schedule_minute_select", ignoreGroupNames: true)]
    public async Task SelectCreateScheduleMinuteSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectCreateScheduleMinuteAsync(values[0]);
    }

    [ComponentInteraction("recruit_create_host_MT", ignoreGroupNames: true)]
    public Task OpenCreateHostJobMtAsync() => OpenCreateHostJobSelectAsync("MT");

    [ComponentInteraction("recruit_create_host_ST", ignoreGroupNames: true)]
    public Task OpenCreateHostJobStAsync() => OpenCreateHostJobSelectAsync("ST");

    [ComponentInteraction("recruit_create_host_H1", ignoreGroupNames: true)]
    public Task OpenCreateHostJobH1Async() => OpenCreateHostJobSelectAsync("H1");

    [ComponentInteraction("recruit_create_host_H2", ignoreGroupNames: true)]
    public Task OpenCreateHostJobH2Async() => OpenCreateHostJobSelectAsync("H2");

    [ComponentInteraction("recruit_create_host_D1", ignoreGroupNames: true)]
    public Task OpenCreateHostJobD1Async() => OpenCreateHostJobSelectAsync("D1");

    [ComponentInteraction("recruit_create_host_D2", ignoreGroupNames: true)]
    public Task OpenCreateHostJobD2Async() => OpenCreateHostJobSelectAsync("D2");

    [ComponentInteraction("recruit_create_host_D3", ignoreGroupNames: true)]
    public Task OpenCreateHostJobD3Async() => OpenCreateHostJobSelectAsync("D3");

    [ComponentInteraction("recruit_create_host_D4", ignoreGroupNames: true)]
    public Task OpenCreateHostJobD4Async() => OpenCreateHostJobSelectAsync("D4");

    [ComponentInteraction("recruit_create_host_flex_d12", ignoreGroupNames: true)]
    public Task OpenCreateHostJobFlexD12Async() => OpenCreateHostJobSelectAsync("FLEX:D1,D2");

    [ComponentInteraction("recruit_create_host_flex_any", ignoreGroupNames: true)]
    public Task OpenCreateHostJobFlexAnyAsync() => ShowCreateHostFlexPositionSelectAsync();

    [ComponentInteraction("recruit_create_host_flex_positions", ignoreGroupNames: true)]
    public async Task HandleCreateHostFlexPositionsAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var selectedPositionSet = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(position => draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
            .Where(position => !draft.PreFilledMembers.ContainsKey(position))
            .Where(position => !IsPositionCoveredByCreatePrefilledFlexSlot(position, draft))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPositions = draft.Positions
            .Where(position => selectedPositionSet.Contains(position))
            .Select(position => position.Trim().ToUpperInvariant())
            .ToArray();
        if (selectedPositions.Length == 0)
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await OpenCreateHostJobSelectAsync($"FLEX:{string.Join(',', selectedPositions)}");
    }

    [ComponentInteraction("recruit_create_host_flex_back", ignoreGroupNames: true)]
    public async Task HandleCreateHostFlexBackAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        await RespondAsync(
            "主催ポジションを選択してください。",
            components: BuildCreateHostSelectComponents(draft.Positions),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_create_host_job_MT", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobMtAsync(string[] values) => ConfirmCreateHostAsync("MT", values);

    [ComponentInteraction("recruit_create_host_job_ST", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobStAsync(string[] values) => ConfirmCreateHostAsync("ST", values);

    [ComponentInteraction("recruit_create_host_job_H1", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobH1Async(string[] values) => ConfirmCreateHostAsync("H1", values);

    [ComponentInteraction("recruit_create_host_job_H2", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobH2Async(string[] values) => ConfirmCreateHostAsync("H2", values);

    [ComponentInteraction("recruit_create_host_job_D1", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobD1Async(string[] values) => ConfirmCreateHostAsync("D1", values);

    [ComponentInteraction("recruit_create_host_job_D2", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobD2Async(string[] values) => ConfirmCreateHostAsync("D2", values);

    [ComponentInteraction("recruit_create_host_job_D3", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobD3Async(string[] values) => ConfirmCreateHostAsync("D3", values);

    [ComponentInteraction("recruit_create_host_job_D4", ignoreGroupNames: true)]
    public Task ConfirmCreateHostJobD4Async(string[] values) => ConfirmCreateHostAsync("D4", values);

    [ComponentInteraction("recruit_create_host_flex_job_select:*", ignoreGroupNames: true)]
    public Task HandleCreateHostFlexJobSelectAsync(string flexSpec, string[] values)
        => ConfirmCreateHostAsync($"FLEX:{flexSpec}", values);

    [ComponentInteraction("recruit_create_host_job_pick:*", ignoreGroupNames: true)]
    public async Task ConfirmCreateHostJobButtonAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await RespondAsync("主催ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        var hostPosition = parts[0].Trim().ToUpperInvariant();
        var hostJob = parts[1].Trim();
        var isFlexSelector = TryParseFlexPositionToken(hostPosition, out _);
        if ((!RecruitmentRules.Positions.Contains(hostPosition, StringComparer.OrdinalIgnoreCase) && !isFlexSelector) ||
            string.IsNullOrWhiteSpace(hostJob))
        {
            await RespondAsync("主催ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        await ConfirmCreateHostAsync(hostPosition, new[] { hostJob });
    }

    [ComponentInteraction("recruit_create_confirm_post", ignoreGroupNames: true)]
    public Task ConfirmCreatePostAsync() => ConfirmCreatePostInternalAsync();

    [ComponentInteraction("recruit_create_confirm_edit_jobs", ignoreGroupNames: true)]
    public Task ShowCreateRequiredPositionSelectAsync() => ShowCreateRequiredPositionSelectInternalAsync();

    [ComponentInteraction("recruit_create_confirm_edit_prefill", ignoreGroupNames: true)]
    public Task ShowCreatePrefillPositionSelectAsync() => ShowCreatePrefillPositionSelectInternalAsync();

    [ComponentInteraction("recruit_create_confirm_edit_schedule", ignoreGroupNames: true)]
    public Task ShowCreateConfirmScheduleSelectAsync() => ShowCreateScheduleSelectFromConfirmAsync();

    [ComponentInteraction("recruit_create_confirm_edit_host", ignoreGroupNames: true)]
    public Task ShowCreateConfirmHostSelectAsync() => ShowCreateHostSelectFromConfirmAsync();

    [ComponentInteraction("recruit_create_confirm_edit_mention_role", ignoreGroupNames: true)]
    public Task ShowCreateConfirmMentionRoleSelectAsync() => ShowCreateMentionRoleSelectFromConfirmAsync();

    [ComponentInteraction("recruit_create_mention_role_select", ignoreGroupNames: true)]
    public async Task HandleCreateMentionRoleSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("メンションロールの選択情報が不正です。", ephemeral: true);
            return;
        }

        var roleIds = new HashSet<ulong>();
        foreach (var value in values)
        {
            if (!ulong.TryParse(value, out var roleId) || roleId == 0)
            {
                await RespondAsync("メンションロールの選択情報が不正です。", ephemeral: true);
                return;
            }

            roleIds.Add(roleId);
        }

        if (roleIds.Count == 0)
        {
            await RespondAsync("メンションロールを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await UpdateCreateMentionRoleInternalAsync(roleIds.ToArray());
    }

    [ComponentInteraction("recruit_create_mention_role_clear", ignoreGroupNames: true)]
    public Task ClearCreateMentionRoleAsync() => UpdateCreateMentionRoleInternalAsync(Array.Empty<ulong>());

    [ComponentInteraction("recruit_create_mention_role_back", ignoreGroupNames: true)]
    public Task BackCreateMentionRoleToConfirmAsync() => ShowCreateConfirmationInternalAsync("投稿前に内容を確認してください。");

    [ComponentInteraction("recruit_create_required_back", ignoreGroupNames: true)]
    public Task BackCreateRequiredToConfirmAsync() => ShowCreateConfirmationInternalAsync("投稿前に内容を確認してください。");

    [ComponentInteraction("recruit_create_prefill_back", ignoreGroupNames: true)]
    public Task BackCreatePrefillToConfirmAsync() => ShowCreateConfirmationInternalAsync("投稿前に内容を確認してください。");

    [ComponentInteraction("recruit_create_prefill_clear", ignoreGroupNames: true)]
    public Task ShowCreatePrefillClearSelectAsync() => ShowCreatePrefillClearSelectInternalAsync();

    [ComponentInteraction("recruit_create_required_pos:*", ignoreGroupNames: true)]
    public async Task OpenCreateRequiredJobSelectAsync(string positionRaw)
    {
        var position = positionRaw.Trim().ToUpperInvariant();
        if (!RecruitmentRules.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        await OpenCreateRequiredJobSelectInternalAsync(position);
    }

    [ComponentInteraction("recruit_create_required_job:*", ignoreGroupNames: true)]
    public async Task HandleCreateRequiredJobSelectAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await RespondAsync("ジョブ指定の選択情報が不正です。", ephemeral: true);
            return;
        }

        var position = parts[0].Trim().ToUpperInvariant();
        var job = parts[1].Trim();
        if (!RecruitmentRules.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("ジョブ指定の選択情報が不正です。", ephemeral: true);
            return;
        }

        await UpdateCreateRequiredJobInternalAsync(position, job);
    }

    [ComponentInteraction("recruit_create_prefill_pos:*", ignoreGroupNames: true)]
    public async Task OpenCreatePrefillJobSelectAsync(string positionRaw)
    {
        var position = positionRaw.Trim().ToUpperInvariant();
        if (!RecruitmentRules.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowCreatePrefillSourceSelectInternalAsync(position);
    }

    [ComponentInteraction("recruit_create_prefill_flex_d12", ignoreGroupNames: true)]
    public Task OpenCreatePrefillFlexD12Async()
        => ShowCreatePrefillFlexSourceSelectInternalAsync("D1,D2");

    [ComponentInteraction("recruit_create_prefill_flex_any", ignoreGroupNames: true)]
    public Task OpenCreatePrefillFlexAnyAsync()
        => ShowCreatePrefillFlexSourceSelectInternalAsync("ANY");

    [ComponentInteraction("recruit_create_prefill_flex_positions", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillFlexPositionsAsync(string[] values)
        => await HandleCreatePrefillFlexPositionsInternalAsync(values, null);

    [ComponentInteraction("recruit_create_prefill_flex_positions_member", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillFlexPositionsMemberAsync(string[] values)
        => await HandleCreatePrefillFlexPositionsInternalAsync(values, true);

    [ComponentInteraction("recruit_create_prefill_flex_positions_external", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillFlexPositionsExternalAsync(string[] values)
        => await HandleCreatePrefillFlexPositionsInternalAsync(values, false);

    [ComponentInteraction("recruit_create_prefill_flex_member_source:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillFlexMemberSourceAsync(string flexSpecRaw)
        => await HandleCreatePrefillFlexSourceInternalAsync(flexSpecRaw, useMemberSource: true);

    [ComponentInteraction("recruit_create_prefill_flex_external_source:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillFlexExternalSourceAsync(string flexSpecRaw)
        => await HandleCreatePrefillFlexSourceInternalAsync(flexSpecRaw, useMemberSource: false);

    private async Task HandleCreatePrefillFlexPositionsInternalAsync(string[] values, bool? useMemberSource)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var selectedPositionSet = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(position => draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedPositions = draft.Positions
            .Where(position => selectedPositionSet.Contains(position))
            .Select(position => position.Trim().ToUpperInvariant())
            .ToArray();
        var allowHostPosition = CanIncludeHostPositionInCreatePrefillSelector(requestedPositions, draft);
        var selectedPositions = requestedPositions
            .Where(position => allowHostPosition || !string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selectedPositions.Length == 0)
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selector = $"FLEX:{string.Join(',', selectedPositions)}";
        if (!useMemberSource.HasValue)
        {
            await ShowCreatePrefillSourceSelectInternalAsync(selector);
            return;
        }

        if (useMemberSource.Value)
        {
            await OpenCreatePrefillMemberJobSelectInternalAsync(selector);
            return;
        }

        await OpenCreatePrefillExternalJobSelectInternalAsync(selector);
    }

    [ComponentInteraction("recruit_create_prefill_flex_back", ignoreGroupNames: true)]
    public Task HandleCreatePrefillFlexBackAsync()
        => ShowCreatePrefillPositionSelectInternalAsync();

    [ComponentInteraction("recruit_create_prefill_member_yes:*", ignoreGroupNames: true)]
    public async Task SelectCreatePrefillMemberYesAsync(string positionRaw)
    {
        var selector = positionRaw.Trim().ToUpperInvariant();
        if (!TryParseFlexPositionToken(selector, out _) &&
            !RecruitmentRules.Positions.Contains(selector, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        await OpenCreatePrefillMemberJobSelectInternalAsync(selector);
    }

    [ComponentInteraction("recruit_create_prefill_member_no:*", ignoreGroupNames: true)]
    public async Task SelectCreatePrefillMemberNoAsync(string positionRaw)
    {
        var selector = positionRaw.Trim().ToUpperInvariant();
        if (!TryParseFlexPositionToken(selector, out _) &&
            !RecruitmentRules.Positions.Contains(selector, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        await OpenCreatePrefillExternalJobSelectInternalAsync(selector);
    }

    [ComponentInteraction("recruit_create_prefill_job:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillJobSelectAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await RespondAsync("事前埋まりの選択情報が不正です。", ephemeral: true);
            return;
        }

        var position = parts[0].Trim().ToUpperInvariant();
        var job = parts[1].Trim();
        if (!TryParseFlexPositionToken(position, out _) &&
            !RecruitmentRules.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("事前埋まりの選択情報が不正です。", ephemeral: true);
            return;
        }

        await UpdateCreatePrefillJobInternalAsync(position, job);
    }

    [ComponentInteraction("recruit_create_prefill_external_job:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillExternalJobSelectAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await RespondAsync("事前埋まり（外部）の選択情報が不正です。", ephemeral: true);
            return;
        }

        var selector = parts[0].Trim().ToUpperInvariant();
        var job = parts[1].Trim();
        if (!TryParseFlexPositionToken(selector, out _) &&
            !RecruitmentRules.Positions.Contains(selector, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("事前埋まり（外部）の選択情報が不正です。", ephemeral: true);
            return;
        }

        await UpdateCreatePrefillExternalJobInternalAsync(selector, new[] { job });
    }

    [ComponentInteraction("recruit_create_prefill_external_job_select:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillExternalJobMultiSelectAsync(string flexSpec, string[] values)
    {
        var selector = $"FLEX:{flexSpec.Trim().ToUpperInvariant()}";
        if (!TryParseFlexPositionToken(selector, out _))
        {
            await RespondAsync("事前埋まり（外部）の選択情報が不正です。", ephemeral: true);
            return;
        }

        var selectedJobs = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedJobs.Length == 0)
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await UpdateCreatePrefillExternalJobInternalAsync(selector, selectedJobs);
    }

    [ComponentInteraction("recruit_create_prefill_member_job:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillMemberJobSelectAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await RespondAsync("事前埋まりメンバー設定の選択情報が不正です。", ephemeral: true);
            return;
        }

        var position = parts[0].Trim().ToUpperInvariant();
        var job = parts[1].Trim();
        if (!TryParseFlexPositionToken(position, out _) &&
            !RecruitmentRules.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("事前埋まりメンバー設定の選択情報が不正です。", ephemeral: true);
            return;
        }

        await OpenCreatePrefillMemberUserSelectInternalAsync(position, new[] { job });
    }

    [ComponentInteraction("recruit_create_prefill_member_job_select:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillMemberJobMultiSelectAsync(string flexSpec, string[] values)
    {
        var selector = $"FLEX:{flexSpec.Trim().ToUpperInvariant()}";
        if (!TryParseFlexPositionToken(selector, out _))
        {
            await RespondAsync("事前埋まりメンバー設定の選択情報が不正です。", ephemeral: true);
            return;
        }

        var selectedJobs = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedJobs.Length == 0)
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await OpenCreatePrefillMemberUserSelectInternalAsync(selector, selectedJobs);
    }

    [ComponentInteraction("recruit_create_prefill_member_user", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillMemberUserSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 } || string.IsNullOrWhiteSpace(values[0]))
        {
            await RespondAsync("メンバーを選択してください。", ephemeral: true);
            return;
        }

        await UpdateCreatePrefillMemberUserInternalAsync(values[0]);
    }

    [ComponentInteraction("recruit_create_prefill_member_page:*", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillMemberPageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        await ShowCreatePrefillMemberUserSelectPageInternalAsync(pageIndex);
    }

    [ComponentInteraction("recruit_create_prefill_clear_select", ignoreGroupNames: true)]
    public async Task HandleCreatePrefillClearSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 } || string.IsNullOrWhiteSpace(values[0]))
        {
            await RespondAsync("解除する事前埋まりを選択してください。", ephemeral: true);
            return;
        }

        await ClearCreatePrefillSelectionInternalAsync(values[0]);
    }

    [ComponentInteraction("recruit_create_host_cancel", ignoreGroupNames: true)]
    public async Task CancelCreateHostAsync()
    {
        PruneInteractionContexts();
        if (!_createDraftTracker.TryRemove(Context.User.Id, out _))
        {
            await RespondAsync("作成中の募集はありません。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        await RespondAsync("募集作成を中止しました。", ephemeral: true);
    }

    [ModalInteraction("recruit_join_comment_modal", ignoreGroupNames: true)]
    public async Task HandleJoinCommentModalAsync(RecruitJoinCommentModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_joinTracker.TryRemove(Context.User.Id, out var context))
        {
            await RespondAsync("参加情報が見つかりませんでした。もう一度参加してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - context.CreatedAt > InteractionContextTimeout)
        {
            await RespondAsync("参加情報の有効期限が切れました。もう一度参加してください。", ephemeral: true);
            return;
        }

        var comment = modal.Comment?.Trim();

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
        if (post is null)
        {
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var previous = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        var displayName = Context.User.GlobalName ?? Context.User.Username;
        var (status, assignedPosition, assignedJob) = await AssignRecruitSlotByContextAsync(post, context, Context.User.Id, displayName, comment);
        var isFlexRequest = context.CandidatePositions is { Count: > 1 } ||
                            context.CandidateJobs is { Count: > 1 } ||
                            !string.IsNullOrWhiteSpace(context.RequestedLabel);
        if (status != RecruitAssignStatus.Success)
        {
            var error = status switch
            {
                RecruitAssignStatus.PositionTaken when isFlexRequest => "選択可能なFLEX枠が埋まっています。もう一度参加してください。",
                RecruitAssignStatus.PositionClosed when isFlexRequest => "選択可能なFLEX枠が〆になりました。もう一度参加してください。",
                RecruitAssignStatus.PositionNotFound when isFlexRequest => "選択可能なFLEX枠が見つかりません。もう一度参加してください。",
                RecruitAssignStatus.JobRestricted when isFlexRequest => "そのジョブで入れるFLEX枠がありません。もう一度参加してください。",
                RecruitAssignStatus.PositionTaken => $"{context.Position} は既に埋まっています。",
                RecruitAssignStatus.PositionClosed => $"{context.Position} は〆です（ゲーム内募集で埋まり）。",
                RecruitAssignStatus.JobTaken => "そのジョブは既に埋まっています。",
                RecruitAssignStatus.JobRestricted => $"{context.Position} は指定ジョブのみ参加できます。",
                RecruitAssignStatus.PositionNotFound => "ポジションが見つかりません。",
                RecruitAssignStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitAssignStatus.PostClosed => "募集は終了しました。",
                _ => "募集の更新に失敗しました。"
            };

            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);

        var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        if (slot is not null)
        {
            var isNewJoin = previous is null;
            var changed = previous is not null &&
                          (!string.Equals(previous.Position, slot.Position, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(previous.Job, slot.Job, StringComparison.OrdinalIgnoreCase));

            await EnsureRecruitThreadMemberAsync(post, Context.User.Id);
            var previousThreadMessageId = previous?.ThreadMessageId.HasValue == true
                ? ToUlong(previous.ThreadMessageId.Value)
                : (ulong?)null;
            await UpsertRecruitParticipantEmbedAsync(
                post,
                Context.User,
                slot,
                mentionHost: isNewJoin,
                fallbackThreadMessageId: previousThreadMessageId,
                preferFallbackThreadMessageId: changed);
            if (previous is not null && changed)
            {
                await PostRecruitChangeLogAsync(post, Context.User, previous, slot);
            }
        }

        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        var joinedPosition = slot?.Position ?? assignedPosition ?? context.Position;
        var joinedJob = slot?.Job ?? assignedJob ?? context.Job;
        var joinedPositionLabel = !string.IsNullOrWhiteSpace(context.RequestedLabel)
            ? context.RequestedLabel
            : joinedPosition;
        await FollowupAsync($"参加しました: {joinedPositionLabel} ({joinedJob})", ephemeral: true);
    }

    private async Task<(RecruitAssignStatus Status, string? Position, string? Job)> AssignRecruitSlotByContextAsync(
        RecruitPost post,
        RecruitJoinContext context,
        ulong userId,
        string userName,
        string? comment)
    {
        var candidates = (context.CandidatePositions is { Count: > 0 }
                ? context.CandidatePositions
                : new[] { context.Position })
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return (RecruitAssignStatus.PositionNotFound, null, null);
        }

        var jobs = (context.CandidateJobs is { Count: > 0 }
                ? context.CandidateJobs
                : new[] { context.Job })
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (jobs.Count == 0)
        {
            return (RecruitAssignStatus.JobRestricted, null, null);
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lastStatus = RecruitAssignStatus.PositionNotFound;
        var participantFlexJobsSpec = jobs.Count >= 2
            ? string.Join(',', jobs)
            : null;
        foreach (var job in jobs)
        {
            var orderedCandidates = OrderFlexCandidatePositionsForJob(candidates, job);
            var participantFlexSpec = ResolveParticipantFlexSpecForJob(orderedCandidates, job, slots, recruitPositions);
            foreach (var candidate in orderedCandidates)
            {
                var status = await _db.AssignRecruitSlotAsync(
                    post.Id,
                    candidate,
                    userId,
                    userName,
                    job,
                    comment ?? string.Empty,
                    post.DisallowDuplicateJobs,
                    participantFlexSpec,
                    participantFlexSpec is null ? null : participantFlexJobsSpec);
                if (status == RecruitAssignStatus.Success)
                {
                    return (RecruitAssignStatus.Success, candidate, job);
                }

                lastStatus = status;
                if (status is RecruitAssignStatus.PostNotFound or RecruitAssignStatus.PostClosed)
                {
                    return (status, null, null);
                }

                if (status == RecruitAssignStatus.JobTaken)
                {
                    break;
                }
            }
        }

        return (lastStatus, null, null);
    }

    private static IReadOnlyList<string> OrderFlexCandidatePositionsForJob(IReadOnlyList<string> candidates, string _)
    {
        return candidates;
    }

    private static string? ResolveParticipantFlexSpecForJob(
        IReadOnlyList<string> candidates,
        string job,
        IReadOnlyList<RecruitSlot> slots,
        IReadOnlyCollection<string> recruitPositions)
    {
        if (candidates.Count < 2)
        {
            return null;
        }

        var normalizedJob = job.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedJob))
        {
            return null;
        }

        var normalizedCandidates = candidates
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedCandidates.Count < 2)
        {
            return null;
        }

        var slotMap = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var hasRuntimeContext = slotMap.Count > 0 && recruitPositions.Count > 0;
        var eligiblePositions = hasRuntimeContext
            ? normalizedCandidates
                .Where(position =>
                {
                    if (!slotMap.TryGetValue(position, out var slot))
                    {
                        return false;
                    }

                    if (!IsJobAllowedForPosition(position, normalizedJob, recruitPositions))
                    {
                        return false;
                    }

                    return string.IsNullOrWhiteSpace(slot.RequiredJob) ||
                           string.Equals(slot.RequiredJob, normalizedJob, StringComparison.OrdinalIgnoreCase);
                })
                .ToList()
            : normalizedCandidates;

        if (eligiblePositions.Count < 2)
        {
            return null;
        }

        if (eligiblePositions.Count == 2 &&
            eligiblePositions.Contains("D1", StringComparer.OrdinalIgnoreCase) &&
            eligiblePositions.Contains("D2", StringComparer.OrdinalIgnoreCase))
        {
            return "D1,D2";
        }

        return string.Join(',', eligiblePositions);
    }

    [ComponentInteraction("recruit_participation_tools", ignoreGroupNames: true)]
    public async Task ShowParticipationToolsAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowParticipationToolsAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_participation_pos:*", ignoreGroupNames: true)]
    public async Task HandleParticipationPositionAsync(string value)
    {
        var (position, messageId) = ParsePositionValue(value);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await OpenJobSelectInternalAsync(position, messageId.Value);
    }

    [ComponentInteraction("recruit_participation_flex:*", ignoreGroupNames: true)]
    public async Task HandleParticipationFlexAsync(string value)
    {
        var (flexSpec, messageId) = ParseFlexiblePositionValue(value);
        if (string.IsNullOrWhiteSpace(flexSpec) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        if (string.Equals(flexSpec, "D1,D2", StringComparison.OrdinalIgnoreCase))
        {
            await OpenJobSelectInternalAsync("FLEX:D1,D2", messageId.Value);
            return;
        }

        await ShowParticipationFlexPositionSelectAsync(flexSpec, messageId.Value);
    }

    [ComponentInteraction("recruit_participation_flex_pos_select:*", ignoreGroupNames: true)]
    public async Task HandleParticipationFlexPositionSelectAsync(string messageIdRaw, string[] values)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 })
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectedPositions = values
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPositions.Length == 0)
        {
            await RespondAsync("ポジションを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await OpenJobSelectInternalAsync($"FLEX:{string.Join(',', selectedPositions)}", messageId);
    }

    [ComponentInteraction("recruit_participation_flex_back:*", ignoreGroupNames: true)]
    public async Task HandleParticipationFlexBackAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowParticipationToolsAsync(messageId);
    }

    [ComponentInteraction("recruit_participation_flex_cancel", ignoreGroupNames: true)]
    public async Task HandleParticipationFlexCancelAsync()
    {
        await RespondAsync("操作を中止しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_participation_comment:*", ignoreGroupNames: true)]
    public async Task HandleParticipationCommentAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowCommentEditModalForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_participation_cancel:*", ignoreGroupNames: true)]
    public async Task HandleParticipationCancelAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await CancelRecruitForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_pos_MT", ignoreGroupNames: true)]
    public Task OpenJobSelectMtAsync() => OpenJobSelectInternalAsync("MT");

    [ComponentInteraction("recruit_pos_ST", ignoreGroupNames: true)]
    public Task OpenJobSelectStAsync() => OpenJobSelectInternalAsync("ST");

    [ComponentInteraction("recruit_pos_H1", ignoreGroupNames: true)]
    public Task OpenJobSelectH1Async() => OpenJobSelectInternalAsync("H1");

    [ComponentInteraction("recruit_pos_H2", ignoreGroupNames: true)]
    public Task OpenJobSelectH2Async() => OpenJobSelectInternalAsync("H2");

    [ComponentInteraction("recruit_pos_D1", ignoreGroupNames: true)]
    public Task OpenJobSelectD1Async() => OpenJobSelectInternalAsync("D1");

    [ComponentInteraction("recruit_pos_D2", ignoreGroupNames: true)]
    public Task OpenJobSelectD2Async() => OpenJobSelectInternalAsync("D2");

    [ComponentInteraction("recruit_pos_D3", ignoreGroupNames: true)]
    public Task OpenJobSelectD3Async() => OpenJobSelectInternalAsync("D3");

    [ComponentInteraction("recruit_pos_D4", ignoreGroupNames: true)]
    public Task OpenJobSelectD4Async() => OpenJobSelectInternalAsync("D4");

    [ComponentInteraction("recruit_pos_select", ignoreGroupNames: true)]
    public async Task OpenJobSelectFromMenuAsync(string[] values)
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 } || string.IsNullOrWhiteSpace(values[0]))
        {
            await RespondAsync("ポジションを選択してください。", ephemeral: true);
            return;
        }

        var position = values[0].Trim().ToUpperInvariant();
        await OpenJobSelectInternalAsync(position, component.Message.Id);
    }

    [ComponentInteraction("recruit_job_MT", ignoreGroupNames: true)]
    public Task HandleJobSelectMtAsync(string[] values) => HandleJobSelectInternalAsync("MT", values);

    [ComponentInteraction("recruit_job_ST", ignoreGroupNames: true)]
    public Task HandleJobSelectStAsync(string[] values) => HandleJobSelectInternalAsync("ST", values);

    [ComponentInteraction("recruit_job_H1", ignoreGroupNames: true)]
    public Task HandleJobSelectH1Async(string[] values) => HandleJobSelectInternalAsync("H1", values);

    [ComponentInteraction("recruit_job_H2", ignoreGroupNames: true)]
    public Task HandleJobSelectH2Async(string[] values) => HandleJobSelectInternalAsync("H2", values);

    [ComponentInteraction("recruit_job_D1", ignoreGroupNames: true)]
    public Task HandleJobSelectD1Async(string[] values) => HandleJobSelectInternalAsync("D1", values);

    [ComponentInteraction("recruit_job_D2", ignoreGroupNames: true)]
    public Task HandleJobSelectD2Async(string[] values) => HandleJobSelectInternalAsync("D2", values);

    [ComponentInteraction("recruit_job_D3", ignoreGroupNames: true)]
    public Task HandleJobSelectD3Async(string[] values) => HandleJobSelectInternalAsync("D3", values);

    [ComponentInteraction("recruit_job_D4", ignoreGroupNames: true)]
    public Task HandleJobSelectD4Async(string[] values) => HandleJobSelectInternalAsync("D4", values);

    [ComponentInteraction("recruit_job_pick:*", ignoreGroupNames: true)]
    public async Task HandleJobPickButtonAsync(string value)
    {
        var (job, position, messageId) = ParseExternalFillJobValue(value);
        if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleJobSelectInternalAsync(position, new[] { $"{job}|{messageId.Value}" });
    }

    [ComponentInteraction("recruit_flex_job_select:*", ignoreGroupNames: true)]
    public Task HandleFlexJobSelectAsync(string flexSpec, string[] values)
        => HandleJobSelectInternalAsync($"FLEX:{flexSpec}", values);

    [ComponentInteraction("recruit_cancel", ignoreGroupNames: true)]
    public async Task CancelRecruitAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await CancelRecruitForMessageAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_comment_edit", ignoreGroupNames: true)]
    public async Task ShowCommentEditModalAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowCommentEditModalForMessageAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_manage_tools", ignoreGroupNames: true)]
    public async Task ShowManageToolsAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowManageToolsAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_manage_tools:*", ignoreGroupNames: true)]
    public async Task ShowManageToolsByMessageIdAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageToolsAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_fill:*", ignoreGroupNames: true)]
    public async Task HandleManageToolFillAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowExternalFillSelectForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_unfill:*", ignoreGroupNames: true)]
    public async Task HandleManageToolUnfillAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowExternalUnfillSelectForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_post_edit:*", ignoreGroupNames: true)]
    public async Task HandleManageToolPostEditAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowPostEditModalForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_reschedule:*", ignoreGroupNames: true)]
    public async Task HandleManageToolRescheduleAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowPostRescheduleForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_positions_edit:*", ignoreGroupNames: true)]
    public async Task HandleManageToolPositionsEditAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowSlotEditSelectForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_tool_close:*", ignoreGroupNames: true)]
    public async Task HandleManageToolCloseAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageToolActionConfirmAsync(messageId, RecruitPostStatus.Closed);
    }

    [ComponentInteraction("recruit_manage_tool_cancel:*", ignoreGroupNames: true)]
    public async Task HandleManageToolCancelAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageToolActionConfirmAsync(messageId, RecruitPostStatus.Canceled);
    }

    [ComponentInteraction("recruit_manage_tool_transfer:*", ignoreGroupNames: true)]
    public async Task HandleManageToolTransferAsync(string messageIdRaw)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageTransferOwnerSelectForMessageAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_transfer_user_select:*", ignoreGroupNames: true)]
    public async Task HandleManageTransferUserSelectAsync(string messageIdRaw, string[] values)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 } || !ulong.TryParse(values[0], out var newCreatorUserId))
        {
            await RespondAsync("委譲先を選択してください。", ephemeral: true);
            return;
        }

        await TransferRecruitOwnerForMessageAsync(messageId, newCreatorUserId);
    }

    [ComponentInteraction("recruit_manage_tool_close_confirm:*", ignoreGroupNames: true)]
    public Task HandleManageToolCloseConfirmAsync(string messageIdRaw)
        => HandleManageSelectionAsync(messageIdRaw, RecruitPostStatus.Closed, "募集を終了しました。");

    [ComponentInteraction("recruit_manage_tool_cancel_confirm:*", ignoreGroupNames: true)]
    public Task HandleManageToolCancelConfirmAsync(string messageIdRaw)
        => HandleManageSelectionAsync(messageIdRaw, RecruitPostStatus.Canceled, "募集を中断しました。");

    [ComponentInteraction("recruit_manage_tool_action_abort", ignoreGroupNames: true)]
    public Task HandleManageToolActionAbortAsync()
        => RespondAsync("操作を取り消しました。", ephemeral: true);

    [ComponentInteraction("recruit_positions_edit", ignoreGroupNames: true)]
    public async Task ShowSlotEditSelectAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowSlotEditSelectForMessageAsync(component.Message.Id);
    }

    private async Task ShowParticipationToolsAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync(BuildJoinBlockedMessage(post.Status), ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var positions = GetOrderedPositions(slots);
        if (positions.Count == 0)
        {
            await RespondAsync("参加可能なポジションがありません。", ephemeral: true);
            return;
        }
        var joinedPositions = slots
            .Where(slot => slot.UserId.HasValue && slot.UserId.Value == ToDbId(Context.User.Id))
            .Select(slot => slot.Position)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userId = ToDbId(Context.User.Id);
        var selectablePositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in positions)
        {
            var slot = slots.FirstOrDefault(item => string.Equals(item.Position, position, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                continue;
            }

            if (slot.IsClosed)
            {
                if (CanJoinByRelocatingClosedFlexAnchor(post, slots, slot, userId))
                {
                    selectablePositions.Add(position);
                }

                continue;
            }

            if (!slot.UserId.HasValue)
            {
                selectablePositions.Add(position);
                continue;
            }

            if (slot.UserId.Value == userId)
            {
                continue;
            }

            if (CanJoinBySwappingFlexibleHost(post, slots, slot, userId))
            {
                selectablePositions.Add(position);
            }
        }

        await RespondAsync(
            "参加操作を選択してください。",
            components: BuildParticipationToolsComponents(post, messageId, positions, joinedPositions, selectablePositions),
            ephemeral: true);
    }

    private async Task ShowParticipationFlexPositionSelectAsync(string flexSpec, ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync(BuildJoinBlockedMessage(post.Status), ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var candidatePositions = ResolveFlexCandidatePositions(flexSpec, slots);
        var userId = ToDbId(Context.User.Id);
        var selectablePositions = candidatePositions
            .Where(position =>
            {
                var slot = slots.FirstOrDefault(item => string.Equals(item.Position, position, StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    return false;
                }

                if (slot.IsClosed)
                {
                    return CanJoinByRelocatingClosedFlexAnchor(post, slots, slot, userId);
                }

                if (!slot.UserId.HasValue)
                {
                    return true;
                }

                if (slot.UserId.Value == userId)
                {
                    return false;
                }

                return CanJoinBySwappingFlexibleHost(post, slots, slot, userId);
            })
            .ToArray();

        if (selectablePositions.Length == 0)
        {
            await RespondAsync($"{BuildFlexSelectionLabel(flexSpec)} で選択できる空き枠がありません。", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_participation_flex_pos_select:{messageId}")
            .WithPlaceholder("参加したいポジションを複数選択")
            .WithMinValues(1)
            .WithMaxValues(Math.Min(selectablePositions.Length, 25));
        foreach (var position in selectablePositions)
        {
            menu.AddOption(position, position);
        }

        var components = new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("戻る", $"recruit_participation_flex_back:{messageId}", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_participation_flex_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
        await RespondAsync(
            $"{BuildFlexSelectionLabel(flexSpec)} の対象ポジションを選択してください。",
            components: components,
            ephemeral: true);
    }

    private async Task CancelRecruitForMessageAsync(ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        var removed = await _db.ClearRecruitSlotAsync(post.Id, Context.User.Id);
        if (!removed)
        {
            await RespondAsync("参加していません。", ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        if (slot is not null)
        {
            await DeleteRecruitParticipantEmbedAsync(post, slot);
        }
        await RemoveRecruitThreadMemberIfNoSlotAsync(post, Context.User.Id);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await RespondAsync("参加をキャンセルしました。", ephemeral: true);
    }

    private async Task ShowCommentEditModalForMessageAsync(ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        if (slot is null)
        {
            await RespondAsync("参加していません。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        _commentEditTracker.Set(Context.User.Id, new RecruitCommentEditContext(messageId, DateTimeOffset.UtcNow));
        await RespondWithModalAsync<RecruitCommentEditModal>("recruit_comment_edit_modal");
    }

    private async Task ShowManageToolsAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var canManage = CanManageRecruitPost(post);
        var isCreator = post.CreatorUserId == ToDbId(Context.User.Id);
        if (!canManage)
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        var canCancelOthers = IsRecruitCancelPrivilegedUser();
        var canTransferOwner = isCreator || canCancelOthers;
        var components = BuildManageToolsComponents(messageId, isCreator, canCancelOthers, canTransferOwner, post.Status);

        if (post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
        {
            await RespondAsync("管理操作を選択してください。", components: components, ephemeral: true);
        }
        else
        {
            await RespondAsync("この募集で利用できる管理操作はありません。", ephemeral: true);
        }
    }

    private async Task ShowManageToolActionConfirmAsync(ulong messageId, RecruitPostStatus targetStatus)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var isCreator = post.CreatorUserId == ToDbId(Context.User.Id);
        var canCancelOthers = IsRecruitCancelPrivilegedUser();
        if (targetStatus == RecruitPostStatus.Closed && !isCreator)
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (targetStatus == RecruitPostStatus.Canceled && !(isCreator || canCancelOthers))
        {
            await RespondAsync("主催者または管理ロールのみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var actionLabel = targetStatus == RecruitPostStatus.Canceled ? "中断" : "終了";
        await RespondAsync(
            $"この募集を{actionLabel}します。よろしいですか？",
            components: BuildManageToolActionConfirmComponents(messageId, targetStatus),
            ephemeral: true);
    }

    private async Task ShowSlotEditSelectForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var ordered = GetOrderedPositions(slots);
        if (ordered.Count == 0)
        {
            await RespondAsync("編集できる枠がありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"ジョブ指定を編集する枠を選択してください。\n{BuildSlotEditSummary(slots)}",
            components: BuildSlotEditPositionButtonsComponents(messageId, ordered, slots),
            ephemeral: true);
    }

    private async Task ShowPostEditModalForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        _postEditTracker.Set(Context.User.Id, new RecruitPostEditContext(
            Context.Guild.Id,
            Context.Channel.Id,
            messageId,
            post.Title,
            post.DataCenter,
            post.ScheduleText,
            post.ScheduleAt,
            post.Note,
            DateTimeOffset.UtcNow));
        await RespondWithModalAsync<RecruitPostEditModal>("recruit_post_edit_modal");
    }

    private async Task ShowPostRescheduleForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        var context = new RecruitPostEditContext(
            Context.Guild.Id,
            Context.Channel.Id,
            messageId,
            post.Title,
            post.DataCenter,
            post.ScheduleText,
            post.ScheduleAt,
            post.Note,
            DateTimeOffset.UtcNow);
        _postEditTracker.Set(Context.User.Id, context);

        if (string.IsNullOrWhiteSpace(context.DataCenter))
        {
            await RespondAsync(
                "開催DCを選択してください。",
                components: BuildPostEditDataCenterSelectComponents(context.DataCenter),
                ephemeral: true);
            return;
        }

        await ShowPostEditScheduleDatePageAsync(0, $"開始日の指定を選択してください。（現在: {context.Schedule ?? "未設定"}）");
    }

    private async Task ShowExternalFillSelectForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var slotMap = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var emptyPositions = GetOrderedPositions(slots)
            .Where(position =>
                slotMap.TryGetValue(position, out var slot) &&
                CanSelectPositionForExternalFill(post, slots, slot))
            .ToList();
        var flexSelectablePositions = GetOrderedPositions(slots)
            .Where(position =>
                slotMap.TryGetValue(position, out var slot) &&
                CanSelectPositionForExternalFillAsFlexAnchor(slot))
            .ToList();
        var hasFlexCandidate = flexSelectablePositions.Count >= 2;

        if (emptyPositions.Count == 0 && !hasFlexCandidate)
        {
            await RespondAsync("ゲーム内募集で埋まりとして〆にできる空きポジションがありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "ゲーム内募集で埋まったポジションを選択してください。",
            components: BuildExternalFillSelectComponents(messageId, emptyPositions, hasFlexCandidate),
            ephemeral: true);
    }

    private async Task ShowManageTransferOwnerSelectForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var isCreator = post.CreatorUserId == ToDbId(Context.User.Id);
        var canCancelOthers = IsRecruitCancelPrivilegedUser();
        if (!(isCreator || canCancelOthers))
        {
            await RespondAsync("主催者または管理ロールのみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var candidateIds = slots
            .Where(slot => slot.UserId.HasValue && slot.UserId.Value != post.CreatorUserId)
            .Select(slot => ToUlong(slot.UserId!.Value))
            .Distinct()
            .ToArray();
        if (candidateIds.Length == 0)
        {
            await RespondAsync("委譲できる参加メンバーがいません。", ephemeral: true);
            return;
        }

        var candidates = new List<(ulong UserId, string Label, string? Description)>();
        foreach (var candidateId in candidateIds)
        {
            var guildUser = Context.Guild.GetUser(candidateId);
            if (guildUser is null || guildUser.IsBot)
            {
                continue;
            }

            var label = BuildUserShortLabel(guildUser.DisplayName ?? guildUser.GlobalName ?? guildUser.Username, guildUser.Username);
            var slot = slots.FirstOrDefault(item => item.UserId.HasValue && ToUlong(item.UserId.Value) == candidateId);
            var description = slot is null
                ? null
                : $"{slot.Position} / {(string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job)}";
            candidates.Add((candidateId, label, description));
        }

        if (candidates.Count == 0)
        {
            await RespondAsync("委譲できる参加メンバーがいません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "委譲先の主催を選択してください。",
            components: BuildManageTransferOwnerSelectComponents(messageId, candidates),
            ephemeral: true);
    }

    private async Task TransferRecruitOwnerForMessageAsync(ulong messageId, ulong newCreatorUserId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var isCreator = post.CreatorUserId == ToDbId(Context.User.Id);
        var canCancelOthers = IsRecruitCancelPrivilegedUser();
        if (!(isCreator || canCancelOthers))
        {
            await RespondAsync("主催者または管理ロールのみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var currentCreatorUserId = ToUlong(post.CreatorUserId);
        if (newCreatorUserId == currentCreatorUserId)
        {
            await RespondAsync("そのユーザーは既に主催者です。", ephemeral: true);
            return;
        }

        var newCreator = Context.Guild.GetUser(newCreatorUserId);
        if (newCreator is null || newCreator.IsBot)
        {
            await RespondAsync("委譲先ユーザーが見つかりません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var isParticipant = slots.Any(slot => slot.UserId.HasValue && ToUlong(slot.UserId.Value) == newCreatorUserId);
        if (!isParticipant)
        {
            await RespondAsync("委譲先は参加中メンバーから選択してください。", ephemeral: true);
            return;
        }

        string? nextHostFlexSpec = null;
        string? nextHostFlexJobsSpec = null;
        var newCreatorSlot = slots.FirstOrDefault(slot => slot.UserId.HasValue && ToUlong(slot.UserId.Value) == newCreatorUserId);
        if (newCreatorSlot is not null && !string.IsNullOrWhiteSpace(newCreatorSlot.FlexSpec))
        {
            var flexCandidates = ResolveFlexCandidatePositions(newCreatorSlot.FlexSpec, slots);
            if (flexCandidates.Count >= 2 &&
                flexCandidates.Contains(newCreatorSlot.Position, StringComparer.OrdinalIgnoreCase))
            {
                nextHostFlexSpec = string.Join(',', flexCandidates);
                var hostCandidateJobs = ParseFlexJobCodes(newCreatorSlot.FlexJobsSpec);
                if (hostCandidateJobs.Count == 0 && !string.IsNullOrWhiteSpace(newCreatorSlot.Job))
                {
                    hostCandidateJobs = new[] { newCreatorSlot.Job.Trim().ToUpperInvariant() };
                }

                if (hostCandidateJobs.Count > 0)
                {
                    nextHostFlexJobsSpec = string.Join(',', hostCandidateJobs);
                }
            }
        }

        await DeferAsync(ephemeral: true);

        var updated = await _db.UpdateRecruitPostCreatorAsync(post.Id, newCreatorUserId, nextHostFlexSpec, nextHostFlexJobsSpec);
        if (!updated)
        {
            await FollowupAsync("主催の委譲に失敗しました。", ephemeral: true);
            return;
        }

        post.CreatorUserId = ToDbId(newCreatorUserId);
        post.HostFlexSpec = nextHostFlexSpec;
        post.HostFlexJobsSpec = nextHostFlexJobsSpec;
        await UpdateRecruitMessageAsync(post);
        await EnsureRecruitThreadMemberAsync(post, newCreatorUserId);

        await PostRecruitStatusMessageAsync(
            post,
            $"主催を {newCreator.Mention} に委譲しました。",
            mentionUserIds: new[] { currentCreatorUserId, newCreatorUserId });
        await SendRecruitAuditAsync(
            post,
            "募集管理: 主催委譲",
            $"主催を {MentionUser(ToDbId(newCreatorUserId))} に委譲（旧主催: {MentionUser(ToDbId(currentCreatorUserId))}）");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);

        await FollowupAsync($"主催を {newCreator.Mention} に委譲しました。", ephemeral: true);
    }

    private async Task ShowExternalUnfillSelectForMessageAsync(ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var closedSet = slots
            .Where(slot => slot.IsClosed && !slot.UserId.HasValue)
            .Select(slot => slot.Position)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var closedPositions = GetOrderedPositions(slots)
            .Where(position => closedSet.Contains(position))
            .ToList();

        if (closedPositions.Count == 0)
        {
            await RespondAsync("〆解除できるポジションがありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "〆解除するポジションを選択してください。",
            components: BuildExternalUnfillSelectComponents(messageId, closedPositions),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_slot_edit_pos:*", ignoreGroupNames: true)]
    public async Task HandleSlotEditPositionButtonAsync(string value)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (position, messageId) = ParsePositionValue(value);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("対象枠が見つかりませんでした。", ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{position} の指定ジョブを選択してください（指定解除も可）。",
            components: BuildSlotEditJobButtonsComponents(position, messageId.Value, jobs, target.RequiredJob),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_slot_edit_job:*", ignoreGroupNames: true)]
    public async Task HandleSlotEditJobButtonAsync(string value)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (job, position, messageId) = ParseExternalFillJobValue(value);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue || string.IsNullOrWhiteSpace(job))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        string? requiredJob = string.Equals(job, "NONE", StringComparison.OrdinalIgnoreCase) ? null : job.Trim();

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
        if (post is null)
        {
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await FollowupAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await FollowupAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        if (requiredJob is not null)
        {
            var slots = await _db.ListRecruitSlotsAsync(post.Id);
            var recruitPositions = slots
                .Select(slot => slot.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs) ||
                !jobs.Any(item => string.Equals(item.Code, requiredJob, StringComparison.OrdinalIgnoreCase)))
            {
                await FollowupAsync("そのジョブは対象ポジションに指定できません。", ephemeral: true);
                return;
            }
        }

        var status = await _db.UpdateRecruitSlotRequiredJobAsync(post.Id, position, requiredJob);
        if (status != RecruitRequiredJobUpdateStatus.Success)
        {
            var error = status switch
            {
                RecruitRequiredJobUpdateStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitRequiredJobUpdateStatus.PostClosed => "終了または中断済みの募集は変更できません。",
                RecruitRequiredJobUpdateStatus.PositionNotFound => "対象枠が見つかりませんでした。",
                RecruitRequiredJobUpdateStatus.PositionOccupiedByOtherJob => "参加中のジョブと異なる指定には変更できません。",
                _ => "指定ジョブの更新に失敗しました。"
            };
            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);

        if (requiredJob is null)
        {
            await FollowupAsync($"{position} の指定ジョブを解除しました。", ephemeral: true);
            return;
        }

        await FollowupAsync($"{position} の指定ジョブを {requiredJob} に更新しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_slot_edit_back:*", ignoreGroupNames: true)]
    public async Task HandleSlotEditBackAsync(string value)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(value, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var ordered = GetOrderedPositions(slots);
        if (ordered.Count == 0)
        {
            await RespondAsync("編集できる枠がありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"ジョブ指定を編集する枠を選択してください。\n{BuildSlotEditSummary(slots)}",
            components: BuildSlotEditPositionButtonsComponents(messageId, ordered, slots),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_post_edit", ignoreGroupNames: true)]
    public async Task ShowPostEditModalAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowPostEditModalForMessageAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_post_edit_dc_meteor", ignoreGroupNames: true)]
    public Task SelectPostEditDcMeteorAsync() => SelectPostEditDataCenterAsync("Meteor");

    [ComponentInteraction("recruit_post_edit_dc_gaia", ignoreGroupNames: true)]
    public Task SelectPostEditDcGaiaAsync() => SelectPostEditDataCenterAsync("Gaia");

    [ComponentInteraction("recruit_post_edit_dc_mana", ignoreGroupNames: true)]
    public Task SelectPostEditDcManaAsync() => SelectPostEditDataCenterAsync("Mana");

    [ComponentInteraction("recruit_post_edit_dc_elemental", ignoreGroupNames: true)]
    public Task SelectPostEditDcElementalAsync() => SelectPostEditDataCenterAsync("Elemental");

    [ComponentInteraction("recruit_post_edit_dc_keep", ignoreGroupNames: true)]
    public Task SelectPostEditDcKeepAsync() => ShowPostEditScheduleDatePageAsync(0, "開催DCは現在のままで開始日の指定を選択してください。");

    [ComponentInteraction("recruit_post_edit_schedule_now", ignoreGroupNames: true)]
    public Task SelectPostEditScheduleNowAsync() => SelectPostEditScheduleNowInternalAsync();

    [ComponentInteraction("recruit_post_edit_schedule_quick:*", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleQuickAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            !int.TryParse(parts[2], out var minute) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleMinuteInternalAsync(dayOffset, hour, minute);
    }

    [ComponentInteraction("recruit_post_edit_schedule_keep", ignoreGroupNames: true)]
    public Task SelectPostEditScheduleKeepAsync() => SelectPostEditScheduleKeepInternalAsync();

    [ComponentInteraction("recruit_post_edit_schedule_page:*", ignoreGroupNames: true)]
    public async Task SelectPostEditSchedulePageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        var maxPage = CreateScheduleMaxDayOffset / CreateSchedulePageSize;
        if (pageIndex < 0 || pageIndex > maxPage)
        {
            await RespondAsync("日付ページの情報が不正です。", ephemeral: true);
            return;
        }

        await ShowPostEditScheduleDatePageAsync(pageIndex);
    }

    [ComponentInteraction("recruit_post_edit_schedule_day_pick:*", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleDayPickAsync(string dayOffsetRaw)
    {
        if (!int.TryParse(dayOffsetRaw, out var dayOffset) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset)
        {
            await RespondAsync("日付の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleDayInternalAsync(dayOffset);
    }

    [ComponentInteraction("recruit_post_edit_schedule_hour:*", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleHourAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23)
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleHourInternalAsync(dayOffset, hour);
    }

    [ComponentInteraction("recruit_post_edit_schedule_hour_select", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleHourSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleHourAsync(values[0]);
    }

    [ComponentInteraction("recruit_post_edit_schedule_minute:*", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleMinuteAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var dayOffset) ||
            !int.TryParse(parts[1], out var hour) ||
            !int.TryParse(parts[2], out var minute) ||
            dayOffset is < 0 or > CreateScheduleMaxDayOffset ||
            hour is < 0 or > 23 ||
            (minute != 0 && minute != 10 && minute != 20 && minute != 30 && minute != 40 && minute != 50))
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleMinuteInternalAsync(dayOffset, hour, minute);
    }

    [ComponentInteraction("recruit_post_edit_schedule_minute_select", ignoreGroupNames: true)]
    public async Task SelectPostEditScheduleMinuteSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("時間の選択情報が不正です。", ephemeral: true);
            return;
        }

        await SelectPostEditScheduleMinuteAsync(values[0]);
    }

    [ComponentInteraction("recruit_post_edit_cancel", ignoreGroupNames: true)]
    public async Task CancelPostEditAsync()
    {
        PruneInteractionContexts();
        if (!_postEditTracker.TryRemove(Context.User.Id, out _))
        {
            await RespondAsync("編集中の募集はありません。", ephemeral: true);
            return;
        }

        await RespondAsync("募集内容編集を中止しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_external_fill", ignoreGroupNames: true)]
    public async Task ShowExternalFillSelectAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowExternalFillSelectForMessageAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_external_fill_select", ignoreGroupNames: true)]
    public async Task HandleExternalFillSelectAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 })
        {
            await RespondAsync("ポジションを選択してください。", ephemeral: true);
            return;
        }

        var (position, messageId) = ParsePositionValue(values[0]);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalFillPositionAsync(position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_pos:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillPositionButtonAsync(string value)
    {
        var (position, messageId) = ParsePositionValue(value);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalFillPositionAsync(position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_flex:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillFlexButtonAsync(string value)
    {
        var (_, messageId) = ParseFlexiblePositionValue(value);
        if (!messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowExternalFillFlexPositionSelectAsync(messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_flex_pos_select:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillFlexPositionSelectAsync(string messageIdRaw, string[] values)
    {
        if (!ulong.TryParse(messageIdRaw, out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 1 })
        {
            await RespondAsync("FLEX候補ポジションを2つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectedPositions = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPositions.Length < 2)
        {
            await RespondAsync("FLEX候補ポジションを2つ以上選択してください。", ephemeral: true);
            return;
        }

        await HandleExternalFillPositionAsync($"FLEX:{string.Join(',', selectedPositions)}", messageId);
    }

    [ComponentInteraction("recruit_external_fill_flex_job_select:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillFlexJobSelectAsync(string value, string[] values)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !ulong.TryParse(parts[1], out var messageId))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        var normalizedFlexSpec = parts[0].Trim();
        if (string.IsNullOrWhiteSpace(normalizedFlexSpec))
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 })
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectedJobs = values
            .Select(item => item.Trim().ToUpperInvariant())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedJobs.Length == 0)
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        await HandleExternalFillJobAsync(string.Join(',', selectedJobs), $"FLEX:{normalizedFlexSpec}", messageId);
    }

    [ComponentInteraction("recruit_external_fill_cancel", ignoreGroupNames: true)]
    public async Task CancelExternalFillAsync()
    {
        await RespondAsync("操作を中止しました。", ephemeral: true);
    }

    private async Task ShowExternalFillFlexPositionSelectAsync(ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var slotMap = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selectablePositions = GetOrderedPositions(slots)
            .Where(position =>
                slotMap.TryGetValue(position, out var slot) &&
                CanSelectPositionForExternalFillAsFlexAnchor(slot))
            .ToArray();
        if (selectablePositions.Length < 2)
        {
            await RespondAsync("FLEXで〆にできる空き枠が2つ以上ありません。", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_external_fill_flex_pos_select:{messageId}")
            .WithPlaceholder("FLEX候補ポジションを選択（複数可）")
            .WithMinValues(2)
            .WithMaxValues(Math.Min(selectablePositions.Length, 25));
        foreach (var position in selectablePositions)
        {
            menu.AddOption(position, position);
        }

        await RespondAsync(
            "FLEXで埋まった候補ポジションを選択してください。",
            components: new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("戻る", $"recruit_manage_tool_fill:{messageId}", ButtonStyle.Secondary, row: 1)
                .WithButton("中止", "recruit_external_fill_cancel", ButtonStyle.Secondary, row: 1)
                .Build(),
            ephemeral: true);
    }

    private async Task HandleExternalFillPositionAsync(string position, ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var normalizedPosition = position.Trim().ToUpperInvariant();
        if (TryParseFlexPositionToken(normalizedPosition, out var flexSpec))
        {
            var candidatePositions = ResolveFlexCandidatePositions(flexSpec, slots);
            if (candidatePositions.Count < 2)
            {
                await RespondAsync("FLEX候補ポジションを2つ以上選択してください。", ephemeral: true);
                return;
            }

            var slotMap = slots
                .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var flexRecruitPositions = slots
                .Select(slot => slot.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var availableByCode = new Dictionary<string, JobOption>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidatePosition in candidatePositions)
            {
                if (!slotMap.TryGetValue(candidatePosition, out var slot) ||
                    !CanSelectPositionForExternalFillAsFlexAnchor(slot) ||
                    !RecruitmentRules.TryGetJobsForRecruit(candidatePosition, flexRecruitPositions, out var positionJobs))
                {
                    continue;
                }

                IReadOnlyList<JobOption> candidateJobs = positionJobs;
                if (!string.IsNullOrWhiteSpace(slot.RequiredJob))
                {
                    candidateJobs = positionJobs
                        .Where(job => string.Equals(job.Code, slot.RequiredJob, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }

                foreach (var candidateJob in candidateJobs)
                {
                    availableByCode.TryAdd(candidateJob.Code, candidateJob);
                }
            }

            var flexAvailableJobs = SortJobsForFlexSelector(availableByCode.Values).ToArray();
            if (flexAvailableJobs.Length == 0)
            {
                await RespondAsync("選択したFLEXで〆にできるジョブがありません。", ephemeral: true);
                return;
            }

            var normalizedFlexSpec = string.Join(',', candidatePositions);
            await RespondAsync(
                $"{BuildFlexSelectionLabel(normalizedFlexSpec)} で埋まったジョブを選択してください。",
                components: BuildExternalFillFlexJobSelectComponents(normalizedFlexSpec, messageId, flexAvailableJobs),
                ephemeral: true);
            return;
        }

        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, normalizedPosition, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("対象ポジションが見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanSelectPositionForExternalFill(post, slots, target))
        {
            var error = target.UserId.HasValue
                ? "そのポジションは既に埋まっています。"
                : "そのポジションは既に〆です。";
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(target.Position, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        IReadOnlyList<JobOption> availableJobs = jobs;
        if (!string.IsNullOrWhiteSpace(target.RequiredJob))
        {
            availableJobs = jobs
                .Where(job => string.Equals(job.Code, target.RequiredJob, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (availableJobs.Count == 0)
            {
                await RespondAsync($"{target.Position} の指定ジョブ設定が不正です。", ephemeral: true);
                return;
            }
        }

        await RespondAsync(
            $"{target.Position} で埋まったジョブを選択してください。",
            components: BuildExternalFillJobButtonsComponents(target.Position, messageId, availableJobs),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_external_fill_job_select", ignoreGroupNames: true)]
    public async Task HandleExternalFillJobSelectAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 })
        {
            await RespondAsync("ジョブを選択してください。", ephemeral: true);
            return;
        }

        var (job, position, messageId) = ParseExternalFillJobValue(values[0]);
        if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalFillJobAsync(job, position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_job:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillJobButtonAsync(string value)
    {
        var (job, position, messageId) = ParseExternalFillJobValue(value);
        if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalFillJobAsync(job, position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_member_yes:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillMemberYesAsync(string value)
    {
        var (job, position, messageId) = ParseExternalFillJobValue(value);
        if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ShowExternalFillMemberSelectPageAsync(job, position, messageId.Value, pageIndex: 0);
    }

    [ComponentInteraction("recruit_external_fill_member_no:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillMemberNoAsync(string value)
    {
        var (job, position, messageId) = ParseExternalFillJobValue(value);
        if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ApplyExternalFillCloseAsync(job, position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_fill_member_page:*", ignoreGroupNames: true)]
    public async Task HandleExternalFillMemberPageAsync(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[3], out var pageIndex) ||
            !ulong.TryParse(parts[2], out var messageId))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var job = parts[0].Trim();
        var position = parts[1].Trim().ToUpperInvariant();
        await ShowExternalFillMemberSelectPageAsync(job, position, messageId, pageIndex);
    }

    [ComponentInteraction("recruit_external_fill_member_user", ignoreGroupNames: true)]
    public async Task HandleExternalFillMemberUserSelectAsync(string[] values)
    {
        if (values is not { Length: > 0 } || string.IsNullOrWhiteSpace(values[0]))
        {
            await RespondAsync("メンバーを選択してください。", ephemeral: true);
            return;
        }

        var (userId, job, position, messageId) = ParseExternalFillMemberValue(values[0]);
        if (!userId.HasValue || string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await ApplyExternalFillMemberAssignAsync(userId.Value, job, position, messageId.Value);
    }

    private async Task HandleExternalFillJobAsync(string job, string position, ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var normalizedPosition = position.Trim().ToUpperInvariant();
        var normalizedJob = job.Trim().ToUpperInvariant();
        if (TryParseFlexPositionToken(normalizedPosition, out var flexSpec))
        {
            if (!TryResolveExternalFillFlexAnchor(
                    post,
                    slots,
                    flexSpec,
                    normalizedJob,
                    out var normalizedFlexSpec,
                    out var normalizedFlexJobsSpec,
                    out _,
                    out _,
                    out var error))
            {
                await RespondAsync(error, ephemeral: true);
                return;
            }

            var selectedJobs = ParseFlexJobCodes(normalizedFlexJobsSpec);
            var selectedJobLabel = selectedJobs.Count == 0
                ? normalizedJob
                : string.Join('/', selectedJobs);

            await RespondAsync(
                $"{BuildFlexSelectionLabel(normalizedFlexSpec)} / {selectedJobLabel} で埋まり設定します。このサーバーの人ですか？",
                components: BuildExternalFillSourceSelectComponents(
                    normalizedFlexJobsSpec,
                    $"FLEX:{normalizedFlexSpec}",
                    messageId,
                    allowMemberSelection: true),
                ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(normalizedPosition, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        var matchedJob = jobs.FirstOrDefault(item => string.Equals(item.Code, normalizedJob, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{normalizedPosition}（{GetRoleLabelByPosition(normalizedPosition)}）/ {matchedJob.Code} で埋まり設定します。このサーバーの人ですか？",
            components: BuildExternalFillSourceSelectComponents(matchedJob.Code, normalizedPosition, messageId),
            ephemeral: true);
    }

    private async Task ShowExternalFillMemberSelectPageAsync(string job, string position, ulong messageId, int pageIndex)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var normalizedPosition = position.Trim().ToUpperInvariant();
        var normalizedJob = job.Trim().ToUpperInvariant();
        if (TryParseFlexPositionToken(normalizedPosition, out var flexSpec))
        {
            if (!TryResolveExternalFillFlexAnchor(
                    post,
                    slots,
                    flexSpec,
                    normalizedJob,
                    out var normalizedFlexSpec,
                    out var normalizedFlexJobsSpec,
                    out _,
                    out _,
                    out var error))
            {
                await RespondAsync(error, ephemeral: true);
                return;
            }

            var selectedJobs = ParseFlexJobCodes(normalizedFlexJobsSpec);
            if (selectedJobs.Count == 0)
            {
                await RespondAsync("選択したジョブに現在選べないものが含まれています。もう一度選択してください。", ephemeral: true);
                return;
            }
            var selectedJobLabel = string.Join('/', selectedJobs);

            var flexCandidateUsers = await LoadGuildUsersForSelectionAsync(Context.Guild);
            var flexToken = $"FLEX:{normalizedFlexSpec}";
            var flexComponent = BuildExternalFillMemberSelectComponents(flexCandidateUsers, normalizedFlexJobsSpec, flexToken, messageId, pageIndex, out var flexPageText);
            if (flexComponent is null)
            {
                await RespondAsync("選択できるメンバーが見つかりません。", ephemeral: true);
                return;
            }

            await RespondAsync(
                $"{BuildFlexSelectionLabel(normalizedFlexSpec)} / {selectedJobLabel} で参加メンバーを選択してください。({flexPageText})",
                components: flexComponent,
                ephemeral: true);
            return;
        }

        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, normalizedPosition, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("対象ポジションが見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanSelectPositionForExternalFill(post, slots, target))
        {
            var error = target.UserId.HasValue
                ? "そのポジションは既に埋まっています。"
                : "そのポジションは既に〆です。";
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(normalizedPosition, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        var matchedJob = jobs.FirstOrDefault(item => string.Equals(item.Code, normalizedJob, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。", ephemeral: true);
            return;
        }

        var candidateUsers = await LoadGuildUsersForSelectionAsync(Context.Guild);
        var component = BuildExternalFillMemberSelectComponents(candidateUsers, normalizedJob, normalizedPosition, messageId, pageIndex, out var pageText);
        if (component is null)
        {
            await RespondAsync("選択できるメンバーが見つかりません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{normalizedPosition}（{GetRoleLabelByPosition(normalizedPosition)}）/ {normalizedJob} で参加メンバーを選択してください。({pageText})",
            components: component,
            ephemeral: true);
    }

    private async Task ApplyExternalFillCloseAsync(string job, string position, ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var normalizedPosition = position.Trim().ToUpperInvariant();
        var normalizedJob = job.Trim().ToUpperInvariant();
        if (TryParseFlexPositionToken(normalizedPosition, out var flexSpec))
        {
            if (!TryResolveExternalFillFlexAnchor(
                    post,
                    slots,
                    flexSpec,
                    normalizedJob,
                    out var normalizedFlexSpec,
                    out var normalizedFlexJobsSpec,
                    out var anchorPosition,
                    out var anchorJob,
                    out var error))
            {
                await RespondAsync(error, ephemeral: true);
                return;
            }

            var selectedJobs = ParseFlexJobCodes(normalizedFlexJobsSpec);
            var selectedJobLabel = selectedJobs.Count == 0 ? anchorJob : string.Join('/', selectedJobs);

            await DeferAsync(ephemeral: true);
            var marked = await _db.MarkRecruitSlotClosedFlexAsync(
                post.Id,
                anchorPosition,
                anchorJob,
                normalizedFlexSpec,
                normalizedFlexJobsSpec);
            if (!marked)
            {
                await FollowupAsync("〆の更新に失敗しました。", ephemeral: true);
                return;
            }

            await UpdateRecruitMessageAsync(post);
            await PostRecruitStatusMessageAsync(post, $"ゲーム内募集で埋まったため {BuildFlexSelectionLabel(normalizedFlexSpec)} を {selectedJobLabel} で〆にしました。");
            await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
            await FollowupAsync($"{BuildFlexSelectionLabel(normalizedFlexSpec)} を {selectedJobLabel} で〆にしました。", ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(normalizedPosition, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        var matchedJob = jobs.FirstOrDefault(item => string.Equals(item.Code, normalizedJob, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var status = await _db.MarkRecruitSlotIngameFilledAsync(post.Id, normalizedPosition, matchedJob.Code, post.DisallowDuplicateJobs);
        if (status != RecruitIngameFillStatus.Success)
        {
            var error = status switch
            {
                RecruitIngameFillStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitIngameFillStatus.PostClosed => "終了または中断済みの募集は変更できません。",
                RecruitIngameFillStatus.PositionNotFound => "対象ポジションが見つかりませんでした。",
                RecruitIngameFillStatus.PositionOccupied => "そのポジションは既に埋まっています。",
                RecruitIngameFillStatus.PositionAlreadyClosed => "そのポジションは既に〆です。",
                RecruitIngameFillStatus.JobTaken => "そのジョブは既に埋まっています。",
                RecruitIngameFillStatus.JobRestricted => $"{normalizedPosition} は指定ジョブのみ〆にできます。",
                _ => "〆の更新に失敗しました。"
            };

            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, $"ゲーム内募集で埋まったため {normalizedPosition} を {matchedJob.Code} で〆にしました。");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync($"{normalizedPosition} を {matchedJob.Code} で〆にしました。", ephemeral: true);
    }

    private async Task ApplyExternalFillMemberAssignAsync(ulong userId, string job, string position, ulong messageId)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var selectedUser = Context.Guild.GetUser(userId);
        if (selectedUser is null)
        {
            var users = await LoadGuildUsersForSelectionAsync(Context.Guild);
            selectedUser = users.FirstOrDefault(user => user.Id == userId);
        }
        if (selectedUser is null || selectedUser.IsBot)
        {
            await RespondAsync("選択したメンバーを取得できませんでした。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var normalizedPosition = position.Trim().ToUpperInvariant();
        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedJob = job.Trim().ToUpperInvariant();
        if (TryParseFlexPositionToken(normalizedPosition, out var flexSpec))
        {
            if (!TryResolveExternalFillFlexAnchor(
                    post,
                    slots,
                    flexSpec,
                    normalizedJob,
                    out var normalizedFlexSpec,
                    out var normalizedFlexJobsSpec,
                    out var anchorPosition,
                    out var anchorJob,
                    out var error))
            {
                await RespondAsync(error, ephemeral: true);
                return;
            }

            var selectedJobs = ParseFlexJobCodes(normalizedFlexJobsSpec);
            if (selectedJobs.Count == 0)
            {
                await RespondAsync("選択したジョブに現在選べないものが含まれています。もう一度選択してください。", ephemeral: true);
                return;
            }
            var selectedJobLabel = string.Join('/', selectedJobs);

            await DeferAsync(ephemeral: true);
            var flexDisplayName = selectedUser.DisplayName ?? selectedUser.GlobalName ?? selectedUser.Username;
            var flexStatus = await _db.AssignRecruitSlotAsync(
                post.Id,
                anchorPosition,
                userId,
                flexDisplayName,
                anchorJob,
                string.Empty,
                post.DisallowDuplicateJobs,
                normalizedFlexSpec,
                normalizedFlexJobsSpec);
            if (flexStatus != RecruitAssignStatus.Success)
            {
                var flexLabel = BuildFlexSelectionLabel(normalizedFlexSpec);
                var assignError = flexStatus switch
                {
                    RecruitAssignStatus.PositionTaken => $"{flexLabel} の候補は既に埋まっています。",
                    RecruitAssignStatus.PositionClosed => $"{flexLabel} の候補は〆です（ゲーム内募集で埋まり）。",
                    RecruitAssignStatus.JobTaken => "そのジョブは既に埋まっています。",
                    RecruitAssignStatus.JobRestricted => $"{flexLabel} は指定ジョブのみ参加できます。",
                    RecruitAssignStatus.PositionNotFound => "ポジションが見つかりません。",
                    RecruitAssignStatus.PostNotFound => "募集が見つかりませんでした。",
                    RecruitAssignStatus.PostClosed => "募集は終了しました。",
                    _ => "募集の更新に失敗しました。"
                };

                await FollowupAsync(assignError, ephemeral: true);
                return;
            }

            await UpdateRecruitMessageAsync(post);
            await EnsureRecruitThreadMemberAsync(post, userId);
            await PostRecruitStatusMessageAsync(post, $"管理メニューで {BuildFlexSelectionLabel(normalizedFlexSpec)} に {MentionUser(ToDbId(userId))} ({selectedJobLabel}) を設定しました。");
            await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
            await FollowupAsync($"{BuildFlexSelectionLabel(normalizedFlexSpec)} を {selectedUser.Mention} ({selectedJobLabel}) で設定しました。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobsForRecruit(normalizedPosition, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        var matchedJob = jobs.FirstOrDefault(item => string.Equals(item.Code, normalizedJob, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var displayName = selectedUser.DisplayName ?? selectedUser.GlobalName ?? selectedUser.Username;
        var status = await _db.AssignRecruitSlotAsync(post.Id, normalizedPosition, userId, displayName, matchedJob.Code, string.Empty, post.DisallowDuplicateJobs);
        if (status != RecruitAssignStatus.Success)
        {
            var error = status switch
            {
                RecruitAssignStatus.PositionTaken => $"{normalizedPosition} は既に埋まっています。",
                RecruitAssignStatus.PositionClosed => $"{normalizedPosition} は〆です（ゲーム内募集で埋まり）。",
                RecruitAssignStatus.JobTaken => "そのジョブは既に埋まっています。",
                RecruitAssignStatus.JobRestricted => $"{normalizedPosition} は指定ジョブのみ参加できます。",
                RecruitAssignStatus.PositionNotFound => "ポジションが見つかりません。",
                RecruitAssignStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitAssignStatus.PostClosed => "募集は終了しました。",
                _ => "募集の更新に失敗しました。"
            };

            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        await EnsureRecruitThreadMemberAsync(post, userId);
        await PostRecruitStatusMessageAsync(post, $"管理メニューで {normalizedPosition} に {MentionUser(ToDbId(userId))} ({matchedJob.Code}) を設定しました。");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync($"{normalizedPosition} を {selectedUser.Mention} ({matchedJob.Code}) で設定しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_external_unfill", ignoreGroupNames: true)]
    public async Task ShowExternalUnfillSelectAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await ShowExternalUnfillSelectForMessageAsync(component.Message.Id);
    }

    [ComponentInteraction("recruit_external_unfill_select", ignoreGroupNames: true)]
    public async Task HandleExternalUnfillSelectAsync(string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 })
        {
            await RespondAsync("ポジションを選択してください。", ephemeral: true);
            return;
        }

        var (position, messageId) = ParsePositionValue(values[0]);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalUnfillPositionAsync(position, messageId.Value);
    }

    [ComponentInteraction("recruit_external_unfill_pos:*", ignoreGroupNames: true)]
    public async Task HandleExternalUnfillPositionButtonAsync(string value)
    {
        var (position, messageId) = ParsePositionValue(value);
        if (string.IsNullOrWhiteSpace(position) || !messageId.HasValue)
        {
            await RespondAsync("選択情報が不正です。", ephemeral: true);
            return;
        }

        await HandleExternalUnfillPositionAsync(position, messageId.Value);
    }

    private async Task HandleExternalUnfillPositionAsync(string position, ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!CanManageRecruitPost(post))
        {
            await RespondAsync("主催者または管理者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var status = await _db.UnmarkRecruitSlotIngameFilledAsync(post.Id, position);
        if (status != RecruitIngameUnfillStatus.Success)
        {
            var error = status switch
            {
                RecruitIngameUnfillStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitIngameUnfillStatus.PostClosed => "終了または中断済みの募集は変更できません。",
                RecruitIngameUnfillStatus.PositionNotFound => "対象ポジションが見つかりませんでした。",
                RecruitIngameUnfillStatus.PositionOccupied => "そのポジションは既に参加者がいます。",
                RecruitIngameUnfillStatus.PositionNotClosed => "そのポジションは〆ではありません。",
                _ => "〆解除に失敗しました。"
            };

            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, $"{position} の〆を解除しました。");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync($"{position} の〆を解除しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_manage_close", ignoreGroupNames: true)]
    public Task ShowManageCloseModalAsync()
        => ShowManageSelectionAsync(RecruitPostStatus.Closed);

    [ComponentInteraction("recruit_manage_panel", ignoreGroupNames: true)]
    public Task ShowManagePanelSelectionAsync()
        => ShowManageToolsSelectionAsync(0);

    [ComponentInteraction("recruit_manage_panel_page:*", ignoreGroupNames: true)]
    public async Task HandleManagePanelPageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageToolsSelectionAsync(pageIndex);
    }

    [ComponentInteraction("recruit_manage_panel_select:*", ignoreGroupNames: true)]
    public async Task HandleManagePanelSelectAsync(string messageIdRaw)
    {
        if (!TryParseRecruitMessageId(messageIdRaw, out var messageId))
        {
            await RespondAsync("募集メッセージのURLかIDを入力してください。", ephemeral: true);
            return;
        }

        await ShowManageToolsAsync(messageId);
    }

    [ComponentInteraction("recruit_manage_cancel", ignoreGroupNames: true)]
    public Task ShowManageCancelModalAsync()
        => ShowManageSelectionAsync(RecruitPostStatus.Canceled);

    [ComponentInteraction("recruit_manage_close_select:*", ignoreGroupNames: true)]
    public Task HandleManageCloseSelectAsync(string messageId)
        => HandleManageSelectionAsync(messageId, RecruitPostStatus.Closed, "募集を終了しました。");

    [ComponentInteraction("recruit_manage_cancel_select:*", ignoreGroupNames: true)]
    public Task HandleManageCancelSelectAsync(string messageId)
        => HandleManageSelectionAsync(messageId, RecruitPostStatus.Canceled, "募集を中断しました。");

    [ComponentInteraction("recruit_manage_close_page:*", ignoreGroupNames: true)]
    public async Task HandleManageClosePageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageSelectionAsync(RecruitPostStatus.Closed, pageIndex);
    }

    [ComponentInteraction("recruit_manage_cancel_page:*", ignoreGroupNames: true)]
    public async Task HandleManageCancelPageAsync(string pageRaw)
    {
        if (!int.TryParse(pageRaw, out var pageIndex))
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        await ShowManageSelectionAsync(RecruitPostStatus.Canceled, pageIndex);
    }

    [ModalInteraction("recruit_comment_edit_modal", ignoreGroupNames: true)]
    public async Task HandleCommentEditModalAsync(RecruitCommentEditModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_commentEditTracker.TryRemove(Context.User.Id, out var context))
        {
            await RespondAsync("編集情報が見つかりませんでした。もう一度操作してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - context.CreatedAt > InteractionContextTimeout)
        {
            await RespondAsync("編集情報の有効期限が切れました。もう一度操作してください。", ephemeral: true);
            return;
        }

        var comment = modal.Comment?.Trim();

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
        if (post is null)
        {
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var updated = await _db.UpdateRecruitSlotCommentAsync(post.Id, Context.User.Id, comment ?? string.Empty);
        if (!updated)
        {
            await FollowupAsync("参加していません。", ephemeral: true);
            return;
        }

        var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        if (slot is not null)
        {
            await UpsertRecruitParticipantEmbedAsync(post, Context.User, slot, mentionHost: false);
        }

        await FollowupAsync("コメントを更新しました。", ephemeral: true);
    }

    [ModalInteraction("recruit_post_edit_modal", ignoreGroupNames: true)]
    public async Task HandlePostEditModalAsync(RecruitPostEditModal modal)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_postEditTracker.TryGet(Context.User.Id, out var context))
        {
            await RespondAsync("編集情報が見つかりませんでした。もう一度操作してください。", ephemeral: true);
            return;
        }

        if (context.GuildId != Context.Guild.Id || context.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは編集を続行できません。もう一度操作してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - context.CreatedAt > InteractionContextTimeout)
        {
            _postEditTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync("編集情報の有効期限が切れました。もう一度操作してください。", ephemeral: true);
            return;
        }

        var title = modal.RecruitTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("募集タイトルを入力してください。", ephemeral: true);
            return;
        }

        var note = modal.Note?.Trim();
        var updatedContext = context with
        {
            Title = title,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _postEditTracker.Set(Context.User.Id, updatedContext);
        await RespondAsync(
            $"開催DCを選択してください。（現在: {updatedContext.DataCenter ?? "未設定"}）",
            components: BuildPostEditDataCenterSelectComponents(updatedContext.DataCenter),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_close", ignoreGroupNames: true)]
    public async Task CloseRecruitAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await HandleManageSelectionAsync(component.Message.Id.ToString(), RecruitPostStatus.Closed, "募集を終了しました。");
    }

    [ComponentInteraction("recruit_cancel_post", ignoreGroupNames: true)]
    public async Task CancelRecruitPostAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await HandleManageSelectionAsync(component.Message.Id.ToString(), RecruitPostStatus.Canceled, "募集を中断しました。");
    }

    private async Task OpenJobSelectInternalAsync(string position)
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        await OpenJobSelectInternalAsync(position, component.Message.Id);
    }

    private async Task OpenJobSelectInternalAsync(string position, ulong messageId)
    {
        if (TryParseFlexPositionToken(position, out var flexSpec))
        {
            await OpenFlexJobSelectInternalAsync(flexSpec, messageId);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
        {
            await RespondAsync("ポジションが不明です。", ephemeral: true);
            return;
        }

        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("ポジションが見つかりません。", ephemeral: true);
            return;
        }

        var userId = ToDbId(Context.User.Id);
        if (target.IsClosed && !target.UserId.HasValue)
        {
            if (!CanJoinByRelocatingClosedFlexAnchor(post, slots, target, userId))
            {
                await RespondAsync($"{position} は〆です（ゲーム内募集で埋まり）。", ephemeral: true);
                return;
            }
        }

        if (target.UserId.HasValue && target.UserId.Value == userId)
        {
            await RespondAsync($"{position} には既に参加しています。", ephemeral: true);
            return;
        }

        if (target.UserId.HasValue &&
            target.UserId.Value != userId &&
            !CanJoinBySwappingFlexibleHost(post, slots, target, userId))
        {
            await RespondAsync($"{position} は既に埋まっています。", ephemeral: true);
            return;
        }

        IReadOnlyList<JobOption> availableJobs = jobs;
        if (!string.IsNullOrWhiteSpace(target.RequiredJob))
        {
            availableJobs = jobs
                .Where(job => string.Equals(job.Code, target.RequiredJob, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (availableJobs.Count == 0)
            {
                await RespondAsync($"{position} の指定ジョブ設定が不正です。主催者に確認してください。", ephemeral: true);
                return;
            }
        }

        var jobStates = availableJobs
            .Select(job => new
            {
                Job = job,
                Disabled = HasTakenJobConflictForJoin(post, slots, job.Code, target.Position, userId, recruitPositions)
            })
            .ToArray();
        var hasSelectableJobs = jobStates.Any(state => !state.Disabled);

        var builder = new ComponentBuilder();
        var index = 0;
        foreach (var state in jobStates)
        {
            var row = index / 5;
            builder.WithButton(
                state.Job.Code,
                $"recruit_job_pick:{state.Job.Code}|{position}|{messageId}",
                GetPositionButtonStyle(position),
                disabled: state.Disabled,
                row: row);
            index++;
        }

        await RespondAsync(
            string.IsNullOrWhiteSpace(target.RequiredJob)
                ? hasSelectableJobs
                    ? $"{position} のジョブを選択してください。"
                    : $"{position} のジョブを選択してください。（重複により選択可能ジョブがありません）"
                : hasSelectableJobs
                    ? $"{position} のジョブを選択してください（指定: {target.RequiredJob}）。"
                    : $"{position} のジョブを選択してください（指定: {target.RequiredJob} / 重複により選択可能ジョブがありません）。",
            components: builder.Build(),
            ephemeral: true);
    }

    private async Task OpenFlexJobSelectInternalAsync(string flexSpec, ulong messageId)
    {
        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var candidatePositions = ResolveFlexCandidatePositions(flexSpec, slots);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("FLEX対象ポジションが見つかりませんでした。", ephemeral: true);
            return;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var userId = ToDbId(Context.User.Id);
        var jobCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in candidatePositions)
        {
            var slot = slots.FirstOrDefault(item => string.Equals(item.Position, position, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                continue;
            }

            if (slot.IsClosed &&
                !CanJoinByRelocatingClosedFlexAnchor(post, slots, slot, userId))
            {
                continue;
            }

            if (slot.UserId.HasValue && slot.UserId.Value == userId)
            {
                continue;
            }

            if (slot.UserId.HasValue && slot.UserId.Value != userId &&
                !CanJoinBySwappingFlexibleHost(post, slots, slot, userId))
            {
                continue;
            }

            if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
            {
                continue;
            }

            var availableJobs = string.IsNullOrWhiteSpace(slot.RequiredJob)
                ? jobs
                : jobs.Where(job => string.Equals(job.Code, slot.RequiredJob, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var job in availableJobs)
            {
                if (HasTakenJobConflictForJoin(post, slots, job.Code, position, userId, recruitPositions))
                {
                    continue;
                }

                jobCodes.Add(job.Code);
            }
        }

        if (jobCodes.Count == 0)
        {
            await RespondAsync($"{BuildFlexSelectionLabel(flexSpec)} で参加できる空き枠がありません。", ephemeral: true);
            return;
        }

        var orderedJobCodes = (string.Equals(flexSpec, "D1,D2", StringComparison.OrdinalIgnoreCase)
            ? SortJobCodesForD1D2Selector(jobCodes)
            : SortJobCodesForFlexSelector(jobCodes))
            .ToArray();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_flex_job_select:{flexSpec}")
            .WithPlaceholder("ジョブを選択（複数可）")
            .WithMinValues(1)
            .WithMaxValues(Math.Min(orderedJobCodes.Length, 25));
        foreach (var jobCode in orderedJobCodes)
        {
            menu.AddOption(jobCode, $"{jobCode}|{messageId}");
        }

        await RespondAsync(
            $"{BuildFlexSelectionLabel(flexSpec)} のジョブを選択してください（複数可）。",
            components: new ComponentBuilder()
                .WithSelectMenu(menu)
                .Build(),
            ephemeral: true);
    }

    private async Task HandleJobSelectInternalAsync(string position, string[] values)
    {
        PruneInteractionContexts();
        if (values is not { Length: > 0 })
        {
            await RespondAsync("ジョブを選択してください。", ephemeral: true);
            return;
        }

        var selected = new List<(string Job, ulong MessageId)>();
        foreach (var value in values)
        {
            var (jobCode, selectedMessageId) = ParseJobValue(value);
            if (string.IsNullOrWhiteSpace(jobCode) || !selectedMessageId.HasValue)
            {
                continue;
            }

            selected.Add((jobCode.Trim().ToUpperInvariant(), selectedMessageId.Value));
        }

        if (selected.Count == 0)
        {
            await RespondAsync("ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        var messageId = selected[0].MessageId;
        if (selected.Any(item => item.MessageId != messageId))
        {
            await RespondAsync("ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        var selectedJobs = selected
            .Select(item => item.Job)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedJob = selectedJobs[0];

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.Status != RecruitPostStatus.Open)
        {
            var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
            if (slot is null)
            {
                await RespondAsync(BuildJoinBlockedMessage(post.Status), ephemeral: true);
                return;
            }
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var requesterUserId = ToDbId(Context.User.Id);
        if (TryParseFlexPositionToken(position, out var flexSpec))
        {
            var candidatePositions = ResolveFlexCandidatePositions(flexSpec, slots);
            if (candidatePositions.Count == 0)
            {
                await RespondAsync("FLEX対象ポジションが見つかりませんでした。", ephemeral: true);
                return;
            }

            var recruitPositionsForFlex = slots
                .Select(slot => slot.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var availableCandidates = new List<(string Position, int Priority, int Order)>();
            var availableSelectedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var order = 0; order < candidatePositions.Count; order++)
            {
                var candidate = candidatePositions[order];
                var slot = slots.FirstOrDefault(item => string.Equals(item.Position, candidate, StringComparison.OrdinalIgnoreCase));
                if (slot is null)
                {
                    continue;
                }

                if (slot.IsClosed &&
                    !CanJoinByRelocatingClosedFlexAnchor(post, slots, slot, requesterUserId))
                {
                    continue;
                }

                if (slot.UserId.HasValue && slot.UserId.Value == requesterUserId)
                {
                    continue;
                }

                if (slot.UserId.HasValue && slot.UserId.Value != requesterUserId &&
                    !CanJoinBySwappingFlexibleHost(post, slots, slot, requesterUserId))
                {
                    continue;
                }

                if (!RecruitmentRules.TryGetJobsForRecruit(candidate, recruitPositionsForFlex, out var jobs))
                {
                    continue;
                }

                var allowedJobs = string.IsNullOrWhiteSpace(slot.RequiredJob)
                    ? jobs
                    : jobs.Where(option => string.Equals(option.Code, slot.RequiredJob, StringComparison.OrdinalIgnoreCase)).ToArray();
                var matchedJobs = selectedJobs
                    .Where(selectedJobCode => allowedJobs.Any(option => string.Equals(option.Code, selectedJobCode, StringComparison.OrdinalIgnoreCase)))
                    .Where(selectedJobCode => !HasTakenJobConflictForJoin(post, slots, selectedJobCode, candidate, requesterUserId, recruitPositionsForFlex))
                    .ToArray();
                if (matchedJobs.Length == 0)
                {
                    continue;
                }

                var priority = slot.UserId.HasValue ? 1 : 0;
                availableCandidates.Add((candidate, priority, order));
                foreach (var matchedJob in matchedJobs)
                {
                    availableSelectedJobs.Add(matchedJob);
                }
            }

            var orderedCandidates = availableCandidates
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Order)
                .Select(candidate => candidate.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidateJobs = selectedJobs
                .Where(jobCode => availableSelectedJobs.Contains(jobCode))
                .ToArray();
            if (orderedCandidates.Length == 0 || candidateJobs.Length == 0)
            {
                await RespondAsync($"{BuildFlexSelectionLabel(flexSpec)} で選択したジョブは使えません。", ephemeral: true);
                return;
            }

            var trackedCandidates = orderedCandidates
                .Concat(candidatePositions
                    .Select(position => position.Trim().ToUpperInvariant())
                    .Where(position => !string.IsNullOrWhiteSpace(position)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _joinTracker.Set(Context.User.Id, new RecruitJoinContext(
                messageId,
                trackedCandidates[0],
                candidateJobs[0],
                DateTimeOffset.UtcNow,
                CandidatePositions: trackedCandidates,
                RequestedLabel: BuildFlexSelectionLabel(flexSpec),
                CandidateJobs: candidateJobs));
            await RespondWithModalAsync<RecruitJoinCommentModal>("recruit_join_comment_modal");
            return;
        }

        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("ポジションが見つかりません。", ephemeral: true);
            return;
        }

        if (target.IsClosed && !target.UserId.HasValue)
        {
            if (!CanJoinByRelocatingClosedFlexAnchor(post, slots, target, requesterUserId))
            {
                await RespondAsync($"{position} は〆です（ゲーム内募集で埋まり）。", ephemeral: true);
                return;
            }
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (HasTakenJobConflictForJoin(post, slots, selectedJob, target.Position, requesterUserId, recruitPositions))
        {
            await RespondAsync("そのジョブは既に埋まっています。", ephemeral: true);
            return;
        }

        _joinTracker.Set(Context.User.Id, new RecruitJoinContext(messageId, position, selectedJob, DateTimeOffset.UtcNow));
        await RespondWithModalAsync<RecruitJoinCommentModal>("recruit_join_comment_modal");
    }

    private async Task<IUserMessage> EnsurePanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing recruit panel message.");
            }
        }

        if (existing is null)
        {
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    (message.Content.StartsWith(RecruitmentPanel.PanelMessage, StringComparison.Ordinal) ||
                     message.Content.StartsWith(RecruitmentPanel.DisabledPanelMessage, StringComparison.Ordinal) ||
                     RecruitmentPanel.LooksLikePanelComponents(message.Components)));
        }

        var panelMessage = RecruitmentPanel.BuildPanelMessage();
        var components = RecruitmentPanel.BuildPanelComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = panelMessage;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(panelMessage, components: components);
    }

    private async Task UpdateRecruitMessageAsync(RecruitPost post)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var latestPost = await _db.GetRecruitPostByMessageIdAsync(ToUlong(post.MessageId));
        if (latestPost is not null)
        {
            post = latestPost;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var filled = slots.Count(slot => slot.UserId.HasValue || slot.IsClosed);
        var slotCount = slots.Count;
        var nextStatus = post.Status;
        if (slotCount > 0 && post.Status == RecruitPostStatus.Open && filled >= slotCount)
        {
            nextStatus = RecruitPostStatus.Full;
        }
        else if (slotCount > 0 && post.Status == RecruitPostStatus.Full && filled < slotCount)
        {
            nextStatus = RecruitPostStatus.Open;
        }

        if (nextStatus != post.Status && post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
        {
            var previousStatus = post.Status;
            await _db.UpdateRecruitPostStatusAsync(post.Id, nextStatus);
            post.Status = nextStatus;
            if (nextStatus == RecruitPostStatus.Full)
            {
                await PostRecruitStatusMessageAsync(post, "募集が満員になりました。");
            }
            else if (previousStatus == RecruitPostStatus.Full && nextStatus == RecruitPostStatus.Open)
            {
                var openSummary = BuildOpenPositionSummary(slots);
                var notice = string.IsNullOrWhiteSpace(openSummary)
                    ? "空きが出たため募集を再開します。"
                    : $"空きが出たため募集を再開します。空き: {openSummary}";
                await PostRecruitStatusMessageAsync(post, notice, mentionUserIds: new[] { ToUlong(post.CreatorUserId) });
            }

            await SendRecruitStatusTransitionAuditAsync(
                post,
                previousStatus,
                nextStatus,
                "自動（空き枠判定）",
                $"filled={filled}, total={slotCount}");
        }

        var embed = BuildRecruitEmbed(post, slots, post.DisallowDuplicateJobs);

        IMessageChannel? channel = Context.Guild.GetTextChannel(ToUlong(post.ChannelId));
        channel ??= Context.Channel as IMessageChannel;
        if (channel is not null)
        {
            var message = await channel.GetMessageAsync(ToUlong(post.MessageId)) as IUserMessage;
            if (message is not null)
            {
                await message.ModifyAsync(props =>
                {
                    props.Embed = embed;
                    var positions = GetOrderedPositions(slots);
                    props.Components = RecruitmentPanel.BuildRecruitComponents(post.Status, positions);
                });
            }
        }
    }

    private async Task<IThreadChannel?> EnsureRecruitThreadAsync(RecruitPost post, ITextChannel? knownChannel = null, IUserMessage? knownMessage = null)
    {
        if (Context.Guild is null)
        {
            return null;
        }

        if (post.ThreadChannelId.HasValue)
        {
            var existingThread = await ResolveRecruitThreadChannelAsync(ToUlong(post.ThreadChannelId.Value));
            if (existingThread is not null)
            {
                return existingThread;
            }
        }

        var textChannel = knownChannel ?? Context.Guild.GetTextChannel(ToUlong(post.ChannelId)) ?? Context.Channel as ITextChannel;
        if (textChannel is null)
        {
            return null;
        }

        var message = knownMessage ?? await textChannel.GetMessageAsync(ToUlong(post.MessageId)) as IUserMessage;
        if (message is null)
        {
            return null;
        }

        var existingByMessage = await ResolveThreadByStarterMessageAsync(Context.Guild, textChannel, message.Id);
        if (existingByMessage is not null)
        {
            await _db.UpdateRecruitThreadAsync(post.Id, existingByMessage.Id, null);
            post.ThreadChannelId = ToDbId(existingByMessage.Id);
            return existingByMessage;
        }

        var threadName = BuildRecruitThreadName(post.Title);

        try
        {
            var threadType = textChannel is INewsChannel ? ThreadType.NewsThread : ThreadType.PublicThread;
            var thread = await textChannel.CreateThreadAsync(threadName, threadType, ThreadArchiveDuration.OneWeek, message);
            await _db.UpdateRecruitThreadAsync(post.Id, thread.Id, null);
            post.ThreadChannelId = ToDbId(thread.Id);
            return thread;
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MessageAlreadyContainsThread)
        {
            var existing = await ResolveThreadByStarterMessageAsync(Context.Guild, textChannel, message.Id);
            if (existing is not null)
            {
                await _db.UpdateRecruitThreadAsync(post.Id, existing.Id, null);
                post.ThreadChannelId = ToDbId(existing.Id);
                return existing;
            }

            _logger.LogWarning(ex, "Thread already exists for message {MessageId}, but failed to resolve the channel.", message.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create recruit thread for message {MessageId}.", message.Id);
            return null;
        }
    }

    private async Task UpsertRecruitParticipantEmbedAsync(
        RecruitPost post,
        SocketUser user,
        RecruitSlot slot,
        bool mentionHost,
        ulong? fallbackThreadMessageId = null,
        bool preferFallbackThreadMessageId = false)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        try
        {
            var embed = await _profileEmbeds.BuildRecruitJoinEmbedAsync(
                user,
                Context.Guild,
                "PT Join",
                Color.Blue,
                slot.Position,
                slot.Job ?? string.Empty,
                slot.Comment ?? string.Empty);

            var candidateMessageIds = new List<ulong>();
            if (preferFallbackThreadMessageId && fallbackThreadMessageId.HasValue)
            {
                candidateMessageIds.Add(fallbackThreadMessageId.Value);
            }

            if (slot.ThreadMessageId.HasValue)
            {
                candidateMessageIds.Add(ToUlong(slot.ThreadMessageId.Value));
            }

            if (!preferFallbackThreadMessageId && fallbackThreadMessageId.HasValue)
            {
                candidateMessageIds.Add(fallbackThreadMessageId.Value);
            }

            ulong? modifiedMessageId = null;
            foreach (var candidateMessageId in candidateMessageIds.Distinct())
            {
                if (!await TryModifyThreadMessageAsync(threadChannel, candidateMessageId, embed))
                {
                    continue;
                }

                modifiedMessageId = candidateMessageId;
                break;
            }

            if (modifiedMessageId.HasValue)
            {
                var currentThreadMessageId = slot.ThreadMessageId.HasValue
                    ? ToUlong(slot.ThreadMessageId.Value)
                    : (ulong?)null;
                if (slot.UserId.HasValue && currentThreadMessageId != modifiedMessageId.Value)
                {
                    await _db.UpdateRecruitSlotThreadMessageAsync(post.Id, ToUlong(slot.UserId.Value), modifiedMessageId.Value);
                }

                return;
            }

            var content = mentionHost ? $"{MentionUser(post.CreatorUserId)} 参加が入りました。" : string.Empty;
            var mentions = mentionHost
                ? new AllowedMentions { UserIds = new List<ulong> { ToUlong(post.CreatorUserId) } }
                : AllowedMentions.None;

            var message = await threadChannel.SendMessageAsync(text: content, embed: embed, allowedMentions: mentions);
            if (slot.UserId.HasValue)
            {
                await _db.UpdateRecruitSlotThreadMessageAsync(post.Id, ToUlong(slot.UserId.Value), message.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post recruit participant embed for post {PostId}.", post.Id);
        }
    }

    private async Task DeleteRecruitParticipantEmbedAsync(RecruitPost post, RecruitSlot slot)
    {
        if (!slot.ThreadMessageId.HasValue)
        {
            return;
        }

        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        try
        {
            await threadChannel.DeleteMessageAsync(ToUlong(slot.ThreadMessageId.Value));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete recruit participant embed for post {PostId}.", post.Id);
        }
    }

    private async Task EnsureRecruitThreadMemberAsync(RecruitPost post, ulong userId, IThreadChannel? knownThread = null)
    {
        if (Context.Guild is null || userId == 0)
        {
            return;
        }

        var threadChannel = knownThread ?? await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        var guildUser = await ResolveRecruitGuildUserAsync(userId);
        if (guildUser is null || guildUser.IsBot)
        {
            return;
        }

        try
        {
            await threadChannel.JoinAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join recruit thread {ThreadId} before adding user {UserId}.", threadChannel.Id, userId);
        }

        try
        {
            await threadChannel.AddUserAsync(guildUser);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add user {UserId} to recruit thread for post {PostId}.", userId, post.Id);
        }
    }

    private async Task RemoveRecruitThreadMemberIfNoSlotAsync(RecruitPost post, ulong userId)
    {
        if (Context.Guild is null || userId == 0)
        {
            return;
        }

        var remainingSlot = await _db.GetRecruitSlotByUserAsync(post.Id, userId);
        if (remainingSlot is not null)
        {
            return;
        }

        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        var guildUser = await ResolveRecruitGuildUserAsync(userId);
        if (guildUser is null || guildUser.IsBot)
        {
            return;
        }

        try
        {
            await threadChannel.RemoveUserAsync(guildUser);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove user {UserId} from recruit thread for post {PostId}.", userId, post.Id);
        }
    }

    private Task<IGuildUser?> ResolveRecruitGuildUserAsync(ulong userId)
    {
        if (Context.Guild is null || userId == 0)
        {
            return Task.FromResult<IGuildUser?>(null);
        }

        return Task.FromResult<IGuildUser?>(Context.Guild.GetUser(userId));
    }

    private async Task<bool> TryModifyThreadMessageAsync(IThreadChannel threadChannel, ulong messageId, Embed embed)
    {
        try
        {
            var message = await threadChannel.GetMessageAsync(messageId) as IUserMessage;
            if (message is null)
            {
                return false;
            }

            await message.ModifyAsync(props => props.Embed = embed);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update recruit thread message {MessageId}.", messageId);
            return false;
        }
    }

    private async Task PostRecruitStatusMessageAsync(RecruitPost post, string text, IReadOnlyCollection<ulong>? mentionUserIds = null)
    {
        var targets = mentionUserIds?
            .Where(id => id != 0)
            .Distinct()
            .ToList() ?? new List<ulong>();
        var mentions = targets.Count == 0 ? string.Empty : string.Join(' ', targets.Select(id => $"<@{id}>"));
        var payload = string.IsNullOrWhiteSpace(mentions) ? text : $"{text}\n{mentions}";
        var allowedMentions = targets.Count == 0
            ? AllowedMentions.None
            : new AllowedMentions { UserIds = targets };

        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is not null)
        {
            try
            {
                await threadChannel.SendMessageAsync(payload, allowedMentions: allowedMentions);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post recruit status message for post {PostId}.", post.Id);
            }
        }

        var channel = Context.Guild?.GetTextChannel(ToUlong(post.ChannelId)) ?? Context.Channel as IMessageChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(payload, allowedMentions: allowedMentions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post recruit status message for post {PostId}.", post.Id);
        }
    }

    private async Task UnarchiveRecruitThreadAsync(RecruitPost post)
    {
        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        try
        {
            await threadChannel.ModifyAsync(props =>
            {
                props.Archived = false;
                props.Locked = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unarchive recruit thread for post {PostId}.", post.Id);
        }
    }

    private async Task PostRecruitChangeLogAsync(RecruitPost post, SocketUser user, RecruitSlot previous, RecruitSlot current)
    {
        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        var from = $"{previous.Position}:{previous.Job ?? "?"}";
        var to = $"{current.Position}:{current.Job ?? "?"}";
        var text = $"変更: {MentionUser(ToDbId(user.Id))} {from} → {to}";
        try
        {
            await threadChannel.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post recruit change log for post {PostId}.", post.Id);
        }
    }

    private async Task<IThreadChannel?> ResolveRecruitThreadChannelAsync(ulong channelId)
    {
        if (Context.Client.GetChannel(channelId) is IThreadChannel cached)
        {
            return cached;
        }

        try
        {
            var channel = await Context.Client.Rest.GetChannelAsync(channelId);
            return channel as IThreadChannel;
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownChannel)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve recruit thread channel {ChannelId}.", channelId);
            return null;
        }
    }

    private async Task<(bool Handled, bool Deleted)> DeleteRecruitThreadForCancelAllAsync(RecruitPost post)
    {
        if (!post.ThreadChannelId.HasValue)
        {
            return (true, false);
        }

        var threadId = ToUlong(post.ThreadChannelId.Value);
        var thread = await ResolveRecruitThreadChannelAsync(threadId);
        if (thread is null)
        {
            try
            {
                await _db.UpdateRecruitThreadAsync(post.Id, null, null);
                post.ThreadChannelId = null;
                post.ThreadMessageId = null;
                return (true, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear stale recruit thread reference for post {PostId}.", post.Id);
                return (false, false);
            }
        }

        try
        {
            await thread.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete recruit thread {ThreadId} for post {PostId}.", thread.Id, post.Id);
            return (false, false);
        }

        try
        {
            await _db.UpdateRecruitThreadAsync(post.Id, null, null);
            post.ThreadChannelId = null;
            post.ThreadMessageId = null;
            return (true, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recruit thread {ThreadId} was deleted but failed to clear DB reference for post {PostId}.", thread.Id, post.Id);
            return (false, true);
        }
    }

    private async Task<IThreadChannel?> ResolveThreadByStarterMessageAsync(SocketGuild guild, ITextChannel textChannel, ulong starterMessageId)
    {
        var cached = guild.GetThreadChannel(starterMessageId);
        if (cached is not null)
        {
            return cached;
        }

        if (textChannel is SocketTextChannel socketTextChannel)
        {
            var inChannel = socketTextChannel.Threads.FirstOrDefault(thread => thread.Id == starterMessageId);
            if (inChannel is not null)
            {
                return inChannel;
            }
        }

        try
        {
            var channel = await Context.Client.Rest.GetChannelAsync(starterMessageId);
            return channel as IThreadChannel;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildRecruitThreadName(string title)
    {
        var name = $"募集: {title}".Trim();
        const int maxLength = 90;
        return name.Length > maxLength ? name[..maxLength] : name;
    }

    private async Task UpdateRecruitListAsync(SocketGuild? guild, IMessageChannel? fallbackChannel)
    {
        if (guild is null || fallbackChannel is null)
        {
            return;
        }

        await _listService.RefreshAsync(guild, fallbackChannel);
    }

    private void PruneInteractionContexts()
    {
        var now = DateTimeOffset.UtcNow;
        _joinTracker.PruneExpired(now, InteractionContextTimeout);
        _commentEditTracker.PruneExpired(now, InteractionContextTimeout);
        _postEditTracker.PruneExpired(now, InteractionContextTimeout);
        _createDraftTracker.PruneExpired(now, CreateDraftTimeout);
    }

    private static string BuildJoinBlockedMessage(RecruitPostStatus status)
    {
        return status switch
        {
            RecruitPostStatus.Full => "募集は満員です。",
            RecruitPostStatus.Closed => "募集は終了しました。",
            RecruitPostStatus.Canceled => "募集は中断されました。",
            _ => "募集に参加できません。"
        };
    }

    private async Task ShowManageSelectionAsync(RecruitPostStatus targetStatus)
        => await ShowManageSelectionAsync(targetStatus, 0);

    private async Task ShowManageToolsSelectionAsync(int pageIndex)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var posts = await _db.ListActiveRecruitPostsByCreatorAsync(Context.Guild.Id, Context.User.Id);
        if (posts.Count == 0)
        {
            await RespondAsync("管理できる自分の募集がありません。", ephemeral: true);
            return;
        }

        var totalPages = (posts.Count + ManageSelectionPageSize - 1) / ManageSelectionPageSize;
        if (pageIndex < 0 || pageIndex >= totalPages)
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var start = pageIndex * ManageSelectionPageSize;
        var pageItems = posts
            .Skip(start)
            .Take(ManageSelectionPageSize)
            .ToList();

        var builder = new ComponentBuilder();
        for (var i = 0; i < pageItems.Count; i++)
        {
            var post = pageItems[i];
            var number = start + i + 1;
            var compactSchedule = BuildCompactScheduleLabel(post.ScheduleText);
            var dcLabel = string.IsNullOrWhiteSpace(post.DataCenter) ? "-" : post.DataCenter!.Trim();
            var meta = $"{dcLabel}/{compactSchedule}";
            var title = TruncateLabel(post.Title, 40);
            var label = TruncateLabel($"{number}: {title} [{meta}]", 80);
            builder.WithButton(label, $"recruit_manage_panel_select:{ToUlong(post.MessageId)}", ButtonStyle.Secondary, row: i / 5);
        }

        builder
            .WithButton("前のページ", $"recruit_manage_panel_page:{pageIndex - 1}", ButtonStyle.Secondary, disabled: pageIndex <= 0, row: 4)
            .WithButton("次のページ", $"recruit_manage_panel_page:{pageIndex + 1}", ButtonStyle.Secondary, disabled: pageIndex >= totalPages - 1, row: 4);

        await RespondAsync(
            $"管理する募集を選択してください。（ページ {pageIndex + 1}/{totalPages}）",
            components: builder.Build(),
            ephemeral: true);
    }

    private async Task ShowManageSelectionAsync(RecruitPostStatus targetStatus, int pageIndex)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var canCancelOthers = targetStatus == RecruitPostStatus.Canceled && IsRecruitCancelPrivilegedUser();
        IReadOnlyList<RecruitPostSummary> posts;
        if (canCancelOthers)
        {
            posts = (await _db.ListRecruitPostSummariesAsync(Context.Guild.Id))
                .Where(post => post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
                .ToList();
        }
        else
        {
            posts = await _db.ListActiveRecruitPostsByCreatorAsync(Context.Guild.Id, Context.User.Id);
        }

        if (posts.Count == 0)
        {
            await RespondAsync("対象の募集がありません。", ephemeral: true);
            return;
        }

        var totalPages = (posts.Count + ManageSelectionPageSize - 1) / ManageSelectionPageSize;
        if (pageIndex < 0 || pageIndex >= totalPages)
        {
            await RespondAsync("ページ情報が不正です。", ephemeral: true);
            return;
        }

        var start = pageIndex * ManageSelectionPageSize;
        var pageItems = posts
            .Skip(start)
            .Take(ManageSelectionPageSize)
            .ToList();

        var builder = new ComponentBuilder();
        var action = targetStatus == RecruitPostStatus.Canceled ? "中断" : "終了";
        for (var i = 0; i < pageItems.Count; i++)
        {
            var post = pageItems[i];
            var number = start + i + 1;
            var compactSchedule = BuildCompactScheduleLabel(post.ScheduleText);
            var dcLabel = string.IsNullOrWhiteSpace(post.DataCenter) ? "-" : post.DataCenter!.Trim();
            var meta = $"{dcLabel}/{compactSchedule}";
            var title = TruncateLabel(post.Title, canCancelOthers ? 30 : 40);
            var creatorLabel = canCancelOthers
                ? $" / {TruncateLabel(BuildRecruitCreatorLabel(Context.Guild, post.CreatorUserId), 10)}"
                : string.Empty;
            var label = TruncateLabel($"{number}: {title} [{meta}]{creatorLabel}", 80);
            var customId = targetStatus == RecruitPostStatus.Canceled
                ? $"recruit_manage_cancel_select:{ToUlong(post.MessageId)}"
                : $"recruit_manage_close_select:{ToUlong(post.MessageId)}";
            builder.WithButton(label, customId, ButtonStyle.Secondary, row: i / 5);
        }

        var prevCustomId = targetStatus == RecruitPostStatus.Canceled
            ? $"recruit_manage_cancel_page:{pageIndex - 1}"
            : $"recruit_manage_close_page:{pageIndex - 1}";
        var nextCustomId = targetStatus == RecruitPostStatus.Canceled
            ? $"recruit_manage_cancel_page:{pageIndex + 1}"
            : $"recruit_manage_close_page:{pageIndex + 1}";
        builder
            .WithButton("前のページ", prevCustomId, ButtonStyle.Secondary, disabled: pageIndex <= 0, row: 4)
            .WithButton("次のページ", nextCustomId, ButtonStyle.Secondary, disabled: pageIndex >= totalPages - 1, row: 4);

        await RespondAsync(
            $"募集を{action}する番号を選んでください。（ページ {pageIndex + 1}/{totalPages}）",
            components: builder.Build(),
            ephemeral: true);
    }

    private async Task HandleManageSelectionAsync(string messageIdRaw, RecruitPostStatus newStatus, string userMessage, bool requireCreator = true)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseRecruitMessageId(messageIdRaw, out var messageId))
        {
            await RespondAsync("募集メッセージのURLかIDを入力してください。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (requireCreator && post.CreatorUserId != ToDbId(Context.User.Id))
        {
            var canCancelOthers = newStatus == RecruitPostStatus.Canceled && IsRecruitCancelPrivilegedUser();
            if (!canCancelOthers)
            {
                await LogRecruitPermissionDeniedAsync(post, "状態変更", "主催者のみが操作できます。");
                await RespondAsync("主催者のみが操作できます。", ephemeral: true);
                return;
            }
        }

        if (newStatus == RecruitPostStatus.Closed &&
            post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("募集は既に終了しています。", ephemeral: true);
            return;
        }

        if (newStatus == RecruitPostStatus.Canceled && post.Status == RecruitPostStatus.Canceled)
        {
            await RespondAsync("募集は既に中断されています。", ephemeral: true);
            return;
        }

        var previousStatus = post.Status;
        await _db.UpdateRecruitPostStatusAsync(post.Id, newStatus);
        post.Status = newStatus;
        await UpdateRecruitMessageAsync(post);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        var action = newStatus == RecruitPostStatus.Canceled ? "募集を中断" : "募集を終了";
        await SendRecruitAuditAsync(post, "募集管理: 状態変更", $"{action}（{userMessage}）");
        await SendRecruitStatusTransitionAuditAsync(post, previousStatus, newStatus, "手動（管理操作）", action);
        await RespondAsync(userMessage, ephemeral: true);
    }

    private async Task HandleReopenAsync(string messageIdRaw, bool requireCreator = true)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseRecruitMessageId(messageIdRaw, out var messageId))
        {
            await RespondAsync("募集メッセージのURLかIDを入力してください。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (requireCreator && post.CreatorUserId != ToDbId(Context.User.Id))
        {
            await LogRecruitPermissionDeniedAsync(post, "再開", "主催者のみが操作できます。");
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
        {
            await RespondAsync("募集は既に開いています。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var openStatus = DetermineOpenStatus(slots);

        var previousStatus = post.Status;
        await _db.UpdateRecruitPostStatusAsync(post.Id, openStatus);
        post.Status = openStatus;
        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, "募集を再開しました。");
        await UnarchiveRecruitThreadAsync(post);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await SendRecruitAuditAsync(post, "募集管理: 状態変更", "募集を再開");
        await SendRecruitStatusTransitionAuditAsync(post, previousStatus, openStatus, "手動（再開）");
        await RespondAsync("募集を再開しました。", ephemeral: true);
    }

    private async Task OpenCreateHostJobSelectAsync(string hostPosition)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != Context.Channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        var candidatePositions = ResolveCreateHostCandidatePositions(hostPosition, draft.Positions);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("選択できないポジションです。再度選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        var availableByCode = new Dictionary<string, JobOption>(StringComparer.OrdinalIgnoreCase);
        var jobsByCandidatePosition = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidatePosition in candidatePositions)
        {
            if (draft.PreFilledMembers.ContainsKey(candidatePosition) ||
                IsPositionCoveredByCreatePrefilledFlexSlot(candidatePosition, draft))
            {
                continue;
            }

            if (!RecruitmentRules.TryGetJobsForRecruit(candidatePosition, draft.Positions, out var jobs))
            {
                continue;
            }

            IReadOnlyList<JobOption> candidateJobs = jobs;
            if (draft.RequiredJobs.TryGetValue(candidatePosition, out var requiredHostJob) && !string.IsNullOrWhiteSpace(requiredHostJob))
            {
                candidateJobs = jobs
                    .Where(job => string.Equals(job.Code, requiredHostJob, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            jobsByCandidatePosition[candidatePosition] = candidateJobs
                .Select(job => job.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidateJob in candidateJobs)
            {
                availableByCode.TryAdd(candidateJob.Code, candidateJob);
            }
        }

        JobOption[] availableJobs;
        if (TryParseFlexPositionToken(hostPosition, out var hostFlexSpec) &&
            IsD1D2FlexSpec(hostFlexSpec))
        {
            var commonCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allSets = jobsByCandidatePosition.Values.ToArray();
            if (allSets.Length > 0)
            {
                commonCodes.UnionWith(allSets[0]);
                foreach (var set in allSets.Skip(1))
                {
                    commonCodes.IntersectWith(set);
                }
            }

            availableJobs = SortJobsForD1D2Selector(availableByCode.Values.Where(job => commonCodes.Contains(job.Code))).ToArray();
        }
        else
        {
            availableJobs = SortJobsForFlexSelector(availableByCode.Values).ToArray();
        }
        if (availableJobs.Length == 0)
        {
            await RespondAsync($"{BuildHostSelectorLabel(hostPosition)} で選択可能な主催ジョブがありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{BuildHostSelectorLabel(hostPosition)} の主催ジョブを選択してください。",
            components: BuildCreateHostJobSelectComponents(hostPosition, availableJobs),
            ephemeral: true);
    }

    private async Task ShowCreateHostFlexPositionSelectAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        var selectablePositions = draft.Positions
            .Where(position => !draft.PreFilledMembers.ContainsKey(position))
            .Where(position => !IsPositionCoveredByCreatePrefilledFlexSlot(position, draft))
            .ToArray();
        if (selectablePositions.Length == 0)
        {
            await RespondAsync("主催に選択できるポジションがありません。", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_create_host_flex_positions")
            .WithPlaceholder("主催候補ポジションを選択（複数可）")
            .WithMinValues(1)
            .WithMaxValues(Math.Min(selectablePositions.Length, 25));
        foreach (var position in selectablePositions)
        {
            menu.AddOption(position, position);
        }

        await RespondAsync(
            "FLEXで使う主催候補ポジションを選択してください。",
            components: new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("主催選択に戻る", "recruit_create_host_flex_back", ButtonStyle.Secondary, row: 1)
                .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
                .Build(),
            ephemeral: true);
    }

    private async Task ConfirmCreateHostAsync(string hostPosition, string[] values)
    {
        if (values is not { Length: > 0 })
        {
            await RespondAsync("主催ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectedJobs = values
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedJobs.Length == 0)
        {
            await RespondAsync("主催ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var usedFlexSelector = TryParseFlexPositionToken(hostPosition, out _);
        var requestedFlexibleHostSelector = usedFlexSelector;

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        var candidatePositions = ResolveCreateHostCandidatePositions(hostPosition, draft.Positions);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("選択できないポジションです。再度選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }
        string? resolvedHostPosition = null;
        string? resolvedHostJob = null;
        string? resolvedHostFlexSpec = null;
        string? resolvedHostFlexJobsSpec = null;
        var normalizedSelectedJobs = selectedJobs
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var flexiblePositionsForSelection = requestedFlexibleHostSelector
            ? ResolveHostFlexiblePositionsForJobs(candidatePositions, draft, normalizedSelectedJobs)
            : Array.Empty<string>();
        var keepFlexibleHostSelector = requestedFlexibleHostSelector && flexiblePositionsForSelection.Count >= 2;
        foreach (var candidatePosition in candidatePositions)
        {
            if (draft.PreFilledMembers.ContainsKey(candidatePosition) ||
                IsPositionCoveredByCreatePrefilledFlexSlot(candidatePosition, draft))
            {
                continue;
            }

            if (keepFlexibleHostSelector &&
                (draft.PreFilledSlots.ContainsKey(candidatePosition) ||
                 IsPositionCoveredByCreatePrefilledFlexSlot(candidatePosition, draft)))
            {
                continue;
            }

            if (!RecruitmentRules.TryGetJobsForRecruit(candidatePosition, draft.Positions, out var jobs))
            {
                continue;
            }

            if (draft.RequiredJobs.TryGetValue(candidatePosition, out var requiredHostJob) &&
                !string.IsNullOrWhiteSpace(requiredHostJob))
            {
                jobs = jobs
                    .Where(job => string.Equals(requiredHostJob, job.Code, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            foreach (var selectedJob in selectedJobs)
            {
                var matched = jobs.FirstOrDefault(job =>
                    string.Equals(job.Code, selectedJob, StringComparison.OrdinalIgnoreCase));
                if (matched is null)
                {
                    continue;
                }

                var candidateJobCode = matched.Code;
                var hostJobReserved = ShouldReserveFlexibleJobForDuplicateCheck(
                    keepFlexibleHostSelector ? string.Join(',', flexiblePositionsForSelection) : null,
                    normalizedSelectedJobs.Length > 0 ? normalizedSelectedJobs : new[] { candidateJobCode },
                    draft.Positions);
                var hostJobTakenByPrefilled = draft.DisallowDuplicateJobs &&
                    (keepFlexibleHostSelector
                        ? draft.PreFilledSlots.Any(pair => string.Equals(pair.Value, candidateJobCode, StringComparison.OrdinalIgnoreCase))
                        : draft.PreFilledSlots.Any(pair =>
                            !string.Equals(pair.Key, candidatePosition, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(pair.Value, candidateJobCode, StringComparison.OrdinalIgnoreCase)));
                var hostJobTakenByPrefilledMember = draft.DisallowDuplicateJobs &&
                    (keepFlexibleHostSelector
                        ? draft.PreFilledMembers.Any(pair =>
                            ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                            string.Equals(pair.Value.Job, candidateJobCode, StringComparison.OrdinalIgnoreCase))
                        : draft.PreFilledMembers.Any(pair =>
                            !string.Equals(pair.Key, candidatePosition, StringComparison.OrdinalIgnoreCase) &&
                            ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                            string.Equals(pair.Value.Job, candidateJobCode, StringComparison.OrdinalIgnoreCase)));
                var hostJobTakenByPrefilledFlex = draft.DisallowDuplicateJobs &&
                    (keepFlexibleHostSelector
                        ? draft.PreFilledFlexSlots.Any(pair =>
                            ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                            string.Equals(pair.Value.Job, candidateJobCode, StringComparison.OrdinalIgnoreCase))
                        : draft.PreFilledFlexSlots.Any(pair =>
                            !string.Equals(pair.Key, candidatePosition, StringComparison.OrdinalIgnoreCase) &&
                            ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                            string.Equals(pair.Value.Job, candidateJobCode, StringComparison.OrdinalIgnoreCase)));
                if ((hostJobReserved && hostJobTakenByPrefilled) ||
                    (hostJobReserved && hostJobTakenByPrefilledMember) ||
                    (hostJobReserved && hostJobTakenByPrefilledFlex))
                {
                    continue;
                }

                resolvedHostPosition = candidatePosition;
                resolvedHostJob = candidateJobCode;
                resolvedHostFlexSpec = keepFlexibleHostSelector
                    ? string.Join(',', flexiblePositionsForSelection)
                    : null;
                resolvedHostFlexJobsSpec = keepFlexibleHostSelector
                    ? string.Join(',', normalizedSelectedJobs)
                    : null;
                break;
            }

            if (!string.IsNullOrWhiteSpace(resolvedHostPosition) && !string.IsNullOrWhiteSpace(resolvedHostJob))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedHostPosition) || string.IsNullOrWhiteSpace(resolvedHostJob))
        {
            await RespondAsync($"{BuildHostSelectorLabel(hostPosition)} で選択したジョブは使用できません。", ephemeral: true);
            return;
        }

        var preFilledSlots = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);
        preFilledSlots.Remove(resolvedHostPosition);
        preFilledFlexSlots.Remove(resolvedHostPosition);

        var updatedDraft = draft with
        {
            HostPosition = resolvedHostPosition,
            HostJob = resolvedHostJob,
            HostFlexSpec = resolvedHostFlexSpec,
            HostFlexJobsSpec = resolvedHostFlexJobsSpec,
            PreFilledSlots = preFilledSlots,
            PreFilledFlexSlots = preFilledFlexSlots,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        await ShowCreateConfirmationInternalAsync(
            !string.IsNullOrWhiteSpace(resolvedHostFlexSpec)
                ? $"主催を {BuildFlexSelectionLabel(resolvedHostFlexSpec)} / {resolvedHostJob} に設定しました。"
                : usedFlexSelector
                ? $"主催を {resolvedHostPosition} / {resolvedHostJob} に設定しました。投稿前に内容を確認してください。"
                : "投稿前に内容を確認してください。");
    }

    private async Task<RecruitCreateDraftContext?> GetValidCreateDraftAsync()
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return null;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return null;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return null;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return null;
        }

        return draft;
    }

    private async Task ShowCreateConfirmationInternalAsync(string? prompt = null)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.HostPosition) || string.IsNullOrWhiteSpace(draft.HostJob))
        {
            await RespondAsync("主催ポジションと主催ジョブを先に選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        var updatedDraft = draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"{prompt ?? "投稿前に内容を確認してください。"}\n{BuildCreateConfirmationSummary(updatedDraft, Context.Guild)}",
            components: BuildCreateConfirmComponents(Context.Guild, updatedDraft),
            ephemeral: true);
    }

    private async Task ShowCreateRequiredPositionSelectInternalAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.HostPosition) || string.IsNullOrWhiteSpace(draft.HostJob))
        {
            await RespondAsync("主催ポジションと主催ジョブを先に選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        var updatedDraft = draft with { CreatedAt = DateTimeOffset.UtcNow };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"ジョブ指定するポジションを選択してください。\n{BuildCreateRequiredJobsSummary(updatedDraft.Positions, updatedDraft.RequiredJobs)}",
            components: BuildCreateRequiredPositionSelectComponents(updatedDraft.Positions, updatedDraft.RequiredJobs),
            ephemeral: true);
    }

    private async Task OpenCreateRequiredJobSelectInternalAsync(string position)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (!draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        draft.RequiredJobs.TryGetValue(position, out var currentRequiredJob);
        var hostJob = string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase) ? draft.HostJob : null;
        _createDraftTracker.Set(Context.User.Id, draft with { CreatedAt = DateTimeOffset.UtcNow });
        await RespondAsync(
            hostJob is null
                ? $"{position} の指定ジョブを選択してください（解除可）。"
                : $"{position} は主催枠です。主催ジョブ `{hostJob}` のみ指定できます（解除可）。",
            components: BuildCreateRequiredJobSelectComponents(position, jobs, currentRequiredJob, hostJob),
            ephemeral: true);
    }

    private async Task UpdateCreateRequiredJobInternalAsync(string position, string job)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (!draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var requiredJobs = new Dictionary<string, string>(draft.RequiredJobs, StringComparer.OrdinalIgnoreCase);
        var normalized = job.Trim();
        if (string.Equals(normalized, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            requiredJobs.Remove(position);
        }
        else
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs) ||
                !jobs.Any(item => string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                await RespondAsync("そのジョブは対象ポジションに指定できません。", ephemeral: true);
                return;
            }

            if (string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(draft.HostJob) &&
                !string.Equals(draft.HostJob, normalized, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync($"{position} は主催ジョブ `{draft.HostJob}` のみ指定できます。", ephemeral: true);
                return;
            }

            if (draft.PreFilledSlots.TryGetValue(position, out var filledJob) &&
                !string.IsNullOrWhiteSpace(filledJob) &&
                !string.Equals(filledJob, normalized, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync($"{position} は事前埋まりで `{filledJob}` を設定中です。先に事前埋まりを更新してください。", ephemeral: true);
                return;
            }

            if (draft.PreFilledMembers.TryGetValue(position, out var preFilledMember) &&
                !string.IsNullOrWhiteSpace(preFilledMember.Job) &&
                !string.Equals(preFilledMember.Job, normalized, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync($"{position} は事前埋まりで `{preFilledMember.Job}` を設定中です。先に事前埋まりを更新してください。", ephemeral: true);
                return;
            }

            requiredJobs[position] = normalized;
        }

        var updatedDraft = draft with
        {
            RequiredJobs = requiredJobs,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"{position} の指定ジョブを更新しました。\n{BuildCreateRequiredJobsSummary(updatedDraft.Positions, updatedDraft.RequiredJobs)}",
            components: BuildCreateRequiredPositionSelectComponents(updatedDraft.Positions, updatedDraft.RequiredJobs),
            ephemeral: true);
    }

    private async Task ShowCreatePrefillPositionSelectInternalAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.HostPosition) || string.IsNullOrWhiteSpace(draft.HostJob))
        {
            await RespondAsync("主催ポジションと主催ジョブを先に選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        var editablePositions = draft.Positions
            .Where(position => !string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (editablePositions.Count == 0)
        {
            await RespondAsync("主催枠以外に事前埋まりを設定できるポジションがありません。", ephemeral: true);
            return;
        }

        var updatedDraft = draft with { CreatedAt = DateTimeOffset.UtcNow };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"事前に埋まっているポジションを選択してください。\n{BuildCreatePreFilledSummaryWithLegend(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition)}",
            components: BuildCreatePrefillPositionSelectComponents(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition, updatedDraft.HostFlexSpec),
            ephemeral: true);
    }

    private async Task ShowCreatePrefillClearSelectInternalAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var clearOptions = BuildCreatePrefillClearOptions(draft);
        if (clearOptions.Count == 0)
        {
            await RespondAsync(
                $"解除できる事前埋まりがありません。\n{BuildCreatePreFilledSummaryWithLegend(draft.Positions, draft.PreFilledSlots, draft.PreFilledFlexSlots, draft.PreFilledMembers, draft.HostPosition)}",
                components: BuildCreatePrefillPositionSelectComponents(draft.Positions, draft.PreFilledSlots, draft.PreFilledFlexSlots, draft.PreFilledMembers, draft.HostPosition, draft.HostFlexSpec),
                ephemeral: true);
            return;
        }

        _createDraftTracker.Set(Context.User.Id, draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await RespondAsync(
            "解除する事前埋まりを選択してください。",
            components: BuildCreatePrefillClearSelectComponents(clearOptions),
            ephemeral: true);
    }

    private async Task ClearCreatePrefillSelectionInternalAsync(string clearTargetRaw)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var clearTarget = clearTargetRaw.Trim();
        if (string.IsNullOrWhiteSpace(clearTarget))
        {
            await RespondAsync("解除対象の情報が不正です。", ephemeral: true);
            return;
        }

        var clearOptions = BuildCreatePrefillClearOptions(draft);
        var selectedOption = clearOptions
            .FirstOrDefault(option => string.Equals(option.Value, clearTarget, StringComparison.OrdinalIgnoreCase));
        if (selectedOption is null)
        {
            await RespondAsync("選択した事前埋まりが見つかりません。もう一度選択してください。", ephemeral: true);
            return;
        }

        var preFilledSlots = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledMembers = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
        var preFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);

        var removed = false;
        if (clearTarget.StartsWith("POS:", StringComparison.OrdinalIgnoreCase))
        {
            var position = clearTarget["POS:".Length..].Trim().ToUpperInvariant();
            if (!draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
            {
                await RespondAsync("解除対象の情報が不正です。", ephemeral: true);
                return;
            }

            removed |= preFilledSlots.Remove(position);
            removed |= preFilledMembers.Remove(position);
            removed |= preFilledFlexSlots.Remove(position);
        }
        else if (TryParseFlexPositionToken(clearTarget, out var flexSpec))
        {
            var candidatePositions = ResolveFlexCandidatePositions(flexSpec, draft.Positions);
            if (candidatePositions.Count < 2)
            {
                await RespondAsync("解除対象の情報が不正です。", ephemeral: true);
                return;
            }

            var candidateSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var flexKeys = preFilledFlexSlots
                .Where(pair => IsMatchingFlexSelector(pair.Value.FlexSpec, candidateSet, draft.Positions))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in flexKeys)
            {
                removed |= preFilledFlexSlots.Remove(key);
            }

            var memberKeys = preFilledMembers
                .Where(pair => IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidateSet, draft.Positions))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in memberKeys)
            {
                removed |= preFilledMembers.Remove(key);
            }
        }
        else
        {
            await RespondAsync("解除対象の情報が不正です。", ephemeral: true);
            return;
        }

        if (!removed)
        {
            await RespondAsync("選択した事前埋まりはすでに解除されています。", ephemeral: true);
            return;
        }

        var updatedDraft = draft with
        {
            PreFilledSlots = preFilledSlots,
            PreFilledFlexSlots = preFilledFlexSlots,
            PreFilledMembers = preFilledMembers,
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"{selectedOption.Label} の事前埋まりを解除しました。\n{BuildCreatePreFilledSummaryWithLegend(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition)}",
            components: BuildCreatePrefillPositionSelectComponents(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition, updatedDraft.HostFlexSpec),
            ephemeral: true);
    }

    private async Task HandleCreatePrefillFlexSourceInternalAsync(string flexSpecRaw, bool useMemberSource)
    {
        var normalizedSpec = flexSpecRaw.Trim().ToUpperInvariant();
        var selector = normalizedSpec.StartsWith("FLEX:", StringComparison.OrdinalIgnoreCase)
            ? normalizedSpec
            : $"FLEX:{normalizedSpec}";
        if (!TryParseFlexPositionToken(selector, out var flexSpec))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        if (string.Equals(flexSpec, "ANY", StringComparison.OrdinalIgnoreCase))
        {
            await ShowCreatePrefillFlexPositionSelectInternalAsync(useMemberSource);
            return;
        }

        if (useMemberSource)
        {
            await OpenCreatePrefillMemberJobSelectInternalAsync(selector);
            return;
        }

        await OpenCreatePrefillExternalJobSelectInternalAsync(selector);
    }

    private async Task ShowCreatePrefillFlexSourceSelectInternalAsync(string flexSpec)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var normalizedSpec = flexSpec.Trim().ToUpperInvariant();
        var selector = normalizedSpec.StartsWith("FLEX:", StringComparison.OrdinalIgnoreCase)
            ? normalizedSpec
            : $"FLEX:{normalizedSpec}";
        if (!TryParseFlexPositionToken(selector, out var parsedSpec))
        {
            await RespondAsync("ポジションの選択情報が不正です。", ephemeral: true);
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        _createDraftTracker.Set(Context.User.Id, draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await RespondAsync(
            "FLEXの事前埋まりを設定します。このサーバーの人ですか？",
            components: BuildCreatePrefillFlexSourceSelectComponents(parsedSpec),
            ephemeral: true);
    }

    private async Task ShowCreatePrefillFlexPositionSelectInternalAsync(bool useMemberSource)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var requestedPositions = draft.Positions
            .Select(position => position.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowHostPosition = CanIncludeHostPositionInCreatePrefillSelector(requestedPositions, draft);
        var selectablePositions = requestedPositions
            .Where(position => allowHostPosition || !string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selectablePositions.Length == 0)
        {
            await RespondAsync("事前埋まりFLEXに選択できるポジションがありません。", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId(useMemberSource
                ? "recruit_create_prefill_flex_positions_member"
                : "recruit_create_prefill_flex_positions_external")
            .WithPlaceholder("事前埋まりFLEXの候補ポジションを選択（複数可）")
            .WithMinValues(1)
            .WithMaxValues(Math.Min(selectablePositions.Length, 25));
        foreach (var position in selectablePositions)
        {
            menu.AddOption(position, position);
        }

        await RespondAsync(
            "FLEXで使う事前埋まり候補ポジションを選択してください。",
            components: new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("戻る", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 1)
                .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
                .Build(),
            ephemeral: true);
    }

    private async Task ShowCreatePrefillSourceSelectInternalAsync(string selector)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }
        var normalizedSelector = candidatePositions.Count >= 2
            ? $"FLEX:{string.Join(',', candidatePositions)}"
            : candidatePositions[0];
        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions[0];

        var updatedDraft = draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        await RespondAsync(
            $"{selectorLabel} の事前埋まりを設定します。このサーバーの人ですか？",
            components: BuildCreatePrefillSourceSelectComponents(normalizedSelector),
            ephemeral: true);
    }

    private async Task OpenCreatePrefillJobSelectInternalAsync(string selector)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        if (candidatePositions.Count >= 2)
        {
            await RespondAsync("FLEXの事前埋まりはサーバーメンバー指定で設定してください。", ephemeral: true);
            return;
        }

        var position = candidatePositions[0];
        var canOverlapHostForSinglePosition = CanIncludeHostPositionInCreatePrefillSelector(
            new[] { position },
            draft);
        if (string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase) &&
            !canOverlapHostForSinglePosition)
        {
            await RespondAsync("主催枠は事前埋まりに設定できません。", ephemeral: true);
            return;
        }

        if (TryFindCreatePrefilledFlexAnchoredAtPosition(position, draft, out var coveringFlexLabel))
        {
            await RespondAsync($"{position} は {coveringFlexLabel} のアンカーです。先に「事前埋まり解除」でFLEXを解除してください。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        draft.PreFilledSlots.TryGetValue(position, out var currentJob);
        var candidatePositionSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (draft.DisallowDuplicateJobs)
        {
            if (ShouldReserveHostJobForDuplicateCheck(draft) &&
                !string.IsNullOrWhiteSpace(draft.HostJob))
            {
                usedJobs.Add(draft.HostJob);
            }

            foreach (var pair in draft.PreFilledSlots)
            {
                if (string.Equals(pair.Key, position, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    usedJobs.Add(pair.Value);
                }
            }

            foreach (var pair in draft.PreFilledMembers)
            {
                if (string.Equals(pair.Key, position, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }

            foreach (var pair in draft.PreFilledFlexSlots)
            {
                if (ResolveFlexCandidatePositions(pair.Value.FlexSpec, draft.Positions).Any(candidatePositionSet.Contains))
                {
                    continue;
                }

                if (ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }
        }

        draft.RequiredJobs.TryGetValue(position, out var requiredJob);
        _createDraftTracker.Set(Context.User.Id, draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await RespondAsync(
            string.IsNullOrWhiteSpace(requiredJob)
                ? $"{position} の事前埋まりジョブを選択してください（解除可）。"
                : $"{position} は指定ジョブ `{requiredJob}` のみ事前埋まりにできます（解除可）。",
            components: BuildCreatePrefillJobSelectComponents(position, jobs, currentJob, usedJobs, requiredJob),
            ephemeral: true);
    }

    private async Task OpenCreatePrefillExternalJobSelectInternalAsync(string selector)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var normalizedSelector = candidatePositions.Count >= 2
            ? $"FLEX:{string.Join(',', candidatePositions)}"
            : candidatePositions[0];
        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions[0];
        var candidatePositionSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includesHostPosition = candidatePositions.Any(position => string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase));
        if (includesHostPosition &&
            !CanIncludeHostPositionInCreatePrefillSelector(candidatePositions, draft))
        {
            await RespondAsync("主催枠は事前埋まりに設定できません。", ephemeral: true);
            return;
        }

        var availableByCode = new Dictionary<string, JobOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in candidatePositions)
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
            {
                continue;
            }

            IReadOnlyList<JobOption> candidateJobs = jobs;
            if (draft.RequiredJobs.TryGetValue(position, out var requiredJobByPosition) &&
                !string.IsNullOrWhiteSpace(requiredJobByPosition))
            {
                candidateJobs = jobs
                    .Where(job => string.Equals(job.Code, requiredJobByPosition, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            foreach (var job in candidateJobs)
            {
                availableByCode.TryAdd(job.Code, job);
            }
        }

        var availableJobs = SortJobsForFlexSelector(availableByCode.Values).ToArray();
        if (availableJobs.Length == 0)
        {
            await RespondAsync($"{selectorLabel} で設定できるジョブがありません。", ephemeral: true);
            return;
        }

        var currentFlexSlot = draft.PreFilledFlexSlots
            .Where(pair => IsMatchingFlexSelector(pair.Value.FlexSpec, candidatePositionSet, draft.Positions))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        string? currentJobSpec;
        if (currentFlexSlot is not null)
        {
            currentJobSpec = string.IsNullOrWhiteSpace(currentFlexSlot.FlexJobsSpec)
                ? currentFlexSlot.Job
                : currentFlexSlot.FlexJobsSpec;
        }
        else if (candidatePositions.Count == 1 &&
                 draft.PreFilledSlots.TryGetValue(candidatePositions[0], out var currentFixedJob))
        {
            currentJobSpec = currentFixedJob;
        }
        else
        {
            currentJobSpec = null;
        }

        var usedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (draft.DisallowDuplicateJobs)
        {
            if (ShouldReserveHostJobForDuplicateCheck(draft) &&
                !string.IsNullOrWhiteSpace(draft.HostJob))
            {
                usedJobs.Add(draft.HostJob);
            }

            foreach (var pair in draft.PreFilledSlots)
            {
                if (candidatePositions.Count == 1 &&
                    candidatePositions.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    usedJobs.Add(pair.Value);
                }
            }

            foreach (var pair in draft.PreFilledMembers)
            {
                if (IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidatePositionSet, draft.Positions))
                {
                    continue;
                }

                if (ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }

            foreach (var pair in draft.PreFilledFlexSlots)
            {
                if (IsMatchingFlexSelector(pair.Value.FlexSpec, candidatePositionSet, draft.Positions))
                {
                    continue;
                }

                if (ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }

        }

        var requiredJobs = candidatePositions
            .Select(position => draft.RequiredJobs.TryGetValue(position, out var requiredJob) ? requiredJob : null)
            .Where(requiredJob => !string.IsNullOrWhiteSpace(requiredJob))
            .Select(requiredJob => requiredJob!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredJobLabel = requiredJobs.Length == 1 ? requiredJobs[0] : null;

        _createDraftTracker.Set(Context.User.Id, draft with
        {
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await RespondAsync(
            string.IsNullOrWhiteSpace(requiredJobLabel)
                ? $"{selectorLabel}（外部）にするジョブを選択してください。"
                : $"{selectorLabel}（外部）は指定ジョブ `{requiredJobLabel}` のみ設定できます。",
            components: BuildCreatePrefillJobSelectComponents(
                normalizedSelector,
                availableJobs,
                currentJobSpec,
                usedJobs,
                requiredJobLabel,
                selectCustomIdPrefix: "recruit_create_prefill_external_job",
                backCustomId: "recruit_create_confirm_edit_prefill"),
            ephemeral: true);
    }

    private async Task UpdateCreatePrefillExternalJobInternalAsync(string selector, IReadOnlyList<string> selectedJobs)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var normalizedJobs = selectedJobs
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedJobs.Length == 0)
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions[0];
        if (normalizedJobs.Contains("NONE", StringComparer.OrdinalIgnoreCase))
        {
            if (normalizedJobs.Length != 1)
            {
                await RespondAsync("ジョブ選択が不正です。選択をやり直してください。", ephemeral: true);
                return;
            }

            var preFilledSlots = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
            var preFilledMembers = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
            var preFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);
            if (candidatePositions.Count >= 2)
            {
                var candidateSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matchingFlexKeys = preFilledFlexSlots
                    .Where(pair => IsMatchingFlexSelector(pair.Value.FlexSpec, candidateSet, draft.Positions))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var key in matchingFlexKeys)
                {
                    preFilledFlexSlots.Remove(key);
                }
            }
            else
            {
                var position = candidatePositions[0];
                preFilledSlots.Remove(position);
                preFilledMembers.Remove(position);
                preFilledFlexSlots.Remove(position);
            }

            var updated = draft with
            {
                PreFilledSlots = preFilledSlots,
                PreFilledFlexSlots = preFilledFlexSlots,
                PreFilledMembers = preFilledMembers,
                PendingPreFilledMemberPosition = null,
                PendingPreFilledMemberJob = null,
                PendingPreFilledMemberJobsSpec = null,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _createDraftTracker.Set(Context.User.Id, updated);
            await RespondAsync(
                $"{selectorLabel} の事前埋まり（外部）を解除しました。\n{BuildCreatePreFilledSummaryWithLegend(updated.Positions, updated.PreFilledSlots, updated.PreFilledFlexSlots, updated.PreFilledMembers, updated.HostPosition)}",
                components: BuildCreatePrefillPositionSelectComponents(updated.Positions, updated.PreFilledSlots, updated.PreFilledFlexSlots, updated.PreFilledMembers, updated.HostPosition, updated.HostFlexSpec),
                ephemeral: true);
            return;
        }

        var eligiblePositions = new List<string>();
        foreach (var position in candidatePositions)
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
            {
                continue;
            }

            var hasMatchedJob = normalizedJobs.Any(job =>
                jobs.Any(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase)));
            if (!hasMatchedJob)
            {
                continue;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !normalizedJobs.Contains(requiredJob, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            eligiblePositions.Add(position);
        }

        if (eligiblePositions.Count == 0)
        {
            await RespondAsync($"{selectorLabel} には選択したジョブ `{string.Join('/', normalizedJobs)}` を設定できません。", ephemeral: true);
            return;
        }

        var resolvedPositionSet = eligiblePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedSelector = eligiblePositions.Count >= 2
            ? $"FLEX:{string.Join(',', eligiblePositions)}"
            : eligiblePositions[0];
        var resolvedLabel = eligiblePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', eligiblePositions))
            : eligiblePositions[0];
        var resolvedFlexSpec = eligiblePositions.Count >= 2 ? string.Join(',', eligiblePositions) : null;

        var candidateJobs = normalizedJobs
            .Where(job =>
                eligiblePositions.Any(position =>
                {
                    if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
                    {
                        return false;
                    }

                    if (!jobs.Any(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                        !string.IsNullOrWhiteSpace(requiredJob) &&
                        !string.Equals(requiredJob, job, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return true;
                }))
            .ToArray();
        if (candidateJobs.Length == 0)
        {
            await RespondAsync($"{resolvedLabel} には選択したジョブを設定できません。", ephemeral: true);
            return;
        }

        if (draft.DisallowDuplicateJobs)
        {
            candidateJobs = candidateJobs
                .Where(job =>
                {
                    if (ShouldReserveHostJobForDuplicateCheck(draft) &&
                        !string.IsNullOrWhiteSpace(draft.HostJob) &&
                        string.Equals(draft.HostJob, job, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var jobTakenByClosed = draft.PreFilledSlots.Any(pair =>
                        !(resolvedPositionSet.Count == 1 &&
                          resolvedPositionSet.Contains(pair.Key)) &&
                        string.Equals(pair.Value, job, StringComparison.OrdinalIgnoreCase));
                    if (jobTakenByClosed)
                    {
                        return false;
                    }

                    var jobTakenByMember = draft.PreFilledMembers.Any(pair =>
                        !IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, resolvedPositionSet, draft.Positions) &&
                        ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                        string.Equals(pair.Value.Job, job, StringComparison.OrdinalIgnoreCase));
                    if (jobTakenByMember)
                    {
                        return false;
                    }

                    var jobTakenByFlexSlot = draft.PreFilledFlexSlots.Any(pair =>
                        !IsMatchingFlexSelector(pair.Value.FlexSpec, resolvedPositionSet, draft.Positions) &&
                        ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                        string.Equals(pair.Value.Job, job, StringComparison.OrdinalIgnoreCase));
                    return !jobTakenByFlexSlot;
                })
                .ToArray();
            if (candidateJobs.Length == 0)
            {
                await RespondAsync("選択したジョブは別枠で事前埋まり設定済みです。", ephemeral: true);
                return;
            }
        }

        var preFilledSlotsUpdated = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledMembersUpdated = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
        var preFilledFlexSlotsUpdated = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);
        var resolvedPosition = eligiblePositions.Count >= 2
            ? ResolvePreferredPrefillExternalAnchor(
                eligiblePositions,
                preFilledSlotsUpdated,
                preFilledMembersUpdated,
                preFilledFlexSlotsUpdated,
                resolvedPositionSet,
                draft.Positions,
                draft.HostPosition)
            : eligiblePositions[0];
        if (eligiblePositions.Count >= 2)
        {
            if (string.IsNullOrWhiteSpace(resolvedPosition))
            {
                await RespondAsync($"{resolvedLabel} の候補内に事前埋まりFLEXのアンカー空きがありません。", ephemeral: true);
                return;
            }
        }

        preFilledSlotsUpdated.Remove(resolvedPosition);
        preFilledMembersUpdated.Remove(resolvedPosition);
        preFilledFlexSlotsUpdated.Remove(resolvedPosition);

        var selectedPrimaryJob = ResolveAnchorCompatibleJobCode(resolvedPosition, candidateJobs, draft.Positions);
        if (string.IsNullOrWhiteSpace(selectedPrimaryJob))
        {
            await RespondAsync($"{resolvedLabel} には選択したジョブを設定できません。", ephemeral: true);
            return;
        }
        var selectedJobsSpec = candidateJobs.Length >= 2
            ? string.Join(',', candidateJobs)
            : null;
        if (eligiblePositions.Count >= 2)
        {
            preFilledFlexSlotsUpdated[resolvedPosition] = new RecruitPreFilledFlexSlot(
                selectedPrimaryJob,
                resolvedFlexSpec!,
                selectedJobsSpec);
        }
        else
        {
            preFilledSlotsUpdated[resolvedPosition] = selectedPrimaryJob;
        }

        var updatedDraft = draft with
        {
            PreFilledSlots = preFilledSlotsUpdated,
            PreFilledFlexSlots = preFilledFlexSlotsUpdated,
            PreFilledMembers = preFilledMembersUpdated,
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        var selectedJobLabel = string.Join('/', candidateJobs);
        await RespondAsync(
            $"{resolvedLabel} を 外部 ({selectedJobLabel}) で事前埋まりに設定しました。\n{BuildCreatePreFilledSummaryWithLegend(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition)}",
            components: BuildCreatePrefillPositionSelectComponents(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition, updatedDraft.HostFlexSpec),
            ephemeral: true);
    }

    private async Task OpenCreatePrefillMemberJobSelectInternalAsync(string selector)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var normalizedSelector = candidatePositions.Count >= 2
            ? $"FLEX:{string.Join(',', candidatePositions)}"
            : candidatePositions[0];
        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions[0];
        var candidatePositionSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includesHostPosition = candidatePositions.Any(position => string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase));
        if (includesHostPosition &&
            !CanIncludeHostPositionInCreatePrefillSelector(candidatePositions, draft))
        {
            await RespondAsync("主催枠は事前埋まりに設定できません。", ephemeral: true);
            return;
        }

        var availableByCode = new Dictionary<string, JobOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in candidatePositions)
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
            {
                continue;
            }

            IReadOnlyList<JobOption> candidateJobs = jobs;
            if (draft.RequiredJobs.TryGetValue(position, out var requiredJobByPosition) &&
                !string.IsNullOrWhiteSpace(requiredJobByPosition))
            {
                candidateJobs = jobs
                    .Where(job => string.Equals(job.Code, requiredJobByPosition, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            foreach (var job in candidateJobs)
            {
                availableByCode.TryAdd(job.Code, job);
            }
        }

        var availableJobs = SortJobsForFlexSelector(availableByCode.Values).ToArray();
        if (availableJobs.Length == 0)
        {
            await RespondAsync($"{selectorLabel} で設定できるジョブがありません。", ephemeral: true);
            return;
        }

        var currentMember = draft.PreFilledMembers
            .Where(pair => IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidatePositionSet, draft.Positions))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        var currentJobSpec = string.IsNullOrWhiteSpace(currentMember?.FlexJobsSpec)
            ? currentMember?.Job
            : currentMember!.FlexJobsSpec;

        var usedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (draft.DisallowDuplicateJobs)
        {
            if (ShouldReserveHostJobForDuplicateCheck(draft) &&
                !string.IsNullOrWhiteSpace(draft.HostJob))
            {
                usedJobs.Add(draft.HostJob);
            }

            foreach (var pair in draft.PreFilledSlots)
            {
                if (candidatePositions.Count == 1 &&
                    candidatePositions.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    usedJobs.Add(pair.Value);
                }
            }

            foreach (var pair in draft.PreFilledMembers)
            {
                if (IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidatePositionSet, draft.Positions))
                {
                    continue;
                }

                if (ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }

            foreach (var pair in draft.PreFilledFlexSlots)
            {
                if (IsMatchingFlexSelector(pair.Value.FlexSpec, candidatePositionSet, draft.Positions))
                {
                    continue;
                }

                if (ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                    !string.IsNullOrWhiteSpace(pair.Value.Job))
                {
                    usedJobs.Add(pair.Value.Job);
                }
            }
        }

        var requiredJobs = candidatePositions
            .Select(position => draft.RequiredJobs.TryGetValue(position, out var requiredJob) ? requiredJob : null)
            .Where(requiredJob => !string.IsNullOrWhiteSpace(requiredJob))
            .Select(requiredJob => requiredJob!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredJobLabel = requiredJobs.Length == 1 ? requiredJobs[0] : null;
        _createDraftTracker.Set(Context.User.Id, draft with
        {
            PendingPreFilledMemberPosition = normalizedSelector,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await RespondAsync(
            string.IsNullOrWhiteSpace(requiredJobLabel)
                ? $"{selectorLabel} に参加するジョブを選択してください。"
                : $"{selectorLabel} は指定ジョブ `{requiredJobLabel}` のみ設定できます。",
            components: BuildCreatePrefillJobSelectComponents(
                normalizedSelector,
                availableJobs,
                currentJobSpec,
                usedJobs,
                requiredJobLabel,
                selectCustomIdPrefix: "recruit_create_prefill_member_job",
                backCustomId: "recruit_create_confirm_edit_prefill"),
            ephemeral: true);
    }

    private async Task OpenCreatePrefillMemberUserSelectInternalAsync(string selector, IReadOnlyList<string> selectedJobs)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var normalizedJobs = selectedJobs
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedJobs.Length == 0)
        {
            await RespondAsync("ジョブを1つ以上選択してください。", ephemeral: true);
            return;
        }

        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions[0];
        if (normalizedJobs.Contains("NONE", StringComparer.OrdinalIgnoreCase))
        {
            if (normalizedJobs.Length != 1)
            {
                await RespondAsync("ジョブ選択が不正です。選択をやり直してください。", ephemeral: true);
                return;
            }

            var memberSlots = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
            var candidatePositionSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchingMemberKeys = memberSlots
                .Where(pair => IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidatePositionSet, draft.Positions))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var position in matchingMemberKeys)
            {
                memberSlots.Remove(position);
            }
            var updated = draft with
            {
                PreFilledMembers = memberSlots,
                PendingPreFilledMemberPosition = null,
                PendingPreFilledMemberJob = null,
                PendingPreFilledMemberJobsSpec = null,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _createDraftTracker.Set(Context.User.Id, updated);
            await RespondAsync(
                $"{selectorLabel} の事前埋まり（サーバーメンバー）を解除しました。\n{BuildCreatePreFilledSummaryWithLegend(updated.Positions, updated.PreFilledSlots, updated.PreFilledFlexSlots, updated.PreFilledMembers, updated.HostPosition)}",
                components: BuildCreatePrefillPositionSelectComponents(updated.Positions, updated.PreFilledSlots, updated.PreFilledFlexSlots, updated.PreFilledMembers, updated.HostPosition, updated.HostFlexSpec),
                ephemeral: true);
            return;
        }

        var eligiblePositions = new List<string>();
        foreach (var position in candidatePositions)
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
            {
                continue;
            }

            var hasMatchedJob = normalizedJobs.Any(job =>
                jobs.Any(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase)));
            if (!hasMatchedJob)
            {
                continue;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !normalizedJobs.Contains(requiredJob, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            eligiblePositions.Add(position);
        }

        if (eligiblePositions.Count == 0)
        {
            await RespondAsync($"{selectorLabel} には選択したジョブ `{string.Join('/', normalizedJobs)}` を設定できません。", ephemeral: true);
            return;
        }

        var resolvedPositionSet = eligiblePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedSelector = eligiblePositions.Count >= 2
            ? $"FLEX:{string.Join(',', eligiblePositions)}"
            : eligiblePositions[0];
        var resolvedLabel = eligiblePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', eligiblePositions))
            : eligiblePositions[0];

        var candidateJobs = normalizedJobs
            .Where(job =>
                eligiblePositions.Any(position =>
                {
                    if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
                    {
                        return false;
                    }

                    if (!jobs.Any(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                        !string.IsNullOrWhiteSpace(requiredJob) &&
                        !string.Equals(requiredJob, job, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return true;
                }))
            .ToArray();
        if (candidateJobs.Length == 0)
        {
            await RespondAsync($"{resolvedLabel} には選択したジョブを設定できません。", ephemeral: true);
            return;
        }

        if (draft.DisallowDuplicateJobs)
        {
            candidateJobs = candidateJobs
                .Where(job =>
                {
                    if (ShouldReserveHostJobForDuplicateCheck(draft) &&
                        !string.IsNullOrWhiteSpace(draft.HostJob) &&
                        string.Equals(draft.HostJob, job, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var jobTakenByClosed = draft.PreFilledSlots.Any(pair =>
                        !(resolvedPositionSet.Count == 1 &&
                          resolvedPositionSet.Contains(pair.Key)) &&
                        string.Equals(pair.Value, job, StringComparison.OrdinalIgnoreCase));
                    if (jobTakenByClosed)
                    {
                        return false;
                    }

                    var jobTakenByMember = draft.PreFilledMembers.Any(pair =>
                        !IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, resolvedPositionSet, draft.Positions) &&
                        ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                        string.Equals(pair.Value.Job, job, StringComparison.OrdinalIgnoreCase));
                    if (jobTakenByMember)
                    {
                        return false;
                    }

                    var jobTakenByFlexSlot = draft.PreFilledFlexSlots.Any(pair =>
                        !IsMatchingFlexSelector(pair.Value.FlexSpec, resolvedPositionSet, draft.Positions) &&
                        ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                        string.Equals(pair.Value.Job, job, StringComparison.OrdinalIgnoreCase));
                    return !jobTakenByFlexSlot;
                })
                .ToArray();
            if (candidateJobs.Length == 0)
            {
                await RespondAsync("選択したジョブは別枠で事前埋まり設定済みです。", ephemeral: true);
                return;
            }
        }

        var selectedPrimaryJob = candidateJobs[0];
        var selectedJobsSpec = candidateJobs.Length >= 2
            ? string.Join(',', candidateJobs)
            : null;

        var updatedDraft = draft with
        {
            PendingPreFilledMemberPosition = resolvedSelector,
            PendingPreFilledMemberJob = selectedPrimaryJob,
            PendingPreFilledMemberJobsSpec = selectedJobsSpec,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        var candidateUsers = await LoadGuildUsersForSelectionAsync(Context.Guild, excludeUserId: Context.User.Id);
        var component = BuildCreatePrefillMemberUserSelectComponents(updatedDraft, resolvedSelector, selectedPrimaryJob, pageIndex: 0, candidateUsers, out var pageText);
        if (component is null)
        {
            await RespondAsync("選択できるメンバーが見つかりません。", ephemeral: true);
            return;
        }

        var selectedJobLabel = string.Join('/', candidateJobs);
        await RespondAsync(
            $"{resolvedLabel} / {selectedJobLabel} で事前埋まりにするメンバーを選択してください。({pageText})",
            components: component,
            ephemeral: true);
    }

    private async Task ShowCreatePrefillMemberUserSelectPageInternalAsync(int pageIndex)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var position = draft.PendingPreFilledMemberPosition?.Trim().ToUpperInvariant();
        var job = draft.PendingPreFilledMemberJob?.Trim().ToUpperInvariant();
        var jobSpec = draft.PendingPreFilledMemberJobsSpec;
        var selectedJobs = ParseFlexJobCodes(jobSpec);
        if (selectedJobs.Count == 0 && !string.IsNullOrWhiteSpace(job))
        {
            selectedJobs = new[] { job };
        }
        var selectedJobLabel = selectedJobs.Count > 0 ? string.Join('/', selectedJobs) : job;
        var candidatePositions = string.IsNullOrWhiteSpace(position)
            ? Array.Empty<string>()
            : ResolveCreatePrefillCandidatePositions(position, draft);
        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions.Count == 1 ? candidatePositions[0] : position;
        var normalizedSelector = candidatePositions.Count >= 2
            ? $"FLEX:{string.Join(',', candidatePositions)}"
            : candidatePositions.Count == 1 ? candidatePositions[0] : position;
        if (string.IsNullOrWhiteSpace(position) ||
            selectedJobs.Count == 0 ||
            candidatePositions.Count == 0 ||
            string.IsNullOrWhiteSpace(normalizedSelector))
        {
            await RespondAsync("事前埋まりメンバー設定の有効期限が切れました。ポジション選択からやり直してください。", ephemeral: true);
            return;
        }

        var updatedDraft = draft with { CreatedAt = DateTimeOffset.UtcNow };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        var candidateUsers = await LoadGuildUsersForSelectionAsync(Context.Guild, excludeUserId: Context.User.Id);
        var component = BuildCreatePrefillMemberUserSelectComponents(updatedDraft, normalizedSelector, selectedJobs[0], pageIndex, candidateUsers, out var pageText);
        if (component is null)
        {
            await RespondAsync("選択できるメンバーが見つかりません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{selectorLabel} / {selectedJobLabel} で事前埋まりにするメンバーを選択してください。({pageText})",
            components: component,
            ephemeral: true);
    }

    private async Task UpdateCreatePrefillMemberUserInternalAsync(string selectedUserIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var position = draft.PendingPreFilledMemberPosition?.Trim().ToUpperInvariant();
        var job = draft.PendingPreFilledMemberJob?.Trim().ToUpperInvariant();
        var jobSpec = draft.PendingPreFilledMemberJobsSpec;
        var selectedJobs = ParseFlexJobCodes(jobSpec);
        if (selectedJobs.Count == 0 && !string.IsNullOrWhiteSpace(job))
        {
            selectedJobs = new[] { job };
        }
        var candidatePositions = string.IsNullOrWhiteSpace(position)
            ? Array.Empty<string>()
            : ResolveCreatePrefillCandidatePositions(position, draft);
        var selectorLabel = candidatePositions.Count >= 2
            ? BuildFlexSelectionLabel(string.Join(',', candidatePositions))
            : candidatePositions.Count == 1 ? candidatePositions[0] : position;
        if (string.IsNullOrWhiteSpace(position) ||
            selectedJobs.Count == 0 ||
            candidatePositions.Count == 0)
        {
            await RespondAsync("事前埋まりメンバー設定の有効期限が切れました。ポジション選択からやり直してください。", ephemeral: true);
            return;
        }
        var resolvedFlexSpec = candidatePositions.Count >= 2 ? string.Join(',', candidatePositions) : null;

        if (!ulong.TryParse(selectedUserIdRaw.Trim(), out var selectedUserId))
        {
            await RespondAsync("メンバー選択情報が不正です。", ephemeral: true);
            return;
        }

        var selectedUser = Context.Guild.GetUser(selectedUserId);
        if (selectedUser is null)
        {
            var users = await LoadGuildUsersForSelectionAsync(Context.Guild);
            selectedUser = users.FirstOrDefault(user => user.Id == selectedUserId);
        }
        if (selectedUser is null || selectedUser.IsBot)
        {
            await RespondAsync("選択したメンバーを取得できませんでした。", ephemeral: true);
            return;
        }

        if (selectedUser.Id == Context.User.Id)
        {
            await RespondAsync("主催者は主催枠以外に事前埋まりメンバーとして設定できません。", ephemeral: true);
            return;
        }

        var memberSlots = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
        var preFilledSlots = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);
        var resolvedPosition = ResolvePreferredPrefillMemberAnchor(
            candidatePositions,
            preFilledSlots,
            memberSlots,
            preFilledFlexSlots,
            selectedUserId,
            draft.HostPosition);
        if (string.IsNullOrWhiteSpace(resolvedPosition))
        {
            await RespondAsync($"{selectorLabel} の候補内に事前埋まりメンバーのアンカー空きがありません。", ephemeral: true);
            return;
        }

        preFilledSlots.Remove(resolvedPosition);
        memberSlots.Remove(resolvedPosition);
        preFilledFlexSlots.Remove(resolvedPosition);

        var duplicateMember = memberSlots.Any(pair => pair.Value.UserId == selectedUserId);
        if (duplicateMember)
        {
            await RespondAsync("同じメンバーを複数ポジションに設定することはできません。", ephemeral: true);
            return;
        }

        var resolvedJob = ResolveAnchorCompatibleJobCode(resolvedPosition, selectedJobs, draft.Positions);
        if (string.IsNullOrWhiteSpace(resolvedJob))
        {
            await RespondAsync($"{selectorLabel} には選択したジョブを設定できません。", ephemeral: true);
            return;
        }
        var resolvedFlexJobsSpec = selectedJobs.Count >= 2 ? string.Join(',', selectedJobs) : null;
        memberSlots[resolvedPosition] = new RecruitPreFilledMember(
            selectedUserId,
            selectedUser.DisplayName ?? selectedUser.GlobalName ?? selectedUser.Username,
            resolvedJob,
            resolvedFlexSpec,
            resolvedFlexJobsSpec);

        var updatedDraft = draft with
        {
            PreFilledSlots = preFilledSlots,
            PreFilledFlexSlots = preFilledFlexSlots,
            PreFilledMembers = memberSlots,
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        var selectedJobLabel = string.Join('/', selectedJobs);
        await RespondAsync(
            $"{selectorLabel} を {selectedUser.Mention} ({selectedJobLabel}) で事前埋まりに設定しました。\n{BuildCreatePreFilledSummaryWithLegend(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition)}",
            components: BuildCreatePrefillPositionSelectComponents(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition, updatedDraft.HostFlexSpec),
            ephemeral: true);
    }

    private async Task UpdateCreatePrefillJobInternalAsync(string position, string job)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (TryParseFlexPositionToken(position, out _))
        {
            await RespondAsync("FLEXの事前埋まりはサーバーメンバー指定で設定してください。", ephemeral: true);
            return;
        }

        if (!draft.Positions.Contains(position, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("そのポジションは今回の募集に含まれていません。", ephemeral: true);
            return;
        }

        var canOverlapHostForPosition = CanIncludeHostPositionInCreatePrefillSelector(
            new[] { position },
            draft);
        if (string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase) &&
            !canOverlapHostForPosition)
        {
            await RespondAsync("主催枠は事前埋まりに設定できません。", ephemeral: true);
            return;
        }

        if (TryFindCreatePrefilledFlexAnchoredAtPosition(position, draft, out var coveringFlexLabel))
        {
            await RespondAsync($"{position} は {coveringFlexLabel} のアンカーです。先に「事前埋まり解除」でFLEXを解除してください。", ephemeral: true);
            return;
        }

        var preFilledSlots = new Dictionary<string, string>(draft.PreFilledSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(draft.PreFilledFlexSlots, StringComparer.OrdinalIgnoreCase);
        var preFilledMembers = new Dictionary<string, RecruitPreFilledMember>(draft.PreFilledMembers, StringComparer.OrdinalIgnoreCase);
        var normalized = job.Trim().ToUpperInvariant();
        if (string.Equals(normalized, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            preFilledSlots.Remove(position);
            if (preFilledMembers.TryGetValue(position, out var memberAtPosition) &&
                string.IsNullOrWhiteSpace(memberAtPosition.FlexSpec))
            {
                preFilledMembers.Remove(position);
            }
        }
        else
        {
            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs) ||
                !jobs.Any(item => string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                await RespondAsync("そのジョブは対象ポジションに設定できません。", ephemeral: true);
                return;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !string.Equals(requiredJob, normalized, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync($"{position} は指定ジョブ `{requiredJob}` のみ事前埋まりに設定できます。", ephemeral: true);
                return;
            }

            if (draft.DisallowDuplicateJobs &&
                ShouldReserveHostJobForDuplicateCheck(draft) &&
                !string.IsNullOrWhiteSpace(draft.HostJob) &&
                string.Equals(draft.HostJob, normalized, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync($"主催ジョブ `{draft.HostJob}` は事前埋まりで重複できません。", ephemeral: true);
                return;
            }

            var taken = draft.DisallowDuplicateJobs && preFilledSlots.Any(pair =>
                !string.Equals(pair.Key, position, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.Value, normalized, StringComparison.OrdinalIgnoreCase));
            if (taken)
            {
                await RespondAsync($"ジョブ `{normalized}` は別枠で事前埋まり設定済みです。", ephemeral: true);
                return;
            }

            var takenByMember = draft.DisallowDuplicateJobs && preFilledMembers.Any(pair =>
                !string.Equals(pair.Key, position, StringComparison.OrdinalIgnoreCase) &&
                ShouldReservePrefilledMemberJobForDuplicateCheck(pair.Value, draft.Positions) &&
                string.Equals(pair.Value.Job, normalized, StringComparison.OrdinalIgnoreCase));
            if (takenByMember)
            {
                await RespondAsync($"ジョブ `{normalized}` は別枠で事前埋まり設定済みです。", ephemeral: true);
                return;
            }

            var takenByFlexSlot = draft.DisallowDuplicateJobs && preFilledFlexSlots.Any(pair =>
                !string.Equals(pair.Key, position, StringComparison.OrdinalIgnoreCase) &&
                ShouldReservePrefilledFlexSlotJobForDuplicateCheck(pair.Value, draft.Positions) &&
                string.Equals(pair.Value.Job, normalized, StringComparison.OrdinalIgnoreCase));
            if (takenByFlexSlot)
            {
                await RespondAsync($"ジョブ `{normalized}` は別枠で事前埋まり設定済みです。", ephemeral: true);
                return;
            }

            preFilledSlots[position] = normalized;
            if (preFilledMembers.TryGetValue(position, out var memberAtPosition) &&
                string.IsNullOrWhiteSpace(memberAtPosition.FlexSpec))
            {
                preFilledMembers.Remove(position);
            }
        }

        var updatedDraft = draft with
        {
            PreFilledSlots = preFilledSlots,
            PreFilledFlexSlots = preFilledFlexSlots,
            PreFilledMembers = preFilledMembers,
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"{position} の事前埋まりを更新しました。\n{BuildCreatePreFilledSummaryWithLegend(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition)}",
            components: BuildCreatePrefillPositionSelectComponents(updatedDraft.Positions, updatedDraft.PreFilledSlots, updatedDraft.PreFilledFlexSlots, updatedDraft.PreFilledMembers, updatedDraft.HostPosition, updatedDraft.HostFlexSpec),
            ephemeral: true);
    }

    private async Task ConfirmCreatePostInternalAsync()
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.HostPosition) || string.IsNullOrWhiteSpace(draft.HostJob))
        {
            await RespondAsync("主催ポジションと主催ジョブを先に選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        if (draft.RequiredJobs.TryGetValue(draft.HostPosition, out var requiredHostJob) &&
            !string.Equals(requiredHostJob, draft.HostJob, StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(
                $"{draft.HostPosition} の指定ジョブが主催ジョブと一致していません。ジョブ指定を見直してください。",
                components: BuildCreateRequiredPositionSelectComponents(draft.Positions, draft.RequiredJobs),
                ephemeral: true);
            return;
        }

        if (!ValidateCreatePreFilledSlots(draft, Context.User.Id, out var prefillError))
        {
            await RespondAsync(
                prefillError ?? "事前埋まり設定が不正です。見直してください。",
                components: BuildCreatePrefillPositionSelectComponents(draft.Positions, draft.PreFilledSlots, draft.PreFilledFlexSlots, draft.PreFilledMembers, draft.HostPosition, draft.HostFlexSpec),
                ephemeral: true);
            return;
        }

        var activeCount = await _db.CountActiveRecruitPostsByCreatorAsync(Context.Guild.Id, Context.User.Id);
        if (activeCount >= 5)
        {
            await RespondAsync("同時に作成できる募集は5件までです。不要な募集を終了または中断してください。", ephemeral: true);
            return;
        }

        var targetChannel = Context.Guild.GetTextChannel(draft.TargetChannelId);
        if (targetChannel is null)
        {
            await RespondAsync("投稿先チャンネルが見つかりません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        _createDraftTracker.Set(Context.User.Id, draft with { CreatedAt = DateTimeOffset.UtcNow });
        await DeferAsync(ephemeral: true);
        var createdMessageLink = await TryCreateRecruitPostAsync(targetChannel, draft, draft.HostPosition, draft.HostJob);
        if (string.IsNullOrWhiteSpace(createdMessageLink))
        {
            return;
        }

        _createDraftTracker.TryRemove(Context.User.Id, out _);
        await FollowupAsync($"募集を作成しました。\n{createdMessageLink}", ephemeral: true);
    }

    private async Task SelectCreatePartySizeAsync(IReadOnlyList<string> positions)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (positions.Count == 0)
        {
            await RespondAsync("募集タイプの情報が不正です。", ephemeral: true);
            return;
        }

        var updatedDraft = draft with
        {
            Positions = positions.ToArray(),
            RequiredJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PreFilledSlots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PreFilledFlexSlots = new Dictionary<string, RecruitPreFilledFlexSlot>(StringComparer.OrdinalIgnoreCase),
            PreFilledMembers = new Dictionary<string, RecruitPreFilledMember>(StringComparer.OrdinalIgnoreCase),
            PendingPreFilledMemberPosition = null,
            PendingPreFilledMemberJob = null,
            PendingPreFilledMemberJobsSpec = null,
            HostPosition = null,
            HostJob = null,
            HostFlexSpec = null,
            HostFlexJobsSpec = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);

        var partyType = positions.Count >= RecruitmentRules.Positions.Count ? "FULL PARTY" : "LIGHT PARTY";
        if (string.IsNullOrWhiteSpace(updatedDraft.Title))
        {
            await RespondWithModalAsync<RecruitCreateModal>("recruit_create_modal");
            return;
        }

        var duplicateLabel = updatedDraft.DisallowDuplicateJobs ? "禁止" : "許可";
        await RespondAsync(
            $"{partyType} を選択しました。ジョブ重複: {duplicateLabel}\n開催DCを選択してください。",
            components: BuildCreateDataCenterSelectComponents(),
            ephemeral: true);
    }

    private async Task SelectCreateDuplicateSettingAsync(bool disallowDuplicateJobs)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        var updatedDraft = draft with
        {
            DisallowDuplicateJobs = disallowDuplicateJobs,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await RespondAsync(
            $"ジョブ重複を {(disallowDuplicateJobs ? "禁止" : "許可")} に設定しました。募集タイプを選択してください。",
            components: BuildCreatePartySizeSelectComponents(disallowDuplicateJobs),
            ephemeral: true);
    }

    private async Task SelectCreateDataCenterAsync(string dataCenter)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.Positions.Count == 0)
        {
            await RespondAsync("募集タイプを先に選択してください。", components: BuildCreatePartySizeSelectComponents(draft.DisallowDuplicateJobs), ephemeral: true);
            return;
        }

        var updatedDraft = draft with { DataCenter = dataCenter, CreatedAt = DateTimeOffset.UtcNow };
        _createDraftTracker.Set(Context.User.Id, updatedDraft);
        await ShowCreateScheduleDatePageAsync(0, $"開催DCを `{dataCenter}` に設定しました。開始日の指定を選択してください。");
    }

    private async Task ShowCreateScheduleDatePageAsync(int pageIndex, string? prompt = null)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        await RespondAsync(
            prompt ?? "開始日の指定を選択してください。",
            components: BuildCreateScheduleDateSelectComponents(pageIndex),
            ephemeral: true);
    }

    private async Task SelectCreateScheduleNowInternalAsync()
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var schedule = nowUtc
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
        _createDraftTracker.Set(Context.User.Id, draft with
        {
            Schedule = schedule,
            ScheduleAt = nowUtc,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await ShowCreateNextStepAfterScheduleUpdatedAsync(draft, schedule);
    }

    private async Task SelectCreateScheduleDayInternalAsync(int dayOffset)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        await RespondAsync(
            "時間（時）を選択してください。",
            components: BuildCreateScheduleHourSelectComponents(dayOffset),
            ephemeral: true);
    }

    private async Task SelectCreateScheduleHourInternalAsync(int dayOffset, int hour)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        await RespondAsync(
            "分を選択してください。",
            components: BuildCreateScheduleMinuteSelectComponents(dayOffset, hour),
            ephemeral: true);
    }

    private async Task SelectCreateScheduleMinuteInternalAsync(int dayOffset, int hour, int minute)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryGet(Context.User.Id, out var draft))
        {
            await RespondAsync("作成情報が見つかりませんでした。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (draft.GuildId != Context.Guild.Id || draft.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは確定できません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - draft.CreatedAt > CreateDraftTimeout)
        {
            await RespondAsync("作成情報の有効期限が切れました。もう一度募集を作成してください。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.DataCenter))
        {
            await RespondAsync("開催DCを先に選択してください。", components: BuildCreateDataCenterSelectComponents(), ephemeral: true);
            return;
        }

        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);
        var targetDate = nowJst.Date.AddDays(dayOffset);
        var scheduleJst = new DateTimeOffset(
            targetDate.Year,
            targetDate.Month,
            targetDate.Day,
            hour,
            minute,
            0,
            JapanTimeOffset);

        if (scheduleJst < nowJst.AddMinutes(-1))
        {
            var pageIndex = dayOffset / CreateSchedulePageSize;
            await RespondAsync(
                "過去の時刻は選択できません。日付を選び直してください。",
                components: BuildCreateScheduleDateSelectComponents(pageIndex),
                ephemeral: true);
            return;
        }

        var scheduleAt = scheduleJst.ToUniversalTime();
        var schedule = scheduleJst.ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
        _createDraftTracker.Set(Context.User.Id, draft with
        {
            Schedule = schedule,
            ScheduleAt = scheduleAt,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await ShowCreateNextStepAfterScheduleUpdatedAsync(draft, schedule);
    }

    private async Task ShowCreateHostSelectFromConfirmAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (!draft.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(draft.Schedule))
        {
            await RespondAsync("開始日時を先に選択してください。", components: BuildCreateScheduleDateSelectComponents(), ephemeral: true);
            return;
        }

        await RespondAsync(
            "主催ポジションを再設定してください。",
            components: BuildCreateHostSelectComponents(draft.Positions),
            ephemeral: true);
    }

    private async Task ShowCreateScheduleSelectFromConfirmAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        var current = string.IsNullOrWhiteSpace(draft.Schedule) ? "未設定" : draft.Schedule;
        await ShowCreateScheduleDatePageAsync(0, $"開始日の指定を選択してください。（現在: {current}）");
    }

    private async Task ShowCreateMentionRoleSelectFromConfirmAsync()
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var roles = ListCreateMentionRoleCandidates(Context.Guild);
        if (roles.Count == 0)
        {
            await RespondAsync(
                "選択可能なメンションロールがありません。`[コンテンツ]` を含むロールを作成してください。",
                components: BuildCreateMentionRoleSelectComponents(draft, roles),
                ephemeral: true);
            return;
        }

        var current = BuildCreateMentionRoleSummary(draft, Context.Guild);
        await RespondAsync(
            $"メンションするロールを選択してください。（現在: {current}）",
            components: BuildCreateMentionRoleSelectComponents(draft, roles),
            ephemeral: true);
    }

    private async Task UpdateCreateMentionRoleInternalAsync(IReadOnlyList<ulong> roleIds)
    {
        var draft = await GetValidCreateDraftAsync();
        if (draft is null)
        {
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var selectedRoleIds = roleIds
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        if (selectedRoleIds.Length > 0)
        {
            var candidates = ListCreateMentionRoleCandidates(Context.Guild);
            var candidateIds = candidates.Select(role => role.Id).ToHashSet();
            if (selectedRoleIds.Any(id => !candidateIds.Contains(id)))
            {
                await RespondAsync("そのロールは選択できません。", ephemeral: true);
                return;
            }
        }

        var updated = draft with
        {
            MentionRoleIds = selectedRoleIds.Length == 0 ? null : selectedRoleIds,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _createDraftTracker.Set(Context.User.Id, updated);

        var message = selectedRoleIds.Length > 0
            ? $"メンションロールを {string.Join(" / ", selectedRoleIds.Select(MentionRole))} に設定しました。"
            : "メンションロールを解除しました。";
        await ShowCreateConfirmationInternalAsync(message);
    }

    private async Task ShowCreateNextStepAfterScheduleUpdatedAsync(RecruitCreateDraftContext draft, string schedule)
    {
        if (!string.IsNullOrWhiteSpace(draft.HostPosition) && !string.IsNullOrWhiteSpace(draft.HostJob))
        {
            await ShowCreateConfirmationInternalAsync($"開始日時を `{schedule}` に更新しました。");
            return;
        }

        await RespondAsync(
            $"開始日時を `{schedule}` に設定しました。主催ポジションを選択してください。",
            components: BuildCreateHostSelectComponents(draft.Positions),
            ephemeral: true);
    }

    private async Task<RecruitPostEditContext?> GetPostEditContextAsync(bool requireDataCenter = false)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return null;
        }

        PruneInteractionContexts();
        if (!_postEditTracker.TryGet(Context.User.Id, out var context))
        {
            await RespondAsync("編集情報が見つかりませんでした。もう一度操作してください。", ephemeral: true);
            return null;
        }

        if (context.GuildId != Context.Guild.Id || context.ChannelId != channel.Id)
        {
            await RespondAsync("このチャンネルでは編集を続行できません。もう一度操作してください。", ephemeral: true);
            return null;
        }

        if (DateTimeOffset.UtcNow - context.CreatedAt > InteractionContextTimeout)
        {
            _postEditTracker.TryRemove(Context.User.Id, out _);
            await RespondAsync("編集情報の有効期限が切れました。もう一度操作してください。", ephemeral: true);
            return null;
        }

        if (requireDataCenter && string.IsNullOrWhiteSpace(context.DataCenter))
        {
            await RespondAsync(
                "開催DCを先に選択してください。",
                components: BuildPostEditDataCenterSelectComponents(context.DataCenter),
                ephemeral: true);
            return null;
        }

        return context;
    }

    private async Task SelectPostEditDataCenterAsync(string dataCenter)
    {
        var context = await GetPostEditContextAsync();
        if (context is null)
        {
            return;
        }

        var updatedContext = context with
        {
            DataCenter = dataCenter,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _postEditTracker.Set(Context.User.Id, updatedContext);
        await ShowPostEditScheduleDatePageAsync(0, $"開催DCを `{dataCenter}` に設定しました。開始日の指定を選択してください。");
    }

    private async Task ShowPostEditScheduleDatePageAsync(int pageIndex, string? prompt = null)
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        var canKeepSchedule = context.ScheduleAt.HasValue && !string.IsNullOrWhiteSpace(context.Schedule);
        await RespondAsync(
            prompt ?? $"開始日の指定を選択してください。（現在: {context.Schedule ?? "未設定"}）",
            components: BuildPostEditScheduleDateSelectComponents(pageIndex, canKeepSchedule),
            ephemeral: true);
    }

    private async Task SelectPostEditScheduleNowInternalAsync()
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var schedule = nowUtc
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);

        var updatedContext = context with
        {
            Schedule = schedule,
            ScheduleAt = nowUtc,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _postEditTracker.Set(Context.User.Id, updatedContext);
        await ApplyPostEditAsync(updatedContext);
    }

    private async Task SelectPostEditScheduleKeepInternalAsync()
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        if (!context.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(context.Schedule))
        {
            await RespondAsync(
                "現在日時が未設定のため、開始日時を選択してください。",
                components: BuildPostEditScheduleDateSelectComponents(0, canKeepSchedule: false),
                ephemeral: true);
            return;
        }

        var updatedContext = context with { CreatedAt = DateTimeOffset.UtcNow };
        _postEditTracker.Set(Context.User.Id, updatedContext);
        await ApplyPostEditAsync(updatedContext);
    }

    private async Task SelectPostEditScheduleDayInternalAsync(int dayOffset)
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        _postEditTracker.Set(Context.User.Id, context with { CreatedAt = DateTimeOffset.UtcNow });
        await RespondAsync(
            "時間（時）を選択してください。",
            components: BuildPostEditScheduleHourSelectComponents(dayOffset),
            ephemeral: true);
    }

    private async Task SelectPostEditScheduleHourInternalAsync(int dayOffset, int hour)
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        var canKeepSchedule = context.ScheduleAt.HasValue && !string.IsNullOrWhiteSpace(context.Schedule);
        _postEditTracker.Set(Context.User.Id, context with { CreatedAt = DateTimeOffset.UtcNow });
        await RespondAsync(
            "分を選択してください。",
            components: BuildPostEditScheduleMinuteSelectComponents(dayOffset, hour, canKeepSchedule),
            ephemeral: true);
    }

    private async Task SelectPostEditScheduleMinuteInternalAsync(int dayOffset, int hour, int minute)
    {
        var context = await GetPostEditContextAsync(requireDataCenter: true);
        if (context is null)
        {
            return;
        }

        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);
        var targetDate = nowJst.Date.AddDays(dayOffset);
        var scheduleJst = new DateTimeOffset(
            targetDate.Year,
            targetDate.Month,
            targetDate.Day,
            hour,
            minute,
            0,
            JapanTimeOffset);

        var canKeepSchedule = context.ScheduleAt.HasValue && !string.IsNullOrWhiteSpace(context.Schedule);
        if (scheduleJst < nowJst.AddMinutes(-1))
        {
            var pageIndex = dayOffset / CreateSchedulePageSize;
            await RespondAsync(
                "過去の時刻は選択できません。日付を選び直してください。",
                components: BuildPostEditScheduleDateSelectComponents(pageIndex, canKeepSchedule),
                ephemeral: true);
            return;
        }

        var scheduleAt = scheduleJst.ToUniversalTime();
        var schedule = scheduleJst.ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
        var updatedContext = context with
        {
            Schedule = schedule,
            ScheduleAt = scheduleAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _postEditTracker.Set(Context.User.Id, updatedContext);
        await ApplyPostEditAsync(updatedContext);
    }

    private async Task ApplyPostEditAsync(RecruitPostEditContext context)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var title = context.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("募集タイトルが未設定です。もう一度募集内容編集をやり直してください。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitDataCenter(context.DataCenter, out var dataCenter, out var dataCenterError))
        {
            await RespondAsync(
                dataCenterError ?? "開催DCの選択が不正です。もう一度選択してください。",
                components: BuildPostEditDataCenterSelectComponents(context.DataCenter),
                ephemeral: true);
            return;
        }

        if (!context.ScheduleAt.HasValue || string.IsNullOrWhiteSpace(context.Schedule))
        {
            await RespondAsync(
                "開始日時を選択してください。",
                components: BuildPostEditScheduleDateSelectComponents(0, canKeepSchedule: false),
                ephemeral: true);
            return;
        }

        var schedule = context.Schedule.Trim();
        var note = context.Note?.Trim();

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
        if (post is null)
        {
            _postEditTracker.TryRemove(Context.User.Id, out _);
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.CreatorUserId != ToDbId(Context.User.Id))
        {
            _postEditTracker.TryRemove(Context.User.Id, out _);
            await FollowupAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            _postEditTracker.TryRemove(Context.User.Id, out _);
            await FollowupAsync("終了または中断済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var previousTitle = post.Title ?? string.Empty;
        var previousDataCenter = post.DataCenter;
        var previousSchedule = post.ScheduleText;
        var previousNote = post.Note;

        var updated = await _db.UpdateRecruitPostDetailsAsync(post.Id, title, dataCenter, schedule, context.ScheduleAt, note);
        if (!updated)
        {
            await FollowupAsync("募集の更新に失敗しました。", ephemeral: true);
            return;
        }

        _postEditTracker.TryRemove(Context.User.Id, out _);

        post.Title = title;
        post.DataCenter = dataCenter;
        post.ScheduleText = schedule;
        post.ScheduleAt = context.ScheduleAt;
        post.Note = note;

        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, "募集内容を更新しました。");
        await UpdateRecruitListAsync(Context.Guild, channel);
        var editDetails = BuildRecruitPostEditDetail(
            previousTitle,
            title,
            previousDataCenter,
            dataCenter,
            previousSchedule,
            schedule,
            previousNote,
            note);
        await SendRecruitAuditAsync(post, "募集管理: 募集内容編集", editDetails);
        await FollowupAsync("募集内容を更新しました。", ephemeral: true);
    }

    private async Task<string?> TryCreateRecruitPostAsync(ITextChannel channel, RecruitCreateDraftContext draft, string hostPosition, string? hostJob)
    {
        var creatorName = Context.User.GlobalName ?? Context.User.Username;
        var positions = draft.Positions;
        var preFilledCount = draft.PreFilledSlots.Count + draft.PreFilledFlexSlots.Count + draft.PreFilledMembers.Count;
        var hostWillBeAssigned = !string.IsNullOrWhiteSpace(hostJob) && !TryParseFlexPositionToken(hostPosition, out _);
        var initialFilledCount = preFilledCount + (hostWillBeAssigned ? 1 : 0);
        var initialStatus = initialFilledCount >= positions.Count
            ? RecruitPostStatus.Full
            : RecruitPostStatus.Open;

        var postDraft = new RecruitPost
        {
            Status = initialStatus,
            Title = draft.Title,
            DataCenter = draft.DataCenter,
            ScheduleText = draft.Schedule,
            ScheduleAt = draft.ScheduleAt,
            Note = draft.Note,
            DisallowDuplicateJobs = draft.DisallowDuplicateJobs,
            HostFlexSpec = draft.HostFlexSpec,
            HostFlexJobsSpec = draft.HostFlexJobsSpec,
            CreatorUserId = ToDbId(Context.User.Id),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var slots = positions
            .Select(position => new RecruitSlot
            {
                Position = position,
                RequiredJob = draft.RequiredJobs.TryGetValue(position, out var requiredJob) ? requiredJob : null
            })
            .ToList();

        foreach (var pair in draft.PreFilledSlots)
        {
            var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            target.IsClosed = true;
            target.Job = pair.Value;
            target.Comment = null;
        }

        foreach (var pair in draft.PreFilledMembers)
        {
            var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            target.IsClosed = false;
            target.UserId = ToDbId(pair.Value.UserId);
            target.UserName = pair.Value.UserName;
            target.FlexSpec = pair.Value.FlexSpec;
            target.FlexJobsSpec = pair.Value.FlexJobsSpec;
            target.Job = pair.Value.Job;
            target.Comment = null;
        }

        foreach (var pair in draft.PreFilledFlexSlots)
        {
            var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            target.IsClosed = true;
            target.UserId = null;
            target.UserName = null;
            target.FlexSpec = pair.Value.FlexSpec;
            target.FlexJobsSpec = pair.Value.FlexJobsSpec;
            target.Job = pair.Value.Job;
            target.Comment = null;
        }

        var hostSlot = slots.FirstOrDefault(slot => string.Equals(slot.Position, hostPosition, StringComparison.OrdinalIgnoreCase));
        if (hostWillBeAssigned && hostSlot is not null)
        {
            hostSlot.IsClosed = false;
            hostSlot.UserId = ToDbId(Context.User.Id);
            hostSlot.UserName = creatorName;
            hostSlot.Job = hostJob;
            hostSlot.Comment = null;
        }

        var embed = BuildRecruitEmbed(postDraft, slots, draft.DisallowDuplicateJobs);
        IUserMessage? posted = null;
        try
        {
            var mentionRoleIds = Context.Guild is null
                ? Array.Empty<ulong>()
                : (draft.MentionRoleIds ?? Array.Empty<ulong>())
                    .Where(id => id != 0 && Context.Guild.GetRole(id) is not null)
                    .Distinct()
                    .ToArray();
            var mentionText = mentionRoleIds.Length == 0
                ? null
                : string.Join(' ', mentionRoleIds.Select(MentionRole));
            var allowedMentions = mentionRoleIds.Length == 0
                ? AllowedMentions.None
                : new AllowedMentions { RoleIds = mentionRoleIds.ToList() };

            posted = await channel.SendMessageAsync(
                text: mentionText,
                embed: embed,
                components: RecruitmentPanel.BuildRecruitComponents(initialStatus, positions),
                allowedMentions: allowedMentions);
            var created = await _db.CreateRecruitPostAsync(
                Context.Guild!.Id,
                channel.Id,
                posted.Id,
                Context.User.Id,
                draft.Title,
                draft.DataCenter,
                draft.Schedule,
                draft.ScheduleAt,
                draft.Note,
                draft.DisallowDuplicateJobs,
                draft.HostFlexSpec,
                draft.HostFlexJobsSpec,
                positions,
                draft.RequiredJobs,
                draft.PreFilledSlots,
                RecruitPostStatus.Open);
            if (hostWillBeAssigned)
            {
                var hostAssignStatus = await _db.AssignRecruitSlotAsync(created.Id, hostPosition, Context.User.Id, creatorName, hostJob!, string.Empty, draft.DisallowDuplicateJobs);
                if (hostAssignStatus != RecruitAssignStatus.Success)
                {
                    throw new InvalidOperationException($"Failed to assign host slot. status={hostAssignStatus}");
                }
            }

            foreach (var pair in draft.PreFilledMembers)
            {
                var memberAssignStatus = await _db.AssignRecruitSlotAsync(
                    created.Id,
                    pair.Key,
                    pair.Value.UserId,
                    pair.Value.UserName,
                    pair.Value.Job,
                    string.Empty,
                    draft.DisallowDuplicateJobs,
                    pair.Value.FlexSpec,
                    pair.Value.FlexJobsSpec);
                if (memberAssignStatus != RecruitAssignStatus.Success)
                {
                    throw new InvalidOperationException($"Failed to assign prefilled member slot. position={pair.Key}, status={memberAssignStatus}");
                }
            }

            foreach (var pair in draft.PreFilledFlexSlots)
            {
                var marked = await _db.MarkRecruitSlotClosedFlexAsync(
                    created.Id,
                    pair.Key,
                    pair.Value.Job,
                    pair.Value.FlexSpec,
                    pair.Value.FlexJobsSpec);
                if (!marked)
                {
                    throw new InvalidOperationException($"Failed to mark external flex prefilled slot. position={pair.Key}");
                }
            }

            if (initialStatus == RecruitPostStatus.Full)
            {
                await _db.UpdateRecruitPostStatusAsync(created.Id, RecruitPostStatus.Full);
                created.Status = RecruitPostStatus.Full;
            }

            var threadChannel = await EnsureRecruitThreadAsync(created, channel, posted);
            var initialParticipantUserIds = new HashSet<ulong>();
            if (hostWillBeAssigned)
            {
                initialParticipantUserIds.Add(Context.User.Id);
            }
            foreach (var pair in draft.PreFilledMembers)
            {
                initialParticipantUserIds.Add(pair.Value.UserId);
            }
            foreach (var participantUserId in initialParticipantUserIds)
            {
                await EnsureRecruitThreadMemberAsync(created, participantUserId, threadChannel);
            }
            if (hostWillBeAssigned)
            {
                var createdHostSlot = await _db.GetRecruitSlotByUserAsync(created.Id, Context.User.Id);
                if (createdHostSlot is not null)
                {
                    await UpsertRecruitParticipantEmbedAsync(created, Context.User, createdHostSlot, mentionHost: false);
                }
            }
            await UpdateRecruitListAsync(Context.Guild, channel);
            await SendRecruitAuditAsync(created, "募集管理: 状態変更", "募集を開始");
            return BuildRecruitMessageLink(Context.Guild!.Id, channel.Id, posted.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create recruit post.");
            if (posted is not null)
            {
                try
                {
                    await posted.DeleteAsync();
                }
                catch
                {
                }
            }

            await FollowupAsync("募集の作成に失敗しました。時間をおいて再試行してください。", ephemeral: true);
            return null;
        }
    }

    private static bool ValidateCreatePreFilledSlots(RecruitCreateDraftContext draft, ulong hostUserId, out string? error)
    {
        error = null;
        var positions = new HashSet<string>(draft.Positions, StringComparer.OrdinalIgnoreCase);
        var normalizedHostPosition = draft.HostPosition?.Trim().ToUpperInvariant();
        var hostFlexiblePositionsForPrefill = string.IsNullOrWhiteSpace(draft.HostFlexSpec)
            ? Array.Empty<string>()
            : ResolveFlexCandidatePositions(draft.HostFlexSpec, draft.Positions)
                .Select(position => position.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var hostCanUseFlexForPrefill =
            !string.IsNullOrWhiteSpace(normalizedHostPosition) &&
            hostFlexiblePositionsForPrefill.Length >= 2 &&
            hostFlexiblePositionsForPrefill.Contains(normalizedHostPosition, StringComparer.OrdinalIgnoreCase);
        var usedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedUsers = new HashSet<ulong> { hostUserId };
        if (draft.DisallowDuplicateJobs &&
            ShouldReserveHostJobForDuplicateCheck(draft) &&
            !string.IsNullOrWhiteSpace(draft.HostJob))
        {
            usedJobs.Add(draft.HostJob);
        }

        foreach (var pair in draft.PreFilledSlots)
        {
            var position = pair.Key.Trim().ToUpperInvariant();
            var job = pair.Value.Trim().ToUpperInvariant();
            if (!positions.Contains(position))
            {
                error = $"{position} は今回の募集に含まれていません。";
                return false;
            }

            if (string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !hostCanUseFlexForPrefill)
            {
                error = "主催枠は事前埋まりに設定できません。";
                return false;
            }

            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs) ||
                !jobs.Any(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"{position} の事前埋まりジョブ `{job}` が不正です。";
                return false;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !string.Equals(requiredJob, job, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{position} は指定ジョブ `{requiredJob}` のみ事前埋まりに設定できます。";
                return false;
            }

            if (draft.DisallowDuplicateJobs && !usedJobs.Add(job))
            {
                error = $"ジョブ `{job}` が重複しています。";
                return false;
            }
        }

        foreach (var pair in draft.PreFilledFlexSlots)
        {
            var position = pair.Key.Trim().ToUpperInvariant();
            var flexSlot = pair.Value;
            var job = flexSlot.Job.Trim().ToUpperInvariant();
            var flexJobs = ParseFlexJobCodes(flexSlot.FlexJobsSpec);
            if (flexJobs.Count == 0 && !string.IsNullOrWhiteSpace(job))
            {
                flexJobs = new[] { job };
            }
            else if (!flexJobs.Contains(job, StringComparer.OrdinalIgnoreCase))
            {
                flexJobs = flexJobs
                    .Append(job)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            var flexPositions = string.IsNullOrWhiteSpace(flexSlot.FlexSpec)
                ? new[] { position }
                : ResolveFlexCandidatePositions(flexSlot.FlexSpec, draft.Positions)
                    .Select(candidate => candidate.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            var canOverlapHostPosition =
                hostCanUseFlexForPrefill &&
                !string.IsNullOrWhiteSpace(normalizedHostPosition) &&
                flexPositions.Contains(normalizedHostPosition, StringComparer.OrdinalIgnoreCase) &&
                flexPositions.Any(candidate =>
                    !string.Equals(candidate, normalizedHostPosition, StringComparison.OrdinalIgnoreCase));

            if (draft.PreFilledSlots.ContainsKey(position))
            {
                error = $"{position} の事前埋まり設定が重複しています。";
                return false;
            }

            if (draft.PreFilledMembers.ContainsKey(position))
            {
                error = $"{position} の事前埋まり設定が重複しています。";
                return false;
            }

            if (!positions.Contains(position))
            {
                error = $"{position} は今回の募集に含まれていません。";
                return false;
            }

            if (string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !canOverlapHostPosition)
            {
                error = "主催枠は事前埋まりに設定できません。";
                return false;
            }

            if (flexPositions.Length < 2 || !flexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
            {
                error = $"{position} のFLEX設定は2枠以上必要です。";
                return false;
            }

            var usableFlexCandidatePositions = new List<string>();
            foreach (var candidatePosition in flexPositions)
            {
                if (!positions.Contains(candidatePosition))
                {
                    error = $"{candidatePosition} は今回の募集に含まれていません。";
                    return false;
                }

                if (string.Equals(candidatePosition, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                    !canOverlapHostPosition)
                {
                    continue;
                }

                if (draft.PreFilledSlots.ContainsKey(candidatePosition))
                {
                    if (string.Equals(candidatePosition, position, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"{candidatePosition} は別の事前埋まり設定で使用中です。";
                        return false;
                    }

                    continue;
                }

                var usedByMember = draft.PreFilledMembers.Any(memberPair =>
                {
                    var memberPosition = memberPair.Key.Trim().ToUpperInvariant();
                    if (string.Equals(memberPosition, position, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var memberPositions = string.IsNullOrWhiteSpace(memberPair.Value.FlexSpec)
                        ? new[] { memberPosition }
                        : ResolveFlexCandidatePositions(memberPair.Value.FlexSpec, draft.Positions)
                            .Select(item => item.Trim().ToUpperInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    var memberIsFlexible = !string.IsNullOrWhiteSpace(memberPair.Value.FlexSpec) &&
                                           memberPositions.Length >= 2;
                    return !memberIsFlexible &&
                           memberPositions.Contains(candidatePosition, StringComparer.OrdinalIgnoreCase);
                });
                if (usedByMember)
                {
                    if (string.Equals(candidatePosition, position, StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"{candidatePosition} は別の事前埋まりメンバー設定で使用中です。";
                        return false;
                    }

                    continue;
                }

                if (!RecruitmentRules.TryGetJobsForRecruit(candidatePosition, draft.Positions, out var jobs) ||
                    !flexJobs.Any(flexJob => jobs.Any(item => string.Equals(item.Code, flexJob, StringComparison.OrdinalIgnoreCase))))
                {
                    error = $"{candidatePosition} の事前埋まりジョブ `{string.Join('/', flexJobs)}` が不正です。";
                    return false;
                }

                if (draft.RequiredJobs.TryGetValue(candidatePosition, out var requiredJobByCandidate) &&
                    !string.IsNullOrWhiteSpace(requiredJobByCandidate) &&
                    !flexJobs.Contains(requiredJobByCandidate, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"{candidatePosition} は指定ジョブ `{requiredJobByCandidate}` のみ事前埋まりに設定できます。";
                    return false;
                }

                usableFlexCandidatePositions.Add(candidatePosition);
            }

            if (usableFlexCandidatePositions.Count == 0)
            {
                error = $"{position} のFLEX候補が利用できません。";
                return false;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !flexJobs.Contains(requiredJob, StringComparer.OrdinalIgnoreCase))
            {
                error = $"{position} は指定ジョブ `{requiredJob}` のみ事前埋まりに設定できます。";
                return false;
            }

            if (draft.DisallowDuplicateJobs &&
                ShouldReserveFlexibleJobForDuplicateCheck(string.Join(',', usableFlexCandidatePositions), flexJobs, draft.Positions))
            {
                var availableJobs = flexJobs
                    .Where(candidateJob => !usedJobs.Contains(candidateJob))
                    .ToArray();
                if (availableJobs.Length == 0)
                {
                    error = $"ジョブ `{string.Join('/', flexJobs)}` が重複しています。";
                    return false;
                }

                usedJobs.Add(availableJobs[0]);
            }
        }

        foreach (var pair in draft.PreFilledMembers)
        {
            var position = pair.Key.Trim().ToUpperInvariant();
            var member = pair.Value;
            var job = member.Job.Trim().ToUpperInvariant();
            var memberJobs = ParseFlexJobCodes(member.FlexJobsSpec);
            if (memberJobs.Count == 0 && !string.IsNullOrWhiteSpace(job))
            {
                memberJobs = new[] { job };
            }
            else if (!memberJobs.Contains(job, StringComparer.OrdinalIgnoreCase))
            {
                memberJobs = memberJobs
                    .Append(job)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            var memberFlexPositions = string.IsNullOrWhiteSpace(member.FlexSpec)
                ? new[] { position }
                : ResolveFlexCandidatePositions(member.FlexSpec, draft.Positions)
                    .Select(candidate => candidate.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            var canOverlapHostPosition =
                hostCanUseFlexForPrefill &&
                !string.IsNullOrWhiteSpace(normalizedHostPosition) &&
                memberFlexPositions.Contains(normalizedHostPosition, StringComparer.OrdinalIgnoreCase) &&
                memberFlexPositions.Any(candidate =>
                    !string.Equals(candidate, normalizedHostPosition, StringComparison.OrdinalIgnoreCase));

            if (draft.PreFilledSlots.ContainsKey(position))
            {
                error = $"{position} の事前埋まり設定が重複しています。";
                return false;
            }

            if (draft.PreFilledFlexSlots.ContainsKey(position))
            {
                error = $"{position} の事前埋まり設定が重複しています。";
                return false;
            }

            if (!positions.Contains(position))
            {
                error = $"{position} は今回の募集に含まれていません。";
                return false;
            }

            if (string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !canOverlapHostPosition)
            {
                error = "主催枠は事前埋まりに設定できません。";
                return false;
            }

            if (memberFlexPositions.Length == 0 || !memberFlexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
            {
                error = $"{position} のFLEX設定が不正です。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(member.FlexSpec) && memberFlexPositions.Length < 2)
            {
                error = $"{position} のFLEX設定は2枠以上必要です。";
                return false;
            }

            var usableMemberCandidatePositions = new List<string>();
            foreach (var candidatePosition in memberFlexPositions)
            {
                if (!positions.Contains(candidatePosition))
                {
                    error = $"{candidatePosition} は今回の募集に含まれていません。";
                    return false;
                }

                if (string.Equals(candidatePosition, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                    !canOverlapHostPosition)
                {
                    continue;
                }

                if (draft.PreFilledSlots.ContainsKey(candidatePosition) &&
                    !string.Equals(candidatePosition, position, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (draft.PreFilledMembers.TryGetValue(candidatePosition, out var occupiedMember) &&
                    !string.Equals(candidatePosition, position, StringComparison.OrdinalIgnoreCase))
                {
                    var occupiedPositions = string.IsNullOrWhiteSpace(occupiedMember.FlexSpec)
                        ? new[] { candidatePosition }
                        : ResolveFlexCandidatePositions(occupiedMember.FlexSpec, draft.Positions)
                            .Select(item => item.Trim().ToUpperInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    var occupiedIsFlexible = !string.IsNullOrWhiteSpace(occupiedMember.FlexSpec) &&
                                             occupiedPositions.Length >= 2;
                    if (!occupiedIsFlexible)
                    {
                        continue;
                    }
                }

                if (!RecruitmentRules.TryGetJobsForRecruit(candidatePosition, draft.Positions, out var jobs) ||
                    !memberJobs.Any(memberJob => jobs.Any(item => string.Equals(item.Code, memberJob, StringComparison.OrdinalIgnoreCase))))
                {
                    error = $"{candidatePosition} の事前埋まりジョブ `{string.Join('/', memberJobs)}` が不正です。";
                    return false;
                }

                if (draft.RequiredJobs.TryGetValue(candidatePosition, out var requiredJobByCandidate) &&
                    !string.IsNullOrWhiteSpace(requiredJobByCandidate) &&
                    !memberJobs.Contains(requiredJobByCandidate, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"{candidatePosition} は指定ジョブ `{requiredJobByCandidate}` のみ事前埋まりに設定できます。";
                    return false;
                }

                usableMemberCandidatePositions.Add(candidatePosition);
            }

            if (usableMemberCandidatePositions.Count == 0)
            {
                error = $"{position} のFLEX候補が利用できません。";
                return false;
            }

            if (draft.RequiredJobs.TryGetValue(position, out var requiredJob) &&
                !string.IsNullOrWhiteSpace(requiredJob) &&
                !memberJobs.Contains(requiredJob, StringComparer.OrdinalIgnoreCase))
            {
                error = $"{position} は指定ジョブ `{requiredJob}` のみ事前埋まりに設定できます。";
                return false;
            }

            if (!usedUsers.Add(member.UserId))
            {
                error = $"同じメンバーを複数ポジションに設定できません。";
                return false;
            }

            if (draft.DisallowDuplicateJobs &&
                ShouldReserveFlexibleJobForDuplicateCheck(string.Join(',', usableMemberCandidatePositions), memberJobs, draft.Positions))
            {
                var availableJobs = memberJobs
                    .Where(candidateJob => !usedJobs.Contains(candidateJob))
                    .ToArray();
                if (availableJobs.Length == 0)
                {
                    error = $"ジョブ `{string.Join('/', memberJobs)}` が重複しています。";
                    return false;
                }

                usedJobs.Add(availableJobs[0]);
            }
        }

        return true;
    }

    private static MessageComponent BuildCreateHostSelectComponents(IReadOnlyList<string> positions)
    {
        var positionSet = new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);
        return new ComponentBuilder()
            .WithButton("MT", "recruit_create_host_MT", ButtonStyle.Primary, disabled: !positionSet.Contains("MT"), row: 0)
            .WithButton("ST", "recruit_create_host_ST", ButtonStyle.Primary, disabled: !positionSet.Contains("ST"), row: 0)
            .WithButton("H1", "recruit_create_host_H1", ButtonStyle.Success, disabled: !positionSet.Contains("H1"), row: 0)
            .WithButton("H2", "recruit_create_host_H2", ButtonStyle.Success, disabled: !positionSet.Contains("H2"), row: 0)
            .WithButton("D1", "recruit_create_host_D1", ButtonStyle.Danger, disabled: !positionSet.Contains("D1"), row: 1)
            .WithButton("D2", "recruit_create_host_D2", ButtonStyle.Danger, disabled: !positionSet.Contains("D2"), row: 1)
            .WithButton("D3", "recruit_create_host_D3", ButtonStyle.Danger, disabled: !positionSet.Contains("D3"), row: 1)
            .WithButton("D4", "recruit_create_host_D4", ButtonStyle.Danger, disabled: !positionSet.Contains("D4"), row: 1)
            .WithButton("FLEX", "recruit_create_host_flex_any", ButtonStyle.Primary, disabled: positionSet.Count == 0, row: 2)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private static MessageComponent BuildCreatePartySizeSelectComponents(bool disallowDuplicateJobs)
    {
        const ButtonStyle onStyle = ButtonStyle.Danger;
        const ButtonStyle offStyle = ButtonStyle.Success;
        return new ComponentBuilder()
            .WithButton("FULL PARTY", "recruit_create_party_full", ButtonStyle.Primary, row: 0)
            .WithButton("LIGHT PARTY", "recruit_create_party_light", ButtonStyle.Success, row: 0)
            .WithButton("ジョブ重複: 禁止", "recruit_create_dup_on", onStyle, row: 1)
            .WithButton("ジョブ重複: 許可", "recruit_create_dup_off", offStyle, row: 1)
            .WithButton(disallowDuplicateJobs ? "現在: 禁止" : "現在: 許可", "recruit_create_dup_state", ButtonStyle.Secondary, disabled: true, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildCreateDataCenterSelectComponents()
    {
        return new ComponentBuilder()
            .WithButton("Meteor", "recruit_create_dc_meteor", ButtonStyle.Primary, row: 0)
            .WithButton("Gaia", "recruit_create_dc_gaia", ButtonStyle.Success, row: 0)
            .WithButton("Mana", "recruit_create_dc_mana", ButtonStyle.Danger, row: 0)
            .WithButton("Elemental", "recruit_create_dc_elemental", ButtonStyle.Secondary, row: 0)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildCreateScheduleDateSelectComponents(int pageIndex = 0)
    {
        var maxPage = CreateScheduleMaxDayOffset / CreateSchedulePageSize;
        var clampedPage = Math.Clamp(pageIndex, 0, maxPage);
        var startOffset = clampedPage * CreateSchedulePageSize;
        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);
        var builder = new ComponentBuilder()
            .WithButton("今", "recruit_create_schedule_now", ButtonStyle.Success, row: 0);

        for (var i = 0; i < CreateSchedulePageSize; i++)
        {
            var dayOffset = startOffset + i;
            if (dayOffset > CreateScheduleMaxDayOffset)
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

            var row = i < 4 ? 0 : 1;
            builder.WithButton(
                label,
                $"recruit_create_schedule_day_pick:{dayOffset}",
                ButtonStyle.Primary,
                row: row);
        }

        builder
            .WithButton("前へ", $"recruit_create_schedule_page:{clampedPage - 1}", ButtonStyle.Secondary, disabled: clampedPage <= 0, row: 2)
            .WithButton("次へ", $"recruit_create_schedule_page:{clampedPage + 1}", ButtonStyle.Secondary, disabled: clampedPage >= maxPage, row: 2)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2);

        return builder.Build();
    }

    private static MessageComponent BuildCreateScheduleHourSelectComponents(int dayOffset)
    {
        var backPage = dayOffset / CreateSchedulePageSize;
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_create_schedule_hour_select")
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
            .WithButton("日付に戻る", $"recruit_create_schedule_page:{backPage}", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .WithButton("20:00", $"recruit_create_schedule_quick:{dayOffset}|20|0", ButtonStyle.Primary, row: 2)
            .WithButton("21:00", $"recruit_create_schedule_quick:{dayOffset}|21|0", ButtonStyle.Primary, row: 2)
            .WithButton("22:00", $"recruit_create_schedule_quick:{dayOffset}|22|0", ButtonStyle.Primary, row: 2)
            .WithButton("23:00", $"recruit_create_schedule_quick:{dayOffset}|23|0", ButtonStyle.Primary, row: 2)
            .Build();
    }

    private static MessageComponent BuildCreateScheduleMinuteSelectComponents(int dayOffset, int hour)
    {
        var backPage = dayOffset / CreateSchedulePageSize;
        var backCustomId = $"recruit_create_schedule_page:{backPage}";
        var backToHourCustomId = $"recruit_create_schedule_day_pick:{dayOffset}";
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_create_schedule_minute_select")
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
            .WithButton("時間に戻る", backToHourCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("日付に戻る", backCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildPostEditDataCenterSelectComponents(string? currentDataCenter)
    {
        var hasCurrent = !string.IsNullOrWhiteSpace(currentDataCenter);
        return new ComponentBuilder()
            .WithButton("Meteor", "recruit_post_edit_dc_meteor", ButtonStyle.Primary, row: 0)
            .WithButton("Gaia", "recruit_post_edit_dc_gaia", ButtonStyle.Success, row: 0)
            .WithButton("Mana", "recruit_post_edit_dc_mana", ButtonStyle.Danger, row: 0)
            .WithButton("Elemental", "recruit_post_edit_dc_elemental", ButtonStyle.Secondary, row: 0)
            .WithButton("現在のDCを使用", "recruit_post_edit_dc_keep", ButtonStyle.Success, disabled: !hasCurrent, row: 1)
            .WithButton("中止", "recruit_post_edit_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildPostEditScheduleDateSelectComponents(int pageIndex = 0, bool canKeepSchedule = true)
    {
        var maxPage = CreateScheduleMaxDayOffset / CreateSchedulePageSize;
        var clampedPage = Math.Clamp(pageIndex, 0, maxPage);
        var startOffset = clampedPage * CreateSchedulePageSize;
        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);
        var builder = new ComponentBuilder()
            .WithButton("今", "recruit_post_edit_schedule_now", ButtonStyle.Success, row: 0);

        for (var i = 0; i < CreateSchedulePageSize; i++)
        {
            var dayOffset = startOffset + i;
            if (dayOffset > CreateScheduleMaxDayOffset)
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

            var row = i < 4 ? 0 : 1;
            builder.WithButton(
                label,
                $"recruit_post_edit_schedule_day_pick:{dayOffset}",
                ButtonStyle.Primary,
                row: row);
        }

        builder
            .WithButton("前へ", $"recruit_post_edit_schedule_page:{clampedPage - 1}", ButtonStyle.Secondary, disabled: clampedPage <= 0, row: 2)
            .WithButton("次へ", $"recruit_post_edit_schedule_page:{clampedPage + 1}", ButtonStyle.Secondary, disabled: clampedPage >= maxPage, row: 2)
            .WithButton("現在日時のまま", "recruit_post_edit_schedule_keep", ButtonStyle.Primary, disabled: !canKeepSchedule, row: 2)
            .WithButton("中止", "recruit_post_edit_cancel", ButtonStyle.Secondary, row: 2);

        return builder.Build();
    }

    private static MessageComponent BuildPostEditScheduleHourSelectComponents(int dayOffset)
    {
        var backPage = dayOffset / CreateSchedulePageSize;
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_post_edit_schedule_hour_select")
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
            .WithButton("日付に戻る", $"recruit_post_edit_schedule_page:{backPage}", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_post_edit_cancel", ButtonStyle.Secondary, row: 1)
            .WithButton("20:00", $"recruit_post_edit_schedule_quick:{dayOffset}|20|0", ButtonStyle.Primary, row: 2)
            .WithButton("21:00", $"recruit_post_edit_schedule_quick:{dayOffset}|21|0", ButtonStyle.Primary, row: 2)
            .WithButton("22:00", $"recruit_post_edit_schedule_quick:{dayOffset}|22|0", ButtonStyle.Primary, row: 2)
            .WithButton("23:00", $"recruit_post_edit_schedule_quick:{dayOffset}|23|0", ButtonStyle.Primary, row: 2)
            .Build();
    }

    private static MessageComponent BuildPostEditScheduleMinuteSelectComponents(int dayOffset, int hour, bool canKeepSchedule)
    {
        var backPage = dayOffset / CreateSchedulePageSize;
        var backCustomId = $"recruit_post_edit_schedule_page:{backPage}";
        var backToHourCustomId = $"recruit_post_edit_schedule_day_pick:{dayOffset}";
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_post_edit_schedule_minute_select")
            .WithPlaceholder($"分を選択（{hour:00}時）")
            .WithMinValues(1)
            .WithMaxValues(1)
            .AddOption("00", $"{dayOffset}|{hour}|0")
            .AddOption("10", $"{dayOffset}|{hour}|10")
            .AddOption("20", $"{dayOffset}|{hour}|20")
            .AddOption("30", $"{dayOffset}|{hour}|30")
            .AddOption("40", $"{dayOffset}|{hour}|40")
            .AddOption("50", $"{dayOffset}|{hour}|50");

        var builder = new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("時間に戻る", backToHourCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("日付に戻る", backCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_post_edit_cancel", ButtonStyle.Secondary, row: 1);

        if (canKeepSchedule)
        {
            builder.WithButton("現在日時のまま", "recruit_post_edit_schedule_keep", ButtonStyle.Primary, row: 1);
        }

        return builder.Build();
    }

    private static MessageComponent BuildCreateHostJobSelectComponents(string hostPosition, IReadOnlyList<JobOption> jobs)
    {
        if (TryParseFlexPositionToken(hostPosition, out var flexSpec))
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"recruit_create_host_flex_job_select:{flexSpec}")
                .WithPlaceholder("主催ジョブを選択（複数可）")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(jobs.Count, 25));
            foreach (var job in jobs.Take(25))
            {
                menu.AddOption(job.Code, job.Code);
            }

            return new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("主催選択に戻る", "recruit_create_host_flex_back", ButtonStyle.Secondary, row: 1)
                .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
                .Build();
        }

        var buttonStyle = GetHostSelectorButtonStyle(hostPosition);
        var builder = new ComponentBuilder();
        const int maxVisibleJobs = 24;
        var visibleJobs = jobs.Take(maxVisibleJobs).ToArray();
        var index = 0;
        foreach (var job in visibleJobs)
        {
            var row = index / 5;
            builder.WithButton(
                job.Code,
                $"recruit_create_host_job_pick:{hostPosition}|{job.Code}",
                buttonStyle,
                row: row);
            index++;
        }

        var actionRow = Math.Clamp(visibleJobs.Length / 5, 1, 4);
        builder.WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: actionRow);
        return builder.Build();
    }

    private static MessageComponent BuildCreateConfirmComponents(SocketGuild? guild, RecruitCreateDraftContext draft)
    {
        var hasMentionRoleCandidates = guild is not null && ListCreateMentionRoleCandidates(guild).Count > 0;
        var mentionSelected = draft.MentionRoleIds is { Count: > 0 };
        var mentionLabel = mentionSelected ? "メンションロール*" : "メンションロール";

        return new ComponentBuilder()
            .WithButton("投稿する", "recruit_create_confirm_post", ButtonStyle.Success, row: 0)
            .WithButton("ジョブ指定", "recruit_create_confirm_edit_jobs", ButtonStyle.Primary, row: 0)
            .WithButton("事前埋まり", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 0)
            .WithButton("開始再設定", "recruit_create_confirm_edit_schedule", ButtonStyle.Secondary, row: 1)
            .WithButton("主催再設定", "recruit_create_confirm_edit_host", ButtonStyle.Secondary, row: 1)
            .WithButton(mentionLabel, "recruit_create_confirm_edit_mention_role", ButtonStyle.Secondary, disabled: !hasMentionRoleCandidates, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private static MessageComponent BuildCreateRequiredPositionSelectComponents(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> requiredJobs)
    {
        var builder = new ComponentBuilder();
        var index = 0;
        foreach (var position in positions)
        {
            var hasRequired = requiredJobs.TryGetValue(position, out var required) && !string.IsNullOrWhiteSpace(required);
            var label = hasRequired ? $"{position}*" : position;
            var row = index < 4 ? 0 : 1;
            builder.WithButton(label, $"recruit_create_required_pos:{position}", GetPositionButtonStyle(position), row: row);
            index++;
        }

        builder
            .WithButton("確認へ戻る", "recruit_create_required_back", ButtonStyle.Secondary, row: 2)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2);
        return builder.Build();
    }

    private static MessageComponent BuildCreateRequiredJobSelectComponents(
        string position,
        IReadOnlyList<JobOption> jobs,
        string? currentRequiredJob,
        string? hostJob)
    {
        var builder = new ComponentBuilder();

        var index = 0;
        foreach (var job in jobs)
        {
            var selected = !string.IsNullOrWhiteSpace(currentRequiredJob) &&
                string.Equals(currentRequiredJob, job.Code, StringComparison.OrdinalIgnoreCase);
            var restricted = !string.IsNullOrWhiteSpace(hostJob) &&
                !string.Equals(hostJob, job.Code, StringComparison.OrdinalIgnoreCase);
            var row = index / 5;
            builder.WithButton(
                job.Code,
                $"recruit_create_required_job:{position}|{job.Code}",
                selected ? ButtonStyle.Primary : ButtonStyle.Secondary,
                disabled: restricted,
                row: row);
            index++;
        }

        builder
            .WithButton("指定解除", $"recruit_create_required_job:{position}|NONE", ButtonStyle.Danger, disabled: string.IsNullOrWhiteSpace(currentRequiredJob), row: 3)
            .WithButton("戻る", "recruit_create_confirm_edit_jobs", ButtonStyle.Secondary, row: 3)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 3);

        return builder.Build();
    }

    private static MessageComponent BuildCreatePrefillPositionSelectComponents(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        string? hostPosition,
        string? hostFlexSpec)
    {
        var builder = new ComponentBuilder();
        var hostAnchor = hostPosition?.Trim().ToUpperInvariant();
        var hostCanOverlap = CanIncludeHostPositionInCreatePrefillSelector(
            string.IsNullOrWhiteSpace(hostAnchor) ? Array.Empty<string>() : new[] { hostAnchor },
            hostPosition,
            hostFlexSpec,
            positions);
        var editable = positions.ToList();
        var selectableForPrefill = positions
            .Where(position =>
                !string.Equals(position, hostAnchor, StringComparison.OrdinalIgnoreCase) ||
                hostCanOverlap)
            .ToList();

        var index = 0;
        foreach (var position in editable)
        {
            var isHostAnchor = !string.IsNullOrWhiteSpace(hostAnchor) &&
                               string.Equals(position, hostAnchor, StringComparison.OrdinalIgnoreCase);
            var hasSinglePrefilled =
                (preFilledSlots.TryGetValue(position, out var job) && !string.IsNullOrWhiteSpace(job)) ||
                (preFilledMembers.TryGetValue(position, out var member) &&
                 !string.IsNullOrWhiteSpace(member.Job) &&
                 string.IsNullOrWhiteSpace(member.FlexSpec));
            var hasFlexPrefilled =
                (preFilledMembers.TryGetValue(position, out var memberByFlex) &&
                 !string.IsNullOrWhiteSpace(memberByFlex.Job) &&
                 !string.IsNullOrWhiteSpace(memberByFlex.FlexSpec)) ||
                (preFilledFlexSlots.TryGetValue(position, out var flexSlotByPosition) &&
                 !string.IsNullOrWhiteSpace(flexSlotByPosition.Job));
            var label = hasSinglePrefilled
                ? $"{position}*S"
                : hasFlexPrefilled
                    ? $"{position}*F"
                    : position;
            var row = index < 4 ? 0 : 1;
            builder.WithButton(
                label,
                $"recruit_create_prefill_pos:{position}",
                GetPositionButtonStyle(position),
                disabled: (isHostAnchor && !hostCanOverlap) || hasSinglePrefilled,
                row: row);
            index++;
        }

        var hasAnyPrefilled =
            preFilledSlots.Any(pair => !string.IsNullOrWhiteSpace(pair.Value)) ||
            preFilledMembers.Values.Any(member => !string.IsNullOrWhiteSpace(member.Job)) ||
            preFilledFlexSlots.Values.Any(slot => !string.IsNullOrWhiteSpace(slot.Job));

        builder
            .WithButton("FLEX", "recruit_create_prefill_flex_any", ButtonStyle.Primary, disabled: editable.Count == 0, row: 2)
            .WithButton("事前埋まり解除", "recruit_create_prefill_clear", ButtonStyle.Danger, disabled: !hasAnyPrefilled, row: 2)
            .WithButton("確認へ戻る", "recruit_create_prefill_back", ButtonStyle.Secondary, row: 2)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2);
        return builder.Build();
    }

    private static MessageComponent BuildCreateMentionRoleSelectComponents(
        RecruitCreateDraftContext draft,
        IReadOnlyList<IRole> roles)
    {
        var builder = new ComponentBuilder();
        var selectedRoleIds = (draft.MentionRoleIds ?? Array.Empty<ulong>())
            .Where(id => id != 0)
            .ToHashSet();
        if (roles.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId("recruit_create_mention_role_select")
                .WithPlaceholder("メンションするロールを選択（複数可）")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(roles.Count, 25));

            foreach (var role in roles.Take(25))
            {
                var isSelected = selectedRoleIds.Contains(role.Id);
                menu.AddOption(role.Name, role.Id.ToString(), isDefault: isSelected);
            }

            builder.WithSelectMenu(menu);
        }

        return builder
            .WithButton("指定解除", "recruit_create_mention_role_clear", ButtonStyle.Danger, disabled: selectedRoleIds.Count == 0, row: 1)
            .WithButton("戻る", "recruit_create_mention_role_back", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private sealed record CreatePrefillClearOption(string Value, string Label);

    private static IReadOnlyList<CreatePrefillClearOption> BuildCreatePrefillClearOptions(RecruitCreateDraftContext draft)
    {
        var options = new List<CreatePrefillClearOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddOption(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            if (!seen.Add(value))
            {
                return;
            }

            options.Add(new CreatePrefillClearOption(value, TruncateLabel(label, 95)));
        }

        foreach (var position in draft.Positions)
        {
            if (draft.PreFilledMembers.TryGetValue(position, out var member) &&
                !string.IsNullOrWhiteSpace(member.Job))
            {
                var memberJobs = ParseFlexJobCodes(member.FlexJobsSpec);
                if (memberJobs.Count == 0)
                {
                    memberJobs = new[] { member.Job.Trim().ToUpperInvariant() };
                }

                var memberJobLabel = string.Join('/', memberJobs);
                var normalizedFlexPositions = ResolveFlexCandidatePositions(member.FlexSpec ?? string.Empty, draft.Positions);
                if (normalizedFlexPositions.Count >= 2)
                {
                    var normalizedFlexSpec = string.Join(',', normalizedFlexPositions);
                    AddOption($"FLEX:{normalizedFlexSpec}", $"{BuildFlexSelectionLabel(normalizedFlexSpec)} / {member.UserName} ({memberJobLabel})");
                }
                else
                {
                    AddOption($"POS:{position}", $"{position} / {member.UserName} ({memberJobLabel})");
                }

                continue;
            }

            if (draft.PreFilledFlexSlots.TryGetValue(position, out var flexSlot) &&
                !string.IsNullOrWhiteSpace(flexSlot.Job))
            {
                var flexJobs = ParseFlexJobCodes(flexSlot.FlexJobsSpec);
                if (flexJobs.Count == 0)
                {
                    flexJobs = new[] { flexSlot.Job.Trim().ToUpperInvariant() };
                }

                var flexJobLabel = string.Join('/', flexJobs);
                var normalizedFlexPositions = ResolveFlexCandidatePositions(flexSlot.FlexSpec, draft.Positions);
                if (normalizedFlexPositions.Count >= 2)
                {
                    var normalizedFlexSpec = string.Join(',', normalizedFlexPositions);
                    AddOption($"FLEX:{normalizedFlexSpec}", $"{BuildFlexSelectionLabel(normalizedFlexSpec)} / 外部 ({flexJobLabel})");
                }
                else
                {
                    AddOption($"POS:{position}", $"{position} / 外部 ({flexJobLabel})");
                }

                continue;
            }

            if (!draft.PreFilledSlots.TryGetValue(position, out var job) || string.IsNullOrWhiteSpace(job))
            {
                continue;
            }

            AddOption($"POS:{position}", $"{position} / 外部 ({job.Trim().ToUpperInvariant()})");
        }

        return options;
    }

    private static MessageComponent BuildCreatePrefillClearSelectComponents(IReadOnlyList<CreatePrefillClearOption> options)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_create_prefill_clear_select")
            .WithPlaceholder("解除する事前埋まりを選択")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var option in options.Take(25))
        {
            menu.AddOption(option.Label, option.Value);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("戻る", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildCreatePrefillSourceSelectComponents(string selector)
    {
        return new ComponentBuilder()
            .WithButton("はい（サーバーメンバー）", $"recruit_create_prefill_member_yes:{selector}", ButtonStyle.Success, row: 0)
            .WithButton("いいえ（外部）", $"recruit_create_prefill_member_no:{selector}", ButtonStyle.Secondary, row: 0)
            .WithButton("戻る", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildCreatePrefillFlexSourceSelectComponents(string flexSpec)
    {
        var normalizedSpec = flexSpec.Trim().ToUpperInvariant();
        return new ComponentBuilder()
            .WithButton("はい（サーバーメンバー）", $"recruit_create_prefill_flex_member_source:{normalizedSpec}", ButtonStyle.Success, row: 0)
            .WithButton("いいえ（外部）", $"recruit_create_prefill_flex_external_source:{normalizedSpec}", ButtonStyle.Secondary, row: 0)
            .WithButton("戻る", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildCreatePrefillJobSelectComponents(
        string position,
        IReadOnlyList<JobOption> jobs,
        string? currentJob,
        IReadOnlySet<string> usedJobs,
        string? requiredJob,
        string selectCustomIdPrefix = "recruit_create_prefill_job",
        string backCustomId = "recruit_create_confirm_edit_prefill")
    {
        var currentJobs = ParseFlexJobCodes(currentJob);
        if (currentJobs.Count == 0 && !string.IsNullOrWhiteSpace(currentJob))
        {
            currentJobs = new[] { currentJob.Trim().ToUpperInvariant() };
        }

        if (TryParseFlexPositionToken(position, out var flexSpec) &&
            (string.Equals(selectCustomIdPrefix, "recruit_create_prefill_member_job", StringComparison.Ordinal) ||
             string.Equals(selectCustomIdPrefix, "recruit_create_prefill_external_job", StringComparison.Ordinal)))
        {
            var selectedSet = currentJobs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectableJobs = jobs
                .Where(job =>
                    (string.IsNullOrWhiteSpace(requiredJob) || string.Equals(requiredJob, job.Code, StringComparison.OrdinalIgnoreCase)) &&
                    (!usedJobs.Contains(job.Code) || selectedSet.Contains(job.Code)))
                .ToArray();
            if (selectableJobs.Length == 0)
            {
                selectableJobs = jobs
                    .Where(job => string.IsNullOrWhiteSpace(requiredJob) || string.Equals(requiredJob, job.Code, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            var menu = new SelectMenuBuilder()
                .WithCustomId($"{selectCustomIdPrefix}_select:{flexSpec}")
                .WithPlaceholder("参加ジョブを選択（複数可）")
                .WithMinValues(1)
                .WithMaxValues(Math.Min(selectableJobs.Length, 25));
            foreach (var job in selectableJobs.Take(25))
            {
                menu.AddOption(job.Code, job.Code, isDefault: selectedSet.Contains(job.Code));
            }

            return new ComponentBuilder()
                .WithSelectMenu(menu)
                .WithButton("指定解除", $"{selectCustomIdPrefix}:{position}|NONE", ButtonStyle.Danger, disabled: currentJobs.Count == 0, row: 1)
                .WithButton("戻る", backCustomId, ButtonStyle.Secondary, row: 1)
                .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
                .Build();
        }

        var builder = new ComponentBuilder();

        var index = 0;
        foreach (var job in jobs)
        {
            var selected = currentJobs.Contains(job.Code, StringComparer.OrdinalIgnoreCase);
            var restrictedByRequired = !string.IsNullOrWhiteSpace(requiredJob) &&
                !string.Equals(requiredJob, job.Code, StringComparison.OrdinalIgnoreCase);
            var taken = usedJobs.Contains(job.Code);
            var row = index / 5;
            var style = selected ? ButtonStyle.Primary : GetPositionButtonStyle(position);
            var label = selected ? $"{job.Code}*" : job.Code;
            builder.WithButton(
                label,
                $"{selectCustomIdPrefix}:{position}|{job.Code}",
                style,
                disabled: restrictedByRequired || (!selected && taken),
                row: row);
            index++;
        }

        builder
            .WithButton("指定解除", $"{selectCustomIdPrefix}:{position}|NONE", ButtonStyle.Danger, disabled: currentJobs.Count == 0, row: 3)
            .WithButton("戻る", backCustomId, ButtonStyle.Secondary, row: 3)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 3);

        return builder.Build();
    }

    private MessageComponent? BuildCreatePrefillMemberUserSelectComponents(
        RecruitCreateDraftContext draft,
        string selector,
        string job,
        int pageIndex,
        IReadOnlyList<SocketGuildUser> users,
        out string pageText)
    {
        pageText = "1/1";
        if (users.Count == 0)
        {
            return null;
        }

        var candidatePositions = ResolveCreatePrefillCandidatePositions(selector, draft);
        if (candidatePositions.Count == 0)
        {
            return null;
        }
        var normalizedSelector = candidatePositions.Count >= 2
            ? $"FLEX:{string.Join(',', candidatePositions)}"
            : candidatePositions[0];
        var candidatePositionSet = candidatePositions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        const int pageSize = 25;
        var totalPages = Math.Max(1, (users.Count + pageSize - 1) / pageSize);
        var currentPage = Math.Clamp(pageIndex, 0, totalPages - 1);
        pageText = $"{currentPage + 1}/{totalPages}";
        var pageUsers = users
            .Skip(currentPage * pageSize)
            .Take(pageSize)
            .ToList();

        var selectedId = draft.PreFilledMembers
            .Where(pair => IsMatchingPrefilledMemberSelector(pair.Key, pair.Value, candidatePositionSet, draft.Positions))
            .Select(pair => pair.Value.UserId)
            .FirstOrDefault();
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_create_prefill_member_user")
            .WithPlaceholder($"メンバーを選択 ({pageText})")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var user in pageUsers)
        {
            var label = BuildUserShortLabel(user.DisplayName ?? user.GlobalName ?? user.Username, user.Username);
            menu.AddOption(label, user.Id.ToString(), user.Username, isDefault: user.Id == selectedId);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("前へ", $"recruit_create_prefill_member_page:{currentPage - 1}", ButtonStyle.Secondary, disabled: currentPage <= 0, row: 1)
            .WithButton("次へ", $"recruit_create_prefill_member_page:{currentPage + 1}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1, row: 1)
            .WithButton("ジョブ再選択", $"recruit_create_prefill_member_yes:{normalizedSelector}", ButtonStyle.Secondary, row: 1)
            .WithButton("外部で設定", $"recruit_create_prefill_member_no:{normalizedSelector}", ButtonStyle.Secondary, row: 1)
            .WithButton("戻る", "recruit_create_confirm_edit_prefill", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private static string BuildCreateConfirmationSummary(RecruitCreateDraftContext draft, SocketGuild? guild)
    {
        var requiredSummary = BuildCreateRequiredJobsSummary(draft.Positions, draft.RequiredJobs);
        var prefillSummary = BuildCreatePreFilledSummary(draft.Positions, draft.PreFilledSlots, draft.PreFilledFlexSlots, draft.PreFilledMembers, draft.HostPosition);
        var hostPosition = string.IsNullOrWhiteSpace(draft.HostPosition)
            ? "未選択"
            : !string.IsNullOrWhiteSpace(draft.HostFlexSpec)
                ? BuildFlexSelectionLabel(draft.HostFlexSpec)
                : BuildHostSelectorLabel(draft.HostPosition);
        var hostFlexJobs = ParseFlexJobCodes(draft.HostFlexJobsSpec);
        var hostJob = hostFlexJobs.Count > 0
            ? string.Join('/', hostFlexJobs)
            : string.IsNullOrWhiteSpace(draft.HostJob) ? "未選択" : draft.HostJob;
        var schedule = string.IsNullOrWhiteSpace(draft.Schedule) ? "未設定" : draft.Schedule;
        var dataCenter = string.IsNullOrWhiteSpace(draft.DataCenter) ? "未設定" : draft.DataCenter;
        var note = string.IsNullOrWhiteSpace(draft.Note) ? "なし" : draft.Note.Trim();
        var duplicate = draft.DisallowDuplicateJobs ? "禁止" : "許可";
        var mentionRole = BuildCreateMentionRoleSummary(draft, guild);

        return string.Join('\n', new[]
        {
            $"タイトル: {draft.Title}",
            $"DC: {dataCenter}",
            $"開始: {schedule}",
            $"主催: {hostPosition} / {hostJob}",
            $"メンションロール: {mentionRole}",
            $"ジョブ重複: {duplicate}",
            $"ジョブ指定: {requiredSummary}",
            $"事前埋まり: {prefillSummary}",
            $"コメント: {note}"
        });
    }

    private static string BuildCreateRequiredJobsSummary(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> requiredJobs)
    {
        var lines = new List<string>();
        foreach (var position in positions)
        {
            requiredJobs.TryGetValue(position, out var required);
            if (!string.IsNullOrWhiteSpace(required))
            {
                lines.Add($"{position}:{required}");
            }
        }

        if (lines.Count == 0)
        {
            return "指定なし";
        }

        return string.Join(", ", lines);
    }

    private static string BuildCreatePreFilledSummary(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        string? hostPosition)
        => RecruitmentFlexEngine.BuildCreatePreFilledSummary(
            positions,
            preFilledSlots,
            preFilledFlexSlots,
            preFilledMembers,
            hostPosition);

    private static string BuildCreatePreFilledSummaryWithLegend(
        IReadOnlyList<string> positions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        string? hostPosition)
        => $"{BuildCreatePrefillLegendLine()}\n{BuildCreatePreFilledSummary(positions, preFilledSlots, preFilledFlexSlots, preFilledMembers, hostPosition)}";

    private static string BuildCreatePrefillLegendLine()
        => "※ `*S`=単体事前埋まり / `*F`=FLEX事前埋まり";

    private static IReadOnlyList<IRole> ListCreateMentionRoleCandidates(SocketGuild guild)
    {
        return guild.Roles
            .Where(role =>
                role.Id != guild.EveryoneRole.Id &&
                role.Name.Contains("[コンテンツ]", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(role => role.Position)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(role => role.Id)
            .Cast<IRole>()
            .ToArray();
    }

    private static string BuildCreateMentionRoleSummary(RecruitCreateDraftContext draft, SocketGuild? guild)
    {
        var mentionRoleIds = (draft.MentionRoleIds ?? Array.Empty<ulong>())
            .Where(id => id != 0)
            .Distinct()
            .ToArray();
        if (mentionRoleIds.Length == 0)
        {
            return "なし";
        }

        if (guild is null)
        {
            return string.Join(", ", mentionRoleIds.Select(MentionRole));
        }

        var labels = new List<string>();
        foreach (var roleId in mentionRoleIds)
        {
            var role = guild.GetRole(roleId);
            labels.Add(role is null
                ? $"削除済み ({roleId})"
                : $"{role.Name} ({MentionRole(role.Id)})");
        }

        return string.Join(", ", labels);
    }

    private static MessageComponent BuildParticipationToolsComponents(
        RecruitPost post,
        ulong messageId,
        IReadOnlyList<string> positions,
        IReadOnlySet<string> joinedPositions,
        IReadOnlySet<string> selectablePositions)
    {
        var set = new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);
        var hasFlexCandidate = positions.Any(position => selectablePositions.Contains(position));

        var builder = new ComponentBuilder()
            .WithButton("MT", $"recruit_participation_pos:MT|{messageId}", ButtonStyle.Primary, disabled: !set.Contains("MT") || !selectablePositions.Contains("MT") || joinedPositions.Contains("MT"), row: 0)
            .WithButton("ST", $"recruit_participation_pos:ST|{messageId}", ButtonStyle.Primary, disabled: !set.Contains("ST") || !selectablePositions.Contains("ST") || joinedPositions.Contains("ST"), row: 0)
            .WithButton("H1", $"recruit_participation_pos:H1|{messageId}", ButtonStyle.Success, disabled: !set.Contains("H1") || !selectablePositions.Contains("H1") || joinedPositions.Contains("H1"), row: 0)
            .WithButton("H2", $"recruit_participation_pos:H2|{messageId}", ButtonStyle.Success, disabled: !set.Contains("H2") || !selectablePositions.Contains("H2") || joinedPositions.Contains("H2"), row: 0)
            .WithButton("D1", $"recruit_participation_pos:D1|{messageId}", ButtonStyle.Danger, disabled: !set.Contains("D1") || !selectablePositions.Contains("D1") || joinedPositions.Contains("D1"), row: 1)
            .WithButton("D2", $"recruit_participation_pos:D2|{messageId}", ButtonStyle.Danger, disabled: !set.Contains("D2") || !selectablePositions.Contains("D2") || joinedPositions.Contains("D2"), row: 1);

        builder
            .WithButton("D3", $"recruit_participation_pos:D3|{messageId}", ButtonStyle.Danger, disabled: !set.Contains("D3") || !selectablePositions.Contains("D3") || joinedPositions.Contains("D3"), row: 1)
            .WithButton("D4", $"recruit_participation_pos:D4|{messageId}", ButtonStyle.Danger, disabled: !set.Contains("D4") || !selectablePositions.Contains("D4") || joinedPositions.Contains("D4"), row: 1)
            .WithButton("FLEX", $"recruit_participation_flex:ANY|{messageId}", ButtonStyle.Primary, disabled: !hasFlexCandidate, row: 2)
            .WithButton("コメント編集", $"recruit_participation_comment:{messageId}", ButtonStyle.Secondary, row: 2)
            .WithButton("参加取消", $"recruit_participation_cancel:{messageId}", ButtonStyle.Danger, row: 2);

        return builder.Build();
    }

    private static MessageComponent BuildManageToolsComponents(ulong messageId, bool isCreator, bool canCancelOthers, bool canTransferOwner, RecruitPostStatus status)
    {
        var builder = new ComponentBuilder();
        if (status is RecruitPostStatus.Open or RecruitPostStatus.Full)
        {
            builder
                .WithButton("埋まった枠を〆る", $"recruit_manage_tool_fill:{messageId}", ButtonStyle.Danger, row: 0)
                .WithButton("〆解除", $"recruit_manage_tool_unfill:{messageId}", ButtonStyle.Secondary, row: 0);

            if (isCreator)
            {
                builder
                    .WithButton("募集内容編集", $"recruit_manage_tool_post_edit:{messageId}", ButtonStyle.Primary, row: 1)
                    .WithButton("開始時刻変更", $"recruit_manage_tool_reschedule:{messageId}", ButtonStyle.Secondary, row: 1)
                    .WithButton("募集枠編集", $"recruit_manage_tool_positions_edit:{messageId}", ButtonStyle.Success, row: 1);
            }

            if (isCreator)
            {
                builder.WithButton("募集終了", $"recruit_manage_tool_close:{messageId}", ButtonStyle.Secondary, row: 2);
            }

            if (isCreator || canCancelOthers)
            {
                builder.WithButton("募集中断", $"recruit_manage_tool_cancel:{messageId}", ButtonStyle.Danger, row: 2);
            }

            if (canTransferOwner)
            {
                builder.WithButton("主催委譲", $"recruit_manage_tool_transfer:{messageId}", ButtonStyle.Primary, row: 2);
            }
        }

        return builder.Build();
    }

    private static MessageComponent BuildManageToolActionConfirmComponents(ulong messageId, RecruitPostStatus targetStatus)
    {
        var confirmCustomId = targetStatus == RecruitPostStatus.Canceled
            ? $"recruit_manage_tool_cancel_confirm:{messageId}"
            : $"recruit_manage_tool_close_confirm:{messageId}";
        var confirmLabel = targetStatus == RecruitPostStatus.Canceled
            ? "中断する"
            : "終了する";
        var confirmStyle = targetStatus == RecruitPostStatus.Canceled
            ? ButtonStyle.Danger
            : ButtonStyle.Secondary;

        return new ComponentBuilder()
            .WithButton(confirmLabel, confirmCustomId, confirmStyle, row: 0)
            .WithButton("戻る", $"recruit_manage_tools:{messageId}", ButtonStyle.Primary, row: 0)
            .WithButton("取消", "recruit_manage_tool_action_abort", ButtonStyle.Secondary, row: 0)
            .Build();
    }

    private static MessageComponent BuildManageTransferOwnerSelectComponents(
        ulong messageId,
        IReadOnlyList<(ulong UserId, string Label, string? Description)> candidates)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_manage_transfer_user_select:{messageId}")
            .WithPlaceholder("委譲先の主催を選択")
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var candidate in candidates)
        {
            menu.AddOption(candidate.Label, candidate.UserId.ToString(), candidate.Description);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("戻る", $"recruit_manage_tools:{messageId}", ButtonStyle.Primary, row: 1)
            .WithButton("中止", "recruit_manage_tool_action_abort", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildExternalFillSelectComponents(ulong messageId, IReadOnlyList<string> positions, bool includeFlex)
    {
        var builder = new ComponentBuilder();
        var index = 0;
        foreach (var position in positions)
        {
            var row = index / 4;
            builder.WithButton(
                position,
                $"recruit_external_fill_pos:{position}|{messageId}",
                GetPositionButtonStyle(position),
                row: row);
            index++;
        }

        if (includeFlex)
        {
            var flexRow = positions.Count == 0
                ? 0
                : Math.Min((positions.Count - 1) / 4 + 1, 4);
            builder.WithButton("FLEX", $"recruit_external_fill_flex:ANY|{messageId}", ButtonStyle.Primary, row: flexRow);
        }

        return builder.Build();
    }

    private static MessageComponent BuildSlotEditPositionButtonsComponents(ulong messageId, IReadOnlyList<string> positions, IReadOnlyList<RecruitSlot> slots)
    {
        var map = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var builder = new ComponentBuilder();

        var index = 0;
        foreach (var position in positions)
        {
            map.TryGetValue(position, out var slot);
            var hasRequired = slot is not null && !string.IsNullOrWhiteSpace(slot.RequiredJob);
            var label = hasRequired ? $"{position}*" : position;
            var row = index < 4 ? 0 : 1;
            builder.WithButton(label, $"recruit_slot_edit_pos:{position}|{messageId}", GetPositionButtonStyle(position), row: row);
            index++;
        }

        return builder.Build();
    }

    private static MessageComponent BuildSlotEditJobButtonsComponents(string position, ulong messageId, IReadOnlyList<JobOption> jobs, string? currentRequiredJob)
    {
        var builder = new ComponentBuilder();

        var index = 0;
        foreach (var job in jobs)
        {
            var selected = !string.IsNullOrWhiteSpace(currentRequiredJob) &&
                string.Equals(currentRequiredJob, job.Code, StringComparison.OrdinalIgnoreCase);
            var row = index / 5;
            builder.WithButton(
                job.Code,
                $"recruit_slot_edit_job:{job.Code}|{position}|{messageId}",
                selected ? ButtonStyle.Primary : ButtonStyle.Secondary,
                row: row);
            index++;
        }

        builder
            .WithButton("指定解除", $"recruit_slot_edit_job:NONE|{position}|{messageId}", ButtonStyle.Danger, disabled: string.IsNullOrWhiteSpace(currentRequiredJob), row: 3)
            .WithButton("戻る", $"recruit_slot_edit_back:{messageId}", ButtonStyle.Secondary, row: 3);

        return builder
            .Build();
    }

    private static ButtonStyle GetPositionButtonStyle(string position)
    {
        return position switch
        {
            "MT" or "ST" => ButtonStyle.Primary,
            "H1" or "H2" => ButtonStyle.Success,
            _ => ButtonStyle.Danger
        };
    }

    private static ButtonStyle GetHostSelectorButtonStyle(string hostSelector)
    {
        if (TryParseFlexPositionToken(hostSelector, out var flexSpec))
        {
            return string.Equals(flexSpec, "D1,D2", StringComparison.OrdinalIgnoreCase)
                ? ButtonStyle.Danger
                : ButtonStyle.Primary;
        }

        return GetPositionButtonStyle(hostSelector);
    }

    private static string BuildSlotEditSummary(IReadOnlyList<RecruitSlot> slots)
    {
        var map = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>();
        foreach (var position in GetOrderedPositions(slots))
        {
            map.TryGetValue(position, out var slot);
            if (slot is null)
            {
                lines.Add($"{position}: 指定なし");
                continue;
            }

            var state = string.IsNullOrWhiteSpace(slot.RequiredJob)
                ? "指定なし"
                : $"指定 {slot.RequiredJob}";

            if (slot.UserId.HasValue)
            {
                var joinedJob = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
                lines.Add($"{position}: {state} / 参加中 {joinedJob}");
                continue;
            }

            if (slot.IsClosed)
            {
                var closedJob = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
                lines.Add($"{position}: {state} / 〆 {closedJob}");
                continue;
            }

            lines.Add($"{position}: {state}");
        }

        return string.Join('\n', lines);
    }

    private static MessageComponent BuildExternalFillJobButtonsComponents(string position, ulong messageId, IReadOnlyList<JobOption> jobs)
    {
        var builder = new ComponentBuilder();
        var buttonStyle = TryParseFlexPositionToken(position, out _)
            ? ButtonStyle.Primary
            : GetPositionButtonStyle(position);
        var index = 0;
        foreach (var job in jobs)
        {
            var row = index / 5;
            builder.WithButton(
                job.Code,
                $"recruit_external_fill_job:{job.Code}|{position}|{messageId}",
                buttonStyle,
                row: row);
            index++;
        }

        return builder.Build();
    }

    private static MessageComponent BuildExternalFillFlexJobSelectComponents(string normalizedFlexSpec, ulong messageId, IReadOnlyList<JobOption> jobs)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_external_fill_flex_job_select:{normalizedFlexSpec}|{messageId}")
            .WithPlaceholder("埋まったジョブを選択（複数可）")
            .WithMinValues(1)
            .WithMaxValues(Math.Min(jobs.Count, 25));

        foreach (var job in jobs)
        {
            menu.AddOption(job.Code, job.Code);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("戻る", $"recruit_external_fill_pos:FLEX:{normalizedFlexSpec}|{messageId}", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_external_fill_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildExternalFillSourceSelectComponents(
        string job,
        string position,
        ulong messageId,
        bool allowMemberSelection = true)
    {
        return new ComponentBuilder()
            .WithButton("はい（サーバーメンバー）", $"recruit_external_fill_member_yes:{job}|{position}|{messageId}", ButtonStyle.Success, disabled: !allowMemberSelection, row: 0)
            .WithButton("いいえ（外部）", $"recruit_external_fill_member_no:{job}|{position}|{messageId}", ButtonStyle.Secondary, row: 0)
            .WithButton("中止", "recruit_external_fill_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent? BuildExternalFillMemberSelectComponents(
        IReadOnlyList<SocketGuildUser> users,
        string job,
        string position,
        ulong messageId,
        int pageIndex,
        out string pageText)
    {
        pageText = "1/1";
        if (users.Count == 0)
        {
            return null;
        }

        const int pageSize = 25;
        var totalPages = Math.Max(1, (users.Count + pageSize - 1) / pageSize);
        var currentPage = Math.Clamp(pageIndex, 0, totalPages - 1);
        pageText = $"{currentPage + 1}/{totalPages}";

        var pageUsers = users
            .Skip(currentPage * pageSize)
            .Take(pageSize)
            .ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_external_fill_member_user")
            .WithPlaceholder($"メンバーを選択 ({pageText})")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var user in pageUsers)
        {
            var label = BuildUserShortLabel(user.DisplayName ?? user.GlobalName ?? user.Username, user.Username);
            menu.AddOption(label, $"{user.Id}|{job}|{position}|{messageId}", user.Username);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .WithButton("前へ", $"recruit_external_fill_member_page:{job}|{position}|{messageId}|{currentPage - 1}", ButtonStyle.Secondary, disabled: currentPage <= 0, row: 1)
            .WithButton("次へ", $"recruit_external_fill_member_page:{job}|{position}|{messageId}|{currentPage + 1}", ButtonStyle.Secondary, disabled: currentPage >= totalPages - 1, row: 1)
            .WithButton("ジョブ再選択", $"recruit_external_fill_pos:{position}|{messageId}", ButtonStyle.Secondary, row: 1)
            .WithButton("外部で〆", $"recruit_external_fill_member_no:{job}|{position}|{messageId}", ButtonStyle.Secondary, row: 1)
            .WithButton("中止", "recruit_external_fill_cancel", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private static MessageComponent BuildExternalUnfillSelectComponents(ulong messageId, IReadOnlyList<string> positions)
    {
        var builder = new ComponentBuilder();
        var index = 0;
        foreach (var position in positions)
        {
            var row = index < 4 ? 0 : 1;
            builder.WithButton(
                position,
                $"recruit_external_unfill_pos:{position}|{messageId}",
                GetPositionButtonStyle(position),
                row: row);
            index++;
        }

        return builder.Build();
    }

    private static bool TryParseRecruitMessageId(string? input, out ulong messageId)
    {
        messageId = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim().Trim('<', '>', ' ');
        if (ulong.TryParse(trimmed, out messageId))
        {
            return true;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && ulong.TryParse(parts[^1], out messageId))
        {
            return true;
        }

        return false;
    }

    private static string TruncateLabel(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private async Task<IReadOnlyList<SocketGuildUser>> LoadGuildUsersForSelectionAsync(SocketGuild guild, ulong? excludeUserId = null)
    {
        var users = ListSortedGuildUsers(guild, excludeUserId);
        var hasLikelyFullCache = guild.MemberCount == 0 || users.Count >= Math.Max(1, guild.MemberCount - 1);
        if (hasLikelyFullCache || users.Count > 1)
        {
            return users;
        }

        try
        {
            await guild.DownloadUsersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download guild users for recruitment member selection. GuildId={GuildId}", guild.Id);
        }

        return ListSortedGuildUsers(guild, excludeUserId);
    }

    private static IReadOnlyList<SocketGuildUser> ListSortedGuildUsers(SocketGuild guild, ulong? excludeUserId = null)
    {
        return guild.Users
            .Where(user => !user.IsBot && (!excludeUserId.HasValue || user.Id != excludeUserId.Value))
            .OrderBy(user => user.DisplayName ?? user.GlobalName ?? user.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildUserShortLabel(string displayName, string username)
    {
        var baseName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "unknown";
        }

        if (!string.IsNullOrWhiteSpace(username) &&
            !string.Equals(baseName, username, StringComparison.OrdinalIgnoreCase))
        {
            baseName = $"{baseName} (@{username})";
        }

        return TruncateLabel(baseName, 95);
    }

    private static string GetRoleLabelByPosition(string position)
    {
        return position switch
        {
            "MT" or "ST" => "TANK",
            "H1" or "H2" => "HEALER",
            _ => "DPS"
        };
    }

    private static string NormalizeTemplateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private static bool TryResolveRecruitTargetChannel(SocketGuild guild, IMessageChannel currentChannel, string? input, out ITextChannel channel, out string? error)
    {
        channel = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            if (currentChannel is ITextChannel currentTextChannel)
            {
                channel = currentTextChannel;
                return true;
            }

            error = "このチャンネルには投稿できません。`Discord__RecruitPostChannelName` を設定してください。";
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("<#", StringComparison.Ordinal) &&
            trimmed.EndsWith(">", StringComparison.Ordinal) &&
            ulong.TryParse(trimmed[2..^1], out var mentionId))
        {
            var mentionChannel = guild.GetTextChannel(mentionId);
            if (mentionChannel is null)
            {
                error = "指定したチャンネルが見つかりません。";
                return false;
            }

            channel = mentionChannel;
            return true;
        }

        var normalized = trimmed.TrimStart('#');
        var candidates = guild.TextChannels
            .Where(textChannel => string.Equals(textChannel.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .Cast<ITextChannel>()
            .ToArray();

        if (candidates.Length == 0)
        {
            error = $"チャンネル名 `{normalized}` が見つかりません。";
            return false;
        }

        if (candidates.Length > 1)
        {
            error = $"同名チャンネルが複数あります。チャンネルメンションで指定してください: {string.Join(", ", candidates.Take(5).Select(textChannel => MentionChannel(textChannel.Id)))}";
            return false;
        }

        channel = candidates[0];
        return true;
    }

    private static RecruitPostStatus DetermineOpenStatus(IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return RecruitPostStatus.Open;
        }

        var filled = slots.Count(slot => slot.UserId.HasValue || slot.IsClosed);
        return filled >= slots.Count ? RecruitPostStatus.Full : RecruitPostStatus.Open;
    }

    private static Embed BuildRecruitEmbed(RecruitPost post, IReadOnlyList<RecruitSlot> slots, bool disallowDuplicateJobs)
    {
        var filled = slots.Count(slot => slot.UserId.HasValue || slot.IsClosed);
        var max = slots.Count;
        if (max == 0)
        {
            max = RecruitmentRules.Positions.Count;
        }
        var schedule = FormatSchedule(post.ScheduleText, post.ScheduleAt);
        var dataCenter = string.IsNullOrWhiteSpace(post.DataCenter) ? "未指定" : post.DataCenter;
        var note = string.IsNullOrWhiteSpace(post.Note) ? "なし" : post.Note;
        var (status, statusPrefix, color, footer) = post.Status switch
        {
            RecruitPostStatus.Full => ("🟡 満員", "[🟡満員] ", new Color(255, 193, 7), "この募集は満員です。参加メニューからコメント編集/参加取消ができます。"),
            RecruitPostStatus.Closed => ("⚫ 終了", "[⚫終了] ", Color.DarkGrey, "この募集は終了しました。"),
            RecruitPostStatus.Canceled => ("🔴 中断", "[🔴中断] ", new Color(231, 76, 60), "この募集は中断されました。"),
            _ => ("🟢 募集中", "[🟢募集中] ", new Color(46, 204, 113), disallowDuplicateJobs ? "参加メニューから操作できます（ジョブ重複不可）" : "参加メニューから操作できます")
        };
        var hostFlexJobs = ParseFlexJobCodes(post.HostFlexJobsSpec);
        var hostSlot = slots.FirstOrDefault(slot => slot.UserId.HasValue && slot.UserId.Value == post.CreatorUserId);
        var hostJoined = hostSlot is not null;
        var hostLabel = hostJoined ? MentionUser(post.CreatorUserId) : $"{MentionUser(post.CreatorUserId)}（未参加）";
        var hostCurrentJob = string.IsNullOrWhiteSpace(hostSlot?.Job) ? "?" : hostSlot!.Job!;
        var hostDisplayJob = BuildHostDisplayedJob(hostCurrentJob, hostFlexJobs);
        var hostFlexPositions = ResolveFlexCandidatePositions(post.HostFlexSpec ?? string.Empty, slots);
        var showHostFlexWaiting = false;
        if (hostJoined &&
            !string.IsNullOrWhiteSpace(post.HostFlexSpec) &&
            hostFlexPositions.Count >= 2)
        {
            var hostFlexSlots = hostFlexPositions
                .Select(position => slots.FirstOrDefault(item => string.Equals(item.Position, position, StringComparison.OrdinalIgnoreCase)))
                .Where(slot => slot is not null)
                .ToArray();
            var hasClosedHostFlexSlot = hostFlexSlots.Any(slot => slot!.IsClosed && !slot.UserId.HasValue);
            var hasOpenHostFlexSlot = hostFlexSlots.Any(slot => !slot!.IsClosed && !slot.UserId.HasValue);
            showHostFlexWaiting = !hasClosedHostFlexSlot && hasOpenHostFlexSlot;
        }
        var hostState = hostJoined
            ? showHostFlexWaiting
                ? $"{BuildFlexSelectionLabel(post.HostFlexSpec!)} ({hostDisplayJob})"
                : $"{hostSlot!.Position} ({hostDisplayJob})"
            : "未参加（参加メニューから参加してください）";
        var countLabel = !hostJoined && post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full
            ? $"{filled}/{max}（主催未参加）"
            : $"{filled}/{max}";
        var footerLabel = !hostJoined && post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full
            ? $"{footer} / 主催はまだ参加枠を選択していません。"
            : footer;

        var embedBuilder = new EmbedBuilder()
            .WithTitle($"{statusPrefix}募集: {post.Title}")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("主催", hostLabel, true)
            .AddField("主催参加", hostState, true)
            .AddField("DC", dataCenter, true)
            .AddField("開始", schedule, true)
            .AddField("人数", countLabel, true)
            .AddField("状態", status, true)
            .AddField("コメント", note, false)
            .AddField("ポジション", BuildSlotLines(post, slots), false)
            .WithFooter(footerLabel);

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            var closedAt = post.ClosedAt?.ToOffset(JapanTimeOffset).ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture) ?? "不明";
            embedBuilder.AddField("終了", closedAt, true);
        }

        return embedBuilder.Build();
    }

    private static string BuildSlotLines(RecruitPost post, IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return "なし";
        }

        var map = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var hostSlot = slots.FirstOrDefault(slot => slot.UserId.HasValue && slot.UserId.Value == post.CreatorUserId);
        var hostFlexJobs = ParseFlexJobCodes(post.HostFlexJobsSpec);
        var orderedPositions = GetOrderedPositions(slots);
        var lines = new List<string>();
        var flexLines = new List<string>();
        var flexOccupiedPositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (hostSlot is not null && !string.IsNullOrWhiteSpace(post.HostFlexSpec))
        {
            var flexPositions = ResolveFlexCandidatePositions(post.HostFlexSpec, orderedPositions).ToArray();
            var hostCandidateJobs = hostFlexJobs.Count > 0
                ? hostFlexJobs
                : string.IsNullOrWhiteSpace(hostSlot.Job)
                    ? Array.Empty<string>()
                    : new[] { hostSlot.Job.Trim().ToUpperInvariant() };
            var hostUserId = hostSlot.UserId ?? post.CreatorUserId;
            var hostAssignableCount = CountDisplayAssignableFlexPositions(
                slots,
                flexPositions,
                hostCandidateJobs,
                hostUserId,
                post.DisallowDuplicateJobs);
            if (flexPositions.Length >= 2 &&
                flexPositions.Contains(hostSlot.Position, StringComparer.OrdinalIgnoreCase) &&
                hostAssignableCount >= 2)
            {
                var hostJob = string.IsNullOrWhiteSpace(hostSlot.Job) ? "?" : hostSlot.Job!;
                var participantLabel = $"{MentionUser(hostSlot.UserId!.Value)} ({BuildHostDisplayedJob(hostJob, hostFlexJobs)})";
                flexLines.Add($"[主催] {BuildFlexEmbedPositionLabel(post.HostFlexSpec!, orderedPositions)}: {participantLabel} どちらでもOK");
                flexOccupiedPositions.Add(hostSlot.Position);
            }
        }

        var participantFlexSlots = slots
            .Where(slot =>
                slot.UserId.HasValue &&
                slot.UserId.Value != post.CreatorUserId &&
                !string.IsNullOrWhiteSpace(slot.FlexSpec))
            .ToArray();
        foreach (var slot in participantFlexSlots)
        {
            var normalizedFlexSpec = NormalizeFlexSpecForDisplay(slot.FlexSpec, orderedPositions);
            if (string.IsNullOrWhiteSpace(normalizedFlexSpec))
            {
                continue;
            }

            var flexPositions = ResolveFlexCandidatePositions(normalizedFlexSpec, orderedPositions).ToArray();
            if (flexPositions.Length < 2 ||
                !flexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var participantJobs = ParseFlexJobCodes(slot.FlexJobsSpec);
            if (participantJobs.Count == 0 && !string.IsNullOrWhiteSpace(slot.Job))
            {
                participantJobs = new[] { slot.Job.Trim().ToUpperInvariant() };
            }
            var participantAssignableCount = CountDisplayAssignableFlexPositions(
                slots,
                flexPositions,
                participantJobs,
                slot.UserId!.Value,
                post.DisallowDuplicateJobs);
            if (participantAssignableCount < 2)
            {
                continue;
            }

            var job = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
            var participantLabel = $"{MentionUser(slot.UserId!.Value)} ({job})";
            flexLines.Add($"[参加] {BuildFlexEmbedPositionLabel(normalizedFlexSpec, orderedPositions)}: {participantLabel} どちらでもOK");
            flexOccupiedPositions.Add(slot.Position);
        }

        var closedFlexSlots = slots
            .Where(slot =>
                !slot.UserId.HasValue &&
                slot.IsClosed &&
                !string.IsNullOrWhiteSpace(slot.FlexSpec))
            .ToArray();
        foreach (var slot in closedFlexSlots)
        {
            var normalizedFlexSpec = NormalizeFlexSpecForDisplay(slot.FlexSpec, orderedPositions);
            if (string.IsNullOrWhiteSpace(normalizedFlexSpec))
            {
                continue;
            }

            var flexPositions = ResolveFlexCandidatePositions(normalizedFlexSpec, orderedPositions).ToArray();
            if (flexPositions.Length < 2 ||
                !flexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // If the closed FLEX cannot relocate anymore, treat it as fixed at the anchored position.
            if (!CanJoinByRelocatingClosedFlexAnchor(post, slots, slot, long.MinValue))
            {
                continue;
            }

            var flexJobs = ParseFlexJobCodes(slot.FlexJobsSpec);
            if (flexJobs.Count == 0 && !string.IsNullOrWhiteSpace(slot.Job))
            {
                flexJobs = new[] { slot.Job.Trim().ToUpperInvariant() };
            }
            var flexJobLabel = flexJobs.Count == 0 ? "?" : string.Join('/', flexJobs);
            flexLines.Add($"[〆] {BuildFlexEmbedPositionLabel(normalizedFlexSpec, orderedPositions)}: 外部 ({flexJobLabel})");
            flexOccupiedPositions.Add(slot.Position);
        }

        foreach (var position in orderedPositions)
        {
            if (!map.TryGetValue(position, out var slot))
            {
                lines.Add($"[空き] {position}");
                continue;
            }

            if (slot.IsClosed && !slot.UserId.HasValue)
            {
                if (flexOccupiedPositions.Contains(position))
                {
                    // Keep both candidate positions visibly joinable when a closed FLEX can relocate.
                    if (!string.IsNullOrWhiteSpace(slot.RequiredJob))
                    {
                        lines.Add($"[空き(指定)] {position}: {slot.RequiredJob}");
                    }
                    else
                    {
                        lines.Add($"[空き] {position}");
                    }
                    continue;
                }

                var closedJob = ResolveClosedAnchorDisplayJob(slot, position, orderedPositions);
                lines.Add($"[〆] {position}: {closedJob}");
                continue;
            }

            if (flexOccupiedPositions.Contains(position))
            {
                if (!string.IsNullOrWhiteSpace(slot.RequiredJob))
                {
                    lines.Add($"[空き(指定)] {position}: {slot.RequiredJob}");
                }
                else
                {
                    lines.Add($"[空き] {position}");
                }
                continue;
            }

            if (!slot.UserId.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(slot.RequiredJob))
                {
                    lines.Add($"[空き(指定)] {position}: {slot.RequiredJob}");
                }
                else
                {
                    lines.Add($"[空き] {position}");
                }
                continue;
            }

            var job = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
            lines.Add(slot.UserId.Value == post.CreatorUserId
                ? $"[主催] {position}: {MentionUser(slot.UserId.Value)} ({BuildHostDisplayedJob(job, hostFlexJobs)})"
                : $"[参加] {position}: {MentionUser(slot.UserId.Value)} ({job})");
        }

        lines.AddRange(flexLines);
        return string.Join('\n', lines);
    }

    private static string BuildHostDisplayedJob(string currentJob, IReadOnlyList<string> hostFlexJobs)
    {
        if (hostFlexJobs.Count <= 1)
        {
            return currentJob;
        }

        return $"候補:{string.Join('/', hostFlexJobs)}";
    }

    private static string ResolveClosedAnchorDisplayJob(
        RecruitSlot slot,
        string anchorPosition,
        IReadOnlyCollection<string> recruitPositions)
    {
        var normalizedCurrent = slot.Job?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedCurrent) &&
            IsJobAllowedForPosition(anchorPosition, normalizedCurrent, recruitPositions))
        {
            return normalizedCurrent;
        }

        var candidateJobs = ParseFlexJobCodes(slot.FlexJobsSpec);
        if (candidateJobs.Count == 0 && !string.IsNullOrWhiteSpace(normalizedCurrent))
        {
            candidateJobs = new[] { normalizedCurrent };
        }

        var compatible = ResolveAnchorCompatibleJobCode(anchorPosition, candidateJobs, recruitPositions);
        if (!string.IsNullOrWhiteSpace(compatible))
        {
            return compatible;
        }

        return "?";
    }

    private static int CountDisplayAssignableFlexPositions(
        IReadOnlyList<RecruitSlot> slots,
        IReadOnlyCollection<string> candidatePositions,
        IReadOnlyCollection<string> candidateJobs,
        long userId,
        bool disallowDuplicateJobs)
    {
        if (candidatePositions.Count == 0 || candidateJobs.Count == 0)
        {
            return 0;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedJobs = candidateJobs
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Select(job => job.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedJobs.Length == 0)
        {
            return 0;
        }

        var blockedJobs = disallowDuplicateJobs
            ? slots
                .Where(slot =>
                    !string.IsNullOrWhiteSpace(slot.Job) &&
                    ((slot.UserId.HasValue && slot.UserId.Value != userId) ||
                     (slot.IsClosed && !slot.UserId.HasValue)))
                .Select(slot => slot.Job!.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var assignableCount = 0;
        foreach (var candidatePosition in candidatePositions)
        {
            var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.IsClosed)
            {
                continue;
            }

            if (target.UserId.HasValue && target.UserId.Value != userId)
            {
                continue;
            }

            var canAssign = normalizedJobs.Any(job =>
                (!disallowDuplicateJobs || !blockedJobs.Contains(job)) &&
                IsJobAllowedForPosition(candidatePosition, job, recruitPositions) &&
                (string.IsNullOrWhiteSpace(target.RequiredJob) ||
                 string.Equals(target.RequiredJob, job, StringComparison.OrdinalIgnoreCase)));
            if (!canAssign)
            {
                continue;
            }

            assignableCount++;
        }

        return assignableCount;
    }

    private static string? NormalizeFlexSpecForDisplay(string? flexSpec, IReadOnlyList<string> orderedPositions)
    {
        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return null;
        }

        var positions = ResolveFlexCandidatePositions(flexSpec, orderedPositions);
        if (positions.Count == 0)
        {
            return null;
        }

        return string.Join(',',
            positions
                .Select(position => position.Trim().ToUpperInvariant())
                .Where(position => !string.IsNullOrWhiteSpace(position))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildFlexEmbedPositionLabel(string flexSpec, IReadOnlyList<string> orderedPositions)
    {
        var requested = ParseFlexSpecPositions(flexSpec);
        if (requested.Count == 0 || requested.Contains("ANY", StringComparer.OrdinalIgnoreCase))
        {
            return "FLEX";
        }

        var orderedSet = new HashSet<string>(orderedPositions, StringComparer.OrdinalIgnoreCase);
        var positions = requested
            .Where(position => orderedSet.Contains(position))
            .ToArray();
        if (positions.Length == 0)
        {
            positions = requested.ToArray();
        }

        return $"FLEX({string.Join('/', positions)})";
    }

    private static string BuildOpenPositionSummary(IReadOnlyList<RecruitSlot> slots)
    {
        var lines = new List<string>();
        foreach (var position in GetOrderedPositions(slots))
        {
            var slot = slots.FirstOrDefault(item => string.Equals(item.Position, position, StringComparison.OrdinalIgnoreCase));
            if (slot is null || slot.IsClosed || slot.UserId.HasValue)
            {
                continue;
            }

            lines.Add(string.IsNullOrWhiteSpace(slot.RequiredJob)
                ? position
                : $"{position}({slot.RequiredJob})");
        }

        return string.Join(", ", lines);
    }

    private static string FormatSchedule(string? scheduleText, DateTimeOffset? scheduleAt)
    {
        if (!string.IsNullOrWhiteSpace(scheduleText))
        {
            return scheduleText;
        }

        if (!scheduleAt.HasValue)
        {
            return "未定";
        }

        return scheduleAt.Value
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool CanSelectPositionForExternalFill(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        RecruitSlot targetSlot)
    {
        if (targetSlot.UserId.HasValue)
        {
            return false;
        }

        if (!targetSlot.IsClosed)
        {
            return true;
        }

        return CanJoinByRelocatingClosedFlexAnchor(post, slots, targetSlot, long.MinValue);
    }

    private static bool CanSelectPositionForExternalFillAsFlexAnchor(RecruitSlot targetSlot)
        => !targetSlot.UserId.HasValue && !targetSlot.IsClosed;

    private static bool TryResolveExternalFillFlexAnchor(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        string flexSpec,
        string jobsSpec,
        out string normalizedFlexSpec,
        out string normalizedFlexJobsSpec,
        out string anchorPosition,
        out string anchorJob,
        out string error)
    {
        normalizedFlexSpec = string.Empty;
        normalizedFlexJobsSpec = string.Empty;
        anchorPosition = string.Empty;
        anchorJob = string.Empty;
        error = "選択情報が不正です。";

        var selectedJobs = ParseFlexJobCodes(jobsSpec);
        if (selectedJobs.Count == 0)
        {
            return false;
        }

        var candidatePositions = ResolveFlexCandidatePositions(flexSpec, slots)
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidatePositions.Length < 2)
        {
            error = "FLEX候補ポジションを2つ以上選択してください。";
            return false;
        }

        normalizedFlexSpec = string.Join(',', candidatePositions);
        var slotMap = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var anchorCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchorCandidateJobs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var compatibleJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidatePosition in candidatePositions)
        {
            if (!slotMap.TryGetValue(candidatePosition, out var slot) ||
                !CanSelectPositionForExternalFillAsFlexAnchor(slot))
            {
                continue;
            }

            var candidateJobs = selectedJobs
                .Where(job =>
                    IsJobAllowedForPosition(candidatePosition, job, recruitPositions) &&
                    (string.IsNullOrWhiteSpace(slot.RequiredJob) ||
                     string.Equals(slot.RequiredJob, job, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (candidateJobs.Length == 0)
            {
                continue;
            }

            anchorCandidates.Add(candidatePosition);
            anchorCandidateJobs[candidatePosition] = candidateJobs;
            foreach (var candidateJob in candidateJobs)
            {
                compatibleJobs.Add(candidateJob);
            }
        }

        if (anchorCandidates.Count == 0)
        {
            var selectedJobLabel = string.Join('/', selectedJobs);
            error = $"{BuildFlexSelectionLabel(normalizedFlexSpec)} には {selectedJobLabel} で〆にできる空き枠がありません。";
            return false;
        }

        if (compatibleJobs.Count != selectedJobs.Count)
        {
            error = "選択したジョブに現在選べないものが含まれています。もう一度選択してください。";
            return false;
        }

        normalizedFlexJobsSpec = string.Join(
            ',',
            selectedJobs
                .Where(compatibleJobs.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase));

        if (post.DisallowDuplicateJobs)
        {
            var normalizedJobs = ParseFlexJobCodes(normalizedFlexJobsSpec);
            if (normalizedJobs.Count == 1)
            {
                var onlyJob = normalizedJobs[0];
                var duplicateExists = slots.Any(slot =>
                    !string.IsNullOrWhiteSpace(slot.Job) &&
                    string.Equals(slot.Job, onlyJob, StringComparison.OrdinalIgnoreCase) &&
                    (slot.UserId.HasValue || slot.IsClosed));
                if (duplicateExists)
                {
                    error = "そのジョブは既に埋まっています。";
                    return false;
                }
            }
        }

        var orderedPositions = GetOrderedPositions(slots);
        anchorPosition = orderedPositions
            .FirstOrDefault(position => anchorCandidates.Contains(position))
            ?? anchorCandidates.First();
        if (!anchorCandidateJobs.TryGetValue(anchorPosition, out var anchorJobs) || anchorJobs.Count == 0)
        {
            error = "選択情報が不正です。";
            return false;
        }

        anchorJob = selectedJobs.FirstOrDefault(job => anchorJobs.Contains(job, StringComparer.OrdinalIgnoreCase))
            ?? anchorJobs[0];
        error = string.Empty;
        return true;
    }

    private static bool CanJoinBySwappingFlexibleHost(RecruitPost post, IReadOnlyList<RecruitSlot> slots, RecruitSlot targetSlot, long joiningUserId)
    {
        if (!targetSlot.UserId.HasValue || targetSlot.UserId.Value == joiningUserId)
        {
            return false;
        }

        if (joiningUserId == post.CreatorUserId)
        {
            return false;
        }

        IReadOnlyList<string> flexiblePositions;
        var occupyingUserId = targetSlot.UserId.Value;
        if (occupyingUserId == post.CreatorUserId && !string.IsNullOrWhiteSpace(post.HostFlexSpec))
        {
            flexiblePositions = ResolveFlexCandidatePositions(post.HostFlexSpec, slots);
        }
        else if (!string.IsNullOrWhiteSpace(targetSlot.FlexSpec))
        {
            flexiblePositions = ResolveFlexCandidatePositions(targetSlot.FlexSpec, slots);
        }
        else
        {
            return false;
        }

        if (flexiblePositions.Count < 2 ||
            !flexiblePositions.Contains(targetSlot.Position, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var occupyingJobs = occupyingUserId == post.CreatorUserId
            ? ParseFlexJobCodes(post.HostFlexJobsSpec)
            : ParseFlexJobCodes(targetSlot.FlexJobsSpec);
        if (occupyingJobs.Count == 0)
        {
            var occupyingJob = targetSlot.Job?.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(occupyingJob))
            {
                occupyingJobs = new[] { occupyingJob };
            }
        }

        if (occupyingJobs.Count == 0)
        {
            return false;
        }

        var recruitPositions = slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in flexiblePositions)
        {
            if (string.Equals(candidate, targetSlot.Position, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var alternate = slots.FirstOrDefault(slot => string.Equals(slot.Position, candidate, StringComparison.OrdinalIgnoreCase));
            if (alternate is null || alternate.IsClosed)
            {
                continue;
            }

            if (alternate.UserId.HasValue &&
                alternate.UserId.Value != occupyingUserId &&
                alternate.UserId.Value != joiningUserId)
            {
                continue;
            }

            var canMove = occupyingJobs.Any(job =>
                IsJobAllowedForPosition(candidate, job, recruitPositions) &&
                (string.IsNullOrWhiteSpace(alternate.RequiredJob) ||
                 string.Equals(alternate.RequiredJob, job, StringComparison.OrdinalIgnoreCase)));
            if (!canMove)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool CanJoinByRelocatingClosedFlexAnchor(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        RecruitSlot closedSlot,
        long joiningUserId)
        => CanJoinByRelocatingClosedFlexAnchorInternal(
            post,
            slots,
            closedSlot,
            joiningUserId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static bool CanJoinByRelocatingClosedFlexAnchorInternal(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        RecruitSlot closedSlot,
        long joiningUserId,
        ISet<string> visitingClosedPositions)
    {
        if (!closedSlot.IsClosed || closedSlot.UserId.HasValue || string.IsNullOrWhiteSpace(closedSlot.FlexSpec))
        {
            return false;
        }

        var normalizedClosedPosition = closedSlot.Position?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedClosedPosition) ||
            !visitingClosedPositions.Add(normalizedClosedPosition))
        {
            return false;
        }

        try
        {

            var flexPositions = ResolveFlexCandidatePositions(closedSlot.FlexSpec, slots)
                .Select(position => position.Trim().ToUpperInvariant())
                .Where(position => !string.IsNullOrWhiteSpace(position))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (flexPositions.Length < 2 ||
                !flexPositions.Contains(closedSlot.Position, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var flexJobs = ParseFlexJobCodes(closedSlot.FlexJobsSpec);
            var closedJob = closedSlot.Job?.Trim().ToUpperInvariant();
            if (flexJobs.Count == 0 && !string.IsNullOrWhiteSpace(closedJob))
            {
                flexJobs = new[] { closedJob };
            }
            else if (!string.IsNullOrWhiteSpace(closedJob) &&
                     !flexJobs.Contains(closedJob, StringComparer.OrdinalIgnoreCase))
            {
                flexJobs = flexJobs
                    .Append(closedJob)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            if (flexJobs.Count == 0)
            {
                return false;
            }

            var recruitPositions = slots
                .Select(slot => slot.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var blockedJobs = post.DisallowDuplicateJobs
                ? slots
                    .Where(slot =>
                        !string.IsNullOrWhiteSpace(slot.Job) &&
                        !string.Equals(slot.Position, closedSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                        ((slot.UserId.HasValue && slot.UserId.Value != joiningUserId) ||
                         (slot.IsClosed && !slot.UserId.HasValue &&
                          ShouldReserveClosedSlotJobForDuplicateCheck(slot, recruitPositions))))
                    .Select(slot => slot.Job!.Trim().ToUpperInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidatePosition in flexPositions)
            {
                if (string.Equals(candidatePosition, closedSlot.Position, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateSlot = slots.FirstOrDefault(slot =>
                    string.Equals(slot.Position, candidatePosition, StringComparison.OrdinalIgnoreCase));
                if (candidateSlot is null)
                {
                    continue;
                }

                var candidateWasClosed = candidateSlot.IsClosed;
                var candidateCurrentJob = candidateSlot.Job?.Trim().ToUpperInvariant();
                var reopenedCandidate = false;
                if (candidateSlot.IsClosed)
                {
                    reopenedCandidate = CanJoinByRelocatingClosedFlexAnchorInternal(
                        post,
                        slots,
                        candidateSlot,
                        joiningUserId,
                        visitingClosedPositions);
                    if (!reopenedCandidate)
                    {
                        continue;
                    }
                }

                if (candidateSlot.UserId.HasValue &&
                    !CanRelocateFlexibleOccupantForClosedFlex(post, slots, candidateSlot, recruitPositions))
                {
                    continue;
                }

                if (candidateSlot.IsClosed && !reopenedCandidate)
                {
                    continue;
                }

                IReadOnlySet<string> blockedJobsForCandidate = blockedJobs;
                if (post.DisallowDuplicateJobs &&
                    candidateWasClosed &&
                    reopenedCandidate &&
                    !string.IsNullOrWhiteSpace(candidateCurrentJob))
                {
                    var candidateJobs = ParseFlexJobCodes(candidateSlot.FlexJobsSpec);
                    if (candidateJobs.Count == 0)
                    {
                        candidateJobs = new[] { candidateCurrentJob };
                    }
                    else if (!candidateJobs.Contains(candidateCurrentJob, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateJobs = candidateJobs
                            .Append(candidateCurrentJob)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }

                    var hasAlternativeJob = candidateJobs.Any(job =>
                        !string.Equals(job, candidateCurrentJob, StringComparison.OrdinalIgnoreCase));
                    if (hasAlternativeJob && blockedJobs.Contains(candidateCurrentJob))
                    {
                        blockedJobsForCandidate = blockedJobs
                            .Where(job => !string.Equals(job, candidateCurrentJob, StringComparison.OrdinalIgnoreCase))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }

                var canMove = flexJobs.Any(job =>
                    IsJobAllowedForPosition(candidatePosition, job, recruitPositions) &&
                    (string.IsNullOrWhiteSpace(candidateSlot.RequiredJob) ||
                     string.Equals(candidateSlot.RequiredJob, job, StringComparison.OrdinalIgnoreCase)) &&
                    (!post.DisallowDuplicateJobs || !blockedJobsForCandidate.Contains(job)));
                if (canMove)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visitingClosedPositions.Remove(normalizedClosedPosition);
        }
    }

    private static bool CanRelocateFlexibleOccupantForClosedFlex(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        RecruitSlot occupiedSlot,
        IReadOnlyList<string> recruitPositions)
    {
        if (!occupiedSlot.UserId.HasValue)
        {
            return true;
        }

        var occupyingUserId = occupiedSlot.UserId.Value;
        var occupyingFlexPositions = occupyingUserId == post.CreatorUserId
            ? ResolveFlexCandidatePositions(post.HostFlexSpec ?? string.Empty, slots)
            : ResolveFlexCandidatePositions(occupiedSlot.FlexSpec ?? string.Empty, slots);
        if (occupyingFlexPositions.Count < 2 ||
            !occupyingFlexPositions.Contains(occupiedSlot.Position, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var occupyingJobs = occupyingUserId == post.CreatorUserId
            ? ParseFlexJobCodes(post.HostFlexJobsSpec)
            : ParseFlexJobCodes(occupiedSlot.FlexJobsSpec);
        var currentOccupyingJob = occupiedSlot.Job?.Trim().ToUpperInvariant();
        if (occupyingJobs.Count == 0 && !string.IsNullOrWhiteSpace(currentOccupyingJob))
        {
            occupyingJobs = new[] { currentOccupyingJob };
        }
        else if (!string.IsNullOrWhiteSpace(currentOccupyingJob) &&
                 !occupyingJobs.Contains(currentOccupyingJob, StringComparer.OrdinalIgnoreCase))
        {
            occupyingJobs = occupyingJobs
                .Append(currentOccupyingJob)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (occupyingJobs.Count == 0)
        {
            return false;
        }

        foreach (var alternate in slots.Where(slot =>
                     !string.Equals(slot.Position, occupiedSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                     occupyingFlexPositions.Contains(slot.Position, StringComparer.OrdinalIgnoreCase) &&
                     !slot.IsClosed &&
                     (!slot.UserId.HasValue || slot.UserId.Value == occupyingUserId)))
        {
            foreach (var candidateJob in occupyingJobs)
            {
                if (!IsJobAllowedForPosition(alternate.Position, candidateJob, recruitPositions))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(alternate.RequiredJob) &&
                    !string.Equals(alternate.RequiredJob, candidateJob, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (post.DisallowDuplicateJobs)
                {
                    var duplicateWithOthers = slots.Any(slot =>
                        !string.IsNullOrWhiteSpace(slot.Job) &&
                        string.Equals(slot.Job, candidateJob, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(slot.Position, occupiedSlot.Position, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(slot.Position, alternate.Position, StringComparison.OrdinalIgnoreCase) &&
                        ((slot.UserId.HasValue && slot.UserId.Value != occupyingUserId) ||
                         (slot.IsClosed && !slot.UserId.HasValue &&
                          ShouldReserveClosedSlotJobForDuplicateCheck(slot, recruitPositions))));
                    if (duplicateWithOthers)
                    {
                        continue;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static bool ShouldReserveClosedSlotJobForDuplicateCheck(
        RecruitSlot slot,
        IReadOnlyList<string> recruitPositions)
    {
        if (string.IsNullOrWhiteSpace(slot.Job))
        {
            return false;
        }

        var slotJobs = ParseFlexJobCodes(slot.FlexJobsSpec);
        if (slotJobs.Count == 0)
        {
            slotJobs = new[] { slot.Job.Trim().ToUpperInvariant() };
        }

        if (slotJobs.Count <= 1)
        {
            return true;
        }

        var flexPositions = ResolveFlexCandidatePositions(slot.FlexSpec ?? string.Empty, recruitPositions);
        return flexPositions.Count < 2;
    }

    private static bool HasReservedClosedJobConflictForJoin(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        string job,
        string targetPosition,
        IReadOnlyList<string> recruitPositions)
    {
        if (!post.DisallowDuplicateJobs || string.IsNullOrWhiteSpace(job))
        {
            return false;
        }

        var normalizedJob = job.Trim().ToUpperInvariant();
        return slots.Any(slot =>
            slot.IsClosed &&
            !slot.UserId.HasValue &&
            !string.IsNullOrWhiteSpace(slot.Job) &&
            string.Equals(slot.Job.Trim(), normalizedJob, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(slot.Position, targetPosition, StringComparison.OrdinalIgnoreCase) &&
            ShouldReserveClosedSlotJobForDuplicateCheck(slot, recruitPositions));
    }

    private static bool HasTakenJobConflictForJoin(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        string job,
        string targetPosition,
        long joiningUserId,
        IReadOnlyList<string> recruitPositions)
    {
        if (!post.DisallowDuplicateJobs || string.IsNullOrWhiteSpace(job))
        {
            return false;
        }

        var normalizedJob = job.Trim().ToUpperInvariant();
        return slots.Any(slot =>
        {
            if (string.IsNullOrWhiteSpace(slot.Job) ||
                !string.Equals(slot.Job.Trim(), normalizedJob, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (slot.UserId.HasValue)
            {
                if (slot.UserId.Value == joiningUserId)
                {
                    return false;
                }

                if (CanResolveTakenJobConflictByFlexibleOccupant(
                        post,
                        slots,
                        slot,
                        normalizedJob,
                        joiningUserId,
                        recruitPositions))
                {
                    return false;
                }

                return true;
            }

            if (string.Equals(slot.Position, targetPosition, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return slot.IsClosed &&
                   ShouldReserveClosedSlotJobForDuplicateCheck(slot, recruitPositions);
        });
    }

    private static bool CanResolveTakenJobConflictByFlexibleOccupant(
        RecruitPost post,
        IReadOnlyList<RecruitSlot> slots,
        RecruitSlot conflictingSlot,
        string takenJob,
        long joiningUserId,
        IReadOnlyList<string> recruitPositions)
    {
        if (!conflictingSlot.UserId.HasValue)
        {
            return false;
        }

        var occupyingUserId = conflictingSlot.UserId.Value;
        IReadOnlyList<string> occupyingJobs;
        if (occupyingUserId == post.CreatorUserId && !string.IsNullOrWhiteSpace(post.HostFlexSpec))
        {
            occupyingJobs = ParseFlexJobCodes(post.HostFlexJobsSpec);
        }
        else if (!string.IsNullOrWhiteSpace(conflictingSlot.FlexSpec))
        {
            occupyingJobs = ParseFlexJobCodes(conflictingSlot.FlexJobsSpec);
        }
        else
        {
            return false;
        }

        if (occupyingJobs.Count == 0 && !string.IsNullOrWhiteSpace(conflictingSlot.Job))
        {
            occupyingJobs = new[] { conflictingSlot.Job.Trim().ToUpperInvariant() };
        }

        if (occupyingJobs.Count < 2)
        {
            return false;
        }

        var normalizedTakenJob = takenJob.Trim().ToUpperInvariant();
        var alternativeJobs = occupyingJobs
            .Where(job => !string.Equals(job, normalizedTakenJob, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (alternativeJobs.Length == 0)
        {
            return false;
        }

        foreach (var alternativeJob in alternativeJobs)
        {
            if (!IsJobAllowedForPosition(conflictingSlot.Position, alternativeJob, recruitPositions))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(conflictingSlot.RequiredJob) &&
                !string.Equals(conflictingSlot.RequiredJob, alternativeJob, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var conflictExists = slots.Any(slot =>
            {
                if (slot.Id == conflictingSlot.Id ||
                    string.IsNullOrWhiteSpace(slot.Job) ||
                    !string.Equals(slot.Job.Trim(), alternativeJob, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (slot.UserId.HasValue)
                {
                    return slot.UserId.Value != occupyingUserId &&
                           slot.UserId.Value != joiningUserId;
                }

                return slot.IsClosed &&
                       ShouldReserveClosedSlotJobForDuplicateCheck(slot, recruitPositions);
            });
            if (!conflictExists)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetOrderedPositions(IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return RecruitmentRules.Positions;
        }

        var set = new HashSet<string>(slots.Select(slot => slot.Position), StringComparer.OrdinalIgnoreCase);
        var ordered = RecruitmentRules.Positions
            .Where(position => set.Contains(position))
            .ToList();

        if (ordered.Count == 0)
        {
            ordered = slots
                .Select(slot => slot.Position)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return ordered;
    }

    private static bool TryParseRecruitSlotsForCreate(
        string? input,
        out IReadOnlyList<string> positions,
        out IReadOnlyDictionary<string, string> requiredJobs,
        out string? error)
    {
        requiredJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input))
        {
            positions = RecruitmentRules.Positions;
            error = null;
            return true;
        }

        var tokens = input.Split(new[] { ' ', '　', ',', '，', '/', '／', '|', '｜', '\n', '\r', '\t', '・', '、' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var expanded = new List<string>();
        var requiredRaw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();

        foreach (var token in tokens)
        {
            var normalized = token.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            var sepIndex = normalized.IndexOf(':');
            if (sepIndex < 0)
            {
                sepIndex = normalized.IndexOf('：');
            }

            if (sepIndex >= 0)
            {
                var rawPosition = normalized[..sepIndex].Trim();
                var rawJob = normalized[(sepIndex + 1)..].Trim();
                var position = rawPosition.ToUpperInvariant();
                if (!RecruitmentRules.Positions.Any(item => string.Equals(item, position, StringComparison.OrdinalIgnoreCase)))
                {
                    invalid.Add(normalized);
                    continue;
                }

                if (requiredRaw.TryGetValue(position, out var existingRaw) &&
                    !string.Equals(existingRaw, rawJob, StringComparison.OrdinalIgnoreCase))
                {
                    invalid.Add(normalized);
                    continue;
                }

                expanded.Add(position);
                requiredRaw[position] = rawJob;
                continue;
            }

            var upper = normalized.ToUpperInvariant();
            switch (upper)
            {
                case "T":
                    expanded.Add("MT");
                    expanded.Add("ST");
                    continue;
                case "H":
                    expanded.Add("H1");
                    expanded.Add("H2");
                    continue;
                case "D":
                    expanded.Add("D1");
                    expanded.Add("D2");
                    expanded.Add("D3");
                    expanded.Add("D4");
                    continue;
            }

            if (RecruitmentRules.Positions.Any(position => string.Equals(position, upper, StringComparison.OrdinalIgnoreCase)))
            {
                expanded.Add(upper);
            }
            else
            {
                invalid.Add(normalized);
            }
        }

        if (invalid.Count > 0)
        {
            error = $"ジョブ指定の入力が不正です: {string.Join(", ", invalid)}（例: MT:暗 ST:ナ H1:白）";
            positions = RecruitmentRules.Positions;
            requiredJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        var expandedSet = new HashSet<string>(expanded, StringComparer.OrdinalIgnoreCase);
        var ordered = RecruitmentRules.Positions
            .Where(position => expandedSet.Contains(position))
            .ToList();

        if (ordered.Count == 0)
        {
            error = "募集ポジションを1つ以上指定してください。";
            positions = RecruitmentRules.Positions;
            requiredJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in ordered)
        {
            if (requiredRaw.TryGetValue(position, out var rawJob))
            {
                if (!TryNormalizeJobCodeForPosition(position, rawJob, ordered, out var jobCode))
                {
                    invalid.Add($"{position}:{rawJob}");
                    continue;
                }

                filtered[position] = jobCode!;
            }
        }

        if (invalid.Count > 0)
        {
            error = $"ジョブ指定の入力が不正です: {string.Join(", ", invalid)}（例: MT:暗 ST:ナ H1:白）";
            positions = RecruitmentRules.Positions;
            requiredJobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        positions = ordered;
        requiredJobs = filtered;
        error = null;
        return true;
    }

    private static bool TryNormalizeJobCodeForPosition(string position, string rawJob, IReadOnlyCollection<string>? recruitPositions, out string? jobCode)
    {
        jobCode = null;
        if (string.IsNullOrWhiteSpace(rawJob))
        {
            return false;
        }

        if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
        {
            return false;
        }

        var trimmed = rawJob.Trim();
        var matched = jobs.FirstOrDefault(job =>
            string.Equals(job.Code, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(job.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            return false;
        }

        jobCode = matched.Code;
        return true;
    }

    private static bool TryNormalizeRecruitDataCenter(string? input, out string dataCenter, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            dataCenter = string.Empty;
            error = $"開催DCを入力してください（{string.Join(", ", DcResolver.KnownDcNames)}）";
            return false;
        }

        var trimmed = input.Trim();
        var matched = DcResolver.KnownDcNames
            .FirstOrDefault(dc => string.Equals(dc, trimmed, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            dataCenter = string.Empty;
            error = $"開催DCは次から選択してください: {string.Join(", ", DcResolver.KnownDcNames)}";
            return false;
        }

        dataCenter = matched;
        error = null;
        return true;
    }

    private static bool TryNormalizeRecruitSchedule(string? input, out string schedule, out DateTimeOffset? scheduleAt, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            schedule = string.Empty;
            scheduleAt = null;
            error = $"開始日時を入力してください（{RecruitScheduleFormat} / HH:mm / 今日HH:mm / 明日HH:mm / 今）";
            return false;
        }

        var trimmed = input
            .Trim()
            .Replace('：', ':')
            .Replace('／', '/');
        var nowJst = DateTimeOffset.UtcNow.ToOffset(JapanTimeOffset);

        if (string.Equals(trimmed, "今", StringComparison.Ordinal))
        {
            scheduleAt = DateTimeOffset.UtcNow;
            schedule = scheduleAt.Value
                .ToOffset(JapanTimeOffset)
                .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
            error = null;
            return true;
        }

        DateTimeOffset jst;
        if (DateTime.TryParseExact(
                trimmed,
                RecruitScheduleFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var fullParsed))
        {
            jst = new DateTimeOffset(fullParsed, JapanTimeOffset);
        }
        else if (TryParseRelativeTimeSchedule(trimmed, nowJst, out var relativeParsed))
        {
            jst = relativeParsed;
        }
        else if (TryParseTimeOnlySchedule(trimmed, nowJst, out var timeOnlyParsed))
        {
            jst = timeOnlyParsed;
        }
        else
        {
            schedule = string.Empty;
            scheduleAt = null;
            error = $"開始日時は {RecruitScheduleFormat} / HH:mm / 今日HH:mm / 明日HH:mm / 今 を入力してください。";
            return false;
        }

        scheduleAt = jst.ToUniversalTime();
        if (scheduleAt.Value < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            schedule = string.Empty;
            scheduleAt = null;
            error = "開始日時に過去は指定できません。現在以降の日時を入力してください。";
            return false;
        }

        schedule = jst
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
        error = null;
        return true;
    }

    private static bool TryParseRelativeTimeSchedule(string input, DateTimeOffset nowJst, out DateTimeOffset scheduleJst)
    {
        scheduleJst = default;
        var normalized = input.Trim();
        var dayOffset = 0;
        string? timeText = null;

        if (normalized.StartsWith("今日", StringComparison.Ordinal))
        {
            dayOffset = 0;
            timeText = normalized["今日".Length..].Trim();
        }
        else if (normalized.StartsWith("明日", StringComparison.Ordinal))
        {
            dayOffset = 1;
            timeText = normalized["明日".Length..].Trim();
        }

        if (timeText is null || !TryParseTimeText(timeText, out var hour, out var minute))
        {
            return false;
        }

        var targetDate = nowJst.Date.AddDays(dayOffset);
        scheduleJst = new DateTimeOffset(
            targetDate.Year,
            targetDate.Month,
            targetDate.Day,
            hour,
            minute,
            0,
            JapanTimeOffset);
        return true;
    }

    private static bool TryParseTimeOnlySchedule(string input, DateTimeOffset nowJst, out DateTimeOffset scheduleJst)
    {
        scheduleJst = default;
        if (!TryParseTimeText(input.Trim(), out var hour, out var minute))
        {
            return false;
        }

        var today = nowJst.Date;
        scheduleJst = new DateTimeOffset(today.Year, today.Month, today.Day, hour, minute, 0, JapanTimeOffset);
        if (scheduleJst < nowJst.AddMinutes(-1))
        {
            scheduleJst = scheduleJst.AddDays(1);
        }

        return true;
    }

    private static bool TryParseTimeText(string input, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (!DateTime.TryParseExact(
                input,
                new[] { "H:mm", "HH:mm", "H:m", "HH:m" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        hour = parsed.Hour;
        minute = parsed.Minute;
        return true;
    }

    private bool IsRecruitCancelPrivilegedUser()
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            return false;
        }

        if (guildUser.GuildPermissions.Administrator)
        {
            return true;
        }

        var roleNames = _options.BuildManagementRoleNameSet();
        if (roleNames.Count == 0)
        {
            return false;
        }

        return guildUser.Roles.Any(role => roleNames.Contains(role.Name));
    }

    private static string BuildRecruitCreatorLabel(SocketGuild guild, long creatorUserId)
    {
        var userId = ToUlong(creatorUserId);
        if (userId == 0)
        {
            return "unknown";
        }

        var user = guild.GetUser(userId);
        return user?.DisplayName
            ?? user?.Username
            ?? $"u:{userId}";
    }

    private static string BuildCompactScheduleLabel(string? scheduleText)
    {
        if (string.IsNullOrWhiteSpace(scheduleText))
        {
            return "--/-- --:--";
        }

        var trimmed = scheduleText.Trim();
        if (DateTime.TryParseExact(
                trimmed,
                RecruitScheduleFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return parsed.ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (trimmed.Length >= 16)
        {
            return trimmed[5..];
        }

        return trimmed;
    }

    private bool CanManageRecruitPost(RecruitPost post)
    {
        if (post.CreatorUserId == ToDbId(Context.User.Id))
        {
            return true;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            return false;
        }

        if (guildUser.GuildPermissions.Administrator)
        {
            return true;
        }

        var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
        return guildUser.Roles.Any(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SendRecruitAuditAsync(RecruitPost post, string title, string operation)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var channelId = ToUlong(post.ChannelId);
        var messageId = ToUlong(post.MessageId);
        var safeTitle = string.IsNullOrWhiteSpace(post.Title) ? "(無題)" : post.Title;
        var description = string.Join('\n', new[]
        {
            $"👤 実行者: {Context.User.Mention} ({Context.User})",
            $"📝 募集: {TruncateLabel(safeTitle, 80)}",
            $"💬 チャンネル: {MentionChannel(channelId)}",
            $"🔗 メッセージ: {BuildRecruitMessageLink(Context.Guild.Id, channelId, messageId)}",
            $"🛠️ 操作: {operation}"
        });

        await _audit.SendAsync(Context.Guild, title, description, Color.Orange, Context.User);
    }

    private async Task SendRecruitStatusTransitionAuditAsync(
        RecruitPost post,
        RecruitPostStatus beforeStatus,
        RecruitPostStatus afterStatus,
        string trigger,
        string? note = null)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var channelId = ToUlong(post.ChannelId);
        var messageId = ToUlong(post.MessageId);
        var lines = new List<string>
        {
            $"📝 募集: {TruncateLabel(string.IsNullOrWhiteSpace(post.Title) ? "(無題)" : post.Title, 80)}",
            $"🔄 状態: {FormatRecruitStatusLabel(beforeStatus)} -> {FormatRecruitStatusLabel(afterStatus)}",
            $"🧭 トリガー: {trigger}",
            $"💬 チャンネル: {MentionChannel(channelId)}",
            $"🔗 メッセージ: {BuildRecruitMessageLink(Context.Guild.Id, channelId, messageId)}"
        };
        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add($"📝 備考: {note}");
        }

        await _audit.SendAsync(Context.Guild, "募集状態遷移", string.Join('\n', lines), Color.Teal, Context.User);
    }

    private async Task LogRecruitPermissionDeniedAsync(RecruitPost post, string operation, string reason)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var channelId = ToUlong(post.ChannelId);
        var messageId = ToUlong(post.MessageId);
        var description = string.Join('\n', new[]
        {
            $"👤 実行者: {Context.User.Mention} ({Context.User})",
            $"📝 募集: {TruncateLabel(string.IsNullOrWhiteSpace(post.Title) ? "(無題)" : post.Title, 80)}",
            $"🛠️ 操作: {operation}",
            $"💬 チャンネル: {MentionChannel(channelId)}",
            $"🔗 メッセージ: {BuildRecruitMessageLink(Context.Guild.Id, channelId, messageId)}",
            $"⛔ 理由: {reason}"
        });
        await _audit.SendAsync(Context.Guild, "権限拒否", description, Color.Orange, Context.User);
    }

    private static string BuildRecruitPostEditDetail(
        string? previousTitle,
        string? currentTitle,
        string? previousDataCenter,
        string? currentDataCenter,
        string? previousSchedule,
        string? currentSchedule,
        string? previousNote,
        string? currentNote)
    {
        var changes = new List<string>();
        AppendRecruitFieldChange(changes, "タイトル", previousTitle, currentTitle);
        AppendRecruitFieldChange(changes, "DC", previousDataCenter, currentDataCenter);
        AppendRecruitFieldChange(changes, "開始", previousSchedule, currentSchedule);
        AppendRecruitFieldChange(changes, "コメント", previousNote, currentNote);

        if (changes.Count == 0)
        {
            return "内容に変更はありません（同値更新）。";
        }

        return string.Join('\n', changes);
    }

    private static void AppendRecruitFieldChange(List<string> changes, string label, string? before, string? after)
    {
        var normalizedBefore = NormalizeRecruitAuditValue(before);
        var normalizedAfter = NormalizeRecruitAuditValue(after);
        if (string.Equals(normalizedBefore, normalizedAfter, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add($"{label}: {normalizedBefore} -> {normalizedAfter}");
    }

    private static string NormalizeRecruitAuditValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(なし)";
        }

        var flattened = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return TruncateLabel(flattened, 80);
    }

    private static string BuildRecruitMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    private static string FormatRecruitStatusLabel(RecruitPostStatus status)
    {
        return status switch
        {
            RecruitPostStatus.Open => "OPEN(募集中)",
            RecruitPostStatus.Full => "FULL(満員)",
            RecruitPostStatus.Closed => "CLOSED(終了)",
            RecruitPostStatus.Canceled => "CANCELED(中断)",
            _ => status.ToString()
        };
    }

    private async Task<int> DeleteAllChannelMessagesAsync(ITextChannel channel)
    {
        var deleted = 0;
        ulong? beforeMessageId = null;
        var now = DateTimeOffset.UtcNow;
        var bulkDeleteLimit = TimeSpan.FromDays(14);

        while (true)
        {
            var messages = (beforeMessageId.HasValue
                    ? await channel.GetMessagesAsync(beforeMessageId.Value, Direction.Before, 100).FlattenAsync()
                    : await channel.GetMessagesAsync(100).FlattenAsync())
                .OfType<IMessage>()
                .OrderByDescending(message => message.Id)
                .ToList();

            if (messages.Count == 0)
            {
                break;
            }

            var bulkTargets = messages
                .Where(message => now - message.Timestamp <= bulkDeleteLimit)
                .ToList();
            var singleTargets = messages
                .Where(message => now - message.Timestamp > bulkDeleteLimit)
                .ToList();

            if (bulkTargets.Count > 0)
            {
                foreach (var chunk in bulkTargets.Chunk(100))
                {
                    try
                    {
                        await channel.DeleteMessagesAsync(chunk);
                        deleted += chunk.Length;
                    }
                    catch
                    {
                        foreach (var message in chunk)
                        {
                            try
                            {
                                await channel.DeleteMessageAsync(message.Id);
                                deleted++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete message {MessageId} in channel {ChannelId}.", message.Id, channel.Id);
                            }
                        }
                    }
                }
            }

            foreach (var message in singleTargets)
            {
                try
                {
                    await channel.DeleteMessageAsync(message.Id);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old message {MessageId} in channel {ChannelId}.", message.Id, channel.Id);
                }
            }

            beforeMessageId = messages[^1].Id;
            if (messages.Count < 100)
            {
                break;
            }
        }

        return deleted;
    }

    private static bool TryParseFlexPositionToken(string position, out string flexSpec)
    {
        flexSpec = string.Empty;
        if (string.IsNullOrWhiteSpace(position))
        {
            return false;
        }

        const string prefix = "FLEX:";
        var normalized = position.Trim().ToUpperInvariant();
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = normalized[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        flexSpec = parsed;
        return true;
    }

    private static IReadOnlyList<string> ResolveFlexCandidatePositions(string flexSpec, IReadOnlyList<RecruitSlot> slots)
    {
        var ordered = GetOrderedPositions(slots);
        return ResolveFlexCandidatePositions(flexSpec, ordered);
    }

    private static IReadOnlyList<string> ResolveFlexCandidatePositions(string flexSpec, IReadOnlyList<string> orderedPositions)
    {
        if (orderedPositions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var requested = ParseFlexSpecPositions(flexSpec);
        if (requested.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (requested.Contains("ANY", StringComparer.OrdinalIgnoreCase))
        {
            return orderedPositions;
        }

        var orderedSet = new HashSet<string>(orderedPositions, StringComparer.OrdinalIgnoreCase);
        return requested
            .Where(position => orderedSet.Contains(position))
            .ToList();
    }

    private static string BuildFlexSelectionLabel(string flexSpec)
    {
        var positions = ParseFlexSpecPositions(flexSpec);
        if (positions.Count == 0 || positions.Contains("ANY", StringComparer.OrdinalIgnoreCase))
        {
            return "FLEX";
        }

        if (positions.Count == 2 &&
            positions.Contains("D1", StringComparer.OrdinalIgnoreCase) &&
            positions.Contains("D2", StringComparer.OrdinalIgnoreCase))
        {
            return "D1/D2どちらでもOK";
        }

        return $"{string.Join('/', positions)} FLEX";
    }

    private static bool IsD1D2FlexSpec(string flexSpec)
    {
        var positions = ParseFlexSpecPositions(flexSpec);
        return positions.Count == 2 &&
               positions.Contains("D1", StringComparer.OrdinalIgnoreCase) &&
               positions.Contains("D2", StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseFlexSpecPositions(string? flexSpec)
    {
        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return Array.Empty<string>();
        }

        var normalized = flexSpec.Trim();
        if (normalized.StartsWith("FLEX:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..].Trim();
        }
        else if (normalized.StartsWith("FLEX(", StringComparison.OrdinalIgnoreCase) &&
                 normalized.EndsWith(")", StringComparison.Ordinal))
        {
            normalized = normalized[5..^1].Trim();
        }
        else if (string.Equals(normalized, "FLEX", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "ANY";
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        return normalized
            .Split(new[] { ',', '/', '|', '，', '／', '｜', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => !string.IsNullOrWhiteSpace(position))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseFlexJobCodes(string? jobsSpec)
    {
        if (string.IsNullOrWhiteSpace(jobsSpec))
        {
            return Array.Empty<string>();
        }

        return jobsSpec
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsMatchingFlexSelector(
        string? flexSpec,
        IReadOnlySet<string> selectorPositionSet,
        IReadOnlyList<string> orderedPositions)
    {
        if (selectorPositionSet.Count == 0 || string.IsNullOrWhiteSpace(flexSpec))
        {
            return false;
        }

        var resolvedPositions = ResolveFlexCandidatePositions(flexSpec, orderedPositions);
        return resolvedPositions.Count == selectorPositionSet.Count &&
               resolvedPositions.All(selectorPositionSet.Contains);
    }

    private static bool IsMatchingPrefilledMemberSelector(
        string position,
        RecruitPreFilledMember member,
        IReadOnlySet<string> selectorPositionSet,
        IReadOnlyList<string> orderedPositions)
    {
        if (!string.IsNullOrWhiteSpace(member.FlexSpec))
        {
            return IsMatchingFlexSelector(member.FlexSpec, selectorPositionSet, orderedPositions);
        }

        return selectorPositionSet.Count == 1 &&
               selectorPositionSet.Contains(position);
    }

    private static string ResolvePreferredPrefillExternalAnchor(
        IReadOnlyList<string> eligiblePositions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        IReadOnlySet<string> selectorPositionSet,
        IReadOnlyList<string> orderedPositions,
        string? hostPositionRaw)
    {
        if (eligiblePositions.Count == 0)
        {
            return string.Empty;
        }

        var normalizedHostPosition = hostPositionRaw?.Trim().ToUpperInvariant();
        var freeAnchor = eligiblePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !preFilledSlots.ContainsKey(position) &&
                !preFilledMembers.ContainsKey(position) &&
                !preFilledFlexSlots.ContainsKey(position));
        if (!string.IsNullOrWhiteSpace(freeAnchor))
        {
            return freeAnchor;
        }

        freeAnchor = eligiblePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !preFilledSlots.ContainsKey(position) &&
                !preFilledMembers.ContainsKey(position) &&
                !preFilledFlexSlots.ContainsKey(position));
        if (!string.IsNullOrWhiteSpace(freeAnchor))
        {
            return freeAnchor;
        }

        var reusableAnchor = eligiblePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
            {
                if (string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (preFilledSlots.ContainsKey(position) || preFilledMembers.ContainsKey(position))
                {
                    return false;
                }

                if (!preFilledFlexSlots.TryGetValue(position, out var existingFlex))
                {
                    return false;
                }

                var existingSet = ResolveFlexCandidatePositions(existingFlex.FlexSpec, orderedPositions)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return existingSet.SetEquals(selectorPositionSet);
            });
        if (!string.IsNullOrWhiteSpace(reusableAnchor))
        {
            return reusableAnchor;
        }

        reusableAnchor = eligiblePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
            {
                if (preFilledSlots.ContainsKey(position) || preFilledMembers.ContainsKey(position))
                {
                    return false;
                }

                if (!preFilledFlexSlots.TryGetValue(position, out var existingFlex))
                {
                    return false;
                }

                var existingSet = ResolveFlexCandidatePositions(existingFlex.FlexSpec, orderedPositions)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return existingSet.SetEquals(selectorPositionSet);
            });
        return reusableAnchor ?? string.Empty;
    }

    private static string ResolvePreferredPrefillMemberAnchor(
        IReadOnlyList<string> candidatePositions,
        IReadOnlyDictionary<string, string> preFilledSlots,
        IReadOnlyDictionary<string, RecruitPreFilledMember> preFilledMembers,
        IReadOnlyDictionary<string, RecruitPreFilledFlexSlot> preFilledFlexSlots,
        ulong selectedUserId,
        string? hostPositionRaw)
    {
        if (candidatePositions.Count == 0)
        {
            return string.Empty;
        }

        var normalizedHostPosition = hostPositionRaw?.Trim().ToUpperInvariant();
        var freeAnchor = candidatePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !preFilledSlots.ContainsKey(position) &&
                !preFilledMembers.ContainsKey(position) &&
                !preFilledFlexSlots.ContainsKey(position));
        if (!string.IsNullOrWhiteSpace(freeAnchor))
        {
            return freeAnchor;
        }

        freeAnchor = candidatePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !preFilledSlots.ContainsKey(position) &&
                !preFilledMembers.ContainsKey(position) &&
                !preFilledFlexSlots.ContainsKey(position));
        if (!string.IsNullOrWhiteSpace(freeAnchor))
        {
            return freeAnchor;
        }

        var reusableAnchor = candidatePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !string.Equals(position, normalizedHostPosition, StringComparison.OrdinalIgnoreCase) &&
                !preFilledSlots.ContainsKey(position) &&
                preFilledMembers.TryGetValue(position, out var existingMember) &&
                existingMember.UserId == selectedUserId);
        if (!string.IsNullOrWhiteSpace(reusableAnchor))
        {
            return reusableAnchor;
        }

        reusableAnchor = candidatePositions
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(position =>
                !preFilledSlots.ContainsKey(position) &&
                preFilledMembers.TryGetValue(position, out var existingMember) &&
                existingMember.UserId == selectedUserId);
        return reusableAnchor ?? string.Empty;
    }

    private static bool ShouldReserveFlexibleJobForDuplicateCheck(
        string? flexSpec,
        IReadOnlyList<string> candidateJobs,
        IReadOnlyList<string> orderedPositions)
    {
        if (candidateJobs.Count <= 1)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(flexSpec))
        {
            return true;
        }

        var positions = ResolveFlexCandidatePositions(flexSpec, orderedPositions);
        return positions.Count < 2;
    }

    private static bool ShouldReserveHostJobForDuplicateCheck(RecruitCreateDraftContext draft)
    {
        if (string.IsNullOrWhiteSpace(draft.HostJob))
        {
            return false;
        }

        var hostJobs = ParseFlexJobCodes(draft.HostFlexJobsSpec);
        if (hostJobs.Count == 0)
        {
            hostJobs = new[] { draft.HostJob.Trim().ToUpperInvariant() };
        }

        return ShouldReserveFlexibleJobForDuplicateCheck(draft.HostFlexSpec, hostJobs, draft.Positions);
    }

    private static bool ShouldReservePrefilledMemberJobForDuplicateCheck(
        RecruitPreFilledMember member,
        IReadOnlyList<string> orderedPositions)
    {
        if (string.IsNullOrWhiteSpace(member.Job))
        {
            return false;
        }

        var memberJobs = ParseFlexJobCodes(member.FlexJobsSpec);
        if (memberJobs.Count == 0)
        {
            memberJobs = new[] { member.Job.Trim().ToUpperInvariant() };
        }

        return ShouldReserveFlexibleJobForDuplicateCheck(member.FlexSpec, memberJobs, orderedPositions);
    }

    private static bool ShouldReservePrefilledFlexSlotJobForDuplicateCheck(
        RecruitPreFilledFlexSlot flexSlot,
        IReadOnlyList<string> orderedPositions)
    {
        if (string.IsNullOrWhiteSpace(flexSlot.Job))
        {
            return false;
        }

        var flexJobs = ParseFlexJobCodes(flexSlot.FlexJobsSpec);
        if (flexJobs.Count == 0)
        {
            flexJobs = new[] { flexSlot.Job.Trim().ToUpperInvariant() };
        }

        return ShouldReserveFlexibleJobForDuplicateCheck(flexSlot.FlexSpec, flexJobs, orderedPositions);
    }

    private static IReadOnlyList<string> ResolveHostFlexiblePositionsForJobs(
        IReadOnlyList<string> candidatePositions,
        RecruitCreateDraftContext draft,
        IReadOnlyCollection<string> jobCodes)
    {
        var normalizedJobs = jobCodes
            .Select(job => job.Trim().ToUpperInvariant())
            .Where(job => !string.IsNullOrWhiteSpace(job))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedJobs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var compatible = new List<string>();
        foreach (var position in candidatePositions)
        {
            if (draft.PreFilledMembers.ContainsKey(position) ||
                draft.PreFilledSlots.ContainsKey(position) ||
                IsPositionCoveredByCreatePrefilledFlexSlot(position, draft))
            {
                continue;
            }

            if (!RecruitmentRules.TryGetJobsForRecruit(position, draft.Positions, out var jobs))
            {
                continue;
            }

            var hasRequiredJob = draft.RequiredJobs.TryGetValue(position, out var requiredHostJob) &&
                                 !string.IsNullOrWhiteSpace(requiredHostJob);
            var allowedJobs = hasRequiredJob
                ? jobs.Where(job => string.Equals(job.Code, requiredHostJob, StringComparison.OrdinalIgnoreCase)).ToArray()
                : jobs;
            if (!allowedJobs.Any(job => normalizedJobs.Contains(job.Code)))
            {
                continue;
            }

            compatible.Add(position.Trim().ToUpperInvariant());
        }

        return compatible
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsJobAllowedForPosition(string position, string jobCode, IReadOnlyCollection<string> recruitPositions)
    {
        if (!RecruitmentRules.TryGetJobsForRecruit(position, recruitPositions, out var jobs))
        {
            return false;
        }

        return jobs.Any(job => string.Equals(job.Code, jobCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveAnchorCompatibleJobCode(
        string anchorPosition,
        IReadOnlyCollection<string> candidateJobs,
        IReadOnlyCollection<string> recruitPositions)
    {
        foreach (var job in candidateJobs)
        {
            var normalized = job?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (IsJobAllowedForPosition(anchorPosition, normalized, recruitPositions))
            {
                return normalized;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveCreateHostCandidatePositions(string hostSelector, IReadOnlyList<string> positions)
    {
        if (TryParseFlexPositionToken(hostSelector, out var flexSpec))
        {
            return ResolveFlexCandidatePositions(flexSpec, positions);
        }

        return positions.Contains(hostSelector, StringComparer.OrdinalIgnoreCase)
            ? new[] { hostSelector.Trim().ToUpperInvariant() }
            : Array.Empty<string>();
    }

    private static IReadOnlyList<string> ResolveCreatePrefillCandidatePositions(string selector, RecruitCreateDraftContext draft)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string> requested = TryParseFlexPositionToken(selector, out var flexSpec)
            ? ResolveFlexCandidatePositions(flexSpec, draft.Positions)
            : draft.Positions.Contains(selector, StringComparer.OrdinalIgnoreCase)
                ? new[] { selector.Trim().ToUpperInvariant() }
                : Array.Empty<string>();
        if (requested.Count == 0)
        {
            return Array.Empty<string>();
        }

        var allowHostPosition = CanIncludeHostPositionInCreatePrefillSelector(requested, draft);
        return draft.Positions
            .Where(position => requested.Contains(position, StringComparer.OrdinalIgnoreCase))
            .Where(position => allowHostPosition || !string.Equals(position, draft.HostPosition, StringComparison.OrdinalIgnoreCase))
            .Select(position => position.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPositionCoveredByCreatePrefilledFlexSlot(string position, RecruitCreateDraftContext draft)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return false;
        }

        return draft.PreFilledFlexSlots.Any(pair =>
            ResolveFlexCandidatePositions(pair.Value.FlexSpec, draft.Positions)
                .Contains(position, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryFindCreatePrefilledFlexCoveringPosition(
        string positionRaw,
        RecruitCreateDraftContext draft,
        out string label)
    {
        label = string.Empty;
        var position = positionRaw?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(position))
        {
            return false;
        }

        foreach (var anchor in draft.Positions)
        {
            if (draft.PreFilledMembers.TryGetValue(anchor, out var member) &&
                !string.IsNullOrWhiteSpace(member.Job) &&
                !string.IsNullOrWhiteSpace(member.FlexSpec))
            {
                var flexPositions = ResolveFlexCandidatePositions(member.FlexSpec, draft.Positions);
                if (flexPositions.Count >= 2 &&
                    flexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
                {
                    var normalizedFlexSpec = string.Join(',', flexPositions);
                    label = $"{BuildFlexSelectionLabel(normalizedFlexSpec)}（{member.UserName}）";
                    return true;
                }
            }

            if (draft.PreFilledFlexSlots.TryGetValue(anchor, out var flexSlot) &&
                !string.IsNullOrWhiteSpace(flexSlot.Job) &&
                !string.IsNullOrWhiteSpace(flexSlot.FlexSpec))
            {
                var flexPositions = ResolveFlexCandidatePositions(flexSlot.FlexSpec, draft.Positions);
                if (flexPositions.Count >= 2 &&
                    flexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
                {
                    var normalizedFlexSpec = string.Join(',', flexPositions);
                    label = $"{BuildFlexSelectionLabel(normalizedFlexSpec)}（外部）";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCreatePrefilledFlexAnchoredAtPosition(
        string positionRaw,
        RecruitCreateDraftContext draft,
        out string label)
    {
        label = string.Empty;
        var position = positionRaw?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(position))
        {
            return false;
        }

        if (draft.PreFilledMembers.TryGetValue(position, out var member) &&
            !string.IsNullOrWhiteSpace(member.Job) &&
            !string.IsNullOrWhiteSpace(member.FlexSpec))
        {
            var flexPositions = ResolveFlexCandidatePositions(member.FlexSpec, draft.Positions);
            if (flexPositions.Count >= 2 &&
                flexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
            {
                var normalizedFlexSpec = string.Join(',', flexPositions);
                label = $"{BuildFlexSelectionLabel(normalizedFlexSpec)}（{member.UserName}）";
                return true;
            }
        }

        if (draft.PreFilledFlexSlots.TryGetValue(position, out var flexSlot) &&
            !string.IsNullOrWhiteSpace(flexSlot.Job) &&
            !string.IsNullOrWhiteSpace(flexSlot.FlexSpec))
        {
            var flexPositions = ResolveFlexCandidatePositions(flexSlot.FlexSpec, draft.Positions);
            if (flexPositions.Count >= 2 &&
                flexPositions.Contains(position, StringComparer.OrdinalIgnoreCase))
            {
                var normalizedFlexSpec = string.Join(',', flexPositions);
                label = $"{BuildFlexSelectionLabel(normalizedFlexSpec)}（外部）";
                return true;
            }
        }

        return false;
    }

    private static bool CanIncludeHostPositionInCreatePrefillSelector(IReadOnlyList<string> requestedPositions, RecruitCreateDraftContext draft)
        => CanIncludeHostPositionInCreatePrefillSelector(
            requestedPositions,
            draft.HostPosition,
            draft.HostFlexSpec,
            draft.Positions);

    private static bool CanIncludeHostPositionInCreatePrefillSelector(
        IReadOnlyList<string> requestedPositions,
        string? hostPositionRaw,
        string? hostFlexSpec,
        IReadOnlyList<string> orderedPositions)
        => RecruitmentFlexEngine.CanIncludeHostPositionInCreatePrefillSelector(
            requestedPositions,
            hostPositionRaw,
            hostFlexSpec,
            orderedPositions);

    private static string BuildHostSelectorLabel(string hostSelector)
    {
        return TryParseFlexPositionToken(hostSelector, out var flexSpec)
            ? BuildFlexSelectionLabel(flexSpec)
            : hostSelector.Trim().ToUpperInvariant();
    }

    private static IReadOnlyList<JobOption> SortJobsForD1D2Selector(IEnumerable<JobOption> jobs)
    {
        var priority = BuildD1D2JobPriorityMap();
        var unique = jobs
            .GroupBy(job => job.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return unique
            .OrderBy(job => priority.TryGetValue(job.Code, out var rank) ? rank : 1000)
            .ThenBy(job => job.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SortJobCodesForD1D2Selector(IEnumerable<string> jobCodes)
    {
        var priority = BuildD1D2JobPriorityMap();
        return jobCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => priority.TryGetValue(code, out var rank) ? rank : 1000)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<JobOption> SortJobsForFlexSelector(IEnumerable<JobOption> jobs)
    {
        var priority = BuildFlexJobPriorityMap();
        var unique = jobs
            .GroupBy(job => job.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return unique
            .OrderBy(job => priority.TryGetValue(job.Code, out var rank) ? rank : 1000)
            .ThenBy(job => job.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SortJobCodesForFlexSelector(IEnumerable<string> jobCodes)
    {
        var priority = BuildFlexJobPriorityMap();
        return jobCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => priority.TryGetValue(code, out var rank) ? rank : 1000)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> BuildFlexJobPriorityMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;
        var priorityPositions = new[] { "MT", "H1", "D1", "D3", "D4", "D2" };
        foreach (var position in priorityPositions)
        {
            if (!RecruitmentRules.JobsByPosition.TryGetValue(position, out var jobs))
            {
                continue;
            }

            foreach (var job in jobs)
            {
                map.TryAdd(job.Code, rank++);
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, int> BuildD1D2JobPriorityMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;

        if (RecruitmentRules.JobsByPosition.TryGetValue("D1", out var meleeJobs))
        {
            foreach (var job in meleeJobs)
            {
                map.TryAdd(job.Code, rank++);
            }
        }

        if (RecruitmentRules.JobsByPosition.TryGetValue("D4", out var casterJobs))
        {
            foreach (var job in casterJobs)
            {
                map.TryAdd(job.Code, rank++);
            }
        }

        return map;
    }

    private static (string? FlexSpec, ulong? MessageId) ParseFlexiblePositionValue(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        var flexSpec = parts[0].Trim().ToUpperInvariant();
        if (!ulong.TryParse(parts[1], out var messageId))
        {
            return (flexSpec, null);
        }

        return (flexSpec, messageId);
    }

    private static (string? Position, ulong? MessageId) ParsePositionValue(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        var position = parts[0].Trim().ToUpperInvariant();
        if (!ulong.TryParse(parts[1], out var messageId))
        {
            return (position, null);
        }

        return (position, messageId);
    }

    private static (string? Job, string? Position, ulong? MessageId) ParseExternalFillJobValue(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return (null, null, null);
        }

        var job = parts[0].Trim();
        var position = parts[1].Trim().ToUpperInvariant();
        if (!ulong.TryParse(parts[2], out var messageId))
        {
            return (job, position, null);
        }

        return (job, position, messageId);
    }

    private static (ulong? UserId, string? Job, string? Position, ulong? MessageId) ParseExternalFillMemberValue(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !ulong.TryParse(parts[0], out var userId))
        {
            return (null, null, null, null);
        }

        var job = parts[1].Trim();
        var position = parts[2].Trim().ToUpperInvariant();
        if (!ulong.TryParse(parts[3], out var messageId))
        {
            return (userId, job, position, null);
        }

        return (userId, job, position, messageId);
    }

    private static (string? Job, ulong? MessageId) ParseJobValue(string value)
    {
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        var job = parts[0].Trim();
        if (!ulong.TryParse(parts[1], out var messageId))
        {
            return (job, null);
        }

        return (job, messageId);
    }

    private static string MentionUser(long userId) => $"<@{ToUlong(userId)}>";
    private static string MentionChannel(ulong channelId) => $"<#{channelId}>";
    private static string MentionRole(ulong roleId) => $"<@&{roleId}>";
    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
}

public sealed class RecruitCreateModal : IModal
{
    public string Title => "募集を作成";

    [InputLabel("募集タイトル")]
    [ModalTextInput("recruit_title", maxLength: 80)]
    public string RecruitTitle { get; set; } = string.Empty;

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_note", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Note { get; set; }

}

public sealed class RecruitJoinCommentModal : IModal
{
    public string Title => "参加コメント";

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_join_comment", TextInputStyle.Paragraph, maxLength: 200)]
    [RequiredInput(false)]
    public string? Comment { get; set; }
}

public sealed class RecruitCommentEditModal : IModal
{
    public string Title => "コメント編集";

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_comment_edit", TextInputStyle.Paragraph, maxLength: 200)]
    [RequiredInput(false)]
    public string? Comment { get; set; }
}

public sealed class RecruitPostEditModal : IModal
{
    public string Title => "募集内容編集";

    [InputLabel("募集タイトル（必須）")]
    [ModalTextInput("recruit_post_edit_title", maxLength: 80)]
    public string RecruitTitle { get; set; } = string.Empty;

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_post_edit_note", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Note { get; set; }
}


