using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;

namespace RaidDcBot.Bot;

[Group("controlpanel", "コントロールパネル")]
public sealed class ControlPanelAdminModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly ILogger<ControlPanelAdminModule> _logger;

    public ControlPanelAdminModule(Db db, ILogger<ControlPanelAdminModule> logger)
    {
        _db = db;
        _logger = logger;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにコントロールパネルを設置します")]
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

        var existing = await _db.GetGuildControlPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = existing is null || existing.MessageId == 0 ? null : ToUlong(existing.MessageId);
        var message = await EnsureControlPanelMessageAsync(channel, existingMessageId);

        await _db.UpsertGuildControlPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        await FollowupAsync("このチャンネルにコントロールパネルを設置しました。", ephemeral: true);
    }

    private async Task<IUserMessage> EnsureControlPanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
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
                _logger.LogWarning(ex, "Failed to fetch existing control panel message.");
            }
        }

        var components = ControlPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props => props.Components = components);
            return existing;
        }

        return await channel.SendMessageAsync(ControlPanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
