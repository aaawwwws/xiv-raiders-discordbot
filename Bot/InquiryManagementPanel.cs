using Discord;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

public static class InquiryManagementPanel
{
    public const string ChannelName = "お問い合わせ-管理";
    public const string RefreshCustomId = "admin_inquiry_list_refresh";
    public const string PickCustomId = "admin_inquiry_list_pick";

    public static Embed BuildSummaryEmbed(IReadOnlyList<InquiryTicket> tickets)
    {
        var openCount = tickets.Count(ticket => !InquiryTicketStatus.IsClosed(ticket.Status));
        var latestTicketNo = tickets.Count == 0 ? 0 : tickets.Max(ticket => ticket.Id);
        var latestTicketText = latestTicketNo <= 0 ? "なし" : $"#{latestTicketNo}（{latestTicketNo}件目）";
        var lines = tickets.Select(BuildLine);

        return new EmbedBuilder()
            .WithTitle("お問い合わせ管理")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .AddField("最新受付", latestTicketText, true)
            .AddField("件数", $"オープン:{openCount}", false)
            .AddField("チケット", JoinWithinLimit(lines, 980), false)
            .Build();
    }

    public static MessageComponent BuildComponents(IReadOnlyList<InquiryTicket> tickets)
    {
        var builder = new ComponentBuilder();

        if (tickets.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(PickCustomId)
                .WithPlaceholder("お問い合わせを選択")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var ticket in tickets.Take(25))
            {
                var label = $"#{ticket.Id}（{ticket.Id}件目）";
                var description = TruncateSingleLine(ticket.Subject ?? "未指定", 80);
                menu.AddOption(label, ticket.Id.ToString(), description);
            }

            builder.WithSelectMenu(menu, row: 0);
        }

        return builder.Build();
    }

    public static bool LooksLikeDashboardMessage(IUserMessage message)
    {
        return message.Embeds.Any(embed =>
            string.Equals(embed.Title, "お問い合わせ管理", StringComparison.Ordinal));
    }

    private static string BuildLine(InquiryTicket ticket)
    {
        var subject = TruncateSingleLine(string.IsNullOrWhiteSpace(ticket.Subject) ? "未指定" : ticket.Subject, 24);
        return $"#{ticket.Id}（{ticket.Id}件目） <#{ToUlong(ticket.ChannelId)}> 件名:{subject}";
    }

    private static string JoinWithinLimit(IEnumerable<string> lines, int maxLength)
    {
        var sb = new System.Text.StringBuilder(capacity: Math.Max(128, maxLength));
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

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
