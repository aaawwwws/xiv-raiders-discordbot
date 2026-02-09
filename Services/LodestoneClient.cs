using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class LodestoneClient
{
    private static readonly Regex LodestoneIdRegex = new(@"lodestone/character/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly HashSet<string> _allowedHosts;

    public LodestoneClient(HttpClient httpClient, IOptions<LodestoneOptions> options)
    {
        _httpClient = httpClient;
        var configured = options.Value.AllowedHosts ?? Array.Empty<string>();
        var hosts = configured
            .Select(host => host.Trim().ToLowerInvariant())
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToArray();

        if (hosts.Length == 0)
        {
            hosts = LodestoneOptions.DefaultAllowedHosts;
        }

        _allowedHosts = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryExtractLodestoneId(string url, out long lodestoneId, out string error)
    {
        lodestoneId = 0;
        error = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "URLの形式が正しくありません。";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "https:// のLodestone URLのみ対応しています。";
            return false;
        }

        if (!IsAllowedHost(uri.Host))
        {
            error = "Lodestone公式ドメインのURLのみ対応しています。";
            return false;
        }

        var match = LodestoneIdRegex.Match(uri.AbsoluteUri);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out lodestoneId))
        {
            error = "LodestoneのキャラクターページURLを入力してください。";
            return false;
        }

        return true;
    }

    public string NormalizeUrl(string url, long lodestoneId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        return $"https://{uri.Host}/lodestone/character/{lodestoneId}/";
    }

    public async Task<string> FetchProfileHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lodestoneの取得に失敗しました (HTTP {(int)response.StatusCode}).");
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedHost(finalUri.Host))
        {
            throw new InvalidOperationException("Lodestone公式ドメイン以外への遷移を検出したため処理を中断しました。");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (IsUnsupportedBrowser(html))
        {
            throw new InvalidOperationException("Lodestoneがブラウザを拒否しました。User-Agentの設定を見直してください。");
        }

        return html;
    }

    public bool TryParseProfile(string html, out LodestoneProfile profile)
    {
        profile = default!;

        if (TryParseFromHtmlBlocks(html, out profile))
        {
            return true;
        }

        if (TryParseFromOgTitle(html, out profile))
        {
            return true;
        }

        return false;
    }

    public string? ExtractPortraitUrl(string html)
    {
        var portrait = FindPortraitImage(html);
        if (!string.IsNullOrWhiteSpace(portrait))
        {
            return portrait;
        }

        var ogImage = FindMetaContent(html, "property", "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage))
        {
            return ogImage;
        }

        var twitterImage = FindMetaContent(html, "name", "twitter:image");
        if (!string.IsNullOrWhiteSpace(twitterImage))
        {
            return twitterImage;
        }

        return null;
    }

    public static bool LooksLikeCharacterPortraitUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/f/", StringComparison.OrdinalIgnoreCase);
    }


    private static bool TryParseWorldText(string text, out string world, out string? dataCenter)
    {
        world = text;
        dataCenter = null;
        var start = text.LastIndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            dataCenter = text[(start + 1)..end].Trim();
            world = text[..start].Trim();
            return true;
        }

        return false;
    }

    private static bool TryParseFromHtmlBlocks(string html, out LodestoneProfile profile)
    {
        profile = default!;

        var name = FindElementText(html, new[]
        {
            new ElementPattern("frame__chara__name", "p"),
            new ElementPattern("frame__chara__name", "h2"),
            new ElementPattern("frame__chara__name", "div"),
            new ElementPattern("frame__chara__name", "span"),
            new ElementPattern("character__name", "p"),
            new ElementPattern("character__name", "h2"),
            new ElementPattern("character__name", "div"),
            new ElementPattern("character__name", "span")
        });

        var worldText = FindElementText(html, new[]
        {
            new ElementPattern("frame__chara__world", "p"),
            new ElementPattern("frame__chara__world", "div"),
            new ElementPattern("frame__chara__world", "span"),
            new ElementPattern("character__world", "p"),
            new ElementPattern("character__world", "div"),
            new ElementPattern("character__world", "span")
        });

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(worldText))
        {
            return false;
        }

        if (TryParseWorldText(worldText, out var world, out var dataCenter))
        {
            profile = new LodestoneProfile(name, world, dataCenter);
        }
        else
        {
            profile = new LodestoneProfile(name, worldText, null);
        }

        return true;
    }

    private static bool TryParseFromOgTitle(string html, out LodestoneProfile profile)
    {
        profile = default!;

        var titleMatch = Regex.Match(html, "property=\"og:title\" content=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (!titleMatch.Success)
        {
            return false;
        }

        var title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
        var parts = title.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var name = parts[0].Trim();
        string? worldText = null;
        for (var index = 1; index < parts.Length; index++)
        {
            var candidate = parts[index].Trim();
            if (IsBrandText(candidate))
            {
                continue;
            }

            worldText = candidate;
            break;
        }

        if (string.IsNullOrWhiteSpace(worldText))
        {
            return false;
        }

        if (TryParseWorldText(worldText, out var world, out var dataCenter))
        {
            profile = new LodestoneProfile(name, world, dataCenter);
        }
        else
        {
            profile = new LodestoneProfile(name, worldText, null);
        }

        return true;
    }

    private static bool IsBrandText(string value)
    {
        return value.Contains("Lodestone", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FINAL FANTASY", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindElementText(string html, IEnumerable<ElementPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(
                html,
                $"{pattern.ClassName}[^>]*>(.*?)</{pattern.Tag}>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                continue;
            }

            var inner = match.Groups[1].Value;
            var stripped = Regex.Replace(inner, "<.*?>", string.Empty, RegexOptions.Singleline);
            var decoded = WebUtility.HtmlDecode(stripped).Trim();
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                return decoded;
            }
        }

        return null;
    }

    private static string? FindMetaContent(string html, string attributeName, string attributeValue)
    {
        var pattern = $"{attributeName}=\"{Regex.Escape(attributeValue)}\"\\s+content=\"([^\"]+)\"";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }

    private static string? FindPortraitImage(string html)
    {
        var anchorBlockPattern = "<a[^>]*class=[\"'][^\"']*js__image_popup[^\"']*[\"'][^>]*>(.*?)</a>";
        var anchorBlockMatch = Regex.Match(html, anchorBlockPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (anchorBlockMatch.Success)
        {
            var block = anchorBlockMatch.Groups[1].Value;
            var imgPattern = "<img(?=[^>]*\\bwidth=[\"']220[\"'])(?=[^>]*\\bheight=[\"']300[\"'])[^>]*\\bsrc=[\"']([^\"']+)[\"'][^>]*>";
            var imgMatch = Regex.Match(block, imgPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (imgMatch.Success)
            {
                return WebUtility.HtmlDecode(imgMatch.Groups[1].Value).Trim();
            }
        }

        var anchorHrefPattern = "<a[^>]*class=[\"'][^\"']*js__image_popup[^\"']*[\"'][^>]*href=[\"']([^\"']+)[\"']";
        var anchorHrefMatch = Regex.Match(html, anchorHrefPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (anchorHrefMatch.Success)
        {
            return WebUtility.HtmlDecode(anchorHrefMatch.Groups[1].Value).Trim();
        }

        var anchorHrefPatternAlt = "<a[^>]*href=[\"']([^\"']+)[\"'][^>]*class=[\"'][^\"']*js__image_popup[^\"']*[\"']";
        var anchorHrefMatchAlt = Regex.Match(html, anchorHrefPatternAlt, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (anchorHrefMatchAlt.Success)
        {
            return WebUtility.HtmlDecode(anchorHrefMatchAlt.Groups[1].Value).Trim();
        }

        var pattern = "<img(?=[^>]*\\bwidth=[\"']220[\"'])(?=[^>]*\\bheight=[\"']300[\"'])[^>]*\\bsrc=[\"']([^\"']+)[\"'][^>]*>";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }


    private readonly record struct ElementPattern(string ClassName, string Tag);

    private bool IsAllowedHost(string host)
    {
        return _allowedHosts.Contains(host.Trim().ToLowerInvariant());
    }

    private static bool IsUnsupportedBrowser(string html)
    {
        return html.Contains("unsupported_browser", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Your browser is not supported", StringComparison.OrdinalIgnoreCase)
            || html.Contains("ご利用のブラウザ", StringComparison.OrdinalIgnoreCase);
    }
}
