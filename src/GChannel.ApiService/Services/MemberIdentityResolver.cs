using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GChannel.ApiService.Services;

/// <summary>
/// Classifies the Google Accounts behind billing IAM member emails (consumer vs managed) and reads their
/// security posture (2SV, Advanced Protection, passkeys / security keys) where Google lets us. Shared by
/// the on-demand <c>POST /api/billing/members/identity</c> endpoint and the worker's nightly posture
/// scan. Stateless apart from the per-call memo caches; takes the Channel client + DbContext per call
/// because the API uses a per-request user token while the worker uses the service account.
/// </summary>
public sealed class MemberIdentityResolver(
    IWorkspaceUserSecurityService workspace,
    IDistributedCache cache,
    IOptions<GoogleChannelOptions> options,
    ILogger<MemberIdentityResolver> logger)
{
    public static readonly HashSet<string> ConsumerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com"
    };

    // Parallel member lookups per batch. Directory is generous (2,400/min) but the Reports API's
    // per-second quota is tight, so Reports calls are additionally paced inside the service; 4-wide
    // keeps Directory latency overlapped without piling up behind that pacer.
    public const int DefaultConcurrency = 4;

    public bool WorkspaceConfigured => workspace.IsConfigured;

    /// <summary>
    /// Per member email: (1) gmail.com → consumer; (2) found via the Admin SDK (our tenant, or a resold
    /// customer the impersonated admin has reseller access to) → managed, plus 2SV + Advanced Protection +
    /// key counts; (3) otherwise ask the Channel API whether the email's domain has a Cloud Identity /
    /// Workspace tenant — a domain-verified tenant means managed, a team account means partially managed,
    /// none means consumer; (4) for a managed resold domain, retry the Admin SDK lookup scoped to that
    /// customer's id. Results are cached per email in Redis (quota failures excepted); unreadable signals
    /// come back null/Unknown rather than failing.
    /// </summary>
    public async Task<MemberIdentityResult> ResolveAsync(
        IEnumerable<string> emails,
        IGoogleChannelClient channel,
        GChannelDbContext db,
        bool refresh,
        CancellationToken cancellationToken,
        int concurrency = DefaultConcurrency)
    {
        var list = emails
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        var ttl = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(60, options.Value.CacheSeconds))
        };

        // Domain → Cloud Identity check, memoised across this batch (and in Redis via the same key the
        // Cloud-identity-check page uses, so both features share one Channel API call per domain).
        // Lazy<Task> so concurrent members of the same domain await a single in-flight call.
        var domainCache = new ConcurrentDictionary<string, Lazy<Task<CheckCloudIdentityResult?>>>(StringComparer.OrdinalIgnoreCase);
        Task<CheckCloudIdentityResult?> CheckDomainAsync(string domain) =>
            domainCache.GetOrAdd(domain, d => new Lazy<Task<CheckCloudIdentityResult?>>(async () =>
            {
                var key = $"cid:{d}:";
                try
                {
                    var cached = await cache.GetStringAsync(key, cancellationToken);
                    if (cached is not null)
                    {
                        return JsonSerializer.Deserialize<CheckCloudIdentityResult>(cached);
                    }

                    var result = await channel.CheckCloudIdentityAsync(new CheckCloudIdentityRequest { Domain = d }, cancellationToken);
                    await cache.SetStringAsync(key, JsonSerializer.Serialize(result), ttl, cancellationToken);
                    return result;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Cloud Identity check failed for member domain {Domain}", d);
                    return null;
                }
            })).Value;

        // Workspace customer id ("C01aihlmh") for a managed domain: prefer the id the Channel API just
        // returned for the DOMAIN-type account; fall back to our read-model customer row for the domain.
        // The DbContext isn't thread-safe, so the fallback query is serialised behind a gate.
        var dbGate = new SemaphoreSlim(1, 1);
        var customerIdCache = new ConcurrentDictionary<string, Lazy<Task<string?>>>(StringComparer.OrdinalIgnoreCase);
        Task<string?> ResolveCustomerIdAsync(string domain, CheckCloudIdentityResult? cid) =>
            customerIdCache.GetOrAdd(domain, d => new Lazy<Task<string?>>(async () =>
            {
                var id = cid?.Accounts.FirstOrDefault(a => a.IsDomain && a.Existing)?.CustomerCloudIdentityId;
                if (!string.IsNullOrWhiteSpace(id)) { return id; }

                await dbGate.WaitAsync(cancellationToken);
                try
                {
                    return await db.CustomerRecords.AsNoTracking()
                        .Where(c => c.CloudIdentityId != null && (c.Domain == d || c.CloudIdentityDomain == d))
                        .Select(c => c.CloudIdentityId)
                        .FirstOrDefaultAsync(cancellationToken);
                }
                finally
                {
                    dbGate.Release();
                }
            })).Value;

        string? directoryError = null;
        var errorLock = new object();
        void RecordDirectoryError(string message) { lock (errorLock) { directoryError ??= message; } }

        async Task<MemberIdentityInfo> ResolveOneAsync(string email)
        {
            var cacheKey = $"member-identity:{email}";
            if (!refresh)
            {
                var cachedJson = await cache.GetStringAsync(cacheKey, cancellationToken);
                if (cachedJson is not null)
                {
                    var cachedInfo = JsonSerializer.Deserialize<MemberIdentityInfo>(cachedJson);
                    if (cachedInfo is not null) { return cachedInfo; }
                }
            }

            var domain = email[(email.IndexOf('@') + 1)..];
            var info = new MemberIdentityInfo { Email = email };
            var transient = false;

            if (ConsumerDomains.Contains(domain))
            {
                info = info with
                {
                    AccountKind = GoogleAccountKind.Consumer,
                    AccountKindReason = $"{domain} is a consumer Google Account domain."
                };
            }
            else
            {
                WorkspaceUserSecurity? sec = null;
                if (workspace.IsConfigured)
                {
                    try
                    {
                        sec = await workspace.GetUserSecurityAsync(email, customerId: null, cancellationToken);
                        if (sec.Error is not null) { RecordDirectoryError(sec.Error); }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Workspace security lookup failed for {Email}", email);
                        RecordDirectoryError(ex.Message);
                    }
                }

                if (sec is { Found: true })
                {
                    transient = sec.Transient;
                    // users.get resolves resold-customer users too; the service flags those via CustomerId.
                    var viaReseller = sec.CustomerId is not null;
                    info = info with
                    {
                        AccountKind = GoogleAccountKind.Managed,
                        AccountKindReason = viaReseller
                            ? $"{domain} is a resold Workspace customer ({sec.CustomerId}) we have reseller admin access to."
                            : "User exists in the reseller's Google Workspace directory.",
                        InResellerDirectory = !viaReseller,
                        WorkspaceCustomerId = sec.CustomerId,
                        TwoStepVerificationEnrolled = sec.Enrolled2Sv,
                        TwoStepVerificationEnforced = sec.Enforced2Sv,
                        Suspended = sec.Suspended,
                        IsAdmin = sec.IsAdmin,
                        AdvancedProtection = sec.AdvancedProtection,
                        AdvancedProtectionAsOf = sec.AdvancedProtectionAsOf,
                        AdvancedProtectionError = sec.AdvancedProtectionError,
                        SecurityKeys = sec.SecurityKeys,
                        Passkeys = sec.Passkeys,
                        KeysAsOf = sec.KeysAsOf,
                        Error = sec.Error
                    };
                }
                else
                {
                    var cid = await CheckDomainAsync(domain);

                    // Resold customer tenant: retry the Admin SDK lookup scoped to its customer id. Works only
                    // for customers who granted us reseller Admin-console access; otherwise Found=false.
                    WorkspaceUserSecurity? resold = null;
                    if (cid is { Exists: true } && workspace.IsConfigured)
                    {
                        var customerId = await ResolveCustomerIdAsync(domain, cid);
                        if (customerId is not null)
                        {
                            try
                            {
                                resold = await workspace.GetUserSecurityAsync(email, customerId, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Resold-customer security lookup failed for {Email} in {CustomerId}", email, customerId);
                            }
                        }
                    }

                    transient = resold?.Transient == true;
                    info = (cid, resold) switch
                    {
                        (_, { Found: true }) => info with
                        {
                            AccountKind = GoogleAccountKind.Managed,
                            AccountKindReason = $"{domain} is a resold Workspace customer ({resold.CustomerId}) we have reseller admin access to.",
                            WorkspaceCustomerId = resold.CustomerId,
                            TwoStepVerificationEnrolled = resold.Enrolled2Sv,
                            TwoStepVerificationEnforced = resold.Enforced2Sv,
                            Suspended = resold.Suspended,
                            IsAdmin = resold.IsAdmin,
                            AdvancedProtection = resold.AdvancedProtection,
                            AdvancedProtectionAsOf = resold.AdvancedProtectionAsOf,
                            AdvancedProtectionError = resold.AdvancedProtectionError,
                            SecurityKeys = resold.SecurityKeys,
                            Passkeys = resold.Passkeys,
                            KeysAsOf = resold.KeysAsOf,
                            Error = resold.Error
                        },
                        ({ Exists: true }, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Managed,
                            AccountKindReason = $"{domain} has a domain-verified Cloud Identity / Workspace tenant we have no reseller admin access to — 2SV not visible."
                        },
                        ({ HasNonDomainAccounts: true }, _) => info with
                        {
                            AccountKind = GoogleAccountKind.ManagedTeam,
                            AccountKindReason = $"{domain} only has a Cloud Identity team (email-verified) account."
                        },
                        (not null, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Consumer,
                            AccountKindReason = $"No Cloud Identity / Workspace tenant exists for {domain} — unmanaged Google Account."
                        },
                        (null, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Unknown,
                            AccountKindReason = "Cloud Identity check for the domain was unavailable."
                        }
                    };
                }
            }

            // A quota/rate-limit failure must not be pinned in the cache for the full TTL — leave it
            // uncached so the next load (or the user's refresh) retries against a replenished bucket.
            if (!transient)
            {
                await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(info), ttl, cancellationToken);
            }
            return info;
        }

        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var members = await Task.WhenAll(list.Select(async email =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await ResolveOneAsync(email); }
            finally { gate.Release(); }
        }));

        return new MemberIdentityResult
        {
            Members = members,
            DirectoryAvailable = workspace.IsConfigured && directoryError is null,
            DirectoryError = workspace.IsConfigured
                ? directoryError
                : "Admin SDK lookups need GoogleChannel:ServiceAccountKeyJson and GoogleChannel:ImpersonateUser."
        };
    }
}
