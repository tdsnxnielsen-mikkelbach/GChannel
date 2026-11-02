using GChannel.Shared.Contracts;
using System.Globalization;

namespace GChannel.Web;

/// <summary>
/// Danish / European date display for the whole UI: dates as <c>dd-MM-yyyy</c>, timestamps as
/// <c>dd-MM-yyyy HH:mm</c> (24-hour clock). Timestamps are shown in UTC whatever the host's time zone.
/// <see cref="Culture"/> is the app's default culture (set in <c>Program.cs</c>), so standard formats and Radzen
/// components follow the same patterns. It is a copy of the invariant culture, so numbers keep their invariant
/// format (1,234.56), month names stay English and no ICU data is needed in the container.
/// </summary>
public static class DisplayFormat
{
    public const string DatePattern = "dd-MM-yyyy";
    public const string TimestampPattern = "dd-MM-yyyy HH:mm";
    public const string MonthPattern = "MMM yyyy";
    private const string Missing = "—";

    public static CultureInfo Culture { get; } = CreateCulture();

    public static string Date(DateTimeOffset? value) => value is { } v ? Format(v.UtcDateTime, DatePattern) : Missing;

    public static string Date(DateTime? value) => value is { } v ? Format(ToUtc(v), DatePattern) : Missing;

    public static string Date(DateOnly? value) => value?.ToString(DatePattern, CultureInfo.InvariantCulture) ?? Missing;

    public static string Timestamp(DateTimeOffset? value) => value is { } v ? Format(v.UtcDateTime, TimestampPattern) : Missing;

    public static string Timestamp(DateTime? value) => value is { } v ? Format(ToUtc(v), TimestampPattern) : Missing;

    /// <summary>An invoice month key (<c>YYYYMM</c>) as <c>MMM yyyy</c>, e.g. "Oct 2026"; an unrecognised key is shown as it is.</summary>
    public static string InvoiceMonth(string? yyyymm) =>
        InvoiceMonthKeys.TryParse(yyyymm, out var month) ? month.ToString(MonthPattern, CultureInfo.InvariantCulture)
        : string.IsNullOrWhiteSpace(yyyymm) ? Missing : yyyymm;

    /// <summary>A window of invoice months (oldest first): "Oct 2026" for one month, "Sep 2026 – Nov 2026" for a range.</summary>
    public static string InvoiceWindow(IReadOnlyList<string>? months) => months switch
    {
        null or { Count: 0 } => Missing,
        { Count: 1 } => InvoiceMonth(months[0]),
        _ => $"{InvoiceMonth(months[0])} – {InvoiceMonth(months[^1])}"
    };

    private static string Format(DateTime value, string pattern) => value.ToString(pattern, CultureInfo.InvariantCulture);

    // Unspecified (e.g. date-only values) and UTC values are shown as they are; only local times are converted.
    private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;

    private static CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var format = culture.DateTimeFormat;
        format.DateSeparator = "-";
        format.ShortDatePattern = DatePattern;
        format.LongDatePattern = DatePattern;
        format.ShortTimePattern = "HH:mm";
        format.LongTimePattern = "HH:mm:ss";
        format.FullDateTimePattern = "dd-MM-yyyy HH:mm:ss";
        format.MonthDayPattern = "dd-MM";
        format.FirstDayOfWeek = DayOfWeek.Monday;
        format.CalendarWeekRule = CalendarWeekRule.FirstFourDayWeek;
        return CultureInfo.ReadOnly(culture);
    }
}
