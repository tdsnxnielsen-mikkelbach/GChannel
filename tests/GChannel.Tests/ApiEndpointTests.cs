using Alba;
using GChannel.Core.Estate;
using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Shared.Contracts;
using GChannel.Tests.Fixtures;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine;
using Wolverine.Tracking;

namespace GChannel.Tests;

/// <summary>
/// Boots the real API (Marten on PostgreSQL, Wolverine.HTTP, handlers) once per class and drives it over HTTP.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private IAlbaHost _host = null!;
    private readonly IGoogleChannelClient _channel = Substitute.For<IGoogleChannelClient>();

    public async ValueTask InitializeAsync() => _host = await ApiHost.StartAsync(postgres.ConnectionString, _channel);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Notifications_feed_is_served_from_marten_and_reports_subscriber_state()
    {
        var result = await _host.GetAsJson<ChannelNotificationsResult>("/api/notifications");

        result.ShouldNotBeNull();
        result.SubscriberEnabled.ShouldBeFalse();
        result.Notifications.ShouldNotBeNull();
    }

    [Fact]
    public async Task Estate_customers_list_pages_the_projected_read_model()
    {
        var result = await _host.GetAsJson<PagedEstateResult<EstateCustomer>>("/api/estate/customers?page=0&pageSize=5&sort=org");

        result.ShouldNotBeNull();
        result.Items.Count.ShouldBeLessThanOrEqualTo(5);
        result.Total.ShouldBeGreaterThanOrEqualTo(result.Items.Count);
    }

    [Fact]
    public async Task Dashboard_status_reports_configuration_before_any_run()
    {
        var status = await _host.GetAsJson<DashboardRefreshStatus>("/api/dashboard/status");

        status.ShouldNotBeNull();
        status.Enabled.ShouldBeFalse();
        status.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Spend_months_report_unconfigured_while_the_billing_export_is_not_set_up()
    {
        var result = await _host.GetAsJson<SpendMonthsResult>("/api/billing/spend/months");

        result.ShouldNotBeNull();
        result.Unconfigured.ShouldBeTrue();
        result.Months.ShouldBeEmpty();
        result.DefaultMonth.ShouldBe(InvoiceMonthKeys.LastComplete(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Transfer_lookups_bind_sku_list_and_billing_account_from_the_query()
    {
        const string sku = "products/p1/skus/s1";
        const string billing = "accounts/test/billingAccounts/eur";
        _channel.QueryEligibleBillingAccountsAsync("cust-ba", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EligibleBillingAccountsResult
            {
                Groups = [new SkuBillingAccountGroup { SkuIds = ["s1"], BillingAccounts = [new EligibleBillingAccount { Name = billing, CurrencyCode = "EUR" }] }]
            });
        _channel.ListTransferableOffersAsync("cust-ba", "p1", "s1", billing, Arg.Any<CancellationToken>())
            .Returns(new TransferableOffersResult { Offers = [new TransferableOffer { OfferName = "accounts/test/offers/o1" }] });

        var accounts = await _host.GetAsJson<EligibleBillingAccountsResult>(
            $"/api/customers/cust-ba/eligible-billing-accounts?skus={Uri.EscapeDataString(sku)}");
        var offers = await _host.GetAsJson<TransferableOffersResult>(ApiRoutes.TransferableOffers("cust-ba", "p1", "s1", billing));

        accounts!.Groups.ShouldHaveSingleItem().BillingAccounts.ShouldHaveSingleItem().Name.ShouldBe(billing);
        offers!.Offers.ShouldHaveSingleItem().OfferName.ShouldBe("accounts/test/offers/o1");
        await _channel.Received().QueryEligibleBillingAccountsAsync(
            "cust-ba", Arg.Is<IReadOnlyList<string>>(s => s.SequenceEqual(new[] { sku })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submitted_transfer_is_listed_with_its_inputs_and_merged_with_googles_failure()
    {
        var customerId = $"cust-op-{Guid.NewGuid():N}";
        var operationId = Guid.NewGuid().ToString("N");
        const string billing = "accounts/test/billingAccounts/eur";
        _channel.TransferEntitlementsAsync(customerId, Arg.Any<TransferEntitlementsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EntitlementOperation { OperationName = $"operations/{operationId}", Done = false });
        _channel.GetOperationAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(new ChannelOperation
            {
                Name = $"operations/{operationId}",
                Id = operationId,
                Done = true,
                Error = "Permission denied",
                ErrorCode = 7,
                ErrorDetails = ["""{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"EXAMPLE"}"""]
            });

        await _host.Scenario(s =>
        {
            s.Post.Json(new TransferEntitlementsRequest
            {
                AuthToken = "secret-token",
                Entitlements =
                [
                    new TransferEntitlementLine
                    {
                        OfferId = "accounts/test/offers/o1",
                        OfferDisplayName = "Annual Plan (Yearly Payment)",
                        BillingAccount = billing,
                        Parameters = [new EntitlementParameterInput { Name = "num_units", IntValue = 3 }]
                    }
                ]
            }).ToUrl($"/api/customers/{customerId}/transfer-entitlements");
            s.StatusCodeShouldBe(202);
        });

        var listed = (await _host.GetAsJson<ChannelOperationsResult>(ApiRoutes.CustomerOperations(customerId)))!
            .Operations.ShouldHaveSingleItem();
        listed.Id.ShouldBe(operationId);
        listed.Done.ShouldBeFalse();
        var tracking = listed.Tracking.ShouldNotBeNull();
        tracking.Action.ShouldBe("transferEntitlements");
        tracking.CustomerId.ShouldBe(customerId);
        tracking.Details.ShouldContain(new OperationDetail("Offer", "Annual Plan (Yearly Payment) (accounts/test/offers/o1)"));
        tracking.Details.ShouldContain(new OperationDetail("Seats", "3"));
        tracking.Details.ShouldContain(new OperationDetail("Billing account", billing));
        tracking.Details.ShouldContain(new OperationDetail("Transfer auth token", "provided"));
        tracking.Details.ShouldNotContain(d => d.Value.Contains("secret-token"));

        var merged = await _host.GetAsJson<ChannelOperation>($"/api/operations/{operationId}");
        merged!.Done.ShouldBeTrue();
        merged.ErrorCode.ShouldBe(7);
        merged.ErrorDetails.ShouldHaveSingleItem().ShouldContain("ErrorInfo");
        merged.CustomerId.ShouldBe(customerId);
        merged.Tracking.ShouldNotBeNull().Action.ShouldBe("transferEntitlements");
    }

    [Fact]
    public async Task Unparseable_query_values_are_rejected_like_minimal_apis()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url("/api/estate/customers?page=abc");
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task Creating_a_customer_records_events_projects_the_read_model_and_publishes_CustomerChanged()
    {
        var customerId = "C" + Guid.NewGuid().ToString("N")[..10];
        _channel.CreateCustomerAsync(Arg.Any<SaveCustomerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Customer
            {
                Name = $"accounts/C0test/customers/{customerId}",
                Id = customerId,
                OrgDisplayName = "Acme Corp",
                Domain = "acme.example",
                CloudIdentityId = "CI" + customerId,
                CreateTime = DateTimeOffset.UtcNow,
            });

        var request = new SaveCustomerRequest
        {
            OrgDisplayName = "Acme Corp",
            Domain = "acme.example",
            Address = new CustomerAddress { RegionCode = "DK" },
        };

        Func<IMessageContext, Task> act = async _ =>
        {
            await _host.Scenario(s =>
            {
                s.Post.Json(request).ToUrl("/api/customers");
                s.StatusCodeShouldBe(201);
            });
        };
        var tracked = await _host.TrackActivity().ExecuteAndWaitAsync(act);

        var published = tracked.Sent.SingleMessage<CustomerChanged>();
        published.CustomerId.ShouldBe(customerId);
        published.Reason.ShouldBe("create");

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var record = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId));
        record.ShouldNotBeNull();
        record.OrgName.ShouldBe("Acme Corp");
        record.OwningLinkId.ShouldBeNull();
        record.IsDeleted.ShouldBeFalse();
        record.MutationCount.ShouldBe(1);

        var events = await session.Events.FetchStreamAsync(EstateStreams.Customer(customerId));
        events.Select(e => e.Data.GetType()).ShouldBe([typeof(CustomerDiscovered), typeof(CustomerMutationRequested)]);

        // The read-model-backed estate list now includes the customer.
        var estate = await _host.GetAsJson<PagedEstateResult<EstateCustomer>>($"/api/estate/customers?search={customerId}");
        estate.ShouldNotBeNull();
        estate.Items.ShouldContain(c => c.CustomerId == customerId && c.OrgName == "Acme Corp");
    }

    [Fact]
    public async Task Deleting_a_customer_marks_it_removed_and_evicts_through_the_read_model_update()
    {
        var customerId = "D" + Guid.NewGuid().ToString("N")[..10];
        _channel.CreateCustomerAsync(Arg.Any<SaveCustomerRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Customer { Name = $"accounts/C0test/customers/{customerId}", Id = customerId, OrgDisplayName = "Gone Ltd", Domain = "gone.example" });
        _channel.DeleteCustomerAsync(customerId, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _host.Scenario(s =>
        {
            s.Post.Json(new SaveCustomerRequest { OrgDisplayName = "Gone Ltd", Domain = "gone.example", Address = new CustomerAddress() }).ToUrl("/api/customers");
            s.StatusCodeShouldBe(201);
        });

        Func<IMessageContext, Task> act = async _ =>
        {
            await _host.Scenario(s =>
            {
                s.Delete.Url($"/api/customers/{customerId}");
                s.StatusCodeShouldBe(204);
            });
        };
        var tracked = await _host.TrackActivity().ExecuteAndWaitAsync(act);

        tracked.Sent.SingleMessage<CustomerReadModelUpdated>().CustomerId.ShouldBe(customerId);

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var record = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId));
        record.ShouldNotBeNull();
        record.IsDeleted.ShouldBeTrue();
    }
}
