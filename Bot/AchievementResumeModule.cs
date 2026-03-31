using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public sealed class AchievementResumeModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string SavageFilterCustomIdPrefix = "resume_savage_filter";
    private const string ResumeGenerateCustomIdPrefix = "resume_generate";
    private static readonly string[] SavageExpansionGroups = ["黄金", "暁月", "漆黒", "紅蓮", "異聞"];
    private static readonly HashSet<string> SavageExpansionGroupSet = new(SavageExpansionGroups, StringComparer.Ordinal);

    private readonly AchievementResumeService _resume;

    public AchievementResumeModule(AchievementResumeService resume)
    {
        _resume = resume;
    }

    [ComponentInteraction(SavageFilterCustomIdPrefix + ":*", ignoreGroupNames: true)]
    public async Task ApplySavageFilterAsync(string targetUserIdRaw, string[] values)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(targetUserIdRaw, out var targetUserId) || targetUserId == 0)
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        if (Context.User.Id != targetUserId)
        {
            await RespondAsync("この履歴書メニューは作成した本人のみ操作できます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はメニューから実行してください。", ephemeral: true);
            return;
        }

        var selectedGroups = NormalizeSelectedGroups(values);
        if (selectedGroups.Count == 0)
        {
            selectedGroups = new HashSet<string>(SavageExpansionGroups, StringComparer.Ordinal);
        }

        await component.UpdateAsync(props =>
        {
            props.Content = "カテゴリを選択しました。`この内容で生成` ボタンを押してください。";
            props.Components = BuildSavageFilterComponents(targetUserId, selectedGroups);
        });
    }

    [ComponentInteraction(ResumeGenerateCustomIdPrefix + ":*:*", ignoreGroupNames: true)]
    public async Task GenerateResumeAsync(string targetUserIdRaw, string selectedRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!ulong.TryParse(targetUserIdRaw, out var targetUserId) || targetUserId == 0)
        {
            await RespondAsync("対象ユーザー情報が不正です。", ephemeral: true);
            return;
        }

        if (Context.User.Id != targetUserId)
        {
            await RespondAsync("この履歴書メニューは作成した本人のみ操作できます。", ephemeral: true);
            return;
        }

        var selectedGroups = ParseSelectedGroups(selectedRaw);
        if (selectedGroups.Count == 0)
        {
            selectedGroups = new HashSet<string>(SavageExpansionGroups, StringComparer.Ordinal);
        }

        await DeferAsync(ephemeral: true);

        IUser? target = Context.Guild.GetUser(targetUserId);
        target ??= await Context.Client.Rest.GetUserAsync(targetUserId);
        if (target is null)
        {
            await FollowupAsync("対象ユーザーが見つかりませんでした。", ephemeral: true);
            return;
        }

        var result = await _resume.BuildAsync(
            Context.Guild,
            target,
            forceRefresh: false,
            savageGroupFilter: selectedGroups.ToArray());
        if (!result.Success || result.Embed is null)
        {
            await FollowupAsync(result.ErrorMessage ?? "履歴書の生成に失敗しました。", ephemeral: true);
            return;
        }

        var components = BuildSavageFilterComponents(targetUserId, selectedGroups);
        if (result.ImagePng is { Length: > 0 } && !string.IsNullOrWhiteSpace(result.ImageFileName))
        {
            await using var imageStream = new MemoryStream(result.ImagePng, writable: false);
            await FollowupWithFileAsync(
                imageStream,
                result.ImageFileName,
                embed: result.Embed,
                components: components,
                ephemeral: true);
            return;
        }

        await FollowupAsync(embed: result.Embed, components: components, ephemeral: true);
    }

    internal static MessageComponent BuildSavageFilterComponents(ulong targetUserId, IReadOnlySet<string>? selectedGroups = null)
    {
        var selected = selectedGroups is { Count: > 0 }
            ? new HashSet<string>(selectedGroups, StringComparer.Ordinal)
            : new HashSet<string>(SavageExpansionGroups, StringComparer.Ordinal);

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{SavageFilterCustomIdPrefix}:{targetUserId}")
            .WithPlaceholder("零式表示: 拡張カテゴリを選択")
            .WithMinValues(1)
            .WithMaxValues(SavageExpansionGroups.Length);

        foreach (var group in SavageExpansionGroups)
        {
            menu.AddOption(group, group, $"零式 {group} を表示", isDefault: selected.Contains(group));
        }

        var encoded = EncodeSelectedGroups(selected);
        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("この内容で生成", $"{ResumeGenerateCustomIdPrefix}:{targetUserId}:{encoded}", ButtonStyle.Primary, row: 1)
            .Build();
    }

    private static HashSet<string> NormalizeSelectedGroups(IEnumerable<string> values)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim();
            if (SavageExpansionGroupSet.Contains(normalized))
            {
                selected.Add(normalized);
            }
        }

        return selected;
    }

    private static HashSet<string> ParseSelectedGroups(string selectedRaw)
    {
        if (string.IsNullOrWhiteSpace(selectedRaw))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return NormalizeSelectedGroups(selectedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string EncodeSelectedGroups(IReadOnlySet<string> selectedGroups)
    {
        var ordered = SavageExpansionGroups.Where(selectedGroups.Contains);
        return string.Join(',', ordered);
    }
}
