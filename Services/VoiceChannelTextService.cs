using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Bot;

namespace RaidDcBot.Services;

public sealed class VoiceChannelTextService
{
    private readonly DiscordSocketClient _client;
    private readonly VoiceTransferTracker _tracker;
    private readonly ILogger<VoiceChannelTextService> _logger;

    public VoiceChannelTextService(
        DiscordSocketClient client,
        VoiceTransferTracker tracker,
        ILogger<VoiceChannelTextService> logger)
    {
        _client = client;
        _tracker = tracker;
        _logger = logger;
    }

    public async Task<IMessageChannel?> ResolveAsync(SocketVoiceChannel channel)
    {
        if (channel is IMessageChannel messageChannel)
        {
            return messageChannel;
        }

        try
        {
            var restChannel = await _client.Rest.GetChannelAsync(channel.Id);
            return restChannel as IMessageChannel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve voice text channel for {ChannelId}.", channel.Id);
            return null;
        }
    }

    public async Task PostOwnerTransferAsync(SocketVoiceChannel channel, SocketGuildUser? previousOwner, SocketGuildUser newOwner, SocketUser? actor)
    {
        var textChannel = await ResolveAsync(channel);
        if (textChannel is null)
        {
            return;
        }

        await DeleteExistingTransferAsync(textChannel, channel.Id);

        var executor = actor is null
            ? "自動移譲"
            : $"{MentionUser(actor.Id)} ({actor.GlobalName ?? actor.Username})";

        var embed = new EmbedBuilder()
            .WithTitle("所有者移譲")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .AddField("VC", $"<#{channel.Id}> ({channel.Name})", false)
            .AddField("前の所有者", FormatUser(previousOwner), true)
            .AddField("新しい所有者", FormatUser(newOwner), true)
            .AddField("実行者", executor, true)
            .Build();

        var posted = await textChannel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
        _tracker.Set(channel.Id, posted.Id);
    }

    public async Task ClearOwnerTransferAsync(SocketVoiceChannel channel)
    {
        var textChannel = await ResolveAsync(channel);
        if (textChannel is null)
        {
            return;
        }

        await DeleteExistingTransferAsync(textChannel, channel.Id);
    }

    private async Task DeleteExistingTransferAsync(IMessageChannel textChannel, ulong channelId)
    {
        if (!_tracker.TryRemove(channelId, out var messageId))
        {
            return;
        }

        try
        {
            await textChannel.DeleteMessageAsync(messageId);
        }
        catch
        {
        }
    }

    private static string FormatUser(SocketGuildUser? user)
    {
        if (user is null)
        {
            return "未設定";
        }

        var display = user.GlobalName ?? user.DisplayName ?? user.Username;
        return $"{user.Mention} ({display})";
    }

    private static string MentionUser(ulong userId) => $"<@{userId}>";
}
