namespace RaidDcBot.Models;

public enum LodestonePageDataStatus
{
    Available = 0,
    Private = 1,
    NotFound = 2,
    Error = 3
}

public sealed record LodestoneJobLevel(string Name, int Level);

public sealed record LodestoneJobLevelsSnapshot(
    LodestonePageDataStatus Status,
    IReadOnlyList<LodestoneJobLevel> Jobs);

public sealed record LodestoneCountSnapshot(
    LodestonePageDataStatus Status,
    int? Count);

public sealed record LodestoneCharacterExtrasSnapshot(
    LodestoneJobLevelsSnapshot JobLevels,
    LodestoneCountSnapshot Mounts,
    LodestoneCountSnapshot Minions,
    LodestoneCountSnapshot Achievements);

public sealed record LodestoneCollectibleItem(
    string Name,
    string? TooltipKey,
    string? TooltipUrl,
    string? IconUrl);

public sealed record LodestoneCollectibleListSnapshot(
    LodestonePageDataStatus Status,
    int? Count,
    IReadOnlyList<LodestoneCollectibleItem> Items);

public sealed record LodestoneAchievementItem(
    int AchievementId,
    string? Title,
    string? Category,
    string RawText,
    DateTimeOffset? EarnedAt,
    string? DetailUrl);

public sealed record LodestoneAchievementListSnapshot(
    LodestonePageDataStatus Status,
    int? Count,
    IReadOnlyList<LodestoneAchievementItem> Items);

public sealed record LodestoneAchievementDetailSnapshot(
    LodestonePageDataStatus Status,
    DateTimeOffset? EarnedAt,
    string? Title,
    string? DetailUrl);

public sealed record LodestoneCharacterExtrasDetailsSnapshot(
    LodestoneJobLevelsSnapshot JobLevels,
    LodestoneCollectibleListSnapshot Mounts,
    LodestoneCollectibleListSnapshot Minions,
    LodestoneAchievementListSnapshot Achievements);
