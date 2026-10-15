using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Reports.reports_v1;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using System.Net;

namespace GChannel.ApiService.Services;

/// <summary>
/// Reads per-user security posture from the reseller's own Google Workspace tenant via the Admin SDK:
/// Directory API <c>users.get</c> (2-Step Verification, suspension, admin) and Reports API user-accounts
/// audit events (Advanced Protection enroll/unenroll). Authenticates with the service-account key via
/// domain-wide delegation as <c>GoogleChannel:ImpersonateUser</c>, so it can only see users in that
/// tenant — members from customer tenants or consumer accounts come back as "not in directory".
/// </summary>
public interface IWorkspaceUserSecurityService
{
    /// <summary>True when a service-account key + impersonation user are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Looks the user up in the impersonated admin's own tenant when <paramref name="customerId"/> is null,
    /// or in a resold customer's tenant (Workspace customer id such as <c>C01aihlmh</c>) using the reseller
    /// admin's delegated access to that customer's Admin console.
    /// </summary>
    Task<WorkspaceUserSecurity> GetUserSecurityAsync(string email, string? customerId, CancellationToken cancellationToken);
}

/// <summary>Raw per-user signals from the Admin SDK; the identity endpoint folds these into <see cref="MemberIdentityInfo"/>.</summary>
public sealed record WorkspaceUserSecurity
{
    public required string Email { get; init; }

    /// <summary>True when <c>users.get</c> returned the user (i.e. they're in the reseller's tenant).</summary>
    public bool Found { get; init; }

    /// <summary>The Workspace customer id the user was found in (null = the impersonated admin's own tenant).</summary>
    public string? CustomerId { get; init; }

    public bool? Enrolled2Sv { get; init; }
    public bool? Enforced2Sv { get; init; }
    public bool? Suspended { get; init; }
    public bool? IsAdmin { get; init; }

    public AdvancedProtectionStatus AdvancedProtection { get; init; }
    public DateTimeOffset? AdvancedProtectionAsOf { get; init; }
    public string? AdvancedProtectionError { get; init; }

    public int? SecurityKeys { get; init; }
    public int? Passkeys { get; init; }
    public DateOnly? KeysAsOf { get; init; }

    /// <summary>Non-fatal lookup error (permission denied, API not enabled, DWD scope missing…).</summary>
    public string? Error { get; init; }

    /// <summary>True when a signal failed for a transient reason (quota / rate limit) and the result shouldn't be cached.</summary>
    public bool Transient { get; init; }
}

public sealed class WorkspaceUserSecurityService : IWorkspaceUserSecurityService
{
    /// <summary>DWD scopes the service account must be granted in the Workspace Admin console.</summary>
    public static readonly string[] RequiredScopes =
    [
        DirectoryService.Scope.AdminDirectoryUserReadonly,
        ReportsService.Scope.AdminReportsAuditReadonly,
        ReportsService.Scope.AdminReportsUsageReadonly,
    ];

    // The Reports API retains user-accounts audit events for 6 months; ask slightly wider so we never
    // trim retained data ourselves — the API just returns what it still holds.
    private static readonly TimeSpan AuditLookback = TimeSpan.FromDays(200);

    private readonly GoogleChannelOptions _options;
    private readonly ILogger<WorkspaceUserSecurityService> _logger;
    private DirectoryService? _directory;
    private ReportsService? _auditReports;
    private ReportsService? _usageReports;

    // The Reports API's per-user-per-second quota is low and its 403 "Request rate higher than
    // configured" is not retried by the client library. One shared token bucket paces every Reports
    // call (audit + usage, all requests) so a wide fan-out stays under it; RunReportsAsync adds
    // bounded retry for the stragglers.
    private readonly RequestPacer _reportsPacer = new(TimeSpan.FromMilliseconds(250));
    private static readonly TimeSpan[] RateLimitBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    public WorkspaceUserSecurityService(
        IOptions<GoogleChannelOptions> options,
        ILogger<WorkspaceUserSecurityService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.HasServiceAccountCredential && !string.IsNullOrWhiteSpace(_options.ImpersonateUser);

    // One credential PER scope. A domain-wide-delegation token request fails outright
    // (unauthorized_client) if ANY requested scope is missing from the Admin-console grant, so bundling
    // scopes would let one un-granted scope take down every signal. Scoped singly, a missing grant only
    // disables the signal that needs it.
    private GoogleCredential CreateCredential(string scope)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Workspace user lookups need GoogleChannel:ServiceAccountKeyJson (or KeyPath) and GoogleChannel:ImpersonateUser.");
        }

        var baseCredential = string.IsNullOrWhiteSpace(_options.ServiceAccountKeyJson)
            ? CredentialFactory.FromFile(_options.ServiceAccountKeyPath, "service_account")
            : CredentialFactory.FromJson(_options.ServiceAccountKeyJson, "service_account");

        return baseCredential.CreateScoped(scope).CreateWithUser(_options.ImpersonateUser);
    }

    private DirectoryService Directory => _directory ??= new DirectoryService(new BaseClientService.Initializer
    {
        HttpClientInitializer = CreateCredential(DirectoryService.Scope.AdminDirectoryUserReadonly),
        ApplicationName = _options.ApplicationName
    });

    private ReportsService AuditReports => _auditReports ??= new ReportsService(new BaseClientService.Initializer
    {
        HttpClientInitializer = CreateCredential(ReportsService.Scope.AdminReportsAuditReadonly),
        ApplicationName = _options.ApplicationName
    });

    private ReportsService UsageReports => _usageReports ??= new ReportsService(new BaseClientService.Initializer
    {
        HttpClientInitializer = CreateCredential(ReportsService.Scope.AdminReportsUsageReadonly),
        ApplicationName = _options.ApplicationName
    });

    private static string ScopeNotGranted(string scope) =>
        $"Domain-wide delegation scope not granted: {scope} (unauthorized_client). Add it to the service account's client ID in the Workspace Admin console.";

    private static bool IsRateLimited(GoogleApiException ex) =>
        ex.HttpStatusCode is HttpStatusCode.TooManyRequests
        || (ex.HttpStatusCode is HttpStatusCode.Forbidden
            && (ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)));

    // Paces a Reports call and retries quota 403/429s with backoff; rethrows the last failure.
    private async Task<T> RunReportsAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _reportsPacer.WaitAsync(cancellationToken);
            try
            {
                return await call();
            }
            catch (GoogleApiException ex) when (IsRateLimited(ex) && attempt < RateLimitBackoff.Length)
            {
                await Task.Delay(RateLimitBackoff[attempt], cancellationToken);
            }
        }
    }

    public async Task<WorkspaceUserSecurity> GetUserSecurityAsync(string email, string? customerId, CancellationToken cancellationToken)
    {
        const string fields = "primaryEmail,isEnrolledIn2Sv,isEnforcedIn2Sv,suspended,isAdmin";
        Google.Apis.Admin.Directory.directory_v1.Data.User? user;
        try
        {
            if (customerId is null)
            {
                var get = Directory.Users.Get(email);
                get.Fields = fields;
                user = await get.ExecuteAsync(cancellationToken);
            }
            else
            {
                // Resold customer: the reseller admin's delegated console access is honoured by the Directory
                // API when the resold customer's id is passed explicitly. users.get(email) alone resolves
                // against the caller's own tenant, so search within that customer instead.
                var list = Directory.Users.List();
                list.Customer = customerId;
                list.Query = $"email={email}";
                list.MaxResults = 1;
                list.Fields = $"users({fields})";
                var page = await list.ExecuteAsync(cancellationToken);
                user = page.UsersValue?.FirstOrDefault();
                if (user is null)
                {
                    return new WorkspaceUserSecurity { Email = email, Found = false, CustomerId = customerId };
                }
            }
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound)
        {
            // Not a user in the impersonated admin's tenant (customer tenant, group, consumer…).
            return new WorkspaceUserSecurity { Email = email, Found = false, CustomerId = customerId };
        }
        catch (TokenResponseException ex) when (ex.Error?.Error == "unauthorized_client")
        {
            var msg = ScopeNotGranted(DirectoryService.Scope.AdminDirectoryUserReadonly);
            _logger.LogWarning("Directory lookup for {Email}: {Message}", email, msg);
            return new WorkspaceUserSecurity { Email = email, Found = false, CustomerId = customerId, Error = msg };
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            // Either the user is in a tenant we can't read (no reseller console access to that customer),
            // or the DWD scope / API isn't enabled. Distinguish via the message so operators can tell a
            // setup problem from a foreign user.
            _logger.LogDebug(ex, "Directory lookup denied for {Email} (customer {CustomerId})", email, customerId ?? "own");
            return new WorkspaceUserSecurity
            {
                Email = email,
                Found = false,
                CustomerId = customerId,
                Error = IsSetupProblem(ex) ? $"Admin SDK: {ex.Message}" : null
            };
        }

        // Independent APIs — run them side by side to halve per-user latency.
        var auditTask = GetAdvancedProtectionAsync(email, customerId, cancellationToken);
        var keysTask = GetKeyCountsAsync(email, customerId, cancellationToken);
        await Task.WhenAll(auditTask, keysTask);
        var (status, asOf, auditError, auditTransient) = auditTask.Result;
        var (keys, passkeys, keysAsOf, keysTransient) = keysTask.Result;

        return new WorkspaceUserSecurity
        {
            Email = user.PrimaryEmail ?? email,
            Found = true,
            CustomerId = customerId,
            Enrolled2Sv = user.IsEnrolledIn2Sv,
            Enforced2Sv = user.IsEnforcedIn2Sv,
            Suspended = user.Suspended,
            IsAdmin = user.IsAdmin,
            AdvancedProtection = status,
            AdvancedProtectionAsOf = asOf,
            AdvancedProtectionError = auditError,
            SecurityKeys = keys,
            Passkeys = passkeys,
            KeysAsOf = keysAsOf,
            Transient = auditTransient || keysTransient,
            Error = auditError is not null && IsSetupMessage(auditError) ? auditError : null
        };
    }

    // There is no Directory/Reports *state* field for Advanced Protection, so infer it from the most
    // recent titanium_enroll / titanium_unenroll user-accounts audit event within the retention window.
    private async Task<(AdvancedProtectionStatus Status, DateTimeOffset? AsOf, string? Error, bool Transient)> GetAdvancedProtectionAsync(
        string email, string? customerId, CancellationToken cancellationToken)
    {
        try
        {
            var list = AuditReports.Activities.List(email, ActivitiesResource.ListRequest.ApplicationNameEnum.UserAccounts);
            list.StartTime = DateTimeOffset.UtcNow.Subtract(AuditLookback).ToString("o");
            list.MaxResults = 1000;
            list.CustomerId = customerId;

            DateTimeOffset? latest = null;
            var status = AdvancedProtectionStatus.Unknown;
            string? pageToken = null;
            do
            {
                list.PageToken = pageToken;
                var page = await RunReportsAsync(() => list.ExecuteAsync(cancellationToken), cancellationToken);
                foreach (var activity in page.Items ?? [])
                {
                    var when = activity.Id?.TimeDateTimeOffset;
                    if (when is null || (latest is not null && when <= latest))
                    {
                        continue;
                    }

                    foreach (var ev in activity.Events ?? [])
                    {
                        switch (ev.Name)
                        {
                            case "titanium_enroll":
                                latest = when;
                                status = AdvancedProtectionStatus.Enrolled;
                                break;
                            case "titanium_unenroll":
                                latest = when;
                                status = AdvancedProtectionStatus.NotEnrolled;
                                break;
                        }
                    }
                }

                pageToken = page.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return (status, latest, null, false);
        }
        catch (TokenResponseException ex) when (ex.Error?.Error == "unauthorized_client")
        {
            var msg = ScopeNotGranted(ReportsService.Scope.AdminReportsAuditReadonly);
            _logger.LogWarning("Audit lookup for {Email}: {Message}", email, msg);
            return (AdvancedProtectionStatus.Unavailable, null, msg, false);
        }
        catch (GoogleApiException ex)
        {
            var transient = IsRateLimited(ex);
            _logger.LogWarning("Reports activities.list failed for {Email} (customer {CustomerId}): {Status} {Message}",
                email, customerId ?? "own", (int)ex.HttpStatusCode, ex.Message);
            return (AdvancedProtectionStatus.Unavailable, null,
                transient ? "Reports API rate limit reached — retry shortly." : $"Reports API {(int)ex.HttpStatusCode}: {ex.Message}",
                transient);
        }
    }

    // Security-key / passkey counts from the user usage report. Advanced Protection REQUIRES a passkey or
    // security key, so these are the strongest live indicator Google exposes when no enroll event is
    // retained. Usage data lags a few days, so walk back until a day with data is found.
    private async Task<(int? Keys, int? Passkeys, DateOnly? AsOf, bool Transient)> GetKeyCountsAsync(
        string email, string? customerId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Usage data is normally available after ~2 days; probe only two dates to bound latency.
        foreach (var daysBack in new[] { 3, 6 })
        {
            var day = today.AddDays(-daysBack);
            try
            {
                var get = UsageReports.UserUsageReport.Get(email, day.ToString("yyyy-MM-dd"));
                get.Parameters = "accounts:num_security_keys,accounts:num_passkeys_enrolled";
                get.CustomerId = customerId;
                var report = await RunReportsAsync(() => get.ExecuteAsync(cancellationToken), cancellationToken);

                var entry = report.UsageReportsValue?.FirstOrDefault();
                if (entry?.Parameters is null || entry.Parameters.Count == 0)
                {
                    continue; // not yet available for this day
                }

                int? keys = null, passkeys = null;
                foreach (var p in entry.Parameters)
                {
                    switch (p.Name)
                    {
                        case "accounts:num_security_keys": keys = (int?)p.IntValue; break;
                        case "accounts:num_passkeys_enrolled": passkeys = (int?)p.IntValue; break;
                    }
                }

                return (keys, passkeys, day, false);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.BadRequest)
            {
                // "Data for dates later than X is not yet available" — try an earlier day.
                continue;
            }
            catch (TokenResponseException ex) when (ex.Error?.Error == "unauthorized_client")
            {
                _logger.LogWarning("Usage lookup for {Email}: {Message}", email, ScopeNotGranted(ReportsService.Scope.AdminReportsUsageReadonly));
                return (null, null, null, false);
            }
            catch (GoogleApiException ex)
            {
                _logger.LogWarning("Reports userUsageReport.get failed for {Email} (customer {CustomerId}): {Status} {Message}",
                    email, customerId ?? "own", (int)ex.HttpStatusCode, ex.Message);
                return (null, null, null, IsRateLimited(ex));
            }
        }

        return (null, null, null, false);
    }

    private static bool IsSetupProblem(GoogleApiException ex) => IsSetupMessage(ex.Message);

    // Token bucket of size 1: at most one call per interval across all concurrent callers.
    private sealed class RequestPacer(TimeSpan interval)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset slot;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                slot = _nextSlot > now ? _nextSlot : now;
                _nextSlot = slot + interval;
            }

            var delay = slot - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
        }
    }

    private static bool IsSetupMessage(string message) =>
        message.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
        || message.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
        || message.Contains("scope", StringComparison.OrdinalIgnoreCase)
        || message.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase);
}
