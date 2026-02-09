using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;

namespace RaidDcBot.Bot;

[Group("member", "メンバー登録")]
public sealed class MemberPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly ILogger<MemberPanelModule> _logger;

    public MemberPanelModule(Db db, ILogger<MemberPanelModule> logger)
    {
        _db = db;
        _logger = logger;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにメンバー登録パネルを設置します")]
    public async Task SetupAsync()
    {
        var channel = await ResolveSetupChannelAsync();
        if (channel is null)
        {
            return;
        }

        await DeferAsync(ephemeral: true);

        var panel = await _db.GetGuildPanelAsync(Context.Guild.Id);
        var message = await EnsurePanelMessageAsync(
            channel,
            panel is null ? null : ToUlong(panel.MessageId));

        await _db.UpsertGuildPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        await FollowupAsync("このチャンネルにメンバー登録パネルを設置しました。", ephemeral: true);
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
                _logger.LogWarning(ex, "Failed to fetch existing member panel message.");
            }
        }

        if (existing is null)
        {
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == Context.Client.CurrentUser.Id &&
                    MemberRegistrationPanel.LooksLikePanelMessage(message.Content));
        }

        var components = MemberRegistrationPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = MemberRegistrationPanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        return await channel.SendMessageAsync(MemberRegistrationPanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
