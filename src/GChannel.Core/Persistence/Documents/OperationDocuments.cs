using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Shared.Contracts;
using Marten;

namespace GChannel.Core.Persistence.Documents;

/// <summary>
/// The app's durable record of a Google long-running operation it submitted. Written by the API when a
/// mutating call returns an operation and updated by the worker's <c>ChannelOperationSaga</c> on every poll,
/// so the Operations page can list, correlate and explain operations (Google's <c>operations.list</c> is
/// not implemented for the Channel API, and the saga's own state is deleted once it finishes).
/// </summary>
public sealed class OperationLogRecord
{
    /// <summary>Full operation resource name, "operations/{id}".</summary>
    public string Id { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerDomain { get; set; }
    public string? EntitlementId { get; set; }
    public string? Subject { get; set; }
    public List<OperationDetail> Details { get; set; } = [];

    public bool Done { get; set; }
    public string? Error { get; set; }
    public int? ErrorCode { get; set; }
    public List<string> ErrorDetails { get; set; } = [];
    public string? OperationType { get; set; }
    public string? ResourceName { get; set; }
    public List<string> ResultResources { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int PollAttempts { get; set; }
    public string? Note { get; set; }
}

/// <summary>Writes, updates and projects <see cref="OperationLogRecord"/>s.</summary>
public static class OperationLog
{
    /// <summary>
    /// Stores the record for an operation a mutating call just returned (no-op without an operation name).
    /// Customer / entitlement names are copied from the read model so the log stays readable even if they
    /// change later; <paramref name="subject"/> describes what was requested when there is no entitlement
    /// yet (purchases, transfers). The caller saves the session.
    /// </summary>
    public static async Task RecordSubmittedAsync(
        IDocumentSession session, string? operationName, bool done, string? error,
        string customerId, string? entitlementId, string action, IReadOnlyList<OperationDetail> details,
        string? requestedBy, CancellationToken cancellationToken, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(operationName))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var record = new OperationLogRecord
        {
            Id = Normalize(operationName),
            Action = action,
            RequestedBy = requestedBy,
            SubmittedAt = now,
            CustomerId = customerId,
            EntitlementId = entitlementId,
            Details = [.. details],
            Done = done,
            Error = string.IsNullOrEmpty(error) ? null : error,
            CompletedAt = done ? now : null,
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject,
        };

        var customer = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), cancellationToken);
        record.CustomerName = customer?.OrgName;
        record.CustomerDomain = customer?.Domain;

        if (!string.IsNullOrEmpty(entitlementId)
            && await session.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(entitlementId), cancellationToken) is { } entitlement)
        {
            record.Subject = string.Join(" · ", new[] { entitlement.ProductName, entitlement.SkuName, entitlement.OfferName }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        }

        session.Store(record);
    }

    /// <summary>Copies a polled Google operation state onto the record.</summary>
    public static void ApplyPoll(OperationLogRecord record, ChannelOperation operation, DateTimeOffset checkedAt)
    {
        record.LastCheckedAt = checkedAt;
        record.PollAttempts++;
        record.Note = null;
        record.OperationType = operation.OperationType ?? record.OperationType;
        record.ResourceName = operation.ResourceName ?? record.ResourceName;
        if (operation.ResultResources.Count > 0)
        {
            record.ResultResources = [.. operation.ResultResources];
        }
        if (string.IsNullOrEmpty(record.EntitlementId) && !string.IsNullOrEmpty(operation.EntitlementId))
        {
            record.EntitlementId = operation.EntitlementId;
        }

        if (operation.Done)
        {
            record.Done = true;
            record.Error = operation.Error;
            record.ErrorCode = operation.ErrorCode;
            record.ErrorDetails = [.. operation.ErrorDetails];
            record.CompletedAt ??= checkedAt;
        }
    }

    /// <summary>The record as an operation contract (for when Google can't be asked live).</summary>
    public static ChannelOperation ToContract(OperationLogRecord record) => Merge(new ChannelOperation
    {
        Name = record.Id,
        Id = ShortId(record.Id),
        Done = record.Done,
        Error = record.Error,
        ErrorCode = record.ErrorCode,
        ErrorDetails = record.ErrorDetails,
        OperationType = record.OperationType,
        ResourceName = record.ResourceName,
        ResultResources = record.ResultResources,
    }, record);

    /// <summary>Overlays the app's tracking record on a (live) operation and fills gaps in its correlation.</summary>
    public static ChannelOperation Merge(ChannelOperation operation, OperationLogRecord? record)
    {
        if (record is null)
        {
            return operation;
        }

        return operation with
        {
            CustomerId = operation.CustomerId ?? record.CustomerId,
            EntitlementId = operation.EntitlementId ?? record.EntitlementId,
            OperationType = operation.OperationType ?? record.OperationType,
            Tracking = new OperationTracking
            {
                Action = record.Action,
                RequestedBy = record.RequestedBy,
                SubmittedAt = record.SubmittedAt,
                CompletedAt = record.CompletedAt,
                LastCheckedAt = record.LastCheckedAt,
                PollAttempts = record.PollAttempts,
                CustomerId = record.CustomerId,
                CustomerName = record.CustomerName,
                CustomerDomain = record.CustomerDomain,
                EntitlementId = record.EntitlementId,
                Subject = record.Subject,
                Details = record.Details,
                Note = record.Note,
            }
        };
    }

    /// <summary>Normalises an operation id or name to the "operations/{id}" document id.</summary>
    public static string Normalize(string operationIdOrName) =>
        operationIdOrName.StartsWith("operations/", StringComparison.OrdinalIgnoreCase)
            ? operationIdOrName
            : $"operations/{operationIdOrName}";

    private static string ShortId(string name) => name[(name.LastIndexOf('/') + 1)..];
}
