using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("settings", "設定")]
public sealed class SettingsPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string SsSourceChannelName = "ss";
    private const string InquiryOpenButtonCustomId = "inquiry_open";
    private const string VoiceTextAutoDeleteSetCustomIdPrefix = "voice_text_autodelete_set:";
    private const string SelfStorySelectCustomIdPrefix = "settings_self_story_select:";

    private static readonly IReadOnlyDictionary<string, string> StoryProgressionOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["arr_hw"] = "新生〜蒼天",
        ["sb"] = "紅蓮",
        ["shb"] = "漆黒",
        ["ew"] = "暁月",
        ["dt"] = "黄金",
        ["completed"] = "最新パッチ済み"
    };

    private readonly Db _db;
    private readonly RecruitmentListService _recruitListService;
    private readonly UserProfileEmbedService _profileEmbeds;
    private readonly GuildAuditLogService _audit;
    private readonly ILogger<SettingsPanelModule> _logger;
    private readonly DiscordOptions _options;

    public SettingsPanelModule(
        Db db,
        RecruitmentListService recruitListService,
        UserProfileEmbedService profileEmbeds,
        GuildAuditLogService audit,
        ILogger<SettingsPanelModule> logger,
        IOptions<DiscordOptions> options)
    {
        _db = db;
        _recruitListService = recruitListService;
        _profileEmbeds = profileEmbeds;
        _audit = audit;
        _logger = logger;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルに設定パネルを設置します")]
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
        var message = await EnsureSettingsPanelMessageAsync(channel);
        await _audit.SendAsync(Context.Guild, "設定パネル設置", $"<#{Context.Channel.Id}> に設定パネルを設置しました。", Color.Blue, Context.User);
        await FollowupAsync($"このチャンネルに設定パネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [ComponentInteraction(SettingsPanel.ToggleSelfVoiceNotifyCustomId, ignoreGroupNames: true)]
    public async Task ToggleSelfVoiceNotifyAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        _logger.LogInformation("Toggle self voice notify. UserId={UserId}, GuildId={GuildId}", Context.User.Id, Context.Guild.Id);

        try
        {
            var enabled = await _db.ToggleVoiceNotificationEnabledAsync(Context.User.Id);
            _logger.LogInformation("Self voice notify toggled. UserId={UserId}, Enabled={Enabled}", Context.User.Id, enabled);
            await _audit.SendAsync(Context.Guild, "自己設定変更", $"VC通知を **{(enabled ? "ON" : "OFF")}** に変更しました。", Color.Green, Context.User);
            var embed = await BuildSelfSettingsEmbedAsync(guildUser, "設定を更新しました", enabled);
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle self VC notification setting. UserId={UserId}", Context.User.Id);
            await FollowupAsync("設定の更新に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(SettingsPanel.ConfigureSelfVoiceTextAutoDeleteCustomId, ignoreGroupNames: true)]
    public async Task ShowSelfVoiceTextAutoDeleteConfigAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        try
        {
            var current = await _db.GetVoiceTextAutoDeletePolicyAsync(Context.User.Id);
            await RespondAsync(
                embed: BuildVoiceTextAutoDeleteConfigEmbed(guildUser, current, null),
                components: BuildVoiceTextAutoDeleteConfigComponents(current),
                ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show VC text auto-delete config. UserId={UserId}", Context.User.Id);
            await RespondAsync("VC内テキスト自動削除設定の表示に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(VoiceTextAutoDeleteSetCustomIdPrefix + "*", ignoreGroupNames: true)]
    public async Task SetSelfVoiceTextAutoDeleteAsync(string modeRaw)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!TryParseVoiceTextAutoDeleteMode(modeRaw, out var enabled, out var retentionHours))
        {
            await RespondAsync("設定値が不正です。", ephemeral: true);
            return;
        }

        try
        {
            var updated = await _db.SetVoiceTextAutoDeletePolicyAsync(Context.User.Id, enabled, retentionHours);
            var notice = updated.Enabled
                ? $"VC内テキスト自動削除を ON ({Math.Clamp(updated.RetentionHours <= 0 ? 6 : updated.RetentionHours, 1, 168)}時間) に設定しました。"
                : "VC内テキスト自動削除を OFF に設定しました。";
            await _audit.SendAsync(Context.Guild, "自己設定変更", notice, Color.Green, Context.User);
            var embed = BuildVoiceTextAutoDeleteConfigEmbed(guildUser, updated, notice);
            var components = BuildVoiceTextAutoDeleteConfigComponents(updated);

            if (Context.Interaction is SocketMessageComponent component)
            {
                await component.UpdateAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                    props.Components = components;
                });
                return;
            }

            await RespondAsync(embed: embed, components: components, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set VC text auto-delete setting. UserId={UserId}", Context.User.Id);
            await RespondAsync("VC内テキスト自動削除の更新に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(SettingsPanel.ShowMyPageCustomId, ignoreGroupNames: true)]
    public async Task ShowMyPageAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        _logger.LogInformation("Show my page requested. UserId={UserId}, GuildId={GuildId}", Context.User.Id, Context.Guild.Id);

        try
        {
            var embed = await BuildMyPageEmbedAsync(guildUser);
            await _audit.SendAsync(Context.Guild, "マイページ表示", "マイページを表示しました。", Color.Blue, Context.User);
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show my page. UserId={UserId}", Context.User.Id);
            await FollowupAsync("マイページの表示に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(SettingsPanel.ShowAchievementResumeCustomId, ignoreGroupNames: true)]
    public async Task ShowAchievementResumeAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        _logger.LogInformation("Show achievement resume requested. UserId={UserId}, GuildId={GuildId}", Context.User.Id, Context.Guild.Id);
        await _audit.SendAsync(Context.Guild, "レイド履歴書表示", "レイド履歴書の表示をリクエストしました。", Color.Purple, Context.User);
        var components = AchievementResumeModule.BuildSavageFilterComponents(Context.User.Id);
        await RespondAsync(
            "表示したい零式カテゴリを選択してください。選択後に履歴書を生成します。",
            components: components,
            ephemeral: true);
    }

    [ComponentInteraction(SettingsPanel.ShowSelfProfileCustomId, ignoreGroupNames: true)]
    public async Task ShowSelfProfileAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        _logger.LogInformation("Show self profile requested. UserId={UserId}, GuildId={GuildId}", Context.User.Id, Context.Guild.Id);

        try
        {
            var embed = await _profileEmbeds.BuildProfileEmbedAsync(
                Context.User,
                Context.Guild,
                "自分の情報",
                Color.Blue,
                includeLodestoneExtras: true);
            await _audit.SendAsync(Context.Guild, "自己プロフィール表示", "自己プロフィールを表示しました。", Color.Blue, Context.User);
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show self profile from settings panel. UserId={UserId}", Context.User.Id);
            await FollowupAsync("自分の情報の表示に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(SettingsPanel.ChangeSelfStoryProgressionCustomId, ignoreGroupNames: true)]
    public async Task ShowSelfStoryProgressionSelectAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var profile = await _db.GetDiscordUserProfileAsync(Context.User.Id);
        var current = FormatStoryProgression(profile?.StoryProgression);

        var components = ProfileUI.BuildStoryProgressionSelect(key => $"{SelfStorySelectCustomIdPrefix}{Context.User.Id}:{key}");

        await RespondAsync(
            $"あなたのストーリー進行度を選択してください。\n（現在: {current}）",
            components: components,
            ephemeral: true);
    }

    [ComponentInteraction(SelfStorySelectCustomIdPrefix + "*:*", ignoreGroupNames: true)]
    public async Task HandleSelfStoryProgressionSelectAsync(string userIdRaw, string storyKey)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(userIdRaw, out var targetUserId) || targetUserId != Context.User.Id)
        {
            await RespondAsync("ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(storyKey))
        {
            await RespondAsync("選択が不正です。", ephemeral: true);
            return;
        }

        var storyProgression = StoryProgressionOptions.ContainsKey(storyKey) ? storyKey : null;
        var label = FormatStoryProgression(storyProgression);

        try
        {
            await _db.UpdateUserStoryProgressionAsync(targetUserId, storyProgression);
        }
        catch
        {
            await RespondAsync("ストーリー進行度の更新に失敗しました。", ephemeral: true);
            return;
        }

        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(props =>
            {
                props.Content = $"ストーリー進行度を **{label}** に更新しました。";
                props.Components = new ComponentBuilder().Build();
                props.Embed = null;
            });
        }

        if (Context.User is SocketGuildUser guildUser)
        {
            await GrantStoryProgressionRoleAsync(guildUser, storyProgression);
        }

        await _audit.SendAsync(Context.Guild, "自己設定変更", $"ストーリー進行度を {label} に変更しました。", Color.Green, Context.User);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ResyncPanelsCustomId, ignoreGroupNames: true)]
    public async Task ResyncPanelsAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel fallbackChannel)
        {
            await RespondAsync("このチャンネルでは再同期できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var updated = 0;
        var targets = 0;
        var details = new List<string>();

        targets++;
        var settingsPanelCount = await RefreshSettingsPanelsByScanAsync(Context.Guild);
        if (settingsPanelCount > 0)
        {
            updated++;
            details.Add($"- 設定パネル: {settingsPanelCount}件更新");
        }
        else
        {
            details.Add("- 設定パネル: 対象なし");
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildPanelAsync(Context.Guild.Id),
                fallbackChannel,
                MemberRegistrationPanel.PanelMessage,
                MemberRegistrationPanel.BuildComponents()))
        {
            updated++;
            details.Add("- メンバー登録パネル: 更新");
        }
        else
        {
            details.Add("- メンバー登録パネル: 対象なし/失敗");
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildLodestonePanelAsync(Context.Guild.Id),
                fallbackChannel,
                LodestonePanel.PanelMessage,
                LodestonePanel.BuildComponents()))
        {
            updated++;
            details.Add("- Lodestoneパネル: 更新");
        }
        else
        {
            details.Add("- Lodestoneパネル: 対象なし/失敗");
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildControlPanelAsync(Context.Guild.Id),
                fallbackChannel,
                null,
                ControlPanel.BuildComponents()))
        {
            updated++;
            details.Add("- コントロールパネル: 更新");
        }
        else
        {
            details.Add("- コントロールパネル: 対象なし/失敗");
        }

        targets++;
        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var registrationApprovalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? true;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id),
                fallbackChannel,
                null,
                AdminControlPanel.BuildComponents(registrationApprovalRequired, inactiveAutoKickEnabled),
                BuildAdminPanelStatusEmbed(registrationApprovalRequired, inactiveAutoKickEnabled)))
        {
            updated++;
            details.Add("- ADMINパネル: 更新");
        }
        else
        {
            details.Add("- ADMINパネル: 対象なし/失敗");
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildRecruitPanelAsync(Context.Guild.Id),
                fallbackChannel,
                RecruitmentPanel.PanelMessage,
                RecruitmentPanel.BuildPanelComponents()))
        {
            updated++;
            details.Add("- 募集パネル: 更新");
        }
        else
        {
            details.Add("- 募集パネル: 対象なし/失敗");
        }

        targets++;
        if (await RefreshRecruitListMessageAsync(Context.Guild, fallbackChannel))
        {
            updated++;
            details.Add("- 募集一覧パネル: 更新");
        }
        else
        {
            details.Add("- 募集一覧パネル: 対象なし/失敗");
        }

        targets++;
        var voiceNotifyPanelCount = await RefreshVoiceNotificationPanelsByScanAsync(Context.Guild);
        if (voiceNotifyPanelCount > 0)
        {
            updated++;
            details.Add($"- VC通知ロールパネル: {voiceNotifyPanelCount}件更新");
        }
        else
        {
            details.Add("- VC通知ロールパネル: 対象なし");
        }

        targets++;
        var contentRolePanelCount = await RefreshContentRolePanelsByScanAsync(Context.Guild);
        if (contentRolePanelCount > 0)
        {
            updated++;
            details.Add($"- コンテンツロールパネル: {contentRolePanelCount}件更新");
        }
        else
        {
            details.Add("- コンテンツロールパネル: 対象なし");
        }

        targets++;
        var ssAlbumPanelCount = await RefreshSsAlbumPanelsByScanAsync(Context.Guild);
        if (ssAlbumPanelCount > 0)
        {
            updated++;
            details.Add($"- SSアルバムパネル: {ssAlbumPanelCount}件更新");
        }
        else
        {
            details.Add("- SSアルバムパネル: 対象なし");
        }

        targets++;
        var inquiryPanelCount = await RefreshInquiryPanelsByScanAsync(Context.Guild);
        if (inquiryPanelCount > 0)
        {
            updated++;
            details.Add($"- 問い合わせパネル: {inquiryPanelCount}件更新");
        }
        else
        {
            details.Add("- 問い合わせパネル: 対象なし");
        }

        await FollowupAsync(
            $"パネル再同期完了: {updated}/{targets} ターゲットを更新しました。\n{string.Join('\n', details)}",
            ephemeral: true);
    }

    private async Task<IUserMessage> EnsureSettingsPanelMessageAsync(IMessageChannel channel)
    {
        var recent = await channel.GetMessagesAsync(100).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                SettingsPanel.LooksLikePanelMessage(message.Content));

        var embed = SettingsPanel.BuildEmbed();
        var components = SettingsPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = SettingsPanel.PanelMessage;
                props.Embed = embed;
                props.Components = components;
            });
            return existing;
        }

        try
        {
            return await channel.SendMessageAsync(
                SettingsPanel.PanelMessage,
                embed: embed,
                components: components,
                flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send settings panel message.");
            throw;
        }
    }

    private async Task<int> RefreshSettingsPanelsByScanAsync(SocketGuild guild)
    {
        return await RefreshPanelsByScanAsync(
            guild,
            match: message => SettingsPanel.LooksLikePanelMessage(message.Content) || message.Embeds.Any(e => e.Title?.Contains("設定パネル") == true),
            update: message => message.ModifyAsync(props =>
            {
                props.Content = SettingsPanel.PanelMessage;
                props.Embed = SettingsPanel.BuildEmbed();
                props.Components = SettingsPanel.BuildComponents();
            }),
            panelName: "settings");
    }

    private async Task<int> RefreshVoiceNotificationPanelsByScanAsync(SocketGuild guild)
    {
        return await RefreshPanelsByScanAsync(
            guild,
            match: message => VoiceNotificationRolePanel.LooksLikePanelMessage(message.Content),
            update: message => message.ModifyAsync(props =>
            {
                props.Content = VoiceNotificationRolePanel.PanelMessage;
                props.Components = VoiceNotificationRolePanel.BuildComponents();
            }),
            panelName: "voice-notification");
    }

    private async Task<int> RefreshContentRolePanelsByScanAsync(SocketGuild guild)
    {
        var definitions = await LoadContentRoleDefinitionsAsync(guild.Id);
        var components = MemberContentRolePanel.BuildComponents(definitions);
        return await RefreshPanelsByScanAsync(
            guild,
            match: message =>
                MemberContentRolePanel.LooksLikePanelMessage(message.Content) ||
                MemberContentRolePanel.LooksLikePanelComponents(message.Components),
            update: message => message.ModifyAsync(props =>
            {
                props.Content = null;
                props.Components = components;
            }),
            panelName: "content-role");
    }

    private async Task<int> RefreshSsAlbumPanelsByScanAsync(SocketGuild guild)
    {
        var sourceChannel = guild.TextChannels
            .Where(ch => string.Equals(ch.Name, SsSourceChannelName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ch => ch.Position)
            .FirstOrDefault();
        var panelMessage = SsAlbumPanel.BuildPanelMessage(sourceChannel?.Id);
        var components = SsAlbumPanel.BuildComponents();
        return await RefreshPanelsByScanAsync(
            guild,
            match: message => SsAlbumPanel.LooksLikePanelMessage(message.Content),
            update: message => message.ModifyAsync(props =>
            {
                props.Content = panelMessage;
                props.Components = components;
            }),
            panelName: "ss-album");
    }

    private async Task<int> RefreshInquiryPanelsByScanAsync(SocketGuild guild)
    {
        var components = new ComponentBuilder()
            .WithButton("お問い合わせ", InquiryOpenButtonCustomId, ButtonStyle.Primary)
            .Build();
        return await RefreshPanelsByScanAsync(
            guild,
            match: HasInquiryOpenButton,
            update: message => message.ModifyAsync(props =>
            {
                props.Content = string.Empty;
                props.Components = components;
            }),
            panelName: "inquiry");
    }

    private async Task<int> RefreshPanelsByScanAsync(
        SocketGuild guild,
        Func<IUserMessage, bool> match,
        Func<IUserMessage, Task> update,
        string panelName)
    {
        var updated = 0;
        foreach (var channel in guild.TextChannels.OrderBy(ch => ch.Position))
        {
            IEnumerable<IMessage> recent;
            try
            {
                recent = await channel.GetMessagesAsync(100).FlattenAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skip panel scan channel. GuildId={GuildId}, ChannelId={ChannelId}, Panel={PanelName}", guild.Id, channel.Id, panelName);
                continue;
            }

            foreach (var message in recent.OfType<IUserMessage>())
            {
                if (message.Author.Id != Context.Client.CurrentUser.Id)
                {
                    continue;
                }

                if (!match(message))
                {
                    continue;
                }

                try
                {
                    await update(message);
                    updated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh scanned panel message. GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}, Panel={PanelName}",
                        guild.Id, channel.Id, message.Id, panelName);
                }
            }
        }

        return updated;
    }

    private static bool HasInquiryOpenButton(IUserMessage message)
    {
        return message.Components
            .OfType<ActionRowComponent>()
            .SelectMany(row => row.Components)
            .OfType<ButtonComponent>()
            .Any(button => string.Equals(button.CustomId, InquiryOpenButtonCustomId, StringComparison.Ordinal));
    }

    private async Task<bool> RefreshRecruitListMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildRecruitListPanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return false;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return false;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return false;
            }

            await _recruitListService.RefreshAsync(guild, fallbackChannel);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recruit list panel message.");
            return false;
        }
    }

    private async Task<bool> RefreshPanelMessageAsync(
        SocketGuild guild,
        GuildPanel? panel,
        IMessageChannel fallbackChannel,
        string? messageText,
        MessageComponent components,
        Embed? embed = null)
    {
        if (panel is null || panel.MessageId == 0)
        {
            return false;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return false;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return false;
            }

            await message.ModifyAsync(props =>
            {
                props.Content = messageText;
                if (embed is not null)
                {
                    props.Embed = embed;
                }
                props.Components = components;
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh panel message. GuildId={GuildId}, MessageId={MessageId}", guild.Id, panel.MessageId);
            return false;
        }
    }

    private async Task<IReadOnlyList<ContentRoleDefinition>> LoadContentRoleDefinitionsAsync(ulong guildId)
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

    private static Embed BuildVoiceTextAutoDeleteConfigEmbed(SocketGuildUser guildUser, VoiceTextAutoDeletePolicy policy, string? notice)
    {
        var normalizedHours = Math.Clamp(policy.RetentionHours <= 0 ? 6 : policy.RetentionHours, 1, 168);
        var current = policy.Enabled ? $"ON ({normalizedHours}時間)" : "OFF";

        var embed = new EmbedBuilder()
            .WithTitle("VC内テキスト自動削除設定")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .WithAuthor(guildUser.DisplayName, guildUser.GetAvatarUrl() ?? guildUser.GetDefaultAvatarUrl())
            .AddField("現在の設定", current, true)
            .AddField("対象", "VC内テキストチャットの発言内容", true)
            .AddField("設定方法", "下のボタンから OFF / 6h / 12h / 24h を直接選択してください。", false);

        if (!string.IsNullOrWhiteSpace(notice))
        {
            embed.AddField("実行結果", notice, false);
        }

        return embed.Build();
    }

    private static MessageComponent BuildVoiceTextAutoDeleteConfigComponents(VoiceTextAutoDeletePolicy policy)
    {
        var normalizedHours = Math.Clamp(policy.RetentionHours <= 0 ? 6 : policy.RetentionHours, 1, 168);
        var offSelected = !policy.Enabled;
        var h6Selected = policy.Enabled && normalizedHours == 6;
        var h12Selected = policy.Enabled && normalizedHours == 12;
        var h24Selected = policy.Enabled && normalizedHours == 24;

        return new ComponentBuilder()
            .WithButton(offSelected ? "OFF (現在)" : "OFF", $"{VoiceTextAutoDeleteSetCustomIdPrefix}off", offSelected ? ButtonStyle.Secondary : ButtonStyle.Danger, disabled: offSelected, row: 0)
            .WithButton(h6Selected ? "6h (現在)" : "6h", $"{VoiceTextAutoDeleteSetCustomIdPrefix}6", h6Selected ? ButtonStyle.Success : ButtonStyle.Secondary, disabled: h6Selected, row: 0)
            .WithButton(h12Selected ? "12h (現在)" : "12h", $"{VoiceTextAutoDeleteSetCustomIdPrefix}12", h12Selected ? ButtonStyle.Success : ButtonStyle.Secondary, disabled: h12Selected, row: 0)
            .WithButton(h24Selected ? "24h (現在)" : "24h", $"{VoiceTextAutoDeleteSetCustomIdPrefix}24", h24Selected ? ButtonStyle.Success : ButtonStyle.Secondary, disabled: h24Selected, row: 0)
            .Build();
    }

    private static bool TryParseVoiceTextAutoDeleteMode(string modeRaw, out bool enabled, out int retentionHours)
    {
        enabled = false;
        retentionHours = 6;
        if (string.IsNullOrWhiteSpace(modeRaw))
        {
            return false;
        }

        var normalized = modeRaw.Trim().ToLowerInvariant();
        if (normalized is "off" or "0")
        {
            enabled = false;
            retentionHours = 6;
            return true;
        }

        if (!int.TryParse(normalized, out var parsed))
        {
            return false;
        }

        if (parsed is not (6 or 12 or 24))
        {
            return false;
        }

        enabled = true;
        retentionHours = parsed;
        return true;
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);

    private async Task<Embed> BuildSelfSettingsEmbedAsync(SocketGuildUser guildUser, string title, bool vcNotifyEnabled, string? notice = null)
    {
        var settings = await _db.GetGuildSettingsAsync(guildUser.Guild.Id);
        var memberRole = settings is not null && settings.MemberRoleId != 0
            ? guildUser.Guild.GetRole(ToUlong(settings.MemberRoleId))
            : null;
        var provisionalRole = settings is not null && settings.ProvisionalRoleId != 0
            ? guildUser.Guild.GetRole(ToUlong(settings.ProvisionalRoleId))
            : null;
        var voiceNotifyRoleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName)
            ? "通話通知"
            : _options.VoiceNotificationRoleName;
        var voiceNotifyRole = guildUser.Guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, voiceNotifyRoleName, StringComparison.OrdinalIgnoreCase));

        var memberState = "未登録";
        if (memberRole is not null && guildUser.Roles.Any(role => role.Id == memberRole.Id))
        {
            memberState = "登録済み";
        }
        else if (provisionalRole is not null && guildUser.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            memberState = "仮登録";
        }

        var hasVoiceNotifyRole = voiceNotifyRole is not null && guildUser.Roles.Any(role => role.Id == voiceNotifyRole.Id);
        var vcTextAutoDelete = await _db.GetVoiceTextAutoDeletePolicyAsync(guildUser.Id);
        var vcTextAutoDeleteState = vcTextAutoDelete.Enabled
            ? $"ON ({Math.Clamp(vcTextAutoDelete.RetentionHours <= 0 ? 6 : vcTextAutoDelete.RetentionHours, 1, 168)}時間)"
            : "OFF";
        var roleList = guildUser.Roles
            .Where(role => role.Id != guildUser.Guild.EveryoneRole.Id)
            .OrderByDescending(role => role.Position)
            .Select(role => role.Mention)
            .ToList();
        var roleText = roleList.Count == 0 ? "なし" : Truncate(string.Join(", ", roleList), 1000);

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .WithAuthor(guildUser.DisplayName, guildUser.GetAvatarUrl() ?? guildUser.GetDefaultAvatarUrl())
            .AddField("ユーザー", $"{guildUser.Mention}\nID: `{guildUser.Id}`", false)
            .AddField("メンバー登録状態", memberState, true)
            .AddField("通話通知ロール", hasVoiceNotifyRole ? "ON" : "OFF", true)
            .AddField("自分のVC通知表示", vcNotifyEnabled ? "ON" : "OFF", true)
            .AddField("VC内テキスト自動削除", vcTextAutoDeleteState, true)
            .AddField("サーバー参加日", guildUser.JoinedAt.HasValue ? $"<t:{guildUser.JoinedAt.Value.ToUnixTimeSeconds()}:F>" : "不明", true)
            .AddField("現在のロール", roleText, false);

        if (!string.IsNullOrWhiteSpace(notice))
        {
            embed.AddField("実行結果", notice, false);
        }

        return embed.Build();
    }

    private async Task<Embed> BuildMyPageEmbedAsync(SocketGuildUser guildUser)
    {
        var guildId = guildUser.Guild.Id;
        var userId = guildUser.Id;

        var hostRecruitsTask = _db.ListActiveRecruitPostsByCreatorAsync(guildId, userId);
        var joinedRecruitsTask = _db.ListActiveRecruitPostsByParticipantAsync(guildId, userId, maxRows: 5);
        var activeLotteriesTask = _db.ListActiveLotteriesByEntryUserAsync(guildId, userId, maxRows: 5);
        var inquiryTask = _db.GetOpenInquiryTicketByRequesterAsync(guildId, userId);
        var profileTask = _db.GetDiscordUserProfileAsync(userId);
        var mainCharTask = _db.GetMainCharacterAsync(userId);
        var settingsTask = _db.GetGuildSettingsAsync(guildId);
        var vcNotifyTask = _db.GetVoiceNotificationEnabledAsync(userId);
        var vcTextAutoDeleteTask = _db.GetVoiceTextAutoDeletePolicyAsync(userId);

        await Task.WhenAll(
            hostRecruitsTask,
            joinedRecruitsTask,
            activeLotteriesTask,
            inquiryTask,
            profileTask,
            mainCharTask,
            settingsTask,
            vcNotifyTask,
            vcTextAutoDeleteTask);

        var hostRecruits = (await hostRecruitsTask).Take(5).ToList();
        var joinedRecruits = (await joinedRecruitsTask).ToList();
        var activeLotteries = (await activeLotteriesTask).ToList();
        var inquiry = await inquiryTask;
        var profile = await profileTask;
        var mainChar = await mainCharTask;
        var settings = await settingsTask;
        var vcNotifyEnabled = await vcNotifyTask;
        var vcTextAutoDelete = await vcTextAutoDeleteTask;

        var memberRole = settings is not null && settings.MemberRoleId != 0
            ? guildUser.Guild.GetRole(ToUlong(settings.MemberRoleId))
            : null;
        var provisionalRole = settings is not null && settings.ProvisionalRoleId != 0
            ? guildUser.Guild.GetRole(ToUlong(settings.ProvisionalRoleId))
            : null;
        var voiceNotifyRoleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName)
            ? "通話通知"
            : _options.VoiceNotificationRoleName;
        var voiceNotifyRole = guildUser.Guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, voiceNotifyRoleName, StringComparison.OrdinalIgnoreCase));

        var memberState = "未登録";
        if (memberRole is not null && guildUser.Roles.Any(role => role.Id == memberRole.Id))
        {
            memberState = "登録済み";
        }
        else if (provisionalRole is not null && guildUser.Roles.Any(role => role.Id == provisionalRole.Id))
        {
            memberState = "仮登録";
        }

        var hasVoiceNotifyRole = voiceNotifyRole is not null && guildUser.Roles.Any(role => role.Id == voiceNotifyRole.Id);
        var vcTextAutoDeleteState = vcTextAutoDelete.Enabled
            ? $"ON ({Math.Clamp(vcTextAutoDelete.RetentionHours <= 0 ? 6 : vcTextAutoDelete.RetentionHours, 1, 168)}時間)"
            : "OFF";
        var roleList = guildUser.Roles
            .Where(role => role.Id != guildUser.Guild.EveryoneRole.Id)
            .OrderByDescending(role => role.Position)
            .Select(role => role.Mention)
            .ToList();
        var roleText = roleList.Count == 0 ? "なし" : Truncate(string.Join(", ", roleList), 1000);

        var embed = new EmbedBuilder()
            .WithTitle("マイページ")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .WithAuthor(guildUser.DisplayName, guildUser.GetAvatarUrl() ?? guildUser.GetDefaultAvatarUrl())
            .AddField(
                "プロフィール",
                $"キャラ名: {profile?.FfxivName ?? "未設定"}\n" +
                $"ストーリー進行度: {FormatStoryProgression(profile?.StoryProgression)}\n" +
                $"ロドスト連携: {(mainChar is not null ? "済み" : "未連携")}",
                false)
            .AddField(
                "設定状態",
                $"メンバー登録: {memberState}\n" +
                $"通話通知ロール: {(hasVoiceNotifyRole ? "ON" : "OFF")}\n" +
                $"自分のVC通知: {(vcNotifyEnabled ? "ON" : "OFF")}\n" +
                $"VCテキスト自動削除: {vcTextAutoDeleteState}",
                false)
            .AddField("サーバー参加日", guildUser.JoinedAt.HasValue ? $"<t:{guildUser.JoinedAt.Value.ToUnixTimeSeconds()}:F>" : "不明", true)
            .AddField("現在のロール", roleText, false)
            .AddField("募集(主催中)", BuildRecruitSummaryLines(guildId, hostRecruits), false)
            .AddField("募集(参加中)", BuildRecruitSummaryLines(guildId, joinedRecruits), false)
            .AddField("抽選(応募中/結果待ち)", BuildLotterySummaryLines(guildId, activeLotteries), false)
            .AddField("問い合わせ", BuildInquirySummaryLine(guildId, inquiry), false);

        return embed.Build();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return Truncate(normalized, maxLength);
    }

    private static string BuildRecruitSummaryLines(ulong guildId, IReadOnlyList<RecruitPostSummary> posts)
    {
        if (posts.Count == 0)
        {
            return "なし";
        }

        var lines = posts.Select(post =>
        {
            var status = post.Status switch
            {
                RecruitPostStatus.Open => "募集中",
                RecruitPostStatus.Full => "満員",
                RecruitPostStatus.Closed => "終了",
                RecruitPostStatus.Canceled => "中断",
                _ => post.Status.ToString()
            };
            var schedule = post.ScheduleAt.HasValue ? $"<t:{post.ScheduleAt.Value.ToUnixTimeSeconds()}:f>" : (post.ScheduleText ?? "日時未設定");
            var title = string.IsNullOrWhiteSpace(post.Title) ? "(無題)" : post.Title.Trim();
            var link = BuildDiscordMessageLink(guildId, ToUlong(post.ChannelId), ToUlong(post.MessageId));
            return $"`{status}` {schedule}\n[{Truncate(title, 80)}]({link}) ({post.FilledCount}/{post.SlotCount})";
        });

        return Truncate(string.Join("\n", lines), 1000);
    }

    private static string BuildLotterySummaryLines(ulong guildId, IReadOnlyList<Lottery> lotteries)
    {
        if (lotteries.Count == 0)
        {
            return "なし";
        }

        var lines = lotteries.Select(lottery =>
        {
            var status = LotteryStatus.IsOpen(lottery.Status) ? "応募中" :
                LotteryStatus.IsClosed(lottery.Status) ? "結果待ち" :
                lottery.Status;
            var closeAt = $"<t:{lottery.CloseAt.ToUnixTimeSeconds()}:f>";
            var title = string.IsNullOrWhiteSpace(lottery.Title) ? "(無題)" : lottery.Title.Trim();
            var link = lottery.MessageId.HasValue && lottery.MessageId.Value != 0
                ? BuildDiscordMessageLink(guildId, ToUlong(lottery.ChannelId), ToUlong(lottery.MessageId.Value))
                : $"https://discord.com/channels/{guildId}/{ToUlong(lottery.ChannelId)}";
            return $"`{status}` 締切: {closeAt}\n[{Truncate(title, 80)}]({link})";
        });

        return Truncate(string.Join("\n", lines), 1000);
    }

    private static string BuildInquirySummaryLine(ulong guildId, InquiryTicket? inquiry)
    {
        if (inquiry is null)
        {
            return "なし";
        }

        var status = InquiryTicketStatus.ToLabel(inquiry.Status);
        var channelLink = inquiry.ChannelId != 0
            ? $"https://discord.com/channels/{guildId}/{ToUlong(inquiry.ChannelId)}"
            : null;
        var subject = string.IsNullOrWhiteSpace(inquiry.Subject) ? "(件名なし)" : inquiry.Subject.Trim();
        var updated = $"<t:{inquiry.UpdatedAt.ToUnixTimeSeconds()}:R>";

        return channelLink is null
            ? $"`{status}` {Truncate(subject, 120)} / 更新: {updated}"
            : $"`{status}` [{Truncate(subject, 120)}]({channelLink}) / 更新: {updated}";
    }

    private static string FormatStoryProgression(string? storyProgression)
    {
        if (string.IsNullOrWhiteSpace(storyProgression))
        {
            return "未設定";
        }

        return storyProgression switch
        {
            "arr_hw" => "新生〜蒼天",
            "sb" => "紅蓮",
            "shb" => "漆黒",
            "ew" => "暁月",
            "dt" => "黄金",
            "completed" => "最新パッチ済み",
            _ => storyProgression,
        };
    }

    private static string BuildDiscordMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    private static Embed BuildAdminPanelStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled, bool sleepKickEnabled = false, bool sleepKickAutoDetect = false, bool sleepKickAdminConfirm = false)
        => AdminControlPanel.BuildStatusEmbed(approvalRequired, inactiveAutoKickEnabled, sleepKickEnabled, sleepKickAutoDetect, sleepKickAdminConfirm);

    private async Task GrantStoryProgressionRoleAsync(SocketGuildUser guildUser, string? storyProgression)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var allStoryRoleNames = StoryProgressionOptions.Values.ToList();

        if (string.IsNullOrWhiteSpace(storyProgression) || !StoryProgressionOptions.TryGetValue(storyProgression, out var targetRoleName))
        {
            var removeRoles = guildUser.Roles
                .Where(role => allStoryRoleNames.Any(name => string.Equals(name, role.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (removeRoles.Count > 0)
            {
                await guildUser.RemoveRolesAsync(removeRoles);
            }
            return;
        }

        IRole? existingRole = Context.Guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, targetRoleName, StringComparison.OrdinalIgnoreCase));

        if (existingRole is null)
        {
            existingRole = await Context.Guild.CreateRoleAsync(targetRoleName, GuildPermissions.None, color: new Color(99, 110, 114), isHoisted: false, isMentionable: false);
        }

        if (existingRole is null)
        {
            return;
        }

        var removeOtherRoles = guildUser.Roles
            .Where(role => allStoryRoleNames.Any(name => string.Equals(name, role.Name, StringComparison.OrdinalIgnoreCase)) && role.Id != existingRole.Id)
            .ToList();

        if (removeOtherRoles.Count > 0)
        {
            await guildUser.RemoveRolesAsync(removeOtherRoles);
        }

        if (!guildUser.Roles.Any(role => role.Id == existingRole.Id))
        {
            await guildUser.AddRoleAsync(existingRole);
        }
    }
}
