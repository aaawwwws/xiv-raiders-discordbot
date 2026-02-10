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
    private readonly Db _db;
    private readonly RecruitmentListService _recruitListService;
    private readonly ILogger<SettingsPanelModule> _logger;
    private readonly DiscordOptions _options;

    public SettingsPanelModule(
        Db db,
        RecruitmentListService recruitListService,
        ILogger<SettingsPanelModule> logger,
        IOptions<DiscordOptions> options)
    {
        _db = db;
        _recruitListService = recruitListService;
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

        try
        {
            var enabled = await _db.ToggleVoiceNotificationEnabledAsync(Context.User.Id);
            var embed = await BuildSelfSettingsEmbedAsync(guildUser, "設定を更新しました", enabled);
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle self VC notification setting. UserId={UserId}", Context.User.Id);
            await FollowupAsync("設定の更新に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [ComponentInteraction(SettingsPanel.ShowSelfStatusCustomId, ignoreGroupNames: true)]
    public async Task ShowSelfSettingsAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        try
        {
            var current = await _db.GetVoiceNotificationEnabledAsync(Context.User.Id);
            var embed = await BuildSelfSettingsEmbedAsync(guildUser, "現在の設定", current);
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show self settings. UserId={UserId}", Context.User.Id);
            await FollowupAsync("設定の取得に失敗しました。時間をおいて再試行してください。", ephemeral: true);
        }
    }

    [RequireBotAdmin]
    [ComponentInteraction(SettingsPanel.ResyncPanelsCustomId, ignoreGroupNames: true)]
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

        targets++;
        if (await RefreshSettingsPanelMessageAsync(fallbackChannel))
        {
            updated++;
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
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildRecruitPanelAsync(Context.Guild.Id),
                fallbackChannel,
                null,
                RecruitmentPanel.BuildPanelComponents()))
        {
            updated++;
        }

        targets++;
        if (await RefreshRecruitListMessageAsync(Context.Guild, fallbackChannel))
        {
            updated++;
        }

        await FollowupAsync($"パネル再同期完了: {updated}/{targets} を更新しました。", ephemeral: true);
    }

    private async Task<IUserMessage> EnsureSettingsPanelMessageAsync(IMessageChannel channel)
    {
        var recent = await channel.GetMessagesAsync(100).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                SettingsPanel.LooksLikePanelMessage(message.Content));

        var components = SettingsPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = SettingsPanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        try
        {
            return await channel.SendMessageAsync(
                SettingsPanel.PanelMessage,
                components: components,
                flags: MessageFlags.SuppressNotification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send settings panel message.");
            throw;
        }
    }

    private async Task<bool> RefreshSettingsPanelMessageAsync(IMessageChannel fallbackChannel)
    {
        IUserMessage? message = null;
        if (Context.Interaction is SocketMessageComponent component &&
            component.Message is IUserMessage componentMessage &&
            componentMessage.Author.Id == Context.Client.CurrentUser.Id &&
            SettingsPanel.LooksLikePanelMessage(componentMessage.Content))
        {
            message = componentMessage;
        }

        if (message is null)
        {
            var recent = await fallbackChannel.GetMessagesAsync(100).FlattenAsync();
            message = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(x =>
                    x.Author.Id == Context.Client.CurrentUser.Id &&
                    SettingsPanel.LooksLikePanelMessage(x.Content));
        }

        if (message is null)
        {
            return false;
        }

        try
        {
            await message.ModifyAsync(props =>
            {
                props.Content = SettingsPanel.PanelMessage;
                props.Components = SettingsPanel.BuildComponents();
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh settings panel message.");
            return false;
        }
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

    private static ulong ToUlong(long value) => unchecked((ulong)value);

    private async Task<Embed> BuildSelfSettingsEmbedAsync(SocketGuildUser guildUser, string title, bool vcNotifyEnabled)
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
            .AddField("サーバー参加日", guildUser.JoinedAt.HasValue ? $"<t:{guildUser.JoinedAt.Value.ToUnixTimeSeconds()}:F>" : "不明", true)
            .AddField("現在のロール", roleText, false);

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

    private static Embed BuildAdminPanelStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled)
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
}
