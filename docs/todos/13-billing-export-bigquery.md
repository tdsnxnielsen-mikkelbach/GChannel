> Part of the [GChannel TODO index](../todo.md).

## 13. Billing export → BigQuery (real invoiced cost / revenue / margin)

> **Status:** Planned (not started). This is the concrete implementation plan for what §11 Phase 4
> deferred. It is **additive and self-contained** — nothing else depends on it and it does not change
> the existing estimated-pricing story (§11 Phases 1–3). Its purpose is to overlay **actual invoiced**
> figures next to the current **estimated (list-price)** figures, with a hard UI boundary so the two are
> never conflated. See [11-pricing-and-billing.md](11-pricing-and-billing.md) Phase 4 for the original
> deferral note.

### Why this is separate from the Cloud Channel API

The Cloud Channel `v1` API exposes **no billing actuals**: `accounts.reports.*` / `reportJobs.*` are
deprecated, and `queryEligibleBillingAccounts` returns *eligibility*, not money. Everything the app
shows today under "Estimated estate value" is derived from **offer list pricing × seats × repricing**
(§11) — a genuine estimate, explicitly labelled *not invoiced*. Real invoiced totals live **only** in
the **Cloud Billing export to BigQuery** that the distributor enables in GCP. Reading it is a second
integration unlike anything the app does today (a BigQuery client + query cost control + a third
credential surface), which is why it is its own section.

### What it adds (user value)

- **Actual invoiced cost** the reseller pays Google, per customer / per entitlement / per invoice month.
- **Actual vs. estimated variance** — reconcile the app's list-price estimate against the real invoice
  so finance can trust (or correct) the estimate.
- Feeds the same surfaces that already show the estimate: the Home **estate value** panel, the
  **customer detail** value panel, and the **channel-partner-link** reseller value panel — each gaining
  an *Actual (invoiced)* column beside *Estimated (list)*.

### Calculated margin (earned vs. commercial)

The export gives **actual cost — what *we* (the direct partner) pay Google** after Google's channel
discount. It contains **no sell price** (neither ours to our customers nor an indirect reseller's to
their end customers) — those live in external margin/billing systems, which is exactly why the §6
repricing configs read **0%** (margin is applied elsewhere). An indirect reseller's *own* margin to
their end customer lives only in the reseller's system and is not derivable by anyone, so it stays out
of scope. Two margins are relevant here — one calculable now, one only if we can supply sell prices:

1. **Earned / back-end margin — fully calculable, no config needed.**
   `list price (§11 catalog) − actual cost (BigQuery)`. This is the margin Google's partner discount
   gives us versus catalog list, derivable everywhere we pay Google and **independent of any repricing
   config** — so it shows a meaningful, non-zero margin even when the §6 config is 0% because we apply
   our margin in a different system. **This is the primary new capability** unlocked by §13 and should be
   surfaced as an *Earned margin* figure on the dashboard **and** the channel-partner-link detail page
   (for indirect, only where we own the billing — see caveat).
2. **Commercial margin (direct) — uncertain at this stage; only if we can supply our sell price.**
   `our sell price − actual cost`. Google doesn't know our sell price, so this needs the sell price
   brought into the app: an optional **per-customer markup % / price override** entered in the UI, or an
   **import from our margin system** (CSV/API). We're not yet sure we can obtain those numbers, so treat
   this as a **later, optional** add-on (Phase 4e) on top of the earned margin — not part of the initial
   scope.

> **Billing-ownership caveat.** Whether the export even includes an indirect reseller's consumption
> depends on who owns the billing account (distributor-owned billing vs. per-reseller billing). Confirm
> from the export's customer / sub-account identifiers before relying on indirect earned margin.

> **Onboarding (ship with the feature).** Per the §9 convention, when this is implemented add onboarding
> surfaces for the new concepts: an ambient `MudTooltip` distinguishing **Earned margin** (list − actual
> cost, from Google's discount) from the **configured (§6) margin**, and a `FeatureBeacon` on the new
> *Actual* / *Earned margin* columns (plus a short walkthrough/tooltip for sell-price entry if the
> optional commercial margin lands later). Record it in the coverage matrix in
> [09-user-onboarding.md](09-user-onboarding.md).

### Design — mirror the §10 read-model, don't query BigQuery per request

BigQuery bills by **bytes scanned**, so it must **never** be queried on the request path. Instead reuse
the established pattern: a **background job in `GChannel.Worker`** runs a partition-filtered query on a
schedule (daily), aggregates the rows into **new SQL read-model tables**, and every UI/endpoint reads
those cheap indexed tables — exactly like `ReadModelSyncService` does for the Channel estate. One
scheduled query per day (or per invoice-close) keeps scan cost bounded and predictable.

### Prerequisites (GCP — one-time, per region)

1. **Enable Cloud Billing export to BigQuery** for the reseller/distributor billing account
   (*Billing → Billing export → BigQuery export*): enable **Detailed usage cost** (and optionally
   **Pricing**) export. Note the **project**, **dataset**, and **table** — the tables are
   schema-versioned + date-partitioned (e.g. `gcp_billing_export_resource_v1_<BILLING_ACCOUNT_ID>`, plus
   the Channel/subscription line items for Workspace resale). The exact table names are the
   distributor's to identify and put in config.
2. **Service account access to the export**: grant a service account **BigQuery Data Viewer** on the
   export dataset and **BigQuery Job User** on a project to run queries. BigQuery needs **no
   domain-wide delegation**, so this can authenticate **key-less with Workload Identity Federation**
   (preferred, reusing the same WIF pattern as the Pub/Sub path), or with a service-account key.
3. Data lags ~a day (export latency) and finalises around invoice close — surface it as such.

### Cost &amp; cost-control (BigQuery charges)

Enabling the export is **free**, but the data then lives in BigQuery, so there are two BigQuery charges
(separate from the Cloud Channel API, which is unaffected):

| Cost | What it is | Rough scale (region-dependent) |
| --- | --- | --- |
| Export process | Google writing billing data into your dataset | **Free** |
| Storage | Export tables sitting in BigQuery | ~$0.02/GB-month active, ~$0.01 long-term; **first 10 GB/month free** |
| Query (on-demand) | Bytes **scanned** per query (not rows returned) | ~$5–6.25 per **TiB** scanned; **first 1 TiB/month free** |
| Streaming inserts | N/A — the export is batch | none |

The design below keeps this at or near **$0** for a small–mid reseller (within the free tiers) and
**modest + predictable** for large estates. The variable cost is query **bytes scanned**, controlled by:

- **Never query on the request path.** One scheduled background job (daily) queries BigQuery and rolls
  results into SQL; all UI/endpoints read the cheap SQL, so users browsing the dashboard scan **zero**
  BigQuery bytes.
- **Partition filters + cursor.** Each run filters on the export's date partition
  (`_PARTITIONTIME`/`usage_start_time`) and only scans **new** partitions since `BillingSyncCursors`, so
  a run scans a day's slice, not the whole history.
- **Column pruning.** `SELECT` only the needed columns (BigQuery is columnar → fewer columns = fewer
  bytes).
- **Optional GCP-side rollup.** A BigQuery **scheduled query / materialized view** can pre-aggregate so
  the app reads a tiny curated table.

Hard caps (belt-and-braces):

- Set **maximum bytes billed** on each query (BigQuery aborts if it would exceed it) — wire it to the
  `appsettings.json` scanned-bytes ceiling below.
- Set **custom cost quotas** per project/user on the billing account.
- (Overkill here, but possible) switch to **capacity/slot reservations** for flat-rate instead of
  per-TiB.

> BigQuery pricing and free tiers vary by region and change over time — confirm current numbers on the
> [BigQuery pricing page](https://cloud.google.com/bigquery/pricing). With the levers above this stays a
> cents-to-a-few-dollars/month reconciliation cost, not a meaningful operating expense.

### Configuration (new `azd` parameters, disabled by default)

Mirror the existing optional-feature wiring (AppHost `AddParameter` → env var → `GoogleChannel:*`), all
empty/off by default so nothing prompts or runs until configured. See
[../configuration.md](../configuration.md) and [../regional-deployment.md](../regional-deployment.md)
for the pattern.

| Setting | Purpose |
| --- | --- |
| `GoogleChannel:BillingExportProjectId` | BigQuery project that runs the queries (Job User). |
| `GoogleChannel:BillingExportDataset` | Dataset holding the billing export tables. |
| `GoogleChannel:BillingExportTable` | Detailed usage cost export table (or a view the distributor curates). |
| `GoogleChannel:BillingExportEnabled` (derived) | True only when project + dataset + table + a credential are all set. |
| Credential | Reuse `WorkloadIdentityCredentialJson` (key-less) or `ServiceAccountKeyJson`; add a dedicated one only if the billing SA must differ. |
| `GoogleChannel:BillingRollupCron`/interval | How often the worker refreshes (default daily). |

Tuning knobs (bytes-scanned control) stay `appsettings.json`-only: max partition window per run, and a
per-run scanned-bytes ceiling.

### Data model (new read-model tables)

Additive tables created idempotently in `Program.cs EnsureReadModelTablesAsync` (the app uses
`EnsureCreated`, no migrations), following the §10 convention:

- `BillingActualRecords` — `Id` (PK), `CustomerId`, `EntitlementId?`, `OwningLinkId?`, `InvoiceMonth`
  (yyyy-MM), `Currency`, `CostActual` (decimal, what the reseller pays Google), `Sku?`/`ProductId?`,
  `Source` (line-item type), `LastSyncedUtc`. Join keys back to `CustomerRecords` /
  `EntitlementRecords` via the export's customer / sub-account / SKU identifiers (mapping is the crux —
  see risks).
- `BillingSyncCursors` — last successful query window + partition high-water mark, so each run only
  scans new partitions.
- `SellPriceOverrides` *(optional, Phase 4e — for commercial margin)* — `Scope` (customer id or link
  id), `Markup` (decimal %) **or** `UnitSellPrice` + `Currency`, `Source` (`manual`/`import`),
  `UpdatedUtc`. Lets a user record the sell price/markup they apply in an external system so a
  **commercial margin** (`sell − actual cost`) can be computed; absent → only the **earned margin**
  (list − actual cost) is shown.

### Layering plan (matches the established convention)

- [ ] **Phase 4a — GCP setup + config + connectivity.** Enable the export; add the options above to
  `GoogleChannelOptions` + AppHost params + `config.json`; add a `BillingExportEnabled` guard; a
  no-op-when-disabled smoke test that runs one tiny partition-filtered `SELECT` and logs row/byte counts.
  NuGet: `Google.Cloud.BigQuery.V2`.
- [ ] **Phase 4b — Query + SQL rollup + background job.** New `BillingExportSyncService`
  (`BackgroundService` in `GChannel.Worker`, Redis single-flight lock like the others), parameterised
  SQL with **partition filters** (`_PARTITIONTIME`/`usage_start_time` between the cursor and now),
  aggregate to per-customer/entitlement/month/currency, upsert `BillingActualRecords`, advance
  `BillingSyncCursors`. Best-effort; failure never blocks the estate sync.
- [ ] **Phase 4c — Contracts + endpoints + Web client.** `Shared/Contracts/Billing.cs`
  (`BillingActuals`, `CustomerBillingActuals`, `EstateBillingActuals` per currency/month);
  `ApiRoutes` entries; read-only endpoints in a new `BillingEndpoints.cs` (served from the SQL rollup,
  cheap, cached) registered in `Program.cs`; typed methods on `GChannelApiClient`.
- [ ] **Phase 4d — UI overlay with a hard boundary.** Add an **Actual (invoiced)** column/section beside
  the existing **Estimated (list)** on the Home estate-value panel, `CustomerDetail` value panel and
  `ChannelPartnerLinkDetail` reseller-value panel; show the **variance** (actual − estimate) and an
  *as-of / invoice-month* badge. Also surface **Earned margin** = `list price (§11) − actual cost`
  (Google's channel discount; non-zero even when the §6 repricing config is 0% because margin is applied
  externally) on the dashboard and the channel-partner-link detail page — labelled distinctly from the
  configured §6 margin. Never merge estimate and actual into one figure; keep the disclaimer that
  estimates are list-price and actuals are invoiced-with-lag. **Ship onboarding** with this phase
  (tooltip/beacon distinguishing earned vs. configured margin — see the onboarding note above).
- [ ] **Phase 4e — Commercial margin (optional).** Add `SellPriceOverrides` (per-customer / per-link
  markup % or sell price, entered in the UI or imported), then compute **Commercial margin** =
  `sell − actual cost` alongside the earned margin. Addresses the "we add reseller margin in a different
  system" case; the indirect reseller's *own* downstream margin stays out of scope (not in any data we
  can see). Ship the sell-price entry with an onboarding walkthrough/tooltip.

### Risks / caveats

- **Row→entitlement mapping** is the hard part: the export identifies consumption by
  project/sub-account/SKU, which must be joined back to our `CustomerRecords`/`EntitlementRecords`.
  Expect some rows to be attributable only to a customer (not a specific entitlement) — model both
  grains.
- **Schema versioning**: Google versions the export schema; pin the version and fail soft on unknown
  columns.
- **Scan cost**: always partition-filter; keep a scanned-bytes ceiling; prefer a daily scheduled rollup
  over ad-hoc queries. Consider a BigQuery **scheduled query / materialised view** on the GCP side so
  the app only reads a small curated table.
- **Currency & credits**: preserve currency per row; account for credits/adjustments so "actual" is the
  net invoiced figure.
- **Audience**: this is a finance/reconciliation feature with day-plus latency — keep it clearly
  secondary to the always-current estimate.

### When to build

Revisit when there is a concrete need to reconcile GChannel's estimates against real Google invoices
(finance sign-off, disputed margins, or reporting requirements). Until then §11 Phases 1–3 cover the
estimated cost → price → margin story end to end, and this remains a clean, additive follow-on.
