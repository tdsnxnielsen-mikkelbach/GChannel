# Running & deploying

## Run locally

```powershell
dotnet run --project src/GChannel.AppHost
```

This starts the Aspire dashboard, spins up the PostgreSQL, Redis and Azure Service Bus emulator
containers (the emulator also starts a SQL Server companion container; the first run pulls that ~2 GB
image), and launches all three services (API, worker, web). Open the `webfrontend` endpoint from the
dashboard.

The PostgreSQL and Redis containers use a **persistent lifetime** and named **data volumes**
(`gchannel-pg-data`, `gchannel-redis-data`), so they stay running and keep their data between
debug sessions — the estate isn't re-synced from Google and the cache stays warm on each run. To wipe
and resync, remove the volumes:

```powershell
docker volume rm gchannel-pg-data gchannel-redis-data
```

Every AppHost parameter needs a value before the services start (the dashboard prompts for any that
are missing). Besides the Google parameters in user secrets, the pacing knobs
`GoogleChannelDashboardRequestsPerMinute` / `GoogleChannelDashboardCustomerListRequestsPerMinute` and the
secret `IonGatewayApiKey` are not defaulted in `appsettings.json`; set them once with
`dotnet user-secrets set Parameters:<name> <value> --project src/GChannel.AppHost` (any placeholder works
for the Ion key while `IonGatewayEnabled` is `false`).

## Deploy to Azure

```powershell
azd up
```

> **First time / a new country or region?** Follow the end-to-end
> [regional deployment guide](regional-deployment.md) (Google Cloud console + Workspace Admin +
> Azure environment + parameters) before running `azd up`.

`azd` provisions the Container Apps environment, **Azure Key Vault**, the **Azure Database for
PostgreSQL flexible server** (`Standard_B1ms` Burstable, PostgreSQL 16, 32 GiB, Entra-only
authentication — no database password exists), the **Azure Service Bus** namespace (Standard tier,
local auth disabled, with the `estate-events` topic/subscriptions and the `estate-sync`, `jobs` and
`operations` queues) and Azure Managed Redis (`Balanced B0`), then deploys all three container apps. The
container apps' managed identities are granted the PostgreSQL Entra administrator / Service Bus Data Owner
/ Redis data roles by the generated Bicep. Note that the Burstable Postgres server is always on (no
auto-pause), unlike the serverless SQL database it replaces. You will be prompted for `GoogleClientId`,
`GoogleClientSecret` and `GoogleChannelAccountId`; the client secret is written to Key Vault and
the Web app's managed identity is granted access automatically. The worker is deployed with a fixed
single replica (min = max = 1, no ingress); the API and web scale independently. After the first deploy, add the
Web app's public URL plus `/signin-google` to the authorized redirect URIs of your Google OAuth
client.

### Aspire dashboard in Azure

`azd` provisions the **Container Apps environment with the managed Aspire dashboard enabled
automatically** — there's no switch to flip. It surfaces the same telemetry as the local dashboard
(structured logs, distributed traces, metrics) collected over OTLP from `apiservice`, `worker` and
`webfrontend` (wired by `AddServiceDefaults()`). Its URL appears in the `azd up` output and on the
Container Apps **environment** resource in the portal.

A few notes:

- **Access is secured by Microsoft Entra ID.** The deploying identity gets in by default; other
  users must be granted access to the Container Apps environment first. To let a teammate in, assign
  their account a role on the environment (resource group → the Container Apps **environment** →
  **Access control (IAM)**), e.g.:

  ```powershell
  az role assignment create `
    --assignee "<user-object-id-or-upn>" `
    --role "Reader" `
    --scope "/subscriptions/<sub>/resourceGroups/rg-<env>/providers/Microsoft.App/managedEnvironments/<env-name>"
  ```

- **Telemetry is ephemeral / in-memory** — the dashboard holds only recent data and resets when it
  restarts; it is not a long-term store. For retained logs and metrics use the **Log Analytics**
  workspace `azd` also provisions (add Application Insights if you want APM).
- The managed dashboard is a **preview** feature, so its behaviour and limits may change.

