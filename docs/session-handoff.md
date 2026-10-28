# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-10-28 · **branch:** `feature/critter-stack-blazorise` (22 commits ahead of `main`,
**never pushed** — `origin` only has `main`) · **head:** the commit "Add session-handoff convention and today's handoff" (`git log -1`) · working tree clean.

## Current state

### Code (this branch)

- **Backend = Critter Stack**: Marten (event store + inline-snapshot read models, PostgreSQL) and
  Wolverine (handlers, saga, recurring job ticks, Pub/Sub→Service Bus bridge) replace EF Core / Azure SQL
  / Redis-lock `BackgroundService`s. API routes are unchanged (Wolverine.HTTP). See
  [architecture.md](architecture.md) "Persistence & messaging".
- **UI = Radzen.Blazor 12.1.8** (MIT, no licence banner) — MudBlazor → Blazorise → Radzen this session.
  Shared helpers in `src/GChannel.Web/Components/Shared/` (`Tip`, `LinkButton`, `DetailRow`, `SearchBox`,
  `ImportCustomerDialog`, `ProvisionCloudIdentityDialog`, `BillingMembersGrid`); `UiNotifier` keeps the
  `Snackbar.Add` / `ConfirmAsync` façade the pages use. Driver.js tours rely on `data-onboarding` /
  `data-walkthrough` attributes — keep them when editing layout/pages.
- **Aspire 13.6.4** + all packages current (`dotnet list package --outdated` clean except
  `Microsoft.OpenApi` pinned to 2.x on purpose). No Service Bus emulator anywhere: local runs use Aspire
  run-mode Azure provisioning (`Azure:SubscriptionId` / `Azure:Location` / `Azure:ResourceGroup` in AppHost
  user secrets). Key Vault is publish-mode only.
- Quality gates: `dotnet build GChannel.slnx` → 0 errors (38 pre-existing xUnit/nullable warnings in
  tests); `dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj` → 14/14 (Testcontainers
  PostgreSQL, needs Docker).

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

Deployed **from this branch's working tree** at 14:35 UTC today via `azd deploy`; all revisions
Running/Healthy (`apiservice--0000072`, `worker--0000066`, `webfrontend--0000069`), zero warn/fail lines
after the last deploy, all queues/subscriptions empty.

| Resource | Notes |
| --- | --- |
| `postgres-676wfhbuks6uu` | Flexible Server PG16 `Standard_B1ms`, zone 2 (no pinning), **Entra-only**; db `gchanneldb` with schemas `gchannel`, `wolverine_api`, `wolverine_worker` (+ the orphaned old `wolverine`, see open items) |
| `messaging-676wfhbuks6uu` | Service Bus Standard, **local auth disabled**; topic `estate-events` (+`worker-estate`, `api-cache`), queues `estate-sync`, `jobs`, `operations`, `wolverine-dead-letter-queue` |
| `cache-676wfhbuks6uu` | Managed Redis B0, access keys disabled (Entra access policy) |
| `secrets-676wfhbuks6uu` | Key Vault: `google-client-secret`, `google-channel-sa-key`, `ion-gateway-api-key` |
| `mi-676wfhbuks6uu` | the **single** user-assigned identity used by all three apps: Postgres Entra admin, Service Bus Data Owner, Redis access policy, KV Secrets User, AcrPull |
| `cae-676wfhbuks6uu`, `acr676wfhbuks6uu`, `law-676wfhbuks6uu` | ACA environment (azd-managed, "limited mode"), registry, Log Analytics |

Public URL: `https://webfrontend.gentlewave-ffb522c3.swedencentral.azurecontainerapps.io` (Google sign-in;
redirect URI unchanged). Aspire dashboard: `https://aspire-dashboard.ext.gentlewave-ffb522c3.swedencentral.azurecontainerapps.io`.

**Removed today (no longer used):** Azure SQL server `sql-676wfhbuks6uu` (+ databases), identity
`sql-admin-676wfhbuks6uu`, the three unused per-app identities (`apiservice_identity-`, `worker_identity-`,
`webfrontend_identity-`) with their KV roles / Redis policies, and an orphaned `AcrPull` assignment.
`.azure/gchannel/.env` was pruned of the matching stale keys (`SQL_*`, `*_IDENTITY_*`, `GCHANNEL_*`).

## Open items

1. **Branch not pushed / no PR.** Production runs code that exists only on this machine. First thing
   tomorrow: `git push -u origin feature/critter-stack-blazorise`, open a PR to `main` (consider renaming
   the branch — it still says "blazorise").
2. **Drop the orphaned `wolverine` schema** in `gchanneldb` (replaced by `wolverine_api` /
   `wolverine_worker`). Needs an Entra login on the server — currently only `mi-676wfhbuks6uu` is admin;
   add yourself with `az postgres flexible-server ad-admin create` or run it from a container app shell.
3. **Recurring jobs after the schema split.** The next ticks were scheduled in the old schema, so the
   watchdog re-arms them ~3 min after they fall due: `estate-sync` 02:19 UTC, `billing-posture-scan`
   02:00 UTC, `billing-sync` 13:42 UTC on 2026-10-29. Check the worker log once afterwards for
   `Started recurring job chain … (overdue …)` and a following `run finished` — then nothing else to do.
4. **Single shared managed identity** for all apps (azd limited mode). Per-app identities would need an
   Aspire-managed `AddAzureContainerAppEnvironment` → new ACA environment → new hostnames and a new Google
   OAuth redirect URI. Deliberately not done; decide if least-privilege per app is wanted.
5. **Postgres SKU/capacity.** `Standard_B1ms` provisioning failed repeatedly today with
   `RegionalAllocationFailed` (zone pinning removed; third retry succeeded). If it ever needs recreating,
   retry or temporarily use `Standard_B2s` / General Purpose.
6. Authenticated UI pages were compile-verified and the shell was browser-tested (light/dark, no console
   errors), but a signed-in click-through of the Radzen pages in Azure has not been done.

## Next steps (suggested order)

1. Push branch, open PR, merge to `main` after a signed-in smoke test of the dashboard, customers,
   entitlements and billing pages on the Azure URL.
2. Verify the three recurring-job re-arms (open item 3) and that `wolverine-dead-letter-queue` stays empty.
3. Drop the old `wolverine` schema (open item 2).
4. Optional hardening: per-app identities (open item 4); scale Postgres down/up as the estate grows.

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect branch + head above, clean tree
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 14 passed (Docker required)
az containerapp revision list -n worker -g rg-gchannel --query "[?properties.active].{r:name,h:properties.healthState}" -o table
```

Useful: `azd provision --preview` (what-if), `azd deploy` (images only), `azd up` (both). Worker log
query (Log Analytics workspace `law-676wfhbuks6uu`, table `ContainerAppConsoleLogs_CL`, filter
`RevisionName_s`); `az containerapp logs show -n worker -g rg-gchannel --type console --tail 300`.
Never provision Azure resources outside `rg-gchannel` on the user's behalf; `.azure/` is git-ignored and
holds secrets.

## Gotchas learned

- Wolverine on Azure Service Bus with `AutoProvision` off: every entity incl. the dead-letter queue must be
  declared in the AppHost; per-node system queues are disabled (`SystemQueuesAreEnabled(false)`).
- Two Wolverine apps on one database need **separate message-store schemas**, otherwise topic fan-out is
  rejected as a duplicate envelope.
- Wolverine's default 60 s execution timeout cancels quota-paced handlers; defaults are now 10 min +
  per-job `[MessageTimeout]` (`RecurringJobs.*BudgetSeconds`), and the watchdog derives "stuck" from them.
- `Aspire.Azure.Npgsql` infers the Postgres username from the managed-identity token; the identity must be
  a server Entra administrator (Aspire's `postgres-roles` Bicep does this).
- Radzen: muted text is `rz-text-secondary-color` (not `rz-color-secondary`); no `rz-gap-*` utilities
  (use `RadzenStack Gap`); `RadzenChip.ChipStyle` takes a `BadgeStyle`.
- Running the AppHost locks DLLs → stop it before `dotnet build/test`.

## Session log

- **2026-10-28** — Critter Stack backend (Marten/Wolverine/PostgreSQL/Service Bus) done; UI MudBlazor →
  Blazorise → Radzen; Aspire 13.6.4 + packages; emulator removed; azd provision/deploy to Azure with
  managed identities; SQL/identity cleanup; messaging hardening (schemas, DLQ, timeouts, watchdog).
