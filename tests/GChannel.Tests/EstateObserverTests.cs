using GChannel.Core.Estate;
using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Tests.Fixtures;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace GChannel.Tests;

/// <summary>
/// Drives the <see cref="EstateObserver"/> directly against a Marten store on the Testcontainers PostgreSQL:
/// discovery, diff-only change events, removals/restores and the entitlement sync with catalog enrichment.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EstateObserverTests(PostgresFixture postgres) : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;
    private EstateObserver _observer = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await WorkerHost.StartAsync(postgres.ConnectionString);
        _store = _host.Services.GetRequiredService<IDocumentStore>();
        _observer = _host.Services.GetRequiredService<EstateObserver>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static Customer MakeCustomer(string id, string org, string domain, string? partner = null) => new()
    {
        Name = $"accounts/C0test/customers/{id}",
        Id = id,
        OrgDisplayName = org,
        Domain = domain,
        CloudIdentityId = "CI-" + id,
        ChannelPartnerId = partner,
        CreateTime = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task List_pass_discovers_changes_and_removes_customers_with_diff_only_events()
    {
        var scope = "L" + Guid.NewGuid().ToString("N")[..6];
        var a = scope + "A";
        var b = scope + "B";
        var now = DateTimeOffset.UtcNow;

        // Pass 1: two customers under one (synthetic) owning link.
        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerListAsync(session, [MakeCustomer(a, "Alpha", "alpha.example"), MakeCustomer(b, "Beta", "beta.example")], scope, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        // Pass 2: identical data — must append nothing.
        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerListAsync(session, [MakeCustomer(a, "Alpha", "alpha.example"), MakeCustomer(b, "Beta", "beta.example")], scope, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        // Pass 3: Alpha renamed, Beta gone.
        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerListAsync(session, [MakeCustomer(a, "Alpha Renamed", "alpha.example")], scope, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        await using var query = _store.QuerySession();
        var alpha = await query.LoadAsync<CustomerRecord>(EstateStreams.Customer(a));
        alpha.ShouldNotBeNull();
        alpha.OrgName.ShouldBe("Alpha Renamed");
        alpha.OwningLinkId.ShouldBe(scope);
        alpha.IsDeleted.ShouldBeFalse();
        (await query.Events.FetchStreamAsync(EstateStreams.Customer(a))).Select(e => e.Data.GetType())
            .ShouldBe([typeof(CustomerDiscovered), typeof(CustomerDetailsChanged)]);

        var beta = await query.LoadAsync<CustomerRecord>(EstateStreams.Customer(b));
        beta.ShouldNotBeNull();
        beta.IsDeleted.ShouldBeTrue();
        (await query.Events.FetchStreamAsync(EstateStreams.Customer(b))).Select(e => e.Data.GetType())
            .ShouldBe([typeof(CustomerDiscovered), typeof(CustomerRemovedObserved)]);

        var betaState = await query.LoadAsync<CustomerSyncState>(b);
        betaState.ShouldNotBeNull();
        betaState.IsDeleted.ShouldBeTrue();
        var alphaState = await query.LoadAsync<CustomerSyncState>(a);
        alphaState.ShouldNotBeNull();
        alphaState.LastSyncedUtc.ShouldBe(DateTimeOffset.MinValue); // entitlements not synced yet → head of the rotation
    }

    [Fact]
    public async Task Customer_moving_between_owners_is_rehomed_not_duplicated()
    {
        var id = "M" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;

        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerAsync(session, MakeCustomer(id, "Mover", "mover.example"), "link-1-" + id, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }
        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerListAsync(session, [MakeCustomer(id, "Mover", "mover.example")], "link-2-" + id, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        await using var query = _store.QuerySession();
        var record = await query.LoadAsync<CustomerRecord>(EstateStreams.Customer(id));
        record.ShouldNotBeNull();
        record.OwningLinkId.ShouldBe("link-2-" + id);
        (await query.Events.FetchStreamAsync(EstateStreams.Customer(id))).Select(e => e.Data.GetType())
            .ShouldBe([typeof(CustomerDiscovered), typeof(CustomerOwnershipChanged)]);
    }

    [Fact]
    public async Task Direct_list_attributes_partner_managed_customers_to_their_link_without_flapping()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var direct = "D" + tag;
        var viaPartner = "P" + tag;
        var link = "link-" + tag;
        var now = DateTimeOffset.UtcNow;

        // The account-level list returns both the direct customer and the partner-managed one (which carries
        // ChannelPartnerId); the partner's own list then returns the managed customer again.
        async Task RunCycleAsync()
        {
            await using (var session = _store.LightweightSession())
            {
                await _observer.ObserveCustomerListAsync(session,
                    [MakeCustomer(direct, "Direct", "direct.example"), MakeCustomer(viaPartner, "Managed", "managed.example", partner: link)],
                    owningLinkId: null, ObservationSource.Sync, now, CancellationToken.None);
                await session.SaveChangesAsync();
            }
            await using (var session = _store.LightweightSession())
            {
                await _observer.ObserveCustomerListAsync(session, [MakeCustomer(viaPartner, "Managed", "managed.example", partner: link)], link, ObservationSource.Sync, now, CancellationToken.None);
                await session.SaveChangesAsync();
            }
        }

        await RunCycleAsync();
        await RunCycleAsync();

        await using var query = _store.QuerySession();
        var managed = await query.LoadAsync<CustomerRecord>(EstateStreams.Customer(viaPartner));
        managed.ShouldNotBeNull();
        managed.OwningLinkId.ShouldBe(link);
        managed.IsDeleted.ShouldBeFalse();
        (await query.Events.FetchStreamAsync(EstateStreams.Customer(viaPartner))).Select(e => e.Data.GetType())
            .ShouldBe([typeof(CustomerDiscovered)]);

        var directRecord = await query.LoadAsync<CustomerRecord>(EstateStreams.Customer(direct));
        directRecord.ShouldNotBeNull();
        directRecord.OwningLinkId.ShouldBeNull();
        directRecord.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Entitlement_sync_projects_pricing_and_names_then_appends_only_the_seat_change()
    {
        var customerId = "E" + Guid.NewGuid().ToString("N")[..8];
        var entitlementId = "ent-" + customerId;
        var now = DateTimeOffset.UtcNow;

        Entitlement MakeEntitlement(long seats) => new()
        {
            Name = $"accounts/C0test/customers/{customerId}/entitlements/{entitlementId}",
            Id = entitlementId,
            OfferId = "offer-1",
            SkuId = "sku-1",
            ProductId = "prod-1",
            ProvisioningState = "ACTIVE",
            CreateTime = now.AddMonths(-3),
            Commitment = new EntitlementCommitment { StartTime = now.AddMonths(-3), EndTime = now.AddMonths(9), RenewalEnabled = true },
            Parameters = [new EntitlementParameter { Name = "num_units", Value = seats.ToString() }],
        };

        var client = Substitute.For<IGoogleChannelClient>();
        client.ListCustomerRepricingConfigsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(new RepricingConfigsResult());
        client.ListEntitlementsForSyncAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(new EntitlementsResult { Entitlements = [MakeEntitlement(10)] }, new EntitlementsResult { Entitlements = [MakeEntitlement(12)] });

        var catalog = new EstateObserver.OfferCatalog(
            new Dictionary<string, (decimal, string)> { ["offer-1"] = (144m, "EUR") },   // annual price → 12/month
            new Dictionary<string, string> { ["offer-1"] = "Business Starter (Annual)" },
            new Dictionary<string, string> { ["sku-1"] = "Business Starter" },
            new Dictionary<string, (string?, string?)> { ["offer-1"] = ("COMMITMENT", "Annual") },
            new Dictionary<string, string> { ["prod-1"] = "Google Workspace" });

        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerAsync(session, MakeCustomer(customerId, "Ent Co", "ent.example"), null, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        var diag = new EstateObserver.PricingDiagnostics();
        await using (var session = _store.LightweightSession())
        {
            await _observer.SyncCustomerEntitlementsAsync(session, client, customerId, null, catalog, new Dictionary<string, string>(), 5m, diag, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        await using (var query = _store.QuerySession())
        {
            var record = await query.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(entitlementId));
            record.ShouldNotBeNull();
            record.CustomerId.ShouldBe(customerId);
            record.Seats.ShouldBe(10);
            record.BillableSeats.ShouldBe(10);
            record.UnitPrice.ShouldBe(12m);
            record.Currency.ShouldBe("EUR");
            record.RepricingPercent.ShouldBe(5m);
            record.OfferName.ShouldBe("Business Starter (Annual)");
            record.SkuName.ShouldBe("Business Starter");
            record.ProductName.ShouldBe("Google Workspace");
            record.PlanDescription.ShouldBe("Annual Plan (Annual Payment)");
            record.RenewalEnabled.ShouldBe(true);
            diag.ActivePriced.ShouldBe(1);

            var state = await query.LoadAsync<CustomerSyncState>(customerId);
            state.ShouldNotBeNull();
            state.LastSyncedUtc.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        }

        // Second pass: only the seat count changed.
        await using (var session = _store.LightweightSession())
        {
            await _observer.SyncCustomerEntitlementsAsync(session, client, customerId, null, catalog, new Dictionary<string, string>(), 5m, new EstateObserver.PricingDiagnostics(), ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        await using (var query = _store.QuerySession())
        {
            var record = await query.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(entitlementId));
            record.ShouldNotBeNull();
            record.Seats.ShouldBe(12);
            var events = await query.Events.FetchStreamAsync(EstateStreams.Entitlement(entitlementId));
            events.Select(e => e.Data.GetType()).ShouldBe(
            [
                typeof(EntitlementDiscovered),
                typeof(EntitlementCatalogResolved),
                typeof(EntitlementPriced),
                typeof(EntitlementSeatsChanged),
            ]);
            var seatChange = events.Select(e => e.Data).OfType<EntitlementSeatsChanged>().Single();
            seatChange.PreviousSeats.ShouldBe(10);
            seatChange.Seats.ShouldBe(12);
        }
    }

    [Fact]
    public async Task Rate_limited_entitlement_sync_leaves_the_sync_stamp_untouched()
    {
        var customerId = "R" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var client = Substitute.For<IGoogleChannelClient>();
        client.ListEntitlementsForSyncAsync(customerId, Arg.Any<CancellationToken>())
            .Returns<EntitlementsResult>(_ => throw new Google.GoogleApiException("cloudchannel", "quota") { HttpStatusCode = System.Net.HttpStatusCode.TooManyRequests });

        await using (var session = _store.LightweightSession())
        {
            await _observer.ObserveCustomerAsync(session, MakeCustomer(customerId, "Throttled", "throttled.example"), null, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        var diag = new EstateObserver.PricingDiagnostics();
        await using (var session = _store.LightweightSession())
        {
            await _observer.SyncCustomerEntitlementsAsync(session, client, customerId, null, EstateObserver.EmptyOfferCatalog, new Dictionary<string, string>(), 0m, diag, ObservationSource.Sync, now, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        diag.RateLimited.ShouldBeTrue();
        await using var query = _store.QuerySession();
        var state = await query.LoadAsync<CustomerSyncState>(customerId);
        state.ShouldNotBeNull();
        state.LastSyncedUtc.ShouldBe(DateTimeOffset.MinValue);
    }
}
