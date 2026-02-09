using Discord;

namespace RaidDcBot.Bot;

public static class SettingsPanel
{
    public const string PanelTitle = "設定パネル";
    public const string PanelMessage = PanelTitle;
    public const string ToggleSelfVoiceNotifyCustomId = "voice_notify_self_toggle";
    public const string ResyncPanelsCustomId = "settings_panels_resync";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("通話通知ロール ON/OFF", VoiceNotificationRolePanel.ToggleCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("自分のVC通知 ON/OFF", ToggleSelfVoiceNotifyCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("Lodestoneコード発行", LodestonePanel.IssueCustomId, ButtonStyle.Primary, row: 1)
            .WithButton("Lodestone連携確認", LodestonePanel.VerifyCustomId, ButtonStyle.Success, row: 1)
            .WithButton("パネル再同期", ResyncPanelsCustomId, ButtonStyle.Secondary, row: 2)
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
