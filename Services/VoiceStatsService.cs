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

    public async Task<Embed> BuildReactionPostRankingEmbedAsync(
        SocketGuild guild,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int topCount,
        string title) {
        if (toUtc <= fromUtc) {
            toUtc = fromUtc.AddMinutes(1);
        }

        var rows = await _db.ListTopReactedPostsAsync(guild.Id, fromUtc, toUtc, topCount);
        var fromJst = fromUtc.ToOffset(TimeSpan.FromHours(9));
        var toJst = toUtc.ToOffset(TimeSpan.FromHours(9));
        var period = $"{fromJst:MM/dd HH:mm} - {toJst:MM/dd HH:mm} (JST)";
        var embedTitle = ResolveReactionRankingTitle(title, fromJst, toJst);

        var totalReactions = Math.Max(0, rows.Sum(x => x.ReactionCount));
        var lines = rows
            .Select((row, index) => BuildReactionPostLine(guild.Id, row, index + 1))
            .ToList();
        var description = JoinWithinLimit(lines, 3800);

        var builder = new EmbedBuilder()
            .WithTitle(embedTitle)
            .WithColor(new Color(241, 196, 15))
            .WithCurrentTimestamp()
            .WithDescription(
                "📅 **集計期間**\n" +
                $"`{period}`\n" +
                $"📝 **対象投稿数**: `{rows.Count}件`\n" +
                $"👏 **リアクション合計**: `{totalReactions}件`")
            .AddField("👏 投稿 TOP", description);

        builder.WithFooter("Raid DC Activity");
        return builder.Build();
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
        var reactionRows = await _db.ListReactionCountsByChannelAsync(guild.Id, fromUtc, toUtc);
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

        var nonAdminReactionCounts = new Dictionary<ulong, int>();
        foreach (var row in reactionRows) {
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
                nonAdminReactionCounts[userId] = nonAdminReactionCounts.TryGetValue(userId, out var prev)
                    ? prev + row.ReactionCount
                    : row.ReactionCount;
            }
        }

        var vcTop = voiceRows
            .Take(Math.Clamp(topCount, 1, 20))
            .ToList();
        var totalVcSeconds = Math.Max(0, voiceRows.Sum(x => x.DurationSeconds));
        var messageTop = nonAdminMessageCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(Math.Clamp(topCount, 1, 20))
            .ToList();
        var reactionTop = nonAdminReactionCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(Math.Clamp(topCount, 1, 20))
            .ToList();
        var totalMessages = Math.Max(0, nonAdminMessageCounts.Values.Sum());
        var totalReactions = Math.Max(0, nonAdminReactionCounts.Values.Sum());

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
                $"👏 **リアクション合計**: `{totalReactions}件`");

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

        var reactionLines = new List<string>();
        for (var i = 0; i < reactionTop.Count; i++) {
            var row = reactionTop[i];
            var display = guild.GetUser(row.Key)?.DisplayName ?? row.Key.ToString();
            reactionLines.Add($"{ToRankBadge(i + 1)} **{display}**  `{row.Value}件`");
        }

        if (reactionLines.Count == 0) {
            reactionLines.Add("😴 データなし");
        }

        builder.AddField("🎙️ VC滞在 TOP", string.Join('\n', vcLines));
        builder.AddField("💬 発言数 TOP", string.Join('\n', messageLines));
        builder.AddField("👏 リアクション TOP", string.Join('\n', reactionLines));
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

    private static string ResolveReactionRankingTitle(string rawTitle, DateTimeOffset fromJst, DateTimeOffset toJst)
    {
        if (rawTitle.Contains("今日", StringComparison.Ordinal))
        {
            return $"{toJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}のリアクション獲得ランキング";
        }

        if (rawTitle.Contains("週", StringComparison.Ordinal))
        {
            return $"{fromJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}-{toJst.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}のリアクション獲得ランキング";
        }

        return rawTitle;
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

    private static string BuildReactionPostLine(ulong guildId, ReactionPostSummary row, int rank)
    {
        var channelId = unchecked((ulong)row.ChannelId);
        var messageId = unchecked((ulong)row.MessageId);
        var link = BuildMessageLink(guildId, channelId, messageId);
        var author = row.AuthorId.HasValue ? $"<@{unchecked((ulong)row.AuthorId.Value)}>" : "(不明)";
        var deleted = row.DeletedAt.HasValue ? " / 削除済み" : string.Empty;
        return $"{ToRankBadge(rank)} [投稿]({link}) <#{channelId}> | 👍 `{row.ReactionCount}` (👥{row.ReactorCount}) | 投稿者: {author}{deleted}";
    }

    private static string JoinWithinLimit(IEnumerable<string> lines, int maxLength)
    {
        var buffer = new List<string>();
        var total = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var extra = (buffer.Count == 0 ? 0 : 1) + line.Length;
            if (total + extra > maxLength)
            {
                break;
            }

            buffer.Add(line);
            total += extra;
        }

        return buffer.Count == 0 ? "😴 データなし" : string.Join('\n', buffer);
    }

    private static string BuildMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

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
