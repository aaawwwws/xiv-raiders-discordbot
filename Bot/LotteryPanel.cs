using System.Text;
using Discord;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

public static class LotteryPanel
{
    public const string ManageCreateCustomId = "lottery_manage_create";
    public const string ManageRefreshCustomId = "lottery_manage_refresh";
    public const string ManageControlCustomId = "lottery_manage_control";
    public const string ManagePickCustomId = "lottery_manage_pick";
    public const string ManageBackCustomId = "lottery_manage_back";
    public const string CreateModalCustomId = "lottery_create_modal";

    public const string JoinCustomIdPrefix = "lottery_join:";
    public const string LeaveCustomIdPrefix = "lottery_leave:";
    public const string RefreshCustomIdPrefix = "lottery_refresh:";
    public const string DrawCustomIdPrefix = "lottery_draw:";
    public const string RedrawCustomIdPrefix = "lottery_redraw:";
    public const string CloseCustomIdPrefix = "lottery_close:";
    public const string CancelCustomIdPrefix = "lottery_cancel:";
    public const string AdminConfirmActionCustomIdPrefix = "lottery_admin_confirm:";
    public const string ClaimPrizeCustomIdPrefix = "lottery_claim:";

    public static Embed BuildManagementEmbed(IReadOnlyList<Lottery> recent)
    {
        var lines = recent.Count == 0
            ? "なし"
            : JoinWithinLimit(recent.Select(item =>
            {
                var status = ToStatusLabel(item.Status);
                var channel = item.ChannelId > 0 ? $"<#{ToUlong(item.ChannelId)}>" : "(unknown)";
                return $"#{item.Id} {status} {channel} {TruncateSingleLine(item.Title, 24)}";
            }), 900);

        return new EmbedBuilder()
            .WithTitle("抽選管理")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .AddField("操作", "送信先チャンネルを選択して作成 / 募集更新", false)
            .AddField("最近の抽選", lines, false)
            .Build();
    }

    public static MessageComponent BuildManagementComponents()
    {
        return new ComponentBuilder()
            .WithButton("抽選を作成", ManageCreateCustomId, ButtonStyle.Success, row: 0)
            .WithButton("管理", ManageControlCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("再表示", ManageRefreshCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    public static Embed BuildManagementSelectionEmbed(IReadOnlyList<Lottery> lotteries)
    {
        var lines = lotteries.Count == 0
            ? "なし"
            : JoinWithinLimit(lotteries.Select(item =>
            {
                var status = ToStatusLabel(item.Status);
                return $"#{item.Id} {status} {TruncateSingleLine(item.Title, 32)}";
            }), 900);

        return new EmbedBuilder()
            .WithTitle("抽選管理: 対象選択")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .AddField("抽選一覧", lines, false)
            .Build();
    }

    public static MessageComponent BuildManagementSelectionComponents(IReadOnlyList<Lottery> lotteries)
    {
        var builder = new ComponentBuilder();
        if (lotteries.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(ManagePickCustomId)
                .WithPlaceholder("管理する抽選を選択")
                .WithMinValues(1)
                .WithMaxValues(1);
            foreach (var lottery in lotteries.Take(25))
            {
                menu.AddOption(
                    $"#{lottery.Id} {TruncateSingleLine(lottery.Title, 40)}",
                    lottery.Id.ToString(),
                    ToStatusLabel(lottery.Status));
            }

            builder.WithSelectMenu(menu, row: 0);
        }

        builder.WithButton("戻る", ManageBackCustomId, ButtonStyle.Secondary, row: 1);
        return builder.Build();
    }

    public static Embed BuildLotteryEmbed(
        Lottery lottery,
        IReadOnlyList<LotteryEntry> entries,
        IReadOnlyList<LotteryWinner> winners,
        DateTimeOffset nowUtc)
    {
        var expired = lottery.CloseAt <= nowUtc;
        var effectiveStatus = LotteryStatus.IsOpen(lottery.Status) && expired
            ? LotteryStatus.Closed
            : LotteryStatus.Normalize(lottery.Status);
        var statusLabel = ToStatusLabel(effectiveStatus);
        var statusColor = ToStatusColor(effectiveStatus);
        var closeAt = lottery.CloseAt.ToUnixTimeSeconds();
        var description = string.IsNullOrWhiteSpace(lottery.Description)
            ? "-"
            : lottery.Description!.Trim();
        var prizes = ParsePrizeList(lottery.PrizeList);
        var prizeLines = prizes.Count == 0
            ? "なし"
            : JoinWithinLimit(prizes.Select((prize, index) => $"{index + 1}. {prize}"), 900);
        var winnerLines = winners.Count == 0
            ? "未抽選"
            : JoinWithinLimit(winners
                .OrderBy(winner => winner.WinnerOrder)
                .Select((winner, index) =>
                {
                    var suffix = index < prizes.Count ? $" / {prizes[index]}" : string.Empty;
                    return $"{index + 1}. <@{ToUlong(winner.UserId)}>{suffix}";
                }), 900);

        var builder = new EmbedBuilder()
            .WithTitle($"🎯 {lottery.Title}")
            .WithColor(statusColor)
            .WithCurrentTimestamp()
            .AddField("状態", statusLabel, true)
            .AddField("当選人数", lottery.WinnerCount.ToString(), true)
            .AddField("締切", $"<t:{closeAt}:F>\n<t:{closeAt}:R>", true)
            .AddField("応募数", entries.Count.ToString(), true)
            .AddField("景品", prizeLines, false)
            .AddField("説明", description, false)
            .AddField("当選者", winnerLines, false);

        if (lottery.DrawnAt.HasValue)
        {
            builder.AddField("抽選実行", $"<t:{lottery.DrawnAt.Value.ToUnixTimeSeconds()}:F>", true);
        }

        if (lottery.CanceledAt.HasValue)
        {
            builder.AddField("中止", $"<t:{lottery.CanceledAt.Value.ToUnixTimeSeconds()}:F>", true);
        }

        return builder.Build();
    }

    public static Embed BuildLotteryAdminEmbed(
        Lottery lottery,
        IReadOnlyList<LotteryEntry> entries,
        IReadOnlyList<LotteryWinner> winners,
        DateTimeOffset nowUtc)
    {
        var expired = lottery.CloseAt <= nowUtc;
        var effectiveStatus = LotteryStatus.IsOpen(lottery.Status) && expired
            ? LotteryStatus.Closed
            : LotteryStatus.Normalize(lottery.Status);
        var statusLabel = ToStatusLabel(effectiveStatus);
        var statusColor = ToStatusColor(effectiveStatus);
        var closeAt = lottery.CloseAt.ToUnixTimeSeconds();
        var description = string.IsNullOrWhiteSpace(lottery.Description)
            ? "-"
            : lottery.Description!.Trim();
        var prizes = ParsePrizeList(lottery.PrizeList);
        var prizeLines = prizes.Count == 0
            ? "なし"
            : JoinWithinLimit(prizes.Select((prize, index) => $"{index + 1}. {prize}"), 900);
        var entryLines = entries.Count == 0
            ? "なし"
            : JoinWithinLimit(entries
                .OrderBy(entry => entry.JoinedAt)
                .ThenBy(entry => entry.Id)
                .Select((entry, index) => $"{index + 1}. <@{ToUlong(entry.UserId)}>"), 900);
        var winnerLines = winners.Count == 0
            ? "未抽選"
            : JoinWithinLimit(winners
                .OrderBy(winner => winner.WinnerOrder)
                .ThenBy(winner => winner.Id)
                .Select((winner, index) =>
                {
                    var suffix = index < prizes.Count ? $" / {prizes[index]}" : string.Empty;
                    return $"{index + 1}. <@{ToUlong(winner.UserId)}>{suffix}";
                }), 900);

        var builder = new EmbedBuilder()
            .WithTitle($"🎯 {lottery.Title}")
            .WithColor(statusColor)
            .WithCurrentTimestamp()
            .AddField("状態", statusLabel, true)
            .AddField("当選人数", lottery.WinnerCount.ToString(), true)
            .AddField("締切", $"<t:{closeAt}:F>\n<t:{closeAt}:R>", true)
            .AddField("応募数", entries.Count.ToString(), true)
            .AddField("景品", prizeLines, false)
            .AddField("説明", description, false)
            .AddField("応募者", entryLines, false)
            .AddField("当選者", winnerLines, false);

        if (lottery.DrawnAt.HasValue)
        {
            builder.AddField("抽選実行", $"<t:{lottery.DrawnAt.Value.ToUnixTimeSeconds()}:F>", true);
        }

        if (lottery.CanceledAt.HasValue)
        {
            builder.AddField("中止", $"<t:{lottery.CanceledAt.Value.ToUnixTimeSeconds()}:F>", true);
        }

        return builder.Build();
    }

    public static Embed BuildLotteryResultEmbed(
        Lottery lottery,
        IReadOnlyList<LotteryEntry> entries,
        IReadOnlyList<LotteryWinner> winners,
        DateTimeOffset nowUtc)
    {
        var expired = lottery.CloseAt <= nowUtc;
        var effectiveStatus = LotteryStatus.IsOpen(lottery.Status) && expired
            ? LotteryStatus.Closed
            : LotteryStatus.Normalize(lottery.Status);
        var statusLabel = ToStatusLabel(effectiveStatus);
        var statusColor = ToStatusColor(effectiveStatus);
        var closeAt = lottery.CloseAt.ToUnixTimeSeconds();
        var prizes = ParsePrizeList(lottery.PrizeList);
        var entryLines = entries.Count == 0
            ? "なし"
            : JoinWithinLimit(entries
                .OrderBy(entry => entry.JoinedAt)
                .ThenBy(entry => entry.Id)
                .Select((entry, index) => $"{index + 1}. <@{ToUlong(entry.UserId)}>"), 900);
        var winnerLines = winners.Count == 0
            ? "なし"
            : JoinWithinLimit(winners
                .OrderBy(winner => winner.WinnerOrder)
                .ThenBy(winner => winner.Id)
                .Select((winner, index) =>
                {
                    var suffix = index < prizes.Count ? $" / {prizes[index]}" : string.Empty;
                    return $"{index + 1}. <@{ToUlong(winner.UserId)}>{suffix}";
                }), 900);

        return new EmbedBuilder()
            .WithTitle($"📣 抽選終了: {lottery.Title}")
            .WithColor(statusColor)
            .WithCurrentTimestamp()
            .AddField("状態", statusLabel, true)
            .AddField("応募数", entries.Count.ToString(), true)
            .AddField("当選数", winners.Count.ToString(), true)
            .AddField("締切", $"<t:{closeAt}:F>", true)
            .AddField("応募者", entryLines, false)
            .AddField("当選者", winnerLines, false)
            .Build();
    }

    public static string? BuildLotteryWinnerAnnouncementText(
        Lottery lottery,
        IReadOnlyList<LotteryWinner> winners)
    {
        if (winners.Count == 0)
        {
            return null;
        }

        var prizes = ParsePrizeList(lottery.PrizeList);
        var lines = JoinWithinLimit(winners
            .OrderBy(winner => winner.WinnerOrder)
            .ThenBy(winner => winner.Id)
            .Select((winner, index) =>
            {
                var prize = index < prizes.Count ? prizes[index] : "景品";
                return $"<@{ToUlong(winner.UserId)}> / {prize}";
            }), 1800);
        if (string.Equals(lines, "なし", StringComparison.Ordinal))
        {
            return null;
        }

        return $"🎉 おめでとうございます！\n当選者\n{lines}";
    }

    public static MessageComponent BuildLotteryResultComponents(Lottery lottery, int winnerCount)
    {
        var idText = lottery.Id.ToString();
        return new ComponentBuilder()
            .WithButton(
                "景品を受け取る",
                $"{ClaimPrizeCustomIdPrefix}{idText}",
                ButtonStyle.Success,
                disabled: winnerCount <= 0,
                row: 0)
            .Build();
    }

    public static MessageComponent BuildLotteryComponents(
        Lottery lottery,
        int entryCount,
        int winnerCount,
        DateTimeOffset nowUtc)
    {
        var expired = lottery.CloseAt <= nowUtc;
        var effectiveStatus = LotteryStatus.IsOpen(lottery.Status) && expired
            ? LotteryStatus.Closed
            : LotteryStatus.Normalize(lottery.Status);
        var openForEntry = LotteryStatus.IsOpen(effectiveStatus);
        var canDraw = !LotteryStatus.IsCanceled(effectiveStatus) &&
                      !LotteryStatus.IsDrawn(effectiveStatus) &&
                      entryCount > 0;
        var canRedraw = !LotteryStatus.IsCanceled(effectiveStatus) &&
                        LotteryStatus.IsDrawn(effectiveStatus) &&
                        entryCount > 0 &&
                        winnerCount > 0;
        var canClose = LotteryStatus.IsOpen(effectiveStatus);
        var canCancel = !LotteryStatus.IsCanceled(effectiveStatus);
        var idText = lottery.Id.ToString();

        return new ComponentBuilder()
            .WithButton("応募する", $"{JoinCustomIdPrefix}{idText}", ButtonStyle.Success, disabled: !openForEntry, row: 0)
            .WithButton("辞退する", $"{LeaveCustomIdPrefix}{idText}", ButtonStyle.Secondary, disabled: !openForEntry, row: 0)
            .Build();
    }

    public static MessageComponent BuildLotteryAdminComponents(
        Lottery lottery,
        int entryCount,
        int winnerCount,
        DateTimeOffset nowUtc)
    {
        var expired = lottery.CloseAt <= nowUtc;
        var effectiveStatus = LotteryStatus.IsOpen(lottery.Status) && expired
            ? LotteryStatus.Closed
            : LotteryStatus.Normalize(lottery.Status);
        var canDraw = !LotteryStatus.IsCanceled(effectiveStatus) &&
                      !LotteryStatus.IsDrawn(effectiveStatus) &&
                      entryCount > 0;
        var canRedraw = !LotteryStatus.IsCanceled(effectiveStatus) &&
                        LotteryStatus.IsDrawn(effectiveStatus) &&
                        entryCount > 0 &&
                        winnerCount > 0;
        var canClose = LotteryStatus.IsOpen(effectiveStatus);
        var canCancel = !LotteryStatus.IsCanceled(effectiveStatus);
        var idText = lottery.Id.ToString();

        return new ComponentBuilder()
            .WithButton("更新", $"{RefreshCustomIdPrefix}{idText}", ButtonStyle.Secondary, row: 0)
            .WithButton("抽選実行", $"{AdminConfirmActionCustomIdPrefix}draw|{idText}", ButtonStyle.Primary, disabled: !canDraw, row: 1)
            .WithButton("再抽選", $"{AdminConfirmActionCustomIdPrefix}redraw|{idText}", ButtonStyle.Primary, disabled: !canRedraw, row: 1)
            .WithButton("締切", $"{AdminConfirmActionCustomIdPrefix}close|{idText}", ButtonStyle.Secondary, disabled: !canClose, row: 1)
            .WithButton("中止", $"{AdminConfirmActionCustomIdPrefix}cancel|{idText}", ButtonStyle.Danger, disabled: !canCancel, row: 1)
            .WithButton("一覧へ戻る", ManageControlCustomId, ButtonStyle.Secondary, row: 2)
            .Build();
    }

    public static string ToStatusLabel(string? status)
    {
        var normalized = LotteryStatus.Normalize(status);
        if (LotteryStatus.IsOpen(normalized)) return "🟢 募集中";
        if (LotteryStatus.IsClosed(normalized)) return "🟠 締切";
        if (LotteryStatus.IsDrawn(normalized)) return "🔵 抽選済み";
        if (LotteryStatus.IsCanceled(normalized)) return "⚫ 中止";
        return normalized;
    }

    private static Color ToStatusColor(string? status)
    {
        var normalized = LotteryStatus.Normalize(status);
        if (LotteryStatus.IsOpen(normalized)) return Color.Green;
        if (LotteryStatus.IsClosed(normalized)) return Color.Orange;
        if (LotteryStatus.IsDrawn(normalized)) return Color.Blue;
        if (LotteryStatus.IsCanceled(normalized)) return Color.DarkGrey;
        return Color.LightGrey;
    }

    private static string JoinWithinLimit(IEnumerable<string> lines, int maxLength)
    {
        var sb = new StringBuilder(capacity: Math.Max(128, maxLength));
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (sb.Length > 0 && sb.Length + 1 + line.Length > maxLength)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, Math.Max(1, maxLength - 3)) + "...";
    }

    private static IReadOnlyList<string> ParsePrizeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
