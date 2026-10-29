namespace GChannel.Core.Estate;

/// <summary>
/// Marten event-stream keys for the estate. Streams are string-identified and share one key space, so
/// each aggregate type gets a short prefix; the projected read-model documents use the full stream key
/// as their Marten identity and carry the bare Google id as a separate (indexed) property.
/// </summary>
public static class EstateStreams
{
    public const string CustomerPrefix = "cust:";
    public const string EntitlementPrefix = "ent:";
    public const string LinkPrefix = "link:";

    public static string Customer(string customerId) => CustomerPrefix + customerId;
    public static string Entitlement(string entitlementId) => EntitlementPrefix + entitlementId;
    public static string Link(string linkId) => LinkPrefix + linkId;
}

/// <summary>Which path observed a fact about the estate.</summary>
public enum ObservationSource
{
    /// <summary>The periodic read-model sync (reconciliation backstop).</summary>
    Sync,
    /// <summary>The result of a mutation the app itself issued to the Channel API.</summary>
    Mutation,
    /// <summary>A targeted refresh triggered by a Pub/Sub change notification.</summary>
    Notification,
}
