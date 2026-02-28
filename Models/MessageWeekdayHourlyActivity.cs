namespace RaidDcBot.Models;

public sealed class MessageWeekdayHourlyActivity
{
    public int Weekday { get; init; }
    public int Hour { get; init; }
    public long MessageCount { get; init; }
    public int ActiveUsers { get; init; }
}
