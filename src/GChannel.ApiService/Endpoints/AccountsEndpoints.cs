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
        HttpContext http,
        bool refresh,
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
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(result);
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
