> Part of the [GChannel TODO index](../todo.md).

## 14. Provisionable Cloud Identity types (sharpen create-vs-transfer)

> **Status:** Deferred — **alpha-only**. The value is small and, crucially, the method is **not in the
> stable `v1` API**: `accounts:listProvisionableCloudIdentityTypes` exists **only in `v1alpha1`**
> ([reference](https://docs.cloud.google.com/channel/docs/reference/rest/v1alpha1/accounts)). That
> conflicts with the app's standing principle of building on GA only (see §8). A **GA interim already
> covers most of the value** (below), so this is tracked as an optional enhancement to revisit **if/when
> the method reaches GA**.

### What it would add

On the **Cloud Identity check** / **new customer** flow (`CloudIdentity.razor`, `CustomerForm.razor`),
show which **Google Workspace customer types** can be provisioned for a domain and whether a **transfer
is required instead of a create** — sharpening the create-vs-transfer decision beyond the yes/no the app
shows today.

### GA interim (already in place — no alpha dependency)

The app already drives the create-vs-transfer decision with the **GA** `accounts:checkCloudIdentity
AccountsExist` call (implemented in §2 / §12.2): it reports whether Cloud Identity accounts exist for the
domain and whether they are owned by this reseller, and the Cloud Identity page already routes the user
to **Provision customer** vs **View customers / transfer** accordingly. So the core decision is covered
on GA today; `listProvisionableCloudIdentityTypes` would only *enrich* it (the specific provisionable
types), not unblock it.

### Why it's deferred

- **Alpha surface.** The method is `v1alpha1` only — no SLA, no deprecation policy, may change or vanish
  without notice (see [../api-surface.md](../api-surface.md) and §8). The app deliberately uses
  `Google.Apis.Cloudchannel.v1` (GA) everywhere.
- **Low marginal value.** The GA `checkCloudIdentityAccountsExist` signal already makes the
  create-vs-transfer call; this only adds the list of provisionable types.

### Implementation plan (only if pursued before GA)

Because the GA SDK (`Google.Apis.Cloudchannel.v1`) does **not** expose this method, it needs one of:

- **Option A — add the alpha client.** Reference `Google.Apis.Cloudchannel.v1alpha1` alongside the GA
  client and call `Accounts.ListProvisionableCloudIdentityTypes(parent = AccountName)`. Isolate all
  alpha calls behind a single wrapper so the blast radius is contained.
- **Option B — raw authenticated HTTP.** POST to
  `https://cloudchannel.googleapis.com/v1alpha1/{account}:listProvisionableCloudIdentityTypes` using the
  existing credential (the same `GoogleCredential` the GA client uses), parsing the JSON directly — no
  new SDK dependency.

Then follow the standard layering, **gated behind an explicit opt-in flag** so alpha is never on by
default:

- [ ] **Config gate.** New `GoogleChannel:AllowAlphaApis` (default `false`); the feature no-ops unless it
  is set, so production stays GA-only.
- [ ] **Contract.** `Shared/Contracts/Customers.cs`: `ProvisionableCloudIdentityType`
  (type + whether a transfer is required + any per-type detail) + a result wrapper; `ApiRoutes` entry.
- [ ] **Client.** `IGoogleChannelClient.ListProvisionableCloudIdentityTypesAsync(domain, ct)` on
  `GoogleChannelClient.Customers.cs` (or a small `*.Alpha.cs` partial), guarded by `AllowAlphaApis`
  (returns empty / throws a clear "alpha disabled" result otherwise). Verify type/method names with
  `dotnet build` (reflection over the SDK fails on missing deps).
- [ ] **Endpoint.** A cached read in `CustomersEndpoints.cs` (or `AccountsEndpoints.cs`) that returns the
  provisionable types for a domain; degrade gracefully to empty when alpha is disabled/unavailable.
- [ ] **Web + UI.** Typed method on `GChannelApiClient`; surface the provisionable types + transfer hint
  on `CloudIdentity.razor` beside the existing exists/owned result, clearly marked as a preview signal.

### When to build

Revisit if/when `listProvisionableCloudIdentityTypes` graduates to **GA `v1`** (then drop the alpha flag
and fold it straight into the existing Cloud Identity flow), or if a concrete onboarding case needs the
per-type provisionability signal badly enough to accept an alpha dependency behind the opt-in flag. Until
then the GA `checkCloudIdentityAccountsExist` path is sufficient.
