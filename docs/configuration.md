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

In Azure the client secret lives in Key Vault; locally it is resolved from user-secrets, so the
app code reads `Authentication:Google:ClientSecret` the same way in both environments. The last three
rows are optional — they enable the [background dashboard refresh](#background-dashboard-refresh-optional).

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

## Local secrets

```powershell
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientId" "<client-id>"
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:AccountId" "accounts/C0xxxxxxx"
```
