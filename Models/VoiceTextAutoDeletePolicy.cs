namespace RaidDcBot.Models;

public sealed class VoiceTextAutoDeletePolicy
{
    public bool Enabled { get; init; }
    public int RetentionHours { get; init; }
}
