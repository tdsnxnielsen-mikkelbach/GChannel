using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Billing IAM security posture — read-only views over the BillingPostureAccounts / BillingPostureMembers
/// read-model the worker's nightly <c>BillingPostureSyncService</c> populates. No Google calls here.
/// </summary>
public static class BillingPostureEndpoints
{
    private const string CursorScope = "billing-posture";

    public static IEndpointRouteBuilder MapBillingPostureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/posture").WithTags("Billing posture");

        group.MapGet("", GetSummaryAsync)
            .WithName("BillingPostureSummary")
            .WithSummary("Estate-wide billing IAM security KPIs + per-account roll-up from the nightly scan.");

        group.MapGet("/accounts/{billingAccountId}/members", GetMembersAsync)
            .WithName("BillingPostureMembers")
            .WithSummary("Scanned members with posture for one billing account.");

        group.MapGet("/findings", GetFindingsAsync)
            .WithName("BillingPostureFindings")
            .WithSummary("Flat action list of non-compliant members and consumer admins across all accounts.");

        return app;
    }

    private static async Task<IResult> GetSummaryAsync(
        GChannelDbContext db,
        IOptions<GoogleChannelOptions> channelOptions,
        IOptions<GoogleBillingOptions> billingOptions,
        CancellationToken ct)
    {
        var unconfigured = !billingOptions.Value.PostureScanEnabled
            || !channelOptions.Value.HasServiceAccountCredential
            || string.IsNullOrWhiteSpace(channelOptions.Value.ImpersonateUser);

        var cursor = await db.SyncCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Scope == CursorScope, ct);
        var accounts = await db.BillingPostureAccounts.AsNoTracking().ToListAsync(ct);
        var members = (await db.BillingPostureMembers.AsNoTracking().ToListAsync(ct)).Select(ToContract).ToList();

        var byAccount = members.ToLookup(m => m.BillingAccountId, StringComparer.OrdinalIgnoreCase);
        var accountRows = accounts
            .Select(a =>
            {
                var rows = byAccount[a.BillingAccountId].ToList();
                return new BillingPostureAccount
                {
                    BillingAccountId = a.BillingAccountId,
                    DisplayName = a.DisplayName,
                    MasterBillingAccountId = a.MasterBillingAccountId,
                    CustomerId = a.CustomerId,
                    Direct = Bucket(rows.Where(m => !m.Inherited)),
                    Inherited = Bucket(rows.Where(m => m.Inherited)),
                    Error = a.Error,
                    LastSyncedUtc = a.LastSyncedUtc
                };
            })
            .OrderByDescending(a => a.HasDirectConsumerAdmin)
            .ThenByDescending(a => a.Direct.NonCompliant)
            .ThenBy(a => a.DisplayName ?? a.BillingAccountId)
            .ToList();

        // Estate buckets count DISTINCT grants (email+role) per slice: inherited members are stored once per
        // sub-account, so without de-duplication a 65-member master policy would be counted 30 times.
        var distinctDirect = members.Where(m => !m.Inherited).DistinctBy(m => (m.Email.ToLowerInvariant(), m.Role));
        var distinctInherited = members.Where(m => m.Inherited).DistinctBy(m => (m.Email.ToLowerInvariant(), m.Role));

        return Results.Ok(new BillingPostureSummary
        {
            AsOf = cursor?.LastFullPassUtc,
            Unconfigured = unconfigured,
            LastRunError = cursor?.Notes,
            AccountsScanned = accounts.Count,
            AccountsWithErrors = accounts.Count(a => a.Error is not null),
            AccountsWithDirectConsumerAdmin = accountRows.Count(a => a.HasDirectConsumerAdmin),
            AccountsWithNonCompliantDirect = accountRows.Count(a => a.Direct.NonCompliant > 0),
            AccountsWithNonCompliantInherited = accountRows.Count(a => a.Inherited.NonCompliant > 0),
            Direct = Bucket(distinctDirect),
            Inherited = Bucket(distinctInherited),
            Accounts = accountRows
        });
    }

    private static async Task<IResult> GetMembersAsync(string billingAccountId, GChannelDbContext db, CancellationToken ct)
    {
        var account = await db.BillingPostureAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.BillingAccountId == billingAccountId, ct);
        var rows = await db.BillingPostureMembers.AsNoTracking()
            .Where(m => m.BillingAccountId == billingAccountId)
            .ToListAsync(ct);

        return Results.Ok(new BillingPostureMembersResult
        {
            BillingAccountId = billingAccountId,
            Members = rows.Select(ToContract)
                .OrderBy(m => m.Inherited)
                .ThenByDescending(m => m.IsConsumerAdmin)
                .ThenByDescending(m => m.NonCompliant)
                .ThenBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AsOf = account?.LastSyncedUtc
        });
    }

    private static async Task<IResult> GetFindingsAsync(string? scope, GChannelDbContext db, CancellationToken ct)
    {
        var cursor = await db.SyncCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Scope == CursorScope, ct);
        var query = db.BillingPostureMembers.AsNoTracking();
        query = scope?.ToLowerInvariant() switch
        {
            "direct" => query.Where(m => !m.Inherited),
            "inherited" => query.Where(m => m.Inherited),
            _ => query
        };
        var rows = (await query.ToListAsync(ct)).Select(ToContract).ToList();

        var findings = rows
            .Where(m => m.NonCompliant || m.IsConsumerAdmin)
            .GroupBy(m => (Email: m.Email.ToLowerInvariant(), m.Role, m.Inherited))
            .Select(g => new BillingPostureFinding
            {
                Member = g.First(),
                BillingAccountIds = g.Select(m => m.BillingAccountId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList()
            })
            .OrderByDescending(f => f.Member.IsConsumerAdmin)
            .ThenBy(f => f.Member.Inherited)
            .ThenBy(f => f.Member.TwoSvOk)
            .ThenBy(f => f.Member.KeysOk)
            .ThenBy(f => f.Member.AdvancedProtectionOk)
            .ThenBy(f => f.Member.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new BillingPostureFindingsResult { Findings = findings, AsOf = cursor?.LastFullPassUtc });
    }

    private static PostureBucket Bucket(IEnumerable<BillingPostureMember> source)
    {
        var list = source.ToList();
        var assessable = list.Where(m => m.Assessable).ToList();
        return new PostureBucket
        {
            Members = list.Count,
            Assessable = assessable.Count,
            Compliant = assessable.Count(m => m.Compliant),
            NonCompliant = assessable.Count(m => !m.Compliant),
            TwoSvOff = assessable.Count(m => !m.TwoSvOk),
            NoAdvancedProtection = assessable.Count(m => !m.AdvancedProtectionOk),
            NoKeys = assessable.Count(m => !m.KeysOk),
            ConsumerAccounts = list.Count(m => m.AccountKind == GoogleAccountKind.Consumer),
            ConsumerAdmins = list.Count(m => m.IsConsumerAdmin),
            Unassessable = list.Count(m => m.AccountKind == GoogleAccountKind.Managed && !m.SecurityVisible)
        };
    }

    private static BillingPostureMember ToContract(BillingPostureMemberRecord r) => new()
    {
        BillingAccountId = r.BillingAccountId,
        Email = r.Email,
        MemberType = r.MemberType,
        Role = r.Role,
        RoleDisplayName = r.RoleDisplayName,
        Inherited = r.Inherited,
        AccountKind = (GoogleAccountKind)r.AccountKind,
        WorkspaceCustomerId = r.WorkspaceCustomerId,
        SecurityVisible = r.SecurityVisible,
        TwoStepVerificationEnrolled = r.TwoSvEnrolled,
        TwoStepVerificationEnforced = r.TwoSvEnforced,
        AdvancedProtection = (AdvancedProtectionStatus)r.AdvancedProtection,
        AdvancedProtectionAsOf = r.AdvancedProtectionAsOf,
        SecurityKeys = r.SecurityKeys,
        Passkeys = r.Passkeys,
        KeysAsOf = r.KeysAsOf,
        IsAdmin = r.IsAdmin,
        Suspended = r.Suspended
    };
}
