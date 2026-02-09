namespace RaidDcBot.Models;

public sealed class LodestoneOptions
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    public static readonly string[] DefaultAllowedHosts =
    {
        "na.finalfantasyxiv.com",
        "jp.finalfantasyxiv.com",
        "eu.finalfantasyxiv.com",
        "fr.finalfantasyxiv.com",
        "de.finalfantasyxiv.com",
        "lodestone.finalfantasyxiv.com"
    };

    public string UserAgent { get; init; } = DefaultUserAgent;
    public int TimeoutSeconds { get; init; } = 15;
    public string[] AllowedHosts { get; init; } = DefaultAllowedHosts;
}
