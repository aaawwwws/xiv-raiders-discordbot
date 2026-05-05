using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Bot;
using RaidDcBot.Data;
using System.Collections.Concurrent;

namespace RaidDcBot.Services;

public sealed class VoiceChannelTextService
{
    public const string StatusPromptSetCustomId = "voice_status_prompt_set";
    public const string StatusPromptClearCustomId = "voice_status_prompt_clear";
    public const string StatusPromptModalCustomId = "voice_status_prompt_modal";
    public const string StatusPromptSleepKickCustomId = "voice_status_sleep_kick";
    public const string StatusPromptSleepKickSelectCustomId = "voice_status_sleep_kick_select";
    public const string StatusPromptSleepKickConfirmCustomId = "voice_status_sleep_kick_confirm";

    private readonly DiscordSocketClient _client;
    private readonly VoiceTransferTracker _tracker;
    private readonly ILogger<VoiceChannelTextService> _logger;
    private readonly Db _db;
    private readonly ConcurrentDictionary<ulong, ulong> _statusPromptMessageIds = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _statusPromptTouchedAt = new();
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> _spoilerWarningCooldowns = new();

    public VoiceChannelTextService(
        DiscordSocketClient client,
        VoiceTransferTracker tracker,
        ILogger<VoiceChannelTextService> logger,
        Db db)
    {
        _client = client;
        _tracker = tracker;
        _logger = logger;
        _db = db;
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

    public async Task UpsertStatusPromptAsync(
        SocketVoiceChannel channel,
        SocketUser? actor = null,
        string? notice = null,
        string? descriptionOverride = null,
        bool suppressNotification = false,
        bool forceRepost = false)
    {
        var textChannel = await ResolveAsync(channel);
        if (textChannel is null)
        {
            return;
        }

        var embed = BuildStatusPromptEmbed(channel, actor, notice, descriptionOverride);
        var components = BuildStatusPromptComponents();
        if (!forceRepost && _statusPromptMessageIds.TryGetValue(channel.Id, out var existingMessageId))
        {
            try
            {
                var existing = await textChannel.GetMessageAsync(existingMessageId) as IUserMessage;
                if (existing is not null)
                {
                    await existing.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = components;
                    });
                    _statusPromptTouchedAt[channel.Id] = DateTimeOffset.UtcNow;
                    return;
                }
            }
            catch
            {
                _statusPromptMessageIds.TryRemove(channel.Id, out _);
            }
        }
        else if (forceRepost)
        {
            await DeleteExistingStatusPromptAsync(textChannel, channel.Id);
        }

        var posted = suppressNotification
            ? await textChannel.SendMessageAsync(
                embed: embed,
                components: components,
                flags: MessageFlags.SuppressNotification)
            : await textChannel.SendMessageAsync(
                embed: embed,
                components: components);
        _statusPromptMessageIds[channel.Id] = posted.Id;
        _statusPromptTouchedAt[channel.Id] = DateTimeOffset.UtcNow;
    }

    public async Task ClearStatusPromptAsync(SocketVoiceChannel channel)
    {
        var textChannel = await ResolveAsync(channel);
        if (textChannel is null)
        {
            _statusPromptMessageIds.TryRemove(channel.Id, out _);
            _statusPromptTouchedAt.TryRemove(channel.Id, out _);
            return;
        }

        await DeleteExistingStatusPromptAsync(textChannel, channel.Id);
        _statusPromptTouchedAt.TryRemove(channel.Id, out _);
    }

    public async Task TryPostStatusPromptReminderAsync(SocketVoiceChannel channel, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        if (!channel.ConnectedUsers.Any(user => !user.IsBot))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_statusPromptTouchedAt.TryGetValue(channel.Id, out var lastTouchedAt) &&
            now - lastTouchedAt < interval)
        {
            return;
        }

        var notice = string.IsNullOrWhiteSpace(GetVoiceChannelStatusText(channel))
            ? "1時間ほど経過しました。VCステータスを設定しませんか？"
            : "1時間ほど経過しました。VCステータスを変更しませんか？";

        await UpsertStatusPromptAsync(
            channel,
            actor: null,
            notice: notice,
            descriptionOverride: "VCステータスの確認タイミングです。必要なら更新してください。",
            suppressNotification: true,
            forceRepost: true);
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

    private async Task DeleteExistingStatusPromptAsync(IMessageChannel textChannel, ulong channelId)
    {
        if (!_statusPromptMessageIds.TryRemove(channelId, out var messageId))
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

    private static Embed BuildStatusPromptEmbed(SocketVoiceChannel channel, SocketUser? actor, string? notice, string? descriptionOverride)
    {
        var status = GetVoiceChannelStatusText(channel);
        var actorDisplay = actor switch
        {
            SocketGuildUser guildUser => guildUser.DisplayName ?? guildUser.GlobalName ?? guildUser.Username,
            not null => actor.GlobalName ?? actor.Username,
            _ => null
        };

        var description = !string.IsNullOrWhiteSpace(descriptionOverride)
            ? descriptionOverride
            : string.IsNullOrWhiteSpace(status)
                ? "最初の参加者が入りました。チャンネルステータスを設定しませんか？"
                : "最初の参加者が入りました。必要ならチャンネルステータスを変更できます。";

        if (!string.IsNullOrWhiteSpace(notice))
        {
            description = $"{description}\n\n{notice}";
        }

        if (!string.IsNullOrWhiteSpace(actorDisplay))
        {
            description = $"{description}\n起点: {MentionUser(actor!.Id)} ({actorDisplay})";
        }

        return new EmbedBuilder()
            .WithTitle("VCステータス設定")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .WithDescription(description)
            .AddField("VC", $"<#{channel.Id}> ({channel.Name})", false)
            .AddField("現在のステータス", string.IsNullOrWhiteSpace(status) ? "未設定" : status, false)
            .Build();
    }

    private static MessageComponent BuildStatusPromptComponents()
    {
        return new ComponentBuilder()
            .WithButton("ステータス設定", StatusPromptSetCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("クリア", StatusPromptClearCustomId, ButtonStyle.Danger, row: 0)
            .WithButton("😴 寝落ち投票", StatusPromptSleepKickCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    private static string? GetVoiceChannelStatusText(SocketVoiceChannel channel)
    {
        var status = channel.Status?.Trim();
        if (!string.IsNullOrWhiteSpace(status))
        {
            return status;
        }

        var topic = channel.Topic?.Trim();
        return string.IsNullOrWhiteSpace(topic) ? null : topic;
    }

    private static string MentionUser(ulong userId) => $"<@{userId}>";

    public async Task TrySendSpoilerWarningAsync(SocketGuildUser user, SocketVoiceChannel channel)
    {
        try
        {
            _logger.LogInformation("SpoilerWarning start. UserId={UserId}, ChannelId={ChannelId}", user.Id, channel.Id);

            var profile = await _db.GetDiscordUserProfileAsync(user.Id);
            var storyProgression = profile?.StoryProgression;
            _logger.LogInformation("SpoilerWarning profile. UserId={UserId}, StoryProgression={StoryProgression}", user.Id, storyProgression);

            if (string.IsNullOrWhiteSpace(storyProgression) || storyProgression == "completed")
            {
                _logger.LogInformation("SpoilerWarning skipped (no progression or completed). UserId={UserId}", user.Id);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (_spoilerWarningCooldowns.TryGetValue(user.Id, out var lastWarning) &&
                now - lastWarning < TimeSpan.FromHours(1))
            {
                _logger.LogInformation("SpoilerWarning skipped (cooldown). UserId={UserId}, LastWarning={LastWarning}", user.Id, lastWarning);
                return;
            }

            var textChannel = await ResolveAsync(channel);
            if (textChannel is null)
            {
                _logger.LogWarning("SpoilerWarning skipped (no text channel). UserId={UserId}, ChannelId={ChannelId}", user.Id, channel.Id);
                return;
            }

            var label = storyProgression switch
            {
                "arr_hw" => "新生〜蒼天",
                "sb" => "紅蓮",
                "shb" => "漆黒",
                "ew" => "暁月",
                "dt" => "黄金",
                _ => storyProgression
            };

            var allMentions = channel.ConnectedUsers
                .Where(u => !u.IsBot && u.Id != user.Id)
                .Select(u => MentionUser(u.Id))
                .ToList();
            var mentionText = allMentions.Count > 0
                ? string.Join(" ", allMentions)
                : "";

            var guildUser = channel.Guild.GetUser(user.Id);
            var displayName = guildUser?.DisplayName ?? guildUser?.GlobalName ?? user.GlobalName ?? user.Username;

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ ネタバレ注意")
                .WithDescription(
                    $"{displayName}さんが入室しました。\n" +
                    $"この人は現在 **{label}** までの進行度です。\n" +
                    "最新ストーリーのネタバレにご注意ください。")
                .WithColor(Color.Orange)
                .WithCurrentTimestamp()
                .Build();

            var components = new ComponentBuilder()
                .WithButton("📖 進行度を変更", SettingsPanel.ChangeSelfStoryProgressionCustomId, ButtonStyle.Secondary)
                .Build();

            _logger.LogInformation("SpoilerWarning sending. UserId={UserId}, TextChannelId={TextChannelId}, MentionCount={MentionCount}", user.Id, textChannel.Id, allMentions.Count);

            await textChannel.SendMessageAsync(
                text: mentionText,
                embed: embed,
                components: components);

            _spoilerWarningCooldowns[user.Id] = now;
            _logger.LogInformation("SpoilerWarning sent successfully. UserId={UserId}", user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send spoiler warning for user {UserId}", user.Id);
        }
    }
}
