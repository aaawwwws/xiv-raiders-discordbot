namespace RaidDcBot.Models;

public static class LotteryStatus
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
    public const string Drawn = "DRAWN";
    public const string Canceled = "CANCELED";

    public static bool IsOpen(string? status)
        => string.Equals(status, Open, StringComparison.OrdinalIgnoreCase);

    public static bool IsClosed(string? status)
        => string.Equals(status, Closed, StringComparison.OrdinalIgnoreCase);

    public static bool IsDrawn(string? status)
        => string.Equals(status, Drawn, StringComparison.OrdinalIgnoreCase);

    public static bool IsCanceled(string? status)
        => string.Equals(status, Canceled, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Open;
        }

        var input = status.Trim();
        if (string.Equals(input, Open, StringComparison.OrdinalIgnoreCase)) return Open;
        if (string.Equals(input, Closed, StringComparison.OrdinalIgnoreCase)) return Closed;
        if (string.Equals(input, Drawn, StringComparison.OrdinalIgnoreCase)) return Drawn;
        if (string.Equals(input, Canceled, StringComparison.OrdinalIgnoreCase)) return Canceled;
        return Open;
    }
}
