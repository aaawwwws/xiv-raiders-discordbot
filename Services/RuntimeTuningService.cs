using RaidDcBot.Models;
using Microsoft.Extensions.Options;

namespace RaidDcBot.Services;

public sealed class RuntimeTuningService
{
    private readonly object _gate = new();
    private int _cleanupIntervalMinutes;
    private int _recruitClosedRetentionHours;
    private int _recruitCanceledRetentionHours;

    public RuntimeTuningService(IOptions<DiscordOptions> options)
    {
        var value = options.Value;
        _cleanupIntervalMinutes = NormalizeCleanupIntervalMinutes(value.CleanupIntervalMinutes);
        _recruitClosedRetentionHours = NormalizeRetentionHours(value.RecruitClosedArtifactRetentionHours);
        _recruitCanceledRetentionHours = NormalizeRetentionHours(value.RecruitCanceledArtifactRetentionHours);
    }

    public RuntimeTuningSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RuntimeTuningSnapshot(
                _cleanupIntervalMinutes,
                _recruitClosedRetentionHours,
                _recruitCanceledRetentionHours);
        }
    }

    public RuntimeTuningSnapshot Update(int cleanupIntervalMinutes, int recruitClosedRetentionHours, int recruitCanceledRetentionHours)
    {
        lock (_gate)
        {
            _cleanupIntervalMinutes = NormalizeCleanupIntervalMinutes(cleanupIntervalMinutes);
            _recruitClosedRetentionHours = NormalizeRetentionHours(recruitClosedRetentionHours);
            _recruitCanceledRetentionHours = NormalizeRetentionHours(recruitCanceledRetentionHours);
            return new RuntimeTuningSnapshot(_cleanupIntervalMinutes, _recruitClosedRetentionHours, _recruitCanceledRetentionHours);
        }
    }

    private static int NormalizeCleanupIntervalMinutes(int value) => Math.Clamp(value <= 0 ? 10 : value, 1, 720);
    private static int NormalizeRetentionHours(int value) => Math.Clamp(value <= 0 ? 24 : value, 1, 24 * 30);
}

public sealed record RuntimeTuningSnapshot(
    int CleanupIntervalMinutes,
    int RecruitClosedRetentionHours,
    int RecruitCanceledRetentionHours);
