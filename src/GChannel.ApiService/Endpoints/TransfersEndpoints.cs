using GChannel.ApiService.Caching;
using GChannel.ApiService.Http;
using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Globalization;
using Wolverine;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Transfer endpoints: the read-only transferability inspection plus the mutating transfer calls, nested
/// under a customer. Transfers return long-running operations; the request is recorded on the customer's
/// event stream and the operation handed to the worker's saga for tracking.
/// </summary>
public static class TransfersEndpoints
{
    [WolverineGet("/api/customers/{customerId}/transferable-skus")]
    public static Task<IResult> ListTransferableSkus(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options,
        IDocumentSession session, ILogger<IncomingTransferRecord> logger, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.TransferableSkusKey(customerId), options.Value.CacheSeconds,
            async () =>
            {
                var result = await channel.ListTransferableSkusAsync(customerId, cancellationToken);
                // A live read also refreshes the customer's row in the estate-wide incoming-transfer list.
                try
                {
                    await IncomingTransfers.RecordAsync(session, customerId, result, null, cancellationToken);
                    await session.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not record the incoming-transfer check for {Customer}.", customerId);
                }
                return result;
            }, cancellationToken);

    /// <summary>
    /// Estate-wide transferable SKUs from the nightly incoming-transfer scan (eligible ones only unless
    /// <paramref name="all"/>), plus the customers whose last check failed. Read model only — no Google calls.
    /// </summary>
    [WolverineGet("/api/transfers/incoming")]
    public static async Task<IncomingTransfersResult> ListIncomingTransfers(
        bool? all, IQuerySession session, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        var includeIneligible = all == true;
        var records = await session.Query<IncomingTransferRecord>().ToListAsync(cancellationToken);
        var cursor = await session.LoadAsync<SyncCursor>(IncomingTransfers.CursorScope, cancellationToken);
        var job = await session.LoadAsync<RecurringJobState>(RecurringJobs.IncomingTransferScan, cancellationToken);

        var linkIds = records.Select(r => r.OwningLinkId).OfType<string>().Distinct().ToList();
        var resellers = linkIds.Count == 0
            ? new Dictionary<string, string?>()
            : (await session.Query<ResellerLinkRecord>().Where(l => l.LinkId.IsOneOf(linkIds)).ToListAsync(cancellationToken))
                .GroupBy(l => l.LinkId).ToDictionary(g => g.Key, g => g.First().PrimaryDomain);

        return new IncomingTransfersResult
        {
            Items = ToRows(records, includeIneligible, resellers),
            Errors = records.Where(r => r.Error is not null)
                .Select(r => new IncomingTransferError { CustomerId = r.Id, CustomerName = r.OrgName, Error = r.Error!, CheckedUtc = r.LastScannedUtc })
                .OrderBy(e => e.CustomerName ?? e.CustomerId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CustomersScanned = records.Count,
            CustomersWithEligible = records.Count(r => r.EligibleCount > 0),
            AsOf = cursor?.LastFullPassUtc,
            NextScanUtc = job?.NextRunUtc,
            Scanning = job?.IsRunning ?? false,
            Unconfigured = !options.Value.IncomingTransferScanEnabled
        };
    }

    /// <summary>Re-checks one customer live (the signed-in user's token) and returns its refreshed rows, eligible or not.</summary>
    [WolverinePost("/api/transfers/incoming/{customerId}/recheck")]
    public static async Task<IncomingTransfersResult> RecheckIncomingTransfers(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options,
        IDocumentSession session, CancellationToken cancellationToken)
    {
        TransferableSkusResult? result = null;
        string? error = null;
        try
        {
            result = await channel.ListTransferableSkusAsync(customerId, cancellationToken);
            await GoogleReadCache.SetAsync(cache, GoogleReadCache.TransferableSkusKey(customerId), result, options.Value.CacheSeconds, cancellationToken);
        }
        catch (Google.GoogleApiException ex)
        {
            error = $"{ex.HttpStatusCode}: {ex.Error?.Message ?? ex.Message}";
        }

        var record = await IncomingTransfers.RecordAsync(session, customerId, result, error, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        var reseller = record.OwningLinkId is null
            ? null
            : (await session.Query<ResellerLinkRecord>().Where(l => l.LinkId == record.OwningLinkId).FirstOrDefaultAsync(cancellationToken))?.PrimaryDomain;
        return new IncomingTransfersResult
        {
            Items = ToRows([record], includeIneligible: true, new Dictionary<string, string?> { [record.OwningLinkId ?? string.Empty] = reseller }),
            Errors = error is null ? [] : [new IncomingTransferError { CustomerId = customerId, CustomerName = record.OrgName, Error = error, CheckedUtc = record.LastScannedUtc }],
            CustomersScanned = 1,
            CustomersWithEligible = record.EligibleCount > 0 ? 1 : 0
        };
    }

    private static List<IncomingTransfer> ToRows(IEnumerable<IncomingTransferRecord> records, bool includeIneligible, IReadOnlyDictionary<string, string?> resellers) =>
        records
            .SelectMany(r => r.Skus
                .Where(s => includeIneligible || s.IsEligible)
                .Select(s => new IncomingTransfer
                {
                    CustomerId = r.Id,
                    CustomerName = r.OrgName,
                    Domain = r.Domain,
                    OwningLinkId = r.OwningLinkId,
                    ResellerName = r.OwningLinkId is null ? null : resellers.GetValueOrDefault(r.OwningLinkId),
                    Sku = s,
                    CheckedUtc = r.LastSucceededUtc
                }))
            .OrderBy(t => t.CustomerName ?? t.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(t => t.Sku.IsEligible)
            .ThenBy(t => t.Sku.ProductDisplayName ?? t.Sku.ProductId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Sku.SkuDisplayName ?? t.Sku.SkuId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [WolverineGet("/api/customers/{customerId}/transferable-offers")]
    public static Task<IResult> ListTransferableOffers(
        string customerId, string productId, string skuId, string? billingAccount, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache,
            $"customer:{customerId}:transferable-offers:{productId}:{skuId}:{(string.IsNullOrWhiteSpace(billingAccount) ? "-" : billingAccount)}",
            options.Value.CacheSeconds,
            () => channel.ListTransferableOffersAsync(customerId, productId, skuId, billingAccount, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/transfer-entitlements")]
    public static async Task<IResult> TransferEntitlements(
        string customerId, TransferEntitlementsRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.TransferEntitlementsAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        var details = TransferDetails(request.Entitlements);
        details.Add(new OperationDetail("Transfer auth token", string.IsNullOrWhiteSpace(request.AuthToken) ? "not provided" : "provided"));
        await RecordAsync(session, bus, observer, customerId, "transferEntitlements", operation, request.Entitlements, details, RequestUser.NameOf(http), cancellationToken);
        return Results.Accepted(value: operation);
    }

    [WolverinePost("/api/customers/{customerId}/transfer-entitlements-to-google")]
    public static async Task<IResult> TransferEntitlementsToGoogle(
        string customerId, TransferEntitlementsToGoogleRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.TransferEntitlementsToGoogleAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        await RecordAsync(session, bus, observer, customerId, "transferEntitlementsToGoogle", operation, request.Entitlements, TransferDetails(request.Entitlements),
            RequestUser.NameOf(http), cancellationToken);
        return Results.Accepted(value: operation);
    }

    /// <summary>One labelled block per transferred line, numbered when the request has several.</summary>
    internal static List<OperationDetail> TransferDetails(IReadOnlyList<TransferEntitlementLine> lines)
    {
        var details = new List<OperationDetail>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var prefix = lines.Count > 1 ? $"#{i + 1} " : string.Empty;
            details.Add(new OperationDetail($"{prefix}Offer", string.IsNullOrWhiteSpace(line.OfferDisplayName) ? line.OfferId : $"{line.OfferDisplayName} ({line.OfferId})"));
            details.AddRange(line.Parameters.Select(p => new OperationDetail($"{prefix}{ParameterLabel(p.Name)}", p.IntValue?.ToString(CultureInfo.InvariantCulture) ?? p.StringValue ?? "—")));
            if (!string.IsNullOrWhiteSpace(line.BillingAccount))
            {
                details.Add(new OperationDetail($"{prefix}Billing account", line.BillingAccount));
            }
            if (!string.IsNullOrWhiteSpace(line.PurchaseOrderId))
            {
                details.Add(new OperationDetail($"{prefix}Purchase order", line.PurchaseOrderId));
            }
        }
        return details;
    }

    internal static string ParameterLabel(string name) => name switch
    {
        "num_units" => "Seats",
        "max_units" => "Max seats",
        _ => name
    };

    private static async Task RecordAsync(
        IDocumentSession session, IMessageBus bus, EstateObserver observer, string customerId, string action,
        EntitlementOperation operation, IReadOnlyList<TransferEntitlementLine> lines, IReadOnlyList<OperationDetail> details, string? user, CancellationToken cancellationToken)
    {
        await observer.RecordCustomerMutationAsync(session, customerId, action, user, operation.OperationName, cancellationToken);
        await OperationLog.RecordSubmittedAsync(session, operation.OperationName, operation.Done, operation.Error, customerId, null, action, details, user, cancellationToken,
            subject: string.Join(", ", lines.Select(l => string.IsNullOrWhiteSpace(l.OfferDisplayName) ? l.OfferId : l.OfferDisplayName)));
        await bus.PublishAsync(new CustomerChanged(customerId, null, action, user));
        if (!string.IsNullOrEmpty(operation.OperationName) && !operation.Done)
        {
            await bus.PublishAsync(new OperationSubmitted(operation.OperationName, customerId, null, action, user, DateTimeOffset.UtcNow));
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drops the affected entitlement-list and transferability caches after a transfer.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.EntitlementsListKey(customerId), cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.TransferableSkusKey(customerId), cancellationToken);
    }
}
