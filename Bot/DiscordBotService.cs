using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;
using RaidDcBot.Services;

namespace RaidDcBot.Bot;

public sealed class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly DiscordOptions _options;
    private readonly Db _db;
    private readonly VoiceChannelTextService _voiceTextService;
    private readonly UserProfileEmbedService _profileEmbeds;
    private readonly RecruitmentListService _recruitListService;
    private readonly GuildAuditLogService _audit;
    private readonly StatsEmbedService _stats;
    private readonly VoiceStatsService _voiceStats;
    private readonly string? _attachmentArchiveRootPath;
    private readonly ConcurrentDictionary<ulong, byte> _tempVoiceChannelIds = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _tempVoiceDeleteTokens = new();
    private readonly ConcurrentDictionary<ulong, int> _dailyActivityRankingPosted = new();
    private readonly ConcurrentDictionary<ulong, int> _weeklyVoiceRankingPosted = new();
    private readonly ConcurrentDictionary<ulong, int> _inactiveAutoKickProcessedDay = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failureAlerts = new();
    private readonly SemaphoreSlim _tempVoiceLock = new(1, 1);
    private static readonly HttpClient AttachmentArchiveHttpClient = CreateAttachmentArchiveHttpClient();
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private static readonly TimeSpan FailureAlertCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LotteryParticipantRoleRetention = TimeSpan.FromHours(24);
    private const string RecruitScheduleFormat = "yyyy/MM/dd HH:mm";
    private const int RecruitArtifactRetentionDays = 7;
    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;
    private CancellationTokenSource? _recruitScheduleCts;
    private Task? _recruitScheduleTask;
    private CancellationTokenSource? _activityRoleSyncCts;
    private Task? _activityRoleSyncTask;

    public DiscordBotService(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        ILogger<DiscordBotService> logger,
        IOptions<DiscordOptions> options,
        Db db,
        VoiceChannelTextService voiceTextService,
        UserProfileEmbedService profileEmbeds,
        RecruitmentListService recruitListService,
        GuildAuditLogService audit,
        StatsEmbedService stats,
        VoiceStatsService voiceStats)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _logger = logger;
        _options = options.Value;
        _db = db;
        _voiceTextService = voiceTextService;
        _profileEmbeds = profileEmbeds;
        _recruitListService = recruitListService;
        _audit = audit;
        _stats = stats;
        _voiceStats = voiceStats;
        _attachmentArchiveRootPath = ResolveAttachmentArchiveRootPath(_options);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("Discord:Token is required.");
        }

        await _db.EnsureRecruitRequiredJobColumnAsync();
        await _db.ApplyRecruitDuplicateJobDiffSqlAsync();
        await _db.EnsureRecruitHostFlexSpecColumnAsync();
        await _db.EnsureRecruitHostFlexJobsSpecColumnAsync();
        await _db.EnsureRecruitSlotFlexSpecColumnAsync();
        await _db.EnsureRecruitSlotFlexJobsSpecColumnAsync();
        await _db.BackfillRecruitSlotFlexJobsSpecAsync();
        await _db.EnsureDiscordUserProfileCharacterColumnsAsync();
        await _db.EnsureDiscordUserVoiceNotificationColumnAsync();
        await _db.EnsureGuildLodestonePanelTableAsync();
        await _db.EnsureGuildAdminControlPanelTableAsync();
        await _db.EnsureGuildMemberContentRolesTableAsync();
        await _db.EnsureGuildStatsPanelTableAsync();
        await _db.EnsureGuildDailyActivityPanelTableAsync();
        await _db.EnsureGuildWeeklyActivityPanelTableAsync();
        await _db.EnsureInquiryTicketsTableAsync();
        await _db.EnsureLotteryTablesAsync();
        await _db.EnsureGuildSettingsColumnsAsync();
        await _db.EnsureRecruitTemplatesTableAsync();
        await _db.EnsureMessageLogPayloadColumnsAsync();
        await _db.EnsureUserReactionLogsTableAsync();
        EnsureAttachmentArchiveDirectory();

        _client.Log += HandleLogAsync;
        _interactions.Log += HandleLogAsync;
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.JoinedGuild += OnJoinedGuildAsync;
        _client.UserJoined += OnUserJoinedAsync;
        _client.UserLeft += OnUserLeftAsync;
        _client.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
        _client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
        _client.MessageUpdated += OnMessageUpdatedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
        _client.ReactionRemoved += OnReactionRemovedAsync;

        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token), _cleanupCts.Token);
        _recruitScheduleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _recruitScheduleTask = Task.Run(() => RecruitScheduleLoopAsync(_recruitScheduleCts.Token), _recruitScheduleCts.Token);
        _activityRoleSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activityRoleSyncTask = Task.Run(() => ActivityRoleSyncLoopAsync(_activityRoleSyncCts.Token), _activityRoleSyncCts.Token);

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cleanupCts is not null)
        {
            _cleanupCts.Cancel();
        }

        if (_cleanupTask is not null)
        {
            try
            {
                await _cleanupTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_recruitScheduleCts is not null)
        {
            _recruitScheduleCts.Cancel();
        }

        if (_recruitScheduleTask is not null)
        {
            try
            {
                await _recruitScheduleTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_activityRoleSyncCts is not null)
        {
            _activityRoleSyncCts.Cancel();
        }

        if (_activityRoleSyncTask is not null)
        {
            try
            {
                await _activityRoleSyncTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _client.StopAsync();
    }

    private async Task OnReadyAsync()
    {
        await RegisterInteractionModulesAsync();

        if (_options.GuildId != 0)
        {
            await _interactions.RegisterCommandsToGuildAsync(_options.GuildId, true);
        }
        else
        {
            await _interactions.RegisterCommandsGloballyAsync(true);
        }

        _logger.LogInformation("Bot ready. Commands registered.");
        await EnsureGuildResourcesAsync();
        if (ShouldRunActivityMaintenance())
        {
            await SyncActivityRolesAsync(DateTimeOffset.UtcNow);
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(_client, interaction);
        if (interaction is SocketSlashCommand)
        {
            if (context.Guild is null || context.User is not SocketGuildUser guildUser)
            {
                await interaction.RespondAsync("このコマンドはサーバー内でのみ使えます。", ephemeral: true);
                return;
            }

            if (!IsBotAdmin(guildUser))
            {
                var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
                await interaction.RespondAsync($"{roleName} ロールが必要です。", ephemeral: true);
                await LogImmediatePermissionDeniedAsync(context, $"{roleName} ロールが必要です。");
                return;
            }
        }

        var result = await _interactions.ExecuteCommandAsync(context, _services);
        await LogCommandExecutionAsync(context, result);
        if (!result.IsSuccess)
        {
            await TryLogInteractionFailureAsync(context, result);
            _logger.LogWarning("Interaction failed: {Error} - {Reason}", result.Error, result.ErrorReason);
        }
    }

    private async Task RegisterInteractionModulesAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (_options.RecruitFeatureEnabled)
        {
            await _interactions.AddModulesAsync(assembly, _services);
            return;
        }

        foreach (var type in assembly.GetTypes().Where(IsInteractionModuleType))
        {
            if (type == typeof(RecruitmentModule))
            {
                continue;
            }

            await _interactions.AddModuleAsync(type, _services);
        }

        _logger.LogInformation("Recruit feature is disabled. RecruitmentModule was not registered.");
    }

    private static bool IsInteractionModuleType(Type type)
    {
        if (!type.IsClass || type.IsAbstract)
        {
            return false;
        }

        var current = type;
        while (current is not null)
        {
            var baseType = current.BaseType;
            if (baseType is null)
            {
                return false;
            }

            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(InteractionModuleBase<>))
            {
                return true;
            }

            current = baseType;
        }

        return false;
    }

    private Task HandleLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };

        _logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private async Task LogCommandExecutionAsync(SocketInteractionContext context, IResult result)
    {
        if (context.Guild is null || context.Interaction is not SocketSlashCommand slash)
        {
            return;
        }

        var commandPath = BuildSlashCommandPath(slash);
        var channel = context.Channel as SocketGuildChannel;
        var channelText = channel is null ? "(不明)" : ChannelMention(channel);
        var description = string.Join('\n', new[]
        {
            $"👤 実行者: {MentionUser(context.User.Id)} ({context.User})",
            $"💬 チャンネル: {channelText}",
            $"⌨️ コマンド: {commandPath}"
        });

        if (result.IsSuccess)
        {
            await _audit.SendAsync(context.Guild, "管理コマンド実行", description, Color.Teal, context.User);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(result.ErrorReason) ? "(不明)" : result.ErrorReason;
        description = $"{description}\n❌ Error: {reason}";
        await _audit.SendAsync(context.Guild, "管理コマンド失敗", description, Color.Red, context.User);
    }

    private async Task LogImmediatePermissionDeniedAsync(SocketInteractionContext context, string reason)
    {
        if (context.Guild is null)
        {
            return;
        }

        var route = BuildInteractionRoute(context.Interaction);
        var channelId = (context.Channel as SocketGuildChannel)?.Id ?? 0;
        var messageId = TryGetInteractionMessageId(context.Interaction) ?? 0;
        var description = string.Join('\n', new[]
        {
            $"👤 実行者: {MentionUser(context.User.Id)} ({context.User})",
            $"🧩 種別: {context.Interaction.Type}",
            $"⌨️ 操作: {route}",
            $"🆔 guild/channel/message/user: {context.Guild.Id}/{channelId}/{messageId}/{context.User.Id}",
            $"⛔ 理由: {reason}"
        });

        await _audit.SendAsync(context.Guild, "権限拒否", description, Color.Orange, context.User);
        _logger.LogWarning(
            "Permission denied before interaction execute. GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}, UserId={UserId}, Route={Route}, Reason={Reason}",
            context.Guild.Id,
            channelId,
            messageId,
            context.User.Id,
            route,
            reason);
    }

    private async Task TryLogInteractionFailureAsync(SocketInteractionContext context, IResult result)
    {
        if (context.Guild is null)
        {
            return;
        }

        var route = BuildInteractionRoute(context.Interaction);
        var channelId = (context.Channel as SocketGuildChannel)?.Id ?? 0;
        var messageId = TryGetInteractionMessageId(context.Interaction) ?? 0;
        var reason = string.IsNullOrWhiteSpace(result.ErrorReason) ? "(不明)" : result.ErrorReason!;
        var isPermission = result.Error == InteractionCommandError.UnmetPrecondition ||
                           reason.Contains("ロールが必要", StringComparison.OrdinalIgnoreCase) ||
                           reason.Contains("主催者のみ", StringComparison.OrdinalIgnoreCase) ||
                           reason.Contains("管理者のみ", StringComparison.OrdinalIgnoreCase);
        var isUnknownEntity =
            reason.Contains("Unknown Message", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Unknown Channel", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("error 10008", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("error 10003", StringComparison.OrdinalIgnoreCase);

        _logger.LogWarning(
            "Interaction execution failed. GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}, UserId={UserId}, InteractionType={InteractionType}, Route={Route}, Error={Error}, Reason={Reason}",
            context.Guild.Id,
            channelId,
            messageId,
            context.User.Id,
            context.Interaction.Type,
            route,
            result.Error,
            reason);

        if (!isPermission && !isUnknownEntity)
        {
            return;
        }

        var title = isPermission ? "権限拒否" : "Discord API参照切れ";
        var color = isPermission ? Color.Orange : Color.Red;
        var description = string.Join('\n', new[]
        {
            $"👤 実行者: {MentionUser(context.User.Id)} ({context.User})",
            $"🧩 種別: {context.Interaction.Type}",
            $"⌨️ 操作: {route}",
            $"🆔 guild/channel/message/user: {context.Guild.Id}/{channelId}/{messageId}/{context.User.Id}",
            $"❌ Error: {reason}"
        });

        await _audit.SendAsync(context.Guild, title, description, color, context.User);
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromHours(12);
        while (!token.IsCancellationRequested)
        {
            await RunCleanupAsync();

            try
            {
                await Task.Delay(interval, token);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    private async Task RecruitScheduleLoopAsync(CancellationToken token)
    {
        var intervalSeconds = _options.RecruitScheduleCheckIntervalSeconds <= 0
            ? 60
            : _options.RecruitScheduleCheckIntervalSeconds;
        var interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 10, 600));

        while (!token.IsCancellationRequested)
        {
            await RunRecruitScheduleTickAsync();

            try
            {
                await Task.Delay(interval, token);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    private async Task RunCleanupAsync()
    {
        try
        {
            if (_options.MessageLogRetentionDays > 0)
            {
                var deleted = await _db.CleanupMessageLogsAsync(_options.MessageLogRetentionDays);
                if (deleted > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} message logs.", deleted);
                }

                var removedFiles = CleanupArchivedAttachmentFiles(_options.MessageLogRetentionDays);
                if (removedFiles > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} archived attachment files.", removedFiles);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup message logs.");
        }

        try
        {
            if (_options.VoiceSessionRetentionDays > 0)
            {
                var deleted = await _db.CleanupVoiceSessionsAsync(_options.VoiceSessionRetentionDays);
                if (deleted > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} voice sessions.", deleted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup voice sessions.");
        }

        try
        {
            var reactionRetentionDays = _options.MessageLogRetentionDays > 0
                ? Math.Max(_options.MessageLogRetentionDays, Math.Max(0, _options.InactiveAutoKickDays))
                : 0;

            if (reactionRetentionDays > 0)
            {
                var deleted = await _db.CleanupReactionLogsAsync(reactionRetentionDays);
                if (deleted > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} reaction logs.", deleted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup reaction logs.");
        }

        try
        {
            if (_options.RecruitFeatureEnabled)
            {
                await CleanupRecruitArtifactsAsync(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup recruit artifacts.");
        }

    }

    private async Task ActivityRoleSyncLoopAsync(CancellationToken token)
    {
        var intervalMinutes = _options.ActivityRoleSyncIntervalMinutes <= 0
            ? 30
            : _options.ActivityRoleSyncIntervalMinutes;
        var interval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 1, 720));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            await RunActivityRoleSyncAsync();
        }
    }

    private async Task RunActivityRoleSyncAsync()
    {
        try
        {
            if (ShouldRunActivityMaintenance())
            {
                await SyncActivityRolesAsync(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync activity roles in periodic loop.");
        }
    }

    private bool ShouldRunActivityMaintenance()
    {
        // Per-guild admin settings can enable/disable inactive auto-kick at runtime,
        // so the maintenance loop should always run and decide inside each guild.
        return true;
    }

    private bool ShouldRunInactiveAutoKickNow(ulong guildId, bool inactiveAutoKickEnabled, DateTimeOffset nowUtc, out int dayKey, out DateTimeOffset evaluationBaseUtc)
    {
        dayKey = 0;
        evaluationBaseUtc = nowUtc;

        if (!(inactiveAutoKickEnabled && _options.InactiveAutoKickDays > 0))
        {
            return false;
        }

        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        if (nowJst.Hour != 0)
        {
            return false;
        }

        dayKey = (nowJst.Year * 10000) + (nowJst.Month * 100) + nowJst.Day;
        if (_inactiveAutoKickProcessedDay.TryGetValue(guildId, out var processedDay) && processedDay == dayKey)
        {
            return false;
        }

        evaluationBaseUtc = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset).ToUniversalTime();
        return true;
    }

    private async Task RunRecruitScheduleTickAsync()
    {
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var recruitReminderDue = 0;
            var recruitReminderSucceeded = 0;
            var recruitReminderFailed = 0;
            var recruitAutoCloseDue = 0;
            var recruitAutoCloseSucceeded = 0;
            var recruitAutoCloseFailed = 0;

            if (_options.RecruitFeatureEnabled && _options.RecruitReminderMinutesBefore > 0)
            {
                var reminderUntil = nowUtc.AddMinutes(_options.RecruitReminderMinutesBefore);
                var remindTargets = await _db.ListDueRecruitPostsForReminderAsync(nowUtc, reminderUntil);
                recruitReminderDue = remindTargets.Count;
                foreach (var post in remindTargets)
                {
                    try
                    {
                        await HandleRecruitReminderAsync(post);
                        recruitReminderSucceeded++;
                    }
                    catch (Exception ex)
                    {
                        recruitReminderFailed++;
                        _logger.LogWarning(ex, "Failed to send recruit reminder. PostId={PostId}", post.Id);
                    }
                }
            }

            if (_options.RecruitFeatureEnabled && _options.RecruitAutoCloseEnabled)
            {
                var autoCloseTargets = await _db.ListDueRecruitPostsForAutoCloseAsync(nowUtc);
                recruitAutoCloseDue = autoCloseTargets.Count;
                foreach (var post in autoCloseTargets)
                {
                    try
                    {
                        await HandleRecruitAutoCloseAsync(post);
                        recruitAutoCloseSucceeded++;
                    }
                    catch (Exception ex)
                    {
                        recruitAutoCloseFailed++;
                        _logger.LogWarning(ex, "Failed to auto-close recruit post. PostId={PostId}", post.Id);
                    }
                }
            }

            if (_options.RecruitFeatureEnabled)
            {
                await RefreshRecruitListPanelsHeartbeatAsync();
            }

            if (recruitReminderDue > 0 || recruitReminderFailed > 0 || recruitAutoCloseDue > 0 || recruitAutoCloseFailed > 0)
            {
                _logger.LogInformation(
                    "Recruit schedule tick summary. Reminder due/success/failed={ReminderDue}/{ReminderSucceeded}/{ReminderFailed}, AutoClose due/success/failed={AutoCloseDue}/{AutoCloseSucceeded}/{AutoCloseFailed}",
                    recruitReminderDue,
                    recruitReminderSucceeded,
                    recruitReminderFailed,
                    recruitAutoCloseDue,
                    recruitAutoCloseSucceeded,
                    recruitAutoCloseFailed);
            }

            await ProcessLotteryAutoCloseTickAsync(nowUtc);
            await ProcessLotteryParticipantRoleCleanupTickAsync(nowUtc);

            await ProcessInquiryReminderTickAsync(nowUtc);

            if (_options.DailyActivityRankingEnabled)
            {
                await TryPostDailyActivityRankingAsync(nowUtc);
            }

            if (_options.VoiceWeeklyRankingEnabled)
            {
                await TryPostWeeklyVoiceRankingAsync(nowUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process recruit schedule tick.");
        }
    }

    private async Task ProcessInquiryReminderTickAsync(DateTimeOffset nowUtc)
    {
        var cutoffUtc = nowUtc.AddHours(-24);
        var tickets = await _db.ListDueInquiryReminderTicketsAsync(cutoffUtc, maxRows: 50);
        if (tickets.Count == 0)
        {
            return;
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var ticket in tickets)
        {
            try
            {
                var guild = _client.GetGuild(ToUlong(ticket.GuildId));
                if (guild is null)
                {
                    await _db.MarkInquiryReminderSentAsync(ticket.Id, nowUtc);
                    succeeded++;
                    continue;
                }

                var channel = guild.GetTextChannel(ToUlong(ticket.ChannelId));
                var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
                var adminRole = guild.Roles.FirstOrDefault(role =>
                    string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
                var mentionText = adminRole is null ? string.Empty : $"{adminRole.Mention} ";
                var statusLabel = InquiryTicketStatus.ToLabel(ticket.Status);
                var channelText = channel is null ? "(チャンネル削除済み)" : $"<#{channel.Id}>";
                var requester = MentionUser(ToUlong(ticket.RequesterUserId));
                var messageText = $"{mentionText}24時間以上更新のないお問い合わせがあります。\nチケット: #{ticket.Id}\n状態: {statusLabel}\n投稿者: {requester}\nチャンネル: {channelText}";

                await _audit.SendSystemAsync(
                    guild,
                    "お問い合わせ未対応リマインド",
                    messageText,
                    Color.Orange);

                if (channel is not null)
                {
                    await channel.SendMessageAsync(
                        text: messageText,
                        flags: MessageFlags.SuppressNotification);
                }

                await _db.MarkInquiryReminderSentAsync(ticket.Id, nowUtc);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to send inquiry reminder. TicketId={TicketId}", ticket.Id);
            }
        }

        _logger.LogInformation(
            "Inquiry reminder tick summary. Due={Due}, Succeeded={Succeeded}, Failed={Failed}",
            tickets.Count,
            succeeded,
            failed);
    }

    private async Task ProcessLotteryAutoCloseTickAsync(DateTimeOffset nowUtc)
    {
        var dueLotteries = await _db.ListDueLotteriesForAutoCloseAsync(nowUtc, maxRows: 200);
        if (dueLotteries.Count == 0)
        {
            return;
        }

        var closed = 0;
        var autoDrawn = 0;
        var failed = 0;
        foreach (var lottery in dueLotteries)
        {
            try
            {
                var result = await HandleLotteryAutoCloseAsync(lottery, nowUtc);
                if (result.Closed)
                {
                    closed++;
                }

                if (result.Drawn)
                {
                    autoDrawn++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to auto-close lottery {LotteryId}.", lottery.Id);
            }
        }

        _logger.LogInformation(
            "Lottery auto-close tick summary. Due={Due}, Closed={Closed}, AutoDrawn={AutoDrawn}, Failed={Failed}",
            dueLotteries.Count,
            closed,
            autoDrawn,
            failed);
    }

    private async Task<(bool Closed, bool Drawn)> HandleLotteryAutoCloseAsync(Lottery lottery, DateTimeOffset nowUtc)
    {
        var previousStatus = LotteryStatus.Normalize(lottery.Status);
        _ = await _db.TryCloseLotteryIfExpiredAsync(lottery.Id, nowUtc);
        var current = await _db.GetLotteryByIdAsync(lottery.Id) ?? lottery;
        if (LotteryStatus.IsOpen(current.Status))
        {
            return (false, false);
        }

        var entries = await _db.ListLotteryEntriesAsync(current.Id);
        var canAutoDraw = !LotteryStatus.IsCanceled(current.Status) && !LotteryStatus.IsDrawn(current.Status);
        var didDraw = false;
        if (canAutoDraw && entries.Count > 0)
        {
            var drawCount = current.AllowDuplicateWinners
                ? Math.Max(current.WinnerCount, 1)
                : Math.Min(Math.Max(current.WinnerCount, 1), entries.Count);
            var picked = PickRandomLotteryEntries(entries, drawCount, current.AllowDuplicateWinners);
            var drawnByUserId = _client.CurrentUser?.Id ?? 0UL;
            await _db.ReplaceLotteryWinnersAsync(current.Id, picked, drawnByUserId, nowUtc);
            await _db.UpdateLotteryStatusAsync(current.Id, LotteryStatus.Drawn, nowUtc);
            didDraw = true;
            current = await _db.GetLotteryByIdAsync(current.Id) ?? current;
            entries = await _db.ListLotteryEntriesAsync(current.Id);
        }

        var winners = await _db.ListLotteryWinnersAsync(current.Id);
        var guild = _client.GetGuild(ToUlong(current.GuildId));
        var channel = guild?.GetTextChannel(ToUlong(current.ChannelId));
        if (channel is not null &&
            current.MessageId.HasValue &&
            current.MessageId.Value != 0)
        {
            IUserMessage? message = null;
            try
            {
                message = await channel.GetMessageAsync(ToUlong(current.MessageId.Value)) as IUserMessage;
            }
            catch (Discord.Net.HttpException ex) when (IsUnknownMessage(ex))
            {
                _logger.LogWarning(
                    "Lottery auto-close skipped message fetch because message was missing. LotteryId={LotteryId}, GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}",
                    current.Id,
                    current.GuildId,
                    current.ChannelId,
                    current.MessageId);
            }

            if (message is not null)
            {
                var embed = LotteryPanel.BuildLotteryEmbed(current, entries, winners, nowUtc);
                var components = LotteryPanel.BuildLotteryComponents(current, entries.Count, winners.Count, nowUtc);
                try
                {
                    await message.ModifyAsync(props =>
                    {
                        props.Content = null;
                        props.Embed = embed;
                        props.Components = components;
                    });
                }
                catch (Discord.Net.HttpException ex) when (IsUnknownMessage(ex))
                {
                    _logger.LogWarning(
                        "Lottery auto-close skipped message modify because message was missing. LotteryId={LotteryId}, GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}",
                        current.Id,
                        current.GuildId,
                        current.ChannelId,
                        current.MessageId);
                }
            }
        }

        if (channel is not null)
        {
            await TryPostLotteryResultEmbedAsync(channel, current, entries, winners, nowUtc);
        }

        await ScheduleLotteryParticipantRoleCleanupAsync(current, "抽選が終了しました。");
        await TrySendLotteryStatusTransitionAuditAsync(current, previousStatus, LotteryStatus.Normalize(current.Status), "自動（締切時刻）", didDraw ? "自動抽選を実行" : null);
        return (true, didDraw);
    }

    private async Task ProcessLotteryParticipantRoleCleanupTickAsync(DateTimeOffset nowUtc)
    {
        var targets = await _db.ListDueLotteryParticipantRoleDeletesAsync(nowUtc, maxRows: 200);
        if (targets.Count == 0)
        {
            return;
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var lottery in targets)
        {
            try
            {
                await DeleteLotteryParticipantRoleAsync(lottery);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to cleanup lottery participant role. LotteryId={LotteryId}", lottery.Id);
            }
        }

        _logger.LogInformation(
            "Lottery participant role cleanup summary. Due={Due}, Succeeded={Succeeded}, Failed={Failed}",
            targets.Count,
            succeeded,
            failed);
    }

    private async Task DeleteLotteryParticipantRoleAsync(Lottery lottery)
    {
        if (!lottery.ParticipantRoleId.HasValue || lottery.ParticipantRoleId.Value == 0)
        {
            return;
        }

        var guild = _client.GetGuild(ToUlong(lottery.GuildId));
        if (guild is null)
        {
            return;
        }

        var role = guild.GetRole(ToUlong(lottery.ParticipantRoleId.Value));
        if (role is null)
        {
            await _db.UpdateLotteryParticipantRoleIdAsync(lottery.Id, null);
            return;
        }

        await role.DeleteAsync(new RequestOptions { AuditLogReason = $"Lottery #{lottery.Id} participant role cleanup." });
        await _db.UpdateLotteryParticipantRoleIdAsync(lottery.Id, null);
    }

    private static List<LotteryEntry> PickRandomLotteryEntries(
        IReadOnlyList<LotteryEntry> source,
        int count,
        bool allowDuplicateWinners)
    {
        if (source.Count == 0 || count <= 0)
        {
            return new List<LotteryEntry>();
        }

        var take = allowDuplicateWinners
            ? count
            : Math.Min(count, source.Count);
        if (take <= source.Count)
        {
            var pool = source.ToList();
            var unique = new List<LotteryEntry>(take);
            for (var i = 0; i < take; i++)
            {
                var index = RandomNumberGenerator.GetInt32(pool.Count);
                unique.Add(pool[index]);
                pool.RemoveAt(index);
            }

            return unique;
        }

        var duplicated = new List<LotteryEntry>(take);
        for (var i = 0; i < take; i++)
        {
            var index = RandomNumberGenerator.GetInt32(source.Count);
            duplicated.Add(source[index]);
        }

        return duplicated;
    }

    private async Task RefreshRecruitListPanelsHeartbeatAsync()
    {
        foreach (var guild in _client.Guilds)
        {
            try
            {
                var preferredChannel = ResolveRecruitListFallbackChannel(guild);
                if (preferredChannel is null)
                {
                    continue;
                }

                var panel = await _db.GetGuildRecruitListPanelAsync(guild.Id);
                ulong? existingMessageId = panel is not null && panel.MessageId != 0
                    ? ToUlong(panel.MessageId)
                    : null;
                var ensured = await _recruitListService.EnsureListMessageAsync(guild, preferredChannel, existingMessageId);
                if (ensured is null)
                {
                    continue;
                }

                if (panel is not null &&
                    panel.ChannelId != 0 &&
                    ToUlong(panel.ChannelId) != preferredChannel.Id &&
                    panel.MessageId != 0)
                {
                    var oldChannel = guild.GetTextChannel(ToUlong(panel.ChannelId));
                    if (oldChannel is not null)
                    {
                        try
                        {
                            var oldMessage = await oldChannel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
                            if (oldMessage is not null)
                            {
                                await oldMessage.DeleteAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old recruit list message during channel migration. GuildId={GuildId}", guild.Id);
                        }
                    }
                }

                if (panel is null ||
                    panel.ChannelId == 0 ||
                    ToUlong(panel.ChannelId) != preferredChannel.Id ||
                    ToUlong(panel.MessageId) != ensured.Id)
                {
                    await _db.UpsertGuildRecruitListPanelAsync(guild.Id, preferredChannel.Id, ensured.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh recruit list heartbeat. GuildId={GuildId}", guild.Id);
            }
        }
    }

    private IMessageChannel? ResolveRecruitListFallbackChannel(SocketGuild guild)
    {
        if (!string.IsNullOrWhiteSpace(_options.RecruitPostChannelName))
        {
            var recruitChannel = guild.TextChannels.FirstOrDefault(channel =>
                string.Equals(channel.Name, _options.RecruitPostChannelName, StringComparison.OrdinalIgnoreCase));
            if (recruitChannel is not null)
            {
                return recruitChannel;
            }
        }

        return guild.TextChannels.FirstOrDefault();
    }

    private async Task CleanupRecruitArtifactsAsync(DateTimeOffset nowUtc)
    {
        var cutoffUtc = nowUtc.AddDays(-RecruitArtifactRetentionDays);
        var targets = await _db.ListRecruitPostsPastRetentionAsync(cutoffUtc);
        if (targets.Count == 0)
        {
            return;
        }

        var refreshedGuildIds = new HashSet<ulong>();
        var deletedCount = 0;
        foreach (var post in targets)
        {
            try
            {
                var guild = _client.GetGuild(ToUlong(post.GuildId));
                if (guild is not null)
                {
                    var threadDeleted = await TryDeleteRecruitThreadAsync(post);
                    var messageDeleted = await TryDeleteRecruitMessageAsync(guild, post);
                    if (!threadDeleted || !messageDeleted)
                    {
                        _logger.LogWarning(
                            "Skipped recruit post DB cleanup because artifact deletion failed. PostId={PostId}, ThreadDeleted={ThreadDeleted}, MessageDeleted={MessageDeleted}",
                            post.Id,
                            threadDeleted,
                            messageDeleted);
                        continue;
                    }

                    refreshedGuildIds.Add(guild.Id);
                }

                if (await _db.DeleteRecruitPostAsync(post.Id))
                {
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup recruit post artifacts. PostId={PostId}", post.Id);
            }
        }

        foreach (var guildId in refreshedGuildIds)
        {
            var guild = _client.GetGuild(guildId);
            if (guild is null)
            {
                continue;
            }

            try
            {
                var fallback = ResolveRecruitListFallbackChannel(guild);
                if (fallback is not null)
                {
                    await _recruitListService.RefreshAsync(guild, fallback);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh recruit list after cleanup. GuildId={GuildId}", guildId);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Cleaned up {Count} recruit posts older than {Days} days after close/cancel.",
                deletedCount,
                RecruitArtifactRetentionDays);
        }
    }

    private async Task EnsureGuildResourcesAsync()
    {
        foreach (var guild in _client.Guilds)
        {
            await EnsureGuildResourcesAsync(guild);
        }
    }

    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        await EnsureGuildResourcesAsync(guild);
    }

    private async Task EnsureGuildResourcesAsync(SocketGuild guild)
    {
        var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
        var memberRoleName = string.IsNullOrWhiteSpace(_options.MemberRoleName) ? "メンバー" : _options.MemberRoleName;
        var provisionalRoleName = string.IsNullOrWhiteSpace(_options.ProvisionalRoleName) ? "仮登録" : _options.ProvisionalRoleName;
        var logChannelName = string.IsNullOrWhiteSpace(_options.LogChannelName) ? "log" : _options.LogChannelName;
        var notificationChannelName = string.IsNullOrWhiteSpace(_options.NotificationChannelName) ? "notice" : _options.NotificationChannelName;
        var activityRankingChannelName = string.IsNullOrWhiteSpace(_options.ActivityRankingChannelName) ? "ランキング" : _options.ActivityRankingChannelName;
        var voiceNotificationRoleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName) ? "通話通知" : _options.VoiceNotificationRoleName;
        var registrationReviewChannelName = string.IsNullOrWhiteSpace(_options.RegistrationReviewChannelName) ? "登録確認" : _options.RegistrationReviewChannelName;
        var controlChannelName = string.IsNullOrWhiteSpace(_options.ControlChannelName) ? "bot-control" : _options.ControlChannelName;
        var autoOps = new List<string>();

        var settings = await _db.GetGuildSettingsAsync(guild.Id);

        IRole? adminRole = null;
        if (settings is not null && settings.AdminRoleId != 0)
        {
            adminRole = guild.GetRole(ToUlong(settings.AdminRoleId));
        }
        adminRole ??= guild.Roles.FirstOrDefault(role => string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
        if (adminRole is null)
        {
            adminRole = await guild.CreateRoleAsync(roleName, GuildPermissions.None, color: new Color(226, 185, 73), isHoisted: false, isMentionable: false);
            _logger.LogInformation("Admin role created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ 管理者ロール作成: {adminRole.Name} ({MentionRole(adminRole.Id)})");
        }

        IRole? memberRole = null;
        if (settings is not null && settings.MemberRoleId != 0)
        {
            memberRole = guild.GetRole(ToUlong(settings.MemberRoleId));
        }
        memberRole ??= guild.Roles.FirstOrDefault(role => string.Equals(role.Name, memberRoleName, StringComparison.OrdinalIgnoreCase));
        if (memberRole is null)
        {
            memberRole = await guild.CreateRoleAsync(memberRoleName, GuildPermissions.None, color: new Color(72, 201, 176), isHoisted: false, isMentionable: false);
            _logger.LogInformation("Member role created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ メンバーロール作成: {memberRole.Name} ({MentionRole(memberRole.Id)})");
        }

        IRole? provisionalRole = null;
        if (settings is not null && settings.ProvisionalRoleId != 0)
        {
            provisionalRole = guild.GetRole(ToUlong(settings.ProvisionalRoleId));
        }
        provisionalRole ??= guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, provisionalRoleName, StringComparison.OrdinalIgnoreCase));
        if (provisionalRole is null)
        {
            provisionalRole = await guild.CreateRoleAsync(provisionalRoleName, GuildPermissions.None, color: new Color(230, 126, 34), isHoisted: false, isMentionable: false);
            _logger.LogInformation("Provisional role created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ 仮登録ロール作成: {provisionalRole.Name} ({MentionRole(provisionalRole.Id)})");
        }

        IRole? voiceNotificationRole = guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, voiceNotificationRoleName, StringComparison.OrdinalIgnoreCase));
        if (voiceNotificationRole is null && _options.AutoCreateVoiceNotificationRole)
        {
            voiceNotificationRole = await guild.CreateRoleAsync(
                voiceNotificationRoleName,
                GuildPermissions.None,
                color: new Color(52, 152, 219),
                isHoisted: false,
                isMentionable: true);
            autoOps.Add($"🛠️ 通話通知ロール作成: {voiceNotificationRole.Name} ({MentionRole(voiceNotificationRole.Id)})");
        }

        IMessageChannel? registrationChannel = null;
        if (settings is not null && settings.RegistrationChannelId != 0)
        {
            registrationChannel = guild.GetTextChannel(ToUlong(settings.RegistrationChannelId));
        }

        IMessageChannel? registrationReviewChannel = null;
        if (settings is not null && settings.RegistrationReviewChannelId != 0)
        {
            registrationReviewChannel = guild.GetTextChannel(ToUlong(settings.RegistrationReviewChannelId));
        }
        registrationReviewChannel ??= guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, registrationReviewChannelName, StringComparison.OrdinalIgnoreCase));
        if (registrationReviewChannel is null)
        {
            registrationReviewChannel = await guild.CreateTextChannelAsync(registrationReviewChannelName, props =>
            {
                props.Topic = "メンバー登録申請の確認・許可/拒否を行います。";
            });
            _logger.LogInformation("Registration review channel created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ 登録確認チャンネル作成: <#{registrationReviewChannel.Id}>");
        }

        IMessageChannel? logChannel = null;
        if (settings is not null && settings.LogChannelId != 0)
        {
            logChannel = guild.GetTextChannel(ToUlong(settings.LogChannelId));
        }
        logChannel ??= guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, logChannelName, StringComparison.OrdinalIgnoreCase));
        if (logChannel is null)
        {
            logChannel = await guild.CreateTextChannelAsync(logChannelName, props =>
            {
                props.Topic = "監視ログが投稿されます。";
            });
            _logger.LogInformation("Log channel created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ ログチャンネル作成: <#{logChannel.Id}>");
        }

        IMessageChannel? notificationChannel = guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, notificationChannelName, StringComparison.OrdinalIgnoreCase));
        if (notificationChannel is null)
        {
            notificationChannel = await guild.CreateTextChannelAsync(notificationChannelName, props =>
            {
                props.Topic = "通知が投稿されます。";
            });
            _logger.LogInformation("Notification channel created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ 通知チャンネル作成: <#{notificationChannel.Id}>");
        }

        IMessageChannel? activityRankingChannel = guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, activityRankingChannelName, StringComparison.OrdinalIgnoreCase));
        if (activityRankingChannel is null && _options.AutoCreateActivityRankingChannel)
        {
            activityRankingChannel = await guild.CreateTextChannelAsync(activityRankingChannelName, props =>
            {
                props.Topic = "日次/週次アクティビティランキングが投稿されます。";
            });
            _logger.LogInformation("Activity ranking channel created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ ランキングチャンネル作成: <#{activityRankingChannel.Id}>");
        }

        IMessageChannel? controlChannel = null;
        if (settings is not null && settings.ControlChannelId != 0)
        {
            controlChannel = guild.GetTextChannel(ToUlong(settings.ControlChannelId));
        }
        controlChannel ??= guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, controlChannelName, StringComparison.OrdinalIgnoreCase));
        if (controlChannel is null)
        {
            controlChannel = await guild.CreateTextChannelAsync(controlChannelName, props =>
            {
                props.Topic = "Botの管理パネルです。";
            });
            _logger.LogInformation("Control channel created in guild {GuildId}.", guild.Id);
            autoOps.Add($"🛠️ コントロールチャンネル作成: <#{controlChannel.Id}>");
        }

        var registrationApprovalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;

        await _db.UpsertGuildSettingsAsync(
            guild.Id,
            adminRole.Id,
            memberRole.Id,
            provisionalRole.Id,
            registrationChannel?.Id ?? 0,
            registrationReviewChannel.Id,
            registrationApprovalRequired,
            inactiveAutoKickEnabled,
            logChannel.Id,
            controlChannel.Id);

        if (_options.ApplyChannelPermissionOverwritesOnStartup)
        {
            await EnsureVisibilityAsync(
                guild,
                adminRole,
                memberRole,
                registrationChannel,
                registrationReviewChannel,
                logChannel,
                notificationChannel,
                controlChannel,
                activityRankingChannel);
            autoOps.Add("🔧 チャンネル権限オーバーライト同期を実施");
        }
        else
        {
            _logger.LogInformation("Skipping channel permission overwrite sync for guild {GuildId}.", guild.Id);
        }
        await EnsureTempVoiceLobbyAsync(guild, adminRole, memberRole);

        if (autoOps.Count > 0)
        {
            await _audit.SendSystemAsync(guild, "自動セットアップ", string.Join('\n', autoOps), Color.LightGrey);
        }

        await RefreshExistingPanelsAsync(guild, registrationChannel, controlChannel);
        _ = _stats.RefreshIfConfiguredAsync(guild);
    }

    private async Task RefreshExistingPanelsAsync(SocketGuild guild, IMessageChannel? registrationChannel, IMessageChannel controlChannel)
    {
        var panelFallbackChannel = registrationChannel ?? controlChannel;
        await RefreshMemberRegistrationPanelMessageAsync(guild, panelFallbackChannel);
        await RefreshLodestonePanelMessageAsync(guild, panelFallbackChannel);
        await RefreshControlPanelMessageAsync(guild, controlChannel);
        if (_options.RecruitFeatureEnabled)
        {
            await RefreshRecruitPanelMessageAsync(guild, controlChannel);
            await RefreshRecruitListMessageAsync(guild, controlChannel);
        }
        else
        {
            await RefreshRecruitPanelMessageAsync(guild, controlChannel);
        }
    }

    private async Task RefreshMemberRegistrationPanelMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildPanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            var components = MemberRegistrationPanel.BuildComponents();
            await message.ModifyAsync(props =>
            {
                props.Content = MemberRegistrationPanel.PanelMessage;
                props.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh member registration panel message.");
        }
    }

    private async Task RefreshLodestonePanelMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildLodestonePanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            var components = LodestonePanel.BuildComponents();
            await message.ModifyAsync(props =>
            {
                props.Content = LodestonePanel.PanelMessage;
                props.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh lodestone panel message.");
        }
    }

    private async Task RefreshControlPanelMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildControlPanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            var components = ControlPanel.BuildComponents();
            await message.ModifyAsync(props =>
            {
                props.Content = null;
                props.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh control panel message.");
        }
    }

    private async Task RefreshRecruitPanelMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        var panel = await _db.GetGuildRecruitPanelAsync(guild.Id);
        if (panel is null || panel.MessageId == 0)
        {
            return;
        }

        var channel = panel.ChannelId != 0
            ? guild.GetTextChannel(ToUlong(panel.ChannelId))
            : null;

        channel ??= fallbackChannel as SocketTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(panel.MessageId)) as IUserMessage;
            if (message is null)
            {
                return;
            }

            var panelMessage = RecruitmentPanel.BuildPanelMessage(_options.RecruitFeatureEnabled);
            var components = RecruitmentPanel.BuildPanelComponents(_options.RecruitFeatureEnabled);
            await message.ModifyAsync(props =>
            {
                props.Content = panelMessage;
                props.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recruit panel message.");
        }
    }

    private async Task RefreshRecruitListMessageAsync(SocketGuild guild, IMessageChannel fallbackChannel)
    {
        await _recruitListService.RefreshAsync(guild, fallbackChannel);
    }

    private async Task HandleRecruitReminderAsync(RecruitPost post)
    {
        var guild = _client.GetGuild(ToUlong(post.GuildId));
        if (guild is null)
        {
            return;
        }

        if (post.ReminderSentAt.HasValue)
        {
            return;
        }

        var schedule = FormatRecruitSchedule(post.ScheduleText, post.ScheduleAt);
        var mentionUserIds = await BuildRecruitNoticeMentionUserIdsAsync(post);
        var sent = await TryPostRecruitNoticeAsync(
            guild,
            post,
            $"開始時刻が近づいています（{schedule}）。",
            mentionUserIds);
        if (sent)
        {
            await _db.MarkRecruitReminderSentAsync(post.Id);
        }
    }

    private async Task HandleRecruitAutoCloseAsync(RecruitPost post)
    {
        var guild = _client.GetGuild(ToUlong(post.GuildId));
        if (guild is null)
        {
            return;
        }

        var previousStatus = post.Status;
        var mentionUserIds = await BuildRecruitNoticeMentionUserIdsAsync(post);
        await _db.UpdateRecruitPostStatusAsync(post.Id, RecruitPostStatus.Closed);
        post.Status = RecruitPostStatus.Closed;
        await UpdateRecruitPostMessageAsync(guild, post);
        await TryPostRecruitNoticeAsync(
            guild,
            post,
            "開始時刻になったため募集を自動で終了しました。",
            mentionUserIds);

        var fallback = guild.GetTextChannel(ToUlong(post.ChannelId));
        if (fallback is not null)
        {
            await _recruitListService.RefreshAsync(guild, fallback);
        }

        await _audit.SendSystemAsync(
            guild,
            "募集状態遷移",
            string.Join('\n', new[]
            {
                $"📝 募集: {post.Title}",
                $"🔄 状態: {FormatRecruitStatusLabel(previousStatus)} -> {FormatRecruitStatusLabel(post.Status)}",
                "🧭 トリガー: 自動（開始時刻到達）",
                $"💬 チャンネル: <#{ToUlong(post.ChannelId)}>",
                $"🔗 メッセージ: {BuildMessageLink(guild.Id, ToUlong(post.ChannelId), ToUlong(post.MessageId))}"
            }),
            Color.DarkGrey);
    }

    private async Task<IReadOnlyList<ulong>> BuildRecruitNoticeMentionUserIdsAsync(RecruitPost post)
    {
        var ids = new HashSet<ulong>();
        ids.Add(ToUlong(post.CreatorUserId));

        try
        {
            var slots = await _db.ListRecruitSlotsAsync(post.Id);
            foreach (var slot in slots)
            {
                if (!slot.UserId.HasValue)
                {
                    continue;
                }

                ids.Add(ToUlong(slot.UserId.Value));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build recruit notice mention targets for post {PostId}.", post.Id);
        }

        return ids.ToList();
    }

    private async Task TryPostDailyActivityRankingAsync(DateTimeOffset nowUtc)
    {
        var topCount = Math.Clamp(_options.DailyActivityRankingTopCount, 1, 10);
        var intervalMinutes = Math.Clamp(_options.DailyActivityRankingUpdateIntervalMinutes, 5, 180);
        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        var slotKey = (int)(nowJst.ToUnixTimeSeconds() / (intervalMinutes * 60L));
        var dayStartJst = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset);
        var fromUtc = dayStartJst.ToUniversalTime();

        foreach (var guild in _client.Guilds)
        {
            if (_dailyActivityRankingPosted.TryGetValue(guild.Id, out var postedSlot) && postedSlot == slotKey)
            {
                continue;
            }

            var panel = await _db.GetGuildDailyActivityPanelAsync(guild.Id);
            if (panel is null || panel.ChannelId == 0)
            {
                continue;
            }

            var channel = guild.GetTextChannel(ToUlong(panel.ChannelId));
            if (channel is null)
            {
                continue;
            }

            try
            {
                var embed = await _voiceStats.BuildActivityRankingEmbedAsync(guild, fromUtc, nowUtc, topCount, "今日のアクティビティランキング");
                var message = await UpsertDailyActivityRankingMessageAsync(guild, channel, panel.MessageId == 0 ? 0 : ToUlong(panel.MessageId), nowJst, embed);
                await _db.UpsertGuildDailyActivityPanelAsync(guild.Id, channel.Id, message.Id);
                _dailyActivityRankingPosted[guild.Id] = slotKey;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post daily activity ranking. GuildId={GuildId}", guild.Id);
                await NotifyFailureAsync(
                    guild,
                    "daily-ranking-update",
                    "障害通知: 今日ランキング更新失敗",
                    $"チャンネル: <#{channel.Id}>",
                    ex);
            }
        }
    }

    private async Task TryPostWeeklyVoiceRankingAsync(DateTimeOffset nowUtc)
    {
        var rangeDays = Math.Clamp(_options.VoiceWeeklyRankingRangeDays, 1, 30);
        var topCount = Math.Clamp(_options.VoiceWeeklyRankingTopCount, 1, 10);
        var targetHour = Math.Clamp(_options.VoiceWeeklyRankingPostHourJst, 0, 23);
        var targetDay = Math.Clamp(_options.VoiceWeeklyRankingPostDayOfWeekJst, 0, 6);

        var nowJst = nowUtc.ToOffset(JapanTimeOffset);
        if ((int)nowJst.DayOfWeek != targetDay || nowJst.Hour != targetHour)
        {
            return;
        }

        var week = ISOWeek.GetWeekOfYear(nowJst.DateTime);
        var year = ISOWeek.GetYear(nowJst.DateTime);
        var weekKey = (year * 100) + week;

        foreach (var guild in _client.Guilds)
        {
            if (_weeklyVoiceRankingPosted.TryGetValue(guild.Id, out var postedWeek) && postedWeek == weekKey)
            {
                continue;
            }

            var panel = await _db.GetGuildWeeklyActivityPanelAsync(guild.Id);
            IMessageChannel? channel = null;
            if (panel is not null && panel.ChannelId != 0)
            {
                channel = guild.GetTextChannel(ToUlong(panel.ChannelId));
            }

            channel ??= await ResolveActivityRankingChannelAsync(guild);
            if (channel is null)
            {
                continue;
            }

            try
            {
                var periodEndJst = new DateTimeOffset(nowJst.Year, nowJst.Month, nowJst.Day, 0, 0, 0, JapanTimeOffset);
                var periodStartJst = periodEndJst.AddDays(-rangeDays);
                if (guild.CreatedAt.ToOffset(JapanTimeOffset) > periodStartJst)
                {
                    _logger.LogInformation(
                        "Skip weekly ranking because full range has not elapsed yet. GuildId={GuildId}, PeriodStartJst={PeriodStartJst}, GuildCreatedAt={GuildCreatedAt}",
                        guild.Id,
                        periodStartJst,
                        guild.CreatedAt);
                    continue;
                }

                var embed = await _voiceStats.BuildActivityRankingEmbedAsync(
                    guild,
                    periodStartJst.ToUniversalTime(),
                    periodEndJst.ToUniversalTime(),
                    topCount,
                    "週間アクティビティランキング");
                var message = await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
                await _db.UpsertGuildWeeklyActivityPanelAsync(guild.Id, channel.Id, message.Id);
                _weeklyVoiceRankingPosted[guild.Id] = weekKey;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post weekly voice ranking. GuildId={GuildId}", guild.Id);
                await NotifyFailureAsync(
                    guild,
                    "weekly-ranking-update",
                    "障害通知: 週間ランキング更新失敗",
                    $"チャンネル: <#{channel.Id}>",
                    ex);
            }
        }
    }

    private async Task SyncActivityRolesAsync(DateTimeOffset nowUtc)
    {
        foreach (var guild in _client.Guilds)
        {
            try
            {
                await SyncActivityRolesInGuildAsync(guild, nowUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync activity roles in guild {GuildId}.", guild.Id);
            }
        }
    }

    private async Task SyncActivityRolesInGuildAsync(SocketGuild guild, DateTimeOffset nowUtc)
    {
        var guildSettings = await _db.GetGuildSettingsAsync(guild.Id);
        var inactiveAutoKickEnabled = guildSettings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;

        var (activeRole, inactiveRole) = await ResolveActivityRolesAsync(guild);
        var roleSyncEnabled = activeRole is not null || inactiveRole is not null;
        var shouldRunKick = ShouldRunInactiveAutoKickNow(guild.Id, inactiveAutoKickEnabled, nowUtc, out var kickDayKey, out var kickEvaluationBaseUtc);
        var kickEnabled = shouldRunKick;
        if (!roleSyncEnabled && !kickEnabled)
        {
            return;
        }

        // Guild member cache can be incomplete right after startup unless downloaded explicitly.
        try
        {
            await guild.DownloadUsersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download guild users before activity role sync. GuildId={GuildId}", guild.Id);
        }

        if (inactiveRole is not null && activeRole is not null && inactiveRole.Id == activeRole.Id)
        {
            _logger.LogWarning("Active role and inactive role are identical in guild {GuildId}. Active role sync is skipped.", guild.Id);
            activeRole = null;
            roleSyncEnabled = inactiveRole is not null || activeRole is not null;
        }

        var roleCutoff = nowUtc.AddDays(-Math.Max(1, _options.InactiveRoleDays));
        var activeUserIds = roleSyncEnabled
            ? await _db.ListActiveUserIdsSinceAsync(guild.Id, roleCutoff)
            : Array.Empty<ulong>();
        var kickDays = Math.Max(1, _options.InactiveAutoKickDays);
        var kickCutoff = kickEvaluationBaseUtc.AddDays(-kickDays);
        var activeUserIdsForKick = kickEnabled
            ? await _db.ListActiveUserIdsSinceAsync(guild.Id, kickCutoff)
            : Array.Empty<ulong>();
        var kickExemptRoleNames = BuildInactiveKickExemptRoleNameSet();
        var currentBotUser = guild.CurrentUser;
        var addedActive = 0;
        var removedActive = 0;
        var addedInactive = 0;
        var removedInactive = 0;
        var kickedUsers = 0;
        var kickExemptUsers = 0;
        var kickPermissionSkippedUsers = 0;
        var eligibleUsers = 0;

        foreach (var user in guild.Users.ToArray())
        {
            if (user.IsBot || user.Id == guild.OwnerId)
            {
                continue;
            }

            eligibleUsers++;
            var hasInactiveRole = inactiveRole is not null && user.Roles.Any(role => role.Id == inactiveRole.Id);
            var hasActiveRole = activeRole is not null && user.Roles.Any(role => role.Id == activeRole.Id);
            var isActive = activeUserIds.Contains(user.Id);
            var joinedAt = user.JoinedAt ?? user.CreatedAt;
            var joinedRecently = joinedAt > roleCutoff;
            var isActiveForKick = activeUserIdsForKick.Contains(user.Id);

            if (kickEnabled && !isActiveForKick && joinedAt <= kickCutoff)
            {
                if (IsInactiveKickExemptUser(user, kickExemptRoleNames))
                {
                    kickExemptUsers++;
                }
                else if (CanKickMember(currentBotUser, user, guild.OwnerId))
                {
                    try
                    {
                        await user.KickAsync($"Inactive for {kickDays} days.");
                        kickedUsers++;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to auto-kick inactive user. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                    }
                }
                else
                {
                    kickPermissionSkippedUsers++;
                }
            }

            if (!roleSyncEnabled)
            {
                continue;
            }

            if (isActive)
            {
                if (hasInactiveRole && inactiveRole is not null)
                {
                    try
                    {
                        await user.RemoveRoleAsync(inactiveRole);
                        removedInactive++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove inactive role. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                    }
                }

                if (!hasActiveRole && activeRole is not null)
                {
                    try
                    {
                        await user.AddRoleAsync(activeRole);
                        addedActive++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to add active role. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                    }
                }

                continue;
            }

            if (joinedRecently)
            {
                if (hasInactiveRole && inactiveRole is not null)
                {
                    try
                    {
                        await user.RemoveRoleAsync(inactiveRole);
                        removedInactive++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove inactive role from recently joined member. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                    }
                }

                continue;
            }

            if (hasActiveRole && activeRole is not null)
            {
                try
                {
                    await user.RemoveRoleAsync(activeRole);
                    removedActive++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove active role. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                }
            }

            if (!hasInactiveRole && inactiveRole is not null)
            {
                try
                {
                    await user.AddRoleAsync(inactiveRole);
                    addedInactive++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add inactive role. GuildId={GuildId}, UserId={UserId}", guild.Id, user.Id);
                }
            }
        }

        if (shouldRunKick)
        {
            _inactiveAutoKickProcessedDay[guild.Id] = kickDayKey;
        }

        _logger.LogInformation(
            "Activity sync completed. GuildId={GuildId}, EligibleUsers={EligibleUsers}, ActiveUsers={ActiveUsers}, +Active={AddedActive}, -Active={RemovedActive}, +Inactive={AddedInactive}, -Inactive={RemovedInactive}, Kicked={KickedUsers}, KickExempt={KickExemptUsers}, KickSkippedByPermission={KickPermissionSkippedUsers}",
            guild.Id,
            eligibleUsers,
            activeUserIds.Count,
            addedActive,
            removedActive,
            addedInactive,
            removedInactive,
            kickedUsers,
            kickExemptUsers,
            kickPermissionSkippedUsers);
    }

    private async Task<(IRole? ActiveRole, IRole? InactiveRole)> ResolveActivityRolesAsync(SocketGuild guild)
    {
        var inactiveRoleName = string.IsNullOrWhiteSpace(_options.InactiveRoleName) ? "非アクティブ" : _options.InactiveRoleName;
        IRole? inactiveRole = null;
        if (_options.InactiveRoleEnabled)
        {
            inactiveRole = guild.Roles.FirstOrDefault(role =>
                string.Equals(role.Name, inactiveRoleName, StringComparison.OrdinalIgnoreCase));

            if (inactiveRole is null && _options.AutoCreateInactiveRole)
            {
                inactiveRole = await guild.CreateRoleAsync(
                    inactiveRoleName,
                    GuildPermissions.None,
                    color: new Color(127, 140, 141),
                    isHoisted: false,
                    isMentionable: false);
            }
        }

        var activeRoleName = string.IsNullOrWhiteSpace(_options.ActiveRoleName) ? "アクティブ" : _options.ActiveRoleName;
        IRole? activeRole = null;
        if (_options.ActiveRoleEnabled)
        {
            activeRole = guild.Roles.FirstOrDefault(role =>
                string.Equals(role.Name, activeRoleName, StringComparison.OrdinalIgnoreCase));

            if (activeRole is null && _options.AutoCreateActiveRole)
            {
                activeRole = await guild.CreateRoleAsync(
                    activeRoleName,
                    GuildPermissions.None,
                    color: new Color(46, 204, 113),
                    isHoisted: false,
                    isMentionable: false);
            }
        }

        if (activeRole is not null && inactiveRole is not null && activeRole.Id == inactiveRole.Id)
        {
            _logger.LogWarning("Active role and inactive role are identical in guild {GuildId}. Active role sync is skipped.", guild.Id);
            activeRole = null;
        }

        return (activeRole, inactiveRole);
    }

    private HashSet<string> BuildInactiveKickExemptRoleNameSet()
    {
        return _options.BuildManagementRoleNameSet();
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

    private async Task MarkUserActiveNowAsync(SocketGuild guild, ulong userId)
    {
        if (!(_options.ActiveRoleEnabled || _options.InactiveRoleEnabled))
        {
            return;
        }

        var user = guild.GetUser(userId);
        if (user is null || user.IsBot || user.Id == guild.OwnerId)
        {
            return;
        }

        IRole? activeRole;
        IRole? inactiveRole;
        try
        {
            (activeRole, inactiveRole) = await ResolveActivityRolesAsync(guild);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve activity roles for immediate apply. GuildId={GuildId}, UserId={UserId}", guild.Id, userId);
            return;
        }

        if (activeRole is null && inactiveRole is null)
        {
            return;
        }

        try
        {
            var removedInactive = false;
            var addedActive = false;
            if (inactiveRole is not null && user.Roles.Any(role => role.Id == inactiveRole.Id))
            {
                await user.RemoveRoleAsync(inactiveRole);
                removedInactive = true;
            }

            if (activeRole is not null && !user.Roles.Any(role => role.Id == activeRole.Id))
            {
                await user.AddRoleAsync(activeRole);
                addedActive = true;
            }

            if (removedInactive || addedActive)
            {
                _logger.LogInformation(
                    "Applied immediate activity role update. GuildId={GuildId}, UserId={UserId}, AddedActive={AddedActive}, RemovedInactive={RemovedInactive}",
                    guild.Id,
                    userId,
                    addedActive,
                    removedInactive);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to immediately apply active role. GuildId={GuildId}, UserId={UserId}", guild.Id, userId);
        }
    }

    private async Task HandleRecruitPostDeletedAsync(SocketGuild guild, SocketGuildChannel deletedChannel, RecruitPost post)
    {
        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            return;
        }

        await _db.UpdateRecruitPostStatusAsync(post.Id, RecruitPostStatus.Canceled);
        post.Status = RecruitPostStatus.Canceled;
        await TryPostRecruitNoticeAsync(guild, post, "募集メッセージが削除されたため募集を自動中断しました。");

        if (guild.GetTextChannel(ToUlong(post.ChannelId)) is IMessageChannel fallback)
        {
            await _recruitListService.RefreshAsync(guild, fallback);
        }
        else if (deletedChannel is IMessageChannel deletedMessageChannel)
        {
            await _recruitListService.RefreshAsync(guild, deletedMessageChannel);
        }
    }

    private async Task UpdateRecruitPostMessageAsync(SocketGuild guild, RecruitPost post)
    {
        var channel = guild.GetTextChannel(ToUlong(post.ChannelId));
        if (channel is null)
        {
            return;
        }

        IUserMessage? message = null;
        try
        {
            message = await channel.GetMessageAsync(ToUlong(post.MessageId)) as IUserMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch recruit message {MessageId} in channel {ChannelId}.", ToUlong(post.MessageId), channel.Id);
        }

        if (message is null)
        {
            return;
        }

        var slots = await _db.ListRecruitSlotsAsync(post.Id);
        var embed = BuildRecruitEmbed(post, slots, post.DisallowDuplicateJobs);
        var positions = GetOrderedPositions(slots.Where(slot => !slot.IsClosed).ToList());
        await message.ModifyAsync(props =>
        {
            props.Embed = embed;
            props.Components = RecruitmentPanel.BuildRecruitComponents(post.Status, positions);
        });
    }

    private async Task<bool> TryPostRecruitNoticeAsync(
        SocketGuild guild,
        RecruitPost post,
        string text,
        IReadOnlyCollection<ulong>? mentionUserIds = null)
    {
        var targetIds = mentionUserIds?
            .Where(id => id != 0)
            .Distinct()
            .ToList() ?? new List<ulong>();
        var mentions = targetIds.Count == 0
            ? string.Empty
            : string.Join(' ', targetIds.Select(MentionUser));
        var payload = string.IsNullOrWhiteSpace(mentions)
            ? text
            : $"{text}\n{mentions}";
        var allowedMentions = targetIds.Count == 0
            ? AllowedMentions.None
            : new AllowedMentions { UserIds = targetIds };

        var thread = post.ThreadChannelId.HasValue
            ? await ResolveRecruitThreadChannelAsync(ToUlong(post.ThreadChannelId.Value))
            : null;

        if (thread is not null)
        {
            try
            {
                await thread.SendMessageAsync(payload, allowedMentions: allowedMentions);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post recruit notice in thread for post {PostId}.", post.Id);
            }
        }

        var channel = guild.GetTextChannel(ToUlong(post.ChannelId));
        if (channel is null)
        {
            return false;
        }

        var link = BuildMessageLink(guild.Id, channel.Id, ToUlong(post.MessageId));
        try
        {
            await channel.SendMessageAsync($"{payload}\n<{link}>", allowedMentions: allowedMentions);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post recruit notice in channel for post {PostId}.", post.Id);
            return false;
        }
    }

    private async Task<bool> TryDeleteRecruitThreadAsync(RecruitPost post)
    {
        if (!post.ThreadChannelId.HasValue)
        {
            return true;
        }

        var thread = await ResolveRecruitThreadChannelAsync(ToUlong(post.ThreadChannelId.Value));
        if (thread is null)
        {
            return true;
        }

        try
        {
            await thread.DeleteAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete recruit thread {ChannelId}.", thread.Id);
            return false;
        }
    }

    private async Task<bool> TryDeleteRecruitMessageAsync(SocketGuild guild, RecruitPost post)
    {
        var channel = guild.GetTextChannel(ToUlong(post.ChannelId));
        if (channel is null)
        {
            return true;
        }

        try
        {
            var message = await channel.GetMessageAsync(ToUlong(post.MessageId)) as IUserMessage;
            if (message is null)
            {
                return true;
            }

            await message.DeleteAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete recruit message {MessageId}.", ToUlong(post.MessageId));
            return false;
        }
    }

    private async Task<IThreadChannel?> ResolveRecruitThreadChannelAsync(ulong channelId)
    {
        if (_client.GetChannel(channelId) is IThreadChannel cached)
        {
            return cached;
        }

        try
        {
            var channel = await _client.Rest.GetChannelAsync(channelId);
            return channel as IThreadChannel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve recruit thread channel {ChannelId}.", channelId);
            return null;
        }
    }

    private static Embed BuildRecruitEmbed(RecruitPost post, IReadOnlyList<RecruitSlot> slots, bool disallowDuplicateJobs)
    {
        var filled = slots.Count(slot => slot.UserId.HasValue || slot.IsClosed);
        var max = slots.Count > 0 ? slots.Count : RecruitmentRules.Positions.Count;
        var (status, statusPrefix, color, footer) = post.Status switch
        {
            RecruitPostStatus.Full => ("🟡 満員", "[🟡満員] ", new Color(255, 193, 7), "この募集は満員です。参加メニューからコメント編集/参加取消ができます。"),
            RecruitPostStatus.Closed => ("⚫ 終了", "[⚫終了] ", Color.DarkGrey, "この募集は終了しました。"),
            RecruitPostStatus.Canceled => ("🔴 中断", "[🔴中断] ", new Color(231, 76, 60), "この募集は中断されました。"),
            _ => ("🟢 募集中", "[🟢募集中] ", new Color(46, 204, 113), disallowDuplicateJobs ? "参加メニューから操作できます（ジョブ重複不可）" : "参加メニューから操作できます")
        };

        var embedBuilder = new EmbedBuilder()
            .WithTitle($"{statusPrefix}募集: {post.Title}")
            .WithColor(color)
            .WithCurrentTimestamp()
            .AddField("主催", MentionUser(ToUlong(post.CreatorUserId)), true)
            .AddField("DC", string.IsNullOrWhiteSpace(post.DataCenter) ? "未指定" : post.DataCenter, true)
            .AddField("開始", FormatRecruitSchedule(post.ScheduleText, post.ScheduleAt), true)
            .AddField("人数", $"{filled}/{max}", true)
            .AddField("状態", status, true)
            .AddField("コメント", string.IsNullOrWhiteSpace(post.Note) ? "なし" : post.Note, false)
            .AddField("ポジション", BuildRecruitSlotLines(slots), false)
            .WithFooter(footer);

        if (post.Status is RecruitPostStatus.Closed or RecruitPostStatus.Canceled)
        {
            var closedAt = post.ClosedAt?.ToOffset(JapanTimeOffset).ToString(RecruitScheduleFormat, CultureInfo.InvariantCulture) ?? "不明";
            embedBuilder.AddField("終了", closedAt, true);
        }

        return embedBuilder.Build();
    }

    private static string BuildRecruitSlotLines(IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return "なし";
        }

        var map = slots
            .GroupBy(slot => slot.Position, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var ordered = GetOrderedPositions(slots);
        var lines = new List<string>();
        foreach (var position in ordered)
        {
            if (!map.TryGetValue(position, out var slot))
            {
                lines.Add($"{position}: 空き");
                continue;
            }

            if (slot.IsClosed && !slot.UserId.HasValue)
            {
                var closedJob = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
                lines.Add($"{position}: 〆（{closedJob}）");
                continue;
            }

            if (!slot.UserId.HasValue)
            {
                lines.Add($"{position}: 空き");
                continue;
            }

            var job = string.IsNullOrWhiteSpace(slot.Job) ? "?" : slot.Job;
            lines.Add($"{position}: {MentionUser(ToUlong(slot.UserId.Value))} ({job})");
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<string> GetOrderedPositions(IReadOnlyList<RecruitSlot> slots)
    {
        if (slots.Count == 0)
        {
            return RecruitmentRules.Positions;
        }

        var set = new HashSet<string>(slots.Select(slot => slot.Position), StringComparer.OrdinalIgnoreCase);
        var ordered = RecruitmentRules.Positions.Where(set.Contains).ToList();
        if (ordered.Count > 0)
        {
            return ordered;
        }

        return slots
            .Select(slot => slot.Position)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatRecruitSchedule(string? scheduleText, DateTimeOffset? scheduleAt)
    {
        if (!string.IsNullOrWhiteSpace(scheduleText))
        {
            return scheduleText;
        }

        if (!scheduleAt.HasValue)
        {
            return "未定";
        }

        return scheduleAt.Value
            .ToOffset(JapanTimeOffset)
            .ToString(RecruitScheduleFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IUserMessage> EnsurePanelMessageAsync(ulong guildId, IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing panel message.");
            }
        }

        var components = MemberRegistrationPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = MemberRegistrationPanel.PanelMessage;
                props.Components = components;
            });
            _logger.LogInformation("Panel message updated in channel {ChannelId}.", channel.Id);
            return existing;
        }

        var posted = await channel.SendMessageAsync(MemberRegistrationPanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
        await _db.UpsertGuildPanelAsync(guildId, channel.Id, posted.Id);
        _logger.LogInformation("Panel message posted in channel {ChannelId}.", channel.Id);
        return posted;
    }

    private async Task<IUserMessage> EnsureControlPanelMessageAsync(ulong guildId, IMessageChannel channel)
    {
        var panel = await _db.GetGuildControlPanelAsync(guildId);
        ulong? existingMessageId = panel is null || panel.MessageId == 0 ? null : ToUlong(panel.MessageId);
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing control panel message.");
            }
        }

        var components = ControlPanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Components = components;
            });
            return existing;
        }

        var posted = await channel.SendMessageAsync(components: components, flags: MessageFlags.SuppressNotification);
        await _db.UpsertGuildControlPanelAsync(guildId, channel.Id, posted.Id);
        return posted;
    }

    private async Task TrySelfHealDeletedPanelsAsync(
        SocketGuild guild,
        SocketGuildChannel deletedChannel,
        ulong deletedMessageId,
        IMessage? deletedMessage)
    {
        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildPanelAsync(guild.Id),
            "メンバー登録パネル",
            async channel =>
            {
                var restored = await EnsurePanelMessageAsync(guild.Id, channel, null);
                await _db.UpsertGuildPanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildLodestonePanelAsync(guild.Id),
            "Lodestone連携パネル",
            async channel =>
            {
                var restored = await EnsureLodestonePanelMessageAsync(guild.Id, channel, null);
                await _db.UpsertGuildLodestonePanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildControlPanelAsync(guild.Id),
            "管理パネル",
            async channel =>
            {
                var restored = await EnsureControlPanelMessageAsync(guild.Id, channel);
                await _db.UpsertGuildControlPanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildAdminControlPanelAsync(guild.Id),
            "ADMINコントロールパネル",
            async channel =>
            {
                var restored = await EnsureAdminControlPanelMessageAsync(guild.Id, channel, null);
                await _db.UpsertGuildAdminControlPanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildRecruitPanelAsync(guild.Id),
            "募集パネル",
            async channel =>
            {
                var restored = await EnsureRecruitPanelMessageAsync(guild.Id, channel, null);
                await _db.UpsertGuildRecruitPanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealTrackedPanelAsync(
            guild,
            deletedChannel,
            deletedMessageId,
            await _db.GetGuildRecruitListPanelAsync(guild.Id),
            "募集一覧",
            async channel =>
            {
                var restored = await _recruitListService.EnsureListMessageAsync(guild, channel, null);
                if (restored is null)
                {
                    throw new InvalidOperationException("Failed to recreate recruit list panel message.");
                }

                await _db.UpsertGuildRecruitListPanelAsync(guild.Id, channel.Id, restored.Id);
                return restored;
            });

        await TrySelfHealContentRolePanelFromCacheAsync(guild, deletedChannel, deletedMessage);
    }

    private async Task TrySelfHealTrackedPanelAsync(
        SocketGuild guild,
        SocketGuildChannel deletedChannel,
        ulong deletedMessageId,
        GuildPanel? panel,
        string panelName,
        Func<SocketTextChannel, Task<IUserMessage>> recreateAsync)
    {
        if (panel is null || panel.MessageId == 0 || ToUlong(panel.MessageId) != deletedMessageId)
        {
            return;
        }

        var channel = ResolveSelfHealChannel(guild, panel, deletedChannel);
        if (channel is null)
        {
            await NotifyFailureAsync(
                guild,
                $"self-heal-{panelName}",
                "障害通知: パネル自己修復失敗",
                $"{panelName}: チャンネル未解決 (message: {deletedMessageId})");
            return;
        }

        try
        {
            var restored = await recreateAsync(channel);
            await _audit.SendSystemAsync(
                guild,
                "パネル自己修復",
                $"{panelName} を再作成しました。\nチャンネル: <#{channel.Id}>\n旧message: {deletedMessageId}\n新message: {restored.Id}",
                Color.Orange);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to self-heal panel {PanelName}. GuildId={GuildId}, DeletedMessageId={MessageId}",
                panelName,
                guild.Id,
                deletedMessageId);
            await NotifyFailureAsync(
                guild,
                $"self-heal-{panelName}",
                "障害通知: パネル自己修復失敗",
                $"{panelName} (message: {deletedMessageId})",
                ex);
        }
    }

    private static SocketTextChannel? ResolveSelfHealChannel(SocketGuild guild, GuildPanel? panel, SocketGuildChannel deletedChannel)
    {
        if (panel is not null && panel.ChannelId != 0)
        {
            var byId = guild.GetTextChannel(ToUlong(panel.ChannelId));
            if (byId is not null)
            {
                return byId;
            }
        }

        return deletedChannel as SocketTextChannel;
    }

    private async Task TrySelfHealContentRolePanelFromCacheAsync(
        SocketGuild guild,
        SocketGuildChannel deletedChannel,
        IMessage? deletedMessage)
    {
        if (deletedChannel is not SocketTextChannel textChannel ||
            deletedMessage is not IUserMessage deletedUserMessage ||
            deletedUserMessage.Author.Id != _client.CurrentUser.Id ||
            !MemberContentRolePanel.LooksLikePanelComponents(deletedUserMessage.Components))
        {
            return;
        }

        try
        {
            var recent = await textChannel.GetMessagesAsync(30).FlattenAsync();
            var existingPanel = recent
                .OfType<IUserMessage>()
                .Any(message =>
                    message.Author.Id == _client.CurrentUser.Id &&
                    MemberContentRolePanel.LooksLikePanelComponents(message.Components));

            if (existingPanel)
            {
                return;
            }

            var definitions = await LoadMemberContentRoleDefinitionsAsync(guild.Id);
            var components = MemberContentRolePanel.BuildComponents(definitions);
            var restored = await textChannel.SendMessageAsync(components: components, flags: MessageFlags.SuppressNotification);
            await _audit.SendSystemAsync(
                guild,
                "パネル自己修復",
                $"コンテンツロールパネルを再作成しました。\nチャンネル: <#{textChannel.Id}>\n新message: {restored.Id}",
                Color.Orange);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to self-heal content role panel. GuildId={GuildId}", guild.Id);
            await NotifyFailureAsync(
                guild,
                "self-heal-content-role-panel",
                "障害通知: パネル自己修復失敗",
                "コンテンツロールパネル",
                ex);
        }
    }

    private async Task<IUserMessage> EnsureLodestonePanelMessageAsync(ulong guildId, IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing lodestone panel message.");
            }
        }

        var components = LodestonePanel.BuildComponents();
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = LodestonePanel.PanelMessage;
                props.Components = components;
            });
            return existing;
        }

        var posted = await channel.SendMessageAsync(LodestonePanel.PanelMessage, components: components, flags: MessageFlags.SuppressNotification);
        await _db.UpsertGuildLodestonePanelAsync(guildId, channel.Id, posted.Id);
        return posted;
    }

    private async Task<IUserMessage> EnsureAdminControlPanelMessageAsync(ulong guildId, IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing admin control panel message.");
            }
        }

        var settings = await _db.GetGuildSettingsAsync(guildId);
        var approvalRequired = settings?.RegistrationApprovalRequired ?? true;
        var inactiveAutoKickEnabled = settings?.InactiveAutoKickEnabled ?? _options.InactiveAutoKickEnabled;
        var embed = BuildAdminPanelStatusEmbed(approvalRequired, inactiveAutoKickEnabled);
        var components = AdminControlPanel.BuildComponents(approvalRequired, inactiveAutoKickEnabled);

        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = null;
                props.Embed = embed;
                props.Components = components;
            });
            return existing;
        }

        var posted = await channel.SendMessageAsync(embed: embed, components: components, flags: MessageFlags.SuppressNotification);
        await _db.UpsertGuildAdminControlPanelAsync(guildId, channel.Id, posted.Id);
        return posted;
    }

    private async Task<IUserMessage> EnsureRecruitPanelMessageAsync(ulong guildId, IMessageChannel channel, ulong? existingMessageId)
    {
        IUserMessage? existing = null;
        if (existingMessageId.HasValue)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId.Value) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch existing recruit panel message.");
            }
        }

        var panelMessage = RecruitmentPanel.BuildPanelMessage(_options.RecruitFeatureEnabled);
        var components = RecruitmentPanel.BuildPanelComponents(_options.RecruitFeatureEnabled);
        if (existing is not null)
        {
            await existing.ModifyAsync(props =>
            {
                props.Content = panelMessage;
                props.Components = components;
            });
            return existing;
        }

        var posted = await channel.SendMessageAsync(panelMessage, components: components, flags: MessageFlags.SuppressNotification);
        await _db.UpsertGuildRecruitPanelAsync(guildId, channel.Id, posted.Id);
        return posted;
    }

    private async Task<IReadOnlyList<ContentRoleDefinition>> LoadMemberContentRoleDefinitionsAsync(ulong guildId)
    {
        var defaults = MemberContentRolePanel.DefaultDefinitions.ToList();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < defaults.Count; i++)
        {
            indexByKey[defaults[i].Key] = i;
        }

        var custom = await _db.ListMemberContentRoleSettingsAsync(guildId);
        foreach (var row in custom)
        {
            var definition = new ContentRoleDefinition(
                row.RoleKey,
                row.Label,
                row.RoleName,
                NormalizeContentRoleButtonStyle(row.ButtonStyle),
                Math.Clamp(row.ButtonRow, 0, 4));

            if (indexByKey.TryGetValue(definition.Key, out var existingIndex))
            {
                defaults[existingIndex] = definition;
                continue;
            }

            indexByKey[definition.Key] = defaults.Count;
            defaults.Add(definition);
        }

        return defaults
            .OrderBy(x => x.Row)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
    }

    private static ButtonStyle NormalizeContentRoleButtonStyle(int style)
        => style switch
        {
            1 => ButtonStyle.Primary,
            2 => ButtonStyle.Secondary,
            3 => ButtonStyle.Success,
            4 => ButtonStyle.Danger,
            _ => ButtonStyle.Secondary
        };

    private static Embed BuildAdminPanelStatusEmbed(bool approvalRequired, bool inactiveAutoKickEnabled)
    {
        var stateText = approvalRequired ? "必要" : "不要";
        var note = approvalRequired
            ? "申請は登録確認チャンネルで管理者が許可/拒否します。"
            : "申請送信時に即時でメンバーロールを付与します。";
        var autoKickText = inactiveAutoKickEnabled ? "ON" : "OFF";
        var autoKickNote = inactiveAutoKickEnabled
            ? "毎日 00:00 (JST) に30日非アクティブのユーザーを判定して自動キックします。(メッセージ / VC / リアクション)"
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

    private async Task EnsureVisibilityAsync(
        SocketGuild guild,
        IRole adminRole,
        IRole memberRole,
        IMessageChannel? registrationChannel,
        IMessageChannel registrationReviewChannel,
        IMessageChannel logChannel,
        IMessageChannel notificationChannel,
        IMessageChannel controlChannel,
        IMessageChannel? activityRankingChannel)
    {
        var everyoneRole = guild.EveryoneRole;
        var registrationAllow = new OverwritePermissions(
            viewChannel: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            sendMessages: PermValue.Allow);

        if (registrationChannel is IGuildChannel regGuildChannel && regGuildChannel is not IThreadChannel)
        {
            await regGuildChannel.AddPermissionOverwriteAsync(everyoneRole, registrationAllow);
            await regGuildChannel.AddPermissionOverwriteAsync(memberRole, registrationAllow);
            await regGuildChannel.AddPermissionOverwriteAsync(adminRole, registrationAllow);
        }

        var denyView = new OverwritePermissions(viewChannel: PermValue.Deny);
        var allowView = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow);

        if (registrationReviewChannel is IGuildChannel reviewGuildChannel && reviewGuildChannel is not IThreadChannel)
        {
            var reviewAdmin = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow);
            await reviewGuildChannel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await reviewGuildChannel.AddPermissionOverwriteAsync(adminRole, reviewAdmin);
        }

        if (logChannel is IGuildChannel logGuildChannel && logGuildChannel is not IThreadChannel)
        {
            await logGuildChannel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await logGuildChannel.AddPermissionOverwriteAsync(memberRole, denyView);
            await logGuildChannel.AddPermissionOverwriteAsync(adminRole, allowView);
        }

        if (notificationChannel is IGuildChannel notificationGuildChannel && notificationGuildChannel is not IThreadChannel)
        {
            var notificationMember = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Deny);
            var notificationAdmin = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow);
            await notificationGuildChannel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await notificationGuildChannel.AddPermissionOverwriteAsync(memberRole, notificationMember);
            await notificationGuildChannel.AddPermissionOverwriteAsync(adminRole, notificationAdmin);
        }

        if (controlChannel is IGuildChannel controlGuildChannel && controlGuildChannel is not IThreadChannel)
        {
            var memberSend = _options.ControlChannelMemberCanSend ? PermValue.Allow : PermValue.Deny;
            var controlMember = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: memberSend);
            var controlAdmin = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow);
            await controlGuildChannel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await controlGuildChannel.AddPermissionOverwriteAsync(memberRole, controlMember);
            await controlGuildChannel.AddPermissionOverwriteAsync(adminRole, controlAdmin);
        }

        if (activityRankingChannel is IGuildChannel rankingGuildChannel && rankingGuildChannel is not IThreadChannel)
        {
            var rankingMember = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Deny);
            var rankingAdmin = new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow);
            await rankingGuildChannel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await rankingGuildChannel.AddPermissionOverwriteAsync(memberRole, rankingMember);
            await rankingGuildChannel.AddPermissionOverwriteAsync(adminRole, rankingAdmin);
        }

        foreach (var channel in guild.Channels)
        {
            if (channel is IThreadChannel)
            {
                continue;
            }

            if ((registrationChannel is not null && channel.Id == registrationChannel.Id)
                || channel.Id == registrationReviewChannel.Id
                || channel.Id == logChannel.Id
                || channel.Id == notificationChannel.Id
                || channel.Id == controlChannel.Id
                || (activityRankingChannel is not null && channel.Id == activityRankingChannel.Id))
            {
                continue;
            }

            await channel.AddPermissionOverwriteAsync(everyoneRole, denyView);
            await channel.AddPermissionOverwriteAsync(memberRole, allowView);
            await channel.AddPermissionOverwriteAsync(adminRole, allowView);
        }
    }

    private async Task OnUserJoinedAsync(SocketGuildUser user)
    {
        var summary = BuildMemberSummary(user, user.Guild.MemberCount);
        var embed = CreateLogEmbedBase(user.Guild, "メンバー参加", Color.Green, user)
            .WithDescription(summary)
            .Build();

        await SendLogEmbedAsync(user.Guild, embed);
        _ = _stats.RefreshIfConfiguredAsync(user.Guild);
    }

    private async Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        var summary = BuildMemberSummary(user, guild.MemberCount);
        var embed = CreateLogEmbedBase(guild, "メンバー退出", Color.DarkGrey, user)
            .WithDescription(summary)
            .Build();

        await SendLogEmbedAsync(guild, embed);
        _ = _stats.RefreshIfConfiguredAsync(guild);
    }

    private async Task OnGuildMemberUpdatedAsync(Cacheable<SocketGuildUser, ulong> beforeCache, SocketGuildUser after)
    {
        await Task.CompletedTask;
    }

    private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot)
        {
            return;
        }

        var beforeChannel = before.VoiceChannel;
        var afterChannel = after.VoiceChannel;

        if (beforeChannel?.Id == afterChannel?.Id)
        {
            return;
        }

        var guild = (user as SocketGuildUser)?.Guild ?? beforeChannel?.Guild ?? afterChannel?.Guild;
        if (guild is null)
        {
            return;
        }

        await MarkUserActiveNowAsync(guild, user.Id);

        var now = DateTimeOffset.UtcNow;
        var lobby = FindTempVoiceLobby(guild);
        if (afterChannel is not null && lobby is not null && afterChannel.Id == lobby.Id)
        {
            var handled = await HandleTempVoiceLobbyJoinAsync(guild, lobby, user);
            if (handled)
            {
                return;
            }
        }

        if (afterChannel is not null && IsTempVoiceChannel(afterChannel, lobby))
        {
            CancelTempVoiceDeletion(afterChannel.Id);
        }

        if (beforeChannel is not null && IsTempVoiceChannel(beforeChannel, lobby) && beforeChannel.Id != afterChannel?.Id)
        {
            await HandleTempVoiceOwnerLeftAsync(beforeChannel, user);
        }
        var duration = await ResolveVoiceDurationAsync(guild, user, beforeChannel, now);
        var summary = BuildVoiceSummary(guild, user, beforeChannel, afterChannel, duration);
        var isFirstJoinInChannel = IsFirstHumanJoinInChannel(beforeChannel, afterChannel);
        var endedChannel = ResolveEndedVoiceChannel(beforeChannel, afterChannel);
        var shouldNotifyUserVoiceActivity = true;
        if (isFirstJoinInChannel || endedChannel is not null)
        {
            shouldNotifyUserVoiceActivity = await ShouldNotifyUserVoiceActivityAsync(user.Id);
        }

        string title;
        Color color;
        if (beforeChannel is null && afterChannel is not null)
        {
            title = "VC参加";
            color = Color.Green;
        }
        else if (beforeChannel is not null && afterChannel is null)
        {
            title = "VC退出";
            color = Color.DarkGrey;
        }
        else
        {
            title = "VC移動";
            color = Color.Orange;
        }

        var embed = CreateLogEmbedBase(guild, title, color, user, preferGuildDisplayName: true)
            .WithDescription(summary)
            .Build();

        await SendLogEmbedAsync(guild, embed);

        if (shouldNotifyUserVoiceActivity && isFirstJoinInChannel && afterChannel is not null)
        {
            var firstJoinEmbed = CreateLogEmbedBase(guild, "通話開始", Color.Green, user, preferGuildDisplayName: true)
                .WithDescription(BuildVoiceFirstJoinSummary(guild, user, afterChannel, now))
                .Build();
            await SendNotificationEmbedAsync(guild, firstJoinEmbed, mentionVoiceRole: true);
        }

        if (shouldNotifyUserVoiceActivity && endedChannel is not null)
        {
            var endedEmbed = CreateLogEmbedBase(guild, "通話終了", Color.DarkGrey, user, preferGuildDisplayName: true)
                .WithDescription(BuildVoiceCallEndedSummary(guild, user, endedChannel, now, duration))
                .Build();
            await SendNotificationEmbedAsync(guild, endedEmbed, mentionVoiceRole: true);
        }

        if (afterChannel is not null && _options.VoiceProfileEmbedEnabled)
        {
            await SendVoiceJoinEmbedAsync(afterChannel, user, now);
        }

        if (beforeChannel is null && afterChannel is not null)
        {
            await CloseOpenVoiceSessionAsync(guild, user, now);
            await _db.StartVoiceSessionAsync(guild.Id, afterChannel.Id, user.Id, user.ToString() ?? string.Empty, now);
        }
        else if (beforeChannel is not null && afterChannel is null)
        {
            await CloseVoiceSessionAsync(guild, user, beforeChannel, now);
        }
        else if (beforeChannel is not null && afterChannel is not null)
        {
            await CloseVoiceSessionAsync(guild, user, beforeChannel, now);
            await _db.StartVoiceSessionAsync(guild.Id, afterChannel.Id, user.Id, user.ToString() ?? string.Empty, now);
        }

        if (beforeChannel is not null && IsTempVoiceChannel(beforeChannel, lobby) && beforeChannel.ConnectedUsers.Count == 0)
        {
            await _voiceTextService.ClearOwnerTransferAsync(beforeChannel);
            await ScheduleTempVoiceDeletionAsync(beforeChannel);
        }
    }

    private async Task OnMessageDeletedAsync(Cacheable<IMessage, ulong> cache, Cacheable<IMessageChannel, ulong> channelCache)
    {
        if (channelCache.HasValue && channelCache.Value is SocketGuildChannel guildChannel)
        {
            var deletedMessage = cache.HasValue ? cache.Value : null;
            try
            {
                await TrySelfHealDeletedPanelsAsync(guildChannel.Guild, guildChannel, cache.Id, deletedMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to self-heal deleted panels. GuildId={GuildId}, MessageId={MessageId}", guildChannel.Guild.Id, cache.Id);
                await NotifyFailureAsync(
                    guildChannel.Guild,
                    "panel-self-heal",
                    "障害通知: パネル自己修復失敗",
                    $"message: {cache.Id}",
                    ex);
            }

            var recruitPost = await _db.GetRecruitPostByMessageIdAsync(cache.Id);
            if (recruitPost is not null)
            {
                try
                {
                    await HandleRecruitPostDeletedAsync(guildChannel.Guild, guildChannel, recruitPost);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup recruit post after message deletion. MessageId={MessageId}", cache.Id);
                }
            }

            var message = deletedMessage;
            var fromDb = await _db.GetMessageAsync(cache.Id);

            if (fromDb is null && message?.Author?.IsBot == true)
            {
                return;
            }

            var authorId = fromDb is null ? message?.Author.Id : ToUlong(fromDb.AuthorId);
            var authorName = fromDb is null ? message?.Author.ToString() : fromDb.AuthorName;
            var authorUser = authorId.HasValue ? guildChannel.Guild.GetUser(authorId.Value) : null;

            var author = authorId.HasValue
                ? $"{MentionUser(authorId.Value)} ({authorName ?? "不明"})"
                : "不明";

            var content = fromDb is null
                ? (string.IsNullOrWhiteSpace(message?.Content) ? "(内容取得不可)" : message.Content)
                : (string.IsNullOrWhiteSpace(fromDb.Content) ? "(内容なし)" : fromDb.Content);

            var attachments = fromDb?.AttachmentCount ?? message?.Attachments.Count ?? 0;
            var embeds = fromDb?.EmbedCount ?? message?.Embeds.Count ?? 0;
            var category = ResolveCategoryName(guildChannel);
            var messageLink = BuildMessageLink(guildChannel.Guild.Id, guildChannel.Id, cache.Id);
            var deletedBy = await TryResolveDeleterAsync(guildChannel.Guild, authorId, guildChannel.Id);
            var createdAt = fromDb?.CreatedAt ?? message?.Timestamp;
            var editedAt = fromDb?.EditedAt;
            var summary = BuildMessageSummary(
                guildChannel,
                author,
                category,
                createdAt,
                editedAt,
                attachments,
                embeds,
                messageLink,
                deletedBy);

            if (!IsLogIgnoredChannel(guildChannel))
            {
                var attachmentUrls = ListAttachmentUrls(message, fromDb);
                var previewImageUrl = ResolvePreviewImageUrl(message, fromDb);
                var embedBuilder = CreateLogEmbedBase(guildChannel.Guild, "メッセージ削除", Color.Orange, authorUser)
                    .WithDescription(summary)
                    .AddField("メッセージID", cache.Id, true);

                if (_options.LogIncludeMessageContent)
                {
                    embedBuilder.AddField("内容", Truncate(content, 900));
                }

                if (attachmentUrls.Count > 0)
                {
                    var links = string.Join('\n', attachmentUrls.Select(url => $"・{url}"));
                    embedBuilder.AddField("添付ファイル", Truncate(links, 1000), false);
                }
                else if (attachments > 0)
                {
                    embedBuilder.AddField("添付ファイル", "(キャッシュ未取得のためURL不明)", false);
                }

                if (!string.IsNullOrWhiteSpace(previewImageUrl))
                {
                    embedBuilder.WithImageUrl(previewImageUrl);
                }

                var embed = embedBuilder.Build();
                await SendLogEmbedAsync(guildChannel.Guild, embed);
            }

            await _db.MarkMessageDeletedAsync(cache.Id);
        }
    }

    private async Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> beforeCache, SocketMessage after, ISocketMessageChannel channel)
    {
        if (channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        if (after.Author.IsBot)
        {
            return;
        }

        var stored = await _db.GetMessageAsync(after.Id);
        var before = beforeCache.HasValue ? beforeCache.Value.Content : stored?.Content ?? "(内容取得不可)";
        var afterContent = string.IsNullOrWhiteSpace(after.Content) ? "(内容取得不可)" : after.Content;
        var contentChanged = !string.Equals(before, afterContent, StringComparison.Ordinal);
        var attachments = after.Attachments.Count;
        var embeds = after.Embeds.Count;
        var attachmentOrEmbedChanged = stored is null
            || stored.AttachmentCount != attachments
            || stored.EmbedCount != embeds;

        if (!contentChanged && !attachmentOrEmbedChanged)
        {
            return;
        }

        var author = $"{after.Author} ({after.Author.Id})";
        var category = ResolveCategoryName(guildChannel);
        var messageLink = BuildMessageLink(guildChannel.Guild.Id, guildChannel.Id, after.Id);
        var createdAt = after.Timestamp;
        var editedAt = after.EditedTimestamp;
        var summary = BuildMessageSummary(
            guildChannel,
            author,
            category,
            createdAt,
            editedAt,
            attachments,
            embeds,
            messageLink);

        if (!IsLogIgnoredChannel(guildChannel))
        {
            var embedBuilder = CreateLogEmbedBase(guildChannel.Guild, "メッセージ編集", Color.Gold, after.Author)
                .WithDescription(summary)
                .AddField("メッセージID", after.Id, true);

            if (_options.LogIncludeMessageContent && contentChanged)
            {
                embedBuilder.AddField("編集前", Truncate(before, 900))
                            .AddField("編集後", Truncate(afterContent, 900));
            }

            var embed = embedBuilder.Build();
            await SendLogEmbedAsync(guildChannel.Guild, embed);
        }

        var archivedAttachmentPaths = await ArchiveMessageAttachmentsAsync(guildChannel.Guild.Id, guildChannel.Id, after.Id, after.Attachments);
        var attachmentsJson = BuildAttachmentsJson(after.Attachments, archivedAttachmentPaths);
        var embedsJson = BuildEmbedsJson(after.Embeds);
        await _db.UpdateMessageAsync(after.Id, afterContent, attachments, embeds, attachmentsJson, embedsJson);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        await TryRefreshRecruitListRealtimeAsync(guildChannel, message.Author.Id);

        if (message.Author.IsBot)
        {
            return;
        }

        if (message is not SocketUserMessage userMessage)
        {
            return;
        }

        var guildUser = message.Author as SocketGuildUser ?? guildChannel.Guild.GetUser(message.Author.Id);
        var authorDisplayName = guildUser?.DisplayName ?? guildUser?.GlobalName;
        var content = userMessage.Content ?? string.Empty;
        var archivedAttachmentPaths = await ArchiveMessageAttachmentsAsync(guildChannel.Guild.Id, guildChannel.Id, userMessage.Id, userMessage.Attachments);
        var attachmentsJson = BuildAttachmentsJson(userMessage.Attachments, archivedAttachmentPaths);
        var embedsJson = BuildEmbedsJson(userMessage.Embeds);
        await _db.UpsertMessageAsync(
            guildChannel.Guild.Id,
            guildChannel.Id,
            userMessage.Id,
            userMessage.Author.Id,
            userMessage.Author.ToString(),
            authorDisplayName,
            content,
            userMessage.Attachments.Count,
            userMessage.Embeds.Count,
            attachmentsJson,
            embedsJson,
            userMessage.Timestamp);
        await _db.TouchInquiryTicketActivityAsync(guildChannel.Id, userMessage.Timestamp);
        await MarkUserActiveNowAsync(guildChannel.Guild, userMessage.Author.Id);
    }

    private async Task TryRefreshRecruitListRealtimeAsync(SocketGuildChannel guildChannel, ulong authorUserId)
    {
        if (!_options.RecruitFeatureEnabled)
        {
            return;
        }

        if (authorUserId == _client.CurrentUser.Id)
        {
            return;
        }

        if (!IsRecruitListRealtimeTargetChannel(guildChannel))
        {
            return;
        }

        if (guildChannel is not IMessageChannel messageChannel)
        {
            return;
        }

        try
        {
            await _recruitListService.RefreshAsync(guildChannel.Guild, messageChannel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recruit list in realtime. GuildId={GuildId}, ChannelId={ChannelId}", guildChannel.Guild.Id, guildChannel.Id);
        }
    }

    private bool IsRecruitListRealtimeTargetChannel(SocketGuildChannel guildChannel)
    {
        var input = _options.RecruitPostChannelName;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("<#", StringComparison.Ordinal) &&
            trimmed.EndsWith(">", StringComparison.Ordinal) &&
            ulong.TryParse(trimmed[2..^1], out var mentionId))
        {
            return guildChannel.Id == mentionId;
        }

        var normalized = trimmed.TrimStart('#');
        return string.Equals(guildChannel.Name, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private async Task OnReactionAddedAsync(Cacheable<IUserMessage, ulong> _, Cacheable<IMessageChannel, ulong> channelCache, SocketReaction reaction)
    {
        if (reaction.UserId == _client.CurrentUser.Id)
        {
            return;
        }

        if (reaction.User.IsSpecified && reaction.User.Value.IsBot)
        {
            return;
        }

        IMessageChannel? channel = null;
        if (channelCache.HasValue)
        {
            channel = channelCache.Value;
        }
        else
        {
            try
            {
                channel = await channelCache.GetOrDownloadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve reaction channel. ChannelId={ChannelId}", channelCache.Id);
                return;
            }
        }

        if (channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        try
        {
            var emojiKey = BuildEmojiKey(reaction.Emote);
            await _db.UpsertReactionAsync(
                guildChannel.Guild.Id,
                guildChannel.Id,
                reaction.MessageId,
                reaction.UserId,
                emojiKey,
                DateTimeOffset.UtcNow);
            await MarkUserActiveNowAsync(guildChannel.Guild, reaction.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert reaction activity. GuildId={GuildId}, UserId={UserId}", guildChannel.Guild.Id, reaction.UserId);
        }
    }

    private async Task OnReactionRemovedAsync(Cacheable<IUserMessage, ulong> _, Cacheable<IMessageChannel, ulong> channelCache, SocketReaction reaction)
    {
        if (reaction.UserId == _client.CurrentUser.Id)
        {
            return;
        }

        if (reaction.User.IsSpecified && reaction.User.Value.IsBot)
        {
            return;
        }

        IMessageChannel? channel = null;
        if (channelCache.HasValue)
        {
            channel = channelCache.Value;
        }
        else
        {
            try
            {
                channel = await channelCache.GetOrDownloadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve reaction channel on remove. ChannelId={ChannelId}", channelCache.Id);
                return;
            }
        }

        if (channel is not SocketGuildChannel guildChannel)
        {
            return;
        }

        try
        {
            var emojiKey = BuildEmojiKey(reaction.Emote);
            await _db.RemoveReactionAsync(
                guildChannel.Guild.Id,
                reaction.MessageId,
                reaction.UserId,
                emojiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove reaction activity. GuildId={GuildId}, UserId={UserId}", guildChannel.Guild.Id, reaction.UserId);
        }
    }

    private EmbedBuilder CreateLogEmbedBase(SocketGuild guild, string title, Color color, SocketUser? user = null, bool preferGuildDisplayName = false)
    {
        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp();

        if (user is not null)
        {
            var avatar = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
            string authorName;
            if (preferGuildDisplayName)
            {
                var guildUser = guild.GetUser(user.Id);
                authorName = guildUser?.DisplayName ?? guildUser?.GlobalName ?? user.GlobalName ?? user.Username;
            }
            else
            {
                authorName = user.GlobalName ?? user.Username;
            }

            builder.WithAuthor(authorName, avatar)
                   .WithThumbnailUrl(avatar);
        }

        return builder;
    }

    private async Task SendLogEmbedAsync(SocketGuild guild, Embed embed)
    {
        var channel = await ResolveLogChannelAsync(guild);
        if (channel is null)
        {
            return;
        }

        await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
    }

    private async Task SendNotificationEmbedAsync(SocketGuild guild, Embed embed, bool mentionVoiceRole = false)
    {
        var channel = await ResolveNotificationChannelAsync(guild);
        if (channel is null)
        {
            return;
        }

        if (!mentionVoiceRole || !_options.MentionVoiceNotificationRole)
        {
            await channel.SendMessageAsync(embed: embed);
            return;
        }

        var role = ResolveVoiceNotificationRole(guild);
        if (role is null)
        {
            await channel.SendMessageAsync(embed: embed);
            return;
        }

        await channel.SendMessageAsync(text: MentionRole(role.Id), embed: embed);
    }

    private async Task NotifyFailureAsync(
        SocketGuild guild,
        string key,
        string title,
        string detail,
        Exception? exception = null)
    {
        var now = DateTimeOffset.UtcNow;
        var throttleKey = $"{guild.Id}:{key}";
        if (_failureAlerts.TryGetValue(throttleKey, out var last) &&
            now - last < FailureAlertCooldown)
        {
            return;
        }

        _failureAlerts[throttleKey] = now;

        var description = detail;
        if (exception is not null)
        {
            var exceptionText = $"{exception.GetType().Name}: {exception.Message}";
            description = string.IsNullOrWhiteSpace(detail)
                ? Truncate(exceptionText, 900)
                : $"{detail}\n{Truncate(exceptionText, 900)}";
        }

        try
        {
            await _audit.SendSystemAsync(guild, title, description, Color.Red);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send failure alert. GuildId={GuildId}, Key={Key}", guild.Id, key);
        }
    }

    private async Task<bool> ShouldNotifyUserVoiceActivityAsync(ulong userId)
    {
        try
        {
            return await _db.GetVoiceNotificationEnabledAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user voice notification setting. UserId={UserId}", userId);
            return true;
        }
    }

    private async Task<IMessageChannel?> ResolveLogChannelAsync(SocketGuild guild)
    {
        var settings = await _db.GetGuildSettingsAsync(guild.Id);
        if (settings is not null && settings.LogChannelId != 0)
        {
            var channel = guild.GetTextChannel(ToUlong(settings.LogChannelId));
            if (channel is not null)
            {
                return channel;
            }
        }

        var logChannelName = string.IsNullOrWhiteSpace(_options.LogChannelName) ? "log" : _options.LogChannelName;
        return guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, logChannelName, StringComparison.OrdinalIgnoreCase));
    }

    private Task<IMessageChannel?> ResolveNotificationChannelAsync(SocketGuild guild)
    {
        var notificationChannelName = string.IsNullOrWhiteSpace(_options.NotificationChannelName) ? "notice" : _options.NotificationChannelName;
        IMessageChannel? channel = guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, notificationChannelName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(channel);
    }

    private Task<IMessageChannel?> ResolveActivityRankingChannelAsync(SocketGuild guild)
    {
        var rankingChannelName = string.IsNullOrWhiteSpace(_options.ActivityRankingChannelName) ? "ランキング" : _options.ActivityRankingChannelName;
        IMessageChannel? channel = guild.TextChannels.FirstOrDefault(c => string.Equals(c.Name, rankingChannelName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(channel);
    }

    private async Task<IUserMessage> UpsertDailyActivityRankingMessageAsync(
        SocketGuild guild,
        IMessageChannel channel,
        ulong existingMessageId,
        DateTimeOffset nowJst,
        Embed embed)
    {
        IUserMessage? existing = null;
        if (existingMessageId != 0)
        {
            try
            {
                existing = await channel.GetMessageAsync(existingMessageId) as IUserMessage;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch daily activity ranking message by id. GuildId={GuildId}, MessageId={MessageId}", guild.Id, existingMessageId);
            }
        }

        if (existing is not null)
        {
            var createdAtJst = existing.Timestamp.ToOffset(JapanTimeOffset).Date;
            if (createdAtJst == nowJst.Date)
            {
                await existing.ModifyAsync(props =>
                {
                    props.Content = null;
                    props.Embed = embed;
                });
                return existing;
            }
        }

        return await channel.SendMessageAsync(
            embed: embed,
            flags: MessageFlags.SuppressNotification);
    }

    private IRole? ResolveVoiceNotificationRole(SocketGuild guild)
    {
        var roleName = string.IsNullOrWhiteSpace(_options.VoiceNotificationRoleName) ? "通話通知" : _options.VoiceNotificationRoleName;
        return guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "…";
    }

    private static string ChannelMention(IGuildChannel channel) => $"<#{channel.Id}>";
    private static string MentionUser(ulong userId) => $"<@{userId}>";
    private static string MentionRole(ulong roleId) => $"<@&{roleId}>";
    private async Task TrySendLotteryStatusTransitionAuditAsync(
        Lottery lottery,
        string beforeStatus,
        string afterStatus,
        string trigger,
        string? note = null)
    {
        var guild = _client.GetGuild(ToUlong(lottery.GuildId));
        if (guild is null)
        {
            return;
        }

        var channelId = ToUlong(lottery.ChannelId);
        var messageId = lottery.MessageId.HasValue ? ToUlong(lottery.MessageId.Value) : 0;
        var lines = new List<string>
        {
            $"🎯 抽選: #{lottery.Id} {lottery.Title}",
            $"🔄 状態: {FormatLotteryStatusLabel(beforeStatus)} -> {FormatLotteryStatusLabel(afterStatus)}",
            $"🧭 トリガー: {trigger}",
            $"💬 チャンネル: <#{channelId}>"
        };
        if (messageId != 0)
        {
            lines.Add($"🔗 メッセージ: {BuildMessageLink(guild.Id, channelId, messageId)}");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add($"📝 備考: {note}");
        }

        await _audit.SendSystemAsync(guild, "抽選状態遷移", string.Join('\n', lines), Color.Orange);
    }

    private static string BuildEmojiKey(IEmote emote)
    {
        return emote switch
        {
            Emote custom => $"custom:{custom.Id}",
            Emoji unicode => $"unicode:{(string.IsNullOrWhiteSpace(unicode.Name) ? "unknown" : unicode.Name)}",
            _ => emote.ToString() ?? "unknown"
        };
    }

    private static string FormatTimestamp(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:F>";
    private static string BuildMessageLink(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";
    private static string FormatLotteryStatusLabel(string? status)
    {
        var normalized = LotteryStatus.Normalize(status);
        return normalized switch
        {
            LotteryStatus.Open => "OPEN(受付中)",
            LotteryStatus.Closed => "CLOSED(締切)",
            LotteryStatus.Drawn => "DRAWN(抽選済み)",
            LotteryStatus.Canceled => "CANCELED(中止)",
            _ => normalized
        };
    }

    private static string FormatRecruitStatusLabel(RecruitPostStatus status)
    {
        return status switch
        {
            RecruitPostStatus.Open => "OPEN(募集中)",
            RecruitPostStatus.Full => "FULL(満員)",
            RecruitPostStatus.Closed => "CLOSED(終了)",
            RecruitPostStatus.Canceled => "CANCELED(中断)",
            _ => status.ToString()
        };
    }

    private static string BuildSlashCommandPath(SocketSlashCommand slash)
    {
        var parts = new List<string> { slash.CommandName };
        AppendSlashCommandPathParts(slash.Data.Options, parts);
        return "/" + string.Join(' ', parts);
    }

    private static string BuildInteractionRoute(SocketInteraction interaction)
    {
        return interaction switch
        {
            SocketSlashCommand slash => BuildSlashCommandPath(slash),
            SocketMessageComponent component => $"component:{component.Data.CustomId}",
            SocketModal modal => $"modal:{modal.Data.CustomId}",
            _ => interaction.Type.ToString()
        };
    }

    private static ulong? TryGetInteractionMessageId(SocketInteraction interaction)
    {
        return interaction switch
        {
            SocketMessageComponent component when component.Message is not null => component.Message.Id,
            _ => null
        };
    }

    private static void AppendSlashCommandPathParts(IReadOnlyCollection<SocketSlashCommandDataOption> options, List<string> parts)
    {
        if (options.Count == 0)
        {
            return;
        }

        var next = options.FirstOrDefault(option =>
            option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup);

        if (next is null)
        {
            return;
        }

        parts.Add(next.Name);
        AppendSlashCommandPathParts(next.Options, parts);
    }

    private static bool IsLikelyRecent(DateTimeOffset timestamp)
        => DateTimeOffset.UtcNow - timestamp < TimeSpan.FromSeconds(20);

    private bool IsLogIgnoredChannel(SocketGuildChannel channel)
    {
        var names = _options.LogIgnoreChannelNames;
        if (names is null || names.Length == 0)
        {
            return false;
        }

        return names.Any(name => string.Equals(name, channel.Name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> TryResolveDeleterAsync(SocketGuild guild, ulong? authorId, ulong channelId)
    {
        try
        {
            var logs = await guild.GetAuditLogsAsync(10, actionType: ActionType.MessageDeleted).FlattenAsync();
            foreach (var entry in logs)
            {
                if (entry is Discord.Rest.RestAuditLogEntry restEntry)
                {
                    if (!IsLikelyRecent(restEntry.CreatedAt))
                    {
                        continue;
                    }
                }

                if (entry.Data is SocketMessageDeleteAuditLogData socketData)
                {
                    if (socketData.ChannelId != channelId)
                    {
                        continue;
                    }

                    if (authorId.HasValue && socketData.Target.Id != authorId.Value)
                    {
                        continue;
                    }

                    return FormatAuditUser(entry);
                }

                if (entry.Data is Discord.Rest.MessageDeleteAuditLogData restData)
                {
                    if (restData.ChannelId != channelId)
                    {
                        continue;
                    }

                    if (authorId.HasValue && restData.Target?.Id != authorId.Value)
                    {
                        continue;
                    }

                    return FormatAuditUser(entry);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve deleter from audit logs.");
        }

        return null;
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);
    private static bool IsUnknownMessage(Discord.Net.HttpException ex)
        => ex.DiscordCode == DiscordErrorCode.UnknownMessage;

    private async Task TryPostLotteryResultEmbedAsync(
        SocketTextChannel channel,
        Lottery lottery,
        IReadOnlyList<LotteryEntry> entries,
        IReadOnlyList<LotteryWinner> winners,
        DateTimeOffset nowUtc)
    {
        try
        {
            var embed = LotteryPanel.BuildLotteryResultEmbed(lottery, entries, winners, nowUtc);
            var winnerAnnouncement = LotteryPanel.BuildLotteryWinnerAnnouncementText(lottery, winners);
            var mentionUserIds = winners
                .Select(winner => ToUlong(winner.UserId))
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            var allowedMentions = mentionUserIds.Count == 0
                ? AllowedMentions.None
                : new AllowedMentions { UserIds = mentionUserIds };
            var resultComponents = LotteryPanel.BuildLotteryResultComponents(lottery, winners.Count);
            await channel.SendMessageAsync(
                text: winnerAnnouncement,
                embed: embed,
                components: resultComponents,
                allowedMentions: allowedMentions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send lottery result summary. LotteryId={LotteryId}", lottery.Id);
        }
    }

    private async Task ScheduleLotteryParticipantRoleCleanupAsync(Lottery lottery, string message)
    {
        if (!lottery.ParticipantRoleId.HasValue || lottery.ParticipantRoleId.Value == 0)
        {
            return;
        }

        if (lottery.ParticipantRoleDeleteAt.HasValue)
        {
            return;
        }

        var guild = _client.GetGuild(ToUlong(lottery.GuildId));
        if (guild is null)
        {
            return;
        }

        var role = guild.GetRole(ToUlong(lottery.ParticipantRoleId.Value));
        if (role is null)
        {
            await _db.UpdateLotteryParticipantRoleIdAsync(lottery.Id, null);
            return;
        }

        var channel = guild.GetTextChannel(ToUlong(lottery.ChannelId));
        if (channel is not null)
        {
            try
            {
                await channel.SendMessageAsync($"{role.Mention} {message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify lottery participant role. LotteryId={LotteryId}", lottery.Id);
            }
        }

        await _db.UpdateLotteryParticipantRoleDeleteAtAsync(
            lottery.Id,
            DateTimeOffset.UtcNow.Add(LotteryParticipantRoleRetention));
    }
    private bool IsBotAdmin(SocketGuildUser guildUser)
    {
        if (guildUser.GuildPermissions.Administrator)
        {
            return true;
        }

        var roleName = string.IsNullOrWhiteSpace(_options.AdminRoleName) ? "RaidDC Admin" : _options.AdminRoleName;
        return guildUser.Roles.Any(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private static IRole? ResolveRole(SocketGuild guild, long roleId, string? roleName)
    {
        if (roleId != 0)
        {
            var byId = guild.GetRole(ToUlong(roleId));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(roleName))
        {
            return null;
        }

        return guild.Roles.FirstOrDefault(role =>
            string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCategoryName(SocketGuildChannel channel)
    {
        if (channel is INestedChannel nested && nested.CategoryId.HasValue)
        {
            var category = channel.Guild.GetCategoryChannel(nested.CategoryId.Value);
            if (category is not null)
            {
                return category.Name;
            }
        }

        return "(カテゴリなし)";
    }

    private static string FormatAuditUser(IAuditLogEntry entry)
    {
        if (entry.User is null)
        {
            return "(不明)";
        }

        var baseText = $"{MentionUser(entry.User.Id)} ({entry.User})";
        if (!string.IsNullOrWhiteSpace(entry.Reason))
        {
            return $"{baseText} - {entry.Reason}";
        }

        return baseText;
    }

    private static string FormatUser(SocketGuildUser? user)
    {
        if (user is null)
        {
            return "未設定";
        }

        var display = user.DisplayName ?? user.GlobalName ?? user.Username;
        return $"{MentionUser(user.Id)} ({display})";
    }

    private static string BuildMessageSummary(
        SocketGuildChannel channel,
        string author,
        string category,
        DateTimeOffset? createdAt,
        DateTimeOffset? editedAt,
        int attachments,
        int embeds,
        string messageLink,
        string? deletedBy = null)
    {
        var lines = new List<string>
        {
            $"🧭 チャンネル: {ChannelMention(channel)}",
            $"👤 投稿者: {author}",
            $"🗂️ カテゴリ: {category}",
            $"🕒 作成: {(createdAt.HasValue ? FormatTimestamp(createdAt.Value) : "(不明)")}",
            $"✏️ 編集: {(editedAt.HasValue ? FormatTimestamp(editedAt.Value) : "(なし)")}",
            $"📎 添付/埋め込み: {attachments} / {embeds}",
            $"🔗 リンク: {messageLink}"
        };

        if (!string.IsNullOrWhiteSpace(deletedBy))
        {
            lines.Add($"🧹 削除実行者: {deletedBy}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildMemberSummary(SocketUser user, int memberCount)
    {
        var lines = new List<string>
        {
            $"👤 ユーザー: {MentionUser(user.Id)} ({user})",
            $"🆔 ユーザーID: {user.Id}",
            $"🕒 アカウント作成: {FormatTimestamp(user.CreatedAt)}",
            $"👥 メンバー数: {memberCount}"
        };

        return string.Join('\n', lines);
    }

    private static string BuildVoiceSummary(SocketGuild guild, SocketUser user, SocketVoiceChannel? beforeChannel, SocketVoiceChannel? afterChannel, TimeSpan? duration)
    {
        var lines = new List<string>
        {
            $"👤 ユーザー: {FormatVoiceUser(guild, user)}",
            $"🆔 ユーザーID: {user.Id}"
        };

        if (beforeChannel is null && afterChannel is not null)
        {
            lines.Add($"🎧 動作: 入室");
            lines.Add($"📢 チャンネル: {ChannelMention(afterChannel)}");
        }
        else if (beforeChannel is not null && afterChannel is null)
        {
            lines.Add($"🎧 動作: 退出");
            lines.Add($"📢 チャンネル: {ChannelMention(beforeChannel)}");
        }
        else
        {
            lines.Add($"🎧 動作: 移動");
            if (beforeChannel is not null)
            {
                lines.Add($"⬅️ 移動元: {ChannelMention(beforeChannel)}");
            }

            if (afterChannel is not null)
            {
                lines.Add($"➡️ 移動先: {ChannelMention(afterChannel)}");
            }
        }

        if (duration.HasValue)
        {
            lines.Add($"⏱️ 滞在時間: {FormatDuration(duration.Value)}");
        }

        return string.Join('\n', lines);
    }

    private static string BuildVoiceFirstJoinSummary(SocketGuild guild, SocketUser user, SocketVoiceChannel channel, DateTimeOffset joinedAt)
    {
        var lines = new List<string>
        {
            $"👤 入室者: {FormatVoiceUser(guild, user)}",
            $"🎙️ チャンネル: {ChannelMention(channel)}",
            $"🕒 開始: {FormatTimestamp(joinedAt)}"
        };

        return string.Join('\n', lines);
    }

    private static string BuildVoiceCallEndedSummary(SocketGuild guild, SocketUser user, SocketVoiceChannel channel, DateTimeOffset endedAt, TimeSpan? duration)
    {
        var lines = new List<string>
        {
            $"👤 最終退出: {FormatVoiceUser(guild, user)}",
            $"🎙️ チャンネル: {ChannelMention(channel)}",
            $"🕒 終了: {FormatTimestamp(endedAt)}"
        };

        if (duration.HasValue)
        {
            lines.Add($"⏱️ 通話時間: {FormatDuration(duration.Value)}");
        }

        return string.Join('\n', lines);
    }

    private static HttpClient CreateAttachmentArchiveHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RaidDcBot/1.0");
        return client;
    }

    private static string? ResolveAttachmentArchiveRootPath(DiscordOptions options)
    {
        if (!options.AttachmentArchiveEnabled || string.IsNullOrWhiteSpace(options.AttachmentArchiveRootPath))
        {
            return null;
        }

        return Path.GetFullPath(options.AttachmentArchiveRootPath, Environment.CurrentDirectory);
    }

    private void EnsureAttachmentArchiveDirectory()
    {
        if (string.IsNullOrWhiteSpace(_attachmentArchiveRootPath))
        {
            return;
        }

        Directory.CreateDirectory(_attachmentArchiveRootPath);
        _logger.LogInformation("Attachment archive enabled. RootPath={RootPath}", _attachmentArchiveRootPath);
    }

    private async Task<IReadOnlyDictionary<ulong, string>> ArchiveMessageAttachmentsAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        IReadOnlyCollection<IAttachment> attachments)
    {
        if (attachments.Count == 0 || string.IsNullOrWhiteSpace(_attachmentArchiveRootPath))
        {
            return new Dictionary<ulong, string>();
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var messageDir = Path.Combine(
            _attachmentArchiveRootPath,
            guildId.ToString(CultureInfo.InvariantCulture),
            nowUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            nowUtc.ToString("MM", CultureInfo.InvariantCulture),
            nowUtc.ToString("dd", CultureInfo.InvariantCulture),
            channelId.ToString(CultureInfo.InvariantCulture),
            messageId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(messageDir);

        var archivedPaths = new Dictionary<ulong, string>();
        foreach (var attachment in attachments)
        {
            var fileName = BuildArchivedAttachmentFileName(attachment.Id, attachment.Filename);
            var absolutePath = Path.Combine(messageDir, fileName);
            var relativePath = ToRelativeArchivePath(absolutePath);
            if (File.Exists(absolutePath))
            {
                archivedPaths[attachment.Id] = relativePath;
                continue;
            }

            var sourceUrl = !string.IsNullOrWhiteSpace(attachment.Url) ? attachment.Url : attachment.ProxyUrl;
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                continue;
            }

            try
            {
                using var response = await AttachmentArchiveHttpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Attachment download failed. StatusCode={StatusCode}, GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}, AttachmentId={AttachmentId}",
                        (int)response.StatusCode,
                        guildId,
                        channelId,
                        messageId,
                        attachment.Id);
                    continue;
                }

                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = new FileStream(
                    absolutePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    useAsync: true);
                await input.CopyToAsync(output);
                archivedPaths[attachment.Id] = relativePath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to archive attachment. GuildId={GuildId}, ChannelId={ChannelId}, MessageId={MessageId}, AttachmentId={AttachmentId}",
                    guildId,
                    channelId,
                    messageId,
                    attachment.Id);
                TryDeleteFile(absolutePath);
            }
        }

        return archivedPaths;
    }

    private int CleanupArchivedAttachmentFiles(int retentionDays)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(_attachmentArchiveRootPath) || !Directory.Exists(_attachmentArchiveRootPath))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var filePath in Directory.EnumerateFiles(_attachmentArchiveRootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) >= threshold)
                {
                    continue;
                }

                File.Delete(filePath);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete archived attachment file. FilePath={FilePath}", filePath);
            }
        }

        TryDeleteEmptyDirectories(_attachmentArchiveRootPath);
        return deleted;
    }

    private static void TryDeleteEmptyDirectories(string rootDirectory)
    {
        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private static string BuildArchivedAttachmentFileName(ulong attachmentId, string? fileName)
    {
        var safeName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "attachment.bin";
        }

        return $"{attachmentId}_{safeName}";
    }

    private static string SanitizeFileName(string? fileName)
    {
        var source = string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = source
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        return new string(chars);
    }

    private string ToRelativeArchivePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(_attachmentArchiveRootPath))
        {
            return absolutePath.Replace('\\', '/');
        }

        return Path.GetRelativePath(_attachmentArchiveRootPath, absolutePath).Replace('\\', '/');
    }

    private static string? TryGetArchivedPath(IReadOnlyDictionary<ulong, string>? archivedPaths, ulong attachmentId)
    {
        if (archivedPaths is null)
        {
            return null;
        }

        return archivedPaths.TryGetValue(attachmentId, out var path) ? path : null;
    }

    private static string? BuildAttachmentsJson(
        IReadOnlyCollection<IAttachment> attachments,
        IReadOnlyDictionary<ulong, string>? archivedPaths = null)
    {
        if (attachments.Count == 0)
        {
            return null;
        }

        var snapshots = attachments
            .Select(attachment => new MessageAttachmentSnapshot(
                attachment.Id,
                attachment.Url,
                attachment.ProxyUrl,
                attachment.Filename,
                attachment.Size,
                attachment.ContentType,
                attachment.Height,
                attachment.Width,
                attachment.IsSpoiler(),
                TryGetArchivedPath(archivedPaths, attachment.Id)))
            .ToList();

        return JsonSerializer.Serialize(snapshots);
    }

    private static string? BuildEmbedsJson(IReadOnlyCollection<Embed> embeds)
    {
        if (embeds.Count == 0)
        {
            return null;
        }

        var snapshots = embeds
            .Select(embed => new MessageEmbedSnapshot(
                embed.Type.ToString(),
                embed.Title,
                embed.Description,
                embed.Url,
                embed.Author?.Name,
                embed.Footer?.Text,
                embed.Image.HasValue ? embed.Image.Value.Url : null,
                embed.Thumbnail.HasValue ? embed.Thumbnail.Value.Url : null))
            .ToList();

        return JsonSerializer.Serialize(snapshots);
    }

    private static IReadOnlyList<string> ListAttachmentUrls(IMessage? message, MessageLog? stored)
    {
        var urls = new List<string>();

        if (message is not null && message.Attachments.Count > 0)
        {
            urls.AddRange(message.Attachments
                .Select(attachment => attachment.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))!);
        }

        foreach (var attachment in ParseAttachmentSnapshots(stored?.AttachmentsJson))
        {
            if (!string.IsNullOrWhiteSpace(attachment.Url))
            {
                urls.Add(attachment.Url!);
            }
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static string? ResolvePreviewImageUrl(IMessage? message, MessageLog? stored)
    {
        if (message is not null)
        {
            foreach (var attachment in message.Attachments)
            {
                if (!IsImageAttachment(attachment))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(attachment.Url))
                {
                    return attachment.Url;
                }
            }

            foreach (var embed in message.Embeds)
            {
                if (embed.Image.HasValue && !string.IsNullOrWhiteSpace(embed.Image.Value.Url))
                {
                    return embed.Image.Value.Url;
                }

                if (embed.Thumbnail.HasValue && !string.IsNullOrWhiteSpace(embed.Thumbnail.Value.Url))
                {
                    return embed.Thumbnail.Value.Url;
                }
            }
        }

        foreach (var attachment in ParseAttachmentSnapshots(stored?.AttachmentsJson))
        {
            if (!string.IsNullOrWhiteSpace(attachment.Url) && IsImageLike(attachment.ContentType, attachment.Filename))
            {
                return attachment.Url;
            }
        }

        foreach (var embed in ParseEmbedSnapshots(stored?.EmbedsJson))
        {
            if (!string.IsNullOrWhiteSpace(embed.ImageUrl))
            {
                return embed.ImageUrl;
            }

            if (!string.IsNullOrWhiteSpace(embed.ThumbnailUrl))
            {
                return embed.ThumbnailUrl;
            }
        }

        return null;
    }

    private static IReadOnlyList<MessageAttachmentSnapshot> ParseAttachmentSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<MessageAttachmentSnapshot>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<MessageAttachmentSnapshot>>(json) ?? new List<MessageAttachmentSnapshot>();
        }
        catch
        {
            return Array.Empty<MessageAttachmentSnapshot>();
        }
    }

    private static IReadOnlyList<MessageEmbedSnapshot> ParseEmbedSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<MessageEmbedSnapshot>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<MessageEmbedSnapshot>>(json) ?? new List<MessageEmbedSnapshot>();
        }
        catch
        {
            return Array.Empty<MessageEmbedSnapshot>();
        }
    }

    private static bool IsImageLike(string? contentType, string? filename)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = filename ?? string.Empty;
        return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageAttachment(IAttachment attachment)
    {
        return IsImageLike(attachment.ContentType, attachment.Filename);
    }

    private static bool IsFirstHumanJoinInChannel(SocketVoiceChannel? beforeChannel, SocketVoiceChannel? afterChannel)
    {
        if (afterChannel is null)
        {
            return false;
        }

        if (beforeChannel is not null && beforeChannel.Id == afterChannel.Id)
        {
            return false;
        }

        var humanCount = afterChannel.ConnectedUsers.Count(member => !member.IsBot);
        return humanCount == 1;
    }

    private static SocketVoiceChannel? ResolveEndedVoiceChannel(SocketVoiceChannel? beforeChannel, SocketVoiceChannel? afterChannel)
    {
        if (beforeChannel is null)
        {
            return null;
        }

        if (afterChannel is not null && beforeChannel.Id == afterChannel.Id)
        {
            return null;
        }

        var humanCount = beforeChannel.ConnectedUsers.Count(member => !member.IsBot);
        return humanCount == 0 ? beforeChannel : null;
    }

    private async Task<TimeSpan?> ResolveVoiceDurationAsync(SocketGuild guild, SocketUser user, SocketVoiceChannel? beforeChannel, DateTimeOffset now)
    {
        if (beforeChannel is null)
        {
            return null;
        }

        var session = await _db.GetOpenVoiceSessionAsync(guild.Id, user.Id);
        if (session is null || session.JoinedAt == default)
        {
            return null;
        }

        var duration = now - session.JoinedAt;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    private async Task CloseVoiceSessionAsync(SocketGuild guild, SocketUser user, SocketVoiceChannel channel, DateTimeOffset leftAt)
    {
        var closed = await _db.EndVoiceSessionAsync(guild.Id, channel.Id, user.Id, leftAt);
        if (closed is not null)
        {
            return;
        }

        var open = await _db.GetOpenVoiceSessionAsync(guild.Id, user.Id);
        if (open is null)
        {
            return;
        }

        await _db.EndVoiceSessionAsync(guild.Id, ToUlong(open.ChannelId), user.Id, leftAt);
    }

    private async Task CloseOpenVoiceSessionAsync(SocketGuild guild, SocketUser user, DateTimeOffset leftAt)
    {
        var open = await _db.GetOpenVoiceSessionAsync(guild.Id, user.Id);
        if (open is null)
        {
            return;
        }

        await _db.EndVoiceSessionAsync(guild.Id, ToUlong(open.ChannelId), user.Id, leftAt);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatVoiceUser(SocketGuild guild, SocketUser user)
    {
        var guildUser = guild.GetUser(user.Id);
        var display = guildUser?.DisplayName ?? guildUser?.GlobalName ?? user.GlobalName ?? user.Username;
        return $"{MentionUser(user.Id)} ({display})";
    }

    private async Task SendVoiceJoinEmbedAsync(SocketVoiceChannel channel, SocketUser user, DateTimeOffset joinedAt)
    {
        var textChannel = await ResolveVoiceTextChannelAsync(channel);
        if (textChannel is null)
        {
            return;
        }

        var embed = await _profileEmbeds.BuildProfileEmbedAsync(user, channel.Guild, "VC参加", Color.Blue);
        await textChannel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
    }

    private async Task<IMessageChannel?> ResolveVoiceTextChannelAsync(SocketVoiceChannel channel)
    {
        if (channel is IMessageChannel messageChannel)
        {
            return messageChannel;
        }

        try
        {
            var restChannel = await _client.Rest.GetChannelAsync(channel.Id);
            return restChannel as IMessageChannel;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve voice text channel for {ChannelId}.", channel.Id);
            return null;
        }
    }

    private async Task EnsureTempVoiceLobbyAsync(SocketGuild guild, IRole adminRole, IRole memberRole)
    {
        var lobbyName = _options.TempVoiceLobbyName;
        if (string.IsNullOrWhiteSpace(lobbyName))
        {
            return;
        }

        var lobby = FindTempVoiceLobby(guild);
        if (lobby is null)
        {
            ICategoryChannel? category = null;
            if (!string.IsNullOrWhiteSpace(_options.TempVoiceCategoryName))
            {
                category = guild.CategoryChannels.FirstOrDefault(c =>
                    string.Equals(c.Name, _options.TempVoiceCategoryName, StringComparison.OrdinalIgnoreCase));
                if (category is null)
                {
                    category = await guild.CreateCategoryChannelAsync(_options.TempVoiceCategoryName);
                    await _audit.SendSystemAsync(
                        guild,
                        "VCカテゴリ自動作成",
                        $"📁 カテゴリ: {category.Name} (<#{category.Id}>)",
                        Color.LightGrey);
                }
            }

            var created = await guild.CreateVoiceChannelAsync(lobbyName, props =>
            {
                if (category is not null)
                {
                    props.CategoryId = category.Id;
                }

                if (_options.TempVoiceUserLimit > 0)
                {
                    props.UserLimit = _options.TempVoiceUserLimit;
                }
            });
            await _audit.SendSystemAsync(
                guild,
                "VCロビー自動作成",
                $"🎙️ チャンネル: <#{created.Id}> ({created.Name})",
                Color.LightGrey);

            lobby = await WaitForVoiceChannelAsync(guild, created.Id);
            if (lobby is null)
            {
                await _audit.SendSystemAsync(
                    guild,
                    "VCロビー解決失敗",
                    $"❌ 作成後にチャンネル取得に失敗しました: <#{created.Id}>",
                    Color.Orange);
                return;
            }
        }

        await EnsureVoicePermissionsAsync(lobby, adminRole, memberRole);
    }

    private SocketVoiceChannel? FindTempVoiceLobby(SocketGuild guild)
    {
        var lobbyName = _options.TempVoiceLobbyName;
        if (string.IsNullOrWhiteSpace(lobbyName))
        {
            return null;
        }

        return guild.VoiceChannels.FirstOrDefault(channel =>
            string.Equals(channel.Name, lobbyName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> HandleTempVoiceLobbyJoinAsync(SocketGuild guild, SocketVoiceChannel lobby, SocketUser user)
    {
        if (user is not SocketGuildUser guildUser)
        {
            return false;
        }

        RestVoiceChannel? createdRest = null;
        await _tempVoiceLock.WaitAsync();
        try
        {
            var baseName = BuildTempVoiceChannelName(guildUser);
            var name = await EnsureUniqueVoiceChannelNameAsync(guild, baseName);

            createdRest = await guild.CreateVoiceChannelAsync(name, props =>
            {
                if (lobby.CategoryId.HasValue)
                {
                    props.CategoryId = lobby.CategoryId.Value;
                }

                if (_options.TempVoiceUserLimit > 0)
                {
                    props.UserLimit = _options.TempVoiceUserLimit;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create temp voice channel for {UserId}.", user.Id);
            await _audit.SendAsync(
                guild,
                "一時VC作成失敗",
                $"👤 ユーザー: {MentionUser(user.Id)} ({user})\n🎙️ ロビー: <#{lobby.Id}>\n❌ Error: {ex.Message}",
                Color.Red,
                user);
        }
        finally
        {
            _tempVoiceLock.Release();
        }

        if (createdRest is null)
        {
            return false;
        }

        await guildUser.ModifyAsync(props => props.Channel = Optional.Create<IVoiceChannel>(createdRest));

        await _db.UpsertTempVoiceChannelAsync(guild.Id, createdRest.Id, guildUser.Id);
        _tempVoiceChannelIds.TryAdd(createdRest.Id, 0);
        await _audit.SendAsync(
            guild,
            "一時VC自動作成",
            $"👤 作成者: {guildUser.Mention} ({guildUser})\n🎙️ チャンネル: <#{createdRest.Id}> ({createdRest.Name})",
            Color.LightGrey,
            guildUser);

        try
        {
            await CopyVoicePermissionsAsync(lobby.PermissionOverwrites, createdRest, guild);
            var ownerOverwrite = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow, manageChannel: PermValue.Allow);
            await createdRest.AddPermissionOverwriteAsync(guildUser, ownerOverwrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply permissions for temp voice channel {ChannelId}.", createdRest.Id);
            await _audit.SendAsync(
                guild,
                "一時VC権限設定失敗",
                $"👤 作成者: {guildUser.Mention} ({guildUser})\n🎙️ チャンネル: <#{createdRest.Id}> ({createdRest.Name})\n❌ Error: {ex.Message}",
                Color.Red,
                guildUser);
        }

        return true;
    }

    private async Task<string> EnsureUniqueVoiceChannelNameAsync(SocketGuild guild, string baseName)
    {
        var name = baseName;
        var index = 2;
        while (guild.VoiceChannels.Any(channel => string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName}-{index}";
            index++;
        }

        return await Task.FromResult(name);
    }

    private string BuildTempVoiceChannelName(SocketGuildUser user)
    {
        var displayName = user.DisplayName ?? user.GlobalName ?? user.Username;
        var prefix = string.IsNullOrWhiteSpace(_options.TempVoiceChannelPrefix) ? "VC-" : _options.TempVoiceChannelPrefix;
        var sanitized = displayName.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var name = $"{prefix}{sanitized}".Trim();
        const int maxLength = 90;
        return name.Length > maxLength ? name[..maxLength] : name;
    }

    private bool IsTempVoiceChannel(SocketVoiceChannel channel, SocketVoiceChannel? lobby)
    {
        if (lobby is not null && channel.Id == lobby.Id)
        {
            return false;
        }

        if (_tempVoiceChannelIds.ContainsKey(channel.Id))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_options.TempVoiceChannelPrefix))
        {
            return false;
        }

        return channel.Name.StartsWith(_options.TempVoiceChannelPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteTempVoiceChannelAsync(SocketVoiceChannel channel)
    {
        CancelTempVoiceDeletion(channel.Id);
        _tempVoiceChannelIds.TryRemove(channel.Id, out _);
        try
        {
            await channel.DeleteAsync();
            await _db.RemoveTempVoiceChannelAsync(channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp voice channel {ChannelId}.", channel.Id);
        }
    }

    private async Task HandleTempVoiceOwnerLeftAsync(SocketVoiceChannel channel, SocketUser user)
    {
        var ownerId = await _db.GetTempVoiceOwnerAsync(channel.Id);
        if (!ownerId.HasValue || ownerId.Value != ToDbId(user.Id))
        {
            return;
        }

        if (channel.ConnectedUsers.Count == 0)
        {
            return;
        }

        var nextOwner = channel.ConnectedUsers.FirstOrDefault(u => !u.IsBot);
        if (nextOwner is null)
        {
            return;
        }

        try
        {
            var oldOwner = channel.Guild.GetUser(ToUlong(ownerId.Value));
            if (oldOwner is not null)
            {
                await channel.RemovePermissionOverwriteAsync(oldOwner);
            }

            var ownerOverwrite = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow, manageChannel: PermValue.Allow);
            await channel.AddPermissionOverwriteAsync(nextOwner, ownerOverwrite);
            await _db.UpsertTempVoiceChannelAsync(channel.Guild.Id, channel.Id, nextOwner.Id);

            await _voiceTextService.PostOwnerTransferAsync(channel, oldOwner, nextOwner, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to transfer temp voice owner for {ChannelId}.", channel.Id);
        }
    }

    private async Task ScheduleTempVoiceDeletionAsync(SocketVoiceChannel channel)
    {
        var delaySeconds = _options.TempVoiceAutoDeleteDelaySeconds;
        if (delaySeconds <= 0)
        {
            await DeleteTempVoiceChannelAsync(channel);
            return;
        }

        var cts = new CancellationTokenSource();
        if (!_tempVoiceDeleteTokens.TryAdd(channel.Id, cts))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
                var current = channel.Guild.GetVoiceChannel(channel.Id);
                if (current is null)
                {
                    return;
                }

                if (current.ConnectedUsers.Count == 0)
                {
                    await DeleteTempVoiceChannelAsync(current);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto delete temp voice channel {ChannelId}.", channel.Id);
            }
            finally
            {
                _tempVoiceDeleteTokens.TryRemove(channel.Id, out _);
                cts.Dispose();
            }
        });
    }

    private void CancelTempVoiceDeletion(ulong channelId)
    {
        if (_tempVoiceDeleteTokens.TryRemove(channelId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task EnsureVoicePermissionsAsync(SocketVoiceChannel channel, IRole adminRole, IRole memberRole)
    {
        var everyoneRole = channel.Guild.EveryoneRole;
        var deny = new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny);
        var allow = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow);

        await channel.AddPermissionOverwriteAsync(everyoneRole, deny);
        await channel.AddPermissionOverwriteAsync(memberRole, allow);
        await channel.AddPermissionOverwriteAsync(adminRole, allow);
    }

    private async Task CopyVoicePermissionsAsync(IReadOnlyCollection<Overwrite> overwrites, IGuildChannel target, SocketGuild guild)
    {
        foreach (var overwrite in overwrites)
        {
            if (overwrite.TargetType == PermissionTarget.Role)
            {
                var role = guild.GetRole(overwrite.TargetId);
                if (role is not null)
                {
                    await target.AddPermissionOverwriteAsync(role, overwrite.Permissions);
                }
            }
            else if (overwrite.TargetType == PermissionTarget.User)
            {
                var user = guild.GetUser(overwrite.TargetId);
                if (user is not null)
                {
                    await target.AddPermissionOverwriteAsync(user, overwrite.Permissions);
                }
            }
        }
    }

    private static async Task<SocketVoiceChannel?> WaitForVoiceChannelAsync(SocketGuild guild, ulong channelId)
    {
        for (var i = 0; i < 5; i++)
        {
            var channel = guild.GetVoiceChannel(channelId);
            if (channel is not null)
            {
                return channel;
            }

            await Task.Delay(200);
        }

        return null;
    }

    private sealed record MessageAttachmentSnapshot(
        ulong Id,
        string? Url,
        string? ProxyUrl,
        string? Filename,
        int Size,
        string? ContentType,
        int? Height,
        int? Width,
        bool IsSpoiler,
        string? LocalPath);

    private sealed record MessageEmbedSnapshot(
        string? Type,
        string? Title,
        string? Description,
        string? Url,
        string? AuthorName,
        string? FooterText,
        string? ImageUrl,
        string? ThumbnailUrl);

}
