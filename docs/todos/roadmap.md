> Part of the [GChannel TODO index](../todo.md).

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
  Planned in **§12** (now that the §7 LRO infrastructure exists). Note: only `provisionCloudIdentity`
  returns a long-running `Operation`; `import` returns the `Customer` resource **directly** (it is a
  synchronous call, so it doesn't need the LRO path).

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
  to drive the customer-creation UX (shows when a transfer is required). Plan +
  GA-interim note in [13-provisionable-cloud-identity-types.md](13-provisionable-cloud-identity-types.md).
- [ ] **Assign channel partner to entitlement** — `entitlements.assignChannelPartner` for n-tier.

