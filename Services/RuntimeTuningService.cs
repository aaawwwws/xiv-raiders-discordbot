using RaidDcBot.Models;
using Microsoft.Extensions.Options;

namespace RaidDcBot.Services;

public sealed class RuntimeTuningService
{
    private readonly object _gate = new();
    private int _cleanupIntervalMinutes;
    private int _recruitClosedRetentionHours;
    private int _recruitCanceledRetentionHours;
    private int _messageLogRetentionDays;
    private int _voiceSessionRetentionDays;
    private int _reactionLogRetentionDays;

    public RuntimeTuningService(IOptions<DiscordOptions> options)
    {
        var value = options.Value;
        _cleanupIntervalMinutes = NormalizeCleanupIntervalMinutes(value.CleanupIntervalMinutes);
        _recruitClosedRetentionHours = NormalizeRetentionHours(value.RecruitClosedArtifactRetentionHours);
        _recruitCanceledRetentionHours = NormalizeRetentionHours(value.RecruitCanceledArtifactRetentionHours);
        _messageLogRetentionDays = NormalizeRetentionDays(value.MessageLogRetentionDays);
        _voiceSessionRetentionDays = NormalizeRetentionDays(value.VoiceSessionRetentionDays);
        var fallbackReactionRetentionDays = value.MessageLogRetentionDays > 0
            ? Math.Max(value.MessageLogRetentionDays, Math.Max(0, value.InactiveAutoKickDays))
            : 0;
        _reactionLogRetentionDays = NormalizeRetentionDays(fallbackReactionRetentionDays);
    }

    public RuntimeTuningSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RuntimeTuningSnapshot(
                _cleanupIntervalMinutes,
                _recruitClosedRetentionHours,
                _recruitCanceledRetentionHours,
                _messageLogRetentionDays,
                _voiceSessionRetentionDays,
                _reactionLogRetentionDays);
        }
    }

    public RuntimeTuningSnapshot Update(
        int cleanupIntervalMinutes,
        int recruitClosedRetentionHours,
        int recruitCanceledRetentionHours,
        int messageLogRetentionDays,
        int voiceSessionRetentionDays,
        int reactionLogRetentionDays)
    {
        lock (_gate)
        {
            _cleanupIntervalMinutes = NormalizeCleanupIntervalMinutes(cleanupIntervalMinutes);
            _recruitClosedRetentionHours = NormalizeRetentionHours(recruitClosedRetentionHours);
            _recruitCanceledRetentionHours = NormalizeRetentionHours(recruitCanceledRetentionHours);
            _messageLogRetentionDays = NormalizeRetentionDays(messageLogRetentionDays);
            _voiceSessionRetentionDays = NormalizeRetentionDays(voiceSessionRetentionDays);
            _reactionLogRetentionDays = NormalizeRetentionDays(reactionLogRetentionDays);
            return new RuntimeTuningSnapshot(
                _cleanupIntervalMinutes,
                _recruitClosedRetentionHours,
                _recruitCanceledRetentionHours,
                _messageLogRetentionDays,
                _voiceSessionRetentionDays,
                _reactionLogRetentionDays);
        }
    }

    private static int NormalizeCleanupIntervalMinutes(int value) => Math.Clamp(value <= 0 ? 10 : value, 1, 720);
    private static int NormalizeRetentionHours(int value) => Math.Clamp(value <= 0 ? 24 : value, 1, 24 * 30);
    private static int NormalizeRetentionDays(int value) => Math.Clamp(value, 0, 3650);
}

public sealed record RuntimeTuningSnapshot(
    int CleanupIntervalMinutes,
    int RecruitClosedRetentionHours,
    int RecruitCanceledRetentionHours,
    int MessageLogRetentionDays,
    int VoiceSessionRetentionDays,
    int ReactionLogRetentionDays);
