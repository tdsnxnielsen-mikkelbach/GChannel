> Part of the [GChannel TODO index](../todo.md).

## 15. Cloud Billing / BigQuery billing export integration (spike / design)

> **Status:** 🧪 Spike / design draft — **not implemented**. Captures how GChannel would ingest
> **actual** GCP consumption/invoice figures, GCP **budgets**, and **spend trend/anomaly** detection.
> These are the deferred "Phase 4" concerns from
> [§11 Pricing &amp; billing](11-pricing-and-billing.md) and the answer to the "can we see GCP spend /
> set budgets / spot spikes?" questions. **None of this is in the Cloud Channel API** — it needs
> separate Google Cloud data sources and credentials. This doc scopes the smallest useful spike and a
> phased plan; nothing here changes the existing (estimate-only) pricing surface.

### Why the Channel API can't do this

The Cloud Channel `v1` API is a *reselling / provisioning* API, not a consumption or billing-actuals
API. Concretely:

- **No spend/consumption endpoint.** The legacy `accounts.reports.*` / `accounts.reportJobs.*`
  reporting API was **removed in `v1`**. `queryEligibleBillingAccounts` returns *which* billing account
  is eligible for a SKU — eligibility, **not money**.
- **No budget concept.** Budgets live in the **Cloud Billing Budget API**
  (`billingbudgets.googleapis.com`), scoped to a Cloud Billing account, not to a Channel customer.
- Today GChannel's revenue/margin figures are **estimates** derived from offer *list* pricing ×
  seats × repricing mark-up (see §11). They are clearly labelled as estimates and are **not** billed
  amounts.

So the three asks map to three separate Google Cloud data sources, all outside the Channel API:

| Ask | Real source | GChannel today |
| --- | --- | --- |
| See GCP spend per customer | **Detailed usage cost BigQuery export** (partner/sub-account billing) | Estimated from list pricing only |
| See / set GCP budgets | **Cloud Billing Budget API** (`billingbudgets.googleapis.com`) | Not available |
| Spend trends / spike detection | Time-series over the **BigQuery** export (+ optional anomaly logic) | Not available |

### Data sources

1. **Cloud Billing → BigQuery Billing Export (detailed usage cost).**
   - Configured on the reseller's **Cloud Billing account** (partner billing account for resold GCP).
     Google streams line items into a BigQuery dataset, table
     `gcp_billing_export_resource_v1_<BILLING_ACCOUNT_ID>` (detailed) or
     `gcp_billing_export_v1_<BILLING_ACCOUNT_ID>` (standard).
   - Key columns: `cost`, `currency`, `usage_start_time`/`usage_end_time`, `invoice.month`,
     `project.id`, `service.description`, `sku.description`, `credits[]`, and crucially the labels /
     `project.ancestry` that identify **which resold customer / billing sub-account** the cost belongs
     to. For Channel-resold GCP the customer's billing **sub-account id** is the join key back to the
     Channel `Customer` (via its `billingAccount` on the entitlement).
   - Export latency is hours (not real-time); treat it as a daily/near-daily batch source.
2. **Cloud Billing Budget API** (`billingbudgets.googleapis.com`).
   - `billingAccounts.budgets.list` / `get` / `create` / `patch` / `delete` — a `Budget` has an
     `amount` (specified or last-period), `budgetFilter` (projects, services, credit types,
     `calendarPeriod`/`customPeriod`), and `thresholdRules[]` (e.g. alert at 50/90/100%).
   - Budgets are defined **per billing (sub-)account**, so a resold customer with its own billing
     sub-account can have its own budget.

### Credentials & auth (the hard part)

Unlike the Channel API (called with the signed-in user's OAuth token), these sources need
**Google Cloud IAM** access to the reseller's billing account + BigQuery project — a
**service-account** flow, not per-user OAuth:

- A GCP **service account** with, at minimum:
  - `roles/bigquery.dataViewer` + `roles/bigquery.jobUser` on the billing-export dataset/project (read
    + run queries), and
  - `roles/billing.viewer` (read budgets) or `roles/billing.budgets.editor` (create/set budgets) on the
    billing account.
- On Azure this mirrors the existing Pub/Sub pattern: the app's **managed identity** reads a Google
  **service-account key from Key Vault**; that key authenticates to BigQuery + Billing Budgets. Locally
  it comes from user-secrets. **No new secret in code.** (See
  [configuration.md](../configuration.md) Pub/Sub section for the analogous wiring.)
- New config keys (mirroring `GoogleChannel:*`):
  `GoogleBilling:BigQueryProjectId`, `GoogleBilling:BillingExportDataset`,
  `GoogleBilling:BillingExportTable`, `GoogleBilling:BillingAccountId`, and a
  feature flag `GoogleBilling:Enabled` (everything is a no-op when unset, exactly like the subscriber).

### Proposed architecture

Follows the established §10/§11 read-model shape — **don't** query BigQuery on the request path:

```
Billing export (BigQuery)  ──daily──▶  Worker: BillingSyncService  ──▶  SQL read-model
Budget API (on-demand/cached)                     │                       (CustomerSpend, SpendDaily)
                                                  ▼
                          Redis cache  ◀── ApiService billing endpoints ──▶  Web (Blazor pages)
```

- **`BillingSyncService`** (new `BackgroundService` in **GChannel.Worker**, no extra container): once
  per day (configurable) runs a **parameterised, aggregated** BigQuery query — group by
  `invoice.month` + billing sub-account + `service.description` — and upserts into new read-model
  tables. Never `SELECT *`; always aggregate server-side in BigQuery to keep bytes-scanned (cost) low
  and use **partition pruning** on `_PARTITIONTIME` / `usage_start_time`.
- **Read-model tables** (SQL, via the existing `EnsureCreated` model):
  - `CustomerSpend` — `CustomerId`, `BillingSubAccountId`, `InvoiceMonth`, `Currency`, `Cost`,
    `Credits`, `LastSyncedUtc`.
  - `SpendDaily` — `CustomerId`, `Day`, `Cost` (for the trend chart + spike detection).
  - Join key: entitlement `billingAccount` → customer, so spend rows correlate to the existing
    `CustomerRecords`.
- **API endpoints** (`GChannel.ApiService`, cached in Redis like the estate views):
  - `GET /api/billing/customers/{customerId}/spend?months=N` — monthly spend series.
  - `GET /api/billing/customers/{customerId}/trend?days=N` — daily series + flagged spikes.
  - `GET /api/billing/customers/{customerId}/budgets` / `POST` / `PUT` — proxy the Budget API.
- **Spike detection (Phase 3):** start simple — flag a day whose cost exceeds
  `mean + k·stddev` of a trailing window (e.g. 30 days, k≈3), or an N% jump vs the same weekday prior
  week. Keep it a pure function over `SpendDaily` so it's unit-testable; no ML needed for v1.

### UI surfaces

- **Customer detail** gains a **Spend** tab: monthly actuals (BigQuery) shown *alongside* the existing
  estimated list-price figure, clearly distinguishing **billed** vs **estimated**.
- A **Spend trends** page (under a new **Billing** nav group): per-customer daily line chart with spike
  markers, and an estate-wide top-spenders view.
- A **Budgets** page: list budgets per customer billing sub-account, with create/edit (threshold rules
  50/90/100%). Writes are gated behind `roles/billing.budgets.editor`; read-only otherwise.

### Cost, security & risk notes

- **Query cost.** BigQuery bills per bytes scanned. Mitigate with partitioned queries, a daily (not
  per-request) sync, `maximum_bytes_billed` on jobs, and materialising the daily/monthly rollups into
  SQL so the UI never hits BigQuery directly.
- **Least privilege.** Prefer a dedicated service account scoped to the billing-export dataset +
  billing account only. Budget **writes** are a separate, opt-in role.
- **Currency.** The export can be multi-currency (like §11's estate value) — keep per-currency rows and
  pick a dominant currency for headlines.
- **Latency expectations.** Export is not real-time (hours); the UI must label figures with an "as of"
  timestamp (reuse the read-model `AsOf` pattern).
- **Secret handling.** Reuse the Key Vault → managed identity path; never embed the SA key.

### Spike (smallest useful proof)

Time-boxed validation before committing to the full build:

1. Manually enable BigQuery billing export on a test billing account; confirm the detailed table
   appears and identify the column/label that carries the **resold customer / sub-account id**.
2. Run one **aggregated** query (monthly cost by sub-account) from a throwaway console/script using a
   scoped service account; confirm the join key matches an entitlement `billingAccount` in GChannel.
3. Call `billingAccounts.budgets.list` for the same billing account; confirm read access and the
   shape of a `Budget`.
4. Decide feasibility of the customer↔sub-account join at scale, and record bytes-scanned per query to
   size the daily sync cost.

**Exit criteria:** we can (a) attribute a real cost figure to a specific GChannel customer, and
(b) read a budget — both with a Key-Vault-stored service account. If either fails, document the gap
and stop before building the sync service.

### Out of scope (for this spike)

- Real-time spend (export latency makes this impractical; near-daily is the design point).
- Rewriting the §11 estimate path — actuals are **additive**, shown next to estimates.
- Cross-cloud / non-GCP spend.
