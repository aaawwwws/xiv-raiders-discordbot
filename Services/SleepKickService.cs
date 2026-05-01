using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class SleepKickService
{
    private const string VoteCustomIdPrefix = "sleep_kick_vote";
    private static readonly ConcurrentDictionary<string, SleepKickVoteState> ActiveVotes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> LastVoteTimestamps = new();
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> InitiatorCooldownTimestamps = new();

    private readonly DiscordSocketClient _client;
    private readonly ILogger<SleepKickService> _logger;
    private readonly DiscordOptions _options;
    private readonly GuildAuditLogService _audit;
    private readonly Db _db;
    private readonly HttpClient _httpClient;

    public SleepKickService(
        DiscordSocketClient client,
        ILogger<SleepKickService> logger,
        IOptions<DiscordOptions> options,
        GuildAuditLogService audit,
        Db db)
    {
        _client = client;
        _logger = logger;
        _options = options.Value;
        _audit = audit;
        _db = db;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _options.Token);
    }

    public static string BuildYesCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{VoteCustomIdPrefix}:y|{guildId}|{targetUserId}|{messageId}";

    public static string BuildNoCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{VoteCustomIdPrefix}:n|{guildId}|{targetUserId}|{messageId}";

    public static string BuildCancelCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{VoteCustomIdPrefix}:c|{guildId}|{targetUserId}|{messageId}";

    public static bool TryParseVoteCustomId(string customId, out bool isYes, out bool isCancel, out ulong guildId, out ulong targetUserId, out ulong messageId)
    {
        isYes = false;
        isCancel = false;
        guildId = 0;
        targetUserId = 0;
        messageId = 0;
        if (string.IsNullOrWhiteSpace(customId))
            return false;

        if (!customId.StartsWith(VoteCustomIdPrefix + ":", StringComparison.Ordinal))
            return false;

        var payload = customId[(VoteCustomIdPrefix.Length + 1)..];
        var parts = payload.Split('|');
        if (parts.Length != 4)
            return false;

        if (parts[0] == "y")
        {
            isYes = true;
        }
        else if (parts[0] == "c")
        {
            isCancel = true;
        }
        else if (parts[0] != "n")
        {
            return false;
        }

        return ulong.TryParse(parts[1], out guildId)
            && ulong.TryParse(parts[2], out targetUserId)
            && ulong.TryParse(parts[3], out messageId);
    }

    private const string LogCancelCustomIdPrefix = "sleep_kick_log_cancel";

    public static string BuildLogCancelCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{LogCancelCustomIdPrefix}:{guildId}|{targetUserId}|{messageId}";

    public static bool TryParseLogCancelCustomId(string customId, out ulong guildId, out ulong targetUserId, out ulong messageId)
    {
        guildId = 0;
        targetUserId = 0;
        messageId = 0;
        if (string.IsNullOrWhiteSpace(customId))
            return false;

        var prefix = LogCancelCustomIdPrefix + ":";
        if (!customId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var payload = customId[prefix.Length..];
        var parts = payload.Split('|');
        if (parts.Length != 3)
            return false;

        return ulong.TryParse(parts[0], out guildId)
            && ulong.TryParse(parts[1], out targetUserId)
            && ulong.TryParse(parts[2], out messageId);
    }

    private const string AdminConfirmCustomIdPrefix = "sleep_kick_admin";

    public static string BuildAdminConfirmCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{AdminConfirmCustomIdPrefix}:confirm|{guildId}|{targetUserId}|{messageId}";

    public static string BuildAdminRejectCustomId(ulong guildId, ulong targetUserId, ulong messageId)
        => $"{AdminConfirmCustomIdPrefix}:reject|{guildId}|{targetUserId}|{messageId}";

    public static bool TryParseAdminConfirmCustomId(string customId, out bool isConfirm, out ulong guildId, out ulong targetUserId, out ulong messageId)
    {
        isConfirm = false;
        guildId = 0;
        targetUserId = 0;
        messageId = 0;
        if (string.IsNullOrWhiteSpace(customId))
            return false;

        var prefix = AdminConfirmCustomIdPrefix + ":";
        if (!customId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var payload = customId[prefix.Length..];
        var parts = payload.Split('|');
        if (parts.Length != 4)
            return false;

        isConfirm = parts[0] == "confirm";

        return ulong.TryParse(parts[1], out guildId)
            && ulong.TryParse(parts[2], out targetUserId)
            && ulong.TryParse(parts[3], out messageId);
    }

    public async Task<bool> StartVoteAsync(SocketGuild guild, ITextChannel? fallbackChannel, SocketGuildUser targetUser, ulong initiatorUserId, bool skipCooldown = false)
    {
        if (!_options.SleepKickEnabled)
            return false;

        if (targetUser.VoiceChannel is null)
            return false;

        if (!targetUser.IsSelfMuted && !targetUser.IsMuted)
            return false;

        if (!skipCooldown
            && InitiatorCooldownTimestamps.TryGetValue(initiatorUserId, out var lastInitiated)
            && DateTimeOffset.UtcNow - lastInitiated < TimeSpan.FromMinutes(10))
            return false;

        if (!skipCooldown)
            InitiatorCooldownTimestamps[initiatorUserId] = DateTimeOffset.UtcNow;

        var initiator = guild.GetUser(initiatorUserId);
        var initiatorName = initiator?.DisplayName ?? initiator?.GlobalName ?? initiator?.Username;
        var joinTime = GetEstimatedJoinTime(targetUser);
        await PostSleepKickVoteAsync(targetUser, targetUser.VoiceChannel, joinTime ?? DateTimeOffset.UtcNow.AddHours(-2), fallbackChannel, initiatorUserId, initiatorName);
        return true;
    }

    public async Task DetectAndNotifyAsync(SocketGuild guild, ITextChannel? fallbackChannel = null)
    {
        if (!_options.SleepKickEnabled)
            return;

        var threshold = TimeSpan.FromMinutes(_options.SleepKickDetectMinutes);
        var now = DateTimeOffset.UtcNow;

        foreach (var voiceChannel in guild.VoiceChannels)
        {
            foreach (var user in voiceChannel.ConnectedUsers)
            {
                if (user.IsBot || user.IsDeafened)
                    continue;

                var isMuted = user.IsSelfMuted || user.IsMuted;
                if (!isMuted)
                    continue;

                var joinTime = GetEstimatedJoinTime(user);
                if (joinTime == null || now - joinTime.Value < threshold)
                    continue;

                if (LastVoteTimestamps.TryGetValue(user.Id, out var lastVote))
                {
                    if (now - lastVote < TimeSpan.FromMinutes(_options.SleepKickCooldownMinutes))
                        continue;
                }

                await PostSleepKickVoteAsync(user, voiceChannel, joinTime.Value, fallbackChannel);
            }
        }
    }

    public async Task HandleVoteAsync(SocketMessageComponent component, bool isYes, bool isCancel, ulong guildId, ulong targetUserId, ulong messageId)
    {
        var voteKey = $"{guildId}:{targetUserId}:{messageId}";
        var vote = ActiveVotes.GetValueOrDefault(voteKey);
        if (vote is null)
        {
            await component.RespondAsync("この投票は期限切れです。", ephemeral: true);
            return;
        }

        // If the person being voted on interacts with the vote buttons, cancel it immediately.
        if (component.User.Id == targetUserId)
        {
            var reason = $"{MentionUtils.MentionUser(targetUserId)} 本人のリアクションを確認したため、投票をキャンセルしました。";
            await component.UpdateAsync(props =>
            {
                props.Embed = BuildResultEmbed(vote, false, reason);
                props.Components = new ComponentBuilder().Build();
            });
            ActiveVotes.TryRemove(voteKey, out _);

            var guildForLog = _client.GetGuild(guildId);
            if (guildForLog != null)
                await UpdateLogMessageAsync(vote, guildForLog, "キャンセル済み（本人反応）", reason);

            return;
        }

        if (isCancel)
        {
            await component.RespondAsync("このボタンは対象者本人のみ有効です。", ephemeral: true);
            return;
        }

        if (vote.VoterIds.Contains(component.User.Id))
        {
            await component.RespondAsync("既に投票済みです。", ephemeral: true);
            return;
        }

        vote.VoterIds.Add(component.User.Id);
        var voterName = component.User.GlobalName ?? component.User.Username;
        vote.VoterDetails.Add((component.User.Id, voterName, isYes));
        if (isYes)
        {
            vote.YesCount++;
        }
        else
        {
            vote.NoCount++;
        }

        var remaining = Math.Max(0, vote.RequiredVotes - (vote.YesCount + vote.NoCount));

        _logger.LogInformation("Sleep kick vote received. GuildId={GuildId}, TargetUserId={TargetUserId}, VoterId={VoterId}, Yes={Yes}, No={No}, Remaining={Remaining}",
            guildId, targetUserId, component.User.Id, vote.YesCount, vote.NoCount, remaining);

        _ = Task.Run(async () =>
        {
            try
            {
                var voterJson = JsonSerializer.Serialize(new { user_id = component.User.Id, user_name = voterName, vote = isYes ? "yes" : "no" });
                await _db.InsertSleepKickLogAsync(
                    vote.GuildId, vote.TargetUserId, vote.TargetUserName,
                    vote.InitiatorUserId, vote.InitiatorUserName,
                    vote.ChannelId, vote.ChannelName,
                    "vote_cast",
                    vote.YesCount, vote.NoCount, vote.RequiredVotes,
                    voterJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log sleep kick vote cast to DB");
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var g = _client.GetGuild(guildId);
                if (g != null)
                    await UpdateLogMessageAsync(vote, g, "投票中");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update log message after vote cast");
            }
        });

        if (remaining == 0)
        {
            if (vote.IsConcluding)
            {
                await component.RespondAsync("現在切断待機中です。", ephemeral: true);
                return;
            }

            if (vote.NoCount >= vote.YesCount)
            {
                var rejectKey = $"{guildId}:{targetUserId}:{messageId}";
                ActiveVotes.TryRemove(rejectKey, out _);

                var rejectText = $"投票の結果、切断は見送られました。（賛成: {vote.YesCount}, 反対: {vote.NoCount}）";
                await component.UpdateAsync(props =>
                {
                    props.Embed = BuildResultEmbed(vote, false, rejectText);
                    props.Components = new ComponentBuilder().Build();
                });

                var guildObj = _client.GetGuild(guildId);
                if (guildObj != null)
                {
                    await _audit.SendSystemAsync(
                        guildObj,
                        "キック投票見送り",
                        $"対象: {MentionUtils.MentionUser(targetUserId)}\n" +
                        $"結果: 見送り\n" +
                        $"賛成: {vote.YesCount} 票\n" +
                        $"反対: {vote.NoCount} 票\n" +
                        $"投票者:\n{FormatVoterDetails(vote)}",
                        Color.Blue);
                }

                await LogVoteResultToDbAsync(vote, "rejected", "vote_rejected");

                if (guildObj != null)
                    await UpdateLogMessageAsync(vote, guildObj, "見送り");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    await DeleteVoteMessageAsync(vote);
                });

                return;
            }

            vote.IsConcluding = true;

            await component.UpdateAsync(props =>
            {
                props.Embed = new EmbedBuilder()
                    .WithTitle("😴 最終確認")
                    .WithDescription($"{MentionUtils.MentionUser(targetUserId)} の切断が可決されました。\n60秒後に切断を実行します。\n(本人がVCミュート解除やチャット発言などを行うと自動キャンセルされます)")
                    .WithColor(Color.Red)
                    .WithCurrentTimestamp()
                    .Build();
                props.Components = new ComponentBuilder().Build();
            });

            await component.FollowupAsync(text: $"{MentionUtils.MentionUser(targetUserId)} 切断が可決されました！60秒以内に反応がない場合は切断されます。", ephemeral: false);

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await ConcludeVoteAsync(vote);
            });
            return;
        }

        await component.UpdateAsync(props =>
        {
            props.Embed = BuildVoteEmbed(vote, remaining);
            props.Components = BuildVoteComponents(vote);
        });

        if (isYes)
        {
            await component.FollowupAsync($"切断に賛成しました。全員の投票まであと {remaining} 票です。", ephemeral: true);
        }
        else
        {
            await component.FollowupAsync("切断に反対しました。", ephemeral: true);
        }
    }

    private async Task ConcludeVoteAsync(SleepKickVoteState vote, SocketMessageComponent? component = null)
    {
        var voteKey = $"{vote.GuildId}:{vote.TargetUserId}:{vote.MessageId}";
        if (!ActiveVotes.TryRemove(voteKey, out _))
        {
            // Already concluded
            return;
        }

        var guildObj = _client.GetGuild(vote.GuildId);
        var target = guildObj?.GetUser(vote.TargetUserId);
        
        bool success = vote.YesCount > vote.NoCount;

        if (success && target?.VoiceChannel != null)
        {
            var settings = await _db.GetGuildSettingsAsync(vote.GuildId);
            var requireAdmin = settings?.SleepKickAdminConfirmationRequired ?? _options.SleepKickRequiresAdminConfirmation;

            if (requireAdmin && guildObj != null)
            {
                vote.IsPendingAdminConfirmation = true;
                ActiveVotes[voteKey] = vote;

                await PostAdminConfirmationAsync(vote, guildObj, target);

                var pendingText = $"{MentionUtils.MentionUser(vote.TargetUserId)} の切断が可決されました。管理者の確認待ちです。（賛成: {vote.YesCount}, 反対: {vote.NoCount}）";
                await UpdateVoteMessageAsync(vote, guildObj, component, pendingText, true);

                await _audit.SendSystemAsync(
                    guildObj,
                    "キック投票 管理者確認待ち",
                    $"対象: {MentionUtils.MentionUser(vote.TargetUserId)}\n" +
                    $"賛成: {vote.YesCount} 票\n" +
                    $"反対: {vote.NoCount} 票\n" +
                    $"投票者:\n{FormatVoterDetails(vote)}",
                    Color.Orange);

                await LogVoteResultToDbAsync(vote, "admin_pending", "pending_admin");

                await UpdateLogMessageAsync(vote, guildObj, "管理者確認待ち");

                return;
            }
        }

        if (success && target?.VoiceChannel != null)
        {
            try
            {
                await _db.EndVoiceSessionByUserAsync(vote.GuildId, vote.TargetUserId, vote.CreatedAt);
                await target.ModifyAsync(props => props.Channel = null);
                _logger.LogInformation("Sleep kick executed. GuildId={GuildId}, TargetUserId={TargetUserId}", vote.GuildId, vote.TargetUserId);
                LastVoteTimestamps[vote.TargetUserId] = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute sleep kick. GuildId={GuildId}, TargetUserId={TargetUserId}", vote.GuildId, vote.TargetUserId);
                success = false;
            }
        }
        else if (success)
        {
            // success, but user already left
            success = false;
        }
        
        var messageText = success 
            ? $"{MentionUtils.MentionUser(vote.TargetUserId)} をVCから切断しました。（賛成: {vote.YesCount}, 反対: {vote.NoCount}）" 
            : $"投票の結果、切断は見送られました。（賛成: {vote.YesCount}, 反対: {vote.NoCount}）";

        if (guildObj != null)
        {
            await _audit.SendSystemAsync(
                guildObj,
                success ? "キック投票実行" : "キック投票見送り",
                $"対象: {MentionUtils.MentionUser(vote.TargetUserId)}\n" +
                $"結果: {(success ? "切断" : "見送り")}\n" +
                $"賛成: {vote.YesCount} 票\n" +
                $"反対: {vote.NoCount} 票\n" +
                $"投票者:\n{FormatVoterDetails(vote)}",
                success ? Color.Red : Color.Blue);
        }

        await LogVoteResultToDbAsync(vote, success ? "kicked" : "rejected", success ? "disconnected" : "vote_rejected");

        if (success)
            await DeleteRegistrationReviewMessageAsync(vote.GuildId, vote.TargetUserId);

        await UpdateVoteMessageAsync(vote, guildObj, component, messageText, success);

        if (guildObj != null)
            await UpdateLogMessageAsync(vote, guildObj, success ? "切断済み" : "見送り");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await DeleteVoteMessageAsync(vote);
        });
    }

    public async Task HandleAdminConfirmAsync(SocketMessageComponent component, ulong guildId, ulong targetUserId, ulong messageId)
    {
        var voteKey = $"{guildId}:{targetUserId}:{messageId}";
        if (!ActiveVotes.TryGetValue(voteKey, out var vote) || !vote.IsPendingAdminConfirmation)
        {
            await component.RespondAsync("この確認は既に処理済みか期限切れです。", ephemeral: true);
            return;
        }

        var guildObj = _client.GetGuild(vote.GuildId);
        var target = guildObj?.GetUser(vote.TargetUserId);

        if (target?.VoiceChannel == null)
        {
            ActiveVotes.TryRemove(voteKey, out _);
            await component.UpdateAsync(props =>
            {
                props.Embed = BuildAdminConfirmationResultEmbed(vote, false, "対象ユーザーは既にVCから退出しています。");
                props.Components = new ComponentBuilder().Build();
            });
            await UpdateVoteMessageForAdminResultAsync(vote, guildObj, false);
            if (guildObj != null)
                await UpdateLogMessageAsync(vote, guildObj, "見送り（ユーザー退出）");
            return;
        }

        try
        {
            await _db.EndVoiceSessionByUserAsync(vote.GuildId, vote.TargetUserId, vote.CreatedAt);
            await target.ModifyAsync(props => props.Channel = null);
            _logger.LogInformation("Sleep kick executed via admin confirmation. GuildId={GuildId}, TargetUserId={TargetUserId}", vote.GuildId, vote.TargetUserId);
            LastVoteTimestamps[vote.TargetUserId] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute sleep kick via admin confirmation. GuildId={GuildId}, TargetUserId={TargetUserId}", vote.GuildId, vote.TargetUserId);
            ActiveVotes.TryRemove(voteKey, out _);
            await component.UpdateAsync(props =>
            {
                props.Embed = BuildAdminConfirmationResultEmbed(vote, false, "切断に失敗しました。");
                props.Components = new ComponentBuilder().Build();
            });
            if (guildObj != null)
                await UpdateLogMessageAsync(vote, guildObj, "切断失敗");
            return;
        }

        ActiveVotes.TryRemove(voteKey, out _);

        var successText = $"{MentionUtils.MentionUser(vote.TargetUserId)} をVCから切断しました。（管理者確認）";
        await component.UpdateAsync(props =>
        {
            props.Embed = BuildAdminConfirmationResultEmbed(vote, true, successText);
            props.Components = new ComponentBuilder().Build();
        });

        await UpdateVoteMessageForAdminResultAsync(vote, guildObj, true);

        if (guildObj != null)
        {
            await _audit.SendSystemAsync(
                guildObj,
                "キック投票実行（管理者確認）",
                $"対象: {MentionUtils.MentionUser(vote.TargetUserId)}\n" +
                $"確認者: {MentionUtils.MentionUser(component.User.Id)}\n" +
                $"賛成: {vote.YesCount} 票 / 反対: {vote.NoCount} 票",
                Color.Red);

            await UpdateLogMessageAsync(vote, guildObj, $"切断済み（管理者: {component.User.GlobalName ?? component.User.Username}）");
        }

        await DeleteRegistrationReviewMessageAsync(vote.GuildId, vote.TargetUserId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await DeleteVoteMessageAsync(vote);
        });
    }

    public async Task HandleAdminRejectAsync(SocketMessageComponent component, ulong guildId, ulong targetUserId, ulong messageId)
    {
        var voteKey = $"{guildId}:{targetUserId}:{messageId}";
        if (!ActiveVotes.TryRemove(voteKey, out var vote) || !vote.IsPendingAdminConfirmation)
        {
            await component.RespondAsync("この確認は既に処理済みか期限切れです。", ephemeral: true);
            return;
        }

        var guildObj = _client.GetGuild(vote.GuildId);

        var rejectText = $"管理者により切断が拒否されました。";
        await component.UpdateAsync(props =>
        {
            props.Embed = BuildAdminConfirmationResultEmbed(vote, false, rejectText);
            props.Components = new ComponentBuilder().Build();
        });

        await UpdateVoteMessageForAdminResultAsync(vote, guildObj, false);

        if (guildObj != null)
        {
            await _audit.SendSystemAsync(
                guildObj,
                "キック投票拒否（管理者確認）",
                $"対象: {MentionUtils.MentionUser(vote.TargetUserId)}\n" +
                $"確認者: {MentionUtils.MentionUser(component.User.Id)}\n" +
                $"賛成: {vote.YesCount} 票 / 反対: {vote.NoCount} 票",
                Color.Blue);

            await UpdateLogMessageAsync(vote, guildObj, $"見送り（管理者拒否: {component.User.GlobalName ?? component.User.Username}）");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await DeleteVoteMessageAsync(vote);
        });
    }

    private async Task UpdateVoteMessageAsync(SleepKickVoteState vote, SocketGuild? guildObj, SocketMessageComponent? component, string messageText, bool success)
    {
        if (component != null)
        {
            await component.UpdateAsync(props =>
            {
                props.Embed = BuildResultEmbed(vote, success, messageText);
                props.Components = new ComponentBuilder().Build();
            });
        }
        else if (guildObj != null)
        {
            await ModifyVoteMessageInChannelAsync(guildObj, vote, BuildResultEmbed(vote, success, messageText), null);
        }
    }

    private async Task UpdateVoteMessageForAdminResultAsync(SleepKickVoteState vote, SocketGuild? guildObj, bool success)
    {
        if (guildObj == null) return;

        var messageText = success
            ? $"{MentionUtils.MentionUser(vote.TargetUserId)} をVCから切断しました。（管理者確認）（賛成: {vote.YesCount}, 反対: {vote.NoCount}）"
            : $"管理者により切断は見送られました。（賛成: {vote.YesCount}, 反対: {vote.NoCount}）";

        await ModifyVoteMessageInChannelAsync(guildObj, vote, BuildResultEmbed(vote, success, messageText), null);
    }

    private async Task PostAdminConfirmationAsync(SleepKickVoteState vote, SocketGuild guild, SocketGuildUser target)
    {
        var embed = new EmbedBuilder()
            .WithTitle("寝落ち切断 管理者確認")
            .WithDescription(
                $"{MentionUtils.MentionUser(vote.TargetUserId)} の寝落ち切断が可決されました。\n" +
                $"VC: {target.VoiceChannel?.Name ?? "不明"}\n" +
                $"賛成: {vote.YesCount} 票 / 反対: {vote.NoCount} 票\n\n" +
                $"切断を実行しますか？")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();

        var components = new ComponentBuilder()
            .WithButton("切断実行", BuildAdminConfirmCustomId(vote.GuildId, vote.TargetUserId, vote.MessageId), ButtonStyle.Danger)
            .WithButton("拒否", BuildAdminRejectCustomId(vote.GuildId, vote.TargetUserId, vote.MessageId), ButtonStyle.Secondary)
            .Build();

        try
        {
            var logChannel = await ResolveAdminConfirmChannelAsync(guild);
            if (logChannel != null)
            {
                var message = await logChannel.SendMessageAsync(embed: embed, components: components);
                vote.AdminConfirmationChannelId = logChannel.Id;
                vote.AdminConfirmationMessageId = message.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post admin confirmation for sleep kick. GuildId={GuildId}", vote.GuildId);
        }
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

    private static Embed BuildAdminConfirmationResultEmbed(SleepKickVoteState vote, bool success, string messageText)
    {
        return new EmbedBuilder()
            .WithTitle(success ? "寝落ち切断 実行済み" : "寝落ち切断 拒否")
            .WithDescription(messageText)
            .WithColor(success ? Color.Red : Color.Blue)
            .WithCurrentTimestamp()
            .Build();
    }

    private async Task EvaluateVoteTimeoutAsync(string voteKey)
    {
        if (ActiveVotes.TryGetValue(voteKey, out var vote))
        {
            _logger.LogInformation("Sleep kick vote timed out. GuildId={GuildId}, TargetUserId={TargetUserId}, Yes={Yes}, No={No}",
                vote.GuildId, vote.TargetUserId, vote.YesCount, vote.NoCount);
            await ConcludeVoteAsync(vote);
            await DeleteVoteMessageAsync(vote);
        }
    }

    public async Task CancelVoteAsync(ulong guildId, ulong targetUserId, string reason)
    {
        var voteKeys = ActiveVotes.Where(kvp => kvp.Value.GuildId == guildId && kvp.Value.TargetUserId == targetUserId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in voteKeys)
        {
            if (ActiveVotes.TryRemove(key, out var vote))
            {
                try
                {
                    var guild = _client.GetGuild(vote.GuildId);
                    if (guild == null) continue;

                    var channelMention = vote.IsVoiceChannel ? $"<#{vote.ChannelId}>" : MentionUtils.MentionChannel(vote.ChannelId);
                    await _audit.SendSystemAsync(
                        guild,
                        "キック投票キャンセル",
                        $"対象: {MentionUtils.MentionUser(targetUserId)}\n" +
                        $"理由: {reason}\n" +
                        $"チャンネル: {channelMention}\n" +
                        $"投票者:\n{FormatVoterDetails(vote)}",
                        Color.Blue);

        await LogVoteResultToDbAsync(vote, "cancelled", reason);

                    await DeleteRegistrationReviewMessageAsync(vote.GuildId, vote.TargetUserId);

                    await ModifyVoteMessageInChannelAsync(guild, vote, BuildResultEmbed(vote, false, reason), null);

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        await DeleteVoteMessageAsync(vote);
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cancel sleep kick vote UI for user {UserId}", targetUserId);
                }
            }
        }
    }

    public async Task HandleLogCancelAsync(SocketMessageComponent component, ulong guildId, ulong targetUserId, ulong messageId)
    {
        var voteKeys = ActiveVotes.Where(kvp => kvp.Value.GuildId == guildId && kvp.Value.TargetUserId == targetUserId)
            .Select(kvp => kvp.Key)
            .ToList();

        if (voteKeys.Count == 0)
        {
            await component.RespondAsync("この投票は既に終了しています。", ephemeral: true);
            return;
        }

        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            await component.RespondAsync("サーバー情報の取得に失敗しました。", ephemeral: true);
            return;
        }

        var cancelledBy = component.User.GlobalName ?? component.User.Username;

        var vote = ActiveVotes.GetValueOrDefault(voteKeys[0]);
        await CancelVoteAsync(guildId, targetUserId, $"logチャンネルからキャンセル（{cancelledBy}）");

        if (vote != null)
        {
            await component.UpdateAsync(props =>
            {
                props.Embed = BuildLogEmbed(vote, $"キャンセル済み（{cancelledBy}）");
                props.Components = new ComponentBuilder().Build();
            });
        }
    }

    private async Task UpdateLogMessageAsync(SleepKickVoteState vote, SocketGuild guild, string status, string? detail = null)
    {
        if (vote.LogChannelId == 0 || vote.LogMessageId == 0)
            return;

        try
        {
            var channel = guild.GetTextChannel(vote.LogChannelId);
            if (channel != null && await channel.GetMessageAsync(vote.LogMessageId) is IUserMessage logMsg)
            {
                var embed = BuildLogEmbed(vote, status, detail);

                var isFinal = status is "キャンセル済み" or "キャンセル済み（本人反応）" or "見送り" or "切断済み" or "管理者確認待ち"
                    || status.StartsWith("キャンセル済み");

                await logMsg.ModifyAsync(props =>
                {
                    props.Embed = embed;
                    if (isFinal)
                        props.Components = new ComponentBuilder().Build();
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update log message for sleep kick vote. GuildId={GuildId}", vote.GuildId);
        }
    }

    private Embed BuildLogEmbed(SleepKickVoteState vote, string status, string? detail = null)
    {
        var channelMention = vote.IsVoiceChannel ? $"<#{vote.ChannelId}>" : MentionUtils.MentionChannel(vote.ChannelId);

        var voterLines = new List<string>();
        if (vote.VoterDetails.Count > 0)
        {
            foreach (var v in vote.VoterDetails)
            {
                voterLines.Add($"{(v.IsYes ? "✅" : "❌")} {MentionUtils.MentionUser(v.UserId)} ({v.UserName})");
            }
        }
        else
        {
            voterLines.Add("なし");
        }

        var description = $"対象: {MentionUtils.MentionUser(vote.TargetUserId)}\n" +
                          $"チャンネル: {channelMention}\n" +
                          $"ステータス: {status}\n" +
                          $"賛成: {vote.YesCount} 票 / 反対: {vote.NoCount} 票 （必要: {vote.RequiredVotes} 票）\n" +
                          $"投票者:\n{string.Join("\n", voterLines)}";

        if (!string.IsNullOrWhiteSpace(detail))
            description += $"\n{detail}";

        var color = status switch
        {
            _ when status.StartsWith("切断済み") => Color.Red,
            _ when status.StartsWith("見送り") => Color.Blue,
            _ when status.StartsWith("キャンセル") => Color.Blue,
            _ when status.StartsWith("切断失敗") => Color.Blue,
            _ => Color.Orange
        };

        return new EmbedBuilder()
            .WithTitle("キック投票")
            .WithDescription(description)
            .WithColor(color)
            .WithCurrentTimestamp()
            .Build();
    }

    private async Task PostSleepKickVoteAsync(SocketGuildUser targetUser, SocketVoiceChannel voiceChannel, DateTimeOffset joinTime, ITextChannel? fallbackChannel, ulong? initiatorUserId = null, string? initiatorUserName = null)
    {
        var duration = DateTimeOffset.UtcNow - joinTime;
        var eligibleVoters = voiceChannel.ConnectedUsers.Count(u => !u.IsBot && u.Id != targetUser.Id);
        var requiredVotes = Math.Max(1, eligibleVoters);

        var voteKey = $"{voiceChannel.Guild.Id}:{targetUser.Id}:0";
        var endTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var voteProgressText = initiatorUserId.HasValue && initiatorUserName != null
            ? $"✅ 賛成: 1 票（{initiatorUserName}）\n❌ 反対: 0 票\n\n残り {requiredVotes - 1} 票の投票です。\n"
            : $"※全員({requiredVotes}人)が投票するか、5分経過時点で賛成が多ければ切断されます。\n";

        var embed = new EmbedBuilder()
            .WithTitle("😴 キック投票")
            .WithDescription(
                $"{MentionUtils.MentionUser(targetUser.Id)} の寝落ち・離席投票が開始しました。\n" +
                $"接続時間: {FormatDuration(duration)}\n" +
                $"ミュート状態: {(targetUser.IsSelfMuted ? "自分でミュート" : targetUser.IsMuted ? "サーバーミュート" : "未ミュート")}\n\n" +
                $"この人をVCから切断しますか？\n{voteProgressText}" +
                $"(5分経過で自動終了します)\n\n" +
                $"**【対象の方へ】**\n" +
                $"起きている場合は、下の「⛔ 起きてるよ！」ボタンを押すか、ミュート解除・チャット発言などをすると投票をキャンセルできます。")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();

        var initialButtons = new Btn[]
        {
            new("✅ はい", 3, BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, 0)),
            new("❌ いいえ", 2, BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, 0))
        };

        var targetChannelId = voiceChannel.Id;
        var isVoiceChannel = true;
        var usedImessageChannel = false;
        ulong messageId = 0;

        var voiceTextChannel = TryGetVoiceTextChannel(voiceChannel);

        if (voiceTextChannel != null)
        {
            var cb = new ComponentBuilder()
                .WithButton("✅ はい", BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, 0), ButtonStyle.Success)
                .WithButton("❌ いいえ", BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, 0), ButtonStyle.Secondary);

            try
            {
                var message = await voiceTextChannel.SendMessageAsync(
                    text: MentionUtils.MentionUser(targetUser.Id), embed: embed, components: cb.Build());
                messageId = message.Id;
                usedImessageChannel = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send vote to voice channel text {ChannelId}, falling back", voiceChannel.Id);
                voiceTextChannel = null;
            }
        }

        if (!usedImessageChannel)
        {
            var httpMsgId = await SendMessageViaHttpAsync(
                targetChannelId, MentionUtils.MentionUser(targetUser.Id), embed, initialButtons);

            if (httpMsgId.HasValue)
            {
                messageId = httpMsgId.Value;
            }
            else if (fallbackChannel != null)
            {
                targetChannelId = fallbackChannel.Id;
                isVoiceChannel = false;

                var cb = new ComponentBuilder()
                    .WithButton("✅ はい", BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, 0), ButtonStyle.Success)
                    .WithButton("❌ いいえ", BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, 0), ButtonStyle.Secondary);

                var message = await fallbackChannel.SendMessageAsync(
                    text: MentionUtils.MentionUser(targetUser.Id), embed: embed, components: cb.Build());
                messageId = message.Id;
                usedImessageChannel = true;
            }
            else
            {
                _logger.LogWarning("No available channel to post sleep kick vote for user {UserId}", targetUser.Id);
                return;
            }
        }

        var newVoteKey = $"{voiceChannel.Guild.Id}:{targetUser.Id}:{messageId}";

        var initialVoterIds = new HashSet<ulong>();
        var initialVoterDetails = new List<(ulong UserId, string? UserName, bool IsYes)>();
        var initialYesCount = 0;

        if (initiatorUserId.HasValue && initiatorUserId.Value != targetUser.Id)
        {
            initialVoterIds.Add(initiatorUserId.Value);
            initialVoterDetails.Add((initiatorUserId.Value, initiatorUserName, true));
            initialYesCount = 1;
        }

        var vote = new SleepKickVoteState
        {
            GuildId = voiceChannel.Guild.Id,
            TargetUserId = targetUser.Id,
            TargetUserName = targetUser.DisplayName ?? targetUser.GlobalName ?? targetUser.Username,
            MessageId = messageId,
            ChannelId = targetChannelId,
            ChannelName = voiceChannel.Name,
            IsVoiceChannel = isVoiceChannel,
            InitiatorUserId = initiatorUserId,
            InitiatorUserName = initiatorUserName,
            VoterIds = initialVoterIds,
            VoterDetails = initialVoterDetails,
            YesCount = initialYesCount,
            NoCount = 0,
            RequiredVotes = requiredVotes,
            EndTime = endTime,
            CreatedAt = DateTimeOffset.UtcNow
        };

        ActiveVotes[newVoteKey] = vote;

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.InsertSleepKickLogAsync(
                    vote.GuildId, vote.TargetUserId, vote.TargetUserName,
                    vote.InitiatorUserId, vote.InitiatorUserName,
                    voiceChannel.Id, voiceChannel.Name,
                    "vote_started",
                    result: "pending");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log sleep kick vote start to DB");
            }
        });

        var updatedButtons = new Btn[]
        {
            new("✅ はい", 3, BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId)),
            new("❌ いいえ", 2, BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId))
        };

        if (usedImessageChannel)
        {
            var ch = _client.GetChannel(targetChannelId) as IMessageChannel
                ?? voiceTextChannel
                ?? _client.GetGuild(voiceChannel.Guild.Id)?.GetTextChannel(targetChannelId);
            if (ch != null && await ch.GetMessageAsync(messageId) is IUserMessage msg)
            {
                var updateCb = new ComponentBuilder()
                    .WithButton("✅ はい", BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId), ButtonStyle.Success)
                    .WithButton("❌ いいえ", BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId), ButtonStyle.Secondary);
                await msg.ModifyAsync(props => props.Components = updateCb.Build());
            }
        }
        else if (isVoiceChannel)
        {
            await ModifyMessageViaHttpAsync(targetChannelId, messageId, embed, updatedButtons);
        }
        else
        {
            var guild = _client.GetGuild(voiceChannel.Guild.Id);
            var textCh = guild?.GetTextChannel(targetChannelId);
            if (textCh != null && await textCh.GetMessageAsync(messageId) is IUserMessage msg2)
            {
                var updateCb = new ComponentBuilder()
                    .WithButton("✅ はい", BuildYesCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId), ButtonStyle.Success)
                    .WithButton("❌ いいえ", BuildNoCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId), ButtonStyle.Secondary);
                await msg2.ModifyAsync(props => props.Components = updateCb.Build());
            }
        }

        _logger.LogInformation("Sleep kick vote posted. GuildId={GuildId}, TargetUserId={TargetUserId}, MessageId={MessageId}, IsVoiceChannel={IsVoiceChannel}",
            voiceChannel.Guild.Id, targetUser.Id, messageId, isVoiceChannel);

        var guildObj = _client.GetGuild(voiceChannel.Guild.Id);
        var channelMention = isVoiceChannel ? $"<#{targetChannelId}>" : MentionUtils.MentionChannel(targetChannelId);
        if (guildObj != null)
        {
            await _audit.SendSystemAsync(
                guildObj,
                "キック投票開始",
                $"{MentionUtils.MentionUser(targetUser.Id)} のキック投票を開始しました。\nチャンネル: {channelMention}",
                Color.Orange);

            try
            {
                var logChannel = await ResolveAdminConfirmChannelAsync(guildObj);
                if (logChannel != null)
                {
                    var logEmbed = BuildLogEmbed(vote, "投票中");

                    var logComponents = new ComponentBuilder()
                        .WithButton("キャンセル", BuildLogCancelCustomId(voiceChannel.Guild.Id, targetUser.Id, messageId), ButtonStyle.Danger)
                        .Build();

                    var logMessage = await logChannel.SendMessageAsync(embed: logEmbed, components: logComponents, flags: MessageFlags.SuppressNotification);
                    vote.LogChannelId = logChannel.Id;
                    vote.LogMessageId = logMessage.Id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post cancel button to log channel for sleep kick vote. GuildId={GuildId}", voiceChannel.Guild.Id);
            }
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            await EvaluateVoteTimeoutAsync(newVoteKey);
        });
    }

    private Embed BuildVoteEmbed(SleepKickVoteState vote, int remaining)
    {
        return new EmbedBuilder()
            .WithTitle("😴 キック投票")
            .WithDescription(
                $"{MentionUtils.MentionUser(vote.TargetUserId)} の切断投票進行中...\n" +
                $"✅ 賛成: {vote.YesCount} 票\n" +
                $"❌ 反対: {vote.NoCount} 票\n\n" +
                $"全員の投票まであと {remaining} 票です。\n" +
                $"(5分経過で自動終了します)\n\n" +
                $"**【対象の方へ】**\n" +
                $"起きている場合は、下の「⛔ 起きてるよ！」ボタンを押すか、ミュート解除・チャット発言などをすると投票をキャンセルできます。")
            .WithColor(Color.Orange)
            .WithCurrentTimestamp()
            .Build();
    }

    private static string FormatVoterDetails(SleepKickVoteState vote)
    {
        if (vote.VoterDetails.Count == 0)
            return "なし";
        var lines = vote.VoterDetails.Select(v =>
            $"{MentionUtils.MentionUser(v.UserId)} ({v.UserName}): {(v.IsYes ? "賛成" : "反対")}");
        return string.Join("\n", lines);
    }

    private static string SerializeVoterDetailsForDb(SleepKickVoteState vote)
    {
        if (vote.VoterDetails.Count == 0)
            return "[]";
        return JsonSerializer.Serialize(vote.VoterDetails.Select(v => new
        {
            user_id = v.UserId,
            user_name = v.UserName,
            vote = v.IsYes ? "yes" : "no"
        }));
    }

    private async Task LogVoteResultToDbAsync(SleepKickVoteState vote, string eventType, string result)
    {
        try
        {
            await _db.InsertSleepKickLogAsync(
                vote.GuildId, vote.TargetUserId, vote.TargetUserName,
                vote.InitiatorUserId, vote.InitiatorUserName,
                vote.ChannelId, vote.ChannelName,
                eventType,
                vote.YesCount, vote.NoCount, vote.RequiredVotes,
                SerializeVoterDetailsForDb(vote),
                result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log sleep kick result to DB");
        }
    }

    private MessageComponent BuildVoteComponents(SleepKickVoteState vote)
    {
        return new ComponentBuilder()
            .WithButton("✅ はい", BuildYesCustomId(vote.GuildId, vote.TargetUserId, vote.MessageId), ButtonStyle.Success)
            .WithButton("❌ いいえ", BuildNoCustomId(vote.GuildId, vote.TargetUserId, vote.MessageId), ButtonStyle.Secondary)
            .WithButton("⛔ 起きてるよ！(本人のみ)", BuildCancelCustomId(vote.GuildId, vote.TargetUserId, vote.MessageId), ButtonStyle.Danger)
            .Build();
    }

    private Embed BuildResultEmbed(SleepKickVoteState vote, bool success, string message)
    {
        return new EmbedBuilder()
            .WithTitle(success ? "✅ 完結済み（切断）" : "ℹ️ 完結済み（見送り）")
            .WithDescription(message)
            .WithColor(success ? Color.Green : Color.Blue)
            .WithCurrentTimestamp()
            .Build();
    }

    private async Task DeleteRegistrationReviewMessageAsync(ulong guildId, ulong targetUserId)
    {
        try
        {
            var profile = await _db.GetDiscordUserProfileAsync(targetUserId);
            if (profile?.RegistrationReviewMessageId is not { } msgId || msgId == 0)
                return;

            var guild = _client.GetGuild(guildId);
            if (guild == null) return;

            var settings = await _db.GetGuildSettingsAsync(guildId);
            if (settings?.RegistrationReviewChannelId == 0) return;

            var channel = guild.GetTextChannel((ulong)settings.RegistrationReviewChannelId);
            if (channel == null) return;

            var msg = await channel.GetMessageAsync((ulong)msgId);
            if (msg != null)
            {
                await msg.DeleteAsync();
                await _db.UpdateDiscordUserRegistrationReviewMessageIdAsync(targetUserId, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete registration review message. GuildId={GuildId}, UserId={UserId}", guildId, targetUserId);
        }
    }

    private static DateTimeOffset? GetEstimatedJoinTime(SocketGuildUser user)
    {
        // Discord doesn't provide join time directly; we use the user's activity or fallback to now
        // For simplicity, we rely on an external tracking or use a heuristic.
        // Since this service is polled periodically, we'll track join times via a dictionary
        // populated from VoiceStateUpdated events in DiscordBotService.
        return SleepKickJoinTracker.GetJoinTime(user.Id);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:F1}時間";
        if (duration.TotalMinutes >= 1)
            return $"{duration.TotalMinutes:F0}分";
        return $"{duration.TotalSeconds:F0}秒";
    }

    #region Voice Channel Text Chat Helpers

    private static IMessageChannel? TryGetVoiceTextChannel(SocketVoiceChannel voiceChannel)
    {
        return voiceChannel as IMessageChannel;
    }

    private static JsonElement SerializeEmbed(Embed embed)
    {
        var obj = new Dictionary<string, object?>();
        if (embed.Title != null) obj["title"] = embed.Title;
        if (embed.Description != null) obj["description"] = embed.Description;
        if (embed.Color.HasValue) obj["color"] = (int)embed.Color.Value.RawValue;
        if (embed.Timestamp.HasValue) obj["timestamp"] = embed.Timestamp.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj, _jsonOpts));
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private record struct Btn(string Label, int Style, string CustomId);

    private static JsonElement SerializeButtons(Btn[]? buttons)
    {
        if (buttons == null || buttons.Length == 0)
        {
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new[]
            {
                new { type = 1, components = Array.Empty<object>() }
            }));
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new[]
        {
            new
            {
                type = 1,
                components = buttons.Select(b => (object)new Dictionary<string, object>
                {
                    ["type"] = 2,
                    ["style"] = b.Style,
                    ["label"] = b.Label,
                    ["custom_id"] = b.CustomId
                }).ToArray()
            }
        }));
    }

    private async Task<ulong?> SendMessageViaHttpAsync(ulong channelId, string? text, Embed embed, Btn[]? buttons)
    {
        var payload = new Dictionary<string, object?>
        {
            ["embeds"] = new[] { SerializeEmbed(embed) },
            ["components"] = new[] { SerializeButtons(buttons) }
        };
        if (text != null) payload["content"] = text;

        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync(
                $"https://discord.com/api/v10/channels/{channelId}/messages", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("HTTP message send failed. Channel={ChannelId} Status={Status} Body={Body}",
                    channelId, (int)response.StatusCode, errorBody);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.GetProperty("id").GetUInt64();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception sending message via HTTP to channel {ChannelId}", channelId);
            return null;
        }
    }

    private async Task ModifyMessageViaHttpAsync(ulong channelId, ulong messageId, Embed embed, Btn[]? buttons)
    {
        var payload = new Dictionary<string, object?>
        {
            ["embeds"] = new[] { SerializeEmbed(embed) },
            ["components"] = new[] { SerializeButtons(buttons) }
        };

        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PatchAsync(
                $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("HTTP message modify failed. Channel={ChannelId} Message={MessageId} Status={Status}",
                    channelId, messageId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception modifying message via HTTP. Channel={ChannelId} Message={MessageId}", channelId, messageId);
        }
    }

    private async Task DeleteVoteMessageAsync(SleepKickVoteState vote)
    {
        var guild = _client.GetGuild(vote.GuildId);
        if (guild == null) return;

        try
        {
            if (!vote.IsVoiceChannel)
            {
                var textChannel = guild.GetTextChannel(vote.ChannelId);
                if (textChannel != null)
                {
                    await textChannel.DeleteMessageAsync(vote.MessageId);
                    return;
                }
            }

            var response = await _httpClient.DeleteAsync(
                $"https://discord.com/api/v10/channels/{vote.ChannelId}/messages/{vote.MessageId}");
            if (!response.IsSuccessStatusCode)
            {
                var textChannel = guild.GetTextChannel(vote.ChannelId);
                if (textChannel != null)
                    await textChannel.DeleteMessageAsync(vote.MessageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete vote message. Channel={ChannelId} Message={MessageId}", vote.ChannelId, vote.MessageId);
        }
    }

    private async Task ModifyVoteMessageInChannelAsync(SocketGuild guild, SleepKickVoteState vote, Embed embed, Btn[]? buttons)
    {
        if (!vote.IsVoiceChannel)
        {
            var textChannel = guild.GetTextChannel(vote.ChannelId);
            if (textChannel == null) return;

            try
            {
                if (await textChannel.GetMessageAsync(vote.MessageId) is IUserMessage message)
                {
                    ComponentBuilder? cb = null;
                    if (buttons != null)
                    {
                        cb = new ComponentBuilder();
                        foreach (var btn in buttons)
                            cb.WithButton(btn.Label, btn.CustomId, (ButtonStyle)btn.Style);
                    }
                    await message.ModifyAsync(props =>
                    {
                        props.Embed = embed;
                        props.Components = cb?.Build() ?? new ComponentBuilder().Build();
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to modify vote message for user {UserId}", vote.TargetUserId);
            }
            return;
        }

        await ModifyMessageViaHttpAsync(vote.ChannelId, vote.MessageId, embed, buttons);
    }

    #endregion
}

public sealed class SleepKickVoteState
{
    public ulong GuildId { get; init; }
    public ulong TargetUserId { get; init; }
    public string? TargetUserName { get; init; }
    public ulong MessageId { get; init; }
    public ulong ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public bool IsVoiceChannel { get; init; }
    public ulong? InitiatorUserId { get; init; }
    public string? InitiatorUserName { get; init; }
    public HashSet<ulong> VoterIds { get; init; } = new();
    public List<(ulong UserId, string? UserName, bool IsYes)> VoterDetails { get; init; } = [];
    public int YesCount { get; set; }
    public int NoCount { get; set; }
    public int RequiredVotes { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsConcluding { get; set; }
    public bool IsPendingAdminConfirmation { get; set; }
    public ulong AdminConfirmationChannelId { get; set; }
    public ulong AdminConfirmationMessageId { get; set; }
    public ulong LogChannelId { get; set; }
    public ulong LogMessageId { get; set; }
}

public static class SleepKickJoinTracker
{
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> JoinTimes = new();

    public static void SetJoinTime(ulong userId, DateTimeOffset time)
    {
        JoinTimes[userId] = time;
    }

    public static void RemoveJoinTime(ulong userId)
    {
        JoinTimes.TryRemove(userId, out _);
    }

    public static DateTimeOffset? GetJoinTime(ulong userId)
    {
        return JoinTimes.TryGetValue(userId, out var time) ? time : null;
    }
}
