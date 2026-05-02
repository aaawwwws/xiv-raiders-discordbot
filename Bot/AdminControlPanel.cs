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
    public const string ToggleSleepKickEnabledCustomId = "admin_toggle_sleep_kick_enabled";
    public const string ToggleSleepKickAutoDetectCustomId = "admin_toggle_sleep_kick_auto_detect";
    public const string ToggleSleepKickAdminConfirmCustomId = "admin_toggle_sleep_kick_admin_confirm";
    public const string ResyncPanelsCustomId = "settings_panels_resync";
    public const string OpenContentRoleCategoryCustomId = "admin_category_content_role";
    public const string ContentRoleListCustomId = "admin_content_role_list";
    public const string ContentRoleAddCustomId = "admin_content_role_add";
    public const string ContentRoleRemoveSelectCustomId = "admin_content_role_remove_select";
    public const string ContentRoleResyncCustomId = "admin_content_role_resync";
    public const string ContentRoleBackCustomId = "admin_content_role_back";

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

    public static MessageComponent BuildSettingsCategoryComponents(bool registrationApprovalRequired, bool inactiveAutoKickEnabled, bool sleepKickEnabled, bool sleepKickAutoDetect, bool sleepKickAdminConfirm)
    {
        var approvalToggleLabel = registrationApprovalRequired ? "登録確認をOFF" : "登録確認をON";
        var approvalToggleStyle = registrationApprovalRequired ? ButtonStyle.Danger : ButtonStyle.Success;
        var autoKickToggleLabel = inactiveAutoKickEnabled ? "自動キックをOFF" : "自動キックをON";
        var autoKickToggleStyle = inactiveAutoKickEnabled ? ButtonStyle.Danger : ButtonStyle.Success;
        var sleepKickEnabledLabel = sleepKickEnabled ? "寝落ち投票をOFF" : "寝落ち投票をON";
        var sleepKickEnabledStyle = sleepKickEnabled ? ButtonStyle.Danger : ButtonStyle.Success;
        var sleepKickAutoDetectLabel = sleepKickAutoDetect ? "自動検出をOFF" : "自動検出をON";
        var sleepKickAutoDetectStyle = sleepKickAutoDetect ? ButtonStyle.Danger : ButtonStyle.Success;
        var sleepKickToggleLabel = sleepKickAdminConfirm ? "管理者確認をOFF" : "管理者確認をON";
        var sleepKickToggleStyle = sleepKickAdminConfirm ? ButtonStyle.Danger : ButtonStyle.Success;

        return new ComponentBuilder()
            .WithButton(approvalToggleLabel, ToggleRegistrationApprovalCustomId, approvalToggleStyle, row: 0)
            .WithButton(autoKickToggleLabel, ToggleInactiveAutoKickCustomId, autoKickToggleStyle, row: 0)
            .WithButton(sleepKickEnabledLabel, ToggleSleepKickEnabledCustomId, sleepKickEnabledStyle, row: 0)
            .WithButton(sleepKickAutoDetectLabel, ToggleSleepKickAutoDetectCustomId, sleepKickAutoDetectStyle, row: 1)
            .WithButton(sleepKickToggleLabel, ToggleSleepKickAdminConfirmCustomId, sleepKickToggleStyle, row: 1)
            .WithButton("運用設定編集", RuntimeTuningCustomId, ButtonStyle.Secondary, row: 2)
            .WithButton("パネル再同期", ResyncPanelsCustomId, ButtonStyle.Secondary, row: 2)
            .Build();
    }

    public static MessageComponent BuildSpecialCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("抽選管理", LotteryCustomId, ButtonStyle.Success, row: 0)
            .WithButton("問い合わせ管理", InquiryDashboardCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton("コンテンツロール管理", OpenContentRoleCategoryCustomId, ButtonStyle.Primary, row: 0)
            .Build();
    }

    public static MessageComponent BuildContentRoleCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("ロール一覧", ContentRoleListCustomId, ButtonStyle.Primary, row: 0)
            .WithButton("ロール追加", ContentRoleAddCustomId, ButtonStyle.Success, row: 0)
            .WithButton("ロール削除", ContentRoleRemoveSelectCustomId, ButtonStyle.Danger, row: 0)
            .WithButton("パネル再設置", ContentRoleResyncCustomId, ButtonStyle.Secondary, row: 1)
            .WithButton("戻る", ContentRoleBackCustomId, ButtonStyle.Secondary, row: 1)
            .Build();
    }

    public static MessageComponent BuildMaintenanceCategoryComponents()
    {
        return new ComponentBuilder()
            .WithButton("CSV出力", ExportMessageCsvCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }

    public static Embed BuildStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled, bool sleepKickEnabled, bool sleepKickAutoDetect, bool sleepKickAdminConfirm, bool includeReactionLabelInAutoKickNote = false)
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
        var sleepKickEnabledState = sleepKickEnabled ? "ON" : "OFF";
        var sleepKickEnabledDetail = sleepKickEnabled
            ? "長時間ミュートの自動検出・投票機能が有効です。"
            : "長時間ミュートの自動検出・投票機能は無効です。";
        var sleepKickAutoDetectState = sleepKickAutoDetect ? "ON" : "OFF";
        var sleepKickAutoDetectDetail = sleepKickAutoDetect
            ? "一定時間ミュートのメンバーを自動検出し投票を開始します。"
            : "自動検出は無効です。手動でのみ投票を開始できます。";
        var sleepKickState = sleepKickAdminConfirm ? "ON（管理者確認あり）" : "OFF（投票通過で即時切断）";
        var sleepKickDetail = sleepKickAdminConfirm
            ? "寝落ち投票可決後、管理者・オフィサーが切断を承認/拒否できます。"
            : "寝落ち投票可決後、60秒で自動切断します。";

        return new EmbedBuilder()
            .WithTitle("ADMINコントロールパネル")
            .WithDescription("状態確認はこのEmbed、操作はカテゴリボタン -> サブメニューで実行します。")
            .WithColor(inactiveAutoKickEnabled ? Color.Orange : Color.Green)
            .WithCurrentTimestamp()
            .AddField("登録確認フロー", approvalState, true)
            .AddField("30日非アクティブ自動キック", autoKickState, true)
            .AddField("寝落ち投票機能", sleepKickEnabledState, true)
            .AddField("寝落ち自動検出", sleepKickAutoDetectState, true)
            .AddField("寝落ち管理者確認", sleepKickState, true)
            .AddField("登録確認の動作", approvalDetail, false)
            .AddField("自動キックの動作", autoKickDetail, false)
            .AddField("寝落ち投票の動作", sleepKickEnabledDetail, false)
            .AddField("寝落ち自動検出の動作", sleepKickAutoDetectDetail, false)
            .AddField("寝落ち管理者確認の動作", sleepKickDetail, false)
            .AddField("メニュー分類", "監視/統計 | メンバー運用 | 設定/保守 | 管理機能 | 補助/エクスポート", false)
            .AddField("ボタン色の意味", "緑: 有効化/開始  |  赤: 無効化/停止  |  灰: 設定/診断/補助", false)
            .Build();
    }
}
