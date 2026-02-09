using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class RecruitmentListService
{
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
                    message.Content.StartsWith(RecruitListMessageHeader, StringComparison.Ordinal));
        }

        var embed = await BuildListEmbedAsync(guild.Id);
        if (existing is not null)
        {
            await existing.ModifyAsync(props => props.Embed = embed);
            return existing;
        }

        return await channel.SendMessageAsync(RecruitListMessageHeader, embed: embed);
    }

    public async Task RefreshAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildRecruitListPanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            var embed = await BuildListEmbedAsync(guild.Id);
            await message.ModifyAsync(props => props.Embed = embed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recruit list message.");
        }
    }

    public async Task<Embed> BuildListEmbedAsync(ulong guildId)
    {
        var posts = await _db.ListRecruitPostSummariesAsync(guildId);
        var visible = posts
            .Where(post => post.Status is RecruitPostStatus.Open or RecruitPostStatus.Full)
            .ToList();

        var builder = new EmbedBuilder()
            .WithTitle("募集一覧")
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
            var status = post.Status == RecruitPostStatus.Full ? "満員" : "募集中";
            var schedule = FormatSchedule(post.ScheduleText, post.ScheduleAt);
            var dataCenter = string.IsNullOrWhiteSpace(post.DataCenter) ? "未指定" : post.DataCenter;
            var messageLink = $"https://discord.com/channels/{guildId}/{ToUlong(post.ChannelId)}/{ToUlong(post.MessageId)}";
            var max = post.SlotCount > 0 ? post.SlotCount : RecruitmentRules.Positions.Count;
            var line = $"[{post.Title}]({messageLink}) | {status} | {post.FilledCount}/{max} | DC: {dataCenter} | 開始: {schedule}";
            lines.Add(line);
        }

        builder.WithDescription(string.Join('\n', lines));
        return builder.Build();
    }

    public const string RecruitListMessageHeader = "募集一覧";

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
