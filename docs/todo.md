# TODO / future developments

See [api-surface.md](api-surface.md) for the full catalog of `v1` Cloud Channel API
resources/methods these items map to.

## Hardening

- **Silent token refresh.** Google access tokens expire after ~1 hour. A refresh token is
  captured (`AccessType=offline`); wiring up silent refresh in the API service is the recommended
  next hardening step.

## Roadmap (Channel API capabilities to grow into)

Roughly in dependency order — read paths first, then customer/entitlement lifecycle, then the
advanced distributor/billing features.

### 1. Catalog browsing (read-only, low risk)

- [ ] **Products** — `products.list` and `products.skus.list` to browse the sellable catalog.
- [ ] **Offers** — `accounts.offers.list` to show the Offers the reseller can sell.
- [ ] **SKU groups** — `accounts.skuGroups.list` + `accounts.skuGroups.billableSkus.list`.

### 2. Customer management

- [ ] **List / view customers** — `accounts.customers.list` + `accounts.customers.get`.
- [ ] **Create / update / delete customer** — `create`, `patch`, `delete`.
- [ ] **Cloud Identity** — `provisionCloudIdentity`, and `import` for pre-transfer onboarding.
- [ ] **Purchasable catalog per customer** — `listPurchasableOffers`, `listPurchasableSkus`,
  `queryEligibleBillingAccounts`.

### 3. Entitlement lifecycle (the core selling flow)

- [ ] **List / view entitlements** — `entitlements.list` + `entitlements.get` +
  `listEntitlementChanges` (history) + `lookupOffer`.
- [ ] **Purchase** — `entitlements.create`.
- [ ] **Modify** — `changeOffer`, `changeParameters` (seats), `changeRenewalSettings`.
- [ ] **State changes** — `activate`, `suspend`, `cancel`, `startPaidService` (trial → paid).

### 4. Transfers

- [ ] **Inspect transferability** — `accounts.listTransferableSkus`,
  `accounts.listTransferableOffers`.
- [ ] **Execute transfer** — `customers.transferEntitlements` and
  `customers.transferEntitlementsToGoogle`.

### 5. Distributor / n-tier (channel partner links)

- [ ] **Manage links** — `accounts.channelPartnerLinks` (list/get/create/patch).
- [ ] **Customers under a partner** — `accounts.channelPartnerLinks.customers.*`.

### 6. Repricing / rebilling margin

- [ ] **Customer repricing** — `accounts.customers.customerRepricingConfigs.*`.
- [ ] **Channel partner repricing** — `accounts.channelPartnerLinks.channelPartnerRepricingConfigs.*`.

### 7. Eventing & operations

- [ ] **Pub/Sub subscribers** — `accounts.register` / `unregister` / `listSubscribers` to receive
  entitlement-change notifications instead of polling.
- [ ] **Long-running operations** — surface `operations.get` / `list` for async calls
  (create/transfer/change return LROs).

### 8. `v1alpha1` preview capabilities (optional, alpha-only)

These have no stable `v1` equivalent yet, so they require opting into the alpha API and accepting
breaking-change risk:

- [ ] **Deal registration** — `opportunities.*` (create/get/patch/query) for submitting and
  tracking sales opportunities.
- [ ] **Provisionable Cloud Identity types** — `accounts.listProvisionableCloudIdentityTypes`
  to drive the customer-creation UX (shows when a transfer is required).
- [ ] **Assign channel partner to entitlement** — `entitlements.assignChannelPartner` for n-tier.

## Known placeholders

- The dashboard figures on the home page are placeholders. **Note:** the `accounts.reports.*` and
  `accounts.reportJobs.fetchReportResults` endpoints are **deprecated** in `v1`, so derive these
  figures from entitlement/customer data rather than the legacy reporting API.

## Notes

- `GoogleChannel:AccountId` is required for every Channel API call and is validated at runtime.
- Most mutating calls (create/transfer/change) return **long-running operations**; the UI will
  need to poll `operations` and reflect pending state.
