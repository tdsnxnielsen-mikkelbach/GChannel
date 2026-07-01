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

/// <summary>
/// Payload for provisioning a brand-new Cloud Identity for a customer that has none
/// (<c>customers.provisionCloudIdentity</c>). Unlike <see cref="ImportCustomerRequest"/> this is a
/// <b>long-running operation</b>: the API returns a <see cref="ChannelOperation"/> to poll on the
/// Operations page (§7) until it reaches <c>done</c>.
/// </summary>
public sealed record ProvisionCloudIdentityRequest
{
    /// <summary>Cloud Identity account details for the new organisation.</summary>
    public CloudIdentityDetails? CloudIdentity { get; init; }

    /// <summary>The initial admin user to create for the new Cloud Identity.</summary>
    public AdminUser? AdminUser { get; init; }

    /// <summary>When true, validate the request (and surface errors) without actually provisioning.</summary>
    public bool ValidateOnly { get; init; }
}

/// <summary>Cloud Identity account details supplied when provisioning. Maps to <c>CloudIdentityInfo</c>.</summary>
public sealed record CloudIdentityDetails
{
    /// <summary>A recovery / notification email outside the new domain.</summary>
    public string? AlternateEmail { get; init; }

    /// <summary>Contact phone number in international format.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Preferred language (e.g. "en-US").</summary>
    public string? LanguageCode { get; init; }
}

/// <summary>The initial admin user created with a new Cloud Identity. Maps to <c>AdminUser</c>.</summary>
public sealed record AdminUser
{
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }

    /// <summary>The admin's email within the customer's primary domain.</summary>
    public string? Email { get; init; }
}

/// <summary>A SKU a customer is eligible to purchase. Maps to <c>customers.listPurchasableSkus</c>.</summary>
public sealed record PurchasableSku
{
    public required string SkuName { get; init; }
    public string? SkuId { get; init; }
    public string? ProductId { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>
/// Which billing accounts a customer may use to purchase given SKUs
/// (<c>customers.queryEligibleBillingAccounts</c>). Only relevant for GCP / n-tier billing-gated SKUs;
/// results are grouped by the SKUs that share the same eligible billing accounts. Returns no monetary
/// amount — just which account is eligible.
/// </summary>
public sealed record EligibleBillingAccountsResult
{
    public IReadOnlyList<SkuBillingAccountGroup> Groups { get; init; } = [];
}

/// <summary>A set of SKUs that share the same eligible billing accounts. Maps to <c>SkuPurchaseGroup</c>.</summary>
public sealed record SkuBillingAccountGroup
{
    /// <summary>The SKU ids (short segments) that share these billing accounts.</summary>
    public IReadOnlyList<string> SkuIds { get; init; } = [];

    /// <summary>The billing accounts eligible for these SKUs.</summary>
    public IReadOnlyList<EligibleBillingAccount> BillingAccounts { get; init; } = [];
}

/// <summary>A billing account eligible for a purchase. Maps to <c>BillingAccount</c>.</summary>
public sealed record EligibleBillingAccount
{
    /// <summary>Resource name, e.g. "accounts/{account}/billingAccounts/{id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short id (the last path segment of <see cref="Name"/>).</summary>
    public string? Id { get; init; }

    public string? DisplayName { get; init; }
    public string? CurrencyCode { get; init; }
    public string? RegionCode { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
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
