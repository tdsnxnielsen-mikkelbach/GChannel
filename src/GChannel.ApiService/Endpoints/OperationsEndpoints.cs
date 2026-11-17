using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Long-running operation endpoints. The Channel API cannot list operations, so the list comes from the
/// app's own <see cref="OperationLogRecord"/>s (written when a mutating call returns an operation and kept
/// current by the worker's <c>ChannelOperationSaga</c>). A single operation is read live from Google and
/// merged with that record; when Google can't be asked, the recorded state is returned instead. Operations
/// are volatile, so nothing here is cached.
/// </summary>
public static class OperationsEndpoints
{
    private const int MaxListed = 200;

    [WolverineGet("/api/operations")]
    public static async Task<ChannelOperationsResult> ListOperations(string? customerId, IQuerySession session, CancellationToken cancellationToken)
    {
        var query = session.Query<OperationLogRecord>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(customerId))
        {
            query = query.Where(r => r.CustomerId == customerId);
        }

        var records = await query.OrderByDescending(r => r.SubmittedAt).Take(MaxListed).ToListAsync(cancellationToken);
        return new ChannelOperationsResult { Operations = records.Select(OperationLog.ToContract).ToList() };
    }

    [WolverineGet("/api/operations/{operationId}")]
    public static async Task<ChannelOperation> GetOperation(
        string operationId, IGoogleChannelClient channel, IQuerySession session, CancellationToken cancellationToken)
    {
        var record = await session.LoadAsync<OperationLogRecord>(OperationLog.Normalize(operationId), cancellationToken);
        try
        {
            return OperationLog.Merge(await channel.GetOperationAsync(operationId, cancellationToken), record);
        }
        catch (Exception ex) when (record is not null && ex is not OperationCanceledException)
        {
            var recorded = OperationLog.ToContract(record);
            return recorded with { Tracking = recorded.Tracking! with { Note = $"Live status unavailable ({ex.Message}); showing the recorded state." } };
        }
    }

    [WolverinePost("/api/operations/{operationId}/cancel")]
    public static async Task<ChannelOperation> CancelOperation(
        string operationId, IGoogleChannelClient channel, IQuerySession session, CancellationToken cancellationToken)
    {
        var operation = await channel.CancelOperationAsync(operationId, cancellationToken);
        var record = await session.LoadAsync<OperationLogRecord>(OperationLog.Normalize(operationId), cancellationToken);
        return OperationLog.Merge(operation, record);
    }
}
