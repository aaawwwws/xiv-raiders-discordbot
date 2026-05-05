namespace RaidDcBot.Models;

public sealed class VoiceHourlyAverage
{
    public int Hour { get; init; }
    public double AverageUsers { get; init; }
    public int TotalDays { get; init; }
}
