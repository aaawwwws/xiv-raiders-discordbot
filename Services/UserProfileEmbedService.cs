using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Data;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class UserProfileEmbedService
{
    private static readonly TimeSpan LodestoneExtrasResyncInterval = TimeSpan.FromDays(1);
    private readonly Db _db;
    private readonly LodestoneClient _lodestone;
    private readonly DiscordOptions _options;
    private readonly ILogger<UserProfileEmbedService> _logger;

    public UserProfileEmbedService(
        Db db,
        LodestoneClient lodestone,
        IOptions<DiscordOptions> options,
        ILogger<UserProfileEmbedService> logger)
    {
        _db = db;
        _lodestone = lodestone;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Embed> BuildProfileEmbedAsync(
        SocketUser user,
        SocketGuild? guild,
        string title,
        Color color,
        bool includeBio = true,
        bool includeLodestoneExtras = false,
        Action<EmbedBuilder>? configure = null)
    {
        var snapshot = await BuildProfileSnapshotAsync(user, guild);

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp()
            .WithAuthor(snapshot.AuthorName, snapshot.AvatarUrl)
            .WithThumbnailUrl(snapshot.ThumbnailUrl);

        embed.AddField("基本情報", snapshot.BasicInfo, false);
        embed.AddField("キャラクター", snapshot.CharacterInfo, true);
        if (includeLodestoneExtras)
        {
            var lodestoneExtras = await BuildLodestoneExtrasInfoAsync(snapshot.LinkedCharacter);
            embed.AddField("Lodestone追加情報", lodestoneExtras, false);
        }
        embed.AddField("SNS", snapshot.SnsInfo, true);
        if (includeBio)
        {
            embed.AddField("一言", snapshot.BioInfo, false);
        }
        embed.AddField("ロール", snapshot.RoleInfo, false);

        configure?.Invoke(embed);

        return embed.Build();
    }

    public async Task<Embed> BuildRecruitJoinEmbedAsync(
        SocketUser user,
        SocketGuild? guild,
        string title,
        Color color,
        string position,
        string job,
        string comment)
    {
        var snapshot = await BuildProfileSnapshotAsync(user, guild);

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp()
            .WithAuthor(snapshot.AuthorName, snapshot.AvatarUrl)
            .WithThumbnailUrl(snapshot.ThumbnailUrl);

        var positionText = string.IsNullOrWhiteSpace(position) ? "不明" : position;
        var jobText = string.IsNullOrWhiteSpace(job) ? "不明" : job;
        var commentText = string.IsNullOrWhiteSpace(comment) ? "なし" : Truncate(comment.Trim(), 900);

        var joinLines = string.Join('\n', new[]
        {
            $"ポジション: {positionText}",
            $"ジョブ: {jobText}",
            $"コメント: {commentText}"
        });
        embed.AddField("参加", joinLines, false);

        var profileLines = string.Join('\n', new[]
        {
            snapshot.BasicInfo,
            "キャラクター",
            snapshot.CharacterInfo
        });
        embed.AddField("プロフィール", profileLines, false);
        embed.AddField("SNS", snapshot.SnsInfo, false);

        return embed.Build();
    }

    private async Task<ProfileSnapshot> BuildProfileSnapshotAsync(SocketUser user, SocketGuild? guild)
    {
        var guildUser = guild?.GetUser(user.Id);
        var displayName = guildUser?.DisplayName ?? user.GlobalName ?? user.Username;
        await _db.UpsertDiscordUserProfileAsync(user.Id, displayName, user.Username, user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl());

        var character = await _db.GetMainCharacterAsync(user.Id);
        var profile = await _db.GetDiscordUserProfileAsync(user.Id);

        var avatar = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
        var authorName = user.GlobalName ?? user.Username;
        var portrait = character?.PortraitUrl;
        if (character is not null)
        {
            portrait = await EnsureCharacterPortraitAsync(character) ?? portrait;
        }

        var thumbnail = !string.IsNullOrWhiteSpace(portrait) ? portrait : avatar;

        return new ProfileSnapshot(
            authorName,
            avatar,
            thumbnail,
            BuildVoiceBasicInfo(user),
            BuildVoiceRoleInfo(guildUser),
            BuildVoiceCharacterInfo(character, profile),
            character,
            BuildVoiceSnsInfo(profile),
            BuildVoiceBioInfo(profile, _options.VoiceProfileMaxBioLength));
    }

    private async Task<string> BuildLodestoneExtrasInfoAsync(LinkedCharacter? character)
    {
        if (character is null || string.IsNullOrWhiteSpace(character.LodestoneUrl))
        {
            return "未連携";
        }

        try
        {
            var extrasDetails = await GetOrRefreshLodestoneExtrasDetailsAsync(character);
            if (extrasDetails is null)
            {
                return "取得失敗";
            }

            var extras = ToSummarySnapshot(extrasDetails);
            var lines = new List<string>
            {
                $"マウント: {FormatLodestoneCount(extras.Mounts)}",
                $"ミニオン: {FormatLodestoneCount(extras.Minions)}",
                $"アチーブメント: {FormatLodestoneCount(extras.Achievements)}",
                $"ジョブLv: {FormatLodestoneJobLevels(extras.JobLevels)}"
            };

            return Truncate(string.Join('\n', lines), 1000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Lodestone extras for {LodestoneId}.", character.LodestoneId);
            return "取得失敗";
        }
    }

    private async Task<LodestoneCharacterExtrasDetailsSnapshot?> GetOrRefreshLodestoneExtrasDetailsAsync(LinkedCharacter character)
    {
        LodestoneCharacterExtrasCache? cache = null;
        LodestoneCharacterExtrasDetailsSnapshot? cachedSnapshot = null;

        try
        {
            cache = await _db.GetCharacterExtrasCacheAsync(character.LodestoneId);
            cachedSnapshot = TryDeserializeLodestoneExtrasDetails(cache?.ExtrasJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Lodestone extras cache for {LodestoneId}.", character.LodestoneId);
        }

        var now = DateTimeOffset.UtcNow;
        var cacheIsFresh = cache?.ExtrasSyncedAt is DateTimeOffset syncedAt &&
                           syncedAt >= now - LodestoneExtrasResyncInterval &&
                           cachedSnapshot is not null;
        if (cacheIsFresh)
        {
            return cachedSnapshot;
        }

        try
        {
            var refreshed = await _lodestone.FetchCharacterExtrasDetailsAsync(character.LodestoneUrl);
            var json = JsonSerializer.Serialize(refreshed);
            await _db.UpdateCharacterExtrasCacheAsync(character.LodestoneId, json, now);
            return refreshed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Lodestone extras details for {LodestoneId}.", character.LodestoneId);
            return cachedSnapshot;
        }
    }

    private static LodestoneCharacterExtrasDetailsSnapshot? TryDeserializeLodestoneExtrasDetails(string? json)
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

    private static LodestoneCharacterExtrasSnapshot ToSummarySnapshot(LodestoneCharacterExtrasDetailsSnapshot details)
    {
        return new LodestoneCharacterExtrasSnapshot(
            details.JobLevels,
            new LodestoneCountSnapshot(details.Mounts.Status, details.Mounts.Count ?? details.Mounts.Items.Count),
            new LodestoneCountSnapshot(details.Minions.Status, details.Minions.Count ?? details.Minions.Items.Count),
            new LodestoneCountSnapshot(details.Achievements.Status, details.Achievements.Count ?? details.Achievements.Items.Count));
    }

    private async Task<string?> EnsureCharacterPortraitAsync(LinkedCharacter character)
    {
        if (string.IsNullOrWhiteSpace(character.LodestoneUrl))
        {
            return character.PortraitUrl;
        }

        var cooldown = _options.LodestoneFetchCooldownMinutes;
        var hasValidPortrait = !string.IsNullOrWhiteSpace(character.PortraitUrl) &&
                               LodestoneClient.LooksLikeCharacterPortraitUrl(character.PortraitUrl);
        if (hasValidPortrait && cooldown > 0)
        {
            var threshold = DateTimeOffset.UtcNow.AddMinutes(-cooldown);
            if (character.UpdatedAt > threshold)
            {
                return character.PortraitUrl;
            }
        }

        try
        {
            var html = await _lodestone.FetchProfileHtmlAsync(character.LodestoneUrl);
            var portrait = _lodestone.ExtractPortraitUrl(html);
            if (string.IsNullOrWhiteSpace(portrait))
            {
                return character.PortraitUrl;
            }

            if (!string.Equals(portrait, character.PortraitUrl, StringComparison.Ordinal))
            {
                await _db.UpdateCharacterPortraitAsync(character.LodestoneId, portrait);
            }

            return portrait;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh portrait for {LodestoneId}.", character.LodestoneId);
            return character.PortraitUrl;
        }
    }

    private static string BuildVoiceBasicInfo(SocketUser user)
    {
        var discordName = user.Username;
        return $"Discord: {MentionUser(user.Id)} (@{discordName})";
    }

    private static string BuildVoiceRoleInfo(SocketGuildUser? guildUser)
    {
        if (guildUser is null)
        {
            return "未取得";
        }

        var roleMentions = guildUser.Roles
            .Where(role => role.Id != guildUser.Guild.EveryoneRole.Id)
            .OrderByDescending(role => role.Position)
            .ThenBy(role => role.Id)
            .Select(role => role.Mention)
            .ToList();

        if (roleMentions.Count == 0)
        {
            return "なし";
        }

        return Truncate(string.Join(", ", roleMentions), 1000);
    }

    private static string BuildVoiceCharacterInfo(LinkedCharacter? character, DiscordUserProfile? profile)
    {
        if (character is not null)
        {
            var linkedName = string.IsNullOrWhiteSpace(character.Name) ? "unknown" : character.Name;
            var linkedWorld = string.IsNullOrWhiteSpace(character.World) ? "unknown" : character.World;
            var linkedDc = string.IsNullOrWhiteSpace(character.DataCenter) ? "unknown" : character.DataCenter;
            return $"名前: {linkedName}\nデータセンター: {linkedDc}\nサーバー: {linkedWorld}";
        }

        if (profile is not null &&
            !string.IsNullOrWhiteSpace(profile.FfxivName) &&
            !string.IsNullOrWhiteSpace(profile.FfxivWorld))
        {
            var profileDc = string.IsNullOrWhiteSpace(profile.FfxivDataCenter) ? "unknown" : profile.FfxivDataCenter;
            return $"名前: {profile.FfxivName}\nデータセンター: {profileDc}\nサーバー: {profile.FfxivWorld}";
        }

        return "未連携";
    }

    private static string BuildVoiceSnsInfo(DiscordUserProfile? profile)
    {
        var x = SocialLinkService.FormatX(profile?.XHandle);
        var bluesky = SocialLinkService.FormatBluesky(profile?.BlueskyHandle);
        return $"X: {x}\nBluesky: {bluesky}";
    }

    private static string BuildVoiceBioInfo(DiscordUserProfile? profile, int maxLength)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Bio))
        {
            return "未設定";
        }

        var limit = maxLength > 0 ? maxLength : 250;
        return Truncate(profile.Bio, limit);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }


    private static string FormatLodestoneCount(LodestoneCountSnapshot snapshot)
    {
        return snapshot.Status switch
        {
            LodestonePageDataStatus.Available when snapshot.Count.HasValue => snapshot.Count.Value.ToString(),
            LodestonePageDataStatus.Private => "非公開",
            LodestonePageDataStatus.NotFound => "未取得",
            _ => "取得失敗"
        };
    }

    private static string FormatLodestoneJobLevels(LodestoneJobLevelsSnapshot snapshot)
    {
        if (snapshot.Status == LodestonePageDataStatus.Private)
        {
            return "非公開";
        }

        if (snapshot.Status == LodestonePageDataStatus.NotFound)
        {
            return "未取得";
        }

        if (snapshot.Status != LodestonePageDataStatus.Available || snapshot.Jobs.Count == 0)
        {
            return "取得失敗";
        }

        var jobs = snapshot.Jobs
            .OrderByDescending(x => x.Level)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var maxLevel = jobs.Max(x => x.Level);
        var maxCount = jobs.Count(x => x.Level == maxLevel);
        var shown = jobs.Take(8).Select(x => $"{x.Name} {x.Level}").ToList();
        var suffix = jobs.Count > shown.Count ? $" (+{jobs.Count - shown.Count})" : string.Empty;

        return $"最大{maxLevel} ({maxCount}ジョブ) / {string.Join(", ", shown)}{suffix}";
    }

    private static string MentionUser(ulong userId) => $"<@{userId}>";
    private static string FormatTimestamp(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:F>";

    private sealed record ProfileSnapshot(
        string AuthorName,
        string AvatarUrl,
        string ThumbnailUrl,
        string BasicInfo,
        string RoleInfo,
        string CharacterInfo,
        LinkedCharacter? LinkedCharacter,
        string SnsInfo,
        string BioInfo);
}
