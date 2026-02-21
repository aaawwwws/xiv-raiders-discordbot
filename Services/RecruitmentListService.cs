using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;
using System.Text;

namespace RaidDcBot.Services;

public sealed class RecruitmentListService
{
    private const string RecruitListEmbedTitle = "募集一覧";
    private const int MaxListDescriptionLength = 3900;
    private const int MaxListLines = 40;

    private readonly Db _db;
    private readonly ILogger<RecruitmentListService> _logger;

    public RecruitmentListService(Db db, ILogger<RecruitmentListService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IUserMessage?> EnsureListMessageAsync(SocketGuild guild, IMessageChannel channel, ulong? existingMessageId)
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
                _logger.LogWarning(ex, "Failed to fetch existing recruit list message.");
            }
        }

        if (existing is null)
        {
            var recent = await channel.GetMessagesAsync(50).FlattenAsync();
            existing = recent
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.Id == guild.CurrentUser.Id &&
                    IsRecruitListMessage(message));
        }

        var embed = await BuildListEmbedAsync(guild.Id);
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
            });
            return await EnsureListMessageLatestAsync(channel, existing, embed);
        }

        return await channel.SendMessageAsync(embed: embed);
    }

    public async Task RefreshAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildRecruitListPanelAsync(guild.Id);
        var channel = panel is not null && panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            IUserMessage? message = null;
            if (panel is not null && panel.MessageId != 0)
            {
                message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            }

            if (message is null)
            {
                var ensured = await EnsureListMessageAsync(guild, channel, null);
                if (ensured is not null)
                {
                    await _db.UpsertGuildRecruitListPanelAsync(guild.Id, channel.Id, ensured.Id);
                }

                return;
            }

            var embed = await BuildListEmbedAsync(guild.Id);
            await message.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
            });
            var latest = await EnsureListMessageLatestAsync(channel, message, embed);
            if (latest.Id != message.Id ||
                panel is null ||
                ToUlong(panel.ChannelId) != channel.Id)
            {
                await _db.UpsertGuildRecruitListPanelAsync(guild.Id, channel.Id, latest.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recruit list message.");
        }
    }

    public async Task<Embed> BuildListEmbedAsync(ulong guildId)
    {
        var posts = await _db.ListRecruitPostSummariesAsync(guildId);
        var nowUtc = DateTimeOffset.UtcNow;
        var visible = posts
            .Where(post =>
                post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full ||
                (post.Status == RecruitPostStatus.Closed &&
                 post.ScheduleAt.HasValue &&
                 post.ScheduleAt.Value > nowUtc))
            .ToList();

        var builder = new EmbedBuilder()
            .WithTitle(RecruitListEmbedTitle)
            .WithColor(new Color(145, 180, 255))
            .WithCurrentTimestamp();

        if (visible.Count == 0)
        {
            builder.WithDescription("現在の募集はありません。");
            return builder.Build();
        }

        var lines = new List<string>();
        foreach (var post in visible)
        {
            var status = post.Status switch
            {
                RecruitPostStatus.Full => "🟡満員",
                RecruitPostStatus.Closed => "⚫終了",
                RecruitPostStatus.Canceled => "🔴中断",
                _ => "🟢募集中"
            };
            var schedule = FormatSchedule(post.ScheduleText, post.ScheduleAt);
            var dataCenter = string.IsNullOrWhiteSpace(post.DataCenter) ? "未指定" : post.DataCenter;
            var messageLink = $"https://discord.com/channels/{guildId}/{ToUlong(post.ChannelId)}/{ToUlong(post.MessageId)}";
            var max = post.SlotCount > 0 ? post.SlotCount : RecruitmentRules.Positions.Count;
            var line = $"[{post.Title}]({messageLink}) | {status} | {post.FilledCount}/{max} | DC: {dataCenter} | 開始: {schedule}";
            lines.Add(line);
        }

        var descriptionBuilder = new StringBuilder();
        var displayed = 0;
        foreach (var line in lines)
        {
            if (displayed >= MaxListLines)
            {
                break;
            }

            var prefix = displayed == 0 ? string.Empty : "\n";
            if (descriptionBuilder.Length + prefix.Length + line.Length > MaxListDescriptionLength)
            {
                break;
            }

            if (prefix.Length > 0)
            {
                descriptionBuilder.Append(prefix);
            }

            descriptionBuilder.Append(line);
            displayed++;
        }

        var omitted = Math.Max(0, visible.Count - displayed);
        if (omitted > 0)
        {
            var suffix = $"\n...他 {omitted} 件（最新順）";
            if (descriptionBuilder.Length + suffix.Length <= MaxListDescriptionLength)
            {
                descriptionBuilder.Append(suffix);
            }
        }

        builder.WithDescription(descriptionBuilder.ToString());
        builder.WithFooter($"表示 {displayed}/{visible.Count} 件");
        return builder.Build();
    }

    private async Task<IUserMessage> EnsureListMessageLatestAsync(IMessageChannel channel, IUserMessage message, Embed embed)
    {
        var latest = (await channel.GetMessagesAsync(1).FlattenAsync())
            .OfType<IUserMessage>()
            .FirstOrDefault();

        if (latest?.Id == message.Id)
        {
            return message;
        }

        var reposted = await channel.SendMessageAsync(embed: embed);
        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete previous recruit list message {MessageId}.", message.Id);
        }

        return reposted;
    }

    private static bool IsRecruitListMessage(IUserMessage message)
    {
        var embedTitle = message.Embeds.FirstOrDefault()?.Title;
        if (string.Equals(embedTitle, RecruitListEmbedTitle, StringComparison.Ordinal))
        {
            return true;
        }

        // Backward compatibility for old text+embed posts.
        return message.Content.StartsWith(RecruitListEmbedTitle, StringComparison.Ordinal);
    }

    private static string FormatSchedule(string? scheduleText, DateTimeOffset? scheduleAt)
    {
        if (!string.IsNullOrWhiteSpace(scheduleText))
        {
            return scheduleText;
        }

        if (!scheduleAt.HasValue)
        {
            return "未定";
        }

        return scheduleAt.Value
            .ToOffset(TimeSpan.FromHours(9))
            .ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
