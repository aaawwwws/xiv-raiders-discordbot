namespace RaidDcBot.Models;

public sealed class LinkedCharacter
{
    public long LodestoneId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
    public string? DataCenter { get; init; }
    public string? PortraitUrl { get; init; }
    public string? SelfIntroduction { get; init; }
    public string Label { get; init; } = "sub";
    public bool IsMain { get; init; }
    public string LodestoneUrl { get; init; } = string.Empty;
    public DateTimeOffset LinkedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
