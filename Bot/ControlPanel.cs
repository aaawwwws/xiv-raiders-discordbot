using Discord;

namespace RaidDcBot.Bot;

public static class ControlPanel
{
    public const string PanelMessage = "管理パネル";

    public static MessageComponent BuildComponents()
    {
        return new ComponentBuilder()
            .WithButton("VC名変更", "voice_rename", ButtonStyle.Primary, row: 0)
            .WithButton("人数制限", "voice_limit_modal", ButtonStyle.Secondary, row: 0)
            .WithButton("人数4", "voice_limit_4", ButtonStyle.Success, row: 0)
            .WithButton("人数8", "voice_limit_8", ButtonStyle.Success, row: 0)
            .WithButton("VCステータス", "voice_status", ButtonStyle.Primary, row: 1)
            .WithButton("所有者移譲", "voice_owner_transfer", ButtonStyle.Danger, row: 1)
            .Build();
    }
}
