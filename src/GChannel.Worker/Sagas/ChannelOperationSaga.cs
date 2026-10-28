using GChannel.Core.Estate;
using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Messaging;
using GChannel.Worker.Infrastructure;
using Marten;
using Wolverine;

namespace GChannel.Worker.Sagas;

/// <summary>
/// Tracks a Google Channel long-running operation the app submitted (entitlement purchase / change / state
/// change, transfer, Cloud Identity provisioning) until it completes: polls <c>operations.get</c> with
/// back-off, records the outcome on the affected entitlement / customer event stream and triggers a
/// re-projection of the customer so the read model reflects the finished change. Persisted by Marten as
/// Wolverine saga storage; keyed by the operation name.
/// </summary>
public sealed class ChannelOperationSaga : Saga
{
    private static readonly TimeSpan MaxTrackingWindow = TimeSpan.FromHours(2);

    public string Id { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string? EntitlementId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public int Attempts { get; set; }

    public static (ChannelOperationSaga, CheckChannelOperation) Start(OperationSubmitted message)
    {
        var saga = new ChannelOperationSaga
        {
            Id = message.OperationName,
            CustomerId = message.CustomerId,
            EntitlementId = message.EntitlementId,
            Action = message.Action,
            RequestedBy = message.RequestedBy,
            SubmittedAt = message.SubmittedAt,
        };
        return (saga, new CheckChannelOperation(message.OperationName, TimeSpan.FromSeconds(5)));
    }

    public async Task<OutgoingMessages> Handle(
        CheckChannelOperation message,
        ServiceAccountChannelClientProvider clients,
        EstateObserver observer,
        IDocumentSession session,
        ILogger<ChannelOperationSaga> logger,
        CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var client = clients.Client;
        if (client is null)
        {
            logger.LogInformation("Cannot track operation {Operation}: no service-account credential configured.", Id);
            MarkCompleted();
            return outgoing;
        }

        Attempts++;
        var operationId = Id[(Id.LastIndexOf('/') + 1)..];
        Shared.Contracts.ChannelOperation operation;
        try
        {
            operation = await client.GetOperationAsync(operationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (DateTimeOffset.UtcNow - SubmittedAt > MaxTrackingWindow)
            {
                logger.LogWarning(ex, "Giving up tracking operation {Operation} after {Attempts} attempts.", Id, Attempts);
                await RecordOutcomeAsync(session, observer, succeeded: false, $"Tracking abandoned: {ex.Message}", ct);
                MarkCompleted();
                return outgoing;
            }
            logger.LogDebug(ex, "Polling operation {Operation} failed (attempt {Attempts}); retrying.", Id, Attempts);
            outgoing.Add(new CheckChannelOperation(Id, Backoff()));
            return outgoing;
        }

        if (!operation.Done)
        {
            if (DateTimeOffset.UtcNow - SubmittedAt > MaxTrackingWindow)
            {
                await RecordOutcomeAsync(session, observer, succeeded: false, "Operation did not complete within the tracking window.", ct);
                MarkCompleted();
                return outgoing;
            }
            outgoing.Add(new CheckChannelOperation(Id, Backoff()));
            return outgoing;
        }

        var succeeded = string.IsNullOrEmpty(operation.Error);
        await RecordOutcomeAsync(session, observer, succeeded, operation.Error, ct);
        outgoing.Add(new ProjectCustomer(CustomerId, $"operation {Action} {(succeeded ? "completed" : "failed")}"));
        logger.LogInformation("Operation {Operation} ({Action}) {Outcome} after {Attempts} poll(s).", Id, Action, succeeded ? "completed" : "failed", Attempts);
        MarkCompleted();
        return outgoing;
    }

    private TimeSpan Backoff() => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(1.5, Math.Min(Attempts, 8))));

    private async Task RecordOutcomeAsync(IDocumentSession session, EstateObserver observer, bool succeeded, string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(EntitlementId)
            && await session.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(EntitlementId), ct) is { } entitlement)
        {
            session.Events.Append(entitlement.Id, succeeded
                ? new EntitlementChangeCompleted(EntitlementId, CustomerId, Action, Id)
                : new EntitlementChangeFailed(EntitlementId, CustomerId, Action, Id, error));
            return;
        }

        if (await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(CustomerId), ct) is { } customer)
        {
            session.Events.Append(customer.Id, new CustomerOperationCompleted(CustomerId, Action, Id, succeeded, error));
        }
    }
}
