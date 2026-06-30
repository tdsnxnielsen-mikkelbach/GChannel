# TODO / future developments

See [api-surface.md](api-surface.md) for the full catalog of `v1` Cloud Channel API
resources/methods these items map to.

## Hardening

- [x] **Silent token refresh.** Google access tokens expire after ~1 hour. The refresh token is
  captured (`AccessType=offline`) and the Web app now silently refreshes the access token via
  `GoogleTokenProvider` before forwarding it to the API service, caching refreshed tokens in
  memory per user. The refresh happens in the **Web app** (not the API service as originally
  suggested) so the long-lived refresh token never leaves the front end — only short-lived access
  tokens are forwarded to the API, which remains a stateless Bearer consumer.
- [x] **Throttling / 429 handling.** Every Channel API call retries `429` (and transient `503`)
  with exponential back-off (`GoogleChannel:MaxRetryAttempts`, default 3). If retries are
  exhausted, `GoogleApiExceptionHandler` returns a clean `ProblemDetails` mirroring the upstream
  status (`429` with `Retry-After`, `403`, `404`, …) instead of a `500`; a missing token becomes
  `401`. See [architecture.md](architecture.md#resilience--throttling-http-429).
- [x] **Cloud Identity caching &amp; recheck.** Check results are cached in Redis and persisted to
  SQL (`IdentityCheckLogs`). The UI shows a **recently checked** list and a **recheck** action that
  bypasses the cache (`?refresh=true`) to re-query Google and refresh the cache.
- [x] **Request timeouts &amp; cancellation.** The shared resilience handler uses raised attempt/total
  timeouts (60s/120s) so cold-start Channel API calls aren't cut at the framework's default 30s, and
  benign client-aborted requests are classified as `499` (not `500`) by `GoogleApiExceptionHandler`.
- [x] **Local dev persistence.** SQL and Redis run as persistent-lifetime containers with named data
  volumes (`gchannel-sql-data`, `gchannel-redis-data`), so data survives between debug sessions and
  cold-start latency is avoided. See [deployment.md](deployment.md#run-locally).

## Roadmap (Channel API capabilities to grow into)

Roughly in dependency order — read paths first, then customer/entitlement lifecycle, then the
advanced distributor/billing features.

### 1. Catalog browsing (read-only, low risk)

- [x] **Products** — `products.list` and `products.skus.list` to browse the sellable catalog.
- [x] **Offers** — `accounts.offers.list` to show the Offers the reseller can sell.
- [x] **SKU groups** — `accounts.skuGroups.list` + `accounts.skuGroups.billableSkus.list`.

> **Implemented.** Read-only catalog browsing is live end-to-end: shared contracts in
> `GChannel.Shared/Contracts/Catalog.cs`, `IGoogleChannelClient` catalog methods (with pagination)
> in the API, cached minimal-API endpoints under `/api/catalog/*` (Redis, `CacheSeconds` TTL), the
> `GChannelApiClient` typed-client methods, and three Blazor pages (`Products`, `Offers`,
> `SkuGroups`) reachable from the **Catalog** nav group. Products and SKU groups lazy-load their
> children (SKUs / billable SKUs) on panel expand.
>
> **Correlation/navigation.** Resources are cross-linked by id (product ↔ SKU ↔ offer ↔ billable
> SKU): SKU rows link to their offers, offers link back to their product/SKU (auto-expand +
> highlight), and billable SKUs link to both. This id model is the hook for correlating with
> **customer management** (§2) later — a customer's entitlements will resolve to these same
> products/SKUs/offers.


### 2. Customer management

- [x] **List / view customers** — `accounts.customers.list` + `accounts.customers.get`. Customers
  table (`/customers`) and detail page (`/customers/{id}`). Cached in Redis with cache invalidation
  on create/update/delete. The list's Cloud Identity column links to the per-customer Cloud Identity
  check (`/accounts/cloud-identity?domain=`).
- [x] **Create / update / delete customer** — `create`, `patch`, `delete`. Shared create/edit form
  (`/customers/new`, `/customers/edit/{id}`) with org/contact/address; delete confirms first. Update
  uses a field mask so the immutable domain is left untouched.
- [x] **Purchasable catalog per customer** — `listPurchasableSkus` + `listPurchasableOffers` on the
  customer detail page, with **catalog correlation**: each purchasable SKU links back to
  `/catalog/products?product=&sku=`. `queryEligibleBillingAccounts` deferred (n-tier distributor
  billing — low value for the standard reseller flow).
- [ ] **Cloud Identity** — `provisionCloudIdentity` and `import` for pre-transfer onboarding.
  Deferred: both return long-running `Operation`s and belong with the LRO infrastructure in §7.

### 3. Entitlement lifecycle (the core selling flow)

- [x] **List / view entitlements** — `entitlements.list` + `entitlements.get` +
  `listEntitlementChanges` (history) + `lookupOffer`.
- [x] **Purchase** — `entitlements.create`.
- [x] **Modify** — `changeOffer`, `changeParameters` (seats), `changeRenewalSettings`.
- [x] **State changes** — `activate`, `suspend`, `cancel`, `startPaidService` (trial → paid).

> **Implemented.** The full entitlement lifecycle is live end-to-end: shared contracts in
> `GChannel.Shared/Contracts/Entitlements.cs`, `IGoogleChannelClient` entitlement methods (list/get
> with pagination, change history, offer lookup, plus the mutating create/modify/state-change calls)
> in the API, cached entitlement endpoints under `/api/customers/{id}/entitlements/*` (Redis,
> `CacheSeconds` TTL, cache invalidated on every mutation), the `GChannelApiClient` typed-client
> methods, and three Blazor pages — `Entitlements` (list + state-change actions),
> `EntitlementDetail` (details, commitment/renewal, modify seats/offer, lifecycle actions and change
> history) and `PurchaseEntitlement` (product → SKU → offer purchase flow).
>
> **Correlation/navigation.** Entitlements hang off a **Customer** (§2): the customer list and
> detail pages link straight to `/customers/{id}/entitlements`. Each entitlement cross-links back to
> the **Catalog** (§1) by id — offer rows link to `/catalog/offers?sku=`, product/SKU links open
> `/catalog/products?product=&sku=`, and the purchase flow reuses the per-customer purchasable SKUs
> and offers (`listPurchasableSkus` / `listPurchasableOffers`) so the offer the customer is eligible
> to buy resolves to the same Catalog ids.
>
> **LROs.** The mutating calls (create/modify/state-change) return long-running operations. The UI
> surfaces the operation as accepted (showing *completed* when Google finishes inline, otherwise
> *submitted — processing*) and reloads the list, so a freshly purchased or changed entitlement
> appears once provisioning finishes. Full operation polling/cancellation is now available on the
> **Operations** page (§7), which tracks any returned operation name to `done`.
>
> **Friendly names.** Entitlements and their change history carry only opaque offer/SKU/product ids;
> the API resolves human-readable names from the offer catalog (`accounts.offers.list`, reusing
> `MarketingInfo.DisplayName`) and the UI shows the friendly name with the id as a tooltip/caption,
> falling back to the id when a name can't be resolved. The same friendly-name-with-id-tooltip
> pattern was applied across the catalog pages (e.g. the Offers page SKU column now shows the SKU
> display name).

### 4. Transfers

- [x] **Inspect transferability** — `accounts.listTransferableSkus`,
  `accounts.listTransferableOffers`.
- [x] **Execute transfer** — `customers.transferEntitlements` and
  `customers.transferEntitlementsToGoogle`.

> **Implemented.** Transferring an existing subscription into the reseller is live end-to-end:
> shared contracts in `GChannel.Shared/Contracts/Transfers.cs`, `IGoogleChannelClient` transfer
> methods (`ListTransferableSkusAsync` / `ListTransferableOffersAsync` with pagination, plus the
> mutating `TransferEntitlementsAsync` and `TransferEntitlementsToGoogleAsync`) in the API, cached
> minimal-API endpoints under `/api/customers/{id}/transferable-skus`,
> `/api/customers/{id}/transferable-offers`, `/api/customers/{id}/transfer-entitlements` and
> `/api/customers/{id}/transfer-entitlements-to-google` (Redis, `CacheSeconds` TTL, transferable-SKU
> and entitlement-list caches invalidated on every transfer), the `GChannelApiClient` typed-client
> methods, and a Blazor `Transfer` page (`/customers/{id}/transfer`) reachable from both the
> **Customer detail** and **Entitlements** pages. The page lists transferable SKUs (eligibility chip,
> ineligible SKUs disabled), lazy-loads transferable offers on panel expand, and builds a basket of
> offers to transfer (per-line seats + purchase order, optional transfer auth token).
>
> **Correlation/navigation.** Transfers hang off a **Customer** (§2) exactly like entitlements, and
> cross-link back to the **Catalog** (§1) by id: transferable SKUs/offers resolve their
> product/SKU/offer ids (and friendly `MarketingInfo.DisplayName` names) from the same offer catalog
> used by §1/§3. The `transferable-offers` lookup is scoped to a `productId`/`skuId`, so the offer a
> customer is eligible to transfer resolves to the same Catalog ids surfaced everywhere else.
>
> **LROs.** Both transfer calls return long-running operations. The UI surfaces the operation as
> accepted (showing *completed* when Google finishes inline, otherwise *submitted — processing*) and
> navigates to the customer's entitlements list, so the transferred subscriptions appear once
> provisioning finishes; the returned operation name can be tracked to completion on the
> **Operations** page (§7). `transferEntitlementsToGoogle`
> (handing a subscription back to direct Google billing) is wired through the API for completeness;
> the basket UI drives the standard `transferEntitlements` reseller flow.

### 5. Distributor / n-tier (channel partner links)

- [x] **Manage links** — `accounts.channelPartnerLinks` (list/get/create/patch).
- [x] **Customers under a partner** — `accounts.channelPartnerLinks.customers.*`.
- [x] **Revisit dashboard card** — restored the **Channel links** figure on the home dashboard
  (it replaces the temporary **Trials** card; **Suspended** stays).

> **Implemented.** Linking downstream resellers (channel partners) is live end-to-end: shared
> contracts in `GChannel.Shared/Contracts/ChannelPartnerLinks.cs` (`ChannelPartnerLink`,
> `ChannelPartnerLinksResult`, `CreateChannelPartnerLinkRequest`, `UpdateChannelPartnerLinkRequest`),
> `IGoogleChannelClient` methods (`ListChannelPartnerLinksAsync` / `GetChannelPartnerLinkAsync`
> (FULL view, so the partner's Cloud Identity comes back), `CreateChannelPartnerLinkAsync` (starts a
> link in the `INVITED` state), `UpdateChannelPartnerLinkStateAsync` (`patch` with
> `update_mask = channel_partner_link.link_state`), and `ListChannelPartnerCustomersAsync`) in the
> API, cached minimal-API endpoints under `/api/channel-partner-links` (list/create),
> `/api/channel-partner-links/{id}` (get), `/api/channel-partner-links/{id}/state` (patch) and
> `/api/channel-partner-links/{id}/customers` (Redis, `CacheSeconds` TTL, list + per-link caches
> invalidated on create/patch), the `GChannelApiClient` typed-client methods, and Blazor pages — a
> **Partner links** list (`/channel-partner-links`), an **Invite partner** form
> (`/channel-partner-links/new`), and a **link detail** page (`/channel-partner-links/{id}`) that
> shows the invitation URI, partner Cloud Identity, an Activate/Suspend state control, and the
> customers the partner owns — all reachable from a new **Channel partners** nav group.
>
> **Correlation/navigation.** A channel partner link's short id is exactly a customer's
> `ChannelPartnerId` (§2): the **Customer detail** page now shows a **Channel partner** row linking to
> the owning link (or "Direct (no partner)"), and the **link detail** page lists the partner's
> customers via `channelPartnerLinks.customers.list`, each row linking back to the customer. The home
> **Channel links** card counts links via a cheap account-level `channelPartnerLinks.list` (BASIC
> view) folded into the quota-light dashboard *overview* phase.
>
> **LROs.** `channelPartnerLinks.create` and `patch` return the link resource directly (not
> long-running operations), so the UI reflects the new state immediately.

### 6. Repricing / rebilling margin

- [x] **Customer repricing** — `accounts.customers.customerRepricingConfigs.*`.
- [x] **Channel partner repricing** — `accounts.channelPartnerLinks.channelPartnerRepricingConfigs.*`.

> **Implemented.** Repricing margins are live end-to-end for both scopes: shared contracts in
> `GChannel.Shared/Contracts/Repricing.cs` (`RepricingConfig`, `RepricingConfigsResult`,
> `SaveRepricingConfigRequest`, plus `RebillingBases` and `RepricingGranularities` constant classes),
> `IGoogleChannelClient` methods (`ListCustomerRepricingConfigsAsync` /
> `CreateCustomerRepricingConfigAsync` / `UpdateCustomerRepricingConfigAsync` /
> `DeleteCustomerRepricingConfigAsync` and the four `…ChannelPartnerRepricingConfig…` equivalents) in
> the API, cached minimal-API endpoints under `/api/customers/{customerId}/repricing-configs` and
> `/api/channel-partner-links/{linkId}/repricing-configs` (Redis, `CacheSeconds` TTL, list caches
> invalidated on create/update/delete), the `GChannelApiClient` typed-client methods, and Blazor
> pages — **Customer repricing** (`/customers/{id}/repricing`) and **Channel partner repricing**
> (`/channel-partner-links/{id}/repricing`) — each with an inline create/edit form (effective invoice
> month, percentage adjustment, rebilling basis) and a delete action.
>
> **Granularity.** Customer configs use **entitlement granularity**: each config targets one of the
> customer's entitlements (required), so the create form populates its entitlement picker from
> `entitlements.list` (§3). Channel partner configs use **channel-partner granularity** and reprice
> the whole downstream reseller's bill, so no entitlement is selected. The percentage adjustment is
> carried as a `GoogleTypeDecimal` string; the effective invoice month must be the current or a
> future month.
>
> **Correlation/navigation.** The **Customer detail** page (§2) gains a **Repricing** action, and each
> config row links its targeted entitlement back to the entitlement detail page (§3). The **Channel
> partner link detail** page (§5) gains a **Repricing** action for the whole-partner margin.
>
> **LROs.** `customerRepricingConfigs` and `channelPartnerRepricingConfigs` `create`/`patch` return
> the config resource directly (not long-running operations), so the UI reflects changes immediately.

### 7. Eventing & operations

- [x] **Pub/Sub subscribers** — `accounts.register` / `unregister` / `listSubscribers` to receive
  entitlement-change notifications instead of polling.
- [x] **Long-running operations** — surface `operations.get` / `list` / `cancel` for async calls
  (create/transfer/change return LROs).

> **Implemented.** A new **Eventing** nav group exposes two pages:
>
> - **Operations** (`/operations`) — track a long-running operation by id (the `operationName` a
>   mutating call returns), watch it poll to `done`, request cancellation, and deep-link to the
>   affected **customer/entitlement** (§2/§3). Backed by `GET /api/operations`,
>   `GET /api/operations/{id}` and `POST /api/operations/{id}/cancel`
>   (`operations.list`/`get`/`cancel`). Listing degrades gracefully if the account doesn't support it.
> - **Notifications** (`/notifications`) — a live feed of Channel change events plus subscriber
>   registration admin. Backed by `GET /api/notifications` (the feed),
>   `GET/POST /api/notifications/subscribers` and `DELETE /api/notifications/subscribers/{email}`
>   (`accounts.listSubscribers`/`register`/`unregister`). Each event deep-links to its customer/
>   entitlement.
>
> **Where the events come from (Azure vs local).** The notification *source* is mandatorily **Google
> Cloud Pub/Sub** — Google publishes entitlement/customer events to a Google-owned topic
> (`accounts.register` grants a service account subscriber access and returns the topic name). There
> is **no Azure Service Bus/Event Grid** equivalent; Azure managed identity is only used to read the
> Google service-account key from Key Vault, and that key then authenticates to Google Pub/Sub.
>
> **Hosting (no new container).** The subscriber runs as a `BackgroundService`
> (`ChannelNotificationsService`) **inside the existing API container app**, exactly like the
> dashboard refresher — no separate worker/container is added. Pub/Sub load-balances messages across
> all connected subscribers, so when the API scales to multiple replicas they share the subscription
> automatically and **no distributed lock is needed** (unlike the dashboard compute, which must be
> single-flight). The only requirement is API `min-replicas ≥ 1`. Received events are written to a
> capped Redis list (`channel:notifications`, trimmed to `PubSubMaxNotifications`) — **not** SQL,
> because the app uses `EnsureCreated` (no migrations) and a new table wouldn't apply to existing
> databases. Local F5 is identical: the same `BackgroundService` runs against your subscription using
> the service-account key from user-secrets. The subscriber is a **no-op** unless
> `GoogleChannel:PubSubProjectId` + `PubSubSubscriptionId` + a service-account key are configured, so
> the rest of the app is unaffected when eventing is off.

### 8. `v1alpha1` preview capabilities (optional, alpha-only)

> **Deferred — not being implemented yet.** These rely on the `v1alpha1` API, which is alpha-only and
> carries breaking-change risk, so they are intentionally out of scope for now. Revisit once (or if)
> they reach stable `v1`.

These have no stable `v1` equivalent yet, so they require opting into the alpha API and accepting
breaking-change risk:

- [ ] **Deal registration** — `opportunities.*` (create/get/patch/query) for submitting and
  tracking sales opportunities.
- [ ] **Provisionable Cloud Identity types** — `accounts.listProvisionableCloudIdentityTypes`
  to drive the customer-creation UX (shows when a transfer is required).
- [ ] **Assign channel partner to entitlement** — `entitlements.assignChannelPartner` for n-tier.

## Known placeholders

- ~~The dashboard figures on the home page are placeholders.~~ **Implemented.** The home page now
  consumes a derived `GET /api/dashboard/summary` endpoint via `GChannelApiClient` instead of the
  hardcoded arrays. **Note:** the `accounts.reports.*` and `accounts.reportJobs.fetchReportResults`
  endpoints are **deprecated** in `v1`, so the figures are aggregated from entitlement/customer data
  rather than the legacy reporting API.

  `GetDashboardSummaryAsync` in `GoogleChannelClient` aggregates the read paths (cached in Redis for
  `CacheSeconds`):

  - **§2 Customer management** (`accounts.customers.list`) — drives the **Customers** card and the
    *customers onboarded* area chart (buckets customers into the trailing 6 months by create time).
  - **§3 Entitlement lifecycle** (`entitlements.list`) — drives **Active SKUs** (active count),
    **Trials**, **Suspended**, active-seat totals, and the **Product mix** donut (active entitlements
    grouped by product, top 8).
  - **§1 Catalog browsing** — a single `offers.list` lookup resolves SKU/product IDs into friendly
    donut labels.

  The aggregation makes N+1 Channel API calls (customers + per-customer entitlements); the
  per-customer lists run with bounded parallelism (6 concurrent) under a 35s time budget so the call
  always completes within the HTTP attempt timeout and the cached result warms up. Customers that
  error out or aren't reached within the budget are reported via `SkippedCustomerCount` and surfaced
  on the home page as an "N customers couldn't be loaded" warning. The home page ties the request to
  the component lifetime and treats cancellation as benign. The former **Pending checks** card was
  replaced with **Suspended**; the **Channel links** card was restored with §5 (it counts channel
  partner links via a cheap account-level `channelPartnerLinks.list` folded into the overview phase)
  and now sits alongside **Customers**, **Active SKUs** and **Suspended**.

## Notes

- `GoogleChannel:AccountId` is required for every Channel API call and is validated at runtime.
- Most mutating calls (create/transfer/change) return **long-running operations**; the **Operations**
  page (§7) polls `operations.get` and reflects pending/done/failed state, and entitlement actions
  surface the returned operation name for tracking.

## 9. User onboarding (implemented)

> **Implemented.** All four phases are live (see the **Implementation phases** + status note below).
> See [UI.md](UI.md) for the manual walkthrough the in-app tour automates. This section is kept as the
> design rationale for the shipped onboarding experience.

Goal: guide a brand-new reseller user from first sign-in to their first successful action (verify a
domain → create a customer → purchase an entitlement) without reading docs.

### Onboarding patterns to consider

- **Product tour / guided tour** — a one-time, app-wide sequence triggered on first sign-in that walks
  across pages (Dashboard → Customers → Catalog → Entitlements). Best for the overall "what is this
  app" orientation. Should be skippable and resumable, with completion stored per user.
- **Guided walkthrough** — a task-focused, multi-step sequence scoped to a single workflow (e.g.
  "Create your first customer"). Launchable on demand from a **Help / Get started** menu, not just at
  first run.
- **Interactive tutorial** — a hands-on variant of the walkthrough that requires the user to perform
  each real step (fill the form, click purchase) before advancing, using safe/test data where
  possible. Highest engagement, highest build cost.
- **First-time onboarding flow** — a short setup checklist surfaced on the Dashboard ("1. Verify a
  domain  2. Add a customer  3. Buy a SKU") with progress that ticks off as the user completes real
  actions. Lowest friction; complements (doesn't replace) a tour.

### UI element techniques (which to use where)

- **Coach marks** (highlighted callouts that dim the rest of the screen and point at a control) — use
  for the **app-wide product tour** to spotlight nav groups and primary actions (e.g. the **New
  customer** button, the **Eventing** nav group). High visibility; use sparingly.
- **Tooltips** (small popups tied to one element, on hover/focus) — use for **always-available,
  passive help** on individual fields and icons (e.g. what *Cloud Identity check* does, what
  *rebilling basis* means on the repricing form). Not a sequence — ambient hints.
- **Hotspots / beacons** (pulsing dots inviting a click) — use to **draw attention to a newly added or
  underused feature** without forcing a full tour (e.g. a beacon on the **Operations** or
  **Notifications** nav link the first time they appear). Dismiss on click.
- **Modals / popovers** (step cards shown in sequence) — use for the **guided walkthrough/interactive
  tutorial** step cards ("Step 2 of 4: enter the customer's primary domain"), and a single welcome
  **modal** on first sign-in offering "Take the tour" / "Skip".

### Suggested mapping to this app

| Flow | Pattern | Primary UI element |
| --- | --- | --- |
| First sign-in orientation | Product tour | Welcome modal → coach marks across nav |
| "Verify a domain" | Guided walkthrough | Popover step cards on `/accounts/cloud-identity` |
| "Create your first customer" | Interactive tutorial | Popover steps on `/customers/new` + field tooltips |
| "Buy your first SKU" | Interactive tutorial | Popover steps on the purchase flow |
| Dashboard setup progress | Onboarding checklist | Checklist card on `/` that ticks real actions |
| Highlight new Eventing pages | Beacon | Hotspot on the **Eventing** nav links |

### Implementation notes (for whoever picks this up)

- Persist per-user completion/dismissal (e.g. a small `UserOnboardingState` row keyed by the signed-in
  Google subject) so tours don't repeat; expose a **"Restart tour"** action.
- Prefer an existing Blazor-compatible tour library over a bespoke build if one fits MudBlazor; only
  hand-roll coach marks/beacons if needed.
- Keep every step skippable and keyboard-accessible; never block the UI.
- Drive step content from the [UI.md](UI.md) walkthrough so docs and the in-app tour stay in sync.

### Implementation phases

- [x] **Phase 1 — Onboarding checklist + welcome (no JS, highest value)**
  - [x] `OnboardingState` model + per-user storage (browser `ProtectedLocalStorage` — chosen over a new
    EF table because the app uses `EnsureCreated` with no migrations; Redis/cross-device is a later
    upgrade).
  - [x] First-run **welcome** card (MudBlazor) with *Get started* / *Skip onboarding*.
  - [x] Dashboard **checklist** that ticks steps off from real signals (customer count, entitlements)
    plus manual steps (verify a domain, explore eventing), dismissable.
- [x] **Phase 2 — App-wide product tour (Driver.js via JS interop)**
  - [x] `wwwroot/js/onboarding.js` + `OnboardingTourService` C# wrapper.
  - [x] Ordered tour over the always-present nav drawer + app bar (spotlight coach marks + popover step
    cards), skippable/resumable, completion persisted; **Take the product tour** action in the app bar.
  - [x] Stable `data-onboarding` target hooks on nav groups + app-bar buttons.
- [x] **Phase 3 — Per-workflow guided walkthroughs / interactive tutorials**
  - [x] Scoped popover step sequences on `/accounts/cloud-identity`, `/customers/new`, and the purchase
    flow, via a reusable `GuidedWalkthrough` component (auto-runs once per user + a **Show me how**
    relaunch button), reusing the phase 2 Driver.js interop.
  - [x] "Interactive" variant that gates **Next** on the real step completing (the new-customer
    walkthrough blocks until the organization name + domain are filled, via `WalkthroughStep.RequireValueOf`).
- [x] **Phase 4 — Ambient tooltips + feature beacons**
  - [x] `MudTooltip` field/icon help (rebilling-basis info icon on the customer + partner repricing pages).
  - [x] Pulsing **beacons** (`FeatureBeacon`) on the new **Operations**/**Notifications** nav links that
    auto-dismiss per user once the feature is visited (or on click).

> **Status:** Phases 1–4 are implemented. Phase 1: welcome card + dashboard checklist (with a
> **Restart tour** action and a dismissed-state launcher). Phase 2: guided Driver.js product tour over
> the nav + app bar. Phase 3: per-workflow walkthroughs (Cloud Identity, new-customer — input-gated,
> purchase). Phase 4: ambient rebilling-basis tooltips + auto-dismissing nav feature beacons.

## 10. Persistent read-model (scale-out for a large distributor)

> **Status:** ✅ Complete. Phases 1–5 implemented (durable read-model + incremental sync worker + SQL-backed
> indirect estate / seat-ranked top resellers + entitlement seat sync + read-model-backed entitlement-list /
> partner-customers pages) plus the read-path polish (server-side paged/sorted lists, *as-of* labels,
> Refresh-now). Only `DashboardSnapshots` trend history is left as optional future work. Feature-flagged
> `GoogleChannel:UseReadModel`. See [architecture.md](architecture.md) (dashboard two-phase + background
> refresh) and the §5 channel-partner-links narrative for the design this evolves from.

### Why (the problem at scale)

Today **nothing about the estate is persisted**. Customers, entitlements and the per-reseller
(indirect) estate are fetched **live from the Channel API per request** and cached only in **Redis**
with short TTLs; the background refresher recomputes the whole dashboard and warms Redis (with durable
`:last` stale copies). The SQL database holds only `IdentityCheckLogs`. That design is fine at the
current scale but breaks down as a large distributor grows:

- **Quota is the hard ceiling.** `accounts.customers.list` and `accounts.channelPartnerLinks.customers.list`
  share a low project quota (~24/min observed). The indirect estate is a **fan-out of one
  `customers.list` per ACTIVE link**. At ~36 links that's ~110s/refresh at 20/min; at **500 links** a
  single full refresh is **~25+ minutes of solid quota** — it can no longer complete within one
  background interval, and on-demand can never compute it.
- **Cold starts re-burn quota.** Redis is a cache; on a cold cache (scale-to-zero, restart, eviction)
  the whole estate must be re-listed before anything renders.
- **No history.** Growth/churn/trend reporting is impossible without storing snapshots over time
  (today only trailing onboarding is derived from `CreateTime`).
- **No queryability.** Sorting/filtering/aggregating across the whole estate is recomputed in memory
  each request instead of being a SQL query.

### Goal

A durable **materialized read-model** in SQL that the UI reads from instantly, kept fresh by an
**incremental** background sync that refreshes only a *slice* of the estate each cycle (round-robin by
staleness) so it stays within quota regardless of estate size. Live API/Redis remains the source of
truth for **writes** and for **on-demand detail** (a single customer/link), overlaid on the stored
read-model with clear *as-of* timestamps.

### Architecture

- **Read-model store (SQL via EF Core).** New tables alongside `IdentityCheckLogs`. Because the app
  currently uses `EnsureCreated()` with **no migrations**, the first task is to **adopt EF Core
  migrations** (or a versioned schema strategy) — additive tables only, no change to existing data.
- **Sync service (evolves `DashboardRefreshService`).** Instead of "recompute everything each tick",
  the worker pulls a **work queue of the stalest entities** (links/customers), refreshes up to a
  **per-cycle quota budget**, upserts rows, and advances each entity's `LastSyncedUtc`. The existing
  Redis single-flight lock + `RequestPacer` are reused to stay under quota cluster-wide.
- **Read path.** Dashboard/estate endpoints serve **from the read-model** (a single indexed SQL query),
  optionally overlaying the live Redis cache for a just-mutated entity, and return an *as-of* timestamp
  (the min/oldest `LastSyncedUtc` in the result) which the UI already has a place to show (the
  refresh-status chip). On-demand **detail** pages (one customer / one link) still call live API +
  Redis for freshness, then write-through to the read-model.
- **Write-through.** Mutations (create/patch/delete customer, entitlement lifecycle, link state) update
  the read-model row immediately (or mark it stale) so the UI reflects the change without waiting for
  the next sync cycle.

### Proposed schema (additive)

| Table | Purpose | Key columns |
| --- | --- | --- |
| `ResellerLinks` | one row per channel partner link | `LinkId` (PK), `ResellerCloudId`, `PrimaryDomain`, `LinkState`, `CustomerCount`, `CreateTime`, `LastSyncedUtc`, `SyncError?` |
| `CustomerRecords` | one row per customer (direct **and** indirect) | `CustomerId` (PK), `OrgName`, `Domain`, `CloudIdentityId?`, `OwningLinkId?` (null = direct), `CreateTime`, `LastSyncedUtc` |
| `EntitlementRecords` *(phase 4)* | one row per entitlement | `EntitlementId` (PK), `CustomerId` (FK), `OwningLinkId?`, `ProductId`, `ProductName?`, `SkuId`, `SkuName?`, `OfferId`, `OfferName?`, `State`, `Seats`, `IsTrial`, `CreateTime?`, `UnitPrice`, `Currency?`, `RepricingPercent`, `LastSyncedUtc`, `IsDeleted` |
| `SyncCursors` | per-entity-type sync bookkeeping | `Scope` (PK, e.g. `links`/`customers`/`entitlements`), `LastFullPassUtc`, `NextPageToken?`, `Notes` |
| `DashboardSnapshots` *(optional, history)* | periodic point-in-time totals | `Id` (PK), `TakenUtc`, `DirectCount`, `IndirectCount`, `ActiveSkus`, `TrialCount`, `SuspendedCount` |

Indexes: `CustomerRecords(OwningLinkId)`, `CustomerRecords(LastSyncedUtc)`, `ResellerLinks(LastSyncedUtc)`,
`EntitlementRecords(CustomerId)` — the read-model aggregates (direct vs indirect totals, top resellers,
product mix) become simple `GROUP BY` queries.

### Incremental sync strategy (the core win)

- **Round-robin by staleness.** Each cycle: take the **N stalest** `ResellerLinks` (and any links never
  synced), refresh each link's `customers.list`, upsert `CustomerRecords` for that link, update the
  link's `CustomerCount` + `LastSyncedUtc`. N is sized to the **per-cycle quota budget** (e.g. at
  20/min and a 60s cycle, N≈18 links/cycle) so **every cycle stays within quota** no matter how many
  links exist — the whole estate is covered over several cycles (a "rolling refresh").
- **Metadata vs entitlements are separated.** The link/customer **metadata** upsert above touches only
  the `ListCustomers` quota, while **entitlement** syncing (the contended `ListEntitlements` quota) is a
  single **unified staleness-rotated pass** at the end of each cycle over the stalest
  `ReadModelCustomersPerCycle` customers across the *whole* estate (direct **and** indirect), ordered by
  `CustomerRecords.LastSyncedUtc` (now meaning *entitlement* freshness; new rows = `MinValue` = head of
  queue, stamped after each customer is synced or skipped). This guarantees the indirect estate +
  per-link customer counts populate from the cheap fan-out **independent of** the slower entitlement
  quota, and stops the direct-customer entitlement fan-out from draining a whole cycle before the
  indirect fan-out runs (the earlier ordering could leave the indirect estate at 0 under quota
  pressure). Tunable via `GoogleChannel:ReadModelCustomersPerCycle` (default 60).
- **Tunable freshness.** Full-estate refresh interval ≈ `(#links / N) × cycleSeconds`; expose it via the
  existing `GoogleChannel:DashboardCustomerListRequestsPerMinute` + a new `links-per-cycle`/budget knob.
- **Deletion reconciliation.** Customers/links absent from a fresh list pass are soft-deleted (or
  `OwningLinkId` cleared) so the read-model converges to the live estate.
- **Quota math (illustrative, 20/min ListCustomers):** 50 links → full pass ≈ 2.5 min; 500 links ≈ 25
  min; 2,000 links ≈ 100 min. The dashboard is **always instant** (SQL) and shows an honest *as-of*
  age; only the freshness of the *tail* degrades with size, which is the correct trade-off.

### Read-path changes

- [x] Dashboard `summary` + the indirect estate + **Top indirect resellers** chart read from SQL
  aggregates instead of the live fan-out; the fan-out is skipped when `UseReadModel` is on.
- [x] Customers and Channel-partner-links **list** pages can page/sort/filter server-side against SQL
  (removes the in-memory full-list load at scale).
- [x] Every estate view shows an *as-of* timestamp; a **Refresh now** action can prioritise a specific
  link/customer into the front of the sync queue.
- [x] The per-customer **entitlement list** and a partner's **customers** list (both on the contended
  `ListEntitlements`/`ListCustomers` quotas) read from the read-model when `UseReadModel` is on, with a
  live fallback before the first sync.

### Phases

- [x] **Phase 1 — Foundations.** Added `ResellerLinks` + `CustomerRecords` + `SyncCursors` (entities in
  `GChannelDbContext`, created idempotently via raw SQL `IF OBJECT_ID(...) IS NULL CREATE TABLE` in
  `Program.cs` since the app uses `EnsureCreated` not migrations); feature-flagged
  `GoogleChannel:UseReadModel` (+ `ReadModelLinksPerCycle`).
- [x] **Phase 2 — Incremental sync worker.** `ReadModelSyncService` (staleness-driven, budgeted,
  round-robin; reuses the Redis single-flight lock + service-account client); upserts links + direct
  + indirect customers; soft-deletes vanished rows; records per-entity `LastSyncedUtc` + `SyncError`.
  No-op unless `ReadModelSyncEnabled`.
- [x] **Phase 3 — Read-model-backed dashboard.** Dashboard `/summary` overlays the indirect estate
  (`IndirectCustomerCount` + **Top indirect resellers** ranked by active seats) from SQL aggregates
  (`OverlayReadModelAsync` over `CustomerRecords`+`ResellerLinks`) when `UseReadModel` is on; the live
  per-reseller fan-out is then skipped to save quota. No-op (keeps live values) until rows are synced.
- [x] **Phase 4 — Entitlements + seats.** `EntitlementRecords` (id/customer/owningLink/product/sku/offer/
  state/seats/trial) synced per customer in the worker; active seats denormalised onto
  `CustomerRecords.SeatCount` for fast reseller seat ranking. (`DashboardSnapshots` history still optional.)
- [x] **Phase 5 — Read-model-backed detail/list pages.** The two interactive reads on the **contended**
  per-minute quotas now serve from SQL when `UseReadModel` is on, so they no longer compete with the sync
  worker for the same buckets: a customer's **entitlement list** (`GET /api/customers/{id}/entitlements`)
  reads `EntitlementRecords`, and a partner's **customers** (`GET /api/channel-partner-links/{id}/customers`)
  reads `CustomerRecords` for the owning link. Both fall back to the live, cached call when the read-model
  has no rows yet (cold start / freshly rostered link). Offer/SKU display names + create time are
  denormalised onto `EntitlementRecord` (`OfferName`/`SkuName`/`CreateTime`) for free from the pricing
  pass's `offers.list`, so the list renders identical names offline. Entitlement **detail** (`.get`),
  customer **detail**, the **catalog**, **repricing** and **transfers** stay live — they're on
  lighter/uncontended quotas or, for transfers, must be computed in real time against current external
  subscriptions (a stored copy would be wrong once stale).

### Risks &amp; caveats

- **Migrations adoption** is a prerequisite and a one-time disruption (the app has only ever used
  `EnsureCreated`); plan a clean cutover (the DB currently holds only audit logs, so low risk).
- **Staleness semantics** must be explicit in the UI (*as-of* labels) so users trust the numbers; the
  refresh-status chip is the foundation to extend.
- **It does not raise the API quota** — it spends quota *incrementally and durably* instead of
  re-spending it per request/restart. Quota increase requests to Google remain the only way to make the
  *tail* fresher faster.
- **Consistency** between live detail views and the read-model (write-through + short Redis overlay keeps
  them aligned; accept brief eventual-consistency windows for the aggregates).

### Backout

Feature-flagged (`UseReadModel`): if disabled, the app falls back to today's live-fan-out + Redis path
unchanged. The read-model tables are additive and can be dropped without affecting existing
functionality.

## 11. Pricing &amp; billing information (offer pricing, computed cost &amp; margin)

> **Status:** proposed — not implemented. This captures what pricing the Cloud Channel `v1` API
> actually exposes, what it does **not**, and a phased plan to surface per-offer / per-entitlement
> pricing and a computed end-customer price + reseller margin. The console
> (`console.cloud.google.com`) shows the same data the API returns plus Google's own invoice exports
> (the latter are **not** part of the Channel API — see caveats). Builds on §1 Catalog (offers),
> §3 Entitlements, and §6 Repricing.

### What the API can and cannot give us

**Available via the Channel API (`v1`):**

- **Offer wholesale pricing.** Each [`Offer`](https://docs.cloud.google.com/channel/docs/reference/rest/v1/Offer)
  carries `priceByResources[]` (`PriceByResource`): a `resourceType` (`SEAT`, `MAU`, `GB`,
  `LICENSED_USER`, `MINUTES`, `IAAS_USAGE`, `SUBSCRIPTION`), a `price` (`Price`) and/or time-ranged
  `pricePhases[]`. `Price` = `basePrice` + `discount` (decimal, 0.2 = 20%) + `effectivePrice` (both
  `Money` = `currencyCode`/`units`/`nanos`) + optional `priceTiers[]` (per-seat tier banding) and
  `discountComponents[]` (incl. `RESELLER_MARGIN`). The `Plan` adds `paymentPlan`/`paymentType`
  (PREPAY/POSTPAY) + `paymentCycle`. This is the **reseller's cost from Google**.
- **Purchasable pricing in context.** `accounts.customers.listPurchasableOffers` /
  `listPurchasableSkus` return offers with the same price structure plus a `priceReferenceId`, which a
  purchase request can echo to lock the quoted price.
- **The markup we apply.** §6 `RepricingConfig` (already implemented) is the percentage adjustment +
  rebilling basis that turns wholesale cost into the end-customer/reseller price — the second half of a
  "cost → price → margin" view.
- **Seats.** `num_units` per active entitlement (already aggregated for the dashboard).

**NOT available via the Channel API:**

- **Actual invoiced amounts.** There is no invoice/billing-actuals endpoint. The deprecated
  `accounts.reports.*` are gone in `v1`; `queryEligibleBillingAccounts` returns *which* billing account
  is eligible, not money. Real monthly invoice/usage totals live in **Cloud Billing / partner billing
  exports (BigQuery)** — a separate API/dataset, out of scope here.
- So this feature surfaces **list/wholesale price + computed price + estimated margin**, clearly
  labelled as estimates, *not* billed figures.

### Goal

Surface, per offer and per entitlement: the **wholesale cost** (offer `effectivePrice` × seats), the
**computed end-customer price** (wholesale + repricing % adjustment), and the **estimated margin**, with
correct currency, payment cycle and tiered pricing — plus an optional estate-wide MRR/cost rollup on the
dashboard. All figures explicitly marked *estimated list pricing*, not invoices.

### Proposed contract additions (`GChannel.Shared/Contracts`)

- `MoneyAmount { CurrencyCode, Units (long), Nanos (int), Display }` — map of `GoogleTypeMoney`.
- `OfferPrice { ResourceType, BasePrice, EffectivePrice, DiscountPercent, PricePeriod, Tiers[] }` and
  `OfferPriceTier { FirstResource, LastResource, EffectivePrice }`.
- Extend `CatalogOffer` (§1) with `IReadOnlyList<OfferPrice> Pricing` + `PaymentPlan`/`PaymentCycle`.
- `EntitlementPricing { OfferEffectivePrice, Seats, WholesaleTotal, RepricingPercent, ComputedPrice, EstimatedMargin, Currency }`.

### Phases

- [x] **Phase 1 — Map offer pricing (read-only).** Extend offer mapping to read `priceByResources` /
  `pricePhases` / `priceTiers` into `OfferPrice`; show price on the **Offers** page (§1) and the
  **Purchase entitlement** flow (price-per-seat, currency, cycle). One `Money` → decimal helper
  (`units + nanos/1e9`). *Implemented: `MoneyAmount`/`OfferPrice`/`OfferPriceTier` contracts,
  `CatalogOffer`/`PurchasableOffer` gained `Pricing` + `PaymentCycle`; `MapOfferPricing`/`MapMoney`/
  `PaymentCycleLabel` helpers; Offers table "Price (est. list)" column; purchase flow shows
  per-seat × seats estimate, labelled not-invoiced.*
- [x] **Phase 2 — Per-entitlement cost.** On entitlement detail/list, resolve the entitlement's offer
  price × `num_units` = wholesale total; overlay §6 `RepricingConfig` to compute end-customer price +
  margin. Cache offer pricing (a single `offers.list`, reuse the catalog lookup). *Implemented: the
  read-model `EntitlementRecord` carries `UnitPrice`/`Currency`/`RepricingPercent`, surfaced on the
  `Entitlement` contract (`UnitPrice`/`PriceCurrency`/`RepricingPercent`) and the `EstateCustomer`
  contract (`EstimatedMonthlyTotal`/`Currency`). The **entitlement list** shows an "Est. monthly"
  column (`price × seats × (1 + percent/100)`) with a breakdown tooltip, the **customers list** shows
  an "Est. monthly" column per customer, and the **customer detail** page sums active priced
  entitlements into an "Estimated monthly value" panel — all labelled estimated/not-invoiced and all
  served from the read-model with no per-request Channel API calls.*
- [x] **Phase 3 — Estate rollups (optional).** Estimated monthly wholesale cost + repriced revenue +
  margin on the dashboard (background-computed alongside seats), with an *estimated, not invoiced*
  disclaimer; per-reseller cost/margin extends the §10 read-model. *Implemented: `EntitlementRecord`
  gained `UnitPrice`/`Currency`/`RepricingPercent` (additive idempotent SQL ALTERs in
  `EnsureReadModelTablesAsync`). `ReadModelSyncService` resolves them per cycle — one quota-light
  `offers.list` builds an `offerId → (effective seat price, currency)` lookup; §6 repricing is
  denormalised per entitlement (per-customer `customerRepricingConfigs` override, else the owning
  link's CHANNEL_PARTNER-granularity `channelPartnerRepricingConfigs` mark-up, else 0), all
  best-effort so a pricing/repricing failure never blocks the estate sync. Dashboard `/summary`
  overlay (`ComputeEstateValueAsync`) rolls up active priced entitlements into `DashboardEstateValue`
  (wholesale/revenue/margin in the dominant currency, mixed-currency + unpriced counts) and adds
  per-reseller `WholesaleMonthly`/`MarginMonthly` to `TopIndirectResellers`. Home page shows an
  "Estimated estate value (monthly)" panel with the not-invoiced disclaimer.*
- [ ] **Phase 4 — Billing export (optional, out of Channel API).** Document/integrate BigQuery partner
  billing export for *actual* invoiced figures; clearly separated from API list pricing. *Deferred (not
  blocking). The Channel API exposes **no** billing actuals (the `accounts.reports.*` reporting API was
  removed in `v1`; `queryEligibleBillingAccounts` returns eligibility, not money), so real invoiced
  totals only exist in the **Cloud Billing partner billing export → BigQuery** — a separate data source
  the distributor must first enable in GCP. Implementing it is a second integration unlike anything the
  app does today: a new `Google.Cloud.BigQuery.V2` client + credential path (a third auth surface beyond
  the user OAuth token and the DWD service account; WIF could work since BigQuery needs no domain-wide
  delegation), parameterised SQL against Google's schema-versioned, date-partitioned export tables,
  bytes-scanned cost control (partition filters + cached/scheduled rollups), and a hard UI boundary so
  invoiced figures are never conflated with the estimated list pricing from Phases 1–3. It serves a
  narrow finance/reconciliation audience and the data lags a day or more. It is self-contained and
  additive, so deferring it costs no rework — revisit only when there's a concrete need to reconcile
  GChannel's estimates against real Google invoices. Until then Phases 1–3 cover the estimated
  cost → price → margin story end to end. Proceeding to Phase 5 (in-API console parity) instead.*

### Console parity — customer list, expandable subscriptions & entitlement detail

Goal: bring our customer area in line with `channelservices.cloud.google.com` and add the pricing
above on top, so a user sees the same shape they know from the console plus computed cost/margin.

- [x] **Phase 5 — Customer list parity + search.** Render the list as **Name · Domain · Subscriptions
  · Renewal date** like the console: `Subscriptions` = count of entitlements by state (e.g. `6 Active
  2 Suspended`, `0 Active`), `Renewal date` = earliest upcoming `commitmentEndTime` + that offer name
  (`Sep 27, 2027 - Google Workspace for Education Plus`) or `—` when none. All sourced from
  entitlement state + offer name (§3) aggregated per customer (reuse the §10 read-model so it's cheap).
  Add a **search box** (by customer name or domain) over the cached customer list, client-side filter
  first, server-side `accounts.customers.list` pagination later.
  *Implemented. Denormalised `CommitmentEndTime` onto `EntitlementRecord` at sync time
  (`ReadModelSyncService` reads `Commitment.EndTime` from the mapped entitlement; additive idempotent
  SQL column). `EstateCustomer` now carries `ActiveSubscriptions`/`SuspendedSubscriptions` and
  `NextRenewalUtc`/`NextRenewalOfferName`; `GET /api/estate/customers` computes them per page from the
  read-model in the same single entitlement query that already produced the estimated monthly total
  (state counts + earliest active future `CommitmentEndTime` and its offer name). `Customers.razor`
  gained **Subscriptions** (`N Active · M Suspended`) and **Renewal** (`MMM d, yyyy` + offer name, or
  `—`) columns. The server-side debounced search box (org/domain/id) was already present, satisfying
  the search requirement directly against the cached read-model.*
- [x] **Phase 6 — Expandable customer → subscription cards.** Expanding a customer shows one card per
  entitlement: offer name, plan summary (`Annual Plan (Monthly Payment)`), `Renewal <date>`,
  `assigned / total licenses` (`2 / 3`) and state badge (`Active`). Renewal/plan/licenses from
  entitlement+offer; **assigned-seat count is _not_ in the Channel API** (it's Admin SDK / Directory
  usage) — show total seats only or a clearly-flagged estimate until that source is added.
  *Implemented. `EntitlementRecord` gained `PlanDescription` (denormalised at sync from the offer's
  payment plan/cycle + the commitment term — `BuildPlanDescription` derives `Annual`/`Monthly`/`N-Year`
  from the commitment start→end span, else the offer payment plan, joined with the offer payment cycle;
  additive idempotent SQL column, built from the same single `offers.list` the catalog/pricing pass
  already does). `Entitlement` contract gained `PlanDescription`; the read-model `MapEntitlement` exposes
  it and maps `CommitmentEndTime`→`Commitment.EndTime`. `Customers.razor` rows now expand (chevron
  toggle) to a `MudGrid` of subscription `MudCard`s — offer title, state chip, plan summary, `Renewal
  <MMM d, yyyy>`, and `— / N licenses` (assigned flagged unavailable via tooltip) + a **Details** link to
  the entitlement (§3). Cards lazy-load the customer's entitlements from the read-model on first expand
  (cached per row). Assigned-seat counts remain out (Admin SDK, deferred).*
  *Assigned-seat counts — why deferred: total seats (`num_units`) come from the entitlement, but how many
  seats are **assigned** to users lives in the **customer's own Workspace tenant** (Enterprise License
  Manager API `licensing.googleapis.com` / `Google.Apis.Licensing.v1` — `LicenseAssignments.ListForProduct
  AndSku(productId, skuId, customerId)`, count the paginated assignments), a different trust boundary than
  the Channel API. The reseller's domain-wide-delegation credential (scoped `apps.order`, impersonating
  **our** reseller admin) does NOT grant access to a customer's directory. Reading it needs a **per-customer
  authorization** — each customer's super-admin authorizing our service-account client id for
  `apps.licensing` in **their** Admin console (or impersonating an admin in that customer's domain) — i.e.
  N tenant-specific consents, not one reseller credential, which most reseller setups don't have and can't
  self-grant. If that existed, the work is additive: new `Google.Apis.Licensing.v1` client + a third
  credential surface (scoped `apps.licensing`, impersonation target resolved per customer/domain), a
  per-entitlement `ListForProductAndSku` count (its own quota bucket; ~1 paged call per entitlement so it'd
  need its own pacer/budget or an opt-in flag), a nullable `EntitlementRecord.AssignedSeats` (idempotent
  SQL ALTER, best-effort so "—" stays distinct from 0) surfaced on the `Entitlement` contract, and swapping
  the card's `—` for the count. Self-contained → no rework cost to add later.*
- [ ] **Phase 7 — Subscription detail (licenses + payment + billing).** Clicking a card shows
  **Licenses** (total `num_units`, assigned where available, manage link → §3), **Payment** (cycle +
  computed `/month` estimate from §11 pricing, marked *estimated*, renewal datetime), `Renewal` term
  text, and **Billing account name + ID** from the entitlement's `billingAccount`. Pricing reuses
  Phases 1–2; billing actuals stay out (Phase 4 caveat).

### Risks &amp; caveats

- **Estimates, not invoices** — must be labelled everywhere; promo/tier/contract terms can diverge from
  list price. **Currency** comes from each `Money`; never assume one currency. **Tiered/phased** pricing
  needs the right tier/phase picked by seat count and elapsed months. **Repricing** is %-only here (the
  conditional-override breakdown is deferred). Pricing adds catalog quota but reuses existing cached
  `offers.list` — no per-entitlement price calls.

