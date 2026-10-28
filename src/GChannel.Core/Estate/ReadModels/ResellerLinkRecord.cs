using GChannel.Core.Estate.Events;
using JasperFx.Events;

namespace GChannel.Core.Estate.ReadModels;

/// <summary>
/// Read-model document for one channel partner link (downstream reseller), projected <b>inline</b> from
/// its event stream. Per-link sync bookkeeping (customer count, freshness, last error) lives in
/// <c>LinkSyncState</c>.
/// </summary>
public sealed class ResellerLinkRecord
{
    /// <summary>Marten identity = the stream key (<see cref="EstateStreams.Link"/>).</summary>
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }

    /// <summary>Short link id (last segment of the link resource name) — equals the partner customer's ChannelPartnerId.</summary>
    public string LinkId { get; set; } = string.Empty;
    public string? ResellerCloudId { get; set; }
    public string? PrimaryDomain { get; set; }
    public string LinkState { get; set; } = string.Empty;
    public DateTimeOffset? CreateTime { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastChangedUtc { get; set; }

    public static ResellerLinkRecord Create(IEvent<LinkDiscovered> e) => new()
    {
        LinkId = e.Data.LinkId,
        ResellerCloudId = e.Data.ResellerCloudId,
        PrimaryDomain = e.Data.PrimaryDomain,
        LinkState = e.Data.LinkState,
        CreateTime = e.Data.CreateTime,
        FirstSeenUtc = e.Timestamp,
        LastChangedUtc = e.Timestamp,
    };

    public void Apply(IEvent<LinkDetailsChanged> e)
    {
        ResellerCloudId = e.Data.ResellerCloudId;
        PrimaryDomain = e.Data.PrimaryDomain;
        CreateTime = e.Data.CreateTime;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<LinkStateChanged> e)
    {
        LinkState = e.Data.LinkState;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<LinkRemovedObserved> e)
    {
        IsDeleted = true;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<LinkRestoredObserved> e)
    {
        IsDeleted = false;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(LinkMutationRequested e)
    {
        // Audit history only.
    }
}
