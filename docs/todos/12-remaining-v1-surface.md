> Part of the [GChannel TODO index](../todo.md).

## 12. Remaining stable `v1` surface (customer provisioning &amp; n-tier customer management)

> **Status:** §12.1 (n-tier customer CRUD) and §12.2 (customer provisioning / pre-transfer import) are
> **implemented**; §12.3–§12.4 remain proposed. This
> section closes the last gaps between the app and the **stable `v1`** Cloud Channel API (excluding the
> deprecated `accounts.reports.*`/`reportJobs.*` and the alpha-only items in §8). See
> [api-surface.md](api-surface.md) for the full cross-check that produced this list. Ordered by value:
> the n-tier customer CRUD (§12.1) is the biggest coherent addition; provisioning/import (§12.2) hangs
> off pages that already exist; the rest (§12.3–§12.4) are niche or doc-hygiene only.
>
> **Convention reminder.** Every phase follows the established layering: shared contracts in
> `GChannel.Shared/Contracts/*.cs` + `ApiRoutes`, client methods on `IGoogleChannelClient` /
> `GoogleChannelClient.*.cs` partials, cached (or explicitly uncached) minimal-API endpoints in
> `src/GChannel.ApiService/Endpoints/*.cs` registered in `Program.cs`, typed methods on
> `GChannelApiClient` (Web), and Blazor pages/actions. Google SDK type/method names must be verified
> with `dotnet build` (reflection over `Google.Apis.Cloudchannel.v1.dll` fails on missing deps).

### 12.1 Manage customers under a channel partner (n-tier) — **high value**

Today a partner-owned (indirect) customer is **read-only**: `channelPartnerLinks.customers.list` is
wired (§5) and the read-model backs the list (§10), but a distributor cannot create/import/edit/delete
a customer **directly under a channel partner**. This adds the full CRUD, mirroring the direct-customer
management already shipped under `accounts.customers.*` (§2). None of these calls are long-running — all
return the `Customer` resource directly (except `delete`, which returns empty).

- [x] **Get / create / update / delete a partner's customer** —
  `accounts.channelPartnerLinks.customers.{get,create,patch,delete}`.
- [x] **Import a partner's customer** — `accounts.channelPartnerLinks.customers.import` (pre-transfer,
  from an existing Cloud Identity id or domain).

> **Implemented.** N-tier customer CRUD is live end-to-end and mirrors the direct-customer flow (§2):
> new `ImportCustomerRequest` contract (`GChannel.Shared/Contracts/Customers.cs`, reused later by
> §12.2); nested routes `ChannelPartnerCustomer(linkId, customerId)` +
> `ChannelPartnerCustomerImport(linkId)` alongside the existing `ChannelPartnerCustomers(linkId)`
> (`ApiRoutes`); five client methods on `IGoogleChannelClient` /
> `GoogleChannelClient.ChannelPartnerLinks.cs`
> (`Get/Create/Update/Delete/ImportChannelPartnerCustomerAsync`) reusing the existing
> `MapCustomer`/`ToGoogleCustomer` helpers plus a new `ChannelPartnerCustomerName` resolver and a
> `ToGoogleImportCustomerRequest` mapper (`Mapping.cs`), with the patch scoped to the same editable
> field mask as the direct update so the immutable domain is untouched; the
> `GET/POST/PUT/DELETE /{linkId}/customers[/ {customerId}|/import]` endpoints on
> `ChannelPartnerLinksEndpoints.cs` (reads cached, mutations invalidate the
> `channel-partner-links:{linkId}:customers[:{customerId}]` caches and let the §10 sync converge); five
> typed `GChannelApiClient` methods; and the UI wiring on `ChannelPartnerLinkDetail.razor` — header
> **Add customer** / **Import customer** buttons plus per-row **Edit**/**Delete** actions, with
> create/edit reusing `CustomerForm.razor` (now `linkId`-aware via a `?linkId=` query param that routes
> saves/loads through the partner endpoints and returns to the link) and an inline import dialog
> (domain / Cloud Identity id / primary admin email + overwrite). A created/imported customer's
> `ChannelPartnerId` still equals the link's short id, so the customer-detail **Channel partner** row
> keeps working unchanged.

**Implementation plan:**

- **Contracts** (`GChannel.Shared/Contracts/Customers.cs`) — reuse `Customer`, `CustomersResult`,
  `SaveCustomerRequest`, `CustomerContact`, `CustomerAddress` unchanged (the partner-owned customer is
  the same shape). Add one new `ImportCustomerRequest { Domain? , CloudIdentityId?, PrimaryAdminEmail?,
  AuthToken?, bool OverwriteIfExists, ChannelPartnerId? }` (reused by §12.2 for the account-level
  `customers.import` too). The response of an import is a `Customer`.
- **Routes** (`ApiRoutes`) — nest under a link so they never collide with direct-customer routes:
  - `ChannelPartnerCustomer(linkId, customerId)` → `/api/channel-partner-links/{linkId}/customers/{customerId}` (get/patch/delete);
  - reuse `ChannelPartnerCustomers(linkId)` (already exists) for list + `POST` create;
  - `ChannelPartnerCustomerImport(linkId)` → `/api/channel-partner-links/{linkId}/customers/import`.
- **Client** (`IGoogleChannelClient` + `GoogleChannelClient.ChannelPartnerLinks.cs`) — add
  `GetChannelPartnerCustomerAsync`, `CreateChannelPartnerCustomerAsync`,
  `UpdateChannelPartnerCustomerAsync`, `DeleteChannelPartnerCustomerAsync`,
  `ImportChannelPartnerCustomerAsync`. SDK surface:
  `service.Accounts.ChannelPartnerLinks.Customers.{Get(name)/Create(GoogleCloudChannelV1Customer, parent=linkName)/Patch(GoogleCloudChannelV1Customer, name)/Delete(name)/Import(GoogleCloudChannelV1ImportCustomerRequest, parent=linkName)}`.
  Reuse the existing `MapCustomer` / `ToGoogleCustomer` helpers (`Mapping.cs`) — the same customer body
  works whether the parent is an account or a link. Patch uses a field mask (mirror the direct
  `UpdateCustomerAsync` mask so the immutable domain is untouched).
- **Endpoints** (`ChannelPartnerLinksEndpoints.cs`) — extend the existing group: `GET /{linkId}/customers/{customerId}`,
  `POST /{linkId}/customers` (Created), `PUT /{linkId}/customers/{customerId}` (Ok),
  `DELETE /{linkId}/customers/{customerId}` (NoContent), `POST /{linkId}/customers/import` (Created).
  On every mutation invalidate the `channel-partner-links:{linkId}:customers` cache **and** the §10
  read-model can pick the change up on its next sync (or mark the link stale via the existing resync
  path). Keep reads cached like the current list; keep mutations uncached.
- **Web client** (`GChannelApiClient`) — five typed methods paralleling the direct-customer ones
  (reuse the `SendAsync` POST/PUT + `DeleteAsync` helpers already present).
- **Web UI** (`ChannelPartnerLinkDetail.razor`) — the link-detail page already lists the partner's
  customers; add per-row **Edit**/**Delete** actions and header **Add customer**/**Import customer**
  buttons. Reuse `CustomerForm.razor` parameterised with the owning link id (it already handles
  create/edit); add a small import dialog (domain or Cloud Identity id + optional overwrite). Each row
  keeps deep-linking to the existing read-only customer detail.
- **Correlation** — a created/imported customer's `ChannelPartnerId` equals the link's short id (the
  existing §5 correlation), so the customer-detail **Channel partner** row keeps working unchanged.

### 12.2 Customer provisioning &amp; pre-transfer onboarding — **medium value**

Both hang off pages that already exist (the Cloud Identity check page and the Transfers flow), and one
of them exercises the §7 LRO machinery. Completes the §2 "Cloud Identity" checkbox.

- [x] **Provision a Cloud Identity** — `accounts.customers.provisionCloudIdentity`. **Returns an LRO**
  → reuse the §7 `ChannelOperation` contract + the **Operations** page for tracking.
- [x] **Import a customer before transfer** — `accounts.customers.import`. **Returns the `Customer`
  resource directly** (synchronous, *not* an LRO — this corrects the earlier "both are LROs"
  assumption).

> **Implemented.** Both flows are live end-to-end. New `ProvisionCloudIdentityRequest`
> (+ `CloudIdentityDetails` / `AdminUser`) contract in `GChannel.Shared/Contracts/Customers.cs`; the
> §12.1 `ImportCustomerRequest` is reused for the account-level import. Routes `CustomerImport`
> (`/api/customers/import`) and `CustomerProvisionCloudIdentity(customerId)`
> (`/api/customers/{customerId}/provision-cloud-identity`) on `ApiRoutes`. Two new client methods on
> `IGoogleChannelClient` / `GoogleChannelClient.Customers.cs`: `ImportCustomerAsync`
> (`Accounts.Customers.Import` → `MapCustomer`, reusing the `ToGoogleImportCustomerRequest` mapper) and
> `ProvisionCloudIdentityAsync` (`Accounts.Customers.ProvisionCloudIdentity` → `MapLongrunningOperation`,
> the §7 LRO mapper), with a new `ToGoogleProvisionCloudIdentityRequest` mapper. Endpoints on
> `CustomersEndpoints.cs`: `POST /import` (Created + list-cache invalidation, synchronous) and
> `POST /{customerId}/provision-cloud-identity` (**202 Accepted** with the operation, mirroring the
> entitlement LRO endpoints). Two typed `GChannelApiClient` methods. UI wiring: an **Import customer**
> header action + dialog on both the **Customers** list (`Customers.razor`) and the **Cloud Identity
> check** page (`CloudIdentity.razor`, prefilled with the checked domain, shown in the "exists" branch);
> a **Provision Cloud Identity** action + dialog on the **customer detail** page (`CustomerDetail.razor`,
> shown only when the customer has no `CloudIdentityId`) that deep-links to the **Operations** page via a
> new `?operation={id}` auto-track query param (`Operations.razor`). Import returns the `Customer`
> directly (synchronous — no LRO UX); only provision routes through Operations. Builds clean; **not
> deployed** (deployment is manual).

**Implementation plan:**

- **Contracts** — reuse the `ImportCustomerRequest` added in §12.1. Add a
  `ProvisionCloudIdentityRequest { CloudIdentityInfo?, AdminUser? (given/family name + email),
  bool ValidateOnly }` (maps `GoogleCloudChannelV1ProvisionCloudIdentityRequest`). Provision returns
  the existing `ChannelOperation` (§7); import returns `Customer`.
- **Routes** (`ApiRoutes`) — `CustomerImport` → `/api/customers/import` (account-level, POST) and
  `CustomerProvisionCloudIdentity(customerId)` → `/api/customers/{customerId}/provision-cloud-identity`
  (POST).
- **Client** (`IGoogleChannelClient` + `GoogleChannelClient.Customers.cs`) — `ImportCustomerAsync`
  (`service.Accounts.Customers.Import(GoogleCloudChannelV1ImportCustomerRequest, parent=AccountName)` → `MapCustomer`)
  and `ProvisionCloudIdentityAsync`
  (`service.Accounts.Customers.ProvisionCloudIdentity(GoogleCloudChannelV1ProvisionCloudIdentityRequest, customer=CustomerName)` → `MapOperation`,
  reusing the §7 LRO mapper).
- **Endpoints** (`CustomersEndpoints.cs`) — `POST /api/customers/import` (Created + invalidate the
  customer-list cache) and `POST /api/customers/{customerId}/provision-cloud-identity`
  (`Results.Accepted(operation)`, mirroring the entitlement LRO endpoints; invalidate `customer:{id}`).
- **Web UI:**
  - **Import** — surface on the **Cloud Identity check** page (`CloudIdentity.razor`), which already
    shows "no account → provision customer / view customers"; add an **Import existing customer** action
    that pre-fills the checked domain, and also expose it from the **Transfers** page header as the
    "bring a not-yet-owned customer in first" step before `transferEntitlements` (§4).
  - **Provision** — add a **Provision Cloud Identity** action on the customer detail page for customers
    with no `CloudIdentityId`; on submit, show the returned operation name and deep-link to the
    **Operations** page (§7) to watch it reach `done`.
- **Read-model** — a newly imported/provisioned customer is picked up by the next §10 sync cycle (or
  mark it stale immediately via the existing resync path) so the estate views converge.

### 12.3 Eligible billing accounts (GCP / n-tier billing) — **niche**

- [ ] **Query eligible billing accounts** — `accounts.customers.queryEligibleBillingAccounts`.

Only relevant if a **GCP** purchase flow (or n-tier distributor billing) is added — it returns *which*
billing account is eligible for given SKUs, not any monetary amount. Plan (kept minimal until there's a
concrete purchase-flow need): new `EligibleBillingAccountsResult` contract (map
`GoogleCloudChannelV1QueryEligibleBillingAccountsResponse` → per-SKU groups of billing accounts with an
eligibility flag + reason); `GET /api/customers/{customerId}/eligible-billing-accounts?skus=` endpoint
(cached) calling `service.Accounts.Customers.QueryEligibleBillingAccounts(customer)` with the `skus`
query; surfaced in the **Purchase entitlement** flow only when the selected SKU is GCP/billing-gated.
Deferred otherwise.

### 12.4 Minor completeness &amp; doc hygiene

- [ ] **`operations.delete`** — deliberately **not surfaced**. It only *forgets* a completed LRO; the
  **Operations** page (§7) tracks operations by name and doesn't need server-side deletion. Documented
  as intentionally omitted in [api-surface.md](api-surface.md); implement only if a future "clear
  tracked operation" UX wants it (`service.Operations.Delete(name)` → 204 endpoint).
- [ ] **Repricing config `.get`** — `customerRepricingConfigs.get` /
  `channelPartnerRepricingConfigs.get` are deliberately **not surfaced**: `list` already returns full
  config bodies, so the UI never needs a single-config fetch. Note-only in api-surface.md.
- [ ] **`integrators.*`** (`listSubscribers` / `registerSubscriber` / `unregisterSubscriber`) —
  intentionally **out of scope**. This is the *integrator-scoped* twin of the account-scoped Pub/Sub
  subscriber admin already shipped (`accounts.register`/`unregister`/`listSubscribers`, §7). An
  "integrator" is a distinct Channel Services identity type (platform integrators, not
  resellers/distributors), which this app is not. This todo item is a **doc-hygiene** task: add a line
  to [api-surface.md](api-surface.md) acknowledging the resource exists in `v1` and explaining why it's
  skipped, so the surface cross-check is complete.

### Risks &amp; caveats

- **No new auth scope.** Everything here sits under the same `apps.order` surface the app already uses;
  no scope/consent change.
- **Reuse over new shapes.** §12.1/§12.2 reuse the existing `Customer`/`SaveCustomerRequest`/mapping and
  the §7 LRO plumbing — the main new work is endpoints, routes, one or two request contracts, and UI
  wiring, not new infrastructure.
- **Read-model consistency.** Partner-customer mutations (§12.1) and import/provision (§12.2) must
  invalidate the relevant Redis caches and let the §10 sync converge (or use the resync-now path) so the
  estate views don't show stale rows.
- **`import` is synchronous.** Don't route it through the LRO/Operations UX — it returns the `Customer`
  directly; only `provisionCloudIdentity` is an LRO.

