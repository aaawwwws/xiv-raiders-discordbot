using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using RaidDcBot.Data;
using RaidDcBot.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using DiscordColor = Discord.Color;
using ImgColor = SixLabors.ImageSharp.Color;
using ImgPointF = SixLabors.ImageSharp.PointF;
using ImgRectF = SixLabors.ImageSharp.RectangleF;

namespace RaidDcBot.Services;

public sealed class AchievementResumeService
{
    private static readonly TimeSpan ExtrasResyncInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan JapanTimeOffset = TimeSpan.FromHours(9);
    private static readonly HashSet<int> KnownUltimateAchievementIds =
    [
        3617, // 絶もうひとつの未来
        3162, // 絶オメガ検証戦
        3074, // 絶竜詩戦争
        2444, // 絶アレキサンダー討滅戦
        2107, // 絶アルテマウェポン破壊作戦
        1993  // 絶バハムート討滅戦
    ];
    private static readonly int[] KnownUltimateAchievementIdOrder = [3617, 3162, 3074, 2444, 2107, 1993];
    private static readonly IReadOnlyDictionary<int, string> KnownUltimateAchievementTitles = new Dictionary<int, string>
    {
        [3617] = "絶もうひとつの未来",
        [3162] = "絶オメガ検証戦",
        [3074] = "絶竜詩戦争",
        [2444] = "絶アレキサンダー討滅戦",
        [2107] = "絶アルテマウェポン破壊作戦",
        [1993] = "絶バハムート討滅戦"
    };
    private static readonly IReadOnlyDictionary<int, string> KnownUltimateDisplayNames = new Dictionary<int, string>
    {
        [3617] = "絶もうひとつの未来",
        [3162] = "絶オメガ",
        [3074] = "絶竜詩",
        [2444] = "絶アレキ",
        [2107] = "絶アルテマ",
        [1993] = "絶バハ"
    };
    private static readonly IReadOnlyDictionary<int, DateTimeOffset> KnownUltimateImplementedAtJst = new Dictionary<int, DateTimeOffset>
    {
        [3617] = new(2024, 11, 26, 0, 0, 0, JapanTimeOffset), // Patch 7.11
        [3162] = new(2023, 1, 10, 0, 0, 0, JapanTimeOffset),   // Patch 6.31
        [3074] = new(2022, 4, 26, 0, 0, 0, JapanTimeOffset),   // Patch 6.11
        [2444] = new(2019, 11, 12, 0, 0, 0, JapanTimeOffset),  // Patch 5.11
        [2107] = new(2018, 5, 22, 0, 0, 0, JapanTimeOffset),   // Patch 4.31
        [1993] = new(2017, 11, 21, 0, 0, 0, JapanTimeOffset)   // Patch 4.11
    };
    private static readonly int[] KnownSavageAchievementIdOrder = [3868, 3630, 3575, 3350, 3340, 3251, 3156, 3111, 3038, 2722, 2594, 2412, 2121, 2027, 1898];
    private static readonly HashSet<int> KnownSavageAchievementIds = [.. KnownSavageAchievementIdOrder];
    private static readonly IReadOnlyDictionary<int, string> KnownSavageDisplayNames = new Dictionary<int, string>
    {
        [3868] = "アルカディア：ヘビー級",
        [3630] = "アルカディア：クルーザー級",
        [3575] = "アルカディア：ライトヘビー級",
        [3350] = "異聞零式：アロアロ島",
        [3340] = "異聞零式：六根山",
        [3251] = "パンデモ：天獄編",
        [3156] = "異聞零式：シラディハ水道",
        [3111] = "パンデモ：煉獄編",
        [3038] = "パンデモ：辺獄編",
        [2722] = "エデン：再生編",
        [2594] = "エデン：共鳴編",
        [2412] = "エデン：覚醒編",
        [2121] = "オメガ：アルファ編",
        [2027] = "オメガ：シグマ編",
        [1898] = "オメガ：デルタ編"
    };
    private static readonly IReadOnlyDictionary<int, DateTimeOffset> KnownSavageImplementedAtJst = new Dictionary<int, DateTimeOffset>
    {
        [3868] = new(2026, 1, 6, 0, 0, 0, JapanTimeOffset),   // Patch 7.41 (Savage unlock)
        [3630] = new(2025, 4, 1, 0, 0, 0, JapanTimeOffset),   // Patch 7.21 (Savage unlock)
        [3575] = new(2024, 7, 30, 0, 0, 0, JapanTimeOffset),  // Patch 7.05
        [3350] = new(2024, 1, 16, 0, 0, 0, JapanTimeOffset),  // Patch 6.51
        [3340] = new(2023, 7, 18, 0, 0, 0, JapanTimeOffset),  // Patch 6.45
        [3251] = new(2023, 5, 30, 0, 0, 0, JapanTimeOffset),  // Patch 6.41 (Savage unlock)
        [3156] = new(2022, 10, 18, 0, 0, 0, JapanTimeOffset), // Patch 6.25
        [3111] = new(2022, 8, 30, 0, 0, 0, JapanTimeOffset),  // Patch 6.21 (Savage unlock)
        [3038] = new(2022, 1, 4, 0, 0, 0, JapanTimeOffset),   // Patch 6.05
        [2722] = new(2020, 12, 8, 0, 0, 0, JapanTimeOffset),  // Patch 5.4
        [2594] = new(2020, 2, 18, 0, 0, 0, JapanTimeOffset),  // Patch 5.2
        [2412] = new(2019, 7, 30, 0, 0, 0, JapanTimeOffset),  // Patch 5.05
        [2121] = new(2018, 9, 18, 0, 0, 0, JapanTimeOffset),  // Patch 4.4
        [2027] = new(2018, 1, 30, 0, 0, 0, JapanTimeOffset),  // Patch 4.2
        [1898] = new(2017, 7, 18, 0, 0, 0, JapanTimeOffset)   // Patch 4.05
    };
    private static readonly (DateTime ReleasedAtJst, string Patch)[] MajorPatchTimelineJst =
    [
        (new DateTime(2017, 6, 20), "4.0"),
        (new DateTime(2017, 10, 10), "4.1"),
        (new DateTime(2018, 1, 30), "4.2"),
        (new DateTime(2018, 5, 22), "4.3"),
        (new DateTime(2018, 9, 18), "4.4"),
        (new DateTime(2019, 1, 8), "4.5"),
        (new DateTime(2019, 7, 2), "5.0"),
        (new DateTime(2019, 10, 29), "5.1"),
        (new DateTime(2020, 2, 18), "5.2"),
        (new DateTime(2020, 8, 11), "5.3"),
        (new DateTime(2020, 12, 8), "5.4"),
        (new DateTime(2021, 4, 13), "5.5"),
        (new DateTime(2021, 12, 7), "6.0"),
        (new DateTime(2022, 4, 12), "6.1"),
        (new DateTime(2022, 8, 23), "6.2"),
        (new DateTime(2023, 1, 10), "6.3"),
        (new DateTime(2023, 5, 23), "6.4"),
        (new DateTime(2023, 10, 3), "6.5"),
        (new DateTime(2024, 7, 2), "7.0"),
        (new DateTime(2024, 11, 12), "7.1"),
        (new DateTime(2025, 3, 25), "7.2"),
        (new DateTime(2025, 8, 5), "7.3"),
        (new DateTime(2025, 12, 16), "7.4")
    ];
    private static readonly IReadOnlyDictionary<int, string> KnownSavageGroupLabels = new Dictionary<int, string>
    {
        [3868] = "黄金",
        [3630] = "黄金",
        [3575] = "黄金",
        [3350] = "異聞",
        [3340] = "異聞",
        [3251] = "暁月",
        [3156] = "異聞",
        [3111] = "暁月",
        [3038] = "暁月",
        [2722] = "漆黒",
        [2594] = "漆黒",
        [2412] = "漆黒",
        [2121] = "紅蓮",
        [2027] = "紅蓮",
        [1898] = "紅蓮"
    };
    private static readonly string[] SavageGroupOrder = ["黄金", "暁月", "漆黒", "紅蓮", "異聞"];

    private static readonly string[] PreferredResumeFonts =
    [
        "Yu Gothic UI",
        "Meiryo",
        "BIZ UDPGothic",
        "Noto Sans JP",
        "Noto Sans CJK JP",
        "Source Han Sans JP",
        "Hiragino Sans",
        "Segoe UI",
        "Arial Unicode MS",
        "Arial"
    ];

    private static readonly ConcurrentDictionary<string, bool> RenderableFontCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Db _db;
    private readonly LodestoneClient _lodestone;
    private readonly ILogger<AchievementResumeService> _logger;
    private readonly HttpClient _http;

    public AchievementResumeService(
        Db db,
        LodestoneClient lodestone,
        ILogger<AchievementResumeService> logger)
    {
        _db = db;
        _lodestone = lodestone;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<AchievementResumeBuildResult> BuildAsync(
        SocketGuild guild,
        IUser targetUser,
        bool forceRefresh,
        int recentCount = 8,
        IReadOnlyCollection<string>? savageGroupFilter = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildAsync(guild, targetUser, forceRefresh, probeAchievements: forceRefresh, recentCount, savageGroupFilter, cancellationToken);
    }

    public async Task<AchievementResumeBuildResult> BuildAsync(
        SocketGuild guild,
        IUser targetUser,
        bool forceRefresh,
        bool probeAchievements,
        int recentCount = 8,
        IReadOnlyCollection<string>? savageGroupFilter = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building raid resume. UserId={UserId}, GuildId={GuildId}, ForceRefresh={ForceRefresh}", targetUser.Id, guild.Id, forceRefresh);

        if (!await _lodestone.IsAvailableAsync(cancellationToken))
        {
            _logger.LogWarning("Lodestone is not available");
            return AchievementResumeBuildResult.FromFailure("Lodestoneが現在メンテナンス中です。時間をおいて再試行してください。");
        }
        var character = await _db.GetMainCharacterAsync(targetUser.Id);
        if (character is null || string.IsNullOrWhiteSpace(character.LodestoneUrl))
        {
            _logger.LogWarning("No main character linked. UserId={UserId}", targetUser.Id);
            return AchievementResumeBuildResult.FromFailure("対象ユーザーにLodestone連携済みのメインキャラクターがありません。");
        }

        _logger.LogInformation("Found character. UserId={UserId}, Character={CharacterName}, LodestoneId={LodestoneId}", targetUser.Id, character.Name, character.LodestoneId);
        
        var details = await GetOrRefreshLodestoneDetailsAsync(character, forceRefresh, cancellationToken);
        if (details is null)
        {
            _logger.LogWarning("Failed to get Lodestone details. UserId={UserId}, LodestoneId={LodestoneId}", targetUser.Id, character.LodestoneId);
            return AchievementResumeBuildResult.FromFailure("Lodestone実績の取得に失敗しました。時間をおいて再試行してください。");
        }

        _logger.LogInformation("Loaded achievements. Count={Count}, Status={Status}", details.Achievements.Items.Count, details.Achievements.Status);

        var profile = await _db.GetDiscordUserProfileAsync(targetUser.Id);
        var guildUser = guild.GetUser(targetUser.Id);
        var displayName = guildUser?.DisplayName ?? targetUser.GlobalName ?? targetUser.Username;
        var avatarUrl = targetUser.GetDisplayAvatarUrl(size: 256) ?? targetUser.GetAvatarUrl(size: 256) ?? targetUser.GetDefaultAvatarUrl();

        var allAchievements = details.Achievements.Items
            .OrderByDescending(item => item.EarnedAt ?? DateTimeOffset.MinValue)
            .ToList();

        var knownUltimateStatusById = await BuildKnownUltimateStatusMapAsync(
            character.LodestoneUrl,
            allAchievements,
            probeAchievements,
            cancellationToken);
        var supplementedUltimateAchievements = BuildSupplementedKnownUltimateAchievements(
            character.LodestoneUrl,
            allAchievements,
            knownUltimateStatusById);
        if (supplementedUltimateAchievements.Count > 0)
        {
            allAchievements.AddRange(supplementedUltimateAchievements);
            allAchievements = allAchievements
                .OrderByDescending(item => item.EarnedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.AchievementId)
                .ToList();
        }

        var raidAchievements = allAchievements
            .Where(IsRaidAchievement)
            .ToList();

        _logger.LogInformation("Raid achievements found: {Count}. Ultimate={UltCount}, Savage={SavCount}. AllAchievements={AllCount}",
            raidAchievements.Count,
            raidAchievements.Count(IsUltimateRaidAchievement),
            raidAchievements.Count(IsSavageRaidAchievement),
            allAchievements.Count);

        if (raidAchievements.Count > 0)
        {
            foreach (var ra in raidAchievements.Take(20))
            {
                _logger.LogInformation("  RaidAchievement: Id={Id}, Title={Title}, EarnedAt={EarnedAt}", ra.AchievementId, ra.Title, ra.EarnedAt);
            }
        }
        var ultimateCount = raidAchievements.Count(IsUltimateRaidAchievement);
        var knownUltimateHitCount = knownUltimateStatusById.Count(entry => entry.Value.Available is true);
        var savageCount = raidAchievements.Count(IsSavageRaidAchievement);
        var fallbackToAllAchievements = raidAchievements.Count == 0;
        var knownUltimateStatusLine = BuildKnownUltimateStatusLine(knownUltimateStatusById);
        var knownUltimateHasPrivate = knownUltimateStatusById.Values.Any(value => value.Available is null);
        var ultimateAchievementLines = BuildUltimateAchievementLines(knownUltimateStatusById);
        var knownSavageStatusById = await BuildKnownSavageStatusMapAsync(character.LodestoneUrl, raidAchievements, probeAchievements, cancellationToken);
        var savageAchievementLines = BuildSavageAchievementLines(knownSavageStatusById);
        savageAchievementLines = FilterSavageAchievementLines(savageAchievementLines, savageGroupFilter);
        var timelineLines = BuildTimelineLines(fallbackToAllAchievements ? allAchievements : raidAchievements);
        var categoryLines = BuildTopCategoryLines(fallbackToAllAchievements ? allAchievements : raidAchievements);
        var snapshot = new RaidResumeSnapshot(
            displayName,
            targetUser.Id,
            character.Name,
            character.World,
            string.IsNullOrWhiteSpace(character.DataCenter) ? "unknown" : character.DataCenter!,
            character.LodestoneUrl,
            character.PortraitUrl,
            forceRefresh,
            raidAchievements.Count,
            ultimateCount,
            knownUltimateHitCount,
            knownUltimateStatusLine,
            knownUltimateHasPrivate,
            savageCount,
            fallbackToAllAchievements,
            allAchievements.Count,
            ultimateAchievementLines,
            savageAchievementLines,
            timelineLines,
            categoryLines,
            DateTimeOffset.UtcNow);

        byte[]? imagePng = null;
        string? imageFileName = null;
        SixLabors.ImageSharp.Image? portraitImage = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.PortraitUrl))
            {
                try
                {
                    _logger.LogDebug("Loading portrait image. PortraitUrl={PortraitUrl}", snapshot.PortraitUrl);
                    var portraitBytes = await _http.GetByteArrayAsync(snapshot.PortraitUrl, cancellationToken);
                    portraitImage = SixLabors.ImageSharp.Image.Load(portraitBytes);
                    _logger.LogDebug("Portrait loaded. Size={Width}x{Height}", portraitImage.Width, portraitImage.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load portrait image. PortraitUrl={PortraitUrl}", snapshot.PortraitUrl);
                }
            }

            _logger.LogDebug("Rendering resume image. UserId={UserId}", targetUser.Id);
            imagePng = BuildResumeImagePng(snapshot, portraitImage);
            portraitImage?.Dispose();
            if (imagePng.Length > 0)
            {
                imageFileName = $"raid_resume_{targetUser.Id}_{snapshot.GeneratedAt:yyyyMMddHHmmss}.png";
                _logger.LogDebug("Resume image rendered. Size={Size}bytes", imagePng.Length);
            }
            else
            {
                _logger.LogWarning("Resume image rendered empty. UserId={UserId}", targetUser.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render raid resume image. UserId={UserId}", targetUser.Id);
            portraitImage?.Dispose();
            imagePng = null;
            imageFileName = null;
        }

        var embed = BuildEmbed(
            guildUser,
            targetUser,
            avatarUrl,
            character,
            profile,
            snapshot,
            imageFileName);

        return AchievementResumeBuildResult.FromSuccess(embed, imagePng, imageFileName);
    }

    private async Task<LodestoneCharacterExtrasDetailsSnapshot?> GetOrRefreshLodestoneDetailsAsync(
        LinkedCharacter character,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        LodestoneCharacterExtrasDetailsSnapshot? cachedSnapshot = null;
        DateTimeOffset? cachedSyncedAt = null;
        try
        {
            var cache = await _db.GetCharacterExtrasCacheAsync(character.LodestoneId);
            cachedSnapshot = TryDeserializeDetails(cache?.ExtrasJson);
            cachedSyncedAt = cache?.ExtrasSyncedAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Lodestone extras cache. LodestoneId={LodestoneId}", character.LodestoneId);
        }

        var now = DateTimeOffset.UtcNow;
        var cacheIsFresh = !forceRefresh &&
                           cachedSnapshot is not null &&
                           cachedSyncedAt.HasValue &&
                           cachedSyncedAt.Value >= now - ExtrasResyncInterval;
        if (cacheIsFresh)
        {
            return cachedSnapshot;
        }

        try
        {
            var fetched = await _lodestone.FetchCharacterExtrasDetailsAsync(character.LodestoneUrl, cancellationToken);
            var json = JsonSerializer.Serialize(fetched);
            await _db.UpdateCharacterExtrasCacheAsync(character.LodestoneId, json, now);
            return fetched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Lodestone extras details. LodestoneId={LodestoneId}", character.LodestoneId);
            return cachedSnapshot;
        }
    }

    private static LodestoneCharacterExtrasDetailsSnapshot? TryDeserializeDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LodestoneCharacterExtrasDetailsSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    private static Embed BuildEmbed(
        SocketGuildUser? guildUser,
        IUser targetUser,
        string avatarUrl,
        LinkedCharacter character,
        DiscordUserProfile? profile,
        RaidResumeSnapshot snapshot,
        string? imageFileName)
    {
        var portrait = !string.IsNullOrWhiteSpace(character.PortraitUrl) ? character.PortraitUrl : avatarUrl;
        var builder = new EmbedBuilder()
            .WithColor(new DiscordColor(236, 188, 92))
            .WithTitle($"{character.Name} のレイド履歴書")
            .WithDescription(BuildDescription(character, profile, snapshot.ForceRefresh))
            .WithAuthor(guildUser?.DisplayName ?? targetUser.GlobalName ?? targetUser.Username, avatarUrl)
            .WithThumbnailUrl(portrait)
            .WithCurrentTimestamp();

        builder.AddField(
            "絶達成状況",
            BuildUltimateAchievementText(snapshot.UltimateAchievementLines),
            false);

        if (!string.IsNullOrWhiteSpace(imageFileName))
        {
            builder.WithImageUrl($"attachment://{imageFileName}");
        }

        return builder.Build();
    }

    private static byte[] BuildResumeImagePng(RaidResumeSnapshot snapshot, SixLabors.ImageSharp.Image? portraitImage)
    {
        const int width = 1380;
        const int height = 840;
        const float margin = 28f;
        var goldAccent = ImgColor.FromRgb(201, 162, 39);
        var panelBorder = ImgColor.FromRgb(44, 60, 84);

        using var image = new Image<Rgba32>(width, height, ImgColor.FromRgb(10, 16, 28));

        var statusRows = BuildUltimateStatusRows(snapshot.UltimateAchievementLines);
        var savagePanelRows = BuildSavagePanelRows(BuildUltimateStatusRows(snapshot.SavageAchievementLines));

        var nameFont = GetResumeFont(44f, FontStyle.Bold);
        var subFont = GetResumeFont(16f, FontStyle.Regular);
        var headingFont = GetResumeFont(22f, FontStyle.Bold);
        var rowTitleFont = GetResumeFont(18f, FontStyle.Bold);
        var rowValueFont = GetResumeFont(16f, FontStyle.Bold);
        var smallFont = GetResumeFont(13f, FontStyle.Regular);

        image.Mutate(ctx =>
        {
            ctx.Fill(ImgColor.FromRgb(8, 14, 24));
            ctx.Fill(goldAccent, new ImgRectF(0, 0, width, 4));

            // Header row: portrait left, name right
            const float portraitSize = 120f;
            var portraitX = margin;
            var portraitY = margin + 10f;
            var portraitRect = new ImgRectF(portraitX, portraitY, portraitSize, portraitSize);
            if (portraitImage is not null)
            {
                var cropped = portraitImage.Clone(i => i.Resize(new ResizeOptions
                {
                    Size = new Size((int)portraitSize, (int)portraitSize),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                ctx.DrawImage(cropped, new Point((int)portraitX, (int)portraitY), 1f);
                cropped.Dispose();
                ctx.Draw(goldAccent, 3f, portraitRect);
            }
            else
            {
                ctx.Fill(ImgColor.FromRgb(30, 40, 56), portraitRect);
                ctx.Draw(goldAccent, 3f, portraitRect);
            }

            var nameX = portraitX + portraitSize + 20f;
            var nameY = portraitY + 14f;
            DrawText(ctx, snapshot.CharacterName, nameFont, ImgColor.FromRgb(245, 249, 255), nameX, nameY);
            DrawText(ctx, $"{snapshot.World} ・ {snapshot.DataCenter}", subFont, ImgColor.FromRgb(160, 175, 198), nameX, nameY + 52f);
            DrawText(ctx, $"Generated: {snapshot.GeneratedAt.ToOffset(JapanTimeOffset):yyyy-MM-dd HH:mm} JST", smallFont, ImgColor.FromRgb(120, 138, 162), nameX, nameY + 78f);

            // Main board (full width, just below header)
            var boardTop = portraitY + portraitSize + 18f;
            var boardRect = new ImgRectF(margin, boardTop, width - margin * 2f, height - boardTop - margin - 28f);
            DrawPanel(ctx, boardRect, ImgColor.FromRgb(15, 22, 36), panelBorder);

            // Two columns
            const float colGap = 18f;
            var innerW = boardRect.Width - 28f;
            var leftW = MathF.Floor((innerW - colGap) / 2f);
            var rightW = innerW - colGap - leftW;
            var leftRect = new ImgRectF(boardRect.X + 14f, boardRect.Y + 14f, leftW, boardRect.Height - 28f);
            var rightRect = new ImgRectF(leftRect.Right + colGap, boardRect.Y + 14f, rightW, boardRect.Height - 28f);
            DrawPanel(ctx, leftRect, ImgColor.FromRgb(18, 24, 36), ImgColor.FromRgb(48, 65, 88));
            DrawPanel(ctx, rightRect, ImgColor.FromRgb(18, 24, 36), ImgColor.FromRgb(48, 65, 88));

            // Headings
            DrawText(ctx, "絶達成状況", headingFont, ImgColor.FromRgb(236, 244, 255), leftRect.X + 14f, leftRect.Y + 12f);
            DrawText(ctx, "零式クリア日", headingFont, ImgColor.FromRgb(236, 244, 255), rightRect.X + 14f, rightRect.Y + 12f);
            ctx.Fill(goldAccent, new ImgRectF(leftRect.X + 14f, leftRect.Y + 44f, 100f, 2.5f));
            ctx.Fill(goldAccent, new ImgRectF(rightRect.X + 14f, rightRect.Y + 44f, 100f, 2.5f));

            // Left: Ultimate badges
            const float badgeGap = 7f;
            var badgeStartY = leftRect.Y + 58f;
            var targetBadgeRows = Math.Max(1, Math.Min(statusRows.Count, 6));
            var badgeAvailH = leftRect.Height - 78f;
            var badgeHeight = Math.Clamp((badgeAvailH - ((targetBadgeRows - 1) * badgeGap)) / targetBadgeRows, 58f, 76f);
            for (int i = 0; i < targetBadgeRows; i++)
            {
                var row = statusRows[i];
                var style = ResolveUltimateStatusStyle(row.Value);
                var bRect = new ImgRectF(leftRect.X + 12f, badgeStartY + i * (badgeHeight + badgeGap), leftRect.Width - 24f, badgeHeight);
                DrawPanel(ctx, bRect, style.Fill, style.Border);
                ctx.Fill(style.Accent, new ImgRectF(bRect.X, bRect.Y, 5f, bRect.Height));
                var dotRect = new ImgRectF(bRect.X + bRect.Width - 22f, bRect.Y + bRect.Height / 2f - 4f, 8f, 8f);
                ctx.Fill(style.ValueText, dotRect);
                DrawText(ctx, Truncate(row.Title, 30), rowTitleFont, ImgColor.FromRgb(236, 243, 252), bRect.X + 16f, bRect.Y + 8f, bRect.Width - 40f);
                DrawText(ctx, Truncate(row.Value, 40), rowValueFont, style.ValueText, bRect.X + 16f, bRect.Y + MathF.Max(34f, badgeHeight * 0.52f), bRect.Width - 40f);
            }

            // Right: Savage list
            const float savGap = 2.5f;
            const float savHeadH = 17f;
            const float savRowH = 22f;
            var savCatCount = savagePanelRows.Count(r => r.IsCategory);
            var savValCount = savagePanelRows.Count - savCatCount;
            var savReqH = savCatCount * (savHeadH + savGap) + savValCount * (savRowH + savGap);
            var savAvailH = rightRect.Height - 60f;
            var savScale = savReqH > 0 ? MathF.Min(1f, savAvailH / savReqH) : 1f;
            var sHeadH = MathF.Max(12f, savHeadH * savScale);
            var sRowH = MathF.Max(17f, savRowH * savScale);
            var sCatFont = GetResumeFont(Math.Clamp(sHeadH - 1f, 11f, 14f), FontStyle.Bold);
            var sNameFont = GetResumeFont(Math.Clamp(sRowH - 7f, 11f, 13f), FontStyle.Bold);
            var sValueFont = GetResumeFont(Math.Clamp(sRowH - 7f, 11f, 13f), FontStyle.Bold);
            var savY = rightRect.Y + 54f;
            var savBottom = rightRect.Y + rightRect.Height - 10f;
            foreach (var row in savagePanelRows)
            {
                if (row.IsCategory)
                {
                    if (savY + sHeadH > savBottom) break;
                    DrawText(ctx, row.Title, sCatFont, ImgColor.FromRgb(190, 205, 228), rightRect.X + 12f, savY, rightRect.Width - 20f);
                    var cw = TextMeasurer.MeasureBounds(row.Title, new TextOptions(sCatFont)).Width;
                    var clx = rightRect.X + 12f + cw + 8f;
                    var clw = rightRect.Right - 12f - clx;
                    if (clw > 14f) ctx.Fill(ImgColor.FromRgba(79, 108, 144, 140), new ImgRectF(clx, savY + sHeadH * 0.58f, clw, 1.2f));
                    savY += sHeadH + savGap;
                    continue;
                }
                if (savY + sRowH > savBottom) break;
                var srRect = new ImgRectF(rightRect.X + 8f, savY, rightRect.Width - 16f, sRowH);
                var sStyle = ResolveUltimateStatusStyle(row.Value);
                DrawPanel(ctx, srRect, sStyle.Fill, ImgColor.FromRgb(42, 58, 79));
                ctx.Fill(sStyle.Accent, new ImgRectF(srRect.X, srRect.Y, 4f, srRect.Height));
                var svBounds = TextMeasurer.MeasureBounds(row.Value, new TextOptions(sValueFont));
                var svX = srRect.X + srRect.Width - svBounds.Width - 8f;
                var sTitleW = MathF.Max(96f, svX - srRect.X - 16f);
                var sOffY = MathF.Max(2f, (sRowH - sNameFont.Size) * 0.35f);
                DrawText(ctx, Truncate(row.Title, 22), sNameFont, ImgColor.FromRgb(236, 243, 252), srRect.X + 8f, srRect.Y + sOffY, sTitleW);
                DrawText(ctx, row.Value, sValueFont, sStyle.ValueText, svX, srRect.Y + sOffY);
                savY += sRowH + savGap;
            }

            // Footer
            DrawText(ctx, $"Lodestone: {Truncate(snapshot.LodestoneUrl, 120)}", smallFont, ImgColor.FromRgb(110, 128, 152), margin + 4f, height - 24f, width - margin * 2f - 8f);
        });

        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static string BuildDescription(LinkedCharacter character, DiscordUserProfile? profile, bool forceRefresh)
    {
        var dc = string.IsNullOrWhiteSpace(character.DataCenter) ? "unknown" : character.DataCenter;
        var roleHints = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile?.FfxivDataCenter))
        {
            roleHints.Add($"登録DC: {profile.FfxivDataCenter}");
        }

        if (!string.IsNullOrWhiteSpace(profile?.FfxivWorld))
        {
            roleHints.Add($"登録World: {profile.FfxivWorld}");
        }

        var syncMode = forceRefresh ? "Lodestone再取得: 強制更新" : "Lodestone再取得: キャッシュ優先";
        var profileLine = roleHints.Count > 0 ? "\n" + string.Join(" / ", roleHints) : string.Empty;
        return $"{character.World} [{dc}]\n{syncMode}{profileLine}\n[キャラクターページを開く]({character.LodestoneUrl})";
    }

    private static string BuildSummaryText(
        int raidAchievementItemCount,
        int ultimateCount,
        int knownUltimateHitCount,
        string knownUltimateStatusLine,
        bool knownUltimateHasPrivate,
        int savageCount,
        bool fallbackToAllAchievements,
        int allAchievementCount)
    {
        var lines = new List<string>
        {
            $"レイド実績(取得明細): {raidAchievementItemCount}件",
            $"絶系: {ultimateCount}件",
            $"固定絶ヒット: {knownUltimateHitCount}/6",
            $"固定絶状態: {knownUltimateStatusLine}",
            $"零式系: {savageCount}件"
        };

        if (knownUltimateHasPrivate)
        {
            lines.Add("※ ?: 実績非公開/確認不可");
        }

        if (fallbackToAllAchievements)
        {
            lines.Add($"※レイド判定が0件のため全実績から表示中 (全{allAchievementCount}件)");
        }

        return string.Join('\n', lines);
    }

    private async Task<IReadOnlyDictionary<int, KnownUltimateProbeResult>> BuildKnownUltimateStatusMapAsync(
        string characterUrl,
        IReadOnlyCollection<LodestoneAchievementItem> achievements,
        bool probeAchievements,
        CancellationToken cancellationToken)
    {
        var statusById = new Dictionary<int, KnownUltimateProbeResult>(KnownUltimateAchievementIdOrder.Length);
        var existingById = achievements
            .Where(item => KnownUltimateAchievementIds.Contains(item.AchievementId))
            .GroupBy(item => item.AchievementId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.EarnedAt ?? DateTimeOffset.MinValue).First());

        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            if (existingById.TryGetValue(id, out var hit))
            {
                statusById[id] = new KnownUltimateProbeResult(
                    true,
                    hit.EarnedAt,
                    hit.Title,
                    hit.DetailUrl);
            }
            else
            {
                statusById[id] = new KnownUltimateProbeResult(false, null, null, null);
            }
        }

        var pendingIds = KnownUltimateAchievementIdOrder
            .Where(id => statusById[id].Available is not true || !statusById[id].EarnedAt.HasValue)
            .ToArray();
        if (!probeAchievements || pendingIds.Length == 0)
        {
            return statusById;
        }

        var tasks = pendingIds.Select(async id =>
        {
            try
            {
                var detail = await _lodestone.FetchAchievementDetailAsync(characterUrl, id, cancellationToken);
                var available = detail.Status switch
                {
                    LodestonePageDataStatus.Available => true,
                    LodestonePageDataStatus.Private => (bool?)null,
                    LodestonePageDataStatus.NotFound => false,
                    _ => false
                };
                return (
                    Id: id,
                    Probe: new KnownUltimateProbeResult(
                        available,
                        detail.EarnedAt,
                        detail.Title,
                        detail.DetailUrl));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve fixed ultimate achievement detail. AchievementId={AchievementId}", id);
                return (Id: id, Probe: new KnownUltimateProbeResult(false, null, null, null));
            }
        });

        var resolved = await Task.WhenAll(tasks);
        foreach (var entry in resolved)
        {
            if (entry.Probe.Available is true)
            {
                statusById[entry.Id] = entry.Probe;
            }
            else if (entry.Probe.Available is null)
            {
                statusById[entry.Id] = entry.Probe;
            }
            else if (statusById[entry.Id].Available is not true)
            {
                statusById[entry.Id] = entry.Probe;
            }
        }

        return statusById;
    }

    private List<LodestoneAchievementItem> BuildSupplementedKnownUltimateAchievements(
        string characterUrl,
        IReadOnlyCollection<LodestoneAchievementItem> achievements,
        IReadOnlyDictionary<int, KnownUltimateProbeResult> knownUltimateStatusById)
    {
        var existingIds = achievements
            .Select(item => item.AchievementId)
            .ToHashSet();
        var supplemented = new List<LodestoneAchievementItem>();

        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            if (existingIds.Contains(id))
            {
                continue;
            }

            if (!knownUltimateStatusById.TryGetValue(id, out var probe) || probe.Available is not true)
            {
                continue;
            }

            var title = probe.Title;
            if (string.IsNullOrWhiteSpace(title) &&
                (!KnownUltimateAchievementTitles.TryGetValue(id, out title) || string.IsNullOrWhiteSpace(title)))
            {
                title = $"achievement:{id}";
            }

            supplemented.Add(new LodestoneAchievementItem(
                id,
                title,
                "高難易度",
                title,
                probe.EarnedAt,
                !string.IsNullOrWhiteSpace(probe.DetailUrl)
                    ? probe.DetailUrl
                    : _lodestone.BuildCharacterAchievementDetailUrl(characterUrl, id)));
        }

        return supplemented;
    }

    private static string BuildKnownUltimateStatusLine(IReadOnlyDictionary<int, KnownUltimateProbeResult> statusById)
    {
        var tokens = new List<string>(KnownUltimateAchievementIdOrder.Length);
        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            statusById.TryGetValue(id, out var status);
            tokens.Add($"{GetKnownUltimateDisplayName(id)}({FormatKnownUltimateStatus(status?.Available)})");
        }

        return string.Join(" / ", tokens);
    }

    private static string GetKnownUltimateDisplayName(int achievementId)
    {
        if (KnownUltimateDisplayNames.TryGetValue(achievementId, out var displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        if (KnownUltimateAchievementTitles.TryGetValue(achievementId, out var title) &&
            !string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return $"achievement:{achievementId}";
    }

    private static string FormatKnownUltimateStatus(bool? status)
    {
        return status switch
        {
            true => "○",
            null => "?",
            _ => "-"
        };
    }

    private static string BuildRecentAchievementText(IReadOnlyList<LodestoneAchievementItem> achievements, bool fallbackToAllAchievements)
    {
        if (achievements.Count == 0)
        {
            return "絶/零式に該当する実績を確認できませんでした。";
        }

        var lines = achievements
            .Select(item =>
            {
                return FormatAchievementTitleWithDate(item, 88);
            })
            .ToList();

        if (fallbackToAllAchievements)
        {
            lines.Insert(0, "※レイド判定0件のため、最近実績を表示しています。");
        }

        return string.Join('\n', lines);
    }

    private static string BuildUltimateAchievementText(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return "絶実績を確認できませんでした。";
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<string> BuildUltimateAchievementLines(IReadOnlyDictionary<int, KnownUltimateProbeResult> statusById)
    {
        var lines = new List<string>(KnownUltimateAchievementIdOrder.Length);
        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            statusById.TryGetValue(id, out var probe);
            var title = GetKnownUltimateDisplayName(id);
            var statusLabel = FormatUltimateAchievementDateLabel(id, probe);
            lines.Add($"{title}：{statusLabel}");
        }

        return lines;
    }

    private async Task<IReadOnlyDictionary<int, KnownSavageProbeResult>> BuildKnownSavageStatusMapAsync(
        string characterUrl,
        IReadOnlyCollection<LodestoneAchievementItem> achievements,
        bool probeAchievements,
        CancellationToken cancellationToken)
    {
        var statusById = new Dictionary<int, KnownSavageProbeResult>(KnownSavageAchievementIds.Count);
        var existingById = achievements
            .Where(item => KnownSavageAchievementIds.Contains(item.AchievementId))
            .GroupBy(item => item.AchievementId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.EarnedAt ?? DateTimeOffset.MinValue).First());

        foreach (var id in KnownSavageAchievementIdOrder)
        {
            if (existingById.TryGetValue(id, out var hit))
            {
                statusById[id] = new KnownSavageProbeResult(
                    true,
                    hit.EarnedAt,
                    hit.Title,
                    hit.DetailUrl);
            }
            else
            {
                statusById[id] = new KnownSavageProbeResult(false, null, null, null);
            }
        }

        var pendingIds = KnownSavageAchievementIdOrder
            .Where(id => statusById[id].Available is not true || !statusById[id].EarnedAt.HasValue)
            .ToArray();
        if (!probeAchievements || pendingIds.Length == 0)
        {
            return statusById;
        }

        var tasks = pendingIds.Select(async id =>
        {
            try
            {
                var detail = await _lodestone.FetchAchievementDetailAsync(characterUrl, id, cancellationToken);
                var available = detail.Status switch
                {
                    LodestonePageDataStatus.Available => true,
                    LodestonePageDataStatus.Private => (bool?)null,
                    LodestonePageDataStatus.NotFound => false,
                    _ => false
                };
                return (
                    Id: id,
                    Probe: new KnownSavageProbeResult(
                        available,
                        detail.EarnedAt,
                        detail.Title,
                        detail.DetailUrl));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve fixed savage achievement detail. AchievementId={AchievementId}", id);
                return (Id: id, Probe: new KnownSavageProbeResult(false, null, null, null));
            }
        });

        var resolved = await Task.WhenAll(tasks);
        foreach (var entry in resolved)
        {
            statusById.TryGetValue(entry.Id, out var current);
            var mergedTitle = !string.IsNullOrWhiteSpace(current?.Title)
                ? current!.Title
                : entry.Probe.Title;
            var merged = new KnownSavageProbeResult(
                entry.Probe.Available,
                entry.Probe.EarnedAt ?? current?.EarnedAt,
                mergedTitle,
                !string.IsNullOrWhiteSpace(current?.DetailUrl) ? current!.DetailUrl : entry.Probe.DetailUrl);

            if (current?.Available is true && entry.Probe.Available is not true)
            {
                continue;
            }

            statusById[entry.Id] = merged;
        }

        return statusById;
    }

    private static IReadOnlyList<string> BuildSavageAchievementLines(IReadOnlyDictionary<int, KnownSavageProbeResult> statusById)
    {
        var lines = new List<string>(KnownSavageAchievementIds.Count);
        foreach (var id in KnownSavageAchievementIdOrder)
        {
            statusById.TryGetValue(id, out var probe);
            var title = GetKnownSavageDisplayName(id, probe);
            if (KnownSavageGroupLabels.TryGetValue(id, out var group) &&
                !string.IsNullOrWhiteSpace(group))
            {
                title = $"{group}｜{title}";
            }
            var dateLabel = FormatSavageAchievementDateLabel(id, probe);
            lines.Add($"{title}\t{dateLabel}");
        }

        return lines;
    }

    private static IReadOnlyList<string> FilterSavageAchievementLines(
        IReadOnlyList<string> lines,
        IReadOnlyCollection<string>? groupFilter)
    {
        if (lines.Count == 0 || groupFilter is null || groupFilter.Count == 0)
        {
            return lines;
        }

        var allowed = new HashSet<string>(
            groupFilter
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim()),
            StringComparer.Ordinal);
        if (allowed.Count == 0 || allowed.Contains("all"))
        {
            return lines;
        }

        var filtered = lines
            .Where(line =>
            {
                var separator = line.IndexOf('｜');
                if (separator <= 0)
                {
                    return false;
                }

                var group = line[..separator].Trim();
                return !string.IsNullOrWhiteSpace(group) && allowed.Contains(group);
            })
            .ToList();

        return filtered.Count > 0 ? filtered : ["零式\t未確認"];
    }

    private static string GetKnownSavageDisplayName(int achievementId, KnownSavageProbeResult? probe)
    {
        if (KnownSavageDisplayNames.TryGetValue(achievementId, out var displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        if (!string.IsNullOrWhiteSpace(probe?.Title))
        {
            var normalized = probe.Title!.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "零式" : normalized;
        }

        return "零式";
    }

    private static string FormatSavageAchievementDateLabel(int achievementId, KnownSavageProbeResult? probe)
    {
        if (probe is null)
        {
            return "未クリア";
        }

        return probe.Available switch
        {
            true when probe.EarnedAt.HasValue => FormatSavageClearDayLabel(achievementId, probe.EarnedAt.Value),
            true => "未クリア",
            null => "非公開",
            _ => "未クリア"
        };
    }

    private static string FormatSavageClearDayLabel(int achievementId, DateTimeOffset earnedAt)
    {
        var clearDateJst = earnedAt.ToOffset(JapanTimeOffset).Date;
        var clearDateLabel = clearDateJst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!KnownSavageImplementedAtJst.TryGetValue(achievementId, out var implementedAt))
        {
            return clearDateLabel;
        }

        var implementedDateJst = implementedAt.ToOffset(JapanTimeOffset).Date;
        var rawDays = (clearDateJst - implementedDateJst).Days;
        var dayIndex = Math.Max(0, rawDays);
        var patchText = ResolveClearPatchLabel(clearDateJst) is { Length: > 0 } patch
            ? $"Patch {patch} / "
            : string.Empty;
        return $"{clearDateLabel}（{patchText}実装{dayIndex}日目）";
    }

    private static IReadOnlyList<LodestoneAchievementItem> BuildHighlightedAchievements(
        IReadOnlyList<LodestoneAchievementItem> raidAchievements,
        int maxCount)
    {
        if (raidAchievements.Count == 0 || maxCount <= 0)
        {
            return Array.Empty<LodestoneAchievementItem>();
        }

        var picked = new List<LodestoneAchievementItem>(maxCount);
        var used = new HashSet<int>();
        var ordered = raidAchievements
            .OrderByDescending(item => item.EarnedAt ?? DateTimeOffset.MinValue)
            .ToList();

        static bool TryAdd(
            LodestoneAchievementItem item,
            int limit,
            List<LodestoneAchievementItem> selected,
            HashSet<int> usedIds)
        {
            if (selected.Count >= limit || usedIds.Contains(item.AchievementId))
            {
                return false;
            }

            selected.Add(item);
            usedIds.Add(item.AchievementId);
            return true;
        }

        // Phase 1: fixed ultimate IDs with known date first.
        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            var hit = ordered.FirstOrDefault(item => item.AchievementId == id && item.EarnedAt.HasValue);
            if (hit is not null && TryAdd(hit, maxCount, picked, used) && picked.Count >= maxCount)
            {
                return picked;
            }
        }

        // Phase 2: dated achievements overall.
        foreach (var item in ordered.Where(item => item.EarnedAt.HasValue))
        {
            if (TryAdd(item, maxCount, picked, used) && picked.Count >= maxCount)
            {
                return picked;
            }
        }

        // Phase 3: fixed ultimate IDs without date.
        foreach (var id in KnownUltimateAchievementIdOrder)
        {
            var hit = ordered.FirstOrDefault(item => item.AchievementId == id && !item.EarnedAt.HasValue);
            if (hit is not null && TryAdd(hit, maxCount, picked, used) && picked.Count >= maxCount)
            {
                return picked;
            }
        }

        // Phase 4: any remaining achievements.
        foreach (var item in ordered)
        {
            if (TryAdd(item, maxCount, picked, used) && picked.Count >= maxCount)
            {
                return picked;
            }
        }

        return picked;
    }

    private static IReadOnlyList<string> BuildTimelineLines(IReadOnlyList<LodestoneAchievementItem> achievements)
    {
        var lines = achievements
            .Where(item => item.EarnedAt.HasValue)
            .GroupBy(item => item.EarnedAt!.Value.Year)
            .OrderByDescending(group => group.Key)
            .Take(8)
            .Select(group => $"{group.Key}: {group.Count()}件")
            .ToList();

        return lines.Count == 0 ? ["年別データなし"] : lines;
    }

    private static IReadOnlyList<string> BuildTopCategoryLines(IReadOnlyList<LodestoneAchievementItem> achievements)
    {
        var lines = achievements
            .Select(item => NormalizeCategory(item.Category))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .GroupBy(category => category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(group => $"{group.Key}: {group.Count()}件")
            .ToList();

        return lines.Count == 0 ? ["カテゴリ情報なし"] : lines;
    }

    private static bool IsRaidAchievement(LodestoneAchievementItem item)
    {
        return IsUltimateRaidAchievement(item) || IsSavageRaidAchievement(item);
    }

    private static bool IsUltimateRaidAchievement(LodestoneAchievementItem item)
    {
        return KnownUltimateAchievementIds.Contains(item.AchievementId);
    }

    private static bool IsSavageRaidAchievement(LodestoneAchievementItem item)
    {
        if (KnownSavageAchievementIds.Contains(item.AchievementId))
        {
            return true;
        }

        var source = $"{item.Category} {item.Title} {item.RawText}";
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var text = source.ToLowerInvariant();
        return text.Contains("零式", StringComparison.Ordinal) ||
               text.Contains("savage", StringComparison.Ordinal);
    }

    private static string PickAchievementTitle(LodestoneAchievementItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title;
        }

        if (!string.IsNullOrWhiteSpace(item.RawText))
        {
            return item.RawText;
        }

        return $"achievement:{item.AchievementId}";
    }

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "その他";
        }

        var normalized = category.Trim();
        return normalized.Length <= 20 ? normalized : normalized[..20];
    }

    private static string FormatDateForImage(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return "取得済み";
        }

        return value.Value.ToOffset(JapanTimeOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatUltimateAchievementDateLabel(int achievementId, KnownUltimateProbeResult? probe)
    {
        if (probe is null)
        {
            return "未クリア";
        }

        return probe.Available switch
        {
            true when probe.EarnedAt.HasValue => FormatClearDayLabel(achievementId, probe.EarnedAt.Value),
            true => "未クリア",
            null => "非公開",
            _ => "未クリア"
        };
    }

    private static string FormatClearDayLabel(int achievementId, DateTimeOffset earnedAt)
    {
        var clearDateJst = earnedAt.ToOffset(JapanTimeOffset).Date;
        var clearDateLabel = clearDateJst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!KnownUltimateImplementedAtJst.TryGetValue(achievementId, out var implementedAt))
        {
            return clearDateLabel;
        }

        var implementedDateJst = implementedAt.ToOffset(JapanTimeOffset).Date;
        var rawDays = (clearDateJst - implementedDateJst).Days;
        var dayIndex = Math.Max(0, rawDays);
        var patchText = ResolveClearPatchLabel(clearDateJst) is { Length: > 0 } patch
            ? $"Patch {patch} / "
            : string.Empty;
        return $"{clearDateLabel}（{patchText}実装{dayIndex}日目）";
    }

    private static string? ResolveClearPatchLabel(DateTime clearDateJst)
    {
        string? resolved = null;
        foreach (var (releasedAtJst, patch) in MajorPatchTimelineJst)
        {
            if (clearDateJst < releasedAtJst)
            {
                break;
            }

            resolved = patch;
        }

        return resolved;
    }

    private static bool TryParseClearDateJst(string value, out DateTime clearDate)
    {
        clearDate = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var datePart = value;
        var suffixIndex = datePart.IndexOf('（');
        if (suffixIndex > 0)
        {
            datePart = datePart[..suffixIndex];
        }

        datePart = datePart.Trim();
        return DateTime.TryParseExact(
            datePart,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out clearDate);
    }

    private static string FormatAchievementTitleWithDate(LodestoneAchievementItem item, int maxTitleLength = 88)
    {
        var title = Truncate(PickAchievementTitle(item), Math.Max(8, maxTitleLength));
        var when = item.EarnedAt.HasValue
            ? item.EarnedAt.Value.ToOffset(JapanTimeOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "取得済み";
        return $"{title}　{when}";
    }

    private static Font GetResumeFont(float size, FontStyle style)
    {
        foreach (var name in PreferredResumeFonts)
        {
            if (SystemFonts.TryGet(name, out var family) &&
                TryCreateUsableFont(family, size, style, out var font))
            {
                return font;
            }
        }

        foreach (var family in SystemFonts.Collection.Families)
        {
            if (TryCreateUsableFont(family, size, style, out var font))
            {
                return font;
            }
        }

        throw new InvalidOperationException("No usable system fonts available for raid resume image rendering.");
    }

    private static bool TryCreateUsableFont(FontFamily family, float size, FontStyle style, out Font font)
    {
        if (TryCreateFont(family, size, style, out font) && IsFontRenderable(font))
        {
            return true;
        }

        if (style != FontStyle.Regular &&
            TryCreateFont(family, size, FontStyle.Regular, out font) &&
            IsFontRenderable(font))
        {
            return true;
        }

        font = default!;
        return false;
    }

    private static bool TryCreateFont(FontFamily family, float size, FontStyle style, out Font font)
    {
        try
        {
            font = family.CreateFont(size, style);
            return true;
        }
        catch
        {
            font = default!;
            return false;
        }
    }

    private static bool IsFontRenderable(Font font)
    {
        var key = font.Family.Name;
        if (RenderableFontCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var renderable = TryMeasureFont(font);
        RenderableFontCache[key] = renderable;
        return renderable;
    }

    private static bool TryMeasureFont(Font font)
    {
        try
        {
            _ = TextMeasurer.MeasureBounds("Aaあア1%", new TextOptions(font));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawPanel(
        IImageProcessingContext ctx,
        ImgRectF rect,
        ImgColor fill,
        ImgColor border)
    {
        ctx.Fill(fill, rect);
        ctx.Draw(border, 1.6f, rect);
    }

    private static void DrawBadge(
        IImageProcessingContext ctx,
        ImgRectF rect,
        string text,
        Font font,
        ImgColor fill,
        ImgColor border,
        ImgColor textColor)
    {
        DrawPanel(ctx, rect, fill, border);
        DrawText(ctx, text, font, textColor, rect.X + 12f, rect.Y + 8f, rect.Width - 18f);
    }

    private static IReadOnlyList<UltimateStatusRow> BuildUltimateStatusRows(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return [new UltimateStatusRow("絶実績", "未確認")];
        }

        var rows = new List<UltimateStatusRow>(lines.Count);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                separator = line.IndexOf('：');
            }
            if (separator <= 0)
            {
                rows.Add(new UltimateStatusRow(line.Trim(), "未クリア"));
                continue;
            }

            var title = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "絶実績";
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = "未クリア";
            }

            rows.Add(new UltimateStatusRow(title, value));
        }

        return rows.Count == 0 ? [new UltimateStatusRow("絶実績", "未確認")] : rows;
    }

    private static IReadOnlyList<SavagePanelRow> BuildSavagePanelRows(IReadOnlyList<UltimateStatusRow> rows)
    {
        var buckets = SavageGroupOrder.ToDictionary(
            group => group,
            _ => new List<UltimateStatusRow>(),
            StringComparer.Ordinal);
        var others = new List<UltimateStatusRow>();

        foreach (var row in rows)
        {
            var title = row.Title.Trim();
            var separator = title.IndexOf('｜');
            if (separator > 0)
            {
                var expansion = title[..separator].Trim();
                var bareTitle = title[(separator + 1)..].Trim();
                var normalized = new UltimateStatusRow(
                    string.IsNullOrWhiteSpace(bareTitle) ? title : bareTitle,
                    row.Value);
                if (buckets.TryGetValue(expansion, out var bucket))
                {
                    bucket.Add(normalized);
                    continue;
                }
            }

            others.Add(row);
        }

        var panelRows = new List<SavagePanelRow>(rows.Count + SavageGroupOrder.Length + 1);
        foreach (var group in SavageGroupOrder)
        {
            var bucket = buckets[group];
            if (bucket.Count == 0)
            {
                continue;
            }

            panelRows.Add(new SavagePanelRow(group, string.Empty, true));
            foreach (var row in bucket)
            {
                panelRows.Add(new SavagePanelRow(row.Title, row.Value, false));
            }
        }

        if (others.Count > 0)
        {
            panelRows.Add(new SavagePanelRow("その他", string.Empty, true));
            foreach (var row in others)
            {
                panelRows.Add(new SavagePanelRow(row.Title, row.Value, false));
            }
        }

        if (panelRows.Count == 0)
        {
            panelRows.Add(new SavagePanelRow("零式", "未確認", false));
        }

        return panelRows;
    }

    private static bool IsDateLabel(string value)
    {
        return TryParseClearDateJst(value, out _);
    }

    private static UltimateStatusStyle ResolveUltimateStatusStyle(string value)
    {
        if (IsDateLabel(value))
        {
            return new UltimateStatusStyle(
                ImgColor.FromRgb(19, 49, 41),
                ImgColor.FromRgb(80, 140, 117),
                ImgColor.FromRgb(74, 151, 122),
                ImgColor.FromRgb(213, 245, 233));
        }

        if (string.Equals(value, "非公開", StringComparison.Ordinal))
        {
            return new UltimateStatusStyle(
                ImgColor.FromRgb(34, 42, 60),
                ImgColor.FromRgb(100, 119, 154),
                ImgColor.FromRgb(108, 128, 165),
                ImgColor.FromRgb(226, 234, 248));
        }

        return new UltimateStatusStyle(
            ImgColor.FromRgb(58, 27, 35),
            ImgColor.FromRgb(155, 84, 98),
            ImgColor.FromRgb(173, 94, 111),
            ImgColor.FromRgb(252, 226, 232));
    }

    private static void DrawStatCard(
        IImageProcessingContext ctx,
        ImgRectF rect,
        string label,
        string value,
        Font labelFont,
        Font numberFont)
    {
        DrawPanel(ctx, rect, ImgColor.FromRgb(21, 38, 62), ImgColor.FromRgb(81, 112, 147));
        DrawText(ctx, label, labelFont, ImgColor.FromRgb(190, 209, 231), rect.X + 14f, rect.Y + 14f);
        DrawText(ctx, value, numberFont, ImgColor.FromRgb(246, 223, 164), rect.X + 14f, rect.Y + 56f);
    }

    private static void DrawText(
        IImageProcessingContext ctx,
        string text,
        Font font,
        ImgColor color,
        float x,
        float y,
        float wrappingLength = 0f)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var options = new RichTextOptions(font)
        {
            Origin = new ImgPointF(x, y)
        };
        if (wrappingLength > 0f)
        {
            options.WrappingLength = wrappingLength;
        }

        ctx.DrawText(options, text, color);
    }

    private static void DrawLineList(
        IImageProcessingContext ctx,
        IReadOnlyList<string> lines,
        Font font,
        ImgColor color,
        float x,
        float startY,
        float lineHeight,
        float maxWrapWidth)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            DrawText(ctx, lines[i], font, color, x, startY + (i * lineHeight), maxWrapWidth);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record UltimateStatusRow(string Title, string Value);
    private sealed record SavagePanelRow(string Title, string Value, bool IsCategory);

    private sealed record UltimateStatusStyle(
        ImgColor Fill,
        ImgColor Border,
        ImgColor Accent,
        ImgColor ValueText);

    private sealed record RaidResumeSnapshot(
        string DisplayName,
        ulong UserId,
        string CharacterName,
        string World,
        string DataCenter,
        string LodestoneUrl,
        string? PortraitUrl,
        bool ForceRefresh,
        int RaidAchievementCount,
        int UltimateCount,
        int KnownUltimateHitCount,
        string KnownUltimateStatusLine,
        bool KnownUltimateHasPrivate,
        int SavageCount,
        bool FallbackToAllAchievements,
        int AllAchievementCount,
        IReadOnlyList<string> UltimateAchievementLines,
        IReadOnlyList<string> SavageAchievementLines,
        IReadOnlyList<string> TimelineLines,
        IReadOnlyList<string> CategoryLines,
        DateTimeOffset GeneratedAt);

    private sealed record KnownUltimateProbeResult(
        bool? Available,
        DateTimeOffset? EarnedAt,
        string? Title,
        string? DetailUrl);

    private sealed record KnownSavageProbeResult(
        bool? Available,
        DateTimeOffset? EarnedAt,
        string? Title,
        string? DetailUrl);
}

public sealed class AchievementResumeBuildResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public Embed? Embed { get; }
    public byte[]? ImagePng { get; }
    public string? ImageFileName { get; }

    private AchievementResumeBuildResult(
        bool success,
        string? errorMessage,
        Embed? embed,
        byte[]? imagePng,
        string? imageFileName)
    {
        Success = success;
        ErrorMessage = errorMessage;
        Embed = embed;
        ImagePng = imagePng;
        ImageFileName = imageFileName;
    }

    public static AchievementResumeBuildResult FromFailure(string message)
        => new(false, message, null, null, null);

    public static AchievementResumeBuildResult FromSuccess(Embed embed, byte[]? imagePng = null, string? imageFileName = null)
        => new(true, null, embed, imagePng, imageFileName);
}
