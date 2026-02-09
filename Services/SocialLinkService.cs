using System;

namespace RaidDcBot.Services;

public static class SocialLinkService
{
    public static string? NormalizeX(string? input) => ExtractXHandle(input);

    public static string? NormalizeBluesky(string? input) => ExtractBlueskyHandle(input);

    public static string FormatX(string? storedValue)
    {
        var handle = ExtractXHandle(storedValue);
        if (string.IsNullOrWhiteSpace(handle))
        {
            return "未設定";
        }

        return $"[@{handle}](https://x.com/{handle})";
    }

    public static string FormatBluesky(string? storedValue)
    {
        var handle = ExtractBlueskyHandle(storedValue);
        if (string.IsNullOrWhiteSpace(handle))
        {
            return "未設定";
        }

        return $"[{handle}](https://bsky.app/profile/{handle})";
    }

    private static string? ExtractXHandle(string? input)
    {
        var raw = input?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.TrimStart('@');
        if (TryExtractHandleFromUrl(raw, out var handle))
        {
            return handle;
        }

        if (raw.Contains("x.com", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            var urlCandidate = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : $"https://{raw}";
            if (TryExtractHandleFromUrl(urlCandidate, out handle))
            {
                return handle;
            }
        }

        if (raw.Contains('/'))
        {
            raw = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? raw;
        }

        raw = raw.TrimStart('@');
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static bool TryExtractHandleFromUrl(string raw, out string handle)
    {
        handle = string.Empty;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains("x.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Contains("twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segment = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        handle = segment.TrimStart('@');
        return !string.IsNullOrWhiteSpace(handle);
    }

    private static string? ExtractBlueskyHandle(string? input)
    {
        var raw = input?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.TrimStart('@');
        if (raw.StartsWith("did:", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        if (TryExtractBlueskyFromUrl(raw, out var handle))
        {
            return handle;
        }

        if (raw.Contains("bsky.app", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("bsky.social", StringComparison.OrdinalIgnoreCase))
        {
            var urlCandidate = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw
                : $"https://{raw}";
            if (TryExtractBlueskyFromUrl(urlCandidate, out handle))
            {
                return handle;
            }
        }

        if (raw.Contains('/'))
        {
            raw = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? raw;
        }

        if (!raw.Contains('.', StringComparison.Ordinal))
        {
            raw = $"{raw}.bsky.social";
        }

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static bool TryExtractBlueskyFromUrl(string raw, out string handle)
    {
        handle = string.Empty;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Contains("bsky.app", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "profile", StringComparison.OrdinalIgnoreCase))
            {
                handle = segments[1].TrimStart('@');
                return !string.IsNullOrWhiteSpace(handle);
            }
        }

        if (uri.Host.Contains('.', StringComparison.Ordinal))
        {
            handle = uri.Host.TrimStart('@');
            return !string.IsNullOrWhiteSpace(handle);
        }

        return false;
    }
}
