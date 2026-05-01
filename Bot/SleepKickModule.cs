using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

[Group("sleepkick", "寝落ち切断投票")]
public sealed class SleepKickModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly SleepKickService _sleepKick;
    private readonly Db _db;
    private readonly DiscordOptions _options;
    private readonly ILogger<SleepKickModule> _logger;

    public SleepKickModule(SleepKickService sleepKick, Db db, IOptions<DiscordOptions> options, ILogger<SleepKickModule> logger)
    {
        _sleepKick = sleepKick;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    [SlashCommand("test", "対象ユーザーの寝落ち切断投票を開始します")]
    public async Task TestAsync([Summary("user", "対象ユーザー")] IUser user)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!await HasMemberRoleAsync(Context.Guild, guildUser))
        {
            await RespondAsync("メンバーロールが付与されているユーザーのみ使用できます。", ephemeral: true);
            return;
        }

        var target = Context.Guild.GetUser(user.Id);
        if (target is null || target.VoiceChannel is null)
        {
            await RespondAsync("対象ユーザーは現在VCに接続していません。", ephemeral: true);
            return;
        }

        var fallbackChannel = Context.Channel as ITextChannel;
        var isAdmin = await IsBotAdminAsync(Context.Guild, guildUser);

        await DeferAsync(ephemeral: true);

        var started = await _sleepKick.StartVoteAsync(Context.Guild, fallbackChannel, target, Context.User.Id, skipCooldown: isAdmin);
        if (started)
        {
            var embed = new EmbedBuilder()
                .WithTitle("⚠️ 寝落ち切断投票の警告")
                .WithDescription(
                    $"{MentionUtils.MentionUser(target.Id)} に対して寝落ち切断投票を開始しました。\n\n" +
                    "**この機能は相手に対して切断を伴う操作です。**\n" +
                    "対象が本当に寝落ちしているか、ミュート状態かを確認してから投票してください。\n" +
                    "軽率な使用はサーバーの雰囲気を損ないます。")
                .AddField("結果", $"<#{target.VoiceChannel.Id}> のテキストチャットに投票を投稿しました。", false)
                .WithColor(Color.Red)
                .WithCurrentTimestamp()
                .Build();
            await FollowupAsync(embed: embed, ephemeral: true);
        }
        else
        {
            await FollowupAsync("投票の開始に失敗しました。設定または権限を確認してください。", ephemeral: true);
        }
    }

    private async Task<bool> HasMemberRoleAsync(SocketGuild guild, SocketGuildUser user)
    {
        if (user.IsBot)
        {
            return false;
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        SocketRole? memberRole = null;
        if (settings is not null && settings.MemberRoleId != 0)
        {
            memberRole = guild.GetRole((ulong)settings.MemberRoleId);
        }

        memberRole ??= guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));
        if (memberRole is null)
        {
            return false;
        }

        return user.Roles.Any(role => role.Id == memberRole.Id);
    }

    [SlashCommand("detect", "VC内の寝落ち候補を検出し、各VCのテキストチャットに投票を投稿します（デバッグ用）")]
    public async Task DetectAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (!await HasMemberRoleAsync(Context.Guild, guildUser))
        {
            await RespondAsync("メンバーロールが付与されているユーザーのみ使用できます。", ephemeral: true);
            return;
        }

        var logChannel = await ResolveAdminConfirmChannelAsync(Context.Guild);

        await DeferAsync(ephemeral: true);
        await _sleepKick.DetectAndNotifyAsync(Context.Guild, logChannel as ITextChannel);
        await FollowupAsync("自動検出を実行しました。該当者がいれば各VCのテキストチャットに投票が投稿されます。", ephemeral: true);
    }

    private async Task<IMessageChannel?> ResolveAdminConfirmChannelAsync(SocketGuild guild)
    {
        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        if (settings is { RegistrationReviewChannelId: not 0 })
        {
            var byId = guild.GetTextChannel(unchecked((ulong)settings.RegistrationReviewChannelId));
            if (byId is not null)
                return byId;
        }

        var name = string.IsNullOrWhiteSpace(_options.AdminConfirmChannelName) ? "管理-確認" : _options.AdminConfirmChannelName;
        return guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    [ComponentInteraction("sleep_kick_vote:*", true)]
    public async Task HandleSleepKickVoteAsync(string payload)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はボタンから実行してください。", ephemeral: true);
            return;
        }

        if (!SleepKickService.TryParseVoteCustomId(component.Data.CustomId, out var isYes, out var isCancel, out var guildId, out var targetUserId, out var messageId))
        {
            await RespondAsync("投票情報が不正です。", ephemeral: true);
            return;
        }

        await _sleepKick.HandleVoteAsync(component, isYes, isCancel, guildId, targetUserId, messageId);
    }

    [ComponentInteraction("sleep_kick_log_cancel:*", true)]
    public async Task HandleLogCancelAsync(string payload)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はボタンから実行してください。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("管理者権限が必要です。", ephemeral: true);
            return;
        }

        if (!await IsBotAdminAsync(Context.Guild, guildUser))
        {
            await RespondAsync("この操作は管理者・オフィサーのみ実行できます。", ephemeral: true);
            return;
        }

        if (!SleepKickService.TryParseLogCancelCustomId(component.Data.CustomId, out var guildId, out var targetUserId, out var messageId))
        {
            await RespondAsync("キャンセル情報が不正です。", ephemeral: true);
            return;
        }

        await _sleepKick.HandleLogCancelAsync(component, guildId, targetUserId, messageId);
    }

    [ComponentInteraction("sleep_kick_admin:*", true)]
    public async Task HandleSleepKickAdminAsync(string payload)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Interaction is not SocketMessageComponent component)
        {
            await RespondAsync("この操作はボタンから実行してください。", ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("管理者権限が必要です。", ephemeral: true);
            return;
        }

        if (!SleepKickService.TryParseAdminConfirmCustomId(component.Data.CustomId, out var isConfirm, out var guildId, out var targetUserId, out var messageId))
        {
            await RespondAsync("確認情報が不正です。", ephemeral: true);
            return;
        }

        if (!await IsBotAdminAsync(Context.Guild, guildUser))
        {
            await RespondAsync("この操作は管理者・オフィサーのみ実行できます。", ephemeral: true);
            return;
        }

        await DeferAsync();

        if (isConfirm)
        {
            await _sleepKick.HandleAdminConfirmAsync(component, guildId, targetUserId, messageId);
        }
        else
        {
            await _sleepKick.HandleAdminRejectAsync(component, guildId, targetUserId, messageId);
        }
    }

    private async Task<bool> IsBotAdminAsync(SocketGuild guild, SocketGuildUser user)
    {
        if (user.IsBot) return false;

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        SocketRole? adminRole = null;
        if (settings is not null && settings.AdminRoleId != 0)
        {
            adminRole = guild.GetRole((ulong)settings.AdminRoleId);
        }
        adminRole ??= guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.AdminRoleName, StringComparison.OrdinalIgnoreCase));
        if (adminRole is not null && user.Roles.Any(role => role.Id == adminRole.Id))
            return true;

        var managementNames = _options.BuildManagementRoleNameSet();
        return user.Roles.Any(role => managementNames.Contains(role.Name));
    }
}
