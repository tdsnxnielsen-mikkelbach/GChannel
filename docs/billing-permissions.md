# Billing permissions — members, inheritance, account type, 2SV and Advanced Protection

This describes how the **Billing permissions** section on the GCP budgets page
(`/billing/budgets`) is built: how we list the users on a Cloud Billing sub-account split into
**direct** and **inherited**, and how each user is then enriched with **managed/consumer**
classification, **2-Step Verification** and **Advanced Protection** status. It mirrors the
Partner Sales Console's *Customers → Billing permissions → Manage billing subaccount roles & users*
view, with security posture added.

Everything runs through the existing reseller service account
(`gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com`) — no end-user credentials are
required, apart from the signed-in user's Channel API token that the rest of the app already uses.

---

## Part 1 — Direct vs inherited members

### What Google exposes

A Cloud Billing account (master or sub-account) has an **IAM policy**: a list of
`bindings`, each a `role` plus a list of `members` (`user:…`, `group:…`, `serviceAccount:…`,
`domain:…`). IAM on billing accounts follows the normal GCP inheritance rule: a sub-account
**inherits** every binding on its master account, and may additionally have bindings of its own.

The Cloud Billing API method `billingAccounts.getIamPolicy` returns **only the bindings set on
the named resource** — it does *not* merge in the parent's policy. That is exactly what we need:
by reading the policy on two resources we can tell the two kinds of grants apart.

### Step by step

Implemented in `BillingBudgetsService.GetBillingAccountMembersAsync`
([src/GChannel.ApiService/Services/BillingBudgetsService.cs](../src/GChannel.ApiService/Services/BillingBudgetsService.cs)),
exposed as `GET /api/billing/accounts/{billingAccountId}/members`.

1. **Resolve the master.**
   `CloudBillingClient.GetBillingAccountAsync("billingAccounts/{id}")` → read
   `masterBillingAccount`. If it's empty, the selected account *is* a master and there is
   nothing to inherit.

2. **Read the sub-account's own policy → DIRECT members.**
   `CloudBillingClient.GetIamPolicyAsync("billingAccounts/{subId}")`. Every
   `(role, member)` pair in the returned bindings was granted on this sub-account itself. These
   are the rows the Partner Sales Console shows **without** the inheritance icon
   (e.g. `mohamad.n.machiah@sentia…` on *Boliga v. Sentia*).

3. **Read the master's policy → INHERITED members.**
   `CloudBillingClient.GetIamPolicyAsync("billingAccounts/{masterId}")`. Every
   `(role, member)` pair here applies to all sub-accounts under that master. These are the rows
   the console shows **with** the inheritance (card) icon — typically the distributor's own staff
   and integration service accounts.

4. **De-duplicate.** If the same `(role, member)` exists on both, the direct grant wins and it's
   removed from the inherited list, so a user never appears twice.

5. **Flatten and label.** Each binding is exploded into one row per member. The member string is
   split on the first `:` into `MemberType` (`user`, `group`, `serviceAccount`, `domain`) and
   `Email`. Role IDs are mapped to the names the console uses:

   | Role id | Shown as |
   |---|---|
   | `roles/billing.admin` | Billing Account Administrator |
   | `roles/billing.user` | Billing Account User |
   | `roles/billing.viewer` | Billing Account Viewer |
   | `roles/billing.costsManager` | Billing Account Costs Manager |
   | `roles/billing.creator` | Billing Account Creator |

   Rows are sorted by role name, then email.

6. **Return** a `BillingAccountMembersResult { Direct[], Inherited[], MasterBillingAccountId,
   Available, Error }`. Any failure (most commonly a missing permission) is returned as
   `Available=false` with the message, so the page shows a warning banner instead of a 500.

### Permission required

The service account needs `billing.accounts.getIamPolicy`, which is in **`roles/billing.viewer`**.
Granting it on the **master** accounts is enough — sub-accounts inherit it:

```bash
gcloud billing accounts add-iam-policy-binding 016E74-10DE75-21330A \
  --member="serviceAccount:gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com" \
  --role="roles/billing.viewer"
gcloud billing accounts add-iam-policy-binding 01F210-8698CB-C16E8D \
  --member="serviceAccount:gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com" \
  --role="roles/billing.viewer"
```

### Rate limiting

All three calls go through the service's existing read `RequestPacer` so a burst of account
switches stays under the Cloud Billing API per-minute quota.

---

## Part 2 — Account type, 2SV and Advanced Protection per member

Once the two member lists are on screen, the page makes a **second** set of calls —
`POST /api/billing/members/identity` with the distinct `user:`/`group:` emails, **in chunks of
16** — and fills in three extra columns in place as each chunk returns. This is deliberately
separate so the member tables render immediately and the (slower) enrichment arrives
progressively. Server-side, each chunk resolves its members **4 at a time** (bounded fan-out), and
for each user the audit and usage Reports calls run concurrently. All Reports API calls additionally
go through one shared pacer (≤ 4/s) with backoff-retry on quota 403/429s, because the Reports
API's per-second quota is far tighter than Directory's — an unpaced 8-wide fan-out produced
`403 Request rate higher than configured` on most members. A 65-member master policy now
completes in ~20 s instead of the minutes a sequential pass would take (which had been tripping
the 60 s HTTP attempt timeout and leaving most rows as `?`).

### 2.1 What we investigated, and what Google actually exposes

| Need | Candidate API | Verdict |
|---|---|---|
| Is the account managed or consumer? | Cloud Identity SCIM `customers.v2.Users.get` | **Not usable.** SCIM is for provisioning users *inside your own tenant*; it cannot classify an arbitrary external email. |
| | Cloud Identity `userinvitations.isInvitableUser` | **Not usable for our case.** Only answers for domains verified in *our* Cloud Identity customer; sub-account members are mostly from other tenants. |
| | Admin SDK Directory `users.get` | **Usable for our own tenant.** A 200 means the user exists in the reseller's Workspace directory → managed. 404 means not ours. |
| | Channel API `accounts.checkCloudIdentityAccountsExist` | **Usable for any domain.** Tells us whether the email's *domain* has a Cloud Identity / Workspace tenant, and returns its customer id. Already used by the "Cloud identity check" page. |
| 2-Step Verification | Admin SDK Directory `users.get` / `users.list(customer=…)` → `isEnrolledIn2Sv`, `isEnforcedIn2Sv` | **Yes**, for users in our own tenant **and** for resold customers whose Admin console we have reseller access to (the `cid=…` you see in `admin.google.com` URLs). The Directory API honours that reseller access when the resold customer's id is passed as the `customer` parameter. |
| Advanced Protection | Directory `users` resource | **No field.** |
| | Reports API user-usage `accounts:*` parameters | **No Advanced Protection field** — but `accounts:num_security_keys` and `accounts:num_passkeys_enrolled` are there. Advanced Protection *requires* a passkey or security key, so these are surfaced as a supporting signal. |
| | Reports API **Activities**, application `user_accounts`, events `titanium_enroll` / `titanium_unenroll` | **The only signal — and it works.** "Titanium" is Google's internal name for Advanced Protection. Status is taken from the most recent event; verified against a resold customer's user whose console shows *Advanced Protection: ON*. Works for resold customers via the `customerId` parameter. Audit events are retained for ~6 months, so older enrollments fall back to the key counts above. |

### 2.2 Authentication

Implemented in `WorkspaceUserSecurityService`
([src/GChannel.ApiService/Services/WorkspaceUserSecurityService.cs](../src/GChannel.ApiService/Services/WorkspaceUserSecurityService.cs)).

The Admin SDK has no service-account identity of its own, so — exactly like the existing
Channel API background refresh — the service account key is used with **domain-wide delegation**
to impersonate the reseller admin configured in `GoogleChannel:ImpersonateUser`:

```
GoogleCredential.FromJson(ServiceAccountKeyJson)
    .CreateScoped(admin.directory.user.readonly,
                  admin.reports.audit.readonly,
                  admin.reports.usage.readonly)
    .CreateWithUser(ImpersonateUser)
```

Because the token is minted *as that admin*, the Directory and Reports APIs answer for the
admin's tenant (`dk.gsuite.dist.techdata.com` and any secondary domains of that Workspace
customer) **plus every resold customer that admin can open in the Admin console** as a reseller
— provided the resold customer's id is passed explicitly (`users.list?customer=C01aihlmh`,
`activities.list?customerId=C01aihlmh`). Without the id, the APIs default to the caller's own
tenant.

### 2.3 Step by step per email

Implemented in `BillingEndpoints.ResolveMemberIdentitiesAsync`
([src/GChannel.ApiService/Endpoints/BillingEndpoints.cs](../src/GChannel.ApiService/Endpoints/BillingEndpoints.cs)).

1. **Normalise and cache-check.** Emails are trimmed, lower-cased, de-duplicated and capped at
   100 per request. Each is looked up in Redis under `member-identity:{email}` first
   (TTL = `GoogleChannel:CacheSeconds`, min 60 s).

2. **Consumer domain short-circuit.** If the domain is `gmail.com` or `googlemail.com` →
   **Consumer**. No further lookups.

3. **Our own directory (Admin SDK Directory API).**
   `users.get(email)` with `fields=primaryEmail,customerId,isEnrolledIn2Sv,isEnforcedIn2Sv,suspended,isAdmin`.

   - **200** → the user is in a tenant we can read → **Managed**, and we now have:
     - `TwoStepVerificationEnrolled` ← `isEnrolledIn2Sv`
     - `TwoStepVerificationEnforced` ← `isEnforcedIn2Sv`
     - `Suspended`, `IsAdmin`

     **Important:** `users.get(email)` with no customer parameter also resolves users in **resold
     customers** we have reseller access to — it is not limited to our own tenant. The returned
     `customerId` is therefore compared with `customers.get("my_customer")`; when they differ the
     user is in a resold tenant and that id is carried into the Reports calls in step 4. Without
     this, the audit and usage queries ran against *our* tenant for a foreign user and came back
     empty — which is exactly why `hn@s360digital.com` showed "No recent event" with no key line
     while the Directory data (2SV) looked fine.

     Then the **Reports API** is queried for Advanced Protection (step 4).
   - **404** → not in any tenant we can read. Fall through to step 5.
   - **403 / 401** → either a foreign user, or a setup problem (API not enabled, DWD scope
     missing). The message is inspected; setup problems are surfaced as `DirectoryError` so the
     page can show the info banner. Fall through to step 5.

4. **Advanced Protection (Admin SDK Reports API).**
   `activities.list(userKey=email, applicationName=user_accounts, startTime=now-200d)`, paged.
   All events are scanned for `titanium_enroll` / `titanium_unenroll` and the **latest** one wins:

   | Outcome | Result |
   |---|---|
   | Latest event `titanium_enroll` | `Enrolled` (with the event timestamp) |
   | Latest event `titanium_unenroll` | `NotEnrolled` (with the event timestamp) |
   | Query succeeded, no event retained | `Unknown` — shown as "No recent event" |
   | Query failed (403, API off, …) | `Unavailable` — shown in red with the API error in the tooltip |

   **Verified live:** `hn@s360digital.com` (resold customer `C01aihlmh`) returns `Enrolled` with
   the `titanium_enroll` event of 13 Aug 2026 and 3 passkeys — matching the *Advanced Protection: ON*
   shown in that customer's Admin console. So for resold customers with reseller access this is a
   genuine, per-user answer, not just a domain-level guess.

   The remaining limit is retention: the Admin console shows the state from internal data, but the
   only public signal is the enrollment **event**, and the Reports API keeps user-accounts events
   for ~6 months. A user whose last enroll/unenroll event is older than that looks identical to one
   who never enrolled (`Unknown`).

   To cover that gap, the **user usage report** is also read:
   `userUsageReport.get(email, date, parameters=accounts:num_security_keys,accounts:num_passkeys_enrolled)`
   (probing two dates, 3 and 6 days back, because usage data lags). Advanced Protection *requires* a passkey or
   security key — it is the factor the programme enforces — so the counts are shown as a second line
   **under every status** and also let the UI resolve the `Unknown` case:

   | Audit event | Usage report | Status chip | Key line |
   |---|---|---|---|
   | `titanium_enroll` | any | `Enrolled` (green) | e.g. `3 passkeys` (green) — the factor that satisfies enrollment |
   | `titanium_unenroll` | any | `Not enrolled` (amber) | counts as reported |
   | none retained | 0 security keys **and** 0 passkeys | `Not enrolled` (amber) — safe inference, since enrollment is impossible without one | `no keys or passkeys` (red) |
   | none retained | ≥ 1 key or passkey | `No recent event` (grey) — indeterminate; the user *may* be enrolled | e.g. `1 security key · 2 passkeys` (green) |
   | none retained | not readable (scope missing / data lag) | `No recent event` (grey) | hidden |
   | query failed | any | `Unavailable` (red, API error in tooltip) | counts if readable |

   The key line's tooltip names the usage-report date it was read for. The audit event always
   decides the chip because usage data lags a few days: a user who enrolled yesterday has an audit
   event today but may still report 0 keys for that date.

   > **Troubleshooting — everything shows "No recent event" / `n/a` at once.** A domain-wide
   > delegation token request is all-or-nothing: if *any* requested scope isn't in the Admin-console
   > grant, Google returns `unauthorized_client` and the token is refused. The service therefore
   > builds **one single-scope credential per API** (Directory, audit Reports, usage Reports) so a
   > missing grant only disables the signal that needs it, and reports it as
   > *"Domain-wide delegation scope not granted: …"* (banner for Directory, red `Unavailable` chip
   > for Advanced Protection). Before this split, adding the usage scope in code before granting it
   > in the console silently took down all three lookups.

5. **Domain classification (Channel API).** For users not in our directory,
   `accounts.checkCloudIdentityAccountsExist(domain)` is called once per distinct domain (memoised
   within the request, and in Redis under the same `cid:{domain}:` key the Cloud-identity-check
   page uses, so both features share one API call per domain):

   | Channel API answer | `AccountKind` | Meaning |
   |---|---|---|
   | A `DOMAIN`-type account exists | **Managed** | Domain-verified Workspace / Cloud Identity tenant. Continue to step 6. |
   | Only non-`DOMAIN` (team) accounts | **Managed (team)** | Email-verified Cloud Identity team account; partially managed. |
   | No account | **Consumer** | No tenant owns this domain → an unmanaged Google Account created with a custom email. |
   | Call failed | **Unknown** | |

6. **Resold-customer lookup (Admin SDK, reseller access).** For a *Managed* domain, resolve the
   customer's Workspace id — `customerCloudIdentityId` from the Channel API answer in step 5
   (e.g. `C01aihlmh` for `s360digital.com`), falling back to `CloudIdentityId` on our read-model
   customer row — and repeat steps 3–4 scoped to it:

   - `users.list(customer=C01aihlmh, query="email=hn@s360digital.com")`
   - `activities.list(userKey=hn@s360digital.com, applicationName=user_accounts, customerId=C01aihlmh)`

   If we have reseller Admin-console access to that customer, both succeed and the row gets real
   2SV / Advanced Protection values with `WorkspaceCustomerId` set. If not, the Directory API
   returns 403/404 and the row stays *Managed* with 2SV `n/a` — nothing breaks.

7. **Cache and return.** Each `MemberIdentityInfo` is stored in Redis and returned in a
   `MemberIdentityResult { Members[], DirectoryAvailable, DirectoryError }`. Results whose Reports
   signal failed on a **quota / rate-limit** error are returned (as `Unavailable` with
   "rate limit reached — retry shortly") but **not cached**, so the next load retries instead of
   pinning the failure for the full TTL.

### 2.4 What the UI shows

[src/GChannel.Web/Components/Pages/Budgets.razor](../src/GChannel.Web/Components/Pages/Budgets.razor)
renders both member tables from one shared template with these columns:

| Column | Source | Values |
|---|---|---|
| Member | IAM policy (+ step 3/6) | email with a type icon (user / group / service account / domain); `admin` badge when `IsAdmin`, red `suspended` badge when the Workspace account is suspended but still holds the billing role |
| Role | IAM policy | friendly role name |
| Account | step 2 / 3 / 5 / 6 | `Managed` (green) · `Managed (team)` (amber) · `Consumer` (red) · `Unknown`. Tooltip explains which signal decided it, including the resold customer id when reseller access was used. |
| 2SV | step 3 / 6 | `On · enforced` · `On` · `Off` for our tenant and resold customers with reseller access; `n/a` for everyone else |
| Adv. Protection | step 4 / 6 | Status chip: `Enrolled` · `Not enrolled` (from an audit event, or inferred from 0 keys + 0 passkeys) · `No recent event` (indeterminate) · `Unavailable` (red, API error in tooltip); `—` for users whose tenant we can't read. Beneath it, a **key line** whenever the usage report was readable: `N security keys · M passkeys` in green, or `no keys or passkeys` in red. |

Service-account and domain members get `—` in the security columns — they are not Google user
accounts. If the Admin SDK side isn't configured or is denied, an info banner explains that
account type still works but 2SV / Advanced Protection are not readable.

Every column header is **sortable**. The three security columns sort by posture rather than
label, so an ascending sort puts the members to act on first:

| Column | Ascending order |
|---|---|
| Account | Consumer → Managed (team) → Unknown → Managed → no data |
| 2SV | Off → On (not enforced) → On · enforced → n/a |
| Adv. Protection | Not enrolled (event **or** 0 keys/passkeys) → Unavailable → No recent event (has keys) → Enrolled → — |

### 2.5 Setup required

1. **Enable the Admin SDK API** in the service account's project:

   ```bash
   gcloud services enable admin.googleapis.com --project tdsgchannel
   ```

2. **Widen the domain-wide delegation grant.** In the Workspace Admin console as a super admin:
   *Security → Access and data control → API controls → Manage Domain Wide Delegation*, edit the
   entry for client ID `117390368106728225081` and set the scopes to:

   ```
   https://www.googleapis.com/auth/apps.order,
   https://www.googleapis.com/auth/admin.directory.user.readonly,
   https://www.googleapis.com/auth/admin.reports.audit.readonly,
   https://www.googleapis.com/auth/admin.reports.usage.readonly
   ```

   (The first scope is the existing Channel API scope and must be kept.) There is no `gcloud`
   equivalent for this step. Propagation can take a few minutes.

3. Nothing else changes — `GoogleChannel:ServiceAccountKeyJson` and
   `GoogleChannel:ImpersonateUser` are already configured for the background refresh and are
   reused as-is.

### 2.6 Known limits

- **Scope of visibility.** 2SV and Advanced Protection are readable for users in the
  impersonated admin's own Workspace tenant and in resold customers that admin has reseller
  Admin-console access to. Customers who have not granted reseller access (or non-resold tenants)
  show account type only.
- **Advanced Protection is inferred, not read.** There is no state field; only enroll/unenroll
  audit events with ~6-month retention, plus passkey/security-key counts as a necessary-but-not-
  sufficient indicator.
- **Domain classification is per domain, not per user.** A domain with a Workspace tenant may
  still have a legacy "conflicting" consumer account on a specific address; the Channel API cannot
  tell those apart, so such a user would be shown as *Managed*.
- **Groups** are classified by their domain like users, but 2SV / Advanced Protection don't apply
  to groups and are shown as `n/a` / `—`.

---

## Part 3 — Nightly security-posture scan and KPIs

The GCP budgets page shows posture for **one** account on demand. The **Security posture** page
(`/billing/posture`) shows it for the **whole estate**, from a scan the worker runs every night.

### What the scan does

Implemented in `BillingPostureSyncService`
([src/GChannel.Worker/Services/BillingPostureSyncService.cs](../src/GChannel.Worker/Services/BillingPostureSyncService.cs)).

1. **Schedule.** Runs daily at `GoogleBilling:PostureScanHourUtc` (default 02:00 UTC). On worker start,
   if the last completed scan is older than `PostureScanStaleHours` (default 26) — or there has never
   been one — it runs immediately. A Redis lock (`billing:posture:lock`, 23 h) keeps a second replica
   from scanning the same night. Disable with `GoogleBilling:PostureScanEnabled=false`.
2. **Enumerate accounts** with the same discovery the budgets page uses (`ListBillingAccountsAsync`):
   master accounts plus every sub-account under them; closed accounts are skipped.
3. **Read members** for each account exactly as Part 1 describes (direct = the account's own policy,
   inherited = the master's policy). Calls are paced by the billing service's read pacer.
4. **Resolve identities once per distinct email** through the shared `MemberIdentityResolver`
   (the same code path as Part 2, now a service used by both the API endpoint and the worker), with
   `refresh=true` so the nightly scan always reads live rather than inheriting the day's UI cache.
5. **Persist** into two read-model tables:
   - `BillingPostureAccounts` — one row per account (display name, master, Channel customer via the §15
     spend mapping, last error, scan time).
   - `BillingPostureMembers` — one row per *(account, email, role, inherited)* grant with the member's
     account kind, 2SV, Advanced Protection, key counts, admin/suspended flags.

   Members are replaced per account each night. An account whose policy was unreadable keeps its
   previous members and records the error. Accounts that disappeared are removed.
6. **Bookkeeping** in `SyncCursors` (scope `billing-posture`): `LastFullPassUtc` = last successful
   scan (the page's "Scanned …" stamp), `Notes` = last failure message if any.

### Best-practice rules

Defined once in `PostureRules`
([src/GChannel.Shared/Contracts/BillingPosture.cs](../src/GChannel.Shared/Contracts/BillingPosture.cs))
so the worker, API and page agree:

| Rule | Passes when | Source |
|---|---|---|
| 2SV | `isEnrolledIn2Sv == true` | Directory API |
| Advanced Protection | latest audit event is `titanium_enroll` | Reports API (activities) |
| Keys | `num_security_keys + num_passkeys_enrolled ≥ 1` | Reports API (usage) |

A member is **assessable** only when it is a managed account in a tenant we can read (our own, or a
resold customer with reseller admin access). **Compliant** = assessable and all three rules pass;
**non-compliant** = assessable and any rule fails. Consumer accounts and unreadable tenants are never
counted as compliant — they appear in their own buckets. A **consumer admin** is a consumer Google
Account (gmail / no tenant) holding `roles/billing.admin`.

### Endpoints

| Route | Returns |
|---|---|
| `GET /api/billing/posture` | `BillingPostureSummary`: scan time, headline counts, a `PostureBucket` each for **direct** and **inherited** (distinct email+role across the estate), and the per-account table |
| `GET /api/billing/posture/accounts/{id}/members` | the scanned member rows for one account |
| `GET /api/billing/posture/findings?scope=all\|direct\|inherited` | distinct members that are non-compliant or consumer admins, each with the list of billing accounts the grant appears on |

Inherited members are stored once per sub-account (so per-account views are self-contained), but the
estate-wide buckets de-duplicate by *(email, role)* so a 65-member master policy counts as 65 people,
not 65 × the number of sub-accounts.

### The page

- **Headline KPIs:** accounts scanned · accounts with a **direct consumer admin** · accounts with
  non-compliant **direct** members · accounts with non-compliant **inherited** members.
- **Direct / Inherited cards:** compliance %, and counts for 2SV off, no Advanced Protection, no keys,
  failing any rule, consumer accounts (with how many are billing admins), and unreadable tenants.
- **Accounts tab:** sortable table (consumer admin first), "only accounts with findings" switch, search,
  and a per-account members drill-down.
- **Action list tab:** the flat findings list, scoped to direct / inherited / both, sortable by 2SV,
  Advanced Protection and keys, with the number of billing accounts each grant is on.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `GoogleBilling:PostureScanEnabled` | `true` | Run the nightly scan |
| `GoogleBilling:PostureScanHourUtc` | `2` | UTC hour the scan starts |
| `GoogleBilling:PostureScanStaleHours` | `26` | On startup, scan immediately if the last one is older than this |

No new Google permissions: the scan reuses the Cloud Billing viewer role (Part 1) and the DWD scopes
(Part 2) already granted.

---

## Part 4 — Native Google vs third-party (Marketplace / ISV / partner)

Two complementary sources, both materialised nightly and surfaced on **Spend trends** and each
**customer detail** page.

### §1 Spend split from the billing export (`CustomerSpendBySeller`)

The Cloud Billing BigQuery export carries purpose-built columns for this:

| Column | Values | Meaning |
|---|---|---|
| `transaction_type` | `GOOGLE` · `THIRD_PARTY_RESELLER` · `THIRD_PARTY_AGENCY` | Who sold the line item. `GOOGLE` = native Google Cloud. The two `THIRD_PARTY_*` values are Marketplace / ISV / partner products (resold by Google, or sold by the partner with Google as agent). Only populated for `cost_type = regular`. |
| `seller_name` | legal entity | The ISV / partner on Marketplace rows (e.g. Anthropic, Databricks); blank on Google rows. |
| `service.description` | text | Product name ("Compute Engine", "Anthropic Claude 3.5 Sonnet"). |

`BillingSyncService` runs a third aggregation per cycle grouped by
*(sub-account, invoice month, currency, transaction_type, seller_name, service.description)*,
attributes each row to a customer with the same resolver as the monthly spend, and replaces the
window in `CustomerSpendBySeller`. The contract maps `transaction_type` to `SellerCategory`
(`Google` / `ThirdPartyReseller` / `ThirdPartyAgency` / `Unknown` for taxes, adjustments and untyped rows).

| Route | Returns |
|---|---|
| `GET /api/billing/spend/sellers?months=` | Estate totals per category + per-seller + per-product (third-party only) |
| `GET /api/billing/customers/{id}/spend/sellers?months=` | Same for one customer |
| `GET /api/billing/spend/sellers/customers?months=` | Per-customer Google vs third-party split with seller list |

UI: `SellerSplitCard` (third-party share, category table, top sellers / products) on Spend trends
(estate, last month) and customer detail (last 3 months); a *Third-party share by customer* table on
Spend trends. No new permissions — it is the same export the spend sync already reads.

### §2 Marketplace orders from the Consumer Procurement API (`MarketplaceOrders`)

Spend only shows products that billed in the window. Marketplace **orders** (subscriptions / plans)
are the entitlement-level view: a customer *has* Databricks on a plan even in a month with no usage.

`MarketplaceOrdersService` calls `GET https://cloudcommerceconsumerprocurement.googleapis.com/v1/billingAccounts/{id}/orders`
over REST (no official .NET client) with the service account's own cloud-platform credential, and
`BillingPostureSyncService` runs it for every open billing account right after the nightly posture
scan. Per order we keep display name, product external names (e.g.
`databricks.endpoints.databricks-prod.cloud.goog`), offers, inferred provider, create/update time and
whether any line item is still active. Accounts that fail (API not enabled, no permission) are recorded
in `SyncCursors['marketplace-orders'].Notes` and surfaced as a warning; their previous snapshot is kept.

| Route | Returns |
|---|---|
| `GET /api/billing/marketplace/orders[?customerId=]` | All orders (or one customer's), with per-account errors and scan time |

UI: `MarketplaceOrdersTable` on Spend trends (all accounts) and customer detail (that customer's).

**Setup required (one-off):**

```bash
# Enable the API in the service account's project
gcloud services enable cloudcommerceconsumerprocurement.googleapis.com --project tdsgchannel

# Grant order-list permission on each MASTER billing account (sub-accounts inherit)
for BA in 016E74-10DE75-21330A 01F210-8698CB-C16E8D; do
  gcloud billing accounts add-iam-policy-binding "$BA" \
    --member="serviceAccount:gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com" \
    --role="roles/consumerprocurement.orderViewer"
done
```

If `roles/consumerprocurement.orderViewer` isn't available in your org, create a custom role with
`consumerprocurement.orders.list` and `consumerprocurement.orders.get` and bind that instead.

### What is **not** available by API

- **Google Cloud Ready** badges, **Partner Advantage** tiers / specialisations, "built with Google"
  marks — partner-programme designations with no public API; they only exist in the Partner Directory
  and Marketplace listing pages.
- **ISV** as a field — not exposed anywhere; in practice `transaction_type ≠ GOOGLE` *is* the ISV /
  partner signal, and `seller_name` names the ISV.
- Marketplace **catalogue** metadata (categories, listing badges) — the Producer Portal / Partner APIs
  are vendor-side only.
- The Channel API itself is first-party only (Workspace, GCP, Chrome, Voice); third-party Marketplace
  never appears there, so *entitlement in Channel API* ⇒ native Google by construction.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `GoogleBilling:MarketplaceOrdersEnabled` | `true` | Scan Marketplace orders nightly with the posture scan |

---

## Files touched

| File | Purpose |
|---|---|
| `src/GChannel.Shared/Contracts/Billing.cs` | `BillingAccountMember(s)Result`, `MemberIdentityRequest/Info/Result`, `GoogleAccountKind`, `AdvancedProtectionStatus` |
| `src/GChannel.Shared/Contracts/BillingPosture.cs` | `PostureRules`, `BillingPostureMember/Account/Summary/Finding(s)`, `PostureBucket` (Part 3) |
| `src/GChannel.Shared/Contracts/SellerSpend.cs` | `SellerCategory`, `SellerSpendSummary/Line`, `CustomerSellerSplit`, `MarketplaceOrder(s)` (Part 4) |
| `src/GChannel.Shared/Contracts/ApiRoutes.cs` | `BillingAccountMembers(id)`, `BillingMemberIdentity`, `BillingPosture*` |
| `src/GChannel.ApiService/Services/BillingBudgetsService.cs` | `GetBillingAccountMembersAsync` (Part 1) |
| `src/GChannel.ApiService/Services/WorkspaceUserSecurityService.cs` | Admin SDK Directory + Reports via DWD (Part 2, steps 3–4) |
| `src/GChannel.ApiService/Services/MemberIdentityResolver.cs` | Shared identity/posture resolution used by the API endpoint and the nightly scan (Part 2 orchestration) |
| `src/GChannel.ApiService/Endpoints/BillingEndpoints.cs` | `GET …/members`, `POST …/members/identity` |
| `src/GChannel.ApiService/Endpoints/BillingPostureEndpoints.cs` | `GET /api/billing/posture`, `…/accounts/{id}/members`, `…/findings` (Part 3) |
| `src/GChannel.ApiService/Endpoints/SellerSpendEndpoints.cs` | `GET /api/billing/spend/sellers*`, `…/customers/{id}/spend/sellers`, `…/marketplace/orders` (Part 4) |
| `src/GChannel.ApiService/Services/MarketplaceOrdersService.cs` | Consumer Procurement API REST client (Part 4 §2) |
| `src/GChannel.ApiService/Data/GChannelDbContext.cs` | `BillingPostureAccountRecord`, `BillingPostureMemberRecord`, `CustomerSpendBySellerRecord`, `MarketplaceOrderRecord` |
| `src/GChannel.ApiService/Program.cs` | DI registrations; DDL for `BillingPostureAccounts` / `BillingPostureMembers` |
| `src/GChannel.ApiService/Configuration/GoogleBillingOptions.cs` | `PostureScanEnabled` / `PostureScanHourUtc` / `PostureScanStaleHours` |
| `src/GChannel.ApiService/GChannel.ApiService.csproj` | `Google.Apis.Admin.Directory.directory_v1`, `Google.Apis.Admin.Reports.reports_v1` |
| `src/GChannel.Worker/Services/BillingPostureSyncService.cs` | Nightly scan (Part 3) + Marketplace orders sweep (Part 4 §2) |
| `src/GChannel.Worker/Services/BillingSyncService.cs` | Seller / transaction_type aggregation (Part 4 §1) |
| `src/GChannel.Worker/Program.cs` | DI + hosted service registration |
| `src/GChannel.Web/Services/GChannelApiClient.cs` | `ListBillingAccountMembersAsync`, `ResolveMemberIdentitiesAsync`, `GetBillingPosture*Async` |
| `src/GChannel.Web/Components/Pages/Budgets.razor` | Billing permissions section + security columns |
| `src/GChannel.Web/Components/Pages/BillingPosture.razor` | Security posture page (Part 3) |
| `src/GChannel.Web/Components/Shared/SellerSplitCard.razor`, `MarketplaceOrdersTable.razor` | Part 4 cards used on Spend trends + customer detail |
| `src/GChannel.Web/Components/Pages/SpendTrends.razor`, `CustomerDetail.razor` | Part 4 wiring |
| `src/GChannel.Web/Components/Layout/NavMenu.razor` | Billing → Security posture link |
| `docs/configuration.md` | DWD scope note; posture scan options |
