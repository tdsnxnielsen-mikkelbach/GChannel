# GChannel

A .NET 10 / Blazor dashboard for Google distributors and resellers. Users sign in with their
Google account and the UI abstracts the **Google Cloud Channel API** behind point-and-click
actions — they never see the underlying REST calls.

Built with **.NET Aspire**: the front end and back-end services run as two independently
scalable **Azure Container Apps**, backed by **Azure SQL (serverless)** and **Azure Managed
Redis**. Deployment is handled by the **Azure Developer CLI (`azd`)**.

## Quick start

```powershell
# 1. set local secrets (see docs/configuration.md)
# 2. run the whole stack via Aspire
dotnet run --project src/GChannel.AppHost
```

Open the `webfrontend` endpoint from the Aspire dashboard.

## Documentation

| Topic | Doc |
| --- | --- |
| Using the app (new-user guide) | [docs/UI.md](docs/UI.md) |
| Architecture, container apps & secrets | [docs/architecture.md](docs/architecture.md) |
| Prerequisites & configuration | [docs/configuration.md](docs/configuration.md) |
| Running locally & deploying to Azure | [docs/deployment.md](docs/deployment.md) |
| Implemented API surface | [docs/api-surface.md](docs/api-surface.md) |
| Roadmap & future work | [docs/todo.md](docs/todo.md) |

## Solution layout

```
src/
  GChannel.AppHost          # Aspire orchestrator
  GChannel.ServiceDefaults  # OpenTelemetry, health checks, resilience
  GChannel.Shared           # DTOs/contracts shared by Web + API
  GChannel.ApiService       # Web API + Google Channel client (internal ingress)
  GChannel.Web              # Blazor UI, Google login (external ingress)
```
