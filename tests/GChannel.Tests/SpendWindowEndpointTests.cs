using Alba;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Tests.Fixtures;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace GChannel.Tests;

/// <summary>
/// The estate-wide spend views default to the last <em>complete</em> invoice month — the current month is
/// month-to-date and reads next to nothing early in the month, which is what made the Dashboard billing card
/// show DKK 0.00 while BigQuery held real spend. An explicit <c>month</c> (YYYYMM) selects any month, and
/// <c>months</c> stays a rolling window ending with the current month. Runs the real API with the BigQuery
/// export configured and billed spend seeded straight into Marten.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SpendWindowEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly string Current = InvoiceMonthKeys.Current(Now);
    private static readonly string LastComplete = InvoiceMonthKeys.LastComplete(Now);
    private static readonly string TwoMonthsBack = InvoiceMonthKeys.Rolling(3, Now)[0];

    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
        => _host = await ApiHost.StartAsync(postgres.ConnectionString, Substitute.For<IGoogleChannelClient>(), ApiHost.BillingExportConfigured);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Estate_billing_views_default_to_the_last_complete_month_not_the_month_to_date()
    {
        var (customer, link) = await SeedSpendAsync(twoMonthsBack: 7m, lastComplete: 100m, current: 1m);

        var billed = await _host.GetAsJson<CustomerBilledSummaryResult>("/api/billing/customers/spend");
        billed.ShouldNotBeNull();
        billed.Unconfigured.ShouldBeFalse();
        billed.InvoiceMonths.ShouldBe([LastComplete]);
        billed.Customers.Single(c => c.CustomerId == customer).Cost.ShouldBe(100m);

        var top = await _host.GetAsJson<TopSpendersResult>("/api/billing/spend/top?limit=100");
        top.ShouldNotBeNull();
        top.InvoiceMonths.ShouldBe([LastComplete]);
        top.Spenders.Single(s => s.CustomerId == customer).Cost.ShouldBe(100m);

        var resellers = await _host.GetAsJson<ResellerSpendResult>("/api/billing/resellers/spend");
        resellers.ShouldNotBeNull();
        resellers.InvoiceMonths.ShouldBe([LastComplete]);
        var reseller = resellers.Resellers.Single(r => r.OwningLinkId == link);
        reseller.Cost.ShouldBe(100m);
        reseller.CustomerCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_month_parameter_selects_that_invoice_month_including_the_month_to_date()
    {
        var (customer, _) = await SeedSpendAsync(twoMonthsBack: 7m, lastComplete: 100m, current: 1m);

        await AssertTopSpenderAsync($"month={Current}", [Current], customer, 1m);
        await AssertTopSpenderAsync($"month={LastComplete}", [LastComplete], customer, 100m);
        await AssertTopSpenderAsync($"month={TwoMonthsBack}", [TwoMonthsBack], customer, 7m);

        // month wins when months is sent as well.
        await AssertTopSpenderAsync($"month={TwoMonthsBack}&months=3", [TwoMonthsBack], customer, 7m);
    }

    [Fact]
    public async Task A_months_parameter_stays_a_rolling_window_ending_with_the_current_month()
    {
        var (customer, _) = await SeedSpendAsync(twoMonthsBack: 7m, lastComplete: 100m, current: 1m);

        await AssertTopSpenderAsync("months=1", [Current], customer, 1m);
        await AssertTopSpenderAsync("months=2", [LastComplete, Current], customer, 101m);
        await AssertTopSpenderAsync("months=3", [TwoMonthsBack, LastComplete, Current], customer, 108m);
    }

    [Theory]
    [InlineData("month=abc")]
    [InlineData("month=202613")]
    [InlineData("month=2026-10")]
    [InlineData("month=")]
    public async Task An_unrecognised_month_is_ignored_in_favour_of_the_default(string query)
    {
        var (customer, _) = await SeedSpendAsync(twoMonthsBack: 7m, lastComplete: 100m, current: 1m);

        await AssertTopSpenderAsync(query, [LastComplete], customer, 100m);
    }

    [Fact]
    public async Task Spend_months_lists_every_synced_month_plus_the_current_and_last_complete_ones()
    {
        // A month that fell out of every rolling window long ago is still pickable.
        await SeedSpendAsync(twoMonthsBack: 1m, lastComplete: 1m, current: 1m, extraMonth: "202401");

        var result = await _host.GetAsJson<SpendMonthsResult>("/api/billing/spend/months");

        result.ShouldNotBeNull();
        result.Unconfigured.ShouldBeFalse();
        result.CurrentMonth.ShouldBe(Current);
        result.DefaultMonth.ShouldBe(LastComplete);
        result.Months.ShouldContain("202401");
        result.Months.ShouldContain(TwoMonthsBack);
        result.Months.ShouldContain(LastComplete);
        result.Months.ShouldContain(Current);
        result.Months.Distinct().Count().ShouldBe(result.Months.Count);
        result.Months.ShouldBe(result.Months.OrderByDescending(m => m, StringComparer.Ordinal).ToList());
        result.Months[0].ShouldBe(Current);
    }

    [Fact]
    public async Task Seller_split_views_default_to_the_last_complete_month_and_honour_month()
    {
        var seller = "Acme ISV " + Guid.NewGuid().ToString("N")[..8];
        var (customer, link) = NewScope();
        await SeedSellerSpendAsync(
            SellerSpend(customer, link, LastComplete, "GOOGLE", null, 100m),
            SellerSpend(customer, link, LastComplete, "THIRD_PARTY_RESELLER", seller, 40m),
            SellerSpend(customer, link, Current, "GOOGLE", null, 1m));

        var splits = await _host.GetAsJson<CustomerSellerSplitResult>("/api/billing/spend/sellers/customers");
        splits.ShouldNotBeNull();
        splits.InvoiceMonths.ShouldBe([LastComplete]);
        var split = splits.Customers.Single(c => c.CustomerId == customer);
        split.GoogleCost.ShouldBe(100m);
        split.ThirdPartyCost.ShouldBe(40m);
        split.Sellers.ShouldBe([seller]);

        var estate = await _host.GetAsJson<SellerSpendSummary>("/api/billing/spend/sellers");
        estate.ShouldNotBeNull();
        estate.InvoiceMonths.ShouldBe([LastComplete]);
        estate.ThirdPartySellers.ShouldContain(l => l.SellerName == seller && l.Cost == 40m);

        var monthToDate = await _host.GetAsJson<CustomerSellerSplitResult>($"/api/billing/spend/sellers/customers?month={Current}");
        monthToDate.ShouldNotBeNull();
        monthToDate.InvoiceMonths.ShouldBe([Current]);
        var current = monthToDate.Customers.Single(c => c.CustomerId == customer);
        current.GoogleCost.ShouldBe(1m);
        current.ThirdPartyCost.ShouldBe(0m);
    }

    [Fact]
    public async Task A_customers_seller_split_card_keeps_its_rolling_window_including_the_current_month()
    {
        var (customer, link) = NewScope();
        await SeedSellerSpendAsync(
            SellerSpend(customer, link, TwoMonthsBack, "GOOGLE", null, 5m),
            SellerSpend(customer, link, LastComplete, "GOOGLE", null, 100m),
            SellerSpend(customer, link, Current, "GOOGLE", null, 1m));

        var card = await _host.GetAsJson<SellerSpendSummary>($"/api/billing/customers/{customer}/spend/sellers");

        card.ShouldNotBeNull();
        card.InvoiceMonths.ShouldBe([TwoMonthsBack, LastComplete, Current]);
        card.ByCategory.Single(l => l.Category == SellerCategory.Google).Cost.ShouldBe(106m);

        var monthToDate = await _host.GetAsJson<SellerSpendSummary>($"/api/billing/customers/{customer}/spend/sellers?months=1");
        monthToDate.ShouldNotBeNull();
        monthToDate.InvoiceMonths.ShouldBe([Current]);
        monthToDate.ByCategory.Single(l => l.Category == SellerCategory.Google).Cost.ShouldBe(1m);
    }

    private async Task AssertTopSpenderAsync(string query, string[] expectedMonths, string customer, decimal expectedCost)
    {
        var top = await _host.GetAsJson<TopSpendersResult>($"/api/billing/spend/top?limit=100&{query}");

        top.ShouldNotBeNull();
        top.InvoiceMonths.ShouldBe(expectedMonths);
        top.Spenders.Single(s => s.CustomerId == customer).Cost.ShouldBe(expectedCost);
    }

    // Each test gets its own customer and reseller link: the PostgreSQL container is shared across tests, so
    // assertions pick out their own rows rather than expecting exact lists.
    private static (string Customer, string Link) NewScope()
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        return ("S" + tag, "link-" + tag);
    }

    private async Task<(string Customer, string Link)> SeedSpendAsync(decimal twoMonthsBack, decimal lastComplete, decimal current, string? extraMonth = null)
    {
        var (customer, link) = NewScope();
        List<CustomerSpendRecord> rows =
        [
            Spend(customer, link, TwoMonthsBack, twoMonthsBack),
            Spend(customer, link, LastComplete, lastComplete),
            Spend(customer, link, Current, current),
        ];
        if (extraMonth is not null)
        {
            rows.Add(Spend(customer, link, extraMonth, 1m));
        }

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(rows.ToArray());
        await session.SaveChangesAsync();
        return (customer, link);
    }

    private async Task SeedSellerSpendAsync(params CustomerSpendBySellerRecord[] rows)
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(rows);
        await session.SaveChangesAsync();
    }

    private static CustomerSpendRecord Spend(string customer, string link, string month, decimal cost) => new()
    {
        Id = CustomerSpendRecord.MakeId("SUB-" + customer, month, "DKK"),
        BillingSubAccountId = "SUB-" + customer,
        InvoiceMonth = month,
        Currency = "DKK",
        CustomerId = customer,
        OwningLinkId = link,
        Cost = cost,
        GrossCost = cost,
        LastSyncedUtc = DateTimeOffset.UtcNow,
    };

    private static CustomerSpendBySellerRecord SellerSpend(string customer, string link, string month, string transactionType, string? seller, decimal cost)
    {
        var sellerKey = seller?.ToLowerInvariant() ?? string.Empty;
        return new CustomerSpendBySellerRecord
        {
            Id = CustomerSpendBySellerRecord.MakeId("SUB-" + customer, month, "DKK", transactionType, sellerKey, "svc"),
            BillingSubAccountId = "SUB-" + customer,
            InvoiceMonth = month,
            Currency = "DKK",
            TransactionType = transactionType,
            SellerKey = sellerKey,
            ServiceKey = "svc",
            SellerName = seller,
            ServiceDescription = "Service",
            CustomerId = customer,
            OwningLinkId = link,
            Cost = cost,
            GrossCost = cost,
            LastSyncedUtc = DateTimeOffset.UtcNow,
        };
    }
}
