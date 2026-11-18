using GChannel.Core.Configuration;
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
/// Records a Cloud Customer Care support event in the feed and refreshes the affected case (with its comments and
/// attachments) from the Cloud Support API. Events whose case cannot be read for a permanent reason (no access,
/// not found, feature not configured) are recorded with the error instead of being retried; throttling and
/// transient failures go through Wolverine's retry policy.
/// </summary>
public sealed class SupportEventArrivedHandler(
    ISupportCasesService support,
    IOptions<GoogleSupportOptions> options,
    ILogger<SupportEventArrivedHandler> logger)
{
    public async Task Handle(SupportEventArrived message, IDocumentSession session, CancellationToken ct)
    {
        var e = message.Event;
        var caseName = e.CaseName ?? SupportCaseNames.CaseNameOf(e.ResourceName);
        string? error = null;

        if (caseName is null)
        {
            error = "The event names no support case.";
            logger.LogWarning("Support event {MessageId} ({Type}) names no case: {Resource}", e.MessageId, e.NotificationType, e.ResourceName);
        }
        else if (!support.IsConfigured)
        {
            error = "Support cases are not configured (GoogleSupport:OrganizationId / service-account key).";
        }
        else
        {
            try
            {
                await SupportCaseStore.RefreshAsync(session, support, caseName, ct);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                                                           or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                error = SupportCaseStore.Describe(ex);
                logger.LogWarning("Support case {Case} could not be read: {Error}", caseName, error);
            }
        }

        session.Store(new SupportEventRecord
        {
            Id = e.MessageId,
            Event = e with { CaseName = caseName, Error = error },
            CaseId = caseName is null ? null : SupportCaseNames.CaseIdOf(caseName),
            ReceivedAt = e.ReceivedAt,
        });

        // Retention: keep the feed bounded.
        var keep = Math.Max(50, options.Value.MaxEvents);
        var count = await session.Query<SupportEventRecord>().CountAsync(ct);
        if (count > keep + 100)
        {
            var cutoff = await session.Query<SupportEventRecord>()
                .OrderByDescending(r => r.ReceivedAt)
                .Skip(keep)
                .Select(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);
            session.DeleteWhere<SupportEventRecord>(r => r.ReceivedAt < cutoff);
        }

        logger.LogInformation("Support event recorded: {Type} {Resource}", e.NotificationType, e.ResourceName);
    }
}

/// <summary>
/// Daily support-case catch-up (the <c>RunSupportCaseCatchUp</c> recurring tick): lists every case of the
/// organization and refreshes the ones whose <c>updateTime</c> differs from the mirror — the safety net for events
/// missed while the subscription was FAILING or the worker was down (Google's documented fallback).
/// </summary>
public sealed class SupportCaseCatchUpHandler(
    ISupportCasesService support,
    IDocumentStore store,
    RecurringJobCatalog catalog,
    ILogger<SupportCaseCatchUpHandler> logger)
{
    [MessageTimeout(RecurringJobs.SupportCaseCatchUpBudgetSeconds)]
    public async Task<OutgoingMessages> Handle(RunSupportCaseCatchUp tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.SupportCaseCatchUp], session, outgoing, logger,
            c => CatchUpAsync(support, store, logger, c), ct);
        return outgoing;
    }

    /// <summary>The catch-up body (public so tests can drive it with a fake service). Returns the run's error, if any.</summary>
    public static async Task<string?> CatchUpAsync(ISupportCasesService support, IDocumentStore store, ILogger logger, CancellationToken ct)
    {
        var cases = await support.ListCasesAsync(ct);
        Dictionary<string, DateTimeOffset?> known;
        await using (var query = store.QuerySession())
        {
            known = (await query.Query<SupportCaseRecord>().Select(r => new { r.Id, r.UpdateTime }).ToListAsync(ct))
                .ToDictionary(r => r.Id, r => r.UpdateTime);
        }

        int refreshed = 0, failed = 0;
        foreach (var supportCase in cases)
        {
            ct.ThrowIfCancellationRequested();
            if (known.TryGetValue(supportCase.CaseId, out var updated) && updated == supportCase.UpdateTime)
            {
                continue;
            }

            try
            {
                await using var session = store.LightweightSession();
                await SupportCaseStore.RefreshAsync(session, support, supportCase.Name, ct);
                await session.SaveChangesAsync(ct);
                refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "Support catch-up could not refresh {Case}.", supportCase.Name);
            }
        }

        var notes = failed > 0 ? $"{failed} of {refreshed + failed} changed cases could not be refreshed." : null;
        await using (var session = store.LightweightSession())
        {
            var cursor = await session.LoadAsync<SyncCursor>(SupportCaseStore.CursorScope, ct) ?? new SyncCursor { Id = SupportCaseStore.CursorScope };
            cursor.LastCycleUtc = DateTimeOffset.UtcNow;
            cursor.LastFullPassUtc = cursor.LastCycleUtc;
            cursor.Notes = notes;
            session.Store(cursor);
            await session.SaveChangesAsync(ct);
        }

        logger.LogInformation("Support catch-up: {Total} cases, {Refreshed} refreshed, {Failed} failed.", cases.Count, refreshed, failed);
        return notes;
    }
}
