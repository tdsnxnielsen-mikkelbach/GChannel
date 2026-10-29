using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using Marten;
using Marten.Schema;

namespace GChannel.Worker.Infrastructure;

/// <summary>
/// Idempotent startup repair (Marten initial data, runs on every worker start): deletes customer / entitlement
/// read-model documents whose identity is not a stream key of their own aggregate type. A former no-op
/// <c>Apply(ChannelNotificationReceived)</c> on both read models made each inline snapshot also run on the
/// other aggregate's streams, leaving blank documents (empty <c>CustomerId</c> / <c>EntitlementId</c>) that
/// collided on the unique indexes and dead-lettered every later notification. Nothing creates such documents
/// any more, so once they are gone this is a cheap no-op.
/// </summary>
public sealed class StrayReadModelCleanup(ILogger<StrayReadModelCleanup> logger) : IInitialData
{
    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        try
        {
            await using var session = store.LightweightSession();
            var customers = await session.Query<CustomerRecord>()
                .Where(x => !x.Id.StartsWith(EstateStreams.CustomerPrefix))
                .Select(x => x.Id)
                .ToListAsync(cancellation);
            var entitlements = await session.Query<EntitlementRecord>()
                .Where(x => !x.Id.StartsWith(EstateStreams.EntitlementPrefix))
                .Select(x => x.Id)
                .ToListAsync(cancellation);

            if (customers.Count == 0 && entitlements.Count == 0)
            {
                return;
            }

            foreach (var id in customers) { session.Delete<CustomerRecord>(id); }
            foreach (var id in entitlements) { session.Delete<EntitlementRecord>(id); }
            await session.SaveChangesAsync(cancellation);

            logger.LogWarning(
                "Removed stray read-model documents: {CustomerCount} CustomerRecord ({CustomerIds}), {EntitlementCount} EntitlementRecord ({EntitlementIds})",
                customers.Count, string.Join(", ", customers), entitlements.Count, string.Join(", ", entitlements));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort repair: never block the worker from starting.
            logger.LogError(ex, "Stray read-model cleanup failed");
        }
    }
}
