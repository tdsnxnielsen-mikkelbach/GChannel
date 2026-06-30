# Prerequisites & configuration

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Docker Desktop](https://www.docker.com/) (local SQL Server + Redis containers)
- [Azure Developer CLI (`azd`)](https://aka.ms/azd)
- A Google Cloud project with the **Cloud Channel API** enabled and an **OAuth 2.0 Client**
  (Web application). Authorized redirect URI for local dev:
  `http://localhost:<web-port>/signin-google`.
- A Cloud Channel **reseller account id** (`accounts/C0xxxxxxx`).

## Configuration

| Setting | Project | Local dev | Azure |
| --- | --- | --- | --- |
| `Authentication:Google:ClientId` | Web | user-secrets | `GoogleClientId` azd param (env var) |
| `Authentication:Google:ClientSecret` | Web | user-secrets | `GoogleClientSecret` azd param → **Key Vault** |
| `GoogleChannel:AccountId` | ApiService + Worker | user-secrets | `GoogleChannelAccountId` azd param (env var) |
| `GoogleChannel:ServiceAccountKeyJson` | Worker | `Parameters:` (AppHost) | `GoogleChannelServiceAccountKeyJson` azd param → **Key Vault** |
| `GoogleChannel:ImpersonateUser` | Worker | `Parameters:` (AppHost) | `GoogleChannelImpersonateUser` azd param (env var) |
| `GoogleChannel:BackgroundRefreshSeconds` | Worker | `Parameters:` (AppHost) | `GoogleChannelBackgroundRefreshSeconds` azd param (env var) |
| `GoogleChannel:PubSubProjectId` | Worker | `Parameters:` (AppHost) | `GoogleChannelPubSubProjectId` azd param (env var) |
| `GoogleChannel:PubSubSubscriptionId` | Worker | `Parameters:` (AppHost) | `GoogleChannelPubSubSubscriptionId` azd param (env var) |

In Azure the client secret lives in Key Vault; locally it is resolved from user-secrets, so the
app code reads `Authentication:Google:ClientSecret` the same way in both environments. The
service-account / impersonation / refresh rows are optional — they enable the
[background dashboard refresh](#background-dashboard-refresh-optional) and run in the separate
**GChannel.Worker** container app; the two `PubSub` rows are
optional too and enable [Pub/Sub notifications](#pubsub-notifications-7).

### Optional tuning (defaults shown)

| Setting | Default | Purpose |
| --- | --- | --- |
| `GoogleChannel:CacheSeconds` | `300` | Redis TTL for idempotent reads (catalog, identity checks). |
| `GoogleChannel:MaxRetryAttempts` | `3` | Retries for throttled (429) / transient (503) Channel API calls. Honours the server's `Retry-After` header when present, otherwise exponential back-off with jitter. Set `0` to disable. |
| `GoogleChannel:MaxRetryDelaySeconds` | `60` | Upper bound (seconds) on a single throttled retry wait, capping a large `Retry-After` so a request can't stall beyond the dashboard time budget. |
| `GoogleChannel:DashboardMaxConcurrency` | `6` | Max concurrent per-customer `entitlements.list` calls when building the dashboard. Lower it if the dashboard reports throttled (429) customers; the Channel API enforces a per-minute request quota. Minimum 1. |
| `GoogleChannel:DashboardRequestsPerMinute` | `20` | Client-side pacing (requests/minute) for the dashboard's `entitlements.list` calls so the aggregation stays under the Channel API's "ListEntitlements requests per minute" quota (commonly **24/min**) and avoids 429 storms. The default leaves a little headroom; set to match (or just under) your project's quota. `0` disables pacing. |
| `GoogleChannel:DashboardCustomerListRequestsPerMinute` | `20` | Client-side pacing (requests/minute) for the dashboard's customer-list calls — both the account-level `accounts.customers.list` and the per-reseller `channelPartnerLinks.customers.list` fan-out behind the indirect (reseller-owned) customer estate. Both draw on the same shared "ListCustomers requests per minute" quota (commonly **24/min**), so they are paced together through one bucket. The indirect fan-out (one call per ACTIVE channel partner link) runs only in the background refresh — never on the budgeted on-demand request — so the on-demand dashboard stays fast and the estate figure is served from the background-warmed cache. `0` disables pacing. |
| `GoogleChannel:DashboardBudgetSeconds` | `45` | Time budget for the on-demand dashboard's per-customer entitlement phase, kept under the 60s HTTP attempt timeout. Roughly `DashboardBudgetSeconds × DashboardRequestsPerMinute / 60` customers are reachable per on-demand request; raise it (with headroom under 60s) to reach more, or enable the background refresh for a complete result. Minimum 5. |
| `GoogleChannel:BackgroundRefreshSeconds` | `0` (off) | Interval for the background worker that recomputes the dashboard summary with a service account and warms the Redis cache. Requires a service account + impersonation user (below). |
| `GoogleChannel:UseReadModel` | `false` | Enables the §10 durable SQL read-model: the dashboard indirect estate, the customers/entitlement list pages and the estate value rollup are served from SQL (kept fresh by the incremental sync worker) instead of live Channel API fan-outs. Falls back to the live path before the first sync, so it is safe to toggle. |
| `GoogleChannel:ReadModelLinksPerCycle` | `18` | How many of the **stalest** ACTIVE channel-partner links the sync worker fans out per cycle (`channelPartnerLinks.customers.list`) to refresh their indirect customer roster + `CustomerCount`. Sized to the `ListCustomers` per-minute quota so each cycle stays within budget; the whole estate is covered over several cycles. |
| `GoogleChannel:ReadModelCustomersPerCycle` | `60` | How many of the **stalest** customers (direct **and** indirect) the worker syncs entitlements for per cycle, in one unified staleness-rotated pass. This is the only consumer of the contended `ListEntitlements` quota in the sync worker, kept separate from the cheap metadata/link fan-out so the indirect estate and per-link counts populate without waiting on entitlement quota. Raise it if your `ListEntitlements` quota allows; lower it under heavy 429s. |
| `GoogleChannel:ServiceAccountKeyJson` | _empty_ | Raw JSON of a Google service-account key used by the background refresher. Treat as a secret. |
| `GoogleChannel:ServiceAccountKeyPath` | _empty_ | Alternative to `ServiceAccountKeyJson`: path to a service-account key file. |
| `GoogleChannel:ImpersonateUser` | _empty_ | Reseller admin email the service account impersonates via domain-wide delegation (required for the background refresh). |
| `GoogleChannel:PubSubProjectId` | _empty_ | Google Cloud project id that hosts the Pub/Sub **subscription** for Channel notifications (your own project). Required to run the notification subscriber. |
| `GoogleChannel:PubSubSubscriptionId` | _empty_ | Pub/Sub subscription id (within `PubSubProjectId`) the background subscriber pulls Channel events from. Blank disables the subscriber. |
| `GoogleChannel:WorkloadIdentityCredentialJson` | _empty_ | Workload Identity Federation credential config (`external_account` JSON) for **key-less** Pub/Sub auth. When set, takes precedence over the service-account key for the subscriber. Not a secret (no private key). Does not apply to the dashboard refresh (domain-wide delegation needs a key). |
| `GoogleChannel:WorkloadIdentityCredentialPath` | _empty_ | Alternative to `WorkloadIdentityCredentialJson`: path to the WIF credential config file. |
| `GoogleChannel:PubSubMaxNotifications` | `200` | Maximum recent notifications retained in the rolling Redis feed (`channel:notifications`). Minimum 1. |

### Background dashboard refresh (optional)

The dashboard summary is a slow N+1 aggregation. By default it is computed on demand from the
signed-in user's token (with a server-side time budget so it always returns within the HTTP timeout).
For large estates you can instead keep it pre-computed: a hosted worker recomputes it on an interval
and warms the Redis cache, so the page serves an instant, complete result.

Because the Channel API has no service-account identity of its own, the worker authenticates with a
service account configured for **domain-wide delegation** that impersonates a reseller admin:

1. Create a service account and a JSON key in Google Cloud (enable **domain-wide delegation** and note
   its **Client ID**).
2. In the Google Workspace Admin console, authorize that service account's client ID for the
   `https://www.googleapis.com/auth/apps.order` scope (domain-wide delegation).
3. Supply the values. The AppHost exposes them as parameters, so they flow the same way as the other
   Google settings: the `Parameters:` configuration section locally, and `azd env set` for deploys.

| AppHost parameter (`azd env set` name) | Maps to env var → config key | Secret |
| --- | --- | --- |
| `GoogleChannelServiceAccountKeyJson` | `GoogleChannel__ServiceAccountKeyJson` → `GoogleChannel:ServiceAccountKeyJson` | **Yes** → Key Vault |
| `GoogleChannelImpersonateUser` | `GoogleChannel__ImpersonateUser` → `GoogleChannel:ImpersonateUser` | No |
| `GoogleChannelBackgroundRefreshSeconds` | `GoogleChannel__BackgroundRefreshSeconds` → `GoogleChannel:BackgroundRefreshSeconds` | No |

**Local (running via the AppHost)** — set under the `Parameters:` section of the **AppHost** project:

```powershell
$saKey = Get-Content -Raw -Path "C:\keys\gchannel-sa.json"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelServiceAccountKeyJson" "$saKey"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelImpersonateUser" "admin@yourdomain.com"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelBackgroundRefreshSeconds" "600"
```

**Deploy (`azd`)** — set them in the azd environment, then `azd up`/`azd provision`:

```powershell
$saKey = Get-Content -Raw -Path "C:\keys\gchannel-sa.json"
azd env set GoogleChannelServiceAccountKeyJson "$saKey"
azd env set GoogleChannelImpersonateUser "admin@yourdomain.com"
azd env set GoogleChannelBackgroundRefreshSeconds "600"
azd up
```

On deploy the secret key is stored in **Key Vault** (`google-channel-sa-key`) and surfaced to the
`apiservice` container app as a Key Vault reference (resolved via its managed identity), mirroring the
OAuth client-secret pattern — the literal JSON never appears in the manifest or app configuration.

The refresh stays disabled unless a key, an impersonation user, and a positive interval are all set.
Since these are plain azd parameters, `azd up` prompts for any you haven't set; pass empty / `0` to keep
the feature off without prompting (`azd env set GoogleChannelBackgroundRefreshSeconds "0"`).

### Pub/Sub notifications (§7)

Channel change events (entitlement/customer) are delivered through **Google Cloud Pub/Sub** — there is
**no Azure messaging** involved. Google publishes to a Google-owned topic; you grant a service account
subscriber access with `accounts.register` (done from the in-app **Notifications** page), then create a
**subscription** to that topic in your own Google Cloud project and point the app at it. A
`BackgroundService` inside the **existing** `apiservice` container app pulls that subscription and
records events into a capped Redis feed — **no extra container** and **no distributed lock** are needed
(Pub/Sub load-balances delivery across replicas; the API just needs `min-replicas ≥ 1`). Because
Pub/Sub needs **no domain-wide delegation**, this path can authenticate **key-less** with **Workload
Identity Federation** (the recommended approach): the Azure managed identity mints short-lived federated
Google tokens, so no service-account key is downloaded or stored. If WIF is not configured the subscriber
falls back to the **same service-account key** as the background dashboard refresh (read from Key Vault
via managed identity on Azure, from user-secrets locally). The same `BackgroundService` runs identically
under local F5.

One-time Google setup:

The topic is **owned by Google** (created by `accounts.register`); the **subscription is owned by you**
(created in your project). The service account is the identity that bridges them.

**Install the gcloud CLI** (one-time). On Windows the simplest options are:

```powershell
# Option A — winget
winget install --id Google.CloudSDK -e

# Option B — Chocolatey
choco install gcloudsdk
```

Or download the installer from <https://cloud.google.com/sdk/docs/install>. Then authenticate and
enable Pub/Sub (run in a new shell so `gcloud` is on `PATH`):

```powershell
gcloud auth login
gcloud config set project tdsgchannel
gcloud services enable pubsub.googleapis.com
```

1. **Register the topic** from the app's **Notifications** page (calls `accounts.register` with the
   subscriber service account). Note the **topic** it returns, e.g.
   `projects/cloudchannel-notifications-prod/topics/C0xxxxxxx-...`.
2. **Create the subscription** in your project against that topic, impersonating the service account
   (only the SA was granted access to Google's topic):

   ```powershell
   gcloud pubsub subscriptions create gchannel-notifications-sub `
     --topic="projects/cloudchannel-notifications-prod/topics/C0xxxxxxx-..." `
     --impersonate-service-account="gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com"
   ```

   If impersonation is blocked, run the command authenticated **as** the SA key instead
   (`gcloud auth activate-service-account --key-file=./sa-key.json` then add `--project=tdsgchannel`).
3. **Grant the SA Pub/Sub Subscriber** on the subscription so the app can pull from it:

   ```powershell
   gcloud pubsub subscriptions add-iam-policy-binding gchannel-notifications-sub `
     --project=tdsgchannel `
     --member="serviceAccount:gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com" `
     --role="roles/pubsub.subscriber"
   ```
4. **Point the app at the subscription** with the parameters below. The subscriber stays disabled until
   a project id, a subscription id, **and** a credential (a WIF config **or** a service-account key) are
   all present.

> IAM cheat-sheet: the SA's access to Google's **topic** is granted by `accounts.register`; you create
> the **subscription** in your own project; the `roles/pubsub.subscriber` binding goes on that
> **subscription** (you don't own the topic, so it can't go there).

##### Key-less auth with Workload Identity Federation (recommended)

Instead of downloading a service-account key, let the **Azure managed identity** of the `apiservice`
container app authenticate to Google. Google's warning on the SA *Keys* page is exactly about avoiding
downloaded keys; WIF removes the key for this path entirely.

> **Provision first.** The Azure managed identity doesn't exist until `azd` has created the Container
> Apps infrastructure, and the WIF binding below needs that identity's **object (principal) id**. So the
> bootstrap order is: **(1)** run `azd up` once *without* the WIF parameter (the subscriber runs on the
> service-account key, or stays disabled if Pub/Sub isn't configured); **(2)** read the identity's object
> id; **(3)** do the GCP setup below; **(4)** `azd env set GoogleChannelWorkloadIdentityCredentialJson`
> and `azd up` again (a config update, not a re-provision). Until you do WIF, deployment behaves exactly
> as before — it keeps using the service-account key.
>
> Get the object id of the user-assigned identity your container apps already use (the same one that
> reads Key Vault):
>
> ```powershell
> az identity list -g <your-resource-group> --query "[].{name:name, principalId:principalId, clientId:clientId}" -o table
> ```
>
> The `principalId` is the `<managed-identity-object-id>` used in the `workloadIdentityUser` binding
> below; an Azure managed-identity token's `sub` claim equals that object id. Your Entra **tenant id**
> (needed in `--issuer-uri`) is known up front — only the binding needs the post-provision object id.

One-time GCP setup (the SA still needs `roles/pubsub.subscriber` from step 3 above):

```powershell
# 1. Create a workload identity pool + an Azure (OIDC) provider that trusts your Entra tenant.
gcloud iam workload-identity-pools create gchannel-pool --location=global --project=tdsgchannel
gcloud iam workload-identity-pools providers create-oidc azure `
  --location=global --workload-identity-pool=gchannel-pool --project=tdsgchannel `
  --issuer-uri="https://login.microsoftonline.com/<entra-tenant-id>/v2.0" `
  --allowed-audiences="api://AzureADTokenExchange" `
  --attribute-mapping="google.subject=assertion.sub"

# 2. Let the federated identity (the ACA managed identity's object id) impersonate the SA.
gcloud iam service-accounts add-iam-policy-binding `
  gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com --project=tdsgchannel `
  --role=roles/iam.workloadIdentityUser `
  --member="principal://iam.googleapis.com/projects/<project-number>/locations/global/workloadIdentityPools/gchannel-pool/subject/<managed-identity-object-id>"

# 3. Generate the external_account credential config (holds NO key — safe to store as plain config).
gcloud iam workload-identity-pools create-cred-config `
  projects/<project-number>/locations/global/workloadIdentityPools/gchannel-pool/providers/azure `
  --service-account=gchannel-dashboard@tdsgchannel.iam.gserviceaccount.com `
  --credential-source-url="http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=api://AzureADTokenExchange" `
  --credential-source-headers="Metadata=true" `
  --output-file=wif-credential.json
```

Then pass the contents of `wif-credential.json` to `GoogleChannelWorkloadIdentityCredentialJson`
(below). It is **not a secret** — it contains no private key, only the pool/provider/SA URLs and the
Azure IMDS endpoint to fetch the managed-identity token from — so it is a plain parameter, not a Key
Vault secret. When set, it **takes precedence** over any service-account key for the Pub/Sub subscriber.

> Domain-wide delegation note: WIF cannot replace the key for the **background dashboard refresh**,
> which impersonates a reseller admin user (the .NET Google auth library only supports domain-wide
> delegation on downloaded service-account keys). That path keeps using
> `GoogleChannelServiceAccountKeyJson`.

| AppHost parameter (`azd env set` name) | Maps to env var → config key | Secret |
| --- | --- | --- |
| `GoogleChannelPubSubProjectId` | `GoogleChannel__PubSubProjectId` → `GoogleChannel:PubSubProjectId` | No |
| `GoogleChannelPubSubSubscriptionId` | `GoogleChannel__PubSubSubscriptionId` → `GoogleChannel:PubSubSubscriptionId` | No |
| `GoogleChannelWorkloadIdentityCredentialJson` | `GoogleChannel__WorkloadIdentityCredentialJson` → `GoogleChannel:WorkloadIdentityCredentialJson` | No |

These parameters default to empty in the AppHost configuration, so they **don't prompt** on `azd up`
or local F5 — set them only when you want eventing on. If you leave the WIF parameter empty, the
subscriber falls back to the existing `GoogleChannelServiceAccountKeyJson` parameter (and Key Vault
secret) from the background-refresh setup.

**Local (running via the AppHost):**

```powershell
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelPubSubProjectId" "tdsgchannel"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelPubSubSubscriptionId" "gchannel-notifications-sub"
```

**Deploy (`azd`):**

```powershell
azd env set GoogleChannelPubSubProjectId "tdsgchannel"
azd env set GoogleChannelPubSubSubscriptionId "gchannel-notifications-sub"
azd up
```

The **Operations** page (`/operations`) needs no configuration — it tracks long-running operation names
returned by mutating calls via `operations.get`/`cancel` using the signed-in user's token. (The Cloud
Channel API does **not** implement `operations.list` — it returns HTTP 501 — so there is no global
operation list; operations are tracked individually by the name a mutation returns.)

#### Notifications feed behaviour & troubleshooting

- **The feed is durable.** Each received event is stored in a capped Redis list
  (`channel:notifications`, newest first, max `PubSubMaxNotifications`/200). The local Redis container
  uses a persistent volume + snapshotting, so the feed **survives app and container restarts** — you
  don't need to keep events flowing to keep history. The **Refresh** button only re-reads this stored
  list, so it appears to "do nothing" when the list is genuinely empty.
- **Customer names** are resolved in the UI (the Notifications page looks up each `customerId` via the
  customers API and shows the org display name, with the raw id underneath). The event payload itself
  only carries ids.
- **Don't `pull --auto-ack` to test.** Pub/Sub delivers each message to **exactly one** consumer and
  drops it once acked, so `gcloud pubsub subscriptions pull … --auto-ack` **steals** events from the
  app — they will never reach the feed. To peek without consuming, omit `--auto-ack` (the message
  redelivers) or just let the app drain the subscription and watch the in-app feed.
- **Payload shape.** Google emits **snake_case** JSON, e.g.
  `{"entitlement_event":{"event_type":"LICENSE_ASSIGNMENT_CHANGED","entitlement":"accounts/…/customers/…/entitlements/…"}}`,
  with attributes `subscriber_event_type=ENTITLEMENT_EVENT|CUSTOMER_EVENT` and `event_type=…`. The
  subscriber parses both the snake_case body and these attributes to correlate the customer/entitlement.
- **Replaying already-acked events.** Pub/Sub does **not** retain acked messages by default. To make
  events replayable, enable retention on the subscription and then `seek` back in time:

  ```bash
  gcloud pubsub subscriptions update gchannel-notifications-sub \
    --project=tdsgchannel --retain-acked-messages --message-retention-duration=7d
  # later, to replay everything from the last hour into the app:
  gcloud pubsub subscriptions seek gchannel-notifications-sub \
    --project=tdsgchannel --time=$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)
  ```

  Events acked **before** retention was enabled are gone for good; trigger a fresh change (e.g. a
  license-assignment change) to populate the feed.



```powershell
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientId" "<client-id>"
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:AccountId" "accounts/C0xxxxxxx"
```
