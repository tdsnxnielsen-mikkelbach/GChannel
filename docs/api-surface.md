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
| **Customers → transferable SKUs / offers** | `accounts.listTransferableSkus` / `accounts.listTransferableOffers` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listTransferableSkus) |
| **Customers → transfer in / to Google** | `accounts.customers.transferEntitlements` / `.transferEntitlementsToGoogle` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/transferEntitlements) |
| **Channel partners → links list / detail** | `accounts.channelPartnerLinks.list` / `.get` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/list) |
| **Channel partners → invite / change state** | `accounts.channelPartnerLinks.create` / `.patch` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/create) |
| **Channel partners → customers under a partner** | `accounts.channelPartnerLinks.customers.list` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks.customers/list) |
| **Eventing → Operations (track / cancel)** | `operations.get` / `operations.cancel` (`operations.list` returns 501) | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/operations/get) |
| **Eventing → Notifications (subscriber admin)** | `accounts.register` / `accounts.unregister` / `accounts.listSubscribers` | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listSubscribers) |
| **Home → dashboard summary** | *derived* (`accounts.customers.list` + `accounts.customers.entitlements.list` + `accounts.offers.list` + `accounts.channelPartnerLinks.list`) | [docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/list) |

> **Cross-navigation.** Catalog resources are correlated by id (product ↔ SKU ↔ offer ↔ billable
> SKU) so the UI can deep-link between products, offers, and SKU groups. Customers extend this: the
> detail page's purchasable SKUs deep-link into the catalog, and each customer row links to the
> Cloud Identity check for its domain (`/accounts/cloud-identity?domain=`). Entitlements complete the
> chain: they hang off a customer (`/customers/{id}/entitlements`) and link back to the catalog by id
> (offer/product/SKU), while the purchase flow reuses the customer's purchasable SKUs/offers.
> **Transfers** reuse the same model — the transfer page (`/customers/{id}/transfer`) hangs off a
> customer, and its transferable SKUs/offers resolve to the same catalog ids/friendly names.
> **Channel partner links** correlate the other way: a link's short id is a customer's
> `ChannelPartnerId`, so the customer-detail page links to its owning partner and the partner-detail
> page lists (and links back to) the customers it owns. See
> [architecture.md](architecture.md#catalog-correlation--navigation).

> **Long-running operations.** Mutating entitlement **and transfer** calls
> (create/change/state-change, `transferEntitlements`/`transferEntitlementsToGoogle`) return LROs.
> Operation polling/cancellation is now **implemented** (§7): `GET /api/operations`,
> `GET /api/operations/{id}` and `POST /api/operations/{id}/cancel` wrap
> `operations.get`/`cancel`, and the **Operations** page tracks any returned operation name to
> `done` and deep-links to the affected customer/entitlement. Note the Cloud Channel API does **not**
> implement `operations.list` (it returns HTTP 501 `notImplemented`), so `GET /api/operations` returns
> an empty list by design — operations are tracked individually by the name a mutation returns.
> Mutating pages still also reflect the
> operation inline as *completed* (when Google finishes synchronously) or *submitted — processing*.

> **Eventing & operations (§7).** Channel change events flow through **Google Cloud Pub/Sub** — Google
> publishes entitlement/customer events to a Google-owned topic, and `accounts.register` grants a
> service account subscriber access to it (`listSubscribers`/`unregister` manage the set). There is no
> Azure messaging in the path: on Azure the app's **managed identity** only reads the Google
> service-account key from **Key Vault**, and that key authenticates to Pub/Sub. The subscriber runs
> as a `BackgroundService` (`ChannelNotificationsService`) **inside the existing API container app** —
> **no extra container** — and writes events to a capped Redis list (`channel:notifications`) that
> `GET /api/notifications` serves; SQL isn't used because the app runs `EnsureCreated` (no migrations).
> Pub/Sub load-balances across subscribers, so multiple API replicas share the subscription with **no
> distributed lock** (only `min-replicas ≥ 1` is required). Local F5 behaves identically using the
> key from user-secrets. The **Notifications** page shows the live feed (each row deep-linking to its
> customer/entitlement) plus subscriber registration; the subscriber is a no-op unless
> `GoogleChannel:PubSubProjectId` + `PubSubSubscriptionId` + a service-account key are set. See
> [configuration.md](configuration.md#pubsub-notifications-7) and
> [architecture.md](architecture.md#eventing--operations).

> **Transfers.** Moving an existing subscription into the reseller is exposed under
> `/api/customers/{id}`: `GET /transferable-skus` and `GET /transferable-offers?productId=&skuId=`
> (cached in Redis for `CacheSeconds`) list what can be transferred, and `POST /transfer-entitlements`
> / `POST /transfer-entitlements-to-google` execute the transfer — both return the LRO and invalidate
> the customer's transferable-SKU and entitlement-list caches. The Blazor **Transfer** page
> (`/customers/{id}/transfer`, reachable from the customer-detail and entitlements page headers) lists
> the eligible SKUs (ineligible ones are disabled with the reason), lazy-loads each SKU's transferable
> offers on expand, and builds a basket of offers to transfer (per-line seats + purchase order, plus
> an optional transfer auth token). Transferable SKUs/offers resolve to the same Catalog ids and
> friendly names as a purchase, so a transfer hangs off a customer and cross-links to the catalog
> exactly like an entitlement. `transferEntitlementsToGoogle` (handing a subscription back to direct
> Google billing) is wired through the API for completeness; the basket drives the standard
> `transferEntitlements` reseller flow.

> **Channel partner links.** Linking downstream resellers (n-tier / distributor) is exposed under
> `/api/channel-partner-links`: `GET /` (list) and `GET /{id}` (get, FULL view so the partner's Cloud
> Identity comes back) read the links, `POST /` invites a reseller (the link starts in the `INVITED`
> state), `PUT /{id}/state` changes the link state (`patch` with
> `update_mask = channel_partner_link.link_state`), and `GET /{id}/customers` lists the customers the
> partner owns (`channelPartnerLinks.customers.list`). All reads are cached in Redis for `CacheSeconds`
> with the list + per-link caches invalidated on create/patch. The Blazor **Partner links** list
> (`/channel-partner-links`), **Invite partner** form (`/channel-partner-links/new`) and **link
> detail** page (`/channel-partner-links/{id}`) live under a new **Channel partners** nav group; the
> detail page surfaces the invitation URI, partner Cloud Identity, an Activate/Suspend control, and
> the partner's customers. **Correlation:** a link's short id is exactly a customer's
> `ChannelPartnerId`, so the customer-detail page links to its owning partner and the link-detail page
> lists (and links back to) the partner's customers. Unlike entitlements/transfers, `create`/`patch`
> return the link resource directly (not LROs), so the UI updates immediately.

> **Repricing (rebilling margin).** Customer and channel-partner repricing configs are exposed under
> `/api/customers/{customerId}/repricing-configs` and
> `/api/channel-partner-links/{linkId}/repricing-configs`: `GET /` lists, `POST /` creates,
> `PUT /{configId}` updates and `DELETE /{configId}` removes a config. A config carries the effective
> invoice month, a percentage adjustment (positive marks up, negative discounts) and a rebilling basis
> (`COST_AT_LIST` or `DIRECT_CUSTOMER_COST`). Reads are cached in Redis for `CacheSeconds` with the
> list cache invalidated on create/update/delete. **Granularity:** customer configs use *entitlement
> granularity* (each targets one of the customer's entitlements — required), so the create form
> populates its entitlement picker from `entitlements.list`; channel-partner configs use *channel-
> partner granularity* and reprice the whole downstream reseller. The Blazor **Customer repricing**
> (`/customers/{id}/repricing`) and **Channel partner repricing**
> (`/channel-partner-links/{id}/repricing`) pages each offer an inline create/edit form and a delete
> action, reachable via a **Repricing** action on the customer-detail and link-detail pages.
> **Correlation:** each customer config row links its targeted entitlement back to the entitlement
> detail page. Like channel partner links, `create`/`patch` return the config resource directly (not
> LROs).

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
> `accounts.customers.list` create times, plus the **Channel links** count from a BASIC-view
> `accounts.channelPartnerLinks.list`) and `GET /api/dashboard/summary` (full aggregation).
> There is no Channel API reporting endpoint — `accounts.reports.*` is deprecated in `v1`.
> The summary adds active/trial/suspended entitlement counts, active seats, and a product-mix breakdown
> (from `accounts.customers.entitlements.list`, with `accounts.offers.list`/`accounts.products.list`
> resolving friendly product labels). Those per-customer entitlement calls are paced under the
> per-minute quota (`DashboardRequestsPerMinute`) and 429s are retried honouring `Retry-After`. Both
> results are cached in Redis for `CacheSeconds`. A third cheap endpoint `GET /api/dashboard/status`
> returns the background refresher's `DashboardRefreshStatus` (enabled / in-progress / last-completed /
> duration / skipped / next-refresh estimate), which the home page polls every 30 s to show an
> "Updated X ago · next refresh in X" / "Refreshing…" indicator and to redraw the figures live while a
> background run publishes partial snapshots. When the §10 read-model is enabled the summary also carries
> `DashboardEstateValue` — an estimated monthly **wholesale cost / repriced revenue / margin** rollup
> derived from offer **list** pricing (`accounts.offers.list`) × seats and §6 repricing mark-ups
> (`customerRepricingConfigs` / `channelPartnerRepricingConfigs`), denormalised onto the read-model by the
> worker and surfaced as a *not-invoiced estimate*.

> **Read-model-backed list endpoints (§10).** When `GoogleChannel:UseReadModel` is on, two interactive
> reads whose live calls sit on the **contended** per-minute quotas are served from SQL instead, so they
> stop competing with the sync worker: `GET /api/customers/{id}/entitlements` reads `EntitlementRecords`
> (rather than `entitlements.list`) once the customer has been synced, and
> `GET /api/channel-partner-links/{id}/customers` reads `CustomerRecords` for the owning link (rather than
> `channelPartnerLinks.customers.list`). Both fall back to the original live, cached call when the
> read-model has no rows yet (cold start / freshly rostered link), so behaviour is unchanged before the
> first sync. The stored entitlement rows carry friendly offer/SKU/product names + create time (denormalised
> for free from the pricing pass's `offers.list`), so the list looks identical to the live path. Entitlement
> **detail** (`.get`), customer **detail**, the **catalog**, **repricing** and **transfers** stay live —
> those are on lighter/uncontended quotas or, for transfers, must be computed in real time.

> **Read-model pricing fields.** The same denormalised pricing the dashboard rollup uses is also exposed
> per row for UI estimates (no extra Channel API calls): read-model `Entitlement` results carry
> `UnitPrice` / `PriceCurrency` / `RepricingPercent`, and `GET /api/estate/customers` rows carry an
> `EstimatedMonthlyTotal` + `Currency` (the customer's active priced entitlements summed as
> `Σ price × seats × (1 + percent/100)` in their dominant currency). All are *estimated list pricing,
> not invoiced amounts*. The estate-customers *as-of* timestamp ignores never-synced rows
> (`LastSyncedUtc == MinValue`).

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
mutating calls return long-running operations; polling/cancellation is implemented in §7 (see the
**Operations** page).

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

All transfer methods below are now **implemented** (see the *Implemented* table above). The two
mutating calls return long-running operations; polling/cancellation is implemented in §7 (see the
**Operations** page).

| Resource.method | Purpose |
| --- | --- |
| [`accounts.listTransferableSkus`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listTransferableSkus) | Transferable SKUs for a customer. |
| [`accounts.listTransferableOffers`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts/listTransferableOffers) | Transferable Offers for a customer. |
| [`accounts.customers.transferEntitlements`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/transferEntitlements) | Transfer entitlements to this reseller. |
| [`accounts.customers.transferEntitlementsToGoogle`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers/transferEntitlementsToGoogle) | Transfer entitlements to Google. |

### Channel partner links (n-tier / distributor)

The link-management and customers-under-a-partner methods below are now **implemented** (see the
*Implemented* table above); only the repricing configs remain (§6). `create`/`patch` return the link
resource directly (not long-running operations).

| Resource.method | Purpose |
| --- | --- |
| [`accounts.channelPartnerLinks.list`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/list) | List channel partner links. **(implemented)** |
| [`accounts.channelPartnerLinks.get`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/get) | Get a channel partner link. **(implemented)** |
| [`accounts.channelPartnerLinks.create`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/create) | Initiate a distributor↔reseller link. **(implemented)** |
| [`accounts.channelPartnerLinks.patch`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks/patch) | Update a channel partner link. **(implemented)** |
| `accounts.channelPartnerLinks.customers.*` | Manage customers under a channel partner ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks.customers)). `list` is **(implemented)**; create/import/get/patch/delete remain. |

### Repricing (rebilling margin)

These configs are now **implemented** (see the *Implemented* table above). `list`/`create`/`patch`
return the config resource directly (not long-running operations). Customer configs use **entitlement
granularity** (each targets one of the customer's entitlements — §3); channel partner configs use
**channel-partner granularity** and reprice the whole downstream reseller.

| Resource.method | Purpose |
| --- | --- |
| `accounts.customers.customerRepricingConfigs.*` | How a reseller modifies a customer's bill ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.customers.customerRepricingConfigs)). `list`/`create`/`patch`/`delete` **(implemented)**. |
| `accounts.channelPartnerLinks.channelPartnerRepricingConfigs.*` | How a distributor modifies a channel partner's bill ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts.channelPartnerLinks.channelPartnerRepricingConfigs)). `list`/`create`/`patch`/`delete` **(implemented)**. |

### Pub/Sub subscribers & operations

These are now **implemented** (§7 — see the *Implemented* table above and the **Operations** /
**Notifications** pages). Subscriber management wraps the Google-owned Pub/Sub topic; a
`BackgroundService` inside the API streams the subscription into a Redis-backed feed.

| Resource.method | Purpose |
| --- | --- |
| `accounts.register` / `accounts.unregister` / `accounts.listSubscribers` | Manage Pub/Sub subscriber service accounts ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/accounts)). `register`/`unregister`/`listSubscribers` **(implemented)**. |
| `operations.{get,list,cancel,delete}` | Track long-running operations ([docs](https://docs.cloud.google.com/channel/docs/reference/rest/v1/operations)). `get`/`list`/`cancel` **(implemented)**; `delete` not surfaced. |

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

