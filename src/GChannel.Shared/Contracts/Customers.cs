namespace GChannel.Shared.Contracts;

// Customer management contracts shared between the Web client and the API service. These mirror a
// trimmed, UI-friendly subset of the Cloud Channel customer resources so UI code never references
// Google REST shapes directly.

/// <summary>A reseller customer. Maps to <c>accounts.customers</c>.</summary>
public sealed record Customer
{
    /// <summary>Resource name, e.g. "accounts/{account}/customers/{customer}".</summary>
    public required string Name { get; init; }

    /// <summary>Short id (the last path segment of <see cref="Name"/>).</summary>
    public required string Id { get; init; }

    public string? OrgDisplayName { get; init; }
    public string? Domain { get; init; }
    public string? CloudIdentityId { get; init; }
    public string? LanguageCode { get; init; }
    public string? ChannelPartnerId { get; init; }
    public DateTimeOffset? CreateTime { get; init; }

    public CustomerContact? PrimaryContact { get; init; }
    public CustomerAddress? Address { get; init; }
    public CustomerCloudIdentity? CloudIdentity { get; init; }
}

/// <summary>A customer's primary contact. Maps to <c>ContactInfo</c>.</summary>
public sealed record CustomerContact
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? Title { get; init; }
    public string? Phone { get; init; }
}

/// <summary>A customer's postal address. Maps to <c>PostalAddress</c>.</summary>
public sealed record CustomerAddress
{
    /// <summary>CLDR region code, e.g. "US". Required by Google when creating a customer.</summary>
    public string? RegionCode { get; init; }

    public string? PostalCode { get; init; }
    public string? AdministrativeArea { get; init; }
    public string? Locality { get; init; }
    public IReadOnlyList<string> AddressLines { get; init; } = [];
}

/// <summary>Read-only Cloud Identity summary for a customer. Maps to <c>CloudIdentityInfo</c>.</summary>
public sealed record CustomerCloudIdentity
{
    public string? CustomerType { get; init; }
    public string? PrimaryDomain { get; init; }
    public bool IsDomainVerified { get; init; }
    public string? AlternateEmail { get; init; }
    public string? AdminConsoleUri { get; init; }
}

/// <summary>Result of listing customers.</summary>
public sealed record CustomersResult
{
    public IReadOnlyList<Customer> Customers { get; init; } = [];
}

/// <summary>Create/update payload for a customer. <see cref="Id"/> is ignored on create.</summary>
public sealed record SaveCustomerRequest
{
    /// <summary>Customer id, set when updating; ignored when creating.</summary>
    public string? Id { get; init; }

    public required string OrgDisplayName { get; init; }

    /// <summary>The customer's primary domain. Required on create; not editable on update.</summary>
    public required string Domain { get; init; }

    public string? LanguageCode { get; init; }
    public CustomerContact? PrimaryContact { get; init; }
    public required CustomerAddress Address { get; init; }
}

/// <summary>
/// Import payload for a pre-existing Cloud Identity customer (<c>customers.import</c> /
/// <c>channelPartnerLinks.customers.import</c>). Supply exactly one of <see cref="Domain"/>,
/// <see cref="CloudIdentityId"/> or <see cref="PrimaryAdminEmail"/> to identify the customer. Returns
/// the <see cref="Customer"/> resource directly (synchronous — not a long-running operation).
/// </summary>
public sealed record ImportCustomerRequest
{
    /// <summary>The customer's primary domain (one identifier option).</summary>
    public string? Domain { get; init; }

    /// <summary>The customer's Cloud Identity id (one identifier option).</summary>
    public string? CloudIdentityId { get; init; }

    /// <summary>The customer's primary admin email (one identifier option).</summary>
    public string? PrimaryAdminEmail { get; init; }

    /// <summary>Optional transfer auth token when importing a customer owned by another reseller.</summary>
    public string? AuthToken { get; init; }

    /// <summary>When true, re-import overwrites an already-imported customer instead of failing.</summary>
    public bool OverwriteIfExists { get; init; }

    /// <summary>
    /// Optional Cloud Identity id of the channel partner that will own the customer. Ignored for the
    /// account-level import; the link-scoped import derives the owner from the route.
    /// </summary>
    public string? ChannelPartnerId { get; init; }
}

/// <summary>A SKU a customer is eligible to purchase. Maps to <c>customers.listPurchasableSkus</c>.</summary>
public sealed record PurchasableSku
{
    public required string SkuName { get; init; }
    public string? SkuId { get; init; }
    public string? ProductId { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>Result of listing a customer's purchasable SKUs for a product.</summary>
public sealed record PurchasableSkusResult
{
    public IReadOnlyList<PurchasableSku> Skus { get; init; } = [];
}

/// <summary>An offer a customer is eligible to purchase. Maps to <c>customers.listPurchasableOffers</c>.</summary>
public sealed record PurchasableOffer
{
    public required string OfferName { get; init; }
    public string? DisplayName { get; init; }
    public string? SkuId { get; init; }
    public string? ProductId { get; init; }
    public string? PriceReferenceId { get; init; }

    /// <summary>Wholesale list pricing per priced resource (seats, GB, etc.). Empty if not exposed.</summary>
    public IReadOnlyList<OfferPrice> Pricing { get; init; } = [];

    /// <summary>Human-friendly payment cycle (e.g. "Monthly", "Annual"). Null when unknown.</summary>
    public string? PaymentCycle { get; init; }
}

/// <summary>Result of listing a customer's purchasable offers for a SKU.</summary>
public sealed record PurchasableOffersResult
{
    public IReadOnlyList<PurchasableOffer> Offers { get; init; } = [];
}
