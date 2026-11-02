using GChannel.Shared.Contracts;

namespace GChannel.Tests;

/// <summary>
/// The invoice-month arithmetic behind every estate-wide spend view: which month a "last month" panel shows
/// (the last <em>complete</em> one, never the still-accruing current month) and how a <c>month</c> / <c>months</c>
/// request becomes a window.
/// </summary>
public sealed class InvoiceMonthKeysTests
{
    private static readonly DateTimeOffset SecondOfNovember = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Current_and_last_complete_are_the_utc_calendar_months()
    {
        InvoiceMonthKeys.Current(SecondOfNovember).ShouldBe("202611");
        InvoiceMonthKeys.LastComplete(SecondOfNovember).ShouldBe("202610");
    }

    [Fact]
    public void Last_complete_rolls_back_across_the_year_boundary()
    {
        InvoiceMonthKeys.LastComplete(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe("202612");
        InvoiceMonthKeys.LastComplete(new DateTimeOffset(2027, 1, 31, 23, 59, 59, TimeSpan.Zero)).ShouldBe("202612");
    }

    [Fact]
    public void Months_follow_utc_whatever_the_offset_of_the_instant()
    {
        // 00:30 on 1 November in UTC+2 is still 31 October in UTC.
        var local = new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(2));

        InvoiceMonthKeys.Current(local).ShouldBe("202610");
        InvoiceMonthKeys.LastComplete(local).ShouldBe("202609");
    }

    [Fact]
    public void Rolling_window_is_oldest_first_and_ends_with_the_current_month()
    {
        InvoiceMonthKeys.Rolling(1, SecondOfNovember).ShouldBe(["202611"]);
        InvoiceMonthKeys.Rolling(3, SecondOfNovember).ShouldBe(["202609", "202610", "202611"]);
        InvoiceMonthKeys.Rolling(4, new DateTimeOffset(2027, 2, 15, 0, 0, 0, TimeSpan.Zero))
            .ShouldBe(["202611", "202612", "202701", "202702"]);
        InvoiceMonthKeys.Rolling(0, SecondOfNovember).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("202610", true, 2026, 10)]
    [InlineData(" 202601 ", true, 2026, 1)]
    [InlineData("200001", true, 2000, 1)]
    [InlineData("202612", true, 2026, 12)]
    [InlineData("202600", false, 0, 0)]
    [InlineData("202613", false, 0, 0)]
    [InlineData("199912", false, 0, 0)]
    [InlineData("2026-10", false, 0, 0)]
    [InlineData("20261", false, 0, 0)]
    [InlineData("2026100", false, 0, 0)]
    [InlineData("oct 2026", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    // Full-width digits are "digits" to char.IsDigit but int.Parse rejects them — they must not reach it.
    [InlineData("\uFF12\uFF10\uFF12\uFF16\uFF11\uFF10", false, 0, 0)]
    public void TryParse_accepts_only_six_digit_year_month_keys(string? value, bool valid, int year, int month)
    {
        InvoiceMonthKeys.TryParse(value, out var parsed).ShouldBe(valid);

        if (valid)
        {
            parsed.ShouldBe(new DateOnly(year, month, 1));
        }
    }

    [Fact]
    public void Key_round_trips_through_TryParse()
    {
        var key = InvoiceMonthKeys.Key(new DateOnly(2026, 3, 17));

        key.ShouldBe("202603");
        InvoiceMonthKeys.TryParse(key, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(new DateOnly(2026, 3, 1));
    }

    [Fact]
    public void Selectable_always_offers_the_current_and_last_complete_months_even_with_nothing_synced()
    {
        InvoiceMonthKeys.Selectable([], SecondOfNovember).ShouldBe(["202611", "202610"]);
    }

    [Fact]
    public void Selectable_merges_synced_months_newest_first_without_duplicates_or_invalid_keys()
    {
        var months = InvoiceMonthKeys.Selectable(["202608", "202610", "202412", "202608", "bogus", "", "202613"], SecondOfNovember);

        months.ShouldBe(["202611", "202610", "202608", "202412"]);
    }

    [Fact]
    public void Resolve_window_defaults_to_the_last_complete_month()
    {
        InvoiceMonthKeys.ResolveWindow(null, null, 12, SecondOfNovember).ShouldBe(["202610"]);
    }

    [Fact]
    public void Resolve_window_prefers_a_valid_month_over_months()
    {
        InvoiceMonthKeys.ResolveWindow("202607", null, 12, SecondOfNovember).ShouldBe(["202607"]);
        InvoiceMonthKeys.ResolveWindow("202607", 3, 12, SecondOfNovember).ShouldBe(["202607"]);
        InvoiceMonthKeys.ResolveWindow("202611", null, 12, SecondOfNovember).ShouldBe(["202611"]);
    }

    [Fact]
    public void Resolve_window_ignores_an_unrecognised_month()
    {
        InvoiceMonthKeys.ResolveWindow("garbage", null, 12, SecondOfNovember).ShouldBe(["202610"]);
        InvoiceMonthKeys.ResolveWindow("202613", null, 12, SecondOfNovember).ShouldBe(["202610"]);
        InvoiceMonthKeys.ResolveWindow("garbage", 2, 12, SecondOfNovember).ShouldBe(["202610", "202611"]);
    }

    [Fact]
    public void Resolve_window_keeps_months_as_a_rolling_window_clamped_to_the_allowed_range()
    {
        InvoiceMonthKeys.ResolveWindow(null, 1, 12, SecondOfNovember).ShouldBe(["202611"]);
        InvoiceMonthKeys.ResolveWindow(null, 2, 12, SecondOfNovember).ShouldBe(["202610", "202611"]);
        InvoiceMonthKeys.ResolveWindow(null, 0, 12, SecondOfNovember).ShouldBe(["202611"]);
        InvoiceMonthKeys.ResolveWindow(null, -5, 12, SecondOfNovember).ShouldBe(["202611"]);

        var capped = InvoiceMonthKeys.ResolveWindow(null, 99, 12, SecondOfNovember);
        capped.Length.ShouldBe(12);
        capped[0].ShouldBe("202512");
        capped[^1].ShouldBe("202611");
    }
}
