using GChannel.Core.GoogleApis;
using GChannel.Shared.Contracts;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Long-running operation endpoints. Mutating Channel calls return an operation name; these let the UI
/// poll an operation until it is <c>done</c> and request cancellation. Operations are volatile, so the
/// responses are not cached. (The worker additionally tracks app-submitted operations through the
/// <c>ChannelOperationSaga</c> and records their outcome on the entitlement's event stream.)
/// </summary>
public static class OperationsEndpoints
{
    [WolverineGet("/api/operations")]
    public static Task<ChannelOperationsResult> ListOperations(IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.ListOperationsAsync(cancellationToken);

    [WolverineGet("/api/operations/{operationId}")]
    public static Task<ChannelOperation> GetOperation(string operationId, IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.GetOperationAsync(operationId, cancellationToken);

    [WolverinePost("/api/operations/{operationId}/cancel")]
    public static Task<ChannelOperation> CancelOperation(string operationId, IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.CancelOperationAsync(operationId, cancellationToken);
}
