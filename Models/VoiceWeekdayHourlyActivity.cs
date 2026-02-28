namespace RaidDcBot.Models;

public sealed class VoiceWeekdayHourlyActivity
{
    public int Weekday { get; init; }
    public int Hour { get; init; }
    public long TotalSeconds { get; init; }
    public int SessionCount { get; init; }
    public int ActiveUsers { get; init; }
}
