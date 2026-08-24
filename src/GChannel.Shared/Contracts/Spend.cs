namespace GChannel.Shared.Contracts;

// §15 actual-spend contracts (BigQuery detailed billing export). Unlike the §11 pricing surface these
// are BILLED figures, not list-price estimates: the worker materialises them from the reseller's Cloud
// Billing → BigQuery export into the CustomerSpend / SpendDaily read-model, resolved to a Channel
// customer (and its owning reseller link) via the entitlement billing sub-account. Every result carries
// an "as of" timestamp because the export lags hours. See docs/todos/15-cloud-billing-bigquery-export.md.

/// <summary>One month of actual billed spend for a customer, in a single currency.</summary>
public sealed record CustomerSpendPoint
{
    /// <summary>Invoice month as <c>YYYYMM</c> (e.g. <c>202608</c>).</summary>
    public required string InvoiceMonth { get; init; }

    public required string Currency { get; init; }

    /// <summary>Net billed cost after credits (gross + credits).</summary>
    public decimal Cost { get; init; }

    /// <summary>Gross cost before credits.</summary>
    public decimal GrossCost { get; init; }

    /// <summary>Sum of credits applied (zero or negative).</summary>
    public decimal Credits { get; init; }
}

/// <summary>A customer's monthly actual-spend series.</summary>
public sealed record CustomerSpendResult
{
    public required string CustomerId { get; init; }

    /// <summary>Monthly points, oldest first. May span multiple currencies (one point per month per currency).</summary>
    public IReadOnlyList<CustomerSpendPoint> Months { get; init; } = [];

    /// <summary>When the underlying export rows were last synced (the freshest LastSyncedUtc), or null when none.</summary>
    public DateTimeOffset? AsOf { get; init; }

    /// <summary>True when the BigQuery export sync isn't configured/enabled (so there is no billed data to show).</summary>
    public bool Unconfigured { get; init; }
}

/// <summary>One day of actual billed spend for a customer, in a single currency, with a spike flag.</summary>
public sealed record SpendTrendPoint
{
    public DateOnly Day { get; init; }

    public required string Currency { get; init; }

    /// <summary>Net billed cost for the day.</summary>
    public decimal Cost { get; init; }

    /// <summary>True when the day's cost exceeds the rolling <c>mean + k·stddev</c> spike threshold.</summary>
    public bool IsSpike { get; init; }
}

/// <summary>A customer's daily actual-spend series with flagged spikes.</summary>
public sealed record SpendTrendResult
{
    public required string CustomerId { get; init; }

    /// <summary>Daily points, oldest first.</summary>
    public IReadOnlyList<SpendTrendPoint> Days { get; init; } = [];

    public DateTimeOffset? AsOf { get; init; }

    public bool Unconfigured { get; init; }
}

/// <summary>A customer ranked by recent billed spend (estate top-spenders view).</summary>
public sealed record TopSpender
{
    public required string CustomerId { get; init; }

    /// <summary>Owning reseller link id, or null for a direct customer.</summary>
    public string? OwningLinkId { get; init; }

    public required string Currency { get; init; }

    /// <summary>Net billed cost over the requested window.</summary>
    public decimal Cost { get; init; }
}

/// <summary>Estate-wide top spenders by billed cost.</summary>
public sealed record TopSpendersResult
{
    public IReadOnlyList<TopSpender> Spenders { get; init; } = [];

    public DateTimeOffset? AsOf { get; init; }

    public bool Unconfigured { get; init; }
}

/// <summary>Billed spend rolled up to an owning reseller (channel partner link), or the direct bucket.</summary>
public sealed record ResellerSpend
{
    /// <summary>Owning reseller link id, or null for the distributor's direct customers.</summary>
    public string? OwningLinkId { get; init; }

    public required string Currency { get; init; }

    /// <summary>Net billed cost over the window across the reseller's customers.</summary>
    public decimal Cost { get; init; }

    /// <summary>Distinct resolved customers contributing to this reseller's spend.</summary>
    public int CustomerCount { get; init; }
}

/// <summary>Billed spend grouped by owning reseller (channel partner) — the distributor's per-reseller rollup.</summary>
public sealed record ResellerSpendResult
{
    public IReadOnlyList<ResellerSpend> Resellers { get; init; } = [];

    public DateTimeOffset? AsOf { get; init; }

    public bool Unconfigured { get; init; }
}
