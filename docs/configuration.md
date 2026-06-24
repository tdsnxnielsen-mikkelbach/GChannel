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

In Azure the client secret lives in Key Vault; locally it is resolved from user-secrets, so the
app code reads `Authentication:Google:ClientSecret` the same way in both environments.

### Optional tuning (defaults shown)

| Setting | Default | Purpose |
| --- | --- | --- |
| `GoogleChannel:CacheSeconds` | `300` | Redis TTL for idempotent reads (catalog, identity checks). |
| `GoogleChannel:MaxRetryAttempts` | `3` | Exponential back-off retries for throttled (429) / transient (503) Channel API calls. Set `0` to disable. |
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

1. Create a service account and a JSON key in Google Cloud.
2. In the Google Workspace Admin console, authorize that service account's client ID for the
   `https://www.googleapis.com/auth/apps.order` scope (domain-wide delegation).
3. Configure the ApiService:

```powershell
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:ServiceAccountKeyPath" "C:\keys\gchannel-sa.json"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:ImpersonateUser" "admin@yourdomain.com"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:BackgroundRefreshSeconds" "600"
```

The refresh stays disabled unless a key, an impersonation user, and a positive interval are all set.
In Azure, supply these to the `apiservice` container app (store the key JSON in Key Vault and reference
it as the `GoogleChannel__ServiceAccountKeyJson` env var, mirroring the OAuth client-secret pattern).

## Local secrets

```powershell
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientId" "<client-id>"
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:AccountId" "accounts/C0xxxxxxx"
```
