> Part of the [GChannel TODO index](../todo.md).

## 11. Pricing &amp; billing information (offer pricing, computed cost &amp; margin)

> **Status:** ✅ Complete (in-scope). All Channel-API-backed phases are implemented — Phase 1 (offer
> pricing), Phases 2–3 (per-entitlement cost + estate rollups), and Phases 5–7 (console-parity customer
> list, expandable subscription cards, subscription detail). **Phase 4 (BigQuery partner billing export
> for *actual* invoiced figures) is deferred out of scope** — it is not part of the Channel API and needs
> a separate GCP data source/credential; it is self-contained and additive, so deferring it costs no
> rework (see the Phase 4 note). This section captures what pricing the Cloud Channel `v1` API actually
> exposes, what it does **not**, and the phased plan that surfaced per-offer / per-entitlement pricing and
> a computed end-customer price + reseller margin. The console (`console.cloud.google.com`) shows the same
> data the API returns plus Google's own invoice exports (the latter are **not** part of the Channel API —
> see caveats). Builds on §1 Catalog (offers), §3 Entitlements, and §6 Repricing.

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
  billing export for *actual* invoiced figures; clearly separated from API list pricing. **A concrete
  implementation plan now lives in [13-billing-export-bigquery.md](13-billing-export-bigquery.md).**
  *Deferred (not
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
- [x] **Phase 7 — Subscription detail (licenses + payment + billing).** Clicking a card shows
  **Licenses** (total `num_units`, assigned where available, manage link → §3), **Payment** (cycle +
  computed `/month` estimate from §11 pricing, marked *estimated*, renewal datetime), `Renewal` term
  text, and **Billing account name + ID** from the entitlement's `billingAccount`. Pricing reuses
  Phases 1–2; billing actuals stay out (Phase 4 caveat).
  *Implemented on `EntitlementDetail.razor` (`/customers/{id}/entitlements/{id}`), the card's "Details"
  target, served from the **live** `GetEntitlement` (full fidelity — `billingAccount`, `commitment`,
  `parameters`). New **Licenses** card: total = `num_units`, **Assigned** shows "— (not available)" with
  a tooltip pointing at the Admin SDK / per-tenant authorization gap (see Phase 6 note), plus a "Manage
  licenses" button that anchors to the `id="modify"` seats/offer card (§3). The pricing card became
  **Payment &amp; pricing (estimated)**: added a **Billing cycle** row, an **Estimated / month** row that
  normalises the per-cycle computed total by `MonthsInCycle()` (Monthly→1, Annual/Yearly→12, `N-monthly`/
  `N-yearly` parsed; shows the `÷ N mo` working when >1 month), and a **Renewal** row from
  `RenewalTermText()` ("Renews|Ends {date} · auto-renew on|off" from `Commitment.EndTime` + the renewal
  toggle). The **Billing account** Details row now surfaces the **ID** (last path segment of
  `billingAccount`) with the full resource string as a caption — the entitlement carries only the resource
  name, so no separate friendly name is available. Pricing reuses the cached `offers.list` lookup
  (`LookupEntitlementOfferAsync`) + read-model repricing %; every figure stays labelled estimated /
  not-invoiced (Phase 4 caveat). No schema change, no new live price calls.*
- [x] **Phase 8 — Estate entitlements list + dashboard deep-links + customer-detail margin.** An
  estate-wide **Entitlements** page (`/entitlements`, nav under Customers) lists individual
  subscriptions from the read-model with **State** (All/Active/Trial/Suspended) and **Scope**
  (Direct/Indirect/All, default Direct) filters, server-side paging/sort/search, an "Est. monthly"
  value per row and links to the customer + entitlement detail. The dashboard's Suspended-card
  Active/Trial/Suspended numbers now deep-link here (`/entitlements?state=…`, matching the direct-only
  KPI semantics). The **customer detail** "Estimated monthly value" panel was expanded to show
  **Wholesale cost / Repriced revenue / Margin** (previously revenue only). *Implemented: new
  `EstateEntitlement` contract + `GET /api/estate/entitlements` (`EstateEndpoints`, joins
  `EntitlementRecords`→`CustomerRecords` for the org name; `state`/`scope` filters mirror the dashboard
  buckets), `GChannelApiClient.ListEstateEntitlementsAsync`, `EstateEntitlements.razor`, a Customers-group
  nav link, `Home.razor` KPI `MudLink`s, and `CustomerDetail.razor`'s panel now computes per-line
  wholesale + revenue. No schema change, no new live price calls.*
- [x] **Phase 9 — Whole-estate dashboard KPIs + estate-value source split + per-currency chips.** The
  read-model dashboard's entitlement KPIs (Active / Trial / Suspended counts, active seats and product
  mix) now span the **whole estate** (direct + reseller-owned) instead of direct-only, so they line up
  with the estate-value panel which was already whole-estate; the Suspended-card deep-links carry
  `scope=all`. The **estate value** is split into a **By source** table (Direct vs Via resellers,
  wholesale/revenue/margin/subscriptions) and the panel shows a **currency chip per currency**
  (dominant highlighted) rather than one. *Implemented: `BuildReadModelSummaryAsync` aggregates
  active/trial/suspended/seats/mix over all non-deleted entitlements (customer count + onboarding stay
  direct-only); `ComputeEstateValueAsync` groups by currency **and** source and fills new
  `DashboardEstateValueScope Direct`/`Indirect` on `DashboardEstateValue` + each
  `DashboardEstateValueCurrency`; `Home.razor` renders the By-source table, per-currency chips and
  `scope=all` KPI links. No schema change, no new live calls.*

- [x] **Phase 10 — Customer Source (direct/reseller) &amp; Auto-renew columns.** The Customers list now
  has a **Source** column marking each customer **Direct** or **Reseller** (with the indirect
  reseller's friendly name, linking to its channel partner link), an **Auto-renew** column (On/Off/—
  for the next renewing subscription) and a **Source** filter (All / Direct / Via resellers). *Implemented:
  new denormalised `EntitlementRecord.RenewalEnabled` (idempotent SQL ALTER + EF model + synced from
  `Commitment.RenewalEnabled`); new `EstateCustomer.ResellerName` (per-page `ResellerLinks` join) and
  `NextRenewalAutoRenew` (from the next-renewal entitlement); `/api/estate/customers` `linkId` now
  accepts `indirect`; `Customers.razor` adds the two columns + Source select (defaults to All). Auto-renew
  populates after a worker redeploy + sync cycle.*
- [x] **Phase 11 — Estate value By-source *per currency* + product-mix name resolution.** The dashboard
  **By source** table now renders a **Direct** and a **Via resellers (indirect)** line **for every
  currency** (not just the dominant one) — a **Currency** column and a *By currency (total)* table appear
  when the estate spans more than one currency — so multi-currency estates see the full source×currency
  matrix. The **Product mix** donut resolves more friendly names: product display names are now
  supplemented from the offer catalog, cutting the number of raw product ids shown. *Implemented (UI-only
  for the source split — `DashboardEstateValueCurrency.Direct`/`Indirect` already existed): `Home.razor`
  iterates `estate.Currencies` for the By-source rows; new `CatalogOffer.ProductDisplayName` (mapped from
  `offer.Sku.Product.MarketingInfo.DisplayName` in `ListOffersAsync`) feeds an `OfferCatalog.ProductNames`
  supplement in `ReadModelSyncService`, used as a fallback after `products.list` when denormalising
  `EntitlementRecord.ProductName`. Product-name improvements land after a worker redeploy + sync cycle.
  **Margin is 0 by design** when no repricing/rebilling is configured — direct margin is your
  customer-level repricing, indirect is your channel-partner rebilling mark-up; a downstream reseller's
  own margin to their end customers is private and not exposed by the Channel API. No schema change.*

- [x] **Phase 12 — Reseller value rollup + estate-wide freshness badge.** The channel-partner-link
  detail page now shows an **Estimated business value (monthly)** panel — wholesale cost, repriced
  revenue and margin across *all* of that reseller's customers' active priced entitlements (dominant
  headline + per-currency table + customer/seat/subscription counts) — so you can see what each reseller
  is doing. The Customers page **As of** badge no longer sticks on "—": its freshness timestamp is now
  computed **estate-wide** (most recent `CustomerRecord.LastSyncedUtc`) instead of from the current page
  only. *Implemented: new `ResellerEstateValue` contract, `GET /api/estate/resellers/{linkId}/value`
  (`EstateEndpoints`, groups the link's `EntitlementRecords` by currency), `ApiRoutes.EstateResellerValue`,
  `GChannelApiClient.GetResellerValueAsync`, an `Estimated business value` card on
  `ChannelPartnerLinkDetail.razor`, and the estate-wide `AsOf` query on `GET /api/estate/customers`. No
  schema change, no new live calls.*

### Risks &amp; caveats

- **Estimates, not invoices** — must be labelled everywhere; promo/tier/contract terms can diverge from
  list price. **Currency** comes from each `Money`; never assume one currency. **Tiered/phased** pricing
  needs the right tier/phase picked by seat count and elapsed months. **Repricing** is %-only here (the
  conditional-override breakdown is deferred). Pricing adds catalog quota but reuses existing cached
  `offers.list` — no per-entitlement price calls.

