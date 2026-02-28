namespace RaidDcBot.Models;

public sealed class FfLogsOptions
{
    public bool Enabled { get; init; } = false;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public int PollIntervalSeconds { get; init; } = 60;
    public int RecentReportsLimitPerCharacter { get; init; } = 10;
    public int BackfillWindowMinutes { get; init; } = 15;
    public int HttpTimeoutSeconds { get; init; } = 15;
    public int MaxConcurrentCharacterQueries { get; init; } = 4;
    public FfLogsDeduplicationGranularity DeduplicationGranularity { get; init; } = FfLogsDeduplicationGranularity.Report;
}

public enum FfLogsDeduplicationGranularity
{
    Report = 0,
    Fight = 1
}
