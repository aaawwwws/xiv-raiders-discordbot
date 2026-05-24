using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RaidDcBot.Services;

public sealed class TokenService
{
    private static readonly Regex TokenRegex = new(@"RDC-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[6];
        RandomNumberGenerator.Fill(buffer);

        var hex = Convert.ToHexString(buffer);
        return $"RDC-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    public string HashToken(string token)
    {
        var normalized = NormalizeToken(token);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    public bool MatchesStoredToken(string html, string storedToken)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(storedToken))
        {
            return false;
        }

        if (storedToken.StartsWith("RDC-", StringComparison.OrdinalIgnoreCase))
        {
            var found = html.Contains(storedToken, StringComparison.OrdinalIgnoreCase);
            if (!found)
            {
                Console.WriteLine($"[TokenDebug] Token '{storedToken}' not found in HTML. HTML length={html.Length}, contains_self_intro={html.Contains("自己紹介", StringComparison.Ordinal)}");
            }
            return found;
        }

        if (!TryParseHex(storedToken, out var expectedHash))
        {
            Console.WriteLine($"[TokenDebug] Stored token is not a valid hash. Length={storedToken.Length}, startsWith={storedToken[..Math.Min(10, storedToken.Length)]}");
            return false;
        }

        var matches = TokenRegex.Matches(html);
        if (matches.Count == 0)
        {
            // HTMLタグを除去してテキスト抽出後にも探す（<wbr>などで分割されている可能性）
            var stripped = Regex.Replace(html, "<.*?>", string.Empty, RegexOptions.Singleline);
            matches = TokenRegex.Matches(stripped);
            if (matches.Count == 0)
            {
                Console.WriteLine($"[TokenDebug] No token pattern found in HTML. HTML length={html.Length}, contains_self_intro={html.Contains("自己紹介", StringComparison.Ordinal)}, stripped_length={stripped.Length}");
                return false;
            }
        }

        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var normalized = NormalizeToken(match.Value);
            var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            if (CryptographicOperations.FixedTimeEquals(candidate, expectedHash))
            {
                return true;
            }
        }

        Console.WriteLine($"[TokenDebug] Hash mismatch. Found {matches.Count} token pattern(s) but none matched stored hash. HTML length={html.Length}, contains_self_intro={html.Contains("自己紹介", StringComparison.Ordinal)}");
        return false;
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var trimmed = value.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(trimmed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeToken(string token)
    {
        return token.Trim().ToUpperInvariant();
    }
}
