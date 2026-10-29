# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-10-29 · **branch:** `main` = `08cfb57` (squash of PR
[#2](https://github.com/tdsnxnielsen-mikkelbach/GChannel/pull/2), on top of `0e77d01` = PR #1). Production
runs exactly this tree. No feature branch is open. This handoff lands via `docs/session-handoff-2026-10-29`.
The repo has no CI workflows.

## Current state

### Code (`main`)

- **PR #2 (merged) fixed two bugs found by the signed-in smoke test**:
  1. The Customers, Entitlements and Channel partner links grids never loaded. `RadzenDataGrid` only
     calls `LoadData` on first render while `Data == null`, so the backing lists now start null.
  2. The worker dead-lettered Channel notifications with `DocumentAlreadyExistsException`. A no-op
     `Apply(ChannelNotificationReceived)` on both read models made each inline snapshot run on the other
     aggregate's streams. Fixed by removing it, plus the idempotent startup cleanup `StrayReadModelCleanup`.
     See [architecture.md](architecture.md) "Event sourcing".
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
- Quality gates: `dotnet build GChannel.slnx` → 0 errors (pre-existing xUnit1051/nullable warnings in
  tests only); `dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj` → 15/15 (Testcontainers
  PostgreSQL, needs Docker).

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

Deployed at 08:33 UTC on 2026-10-29 via `azd deploy`, from the PR #2 branch. Its tree is identical to
`main` `08cfb57`. All revisions are Running/Healthy (`apiservice--0000073`, `worker--0000067`,
`webfrontend--0000070`), and no fail/error lines were logged after the deploy. On startup the worker's
cleanup removed the two stray documents (`CustomerRecord ent:ScANOhjbI1l9gj`,
`EntitlementRecord cust:SKeqgQ0dcVXJ2A`).

The signed-in smoke test passed for all 15 nav routes. Customers (131), Entitlements and Partner links
(44) now load and page.

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

**Removed on 2026-10-28 (no longer used):** Azure SQL server `sql-676wfhbuks6uu` (+ databases), identity
`sql-admin-676wfhbuks6uu`, the three unused per-app identities (`apiservice_identity-`, `worker_identity-`,
`webfrontend_identity-`) with their KV roles / Redis policies, and an orphaned `AcrPull` assignment.
`.azure/gchannel/.env` was pruned of the matching stale keys (`SQL_*`, `*_IDENTITY_*`, `GCHANNEL_*`).

## Open items

1. **Confirm notification handling on `worker--0000067`.** No Channel notification had arrived yet when
   the session ended. Normal volume is about 12–48 per 2 h, in bursts. Check that new rows appear on
   `/notifications` (the feed row is written in the same transaction as the stream appends) and that the
   worker logs no `DocumentAlreadyExists` and no "moved to the error queue".
2. **Notifications dead-lettered before the fix** (about 1,170 related error log lines, including
   retries) sit in the Postgres durable dead letters (`wolverine_worker.wolverine_dead_letters`); the
   Service Bus DLQ is empty. The 02:23 estate sync has since reconciled state, so they matter only to the
   notification feed. Purge them, or replay them with `replayable = true`; both need the DB access
   described in item 3.
3. **Drop the orphaned `wolverine` schema** in `gchanneldb` (replaced by `wolverine_api` /
   `wolverine_worker`). Needs an Entra login on the server — currently only `mi-676wfhbuks6uu` is admin;
   add yourself with `az postgres flexible-server ad-admin create` or run it from a container app shell.
4. **Recurring-job re-arm after the schema split.** `billing-posture-scan` (02:03) and `estate-sync`
   (02:23) re-armed and finished without error. Still to check: `billing-sync`, due 13:42 UTC on
   2026-10-29. Look for `Started recurring job chain billing-sync … (overdue …)` and then `run finished`.
5. **Single shared managed identity** for all apps (azd limited mode). Per-app identities would need an
   Aspire-managed `AddAzureContainerAppEnvironment` → new ACA environment → new hostnames and a new Google
   OAuth redirect URI. Deliberately not done; decide if least-privilege per app is wanted.
6. **Postgres SKU/capacity.** `Standard_B1ms` provisioning failed repeatedly on 2026-10-28 with
   `RegionalAllocationFailed` (zone pinning removed; third retry succeeded). If it ever needs recreating,
   retry or temporarily use `Standard_B2s` / General Purpose.
7. Noise, not a bug: the API logs `No known handler for ChannelNotificationArrived` because its `api-cache`
   subscription receives every topic message. A SQL filter on it (AppHost + `MessagingTopology`) would fix it.

## Next steps (suggested order)

1. Confirm notification handling (open item 1).
2. Check the `billing-sync` re-arm (open item 4).
3. Get Postgres Entra admin access, drop the old `wolverine` schema, then purge or replay the dead
   letters (open items 2–3).
4. Optional: `api-cache` subscription filter (item 7), per-app identities (item 5), Postgres scaling.

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect branch + head above, clean tree
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 15 passed (Docker required)
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
- `git push` authenticates through Git Credential Manager, but `gh` is **not** logged in and the GitHub MCP
  server is read-only: create/merge PRs in the signed-in integrated browser, or run `gh auth login` first.
- Radzen server-side grids: `Data` must be **null** until the first `LoadData`, or the grid never loads.
- Marten inline snapshots run on every stream that contains an event type they handle. Never add an
  `Apply` (not even a no-op) for an event that is also appended to another aggregate's streams.
- Wolverine's "moved to the error queue" means the Postgres durable dead letters (`wolverine_*` schema),
  not the Service Bus DLQ. In KQL, `last` is a reserved word; use another alias.

## Session log

- **2026-10-28** — Critter Stack backend (Marten/Wolverine/PostgreSQL/Service Bus) done; UI MudBlazor →
  Blazorise → Radzen; Aspire 13.6.4 + packages; emulator removed; azd provision/deploy to Azure with
  managed identities; SQL/identity cleanup; messaging hardening (schemas, DLQ, timeouts, watchdog).
- **2026-10-29** — Checklist green; PR #1 opened and squash-merged (`0e77d01`). The signed-in smoke test
  found 2 bugs (3 grids not loading; notification snapshot collision). Fixed with tests in PR #2 (merged
  `08cfb57`), deployed, re-verified.
