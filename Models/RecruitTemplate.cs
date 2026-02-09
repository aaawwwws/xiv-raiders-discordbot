namespace RaidDcBot.Models;

public sealed class RecruitTemplate
{
    public int Id { get; set; }
    public long GuildId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? DataCenter { get; set; }
    public string? Note { get; set; }
    public string? PositionsText { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
