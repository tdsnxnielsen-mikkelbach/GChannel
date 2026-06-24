# API surface

Based on the stable **`v1`** of the [Cloud Channel API](https://docs.cloud.google.com/channel/docs/reference/rest).
All paths are relative to `https://cloudchannel.googleapis.com`.

## Implemented

| UI action | Resource.method | Channel API |
| --- | --- | --- |
| **Accounts → Cloud Identity check** | `accounts.checkCloudIdentityAccountsExist` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/checkCloudIdentityAccountsExist) |
| **Catalog → Products** | `products.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/products/list) |
| **Catalog → Products (SKUs)** | `products.skus.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/products.skus/list) |
| **Catalog → Offers** | `accounts.offers.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.offers/list) |
| **Catalog → SKU groups** | `accounts.skuGroups.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.skuGroups/list) |
| **Catalog → SKU groups (billable SKUs)** | `accounts.skuGroups.billableSkus.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.skuGroups.billableSkus/list) |
| **Customers → list / detail** | `accounts.customers.list` / `accounts.customers.get` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/list) |
| **Customers → create / edit** | `accounts.customers.create` / `accounts.customers.patch` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/create) |
| **Customers → delete** | `accounts.customers.delete` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/delete) |
| **Customers → purchasable SKUs** | `accounts.customers.listPurchasableSkus` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/listPurchasableSkus) |
| **Customers → purchasable offers** | `accounts.customers.listPurchasableOffers` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/listPurchasableOffers) |
| **Entitlements → list / detail** | `accounts.customers.entitlements.list` / `.get` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/list) |
| **Entitlements → change history** | `accounts.customers.entitlements.listEntitlementChanges` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/listEntitlementChanges) |
| **Entitlements → offer lookup** | `accounts.customers.entitlements.lookupOffer` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/lookupOffer) |
| **Entitlements → purchase** | `accounts.customers.entitlements.create` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/create) |
| **Entitlements → change offer / parameters / renewal** | `.changeOffer` / `.changeParameters` / `.changeRenewalSettings` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/changeOffer) |
| **Entitlements → activate / suspend / cancel / start paid** | `.activate` / `.suspend` / `.cancel` / `.startPaidService` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/activate) |
| **Home → dashboard summary** | *derived* (`accounts.customers.list` + `accounts.customers.entitlements.list` + `accounts.offers.list`) | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/list) |

> **Cross-navigation.** Catalog resources are correlated by id (product ↔ SKU ↔ offer ↔ billable
> SKU) so the UI can deep-link between products, offers, and SKU groups. Customers extend this: the
> detail page's purchasable SKUs deep-link into the catalog, and each customer row links to the
> Cloud Identity check for its domain (`/accounts/cloud-identity?domain=`). Entitlements complete the
> chain: they hang off a customer (`/customers/{id}/entitlements`) and link back to the catalog by id
> (offer/product/SKU), while the purchase flow reuses the customer's purchasable SKUs/offers. See
> [architecture.md](architecture.md#catalog-correlation--navigation).

> **Long-running operations.** Mutating entitlement calls (create/change/state-change) return LROs.
> Operation polling is deferred to the roadmap's §7; the UI currently reflects the operation as
> *completed* (when Google finishes inline) or *submitted — processing* and reloads the list.

> **Friendly names.** Entitlements and their change history carry only opaque ids; the API resolves
> human-readable **offer / SKU / product** names from the offer catalog (`accounts.offers.list`,
> reusing `MarketingInfo.DisplayName`) and the UI shows the name with the id as a tooltip/caption,
> falling back to the id if a name can't be resolved. The same friendly-name-with-id-tooltip pattern
> is applied across the catalog pages (e.g. the Offers page SKU column).

> **Resilience.** All Channel API calls retry `429`/`503` with exponential back-off and surface a
> clean `ProblemDetails` (with `Retry-After` on 429) when throttled. The shared resilience handler
> uses raised attempt/total timeouts (60s/120s) so cold-start calls aren't cut at the framework's
> default 30s, and benign client-aborted requests are classified as `499` rather than `500`.
> Idempotent reads are cached in Redis (customer list/get are cached with invalidation on writes).
> Cloud Identity checks are also persisted to SQL with a history/recheck (cache-bypass) path —
> internal endpoint `GET /api/accounts/check-cloud-identity/history`.

> **Derived dashboard.** The home page is backed by two internal endpoints:
> `GET /api/dashboard/overview` (cheap phase 1 — customer count + onboarded-by-month buckets from
> `accounts.customers.list` create times only) and `GET /api/dashboard/summary` (full aggregation).
> There is no Channel API reporting endpoint — `accounts.reports.*` is deprecated in `v1`.
> The summary adds active/trial/suspended entitlement counts, active seats, and a product-mix breakdown
> (from `accounts.customers.entitlements.list`, with `accounts.offers.list`/`accounts.products.list`
> resolving friendly product labels). Those per-customer entitlement calls are paced under the
> per-minute quota (`DashboardRequestsPerMinute`) and 429s are retried honouring `Retry-After`. Both
> results are cached in Redis for `CacheSeconds`. A third cheap endpoint `GET /api/dashboard/status`
> returns the background refresher's `DashboardRefreshStatus` (enabled / in-progress / last-completed /
> duration / skipped), which the home page polls every 30 s to show an "Updated X ago" / "Refreshing…"
> indicator and to redraw the figures live while a background run publishes partial snapshots.

## Available possibilities

The following are the full set of `v1` resources/methods the dashboard could grow into,
grouped by feature area. See [todo.md](todo.md) for sequencing/priority.

### Customers

Most customer methods are now **implemented** (see the table above). The following remain available:

| Resource.method | Purpose |
| --- | --- |
| [`accounts.customers.import`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/import) | Import a customer from Cloud Identity before transfer. *(LRO — deferred to §7.)* |
| [`accounts.customers.provisionCloudIdentity`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/provisionCloudIdentity) | Provision a Cloud Identity for a customer. *(LRO — deferred to §7.)* |
| [`accounts.customers.queryEligibleBillingAccounts`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/queryEligibleBillingAccounts) | Billing accounts eligible for given SKUs. *(N-tier distributor billing — deferred.)* |

### Entitlements (subscriptions / lifecycle)

All entitlement methods below are now **implemented** (see the *Implemented* table above), except
`listEntitlementChanges`/`lookupOffer` which back the detail page's history and offer cards. The
mutating calls return long-running operations (see §7 for the deferred polling work).

| Resource.method | Purpose |
| --- | --- |
| [`accounts.customers.entitlements.list`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/list) | List a customer's entitlements. |
| [`accounts.customers.entitlements.get`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/get) | Get an entitlement. |
| [`accounts.customers.entitlements.create`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/create) | Create (purchase) an entitlement. |
| [`accounts.customers.entitlements.changeOffer`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/changeOffer) | Change the Offer of an entitlement. |
| [`accounts.customers.entitlements.changeParameters`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/changeParameters) | Change parameters (e.g. seats). |
| [`accounts.customers.entitlements.changeRenewalSettings`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/changeRenewalSettings) | Update renewal settings. |
| [`accounts.customers.entitlements.activate`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/activate) | Activate a suspended entitlement. |
| [`accounts.customers.entitlements.suspend`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/suspend) | Suspend an entitlement. |
| [`accounts.customers.entitlements.cancel`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/cancel) | Cancel an entitlement. |
| [`accounts.customers.entitlements.startPaidService`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/startPaidService) | Start paid service for a trial. |
| [`accounts.customers.entitlements.lookupOffer`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/lookupOffer) | Look up the Offer of an entitlement. |
| [`accounts.customers.entitlements.listEntitlementChanges`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.entitlements/listEntitlementChanges) | Entitlement change history. |

### Transfers

| Resource.method | Purpose |
| --- | --- |
| [`accounts.listTransferableSkus`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listTransferableSkus) | Transferable SKUs for a customer. |
| [`accounts.listTransferableOffers`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listTransferableOffers) | Transferable Offers for a customer. |
| [`accounts.customers.transferEntitlements`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/transferEntitlements) | Transfer entitlements to this reseller. |
| [`accounts.customers.transferEntitlementsToGoogle`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/transferEntitlementsToGoogle) | Transfer entitlements to Google. |

### Channel partner links (n-tier / distributor)

| Resource.method | Purpose |
| --- | --- |
| [`accounts.channelPartnerLinks.list`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/list) | List channel partner links. |
| [`accounts.channelPartnerLinks.get`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/get) | Get a channel partner link. |
| [`accounts.channelPartnerLinks.create`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/create) | Initiate a distributor↔reseller link. |
| [`accounts.channelPartnerLinks.patch`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/patch) | Update a channel partner link. |
| `accounts.channelPartnerLinks.customers.*` | Manage customers under a channel partner ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks.customers)). |

### Repricing (rebilling margin)

| Resource.method | Purpose |
| --- | --- |
| `accounts.customers.customerRepricingConfigs.*` | How a reseller modifies a customer's bill ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.customerRepricingConfigs)). |
| `accounts.channelPartnerLinks.channelPartnerRepricingConfigs.*` | How a distributor modifies a channel partner's bill ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks.channelPartnerRepricingConfigs)). |

### Pub/Sub subscribers & operations

| Resource.method | Purpose |
| --- | --- |
| `accounts.register` / `accounts.unregister` / `accounts.listSubscribers` | Manage Pub/Sub subscriber service accounts ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts)). |
| `operations.{get,list,cancel,delete}` | Track long-running operations ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/operations)). |

### Reporting (deprecated in v1)

`accounts.reports.*` and `accounts.reportJobs.fetchReportResults` are **deprecated**; avoid
building new UI on them. Drive the home-page figures from entitlement data instead.

## `v1alpha1` preview (use with caution)

The API also exposes a [`v1alpha1`](https://docs.cloud.google.com/channel/docs/reference/rest)
version. It is **alpha**: subject to breaking changes, not recommended for production. It mostly
mirrors `v1`, but adds a few capabilities that have no stable equivalent yet:

| Resource.method | Purpose |
| --- | --- |
| [`accounts.listProvisionableCloudIdentityTypes`](https://docs.cloud.google.com/channel/docs/reference/rest/v1alpha1/accounts/listProvisionableCloudIdentityTypes) | Workspace customer types creatable for a domain, and whether a transfer is required. |
| [`accounts.customers.entitlements.assignChannelPartner`](https://docs.cloud.google.com/channel/docs/reference/rest/v1alpha1/accounts.customers.entitlements/assignChannelPartner) | Assign a channel partner to an entitlement (n-tier). |
| [`opportunities`](https://docs.cloud.google.com/channel/docs/reference/rest/v1alpha1/opportunities) `create` / `get` / `patch` / `query` | Deal-registration / opportunity submission flow. |

It also still exposes the **deprecated** entitlement variants `changePlan`, `changeQuantity`,
and `changeSku` — prefer the `v1` `changeOffer` / `changeParameters` methods instead.

