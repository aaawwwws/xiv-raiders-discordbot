using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public sealed class ControlPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly VoiceChannelTextService _voiceTextService;
    private readonly DiscordOptions _options;

    public ControlPanelModule(Db db, VoiceChannelTextService voiceTextService, IOptions<DiscordOptions> options)
    {
        _db = db;
        _voiceTextService = voiceTextService;
        _options = options.Value;
    }

    [ComponentInteraction("voice_rename", ignoreGroupNames: true)]
    public async Task RenameVoiceAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await RespondAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<VoiceRenameModal>("voice_rename_modal");
    }

    [ComponentInteraction("voice_limit_modal", ignoreGroupNames: true)]
    public async Task OpenLimitModalAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await RespondAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<VoiceLimitModal>("voice_limit_modal_submit");
    }

    [ComponentInteraction("voice_owner_transfer", ignoreGroupNames: true)]
    public async Task OpenOwnerTransferModalAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await RespondAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        var sessions = await _db.ListOpenVoiceSessionsAsync(target.Guild.Id, target.Id);
        var joinedMap = sessions
            .Where(s => s.JoinedAt != default)
            .ToDictionary(s => ToUlong(s.UserId), s => s.JoinedAt);

        var candidates = target.ConnectedUsers
            .Where(u => !u.IsBot && u.Id != guildUser.Id)
            .OrderBy(u => joinedMap.TryGetValue(u.Id, out var joined) ? joined : DateTimeOffset.MaxValue)
            .ThenBy(u => u.DisplayName ?? u.GlobalName ?? u.Username, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        if (candidates.Count == 0)
        {
            await RespondAsync("移譲できるユーザーがVC内にいません。", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId("voice_owner_transfer_select")
            .WithPlaceholder("移譲先を選択")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var candidate in candidates)
        {
            var label = BuildUserLabel(candidate);
            menu.AddOption(label, candidate.Id.ToString(), $"{candidate.Username}");
        }

        var component = new ComponentBuilder()
            .WithSelectMenu(menu)
            .Build();

        await RespondAsync("移譲先を選んでください。", components: component, ephemeral: true);
    }

    [ComponentInteraction("voice_status", ignoreGroupNames: true)]
    public async Task ShowVoiceStatusAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var channel = guildUser.VoiceChannel;
        if (channel is null)
        {
            await FollowupAsync("VCに参加してから操作してください。", ephemeral: true);
            return;
        }

        var embed = await BuildVoiceStatusEmbedAsync(channel);
        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [ComponentInteraction(VoiceChannelTextService.StatusPromptSetCustomId, ignoreGroupNames: true)]
    public async Task OpenVoiceStatusPromptModalAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        var (channel, error) = TryGetVoiceChannelFromPromptContext(guildUser);
        if (channel is null)
        {
            await RespondAsync(error ?? "このVCテキストからのみ操作できます。", ephemeral: true);
            return;
        }

        if (!CanSetVoiceChannelStatus(guildUser, channel))
        {
            await RespondAsync("このVCのステータスを変更する権限がありません。", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<VoiceStatusPromptModal>(VoiceChannelTextService.StatusPromptModalCustomId);
    }

    [ComponentInteraction(VoiceChannelTextService.StatusPromptClearCustomId, ignoreGroupNames: true)]
    public async Task ClearVoiceStatusFromPromptAsync()
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (channel, error) = TryGetVoiceChannelFromPromptContext(guildUser);
        if (channel is null)
        {
            await FollowupAsync(error ?? "このVCテキストからのみ操作できます。", ephemeral: true);
            return;
        }

        if (!CanSetVoiceChannelStatus(guildUser, channel))
        {
            await FollowupAsync("このVCのステータスを変更する権限がありません。", ephemeral: true);
            return;
        }

        await channel.SetStatusAsync(string.Empty);
        await _voiceTextService.UpsertStatusPromptAsync(channel, guildUser, "VCステータスをクリアしました。");
        await FollowupAsync("VCステータスをクリアしました。", ephemeral: true);
    }

    [ModalInteraction("voice_rename_modal", ignoreGroupNames: true)]
    public async Task HandleRenameModalAsync(VoiceRenameModal modal)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await FollowupAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        var name = modal.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            await FollowupAsync("VC名を入力してください。", ephemeral: true);
            return;
        }

        await target.ModifyAsync(props => props.Name = name);
        await FollowupAsync($"VC名を `{name}` に変更しました。", ephemeral: true);
    }

    [ModalInteraction("voice_limit_modal_submit", ignoreGroupNames: true)]
    public async Task HandleLimitModalAsync(VoiceLimitModal modal)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await FollowupAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(modal.UserLimit))
        {
            await FollowupAsync("人数制限を入力してください。", ephemeral: true);
            return;
        }

        if (!int.TryParse(modal.UserLimit, out var limit))
        {
            await FollowupAsync("人数制限は数字で入力してください。", ephemeral: true);
            return;
        }

        limit = Math.Clamp(limit, 0, 99);
        await target.ModifyAsync(props => props.UserLimit = limit);
        var text = limit == 0 ? "人数制限を解除しました。" : $"人数制限を `{limit}` に変更しました。";
        await FollowupAsync(text, ephemeral: true);
    }

    [ModalInteraction(VoiceChannelTextService.StatusPromptModalCustomId, ignoreGroupNames: true)]
    public async Task HandleVoiceStatusPromptModalAsync(VoiceStatusPromptModal modal)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (channel, error) = TryGetVoiceChannelFromPromptContext(guildUser);
        if (channel is null)
        {
            await FollowupAsync(error ?? "このVCテキストからのみ操作できます。", ephemeral: true);
            return;
        }

        if (!CanSetVoiceChannelStatus(guildUser, channel))
        {
            await FollowupAsync("このVCのステータスを変更する権限がありません。", ephemeral: true);
            return;
        }

        var status = modal.Status?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(status))
        {
            await FollowupAsync("ステータスを入力してください。クリアしたい場合は `クリア` ボタンを使ってください。", ephemeral: true);
            return;
        }

        await channel.SetStatusAsync(status);
        await _voiceTextService.UpsertStatusPromptAsync(channel, guildUser, "VCステータスを更新しました。");
        await FollowupAsync($"VCステータスを更新しました: `{status}`", ephemeral: true);
    }

    [ComponentInteraction("voice_owner_transfer_select", ignoreGroupNames: true)]
    public async Task HandleOwnerTransferSelectAsync(string[] values)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (channel, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (channel is null)
        {
            await FollowupAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        var targetId = values is { Length: > 0 } && ulong.TryParse(values[0], out var parsed)
            ? parsed
            : (ulong?)null;

        if (!targetId.HasValue)
        {
            await FollowupAsync("移譲先のユーザーを選択してください。", ephemeral: true);
            return;
        }

        var targetUser = guildUser.Guild.GetUser(targetId.Value);
        if (targetUser is null)
        {
            await FollowupAsync("移譲先のユーザーが見つかりません。", ephemeral: true);
            return;
        }

        if (targetUser.VoiceChannel?.Id != channel.Id)
        {
            await FollowupAsync("移譲先は同じVCに参加している必要があります。", ephemeral: true);
            return;
        }

        var ownerId = await _db.GetTempVoiceOwnerAsync(channel.Id);
        var previousOwner = ownerId.HasValue ? guildUser.Guild.GetUser(ToUlong(ownerId.Value)) : null;
        await SetVoiceOwnerAsync(channel, targetUser, ownerId);
        await _voiceTextService.PostOwnerTransferAsync(channel, previousOwner, targetUser, guildUser);
        await FollowupAsync($"所有者を <@{targetUser.Id}> に移譲しました。", ephemeral: true);
    }

    [ComponentInteraction("voice_limit_4", ignoreGroupNames: true)]
    public async Task SetLimitFourAsync()
    {
        await SetLimitPresetAsync(4);
    }

    [ComponentInteraction("voice_limit_8", ignoreGroupNames: true)]
    public async Task SetLimitEightAsync()
    {
        await SetLimitPresetAsync(8);
    }


    private async Task SetVoiceOwnerAsync(SocketVoiceChannel channel, SocketGuildUser newOwner, long? previousOwnerId)
    {
        if (previousOwnerId.HasValue)
        {
            var previousOwner = channel.Guild.GetUser(ToUlong(previousOwnerId.Value));
            if (previousOwner is not null)
            {
                await channel.RemovePermissionOverwriteAsync(previousOwner);
            }
        }

        var ownerOverwrite = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow, manageChannel: PermValue.Allow);
        await channel.AddPermissionOverwriteAsync(newOwner, ownerOverwrite);

        await _db.UpsertTempVoiceChannelAsync(channel.Guild.Id, channel.Id, newOwner.Id);
    }

    // Voice transfer embed posting is handled by VoiceChannelTextService.

    private async Task<Embed> BuildVoiceStatusEmbedAsync(SocketVoiceChannel channel)
    {
        await EnsureTempVoiceOwnerAsync(channel);
        var sessions = await _db.ListOpenVoiceSessionsAsync(channel.Guild.Id, channel.Id);
        var joinedMap = sessions
            .Where(s => s.JoinedAt != default)
            .ToDictionary(s => ToUlong(s.UserId), s => s.JoinedAt);

        var participants = channel.ConnectedUsers
            .Where(u => !u.IsBot)
            .OrderBy(u => joinedMap.TryGetValue(u.Id, out var joined) ? joined : DateTimeOffset.MaxValue)
            .ThenBy(u => u.DisplayName ?? u.GlobalName ?? u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ownerId = await _db.GetTempVoiceOwnerAsync(channel.Id);
        var ownerText = "未設定";
        if (ownerId.HasValue)
        {
            var ownerUser = channel.Guild.GetUser(ToUlong(ownerId.Value));
            if (ownerUser is not null)
            {
                ownerText = $"{ownerUser.Mention} ({ownerUser.DisplayName ?? ownerUser.GlobalName ?? ownerUser.Username})";
            }
        }

        var limitText = channel.UserLimit.HasValue && channel.UserLimit.Value > 0
            ? channel.UserLimit.Value.ToString()
            : "∞";

        var countText = $"{participants.Count} / {limitText}";
        var participantLines = BuildParticipantLines(participants);
        var startText = sessions.Count > 0 ? FormatRelativeTimestamp(sessions.Min(s => s.JoinedAt)) : "不明";

        var embed = new EmbedBuilder()
            .WithTitle("VCステータス")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .AddField("VC", $"<#{channel.Id}> ({channel.Name})", false)
            .AddField("人数", countText, true)
            .AddField("所有者", ownerText, true)
            .AddField("開始", startText, true)
            .AddField("参加者", participantLines, false);

        return embed.Build();
    }

    private static string BuildParticipantLines(IReadOnlyList<SocketGuildUser> participants)
    {
        if (participants.Count == 0)
        {
            return "なし";
        }

        const int maxLines = 20;
        var lines = participants
            .Take(maxLines)
            .Select(user =>
            {
                var name = user.DisplayName ?? user.GlobalName ?? user.Username;
                return $"{user.Mention} ({name})";
            })
            .ToList();

        if (participants.Count > maxLines)
        {
            lines.Add($"…他{participants.Count - maxLines}人");
        }

        return string.Join('\n', lines);
    }

    private static string FormatRelativeTimestamp(DateTimeOffset time)
        => $"<t:{time.ToUnixTimeSeconds()}:R>";

    private async Task<(SocketVoiceChannel? Channel, string? Error)> TryGetControllableVoiceChannelAsync(SocketGuildUser user)
    {
        var channel = user.VoiceChannel;
        if (channel is null)
        {
            return (null, "VCに参加してから操作してください。");
        }

        var ownerId = await EnsureTempVoiceOwnerAsync(channel);
        if (!ownerId.HasValue)
        {
            return (null, "このVCは管理対象ではありません。");
        }

        var isAdmin = await IsAdminAsync(user);
        if (ownerId.Value != ToDbId(user.Id) && !isAdmin)
        {
            return (null, "このVCを操作できるのは作成者または管理者のみです。");
        }

        return (channel, null);
    }

    private async Task<long?> EnsureTempVoiceOwnerAsync(SocketVoiceChannel channel)
    {
        var ownerId = await _db.GetTempVoiceOwnerAsync(channel.Id);
        if (ownerId.HasValue)
        {
            return ownerId;
        }

        if (!LooksLikeTempVoiceChannel(channel))
        {
            return null;
        }

        var candidate = await ResolveOldestParticipantAsync(channel);
        if (candidate is null)
        {
            return null;
        }

        await _db.UpsertTempVoiceChannelAsync(channel.Guild.Id, channel.Id, candidate.Id);

        try
        {
            var ownerOverwrite = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow, manageChannel: PermValue.Allow);
            await channel.AddPermissionOverwriteAsync(candidate, ownerOverwrite);
        }
        catch
        {
        }

        return ToDbId(candidate.Id);
    }

    private async Task<SocketGuildUser?> ResolveOldestParticipantAsync(SocketVoiceChannel channel)
    {
        var sessions = await _db.ListOpenVoiceSessionsAsync(channel.Guild.Id, channel.Id);
        if (sessions.Count > 0)
        {
            foreach (var session in sessions.OrderBy(s => s.JoinedAt))
            {
                var user = channel.Guild.GetUser(ToUlong(session.UserId));
                if (user is null || user.IsBot)
                {
                    continue;
                }

                if (user.VoiceChannel?.Id == channel.Id)
                {
                    return user;
                }
            }
        }

        return channel.ConnectedUsers.FirstOrDefault(u => !u.IsBot);
    }

    private bool LooksLikeTempVoiceChannel(SocketVoiceChannel channel)
    {
        var prefix = _options.TempVoiceChannelPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        return channel.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsAdminAsync(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator)
        {
            return true;
        }

        var settings = await _db.GetGuildSettingsAsync(user.Guild.Id);
        if (settings is null || settings.AdminRoleId == 0)
        {
            return false;
        }

        return user.Roles.Any(role => role.Id == ToUlong(settings.AdminRoleId));
    }

    private async Task SetLimitPresetAsync(int limit)
    {
        if (Context.User is not SocketGuildUser guildUser)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var (target, error) = await TryGetControllableVoiceChannelAsync(guildUser);
        if (target is null)
        {
            await FollowupAsync(error ?? "操作できるVCが見つかりません。", ephemeral: true);
            return;
        }

        var next = Math.Clamp(limit, 0, 99);
        await target.ModifyAsync(props => props.UserLimit = next);
        await FollowupAsync($"人数制限を `{next}` に変更しました。", ephemeral: true);
    }

    private static string BuildUserLabel(SocketGuildUser user)
    {
        var display = user.DisplayName ?? user.GlobalName ?? user.Username;
        var baseLabel = $"{display} (@{user.Username})";
        return baseLabel.Length > 90 ? baseLabel[..90] : baseLabel;
    }

    private (SocketVoiceChannel? Channel, string? Error) TryGetVoiceChannelFromPromptContext(SocketGuildUser user)
    {
        if (Context.Guild is null || Context.Channel is not SocketGuildChannel guildChannel)
        {
            return (null, "この操作はサーバー内でのみ使えます。");
        }

        var voiceChannel = guildChannel as SocketVoiceChannel ?? Context.Guild.GetVoiceChannel(guildChannel.Id);
        if (voiceChannel is null)
        {
            return (null, "このVCテキストからのみ操作できます。");
        }

        if (user.VoiceChannel?.Id != voiceChannel.Id)
        {
            return (null, "VCに参加してから操作してください。");
        }

        return (voiceChannel, null);
    }

    private static bool CanSetVoiceChannelStatus(SocketGuildUser user, SocketVoiceChannel channel)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageChannels)
        {
            return true;
        }

        var permissions = user.GetPermissions(channel);
        return permissions.SetVoiceChannelStatus || permissions.ManageChannel;
    }

    private static long ToDbId(ulong value) => unchecked((long)value);
    private static ulong ToUlong(long value) => unchecked((ulong)value);
}

public sealed class VoiceRenameModal : IModal
{
    public string Title => "VC名変更";

    [InputLabel("新しいVC名")]
    [ModalTextInput("voice_name", maxLength: 80)]
    public string Name { get; set; } = string.Empty;
}

public sealed class VoiceLimitModal : IModal
{
    public string Title => "人数制限";

    [InputLabel("人数 (0で解除, 最大99)")]
    [ModalTextInput("voice_limit", maxLength: 2)]
    public string UserLimit { get; set; } = string.Empty;
}

public sealed class VoiceStatusPromptModal : IModal
{
    public string Title => "VCステータス設定";

    [InputLabel("ステータス")]
    [ModalTextInput("voice_status_prompt_value", maxLength: 120, placeholder: "例: 雑談 / 零式 1-4通し / 地図")]
    public string Status { get; set; } = string.Empty;
}
