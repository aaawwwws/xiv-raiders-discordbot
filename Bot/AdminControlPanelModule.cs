using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Bot;

[Group("adminpanel", "ADMINコントロールパネル")]
public sealed class AdminControlPanelModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly Db _db;
    private readonly DiscordOptions _options;
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);

    public AdminControlPanelModule(Db db, IOptions<DiscordOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [RequireBotAdmin]
    [SlashCommand("setup", "このチャンネルにADMINコントロールパネルを設置します")]
    public async Task SetupAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync("このチャンネルにはパネルを設置できません。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var existing = await _db.GetGuildAdminControlPanelAsync(Context.Guild.Id);
        ulong? existingMessageId = existing is null || existing.MessageId == 0 ? null : ToUlong(existing.MessageId);
        var message = await EnsureAdminControlPanelMessageAsync(channel, existingMessageId);
        await _db.UpsertGuildAdminControlPanelAsync(Context.Guild.Id, channel.Id, message.Id);
        await FollowupAsync($"このチャンネルにADMINコントロールパネルを設置しました。(message: {message.Id})", ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ExportMessageCsvCustomId, ignoreGroupNames: true)]
    public async Task ExportMessageCsvAllAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ExportMessageCsvAsync();
    }

    [RequireBotAdmin]
    [SlashCommand("inactive_preview", "次回の自動キック判定で対象になる候補を表示します")]
    public async Task PreviewInactiveKickByCommandAsync([Summary("top", "各カテゴリの表示件数(1-50)")] int top = 20)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await PreviewInactiveKickAsync(Math.Clamp(top, 1, 50));
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.PreviewInactiveKickCustomId, ignoreGroupNames: true)]
    public async Task PreviewInactiveKickByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await PreviewInactiveKickAsync(20);
    }

    [RequireBotAdmin]
    [SlashCommand("log_search", "メッセージ/VCログを期間・ユーザーで検索します")]
    public async Task SearchLogsAsync(
        [Summary("days", "過去何日を対象にするか(1-30)")] int days = 7,
        [Summary("user", "対象ユーザー(未指定で全員)")] IUser? user = null,
        [Summary("limit", "表示件数(1-30)")] int limit = 10)
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await SearchLogsInternalAsync(Math.Clamp(days, 1, 30), user?.Id, Math.Clamp(limit, 1, 30));
    }

    [RequireBotAdmin]
    [SlashCommand("health", "Botの権限・主要チャンネル・DB接続状態を診断します")]
    public async Task HealthAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowHealthAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.HealthCheckCustomId, ignoreGroupNames: true)]
    public async Task HealthByButtonAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        await ShowHealthAsync();
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ToggleRegistrationApprovalCustomId, ignoreGroupNames: true)]
    public async Task ToggleRegistrationApprovalAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        if (settings is null)
        {
            await FollowupAsync("サーバー設定が未初期化です。Botを再起動してから再試行してください。", ephemeral: true);
            return;
        }

        var updated = await _db.UpdateGuildRegistrationApprovalRequiredAsync(
            Context.Guild.Id,
            !settings.RegistrationApprovalRequired);
        if (!updated)
        {
            await FollowupAsync("設定更新に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var refreshedSettings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = refreshedSettings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? true;
        if (Context.Interaction is SocketMessageComponent component && component.Message is IUserMessage message)
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }

        await FollowupAsync(
            approvalRequired
                ? "登録確認フローを「必要」に変更しました。"
                : "登録確認フローを「不要」に変更しました（申請送信時に即時メンバー付与）。",
            ephemeral: true);
    }

    [RequireBotAdmin]
    [ComponentInteraction(AdminControlPanel.ToggleInactiveAutoKickCustomId, ignoreGroupNames: true)]
    public async Task ToggleInactiveAutoKickAsync()
    {
        if (Context.Guild is null)
        {
            await RespondAsync("この操作はサーバー内でのみ使えます。", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        var settings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        if (settings is null)
        {
            await FollowupAsync("サーバー設定が未初期化です。Botを再起動してから再試行してください。", ephemeral: true);
            return;
        }

        var updated = await _db.UpdateGuildInactiveAutoKickEnabledAsync(
            Context.Guild.Id,
            !settings.InactiveAutoKickEnabled);
        if (!updated)
        {
            await FollowupAsync("設定更新に失敗しました。時間を置いて再試行してください。", ephemeral: true);
            return;
        }

        var refreshedSettings = await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = refreshedSettings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = refreshedSettings?.InactiveAutoKickEnabled ?? true;
        if (Context.Interaction is SocketMessageComponent component && component.Message is IUserMessage message)
        {
            await ApplyAdminControlPanelStateAsync(message, approvalRequired, inactiveAutoKickEnabled);
        }

        await FollowupAsync(
            inactiveAutoKickEnabled
                ? "30日非アクティブ自動キックを「ON」に変更しました。"
                : "30日非アクティブ自動キックを「OFF」に変更しました。",
            ephemeral: true);
    }

    private async Task ExportMessageCsvAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = DateTimeOffset.UnixEpoch;
        var rows = await _db.ListMessageLogsForExportAsync(Context.Guild.Id, fromUtcInclusive, toUtcExclusive);

        if (rows.Count == 0)
        {
            await FollowupAsync("メッセージログはありません。", ephemeral: true);
            return;
        }

        var csv = BuildMessageLogCsv(rows);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes, writable: false);
        var fileName = $"message_logs_{Context.Guild.Id}_all_to_{toUtcExclusive:yyyyMMddHHmm}.csv";
        await FollowupWithFileAsync(stream, fileName, text: $"CSVを出力しました。期間: 全期間 / 件数: {rows.Count}", ephemeral: true);
    }

    private async Task SearchLogsInternalAsync(int days, ulong? userId, int limit)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var toUtcExclusive = DateTimeOffset.UtcNow;
        var fromUtcInclusive = toUtcExclusive.AddDays(-days);
        var readLimit = Math.Clamp(limit, 1, 30);
        var messageTask = _db.ListRecentMessageLogsAsync(
            Context.Guild.Id,
            fromUtcInclusive,
            toUtcExclusive,
            userId,
            maxRows: readLimit,
            includeDeleted: true);
        var voiceTask = _db.ListRecentVoiceSessionsAsync(
            Context.Guild.Id,
            fromUtcInclusive,
            toUtcExclusive,
            userId,
            maxRows: readLimit);

        await Task.WhenAll(messageTask, voiceTask);
        var messageRows = messageTask.Result;
        var voiceRows = voiceTask.Result;
        var targetText = userId.HasValue ? $"<@{userId.Value}>" : "全員";

        var embed = new EmbedBuilder()
            .WithTitle("ログ検索結果")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .AddField("対象", targetText, true)
            .AddField("期間", $"{days}日 ({fromUtcInclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} - {toUtcExclusive.ToOffset(JapanTimeOffset):MM/dd HH:mm} JST)", true)
            .AddField("取得件数", $"メッセージ: {messageRows.Count} / VC: {voiceRows.Count}", true)
            .AddField($"メッセージ(最新{readLimit}件)", BuildMessageLogSearchLines(messageRows, readLimit), false)
            .AddField($"VCセッション(最新{readLimit}件)", BuildVoiceLogSearchLines(voiceRows, readLimit, toUtcExclusive), false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task ShowHealthAsync()
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        var checks = new List<HealthCheckItem>();
        GuildSettings? settings = null;

        try
        {
            settings = await _db.GetGuildSettingsAsync(guild.Id);
            checks.Add(HealthCheckItem.Ok("DB接続", "OK"));
        }
        catch (Exception ex)
        {
            checks.Add(HealthCheckItem.Error("DB接続", $"失敗: {ex.Message}"));
        }

        var botUser = guild.CurrentUser;
        var missingPermissions = new List<string>();
        if (!botUser.GuildPermissions.ManageRoles) missingPermissions.Add("ManageRoles");
        if (!botUser.GuildPermissions.ManageChannels) missingPermissions.Add("ManageChannels");
        if (!botUser.GuildPermissions.ManageNicknames) missingPermissions.Add("ManageNicknames");
        if (!botUser.GuildPermissions.ReadMessageHistory) missingPermissions.Add("ReadMessageHistory");
        if (!botUser.GuildPermissions.SendMessages) missingPermissions.Add("SendMessages");
        var autoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        if (autoKickEnabled && !botUser.GuildPermissions.KickMembers) missingPermissions.Add("KickMembers");

        if (missingPermissions.Count == 0)
        {
            checks.Add(HealthCheckItem.Ok("権限", "主要権限は満たしています。"));
        }
        else
        {
            checks.Add(HealthCheckItem.Error("権限", $"不足: {string.Join(", ", missingPermissions)}"));
        }

        var logChannel = FindTextChannelByName(guild, _options.LogChannelName);
        checks.Add(logChannel is null
            ? HealthCheckItem.Warn("logチャンネル", $"未検出: {_options.LogChannelName}")
            : HealthCheckItem.Ok("logチャンネル", $"OK: <#{logChannel.Id}>"));

        var notifyChannel = FindTextChannelByName(guild, _options.NotificationChannelName);
        checks.Add(notifyChannel is null
            ? HealthCheckItem.Warn("通知チャンネル", $"未検出: {_options.NotificationChannelName}")
            : HealthCheckItem.Ok("通知チャンネル", $"OK: <#{notifyChannel.Id}>"));

        var controlChannel = FindTextChannelByName(guild, _options.ControlChannelName);
        checks.Add(controlChannel is null
            ? HealthCheckItem.Warn("コントロールチャンネル", $"未検出: {_options.ControlChannelName}")
            : HealthCheckItem.Ok("コントロールチャンネル", $"OK: <#{controlChannel.Id}>"));

        var adminRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.AdminRoleName, StringComparison.OrdinalIgnoreCase));
        checks.Add(adminRole is null
            ? HealthCheckItem.Warn("管理者ロール", $"未検出: {_options.AdminRoleName}")
            : HealthCheckItem.Ok("管理者ロール", $"OK: <@&{adminRole.Id}>"));

        var memberRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, _options.MemberRoleName, StringComparison.OrdinalIgnoreCase));
        checks.Add(memberRole is null
            ? HealthCheckItem.Warn("メンバーロール", $"未検出: {_options.MemberRoleName}")
            : HealthCheckItem.Ok("メンバーロール", $"OK: <@&{memberRole.Id}>"));

        if (settings is not null)
        {
            checks.Add(settings.LogChannelId == 0
                ? HealthCheckItem.Warn("設定LogChannelId", "未設定")
                : HealthCheckItem.Ok("設定LogChannelId", settings.LogChannelId.ToString()));
            checks.Add(settings.ControlChannelId == 0
                ? HealthCheckItem.Warn("設定ControlChannelId", "未設定")
                : HealthCheckItem.Ok("設定ControlChannelId", settings.ControlChannelId.ToString()));
        }

        var hasError = checks.Any(x => x.Level == "ERROR");
        var hasWarn = checks.Any(x => x.Level == "WARN");
        var overall = hasError ? "ERROR" : hasWarn ? "WARN" : "OK";
        var color = hasError ? Color.Red : hasWarn ? Color.Orange : Color.Green;
        var summaryLines = checks.Select(x => $"[{x.Level}] {x.Name}: {x.Message}");
        var summary = JoinWithinLimit(summaryLines, 950);
        var embed = new EmbedBuilder()
            .WithTitle("Bot Health Check")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("総合", overall, true)
            .AddField("自動キック", autoKickEnabled ? "ON" : "OFF", true)
            .AddField("対象Guild", $"{guild.Name} ({guild.Id})", true)
            .AddField("詳細", string.IsNullOrWhiteSpace(summary) ? "なし" : summary, false)
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task PreviewInactiveKickAsync(int top)
    {
        if (Context.Guild is null)
        {
            return;
        }

        var guild = Context.Guild;
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch
        {
        }

        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        var kickDays = Math.Max(1, _options.InactiveAutoKickDays);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        var todayJst = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset);
        var evaluationBaseJst = nowJst.Hour == 0 ? todayJst : todayJst.AddDays(1);
        var evaluationBaseUtc = evaluationBaseJst.ToUniversalTime();
        var kickCutoffUtc = evaluationBaseUtc.AddDays(-kickDays);

        var activeUserIds = await _db.ListActiveUserIdsSinceAsync(guild.Id, kickCutoffUtc);
        var exemptRoleNames = BuildInactiveKickExemptRoleNameSet(_options);

        var kickTargets = new List<SocketGuildUser>();
        var exemptUsers = new List<SocketGuildUser>();
        var permissionSkippedUsers = new List<SocketGuildUser>();
        var eligibleUsers = 0;
        var currentBotUser = guild.CurrentUser;

        foreach (var user in guild.Users.ToArray())
        {
            if (user.IsBot || user.Id == guild.OwnerId)
            {
                continue;
            }

            eligibleUsers++;
            var joinedAt = user.JoinedAt ?? user.CreatedAt;
            if (joinedAt > kickCutoffUtc || activeUserIds.Contains(user.Id))
            {
                continue;
            }

            if (IsInactiveKickExemptUser(user, exemptRoleNames))
            {
                exemptUsers.Add(user);
                continue;
            }

            if (CanKickMember(currentBotUser, user, guild.OwnerId))
            {
                kickTargets.Add(user);
                continue;
            }

            permissionSkippedUsers.Add(user);
        }

        var color = inactiveAutoKickEnabled ? Color.Orange : Color.DarkGrey;
        var stateText = inactiveAutoKickEnabled ? "ON" : "OFF";
        var embed = new EmbedBuilder()
            .WithTitle("自動キック候補プレビュー")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("設定", $"自動キック: **{stateText}** / 日数: **{kickDays}日**", false)
            .AddField("次回判定(JST)", $"{evaluationBaseJst:yyyy/MM/dd HH:mm}", true)
            .AddField("判定基準", $"{kickCutoffUtc.ToOffset(JapanTimeOffset):yyyy/MM/dd HH:mm} 以降の活動なし", true)
            .AddField("キック対象", $"{kickTargets.Count}人", true)
            .AddField("除外(管理者/除外ロール)", $"{exemptUsers.Count}人", true)
            .AddField("権限不足でスキップ", $"{permissionSkippedUsers.Count}人", true)
            .AddField($"対象一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(kickTargets, top), false)
            .AddField($"除外一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(exemptUsers, top), false)
            .AddField($"権限不足一覧(最大 {top} 件)", BuildInactiveKickPreviewSection(permissionSkippedUsers, top), false)
            .WithFooter($"判定対象メンバー: {eligibleUsers}人 (Bot/オーナー除外)")
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    private async Task<IUserMessage> EnsureAdminControlPanelMessageAsync(IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch
            {
            }
        }

        var settings = Context.Guild is null
            ? null
            : await _db.GetGuildSettingsAsync(Context.Guild.Id);
        var approvalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? true;
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled);
                props.Components = AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled);
            });
            return existing;
        }

        return await channel.SendMessageAsync(
            embed: BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled),
            components: AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled),
            flags: MessageFlags.SuppressNotification);
    }

    private static Task ApplyAdminControlPanelStateAsync(IUserMessage message, bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        var embed = BuildAdminStatusEmbed(approvalRequired, inactiveAutoKickEnabled);
        var components = AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled);
        return message.ModifyAsync(props =>
        {
            props.Content = null;
            props.Embed = embed;
            props.Components = components;
        });
    }

    private static Embed BuildAdminStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        var stateText = approvalRequired ? "必要" : "不要";
        var note = approvalRequired
            ? "申請は登録確認チャンネルで管理者が許可/拒否します。"
            : "申請送信時に即時でメンバーロールを付与します。";
        var autoKickText = inactiveAutoKickEnabled ? "ON" : "OFF";
        var autoKickNote = inactiveAutoKickEnabled
            ? "毎日 00:00 (JST) に30日非アクティブのユーザーを判定して自動キックします。"
            : "30日非アクティブ自動キックは無効です。";

        return new EmbedBuilder()
            .WithTitle("ADMIN設定")
            .WithColor(inactiveAutoKickEnabled ? Color.Orange : Color.Green)
            .WithCurrentTimestamp()
            .AddField("登録確認フロー", stateText, true)
            .AddField("動作", note, false)
            .AddField("30日非アクティブ自動キック", autoKickText, true)
            .AddField("キック動作", autoKickNote, false)
            .Build();
    }

    private static string BuildMessageLogCsv(IReadOnlyList<MessageLog> rows)
    {
        var sb = new StringBuilder(capacity: Math.Max(1024, rows.Count * 128));
        sb.AppendLine("created_at_utc,message_id,channel_id,author_id,author_name,author_display_name,content,attachment_count,embed_count,attachments_json,embeds_json,edited_at_utc,deleted_at_utc");

        foreach (var row in rows)
        {
            var displayName = string.IsNullOrWhiteSpace(row.AuthorDisplayName) ? row.AuthorName : row.AuthorDisplayName;
            sb.Append(ToCsvCell(row.CreatedAt.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.MessageId.ToString())).Append(',')
              .Append(ToCsvCell(row.ChannelId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorId.ToString())).Append(',')
              .Append(ToCsvCell(row.AuthorName)).Append(',')
              .Append(ToCsvCell(displayName)).Append(',')
              .Append(ToCsvCell(row.Content)).Append(',')
              .Append(ToCsvCell(row.AttachmentCount.ToString())).Append(',')
              .Append(ToCsvCell(row.EmbedCount.ToString())).Append(',')
              .Append(ToCsvCell(row.AttachmentsJson)).Append(',')
              .Append(ToCsvCell(row.EmbedsJson)).Append(',')
              .Append(ToCsvCell(row.EditedAt?.ToUniversalTime().ToString("O"))).Append(',')
              .Append(ToCsvCell(row.DeletedAt?.ToUniversalTime().ToString("O")))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string ToCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string BuildMessageLogSearchLines(IReadOnlyList<MessageLog> rows, int maxRows)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows
            .Take(Math.Clamp(maxRows, 1, 30))
            .Select(row =>
            {
                var time = row.CreatedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
                var content = TruncateSingleLine(string.IsNullOrWhiteSpace(row.Content) ? "(no content)" : row.Content, 60);
                var deleted = row.DeletedAt.HasValue ? " [deleted]" : string.Empty;
                return $"[{time}] <#{ToUlong(row.ChannelId)}> <@{ToUlong(row.AuthorId)}> {content}{deleted}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static string BuildVoiceLogSearchLines(IReadOnlyList<VoiceSession> rows, int maxRows, DateTimeOffset toUtcExclusive)
    {
        if (rows.Count == 0)
        {
            return "なし";
        }

        var lines = rows
            .Take(Math.Clamp(maxRows, 1, 30))
            .Select(row =>
            {
                var joined = row.JoinedAt.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm");
                var left = row.LeftAt?.ToOffset(JapanTimeOffset).ToString("MM/dd HH:mm") ?? "進行中";
                var durationSeconds = row.DurationSeconds
                    ?? (int)Math.Max(0, (toUtcExclusive - row.JoinedAt).TotalSeconds);
                var durationText = FormatDuration(durationSeconds);
                return $"[{joined}] <#{ToUlong(row.ChannelId)}> <@{ToUlong(row.UserId)}> {left} / {durationText}";
            });
        return JoinWithinLimit(lines, 980);
    }

    private static string JoinWithinLimit(IEnumerable<string> lines, int maxLength)
    {
        var sb = new StringBuilder(capacity: Math.Max(128, maxLength));
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (sb.Length > 0 && sb.Length + 1 + line.Length > maxLength)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static ITextChannel? FindTextChannelByName(SocketGuild guild, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDuration(int durationSeconds)
    {
        var seconds = Math.Max(0, durationSeconds);
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        var secs = seconds % 60;
        if (hours > 0)
        {
            return $"{hours}h{minutes:00}m";
        }

        if (minutes > 0)
        {
            return $"{minutes}m{secs:00}s";
        }

        return $"{secs}s";
    }

    private static string TruncateSingleLine(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, Math.Max(1, maxLength - 3)) + "...";
    }

    private static string BuildInactiveKickPreviewSection(IReadOnlyList<SocketGuildUser> users, int maxRows)
    {
        if (users.Count == 0)
        {
            return "なし";
        }

        var rows = Math.Clamp(maxRows, 1, 50);
        var sb = new StringBuilder(capacity: 512);
        var shown = 0;
        foreach (var user in users
                     .OrderBy(x => x.JoinedAt ?? x.CreatedAt)
                     .ThenBy(x => x.Id))
        {
            if (shown >= rows)
            {
                break;
            }

            var joinedJst = (user.JoinedAt ?? user.CreatedAt).ToOffset(JapanTimeOffset);
            var line = $"<@{user.Id}> / 参加:{joinedJst:yyyy/MM/dd}";
            if (sb.Length + line.Length + 1 > 980)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            shown++;
        }

        if (users.Count > shown)
        {
            var suffix = $"...他 {users.Count - shown} 件";
            if (sb.Length + suffix.Length + 1 <= 1000)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(suffix);
            }
        }

        return sb.Length == 0 ? "なし" : sb.ToString();
    }

    private static HashSet<string> BuildInactiveKickExemptRoleNameSet(DiscordOptions options)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var roleName in options.InactiveAutoKickExemptRoleNames ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                names.Add(roleName.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(options.AdminRoleName))
        {
            names.Add(options.AdminRoleName.Trim());
        }

        return names;
    }

    private static bool IsInactiveKickExemptUser(SocketGuildUser user, IReadOnlySet<string> exemptRoleNames)
    {
        if (user.GuildPermissions.Administrator)
        {
            return true;
        }

        if (exemptRoleNames.Count == 0)
        {
            return false;
        }

        foreach (var role in user.Roles)
        {
            if (exemptRoleNames.Contains(role.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanKickMember(SocketGuildUser? botUser, SocketGuildUser targetUser, ulong guildOwnerId)
    {
        if (botUser is null)
        {
            return false;
        }

        if (targetUser.Id == guildOwnerId || targetUser.Id == botUser.Id)
        {
            return false;
        }

        if (!botUser.GuildPermissions.KickMembers)
        {
            return false;
        }

        return botUser.Hierarchy > targetUser.Hierarchy;
    }

    private sealed record HealthCheckItem(string Level, string Name, string Message)
    {
        public static HealthCheckItem Ok(string name, string message) => new("OK", name, message);
        public static HealthCheckItem Warn(string name, string message) => new("WARN", name, message);
        public static HealthCheckItem Error(string name, string message) => new("ERROR", name, message);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
}
