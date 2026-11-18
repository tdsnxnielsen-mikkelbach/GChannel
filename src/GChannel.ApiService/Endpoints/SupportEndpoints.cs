using GChannel.ApiService.Http;
using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Cloud Customer Care support cases of the reseller's Google Cloud organization. Reads come from the mirror the
/// worker keeps current from the support event subscription (Pub/Sub) and its daily catch-up; mutations
/// (create / comment / close / escalate) go to the Cloud Support API with the service account and refresh the
/// mirror right away. Google records the service account as the author, so comments carry the acting user.
/// </summary>
public static partial class SupportEndpoints
{
    private static readonly string[] EscalationReasons = ["RESOLUTION_TIME", "TECHNICAL_EXPERTISE", "BUSINESS_IMPACT"];
    private static readonly string[] Priorities = ["P0", "P1", "P2", "P3", "P4"];
    private const int MaxCommentLength = 10_000;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex CaseIdPattern();

    [WolverineGet("/api/support/cases")]
    public static async Task<SupportCasesResult> ListCases(
        bool? open, IQuerySession session, IOptions<GoogleSupportOptions> support, IOptions<GoogleChannelOptions> channel,
        CancellationToken cancellationToken)
    {
        var query = session.Query<SupportCaseRecord>();
        var records = open == true
            ? await query.Where(r => r.State != "CLOSED").ToListAsync(cancellationToken)
            : await query.ToListAsync(cancellationToken);
        var cursor = await session.LoadAsync<SyncCursor>(SupportCaseStore.CursorScope, cancellationToken);
        var job = await session.LoadAsync<RecurringJobState>(RecurringJobs.SupportCaseCatchUp, cancellationToken);

        return new SupportCasesResult
        {
            Cases = records.Select(r => r.Case).OrderByDescending(c => c.UpdateTime ?? c.CreateTime).ToList(),
            Organization = string.IsNullOrEmpty(support.Value.OrganizationName) ? null : support.Value.OrganizationName,
            Configured = support.Value.IsConfigured(channel.Value),
            SubscriberEnabled = support.Value.SubscriberEnabled(channel.Value),
            LastCatchUpUtc = cursor?.LastFullPassUtc,
            NextCatchUpUtc = job?.NextRunUtc,
            CatchUpRunning = job?.IsRunning ?? false,
            CatchUpError = job?.LastError,
        };
    }

    [WolverineGet("/api/support/cases/{caseId}")]
    public static async Task<IResult> GetCase(
        string caseId, bool? refresh, ISupportCasesService support, IDocumentSession session, CancellationToken cancellationToken)
    {
        if (!CaseIdPattern().IsMatch(caseId))
        {
            return Results.NotFound();
        }

        var record = await session.LoadAsync<SupportCaseRecord>(caseId, cancellationToken);
        string? refreshError = null;
        if ((refresh == true || record is null) && support.IsConfigured)
        {
            try
            {
                record = await SupportCaseStore.RefreshAsync(session, support, SupportCaseNames.CaseName(support.OrganizationName, caseId), cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (Google.GoogleApiException ex) when (record is not null)
            {
                refreshError = SupportCaseStore.Describe(ex);
            }
        }

        return record is null ? Results.NotFound() : Results.Ok(await DetailAsync(session, record, refreshError, cancellationToken));
    }

    [WolverinePost("/api/support/cases")]
    public static async Task<IResult> CreateCase(
        CreateSupportCaseRequest request, ISupportCasesService support, IDocumentSession session, CancellationToken cancellationToken)
    {
        if (!support.IsConfigured)
        {
            return NotConfigured();
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.DisplayName)) { errors["displayName"] = ["A title is required."]; }
        if (string.IsNullOrWhiteSpace(request.Description)) { errors["description"] = ["A description is required."]; }
        if (string.IsNullOrWhiteSpace(request.ClassificationId)) { errors["classificationId"] = ["Pick a classification."]; }
        if (!Priorities.Contains(request.Priority)) { errors["priority"] = ["Priority must be P0–P4."]; }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var created = await support.CreateCaseAsync(request, cancellationToken);
        var record = await SupportCaseStore.RefreshAsync(session, support, created.Name, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return Results.Ok(await DetailAsync(session, record, null, cancellationToken));
    }

    [WolverinePost("/api/support/cases/{caseId}/comments")]
    public static async Task<IResult> AddComment(
        string caseId, AddSupportCommentRequest request, ISupportCasesService support, IDocumentSession session,
        HttpContext http, CancellationToken cancellationToken)
    {
        if (!support.IsConfigured) { return NotConfigured(); }
        if (!CaseIdPattern().IsMatch(caseId)) { return Results.NotFound(); }
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > MaxCommentLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [$"A comment of 1–{MaxCommentLength} characters is required."] });
        }

        var caseName = SupportCaseNames.CaseName(support.OrganizationName, caseId);
        await support.AddCommentAsync(caseName, WithAuthor(request.Body.Trim(), RequestUser.NameOf(http)), cancellationToken);
        return await RefreshedAsync(session, support, caseName, cancellationToken);
    }

    [WolverinePost("/api/support/cases/{caseId}/close")]
    public static async Task<IResult> CloseCase(
        string caseId, ISupportCasesService support, IDocumentSession session, CancellationToken cancellationToken)
    {
        if (!support.IsConfigured) { return NotConfigured(); }
        if (!CaseIdPattern().IsMatch(caseId)) { return Results.NotFound(); }

        var caseName = SupportCaseNames.CaseName(support.OrganizationName, caseId);
        await support.CloseCaseAsync(caseName, cancellationToken);
        return await RefreshedAsync(session, support, caseName, cancellationToken);
    }

    [WolverinePost("/api/support/cases/{caseId}/escalate")]
    public static async Task<IResult> EscalateCase(
        string caseId, EscalateSupportCaseRequest request, ISupportCasesService support, IDocumentSession session,
        HttpContext http, CancellationToken cancellationToken)
    {
        if (!support.IsConfigured) { return NotConfigured(); }
        if (!CaseIdPattern().IsMatch(caseId)) { return Results.NotFound(); }

        var errors = new Dictionary<string, string[]>();
        if (!EscalationReasons.Contains(request.Reason)) { errors["reason"] = [$"Reason must be one of {string.Join(", ", EscalationReasons)}."]; }
        if (string.IsNullOrWhiteSpace(request.Justification)) { errors["justification"] = ["A justification is required."]; }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var caseName = SupportCaseNames.CaseName(support.OrganizationName, caseId);
        await support.EscalateCaseAsync(caseName, request.Reason, WithAuthor(request.Justification.Trim(), RequestUser.NameOf(http)), cancellationToken);
        return await RefreshedAsync(session, support, caseName, cancellationToken);
    }

    [WolverineGet("/api/support/classifications")]
    public static async Task<IResult> SearchClassifications(string? search, ISupportCasesService support, CancellationToken cancellationToken)
    {
        if (!support.IsConfigured) { return NotConfigured(); }
        return Results.Ok(new SupportClassificationsResult { Classifications = await support.SearchClassificationsAsync(search, cancellationToken) });
    }

    [WolverineGet("/api/support/subscription")]
    public static Task<SupportSubscriptionStatus> GetSubscription(
        ISupportCasesService support, IOptions<GoogleSupportOptions> supportOptions, IOptions<GoogleChannelOptions> channel,
        CancellationToken cancellationToken)
        => SubscriptionStatusAsync(support, supportOptions.Value.TopicName(channel.Value), cancellationToken);

    /// <summary>Resumes every FAILING event subscription of the organization (Google's documented no-op update).</summary>
    [WolverinePost("/api/support/subscription/resume")]
    public static async Task<IResult> ResumeSubscription(
        ISupportCasesService support, IOptions<GoogleSupportOptions> supportOptions, IOptions<GoogleChannelOptions> channel,
        CancellationToken cancellationToken)
    {
        if (!support.IsConfigured) { return NotConfigured(); }
        foreach (var subscription in await support.ListEventSubscriptionsAsync(cancellationToken))
        {
            if (string.Equals(subscription.State, "FAILING", StringComparison.OrdinalIgnoreCase))
            {
                await support.ResumeEventSubscriptionAsync(subscription.Name, cancellationToken);
            }
        }
        return Results.Ok(await SubscriptionStatusAsync(support, supportOptions.Value.TopicName(channel.Value), cancellationToken));
    }

    [WolverineGet("/api/support/events")]
    public static async Task<SupportEventsResult> ListEvents(IQuerySession session, CancellationToken cancellationToken)
    {
        var records = await session.Query<SupportEventRecord>()
            .OrderByDescending(r => r.ReceivedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        return new SupportEventsResult { Events = records.Select(r => r.Event).ToList() };
    }

    /// <summary>Google records the service account as the author, so the acting user is appended to the text.</summary>
    internal static string WithAuthor(string body, string? user) =>
        string.IsNullOrWhiteSpace(user) ? $"{body}\n\n— posted via GChannel" : $"{body}\n\n— posted by {user} via GChannel";

    private static async Task<SupportSubscriptionStatus> SubscriptionStatusAsync(ISupportCasesService support, string? expectedTopic, CancellationToken ct)
    {
        if (!support.IsConfigured)
        {
            return new SupportSubscriptionStatus { Configured = false, ExpectedTopic = expectedTopic };
        }
        try
        {
            return new SupportSubscriptionStatus
            {
                Configured = true,
                ExpectedTopic = expectedTopic,
                Subscriptions = await support.ListEventSubscriptionsAsync(ct),
            };
        }
        catch (Google.GoogleApiException ex)
        {
            return new SupportSubscriptionStatus { Configured = true, ExpectedTopic = expectedTopic, Error = SupportCaseStore.Describe(ex) };
        }
    }

    private static async Task<IResult> RefreshedAsync(IDocumentSession session, ISupportCasesService support, string caseName, CancellationToken ct)
    {
        var record = await SupportCaseStore.RefreshAsync(session, support, caseName, ct);
        await session.SaveChangesAsync(ct);
        return Results.Ok(await DetailAsync(session, record, null, ct));
    }

    private static async Task<SupportCaseDetail> DetailAsync(IQuerySession session, SupportCaseRecord record, string? refreshError, CancellationToken ct)
    {
        var events = await session.Query<SupportEventRecord>()
            .Where(e => e.CaseId == record.Id)
            .OrderByDescending(e => e.ReceivedAt)
            .Take(50)
            .ToListAsync(ct);
        return new SupportCaseDetail
        {
            Case = record.Case,
            Comments = record.Comments,
            Attachments = record.Attachments,
            Events = events.Select(e => e.Event).ToList(),
            LastSyncedUtc = record.LastSyncedUtc,
            RefreshError = refreshError,
        };
    }

    private static IResult NotConfigured() =>
        Results.Problem("Support cases are not configured: set GoogleSupport:OrganizationId and the service-account key.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
