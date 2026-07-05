# TODO / future developments

See [api-surface.md](api-surface.md) for the full catalog of `v1` Cloud Channel API
resources/methods these items map to.

This page is an **index**. Each section now lives in its own file under [`todos/`](todos/) so this
overview stays short and scannable while still carrying status; open a linked file for the full
detail, rationale and per-phase breakdown.

## Sections

| Section | Status | Detail |
| --- | --- | --- |
| Hardening | Complete | [todos/hardening.md](todos/hardening.md) |
| Roadmap — Channel API capabilities (§1–§8) | §1–§7 implemented; §8 deferred (alpha-only) | [todos/roadmap.md](todos/roadmap.md) |
| Known placeholders | Implemented (dashboard summary) | [todos/known-placeholders.md](todos/known-placeholders.md) |
| Notes | Reference | [todos/notes.md](todos/notes.md) |
| 9. User onboarding | Complete (Phases 1–4) + per-feature coverage matrix &amp; new-feature convention | [todos/09-user-onboarding.md](todos/09-user-onboarding.md) |
| 10. Persistent read-model | Complete (Phases 1–5; snapshots optional) | [todos/10-persistent-read-model.md](todos/10-persistent-read-model.md) |
| 11. Pricing &amp; billing | Complete in-scope; Phase 4 (BigQuery) deferred (out of Channel API scope) | [todos/11-pricing-and-billing.md](todos/11-pricing-and-billing.md) |
| 12. Remaining stable `v1` surface | §12.1–12.4 complete (§12.4 doc-only) | [todos/12-remaining-v1-surface.md](todos/12-remaining-v1-surface.md) |
| 13. Provisionable Cloud Identity types | Deferred (alpha-only; GA `checkCloudIdentityAccountsExist` covers the core decision) | [todos/13-provisionable-cloud-identity-types.md](todos/13-provisionable-cloud-identity-types.md) |
| 14. CQRS &amp; event-driven projections | Analysis only (no code) — write-through on commands &amp; event-driven projections worth revisiting | [todos/14-cqrs-and-event-driven-projections.md](todos/14-cqrs-and-event-driven-projections.md) |

## Roadmap capabilities (§1–§8)

Discrete Channel API capability areas — full detail in [todos/roadmap.md](todos/roadmap.md):

| # | Capability | Status |
| --- | --- | --- |
| 1 | Catalog browsing | Implemented |
| 2 | Customer management | Implemented (Cloud Identity provision/import tracked in §12.2) |
| 3 | Entitlement lifecycle | Implemented |
| 4 | Transfers | Implemented |
| 5 | Distributor / n-tier links | Implemented |
| 6 | Repricing / rebilling margin | Implemented |
| 7 | Eventing &amp; operations | Implemented |
| 8 | `v1alpha1` preview capabilities | Deferred (alpha-only) |

## Pricing &amp; billing phases (§11)

Full detail in [todos/11-pricing-and-billing.md](todos/11-pricing-and-billing.md):

| Phase | Status |
| --- | --- |
| 1 — Map offer pricing | Implemented |
| 2 — Per-entitlement cost | Implemented |
| 3 — Estate rollups | Implemented |
| 4 — Billing export (BigQuery) | Deferred (out of Channel API scope) |
| 5 — Customer list parity + search | Implemented |
| 6 — Expandable subscription cards | Implemented |
| 7 — Subscription detail | Implemented |
| 8 — Estate entitlements list + deep-links | Implemented |
| 9 — Whole-estate KPIs + source split | Implemented |
| 10 — Customer Source &amp; Auto-renew columns | Implemented |
| 11 — By-source per currency + product-mix names | Implemented |
| 12 — Reseller value rollup + estate-wide as-of | Implemented |

## Remaining stable `v1` surface (§12)

Full detail in [todos/12-remaining-v1-surface.md](todos/12-remaining-v1-surface.md):

| Item | Status |
| --- | --- |
| 12.1 — N-tier customer CRUD | Implemented |
| 12.2 — Customer provisioning &amp; pre-transfer onboarding | Implemented |
| 12.3 — Eligible billing accounts | Implemented (niche) |
| 12.4 — Minor completeness &amp; doc hygiene | Resolved (doc-only, no code) |
