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
progressively. Server-side, each chunk resolves its members **8 at a time** (bounded fan-out), and
for each user the audit and usage Reports calls run concurrently; a 65-member master policy
therefore completes in a few seconds instead of the minutes a sequential pass would take (which
had been tripping the 60 s HTTP attempt timeout and leaving most rows as `?`).

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
   `users.get(email)` with `fields=primaryEmail,isEnrolledIn2Sv,isEnforcedIn2Sv,suspended,isAdmin`.

   - **200** → the user is in the reseller's tenant → **Managed**, and we now have:
     - `TwoStepVerificationEnrolled` ← `isEnrolledIn2Sv`
     - `TwoStepVerificationEnforced` ← `isEnforcedIn2Sv`
     - `Suspended`, `IsAdmin`

     Then the **Reports API** is queried for Advanced Protection (step 4).
   - **404** → not in our tenant. Fall through to step 5.
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
   security key, so the counts let the UI resolve the `Unknown` case further:

   | Audit event | Usage report | Shown as |
   |---|---|---|
   | none retained | 0 security keys **and** 0 passkeys | `Not enrolled · no keys` — safe inference, since enrollment is impossible without one |
   | none retained | ≥ 1 key or passkey | `No recent event · N keys, M passkeys` — indeterminate; the user *may* be enrolled |
   | none retained | not readable (scope missing / data lag) | `No recent event` |
   | `titanium_enroll` / `titanium_unenroll` | any | the event wins (`Enrolled` / `Not enrolled`); counts appear in the tooltip only |

   The event always takes precedence because usage data lags a few days: a user who enrolled
   yesterday has an audit event today but may still report 0 keys for that date.

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
   `MemberIdentityResult { Members[], DirectoryAvailable, DirectoryError }`.

### 2.4 What the UI shows

[src/GChannel.Web/Components/Pages/Budgets.razor](../src/GChannel.Web/Components/Pages/Budgets.razor)
renders both member tables from one shared template with these columns:

| Column | Source | Values |
|---|---|---|
| Member | IAM policy (+ step 3/6) | email with a type icon (user / group / service account / domain); `admin` badge when `IsAdmin`, red `suspended` badge when the Workspace account is suspended but still holds the billing role |
| Role | IAM policy | friendly role name |
| Account | step 2 / 3 / 5 / 6 | `Managed` (green) · `Managed (team)` (amber) · `Consumer` (red) · `Unknown`. Tooltip explains which signal decided it, including the resold customer id when reseller access was used. |
| 2SV | step 3 / 6 | `On · enforced` · `On` · `Off` for our tenant and resold customers with reseller access; `n/a` for everyone else |
| Adv. Protection | step 4 / 6 | `Enrolled` · `Not enrolled` (audit event, date in tooltip) · `Not enrolled · no keys` (no event, but usage report confirms 0 passkeys and 0 security keys) · `No recent event · N keys, M passkeys` (indeterminate) · `Unavailable` (red, API error in tooltip); `—` for users whose tenant we can't read |

Service-account and domain members get `—` in the security columns — they are not Google user
accounts. If the Admin SDK side isn't configured or is denied, an info banner explains that
account type still works but 2SV / Advanced Protection are not readable.

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

## Files touched

| File | Purpose |
|---|---|
| `src/GChannel.Shared/Contracts/Billing.cs` | `BillingAccountMember(s)Result`, `MemberIdentityRequest/Info/Result`, `GoogleAccountKind`, `AdvancedProtectionStatus` |
| `src/GChannel.Shared/Contracts/ApiRoutes.cs` | `BillingAccountMembers(id)`, `BillingMemberIdentity` |
| `src/GChannel.ApiService/Services/BillingBudgetsService.cs` | `GetBillingAccountMembersAsync` (Part 1) |
| `src/GChannel.ApiService/Services/WorkspaceUserSecurityService.cs` | Admin SDK Directory + Reports via DWD (Part 2, steps 3–4) |
| `src/GChannel.ApiService/Endpoints/BillingEndpoints.cs` | `GET …/members`, `POST …/members/identity` (Part 2 orchestration) |
| `src/GChannel.ApiService/Program.cs` | DI registration of `IWorkspaceUserSecurityService` |
| `src/GChannel.ApiService/GChannel.ApiService.csproj` | `Google.Apis.Admin.Directory.directory_v1`, `Google.Apis.Admin.Reports.reports_v1` |
| `src/GChannel.Web/Services/GChannelApiClient.cs` | `ListBillingAccountMembersAsync`, `ResolveMemberIdentitiesAsync` |
| `src/GChannel.Web/Components/Pages/Budgets.razor` | Billing permissions section + security columns |
| `docs/configuration.md` | DWD scope note |
