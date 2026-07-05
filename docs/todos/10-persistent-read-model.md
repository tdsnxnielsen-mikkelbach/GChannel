> Part of the [GChannel TODO index](../todo.md).

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
- [x] Dashboard `/summary` + `/overview` aggregate **directly from SQL on the request path** (short
  cache under `dashboard:summary:live` / `dashboard:overview:live`, TTL `ReadModelDashboardCacheSeconds`)
  rather than serving the background worker's long-lived warmed snapshot — so the dashboard reflects the
  **full estate already persisted in SQL immediately after a redeploy** instead of "starting over" from
  the latest worker refresh. The read-model is durable (SQL, `EnsureCreated` never drops) and the sync
  worker only adds **deltas**, so a redeploy resumes the staleness rotation without re-collecting.
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
- [x] **Write-through &amp; event-driven projection (§14).** Freshness no longer depends solely on the poll:
  the per-customer projection now lives in a shared `ReadModelProjector` reused by three callers. Mutation
  endpoints **write through** the changed `CustomerRecord` immediately after a successful create/import/
  update/delete (no extra API call), and the Pub/Sub `ChannelNotificationsService` triggers a **targeted
  projection** of the affected customer on each change event (re-reads live state → idempotent under
  duplicate/out-of-order events), with the background sync demoted to a reconciliation backstop. See
  [14 — CQRS &amp; event-driven projections](14-cqrs-and-event-driven-projections.md).

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

