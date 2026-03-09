namespace RaidDcBot.Models;

public sealed class MessageHourlyActivity
{
    public int Hour { get; init; }
    public long MessageCount { get; init; }
}
