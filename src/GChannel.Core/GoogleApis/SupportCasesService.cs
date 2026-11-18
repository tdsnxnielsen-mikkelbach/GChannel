using GChannel.Core.Configuration;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Apis.CloudSupport.v2;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Data = Google.Apis.CloudSupport.v2.Data;

namespace GChannel.Core.GoogleApis;

/// <summary>A case read from Google with its comment thread and attachments.</summary>
public sealed record SupportCaseSnapshot(SupportCase Case, IReadOnlyList<SupportComment> Comments, IReadOnlyList<SupportAttachment> Attachments);

/// <summary>
/// Cloud Customer Care cases of the reseller's Google Cloud organization (Cloud Support API v2). Authenticated with
/// the reseller service-account key reused from <c>GoogleChannel:ServiceAccountKeyJson</c>, which needs
/// <c>roles/cloudsupport.techSupportEditor</c> (cases) and <c>roles/cloudsupport.supportSubscriptionViewer</c>
/// (event subscriptions; Editor to resume one) on the organization.
/// </summary>
public interface ISupportCasesService
{
    /// <summary>True when the organization and the service-account key are configured.</summary>
    bool IsConfigured { get; }

    /// <summary><c>organizations/{id}</c>, or empty when not configured.</summary>
    string OrganizationName { get; }

    /// <summary>Every case of the organization (all states).</summary>
    Task<IReadOnlyList<SupportCase>> ListCasesAsync(CancellationToken ct);

    Task<SupportCaseSnapshot> GetCaseAsync(string caseName, CancellationToken ct);
    Task<SupportCase> CreateCaseAsync(CreateSupportCaseRequest request, CancellationToken ct);
    Task AddCommentAsync(string caseName, string body, CancellationToken ct);
    Task CloseCaseAsync(string caseName, CancellationToken ct);
    Task EscalateCaseAsync(string caseName, string reason, string justification, CancellationToken ct);
    Task<IReadOnlyList<SupportClassification>> SearchClassificationsAsync(string? search, CancellationToken ct);
    Task<IReadOnlyList<SupportEventSubscriptionInfo>> ListEventSubscriptionsAsync(CancellationToken ct);

    /// <summary>Resumes a FAILING subscription: a no-op update of its topic makes Google re-verify publishing.</summary>
    Task<SupportEventSubscriptionInfo> ResumeEventSubscriptionAsync(string subscriptionName, CancellationToken ct);
}

public sealed class SupportCasesService : ISupportCasesService
{
    private readonly GoogleChannelOptions _channel;
    private readonly GoogleSupportOptions _support;
    private CloudSupportService? _client;

    public SupportCasesService(IOptions<GoogleChannelOptions> channel, IOptions<GoogleSupportOptions> support)
    {
        _channel = channel.Value;
        _support = support.Value;
    }

    public bool IsConfigured => _support.IsConfigured(_channel);

    public string OrganizationName => _support.OrganizationName;

    // Built lazily and reused (the REST client service is thread-safe).
    private CloudSupportService Client => _client ??= IsConfigured
        ? new CloudSupportService(new BaseClientService.Initializer
        {
            HttpClientInitializer = CredentialFactory.FromJson(ServiceAccountKeyJson(), "service_account")
                .CreateScoped(CloudSupportService.Scope.CloudPlatform),
            ApplicationName = _channel.ApplicationName,
        })
        : throw new InvalidOperationException(
            "Support cases are not configured: set GoogleSupport:OrganizationId and GoogleChannel:ServiceAccountKeyJson.");

    private string ServiceAccountKeyJson() =>
        !string.IsNullOrWhiteSpace(_channel.ServiceAccountKeyJson)
            ? _channel.ServiceAccountKeyJson
            : File.ReadAllText(_channel.ServiceAccountKeyPath);

    public async Task<IReadOnlyList<SupportCase>> ListCasesAsync(CancellationToken ct)
    {
        var cases = new List<SupportCase>();
        string? pageToken = null;
        do
        {
            var request = Client.Cases.List(OrganizationName);
            request.PageSize = 100;
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(ct);
            cases.AddRange((page.Cases ?? []).Select(Map));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));
        return cases;
    }

    public async Task<SupportCaseSnapshot> GetCaseAsync(string caseName, CancellationToken ct)
    {
        var supportCase = Map(await Client.Cases.Get(caseName).ExecuteAsync(ct));

        var comments = new List<SupportComment>();
        string? pageToken = null;
        do
        {
            var request = Client.Cases.Comments.List(caseName);
            request.PageSize = 100;
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(ct);
            comments.AddRange((page.Comments ?? []).Select(Map));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        var attachments = new List<SupportAttachment>();
        pageToken = null;
        do
        {
            var request = Client.Cases.Attachments.List(caseName);
            request.PageSize = 100;
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(ct);
            attachments.AddRange((page.Attachments ?? []).Select(Map));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new SupportCaseSnapshot(
            supportCase,
            comments.OrderBy(c => c.CreateTime ?? DateTimeOffset.MinValue).ToList(),
            attachments.OrderBy(a => a.CreateTime ?? DateTimeOffset.MinValue).ToList());
    }

    public async Task<SupportCase> CreateCaseAsync(CreateSupportCaseRequest request, CancellationToken ct)
    {
        var body = new Data.Case
        {
            DisplayName = request.DisplayName.Trim(),
            Description = request.Description.Trim(),
            Classification = new Data.CaseClassification { Id = request.ClassificationId },
            Priority = request.Priority,
            TestCase = request.TestCase,
            SubscriberEmailAddresses = request.SubscriberEmailAddresses
                .Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return Map(await Client.Cases.Create(body, OrganizationName).ExecuteAsync(ct));
    }

    public Task AddCommentAsync(string caseName, string body, CancellationToken ct) =>
        Client.Cases.Comments.Create(new Data.Comment { Body = body }, caseName).ExecuteAsync(ct);

    public Task CloseCaseAsync(string caseName, CancellationToken ct) =>
        Client.Cases.Close(new Data.CloseCaseRequest(), caseName).ExecuteAsync(ct);

    public Task EscalateCaseAsync(string caseName, string reason, string justification, CancellationToken ct) =>
        Client.Cases.Escalate(
            new Data.EscalateCaseRequest { Escalation = new Data.Escalation { Reason = reason, Justification = justification } },
            caseName).ExecuteAsync(ct);

    public async Task<IReadOnlyList<SupportClassification>> SearchClassificationsAsync(string? search, CancellationToken ct)
    {
        var request = Client.CaseClassifications.Search();
        request.PageSize = 100;
        if (!string.IsNullOrWhiteSpace(search))
        {
            request.Query = $"display_name:\"*{search.Trim().Replace("\"", string.Empty)}*\"";
        }
        var page = await request.ExecuteAsync(ct);
        return (page.CaseClassifications ?? [])
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new SupportClassification { Id = c.Id, DisplayName = c.DisplayName })
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SupportEventSubscriptionInfo>> ListEventSubscriptionsAsync(CancellationToken ct)
    {
        var result = new List<SupportEventSubscriptionInfo>();
        string? pageToken = null;
        do
        {
            var request = Client.Organizations.SupportEventSubscriptions.List(OrganizationName);
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(ct);
            result.AddRange((page.SupportEventSubscriptions ?? []).Select(Map));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));
        return result;
    }

    public async Task<SupportEventSubscriptionInfo> ResumeEventSubscriptionAsync(string subscriptionName, CancellationToken ct)
    {
        var current = await Client.Organizations.SupportEventSubscriptions.Get(subscriptionName).ExecuteAsync(ct);
        var patch = Client.Organizations.SupportEventSubscriptions.Patch(
            new Data.SupportEventSubscription { PubSubTopic = current.PubSubTopic }, subscriptionName);
        patch.UpdateMask = "pub_sub_topic";
        return Map(await patch.ExecuteAsync(ct));
    }

    public static SupportCase Map(Data.Case c) => new()
    {
        Name = c.Name,
        CaseId = SupportCaseNames.CaseIdOf(c.Name),
        DisplayName = c.DisplayName,
        Description = c.Description,
        State = c.State,
        Priority = c.Priority,
        ClassificationId = c.Classification?.Id,
        ClassificationName = c.Classification?.DisplayName,
        Escalated = c.Escalated ?? false,
        TestCase = c.TestCase ?? false,
        CreatorName = c.Creator?.DisplayName,
        CreatorEmail = c.Creator?.Email,
        ContactEmail = c.ContactEmail,
        SubscriberEmailAddresses = c.SubscriberEmailAddresses?.ToList() ?? [],
        LanguageCode = c.LanguageCode,
        TimeZone = c.TimeZone,
        CreateTime = c.CreateTimeDateTimeOffset,
        UpdateTime = c.UpdateTimeDateTimeOffset,
    };

    private static SupportComment Map(Data.Comment c) => new()
    {
        Name = c.Name,
        Body = c.PlainTextBody ?? c.Body,
        CreatorName = c.Creator?.DisplayName,
        CreatorEmail = c.Creator?.Email,
        FromGoogleSupport = c.Creator?.GoogleSupport ?? false,
        CreateTime = c.CreateTimeDateTimeOffset,
    };

    private static SupportAttachment Map(Data.Attachment a) => new()
    {
        Name = a.Name,
        Filename = a.Filename,
        MimeType = a.MimeType,
        SizeBytes = a.SizeBytes,
        CreatorName = a.Creator?.DisplayName,
        CreateTime = a.CreateTimeDateTimeOffset,
    };

    private static SupportEventSubscriptionInfo Map(Data.SupportEventSubscription s) => new()
    {
        Name = s.Name,
        PubSubTopic = s.PubSubTopic,
        State = s.State,
        FailureReason = s.FailureReason,
        CreateTime = s.CreateTimeDateTimeOffset,
        UpdateTime = s.UpdateTimeDateTimeOffset,
    };
}
