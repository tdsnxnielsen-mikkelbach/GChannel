using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

// Resource mapping & name helpers — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    /// <summary>Maps a Google entitlement resource to the UI-facing <see cref="Entitlement"/> contract.</summary>
    private static Entitlement MapEntitlement(GoogleCloudChannelV1Entitlement entitlement, CatalogLookups lookups = default)    {
        var offerId = LastSegment(entitlement.Offer);
        var productId = entitlement.ProvisionedService?.ProductId;
        var skuId = entitlement.ProvisionedService?.SkuId;

        string? offerDisplayName = null;
        string? skuDisplayName = null;
        string? productDisplayName = null;

        // 1. Offer catalog (offers.list) — the richest source: resolves offer, SKU and product names
        //    in one hit. Only present while the entitlement's specific offer is still listed.
        if (!string.IsNullOrEmpty(offerId) && lookups.Offers is { } offers && offers.TryGetValue(offerId, out var offerDisplay))
        {
            offerDisplayName = offerDisplay.OfferDisplayName;
            skuDisplayName = offerDisplay.SkuDisplayName;
            productDisplayName = offerDisplay.ProductDisplayName;
        }

        // 2. SKU catalog (products.skus.list) — covers entitlements whose offer is no longer listed,
        //    which is the usual reason the UI fell back to raw SKU/product ids.
        if (string.IsNullOrEmpty(skuDisplayName) && !string.IsNullOrEmpty(skuId)
            && lookups.Skus is { } skus && skus.TryGetValue(skuId, out var skuDisplay))
        {
            skuDisplayName = skuDisplay.SkuDisplayName;
            if (string.IsNullOrEmpty(productDisplayName))
            {
                productDisplayName = skuDisplay.ProductDisplayName;
            }
        }

        // 3. Product catalog (products.list) — last resort for the product name.
        if (string.IsNullOrEmpty(productDisplayName) && !string.IsNullOrEmpty(productId)
            && lookups.Products is { } productNames && productNames.TryGetValue(productId, out var productName))
        {
            productDisplayName = productName;
        }

        return new()
        {
            Name = entitlement.Name ?? string.Empty,
            Id = LastSegment(entitlement.Name),
            OfferName = entitlement.Offer,
            OfferId = offerId,
            OfferDisplayName = offerDisplayName,
            ProductId = productId,
            ProductDisplayName = productDisplayName,
            SkuId = skuId,
            SkuDisplayName = skuDisplayName,
            ProvisioningState = entitlement.ProvisioningState,
            PurchaseOrderId = entitlement.PurchaseOrderId,
            BillingAccount = entitlement.BillingAccount,
            CreateTime = entitlement.CreateTimeDateTimeOffset,
            UpdateTime = entitlement.UpdateTimeDateTimeOffset,
            SuspensionReasons = entitlement.SuspensionReasons is { } reasons ? [.. reasons] : [],
            IsTrial = entitlement.TrialSettings?.Trial ?? false,
            TrialEndTime = entitlement.TrialSettings?.EndTimeDateTimeOffset,
            Commitment = entitlement.CommitmentSettings is { } commitment
                ? new EntitlementCommitment
                {
                    StartTime = commitment.StartTimeDateTimeOffset,
                    EndTime = commitment.EndTimeDateTimeOffset,
                    RenewalEnabled = commitment.RenewalSettings?.EnableRenewal,
                    PaymentPlan = commitment.RenewalSettings?.PaymentPlan
                }
                : null,
            Parameters = entitlement.Parameters is { } parameters
                ? parameters.Select(MapParameter).ToList()
                : []
        };
    }

    /// <summary>Friendly display names for an offer (and its SKU/product), resolved from the Catalog.</summary>
    private readonly record struct OfferDisplay(string? OfferDisplayName, string? SkuDisplayName, string? ProductDisplayName);

    /// <summary>Friendly display names for a SKU (and its product), resolved from <c>products.skus.list</c>.</summary>
    private readonly record struct SkuDisplay(string? SkuDisplayName, string? ProductDisplayName);

    /// <summary>Catalog display-name lookups for entitlement and dashboard labels.</summary>
    private readonly record struct CatalogLookups(
        IReadOnlyDictionary<string, OfferDisplay> Offers,
        IReadOnlyDictionary<string, string> Products,
        IReadOnlyDictionary<string, SkuDisplay> Skus);

    /// <summary>
    /// Builds offer-, product- and (optionally) SKU-level display-name lookups from the reseller's
    /// catalog. The product map is seeded from the full <c>products.list</c> catalog (authoritative,
    /// so it covers products whose specific offer is no longer listed) and supplemented from
    /// <c>offers.list</c>, which also yields the offer map that turns opaque entitlement offer ids
    /// into names. When <paramref name="includeSkus"/> is set, a SKU-id -> name map is also built
    /// from <c>products.skus.list</c> per product, so an entitlement's SKU/product names resolve even
    /// when its specific offer is no longer listed. Failures are non-fatal: callers fall back to raw ids.
    /// </summary>
    private async Task<CatalogLookups> BuildCatalogLookupsAsync(
        CloudchannelService service, CancellationToken cancellationToken, bool includeSkus = false)
    {
        var offers = new Dictionary<string, OfferDisplay>(StringComparer.OrdinalIgnoreCase);
        var products = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Authoritative product-id -> name map from the full product catalog. Covers products whose
        // specific offer is no longer listed (the dashboard would otherwise show raw product ids).
        try
        {
            string? pageToken = null;
            do
            {
                var request = service.Products.List();
                request.Account = _options.AccountName;
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var product in response.Products ?? [])
                {
                    var productId = LastSegment(product.Name);
                    var productName = product.MarketingInfo?.DisplayName;
                    if (!string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(productName))
                    {
                        products[productId] = productName;
                    }
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list products for display-name resolution.");
        }

        // Offer-id -> display names (also supplements the product map for any product not already
        // covered above).
        try
        {
            string? pageToken = null;
            do
            {
                var request = service.Accounts.Offers.List(_options.AccountName);
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var offer in response.Offers ?? [])
                {
                    var offerId = LastSegment(offer.Name);
                    if (!string.IsNullOrEmpty(offerId))
                    {
                        offers[offerId] = new OfferDisplay(
                            offer.MarketingInfo?.DisplayName,
                            offer.Sku?.MarketingInfo?.DisplayName,
                            offer.Sku?.Product?.MarketingInfo?.DisplayName);
                    }

                    var productId = LastSegment(offer.Sku?.Product?.Name);
                    var productName = offer.Sku?.Product?.MarketingInfo?.DisplayName;
                    if (!string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(productName))
                    {
                        products.TryAdd(productId, productName);
                    }
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve catalog offer display names; entitlements may show ids.");
        }

        // SKU-id -> name map (and its product name) from the authoritative per-product SKU catalog.
        // This is what lets an entitlement resolve its SKU/product names when its specific offer is no
        // longer listed in offers.list (the common reason the UI shows raw ids). Bounded concurrency
        // keeps the per-product fan-out quick; each product is independently graceful.
        var skus = new Dictionary<string, SkuDisplay>(StringComparer.OrdinalIgnoreCase);
        if (includeSkus && products.Count > 0)
        {
            var skuGate = new object();
            using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));
            await Task.WhenAll(products.Select(async kv =>
            {
                var productId = kv.Key;
                var productName = kv.Value;
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    string? skuToken = null;
                    do
                    {
                        var request = service.Products.Skus.List($"products/{productId}");
                        request.Account = _options.AccountName;
                        request.PageToken = skuToken;
                        var response = await request.ExecuteAsync(cancellationToken);

                        foreach (var sku in response.Skus ?? [])
                        {
                            var skuId = LastSegment(sku.Name);
                            if (string.IsNullOrEmpty(skuId))
                            {
                                continue;
                            }

                            var display = new SkuDisplay(sku.MarketingInfo?.DisplayName, productName);
                            lock (skuGate)
                            {
                                skus[skuId] = display;
                            }
                        }

                        skuToken = response.NextPageToken;
                    }
                    while (!string.IsNullOrEmpty(skuToken));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not list SKUs for product {Product} during display-name resolution.", productId);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        return new CatalogLookups(offers, products, skus);
    }

    /// <summary>Offer id -&gt; friendly display names (a thin view over <see cref="BuildCatalogLookupsAsync"/>).</summary>
    private async Task<IReadOnlyDictionary<string, OfferDisplay>> BuildOfferDisplayLookupAsync(
        CloudchannelService service, CancellationToken cancellationToken)
        => (await BuildCatalogLookupsAsync(service, cancellationToken)).Offers;

    /// <summary>Maps a Google entitlement-change resource to the UI-facing <see cref="EntitlementChange"/>.</summary>
    private static EntitlementChange MapEntitlementChange(GoogleCloudChannelV1EntitlementChange change, IReadOnlyDictionary<string, OfferDisplay>? offerLookup = null)
    {
        var offerId = LastSegment(change.Offer);
        string? offerDisplayName = null;
        if (offerLookup is not null && !string.IsNullOrEmpty(offerId) && offerLookup.TryGetValue(offerId, out var display))
        {
            offerDisplayName = display.OfferDisplayName;
        }

        return new()
        {
            ChangeType = change.ChangeType,
            OfferId = offerId,
            OfferDisplayName = offerDisplayName,
            OperatorType = change.OperatorType,
            CreateTime = change.CreateTimeDateTimeOffset,
            Reason = change.ActivationReason
                ?? change.CancellationReason
                ?? change.SuspensionReason
                ?? change.OtherChangeReason
        };
    }

    private static EntitlementParameter MapParameter(GoogleCloudChannelV1Parameter parameter) => new()
    {
        Name = parameter.Name ?? string.Empty,
        Value = ValueToString(parameter.Value),
        Editable = parameter.Editable ?? false
    };

    private static string? ValueToString(GoogleCloudChannelV1Value? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.StringValue is not null)
        {
            return value.StringValue;
        }

        if (value.Int64Value.HasValue)
        {
            return value.Int64Value.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (value.DoubleValue.HasValue)
        {
            return value.DoubleValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        return value.BoolValue.HasValue ? (value.BoolValue.Value ? "true" : "false") : null;
    }

    /// <summary>Translates UI parameter inputs to Google typed parameters (numeric -> int64, else string).</summary>
    private static IList<GoogleCloudChannelV1Parameter>? ToGoogleParameters(IReadOnlyList<EntitlementParameterInput> inputs) =>
        inputs is { Count: > 0 }
            ? inputs.Select(p => new GoogleCloudChannelV1Parameter
            {
                Name = p.Name,
                Value = new GoogleCloudChannelV1Value
                {
                    Int64Value = p.IntValue,
                    StringValue = p.IntValue.HasValue ? null : p.StringValue
                }
            }).ToList()
            : null;

    /// <summary>Wraps a long-running operation into the UI-facing <see cref="EntitlementOperation"/>.</summary>
    private static EntitlementOperation MapOperation(GoogleLongrunningOperation operation) => new()
    {
        OperationName = operation.Name,
        Done = operation.Done ?? false,
        Error = operation.Error?.Message
    };

    private static void EnsureEntitlementArgs(string customerId, string entitlementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
    }

    /// <summary>Builds the full entitlement resource name for a customer + entitlement id.</summary>
    private string EntitlementName(string customerId, string entitlementId) =>
        $"{_options.AccountName}/customers/{customerId}/entitlements/{entitlementId}";

    /// <summary>Resolves an offer id or full resource name to a full offer resource name.</summary>
    private string OfferName(string offerIdOrName) =>
        offerIdOrName.Contains('/', StringComparison.Ordinal)
            ? offerIdOrName
            : $"{_options.AccountName}/offers/{offerIdOrName}";

    /// <summary>Maps a Google customer resource to the UI-facing <see cref="Customer"/> contract.</summary>
    private Customer MapCustomer(GoogleCloudChannelV1Customer customer) => new()
    {
        Name = customer.Name ?? string.Empty,
        Id = LastSegment(customer.Name),
        OrgDisplayName = customer.OrgDisplayName,
        Domain = customer.Domain,
        CloudIdentityId = customer.CloudIdentityId,
        LanguageCode = customer.LanguageCode,
        ChannelPartnerId = customer.ChannelPartnerId,
        CreateTime = customer.CreateTimeDateTimeOffset,
        PrimaryContact = customer.PrimaryContactInfo is { } contact
            ? new CustomerContact
            {
                FirstName = contact.FirstName,
                LastName = contact.LastName,
                Email = contact.Email,
                Title = contact.Title,
                Phone = contact.Phone
            }
            : null,
        Address = customer.OrgPostalAddress is { } address
            ? new CustomerAddress
            {
                RegionCode = address.RegionCode,
                PostalCode = address.PostalCode,
                AdministrativeArea = address.AdministrativeArea,
                Locality = address.Locality,
                AddressLines = address.AddressLines is { } lines ? [.. lines] : []
            }
            : null,
        CloudIdentity = customer.CloudIdentityInfo is { } info
            ? new CustomerCloudIdentity
            {
                CustomerType = info.CustomerType,
                PrimaryDomain = info.PrimaryDomain,
                IsDomainVerified = info.IsDomainVerified ?? false,
                AlternateEmail = info.AlternateEmail,
                AdminConsoleUri = info.AdminConsoleUri
            }
            : null
    };

    /// <summary>Builds a Google customer body from a save request (shared by create and update).</summary>
    private static GoogleCloudChannelV1Customer ToGoogleCustomer(SaveCustomerRequest request) => new()
    {
        OrgDisplayName = request.OrgDisplayName,
        LanguageCode = string.IsNullOrWhiteSpace(request.LanguageCode) ? null : request.LanguageCode,
        OrgPostalAddress = new GoogleTypePostalAddress
        {
            RegionCode = request.Address.RegionCode,
            PostalCode = request.Address.PostalCode,
            AdministrativeArea = request.Address.AdministrativeArea,
            Locality = request.Address.Locality,
            AddressLines = request.Address.AddressLines is { Count: > 0 } lines ? [.. lines] : null
        },
        PrimaryContactInfo = request.PrimaryContact is { } contact
            ? new GoogleCloudChannelV1ContactInfo
            {
                FirstName = contact.FirstName,
                LastName = contact.LastName,
                Email = contact.Email,
                Title = contact.Title,
                Phone = contact.Phone
            }
            : null
    };

    /// <summary>Builds the full customer resource name for a short customer id.</summary>
    private string CustomerName(string customerId) => $"{_options.AccountName}/customers/{customerId}";

    /// <summary>Builds the full channel partner link resource name for a short link id.</summary>
    private string ChannelPartnerLinkName(string linkId) => $"{_options.AccountName}/channelPartnerLinks/{linkId}";

    /// <summary>Builds the full customer repricing config resource name for a short config id.</summary>
    private string CustomerRepricingConfigName(string customerId, string configId) =>
        $"{CustomerName(customerId)}/customerRepricingConfigs/{configId}";

    /// <summary>Builds the full channel partner repricing config resource name for a short config id.</summary>
    private string ChannelPartnerRepricingConfigName(string linkId, string configId) =>
        $"{ChannelPartnerLinkName(linkId)}/channelPartnerRepricingConfigs/{configId}";

    /// <summary>
    /// Resolves the entitlement target for a channel partner config. The UI drives whole-partner
    /// (channel-partner-granularity) configs, so a blank id means "no entitlement target"; a caller
    /// may still pass a full entitlement resource name to scope the config to a single entitlement.
    /// </summary>
    private static string? ChannelPartnerEntitlementName(string? entitlementId) =>
        string.IsNullOrWhiteSpace(entitlementId) ? null : entitlementId;

    /// <summary>
    /// Builds a Google <c>RepricingConfig</c> from the UI request. A non-empty
    /// <paramref name="entitlementName"/> selects entitlement granularity (the recommended level);
    /// a blank one falls back to whole-partner (channel-partner) granularity.
    /// </summary>
    private static GoogleCloudChannelV1RepricingConfig ToGoogleRepricingConfig(SaveRepricingConfigRequest request, string? entitlementName)
    {
        var config = new GoogleCloudChannelV1RepricingConfig
        {
            EffectiveInvoiceMonth = new GoogleTypeDate
            {
                Year = request.EffectiveInvoiceYear,
                Month = request.EffectiveInvoiceMonth
            },
            RebillingBasis = request.RebillingBasis,
            Adjustment = new GoogleCloudChannelV1RepricingAdjustment
            {
                PercentageAdjustment = new GoogleCloudChannelV1PercentageAdjustment
                {
                    Percentage = new GoogleTypeDecimal
                    {
                        Value = request.PercentageAdjustment.ToString(CultureInfo.InvariantCulture)
                    }
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(entitlementName))
        {
            config.EntitlementGranularity = new GoogleCloudChannelV1RepricingConfigEntitlementGranularity
            {
                Entitlement = entitlementName
            };
        }
        else
        {
            config.ChannelPartnerGranularity = new GoogleCloudChannelV1RepricingConfigChannelPartnerGranularity();
        }

        return config;
    }

    /// <summary>Maps a Google repricing config (customer or channel partner) to the UI-facing contract.</summary>
    private static RepricingConfig MapRepricingConfig(string? name, GoogleCloudChannelV1RepricingConfig? config, DateTimeOffset? updateTime)
    {
        var entitlementName = config?.EntitlementGranularity?.Entitlement;

        decimal percentage = 0;
        var rawPercentage = config?.Adjustment?.PercentageAdjustment?.Percentage?.Value;
        if (!string.IsNullOrWhiteSpace(rawPercentage))
        {
            decimal.TryParse(rawPercentage, NumberStyles.Number, CultureInfo.InvariantCulture, out percentage);
        }

        return new RepricingConfig
        {
            Name = name ?? string.Empty,
            Id = LastSegment(name),
            EffectiveInvoiceYear = config?.EffectiveInvoiceMonth?.Year ?? 0,
            EffectiveInvoiceMonth = config?.EffectiveInvoiceMonth?.Month ?? 0,
            PercentageAdjustment = percentage,
            RebillingBasis = config?.RebillingBasis,
            Granularity = string.IsNullOrEmpty(entitlementName)
                ? RepricingGranularities.ChannelPartner
                : RepricingGranularities.Entitlement,
            EntitlementName = entitlementName,
            EntitlementId = string.IsNullOrEmpty(entitlementName) ? null : LastSegment(entitlementName),
            ConditionalOverrideCount = config?.ConditionalOverrides?.Count ?? 0,
            UpdateTime = updateTime
        };
    }

    /// <summary>Maps a Google channel partner link resource to the UI-facing <see cref="ChannelPartnerLink"/> contract.</summary>
    private static ChannelPartnerLink MapChannelPartnerLink(GoogleCloudChannelV1ChannelPartnerLink link) => new()
    {
        Name = link.Name ?? string.Empty,
        Id = LastSegment(link.Name),
        ResellerCloudIdentityId = link.ResellerCloudIdentityId,
        LinkState = link.LinkState,
        InviteLinkUri = link.InviteLinkUri,
        PublicId = link.PublicId,
        CreateTime = link.CreateTimeDateTimeOffset,
        UpdateTime = link.UpdateTimeDateTimeOffset,
        ChannelPartner = link.ChannelPartnerCloudIdentityInfo is { } info
            ? new ChannelPartnerCloudIdentity
            {
                CustomerType = info.CustomerType,
                PrimaryDomain = info.PrimaryDomain,
                IsDomainVerified = info.IsDomainVerified ?? false,
                AlternateEmail = info.AlternateEmail
            }
            : null
    };

    /// <summary>Returns the last "/"-separated segment of a resource name (its short id).</summary>
    private static string LastSegment(string? resourceName) =>
        string.IsNullOrEmpty(resourceName)
            ? string.Empty
            : resourceName[(resourceName.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Extracts the product id from a SKU resource name of the form
    /// <c>products/{product}/skus/{sku}</c>. Returns an empty string when not present.
    /// </summary>
    private static string ProductIdFromResourceName(string? resourceName)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            return string.Empty;
        }

        var segments = resourceName.Split('/');
        var index = Array.IndexOf(segments, "products");
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : string.Empty;
    }

    /// <summary>Maps an offer's wholesale list pricing (price-by-resource + tiers) into contract DTOs.</summary>
    private static IReadOnlyList<OfferPrice> MapOfferPricing(GoogleCloudChannelV1Offer? offer)
    {
        if (offer?.PriceByResources is not { Count: > 0 } prices)
        {
            return [];
        }

        var result = new List<OfferPrice>(prices.Count);
        foreach (var p in prices)
        {
            var price = p.Price;
            // Tiers live on the price phases (seat-count banding), not on the flat price.
            var tiers = (p.PricePhases ?? [])
                .SelectMany(ph => ph.PriceTiers ?? [])
                .Select(t => new OfferPriceTier
                {
                    FirstResource = t.FirstResource ?? 0,
                    LastResource = t.LastResource ?? 0,
                    EffectivePrice = MapMoney(t.Price?.EffectivePrice)
                })
                .ToList();

            result.Add(new OfferPrice
            {
                ResourceType = p.ResourceType,
                BasePrice = MapMoney(price?.BasePrice),
                EffectivePrice = MapMoney(price?.EffectivePrice),
                DiscountPercent = (decimal)(price?.Discount ?? 0),
                Tiers = tiers
            });
        }

        return result;
    }

    /// <summary>Maps a <c>google.type.Money</c> into a <see cref="MoneyAmount"/>; null when no currency.</summary>
    private static MoneyAmount? MapMoney(GoogleTypeMoney? money) =>
        money is null || string.IsNullOrEmpty(money.CurrencyCode)
            ? null
            : new MoneyAmount
            {
                CurrencyCode = money.CurrencyCode,
                Units = money.Units ?? 0,
                Nanos = money.Nanos ?? 0
            };

    /// <summary>Friendly payment cycle (e.g. "Monthly", "Annual") from an offer plan; null when absent.</summary>
    private static string? PaymentCycleLabel(GoogleCloudChannelV1Plan? plan)
    {
        var period = plan?.PaymentCycle;
        if (period?.PeriodType is not { Length: > 0 } type)
        {
            return null;
        }

        var count = period.Duration ?? 1;
        var unit = type.ToUpperInvariant() switch
        {
            "MONTH" => count == 12 ? "Annual" : count == 1 ? "Monthly" : $"{count}-monthly",
            "YEAR" => count == 1 ? "Annual" : $"{count}-yearly",
            "DAY" => count == 1 ? "Daily" : $"{count}-daily",
            _ => $"{count} {type.ToLowerInvariant()}"
        };
        return unit;
    }
}
