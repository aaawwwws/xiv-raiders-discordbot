using Discord;

namespace RaidDcBot.Bot;

public static class VoiceNotificationRolePanel
{
    public const string PanelTitle = "VC通知ロール";
    public const string PanelMessage = PanelTitle;
    public const string ToggleCustomId = "voice_notify_role_toggle";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("通話通知ロール ON/OFF", ToggleCustomId, ButtonStyle.Primary)
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
