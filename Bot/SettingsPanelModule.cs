using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
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

    public SettingsPanelModule(
        Db db,
        RecruitmentListService recruitListService,
        ILogger<SettingsPanelModule> logger)
    {
        _db = db;
        _recruitListService = recruitListService;
        _logger = logger;
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
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        try
        {
            var enabled = await _db.ToggleVoiceNotificationEnabledAsync(Context.User.Id);
            var state = enabled ? "ON" : "OFF";
            await RespondAsync($"自分のVC通知表示を `{state}` にしました。", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle self VC notification setting. UserId={UserId}", Context.User.Id);
            await RespondAsync("設定の更新に失敗しました。時間をおいて再試行してください。", ephemeral: true);
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
                ControlPanel.PanelMessage,
                ControlPanel.BuildComponents()))
        {
            updated++;
        }

        targets++;
        if (await RefreshPanelMessageAsync(
                Context.Guild,
                await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id),
                fallbackChannel,
                AdminControlPanel.PanelMessage,
                AdminControlPanel.BuildComponents()))
        {
            updated++;
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
        string messageText,
        MessageComponent components)
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
}
