> Part of the [GChannel TODO index](../todo.md).

## 14. CQRS &amp; event-driven projections (architecture note)

> **Status:** Not planned / analysis only — **no code written**. This is a design note capturing whether
> the CQRS pattern would benefit the app now that a persistent read-model exists (see
> [10 — Persistent read-model](10-persistent-read-model.md)). Short answer: the *pattern label* buys
> little (we already have the hard half), but two ideas **adjacent** to CQRS are genuinely valuable and
> worth revisiting later — **write-through on commands** and **event-driven projections**.

### TL;DR

The app already implements the core of CQRS — a separate, denormalised **read model** (SQL
`CustomerRecords` / `EntitlementRecords` / `ResellerLinks`) physically distinct from the **write side**
(the Google Channel API). Adopting *formal* CQRS (command/query handlers, MediatR, etc.) would mostly
**relabel and reorganise what already exists** and would not touch the real bottleneck (Channel API
quota). The beneficial parts live *near* CQRS, not in the pattern itself.

### What CQRS actually is

**Command Query Responsibility Segregation** makes two independent claims:

1. **Model separation** — the shape you *read* data in need not match the shape you *write* it in.
   Reads hit an optimised projection; writes go through a domain/command model.
2. **(Optional) infrastructure separation** — reads and writes can use different stores, scale
   independently, and update asynchronously (eventual consistency).

CQRS is frequently conflated with **Event Sourcing** (the write model is an append-only event log) and
**MediatR / a command bus** (a code-level dispatch mechanism). Those are separate, optional choices.

### What we already have (de-facto CQRS)

| CQRS concept | Current implementation |
| --- | --- |
| Write model / source of truth | Google Channel API (`create`, `changeOffer`, `provisionCloudIdentity`, repricing, …) |
| Read model / projection | SQL read-model tables, denormalised (`UnitPrice`, `CommitmentEndTime`, `ProductName`, `RenewalEnabled`, `BillableSeats`, …) |
| Projection builder | `ReadModelSyncService` (Worker) |
| Query side | `EstateEndpoints` / `DashboardEndpoints` reading straight from SQL |
| Eventual consistency + freshness marker | Sync rotation + the "As of X ago" badge |

So the question is not *"should we adopt CQRS?"* — the core idea is already adopted. The question is
whether formalising it further, or adopting the event-driven variant, is worth it.

### Where formalising CQRS would help — modestly

The one real code-quality win is **explicit query/command separation** as the surface grows:

- **Queries** (dashboard, estate lists, reseller value) are already read-model-only and side-effect-free
  — they map cleanly onto query handlers.
- **Commands** (create/import customer, change entitlement, repricing) mutate an **external** system and
  the read model only catches up on the next sync cycle. That eventual-consistency gap is the source of
  known UX papercuts (auto-renew showing "—" until re-sync; a link's `CustomerCount` sitting at 0 until
  the fan-out runs).

Formalising commands gives **one obvious place** to close that gap: a command handler that, after a
successful Channel API mutation, does an **optimistic local projection write** (upsert the row it just
created/changed) so the UI reflects it immediately instead of waiting for the worker. This is the
concrete, user-visible benefit — it is really "write-through the read model on command completion".

### Where CQRS would *not* help

- **Event Sourcing is largely off the table.** We do not own the write model — Google does — so we
  cannot make the authoritative store an event log. The classic ES benefits (full audit / temporal
  replay of the source of truth) do not apply. Event-sourcing our *own* local actions is a niche audit
  feature, not a core need.
- **The bottleneck is quota, not model coupling.** The real pain is `ListEntitlements` / `ListCustomers`
  at ~24/min and the poll cadence (hence the pacers, budgets, rotation and stale fallback). CQRS-the-
  pattern buys **zero** extra API calls; reorganising handlers won't change it.
- **Added ceremony.** MediatR + command/query DTOs + handler plumbing is real overhead for a system
  whose read/write split is already clean. At this size it risks being architecture for its own sake.

### The idea that *is* beneficial (adjacent to CQRS): event-driven projections

The app already ingests **Pub/Sub change notifications** (`ChannelNotificationsService`;
entitlement/customer events — see §7). Today those events mostly feed a notifications feed, while the
read model is refreshed by **polling** (stalest-first rotation, throttled by quota).

The CQRS-flavoured upgrade is to treat those Pub/Sub events as **projection triggers**:

```mermaid
flowchart LR
    G[Google Channel API<br/>write side] -->|Pub/Sub event| N[Notification consumer]
    N -->|targeted refresh:<br/>one customer/entitlement| P[Read-model projection<br/>SQL]
    G -.->|slow full poll<br/>reconciliation| P
    P --> Q[Query endpoints<br/>dashboard / estate]
```

Why this is the real win:

- **Freshness without quota burn.** An event names *exactly which* customer/entitlement changed, so we
  re-sync **one row** instead of rotating the whole estate at 24/min. The "As of" lag shrinks sharply
  for the things that actually changed.
- **Poll becomes reconciliation, not primary.** Keep the periodic full sync as a safety net (catch
  missed events), but stop relying on it for timeliness.
- **It fits what we already have.** No new infrastructure — the consumer, the SQL projections and the
  Worker all exist. Note it is the *event-driven projection* that helps, **not** the command/query code
  split.

### Recommendation

1. **Do not** undertake a formal CQRS / MediatR refactor for its own sake — it renames a design that
   already exists and does not touch the real constraint (quota).
2. **Do** borrow two specific CQRS-adjacent ideas when there's appetite:
   - **Write-through on commands** — after a successful Channel API mutation, optimistically upsert the
     affected read-model row so the UI updates instantly (closes the "shows — until next sync" gaps).
   - **Event-driven projections** — promote the Pub/Sub notifications from a feed into **targeted**
     read-model refresh triggers, demoting the poll to a reconciliation backstop. Highest-value,
     best-fit improvement.

Both are **incremental and additive** to the current Worker / read-model design — no big-bang rewrite —
and they attack the freshness/quota pain directly rather than the code-organisation cosmetics.

### If revisited — rough sequencing (not scheduled)

1. **Write-through on commands** (small, isolated): on each successful mutation endpoint, upsert the one
   changed `CustomerRecord` / `EntitlementRecord` before returning, mirroring the fields the worker
   denormalises. Immediate UX win, no infra change.
2. **Targeted event projection** (medium): route `ChannelNotificationsService` events to a per-resource
   refresh (one customer / one entitlement) that reuses the existing sync helpers; keep the full
   rotation as reconciliation. Watch for duplicate/out-of-order events (idempotent upserts, last-write-
   wins by update time) and missed events (the poll backstop covers gaps).
3. **Optional handler organisation** (low priority): only if the command/query surface grows enough that
   an explicit dispatch layer improves clarity — otherwise skip.
