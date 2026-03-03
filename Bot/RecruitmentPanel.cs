using Discord;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public static class RecruitmentPanel {
    public const string PanelMessage = "※不具合がある可能性があります。\n終了: 募集を完了します。\n中断: 募集を取りやめます。\n終了/中断後も管理メニューから再開できます。";
    public const string DisabledPanelMessage = "（開発中・現在利用できません）\n※不具合がある可能性があります。\n終了: 募集を完了します。\n中断: 募集を取りやめます。\n終了/中断後も管理メニューから再開できます。";

    public static string BuildPanelMessage(bool enabled = true)
        => enabled ? PanelMessage : DisabledPanelMessage;

    public static MessageComponent BuildPanelComponents(bool enabled = true) {
        return new ComponentBuilder()
            .WithButton("募集を作成", "recruit_create", ButtonStyle.Success, disabled: !enabled, row: 0)
            .WithButton("管理メニュー", "recruit_manage_panel", ButtonStyle.Primary, disabled: !enabled, row: 0)
            .Build();
    }

    public static MessageComponent BuildRecruitComponents(RecruitPostStatus status)
        => BuildRecruitComponents(status, RecruitmentRules.Positions);

    public static MessageComponent BuildRecruitComponents(RecruitPostStatus status, IReadOnlyList<string> positions) {
        var participationEnabled = status is RecruitPostStatus.Open or RecruitPostStatus.Full;
        var hasOpenPositions = positions.Count > 0;

        return new ComponentBuilder()
            .WithButton("参加メニュー", "recruit_participation_tools", ButtonStyle.Success, disabled: !participationEnabled || !hasOpenPositions, row: 0)
            .WithButton("管理メニュー", "recruit_manage_tools", ButtonStyle.Primary, disabled: false, row: 0)
            .Build();
    }

    public static bool LooksLikePanelComponents(IReadOnlyCollection<IMessageComponent> components)
    {
        foreach (var root in components)
        {
            if (root is ActionRowComponent row)
            {
                foreach (var component in row.Components)
                {
                    if (component is ButtonComponent button &&
                        button.CustomId is not null &&
                        button.CustomId is "recruit_create" or "recruit_manage_panel" or "recruit_manage_close" or "recruit_manage_cancel")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
