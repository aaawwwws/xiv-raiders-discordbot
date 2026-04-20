namespace RaidDcBot.Models;

public sealed class BotAdminOperation
{
    public int Id { get; init; }
    public string OperationType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Note { get; init; }
}
