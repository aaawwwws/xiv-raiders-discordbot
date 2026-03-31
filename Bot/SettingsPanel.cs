using Discord;

namespace RaidDcBot.Bot;

public static class SettingsPanel
{
    public const string PanelTitle = "設定パネル";
    public const string PanelMessage = PanelTitle;
    public const string ToggleSelfVoiceNotifyCustomId = "voice_notify_self_toggle";
    public const string ConfigureSelfVoiceTextAutoDeleteCustomId = "voice_text_autodelete_self_configure";
    public const string ShowSelfStatusCustomId = "settings_show_self_status";
    public const string ShowSelfProfileCustomId = "settings_show_self_profile";
    public const string ShowMyPageCustomId = "settings_show_my_page";
    public const string ShowAchievementResumeCustomId = "settings_show_achievement_resume";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("通話通知ロール ON/OFF", VoiceNotificationRolePanel.ToggleCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("自分のVC通知 ON/OFF", ToggleSelfVoiceNotifyCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("VCテキスト自動削除設定", ConfigureSelfVoiceTextAutoDeleteCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("Lodestoneコード発行", LodestonePanel.IssueCustomId, ButtonStyle.Primary, row: 1)
            .WithButton("Lodestone連携確認", LodestonePanel.VerifyCustomId, ButtonStyle.Success, row: 1)
            .WithButton("プロフィール編集", LodestonePanel.EditProfileCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("DC/サーバー変更", LodestonePanel.ChangeDataCenterWorldCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("現在の設定を確認", ShowSelfStatusCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("マイページ", ShowMyPageCustomId, ButtonStyle.Success, row: 3)
            .WithButton("レイド履歴書", ShowAchievementResumeCustomId, ButtonStyle.Primary, row: 3)
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
