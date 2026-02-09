using Discord;

namespace RaidDcBot.Bot;

public static class AdminControlPanel
{
    public const string PanelMessage = "ADMINコントロールパネル";
    public const string ExportMessageCsvCustomId = "admin_export_message_csv_7d";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("メッセージCSV(全期間)", ExportMessageCsvCustomId, ButtonStyle.Secondary, row: 0)
            .Build();
    }
}
