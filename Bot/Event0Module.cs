using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;

namespace RaidDcBot.Bot;

public sealed class Event0Module : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly ILogger<Event0Module> _logger;

    public Event0Module(Db db, ILogger<Event0Module> logger)
    {
        _db = db;
        _logger = logger;
    }

    [SlashCommand("event0sendmsg", "イベント参加ボタン付きメッセージを送信します")]
    public async Task SendEventMessageAsync(
        [Summary("title", "イベントタイトル")] string title,
        [Summary("description", "イベント説明")] string? description = null)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルには投稿できません。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("サーバーメンバー情報を取得できませんでした。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var eventId = await _db.CreateEvent0PostAsync(
            Context.Guild.Id,
            channel.Id,
            null,
            title,
            description,
            Context.User.Id);

        var post = await _db.GetEvent0PostAsync(eventId);
        if (post is null)
        {
            await FollowupAsync("イベントの作成に失敗しました。", ephemeral: true);
            return;
        }

        var content = Event0Panel.BuildEventMessage(post);
        var components = Event0Panel.BuildEventComponents(eventId);

        var message = await channel.SendMessageAsync(content, components: components);
        await _db.UpdateEvent0PostMessageIdAsync(eventId, message.Id);

        await EnsureAndAssignRoleAsync(guildUser, "新生活");

        await FollowupAsync("イベントメッセージを送信しました。", ephemeral: true);
    }

    private async Task EnsureAndAssignRoleAsync(SocketGuildUser guildUser, string roleName)
    {
        try
        {
            IRole? role = Context.Guild!.Roles.FirstOrDefault(r =>
                string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));

            if (role is null)
            {
                role = await Context.Guild.CreateRoleAsync(
                    roleName,
                    GuildPermissions.None,
                    color: new Color(127, 140, 141),
                    isHoisted: false,
                    isMentionable: false);
            }

            if (!guildUser.Roles.Any(r => r.Id == role.Id))
            {
                await guildUser.AddRoleAsync(role);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to assign role {RoleName} to user {UserId}.", roleName, guildUser.Id);
        }
    }

    [ComponentInteraction("event0_join:*", ignoreGroupNames: true)]
    public async Task JoinEventAsync(string eventIdRaw)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User.IsBot)
        {
            await RespondAsync("Botは参加できません。", ephemeral: true);
            return;
        }

        if (!int.TryParse(eventIdRaw, out var eventId))
        {
            await RespondAsync("イベント情報が不正です。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var post = await _db.GetEvent0PostAsync(eventId);
        if (post is null)
        {
            await FollowupAsync("イベントが見つかりませんでした。", ephemeral: true);
            return;
        }

        var displayName = Context.User.GlobalName ?? Context.User.Username;
        var added = await _db.AddEvent0ParticipantAsync(eventId, Context.User.Id, displayName);

        await FollowupAsync(added ? "参加しました！" : "すでに参加済みです。", ephemeral: true);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
