# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-10-29 10:45 UTC (end of session) · **branch:** `main` = `eb042b6` (PR #6) plus this
handoff update. Production runs exactly the code on `main`; no PRs are open. The repo has no CI workflows.

## Current state

### Code

- **PR #6 (merged, `eb042b6`): pre-generated Wolverine code.**
  - Roslyn compiling handler and endpoint adapters on first use had OOM-killed the 1 GiB API in a loop.
  - Production now uses `TypeLoadMode.Static`; the azd `predeploy` hook runs `codegen write` before the
    image build. The output (`Internal/Generated/`) is git-ignored and compiled only in Release.
  - The API is back on 0.5 vCPU / 1 GiB at about 300 MiB (1,469 MiB at 2 GiB before), and PR #5's 2 GiB
    stop-gap is removed. See [deployment.md](deployment.md#pre-generated-wolverine-code).
- **PR #4 (merged, `bc60335`):**
  - UI dates are `dd-MM-yyyy` and timestamps `dd-MM-yyyy HH:mm` (UTC), via `DisplayFormat` in `src/GChannel.Web`.
  - `NotificationDeadLetterReplay` (worker) replayed the 135 dead-lettered notifications. All succeeded and
    it is now a no-op.
- **Stack:** Marten and Wolverine on PostgreSQL / Service Bus (the Critter Stack). Radzen.Blazor 12.1.8, with
  toasts and confirms through `UiNotifier`; the Driver.js tours need the `data-onboarding` /
  `data-walkthrough` attributes. Aspire 13.6.4; packages current, `Microsoft.OpenApi` pinned to 2.x on purpose.
- Quality gates, re-run on `eb042b6` at the end of the session:
  - `dotnet build GChannel.slnx` → 0 errors (the 46 xUnit1051 / CS8602 warnings are in tests only).
  - `dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj` → 16/16 (needs Docker).

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed at 10:27 UTC on 2026-10-29 by `azd deploy` from the PR #6 branch (tree-identical to `eb042b6`).
  Both hosts log "code generation mode is Static"; a signed-in check of 18 pages showed `dd-MM-yyyy` dates.
- Checked at 10:42 UTC: `apiservice--0000078`, `worker--0000070` and `webfrontend--0000073` are Healthy on
  0.5 vCPU / 1 GiB with 0 restarts and no `fail`/`crit` log lines. Peak memory since the deploy: API 336 MiB,
  worker 264 MiB, web 316 MiB.

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
The old Azure SQL server and the unused per-app identities were deleted on 2026-10-28.

## Open items

1. **Blazor circuit crash, seen in production.**
   - Trigger: leaving `/notifications` or `/operations` while they are still loading.
   - Error: `ObjectDisposedException`, because `_cts.Token` is read after `Dispose`.
   - Fix: `Notifications.razor` (`OnAfterRenderAsync`) and `Operations.razor` (`StartPolling`) need the guard
     `Home.razor` already has — capture the token once, inside a try/catch.
2. **Slow cold loads:** about 20 s for entitlement detail and about 40 s for `/license-change-alerts`, the
   same with and without pre-generated code. Probably the per-entitlement Google calls; profile them before
   optimising.
3. **Remove `NotificationDeadLetterReplay`** (worker) in a later change; it has done its job.
4. **Drop the orphaned `wolverine` schema** in `gchanneldb`.
   - It needs an Entra login on the server, and only `mi-676wfhbuks6uu` is admin today.
   - Add yourself with `az postgres flexible-server ad-admin create`, or run it from a container app shell.
5. **`billing-sync` re-arm** after the schema split, due 13:42 UTC on 2026-10-29 — not verified yet. In the
   worker logs (Log Analytics, see *Useful* below), look for
   `Started recurring job chain billing-sync … (overdue …)`, then `run finished`.
6. **Single shared managed identity** (azd limited mode).
   - Per-app identities would need a new ACA environment, which means new hostnames and a new Google OAuth
     redirect URI.
   - Not done on purpose; decide whether least privilege per app is wanted.
7. **Postgres SKU:** creating `Standard_B1ms` failed with `RegionalAllocationFailed` on 2026-10-28. If it
   ever needs recreating, retry or use `Standard_B2s` temporarily.
8. Noise, not bugs:
   - The API logs `No known handler for ChannelNotificationArrived`. Fix with a SQL filter on its `api-cache`
     subscription, in both the AppHost and `MessagingTopology`.
   - The worker logs `No known handler for CustomerReadModelUpdated`.
   - KEDA warns "no triggers" for the fixed-replica worker.
   - Marten stream-version conflicts (`pk_mt_events_stream_and_version`) are absorbed by retries.

## Next steps (suggested order)

1. Check the `billing-sync` re-arm (item 5); a quick log search.
2. Fix the circuit crash (item 1). Verify by navigating away from both pages mid-load.
3. Remove `NotificationDeadLetterReplay` (item 3) and profile the slow pages (item 2).
4. Get Postgres Entra admin access and drop the old schema (item 4).
5. Optional: noise filters, per-app identities.

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect branch + head above, clean tree
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 16 passed (Docker required)
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
  (use `RadzenStack Gap`); `RadzenChip.ChipStyle` takes a `BadgeStyle`; server-side grids need `Data` to be
  **null** until the first `LoadData`, or they never load.
- Running the AppHost locks DLLs → stop it before `dotnet build/test`.
- `git push` authenticates through Git Credential Manager, but `gh` is **not** logged in and the GitHub MCP
  server is read-only: create/merge PRs in the signed-in integrated browser, or run `gh auth login` first.
- Marten inline snapshots run on every stream that contains an event type they handle. Never add an
  `Apply` (not even a no-op) for an event that is also appended to another aggregate's streams.
- Wolverine's "moved to the error queue" means the Postgres durable dead letters (`wolverine_*` schema),
  not the Service Bus DLQ. In KQL, `last` is a reserved word; use another alias.
- Production loads **pre-generated** Wolverine code; never commit `Internal/Generated/`. If a host won't start
  after a deploy, check the `predeploy` codegen hook ([deployment.md](deployment.md#pre-generated-wolverine-code)).
- azd's generated container-app template has no `resources` block, so every deploy resets each app to
  0.5 vCPU / 1 GiB. Sizing needs a hook (as in PR #5) or an Aspire-managed ACA environment.
- Blazor pages with a `_cts` field: read `_cts.Token` once, before the first await. Reading it after `Dispose`
  kills the whole circuit.
- UI dates go through `DisplayFormat` (`dd-MM-yyyy`, UTC). Don't use culture-dependent format strings.

## Session log

- **2026-10-28** — Critter Stack backend (Marten/Wolverine/PostgreSQL/Service Bus) done; UI MudBlazor →
  Blazorise → Radzen; Aspire 13.6.4 + packages; emulator removed; azd provision/deploy to Azure with
  managed identities; SQL/identity cleanup; messaging hardening (schemas, DLQ, timeouts, watchdog).
- **2026-10-29** — Checklist green; PR #1 opened and squash-merged (`0e77d01`). The signed-in smoke test
  found 2 bugs (3 grids not loading; notification snapshot collision). Fixed with tests in PR #2 (merged
  `08cfb57`), deployed, re-verified.
- **2026-10-29 (cont.)** — PR #4 (`bc60335`) replayed the 135 dead-lettered notifications and made all dates
  `dd-MM-yyyy`. Found the API OOM restart loop: PR #5 (`4873b6a`) was a 2 GiB stop-gap and PR #6 the fix
  (pre-generated Wolverine code). Deployed; the API is back at 1 GiB, peaking around 300 MiB.
- **2026-10-29 (end)** — PR #6 merged (`eb042b6`), so `main` matches production. End-of-session check green:
  build, 16/16 tests, all three apps Healthy on 1 GiB with 0 restarts.
