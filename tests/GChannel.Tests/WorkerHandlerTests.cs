using GChannel.Core.Estate;
using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Tests.Fixtures;
using GChannel.Worker.Sagas;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace GChannel.Tests;

/// <summary>Worker message handlers and the operation saga, executed through Wolverine's tracked sessions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerHandlerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await WorkerHost.StartAsync(postgres.ConnectionString);
        _store = _host.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Channel_notification_is_recorded_in_the_feed_and_on_the_customer_stream()
    {
        var customerId = "N" + Guid.NewGuid().ToString("N")[..8];
        var observer = _host.Services.GetRequiredService<EstateObserver>();
        await using (var session = _store.LightweightSession())
        {
            await observer.ObserveCustomerAsync(session,
                new Customer { Name = $"accounts/C0test/customers/{customerId}", Id = customerId, OrgDisplayName = "Notified Co", Domain = "notified.example" },
                null, ObservationSource.Sync, DateTimeOffset.UtcNow, CancellationToken.None);
            await session.SaveChangesAsync();
        }

        var messageId = "pubsub-" + Guid.NewGuid().ToString("N");
        var notification = new ChannelNotification
        {
            Kind = "Entitlement",
            EventType = "LICENSE_ASSIGNMENT_CHANGED",
            ResourceName = $"accounts/C0test/customers/{customerId}/entitlements/ent-1",
            CustomerId = customerId,
            EntitlementId = "ent-1",
            MessageId = messageId,
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        var tracked = await _host.InvokeMessageAndWaitAsync(new ChannelNotificationArrived(notification));

        // No service-account credential in tests → the read-model sync is disabled → no re-projection cascade.
        tracked.Sent.MessagesOf<ProjectCustomer>().ShouldBeEmpty();

        await using var query = _store.QuerySession();
        var record = await query.LoadAsync<ChannelNotificationRecord>(messageId);
        record.ShouldNotBeNull();
        record.CustomerId.ShouldBe(customerId);
        record.Notification.EventType.ShouldBe("LICENSE_ASSIGNMENT_CHANGED");

        var events = await query.Events.FetchStreamAsync(EstateStreams.Customer(customerId));
        events.Select(e => e.Data).OfType<ChannelNotificationReceived>().Single().MessageId.ShouldBe(messageId);
    }

    [Fact]
    public async Task Stale_recurring_tick_is_ignored_and_disabled_job_ends_its_chain()
    {
        var current = Guid.NewGuid();
        await using (var session = _store.LightweightSession())
        {
            session.Store(new RecurringJobState { Id = RecurringJobs.EstateSync, RunId = current, NextRunUtc = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync();
        }

        // A tick from a superseded chain changes nothing.
        var stale = await _host.InvokeMessageAndWaitAsync(new RunEstateSyncCycle(Guid.NewGuid()));
        stale.Sent.MessagesOf<RunEstateSyncCycle>().ShouldBeEmpty();
        await using (var query = _store.QuerySession())
        {
            var state = await query.LoadAsync<RecurringJobState>(RecurringJobs.EstateSync);
            state!.RunId.ShouldBe(current);
            state.NextRunUtc.ShouldNotBeNull();
        }

        // The current tick finds the job disabled (no service account configured) and ends the chain.
        var tick = await _host.InvokeMessageAndWaitAsync(new RunEstateSyncCycle(current));
        tick.Sent.MessagesOf<RunEstateSyncCycle>().ShouldBeEmpty();
        await using (var query = _store.QuerySession())
        {
            var state = await query.LoadAsync<RecurringJobState>(RecurringJobs.EstateSync);
            state!.NextRunUtc.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Submitted_operation_starts_a_saga_that_schedules_its_first_poll()
    {
        var operationName = "operations/" + Guid.NewGuid().ToString("N");
        var tracked = await _host.InvokeMessageAndWaitAsync(
            new OperationSubmitted(operationName, "C-saga", "ent-saga", "changeParameters", "tester@example.com", DateTimeOffset.UtcNow));

        await using var query = _store.QuerySession();
        var saga = await query.LoadAsync<ChannelOperationSaga>(operationName);
        saga.ShouldNotBeNull();
        saga.CustomerId.ShouldBe("C-saga");
        saga.EntitlementId.ShouldBe("ent-saga");
        saga.Action.ShouldBe("changeParameters");

        // The first poll is a saga timeout: Wolverine persists it as a durable scheduled envelope (+5 s).
        var scheduled = tracked.AllRecordsInOrder()
            .Where(r => r.MessageEventType == MessageEventType.Sent && r.Envelope.ScheduledTime is not null)
            .ToList();
        scheduled.ShouldNotBeEmpty();
        scheduled[0].Envelope.ScheduledTime!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(2));
    }
}
