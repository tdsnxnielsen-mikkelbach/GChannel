# Running & deploying

## Run locally

```powershell
dotnet run --project src/GChannel.AppHost
```

This starts the Aspire dashboard, spins up the PostgreSQL and Redis containers, provisions (or reuses)
a **real Azure Service Bus namespace** for you, and launches all three services (API, worker, web). Open
the `webfrontend` endpoint from the dashboard.

There is deliberately **no Service Bus emulator**: messaging runs against Azure in every environment.
Aspire's local Azure provisioning creates the namespace, topic, subscriptions and queues declared in the
AppHost in your own subscription (in a dev resource group, separate from the `azd` environment) and
grants your signed-in identity the *Azure Service Bus Data Owner* role. Sign in once (`az login`) and
tell Aspire where to provision:

```powershell
cd src/GChannel.AppHost
dotnet user-secrets set Azure:SubscriptionId "<subscription-id>"
dotnet user-secrets set Azure:Location "westeurope"
dotnet user-secrets set Azure:ResourceGroup "rg-gchannel-dev-<you>"   # optional; default rg-aspire-<apphost>-<suffix>
```

(or answer the **Missing subscription configuration** prompt the dashboard shows on first run). Until this
is configured the `messaging` resources wait in that state and the API/worker, which `WaitFor` them, don't
start. The namespace is reused on every subsequent run; delete the dev resource group when you no longer
need it.

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

### Pre-generated Wolverine code

All three container apps use the Container Apps default of 0.5 vCPU / 1 GiB. In Production the API and
the worker load **pre-generated** Wolverine handler and HTTP-endpoint code (`TypeLoadMode.Static`) instead
of compiling it with Roslyn on first use. Runtime compilation pushed the API past 1 GiB within a few
page loads (OOM-killed with exit 137, restarting in a loop); with pre-generated code it stays below about 300 MiB.

- The `predeploy` hook in `azure.yaml` runs `codegen write` for both projects (Production environment,
  ~20 s each, no connection strings needed). It writes the code to `Internal/Generated/` before azd builds
  the images, so every `azd deploy` / `azd up` ships code that matches the source.
- `Internal/Generated/` is git-ignored and compiled **only into Release builds** (what azd publishes). Debug
  builds (local runs, tests) keep compiling at runtime, so leftover generated files never go stale there.
- If a handler or endpoint type is missing, the host fails at startup rather than falling back to
  runtime compilation. A container that won't start after a deploy therefore points at a skipped or failed codegen
  step.
- To inspect or regenerate the code by hand, run this from `src/GChannel.ApiService` or `src/GChannel.Worker`
  with `$env:DOTNET_ENVIRONMENT = 'Production'` (and `ASPNETCORE_ENVIRONMENT` for the API):
  `dotnet run --no-launch-profile -- codegen preview` or `-- codegen write`.

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

