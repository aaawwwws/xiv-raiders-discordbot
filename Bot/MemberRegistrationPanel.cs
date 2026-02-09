using Discord;

namespace RaidDcBot.Bot;

public static class MemberRegistrationPanel
{
    public const string PanelTitle = "メンバー登録パネル";
    public const string PanelMessage = PanelTitle;

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("メンバー登録", "member_registration_start", ButtonStyle.Primary)
            .Build();
    }

    public static bool LooksLikePanelMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.StartsWith(PanelTitle, StringComparison.Ordinal);
    }
}
