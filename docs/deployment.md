# Running & deploying

## Run locally

```powershell
dotnet run --project src/GChannel.AppHost
```

This starts the Aspire dashboard, spins up SQL Server and Redis containers, and launches both
services. Open the `webfrontend` endpoint from the dashboard.

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
