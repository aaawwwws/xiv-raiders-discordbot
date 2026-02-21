namespace RaidDcBot.Models;

public sealed record LodestoneCwlsMember(
    string Name,
    string World,
    string? DataCenter,
    string? Rank,
    string CharacterUrl);

public sealed record LodestoneCwlsProfile(
    string Name,
    string? DataCenter,
    int? MemberCount,
    DateTimeOffset? FormedAt,
    string Url,
    string? IconUrl,
    IReadOnlyList<LodestoneCwlsMember> Members);
