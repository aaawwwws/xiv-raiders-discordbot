using Discord;

namespace RaidDcBot.Bot;

public static class MemberContentRolePanel
{
    public const string Prefix = "content_role";

    public static readonly IReadOnlyList<ContentRoleDefinition> DefaultDefinitions =
    [
        new("fl", "FL", "[コンテンツ] FL", ButtonStyle.Success, 0),
        new("raid", "レイド", "[コンテンツ] レイド", ButtonStyle.Success, 0),
        new("roulette", "ルレ", "[コンテンツ] ルレ", ButtonStyle.Success, 0),
        new("maps", "地図", "[コンテンツ] 地図", ButtonStyle.Success, 0),
        new("special_field", "特殊フィールド", "[コンテンツ] 特殊フィールド", ButtonStyle.Success, 0),
        new("dd", "DD", "[コンテンツ] DD", ButtonStyle.Success, 1),
        new("ss", "SS", "[コンテンツ] SS", ButtonStyle.Success, 1),
        new("chat", "雑談", "[コンテンツ] 雑談", ButtonStyle.Success, 1),
        new("listen_only", "聞き専", "[コンテンツ] 聞き専", ButtonStyle.Success, 1)
    ];

    public const string PanelTitle = "コンテンツロールパネル";
    public const string PanelMessage = PanelTitle;

    public static MessageComponent BuildComponents(IReadOnlyList<ContentRoleDefinition> definitions)
    {
        var builder = new ComponentBuilder();
        foreach (var definition in definitions.Take(25))
        {
            var row = Math.Clamp(definition.Row, 0, 4);
            builder.WithButton(
                definition.Label,
                $"{Prefix}:{definition.Key}",
                ButtonStyle.Success,
                row: row);
        }

        return builder.Build();
    }

    public static bool TryResolveRole(IReadOnlyList<ContentRoleDefinition> definitions, string key, out ContentRoleDefinition definition)
    {
        var match = definitions.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            definition = default!;
            return false;
        }

        definition = match;
        return true;
    }

    public static bool LooksLikePanelMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.StartsWith(PanelTitle, StringComparison.Ordinal);
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
                        button.CustomId.StartsWith($"{Prefix}:", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

public sealed record ContentRoleDefinition(string Key, string Label, string RoleName, ButtonStyle Style, int Row);
