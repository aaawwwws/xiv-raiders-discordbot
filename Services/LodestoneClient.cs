using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class LodestoneClient
{
    private static readonly Regex LodestoneIdRegex = new(@"lodestone/character/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LodestoneCwlsIdRegex = new(@"lodestone/crossworld_linkshell/([0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    public bool TryNormalizeCwlsUrl(string url, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
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

        var match = LodestoneCwlsIdRegex.Match(uri.AbsoluteUri);
        if (!match.Success)
        {
            error = "LodestoneのCWLSページURLを入力してください。";
            return false;
        }

        normalizedUrl = $"https://{uri.Host}/lodestone/crossworld_linkshell/{match.Groups[1].Value}/";
        return true;
    }

    public Task<string> FetchProfileHtmlAsync(string url, CancellationToken cancellationToken = default)
        => FetchHtmlAsync(url, cancellationToken);

    public async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
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

    public bool TryParseCwls(string html, string sourceUrl, out LodestoneCwlsProfile profile)
    {
        profile = default!;

        var name = TryExtractCwlsName(html);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var dc = FindElementText(html, new[]
        {
            new ElementPattern("heading__cwls__dcname", "span")
        });

        var memberCount = TryExtractInteger(
            html,
            "<div\\s+class=\"parts__total\">\\s*(\\d+)\\s*人\\s*</div>");

        var formedAt = TryExtractCwlsFormedAt(html);
        var iconUrl = FindCwlsIconUrl(html);
        var members = ParseCwlsMembers(html, sourceUrl);

        profile = new LodestoneCwlsProfile(
            name,
            string.IsNullOrWhiteSpace(dc) ? null : dc,
            memberCount,
            formedAt,
            sourceUrl,
            iconUrl,
            members);

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

    private static string? TryExtractCwlsName(string html)
    {
        var ogTitle = FindMetaContent(html, "property", "og:title");
        if (!string.IsNullOrWhiteSpace(ogTitle))
        {
            var parts = ogTitle.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                return parts[0];
            }
        }

        var headingMatch = Regex.Match(
            html,
            "<h3[^>]*class=[\"'][^\"']*heading__linkshell__name[^\"']*[\"'][^>]*>(?<inner>.*?)</h3>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!headingMatch.Success)
        {
            return null;
        }

        var inner = Regex.Replace(
            headingMatch.Groups["inner"].Value,
            "<span[^>]*class=[\"'][^\"']*heading__cwls__dcname[^\"']*[\"'][^>]*>.*?</span>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var normalized = NormalizeInnerText(inner);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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

    private static int? TryExtractInteger(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
        {
            return null;
        }

        return value;
    }

    private static DateTimeOffset? TryExtractCwlsFormedAt(string html)
    {
        var formedMatch = Regex.Match(
            html,
            "heading__cwls__formed.*?ldst_strftime\\((\\d+),\\s*'YMD'\\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!formedMatch.Success || !long.TryParse(formedMatch.Groups[1].Value, out var epoch))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindCwlsIconUrl(string html)
    {
        var match = Regex.Match(
            html,
            "<div[^>]*class=[\"'][^\"']*heading__linkshell__icon[^\"']*[\"'][^>]*>\\s*<img[^>]*src=[\"']([^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return FindMetaContent(html, "property", "og:image");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }

    private static IReadOnlyList<LodestoneCwlsMember> ParseCwlsMembers(string html, string sourceUrl)
    {
        var list = new List<LodestoneCwlsMember>();
        Uri? sourceUri = null;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed))
        {
            sourceUri = parsed;
        }

        var start = Regex.Match(
            html,
            "<div\\s+class=[\"'][^\"']*ls__member[^\"']*[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!start.Success)
        {
            return list;
        }

        var remaining = html[start.Index..];
        var pager = Regex.Match(
            remaining,
            "<ul[^>]*class=[\"'][^\"']*btn__pager[^\"']*[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var side = Regex.Match(
            remaining,
            "<div[^>]*class=[\"'][^\"']*ldst__side[^\"']*[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var cutCandidates = new List<int>();
        if (pager.Success)
        {
            cutCandidates.Add(pager.Index);
        }

        if (side.Success)
        {
            cutCandidates.Add(side.Index);
        }

        var section = cutCandidates.Count > 0
            ? remaining[..cutCandidates.Min()]
            : remaining;

        var matches = Regex.Matches(
            section,
            "<a(?=[^>]*href=[\"'](?<char>/lodestone/character/\\d+/)[\"'])(?=[^>]*class=[\"'][^\"']*entry__link[^\"']*[\"'])[^>]*>.*?" +
            "<p[^>]*class=[\"'][^\"']*entry__name[^\"']*[\"'][^>]*>(?<name>.*?)</p>.*?" +
            "<p[^>]*class=[\"'][^\"']*entry__world[^\"']*[\"'][^>]*>(?<world>.*?)</p>.*?" +
            "(?:<div[^>]*class=[\"'][^\"']*entry__chara_info__linkshell[^\"']*[\"'][^>]*>(?<rank>.*?)</div>)?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var name = NormalizeInnerText(match.Groups["name"].Value);
            var worldText = NormalizeInnerText(match.Groups["world"].Value);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(worldText))
            {
                continue;
            }

            var rankText = match.Groups["rank"].Success
                ? NormalizeInnerText(match.Groups["rank"].Value)
                : null;
            var rank = string.IsNullOrWhiteSpace(rankText) ? null : rankText;
            var characterPath = WebUtility.HtmlDecode(match.Groups["char"].Value).Trim();
            if (TryParseWorldText(worldText, out var world, out var dataCenter))
            {
                list.Add(new LodestoneCwlsMember(
                    name,
                    world,
                    dataCenter,
                    string.IsNullOrWhiteSpace(rank) ? null : rank,
                    ToAbsoluteUrl(sourceUri, characterPath)));
            }
            else
            {
                list.Add(new LodestoneCwlsMember(
                    name,
                    worldText,
                    null,
                    string.IsNullOrWhiteSpace(rank) ? null : rank,
                    ToAbsoluteUrl(sourceUri, characterPath)));
            }
        }

        return list;
    }

    private static string ToAbsoluteUrl(Uri? sourceUri, string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (sourceUri is not null && Uri.TryCreate(sourceUri, pathOrUrl, out var combined))
        {
            return combined.ToString();
        }

        return pathOrUrl;
    }

    private static string? FindClassText(string html, string className)
    {
        var match = Regex.Match(
            html,
            $"<[^>]+class=[\"'][^\"']*{Regex.Escape(className)}[^\"']*[\"'][^>]*>(?<inner>.*?)</[^>]+>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var normalized = NormalizeInnerText(match.Groups["inner"].Value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeInnerText(string html)
    {
        var stripped = Regex.Replace(html, "<.*?>", string.Empty, RegexOptions.Singleline);
        var decoded = WebUtility.HtmlDecode(stripped).Trim();
        return Regex.Replace(decoded, "\\s+", " ");
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
