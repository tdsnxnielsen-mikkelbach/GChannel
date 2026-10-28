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
