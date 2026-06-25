# Running & deploying

## Run locally

```powershell
dotnet run --project src/GChannel.AppHost
```

This starts the Aspire dashboard, spins up SQL Server and Redis containers, and launches both
services. Open the `webfrontend` endpoint from the dashboard.

The SQL and Redis containers use a **persistent lifetime** and named **data volumes**
(`gchannel-sql-data`, `gchannel-redis-data`), so they stay running and keep their data between
debug sessions — the database isn't re-seeded and the cache stays warm on each run. To wipe and
reseed, remove the volumes:

```powershell
docker volume rm gchannel-sql-data gchannel-redis-data
```

## Deploy to Azure

```powershell
azd up
```

`azd` provisions the Container Apps environment, **Azure Key Vault**, the serverless Azure SQL
database (`GP_S_Gen5_2`, auto-pause after 60 min, min capacity 0.5) and Azure Managed Redis
(`Balanced B0`), then deploys both container apps. You will be prompted for `GoogleClientId`,
`GoogleClientSecret` and `GoogleChannelAccountId`; the client secret is written to Key Vault and
the Web app's managed identity is granted access automatically. After the first deploy, add the
Web app's public URL plus `/signin-google` to the authorized redirect URIs of your Google OAuth
client.

### Aspire dashboard in Azure

`azd` provisions the **Container Apps environment with the managed Aspire dashboard enabled
automatically** — there's no switch to flip. It surfaces the same telemetry as the local dashboard
(structured logs, distributed traces, metrics) collected over OTLP from `apiservice` and
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

