using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class FfLogsReportWatcherService
{
    private static readonly HashSet<string> JpDataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Elemental", "Gaia", "Mana", "Meteor"
    };
    private static readonly HashSet<string> NaDataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aether", "Primal", "Crystal", "Dynamis"
    };
    private static readonly HashSet<string> EuDataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chaos", "Light", "Shadow"
    };
    private static readonly HashSet<string> OcDataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Materia"
    };

    private readonly Db _db;
    private readonly FfLogsClient _fflogs;
    private readonly GuildAuditLogService _audit;
    private readonly FfLogsOptions _options;
    private readonly ILogger<FfLogsReportWatcherService> _logger;
    private readonly DateTimeOffset _serviceStartedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastConfigWarningAt = new();

    public FfLogsReportWatcherService(
        Db db,
        FfLogsClient fflogs,
        GuildAuditLogService audit,
        IOptions<FfLogsOptions> options,
        ILogger<FfLogsReportWatcherService> logger)
    {
        _db = db;
        _fflogs = fflogs;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled()
        => _options.Enabled;

    public int GetPollIntervalSeconds()
        => Math.Max(30, _options.PollIntervalSeconds <= 0 ? 60 : _options.PollIntervalSeconds);

    public async Task RunTickAsync(IReadOnlyCollection<SocketGuild> guilds, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (!_fflogs.IsConfigured())
        {
            foreach (var guild in guilds)
            {
                await WarnMissingConfigurationAsync(guild);
            }
            return;
        }

        foreach (var guild in guilds)
        {
            try
            {
                await RunGuildTickAsync(guild, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FFLogs polling tick failed for guild {GuildId}.", guild.Id);
            }
        }
    }

    private async Task RunGuildTickAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        var targets = await BuildCharacterTargetsAsync(guild);
        if (targets.Count == 0)
        {
            return;
        }

        var recentLimit = Math.Clamp(_options.RecentReportsLimitPerCharacter <= 0 ? 10 : _options.RecentReportsLimitPerCharacter, 1, 50);
        var backfillCutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(0, _options.BackfillWindowMinutes));
        if (backfillCutoff < _serviceStartedAt)
        {
            backfillCutoff = _serviceStartedAt.AddMinutes(-1);
        }

        var aggregate = new Dictionary<string, ReportAggregate>(StringComparer.OrdinalIgnoreCase);
        var aggregateLock = new object();
        var concurrency = Math.Clamp(_options.MaxConcurrentCharacterQueries <= 0 ? 4 : _options.MaxConcurrentCharacterQueries, 1, 16);
        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var tasks = targets.Values.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var reports = await _fflogs.GetRecentReportsAsync(
                    target.CharacterName,
                    target.ServerSlug,
                    target.ServerRegion,
                    recentLimit,
                    cancellationToken);

                foreach (var report in reports)
                {
                    var reportTime = report.EndTime ?? report.StartTime;
                    if (reportTime.HasValue && reportTime.Value < backfillCutoff)
                    {
                        continue;
                    }

                    lock (aggregateLock)
                    {
                        if (aggregate.TryGetValue(report.Code, out var existing))
                        {
                            existing.WithTarget(target);
                        }
                        else
                        {
                            aggregate[report.Code] = ReportAggregate.Create(report, target);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "FFLogs recentReports fetch failed for {Character}@{World} ({GuildId}).",
                    target.CharacterName,
                    target.World,
                    guild.Id);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        if (aggregate.Count == 0)
        {
            return;
        }

        var ordered = aggregate.Values
            .OrderBy(item => item.Report.EndTime ?? item.Report.StartTime ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Report.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dedupeGranularity = _options.DeduplicationGranularity;

        foreach (var item in ordered)
        {
            if (dedupeGranularity == FfLogsDeduplicationGranularity.Fight)
            {
                var fightLevelHandled = await TryPostFightLevelAsync(guild, item, cancellationToken);
                if (fightLevelHandled)
                {
                    continue;
                }
            }

            var inserted = await _db.TryInsertFfLogsSeenReportAsync(
                guild.Id,
                item.Report.Code,
                DateTimeOffset.UtcNow,
                item.Report.EndTime);
            if (!inserted)
            {
                continue;
            }

            var latestFight = await TryGetLatestFightForReportAsync(item.Report.Code, cancellationToken);
            await PostReportAsync(guild, item, latestFight);
        }
    }

    private async Task<bool> TryPostFightLevelAsync(SocketGuild guild, ReportAggregate item, CancellationToken cancellationToken)
    {
        IReadOnlyList<FfLogsClient.ReportFight> fights;
        try
        {
            fights = await _fflogs.GetReportFightsAsync(item.Report.Code, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FFLogs fight-level fetch failed for report {ReportCode}. Falling back to report-level.", item.Report.Code);
            return false;
        }

        if (fights.Count == 0)
        {
            return false;
        }

        var latestFight = SelectLatestFight(fights);
        if (latestFight is null)
        {
            return false;
        }

        var dedupeKey = BuildFightDedupeKey(item.Report.Code, latestFight.Id);
        var inserted = await _db.TryInsertFfLogsSeenReportAsync(
            guild.Id,
            dedupeKey,
            DateTimeOffset.UtcNow,
            item.Report.EndTime);
        if (inserted)
        {
            await PostReportAsync(guild, item, latestFight);
        }

        return true;
    }

    private async Task<FfLogsClient.ReportFight?> TryGetLatestFightForReportAsync(string reportCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportCode))
        {
            return null;
        }

        try
        {
            var fights = await _fflogs.GetReportFightsAsync(reportCode, cancellationToken);
            return SelectLatestFight(fights);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FFLogs latest-fight fetch failed for report {ReportCode}. Falling back to report URL.", reportCode);
            return null;
        }
    }

    private static FfLogsClient.ReportFight? SelectLatestFight(IReadOnlyList<FfLogsClient.ReportFight> fights)
    {
        if (fights.Count == 0)
        {
            return null;
        }

        return fights
            .OrderByDescending(f => f.EndTimeMs ?? f.StartTimeMs ?? long.MinValue)
            .ThenByDescending(f => f.Id)
            .FirstOrDefault();
    }

    private async Task<Dictionary<string, CharacterTarget>> BuildCharacterTargetsAsync(SocketGuild guild)
    {
        var members = guild.Users
            .Where(user => !user.IsBot)
            .ToArray();
        if (members.Length == 0)
        {
            return new Dictionary<string, CharacterTarget>(StringComparer.OrdinalIgnoreCase);
        }

        var userIds = members.Select(user => user.Id).ToArray();
        var profiles = await _db.ListDiscordUserProfilesAsync(userIds);
        var profileByUserId = profiles.ToDictionary(p => p.DiscordUserId, p => p);
        var mainChars = await _db.ListMainLinkedCharactersForUsersAsync(userIds);
        var mainCharByUserId = mainChars.ToDictionary(row => ToUlong(row.DiscordUserId), row => row);

        var result = new Dictionary<string, CharacterTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var userId = member.Id;
            if (mainCharByUserId.TryGetValue(userId, out var linked) &&
                TryBuildTarget(linked.Name, linked.World, linked.DataCenter, userId, member.DisplayName, out var fromLinked))
            {
                MergeTarget(result, fromLinked);
                continue;
            }

            if (profileByUserId.TryGetValue(ToDbId(userId), out var profile) &&
                TryBuildTarget(profile.FfxivName, profile.FfxivWorld, profile.FfxivDataCenter, userId, member.DisplayName, out var fromProfile))
            {
                MergeTarget(result, fromProfile);
            }
        }

        return result;
    }

    private static void MergeTarget(IDictionary<string, CharacterTarget> map, CharacterTarget incoming)
    {
        if (map.TryGetValue(incoming.Key, out var existing))
        {
            existing.UserIds.UnionWith(incoming.UserIds);
            existing.MemberLabels.UnionWith(incoming.MemberLabels);
            existing.CharacterLabels.UnionWith(incoming.CharacterLabels);
            return;
        }

        map[incoming.Key] = incoming;
    }

    private static bool TryBuildTarget(
        string? characterName,
        string? world,
        string? dataCenter,
        ulong userId,
        string? memberDisplayName,
        out CharacterTarget target)
    {
        target = default!;
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(world))
        {
            return false;
        }

        var trimmedName = characterName.Trim();
        var trimmedWorld = world.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || string.IsNullOrWhiteSpace(trimmedWorld))
        {
            return false;
        }

        var normalizedDc = !string.IsNullOrWhiteSpace(dataCenter)
            ? dataCenter!.Trim()
            : DcResolver.Resolve(null, trimmedWorld);
        if (!TryMapServerRegion(trimmedWorld, normalizedDc, out var region))
        {
            return false;
        }

        var serverSlug = ToServerSlug(trimmedWorld);
        if (string.IsNullOrWhiteSpace(serverSlug))
        {
            return false;
        }

        var key = $"{trimmedName.ToUpperInvariant()}@{trimmedWorld.ToUpperInvariant()}";
        target = new CharacterTarget(
            Key: key,
            CharacterName: trimmedName,
            World: trimmedWorld,
            ServerSlug: serverSlug,
            ServerRegion: region,
            UserIds: new HashSet<ulong> { userId },
            MemberLabels: new HashSet<string>(StringComparer.Ordinal) { string.IsNullOrWhiteSpace(memberDisplayName) ? $"u:{userId}" : memberDisplayName.Trim() },
            CharacterLabels: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{trimmedName}@{trimmedWorld}" });
        return true;
    }

    private async Task PostReportAsync(SocketGuild guild, ReportAggregate item, FfLogsClient.ReportFight? fight)
    {
        var reportUrl = fight is null
            ? $"https://www.fflogs.com/reports/{item.Report.Code}"
            : $"https://www.fflogs.com/reports/{item.Report.Code}#fight={fight.Id}";
        var matchedUsers = item.UserIds
            .OrderBy(id => id)
            .Select(id => $"<@{id}>")
            .ToArray();
        var memberText = matchedUsers.Length == 0 ? "-" : string.Join(' ', matchedUsers.Take(10));
        var extraMemberCount = matchedUsers.Length > 10 ? $" (+{matchedUsers.Length - 10})" : string.Empty;
        var characterText = item.CharacterLabels.Count == 0
            ? "-"
            : string.Join(", ", item.CharacterLabels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(8));
        var extraCharacterCount = item.CharacterLabels.Count > 8 ? $" (+{item.CharacterLabels.Count - 8})" : string.Empty;
        var title = string.IsNullOrWhiteSpace(item.Report.Title) ? "(untitled)" : item.Report.Title!;
        var zoneName = string.IsNullOrWhiteSpace(item.Report.ZoneName) ? "-" : item.Report.ZoneName!;
        var contentName = fight is not null && !string.IsNullOrWhiteSpace(fight.Name)
            ? fight.Name!
            : zoneName;
        var time = ResolveReportDisplayTime(item.Report, fight);
        var timeLine = time.HasValue
            ? $"🕒 時刻: <t:{time.Value.ToUnixTimeSeconds()}:F> (<t:{time.Value.ToUnixTimeSeconds()}:R>)"
            : "🕒 時刻: 不明";
        var fightLine = fight is null
            ? null
            : $"⚔️ fight: #{fight.Id} / {(fight.Kill == true ? "Kill" : fight.Kill == false ? "Wipe" : "Unknown")}";

        var lines = new List<string>
        {
            $"📄 FFLogs: {reportUrl}",
            $"🏷️ タイトル: {title}",
            $"🎯 コンテンツ: {contentName}",
            $"👥 サーバーメンバー: {memberText}{extraMemberCount}",
            $"🧩 検出キャラ: {characterText}{extraCharacterCount}"
        };
        if (!string.IsNullOrWhiteSpace(fightLine))
        {
            lines.Add(fightLine);
            if (!string.Equals(contentName, zoneName, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"🗺️ レポート: {zoneName}");
            }
        }

        lines.Add(timeLine);
        lines.Add("※ 新規レポート検知（ワイプ含む）");
        var description = string.Join('\n', lines);

        await _audit.SendSystemAsync(guild, "FFLogs ログ検知", description, Color.DarkBlue);
    }

    private static string BuildFightDedupeKey(string reportCode, int fightId)
        => $"{reportCode}#fight:{fightId}";

    private static DateTimeOffset? ResolveReportDisplayTime(FfLogsClient.RecentReport report, FfLogsClient.ReportFight? fight)
    {
        var fightTime = ResolveFightTimeMs(report.StartTime, fight?.EndTimeMs) ??
                        ResolveFightTimeMs(report.StartTime, fight?.StartTimeMs);
        return fightTime ?? report.EndTime ?? report.StartTime;
    }

    private static DateTimeOffset? ResolveFightTimeMs(DateTimeOffset? reportStart, long? rawMs)
    {
        if (!rawMs.HasValue || rawMs.Value <= 0)
        {
            return null;
        }

        const long unixMsYear2000 = 946684800000L;
        const long unixMsYear2100 = 4102444800000L;
        var value = rawMs.Value;

        // FFLogs fights は Unix ms の場合と report 開始からの offset(ms) の場合がある。
        if (value >= unixMsYear2000 && value <= unixMsYear2100)
        {
            return TryFromUnixMs(value);
        }

        if (reportStart.HasValue)
        {
            try
            {
                return reportStart.Value.AddMilliseconds(value);
            }
            catch
            {
                // Fall through to unix attempt below.
            }
        }

        return TryFromUnixMs(value);
    }

    private static DateTimeOffset? TryFromUnixMs(long unixMs)
    {
        if (unixMs <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch
        {
            return null;
        }
    }

    private async Task WarnMissingConfigurationAsync(SocketGuild guild)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastConfigWarningAt.TryGetValue(guild.Id, out var last) &&
            now - last < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastConfigWarningAt[guild.Id] = now;
        await _audit.SendSystemAsync(
            guild,
            "FFLogs 監視未設定",
            "FFLogs監視が有効ですが、`FFLogs__ClientId` / `FFLogs__ClientSecret` が未設定です。",
            Color.Orange);
    }

    private static bool TryMapServerRegion(string world, string? dataCenter, out string region)
    {
        region = string.Empty;

        if (!string.IsNullOrWhiteSpace(dataCenter))
        {
            var dc = dataCenter.Trim();
            if (JpDataCenters.Contains(dc))
            {
                region = "JP";
                return true;
            }

            if (NaDataCenters.Contains(dc))
            {
                region = "NA";
                return true;
            }

            if (EuDataCenters.Contains(dc))
            {
                region = "EU";
                return true;
            }

            if (OcDataCenters.Contains(dc))
            {
                region = "OC";
                return true;
            }
        }

        var resolvedJpDc = DcResolver.Resolve(null, world);
        if (!string.IsNullOrWhiteSpace(resolvedJpDc))
        {
            region = "JP";
            return true;
        }

        return false;
    }

    private static string ToServerSlug(string world)
    {
        var chars = world
            .Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-')
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static ulong ToUlong(long value) => unchecked((ulong)value);
    private static long ToDbId(ulong value) => unchecked((long)value);

    private sealed record CharacterTarget(
        string Key,
        string CharacterName,
        string World,
        string ServerSlug,
        string ServerRegion,
        HashSet<ulong> UserIds,
        HashSet<string> MemberLabels,
        HashSet<string> CharacterLabels);

    private sealed record ReportAggregate(
        FfLogsClient.RecentReport Report,
        HashSet<ulong> UserIds,
        HashSet<string> CharacterLabels)
    {
        public static ReportAggregate Create(FfLogsClient.RecentReport report, CharacterTarget target)
            => new(
                report,
                new HashSet<ulong>(target.UserIds),
                new HashSet<string>(target.CharacterLabels, StringComparer.OrdinalIgnoreCase));

        public ReportAggregate WithTarget(CharacterTarget target)
        {
            UserIds.UnionWith(target.UserIds);
            CharacterLabels.UnionWith(target.CharacterLabels);
            return this;
        }
    }
}
