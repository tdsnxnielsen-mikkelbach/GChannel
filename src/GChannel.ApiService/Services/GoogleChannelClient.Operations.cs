using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

// Long-running operations (§7) — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<ChannelOperationsResult> ListOperationsAsync(CancellationToken cancellationToken)
    {
        using var service = CreateService();

        var operations = new List<ChannelOperation>();
        var request = service.Operations.List("operations");
        request.PageSize = 100;

        try
        {
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var operation in response.Operations ?? [])
            {
                operations.Add(MapLongrunningOperation(operation));
            }
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotImplemented)
        {
            // The Cloud Channel API implements operations.get and operations.cancel but NOT
            // operations.list (it returns HTTP 501 notImplemented). Degrade gracefully so the UI can
            // still track a specific operation by id; there is simply no global listing to return.
            logger.LogInformation(
                "operations.list is not supported by the Cloud Channel API (501); returning an empty list.");
        }

        return new ChannelOperationsResult { Operations = operations };
    }

    public async Task<ChannelOperation> GetOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        using var service = CreateService();

        var operation = await service.Operations
            .Get(OperationName(operationId))
            .ExecuteAsync(cancellationToken);

        return MapLongrunningOperation(operation);
    }

    public async Task<ChannelOperation> CancelOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        using var service = CreateService();

        logger.LogInformation("Requesting cancellation of operation {Operation}", operationId);

        await service.Operations
            .Cancel(new GoogleLongrunningCancelOperationRequest(), OperationName(operationId))
            .ExecuteAsync(cancellationToken);

        // Cancel returns an empty body, so re-read the operation to report its current state.
        var operation = await service.Operations
            .Get(OperationName(operationId))
            .ExecuteAsync(cancellationToken);

        return MapLongrunningOperation(operation);
    }

    /// <summary>Normalises an operation id or name to a full "operations/{id}" resource name.</summary>
    private static string OperationName(string operationId) =>
        operationId.StartsWith("operations/", StringComparison.OrdinalIgnoreCase)
            ? operationId
            : $"operations/{operationId}";

    /// <summary>Projects a Google LRO into the shared contract, pulling the operation type from its
    /// metadata and the acted-on resource (entitlement/customer) from its response for correlation.</summary>
    private static ChannelOperation MapLongrunningOperation(GoogleLongrunningOperation operation)
    {
        var name = operation.Name ?? string.Empty;
        string? operationType = null;
        string? resourceName = null;

        // Channel operation metadata is { "@type": "...OperationMetadata", "operationType": "..." };
        // the response (once done) is the affected resource, which carries its own "name".
        if (operation.Metadata is IDictionary<string, object> metadata
            && metadata.TryGetValue("operationType", out var type) && type is not null)
        {
            operationType = type.ToString();
        }

        if (operation.Response is IDictionary<string, object> response
            && response.TryGetValue("name", out var resource) && resource is not null)
        {
            resourceName = resource.ToString();
        }

        var (customerId, entitlementId) = SplitCustomerEntitlement(resourceName);

        return new ChannelOperation
        {
            Name = name,
            Id = LastSegment(name) ?? name,
            Done = operation.Done ?? false,
            Error = operation.Error?.Message,
            ErrorCode = operation.Error?.Code,
            OperationType = operationType,
            ResourceName = resourceName,
            CustomerId = customerId,
            EntitlementId = entitlementId
        };
    }

    /// <summary>Pulls the customer and entitlement ids out of any Channel resource name, for
    /// correlating operations and notifications back to the customer/entitlement pages.</summary>
    private static (string? CustomerId, string? EntitlementId) SplitCustomerEntitlement(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return (null, null);
        }

        var segments = resourceName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? customerId = null;
        string? entitlementId = null;
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i] == "customers")
            {
                customerId = segments[i + 1];
            }
            else if (segments[i] == "entitlements")
            {
                entitlementId = segments[i + 1];
            }
        }

        return (customerId, entitlementId);
    }
}
