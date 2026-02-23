using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("lodestone", "Lodestone連携")]
public sealed class LodestoneModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string MemberRegistrationDcPrefix = "member_registration_dc";
    private const string MemberRegistrationWorldPrefix = "member_registration_world";
    private const string MemberProfileChangeDcPrefix = "member_profile_change_dc";
    private const string MemberProfileChangeWorldPrefix = "member_profile_change_world";
    private const string MemberRegistrationSubmitCustomId = "member_registration_submit";
    private const string MemberRegistrationSubmitConfirmCustomId = "member_registration_submit_confirm";
    private const string MemberRegistrationSubmitCancelCustomId = "member_registration_submit_cancel";
    private const string MemberRegistrationSubmitWithVoiceCustomId = "member_registration_submit_with_voice";
    private const string MemberRegistrationSubmitWithoutVoiceCustomId = "member_registration_submit_without_voice";
    private const string MemberProfileEditModalCustomId = "member_profile_edit_modal";
    private const string MemberRegistrationApprovePrefix = "member_registration_review_approve";
    private const string MemberRegistrationRejectPrefix = "member_registration_review_reject";
    private const string CwlsChannelName = "cwls";
    private static readonly TimeSpan MemberRegistrationDraftTimeout = TimeSpan.FromMinutes(15);

    private readonly Db _db;
    private readonly LodestoneClient _lodestone;
    private readonly TokenService _tokenService;
    private readonly LodestoneRateLimitService _rateLimit;
    private readonly MemberRegistrationDraftContextTracker _memberRegistrationDrafts;
    private readonly StatsEmbedService _stats;
    private readonly GuildAuditLogService _audit;
    private readonly ILogger<LodestoneModule> _logger;
    private readonly DiscordOptions _options;

    public LodestoneModule(
        Db db,
        LodestoneClient lodestone,
        TokenService tokenService,
        LodestoneRateLimitService rateLimit,
        MemberRegistrationDraftContextTracker memberRegistrationDrafts,
        StatsEmbedService stats,
        GuildAuditLogService audit,
        ILogger<LodestoneModule> logger,
        IOptions<DiscordOptions> options)
    {
        _db = db;
        _lodestone = lodestone;
        _tokenService = tokenService;
        _rateLimit = rateLimit;
        _memberRegistrationDrafts = memberRegistrationDrafts;
        _stats = stats;
        _audit = audit;
        _logger = logger;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにLodestone連携パネルを設置します")]
    public async Task SetupAsync()
    {
        var channel = await ResolveSetupChannelAsync();
        if (channel is null)
        {
            return;
        }

        await DeferAsync(ephemeral: true);

        var lodestonePanel = await _db.GetGuildLodestonePanelAsync(Context.Guild.Id);

        var lodestoneMessage = await EnsureLodestonePanelMessageAsync(
            channel,
            lodestonePanel is null ? null : ToUlong(lodestonePanel.MessageId));

        await _db.UpsertGuildLodestonePanelAsync(Context.Guild.Id, channel.Id, lodestoneMessage.Id);
        await FollowupAsync("このチャンネルにLodestone連携パネルを設置しました。", ephemeral: true);
    }

    [RequireBotAdmin]
    [SlashCommand("cwls", "CWLS情報を取得してcwlsチャンネルへ投稿します")]
    public async Task PostCwlsAsync([Summary(description: "LodestoneのCWLSページURL")] string url)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        if (!_lodestone.TryNormalizeCwlsUrl(url, out var normalizedUrl, out var normalizeError))
        {
            await RespondEphemeralAsync(normalizeError, followup: true);
            return;
        }

        string html;
        try
        {
            html = await _lodestone.FetchHtmlAsync(normalizedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CWLS page.");
            await RespondEphemeralAsync($"CWLSページの取得に失敗しました: {ex.Message}", followup: true);
            return;
        }

        if (!_lodestone.TryParseCwls(html, normalizedUrl, out var cwls))
        {
            await RespondEphemeralAsync("CWLSページの解析に失敗しました。URLを確認してください。", followup: true);
            return;
        }

        var channel = Context.Guild.TextChannels.FirstOrDefault(text =>
            string.Equals(text.Name, CwlsChannelName, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            await RespondEphemeralAsync($"`{CwlsChannelName}` チャンネルが見つかりません。", followup: true);
            return;
        }

        var embed = BuildCwlsEmbed(cwls);
        await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);

        await RespondEphemeralAsync($"`{channel.Name}` にCWLS情報を投稿しました。", followup: true);
    }

    [ComponentInteraction(LodestonePanel.IssueCustomId, ignoreGroupNames: true)]
    public async Task LinkIssueButtonAsync()
    {
        await DeferAsync(ephemeral: true);
        await CreateLinkAsync(followup: true);
    }

    [ComponentInteraction(LodestonePanel.VerifyCustomId, ignoreGroupNames: true)]
    public async Task ShowVerifyModalAsync()
    {
        await RespondWithModalAsync<LodestoneVerifyModal>("lodestone_verify_modal");
    }

    [ModalInteraction("lodestone_verify_modal", ignoreGroupNames: true)]
    public async Task HandleVerifyModalAsync(LodestoneVerifyModal modal)
    {
        await DeferAsync(ephemeral: true);
        await VerifyInternalAsync(modal.Url, followup: true);
    }

    private async Task CreateLinkAsync(bool followup)
    {
        _rateLimit.Prune(DateTimeOffset.UtcNow, _options.LodestoneVerifyWindowMinutes);
        if (!_rateLimit.TryIssue(Context.User.Id, DateTimeOffset.UtcNow, _options.LodestoneIssueCooldownSeconds, out var issueRetry))
        {
            var wait = Math.Max(1, (int)Math.Ceiling(issueRetry.TotalSeconds));
            await LogRateLimitAsync("Lodestoneコード再発行制限", wait);
            await RespondEphemeralAsync($"連携コードの再発行が速すぎます。{wait}秒後に再試行してください。", followup);
            return;
        }

        await _db.EnsureDiscordUserAsync(Context.User.Id);
        await _db.InvalidateActiveTokensAsync(Context.User.Id);

        var token = _tokenService.GenerateToken();
        var tokenHash = _tokenService.HashToken(token);
        var expiresMinutes = _options.TokenExpiryMinutes > 0 ? _options.TokenExpiryMinutes : 30;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(expiresMinutes);

        await _db.CreateLinkTokenAsync(Context.User.Id, tokenHash, expiresAt);

        var unix = expiresAt.ToUnixTimeSeconds();
        var message = $"連携コード: `{token}`\n" +
                      $"有効期限: <t:{unix}:f> ({expiresMinutes}分)\n" +
                      "1. Lodestoneプロフィールの自己紹介文にコードを貼る\n" +
                      "2. 「連携確認」ボタンを押す";

        await RespondEphemeralAsync(message, followup);
    }

    private async Task VerifyInternalAsync(string url, bool followup)
    {
        if (!_lodestone.TryExtractLodestoneId(url, out var lodestoneId, out var error))
        {
            await LogInvalidOperationAsync("Lodestone URL不正", $"👤 ユーザー: {FormatUser(Context.User)}\n🔗 入力: {TruncateForLog(url, 240)}");
            await RespondEphemeralAsync(error, followup);
            return;
        }

        var token = await _db.GetActiveTokenAsync(Context.User.Id);
        if (token is null)
        {
            await LogInvalidOperationAsync("Lodestone連携コード未発行", $"👤 ユーザー: {FormatUser(Context.User)}\n🆔 UserId: {Context.User.Id}");
            await RespondEphemeralAsync("有効な連携コードが見つかりません。発行ボタンで再発行してください。", followup);
            return;
        }

        var normalizedUrl = _lodestone.NormalizeUrl(url, lodestoneId);

        _rateLimit.Prune(DateTimeOffset.UtcNow, _options.LodestoneVerifyWindowMinutes);
        if (!_rateLimit.TryVerify(Context.User.Id, DateTimeOffset.UtcNow, _options.LodestoneVerifyMaxAttempts, _options.LodestoneVerifyWindowMinutes, out var verifyRetry))
        {
            var wait = Math.Max(1, (int)Math.Ceiling(verifyRetry.TotalSeconds));
            await LogRateLimitAsync("Lodestone連携確認制限", wait);
            await RespondEphemeralAsync($"連携確認の試行回数が上限です。{wait}秒後に再試行してください。", followup);
            return;
        }

        string html;
        try
        {
            html = await _lodestone.FetchProfileHtmlAsync(normalizedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Lodestone profile.");
            await LogFailureAsync(
                "Lodestoneプロフィール取得失敗",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🔗 URL: {normalizedUrl}\n❌ Error: {ex.Message}");
            await RespondEphemeralAsync(ex.Message, followup);
            return;
        }

        if (!_tokenService.MatchesStoredToken(html, token.Token))
        {
            await LogInvalidOperationAsync(
                "Lodestone連携コード不一致",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🆔 UserId: {Context.User.Id}\n🔗 URL: {normalizedUrl}");
            await RespondEphemeralAsync("プロフィールに連携コードが見つかりませんでした。貼り付けを確認してください。", followup);
            return;
        }

        if (!_lodestone.TryParseProfile(html, out var profile))
        {
            await LogFailureAsync(
                "Lodestoneプロフィール解析失敗",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🔗 URL: {normalizedUrl}");
            await RespondEphemeralAsync("プロフィール解析に失敗しました。時間をおいて再試行してください。", followup);
            return;
        }

        var linkedByOther = await _db.IsLodestoneLinkedByAnotherUserAsync(Context.User.Id, lodestoneId);
        if (linkedByOther)
        {
            await LogInvalidOperationAsync(
                "Lodestone重複連携",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🆔 LodestoneId: {lodestoneId}");
            await RespondEphemeralAsync("このキャラクターは既に別のユーザーで連携されています。", followup);
            return;
        }

        var resolvedDc = DcResolver.Resolve(profile.DataCenter, profile.World);
        var portraitUrl = _lodestone.ExtractPortraitUrl(html);

        await _db.UpsertCharacterAsync(new LodestoneCharacter
        {
            LodestoneId = lodestoneId,
            Name = profile.Name,
            World = profile.World,
            DataCenter = resolvedDc,
            PortraitUrl = portraitUrl,
            LodestoneUrl = normalizedUrl
        });

        await _db.ReplaceMainCharacterAsync(Context.User.Id, lodestoneId);

        await _db.MarkTokenUsedAsync(token.TokenId);

        var dc = string.IsNullOrWhiteSpace(resolvedDc) ? string.Empty : $" [{resolvedDc}]";
        var success = $"連携完了: {profile.Name} {profile.World}{dc}";

        await LogCharacterLinkedAsync(profile.Name, profile.World, resolvedDc, normalizedUrl, portraitUrl);

        var components = BuildMemberRegistrationStartComponents();
        var message = $"{success}\nLODESTONE連携は任意です。メンバー付与は「メンバー登録」から進めてください。";
        if (followup)
        {
            await FollowupAsync(message, components: components, ephemeral: true);
        }
        else
        {
            await RespondAsync(message, components: components, ephemeral: true);
        }
    }

    [ComponentInteraction("member_registration_start", ignoreGroupNames: true)]
    public Task ShowMemberRegistrationModalAsync()
        => ShowMemberRegistrationModalInternalAsync();

    [ComponentInteraction("user_profile_input", ignoreGroupNames: true)]
    public Task ShowLegacyProfileModalAsync()
        => ShowMemberRegistrationModalInternalAsync();

    [ComponentInteraction(LodestonePanel.EditProfileCustomId, ignoreGroupNames: true)]
    public async Task ShowMemberProfileEditModalAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<MemberProfileEditModal>(MemberProfileEditModalCustomId);
    }

    [ComponentInteraction(LodestonePanel.ChangeDataCenterWorldCustomId, ignoreGroupNames: true)]
    public async Task ShowMemberProfileWorldChangeDcSelectAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(profile))
        {
            await RespondAsync("DC/サーバー変更はメンバー登録後に利用できます。先に「メンバー登録」を完了してください。", ephemeral: true);
            return;
        }

        await RespondAsync("変更先のDCを選択してください。", components: BuildDataCenterSelectComponents(MemberProfileChangeDcPrefix), ephemeral: true);
    }

    private async Task ShowMemberRegistrationModalInternalAsync()
    {
        await RespondWithModalAsync<UserProfileModal>("member_registration_modal");
    }

    [ModalInteraction("member_registration_modal", ignoreGroupNames: true)]
    public Task HandleMemberRegistrationModalAsync(UserProfileModal modal)
        => HandleMemberRegistrationModalInternalAsync(modal);

    [ModalInteraction("user_profile_modal", ignoreGroupNames: true)]
    public Task HandleLegacyProfileModalAsync(UserProfileModal modal)
        => HandleMemberRegistrationModalInternalAsync(modal);

    [ModalInteraction(MemberProfileEditModalCustomId, ignoreGroupNames: true)]
    public async Task HandleMemberProfileEditModalAsync(MemberProfileEditModal modal)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var currentProfile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(currentProfile))
        {
            await FollowupAsync("プロフィール編集はメンバー登録後に利用できます。先に「メンバー登録」を完了してください。", ephemeral: true);
            return;
        }

        var inputFfxivName = modal.FfxivName?.Trim() ?? string.Empty;
        var inputBio = modal.Bio?.Trim() ?? string.Empty;
        var inputX = modal.XHandle?.Trim() ?? string.Empty;
        var inputBluesky = modal.BlueskyHandle?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(inputFfxivName) &&
            string.IsNullOrWhiteSpace(inputBio) &&
            string.IsNullOrWhiteSpace(inputX) &&
            string.IsNullOrWhiteSpace(inputBluesky))
        {
            await FollowupAsync("更新項目がありませんでした。", ephemeral: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(inputFfxivName) && !IsSpacedCharacterName(inputFfxivName))
        {
            await FollowupAsync("FFXIV名は「姓 名」のように空白を入れて入力してください。", ephemeral: true);
            return;
        }

        var currentFfxivName = currentProfile!.FfxivName ?? string.Empty;
        var currentWorld = currentProfile.FfxivWorld ?? string.Empty;
        var currentDataCenter = currentProfile.FfxivDataCenter ?? string.Empty;
        var currentBio = currentProfile.Bio ?? string.Empty;
        var currentX = currentProfile.XHandle;
        var currentBluesky = currentProfile.BlueskyHandle;

        var ffxivName = string.IsNullOrWhiteSpace(inputFfxivName) ? currentFfxivName : inputFfxivName;
        var bio = string.IsNullOrWhiteSpace(inputBio) ? currentBio : inputBio;
        var xHandle = string.IsNullOrWhiteSpace(inputX) ? currentX : SocialLinkService.NormalizeX(inputX);
        var blueskyHandle = string.IsNullOrWhiteSpace(inputBluesky) ? currentBluesky : SocialLinkService.NormalizeBluesky(inputBluesky);

        var changed =
            !string.Equals(ffxivName, currentFfxivName, StringComparison.Ordinal) ||
            !string.Equals(bio, currentBio, StringComparison.Ordinal) ||
            !string.Equals(xHandle ?? string.Empty, currentX ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(blueskyHandle ?? string.Empty, currentBluesky ?? string.Empty, StringComparison.Ordinal);
        if (!changed)
        {
            await FollowupAsync("変更はありませんでした。", ephemeral: true);
            return;
        }

        await _db.EnsureDiscordUserAsync(Context.User.Id);
        await _db.UpsertDiscordUserProfileAsync(Context.User.Id, guildUser.DisplayName, Context.User.Username);
        await _db.UpdateUserRegistrationProfileAsync(
            Context.User.Id,
            ffxivName,
            currentWorld,
            currentDataCenter,
            xHandle,
            blueskyHandle,
            bio);

        await UpdateNicknameAsync(guildUser, ffxivName, currentWorld);
        await FollowupAsync("プロフィールを更新しました。", ephemeral: true);
    }

    private async Task HandleMemberRegistrationModalInternalAsync(UserProfileModal modal)
    {
        await DeferAsync(ephemeral: true);

        var ffxivName = modal.FfxivName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ffxivName))
        {
            await LogInvalidOperationAsync("メンバー登録入力不正", $"👤 ユーザー: {FormatUser(Context.User)}\n❌ 理由: FFXIV名が空です");
            await FollowupAsync("FFXIV名を入力してください。", ephemeral: true);
            return;
        }
        if (!IsSpacedCharacterName(ffxivName))
        {
            await LogInvalidOperationAsync("メンバー登録入力不正", $"👤 ユーザー: {FormatUser(Context.User)}\n🧩 入力名: {TruncateForLog(ffxivName, 80)}\n❌ 理由: 姓名の空白なし");
            await FollowupAsync("FFXIV名は「姓 名」のように空白を入れて入力してください。", ephemeral: true);
            return;
        }

        var bio = modal.Bio?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bio))
        {
            await LogInvalidOperationAsync("メンバー登録入力不正", $"👤 ユーザー: {FormatUser(Context.User)}\n❌ 理由: 一言が空です");
            await FollowupAsync("一言を入力してください。", ephemeral: true);
            return;
        }

        var xHandle = SocialLinkService.NormalizeX(modal.XHandle);
        var blueskyHandle = SocialLinkService.NormalizeBluesky(modal.BlueskyHandle);

        _memberRegistrationDrafts.PruneExpired(DateTimeOffset.UtcNow, MemberRegistrationDraftTimeout);
        _memberRegistrationDrafts.Set(Context.User.Id, new MemberRegistrationDraftContext(
            ffxivName,
            bio,
            xHandle,
            blueskyHandle,
            DateTimeOffset.UtcNow));

        await FollowupAsync("DCを選択してください。", components: BuildDataCenterSelectComponents(MemberRegistrationDcPrefix), ephemeral: true);
    }

    [ComponentInteraction("member_profile_change_dc:*", ignoreGroupNames: true)]
    public async Task HandleMemberProfileChangeDcSelectAsync(string dataCenterToken)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(profile))
        {
            await RespondAsync("DC/サーバー変更はメンバー登録後に利用できます。先に「メンバー登録」を完了してください。", ephemeral: true);
            return;
        }

        if (!TryResolveDataCenterFromToken(dataCenterToken, out var dataCenterName))
        {
            await RespondAsync("選択したDCを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        if (!DcResolver.TryGetWorlds(dataCenterName, out var worlds) || worlds.Count == 0)
        {
            await RespondAsync("このDCのサーバー一覧を取得できませんでした。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{dataCenterName} のサーバーを選択してください。",
            components: BuildWorldSelectComponents(MemberProfileChangeWorldPrefix, dataCenterName, worlds),
            ephemeral: true);
    }

    [ComponentInteraction("member_profile_change_world:*:*", ignoreGroupNames: true)]
    public async Task HandleMemberProfileChangeWorldSelectAsync(string dataCenterToken, string worldToken)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(profile))
        {
            await FollowupAsync("DC/サーバー変更はメンバー登録後に利用できます。先に「メンバー登録」を完了してください。", ephemeral: true);
            return;
        }

        if (!TryResolveDataCenterFromToken(dataCenterToken, out var dataCenterName))
        {
            await FollowupAsync("選択したDCを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        if (!DcResolver.TryGetWorlds(dataCenterName, out var worlds) || worlds.Count == 0)
        {
            await FollowupAsync("このDCのサーバー一覧を取得できませんでした。", ephemeral: true);
            return;
        }

        var worldName = worlds.FirstOrDefault(world =>
            string.Equals(ToWorldToken(world), worldToken, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(worldName))
        {
            await FollowupAsync("選択したサーバーを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        var currentProfile = profile!;
        var currentWorld = currentProfile.FfxivWorld ?? string.Empty;
        var currentDataCenter = currentProfile.FfxivDataCenter ?? string.Empty;
        if (string.Equals(worldName, currentWorld, StringComparison.Ordinal) &&
            string.Equals(dataCenterName, currentDataCenter, StringComparison.Ordinal))
        {
            await FollowupAsync("変更はありませんでした。", ephemeral: true);
            return;
        }

        await _db.EnsureDiscordUserAsync(Context.User.Id);
        await _db.UpsertDiscordUserProfileAsync(Context.User.Id, guildUser.DisplayName, Context.User.Username);
        await _db.UpdateUserRegistrationProfileAsync(
            Context.User.Id,
            currentProfile.FfxivName ?? string.Empty,
            worldName,
            dataCenterName,
            currentProfile.XHandle,
            currentProfile.BlueskyHandle,
            currentProfile.Bio ?? string.Empty);

        await GrantDcRoleAsync(guildUser, dataCenterName);
        await UpdateNicknameAsync(guildUser, currentProfile.FfxivName ?? string.Empty, worldName);
        await FollowupAsync($"DC/サーバーを更新しました: {worldName} [{dataCenterName}]", ephemeral: true);
    }

    [ComponentInteraction("member_registration_dc:*", ignoreGroupNames: true)]
    public async Task HandleMemberRegistrationDcSelectAsync(string dataCenterToken)
    {
        _memberRegistrationDrafts.PruneExpired(DateTimeOffset.UtcNow, MemberRegistrationDraftTimeout);
        if (!_memberRegistrationDrafts.TryGet(Context.User.Id, out _))
        {
            await LogInvalidOperationAsync("メンバー登録期限切れ", $"👤 ユーザー: {FormatUser(Context.User)}");
            await RespondAsync("登録情報の有効期限が切れました。もう一度「メンバー登録」から入力してください。", ephemeral: true);
            return;
        }

        if (!TryResolveDataCenterFromToken(dataCenterToken, out var dataCenterName))
        {
            await LogInvalidOperationAsync(
                "メンバー登録DC不正",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🏷️ token: {TruncateForLog(dataCenterToken, 80)}");
            await RespondAsync("選択したDCを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        if (!DcResolver.TryGetWorlds(dataCenterName, out var worlds) || worlds.Count == 0)
        {
            await LogFailureAsync(
                "メンバー登録DCワールド取得失敗",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🌐 DC: {dataCenterName}");
            await RespondAsync("このDCのサーバー一覧を取得できませんでした。", ephemeral: true);
            return;
        }

        await RespondAsync(
            $"{dataCenterName} のサーバーを選択してください。",
            components: BuildWorldSelectComponents(MemberRegistrationWorldPrefix, dataCenterName, worlds),
            ephemeral: true);
    }

    [ComponentInteraction("member_registration_world:*:*", ignoreGroupNames: true)]
    public async Task HandleMemberRegistrationWorldSelectAsync(string dataCenterToken, string worldToken)
    {
        _memberRegistrationDrafts.PruneExpired(DateTimeOffset.UtcNow, MemberRegistrationDraftTimeout);
        if (!_memberRegistrationDrafts.TryRemove(Context.User.Id, out var draft))
        {
            await LogInvalidOperationAsync("メンバー登録期限切れ", $"👤 ユーザー: {FormatUser(Context.User)}");
            await RespondAsync("登録情報の有効期限が切れました。もう一度「メンバー登録」から入力してください。", ephemeral: true);
            return;
        }

        if (!TryResolveDataCenterFromToken(dataCenterToken, out var dataCenterName))
        {
            await LogInvalidOperationAsync(
                "メンバー登録DC不正",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🏷️ token: {TruncateForLog(dataCenterToken, 80)}");
            await RespondAsync("選択したDCを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        if (!DcResolver.TryGetWorlds(dataCenterName, out var worlds) || worlds.Count == 0)
        {
            await LogFailureAsync(
                "メンバー登録DCワールド取得失敗",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🌐 DC: {dataCenterName}");
            await RespondAsync("このDCのサーバー一覧を取得できませんでした。", ephemeral: true);
            return;
        }

        var worldName = worlds.FirstOrDefault(world =>
            string.Equals(ToWorldToken(world), worldToken, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(worldName))
        {
            await LogInvalidOperationAsync(
                "メンバー登録ワールド不正",
                $"👤 ユーザー: {FormatUser(Context.User)}\n🌐 DC: {dataCenterName}\n🏷️ token: {TruncateForLog(worldToken, 80)}");
            await RespondAsync("選択したサーバーを判定できませんでした。もう一度やり直してください。", ephemeral: true);
            return;
        }

        await _db.EnsureDiscordUserAsync(Context.User.Id);

        var displayName = (Context.User as SocketGuildUser)?.DisplayName ?? Context.User.GlobalName ?? Context.User.Username;
        await _db.UpsertDiscordUserProfileAsync(Context.User.Id, displayName, Context.User.Username);
        await _db.UpdateUserRegistrationProfileAsync(
            Context.User.Id,
            draft.FfxivName,
            worldName,
            dataCenterName,
            draft.XHandle,
            draft.BlueskyHandle,
            draft.Bio);

        var contentRoleDefinitions = await LoadMemberContentRoleDefinitionsAsync(Context.Guild!.Id);
        var contentRoleComponents = MemberContentRolePanel.BuildComponents(contentRoleDefinitions);
        var completedMessage =
            $"登録情報を受け付けました: {draft.FfxivName}@{worldName} [{dataCenterName}]\n" +
            "プレイ中のコンテンツロールを選択し、最後に「登録申請を送信」を押してください。";
        await RespondAsync(completedMessage, components: contentRoleComponents, ephemeral: true);
        await FollowupAsync(
            "最終確認: ロール選択が終わったら、こちらから申請を送信してください。",
            components: BuildMemberRegistrationSubmitComponents(),
            ephemeral: true);
    }

    [ComponentInteraction(MemberRegistrationSubmitCustomId, ignoreGroupNames: true)]
    public async Task ShowMemberRegistrationSubmitConfirmAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(profile))
        {
            await RespondAsync("登録情報が不足しています。もう一度「メンバー登録」から入力してください。", ephemeral: true);
            return;
        }

        var previewEmbed = await BuildMemberRegistrationPreviewEmbedAsync(guildUser, profile!);
        await RespondAsync(
            "最終確認: この内容でメンバー登録申請を送信しますか？",
            embed: previewEmbed,
            components: BuildMemberRegistrationSubmitConfirmComponents(),
            ephemeral: true);
    }

    [ComponentInteraction(MemberRegistrationSubmitConfirmCustomId, ignoreGroupNames: true)]
    public Task ConfirmMemberRegistrationSubmitAsync()
        => SubmitMemberRegistrationInternalAsync();

    [ComponentInteraction(MemberRegistrationSubmitCancelCustomId, ignoreGroupNames: true)]
    public Task CancelMemberRegistrationSubmitAsync()
        => RespondAsync("送信を取り消しました。", ephemeral: true);

    [ComponentInteraction(MemberRegistrationSubmitWithVoiceCustomId, ignoreGroupNames: true)]
    public Task GrantVoiceNotificationRoleAfterRegistrationAsync()
        => HandlePostRegistrationVoiceNotificationOptionAsync(grantVoiceNotificationRole: true);

    [ComponentInteraction(MemberRegistrationSubmitWithoutVoiceCustomId, ignoreGroupNames: true)]
    public Task SkipVoiceNotificationRoleAfterRegistrationAsync()
        => HandlePostRegistrationVoiceNotificationOptionAsync(grantVoiceNotificationRole: false);

    private async Task SubmitMemberRegistrationInternalAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = settings?.RegistrationApprovalRequired ?? true;
        var memberRole = ResolveMemberRole(Context.Guild, settings);
        if (memberRole is null)
        {
            await FollowupAsync("メンバーロールを取得できませんでした。管理者に連絡してください。", ephemeral: true);
            return;
        }

        if (memberRole is not null && guildUser.Roles.Any(role => role.Id == memberRole.Id))
        {
            await FollowupAsync("既にメンバー登録済みです。", ephemeral: true);
            return;
        }

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        if (!HasCompletedRegistrationProfile(profile))
        {
            await FollowupAsync("登録情報が不足しています。もう一度「メンバー登録」から入力してください。", ephemeral: true);
            return;
        }

        var completedProfile = profile!;
        if (!approvalRequired)
        {
            var directProvisionalRole = ResolveProvisionalRole(Context.Guild, settings);
            try
            {
                if (directProvisionalRole is not null && guildUser.Roles.Any(role => role.Id == directProvisionalRole.Id))
                {
                    if (!TryValidateRoleOperation(Context.Guild, guildUser, directProvisionalRole, out var removeReason))
                    {
                        _logger.LogWarning(
                            "Skipped provisional role revoke on direct registration due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                            Context.Guild.Id,
                            guildUser.Id,
                            directProvisionalRole.Id,
                            removeReason);
                        await FollowupAsync($"仮登録ロールを解除できません: {removeReason}", ephemeral: true);
                        return;
                    }

                    await guildUser.RemoveRoleAsync(directProvisionalRole);
                }

                if (!TryValidateRoleOperation(Context.Guild, guildUser, memberRole!, out var grantReason))
                {
                    _logger.LogWarning(
                        "Skipped direct member role grant due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                        Context.Guild.Id,
                        guildUser.Id,
                        memberRole!.Id,
                        grantReason);
                    await FollowupAsync($"メンバーロールを付与できません: {grantReason}", ephemeral: true);
                    return;
                }

                await guildUser.AddRoleAsync(memberRole!);
                await GrantDcRoleAsync(guildUser, completedProfile.FfxivDataCenter);
                await UpdateNicknameAsync(guildUser, completedProfile.FfxivName ?? string.Empty, completedProfile.FfxivWorld ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete direct member registration. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, guildUser.Id);
                await FollowupAsync("登録処理に失敗しました。Botの権限を確認してください。", ephemeral: true);
                return;
            }

            await LogMemberRegistrationCompletedAsync(
                guildUser,
                completedProfile.FfxivName ?? "(unknown)",
                completedProfile.FfxivWorld ?? "(unknown)",
                completedProfile.FfxivDataCenter ?? "(unknown)",
                completedProfile.XHandle,
                completedProfile.BlueskyHandle);
            _ = _stats.RefreshIfConfiguredAsync(Context.Guild);
            await FollowupAsync("登録を完了しました。メンバーロールを付与しました。", ephemeral: true);
            await ShowPostRegistrationVoiceNotificationOfferAsync();
            return;
        }

        var provisionalRole = ResolveProvisionalRole(Context.Guild, settings);
        if (provisionalRole is null)
        {
            await FollowupAsync("仮登録ロールを取得できませんでした。管理者に連絡してください。", ephemeral: true);
            return;
        }

        if (guildUser.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            await FollowupAsync("登録申請は送信済みです。管理者の確認をお待ちください。", ephemeral: true);
            return;
        }

        var reviewChannel = ResolveRegistrationReviewChannel(Context.Guild, settings);
        if (reviewChannel is null)
        {
            await FollowupAsync("登録確認チャンネルを取得できませんでした。管理者に連絡してください。", ephemeral: true);
            return;
        }

        try
        {
            if (!TryValidateRoleOperation(Context.Guild, guildUser, provisionalRole, out var reason))
            {
                _logger.LogWarning(
                    "Skipped provisional role grant due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                    Context.Guild.Id,
                    guildUser.Id,
                    provisionalRole.Id,
                    reason);
                await FollowupAsync($"仮登録ロールを付与できません: {reason}", ephemeral: true);
                return;
            }

            await guildUser.AddRoleAsync(provisionalRole);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to grant provisional role. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, guildUser.Id);
            await FollowupAsync("仮登録ロールの付与に失敗しました。Botの権限を確認してください。", ephemeral: true);
            return;
        }

        try
        {
            var embed = await BuildMemberRegistrationReviewEmbedAsync(guildUser, completedProfile);
            await reviewChannel.SendMessageAsync(
                embed: embed,
                components: BuildMemberRegistrationReviewComponents(guildUser.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit member registration review. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, guildUser.Id);
            try
            {
                if (guildUser.Roles.Any(role => role.Id == provisionalRole.Id))
                {
                    await guildUser.RemoveRoleAsync(provisionalRole);
                }
            }
            catch (Exception removeEx)
            {
                _logger.LogWarning(removeEx, "Failed to rollback provisional role. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, guildUser.Id);
            }

            await FollowupAsync("申請の送信に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        await LogMemberRegistrationRequestedAsync(completedProfile, guildUser);
        await FollowupAsync("登録申請を送信しました。管理者の許可後にメンバーロールが付与されます。", ephemeral: true);
        await ShowPostRegistrationVoiceNotificationOfferAsync();
    }

    private async Task HandlePostRegistrationVoiceNotificationOptionAsync(bool grantVoiceNotificationRole)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var status = await TryGrantVoiceNotificationRoleForRegistrationAsync(Context.Guild, guildUser, grantVoiceNotificationRole);
        if (!grantVoiceNotificationRole)
        {
            await FollowupAsync("通話通知ロールは付与しませんでした。必要なら後で設定パネルから変更できます。", ephemeral: true);
            return;
        }

        await FollowupAsync(status ?? "通話通知ロールを付与しました。", ephemeral: true);
    }

    private Task ShowPostRegistrationVoiceNotificationOfferAsync()
        => FollowupAsync(
            "オプション: 通話通知ロールを付与しますか？\nこのロールを持っていると、VC入室時に通知されます（後で設定パネルから変更可能）。",
            components: BuildMemberRegistrationSubmitOptionsComponents(),
            ephemeral: true);

    [RequireBotAdmin]
    [ComponentInteraction("member_registration_review_approve:*", ignoreGroupNames: true)]
    public Task ApproveMemberRegistrationAsync(string userIdToken)
        => HandleMemberRegistrationReviewAsync(userIdToken, approved: true);

    [RequireBotAdmin]
    [ComponentInteraction("member_registration_review_reject:*", ignoreGroupNames: true)]
    public Task RejectMemberRegistrationAsync(string userIdToken)
        => HandleMemberRegistrationReviewAsync(userIdToken, approved: false);

    private async Task HandleMemberRegistrationReviewAsync(string userIdToken, bool approved)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(userIdToken, out var targetUserId))
        {
            await RespondAsync("対象ユーザーを判定できませんでした。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var targetUser = Context.Guild.GetUser(targetUserId);
        if (targetUser is null)
        {
            await Context.Guild.DownloadUsersAsync();
            targetUser = Context.Guild.GetUser(targetUserId);
        }

        if (targetUser is null)
        {
            await FinalizeMemberRegistrationReviewMessageAsync(
                approved,
                $"対象ユーザー ({targetUserId}) がサーバー内で見つかりませんでした。",
                markError: true);
            await FollowupAsync("対象ユーザーが見つかりませんでした。", ephemeral: true);
            return;
        }

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var memberRole = ResolveMemberRole(Context.Guild, settings);
        var provisionalRole = ResolveProvisionalRole(Context.Guild, settings);
        var profile = await _db.GetDiscordUserProfileAsync(targetUser.Id);

        if (approved && memberRole is null)
        {
            await FollowupAsync("メンバーロールを取得できませんでした。", ephemeral: true);
            return;
        }

        try
        {
            if (provisionalRole is not null && targetUser.Roles.Any(role => role.Id == provisionalRole.Id))
            {
                if (!TryValidateRoleOperation(Context.Guild, targetUser, provisionalRole, out var reason))
                {
                    _logger.LogWarning(
                        "Skipped provisional role revoke due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                        Context.Guild.Id,
                        targetUser.Id,
                        provisionalRole.Id,
                        reason);
                    await FollowupAsync($"仮登録ロールを解除できません: {reason}", ephemeral: true);
                    return;
                }

                await targetUser.RemoveRoleAsync(provisionalRole);
            }

            if (approved && memberRole is not null && targetUser.Roles.All(role => role.Id != memberRole.Id))
            {
                if (!TryValidateRoleOperation(Context.Guild, targetUser, memberRole, out var reason))
                {
                    _logger.LogWarning(
                        "Skipped member role grant due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                        Context.Guild.Id,
                        targetUser.Id,
                        memberRole.Id,
                        reason);
                    await FollowupAsync($"メンバーロールを付与できません: {reason}", ephemeral: true);
                    return;
                }

                await targetUser.AddRoleAsync(memberRole);
            }

            if (approved && profile is not null)
            {
                await GrantDcRoleAsync(targetUser, profile.FfxivDataCenter);
                await UpdateNicknameAsync(targetUser, profile.FfxivName ?? string.Empty, profile.FfxivWorld ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process member registration review. GuildId={GuildId}, UserId={UserId}, Approved={Approved}", Context.Guild.Id, targetUser.Id, approved);
            await FollowupAsync("ロール付与処理に失敗しました。Botの権限を確認してください。", ephemeral: true);
            return;
        }

        if (approved)
        {
            await LogMemberRegistrationCompletedAsync(
                targetUser,
                profile?.FfxivName ?? "(unknown)",
                profile?.FfxivWorld ?? "(unknown)",
                profile?.FfxivDataCenter ?? "(unknown)",
                profile?.XHandle,
                profile?.BlueskyHandle);
            _ = _stats.RefreshIfConfiguredAsync(Context.Guild);
        }
        else
        {
            await LogMemberRegistrationRejectedAsync(targetUser);
        }

        var status = approved ? "許可" : "拒否";
        await FinalizeMemberRegistrationReviewMessageAsync(
            approved,
            $"対応: {status}\n対象: {FormatUser(targetUser)}\n対応者: {FormatUser(Context.User)}",
            markError: false);

        await FollowupAsync(
            approved ? $"登録申請を許可しました: {targetUser.Mention}" : $"登録申請を拒否しました: {targetUser.Mention}",
            ephemeral: true);
    }

    private static MessageComponent BuildMemberRegistrationStartComponents()
        => MemberRegistrationPanel.BuildComponents();

    private static Embed BuildCwlsEmbed(LodestoneCwlsProfile cwls)
    {
        var total = cwls.MemberCount ?? cwls.Members.Count;
        var roleSummary = BuildCwlsRoleSummary(cwls.Members);
        var memberLines = BuildCwlsMemberLines(cwls.Members, 30);

        var builder = new EmbedBuilder()
            .WithTitle($"CWLS: {cwls.Name}")
            .WithUrl(cwls.Url)
            .WithColor(Color.Blue)
            .AddField("DC", string.IsNullOrWhiteSpace(cwls.DataCenter) ? "不明" : cwls.DataCenter, true)
            .AddField("人数", $"{total}人", true)
            .AddField("結成日", cwls.FormedAt.HasValue ? $"<t:{cwls.FormedAt.Value.ToUnixTimeSeconds()}:D>" : "不明", true)
            .AddField("ランク内訳", roleSummary, false)
            .AddField("メンバー", memberLines, false)
            .WithFooter($"取得: {DateTimeOffset.Now:yyyy/MM/dd HH:mm:ss}");

        if (!string.IsNullOrWhiteSpace(cwls.IconUrl))
        {
            builder.WithThumbnailUrl(cwls.IconUrl);
        }

        return builder.Build();
    }

    private static string BuildCwlsRoleSummary(IReadOnlyList<LodestoneCwlsMember> members)
    {
        var groups = members
            .Where(member => !string.IsNullOrWhiteSpace(member.Rank))
            .GroupBy(member => member.Rank!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {group.Count()}");

        var summary = string.Join(" / ", groups);
        return string.IsNullOrWhiteSpace(summary) ? "なし" : summary;
    }

    private static string BuildCwlsMemberLines(IReadOnlyList<LodestoneCwlsMember> members, int maxLines)
    {
        if (members.Count == 0)
        {
            return "メンバーを取得できませんでした。";
        }

        var builder = new StringBuilder();
        var maxChars = 980;
        var lines = Math.Min(maxLines, members.Count);
        var appended = 0;

        for (var i = 0; i < lines; i++)
        {
            var member = members[i];
            var world = string.IsNullOrWhiteSpace(member.DataCenter)
                ? member.World
                : $"{member.World} [{member.DataCenter}]";
            var rankSuffix = string.IsNullOrWhiteSpace(member.Rank) ? string.Empty : $" ({member.Rank})";
            var line = $"・{member.Name} - {world}{rankSuffix}";
            var needed = line.Length + (appended > 0 ? Environment.NewLine.Length : 0);
            if (builder.Length + needed > maxChars)
            {
                break;
            }

            if (appended > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
            appended++;
        }

        if (members.Count > appended)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append($"... 他 {members.Count - appended} 名");
        }

        return builder.ToString();
    }

    private static MessageComponent BuildMemberRegistrationSubmitComponents()
        => new ComponentBuilder()
            .WithButton("登録申請を送信", MemberRegistrationSubmitCustomId, ButtonStyle.Primary)
            .Build();

    private static MessageComponent BuildMemberRegistrationSubmitConfirmComponents()
        => new ComponentBuilder()
            .WithButton("送信する", MemberRegistrationSubmitConfirmCustomId, ButtonStyle.Success, row: 0)
            .WithButton("中止", MemberRegistrationSubmitCancelCustomId, ButtonStyle.Secondary, row: 0)
            .Build();

    private static MessageComponent BuildMemberRegistrationSubmitOptionsComponents()
        => new ComponentBuilder()
            .WithButton("通話通知を付与", MemberRegistrationSubmitWithVoiceCustomId, ButtonStyle.Success, row: 0)
            .WithButton("付与しない", MemberRegistrationSubmitWithoutVoiceCustomId, ButtonStyle.Secondary, row: 0)
            .Build();

    private static MessageComponent BuildMemberRegistrationReviewComponents(ulong userId)
        => new ComponentBuilder()
            .WithButton("許可", $"{MemberRegistrationApprovePrefix}:{userId}", ButtonStyle.Success)
            .WithButton("拒否", $"{MemberRegistrationRejectPrefix}:{userId}", ButtonStyle.Danger)
            .Build();

    private static MessageComponent BuildDataCenterSelectComponents(string prefix)
    {
        var builder = new ComponentBuilder();
        var names = DcResolver.KnownDcNames;
        for (var i = 0; i < names.Count; i++)
        {
            var dcName = names[i];
            builder.WithButton(
                dcName,
                $"{prefix}:{ToDataCenterToken(dcName)}",
                ButtonStyle.Primary,
                row: i / 5);
        }

        return builder.Build();
    }

    private static MessageComponent BuildWorldSelectComponents(string prefix, string dataCenterName, IReadOnlyList<string> worlds)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            builder.WithButton(
                world,
                $"{prefix}:{ToDataCenterToken(dataCenterName)}:{ToWorldToken(world)}",
                ButtonStyle.Secondary,
                row: i / 5);
        }

        return builder.Build();
    }

    private static bool TryResolveDataCenterFromToken(string token, out string dataCenterName)
    {
        dataCenterName = DcResolver.KnownDcNames
            .FirstOrDefault(dc => string.Equals(ToDataCenterToken(dc), token, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(dataCenterName);
    }

    private static string ToDataCenterToken(string dataCenterName)
        => dataCenterName.Trim().ToLowerInvariant();

    private static string ToWorldToken(string worldName)
        => worldName.Trim().ToLowerInvariant();

    private async Task<IReadOnlyList<ContentRoleDefinition>> LoadMemberContentRoleDefinitionsAsync(ulong guildId)
    {
        var defaults = MemberContentRolePanel.DefaultDefinitions.ToList();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < defaults.Count; i++)
        {
            indexByKey[defaults[i].Key] = i;
        }

        var custom = await _db.ListMemberContentRoleSettingsAsync(guildId);
        foreach (var row in custom)
        {
            var definition = new ContentRoleDefinition(
                row.RoleKey,
                row.Label,
                row.RoleName,
                NormalizeButtonStyle(row.ButtonStyle),
                Math.Clamp(row.ButtonRow, 0, 4));

            if (indexByKey.TryGetValue(definition.Key, out var existingIndex))
            {
                defaults[existingIndex] = definition;
                continue;
            }

            indexByKey[definition.Key] = defaults.Count;
            defaults.Add(definition);
        }

        return defaults
            .OrderBy(x => x.Row)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
    }

    private static ButtonStyle NormalizeButtonStyle(int style)
    {
        return style switch
        {
            1 => ButtonStyle.Primary,
            2 => ButtonStyle.Secondary,
            3 => ButtonStyle.Success,
            4 => ButtonStyle.Danger,
            _ => ButtonStyle.Secondary
        };
    }

    private async Task RespondEphemeralAsync(string message, bool followup)
    {
        if (followup)
        {
            await FollowupAsync(message, ephemeral: true);
        }
        else
        {
            await RespondAsync(message, ephemeral: true);
        }
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

    private SocketTextChannel? ResolveRegistrationReviewChannel(SocketGuild guild, GuildSettings? settings)
    {
        if (settings is not null && settings.RegistrationReviewChannelId != 0)
        {
            var byId = guild.GetTextChannel(ToUlong(settings.RegistrationReviewChannelId));
            if (byId is not null)
            {
                return byId;
            }
        }

        var channelName = string.IsNullOrWhiteSpace(_options.RegistrationReviewChannelName)
            ? "登録確認"
            : _options.RegistrationReviewChannelName;
        return guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> TryGrantVoiceNotificationRoleForRegistrationAsync(
        SocketGuild guild,
        SocketGuildUser guildUser,
        bool grantVoiceNotificationRole)
    {
        if (!grantVoiceNotificationRole)
        {
            return null;
        }

        var roleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName)
            ? "通話通知"
            : _options.VoiceNotificationRoleName;
        IRole? role = guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));

        if (role is null && _options.AutoCreateVoiceNotificationRole)
        {
            try
            {
                role = await guild.CreateRoleAsync(
                    roleName,
                    GuildPermissions.None,
                    color: new Color(52, 152, 219),
                    isHoisted: false,
                    isMentionable: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create VC notification role on registration. GuildId={GuildId}", guild.Id);
                return "通話通知ロールを作成できなかったため、付与をスキップしました。";
            }
        }

        if (role is null)
        {
            return $"通話通知ロールが見つからないため、付与をスキップしました (`{roleName}`)。";
        }

        if (guildUser.Roles.Any(r => r.Id == role.Id))
        {
            return $"通話通知ロールは既に付与済みです (`{role.Name}`)。";
        }

        if (!TryValidateRoleOperation(guild, guildUser, role, out var reason))
        {
            _logger.LogWarning(
                "Skipped VC notification role grant due to permission/hierarchy. GuildId={GuildId}, UserId={UserId}, RoleId={RoleId}, Reason={Reason}",
                guild.Id,
                guildUser.Id,
                role.Id,
                reason);
            return $"通話通知ロールを付与できませんでした: {reason}";
        }

        try
        {
            await guildUser.AddRoleAsync(role);
            return $"通話通知ロールを付与しました (`{role.Name}`)。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to grant VC notification role on registration. GuildId={GuildId}, UserId={UserId}", guild.Id, guildUser.Id);
            return "通話通知ロールの付与に失敗しました。必要であれば設定パネルからON/OFFしてください。";
        }
    }

    private static bool HasCompletedRegistrationProfile(DiscordUserProfile? profile)
    {
        return profile is not null
               && !string.IsNullOrWhiteSpace(profile.FfxivName)
               && !string.IsNullOrWhiteSpace(profile.FfxivWorld)
               && !string.IsNullOrWhiteSpace(profile.FfxivDataCenter)
               && !string.IsNullOrWhiteSpace(profile.Bio);
    }

    private async Task<Embed> BuildMemberRegistrationPreviewEmbedAsync(SocketGuildUser guildUser, DiscordUserProfile profile)
        => await BuildMemberRegistrationSummaryEmbedAsync(
            guildUser,
            profile,
            title: "メンバー登録プレビュー",
            color: Color.Blue,
            descriptionPrefix: "送信前確認",
            includeDiscordDateFields: false);

    private async Task<Embed> BuildMemberRegistrationReviewEmbedAsync(SocketGuildUser guildUser, DiscordUserProfile profile)
        => await BuildMemberRegistrationSummaryEmbedAsync(
            guildUser,
            profile,
            title: "メンバー登録申請",
            color: Color.Orange,
            descriptionPrefix: "申請者",
            includeDiscordDateFields: true);

    private async Task<Embed> BuildMemberRegistrationSummaryEmbedAsync(
        SocketGuildUser guildUser,
        DiscordUserProfile profile,
        string title,
        Color color,
        string descriptionPrefix,
        bool includeDiscordDateFields)
    {
        var guild = Context.Guild!;
        var roleDefinitions = await LoadMemberContentRoleDefinitionsAsync(guild.Id);
        var selectedRoleText = BuildSelectedContentRoleText(guildUser, roleDefinitions);

        var ffxivName = string.IsNullOrWhiteSpace(profile.FfxivName) ? "(未設定)" : profile.FfxivName;
        var ffxivWorld = string.IsNullOrWhiteSpace(profile.FfxivWorld) ? "(未設定)" : profile.FfxivWorld;
        var dataCenter = string.IsNullOrWhiteSpace(profile.FfxivDataCenter) ? "(未設定)" : profile.FfxivDataCenter;
        var xText = string.IsNullOrWhiteSpace(profile.XHandle) ? "(なし)" : profile.XHandle;
        var blueskyText = string.IsNullOrWhiteSpace(profile.BlueskyHandle) ? "(なし)" : profile.BlueskyHandle;
        var bio = string.IsNullOrWhiteSpace(profile.Bio) ? "(なし)" : TruncateForLog(profile.Bio, 900);
        var displayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? guildUser.DisplayName : profile.DisplayName;
        var discordName = string.IsNullOrWhiteSpace(profile.DiscordName) ? guildUser.Username : profile.DiscordName;
        var discordLines = new List<string>
        {
            $"表示名: {TruncateForLog(displayName, 120)}",
            $"ユーザー名: {TruncateForLog(discordName, 120)}"
        };
        if (includeDiscordDateFields)
        {
            var discordCreatedAt = $"<t:{guildUser.CreatedAt.ToUnixTimeSeconds()}:F>";
            var joinedAt = guildUser.JoinedAt.HasValue ? $"<t:{guildUser.JoinedAt.Value.ToUnixTimeSeconds()}:F>" : "(不明)";
            discordLines.Add($"Discord開始日: {discordCreatedAt}");
            discordLines.Add($"サーバー参加日: {joinedAt}");
        }

        var avatar = guildUser.GetAvatarUrl() ?? guildUser.GetDisplayAvatarUrl();
        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp()
            .WithDescription($"{descriptionPrefix}: {guildUser.Mention}\nUserId: `{guildUser.Id}`")
            .AddField("Discord", string.Join('\n', discordLines), false)
            .AddField("FFXIV", $"{ffxivName}@{ffxivWorld} [{dataCenter}]", false)
            .AddField("SNS", $"𝕏: {TruncateForLog(xText, 120)}\nBluesky: {TruncateForLog(blueskyText, 120)}", false)
            .AddField("一言", bio, false)
            .AddField("コンテンツロール", selectedRoleText, false);

        if (!string.IsNullOrWhiteSpace(avatar))
        {
            embed.WithThumbnailUrl(avatar);
        }

        return embed.Build();
    }

    private static string BuildSelectedContentRoleText(SocketGuildUser guildUser, IReadOnlyList<ContentRoleDefinition> roleDefinitions)
    {
        var selectedLabels = roleDefinitions
            .Where(definition => guildUser.Roles.Any(role =>
                string.Equals(role.Name, definition.RoleName, StringComparison.OrdinalIgnoreCase)))
            .Select(definition => definition.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedLabels.Count == 0)
        {
            return "（未選択）";
        }

        var text = string.Join('\n', selectedLabels.Select(label => $"・{label}"));
        return TruncateForLog(text, 900);
    }

    private async Task FinalizeMemberRegistrationReviewMessageAsync(bool approved, string summary, bool markError)
    {
        if (Context.Interaction is not SocketMessageComponent component || component.Message is not IUserMessage message)
        {
            return;
        }

        var state = markError ? "処理失敗" : approved ? "承認済み" : "拒否済み";
        var content = $"[{state}]\n{summary}";

        try
        {
            await message.ModifyAsync(props =>
            {
                props.Content = content;
                props.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to finalize member registration review message. GuildId={GuildId}", Context.Guild?.Id);
        }
    }

    private async Task GrantDcRoleAsync(SocketGuildUser guildUser, string? dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter) || Context.Guild is null)
        {
            return;
        }

        var dcName = dataCenter.Trim();
        var dcRoles = DcResolver.KnownDcNames;
        if (!dcRoles.Any(role => string.Equals(role, dcName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IRole? existingRole = Context.Guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, dcName, StringComparison.OrdinalIgnoreCase));

        if (existingRole is null && _options.AutoCreateDcRoles)
        {
            existingRole = await Context.Guild.CreateRoleAsync(dcName, GuildPermissions.None, color: new Color(99, 110, 114), isHoisted: false, isMentionable: false);
        }

        if (existingRole is null)
        {
            return;
        }

        var removeRoles = guildUser.Roles
            .Where(role => dcRoles.Any(dcRole => string.Equals(dcRole, role.Name, StringComparison.OrdinalIgnoreCase)) && role.Id != existingRole.Id)
            .ToList();

        if (removeRoles.Count > 0)
        {
            await guildUser.RemoveRolesAsync(removeRoles);
        }

        if (!guildUser.Roles.Any(role => role.Id == existingRole.Id))
        {
            await guildUser.AddRoleAsync(existingRole);
        }
    }

    private async Task UpdateNicknameAsync(SocketGuildUser guildUser, string characterName, string world)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var target = $"{characterName}@{world}".Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var maxLength = 32;
        var newNickname = target.Length > maxLength ? target[..maxLength] : target;

        var current = guildUser.Nickname ?? guildUser.Username;
        if (string.Equals(current, newNickname, StringComparison.Ordinal))
        {
            return;
        }

        var botUser = Context.Guild.CurrentUser;
        if (botUser is null)
        {
            return;
        }

        if (!botUser.GuildPermissions.ManageNicknames)
        {
            _logger.LogInformation(
                "Skipped nickname update: bot lacks ManageNicknames. GuildId={GuildId}, UserId={UserId}",
                Context.Guild.Id,
                guildUser.Id);
            return;
        }

        if (guildUser.Id == Context.Guild.OwnerId)
        {
            _logger.LogInformation(
                "Skipped nickname update: target is guild owner. GuildId={GuildId}, UserId={UserId}",
                Context.Guild.Id,
                guildUser.Id);
            return;
        }

        if (guildUser.Id != botUser.Id && guildUser.Hierarchy >= botUser.Hierarchy)
        {
            _logger.LogInformation(
                "Skipped nickname update: target role hierarchy is too high. GuildId={GuildId}, UserId={UserId}, TargetHierarchy={TargetHierarchy}, BotHierarchy={BotHierarchy}",
                Context.Guild.Id,
                guildUser.Id,
                guildUser.Hierarchy,
                botUser.Hierarchy);
            return;
        }

        try
        {
            await guildUser.ModifyAsync(props => props.Nickname = newNickname);
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode == DiscordErrorCode.MissingPermissions)
        {
            _logger.LogInformation(
                "Skipped nickname update due to missing permissions. GuildId={GuildId}, UserId={UserId}",
                Context.Guild.Id,
                guildUser.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update nickname for user {UserId}.", guildUser.Id);
        }
    }

    private async Task LogCharacterLinkedAsync(string characterName, string world, string? dataCenter, string lodestoneUrl, string? portraitUrl)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var dcText = string.IsNullOrWhiteSpace(dataCenter) ? "(unknown)" : dataCenter;
        var description = BuildCharacterLinkedSummary(Context.User, characterName, world, dcText, lodestoneUrl);
        if (!string.IsNullOrWhiteSpace(portraitUrl))
        {
            description = $"{description}\n🖼️ Portrait: {portraitUrl}";
        }

        await _audit.SendAsync(Context.Guild, "Lodestone連携完了", description, Color.Blue, Context.User);
    }

    private async Task LogMemberRegistrationRequestedAsync(DiscordUserProfile profile, SocketGuildUser applicant)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var description = string.Join('\n', new[]
        {
            $"👤 申請者: {FormatUser(applicant)}",
            $"🧩 FFXIV名: {profile.FfxivName}",
            $"🌐 ワールド: {profile.FfxivWorld} [{profile.FfxivDataCenter}]"
        });

        await _audit.SendAsync(Context.Guild, "メンバー登録申請", description, Color.Orange, applicant);
    }

    private async Task LogMemberRegistrationCompletedAsync(IUser targetUser, string ffxivName, string world, string dataCenter, string? xHandle, string? blueskyHandle)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var xText = string.IsNullOrWhiteSpace(xHandle) ? "(none)" : xHandle;
        var blueskyText = string.IsNullOrWhiteSpace(blueskyHandle) ? "(none)" : blueskyHandle;
        var description = string.Join('\n', new[]
        {
            $"👤 対象ユーザー: {FormatUser(targetUser)}",
            $"✅ 対応者: {FormatUser(Context.User)}",
            $"🧩 FFXIV名: {ffxivName}",
            $"🌐 ワールド: {world} [{dataCenter}]",
            $"𝕏: {xText}",
            $"🦋 Bluesky: {blueskyText}"
        });

        await _audit.SendAsync(Context.Guild, "メンバー登録完了", description, Color.Green, targetUser);
    }

    private async Task LogMemberRegistrationRejectedAsync(IUser targetUser)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var description = string.Join('\n', new[]
        {
            $"👤 対象ユーザー: {FormatUser(targetUser)}",
            $"⛔ 対応者: {FormatUser(Context.User)}"
        });

        await _audit.SendAsync(Context.Guild, "メンバー登録拒否", description, Color.DarkRed, targetUser);
    }

    private Task LogRateLimitAsync(string title, int waitSeconds)
        => LogInvalidOperationAsync(
            title,
            $"👤 ユーザー: {FormatUser(Context.User)}\n⏳ RetryAfter: {waitSeconds}秒");

    private async Task LogInvalidOperationAsync(string title, string description)
    {
        if (Context.Guild is null)
        {
            return;
        }

        await _audit.SendAsync(Context.Guild, title, description, Color.Orange, Context.User);
    }

    private async Task LogFailureAsync(string title, string description)
    {
        if (Context.Guild is null)
        {
            return;
        }

        await _audit.SendAsync(Context.Guild, title, description, Color.Red, Context.User);
    }

    private async Task<IUserMessage> EnsureLodestonePanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
    {
        return await EnsurePanelMessageInternalAsync(
            channel,
            existingMessageId,
            LodestonePanel.LooksLikePanelMessage,
            LodestonePanel.PanelMessage,
            LodestonePanel.BuildComponents());
    }

    private async Task<IUserMessage> EnsurePanelMessageInternalAsync(
        IMessageChannel channel,
        ulong? existingMessageId,
        Func<string, bool> messageMatcher,
        string messageText,
        MessageComponent components)
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
                _logger.LogWarning(ex, "Failed to fetch existing panel message.");
            }
        }

        if (existing is null)
        {
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    messageMatcher(message.Content));
        }

        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = messageText;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(messageText, components: components, flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);

    private static bool IsSpacedCharacterName(string value)
    {
        var parts = value.Split([' ', '\t', '\u3000'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2;
    }

    private async Task<IMessageChannel?> ResolveSetupChannelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return null;
        }

        if (Context.Channel is not IMessageChannel messageChannel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return null;
        }

        return messageChannel;
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

    private static string BuildCharacterLinkedSummary(IUser user, string characterName, string world, string dataCenter, string lodestoneUrl)
    {
        var lines = new List<string>
        {
            $"👤 ユーザー: {user.Mention} ({user})",
            $"🆔 UserId: {user.Id}",
            $"🧩 Character: {characterName}@{world} [{dataCenter}]",
            $"🔗 Lodestone: {lodestoneUrl}"
        };

        return string.Join('\n', lines);
    }

    private static string FormatUser(IUser user)
    {
        var display = user.GlobalName ?? user.Username;
        return $"{user.Mention} ({display})";
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
