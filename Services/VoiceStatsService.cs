using Discord;
using Discord.WebSocket;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Npgsql;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class VoiceStatsService {
    private readonly Db _db;
    private readonly ILogger<VoiceStatsService> _logger;
    private static readonly TimeSpan RankingDbRetryDelayBase = TimeSpan.FromMilliseconds(300);
    private const int RankingDbMaxAttempts = 3;
    public const string RankingMetricVoiceDuration = "voice_duration";
    public const string RankingMetricMessageCount = "message_count";

    public VoiceStatsService(Db db, ILogger<VoiceStatsService> logger) {
        _db = db;
        _logger = logger;
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
        string title,
        bool recordAwards = true) {
        if (toUtc <= fromUtc) {
            toUtc = fromUtc.AddMinutes(1);
        }

        var topLimit = Math.Clamp(topCount, 1, 10);
        var voiceRows = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListVoiceDurationSummaryAsync),
            () => _db.ListVoiceDurationSummaryAsync(guild.Id, fromUtc, toUtc, topLimit));
        var messageRows = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListMessageCountsByChannelAsync),
            () => _db.ListMessageCountsByChannelAsync(guild.Id, fromUtc, toUtc));
        var reactionGivenRows = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListReactionCountsByChannelAsync),
            () => _db.ListReactionCountsByChannelAsync(guild.Id, fromUtc, toUtc));
        var reactionReceivedRows = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListReactionReceivedCountsByChannelAsync),
            () => _db.ListReactionReceivedCountsByChannelAsync(guild.Id, fromUtc, toUtc));
        var reactionReceivedTopPostRows = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListTopReactionReceivedPostsByUserAsync),
            () => _db.ListTopReactionReceivedPostsByUserAsync(guild.Id, fromUtc, toUtc));
        var activeUsers = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.ListActiveUserIdsSinceAsync),
            () => _db.ListActiveUserIdsSinceAsync(guild.Id, fromUtc));

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
                if (IsVoiceTextMessageChannel(guild, channelId)) {
                    continue;
                }

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
        var totalVcSeconds = await ExecuteRankingDbQueryWithRetryAsync(
            guild.Id,
            nameof(Db.SummarizeVoiceDurationByGuildAsync),
            () => _db.SummarizeVoiceDurationByGuildAsync(guild.Id, fromUtc, toUtc));
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
        var bestRanksByUser = new Dictionary<ulong, int>();
        for (var i = 0; i < vcTop.Count; i++)
        {
            UpdateBestRank(bestRanksByUser, unchecked((ulong)vcTop[i].UserId), i + 1);
        }
        for (var i = 0; i < messageTop.Count; i++)
        {
            UpdateBestRank(bestRanksByUser, messageTop[i].Key, i + 1);
        }
        for (var i = 0; i < reactionGivenTop.Count; i++)
        {
            UpdateBestRank(bestRanksByUser, reactionGivenTop[i].Key, i + 1);
        }
        for (var i = 0; i < reactionReceivedTop.Count; i++)
        {
            UpdateBestRank(bestRanksByUser, reactionReceivedTop[i].Key, i + 1);
        }
        var totalMessages = Math.Max(0, nonAdminMessageCounts.Values.Sum());
        var reactorUserCount = nonAdminReactionGivenCounts.Count;
        var reactedUserCount = nonAdminReactionReceivedCounts.Count;
        var includeAllTimeVoiceTotal = title.Contains("今日", StringComparison.Ordinal);
        var allTimeVoiceTotalSeconds = includeAllTimeVoiceTotal
            ? await ExecuteRankingDbQueryWithRetryAsync(
                guild.Id,
                nameof(Db.SummarizeVoiceDurationByGuildAsync),
                () => _db.SummarizeVoiceDurationByGuildAsync(guild.Id, DateTimeOffset.UnixEpoch, toUtc))
            : 0L;

        var fromJst = fromUtc.ToOffset(TimeSpan.FromHours(9));
        var toJst = toUtc.ToOffset(TimeSpan.FromHours(9));
        var rankingAwardKey = ResolveRankingAwardKey(title, fromJst, toJst);
        if (recordAwards && !string.IsNullOrWhiteSpace(rankingAwardKey))
        {
            try
            {
                var rankedUserIds = bestRanksByUser.Keys.ToArray();
                if (rankedUserIds.Length > 0)
                {
                    await _db.RecordActivityRankingAwardsAsync(guild.Id, rankingAwardKey, rankedUserIds, bestRanksByUser);
                }

                var vcRankedUserIds = vcTop
                    .Select(row => unchecked((ulong)row.UserId))
                    .Where(id => id != 0)
                    .Distinct()
                    .ToArray();
                if (vcRankedUserIds.Length > 0)
                {
                    await _db.RecordActivityRankingMetricAwardsAsync(
                        guild.Id,
                        rankingAwardKey,
                        RankingMetricVoiceDuration,
                        vcRankedUserIds);
                }

                var messageRankedUserIds = messageTop
                    .Select(row => row.Key)
                    .Where(id => id != 0)
                    .Distinct()
                    .ToArray();
                if (messageRankedUserIds.Length > 0)
                {
                    await _db.RecordActivityRankingMetricAwardsAsync(
                        guild.Id,
                        rankingAwardKey,
                        RankingMetricMessageCount,
                        messageRankedUserIds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record activity ranking awards. GuildId={GuildId}, Key={RankingAwardKey}", guild.Id, rankingAwardKey);
            }
        }

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
                $"🎙️ **VC稼働時間**: `{FormatDuration(totalVcSeconds)}`\n" +
                (includeAllTimeVoiceTotal ? $"🗃️ **VC累計(全期間)**: `{FormatDuration(allTimeVoiceTotalSeconds)}`\n" : string.Empty) +
                $"💬 **発言合計**: `{totalMessages}件`\n" +
                $"👏 **リアクションした人数**: `{reactorUserCount}人`\n" +
                $"🎯 **リアクションされた人数**: `{reactedUserCount}人`");

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
        builder.AddField("🎯 リアクション獲得数 TOP", JoinLinesWithinLimit(reactionReceivedLines));
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

    private static string? ResolveRankingAwardKey(string rawTitle, DateTimeOffset fromJst, DateTimeOffset toJst)
    {
        if (rawTitle.Contains("今日", StringComparison.Ordinal))
        {
            return $"daily:{toJst:yyyyMMdd}";
        }

        if (rawTitle.Contains("週", StringComparison.Ordinal))
        {
            return $"weekly:{fromJst:yyyyMMdd}-{toJst:yyyyMMdd}";
        }

        return null;
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

    public static string FormatDuration(long durationSeconds) {
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

    private async Task<T> ExecuteRankingDbQueryWithRetryAsync<T>(
        ulong guildId,
        string operation,
        Func<Task<T>> query)
    {
        for (var attempt = 1; attempt <= RankingDbMaxAttempts; attempt++)
        {
            try
            {
                return await query();
            }
            catch (Exception ex) when (attempt < RankingDbMaxAttempts && IsTransientRankingDbException(ex))
            {
                var delay = TimeSpan.FromMilliseconds(RankingDbRetryDelayBase.TotalMilliseconds * attempt);
                _logger.LogWarning(
                    ex,
                    "Transient DB error while building activity ranking. GuildId={GuildId}, Operation={Operation}, Attempt={Attempt}/{MaxAttempts}. Retrying after {DelayMs}ms.",
                    guildId,
                    operation,
                    attempt,
                    RankingDbMaxAttempts,
                    (int)Math.Round(delay.TotalMilliseconds));
                await Task.Delay(delay);
            }
        }

        throw new InvalidOperationException("Unexpected retry flow exit in activity ranking DB query.");
    }

    private static bool IsTransientRankingDbException(Exception ex)
    {
        if (ex is TimeoutException or IOException or SocketException)
        {
            return true;
        }

        if (ex is NpgsqlException npgsqlEx)
        {
            if (npgsqlEx.IsTransient)
            {
                return true;
            }

            if (npgsqlEx.Message.Contains("Exception while reading from stream", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex.InnerException is not null)
        {
            return IsTransientRankingDbException(ex.InnerException);
        }

        return false;
    }

    private static void UpdateBestRank(IDictionary<ulong, int> bestRanksByUser, ulong userId, int rank)
    {
        if (userId == 0 || rank <= 0)
        {
            return;
        }

        if (bestRanksByUser.TryGetValue(userId, out var current))
        {
            bestRanksByUser[userId] = Math.Min(current, rank);
            return;
        }

        bestRanksByUser[userId] = rank;
    }

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

    private static bool IsVoiceTextMessageChannel(SocketGuild guild, ulong channelId)
    {
        var channel = guild.GetChannel(channelId);
        if (channel is null)
        {
            return false;
        }

        if (channel is SocketVoiceChannel)
        {
            return true;
        }

        if (channel is SocketThreadChannel thread && thread.ParentChannel is SocketVoiceChannel)
        {
            return true;
        }

        return false;
    }

    public async Task AssignWeeklyRankingRolesAsync(SocketGuild guild, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        try
        {
            // 上位10位まで取得（除外対象がいる場合の繰り上げ用）
            var voiceRows = await ExecuteRankingDbQueryWithRetryAsync(
                guild.Id,
                nameof(Db.ListVoiceDurationSummaryAsync),
                () => _db.ListVoiceDurationSummaryAsync(guild.Id, fromUtc, toUtc, 10));

            // 除外対象ロールを持つ人をフィルタリング
            var excludedRoles = new[] { "管理者", "オフィサー", "運営" };
            var filteredTop = voiceRows
                .Where(row => {
                    var user = guild.GetUser(unchecked((ulong)row.UserId));
                    if (user == null) return false;
                    // 除外ロールを持っていないかチェック
                    return !user.Roles.Any(r => excludedRoles.Contains(r.Name));
                })
                .Take(3)
                .Select(row => unchecked((ulong)row.UserId))
                .ToList();

            var top3 = filteredTop;

            var roleConfigs = new[]
            {
                new { Rank = 1, Name = "🥇1位", Colors = new RoleColors(new Color(0xFF, 0xD7, 0x00), new Color(0xFF, 0x95, 0x00), null) },
                new { Rank = 2, Name = "🥈2位", Colors = new RoleColors(new Color(0xE0, 0xE0, 0xE0), new Color(0x80, 0x80, 0x80), null) },
                new { Rank = 3, Name = "🥉3位", Colors = new RoleColors(new Color(0xCD, 0x7F, 0x32), new Color(0x8B, 0x45, 0x13), null) }
            };

            foreach (var config in roleConfigs)
            {
                var userId = top3.ElementAtOrDefault(config.Rank - 1);

                // 既存のロールを探す
                var role = (IRole?)guild.Roles.FirstOrDefault(r => r.Name == config.Name);
                
                if (role is null)
                {
                    try
                    {
                        role = await guild.CreateRoleAsync(config.Name, isMentionable: false);
                        await role.ModifyAsync(r => r.Colors = config.Colors);
                        
                        // ロールをメンバーロールより上位に配置（@everyoneの1つ上）
                        var everyoneRole = guild.EveryoneRole;
                        if (everyoneRole != null)
                        {
                            try
                            {
                                await role.ModifyAsync(r => r.Position = everyoneRole.Position + 1);
                            }
                            catch (Exception posEx)
                            {
                                _logger.LogWarning(posEx, "Failed to move role position. GuildId={GuildId}, Role={RoleName}", guild.Id, config.Name);
                            }
                        }
                        
                        _logger.LogInformation("Created ranking role with gradient. GuildId={GuildId}, Role={RoleName}", guild.Id, config.Name);
                        // ロール作成後、Discord側で反映されるまで少し待機
                        await Task.Delay(500);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to create ranking role. GuildId={GuildId}, Role={RoleName}", guild.Id, config.Name);
                        continue;
                    }
                }

                // 既存のこのロールを持つメンバーから解除（上位者が変わった場合）
                foreach (var member in guild.Users.Where(u => u.Roles.Any(r => r.Id == role.Id)))
                {
                    if (member.Id != userId)
                    {
                        try
                        {
                            await member.RemoveRoleAsync(role);
                            _logger.LogInformation("Removed ranking role. GuildId={GuildId}, Role={RoleName}, UserId={UserId}", guild.Id, config.Name, member.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove ranking role. GuildId={GuildId}, Role={RoleName}, UserId={UserId}", guild.Id, config.Name, member.Id);
                        }
                    }
                }

                // 新しい上位者に付与（対象がいる場合のみ）
                if (userId != 0)
                {
                    var targetUser = guild.GetUser(userId);
                    if (targetUser is not null && !targetUser.Roles.Any(r => r.Id == role.Id))
                    {
                        // 最大3回リトライ
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                await targetUser.AddRoleAsync(role);
                                _logger.LogInformation("Assigned ranking role. GuildId={GuildId}, Role={RoleName}, UserId={UserId}", guild.Id, config.Name, userId);
                                break;
                            }
                            catch (Exception ex) when (attempt < 2)
                            {
                                _logger.LogWarning(ex, "Failed to assign ranking role (attempt {Attempt}/3). Retrying... GuildId={GuildId}, Role={RoleName}, UserId={UserId}", attempt + 1, guild.Id, config.Name, userId);
                                await Task.Delay(1000);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to assign ranking role after 3 attempts. GuildId={GuildId}, Role={RoleName}, UserId={UserId}", guild.Id, config.Name, userId);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to assign weekly ranking roles. GuildId={GuildId}", guild.Id);
        }
    }
}
