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
    ];

    // The Reports API retains audit events for 6 months; older enrollments are invisible to us.
    private static readonly TimeSpan AuditLookback = TimeSpan.FromDays(180);

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
            Error = auditError
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
            _logger.LogDebug(ex, "Reports activities.list failed for {Email}", email);
            return (AdvancedProtectionStatus.Unknown, null, IsSetupProblem(ex) ? $"Reports API: {ex.Message}" : null);
        }
    }

    private static bool IsSetupProblem(GoogleApiException ex) =>
        ex.Message.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("scope", StringComparison.OrdinalIgnoreCase);
}
