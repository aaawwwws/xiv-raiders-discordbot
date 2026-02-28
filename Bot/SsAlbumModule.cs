using System.Collections.Concurrent;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RaidDcBot.Bot;

[Group("ssalbum", "SSアルバム")]
public sealed class SsAlbumModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string SourceChannelName = "ss";
    private const int MaxMessageScanRows = 1500;
    private const int AlbumPageSize = 25;
    private const int GridSize = 5;
    private const int TileSize = 216;
    private const int TileGap = 8;
    private const int CanvasPadding = 14;
    private const int MaxFavoriteRows = 3000;
    private const int QueryStateTtlMinutes = 30;
    private const int FavoriteActionStateTtlMinutes = 30;
    private const string AlbumFilterModalCustomIdPrefix = "ss_album_filter_modal:";
    private const string AlbumFilterTargetUserInputId = "ss_album_filter_target_user";
    private const string AlbumFilterFromDateInputId = "ss_album_filter_from_date";
    private const string AlbumFilterToDateInputId = "ss_album_filter_to_date";
    private const string AlbumFilterKeywordInputId = "ss_album_filter_keyword";
    private static readonly HttpClient SsAlbumImageHttpClient = CreateSsAlbumImageHttpClient();
    private static readonly Font? SsAlbumBadgeFont = TryCreateSsAlbumBadgeFont();
    private static readonly ConcurrentDictionary<string, AlbumQueryState> AlbumQueryStates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, FavoriteActionState> FavoriteActionStates = new(StringComparer.Ordinal);

    private readonly Db _db;
    private readonly ILogger<SsAlbumModule> _logger;
    private readonly DiscordOptions _options;
    private readonly string? _attachmentArchiveRootPath;

    public SsAlbumModule(Db db, ILogger<SsAlbumModule> logger, IOptions<DiscordOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
        _attachmentArchiveRootPath = ResolveAttachmentArchiveRootPath(_options);
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにSSアルバムパネルを設置します")]
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
        var message = await EnsurePanelMessageAsync(channel);
        await FollowupAsync($"このチャンネルにSSアルバムパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [SlashCommand("me", "自分のSSアルバムを表示します")]
    public async Task ShowMyAlbumAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var state = CreateDefaultQueryState(Context.Guild.Id, Context.User.Id, Context.User.Id);
        await RespondWithAlbumAsync(state, 0);
    }

    [SlashCommand("user", "指定ユーザーのSSアルバムを表示します")]
    public async Task ShowUserAlbumAsync([Summary("user", "対象ユーザー")] IUser user)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var state = CreateDefaultQueryState(Context.Guild.Id, Context.User.Id, user.Id);
        await RespondWithAlbumAsync(state, 0);
    }

    [ComponentInteraction(SsAlbumPanel.OpenMyAlbumCustomId, ignoreGroupNames: true)]
    public async Task OpenMyAlbumFromPanelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var state = CreateDefaultQueryState(Context.Guild.Id, Context.User.Id, Context.User.Id);
        await RespondWithAlbumAsync(state, 0);
    }

    [ComponentInteraction(SsAlbumPanel.PickUserCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task PickUserAlbumFromPanelAsync(string _, string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values.Length == 0 || !ulong.TryParse(values[0], out var targetUserId))
        {
            await RespondAsync("ユーザー選択の情報が不正です。", ephemeral: true);
            return;
        }

        if (!await IsSelectableMemberForAlbumAsync(Context.Guild, targetUserId))
        {
            await RespondAsync("メンバーロールが付与されているユーザーのみ選択できます。", ephemeral: true);
            return;
        }

        var state = CreateDefaultQueryState(Context.Guild.Id, Context.User.Id, targetUserId);
        await RespondWithAlbumAsync(state, 0);
    }

    [ComponentInteraction(SsAlbumPanel.PageCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ChangeAlbumPageAsync(string payload)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseStatePagePayload(payload, out var stateKey, out var page))
        {
            await RespondAsync("アルバムのページ情報が不正です。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var state, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はボタンから実行してください。", ephemeral: true);
            return;
        }

        var result = await BuildAlbumViewAsync(state!, page);
        if (!result.Success)
        {
            await RespondAsync(result.ErrorMessage ?? "アルバムを表示できませんでした。", ephemeral: true);
            return;
        }

        await using var updateStream = new MemoryStream(result.CollagePng!, writable: false);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = result.Embed!;
            props.Components = result.Components!;
            props.Attachments = Optional.Create<IEnumerable<FileAttachment>>(
                [new FileAttachment(updateStream, result.CollageFileName!)]);
        });
    }

    [ComponentInteraction(SsAlbumPanel.PageSelectCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectAlbumPageAsync(string stateKey, string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (values.Length == 0 || !int.TryParse(values[0], out var page))
        {
            await RespondAsync("ページ選択の情報が不正です。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var state, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はメニューから実行してください。", ephemeral: true);
            return;
        }

        var result = await BuildAlbumViewAsync(state!, page);
        if (!result.Success)
        {
            await RespondAsync(result.ErrorMessage ?? "アルバムを表示できませんでした。", ephemeral: true);
            return;
        }

        await using var updateStream = new MemoryStream(result.CollagePng!, writable: false);
        await component.UpdateAsync(props =>
        {
            props.Content = null;
            props.Embed = result.Embed!;
            props.Components = result.Components!;
            props.Attachments = Optional.Create<IEnumerable<FileAttachment>>(
                [new FileAttachment(updateStream, result.CollageFileName!)]);
        });
    }

    [ComponentInteraction(SsAlbumPanel.ImageSelectCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SelectAlbumImageAsync(string payload, string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseStatePagePayload(payload, out var stateKey, out var page) ||
            values.Length == 0 ||
            !int.TryParse(values[0], out var localIndex))
        {
            await RespondAsync("画像選択の情報が不正です。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var state, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var detail = await BuildAlbumDetailViewAsync(state!, page, localIndex);
        if (!detail.Success)
        {
            await RespondAsync(detail.ErrorMessage ?? "画像詳細の表示に失敗しました。", ephemeral: true);
            return;
        }

        await RespondAsync(embed: detail.Embed, components: detail.Components, ephemeral: true);
    }

    [ComponentInteraction(SsAlbumPanel.FilterOpenCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenAlbumFilterModalAsync(string stateKey)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var state, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("SSアルバム絞り込み")
            .WithCustomId($"{AlbumFilterModalCustomIdPrefix}{stateKey}")
            .AddTextInput(
                "投稿者ID or @メンション（空欄で維持）",
                AlbumFilterTargetUserInputId,
                value: state!.TargetUserId.ToString(),
                maxLength: 32)
            .AddTextInput(
                "開始日（YYYY-MM-DD / 空欄で無効）",
                AlbumFilterFromDateInputId,
                value: state.FromUtc.HasValue ? state.FromUtc.Value.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd") : string.Empty,
                maxLength: 10,
                required: false)
            .AddTextInput(
                "終了日（YYYY-MM-DD / 空欄で無効）",
                AlbumFilterToDateInputId,
                value: state.ToUtcExclusive.HasValue ? state.ToUtcExclusive.Value.AddDays(-1).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd") : string.Empty,
                maxLength: 10,
                required: false)
            .AddTextInput(
                "キーワード（本文/ファイル名）",
                AlbumFilterKeywordInputId,
                value: state.Keyword ?? string.Empty,
                maxLength: 80,
                required: false);
        await RespondWithModalAsync(modal.Build());
    }

    [ModalInteraction(AlbumFilterModalCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task HandleAlbumFilterModalAsync(string stateKey, SsAlbumFilterModal modal)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var currentState, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        if (!TryResolveTargetUserId(modal.TargetUser, currentState!.TargetUserId, out var targetUserId))
        {
            await RespondAsync("投稿者はユーザーIDまたは@メンションで入力してください。", ephemeral: true);
            return;
        }

        if (!TryParseDateRange(modal.FromDate, modal.ToDate, out var fromUtc, out var toUtcExclusive, out var dateError))
        {
            await RespondAsync(dateError, ephemeral: true);
            return;
        }

        var normalizedKeyword = NormalizeKeyword(modal.Keyword);
        var nextState = CreateQueryState(
            Context.Guild.Id,
            Context.User.Id,
            targetUserId,
            fromUtc,
            toUtcExclusive,
            normalizedKeyword,
            currentState.FavoritesOnly);

        await RespondWithAlbumAsync(nextState, 0);
    }

    [ComponentInteraction(SsAlbumPanel.FilterClearCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ClearAlbumFilterAsync(string stateKey)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var currentState, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var nextState = CreateQueryState(
            Context.Guild.Id,
            Context.User.Id,
            currentState!.TargetUserId,
            fromUtc: null,
            toUtcExclusive: null,
            keyword: null,
            favoritesOnly: false);
        await RespondWithAlbumAsync(nextState, 0);
    }

    [ComponentInteraction(SsAlbumPanel.ToggleFavoritesOnlyCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleAlbumFavoritesOnlyAsync(string stateKey)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryGetQueryStateForInteraction(stateKey, out var currentState, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var nextState = CreateQueryState(
            Context.Guild.Id,
            Context.User.Id,
            currentState!.TargetUserId,
            currentState.FromUtc,
            currentState.ToUtcExclusive,
            currentState.Keyword,
            !currentState.FavoritesOnly);
        await RespondWithAlbumAsync(nextState, 0);
    }

    [ComponentInteraction(SsAlbumPanel.ToggleFavoriteCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleAlbumFavoriteAsync(string actionToken)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はボタンから実行してください。", ephemeral: true);
            return;
        }

        if (!TryGetFavoriteActionState(actionToken, out var actionState, out var error))
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        if (actionState!.GuildId != Context.Guild.Id || actionState.RequestUserId != Context.User.Id)
        {
            await RespondAsync("この操作は実行できません。", ephemeral: true);
            return;
        }

        var exists = await _db.ExistsSsAlbumFavoriteAsync(
            Context.Guild.Id,
            Context.User.Id,
            actionState.Entry.MessageId,
            actionState.Entry.AttachmentId);
        bool isFavoriteNow;
        if (exists)
        {
            await _db.DeleteSsAlbumFavoriteAsync(
                Context.Guild.Id,
                Context.User.Id,
                actionState.Entry.MessageId,
                actionState.Entry.AttachmentId);
            isFavoriteNow = false;
        }
        else
        {
            await _db.UpsertSsAlbumFavoriteAsync(
                Context.Guild.Id,
                Context.User.Id,
                actionState.Entry.AuthorUserId,
                actionState.Entry.MessageId,
                actionState.Entry.AttachmentId,
                actionState.Entry.ChannelId,
                actionState.Entry.ImageUrl,
                actionState.Entry.FileName,
                actionState.Entry.MessageContent,
                actionState.Entry.LocalPath,
                actionState.Entry.CreatedAt);
            isFavoriteNow = true;
        }

        var refreshedToken = CreateFavoriteActionToken(
            actionState.GuildId,
            actionState.RequestUserId,
            actionState.QueryStateKey,
            actionState.DisplayName,
            actionState.Page,
            actionState.LocalIndex,
            actionState.GlobalIndex,
            actionState.Entry);
        await component.UpdateAsync(props =>
        {
            props.Embed = BuildAlbumDetailEmbed(Context.Guild.Id, actionState.DisplayName, actionState.Page, actionState.LocalIndex, actionState.GlobalIndex, actionState.Entry, isFavoriteNow);
            props.Components = BuildAlbumDetailComponents(refreshedToken, isFavoriteNow);
        });
    }

    private async Task RespondWithAlbumAsync(AlbumQueryState state, int page)
    {
        var result = await BuildAlbumViewAsync(state, page);
        if (!result.Success)
        {
            await RespondAsync(result.ErrorMessage ?? "アルバムを表示できませんでした。", ephemeral: true);
            return;
        }

        await using var stream = new MemoryStream(result.CollagePng!, writable: false);
        await RespondWithFileAsync(
            stream,
            result.CollageFileName!,
            embed: result.Embed,
            components: result.Components,
            ephemeral: true);
    }

    private async Task<AlbumDetailResult> BuildAlbumDetailViewAsync(AlbumQueryState state, int page, int localIndex)
    {
        if (Context.Guild is null)
        {
            return AlbumDetailResult.Fail("この操作はサーバー内でのみ使えます。");
        }

        var load = await LoadAlbumEntriesAsync(state);
        if (!load.Success)
        {
            return AlbumDetailResult.Fail(load.ErrorMessage ?? "SSアルバムの読み込みに失敗しました。");
        }

        var entries = load.Entries!;
        if (entries.Count == 0)
        {
            return AlbumDetailResult.Fail("画像が見つかりませんでした。");
        }

        var totalPages = (int)Math.Ceiling(entries.Count / (double)AlbumPageSize);
        var safePage = Math.Clamp(page, 0, Math.Max(0, totalPages - 1));
        var pageEntries = entries.Skip(safePage * AlbumPageSize).Take(AlbumPageSize).ToList();
        if (pageEntries.Count == 0)
        {
            return AlbumDetailResult.Fail("対象ページに画像がありません。");
        }

        var safeLocalIndex = Math.Clamp(localIndex, 1, pageEntries.Count);
        var entry = pageEntries[safeLocalIndex - 1];
        var globalIndex = (safePage * AlbumPageSize) + safeLocalIndex;
        var displayName = ResolveAlbumTargetDisplayName(Context.Guild, state.TargetUserId, load.AuthorHint);
        var isFavorite = await _db.ExistsSsAlbumFavoriteAsync(Context.Guild.Id, Context.User.Id, entry.MessageId, entry.AttachmentId);
        var actionToken = CreateFavoriteActionToken(
            Context.Guild.Id,
            Context.User.Id,
            state.StateKey,
            displayName,
            safePage + 1,
            safeLocalIndex,
            globalIndex,
            entry);
        var embed = BuildAlbumDetailEmbed(Context.Guild.Id, displayName, safePage + 1, safeLocalIndex, globalIndex, entry, isFavorite);
        var components = BuildAlbumDetailComponents(actionToken, isFavorite);
        return AlbumDetailResult.Ok(embed, components);
    }

    private async Task<AlbumRenderResult> BuildAlbumViewAsync(AlbumQueryState state, int requestedPage)
    {
        if (Context.Guild is null)
        {
            return AlbumRenderResult.Fail("この操作はサーバー内でのみ使えます。");
        }

        var load = await LoadAlbumEntriesAsync(state);
        if (!load.Success)
        {
            return AlbumRenderResult.Fail(load.ErrorMessage ?? "SSアルバムの読み込みに失敗しました。");
        }

        var entries = load.Entries!;
        if (entries.Count == 0)
        {
            if (state.FavoritesOnly)
            {
                return AlbumRenderResult.Fail("お気に入り画像がまだありません。");
            }

            return AlbumRenderResult.Fail($"<@{state.TargetUserId}> の画像が見つかりませんでした。");
        }

        var favoriteKeys = (await _db.ListSsAlbumFavoriteKeysAsync(Context.Guild.Id, Context.User.Id, state.TargetUserId, MaxFavoriteRows))
            .Select(key => BuildFavoriteKey(ToUlong(key.MessageId), ToUlong(key.AttachmentId)))
            .ToHashSet(StringComparer.Ordinal);

        var totalPages = (int)Math.Ceiling(entries.Count / (double)AlbumPageSize);
        var page = Math.Clamp(requestedPage, 0, Math.Max(0, totalPages - 1));
        var pageEntries = entries
            .Skip(page * AlbumPageSize)
            .Take(AlbumPageSize)
            .ToList();
        var firstGlobalIndex = (page * AlbumPageSize) + 1;
        var lastGlobalIndex = firstGlobalIndex + pageEntries.Count - 1;
        var displayName = ResolveAlbumTargetDisplayName(Context.Guild, state.TargetUserId, load.AuthorHint);

        byte[] collagePng;
        try
        {
            collagePng = await BuildAlbumCollagePngAsync(pageEntries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render SS album collage. GuildId={GuildId}, UserId={UserId}, Page={Page}",
                Context.Guild.Id, state.TargetUserId, page);
            return AlbumRenderResult.Fail("SSアルバム画像の生成に失敗しました。時間をおいて再試行してください。");
        }

        var collageFileName = $"ss-album-{state.TargetUserId}-{page + 1}.png";
        var filters = BuildFilterSummaryText(state);

        var embed = new EmbedBuilder()
            .WithTitle(state.FavoritesOnly ? $"SSお気に入り一覧: {displayName}" : $"SSアルバム一覧: {displayName}")
            .WithColor(state.FavoritesOnly ? Discord.Color.Gold : Discord.Color.Teal)
            .WithDescription(
                $"対象: {MentionUtils.MentionUser(state.TargetUserId)}\n" +
                $"ページ {page + 1}/{totalPages}  ・  {firstGlobalIndex}〜{lastGlobalIndex} / {entries.Count} 枚\n" +
                $"{filters}\n" +
                "各サムネイル左上の番号を、下のプルダウンから選ぶと詳細を表示できます。")
            .WithImageUrl($"attachment://{collageFileName}")
            .AddField("表示枚数", $"{pageEntries.Count} / {AlbumPageSize} 枚", true)
            .AddField("総枚数", $"{entries.Count} 枚", true)
            .WithFooter(load.SourceSummary)
            .WithCurrentTimestamp();

        var builtEmbed = embed.Build();
        var components = BuildAlbumPagingComponents(state, page, totalPages, entries.Count, pageEntries, favoriteKeys);
        return AlbumRenderResult.Ok(builtEmbed, components, collagePng, collageFileName);
    }

    private async Task<IUserMessage> EnsurePanelMessageAsync(IMessageChannel channel)
    {
        MessageComponent panelComponents = SsAlbumPanel.BuildComponents();

        IUserMessage? existing = null;
        try
        {
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    SsAlbumPanel.LooksLikePanelMessage(message.Content));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search existing SS album panel message.");
        }

        if (existing is not null)
        {
            var sourceChannelId = Context.Guild is null ? null : ResolveSsChannel(Context.Guild)?.Id;
            await existing.ModifyAsync(props =>
            {
                props.Content = SsAlbumPanel.BuildPanelMessage(sourceChannelId);
                props.Components = panelComponents;
            });
            return existing;
        }

        var panelMessage = SsAlbumPanel.BuildPanelMessage(Context.Guild is null ? null : ResolveSsChannel(Context.Guild)?.Id);
        return await channel.SendMessageAsync(panelMessage, components: panelComponents, flags: MessageFlags.SuppressNotification);
    }

    private async Task<bool> IsSelectableMemberForAlbumAsync(SocketGuild guild, ulong userId)
    {
        if (userId == 0)
        {
            return false;
        }

        var user = guild.GetUser(userId);
        if (user is null || user.IsBot)
        {
            return false;
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        SocketRole? memberRole = null;
        if (settings is not null && settings.MemberRoleId != 0)
        {
            memberRole = guild.GetRole(ToUlong(settings.MemberRoleId));
        }

        memberRole ??= guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));
        if (memberRole is null)
        {
            return false;
        }

        return user.Roles.Any(role => role.Id == memberRole.Id);
    }

    private AlbumQueryState CreateDefaultQueryState(ulong guildId, ulong requestUserId, ulong targetUserId)
    {
        return CreateQueryState(guildId, requestUserId, targetUserId, fromUtc: null, toUtcExclusive: null, keyword: null, favoritesOnly: false);
    }

    private AlbumQueryState CreateQueryState(
        ulong guildId,
        ulong requestUserId,
        ulong targetUserId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive,
        string? keyword,
        bool favoritesOnly)
    {
        PruneStateCaches();
        var now = DateTimeOffset.UtcNow;
        var state = new AlbumQueryState(
            StateKey: CreateShortToken(),
            GuildId: guildId,
            RequestUserId: requestUserId,
            TargetUserId: targetUserId,
            FromUtc: fromUtc,
            ToUtcExclusive: toUtcExclusive,
            Keyword: NormalizeKeyword(keyword),
            FavoritesOnly: favoritesOnly,
            CreatedAtUtc: now,
            ExpiresAtUtc: now.AddMinutes(QueryStateTtlMinutes));
        AlbumQueryStates[state.StateKey] = state;
        return state;
    }

    private static string CreateShortToken()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }

    private bool TryGetQueryStateForInteraction(string stateKey, out AlbumQueryState? state, out string error)
    {
        state = null;
        error = "アルバム表示状態の取得に失敗しました。";

        PruneStateCaches();
        if (string.IsNullOrWhiteSpace(stateKey) || !AlbumQueryStates.TryGetValue(stateKey, out var found))
        {
            error = "表示状態が見つかりません。もう一度アルバムを開いてください。";
            return false;
        }

        if (found.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            AlbumQueryStates.TryRemove(stateKey, out _);
            error = "表示状態の有効期限が切れました。もう一度アルバムを開いてください。";
            return false;
        }

        if (Context.Guild is null || found.GuildId != Context.Guild.Id)
        {
            error = "このサーバーでは使えない表示状態です。";
            return false;
        }

        if (found.RequestUserId != Context.User.Id)
        {
            error = "この表示は作成者のみ操作できます。";
            return false;
        }

        state = found;
        return true;
    }

    private string CreateFavoriteActionToken(
        ulong guildId,
        ulong requestUserId,
        string queryStateKey,
        string displayName,
        int page,
        int localIndex,
        int globalIndex,
        SsAlbumEntry entry)
    {
        PruneStateCaches();
        var token = CreateShortToken();
        FavoriteActionStates[token] = new FavoriteActionState(
            Token: token,
            GuildId: guildId,
            RequestUserId: requestUserId,
            QueryStateKey: queryStateKey,
            DisplayName: displayName,
            Page: page,
            LocalIndex: localIndex,
            GlobalIndex: globalIndex,
            Entry: entry,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(FavoriteActionStateTtlMinutes));
        return token;
    }

    private static bool TryGetFavoriteActionState(string token, out FavoriteActionState? state, out string error)
    {
        state = null;
        error = "お気に入り状態の取得に失敗しました。";

        PruneStateCaches();
        if (string.IsNullOrWhiteSpace(token) || !FavoriteActionStates.TryGetValue(token, out var found))
        {
            error = "このボタンの有効期限が切れています。アルバムから画像を開き直してください。";
            return false;
        }

        if (found.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            FavoriteActionStates.TryRemove(token, out _);
            error = "このボタンの有効期限が切れています。アルバムから画像を開き直してください。";
            return false;
        }

        state = found;
        return true;
    }

    private static void PruneStateCaches()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in AlbumQueryStates)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                AlbumQueryStates.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in FavoriteActionStates)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                FavoriteActionStates.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task<AlbumEntryLoadResult> LoadAlbumEntriesAsync(AlbumQueryState state)
    {
        if (Context.Guild is null)
        {
            return AlbumEntryLoadResult.Fail("この操作はサーバー内でのみ使えます。");
        }

        if (state.FavoritesOnly)
        {
            try
            {
                var favorites = await _db.ListSsAlbumFavoritesAsync(Context.Guild.Id, Context.User.Id, state.TargetUserId, MaxFavoriteRows);
                var entries = ApplyAlbumFilter(BuildAlbumEntriesFromFavorites(favorites), state);
                return AlbumEntryLoadResult.Ok(entries, sourceSummary: $"お気に入り {Math.Min(favorites.Count, MaxFavoriteRows)} 件を走査", authorHint: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load SS album favorites. GuildId={GuildId}, RequestUserId={RequestUserId}, TargetUserId={TargetUserId}",
                    Context.Guild.Id, Context.User.Id, state.TargetUserId);
                return AlbumEntryLoadResult.Fail("お気に入り画像の読み込みに失敗しました。");
            }
        }

        var ssChannel = ResolveSsChannel(Context.Guild);
        if (ssChannel is null)
        {
            return AlbumEntryLoadResult.Fail("`#ss` チャンネルが見つかりません。");
        }

        IReadOnlyList<MessageLog> rows;
        try
        {
            rows = await _db.ListRecentMessageLogsByChannelAsync(Context.Guild.Id, ssChannel.Id, state.TargetUserId, MaxMessageScanRows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load SS album logs. GuildId={GuildId}, ChannelId={ChannelId}, UserId={UserId}",
                Context.Guild.Id, ssChannel.Id, state.TargetUserId);
            return AlbumEntryLoadResult.Fail("SSアルバムの読み込みに失敗しました。時間をおいて再試行してください。");
        }

        var filtered = ApplyAlbumFilter(BuildAlbumEntries(rows), state);
        var summary = $"{MentionUtils.MentionChannel(ssChannel.Id)} から最新 {Math.Min(rows.Count, MaxMessageScanRows)} 件を走査";
        var authorHint = rows.FirstOrDefault();
        return AlbumEntryLoadResult.Ok(filtered, summary, authorHint);
    }

    private static List<SsAlbumEntry> ApplyAlbumFilter(IReadOnlyList<SsAlbumEntry> source, AlbumQueryState state)
    {
        IEnumerable<SsAlbumEntry> query = source;
        if (state.FromUtc.HasValue)
        {
            query = query.Where(entry => entry.CreatedAt >= state.FromUtc.Value);
        }

        if (state.ToUtcExclusive.HasValue)
        {
            query = query.Where(entry => entry.CreatedAt < state.ToUtcExclusive.Value);
        }

        if (!string.IsNullOrWhiteSpace(state.Keyword))
        {
            var keyword = state.Keyword.Trim();
            query = query.Where(entry =>
                (!string.IsNullOrWhiteSpace(entry.MessageContent) && entry.MessageContent.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(entry.FileName) && entry.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        return query.ToList();
    }

    private static string ResolveAlbumTargetDisplayName(SocketGuild guild, ulong targetUserId, MessageLog? authorHint)
    {
        var targetUser = guild.GetUser(targetUserId);
        return targetUser?.DisplayName
            ?? targetUser?.GlobalName
            ?? authorHint?.AuthorDisplayName
            ?? authorHint?.AuthorName
            ?? targetUserId.ToString();
    }

    private static string BuildFilterSummaryText(AlbumQueryState state)
    {
        var items = new List<string>();
        if (state.FromUtc.HasValue || state.ToUtcExclusive.HasValue)
        {
            var from = state.FromUtc?.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd") ?? "指定なし";
            var to = state.ToUtcExclusive?.AddDays(-1).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd") ?? "指定なし";
            items.Add($"期間: {from} - {to}");
        }

        if (!string.IsNullOrWhiteSpace(state.Keyword))
        {
            items.Add($"キーワード: {state.Keyword}");
        }

        items.Add(state.FavoritesOnly ? "表示モード: お気に入りのみ" : "表示モード: 全件");
        return "絞り込み: " + string.Join(" / ", items);
    }

    private static Embed BuildAlbumDetailEmbed(
        ulong guildId,
        string displayName,
        int pageNumber,
        int localIndex,
        int globalIndex,
        SsAlbumEntry entry,
        bool isFavorite)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"SS詳細: {displayName}")
            .WithColor(Discord.Color.Gold)
            .WithDescription(
                $"画像番号: {localIndex}（ページ {pageNumber}） / 全体 {globalIndex} 枚目\n" +
                $"[元メッセージへ]({BuildMessageLink(guildId, entry.ChannelId, entry.MessageId)})")
            .WithImageUrl(entry.ImageUrl)
            .AddField("投稿日時", entry.CreatedAt.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm:ss"), true)
            .AddField("ファイル", string.IsNullOrWhiteSpace(entry.FileName) ? "(unknown)" : entry.FileName, true)
            .AddField("お気に入り", isFavorite ? "登録済み" : "未登録", true)
            .WithTimestamp(entry.CreatedAt);

        if (!string.IsNullOrWhiteSpace(entry.MessageContent))
        {
            embed.AddField("投稿メッセージ", Truncate(entry.MessageContent, 1000), false);
        }

        return embed.Build();
    }

    private static MessageComponent BuildAlbumDetailComponents(string actionToken, bool isFavorite)
    {
        var label = isFavorite ? "★ お気に入り解除" : "☆ お気に入り追加";
        var style = isFavorite ? ButtonStyle.Danger : ButtonStyle.Success;
        return new ComponentBuilder()
            .WithButton(label, $"{SsAlbumPanel.ToggleFavoriteCustomIdPrefix}{actionToken}", style, row: 0)
            .Build();
    }

    private static string BuildFavoriteKey(ulong messageId, ulong attachmentId) => $"{messageId}:{attachmentId}";

    private static bool TryResolveTargetUserId(string? raw, ulong fallbackUserId, out ulong userId)
    {
        userId = fallbackUserId;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (ulong.TryParse(trimmed, out var parsed) && parsed != 0)
        {
            userId = parsed;
            return true;
        }

        var numeric = new string(trimmed.Where(char.IsDigit).ToArray());
        if (ulong.TryParse(numeric, out parsed) && parsed != 0)
        {
            userId = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseDateRange(
        string? fromRaw,
        string? toRaw,
        out DateTimeOffset? fromUtc,
        out DateTimeOffset? toUtcExclusive,
        out string error)
    {
        fromUtc = null;
        toUtcExclusive = null;
        error = string.Empty;

        if (!TryParseOptionalDate(fromRaw, out var fromDate))
        {
            error = "開始日は YYYY-MM-DD 形式で入力してください。";
            return false;
        }

        if (!TryParseOptionalDate(toRaw, out var toDate))
        {
            error = "終了日は YYYY-MM-DD 形式で入力してください。";
            return false;
        }

        if (fromDate.HasValue)
        {
            fromUtc = new DateTimeOffset(fromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)).ToUniversalTime();
        }

        if (toDate.HasValue)
        {
            toUtcExclusive = new DateTimeOffset(toDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)).AddDays(1).ToUniversalTime();
        }

        if (fromUtc.HasValue && toUtcExclusive.HasValue && fromUtc.Value >= toUtcExclusive.Value)
        {
            error = "開始日は終了日以前になるように入力してください。";
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalDate(string? raw, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    private static string? NormalizeKeyword(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static SocketTextChannel? ResolveSsChannel(SocketGuild guild)
    {
        return guild.TextChannels
            .Where(ch => string.Equals(ch.Name, SourceChannelName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ch => ch.Position)
            .FirstOrDefault();
    }

    private static List<SsAlbumEntry> BuildAlbumEntries(IReadOnlyList<MessageLog> rows)
    {
        var entries = new List<SsAlbumEntry>();
        foreach (var row in rows)
        {
            var snapshots = ParseAttachmentSnapshots(row.AttachmentsJson).ToList();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var attachment = snapshots[i];
                if (!IsImageLike(attachment.ContentType, attachment.Filename))
                {
                    continue;
                }

                var imageUrl = !string.IsNullOrWhiteSpace(attachment.Url)
                    ? attachment.Url
                    : attachment.ProxyUrl;
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    continue;
                }

                var attachmentId = attachment.Id != 0 ? attachment.Id : (ulong)(i + 1);
                entries.Add(new SsAlbumEntry(
                    ToUlong(row.AuthorId),
                    ToUlong(row.MessageId),
                    attachmentId,
                    ToUlong(row.ChannelId),
                    row.CreatedAt,
                    imageUrl,
                    attachment.Filename,
                    row.Content,
                    attachment.LocalPath));
            }
        }

        return entries;
    }

    private static List<SsAlbumEntry> BuildAlbumEntriesFromFavorites(IReadOnlyList<SsAlbumFavorite> rows)
    {
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ImageUrl))
            .Select(row => new SsAlbumEntry(
                ToUlong(row.TargetUserId),
                ToUlong(row.MessageId),
                ToUlong(row.AttachmentId),
                ToUlong(row.ChannelId),
                row.PostedAt,
                row.ImageUrl,
                row.FileName,
                row.MessageContent,
                row.LocalPath))
            .ToList();
    }

    private static IEnumerable<AttachmentSnapshot> ParseAttachmentSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<AttachmentSnapshot>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<AttachmentSnapshot>>(json) ?? new List<AttachmentSnapshot>();
        }
        catch
        {
            return Array.Empty<AttachmentSnapshot>();
        }
    }

    private static bool IsImageLike(string? contentType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private static MessageComponent BuildAlbumPagingComponents(
        AlbumQueryState state,
        int page,
        int totalPages,
        int totalImages,
        IReadOnlyList<SsAlbumEntry> pageEntries,
        IReadOnlySet<string> favoriteKeys)
    {
        var prevDisabled = page <= 0;
        var nextDisabled = page >= totalPages - 1;
        var hasFilter = state.FromUtc.HasValue || state.ToUtcExclusive.HasValue || !string.IsNullOrWhiteSpace(state.Keyword) || state.FavoritesOnly;

        var builder = new ComponentBuilder()
            .WithButton("前へ", $"{SsAlbumPanel.PageCustomIdPrefix}{state.StateKey}|{page - 1}", ButtonStyle.Secondary, disabled: prevDisabled, row: 0)
            .WithButton("次へ", $"{SsAlbumPanel.PageCustomIdPrefix}{state.StateKey}|{page + 1}", ButtonStyle.Secondary, disabled: nextDisabled, row: 0)
            .WithButton("絞り込み", $"{SsAlbumPanel.FilterOpenCustomIdPrefix}{state.StateKey}", ButtonStyle.Primary, row: 0)
            .WithButton("条件解除", $"{SsAlbumPanel.FilterClearCustomIdPrefix}{state.StateKey}", ButtonStyle.Secondary, disabled: !hasFilter, row: 0)
            .WithButton(state.FavoritesOnly ? "★のみ:ON" : "★のみ:OFF", $"{SsAlbumPanel.ToggleFavoritesOnlyCustomIdPrefix}{state.StateKey}", state.FavoritesOnly ? ButtonStyle.Success : ButtonStyle.Secondary, row: 0);

        if (totalPages > 1)
        {
            var menu = new SelectMenuBuilder
            {
                CustomId = $"{SsAlbumPanel.PageSelectCustomIdPrefix}{state.StateKey}",
                Placeholder = $"ページを選択 ({page + 1}/{totalPages})",
                MinValues = 1,
                MaxValues = 1
            };

            var pageWindowSize = 25;
            var startPage = Math.Max(0, page - (pageWindowSize / 2));
            if (startPage + pageWindowSize > totalPages)
            {
                startPage = Math.Max(0, totalPages - pageWindowSize);
            }

            var endPageExclusive = Math.Min(totalPages, startPage + pageWindowSize);
            for (var pageIndex = startPage; pageIndex < endPageExclusive; pageIndex++)
            {
                var from = (pageIndex * AlbumPageSize) + 1;
                var to = Math.Min(totalImages, (pageIndex + 1) * AlbumPageSize);
                menu.AddOption(
                    label: $"{pageIndex + 1}ページ目 ({from}-{to})",
                    value: pageIndex.ToString(),
                    description: $"{to - from + 1}枚表示");
            }

            builder.WithSelectMenu(menu, 1);
        }

        if (pageEntries.Count > 0)
        {
            var imageMenu = new SelectMenuBuilder
            {
                CustomId = $"{SsAlbumPanel.ImageSelectCustomIdPrefix}{state.StateKey}|{page}",
                Placeholder = "画像番号を選択して詳細表示",
                MinValues = 1,
                MaxValues = 1
            };

            for (var i = 0; i < pageEntries.Count; i++)
            {
                var entry = pageEntries[i];
                var isFavorite = favoriteKeys.Contains(BuildFavoriteKey(entry.MessageId, entry.AttachmentId));
                var label = $"{(isFavorite ? "★" : "")}#{i + 1} {entry.CreatedAt.ToOffset(TimeSpan.FromHours(9)):MM/dd HH:mm}";
                var desc = string.IsNullOrWhiteSpace(entry.FileName)
                    ? "画像詳細を表示"
                    : Truncate(entry.FileName!, 90);
                imageMenu.AddOption(label, (i + 1).ToString(), desc);
            }

            builder.WithSelectMenu(imageMenu, totalPages > 1 ? 2 : 1);
        }

        return builder.Build();
    }

    private static bool TryParseStatePagePayload(string payload, out string stateKey, out int page)
    {
        stateKey = string.Empty;
        page = 0;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var parts = payload.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        stateKey = parts[0];
        if (string.IsNullOrWhiteSpace(stateKey))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out page))
        {
            return false;
        }

        return true;
    }

    private static string BuildMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    private async Task<byte[]> BuildAlbumCollagePngAsync(IReadOnlyList<SsAlbumEntry> pageEntries)
    {
        var width = (CanvasPadding * 2) + (GridSize * TileSize) + ((GridSize - 1) * TileGap);
        var height = (CanvasPadding * 2) + (GridSize * TileSize) + ((GridSize - 1) * TileGap);
        using var canvas = new SixLabors.ImageSharp.Image<Rgba32>(width, height, new Rgba32(28, 31, 36));

        for (var i = 0; i < (GridSize * GridSize); i++)
        {
            var x = CanvasPadding + ((i % GridSize) * (TileSize + TileGap));
            var y = CanvasPadding + ((i / GridSize) * (TileSize + TileGap));
            using var placeholder = new SixLabors.ImageSharp.Image<Rgba32>(TileSize, TileSize, new Rgba32(51, 56, 66));
            canvas.Mutate(ctx => ctx.DrawImage(placeholder, new Point(x, y), 1f));
        }

        for (var i = 0; i < pageEntries.Count; i++)
        {
            var entry = pageEntries[i];
            using var tile = await TryLoadAlbumTileImageAsync(entry);
            var x = CanvasPadding + ((i % GridSize) * (TileSize + TileGap));
            var y = CanvasPadding + ((i / GridSize) * (TileSize + TileGap));
            if (tile is not null)
            {
                tile.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(TileSize, TileSize),
                    Mode = ResizeMode.Crop
                }));

                canvas.Mutate(ctx => ctx.DrawImage(tile, new Point(x, y), 1f));
            }

            DrawTileNumberBadge(canvas, x, y, i + 1);
        }

        await using var ms = new MemoryStream();
        await canvas.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    private async Task<SixLabors.ImageSharp.Image<Rgba32>?> TryLoadAlbumTileImageAsync(SsAlbumEntry entry)
    {
        try
        {
            var localPath = ResolveArchiveFilePath(entry.LocalPath);
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                await using var file = File.OpenRead(localPath);
                return await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(file);
            }

            if (string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                return null;
            }

            using var response = await SsAlbumImageHttpClient.GetAsync(entry.ImageUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(stream);
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveArchiveFilePath(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(_attachmentArchiveRootPath))
        {
            return null;
        }

        try
        {
            var combined = Path.GetFullPath(
                Path.Combine(_attachmentArchiveRootPath, localPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(_attachmentArchiveRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return combined;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateSsAlbumImageHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static string? ResolveAttachmentArchiveRootPath(DiscordOptions options)
    {
        if (!options.AttachmentArchiveEnabled || string.IsNullOrWhiteSpace(options.AttachmentArchiveRootPath))
        {
            return null;
        }

        return Path.GetFullPath(options.AttachmentArchiveRootPath, Environment.CurrentDirectory);
    }

    private static void DrawTileNumberBadge(SixLabors.ImageSharp.Image<Rgba32> image, int tileX, int tileY, int localNumber)
    {
        const int badgeWidth = 42;
        const int badgeHeight = 28;
        const int margin = 6;

        for (var y = tileY + margin; y < tileY + margin + badgeHeight; y++)
        {
            for (var x = tileX + margin; x < tileX + margin + badgeWidth; x++)
            {
                if (x >= 0 && y >= 0 && x < image.Width && y < image.Height)
                {
                    image[x, y] = new Rgba32(8, 10, 14, 210);
                }
            }
        }

        if (SsAlbumBadgeFont is null)
        {
            return;
        }

        var text = localNumber.ToString();
        var originX = tileX + margin + (text.Length == 1 ? 13 : 8);
        var originY = tileY + margin + 2;
        DrawTextSmooth(image, text, SsAlbumBadgeFont, new Rgba32(255, 255, 255, 255), originX, originY);
    }

    private static Font? TryCreateSsAlbumBadgeFont()
    {
        var preferredFonts = new[] { "Segoe UI", "Yu Gothic UI", "Meiryo UI", "Meiryo", "Arial" };
        foreach (var name in preferredFonts)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                return family.CreateFont(20, FontStyle.Bold);
            }
        }

        if (SystemFonts.Collection.Families.Any())
        {
            return SystemFonts.Collection.Families.First().CreateFont(20, FontStyle.Bold);
        }

        return null;
    }

    private static void DrawTextSmooth(SixLabors.ImageSharp.Image<Rgba32> image, string text, Font font, Rgba32 color, float x, float y)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        image.Mutate(ctx =>
        {
            ctx.DrawText(new RichTextOptions(font)
            {
                Origin = new PointF(x, y)
            }, text, color);
        });
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0 || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);

    private sealed record AttachmentSnapshot(
        ulong Id,
        string? Url,
        string? ProxyUrl,
        string? Filename,
        int Size,
        string? ContentType,
        int? Height,
        int? Width,
        bool IsSpoiler,
        string? LocalPath);

    private sealed record SsAlbumEntry(
        ulong AuthorUserId,
        ulong MessageId,
        ulong AttachmentId,
        ulong ChannelId,
        DateTimeOffset CreatedAt,
        string ImageUrl,
        string? FileName,
        string? MessageContent,
        string? LocalPath);

    private sealed record AlbumQueryState(
        string StateKey,
        ulong GuildId,
        ulong RequestUserId,
        ulong TargetUserId,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtcExclusive,
        string? Keyword,
        bool FavoritesOnly,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record FavoriteActionState(
        string Token,
        ulong GuildId,
        ulong RequestUserId,
        string QueryStateKey,
        string DisplayName,
        int Page,
        int LocalIndex,
        int GlobalIndex,
        SsAlbumEntry Entry,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed class AlbumEntryLoadResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public IReadOnlyList<SsAlbumEntry>? Entries { get; init; }
        public string SourceSummary { get; init; } = string.Empty;
        public MessageLog? AuthorHint { get; init; }

        public static AlbumEntryLoadResult Ok(IReadOnlyList<SsAlbumEntry> entries, string sourceSummary, MessageLog? authorHint)
            => new() { Success = true, Entries = entries, SourceSummary = sourceSummary, AuthorHint = authorHint };

        public static AlbumEntryLoadResult Fail(string message)
            => new() { Success = false, ErrorMessage = message };
    }

    private sealed class AlbumRenderResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public Embed? Embed { get; init; }
        public MessageComponent? Components { get; init; }
        public byte[]? CollagePng { get; init; }
        public string? CollageFileName { get; init; }

        public static AlbumRenderResult Ok(Embed embed, MessageComponent components, byte[] collagePng, string collageFileName)
            => new() { Success = true, Embed = embed, Components = components, CollagePng = collagePng, CollageFileName = collageFileName };

        public static AlbumRenderResult Fail(string message)
            => new() { Success = false, ErrorMessage = message };
    }

    private sealed class AlbumDetailResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public Embed? Embed { get; init; }
        public MessageComponent? Components { get; init; }

        public static AlbumDetailResult Ok(Embed embed, MessageComponent components)
            => new() { Success = true, Embed = embed, Components = components };

        public static AlbumDetailResult Fail(string message)
            => new() { Success = false, ErrorMessage = message };
    }
}

public sealed class SsAlbumFilterModal : IModal
{
    public string Title => "SSアルバム絞り込み";

    [ModalTextInput("ss_album_filter_target_user", TextInputStyle.Short, maxLength: 32)]
    public string TargetUser { get; set; } = string.Empty;

    [ModalTextInput("ss_album_filter_from_date", TextInputStyle.Short, maxLength: 10)]
    public string FromDate { get; set; } = string.Empty;

    [ModalTextInput("ss_album_filter_to_date", TextInputStyle.Short, maxLength: 10)]
    public string ToDate { get; set; } = string.Empty;

    [ModalTextInput("ss_album_filter_keyword", TextInputStyle.Short, maxLength: 80)]
    public string Keyword { get; set; } = string.Empty;
}
