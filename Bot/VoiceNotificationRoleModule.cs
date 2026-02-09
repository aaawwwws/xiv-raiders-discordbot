using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

[Group("vcnotify", "VC通知ロール")]
public sealed class VoiceNotificationRoleModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly DiscordOptions _options;
    private readonly ILogger<VoiceNotificationRoleModule> _logger;

    public VoiceNotificationRoleModule(Db db, IOptions<DiscordOptions> options, ILogger<VoiceNotificationRoleModule> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにVC通知ロールパネルを設置します")]
    public async Task SetupAsync()
    {
        var channel = await ResolveSetupChannelAsync();
        if (channel is null)
        {
            return;
        }

        await DeferAsync(ephemeral: true);
        var message = await EnsurePanelMessageAsync(channel);
        await FollowupAsync($"このチャンネルにVC通知ロールパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [ComponentInteraction(VoiceNotificationRolePanel.ToggleCustomId, ignoreGroupNames: true)]
    public async Task ToggleVoiceNotificationRoleAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var roleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName)
            ? "通話通知"
            : _options.VoiceNotificationRoleName;

        IRole? role = Context.Guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));

        if (role is null && _options.AutoCreateVoiceNotificationRole)
        {
            try
            {
                role = await Context.Guild.CreateRoleAsync(
                    roleName,
                    GuildPermissions.None,
                    color: new Color(52, 152, 219),
                    isHoisted: false,
                    isMentionable: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create VC notification role. GuildId={GuildId}", Context.Guild.Id);
                await RespondAsync("通知ロールの作成に失敗しました。Bot権限を確認してください。", ephemeral: true);
                return;
            }
        }

        if (role is null)
        {
            await RespondAsync($"通知ロールが見つかりません: `{roleName}`", ephemeral: true);
            return;
        }

        try
        {
            var hasRole = guildUser.Roles.Any(r => r.Id == role.Id);
            if (hasRole)
            {
                await guildUser.RemoveRoleAsync(role);
                var selfNotify = await _db.GetVoiceNotificationEnabledAsync(guildUser.Id);
                var embed = BuildVoiceNotificationStateEmbed(guildUser, role.Name, hasRoleAfter: false, selfNotify);
                await RespondAsync(embed: embed, ephemeral: true);
                return;
            }

            await guildUser.AddRoleAsync(role);
            var enabled = await _db.GetVoiceNotificationEnabledAsync(guildUser.Id);
            var updatedEmbed = BuildVoiceNotificationStateEmbed(guildUser, role.Name, hasRoleAfter: true, enabled);
            await RespondAsync(embed: updatedEmbed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle VC notification role. GuildId={GuildId}, UserId={UserId}", Context.Guild.Id, guildUser.Id);
            await RespondAsync("ロール変更に失敗しました。Botのロール権限を確認してください。", ephemeral: true);
        }
    }

    private async Task<IMessageChannel?> ResolveSetupChannelAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return null;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return null;
        }

        return channel;
    }

    private async Task<IUserMessage> EnsurePanelMessageAsync(IMessageChannel channel)
    {
        var recent = await channel.GetMessagesAsync(100).FlattenAsync();
        var existing = recent
            .OfType<IUserMessage>()
            .FirstOrDefault(message =>
                message.Author.Id == Context.Client.CurrentUser.Id &&
                VoiceNotificationRolePanel.LooksLikePanelMessage(message.Content));

        var components = VoiceNotificationRolePanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = VoiceNotificationRolePanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            VoiceNotificationRolePanel.PanelMessage,
            components: components,
            flags: MessageFlags.SuppressNotification);
    }

    private static Embed BuildVoiceNotificationStateEmbed(SocketGuildUser user, string roleName, bool hasRoleAfter, bool selfNotifyEnabled)
    {
        var roleState = hasRoleAfter ? "ON" : "OFF";
        var selfState = selfNotifyEnabled ? "ON" : "OFF";
        return new EmbedBuilder()
            .WithTitle("通知設定を更新しました")
            .WithColor(hasRoleAfter ? Color.Green : Color.Orange)
            .WithCurrentTimestamp()
            .WithAuthor(user.DisplayName, user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl())
            .AddField("ユーザー", $"{user.Mention}\nID: `{user.Id}`", false)
            .AddField("通話通知ロール", $"{roleState} (`{roleName}`)", true)
            .AddField("自分のVC通知表示", selfState, true)
            .Build();
    }
}
