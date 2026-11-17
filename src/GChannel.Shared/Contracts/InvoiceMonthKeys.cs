using System.Globalization;

namespace GChannel.Shared.Contracts;

/// <summary>
/// Invoice-month keys (<c>YYYYMM</c>, the Cloud Billing export's <c>invoice.month</c>) and the window each
/// estate-wide spend view reads. Months are taken in UTC. The current month is still accruing, so its totals are
/// month-to-date; <see cref="LastComplete"/> is the most recent month with a final figure and is what the
/// "last month" views show unless a month is chosen.
/// </summary>
public static class InvoiceMonthKeys
{
    /// <summary>The <c>YYYYMM</c> key of <paramref name="month"/>.</summary>
    public static string Key(DateOnly month) => month.ToString("yyyyMM", CultureInfo.InvariantCulture);

    /// <summary>The month that contains <paramref name="now"/> (its spend is month-to-date).</summary>
    public static string Current(DateTimeOffset now) => Key(FirstOfMonth(now));

    /// <summary>The most recent fully elapsed month.</summary>
    public static string LastComplete(DateTimeOffset now) => Key(FirstOfMonth(now).AddMonths(-1));

    /// <summary>The last <paramref name="count"/> months, oldest first, ending with the current one.</summary>
    public static string[] Rolling(int count, DateTimeOffset now)
    {
        var current = FirstOfMonth(now);
        return [.. Enumerable.Range(0, Math.Max(count, 0)).Select(i => Key(current.AddMonths(i + 1 - count)))];
    }

    /// <summary>Parses a <c>YYYYMM</c> key into the first day of that month; false for anything else.</summary>
    public static bool TryParse(string? value, out DateOnly month)
    {
        month = default;
        var s = value?.Trim();
        if (s is not { Length: 6 } || !s.All(char.IsAsciiDigit))
        {
            return false;
        }

        var year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var m = int.Parse(s.AsSpan(4, 2), CultureInfo.InvariantCulture);
        if (year < 2000 || m is < 1 or > 12)
        {
            return false;
        }

        month = new DateOnly(year, m, 1);
        return true;
    }

    /// <summary>
    /// The window an estate-wide spend endpoint reads, oldest first: a valid <paramref name="month"/> wins (an
    /// unrecognised one is ignored); otherwise a rolling <paramref name="months"/> window (clamped to
    /// 1..<paramref name="maxMonths"/>, ending with the current month); otherwise the <see cref="LastComplete"/> month.
    /// </summary>
    public static string[] ResolveWindow(string? month, int? months, int maxMonths, DateTimeOffset now)
    {
        if (TryParse(month, out var explicitMonth))
        {
            return [Key(explicitMonth)];
        }

        return months is { } n ? Rolling(Math.Clamp(n, 1, maxMonths), now) : [LastComplete(now)];
    }

    /// <summary>
    /// The months a month picker offers, newest first: every valid key in <paramref name="synced"/> plus the
    /// current and last complete months, so the picker is never empty and always offers its default.
    /// </summary>
    public static string[] Selectable(IEnumerable<string> synced, DateTimeOffset now)
        => [.. synced
            .Append(Current(now))
            .Append(LastComplete(now))
            .Select(m => TryParse(m, out var parsed) ? Key(parsed) : null)
            .OfType<string>()
            .Distinct()
            .OrderByDescending(m => m, StringComparer.Ordinal)];

    private static DateOnly FirstOfMonth(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateOnly(utc.Year, utc.Month, 1);
    }
}
