> Part of the [GChannel TODO index](../todo.md).

## 9. User onboarding (implemented)

> **Implemented.** All four phases are live (see the **Implementation phases** + status note below).
> See [UI.md](UI.md) for the manual walkthrough the in-app tour automates. This section is kept as the
> design rationale for the shipped onboarding experience.

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

- [x] **Phase 1 — Onboarding checklist + welcome (no JS, highest value)**
  - [x] `OnboardingState` model + per-user storage (browser `ProtectedLocalStorage` — chosen over a new
    EF table because the app uses `EnsureCreated` with no migrations; Redis/cross-device is a later
    upgrade).
  - [x] First-run **welcome** card (MudBlazor) with *Get started* / *Skip onboarding*.
  - [x] Dashboard **checklist** that ticks steps off from real signals (customer count, entitlements)
    plus manual steps (verify a domain, explore eventing), dismissable.
- [x] **Phase 2 — App-wide product tour (Driver.js via JS interop)**
  - [x] `wwwroot/js/onboarding.js` + `OnboardingTourService` C# wrapper.
  - [x] Ordered tour over the always-present nav drawer + app bar (spotlight coach marks + popover step
    cards), skippable/resumable, completion persisted; **Take the product tour** action in the app bar.
  - [x] Stable `data-onboarding` target hooks on nav groups + app-bar buttons.
- [x] **Phase 3 — Per-workflow guided walkthroughs / interactive tutorials**
  - [x] Scoped popover step sequences on `/accounts/cloud-identity`, `/customers/new`, and the purchase
    flow, via a reusable `GuidedWalkthrough` component (auto-runs once per user + a **Show me how**
    relaunch button), reusing the phase 2 Driver.js interop.
  - [x] "Interactive" variant that gates **Next** on the real step completing (the new-customer
    walkthrough blocks until the organization name + domain are filled, via `WalkthroughStep.RequireValueOf`).
- [x] **Phase 4 — Ambient tooltips + feature beacons**
  - [x] `MudTooltip` field/icon help (rebilling-basis info icon on the customer + partner repricing pages).
  - [x] Pulsing **beacons** (`FeatureBeacon`) on the new **Operations**/**Notifications** nav links that
    auto-dismiss per user once the feature is visited (or on click).

> **Status:** Phases 1–4 are implemented. Phase 1: welcome card + dashboard checklist (with a
> **Restart tour** action and a dismissed-state launcher). Phase 2: guided Driver.js product tour over
> the nav + app bar. Phase 3: per-workflow walkthroughs (Cloud Identity, new-customer — input-gated,
> purchase). Phase 4: ambient rebilling-basis tooltips + auto-dismissing nav feature beacons.

### Onboarding coverage by feature

The onboarding toolkit has four surfaces: the **dashboard checklist** (`OnboardingPanel.razor`), the
app-wide **product tour** (`OnboardingTourService`, nav + app bar), page-scoped **guided walkthroughs**
(`GuidedWalkthrough` + `data-walkthrough` hooks), and **feature beacons** (`FeatureBeacon`, pulsing
"new" dots that dismiss on click or first visit). This matrix tracks what each shipped feature uses so
gaps are visible; **every new feature must add at least one appropriate surface** (see convention
below).

| Feature (section) | Onboarding surface |
| --- | --- |
| First sign-in orientation (§9) | Welcome modal + product tour |
| Dashboard setup progress (§9) | Checklist (verify-domain, create-customer, buy-entitlement, eventing) |
| Cloud Identity check (§2) | Guided walkthrough (`cloud-identity`, auto-start) |
| Create a customer (§2) | Guided walkthrough (`customer-new`, input-gated) |
| Purchase an entitlement (§3) | Guided walkthrough (`purchase-entitlement`) |
| Catalog / offers / SKU groups (§1) | Product-tour nav step |
| Transfers (§4) | Product-tour nav step (Customers) |
| Repricing / rebilling margin (§6) | Ambient `MudTooltip` (rebilling-basis info icon) |
| Operations & Notifications (§7) | Nav **feature beacons** (`operations-v1`, `notifications-v1`) |
| Estimated value / product mix (§11) | Ambient tooltip on the **Product mix** panel (raw-ID fallback explainer) |
| N-tier customer CRUD (§12.1) | Guided walkthrough (`partner-customers`) on the channel-partner-link detail |
| Customer import (§12.2) | Feature beacon (`import-customer-v1`) on the Customers list |
| Provision Cloud Identity (§12.2) | Feature beacon (`provision-cloud-identity-v1`) on the customer detail |
| Eligible billing accounts (§12.3) | Extra step in the `purchase-entitlement` walkthrough (`pe-billing`) |
| `v1` doc-hygiene non-implementations (§12.4) | n/a (no UI) |

### Convention: new features ship with onboarding

Every new user-facing feature must include onboarding as part of its definition of done. Pick the
lightest surface that fits:

- **New top-level page or nav entry** → add a **product-tour** step (a `data-onboarding` hook + a line
  in `OnboardingTourService.Steps`) and, if the page is a multi-step workflow, a page-scoped
  **`GuidedWalkthrough`** with `data-walkthrough` hooks.
- **New action/button/dialog on an existing page** → add a **`FeatureBeacon`** next to it (wrap the
  control in a `position:relative` inline-flex span) or extend that page's existing walkthrough with a
  new step (`data-walkthrough` hook + `WalkthroughStep`; steps whose target is absent are skipped, so
  conditional controls are safe).
- **New field or an unfamiliar concept** → add an ambient **`MudTooltip`** info icon.
- **New first-run-relevant milestone** → add a **checklist** step in `OnboardingPanel.Steps`.

Keep step copy short and drive it from [UI.md](UI.md) so docs and the in-app tour stay in sync;
persist any per-user completion via `OnboardingStateService` (browser `ProtectedLocalStorage`).


