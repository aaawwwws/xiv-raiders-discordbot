namespace RaidDcBot.Models;

public sealed record LodestoneFcProfile(
    string Name,
    string? Tag,
    string World,
    string? DataCenter,
    int? MemberCount,
    DateTimeOffset? FormedAt,
    string? IconUrl,
    string? Introduction,
    string? ActiveTime,
    string? Recruiting,
    string? ActiveMembers,
    string? Focus,
    string? Estate,
    string Url,
    int? Rank = null,
    IReadOnlyList<string>? GrandCompanyReputation = null,
    IReadOnlyList<FcMember>? Members = null);

public sealed record FcMember(
    string Name,
    string World,
    string? DataCenter,
    string? Rank);

