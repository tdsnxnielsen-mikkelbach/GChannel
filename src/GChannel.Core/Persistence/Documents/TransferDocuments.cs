using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Shared.Contracts;
using Marten;

namespace GChannel.Core.Persistence.Documents;

/// <summary>
/// Latest <c>accounts.listTransferableSkus</c> result for one customer — the SKUs it holds elsewhere (direct
/// with Google or with another reseller) that could be transferred in. Written by the worker's nightly
/// incoming-transfer scan and refreshed whenever a user loads (or rechecks) the customer's transferability.
/// </summary>
public sealed class IncomingTransferRecord
{
    /// <summary>The bare customer id.</summary>
    public string Id { get; set; } = string.Empty;
    public string? OrgName { get; set; }
    public string? Domain { get; set; }
    public string? OwningLinkId { get; set; }
    public List<TransferableSku> Skus { get; set; } = [];
    /// <summary>Number of <see cref="Skus"/> Google reports as eligible (indexed: the list page filters on it).</summary>
    public int EligibleCount { get; set; }
    /// <summary>When the customer was last checked (successfully or not).</summary>
    public DateTimeOffset LastScannedUtc { get; set; }
    /// <summary>When <see cref="Skus"/> was last refreshed successfully.</summary>
    public DateTimeOffset? LastSucceededUtc { get; set; }
    /// <summary>Error of the last check; the previous <see cref="Skus"/> are kept when it fails.</summary>
    public string? Error { get; set; }
}

public static class IncomingTransfers
{
    /// <summary><see cref="SyncCursor"/> id recording the scan's last full pass and run error.</summary>
    public const string CursorScope = "incoming-transfers";

    /// <summary>
    /// Stores a customer's transferability check: <paramref name="result"/> replaces the SKUs; a failed check
    /// (<paramref name="error"/>) keeps the previous ones and records the error. Customer names come from the read model.
    /// The caller saves the session.
    /// </summary>
    public static async Task<IncomingTransferRecord> RecordAsync(
        IDocumentSession session, string customerId, TransferableSkusResult? result, string? error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var record = await session.LoadAsync<IncomingTransferRecord>(customerId, ct) ?? new IncomingTransferRecord { Id = customerId };
        if (await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), ct) is { } customer)
        {
            record.OrgName = customer.OrgName;
            record.Domain = customer.Domain;
            record.OwningLinkId = customer.OwningLinkId;
        }

        record.LastScannedUtc = now;
        if (result is not null)
        {
            record.Skus = result.Skus.ToList();
            record.LastSucceededUtc = now;
            record.Error = null;
        }
        else
        {
            record.Error = error is { Length: > 500 } ? error[..500] : error ?? "Unknown error";
        }
        record.EligibleCount = record.Skus.Count(s => s.IsEligible);
        session.Store(record);
        return record;
    }
}
