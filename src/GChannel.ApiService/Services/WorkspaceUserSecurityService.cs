using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Reports.reports_v1;
using Google.Apis.Auth.OAuth2;
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
    private GoogleCredential? _credential;
    private DirectoryService? _directory;
    private ReportsService? _reports;

    public WorkspaceUserSecurityService(
        IOptions<GoogleChannelOptions> options,
        ILogger<WorkspaceUserSecurityService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.HasServiceAccountCredential && !string.IsNullOrWhiteSpace(_options.ImpersonateUser);

    private GoogleCredential Credential
    {
        get
        {
            if (_credential is not null)
            {
                return _credential;
            }

            if (!IsConfigured)
            {
                throw new InvalidOperationException(
                    "Workspace user lookups need GoogleChannel:ServiceAccountKeyJson (or KeyPath) and GoogleChannel:ImpersonateUser.");
            }

            var baseCredential = string.IsNullOrWhiteSpace(_options.ServiceAccountKeyJson)
                ? CredentialFactory.FromFile(_options.ServiceAccountKeyPath, "service_account")
                : CredentialFactory.FromJson(_options.ServiceAccountKeyJson, "service_account");

            return _credential = baseCredential
                .CreateScoped(RequiredScopes)
                .CreateWithUser(_options.ImpersonateUser);
        }
    }

    private DirectoryService Directory => _directory ??= new DirectoryService(new BaseClientService.Initializer
    {
        HttpClientInitializer = Credential,
        ApplicationName = _options.ApplicationName
    });

    private ReportsService Reports => _reports ??= new ReportsService(new BaseClientService.Initializer
    {
        HttpClientInitializer = Credential,
        ApplicationName = _options.ApplicationName
    });

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

        var (status, asOf, auditError) = await GetAdvancedProtectionAsync(email, customerId, cancellationToken);
        var (keys, passkeys, keysAsOf) = await GetKeyCountsAsync(email, customerId, cancellationToken);

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
            Error = auditError is not null && IsSetupMessage(auditError) ? auditError : null
        };
    }

    // There is no Directory/Reports *state* field for Advanced Protection, so infer it from the most
    // recent titanium_enroll / titanium_unenroll user-accounts audit event within the retention window.
    private async Task<(AdvancedProtectionStatus Status, DateTimeOffset? AsOf, string? Error)> GetAdvancedProtectionAsync(
        string email, string? customerId, CancellationToken cancellationToken)
    {
        try
        {
            var list = Reports.Activities.List(email, ActivitiesResource.ListRequest.ApplicationNameEnum.UserAccounts);
            list.StartTime = DateTimeOffset.UtcNow.Subtract(AuditLookback).ToString("o");
            list.MaxResults = 1000;
            list.CustomerId = customerId;

            DateTimeOffset? latest = null;
            var status = AdvancedProtectionStatus.Unknown;
            string? pageToken = null;
            do
            {
                list.PageToken = pageToken;
                var page = await list.ExecuteAsync(cancellationToken);
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

            return (status, latest, null);
        }
        catch (GoogleApiException ex)
        {
            _logger.LogWarning("Reports activities.list failed for {Email} (customer {CustomerId}): {Status} {Message}",
                email, customerId ?? "own", (int)ex.HttpStatusCode, ex.Message);
            return (AdvancedProtectionStatus.Unavailable, null, $"Reports API {(int)ex.HttpStatusCode}: {ex.Message}");
        }
    }

    // Security-key / passkey counts from the user usage report. Advanced Protection REQUIRES a passkey or
    // security key, so these are the strongest live indicator Google exposes when no enroll event is
    // retained. Usage data lags a few days, so walk back until a day with data is found.
    private async Task<(int? Keys, int? Passkeys, DateOnly? AsOf)> GetKeyCountsAsync(
        string email, string? customerId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var daysBack in new[] { 2, 3, 5, 8 })
        {
            var day = today.AddDays(-daysBack);
            try
            {
                var get = Reports.UserUsageReport.Get(email, day.ToString("yyyy-MM-dd"));
                get.Parameters = "accounts:num_security_keys,accounts:num_passkeys_enrolled";
                get.CustomerId = customerId;
                var report = await get.ExecuteAsync(cancellationToken);

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

                return (keys, passkeys, day);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.BadRequest)
            {
                // "Data for dates later than X is not yet available" — try an earlier day.
                continue;
            }
            catch (GoogleApiException ex)
            {
                _logger.LogWarning("Reports userUsageReport.get failed for {Email} (customer {CustomerId}): {Status} {Message}",
                    email, customerId ?? "own", (int)ex.HttpStatusCode, ex.Message);
                return (null, null, null);
            }
        }

        return (null, null, null);
    }

    private static bool IsSetupProblem(GoogleApiException ex) => IsSetupMessage(ex.Message);

    private static bool IsSetupMessage(string message) =>
        message.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
        || message.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
        || message.Contains("scope", StringComparison.OrdinalIgnoreCase)
        || message.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase);
}
