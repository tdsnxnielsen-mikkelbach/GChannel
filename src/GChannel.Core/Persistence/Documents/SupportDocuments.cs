using GChannel.Core.GoogleApis;
using GChannel.Shared.Contracts;
using Marten;

namespace GChannel.Core.Persistence.Documents;

/// <summary>
/// Mirror of one Cloud Customer Care case with its comments and attachments. Refreshed by the worker when a
/// support event arrives (and by the daily catch-up), and by the API after a mutation or an explicit refresh.
/// </summary>
public sealed class SupportCaseRecord
{
    /// <summary>The case number (last segment of the resource name).</summary>
    public string Id { get; set; } = string.Empty;
    public SupportCase Case { get; set; } = null!;
    public List<SupportComment> Comments { get; set; } = [];
    public List<SupportAttachment> Attachments { get; set; } = [];

    /// <summary>Copied from <see cref="Case"/> for indexing.</summary>
    public string? State { get; set; }

    /// <summary>Copied from <see cref="Case"/> for indexing / ordering.</summary>
    public DateTimeOffset? UpdateTime { get; set; }

    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>One support event received from the Cloud Support event subscription (the feed, newest first).</summary>
public sealed class SupportEventRecord
{
    /// <summary>The Pub/Sub message id.</summary>
    public string Id { get; set; } = string.Empty;
    public SupportEvent Event { get; set; } = null!;
    public string? CaseId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Resource-name helpers for Cloud Support resources.</summary>
public static class SupportCaseNames
{
    /// <summary>
    /// The case a support resource belongs to: <c>organizations/1/cases/2/comments/3</c> →
    /// <c>organizations/1/cases/2</c>. Null when the name contains no <c>cases/{id}</c> segment.
    /// </summary>
    public static string? CaseNameOf(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName)) { return null; }
        var parts = resourceName.Trim().Trim('/').Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "cases" && parts[i + 1].Length > 0)
            {
                return string.Join('/', parts.Take(i + 2));
            }
        }
        return null;
    }

    /// <summary>The case number of a case resource name (its last segment).</summary>
    public static string CaseIdOf(string caseName)
    {
        var trimmed = caseName.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The case resource name for <paramref name="caseId"/> under <paramref name="organizationName"/>.</summary>
    public static string CaseName(string organizationName, string caseId) => $"{organizationName}/cases/{caseId}";
}

public static class SupportCaseStore
{
    /// <summary><see cref="SyncCursor"/> id of the daily catch-up (last full pass + notes).</summary>
    public const string CursorScope = "support-cases";

    /// <summary>Reads the case (with comments and attachments) from Google and stores it. The caller saves the session.</summary>
    public static async Task<SupportCaseRecord> RefreshAsync(IDocumentSession session, ISupportCasesService support, string caseName, CancellationToken ct)
    {
        var snapshot = await support.GetCaseAsync(caseName, ct);
        var record = new SupportCaseRecord
        {
            Id = snapshot.Case.CaseId,
            Case = snapshot.Case,
            Comments = snapshot.Comments.ToList(),
            Attachments = snapshot.Attachments.ToList(),
            State = snapshot.Case.State,
            UpdateTime = snapshot.Case.UpdateTime,
            LastSyncedUtc = DateTimeOffset.UtcNow,
        };
        session.Store(record);
        return record;
    }

    /// <summary>Error text for a failed Google call, trimmed for storage.</summary>
    public static string Describe(Exception ex)
    {
        var message = ex is Google.GoogleApiException g ? $"{g.HttpStatusCode}: {g.Error?.Message ?? g.Message}" : ex.Message;
        return message.Length > 500 ? message[..500] : message;
    }
}
