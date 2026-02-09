using Discord;

namespace RaidDcBot.Bot;

public static class LodestonePanel
{
    public const string PanelTitle = "Lodestone連携パネル（任意）";
    public const string LegacyPanelTitle = "Lodestone連携パネル";
    public const string PanelMessage = PanelTitle;
    public const string IssueCustomId = "lodestone_link_issue";
    public const string VerifyCustomId = "lodestone_verify_button";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("連携コードを発行", IssueCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("連携確認", VerifyCustomId, ButtonStyle.Success, row: 0)
            .Build();
    }

    public static bool LooksLikePanelMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.StartsWith(PanelTitle, StringComparison.Ordinal) ||
               content.StartsWith(LegacyPanelTitle, StringComparison.Ordinal);
    }
}
