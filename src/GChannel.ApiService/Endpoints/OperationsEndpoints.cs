using GChannel.ApiService.Services;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the long-running operation endpoints (§7). Mutating Channel calls (entitlement
/// create/change/state changes, transfers) return an operation name; these endpoints let the UI poll
/// an operation until it is <c>done</c> and request cancellation. Operations are volatile, so the
/// responses are not cached.
/// </summary>
public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations").WithTags("Operations");

        group.MapGet("/", (IGoogleChannelClient channel, CancellationToken cancellationToken) =>
                channel.ListOperationsAsync(cancellationToken))
            .WithName("ListOperations")
            .WithSummary("Lists recent long-running operations.");

        group.MapGet("/{operationId}", (
                string operationId,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
                channel.GetOperationAsync(operationId, cancellationToken))
            .WithName("GetOperation")
            .WithSummary("Gets a single long-running operation (poll until done).");

        group.MapPost("/{operationId}/cancel", (
                string operationId,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
                channel.CancelOperationAsync(operationId, cancellationToken))
            .WithName("CancelOperation")
            .WithSummary("Requests cancellation of a long-running operation.");

        return app;
    }
}
