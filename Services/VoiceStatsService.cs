using Discord;
using Discord.WebSocket;
using System.Globalization;
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

        var topLimit = Math.Clamp(topCount, 1, 10);
        var voiceRows = await _db.ListVoiceDurationSummaryAsync(guild.Id, fromUtc, toUtc, topLimit);
        var messageRows = await _db.ListMessageCountsByChannelAsync(guild.Id, fromUtc, toUtc);
        var reactionGivenRows = await _db.ListReactionCountsByChannelAsync(guild.Id, fromUtc, toUtc);
        var reactionReceivedRows = await _db.ListReactionReceivedCountsByChannelAsync(guild.Id, fromUtc, toUtc);
        var reactionReceivedTopPostRows = await _db.ListTopReactionReceivedPostsByUserAsync(guild.Id, fromUtc, toUtc);
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

        var nonAdminReactionGivenCounts = new Dictionary<ulong, int>();
        foreach (var row in reactionGivenRows) {
            var userId = unchecked((ulong)row.UserId);
            var channelId = unchecked((ulong)row.ChannelId);
            if (userId == 0 || channelId == 0 || row.ReactionCount <= 0) {
                continue;
            }

            var guildChannel = ResolveBaseGuildChannel(guild, channelId);
            if (guildChannel is null) {
                continue;
            }

            if (!IsAdminCategoryChannel(guild, guildChannel)) {
                nonAdminReactionGivenCounts[userId] = nonAdminReactionGivenCounts.TryGetValue(userId, out var prev)
                    ? prev + row.ReactionCount
                    : row.ReactionCount;
            }
        }

        var nonAdminReactionReceivedCounts = new Dictionary<ulong, int>();
        foreach (var row in reactionReceivedRows) {
            var userId = unchecked((ulong)row.UserId);
            var channelId = unchecked((ulong)row.ChannelId);
            if (userId == 0 || channelId == 0 || row.ReactionCount <= 0) {
                continue;
            }

            var guildChannel = ResolveBaseGuildChannel(guild, channelId);
            if (guildChannel is null) {
                continue;
            }

            if (!IsAdminCategoryChannel(guild, guildChannel)) {
                nonAdminReactionReceivedCounts[userId] = nonAdminReactionReceivedCounts.TryGetValue(userId, out var prev)
                    ? prev + row.ReactionCount
                    : row.ReactionCount;
            }
        }

        var nonAdminReactionReceivedTopPosts = new Dictionary<ulong, ReactionReceivedTopPost>();
        foreach (var row in reactionReceivedTopPostRows) {
            var userId = unchecked((ulong)row.UserId);
            var channelId = unchecked((ulong)row.ChannelId);
            if (userId == 0 || channelId == 0 || row.MessageId == 0 || row.ReactionCount <= 0) {
                continue;
            }

            var guildChannel = ResolveBaseGuildChannel(guild, channelId);
            if (guildChannel is null || IsAdminCategoryChannel(guild, guildChannel)) {
                continue;
            }

            nonAdminReactionReceivedTopPosts[userId] = row;
        }

        var vcTop = voiceRows
            .Take(topLimit)
            .ToList();
        var totalVcSeconds = Math.Max(0, voiceRows.Sum(x => x.DurationSeconds));
        var messageTop = nonAdminMessageCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(topLimit)
            .ToList();
        var reactionGivenTop = nonAdminReactionGivenCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(topLimit)
            .ToList();
        const int reactionReceivedTopLimit = 5;
        var reactionReceivedTop = nonAdminReactionReceivedCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(reactionReceivedTopLimit)
            .ToList();
        var totalMessages = Math.Max(0, nonAdminMessageCounts.Values.Sum());
        var totalReactionGiven = Math.Max(0, nonAdminReactionGivenCounts.Values.Sum());
        var totalReactionReceived = Math.Max(0, nonAdminReactionReceivedCounts.Values.Sum());

        var fromJst = fromUtc.ToOffset(TimeSpan.FromHours(9));
        var toJst = toUtc.ToOffset(TimeSpan.FromHours(9));
        var period = $"{fromJst:MM/dd HH:mm} - {toJst:MM/dd HH:mm} (JST)";
        var embedTitle = ResolveRankingTitle(title, fromJst, toJst);
        var color = ResolveRankingColor(title);

        var builder = new EmbedBuilder()
            .WithTitle(embedTitle)
            .WithColor(color)
            .WithCurrentTimestamp()
            .WithDescription(
                "📅 **集計期間**\n" +
                $"`{period}`\n" +
                $"👥 **アクティブ人数**: `{activeUsers.Count}人`\n" +
                $"🎙️ **VC合計**: `{FormatDuration(totalVcSeconds)}`\n" +
                $"💬 **発言合計**: `{totalMessages}件`\n" +
                $"👏 **付与リアクション合計**: `{totalReactionGiven}件`\n" +
                $"🎯 **被リアクション合計**: `{totalReactionReceived}件`");

        var vcLines = new List<string>();
        for (var i = 0; i < vcTop.Count; i++) {
            var row = vcTop[i];
            var userId = unchecked((ulong)row.UserId);
            var display = guild.GetUser(userId)?.DisplayName ?? userId.ToString();
            vcLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{FormatDuration(row.DurationSeconds)}`");
        }

        if (vcLines.Count == 0) {
            vcLines.Add("😴 データなし");
        }

        var messageLines = new List<string>();
        for (var i = 0; i < messageTop.Count; i++) {
            var row = messageTop[i];
            var display = guild.GetUser(row.Key)?.DisplayName ?? row.Key.ToString();
            messageLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{row.Value}件`");
        }

        if (messageLines.Count == 0) {
            messageLines.Add("😴 データなし");
        }

        var reactionGivenLines = new List<string>();
        for (var i = 0; i < reactionGivenTop.Count; i++) {
            var row = reactionGivenTop[i];
            var display = guild.GetUser(row.Key)?.DisplayName ?? row.Key.ToString();
            reactionGivenLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{row.Value}件`");
        }

        if (reactionGivenLines.Count == 0) {
            reactionGivenLines.Add("😴 データなし");
        }

        var reactionReceivedLines = new List<string>();
        for (var i = 0; i < reactionReceivedTop.Count; i++) {
            var row = reactionReceivedTop[i];
            var display = guild.GetUser(row.Key)?.DisplayName ?? row.Key.ToString();
            if (nonAdminReactionReceivedTopPosts.TryGetValue(row.Key, out var topPost)) {
                var link = BuildMessageLink(guild.Id, unchecked((ulong)topPost.ChannelId), unchecked((ulong)topPost.MessageId));
                reactionReceivedLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{row.Value}件` ([投稿]({link}) `{topPost.ReactionCount}件`)");
                continue;
            }

            reactionReceivedLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{row.Value}件`");
        }

        if (reactionReceivedLines.Count == 0) {
            reactionReceivedLines.Add("😴 データなし");
        }

        builder.AddField("🎙️ VC滞在 TOP", JoinLinesWithinLimit(vcLines));
        builder.AddField("💬 発言数 TOP", JoinLinesWithinLimit(messageLines));
        builder.AddField("👏 リアクション付与 TOP", JoinLinesWithinLimit(reactionGivenLines));
        builder.AddField("🎯 リアクション獲得数TOP5", JoinLinesWithinLimit(reactionReceivedLines));
        builder.WithFooter("Raid DC Activity");
        return builder.Build();
    }

    private static string ToRankBadge(int rank)
    {
        return rank switch
        {
            1 => "🥇",
            2 => "🥈",
            3 => "🥉",
            _ => $"`{rank,2}.`"
        };
    }

    private static string BuildThemedTitle(string rawTitle)
    {
        if (rawTitle.Contains("今日", StringComparison.Ordinal))
        {
            return $"📊 {rawTitle}";
        }

        if (rawTitle.Contains("週", StringComparison.Ordinal))
        {
            return $"🏆 {rawTitle}";
        }

        return $"✨ {rawTitle}";
    }

    private static string ResolveRankingTitle(string rawTitle, DateTimeOffset fromJst, DateTimeOffset toJst)
    {
        if (rawTitle.Contains("今日", StringComparison.Ordinal))
        {
            return $"{toJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}のランキング";
        }

        if (rawTitle.Contains("週", StringComparison.Ordinal))
        {
            return $"{fromJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}-{toJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}のランキング";
        }

        return BuildThemedTitle(rawTitle);
    }

    private static Color ResolveRankingColor(string title)
    {
        if (title.Contains("今日", StringComparison.Ordinal))
        {
            return new Color(46, 204, 113);
        }

        if (title.Contains("週", StringComparison.Ordinal))
        {
            return new Color(241, 196, 15);
        }

        return new Color(52, 152, 219);
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

    private static string BuildMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    private static string JoinLinesWithinLimit(IReadOnlyList<string> lines, int maxLength = 1024)
    {
        if (lines.Count == 0)
        {
            return "😴 データなし";
        }

        var buffer = new List<string>();
        var total = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var extra = (buffer.Count == 0 ? 0 : 1) + line.Length;
            if (total + extra > maxLength)
            {
                var remaining = lines.Count - i;
                if (remaining > 0)
                {
                    var moreLine = $"…ほか{remaining}件";
                    var moreExtra = (buffer.Count == 0 ? 0 : 1) + moreLine.Length;
                    if (total + moreExtra <= maxLength)
                    {
                        buffer.Add(moreLine);
                    }
                }

                break;
            }

            buffer.Add(line);
            total += extra;
        }

        if (buffer.Count == 0)
        {
            var first = lines.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x)) ?? "😴 データなし";
            if (first.Length <= maxLength)
            {
                return first;
            }

            return $"{first[..Math.Max(1, maxLength - 1)]}…";
        }

        return string.Join('\n', buffer);
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
