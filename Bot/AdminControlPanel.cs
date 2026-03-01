using Discord;

namespace RaidDcBot.Bot;

public static class AdminControlPanel
{
    public const string PanelMessage = "ADMINコントロールパネル";
    public const string OpenMonitoringCategoryCustomId = "admin_category_monitoring";
    public const string OpenMemberOpsCategoryCustomId = "admin_category_member_ops";
    public const string OpenSettingsCategoryCustomId = "admin_category_settings";
    public const string OpenMaintenanceCategoryCustomId = "admin_category_maintenance";
    public const string OpenSpecialCategoryCustomId = "admin_category_special";
    public const string ExportMessageCsvCustomId = "admin_export_message_csv_7d";
    public const string PreviewInactiveKickCustomId = "admin_preview_inactive_kick";
    public const string HealthCheckCustomId = "admin_health_check";
    public const string IntegrityCheckCustomId = "admin_integrity_check";
    public const string VoiceSessionIntegrityCustomId = "admin_voice_session_integrity";
    public const string RuntimeTuningCustomId = "admin_runtime_tuning";
    public const string VoiceHourlyStatsCustomId = "admin_voice_hourly_stats";
    public const string OpsKpiCustomId = "admin_ops_kpi";
    public const string OpsKpiCohortHourlyCustomId = "admin_ops_kpi_cohort_hourly";
    public const string ModerationTimelineCustomId = "admin_moderation_timeline";
    public const string RecruitKpiCustomId = "admin_recruit_kpi";
    public const string MemberManageCustomId = "admin_member_manage";
    public const string LotteryCustomId = "admin_lottery";
    public const string InquiryDashboardCustomId = "admin_inquiry_dashboard";
    public const string RegistrationPendingListCustomId = "admin_registration_pending_list";
    public const string ToggleRegistrationApprovalCustomId = "admin_toggle_registration_approval";
    public const string ToggleInactiveAutoKickCustomId = "admin_toggle_inactive_auto_kick";
    public const string ResyncPanelsCustomId = "settings_panels_resync";

    public static MessageComponent BuildComponents(bool registrationApprovalRequired, bool inactiveAutoKickEnabled)
    {
        return new ComponentBuilder()
            .WithButton("監視/統計", OpenMonitoringCategoryCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("メンバー運用", OpenMemberOpsCategoryCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("設定/保守", OpenSettingsCategoryCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("管理機能", OpenSpecialCategoryCustomId, ButtonStyle.Success, row: 1)
            .WithButton("補助/エクスポート", OpenMaintenanceCategoryCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    public static MessageComponent BuildMonitoringCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("ヘルスチェック", HealthCheckCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("整合性チェック", IntegrityCheckCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("運用KPI", OpsKpiCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("募集KPI", RecruitKpiCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("VC時間帯統計", VoiceHourlyStatsCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("VC整合性修復", VoiceSessionIntegrityCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("新規定着コホート/時間帯", OpsKpiCohortHourlyCustomId, ButtonStyle.Primary, row: 1)
            .WithButton("モデレーション時系列", ModerationTimelineCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    public static MessageComponent BuildMemberOpsCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("メンバー管理", MemberManageCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("登録保留リスト", RegistrationPendingListCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("自動キック候補", PreviewInactiveKickCustomId, ButtonStyle.Primary, row: 0)
            .Build();
    }

    public static MessageComponent BuildSettingsCategoryComponents(bool registrationApprovalRequired, bool inactiveAutoKickEnabled)
    {
        var approvalToggleLabel = registrationApprovalRequired ? "登録確認をOFF" : "登録確認をON";
        var approvalToggleStyle = registrationApprovalRequired ? ButtonStyle.Danger : ButtonStyle.Success;
        var autoKickToggleLabel = inactiveAutoKickEnabled ? "自動キックをOFF" : "自動キックをON";
        var autoKickToggleStyle = inactiveAutoKickEnabled ? ButtonStyle.Danger : ButtonStyle.Success;

        return new ComponentBuilder()
            .WithButton(approvalToggleLabel, ToggleRegistrationApprovalCustomId, approvalToggleStyle, row: 0)
            .WithButton(autoKickToggleLabel, ToggleInactiveAutoKickCustomId, autoKickToggleStyle, row: 0)
            .WithButton("運用設定編集", RuntimeTuningCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("パネル再同期", ResyncPanelsCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    public static MessageComponent BuildSpecialCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("抽選管理", LotteryCustomId, ButtonStyle.Success, row: 0)
            .WithButton("問い合わせ管理", InquiryDashboardCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    public static MessageComponent BuildMaintenanceCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("CSV出力", ExportMessageCsvCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    public static Embed BuildStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled, bool includeReactionLabelInAutoKickNote = false)
    {
        var approvalState = approvalRequired ? "ON（管理者確認あり）" : "OFF（即時付与）";
        var approvalDetail = approvalRequired
            ? "登録申請は確認チャンネルで管理者が許可/拒否します。"
            : "登録申請時にメンバーロールを即時で付与します。";
        var autoKickState = inactiveAutoKickEnabled ? "ON" : "OFF";
        var autoKickDetail = inactiveAutoKickEnabled
            ? includeReactionLabelInAutoKickNote
                ? "毎日 00:00 (JST) に30日非アクティブを判定して自動キックします。（判定対象: メッセージ / VC / リアクション）"
                : "毎日 00:00 (JST) に30日非アクティブを判定して自動キックします。"
            : "30日非アクティブ自動キックは無効です。";

        return new EmbedBuilder()
            .WithTitle("ADMIN設定パネル")
            .WithDescription("状態確認はこのEmbed、操作はカテゴリボタン -> サブメニューで実行します。")
            .WithColor(inactiveAutoKickEnabled ? Color.Orange : Color.Green)
            .WithCurrentTimestamp()
            .AddField("登録確認フロー", approvalState, true)
            .AddField("30日非アクティブ自動キック", autoKickState, true)
            .AddField("登録確認の動作", approvalDetail, false)
            .AddField("自動キックの動作", autoKickDetail, false)
            .AddField("メニュー分類", "監視/統計 | メンバー運用 | 設定/保守 | 管理機能 | 補助/エクスポート", false)
            .AddField("ボタン色の意味", "緑: 有効化/開始  |  赤: 無効化/停止  |  灰: 設定/診断/補助", false)
            .Build();
    }
}
