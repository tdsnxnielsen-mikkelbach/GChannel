using GChannel.Core.Configuration;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Worker.Infrastructure;
using Marten;
using Microsoft.Extensions.Options;
using System.Net;
using Wolverine;
using Wolverine.Attributes;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Nightly incoming-transfer scan (the <c>RunIncomingTransferScan</c> recurring tick): calls
/// <c>accounts.listTransferableSkus</c> for every customer in the read model, paced by
/// <c>TransferScanRequestsPerMinute</c>, and stores one <see cref="IncomingTransferRecord"/> per customer.
/// Customers are visited least-recently-checked first and saved one by one, so a run cut short by its budget
/// still advances and the next run resumes with the customers it missed.
/// </summary>
public sealed class IncomingTransferScanHandler(
    IOptions<GoogleChannelOptions> options,
    ServiceAccountChannelClientProvider clients,
    IDocumentStore store,
    RecurringJobCatalog catalog,
    ILogger<IncomingTransferScanHandler> logger)
{
    [MessageTimeout(RecurringJobs.IncomingTransferScanBudgetSeconds)]
    public async Task<OutgoingMessages> Handle(RunIncomingTransferScan tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.IncomingTransferScan], session, outgoing, logger, async c =>
        {
            var client = clients.Client ?? throw new InvalidOperationException("No service-account credential configured.");
            var pace = TimeSpan.FromSeconds(60.0 / Math.Max(1, options.Value.TransferScanRequestsPerMinute));
            var summary = await ScanAsync(client, store, pace, logger, c);
            return summary.Failed > 0 ? $"{summary.Failed} of {summary.Scanned} customers could not be checked." : null;
        }, ct);
        return outgoing;
    }

    public sealed record ScanSummary(int Scanned, int Failed, int WithEligible);

    /// <summary>The scan body (public so tests can drive it with a fake client and no pacing).</summary>
    public static async Task<ScanSummary> ScanAsync(IGoogleChannelClient client, IDocumentStore store, TimeSpan pace, ILogger logger, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        List<string> order;
        await using (var query = store.QuerySession())
        {
            var customers = await query.Query<CustomerRecord>().Where(c => !c.IsDeleted).Select(c => c.CustomerId).ToListAsync(ct);
            var lastScanned = (await query.Query<IncomingTransferRecord>().ToListAsync(ct)).ToDictionary(r => r.Id, r => r.LastScannedUtc);
            order = customers
                .Distinct()
                .OrderBy(id => lastScanned.TryGetValue(id, out var at) ? at : DateTimeOffset.MinValue)
                .ToList();

            // Customers that left the estate drop out of the list.
            var gone = lastScanned.Keys.Except(customers).ToList();
            if (gone.Count > 0)
            {
                await using var cleanup = store.LightweightSession();
                cleanup.DeleteWhere<IncomingTransferRecord>(r => r.Id.IsOneOf(gone));
                await cleanup.SaveChangesAsync(ct);
            }
        }

        logger.LogInformation("Incoming-transfer scan: checking {Count} customers.", order.Count);
        int scanned = 0, failed = 0, withEligible = 0;
        foreach (var customerId in order)
        {
            ct.ThrowIfCancellationRequested();
            if (scanned > 0 && pace > TimeSpan.Zero)
            {
                await Task.Delay(pace, ct);
            }

            var (result, error) = await CheckAsync(client, customerId, logger, ct);
            await using var session = store.LightweightSession();
            var record = await IncomingTransfers.RecordAsync(session, customerId, result, error, ct);
            await session.SaveChangesAsync(ct);

            scanned++;
            if (error is not null) { failed++; }
            if (record.EligibleCount > 0) { withEligible++; }
        }

        await using (var session = store.LightweightSession())
        {
            var cursor = await session.LoadAsync<SyncCursor>(IncomingTransfers.CursorScope, ct) ?? new SyncCursor { Id = IncomingTransfers.CursorScope };
            cursor.LastCycleUtc = DateTimeOffset.UtcNow;
            cursor.LastFullPassUtc = cursor.LastCycleUtc;
            cursor.Notes = failed > 0 ? $"{failed} of {scanned} customers could not be checked." : null;
            session.Store(cursor);
            await session.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Incoming-transfer scan complete in {Elapsed:0}s: {Scanned} customers, {WithEligible} with eligible SKUs, {Failed} failed.",
            (DateTimeOffset.UtcNow - started).TotalSeconds, scanned, withEligible, failed);
        return new ScanSummary(scanned, failed, withEligible);
    }

    // One customer; a throttled call waits a minute and retries once before being recorded as failed.
    private static async Task<(Shared.Contracts.TransferableSkusResult? Result, string? Error)> CheckAsync(
        IGoogleChannelClient client, string customerId, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await client.ListTransferableSkusAsync(customerId, ct), null);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
            {
                logger.LogInformation("Incoming-transfer scan throttled at {Customer}; pausing 60s.", customerId);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = ex is Google.GoogleApiException g ? $"{g.HttpStatusCode}: {g.Error?.Message ?? g.Message}" : ex.Message;
                logger.LogDebug(ex, "Incoming-transfer scan could not check {Customer}.", customerId);
                return (null, message);
            }
        }
    }
}
