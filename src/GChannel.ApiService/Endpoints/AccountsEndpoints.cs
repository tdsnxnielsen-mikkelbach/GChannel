using GChannel.ApiService.Caching;
using GChannel.ApiService.Http;
using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>Accounts-related Channel API endpoints: the Cloud Identity existence check and its audit history.</summary>
public static class AccountsEndpoints
{
    [WolverinePost("/api/accounts/check-cloud-identity")]
    public static async Task<IResult> CheckCloudIdentity(
        CheckCloudIdentityRequest request,
        bool refresh,
        IGoogleChannelClient channel,
        IDistributedCache cache,
        IDocumentSession session,
        IOptions<GoogleChannelOptions> options,
        ILogger<CheckCloudIdentityRequest> logger,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["domain"] = ["A domain is required."]
            });
        }

        var cacheKey = $"cid:{request.Domain.ToLowerInvariant()}:{request.PrimaryAdminEmail?.ToLowerInvariant()}";

        // A forced recheck (refresh=true) bypasses the cached result and re-queries Google, then refreshes
        // the cache. Normal checks reuse the cached result while it is still warm.
        if (!refresh)
        {
            var cached = await GoogleReadCache.GetAsync<CheckCloudIdentityResult>(cache, cacheKey, cancellationToken);
            if (cached is not null)
            {
                return Results.Ok(cached);
            }
        }

        var result = await channel.CheckCloudIdentityAsync(request, cancellationToken);

        // Cross-correlate against our channel partner links: if a link's Cloud Identity domain matches this
        // domain, surface it (e.g. a still-pending INVITED invitation we already sent).
        result = result with
        {
            PartnerLink = await FindPartnerLinkForDomainAsync(channel, cache, result, logger, cancellationToken)
        };

        await GoogleReadCache.SetAsync(cache, cacheKey, result, options.Value.CacheSeconds, cancellationToken);

        session.Store(new IdentityCheckLog
        {
            Id = Guid.CreateVersion7(),
            Domain = result.Domain,
            Exists = result.Exists,
            AccountsFound = result.Accounts.Count,
            PerformedBy = RequestUser.NameOf(http),
            PerformedAt = DateTimeOffset.UtcNow,
        });

        // The audit write is best-effort: a database hiccup must not fail the user's check, which has already
        // succeeded against Google at this point.
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist Cloud Identity audit log for {Domain}.", result.Domain);
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// Finds a channel partner link that corresponds to the result's domain, trying, in order: (1) the link's
    /// Cloud Identity primary domain, (2) the reseller Cloud Identity ID found by the check, and (3) the domain
    /// we recorded when we sent the invitation. Best-effort: partner-link discovery must never fail the check.
    /// </summary>
    private static async Task<ChannelPartnerLink?> FindPartnerLinkForDomainAsync(
        IGoogleChannelClient channel,
        IDistributedCache cache,
        CheckCloudIdentityResult result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var links = (await channel.ListChannelPartnerLinksAsync(cancellationToken)).Links;
            if (links.Count == 0)
            {
                return null;
            }

            var domain = result.Domain;

            var match = links.FirstOrDefault(l =>
                string.Equals(l.ChannelPartner?.PrimaryDomain, domain, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            var checkedIds = result.Accounts
                .Select(a => a.CustomerCloudIdentityId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (checkedIds.Count > 0)
            {
                match = links.FirstOrDefault(l =>
                    !string.IsNullOrWhiteSpace(l.ResellerCloudIdentityId) &&
                    checkedIds.Contains(l.ResellerCloudIdentityId!));
                if (match is not null)
                {
                    return match;
                }
            }

            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.ResellerCloudIdentityId))
                {
                    continue;
                }

                var invitedDomain = await cache.GetStringAsync(
                    GoogleReadCache.InvitedDomainKey(link.ResellerCloudIdentityId!), cancellationToken);
                if (string.Equals(invitedDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return link;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cross-correlate channel partner links for {Domain}.", result.Domain);
            return null;
        }
    }

    [WolverineGet("/api/accounts/check-cloud-identity/history")]
    public static async Task<IdentityCheckHistoryResult> GetHistory(IQuerySession session, CancellationToken cancellationToken)
    {
        // Pull the most recent audit rows, then keep the latest entry per domain so the UI can surface a
        // short "recently checked" list with one-click recheck.
        var recent = await session.Query<IdentityCheckLog>()
            .OrderByDescending(l => l.PerformedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var items = recent
            .GroupBy(l => l.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(20)
            .Select(l => new IdentityCheckHistoryItem
            {
                Domain = l.Domain,
                Exists = l.Exists,
                AccountsFound = l.AccountsFound,
                PerformedAt = l.PerformedAt,
                PerformedBy = l.PerformedBy
            })
            .ToList();

        return new IdentityCheckHistoryResult { Items = items };
    }
}
