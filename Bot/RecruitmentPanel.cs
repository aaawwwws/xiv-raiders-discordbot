using Discord;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public static class RecruitmentPanel {
    public const string PanelMessage = "募集パネル";
    public const string DisabledPanelMessage = "募集パネル（開発中・現在利用できません）";

    public static MessageComponent BuildPanelComponents(bool enabled = true) {
        return new ComponentBuilder()
            .WithButton("募集を作成", "recruit_create", ButtonStyle.Success, disabled: !enabled, row: 0)
            .WithButton("募集終了", "recruit_manage_close", ButtonStyle.Secondary, disabled: !enabled, row: 0)
            .WithButton("募集キャンセル", "recruit_manage_cancel", ButtonStyle.Danger, disabled: !enabled, row: 0)
            .Build();
    }

    public static MessageComponent BuildRecruitComponents(RecruitPostStatus status)
        => BuildRecruitComponents(status, RecruitmentRules.Positions);

    public static MessageComponent BuildRecruitComponents(RecruitPostStatus status, IReadOnlyList<string> positions) {
        var joinEnabled = status is RecruitPostStatus.Open or RecruitPostStatus.Full;
        var cancelEnabled = status != RecruitPostStatus.Canceled;
        var editEnabled = status is RecruitPostStatus.Open or RecruitPostStatus.Full;
        var positionSet = new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);

        var builder = new ComponentBuilder();
        var availablePositions = RecruitmentRules.Positions
            .Where(positionSet.Contains)
            .ToList();

        var menu = new SelectMenuBuilder()
            .WithCustomId("recruit_pos_select")
            .WithPlaceholder("参加ポジションを選択")
            .WithMinValues(1)
            .WithMaxValues(1)
            .WithDisabled(!joinEnabled || availablePositions.Count == 0);

        foreach (var position in availablePositions)
        {
            menu.AddOption(position, position);
        }

        builder
            .WithSelectMenu(menu, row: 0)
            .WithButton("コメント編集", "recruit_comment_edit", ButtonStyle.Secondary, disabled: !cancelEnabled, row: 1)
            .WithButton("参加取消", "recruit_cancel", ButtonStyle.Danger, disabled: !cancelEnabled, row: 1)
            .WithButton("埋まった枠を〆る", "recruit_external_fill", ButtonStyle.Danger, disabled: !editEnabled, row: 2)
            .WithButton("〆解除", "recruit_external_unfill", ButtonStyle.Secondary, disabled: !editEnabled, row: 2)
            .WithButton("募集内容編集", "recruit_post_edit", ButtonStyle.Secondary, disabled: !editEnabled, row: 2)
            .WithButton("募集枠編集", "recruit_positions_edit", ButtonStyle.Primary, disabled: !editEnabled, row: 2)
            .WithButton("遅刻連絡", "recruit_late", ButtonStyle.Secondary, disabled: !cancelEnabled, row: 3)
            .WithButton("欠席連絡", "recruit_absent", ButtonStyle.Danger, disabled: !cancelEnabled, row: 3);

        return builder.Build();
    }
}
