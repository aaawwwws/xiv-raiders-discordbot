using Discord;
using RaidDcBot.Data;

namespace RaidDcBot.Bot;

public static class Event0Panel
{
    public const string JoinCustomIdPrefix = "event0_join:";

    public static string BuildEventMessage(Event0Post post)
    {
        var title = $"**{post.Title}**";
        var description = string.IsNullOrWhiteSpace(post.Description)
            ? ""
            : $"\n{post.Description!.Trim()}";

        return $"{title}{description}";
    }

    public static MessageComponent BuildEventComponents(int eventId)
    {
        var idText = eventId.ToString();
        return new ComponentBuilder()
            .WithButton("参加する", $"{JoinCustomIdPrefix}{idText}", ButtonStyle.Success, row: 0)
            .Build();
    }
}
