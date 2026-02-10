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
    private readonly DiscordOptions _options;
    private static readonly TimeSpan CreateDraftTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InteractionContextTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private const string RecruitScheduleFormat = "yyyy/MM/dd HH:mm";

    public RecruitmentModule(
        Db db,
        ILogger<RecruitmentModule> logger,
        UserProfileEmbedService profileEmbeds,
        RecruitJoinContextTracker joinTracker,
        RecruitCommentEditContextTracker commentEditTracker,
        RecruitCreateDraftContextTracker createDraftTracker,
        RecruitPostEditContextTracker postEditTracker,
        RecruitmentListService listService,
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
        var listPanel = await _db.GetGuildRecruitListPanelAsync(Context.Guild.Id);
        ulong? listMessageId = listPanel is null ? null : ToUlong(listPanel.MessageId);
        var listMessage = await _listService.EnsureListMessageAsync(Context.Guild, channel, listMessageId);
        if (listMessage is not null)
        {
            await _db.UpsertGuildRecruitListPanelAsync(Context.Guild.Id, channel.Id, listMessage.Id);
        }
        await FollowupAsync("このチャンネルに募集パネルを設置しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("cancel", "管理者が募集をキャンセルします（URL/ID指定）")]
    public Task CancelByAdminAsync(
        [Summary("message", "募集メッセージのURLまたはID")] string messageIdOrUrl)
        => HandleManageSelectionAsync(messageIdOrUrl, RecruitPostStatus.Canceled, "管理者により募集をキャンセルしました。", requireCreator: false);

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
        [Summary("schedule", "開始日時（例: 2026/02/09 21:00 / 今）")] string schedule)
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
            DateTimeOffset.UtcNow));

        await RespondAsync(
            $"テンプレ `{key}` を読み込みました。主催ポジションを選択してください。\n投稿先: {MentionChannel(targetChannel.Id)}",
            components: BuildCreateHostSelectComponents(positions),
            ephemeral: true);
    }

    [ComponentInteraction("recruit_create", ignoreGroupNames: true)]
    public async Task ShowCreateModalAsync()
    {
        await RespondWithModalAsync<RecruitCreateModal>("recruit_create_modal");
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

        if (!TryNormalizeRecruitDataCenter(modal.DataCenter, out var dataCenter, out var dataCenterError))
        {
            await RespondAsync(dataCenterError ?? "開催DCの入力が不正です。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitSchedule(modal.Schedule, out var schedule, out var scheduleAt, out var scheduleError))
        {
            await RespondAsync(scheduleError ?? "開始日時の入力が不正です。", ephemeral: true);
            return;
        }

        var note = modal.Note?.Trim();
        if (!TryParseRecruitSlotsForCreate(modal.Positions, out var positions, out var requiredJobs, out var positionError))
        {
            await RespondAsync(positionError ?? "ポジションの入力が不正です。", ephemeral: true);
            return;
        }

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
            title,
            dataCenter,
            schedule,
            scheduleAt,
            note,
            positions.ToArray(),
            requiredJobs,
            DateTimeOffset.UtcNow));

        await RespondAsync(
            $"主催ポジションをボタンで選択してください。次に主催ジョブを選ぶと募集を投稿します。\n投稿先: {MentionChannel(targetChannel.Id)}",
            components: BuildCreateHostSelectComponents(positions),
            ephemeral: true);
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
        if (string.IsNullOrWhiteSpace(comment))
        {
            _joinTracker.Set(Context.User.Id, context);
            await RespondAsync("コメントを入力してください。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
        if (post is null)
        {
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var previous = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        var displayName = Context.User.GlobalName ?? Context.User.Username;
        var status = await _db.AssignRecruitSlotAsync(post.Id, context.Position, Context.User.Id, displayName, context.Job, comment);
        if (status != RecruitAssignStatus.Success)
        {
            var error = status switch
            {
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

            await UpsertRecruitParticipantEmbedAsync(post, Context.User, slot, mentionHost: isNewJoin);
            if (previous is not null && changed)
            {
                await PostRecruitChangeLogAsync(post, Context.User, previous, slot);
            }
        }

        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync($"参加しました: {context.Position} ({context.Job})", ephemeral: true);
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

    [ComponentInteraction("recruit_cancel", ignoreGroupNames: true)]
    public async Task CancelRecruitAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await RespondAsync("参加をキャンセルしました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_late", ignoreGroupNames: true)]
    public async Task NotifyLateAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了またはキャンセル済みの募集には連絡できません。", ephemeral: true);
            return;
        }

        await PostRecruitStatusMessageAsync(post, $"⚠️ 遅刻連絡: {MentionUser(ToDbId(Context.User.Id))}");
        await RespondAsync("遅刻連絡を投稿しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_absent", ignoreGroupNames: true)]
    public async Task NotifyAbsentAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("終了またはキャンセル済みの募集には連絡できません。", ephemeral: true);
            return;
        }

        var slot = await _db.GetRecruitSlotByUserAsync(post.Id, Context.User.Id);
        if (slot is not null)
        {
            var removed = await _db.ClearRecruitSlotAsync(post.Id, Context.User.Id);
            if (removed)
            {
                await DeleteRecruitParticipantEmbedAsync(post, slot);
                await UpdateRecruitMessageAsync(post);
                await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
            }
        }

        await PostRecruitStatusMessageAsync(post, $"❌ 欠席連絡: {MentionUser(ToDbId(Context.User.Id))}");
        await RespondAsync("欠席連絡を投稿しました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_comment_edit", ignoreGroupNames: true)]
    public async Task ShowCommentEditModalAsync()
    {
        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
        _commentEditTracker.Set(Context.User.Id, new RecruitCommentEditContext(component.Message.Id, DateTimeOffset.UtcNow));
        await RespondWithModalAsync<RecruitCommentEditModal>("recruit_comment_edit_modal");
    }

    [ComponentInteraction("recruit_positions_edit", ignoreGroupNames: true)]
    public async Task ShowSlotEditSelectAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
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
            components: BuildSlotEditPositionButtonsComponents(component.Message.Id, ordered, slots),
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("対象枠が見つかりませんでした。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobs(position, out var jobs))
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

        if (requiredJob is not null)
        {
            if (!RecruitmentRules.TryGetJobs(position, out var jobs) ||
                !jobs.Any(item => string.Equals(item.Code, requiredJob, StringComparison.OrdinalIgnoreCase)))
            {
                await RespondAsync("そのジョブは対象ポジションに指定できません。", ephemeral: true);
                return;
            }
        }

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
            await FollowupAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var status = await _db.UpdateRecruitSlotRequiredJobAsync(post.Id, position, requiredJob);
        if (status != RecruitRequiredJobUpdateStatus.Success)
        {
            var error = status switch
            {
                RecruitRequiredJobUpdateStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitRequiredJobUpdateStatus.PostClosed => "終了またはキャンセル済みの募集は変更できません。",
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
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
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        _postEditTracker.Set(Context.User.Id, new RecruitPostEditContext(component.Message.Id, DateTimeOffset.UtcNow));
        await RespondWithModalAsync<RecruitPostEditModal>("recruit_post_edit_modal");
    }

    [ComponentInteraction("recruit_external_fill", ignoreGroupNames: true)]
    public async Task ShowExternalFillSelectAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var emptySet = slots
            .Where(slot => !slot.UserId.HasValue && !slot.IsClosed)
            .Select(slot => slot.Position)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var emptyPositions = GetOrderedPositions(slots)
            .Where(position => emptySet.Contains(position))
            .ToList();

        if (emptyPositions.Count == 0)
        {
            await RespondAsync("ゲーム内募集で埋まりとして〆にできる空きポジションがありません。", ephemeral: true);
            return;
        }

        await RespondAsync(
            "ゲーム内募集で埋まったポジションを選択してください。",
            components: BuildExternalFillSelectComponents(component.Message.Id, emptyPositions),
            ephemeral: true);
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

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("対象ポジションが見つかりませんでした。", ephemeral: true);
            return;
        }

        if (target.IsClosed)
        {
            await RespondAsync("そのポジションは既に〆です。", ephemeral: true);
            return;
        }

        if (target.UserId.HasValue)
        {
            await RespondAsync("そのポジションは既に埋まっています。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobs(target.Position, out var jobs))
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
            components: BuildExternalFillJobSelectComponents(target.Position, messageId.Value, availableJobs),
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

        if (!RecruitmentRules.TryGetJobs(position, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        var matchedJob = jobs.FirstOrDefault(item => string.Equals(item.Code, job, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var status = await _db.MarkRecruitSlotIngameFilledAsync(post.Id, position, matchedJob.Code);
        if (status != RecruitIngameFillStatus.Success)
        {
            var error = status switch
            {
                RecruitIngameFillStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitIngameFillStatus.PostClosed => "終了またはキャンセル済みの募集は変更できません。",
                RecruitIngameFillStatus.PositionNotFound => "対象ポジションが見つかりませんでした。",
                RecruitIngameFillStatus.PositionOccupied => "そのポジションは既に埋まっています。",
                RecruitIngameFillStatus.PositionAlreadyClosed => "そのポジションは既に〆です。",
                RecruitIngameFillStatus.JobTaken => "そのジョブは既に埋まっています。",
                RecruitIngameFillStatus.JobRestricted => $"{position} は指定ジョブのみ〆にできます。",
                _ => "〆の更新に失敗しました。"
            };

            await FollowupAsync(error, ephemeral: true);
            return;
        }

        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, $"ゲーム内募集で埋まったため {position} を {matchedJob.Code} で〆にしました。");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync($"{position} を {matchedJob.Code} で〆にしました。", ephemeral: true);
    }

    [ComponentInteraction("recruit_external_unfill", ignoreGroupNames: true)]
    public async Task ShowExternalUnfillSelectAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("操作を認識できませんでした。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(component.Message.Id);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
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
            components: BuildExternalUnfillSelectComponents(component.Message.Id, closedPositions),
            ephemeral: true);
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

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
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
            await RespondAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var status = await _db.UnmarkRecruitSlotIngameFilledAsync(post.Id, position);
        if (status != RecruitIngameUnfillStatus.Success)
        {
            var error = status switch
            {
                RecruitIngameUnfillStatus.PostNotFound => "募集が見つかりませんでした。",
                RecruitIngameUnfillStatus.PostClosed => "終了またはキャンセル済みの募集は変更できません。",
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

    [ComponentInteraction("recruit_manage_cancel", ignoreGroupNames: true)]
    public Task ShowManageCancelModalAsync()
        => ShowManageSelectionAsync(RecruitPostStatus.Canceled);

    [ComponentInteraction("recruit_manage_close_select:*", ignoreGroupNames: true)]
    public Task HandleManageCloseSelectAsync(string messageId)
        => HandleManageSelectionAsync(messageId, RecruitPostStatus.Closed, "募集を終了しました。");

    [ComponentInteraction("recruit_manage_cancel_select:*", ignoreGroupNames: true)]
    public Task HandleManageCancelSelectAsync(string messageId)
        => HandleManageSelectionAsync(messageId, RecruitPostStatus.Canceled, "募集をキャンセルしました。");

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
        if (string.IsNullOrWhiteSpace(comment))
        {
            _commentEditTracker.Set(Context.User.Id, context);
            await RespondAsync("コメントを入力してください。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
        if (post is null)
        {
            await FollowupAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var updated = await _db.UpdateRecruitSlotCommentAsync(post.Id, Context.User.Id, comment);
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
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_postEditTracker.TryRemove(Context.User.Id, out var context))
        {
            await RespondAsync("編集情報が見つかりませんでした。もう一度操作してください。", ephemeral: true);
            return;
        }

        if (DateTimeOffset.UtcNow - context.CreatedAt > InteractionContextTimeout)
        {
            await RespondAsync("編集情報の有効期限が切れました。もう一度操作してください。", ephemeral: true);
            return;
        }

        var title = modal.RecruitTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await RespondAsync("募集タイトルを入力してください。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitDataCenter(modal.DataCenter, out var dataCenter, out var dataCenterError))
        {
            await RespondAsync(dataCenterError ?? "開催DCの入力が不正です。", ephemeral: true);
            return;
        }

        if (!TryNormalizeRecruitSchedule(modal.Schedule, out var schedule, out var scheduleAt, out var scheduleError))
        {
            await RespondAsync(scheduleError ?? "開始日時の入力が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var post = await _db.GetRecruitPostByMessageIdAsync(context.MessageId);
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
            await FollowupAsync("終了またはキャンセル済みの募集は変更できません。", ephemeral: true);
            return;
        }

        var note = modal.Note?.Trim();
        var updated = await _db.UpdateRecruitPostDetailsAsync(post.Id, title, dataCenter, schedule, scheduleAt, note);
        if (!updated)
        {
            await FollowupAsync("募集の更新に失敗しました。", ephemeral: true);
            return;
        }

        post.Title = title;
        post.DataCenter = dataCenter;
        post.ScheduleText = schedule;
        post.ScheduleAt = scheduleAt;
        post.Note = note;
        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, "募集内容を更新しました。");
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
        await FollowupAsync("募集内容を更新しました。", ephemeral: true);
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

        await HandleManageSelectionAsync(component.Message.Id.ToString(), RecruitPostStatus.Canceled, "募集をキャンセルしました。");
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
        if (!RecruitmentRules.TryGetJobs(position, out var jobs))
        {
            await RespondAsync("ポジションが不明です。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId);
        if (post is null)
        {
            await RespondAsync("募集が見つかりませんでした。", ephemeral: true);
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("ポジションが見つかりません。", ephemeral: true);
            return;
        }

        if (target.IsClosed && !target.UserId.HasValue)
        {
            await RespondAsync($"{position} は〆です（ゲーム内募集で埋まり）。", ephemeral: true);
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

        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_job_{position}")
            .WithPlaceholder(string.IsNullOrWhiteSpace(target.RequiredJob)
                ? $"{position} のジョブを選択"
                : $"{position} のジョブを選択（指定: {target.RequiredJob}）")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var job in availableJobs)
        {
            var value = $"{job.Code}|{messageId}";
            var label = job.Code;
            var description = job.Name.Length > 50 ? job.Name[..50] : job.Name;
            menu.AddOption(label, value, description);
        }

        var components = new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();

        await RespondAsync("ジョブを選択してください。", components: components, ephemeral: true);
    }

    private async Task HandleJobSelectInternalAsync(string position, string[] values)
    {
        PruneInteractionContexts();
        if (values is not { Length: > 0 })
        {
            await RespondAsync("ジョブを選択してください。", ephemeral: true);
            return;
        }

        var (job, messageId) = ParseJobValue(values[0]);
        if (string.IsNullOrWhiteSpace(job) || !messageId.HasValue)
        {
            await RespondAsync("ジョブの選択情報が不正です。", ephemeral: true);
            return;
        }

        var post = await _db.GetRecruitPostByMessageIdAsync(messageId.Value);
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
        var target = slots.FirstOrDefault(slot => string.Equals(slot.Position, position, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await RespondAsync("ポジションが見つかりません。", ephemeral: true);
            return;
        }

        if (target.IsClosed && !target.UserId.HasValue)
        {
            await RespondAsync($"{position} は〆です（ゲーム内募集で埋まり）。", ephemeral: true);
            return;
        }

        _joinTracker.Set(Context.User.Id, new RecruitJoinContext(messageId.Value, position, job, DateTimeOffset.UtcNow));
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

        var components = RecruitmentPanel.BuildPanelComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(components: components);
    }

    private async Task UpdateRecruitMessageAsync(RecruitPost post)
    {
        if (Context.Guild is null)
        {
            return;
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
            await _db.UpdateRecruitPostStatusAsync(post.Id, nextStatus);
            post.Status = nextStatus;
            var notice = nextStatus == RecruitPostStatus.Full
                ? "募集が満員になりました。"
                : "空きが出たため募集を再開します。";
            await PostRecruitStatusMessageAsync(post, notice);
        }

        var embed = BuildRecruitEmbed(post, slots);

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
                    var positions = GetOrderedPositions(slots.Where(slot => !slot.IsClosed).ToList());
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
            var thread = await textChannel.CreateThreadAsync(threadName, threadType, ThreadArchiveDuration.OneDay, message);
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

    private async Task UpsertRecruitParticipantEmbedAsync(RecruitPost post, SocketUser user, RecruitSlot slot, bool mentionHost)
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

            if (slot.ThreadMessageId.HasValue)
            {
                if (await TryModifyThreadMessageAsync(threadChannel, ToUlong(slot.ThreadMessageId.Value), embed))
                {
                    return;
                }
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

    private async Task PostRecruitStatusMessageAsync(RecruitPost post, string text)
    {
        var threadChannel = await EnsureRecruitThreadAsync(post);
        if (threadChannel is null)
        {
            return;
        }

        try
        {
            await threadChannel.SendMessageAsync(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post recruit status message for post {PostId}.", post.Id);
        }
    }

    private async Task ArchiveRecruitThreadAsync(RecruitPost post, bool lockThread)
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
                props.Archived = true;
                props.Locked = lockThread;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to archive recruit thread for post {PostId}.", post.Id);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve recruit thread channel {ChannelId}.", channelId);
            return null;
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
            RecruitPostStatus.Canceled => "募集はキャンセルされました。",
            _ => "募集に参加できません。"
        };
    }

    private async Task ShowManageSelectionAsync(RecruitPostStatus targetStatus)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var posts = await _db.ListActiveRecruitPostsByCreatorAsync(Context.Guild.Id, Context.User.Id);
        if (posts.Count == 0)
        {
            await RespondAsync("対象の募集がありません。", ephemeral: true);
            return;
        }

        var builder = new ComponentBuilder();
        var action = targetStatus == RecruitPostStatus.Canceled ? "キャンセル" : "終了";
        for (var i = 0; i < posts.Count && i < 25; i++)
        {
            var label = $"{i + 1}: {TruncateLabel(posts[i].Title, 60)}";
            var customId = targetStatus == RecruitPostStatus.Canceled
                ? $"recruit_manage_cancel_select:{ToUlong(posts[i].MessageId)}"
                : $"recruit_manage_close_select:{ToUlong(posts[i].MessageId)}";
            builder.WithButton(label, customId, ButtonStyle.Secondary, row: i / 5);
        }

        var suffix = posts.Count > 25
            ? "\n表示は最新25件までです。その他はスラッシュコマンドでURL/ID指定してください。"
            : string.Empty;
        await RespondAsync($"募集を{action}する番号を選んでください。{suffix}", components: builder.Build(), ephemeral: true);
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
            await RespondAsync("主催者のみが操作できます。", ephemeral: true);
            return;
        }

        if (newStatus == RecruitPostStatus.Closed &&
            post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            await RespondAsync("募集は既に終了しています。", ephemeral: true);
            return;
        }

        if (newStatus == RecruitPostStatus.Canceled && post.Status == RecruitPostStatus.Canceled)
        {
            await RespondAsync("募集は既にキャンセルされています。", ephemeral: true);
            return;
        }

        await _db.UpdateRecruitPostStatusAsync(post.Id, newStatus);
        post.Status = newStatus;
        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, userMessage);
        await ArchiveRecruitThreadAsync(post, lockThread: true);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
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

        await _db.UpdateRecruitPostStatusAsync(post.Id, openStatus);
        post.Status = openStatus;
        await UpdateRecruitMessageAsync(post);
        await PostRecruitStatusMessageAsync(post, "募集を再開しました。");
        await UnarchiveRecruitThreadAsync(post);
        await UpdateRecruitListAsync(Context.Guild, Context.Channel as IMessageChannel);
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

        if (!draft.Positions.Contains(hostPosition, StringComparer.OrdinalIgnoreCase))
        {
            await RespondAsync("選択できないポジションです。再度選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobs(hostPosition, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        IReadOnlyList<JobOption> availableJobs = jobs;
        if (draft.RequiredJobs.TryGetValue(hostPosition, out var requiredHostJob) && !string.IsNullOrWhiteSpace(requiredHostJob))
        {
            availableJobs = jobs
                .Where(job => string.Equals(job.Code, requiredHostJob, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (availableJobs.Count == 0)
            {
                await RespondAsync($"{hostPosition} の指定ジョブ設定が不正です。募集作成をやり直してください。", ephemeral: true);
                return;
            }
        }

        await RespondAsync(
            draft.RequiredJobs.TryGetValue(hostPosition, out var requiredCode) && !string.IsNullOrWhiteSpace(requiredCode)
                ? $"{hostPosition} の主催ジョブを選択してください（指定: {requiredCode}）。"
                : $"{hostPosition} の主催ジョブを選択してください。",
            components: BuildCreateHostJobSelectComponents(hostPosition, availableJobs),
            ephemeral: true);
    }

    private async Task ConfirmCreateHostAsync(string hostPosition, string[] values)
    {
        if (Context.Guild is null || Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!RecruitmentRules.TryGetJobs(hostPosition, out var jobs))
        {
            await RespondAsync("ポジションに対応するジョブが見つかりません。", ephemeral: true);
            return;
        }

        if (values is not { Length: > 0 } || string.IsNullOrWhiteSpace(values[0]))
        {
            await RespondAsync("主催ジョブを選択してください。", ephemeral: true);
            return;
        }

        var selectedJob = values[0].Trim();
        var matchedJob = jobs.FirstOrDefault(job => string.Equals(job.Code, selectedJob, StringComparison.OrdinalIgnoreCase));
        if (matchedJob is null)
        {
            await RespondAsync("選択したジョブが不正です。もう一度選択してください。", ephemeral: true);
            return;
        }

        PruneInteractionContexts();
        if (!_createDraftTracker.TryRemove(Context.User.Id, out var draft))
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

        if (!draft.Positions.Contains(hostPosition, StringComparer.OrdinalIgnoreCase))
        {
            _createDraftTracker.Set(Context.User.Id, draft);
            await RespondAsync("選択できないポジションです。再度選択してください。", components: BuildCreateHostSelectComponents(draft.Positions), ephemeral: true);
            return;
        }

        if (draft.RequiredJobs.TryGetValue(hostPosition, out var requiredHostJob) &&
            !string.Equals(requiredHostJob, matchedJob.Code, StringComparison.OrdinalIgnoreCase))
        {
            _createDraftTracker.Set(Context.User.Id, draft);
            await RespondAsync($"{hostPosition} は指定ジョブ {requiredHostJob} のみ選択できます。", ephemeral: true);
            return;
        }

        var activeCount = await _db.CountActiveRecruitPostsByCreatorAsync(Context.Guild.Id, Context.User.Id);
        if (activeCount >= 5)
        {
            _createDraftTracker.Set(Context.User.Id, draft);
            await RespondAsync("同時に作成できる募集は5件までです。不要な募集を終了またはキャンセルしてください。", ephemeral: true);
            return;
        }

        var targetChannel = Context.Guild.GetTextChannel(draft.TargetChannelId);
        if (targetChannel is null)
        {
            await RespondAsync("投稿先チャンネルが見つかりません。募集作成をやり直してください。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var success = await TryCreateRecruitPostAsync(targetChannel, draft, hostPosition, matchedJob.Code);
        if (!success)
        {
            _createDraftTracker.Set(Context.User.Id, draft);
            return;
        }

        await FollowupAsync($"募集を作成しました。投稿先: {MentionChannel(targetChannel.Id)}", ephemeral: true);
    }

    private async Task<bool> TryCreateRecruitPostAsync(ITextChannel channel, RecruitCreateDraftContext draft, string hostPosition, string hostJob)
    {
        var creatorName = Context.User.GlobalName ?? Context.User.Username;
        var positions = draft.Positions;

        var postDraft = new RecruitPost
        {
            Title = draft.Title,
            DataCenter = draft.DataCenter,
            ScheduleText = draft.Schedule,
            ScheduleAt = draft.ScheduleAt,
            Note = draft.Note,
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

        var hostSlot = slots.FirstOrDefault(slot => string.Equals(slot.Position, hostPosition, StringComparison.OrdinalIgnoreCase));
        if (hostSlot is not null)
        {
            hostSlot.UserId = ToDbId(Context.User.Id);
            hostSlot.UserName = creatorName;
            hostSlot.Job = hostJob;
            hostSlot.Comment = null;
        }

        var embed = BuildRecruitEmbed(postDraft, slots);
        IUserMessage? posted = null;
        try
        {
            posted = await channel.SendMessageAsync(embed: embed, components: RecruitmentPanel.BuildRecruitComponents(RecruitPostStatus.Open, positions));
            var created = await _db.CreateRecruitPostAsync(Context.Guild!.Id, channel.Id, posted.Id, Context.User.Id, draft.Title, draft.DataCenter, draft.Schedule, draft.ScheduleAt, draft.Note, positions, draft.RequiredJobs);
            var hostAssignStatus = await _db.AssignRecruitSlotAsync(created.Id, hostPosition, Context.User.Id, creatorName, hostJob, string.Empty);
            if (hostAssignStatus != RecruitAssignStatus.Success)
            {
                throw new InvalidOperationException($"Failed to assign host slot. status={hostAssignStatus}");
            }

            await EnsureRecruitThreadAsync(created, channel, posted);
            var createdHostSlot = await _db.GetRecruitSlotByUserAsync(created.Id, Context.User.Id);
            if (createdHostSlot is not null)
            {
                await UpsertRecruitParticipantEmbedAsync(created, Context.User, createdHostSlot, mentionHost: false);
            }
            await UpdateRecruitListAsync(Context.Guild, channel);
            return true;
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
            return false;
        }
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
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 2)
            .Build();
    }

    private static MessageComponent BuildCreateHostJobSelectComponents(string hostPosition, IReadOnlyList<JobOption> jobs)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"recruit_create_host_job_{hostPosition}")
            .WithPlaceholder($"{hostPosition} の主催ジョブを選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var job in jobs)
        {
            var description = job.Name.Length > 50 ? job.Name[..50] : job.Name;
            menu.AddOption(job.Code, job.Code, description);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("中止", "recruit_create_host_cancel", ButtonStyle.Secondary, row: 1)
            .Build();
    }

    private static MessageComponent BuildExternalFillSelectComponents(ulong messageId, IReadOnlyList<string> positions)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_external_fill_select")
            .WithPlaceholder("ゲーム内募集で埋まったポジションを選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var position in positions)
        {
            menu.AddOption(position, $"{position}|{messageId}");
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();
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

    private static MessageComponent BuildExternalFillJobSelectComponents(string position, ulong messageId, IReadOnlyList<JobOption> jobs)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_external_fill_job_select")
            .WithPlaceholder($"{position} で埋まったジョブを選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var job in jobs)
        {
            var description = job.Name.Length > 50 ? job.Name[..50] : job.Name;
            menu.AddOption(job.Code, $"{job.Code}|{position}|{messageId}", description);
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();
    }

    private static MessageComponent BuildExternalUnfillSelectComponents(ulong messageId, IReadOnlyList<string> positions)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_external_unfill_select")
            .WithPlaceholder("〆解除するポジションを選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var position in positions)
        {
            menu.AddOption(position, $"{position}|{messageId}");
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();
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

    private static Embed BuildRecruitEmbed(RecruitPost post, IReadOnlyList<RecruitSlot> slots)
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
        var status = post.Status switch
        {
            RecruitPostStatus.Full => "満員",
            RecruitPostStatus.Closed => "終了",
            RecruitPostStatus.Canceled => "キャンセル",
            _ => "募集中"
        };

        var embed = new EmbedBuilder()
            .WithTitle($"募集: {post.Title}")
            .WithColor(new Color(250, 177, 160))
            .WithCurrentTimestamp()
            .AddField("主催", MentionUser(post.CreatorUserId), true)
            .AddField("DC", dataCenter, true)
            .AddField("開始", schedule, true)
            .AddField("人数", $"{filled}/{max}", true)
            .AddField("状態", status, true)
            .AddField("コメント", note, false)
            .AddField("ポジション", BuildSlotLines(slots), false)
            .WithFooter("メニューで参加できます（ジョブ重複不可）");

        return embed.Build();
    }

    private static string BuildSlotLines(IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return "なし";
        }

        var map = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var orderedPositions = GetOrderedPositions(slots);
        var lines = new List<string>();
        foreach (var position in orderedPositions)
        {
            if (!map.TryGetValue(position, out var slot))
            {
                lines.Add($"{position}: 空き");
                continue;
            }

            if (slot.IsClosed && !slot.UserId.HasValue)
            {
                var closedJob = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
                lines.Add($"{position}: 〆（{closedJob}）");
                continue;
            }

            if (!slot.UserId.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(slot.RequiredJob))
                {
                    lines.Add($"{position}: 空き（希望: {slot.RequiredJob}）");
                }
                else
                {
                    lines.Add($"{position}: 空き");
                }
                continue;
            }

            var job = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
            lines.Add($"{position}: {MentionUser(slot.UserId.Value)} ({job})");
        }

        return string.Join('\n', lines);
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
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

                if (!TryNormalizeJobCodeForPosition(position, rawJob, out var jobCode))
                {
                    invalid.Add(normalized);
                    continue;
                }

                expanded.Add(position);
                if (required.TryGetValue(position, out var existing) &&
                    !string.Equals(existing, jobCode, StringComparison.OrdinalIgnoreCase))
                {
                    invalid.Add(normalized);
                    continue;
                }

                required[position] = jobCode!;
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
            if (required.TryGetValue(position, out var job))
            {
                filtered[position] = job;
            }
        }

        positions = ordered;
        requiredJobs = filtered;
        error = null;
        return true;
    }

    private static bool TryNormalizeJobCodeForPosition(string position, string rawJob, out string? jobCode)
    {
        jobCode = null;
        if (string.IsNullOrWhiteSpace(rawJob))
        {
            return false;
        }

        if (!RecruitmentRules.TryGetJobs(position, out var jobs))
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
            error = $"開始日時を入力してください（{RecruitScheduleFormat} または 今）";
            return false;
        }

        var trimmed = input.Trim();
        if (string.Equals(trimmed, "今", StringComparison.Ordinal))
        {
            scheduleAt = DateTimeOffset.UtcNow;
            schedule = scheduleAt.Value
                .ToOffset(JapanTimeOffset)
                .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
            error = null;
            return true;
        }

        if (!DateTime.TryParseExact(
                trimmed,
                RecruitScheduleFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            schedule = string.Empty;
            scheduleAt = null;
            error = $"開始日時は {RecruitScheduleFormat} または 今 を入力してください。";
            return false;
        }

        var jst = new DateTimeOffset(parsed, JapanTimeOffset);
        scheduleAt = jst.ToUniversalTime();
        schedule = jst
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
        error = null;
        return true;
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
    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
}

public sealed class RecruitCreateModal : IModal
{
    public string Title => "募集を作成";

    [InputLabel("募集タイトル")]
    [ModalTextInput("recruit_title", maxLength: 80)]
    public string RecruitTitle { get; set; } = string.Empty;

    [InputLabel("開始日時（必須・例: 2026/01/31 21:00 / 今）")]
    [ModalTextInput("recruit_schedule", placeholder: "例: 2026/01/31 21:00 または 今", maxLength: 80)]
    public string Schedule { get; set; } = string.Empty;

    [InputLabel("開催DC（必須）")]
    [ModalTextInput("recruit_datacenter", placeholder: "Meteor / Gaia / Mana / Elemental", maxLength: 40)]
    public string DataCenter { get; set; } = string.Empty;

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_note", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Note { get; set; }

    [InputLabel("ジョブ指定（任意）")]
    [ModalTextInput("recruit_positions", placeholder: "例: MT:暗 ST:ナ H1:白（未入力なら全8枠を自動作成）", maxLength: 120)]
    [RequiredInput(false)]
    public string? Positions { get; set; }

}

public sealed class RecruitJoinCommentModal : IModal
{
    public string Title => "参加コメント";

    [InputLabel("コメント（必須）")]
    [ModalTextInput("recruit_join_comment", TextInputStyle.Paragraph, maxLength: 200)]
    public string? Comment { get; set; }
}

public sealed class RecruitCommentEditModal : IModal
{
    public string Title => "コメント編集";

    [InputLabel("コメント（必須）")]
    [ModalTextInput("recruit_comment_edit", TextInputStyle.Paragraph, maxLength: 200)]
    public string? Comment { get; set; }
}

public sealed class RecruitPostEditModal : IModal
{
    public string Title => "募集内容編集";

    [InputLabel("募集タイトル（必須）")]
    [ModalTextInput("recruit_post_edit_title", maxLength: 80)]
    public string RecruitTitle { get; set; } = string.Empty;

    [InputLabel("開始日時（必須・例: 2026/01/31 21:00 / 今）")]
    [ModalTextInput("recruit_post_edit_schedule", placeholder: "例: 2026/01/31 21:00 または 今", maxLength: 80)]
    public string Schedule { get; set; } = string.Empty;

    [InputLabel("開催DC（必須）")]
    [ModalTextInput("recruit_post_edit_datacenter", placeholder: "Meteor / Gaia / Mana / Elemental", maxLength: 40)]
    public string DataCenter { get; set; } = string.Empty;

    [InputLabel("コメント（任意）")]
    [ModalTextInput("recruit_post_edit_note", TextInputStyle.Paragraph, maxLength: 300)]
    [RequiredInput(false)]
    public string? Note { get; set; }
}
