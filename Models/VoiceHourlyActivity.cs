namespace RaidDcBot.Models;

public sealed class VoiceHourlyActivity
{
    public int Hour { get; init; }
    public long TotalSeconds { get; init; }
    public int SessionCount { get; init; }
    public int ActiveUsers { get; init; }
}
