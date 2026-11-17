# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-11-17 09:36 UTC (mid-session) · **branch:** `main` after `d1a9f71` (PR #8 merged).
The billing code matches production. The cancellation fix below is verified; **deployment is pending**.
The repo has no CI workflows.

## Current state

### Code

- **Cancellation fix:** Notifications captures its token before initial loading and skips further
  setup after cancellation; Operations captures its token before lookup and passes it into polling,
  which does not start after cancellation. No disposed-source read when starting either poll.
  Two regression tests dispose Notifications during feed/subscriber loading; both reproduced the original
  `ObjectDisposedException` and pass with the fix. Tests use the framework renderer, with no new packages.
- **PR #8 merged:** estate spend views show the last *complete* month, not the current empty month.
  - Estate endpoints now take `month=YYYYMM` or `months=N` (rolling, includes the current month) and default to
    the last complete UTC month, so never send `months=1` for "last month". The result's `InvoiceMonths` names
    the window used, so the UI never computes the month itself; an unrecognised `month` is ignored (no 400).
    The per-customer seller card is unchanged. Shared logic: `InvoiceMonthKeys` in `GChannel.Shared`.
  - New `GET /api/billing/spend/months` feeds a month picker on **Spend trends**; Dashboard, Customers and
    Channel partner links label their window ("Total billed, Oct 2026", "Billed (Oct 2026)"). See
    [billing-permissions.md](billing-permissions.md) Part 4 and [the todo](todos/15-cloud-billing-bigquery-export.md).
- **Stack:** Marten and Wolverine on PostgreSQL / Service Bus. Radzen.Blazor 12.1.8 (toasts and confirms through
  `UiNotifier`; the Driver.js tours need `data-onboarding` / `data-walkthrough`). Aspire 13.6.4; packages current,
  `Microsoft.OpenApi` pinned to 2.x on purpose.
- Quality gates on 2026-11-17: `dotnet build GChannel.slnx` → 0 errors / warnings (incremental);
  `dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj --no-build` → 54/54 (Docker, ~1 min).
  Existing xUnit1051 / CS8602 warnings remain in older tests on recompilation.

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed at 12:46 UTC on 2026-11-02 by `azd deploy` from `billing-last-complete-month` (code = `1182418`):
  `apiservice--0000079`, `worker--0000071`, `webfrontend--0000074`, all Healthy / Running when checked on
  2026-11-17. This code matches merged `main` (`d1a9f71`); only the handoff differs. No deployment this session.
  Both hosts logged "code generation mode is Static" at deployment.
- Before that deploy, the daily `billing-sync` had run on 1 Nov (43 monthly, 951 daily, 564 seller rows, 0
  unattributed, no error), so the schema-split re-arm worked.

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

## Open items

1. **Deploy the cancellation fix.** PR #8 was merged on 2026-11-17; `main` was pulled.
   The billing-month UI was checked signed-in on 2026-11-02. Production does not yet include the new fix.
2. **Month-boundary edge:** "last complete" is computed in UTC but GCP invoice months follow US Pacific, so for a
   few hours around a month change it can be off by one. Accepted. Google may also adjust the last complete
   month for a few days after it ends; the daily sync re-reads the last 13 months.
3. Marketplace orders on Spend trends show Provider "—" and a blank Product. Seen in a screenshot; not investigated.
4. **Post-deploy circuit smoke test:** navigate away from `/notifications` and `/operations` mid-load
   (including an operation deep link). Automated notification teardown tests pass; signed-in browser
   navigation has not yet been checked with the fix.
5. **Slow cold loads:** about 20 s for entitlement detail and about 40 s for `/license-change-alerts`, with or
   without pre-generated code. Probably the per-entitlement Google calls; profile before optimising.
6. **Remove `NotificationDeadLetterReplay`** (worker) in a later change; it has done its job.
7. **Drop the orphaned `wolverine` schema** in `gchanneldb`. It needs an Entra login, and only `mi-676wfhbuks6uu`
   is admin today: add yourself with `az postgres flexible-server ad-admin create`, or use a container app shell.
8. **Single shared managed identity** (azd limited mode). Per-app identities would need a new ACA environment,
   i.e. new hostnames and a new Google OAuth redirect URI. Not done on purpose; decide if least privilege per app
   is wanted.
9. **Postgres SKU:** creating `Standard_B1ms` failed with `RegionalAllocationFailed` on 2026-10-28. If it
   ever needs recreating, retry or use `Standard_B2s` temporarily.
10. Noise, not bugs: the API logs `No known handler for ChannelNotificationArrived` (fix: a SQL filter on its
    `api-cache` subscription, in the AppHost and `MessagingTopology`) and the worker logs the same for
    `CustomerReadModelUpdated`; KEDA warns "no triggers" for the fixed-replica worker; Marten stream-version
    conflicts (`pk_mt_events_stream_and_version`) are absorbed by retries.

## Next steps (suggested order)

1. Deploy the cancellation fix (item 1); no provisioning needed.
2. Verify signed-in navigation away from both pages mid-load after deployment (item 4).
3. Remove `NotificationDeadLetterReplay` (item 6) and profile the slow pages (item 5).
4. Get Postgres Entra admin access and drop the old schema (item 7).
5. Optional: noise filters, per-app identities, the Marketplace Provider gap (item 3).

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect main with the cancellation fix
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 54 passed (Docker required)
az containerapp revision list -n worker -g rg-gchannel --query "[?properties.active].{r:name,h:properties.healthState}" -o table
```

Useful: `azd provision --preview` (what-if), `azd deploy` (images only), `azd up` (both). Worker logs: Log
Analytics workspace `law-676wfhbuks6uu`, table `ContainerAppConsoleLogs_CL`, `ContainerAppName_s == 'worker'`
(GUID: `az monitor log-analytics workspace show -g rg-gchannel -n law-676wfhbuks6uu --query customerId -o tsv`),
or `az containerapp logs show -n worker -g rg-gchannel --type console --tail 300`. Never provision Azure
resources outside `rg-gchannel` on the user's behalf; `.azure/` is git-ignored and holds secrets.

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
- `git push` authenticates through Git Credential Manager, and `gh` was logged in on 2026-11-02 (check
  `gh auth status`; if not, `gh auth login` or use the signed-in integrated browser). The GitHub MCP server is read-only.
- Marten inline snapshots run on every stream that contains an event type they handle. Never add an
  `Apply` (not even a no-op) for an event that is also appended to another aggregate's streams.
- Wolverine's "moved to the error queue" means the Postgres durable dead letters (`wolverine_*` schema),
  not the Service Bus DLQ. In KQL, `last` is a reserved word; use another alias.
- Production loads **pre-generated** Wolverine code; never commit `Internal/Generated/`. If a host won't start
  after a deploy, check the `predeploy` codegen hook ([deployment.md](deployment.md#pre-generated-wolverine-code)).
- azd's generated container-app template has no `resources` block, so every deploy resets each app to
  0.5 vCPU / 1 GiB. Sizing needs a hook (as in PR #5) or an Aspire-managed ACA environment.
- Blazor pages with a `_cts` field: read `_cts.Token` once, before the first await; after `Dispose` it kills the circuit.
- UI dates go through `DisplayFormat` (`dd-MM-yyyy`, UTC). Don't use culture-dependent format strings.
- On Windows `az.cmd` / `bq.cmd` cut multi-line arguments at the first newline: pass KQL / SQL on one line (a
  multi-line `--analytics-query` once dumped 140 MB). Run `bq query --dry_run` first (about 725 MB per aggregate).

## Session log

- **2026-10-28** — Critter Stack backend, UI MudBlazor → Blazorise → Radzen, Aspire 13.6.4, azd deploy to Azure
  with managed identities, SQL/identity cleanup, messaging hardening (schemas, DLQ, timeouts, watchdog).
- **2026-10-29** — PR #1 and #2 (smoke-test fixes: 3 grids not loading, notification snapshot collision); PR #4
  (dead-letter replay of 135 notifications, `dd-MM-yyyy` dates); PR #5 / #6 (API OOM loop → pre-generated
  Wolverine code). Ended with `main` = production at `eb042b6`, 16/16 tests, all apps Healthy on 1 GiB.
- **2026-11-02** — "Why is GCP not shown?": the estate "last month" panels resolved to the current, empty month.
  Branch `billing-last-complete-month`: last-complete-month default, `month` / `months` API, `/spend/months`,
  Spend trends picker, 36 new tests (52/52). Deployed 12:46 UTC, UI checked signed-in; PR #8 open.
- **2026-11-17** — PR #8 merged/pulled; production healthy and code-equivalent. Cancellation fix and two
  regression tests added; build passed, 54/54 tests. Deployment pending.
