namespace RaidDcBot.Models;

public sealed class LodestoneCharacter
{
    public long LodestoneId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
    public string? DataCenter { get; init; }
    public string? PortraitUrl { get; init; }
    public string? SelfIntroduction { get; init; }
    public string LodestoneUrl { get; init; } = string.Empty;
}
