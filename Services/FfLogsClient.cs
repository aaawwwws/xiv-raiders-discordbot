using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaidDcBot.Models;

namespace RaidDcBot.Services;

public sealed class FfLogsClient
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string GraphQlUrl = "https://www.fflogs.com/api/v2/client";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly FfLogsOptions _options;
    private readonly ILogger<FfLogsClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public FfLogsClient(IOptions<FfLogsOptions> options, ILogger<FfLogsClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        var timeoutSeconds = _options.HttpTimeoutSeconds <= 0 ? 15 : _options.HttpTimeoutSeconds;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60))
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RaidDcBot/1.0 (+FFLogs polling)");
    }

    public bool IsConfigured()
        => !string.IsNullOrWhiteSpace(_options.ClientId) &&
           !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<IReadOnlyList<RecentReport>> GetRecentReportsAsync(
        string characterName,
        string serverSlug,
        string serverRegion,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName) ||
            string.IsNullOrWhiteSpace(serverSlug) ||
            string.IsNullOrWhiteSpace(serverRegion))
        {
            return Array.Empty<RecentReport>();
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var normalizedLimit = Math.Clamp(limit <= 0 ? 10 : limit, 1, 50);
        const string query = """
query RecentReports($name: String!, $serverSlug: String!, $serverRegion: String!, $limit: Int!) {
  characterData {
    character(name: $name, serverSlug: $serverSlug, serverRegion: $serverRegion) {
      recentReports(limit: $limit) {
        data {
          code
          title
          startTime
          endTime
          zone {
            name
          }
        }
      }
    }
  }
}
""";

        var requestBody = JsonSerializer.Serialize(new
        {
            query,
            variables = new
            {
                name = characterName,
                serverSlug,
                serverRegion,
                limit = normalizedLimit
            }
        }, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FFLogs GraphQL request failed (HTTP {(int)response.StatusCode}): {TrimForError(raw)}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"FFLogs GraphQL error: {TrimForError(errors.ToString())}");
        }

        if (!TryGetProperty(root, "data", out var data) ||
            !TryGetProperty(data, "characterData", out var characterData) ||
            !TryGetProperty(characterData, "character", out var character) ||
            character.ValueKind == JsonValueKind.Null ||
            !TryGetProperty(character, "recentReports", out var recentReports) ||
            !TryGetProperty(recentReports, "data", out var reportsArray) ||
            reportsArray.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RecentReport>();
        }

        var results = new List<RecentReport>();
        foreach (var item in reportsArray.EnumerateArray())
        {
            if (!TryGetString(item, "code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var title = TryGetStringValue(item, "title");
            var startTimeMs = TryGetInt64Value(item, "startTime");
            var endTimeMs = TryGetInt64Value(item, "endTime");
            string? zoneName = null;
            if (TryGetProperty(item, "zone", out var zone) && zone.ValueKind == JsonValueKind.Object)
            {
                zoneName = TryGetStringValue(zone, "name");
            }

            results.Add(new RecentReport(
                code.Trim(),
                title?.Trim(),
                FromUnixMs(startTimeMs),
                FromUnixMs(endTimeMs),
                zoneName?.Trim()));
        }

        return results;
    }

    public async Task<IReadOnlyList<ReportFight>> GetReportFightsAsync(
        string reportCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportCode))
        {
            return Array.Empty<ReportFight>();
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        const string query = """
query ReportFights($code: String!) {
  reportData {
    report(code: $code) {
      fights {
        id
        name
        startTime
        endTime
        kill
      }
    }
  }
}
""";

        var requestBody = JsonSerializer.Serialize(new
        {
            query,
            variables = new
            {
                code = reportCode.Trim()
            }
        }, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FFLogs GraphQL request failed (HTTP {(int)response.StatusCode}): {TrimForError(raw)}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"FFLogs GraphQL error: {TrimForError(errors.ToString())}");
        }

        if (!TryGetProperty(root, "data", out var data) ||
            !TryGetProperty(data, "reportData", out var reportData) ||
            !TryGetProperty(reportData, "report", out var report) ||
            report.ValueKind == JsonValueKind.Null ||
            !TryGetProperty(report, "fights", out var fightsArray) ||
            fightsArray.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ReportFight>();
        }

        var results = new List<ReportFight>();
        foreach (var item in fightsArray.EnumerateArray())
        {
            var id = TryGetInt32Value(item, "id");
            if (!id.HasValue || id.Value <= 0)
            {
                continue;
            }

            results.Add(new ReportFight(
                Id: id.Value,
                Name: TryGetStringValue(item, "name")?.Trim(),
                StartTimeMs: TryGetInt64Value(item, "startTime"),
                EndTimeMs: TryGetInt64Value(item, "endTime"),
                Kill: TryGetBooleanValue(item, "kill")));
        }

        return results;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            if (!IsConfigured())
            {
                throw new InvalidOperationException("FFLogs client is not configured. Set FFLogs__ClientId and FFLogs__ClientSecret.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret
                })
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"FFLogs token request failed (HTTP {(int)response.StatusCode}): {TrimForError(raw)}");
            }

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!TryGetString(root, "access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("FFLogs token response did not contain access_token.");
            }

            var expiresIn = TryGetInt32Value(root, "expires_in") ?? 3600;
            _accessToken = accessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            return _accessToken;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to acquire FFLogs access token.");
            throw;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!TryGetProperty(element, name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString();
        return true;
    }

    private static string? TryGetStringValue(JsonElement element, string name)
        => TryGetString(element, name, out var value) ? value : null;

    private static long? TryGetInt64Value(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var asLong))
        {
            return asLong;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var asDouble))
        {
            return (long)asDouble;
        }

        return null;
    }

    private static int? TryGetInt32Value(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var asInt))
        {
            return asInt;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var asDouble))
        {
            return (int)asDouble;
        }

        return null;
    }

    private static bool? TryGetBooleanValue(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTimeOffset? FromUnixMs(long? unixMs)
    {
        if (!unixMs.HasValue || unixMs.Value <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(empty)";
        }

        var trimmed = raw.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400];
    }

    public sealed record RecentReport(
        string Code,
        string? Title,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        string? ZoneName);

    public sealed record ReportFight(
        int Id,
        string? Name,
        long? StartTimeMs,
        long? EndTimeMs,
        bool? Kill);
}
