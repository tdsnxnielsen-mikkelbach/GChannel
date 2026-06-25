using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>Maps the Accounts-related Channel API endpoints exposed to the front end.</summary>
public static class AccountsEndpoints
{
    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapPost("/check-cloud-identity", CheckCloudIdentityAsync)
            .WithName("CheckCloudIdentity")
            .WithSummary("Checks whether a Cloud Identity account already exists for a domain.");

        group.MapGet("/check-cloud-identity/history", GetHistoryAsync)
            .WithName("CloudIdentityHistory")
            .WithSummary("Lists recently checked domains (latest result per domain).");

        return app;
    }

    private static async Task<IResult> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        IGoogleChannelClient channel,
        IDistributedCache cache,
        GChannelDbContext db,
        IOptions<GoogleChannelOptions> options,
        ILoggerFactory loggerFactory,
        HttpContext http,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["domain"] = ["A domain is required."]
            });
        }

        var cacheKey = $"cid:{request.Domain.ToLowerInvariant()}:{request.PrimaryAdminEmail?.ToLowerInvariant()}";

        // A forced recheck (refresh=true) bypasses the cached result and re-queries Google, then
        // refreshes the cache. Normal checks reuse the cached result while it is still warm.
        if (!refresh)
        {
            var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                return Results.Ok(JsonSerializer.Deserialize<CheckCloudIdentityResult>(cached));
            }
        }

        var result = await channel.CheckCloudIdentityAsync(request, cancellationToken);

        // Cross-correlate against our channel partner links: if a link's Cloud Identity domain
        // matches this domain, surface it (e.g. a still-pending INVITED invitation we already sent).
        result = result with
        {
            PartnerLink = await FindPartnerLinkForDomainAsync(channel, cache, result, loggerFactory, cancellationToken)
        };

        await cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(result),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.Value.CacheSeconds)
            },
            cancellationToken);

        db.IdentityCheckLogs.Add(new IdentityCheckLog
        {
            Domain = result.Domain,
            Exists = result.Exists,
            AccountsFound = result.Accounts.Count,
            PerformedBy = http.User.Identity?.Name ?? http.Request.Headers["X-User-Email"].ToString()
        });

        // The audit write is best-effort: a paused/warming serverless database must not fail the
        // user's check, which has already succeeded against Google at this point.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("AccountsEndpoints")
                .LogWarning(ex, "Failed to persist Cloud Identity audit log for {Domain}.", result.Domain);
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// Finds a channel partner link that corresponds to <paramref name="result"/>'s domain, trying,
    /// in order: (1) the link's Cloud Identity primary domain, (2) the reseller Cloud Identity ID
    /// found by the check, and (3) the domain we recorded when we sent the invitation. Best-effort:
    /// partner-link discovery must never fail the Cloud Identity check itself.
    /// </summary>
    private static async Task<ChannelPartnerLink?> FindPartnerLinkForDomainAsync(
        IGoogleChannelClient channel,
        IDistributedCache cache,
        CheckCloudIdentityResult result,
        ILoggerFactory loggerFactory,
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

            // 1) Authoritative: the partner link's Cloud Identity primary domain matches.
            var match = links.FirstOrDefault(l =>
                string.Equals(l.ChannelPartner?.PrimaryDomain, domain, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            // 2) Fallback: match a reseller Cloud Identity ID found by the check against a link's
            //    ResellerCloudIdentityId (covers fresh INVITED links with no primary domain yet).
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

            // 3) Fallback: the domain we recorded when sending the invitation (stored at create time),
            //    keyed by the reseller Cloud Identity ID.
            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.ResellerCloudIdentityId))
                {
                    continue;
                }

                var invitedDomain = await cache.GetStringAsync(
                    ChannelPartnerLinksEndpoints.InvitedDomainCacheKey(link.ResellerCloudIdentityId!),
                    cancellationToken);
                if (string.Equals(invitedDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return link;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("AccountsEndpoints")
                .LogWarning(ex, "Failed to cross-correlate channel partner links for {Domain}.", result.Domain);
            return null;
        }
    }

    private static async Task<IResult> GetHistoryAsync(
        GChannelDbContext db,
        CancellationToken cancellationToken)
    {
        // Pull the most recent audit rows, then keep the latest entry per domain so the UI can
        // surface a short "recently checked" list with one-click recheck.
        var recent = await db.IdentityCheckLogs
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

        return Results.Ok(new IdentityCheckHistoryResult { Items = items });
    }
}
