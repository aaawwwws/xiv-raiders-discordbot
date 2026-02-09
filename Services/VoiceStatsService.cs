using Discord;
using Discord.WebSocket;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class VoiceStatsService {
    private readonly Db _db;

    public VoiceStatsService(Db db) {
        _db = db;
    }

    public async Task<Embed> BuildWeeklyRankingEmbedAsync(SocketGuild guild, DateTimeOffset nowUtc, int rangeDays, int topCount) {
        var fromUtc = nowUtc.AddDays(-Math.Max(1, rangeDays));
        return await BuildActivityRankingEmbedAsync(guild, fromUtc, nowUtc, topCount, "週間アクティビティランキング");
    }

    public async Task<Embed> BuildActivityRankingEmbedAsync(
        SocketGuild guild,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int topCount,
        string title) {
        if (toUtc <= fromUtc) {
            toUtc = fromUtc.AddMinutes(1);
        }

        var voiceRows = await _db.ListVoiceDurationSummaryAsync(guild.Id, fromUtc, toUtc, topCount);
        var messageRows = await _db.ListMessageCountsByChannelAsync(guild.Id, fromUtc, toUtc);
        var activeUsers = await _db.ListActiveUserIdsSinceAsync(guild.Id, fromUtc);

        var nonAdminMessageCounts = new Dictionary<ulong, int>();
        foreach (var row in messageRows) {
            var userId = unchecked((ulong)row.UserId);
            var channelId = unchecked((ulong)row.ChannelId);
            if (userId == 0 || channelId == 0 || row.MessageCount <= 0) {
                continue;
            }

            var guildChannel = ResolveBaseGuildChannel(guild, channelId);
            if (guildChannel is null) {
                continue;
            }

            if (!IsAdminCategoryChannel(guild, guildChannel)) {
                nonAdminMessageCounts[userId] = nonAdminMessageCounts.TryGetValue(userId, out var prev)
                    ? prev + row.MessageCount
                    : row.MessageCount;
            }
        }

        var vcTop = voiceRows
            .Take(Math.Clamp(topCount, 1, 20))
            .ToList();
        var messageTop = nonAdminMessageCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(Math.Clamp(topCount, 1, 20))
            .ToList();

        var fromJst = fromUtc.ToOffset(TimeSpan.FromHours(9));
        var toJst = toUtc.ToOffset(TimeSpan.FromHours(9));
        var period = $"{fromJst:MM/dd HH:mm} - {toJst:MM/dd HH:mm} (JST)";

        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(new Color(52, 152, 219))
            .WithCurrentTimestamp()
            .WithDescription($"集計期間: {period}\nアクティブ人数: {activeUsers.Count}");

        var vcLines = new List<string>();
        for (var i = 0; i < vcTop.Count; i++) {
            var row = vcTop[i];
            var userId = unchecked((ulong)row.UserId);
            var mention = $"<@{userId}>";
            var display = guild.GetUser(userId)?.DisplayName ?? userId.ToString();
            vcLines.Add($"{i + 1}. {mention} ({display}) - {FormatDuration(row.DurationSeconds)}");
        }

        if (vcLines.Count == 0) {
            vcLines.Add("データなし");
        }

        var messageLines = new List<string>();
        for (var i = 0; i < messageTop.Count; i++) {
            var row = messageTop[i];
            var mention = $"<@{row.Key}>";
            var display = guild.GetUser(row.Key)?.DisplayName ?? row.Key.ToString();
            messageLines.Add($"{i + 1}. {mention} ({display}) - {row.Value}件");
        }

        if (messageLines.Count == 0) {
            messageLines.Add("データなし");
        }

        builder.AddField("VC滞在ランキング", string.Join('\n', vcLines));
        builder.AddField("発言数ランキング", string.Join('\n', messageLines));
        return builder.Build();
    }

    private static string FormatDuration(long durationSeconds) {
        if (durationSeconds < 0) {
            durationSeconds = 0;
        }

        var span = TimeSpan.FromSeconds(durationSeconds);
        if (span.TotalHours >= 1) {
            return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }

        return $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static SocketGuildChannel? ResolveBaseGuildChannel(SocketGuild guild, ulong channelId) {
        var channel = guild.GetChannel(channelId);
        if (channel is null) {
            return null;
        }

        if (channel is SocketThreadChannel thread && thread.ParentChannel is SocketGuildChannel parent) {
            return parent;
        }

        return channel;
    }

    private static bool IsAdminCategoryChannel(SocketGuild guild, SocketGuildChannel channel) {
        string? categoryName = null;
        if (channel is INestedChannel nested && nested.CategoryId.HasValue) {
            categoryName = guild.GetCategoryChannel(nested.CategoryId.Value)?.Name;
        }

        if (string.IsNullOrWhiteSpace(categoryName)) {
            categoryName = channel.Name;
        }

        if (string.IsNullOrWhiteSpace(categoryName)) {
            return false;
        }

        return categoryName.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
               categoryName.Contains("管理", StringComparison.OrdinalIgnoreCase);
    }
}
