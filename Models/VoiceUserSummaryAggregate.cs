namespace RaidDcBot.Models;

public sealed class VoiceUserSummaryAggregate
{
    public int SessionCount { get; init; }
    public long TotalDurationSeconds { get; init; }
}
