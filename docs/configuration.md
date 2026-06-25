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
| `GoogleChannel:AccountId` | ApiService | user-secrets | `GoogleChannelAccountId` azd param (env var) |
| `GoogleChannel:ServiceAccountKeyJson` | ApiService | `Parameters:` (AppHost) | `GoogleChannelServiceAccountKeyJson` azd param → **Key Vault** |
| `GoogleChannel:ImpersonateUser` | ApiService | `Parameters:` (AppHost) | `GoogleChannelImpersonateUser` azd param (env var) |
| `GoogleChannel:BackgroundRefreshSeconds` | ApiService | `Parameters:` (AppHost) | `GoogleChannelBackgroundRefreshSeconds` azd param (env var) |
| `GoogleChannel:PubSubProjectId` | ApiService | `Parameters:` (AppHost) | `GoogleChannelPubSubProjectId` azd param (env var) |
| `GoogleChannel:PubSubSubscriptionId` | ApiService | `Parameters:` (AppHost) | `GoogleChannelPubSubSubscriptionId` azd param (env var) |

In Azure the client secret lives in Key Vault; locally it is resolved from user-secrets, so the
app code reads `Authentication:Google:ClientSecret` the same way in both environments. The
service-account / impersonation / refresh rows are optional — they enable the
[background dashboard refresh](#background-dashboard-refresh-optional); the two `PubSub` rows are
optional too and enable [Pub/Sub notifications](#pubsub-notifications-7).

### Optional tuning (defaults shown)

| Setting | Default | Purpose |
| --- | --- | --- |
| `GoogleChannel:CacheSeconds` | `300` | Redis TTL for idempotent reads (catalog, identity checks). |
| `GoogleChannel:MaxRetryAttempts` | `3` | Retries for throttled (429) / transient (503) Channel API calls. Honours the server's `Retry-After` header when present, otherwise exponential back-off with jitter. Set `0` to disable. |
| `GoogleChannel:MaxRetryDelaySeconds` | `60` | Upper bound (seconds) on a single throttled retry wait, capping a large `Retry-After` so a request can't stall beyond the dashboard time budget. |
| `GoogleChannel:DashboardMaxConcurrency` | `6` | Max concurrent per-customer `entitlements.list` calls when building the dashboard. Lower it if the dashboard reports throttled (429) customers; the Channel API enforces a per-minute request quota. Minimum 1. |
| `GoogleChannel:DashboardRequestsPerMinute` | `60` | Client-side pacing (requests/minute) for the dashboard's `entitlements.list` calls so the aggregation stays under the Channel API's "ListEntitlements requests per minute" quota and avoids 429 storms. Set to match (or just under) your project's quota; `0` disables pacing. |
| `GoogleChannel:DashboardBudgetSeconds` | `45` | Time budget for the on-demand dashboard's per-customer entitlement phase, kept under the 60s HTTP attempt timeout. Roughly `DashboardBudgetSeconds × DashboardRequestsPerMinute / 60` customers are reachable per on-demand request; raise it (with headroom under 60s) to reach more, or enable the background refresh for a complete result. Minimum 5. |
| `GoogleChannel:BackgroundRefreshSeconds` | `0` (off) | Interval for the background worker that recomputes the dashboard summary with a service account and warms the Redis cache. Requires a service account + impersonation user (below). |
| `GoogleChannel:ServiceAccountKeyJson` | _empty_ | Raw JSON of a Google service-account key used by the background refresher. Treat as a secret. |
| `GoogleChannel:ServiceAccountKeyPath` | _empty_ | Alternative to `ServiceAccountKeyJson`: path to a service-account key file. |
| `GoogleChannel:ImpersonateUser` | _empty_ | Reseller admin email the service account impersonates via domain-wide delegation (required for the background refresh). |
| `GoogleChannel:PubSubProjectId` | _empty_ | Google Cloud project id that hosts the Pub/Sub **subscription** for Channel notifications (your own project). Required to run the notification subscriber. |
| `GoogleChannel:PubSubSubscriptionId` | _empty_ | Pub/Sub subscription id (within `PubSubProjectId`) the background subscriber pulls Channel events from. Blank disables the subscriber. |
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
(Pub/Sub load-balances delivery across replicas; the API just needs `min-replicas ≥ 1`). The subscriber
reuses the **same service-account key** as the background dashboard refresh (Pub/Sub uses the key
directly — no domain-wide delegation), so on Azure the key is read from Key Vault via managed identity
and locally from user-secrets. The same `BackgroundService` runs identically under local F5.

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
   a project id, a subscription id, **and** a service-account key are all present.

> IAM cheat-sheet: the SA's access to Google's **topic** is granted by `accounts.register`; you create
> the **subscription** in your own project; the `roles/pubsub.subscriber` binding goes on that
> **subscription** (you don't own the topic, so it can't go there).

| AppHost parameter (`azd env set` name) | Maps to env var → config key | Secret |
| --- | --- | --- |
| `GoogleChannelPubSubProjectId` | `GoogleChannel__PubSubProjectId` → `GoogleChannel:PubSubProjectId` | No |
| `GoogleChannelPubSubSubscriptionId` | `GoogleChannel__PubSubSubscriptionId` → `GoogleChannel:PubSubSubscriptionId` | No |

These two parameters default to empty in the AppHost configuration, so they **don't prompt** on `azd up`
or local F5 — set them only when you want eventing on. The service-account key reuses the existing
`GoogleChannelServiceAccountKeyJson` parameter (and Key Vault secret) from the background-refresh setup.

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

## Local secrets

```powershell
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientId" "<client-id>"
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:AccountId" "accounts/C0xxxxxxx"
```
