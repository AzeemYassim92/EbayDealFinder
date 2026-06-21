using System.Globalization;
using System.Text.RegularExpressions;

namespace P2W.DealFinder.Application.Grading;

public enum AuctionTimeParseSource
{
    StructuredEndDate,
    EmbeddedJson,
    HtmlTimeText,
    Unknown
}

public enum AuctionTimeParseStatus
{
    Parsed,
    Missing,
    Invalid,
    Ended
}

public sealed record AuctionTimeParseResult(
    AuctionTimeParseStatus Status,
    AuctionTimeParseSource Source,
    string? RawText,
    DateTimeOffset? AuctionEndUtc,
    decimal? MinutesRemainingAtCapture,
    decimal? HoursRemainingAtCapture,
    string? ErrorMessage);

public static class AuctionTimeParser
{
    public static AuctionTimeParseResult Missing(string? rawText = null)
        => new(AuctionTimeParseStatus.Missing, AuctionTimeParseSource.Unknown, rawText, null, null, null, "Auction end time was not found.");

    public static AuctionTimeParseResult FromRelativeText(string? value, DateTimeOffset capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(value)) return Missing(value);

        var text = Normalize(value);
        if (Regex.IsMatch(text, @"\b(ended|sold|complete|closed)\b", RegexOptions.IgnoreCase))
        {
            return new AuctionTimeParseResult(AuctionTimeParseStatus.Ended, AuctionTimeParseSource.HtmlTimeText, value, null, 0m, 0m, "Auction already ended.");
        }

        decimal totalMinutes = 0;
        foreach (Match match in Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(d|day|days|h|hr|hrs|hour|hours|m|min|mins|minute|minutes|s|sec|secs|second|seconds)\b", RegexOptions.IgnoreCase))
        {
            var amount = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups[2].Value.ToLowerInvariant();
            totalMinutes += unit switch
            {
                "d" or "day" or "days" => amount * 1440m,
                "h" or "hr" or "hrs" or "hour" or "hours" => amount * 60m,
                "m" or "min" or "mins" or "minute" or "minutes" => amount,
                "s" or "sec" or "secs" or "second" or "seconds" => amount / 60m,
                _ => 0m
            };
        }

        if (totalMinutes <= 0)
        {
            return new AuctionTimeParseResult(AuctionTimeParseStatus.Invalid, AuctionTimeParseSource.HtmlTimeText, value, null, null, null, "Auction time text could not be parsed.");
        }

        var endUtc = capturedAtUtc.AddMinutes((double)totalMinutes);
        return new AuctionTimeParseResult(
            AuctionTimeParseStatus.Parsed,
            AuctionTimeParseSource.HtmlTimeText,
            value,
            endUtc,
            Math.Round(totalMinutes, 2),
            Math.Round(totalMinutes / 60m, 2),
            null);
    }

    public static AuctionTimeParseResult FromEndUtc(DateTimeOffset? endUtc, DateTimeOffset capturedAtUtc, AuctionTimeParseSource source)
    {
        if (endUtc is null) return Missing();
        var remaining = endUtc.Value - capturedAtUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return new AuctionTimeParseResult(AuctionTimeParseStatus.Ended, source, null, endUtc.Value, 0m, 0m, "Auction already ended.");
        }

        var minutes = (decimal)remaining.TotalMinutes;
        return new AuctionTimeParseResult(
            AuctionTimeParseStatus.Parsed,
            source,
            null,
            endUtc.Value,
            Math.Round(minutes, 2),
            Math.Round(minutes / 60m, 2),
            null);
    }

    public static bool IsInsideWindow(AuctionTimeParseResult time, DateTimeOffset capturedAtUtc, int endingWithinHours)
    {
        if (time.Status != AuctionTimeParseStatus.Parsed || time.AuctionEndUtc is null) return false;
        var remaining = time.AuctionEndUtc.Value - capturedAtUtc;
        return remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(Math.Clamp(endingWithinHours, 1, 24));
    }

    private static string Normalize(string value)
    {
        var text = value.Replace("\u00a0", " ", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
