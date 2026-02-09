using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("lodestone", "Lodestone連携")]
public sealed class LodestoneModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string MemberRegistrationDcPrefix = "member_registration_dc";
    private const string MemberRegistrationWorldPrefix = "member_registration_world";
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

        await FollowupAsync("DCを選択してください。", components: BuildDataCenterSelectComponents(), ephemeral: true);
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

        await RespondAsync($"{dataCenterName} のサーバーを選択してください。", components: BuildWorldSelectComponents(dataCenterName, worlds), ephemeral: true);
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

        await GrantMemberRoleAsync();
        await GrantDcRoleAsync(dataCenterName);
        await UpdateNicknameAsync(draft.FfxivName, worldName);
        await LogMemberRegistrationCompletedAsync(draft.FfxivName, worldName, dataCenterName, draft.XHandle, draft.BlueskyHandle);
        _ = _stats.RefreshIfConfiguredAsync(Context.Guild!);

        var contentRoleDefinitions = await LoadMemberContentRoleDefinitionsAsync(Context.Guild!.Id);
        var contentRoleComponents = MemberContentRolePanel.BuildComponents(contentRoleDefinitions);
        var completedMessage =
            $"メンバー登録を完了しました: {draft.FfxivName}@{worldName} [{dataCenterName}]\n" +
            "次に、プレイしているコンテンツのロールを選択してください（必要に応じて後から変更できます）。";
        await RespondAsync(completedMessage, components: contentRoleComponents, ephemeral: true);
    }

    private static MessageComponent BuildMemberRegistrationStartComponents()
        => MemberRegistrationPanel.BuildComponents();

    private static MessageComponent BuildDataCenterSelectComponents()
    {
        var builder = new ComponentBuilder();
        var names = DcResolver.KnownDcNames;
        for (var i = 0; i < names.Count; i++)
        {
            var dcName = names[i];
            builder.WithButton(
                dcName,
                $"{MemberRegistrationDcPrefix}:{ToDataCenterToken(dcName)}",
                ButtonStyle.Primary,
                row: i / 5);
        }

        return builder.Build();
    }

    private static MessageComponent BuildWorldSelectComponents(string dataCenterName, IReadOnlyList<string> worlds)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            builder.WithButton(
                world,
                $"{MemberRegistrationWorldPrefix}:{ToDataCenterToken(dataCenterName)}:{ToWorldToken(world)}",
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

    private async Task GrantMemberRoleAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            return;
        }

        IRole? memberRole = null;
        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        if (settings is not null && settings.MemberRoleId != 0)
        {
            memberRole = Context.Guild.GetRole(ToUlong(settings.MemberRoleId));
        }

        memberRole ??= Context.Guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));

        if (memberRole is null)
        {
            return;
        }

        if (guildUser.Roles.Any(role => role.Id == memberRole.Id))
        {
            return;
        }

        await guildUser.AddRoleAsync(memberRole);
    }

    private async Task GrantDcRoleAsync(string? dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            return;
        }

        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
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

    private async Task UpdateNicknameAsync(string characterName, string world)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
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

    private async Task LogMemberRegistrationCompletedAsync(string ffxivName, string world, string dataCenter, string? xHandle, string? blueskyHandle)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var xText = string.IsNullOrWhiteSpace(xHandle) ? "(none)" : xHandle;
        var blueskyText = string.IsNullOrWhiteSpace(blueskyHandle) ? "(none)" : blueskyHandle;
        var description = string.Join('\n', new[]
        {
            $"👤 ユーザー: {FormatUser(Context.User)}",
            $"🧩 FFXIV名: {ffxivName}",
            $"🌐 ワールド: {world} [{dataCenter}]",
            $"𝕏: {xText}",
            $"🦋 Bluesky: {blueskyText}"
        });

        await _audit.SendAsync(Context.Guild, "メンバー登録完了", description, Color.Green, Context.User);
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
