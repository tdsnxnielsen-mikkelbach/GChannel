# Using GChannel — a guide for new users

This is a step-by-step walkthrough of what you can do in the GChannel console. It assumes the app is
already running (see [deployment.md](deployment.md)) and that an administrator has configured the
reseller account (`GoogleChannel:AccountId`). You sign in with your Google account.

> **Mental model.** GChannel is a thin, friendly console over the Google Cloud Channel (reseller) API.
> You manage **customers**, the **catalog** you can sell them (products → SKUs → offers), the
> **entitlements** (subscriptions) they own, **transfers**, **channel partners**, **repricing**, and
> **eventing** (operations + notifications). Most things deep-link to each other, so you can follow a
> trail rather than memorising ids.

## 1. Sign in and get your bearings

1. Open the web app URL and sign in with Google when prompted.
2. You land on the **Dashboard** (`/`). The left **navigation** is grouped:
   - **Dashboard** — at-a-glance overview.
   - **Accounts** — Cloud Identity check.
   - **Catalog** — Products, Offers, SKU groups.
   - **Customers** — All customers, New customer, Entitlements.
   - **Channel partners** — Partner links, Invite partner.
   - **Eventing** — Operations, Notifications.

### What the Dashboard shows
- **Summary cards**: Customers, Active SKUs, Suspended, Channel links. The **Suspended** card's
  **Details** expander breaks the entitlement estate down into **Active / Trial / Suspended** counts,
  and each number deep-links to the **Entitlements** page pre-filtered to that state
  (`/entitlements?state=…&scope=all`). These counts (and the *Active SKUs* card / product mix) span the
  **whole estate** — direct customers *plus* reseller-owned (indirect) ones — so they line up with the
  estate-value panel, which is also whole-estate.
- **Estimated estate value (monthly)** — wholesale cost, repriced revenue and margin across your active
  entitlements, in your estate's main currency. A **By source** table splits the value into **Direct**
  (your own customers) vs **Via resellers** (indirect) so you can see where the value comes from, and a
  chip per currency is shown top-right (the dominant one highlighted). These are *estimates from list
  pricing, not invoiced amounts*, and appear once the background read-model has priced your entitlements.
- **Customers onboarded** — an area chart bucketing new customers into the trailing six months.
- **Product mix** — a donut of active entitlements grouped by product.
- **Top indirect resellers** — your linked resellers ranked by downstream seats.

A status line under the title shows when the figures were last refreshed (e.g. "Updated 22 min ago ·
took 433s · next refresh in 8 min"), a "Refreshing…" chip while a background run is in progress, or
"On demand" when the background refresher isn't configured.

If a banner says "N customers couldn't be loaded", the live aggregation hit its time budget; refresh
or wait for the background refresh to warm the cache. Nothing is broken — it's a partial result.

## 2. Check a domain (Cloud Identity)

Before creating a customer, check whether their domain already has a Google Cloud Identity (which can
mean a transfer is required instead of a fresh create).

1. Go to **Accounts → Cloud Identity check** (`/accounts/cloud-identity`).
2. Enter the customer's primary **domain** and run the check.
3. The result tells you whether the domain is known to Google. From a customer row elsewhere in the
   app you can jump straight here via the domain link.

## 3. Browse the catalog (what you can sell)

The catalog is read-only and correlated by id, so you can hop between the three views.

- **Catalog → Products** (`/catalog/products`) — the products you're authorised to resell; drill into a
  product to see its **SKUs**.
- **Catalog → Offers** (`/catalog/offers`) — the purchasable offers (an offer pairs a SKU with pricing
  and terms). This is what you actually buy when creating an entitlement.
- **Catalog → SKU groups** (`/catalog/sku-groups`) — billable SKU groupings, used for repricing scope.

You don't have to start here — the purchase flow shows a customer's purchasable SKUs/offers directly —
but it's useful for understanding what's available.

## 4. Create your first customer

1. Go to **Customers → New customer** (`/customers/new`).
2. Fill in the organisation **display name**, **primary domain**, and the required address/contact
   fields.
3. Save. You're taken to the **customer detail** page (`/customers/{id}`), the hub for everything about
   that customer.

> Tip: if the Cloud Identity check (step 2) said the domain already exists, you likely need a
> **transfer** (step 7) rather than a create.

## 5. View and manage a customer

From **Customers → All customers** (`/customers`) open any row to reach **customer detail**
(`/customers/{id}`). The list mirrors the Google console with **Subscriptions** (entitlement counts by
state, e.g. `6 Active · 2 Suspended`) and **Renewal** (the earliest upcoming commitment end date plus
that offer's name, or “—” when none commit) columns, plus an **Est. monthly** column — the estimated
monthly value of each customer's active subscriptions (from list pricing with your repricing applied,
*not* invoiced amounts; “—” until their entitlements have been priced by the background read-model). A
server-side **search box** filters by organization, domain or customer id. Each row can be **expanded**
(chevron) to reveal one **subscription card** per entitlement — offer name, state badge, plan summary
(e.g. *Annual Plan (Monthly Payment)*), renewal date, and `— / N licenses` (assigned-seat counts aren't
available from the Channel API, so only the total is shown) with a **Details** link to the entitlement.
From the detail page you can:

- **Edit** the customer (`/customers/edit/{id}`).
- See and open the customer's **entitlements** (`/customers/{id}/entitlements`).
- Start a **purchase**, a **transfer**, or **repricing**.
- Jump to the owning **channel partner** (if the customer has one).

The customer detail page also shows an **Estimated monthly value** panel for that customer's active
priced subscriptions, broken into **Wholesale cost** (what you pay Google), **Repriced revenue** (what
the customer is billed) and **Margin** — all on the same estimated, not-invoiced basis and shown in the
customer's dominant currency.

### Estate-wide entitlements list

**Customers → Entitlements** (`/entitlements`) is an estate-wide list of individual subscriptions from
the read-model, joined to each owning customer. Filter by **State** (All / Active / Trial / Suspended —
the same lifecycle buckets as the dashboard KPIs) and **Scope** (Direct / Indirect resellers / All;
defaults to **Direct** so counts match the dashboard). Server-side search covers customer, offer, SKU,
product and id. Each row shows the offer/product, a state badge, seats, an **Est. monthly** value (with
a wholesale × markup tooltip) and the renewal date, and links to the customer and the entitlement
detail. The dashboard's Active/Trial/Suspended numbers open this page pre-filtered via
`/entitlements?state=…`.

## 6. Buy an entitlement (subscription)

1. From the customer detail (or **Entitlements** list), choose **New / Purchase**
   (`/customers/{id}/entitlements/new`).
2. Pick from the customer's **purchasable SKUs/offers** (these deep-link back to the catalog).
3. Set quantity/seats and any required terms, then submit.
4. Because Google processes this as a **long-running operation**, the page reflects the result inline —
   either *completed* (Google finished synchronously) or *submitted — processing*. If it's still
   processing, you'll get an **operation name** you can track on the **Operations** page (step 9).
5. Open the **entitlement detail** (`/customers/{id}/entitlements/{id}`) to see status, seats, and
   lifecycle actions (suspend/activate/change).

The **entitlement (subscription) detail** page groups the subscription into **Licenses** (total
`num_units`; *Assigned* shows “— (not available)” — assigned-seat counts aren't exposed by the Channel
API; a **Manage licenses** link jumps to the seats/offer controls), **Payment & pricing (estimated)**
(billing cycle, an **estimated /month** figure that normalises the per-cycle list price, repricing %, and
the **Renewal** term — “Renews/Ends {date} · auto-renew on/off”), and a **Billing account** showing its
**ID** with the full resource path. All monetary figures are *estimates from list pricing, not invoices*.

The entitlement list (`/customers/{id}/entitlements`) includes an **Est. monthly** column — unit list
price × seats × (1 + your repricing %), with a breakdown tooltip; it shows “—” for trials or offers
that couldn't be priced. As with the dashboard, these are *estimates from list pricing, not invoices*.

## 7. Transfer entitlements

When a customer already has Google subscriptions (e.g. with another reseller), transfer them in:

1. From customer detail choose **Transfer** (`/customers/{id}/transfer`).
2. The page lists **transferable SKUs/offers** for that customer (resolved to friendly catalog names).
3. Select what to transfer and submit. Like purchases, transfers are long-running — track them on the
   **Operations** page.

## 8. Channel partners and repricing

- **Channel partners → Partner links** (`/channel-partner-links`) — see your channel partner links;
  open one to see (and link back to) the **customers** it owns. From the link detail page you can
  **Add** a new customer, **Import** an existing Cloud Identity customer, or **Edit** / **Delete** the
  partner's customers directly (n-tier customer management).
- **Channel partners → Invite partner** (`/channel-partner-links/new`) — invite a new partner / change
  link state.
- **Repricing** — adjust your rebilling margin:
  - Per customer: `/customers/{id}/repricing` (open from the customer detail header).
  - Per partner: `/channel-partner-links/{id}/repricing` (whole-partner).
  - On each form, set the effective **year/month**, the **percentage** adjustment, and the
    **rebilling basis**.

## 9. Eventing — track operations and watch changes

- **Eventing → Operations** (`/operations`) — track a long-running operation by the name a mutation
  returned. The page polls it to **done** and deep-links to the affected customer/entitlement. (There's
  no global "list all" — Google doesn't support it — so you track specific operations.)
- **Eventing → Notifications** (`/notifications`) — a live feed of Channel change events (entitlement
  and customer changes) delivered via Google Cloud Pub/Sub. Each row resolves to the customer name and
  deep-links to the affected resource. The right-hand card manages the **subscriber registration**
  (which service accounts may receive events). The feed needs an administrator to configure Pub/Sub
  (see [configuration.md](configuration.md#pubsub-notifications-7)); until then the feed simply stays
  empty and the rest of the app is unaffected.

## A typical first session, end to end

1. Sign in → land on the **Dashboard**.
2. **Cloud Identity check** the customer's domain.
3. **Create the customer** (or **Transfer** if the domain already exists).
4. Open the customer, **Purchase** an entitlement from their available offers.
5. If it's still processing, copy the **operation name** and watch it finish on **Operations**.
6. Later, watch ongoing changes arrive on **Notifications**.

## Tips & troubleshooting

- **Everything deep-links.** Follow links between customers, entitlements, catalog, partners, and
  events instead of copying ids by hand.
- **Long-running actions** don't block — submit, then track on **Operations**.
- **Empty Notifications feed?** That's expected until Pub/Sub is configured; it doesn't affect any
  other feature.
- **Partial Dashboard?** The "N customers couldn't be loaded" note means the live aggregation ran out
  of time budget; it fills in as the cache warms.
