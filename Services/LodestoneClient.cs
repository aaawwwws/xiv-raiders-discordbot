using System.Net;
using System.Globalization;
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

    public async Task<LodestoneAchievementDetailSnapshot> FetchAchievementDetailAsync(
        string characterUrl,
        int achievementId,
        CancellationToken cancellationToken = default)
    {
        if (achievementId <= 0)
        {
            return new LodestoneAchievementDetailSnapshot(LodestonePageDataStatus.Error, null, null, null);
        }

        var detailUrl = BuildCharacterAchievementDetailUrl(characterUrl, achievementId);
        if (string.IsNullOrWhiteSpace(detailUrl))
        {
            return new LodestoneAchievementDetailSnapshot(LodestonePageDataStatus.Error, null, null, null);
        }

        var html = await TryFetchOptionalHtmlAsync(detailUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return new LodestoneAchievementDetailSnapshot(LodestonePageDataStatus.Error, null, null, detailUrl);
        }

        var status = DetectPageDataStatus(html);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneAchievementDetailSnapshot(status, null, null, detailUrl);
        }

        var earnedAt = TryExtractAchievementDetailEarnedAt(html);
        var title = TryExtractAchievementDetailTitle(html);
        return new LodestoneAchievementDetailSnapshot(status, earnedAt, title, detailUrl);
    }

    public async Task<bool?> TryResolveAchievementDetailAvailableAsync(
        string characterUrl,
        int achievementId,
        CancellationToken cancellationToken = default)
    {
        var detail = await FetchAchievementDetailAsync(characterUrl, achievementId, cancellationToken);
        var status = detail.Status;
        return status switch
        {
            LodestonePageDataStatus.Available => true,
            LodestonePageDataStatus.Private => null,
            LodestonePageDataStatus.NotFound => false,
            _ => false
        };
    }

    public string? BuildCharacterAchievementDetailUrl(string characterUrl, int achievementId)
    {
        if (achievementId <= 0)
        {
            return null;
        }

        if (!Uri.TryCreate(characterUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var match = LodestoneIdRegex.Match(uri.AbsoluteUri);
        if (!match.Success)
        {
            return null;
        }

        var characterId = match.Groups[1].Value;
        return $"https://{uri.Host}/lodestone/character/{characterId}/achievement/detail/{achievementId}/";
    }

    public async Task<LodestoneCharacterExtrasSnapshot> FetchCharacterExtrasAsync(
        string characterUrl,
        CancellationToken cancellationToken = default)
    {
        var classJobUrl = BuildCharacterSubpageUrl(characterUrl, "class_job/");
        var mountUrl = BuildCharacterSubpageUrl(characterUrl, "mount/");
        var minionUrl = BuildCharacterSubpageUrl(characterUrl, "minion/");
        var achievementUrl = BuildCharacterSubpageUrl(characterUrl, "achievement/");
        if (classJobUrl is null || mountUrl is null || minionUrl is null || achievementUrl is null)
        {
            return CreateErrorExtrasSnapshot();
        }

        var classJobTask = TryFetchOptionalHtmlAsync(classJobUrl, cancellationToken);
        var mountTask = TryFetchOptionalHtmlAsync(mountUrl, cancellationToken);
        var minionTask = TryFetchOptionalHtmlAsync(minionUrl, cancellationToken);
        var achievementTask = TryFetchOptionalHtmlAsync(achievementUrl, cancellationToken);

        await Task.WhenAll(classJobTask, mountTask, minionTask, achievementTask);

        return new LodestoneCharacterExtrasSnapshot(
            ParseJobLevelsSnapshot(await classJobTask),
            ParseMinionOrMountCountSnapshot(await mountTask),
            ParseMinionOrMountCountSnapshot(await minionTask),
            ParseAchievementCountSnapshot(await achievementTask));
    }

    public async Task<LodestoneCharacterExtrasDetailsSnapshot> FetchCharacterExtrasDetailsAsync(
        string characterUrl,
        CancellationToken cancellationToken = default)
    {
        var classJobUrl = BuildCharacterSubpageUrl(characterUrl, "class_job/");
        var mountUrl = BuildCharacterSubpageUrl(characterUrl, "mount/");
        var minionUrl = BuildCharacterSubpageUrl(characterUrl, "minion/");
        var achievementUrl = BuildCharacterSubpageUrl(characterUrl, "achievement/");
        if (classJobUrl is null || mountUrl is null || minionUrl is null || achievementUrl is null)
        {
            return CreateErrorExtrasDetailsSnapshot();
        }

        var classJobTask = TryFetchOptionalHtmlAsync(classJobUrl, cancellationToken);
        var mountTask = TryFetchOptionalHtmlAsync(mountUrl, cancellationToken);
        var minionTask = TryFetchOptionalHtmlAsync(minionUrl, cancellationToken);
        var achievementTask = TryFetchOptionalHtmlAsync(achievementUrl, cancellationToken);
        await Task.WhenAll(classJobTask, mountTask, minionTask, achievementTask);

        var classJobHtml = await classJobTask;
        var mountHtml = await mountTask;
        var minionHtml = await minionTask;
        var achievementHtml = await achievementTask;

        var jobs = ParseJobLevelsSnapshot(classJobHtml);
        var mounts = await ParseCollectibleListWithTooltipsAsync(mountHtml, mountUrl, "mount", cancellationToken);
        var minions = await ParseCollectibleListWithTooltipsAsync(minionHtml, minionUrl, "minion", cancellationToken);
        var achievements = await ParseAchievementListAsync(achievementHtml, achievementUrl, cancellationToken);

        return new LodestoneCharacterExtrasDetailsSnapshot(jobs, mounts, minions, achievements);
    }

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

    private static LodestoneCharacterExtrasSnapshot CreateErrorExtrasSnapshot()
    {
        return new LodestoneCharacterExtrasSnapshot(
            new LodestoneJobLevelsSnapshot(LodestonePageDataStatus.Error, Array.Empty<LodestoneJobLevel>()),
            new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null),
            new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null),
            new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null));
    }

    private static LodestoneCharacterExtrasDetailsSnapshot CreateErrorExtrasDetailsSnapshot()
    {
        return new LodestoneCharacterExtrasDetailsSnapshot(
            new LodestoneJobLevelsSnapshot(LodestonePageDataStatus.Error, Array.Empty<LodestoneJobLevel>()),
            new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Error, null, Array.Empty<LodestoneCollectibleItem>()),
            new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Error, null, Array.Empty<LodestoneCollectibleItem>()),
            new LodestoneAchievementListSnapshot(LodestonePageDataStatus.Error, null, Array.Empty<LodestoneAchievementItem>()));
    }

    private async Task<string?> TryFetchOptionalHtmlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchHtmlAsync(url, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildCharacterSubpageUrl(string characterUrl, string suffix)
    {
        if (!Uri.TryCreate(characterUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var match = LodestoneIdRegex.Match(uri.AbsoluteUri);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value;
        var normalizedSuffix = suffix.Trim('/');
        return $"https://{uri.Host}/lodestone/character/{id}/{normalizedSuffix}/";
    }

    private static LodestoneJobLevelsSnapshot ParseJobLevelsSnapshot(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new LodestoneJobLevelsSnapshot(LodestonePageDataStatus.Error, Array.Empty<LodestoneJobLevel>());
        }

        var status = DetectPageDataStatus(html);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneJobLevelsSnapshot(status, Array.Empty<LodestoneJobLevel>());
        }

        var jobs = new List<LodestoneJobLevel>();
        var matches = Regex.Matches(
            html,
            "<li[^>]*>.*?<div[^>]*class=[\"'][^\"']*character__job__level[^\"']*[\"'][^>]*>(?<level>.*?)</div>.*?" +
            "<div[^>]*class=[\"'][^\"']*character__job__name[^\"']*[\"'][^>]*>(?<name>.*?)</div>.*?</li>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var levelText = NormalizeInnerText(match.Groups["level"].Value);
            if (!int.TryParse(levelText.Replace(",", string.Empty), out var level))
            {
                continue;
            }

            var name = NormalizeInnerText(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            jobs.Add(new LodestoneJobLevel(name, level));
        }

        return new LodestoneJobLevelsSnapshot(
            jobs.Count > 0 ? LodestonePageDataStatus.Available : LodestonePageDataStatus.Error,
            jobs);
    }

    private static LodestoneCountSnapshot ParseMinionOrMountCountSnapshot(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null);
        }

        var status = DetectPageDataStatus(html);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneCountSnapshot(status, null);
        }

        var count = TryExtractIntegerLoose(
            html,
            "<p\\s+class=[\"'][^\"']*minion__sort__total[^\"']*[\"'][^>]*>.*?<span>([\\d,]+)</span>");

        return count.HasValue
            ? new LodestoneCountSnapshot(LodestonePageDataStatus.Available, count.Value)
            : new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null);
    }

    private static LodestoneCountSnapshot ParseAchievementCountSnapshot(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null);
        }

        var status = DetectPageDataStatus(html);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneCountSnapshot(status, null);
        }

        var count = TryExtractIntegerLoose(
            html,
            "<div\\s+class=[\"'][^\"']*parts__total[^\"']*[\"'][^>]*>\\s*([\\d,]+)\\b");

        return count.HasValue
            ? new LodestoneCountSnapshot(LodestonePageDataStatus.Available, count.Value)
            : new LodestoneCountSnapshot(LodestonePageDataStatus.Error, null);
    }

    private static LodestonePageDataStatus DetectPageDataStatus(string html)
    {
        if (IsPermissionDeniedPage(html))
        {
            return LodestonePageDataStatus.Private;
        }

        if (IsLodestoneErrorPage(html))
        {
            return LodestonePageDataStatus.NotFound;
        }

        return LodestonePageDataStatus.Available;
    }

    private static DateTimeOffset? TryExtractAchievementDetailEarnedAt(string html)
    {
        var earnedAtMatches = Regex.Matches(
            html,
            "<[^>]*(?=[^>]*class=[\"'][^\"']*(?:entry__achievement__datetime|achievement__datetime|entry__achievement)[^\"']*[\"'])[^>]*>.*?ldst_strftime\\((?<epoch>\\d{9,}),\\s*'[^']*'\\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in earnedAtMatches)
        {
            if (long.TryParse(match.Groups["epoch"].Value, out var epoch))
            {
                var parsed = TryConvertUnixTime(epoch);
                if (parsed.HasValue)
                {
                    return parsed;
                }
            }
        }

        var dateTimeAttributeMatches = Regex.Matches(
            html,
            "<[^>]*(?=[^>]*class=[\"'][^\"']*(?:entry__achievement__datetime|achievement__datetime|entry__achievement)[^\"']*[\"'])(?=[^>]*datetime=[\"'](?<value>[^\"']+)[\"'])[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in dateTimeAttributeMatches)
        {
            var parsed = TryParseDateTimeOffsetFlexible(match.Groups["value"].Value);
            if (parsed.HasValue)
            {
                return parsed.Value;
            }
        }

        return null;
    }

    private static string? TryExtractAchievementDetailTitle(string html)
    {
        var title = FindElementText(html, new[]
        {
            new ElementPattern("achievement__name", "p"),
            new ElementPattern("achievement__name", "h2"),
            new ElementPattern("achievement__name", "h3"),
            new ElementPattern("entry__achievement__name", "p"),
            new ElementPattern("entry__achievement__name", "h2"),
            new ElementPattern("entry__achievement__name", "h3")
        });
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return null;
    }

    private static DateTimeOffset? TryConvertUnixTime(long epoch)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseDateTimeOffsetFlexible(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var dto))
        {
            return IsReasonableDate(dto) ? dto : null;
        }

        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.GetCultureInfo("ja-JP"),
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out dto))
        {
            return IsReasonableDate(dto) ? dto : null;
        }

        if (DateTime.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dt))
        {
            var assumed = new DateTimeOffset(
                dt.Year,
                dt.Month,
                dt.Day,
                dt.Hour,
                dt.Minute,
                dt.Second,
                TimeSpan.FromHours(9));
            return IsReasonableDate(assumed) ? assumed : null;
        }

        return null;
    }

    private static bool TryBuildDateTimeOffset(string yearRaw, string monthRaw, string dayRaw, out DateTimeOffset value)
    {
        value = default;
        if (!int.TryParse(yearRaw, out var year) ||
            !int.TryParse(monthRaw, out var month) ||
            !int.TryParse(dayRaw, out var day))
        {
            return false;
        }

        if (year is < 2013 or > 2100)
        {
            return false;
        }

        try
        {
            value = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(9));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReasonableDate(DateTimeOffset value)
    {
        return value.Year is >= 2013 and <= 2100;
    }

    private static bool IsPermissionDeniedPage(string html)
    {
        return html.Contains("You do not have permission to view this page.", StringComparison.OrdinalIgnoreCase)
            || (html.Contains("閲覧", StringComparison.Ordinal) && html.Contains("権限", StringComparison.Ordinal));
    }

    private static bool IsLodestoneErrorPage(string html)
    {
        return html.Contains("class=\"error__body\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Page not found.", StringComparison.OrdinalIgnoreCase)
            || html.Contains("ページが見つかりません", StringComparison.Ordinal)
            || html.Contains("お探しのページ", StringComparison.Ordinal);
    }

    private async Task<LodestoneCollectibleListSnapshot> ParseCollectibleListWithTooltipsAsync(
        string? html,
        string pageUrl,
        string collectibleType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Error, null, Array.Empty<LodestoneCollectibleItem>());
        }

        var status = DetectPageDataStatus(html);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneCollectibleListSnapshot(status, null, Array.Empty<LodestoneCollectibleItem>());
        }

        var count = TryExtractIntegerLoose(
            html,
            "<p\\s+class=[\"'][^\"']*minion__sort__total[^\"']*[\"'][^>]*>.*?<span>([\\d,]+)</span>");

        var baseItems = ParseCollectibleTooltipRefs(html, pageUrl);
        if (baseItems.Count == 0)
        {
            return new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Available, count, Array.Empty<LodestoneCollectibleItem>());
        }

        var resolvedItems = await ResolveCollectibleNamesAsync(baseItems, collectibleType, cancellationToken);
        return new LodestoneCollectibleListSnapshot(LodestonePageDataStatus.Available, count ?? resolvedItems.Count, resolvedItems);
    }

    private async Task<IReadOnlyList<LodestoneCollectibleItem>> ResolveCollectibleNamesAsync(
        IReadOnlyList<CollectibleTooltipRef> refs,
        string collectibleType,
        CancellationToken cancellationToken)
    {
        var results = new LodestoneCollectibleItem[refs.Count];
        using var gate = new SemaphoreSlim(6);
        var tasks = refs.Select(async (entry, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var name = entry.TooltipUrl is not null
                    ? await TryFetchCollectibleTooltipNameAsync(entry.TooltipUrl, collectibleType, cancellationToken)
                    : null;

                results[index] = new LodestoneCollectibleItem(
                    string.IsNullOrWhiteSpace(name) ? $"unknown:{entry.TooltipKey ?? (index + 1).ToString()}" : name,
                    entry.TooltipKey,
                    entry.TooltipUrl,
                    entry.IconUrl);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<string?> TryFetchCollectibleTooltipNameAsync(
        string tooltipUrl,
        string collectibleType,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await FetchHtmlAsync(tooltipUrl, cancellationToken);
            return ParseCollectibleTooltipName(html, collectibleType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseCollectibleTooltipName(string html, string collectibleType)
    {
        var className = collectibleType.Equals("mount", StringComparison.OrdinalIgnoreCase)
            ? "mount__header__label"
            : "minion__header__label";

        return FindClassText(html, className);
    }

    private static List<CollectibleTooltipRef> ParseCollectibleTooltipRefs(string html, string pageUrl)
    {
        var list = new List<CollectibleTooltipRef>();
        Uri? sourceUri = null;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsed))
        {
            sourceUri = parsed;
        }

        var matches = Regex.Matches(
            html,
            "<li[^>]*class=[\"'][^\"']*(?:mount__list_icon|minion__list_icon)[^\"']*[\"'][^>]*data-tooltip_href=[\"'](?<tooltip>[^\"']+)[\"'][^>]*>.*?" +
            "<img[^>]*class=[\"'][^\"']*character__item_icon__img[^\"']*[\"'][^>]*src=[\"'](?<icon>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var tooltipPath = WebUtility.HtmlDecode(match.Groups["tooltip"].Value).Trim();
            if (string.IsNullOrWhiteSpace(tooltipPath))
            {
                continue;
            }

            var tooltipUrl = ToAbsoluteUrl(sourceUri, tooltipPath);
            var tooltipKeyMatch = Regex.Match(tooltipPath, "/tooltip/([0-9a-f]+)", RegexOptions.IgnoreCase);
            var tooltipKey = tooltipKeyMatch.Success ? tooltipKeyMatch.Groups[1].Value : null;
            var iconUrl = match.Groups["icon"].Success
                ? WebUtility.HtmlDecode(match.Groups["icon"].Value).Trim()
                : null;

            list.Add(new CollectibleTooltipRef(tooltipKey, tooltipUrl, string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl));
        }

        return list;
    }

    private async Task<LodestoneAchievementListSnapshot> ParseAchievementListAsync(
        string? firstPageHtml,
        string achievementPageUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(firstPageHtml))
        {
            return new LodestoneAchievementListSnapshot(LodestonePageDataStatus.Error, null, Array.Empty<LodestoneAchievementItem>());
        }

        var status = DetectPageDataStatus(firstPageHtml);
        if (status != LodestonePageDataStatus.Available)
        {
            return new LodestoneAchievementListSnapshot(status, null, Array.Empty<LodestoneAchievementItem>());
        }

        var count = TryExtractIntegerLoose(
            firstPageHtml,
            "<div\\s+class=[\"'][^\"']*parts__total[^\"']*[\"'][^>]*>\\s*([\\d,]+)\\b");

        var items = new List<LodestoneAchievementItem>();
        items.AddRange(ParseAchievementItemsFromPage(firstPageHtml, achievementPageUrl));

        var totalPages = TryParseAchievementTotalPages(firstPageHtml) ?? 1;
        if (totalPages > 1)
        {
            var additionalPageUrls = Enumerable.Range(2, totalPages - 1)
                .Select(page => BuildPagedAchievementUrl(achievementPageUrl, page))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToArray();

            using var gate = new SemaphoreSlim(3);
            var pageTasks = additionalPageUrls.Select(async url =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var html = await TryFetchOptionalHtmlAsync(url!, cancellationToken);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return Array.Empty<LodestoneAchievementItem>();
                    }

                    if (DetectPageDataStatus(html) != LodestonePageDataStatus.Available)
                    {
                        return Array.Empty<LodestoneAchievementItem>();
                    }

                    return ParseAchievementItemsFromPage(html, url!).ToArray();
                }
                finally
                {
                    gate.Release();
                }
            });

            var pages = await Task.WhenAll(pageTasks);
            foreach (var pageItems in pages)
            {
                items.AddRange(pageItems);
            }
        }

        var deduped = items
            .GroupBy(x => x.AchievementId)
            .Select(group => group.OrderByDescending(x => x.EarnedAt ?? DateTimeOffset.MinValue).First())
            .OrderByDescending(x => x.EarnedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.AchievementId)
            .ToList();

        return new LodestoneAchievementListSnapshot(LodestonePageDataStatus.Available, count ?? deduped.Count, deduped);
    }

    private static List<LodestoneAchievementItem> ParseAchievementItemsFromPage(string html, string sourceUrl)
    {
        var list = new List<LodestoneAchievementItem>();
        Uri? sourceUri = null;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed))
        {
            sourceUri = parsed;
        }

        var matches = Regex.Matches(
            html,
            "<a[^>]*href=[\"'](?<detail>/lodestone/character/\\d+/achievement/detail/(?<id>\\d+)/)[\"'][^>]*class=[\"'][^\"']*entry__achievement[^\"']*[\"'][^>]*>.*?" +
            "<p[^>]*class=[\"'][^\"']*entry__activity__txt[^\"']*[\"'][^>]*>(?<text>.*?)</p>.*?</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["id"].Value, out var achievementId))
            {
                continue;
            }

            var rawText = NormalizeInnerText(match.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(rawText))
            {
                rawText = $"achievement:{achievementId}";
            }

            DateTimeOffset? earnedAt = null;
            var epochMatch = Regex.Match(
                match.Value,
                "ldst_strftime\\((?<epoch>\\d{9,}),\\s*'[^']*'\\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (epochMatch.Success && long.TryParse(epochMatch.Groups["epoch"].Value, out var epoch))
            {
                earnedAt = TryConvertUnixTime(epoch);
            }

            if (!earnedAt.HasValue)
            {
                var dateTimeAttributeMatch = Regex.Match(
                    match.Value,
                    "<[^>]*(?=[^>]*class=[\"'][^\"']*(?:entry__achievement__datetime|achievement__datetime)[^\"']*[\"'])(?=[^>]*datetime=[\"'](?<value>[^\"']+)[\"'])[^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (dateTimeAttributeMatch.Success)
                {
                    earnedAt = TryParseDateTimeOffsetFlexible(dateTimeAttributeMatch.Groups["value"].Value);
                }
            }

            var (category, title) = TryParseAchievementText(rawText);
            var detailPath = WebUtility.HtmlDecode(match.Groups["detail"].Value).Trim();
            list.Add(new LodestoneAchievementItem(
                achievementId,
                title,
                category,
                rawText,
                earnedAt,
                ToAbsoluteUrl(sourceUri, detailPath)));
        }

        return list;
    }

    private static int? TryParseAchievementTotalPages(string html)
    {
        return TryExtractIntegerLoose(
            html,
            "<li\\s+class=[\"'][^\"']*btn__pager__current[^\"']*[\"'][^>]*>\\s*Page\\s+\\d+\\s+of\\s+([\\d,]+)\\s*</li>");
    }

    private static string? BuildPagedAchievementUrl(string baseAchievementUrl, int page)
    {
        if (page <= 1 || !Uri.TryCreate(baseAchievementUrl, UriKind.Absolute, out var uri))
        {
            return baseAchievementUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Query = $"page={page}"
        };
        return builder.Uri.ToString();
    }

    private static (string? Category, string? Title) TryParseAchievementText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return (null, null);
        }

        var en = Regex.Match(
            rawText,
            "^(?<category>.+?)\\s+achievement\\s+\"(?<title>.+)\"\\s+earned!$",
            RegexOptions.IgnoreCase);
        if (en.Success)
        {
            return (
                en.Groups["category"].Value.Trim(),
                en.Groups["title"].Value.Trim());
        }

        var quoted = Regex.Match(rawText, "[\"“”「『](?<title>.+?)[\"”」』]");
        if (quoted.Success)
        {
            return (null, quoted.Groups["title"].Value.Trim());
        }

        return (null, null);
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

    private static int? TryExtractIntegerLoose(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (!int.TryParse(raw, out var value))
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

    private readonly record struct CollectibleTooltipRef(string? TooltipKey, string TooltipUrl, string? IconUrl);
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
