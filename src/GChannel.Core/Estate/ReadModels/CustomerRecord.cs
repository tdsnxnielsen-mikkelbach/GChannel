using GChannel.Core.Estate.Events;
using JasperFx.Events;

namespace GChannel.Core.Estate.ReadModels;

/// <summary>
/// Read-model document for one customer, projected <b>inline</b> from its event stream (so it is
/// immediately consistent with the events appended in the same transaction). Direct customers have a
/// null <see cref="OwningLinkId"/>; indirect ones are owned by a channel partner link.
/// Sync bookkeeping (freshness, errors) lives in <c>CustomerSyncState</c>, not here.
/// </summary>
public sealed class CustomerRecord
{
    /// <summary>Marten identity = the stream key (<see cref="EstateStreams.Customer"/>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Stream version (set by Marten); doubles as the optimistic-concurrency token.</summary>
    public int Version { get; set; }

    /// <summary>Short customer id (last segment of the customer resource name).</summary>
    public string CustomerId { get; set; } = string.Empty;
    public string? OrgName { get; set; }
    public string? Domain { get; set; }
    public string? CloudIdentityId { get; set; }
    /// <summary>Cloud Identity primary domain (may differ from <see cref="Domain"/>) — spend-attribution fallback key.</summary>
    public string? CloudIdentityDomain { get; set; }
    /// <summary>Owning channel partner link id, or null for the account's direct customers.</summary>
    public string? OwningLinkId { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    /// <summary>True when this customer IS one of our resellers buying for their own use — a DIRECT sale from us.</summary>
    public bool IsResellerSelf { get; set; }
    /// <summary>Soft-delete flag set when the customer disappears from a fresh list pass (or is deleted via the app).</summary>
    public bool IsDeleted { get; set; }
    /// <summary>When the customer was first observed.</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }
    /// <summary>When the projected state last changed (any event other than a mutation-request record).</summary>
    public DateTimeOffset LastChangedUtc { get; set; }
    /// <summary>Count of mutations the app issued against this customer (audit summary).</summary>
    public int MutationCount { get; set; }

    public static CustomerRecord Create(IEvent<CustomerDiscovered> e) => new()
    {
        CustomerId = e.Data.CustomerId,
        OrgName = e.Data.OrgName,
        Domain = e.Data.Domain,
        CloudIdentityId = e.Data.CloudIdentityId,
        CloudIdentityDomain = e.Data.CloudIdentityDomain,
        OwningLinkId = e.Data.OwningLinkId,
        CreateTime = e.Data.CreateTime,
        FirstSeenUtc = e.Timestamp,
        LastChangedUtc = e.Timestamp,
    };

    public void Apply(IEvent<CustomerDetailsChanged> e)
    {
        OrgName = e.Data.OrgName;
        Domain = e.Data.Domain;
        CloudIdentityId = e.Data.CloudIdentityId;
        CloudIdentityDomain = e.Data.CloudIdentityDomain;
        CreateTime = e.Data.CreateTime;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<CustomerOwnershipChanged> e)
    {
        OwningLinkId = e.Data.OwningLinkId;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<CustomerClassified> e)
    {
        IsResellerSelf = e.Data.IsResellerSelf;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<CustomerRemovedObserved> e)
    {
        IsDeleted = true;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<CustomerRestoredObserved> e)
    {
        IsDeleted = false;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(CustomerMutationRequested e) => MutationCount++;

    public void Apply(ChannelNotificationReceived e)
    {
        // Notifications are history, not state: nothing to project.
    }
}
