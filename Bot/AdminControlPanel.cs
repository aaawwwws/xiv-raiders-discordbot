using Discord;

namespace RaidDcBot.Bot;

public static class AdminControlPanel
{
    public const string PanelMessage = "ADMINコントロールパネル";
    public const string ExportMessageCsvCustomId = "admin_export_message_csv_7d";
    public const string ToggleRegistrationApprovalCustomId = "admin_toggle_registration_approval";
    public const string ToggleInactiveAutoKickCustomId = "admin_toggle_inactive_auto_kick";

    public static MessageComponent BuildComponents(bool registrationApprovalRequired, bool inactiveAutoKickEnabled)
    {
        var statusLabel = registrationApprovalRequired ? "登録確認: 必要" : "登録確認: 不要";
        var statusStyle = registrationApprovalRequired ? ButtonStyle.Success : ButtonStyle.Secondary;
        var autoKickLabel = inactiveAutoKickEnabled ? "自動キック: ON" : "自動キック: OFF";
        var autoKickStyle = inactiveAutoKickEnabled ? ButtonStyle.Danger : ButtonStyle.Secondary;

        return new ComponentBuilder()
            .WithButton("メッセージCSV(全期間)", ExportMessageCsvCustomId, ButtonStyle.Secondary, row: 0)
            .WithButton(statusLabel, ToggleRegistrationApprovalCustomId, statusStyle, row: 1)
            .WithButton(autoKickLabel, ToggleInactiveAutoKickCustomId, autoKickStyle, row: 1)
            .Build();
    }
}
