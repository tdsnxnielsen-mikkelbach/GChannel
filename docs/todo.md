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

## 9. User onboarding (suggestions — not yet implemented)

> **Not implemented.** Captured here as a design backlog item to make the console friendlier for
> first-time users. See [UI.md](UI.md) for the current manual walkthrough this would automate. No code
> exists for any of the below yet — these are proposed approaches to evaluate.

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

- [ ] **Phase 1 — Onboarding checklist + welcome (no JS, highest value)**
  - [x] `OnboardingState` model + per-user storage (browser `ProtectedLocalStorage` — chosen over a new
    EF table because the app uses `EnsureCreated` with no migrations; Redis/cross-device is a later
    upgrade).
  - [x] First-run **welcome** card (MudBlazor) with *Get started* / *Skip onboarding*.
  - [x] Dashboard **checklist** that ticks steps off from real signals (customer count, entitlements)
    plus manual steps (verify a domain, explore eventing), dismissable.
- [ ] **Phase 2 — App-wide product tour (Driver.js via JS interop)**
  - [x] `wwwroot/js/onboarding.js` + `OnboardingTourService` C# wrapper.
  - [x] Ordered tour over the always-present nav drawer + app bar (spotlight coach marks + popover step
    cards), skippable/resumable, completion persisted; **Take the product tour** action in the app bar.
  - [x] Stable `data-onboarding` target hooks on nav groups + app-bar buttons.
- [ ] **Phase 3 — Per-workflow guided walkthroughs / interactive tutorials**
  - [x] Scoped popover step sequences on `/accounts/cloud-identity`, `/customers/new`, and the purchase
    flow, via a reusable `GuidedWalkthrough` component (auto-runs once per user + a **Show me how**
    relaunch button), reusing the phase 2 Driver.js interop.
  - [ ] "Interactive" variant that gates **Next** on the real step completing (e.g. a field filled).
- [ ] **Phase 4 — Ambient tooltips + feature beacons**
  - [ ] `MudTooltip` field/icon help (e.g. *rebilling basis*, *Cloud Identity check*).
  - [ ] Pulsing **beacons** on newly added nav links (e.g. **Eventing**) that dismiss on click, per user.

> **Status:** Phases 1–3 are implemented (welcome card + dashboard checklist; guided Driver.js product
> tour over the nav + app bar; per-workflow walkthroughs on the Cloud Identity, new-customer and purchase
> pages). Phase 4, plus the optional interactive (input-gated) walkthrough variant, remain open.

