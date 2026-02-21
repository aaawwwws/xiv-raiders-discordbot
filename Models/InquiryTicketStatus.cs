namespace RaidDcBot.Models;

public static class InquiryTicketStatus
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string OnHold = "ON_HOLD";
    public const string Resolved = "RESOLVED";
    public const string Closed = "CLOSED";

    private static readonly string[] SelectableStatuses = { Open, InProgress, OnHold, Resolved };

    public static bool IsClosed(string? status)
        => string.Equals(status, Closed, StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string? status)
    {
        return status is not null && (
            string.Equals(status, Open, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, InProgress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, OnHold, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, Resolved, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, Closed, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSelectable(string? status)
    {
        if (status is null)
        {
            return false;
        }

        return SelectableStatuses.Any(value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Open;
        }

        var input = status.Trim();
        if (string.Equals(input, Open, StringComparison.OrdinalIgnoreCase)) return Open;
        if (string.Equals(input, InProgress, StringComparison.OrdinalIgnoreCase)) return InProgress;
        if (string.Equals(input, OnHold, StringComparison.OrdinalIgnoreCase)) return OnHold;
        if (string.Equals(input, Resolved, StringComparison.OrdinalIgnoreCase)) return Resolved;
        if (string.Equals(input, Closed, StringComparison.OrdinalIgnoreCase)) return Closed;
        return Open;
    }

    public static string ToLabel(string? status)
    {
        return IsClosed(status) ? "クローズ" : "オープン";
    }
}
