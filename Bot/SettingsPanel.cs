using Discord;

namespace RaidDcBot.Bot;

public static class SettingsPanel
{
    public const string PanelTitle = "設定パネル";
    public const string PanelMessage = "";
    public const string ToggleSelfVoiceNotifyCustomId = "voice_notify_self_toggle";
    public const string ConfigureSelfVoiceTextAutoDeleteCustomId = "voice_text_autodelete_self_configure";
    public const string ShowSelfProfileCustomId = "settings_show_self_profile";
    public const string ShowMyPageCustomId = "settings_show_my_page";
    public const string ShowAchievementResumeCustomId = "settings_show_achievement_resume";
    public const string ChangeSelfStoryProgressionCustomId = "settings_change_self_story_progression";

    public static Embed BuildEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("⚙️ 設定パネル")
            .WithDescription("各種設定は下のボタンから変更できます。")
            .AddField("🔔 通知設定", "・通話通知ロール: 自分にメンションするVC開始通知を受け取る\n・自分のVC通知: VCに入った時に自動投稿されるミラーメッセージ", false)
            .AddField("📝 連携・プロフィール", "・Lodestone連携: キャラクター認証\n・プロフィール編集: 自己紹介・SNSリンク", false)
            .AddField("📊 確認系", "・マイページ: プロフィール・設定・活動確認\n・レイド履歴書: 絶・零式クリア状況", false)
            .WithColor(Color.Blue)
            .Build();
    }

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            // Row 0: Notification toggles
            .WithButton("🔔 通話通知ロール ON/OFF", VoiceNotificationRolePanel.ToggleCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("📣 自分のVC通知 ON/OFF", ToggleSelfVoiceNotifyCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("🗑️ VCテキスト自動削除", ConfigureSelfVoiceTextAutoDeleteCustomId, ButtonStyle.Secondary, row: 0)
            // Row 1: Lodestone
            .WithButton("📝 Lodestoneコード発行", LodestonePanel.IssueCustomId, ButtonStyle.Primary, row: 1)
            .WithButton("✅ Lodestone連携確認", LodestonePanel.VerifyCustomId, ButtonStyle.Success, row: 1)
            // Row 2: Profile / Settings
            .WithButton("👤 プロフィール編集", LodestonePanel.EditProfileCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("🌐 DC/サーバー変更", LodestonePanel.ChangeDataCenterWorldCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("📖 ストーリー進行度", ChangeSelfStoryProgressionCustomId, ButtonStyle.Secondary, row: 2)
            // Row 3: My page / Resume
            .WithButton("📊 マイページ", ShowMyPageCustomId, ButtonStyle.Success, row: 3)
            .WithButton("📜 レイド履歴書", ShowAchievementResumeCustomId, ButtonStyle.Primary, row: 3)
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
