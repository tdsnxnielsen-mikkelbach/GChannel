> Part of the [GChannel TODO index](../todo.md).

## Known placeholders

- ~~The dashboard figures on the home page are placeholders.~~ **Implemented.** The home page now
  consumes a derived `GET /api/dashboard/summary` endpoint via `GChannelApiClient` instead of the
  hardcoded arrays. **Note:** the `accounts.reports.*` and `accounts.reportJobs.fetchReportResults`
  endpoints are **deprecated** in `v1`, so the figures are aggregated from entitlement/customer data
  rather than the legacy reporting API.

  `GetDashboardSummaryAsync` in `GoogleChannelClient` aggregates the read paths (cached in Redis for
  `CacheSeconds`):

  - **§2 Customer management** (`accounts.customers.list`) — drives the **Customers** card and the
    *customers onboarded* line chart (buckets customers by create month across the full available
    history, split into **Direct** and **Via resellers (indirect)** lines, with a selectable From/To
    month range).
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

