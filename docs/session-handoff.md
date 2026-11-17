# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-11-17 10:19 UTC (mid-session) · **branch:** `main`, deployed application code `ec73b40`.
The cancellation fix (`6897fe2`) and temporary replay removal (`ec73b40`) are pushed/deployed.
The repo has no CI workflows.

## Current state

### Code

- **Cancellation fix:** Notifications captures its token before initial loading and skips further
  setup after cancellation; Operations captures its token before lookup and passes it into polling,
  which does not start after cancellation. No disposed-source read when starting either poll.
  Two regression tests dispose Notifications during feed/subscriber loading; both reproduced the original
  `ObjectDisposedException` and pass with the fix. Tests use the framework renderer, with no new packages.
- **Completed replay removed:** the temporary startup recovery service and its obsolete test are gone.
  Normal notification handling, durable dead-letter storage and `StrayReadModelCleanup` are unchanged.
- **PR #8 merged:** estate spend defaults to the last complete UTC month; `month=YYYYMM` selects one,
  `months=N` is rolling and includes the current month. Never send `months=1` for "last month".
  `InvoiceMonths` labels the window; `/api/billing/spend/months` feeds the picker. See
  [billing-permissions.md](billing-permissions.md) Part 4 and [the todo](todos/15-cloud-billing-bigquery-export.md).
- **Signed-in smoke passed:** navigated away from both Notifications and Operations with their loading
  panels visible; the circuit stayed usable, with no browser errors or server-side circuit failures.
- **Production profiling (no cache flush):** license alerts first visit 27.820 s (shell 139 ms), refresh
  0.295 s, 50 rows; same entitlement detail first visit 25.063 s, revisit 0.342 s. These are first/repeat
  visits, not controlled cold-cache benchmarks. Source shows sequential enrichment and live catalog
  rebuilds on detail/history cache misses; per-stage timing is still needed to apportion the delay.
  The checked last-day console logs contain no API/HttpClient timing entries.
- **First-visit optimization (validated; deployment pending):** detail reads list SKUs only for their product;
  history resolves offer names without listing products. Two tests pass; build 0 warnings/errors, suite 55/55.
  No production timing yet; sequential enrichment and per-history offer lookups remain.
- **Stack:** Marten and Wolverine on PostgreSQL / Service Bus. Radzen.Blazor 12.1.8 (toasts and confirms through
  `UiNotifier`; the Driver.js tours need `data-onboarding` / `data-walkthrough`). Aspire 13.6.4; packages current,
  `Microsoft.OpenApi` pinned to 2.x on purpose.
- Quality gates on 2026-11-17: build 0 warnings/errors; suite 55/55 (including two catalog regression tests);
  worker suite 4/4 after replay removal. Older tests emit xUnit1051 / CS8602 on recompilation.

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed at 09:45 UTC on 2026-11-17 by `azd deploy` from `main` (`ec73b40`):
  `apiservice--0000081`, `worker--0000073`, `webfrontend--0000076`, all Healthy / Running, 0 restarts.
  Both hosts log "code generation mode is Static"; latest revisions still Ready / Running at 09:53.
  Before removal, startup replay found no matching snapshot-collision notifications; 3 unrelated
  `EventStreamUnexpectedMaxEventIdException` dead letters remain untouched.

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

1. **Triage 3 notification dead letters** with `EventStreamUnexpectedMaxEventIdException` when appropriate.
   They are not the old snapshot-collision failures; do not blindly replay or delete them.
2. **Month-boundary edge:** "last complete" is computed in UTC but GCP invoice months follow US Pacific, so for a
   few hours around a month change it can be off by one. Accepted. Google may also adjust the last complete
   month for a few days after it ends; the daily sync re-reads the last 13 months.
3. Marketplace orders on Spend trends show Provider "—" and a blank Product. Seen in a screenshot; not investigated.
4. **Operation deep-link smoke:** no recent operations were available for a real sample; verify a valid
   `?operation=...` link when one exists. Regular mid-load navigation passed on both pages.
5. **Slow first visits:** validated optimization is unmeasured in production; commit, push and deploy before
   measuring. Sequential enrichment and per-history offer lookups remain; avoid quota-risky concurrency.
6. **Drop the orphaned `wolverine` schema** in `gchanneldb`. It needs an Entra login, and only `mi-676wfhbuks6uu`
   is admin today: add yourself with `az postgres flexible-server ad-admin create`, or use a container app shell.
7. **Single shared managed identity** (azd limited mode). Per-app identities would need a new ACA environment,
   i.e. new hostnames and a new Google OAuth redirect URI. Not done on purpose; decide if least privilege per app
   is wanted.
8. **Postgres SKU:** creating `Standard_B1ms` failed with `RegionalAllocationFailed` on 2026-10-28. If it
   ever needs recreating, retry or use `Standard_B2s` temporarily.
9. Noise, not bugs: the API logs `No known handler for ChannelNotificationArrived` (fix: a SQL filter on its
    `api-cache` subscription, in the AppHost and `MessagingTopology`) and the worker logs the same for
    `CustomerReadModelUpdated`; KEDA warns "no triggers" for the fixed-replica worker; Marten stream-version
    conflicts (`pk_mt_events_stream_and_version`) are absorbed by retries.

## Next steps (suggested order)

1. Commit, push and deploy the validated first-visit optimization, then remeasure (item 5).
2. Verify a real operation deep link when available (item 4).
3. Triage the 3 unrelated notification dead letters without blindly replaying (item 1).
4. Get Postgres Entra admin access and drop the old schema (item 6).
5. Optional: noise filters, per-app identities, the Marketplace Provider gap (item 3).

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect main; deployed application code ec73b40
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 55 tests (Docker required)
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

- **2026-10-28** — Backend/UI moved to Radzen; Aspire/azd deployment, managed identity, SQL and messaging hardening.
- **2026-10-29** — PR #1/#2/#4/#5/#6: grid/snapshot, replay, dates and static Wolverine; production at `eb042b6`, 16/16, healthy.
- **2026-11-02** — Billing-month fix deployed 12:46 UTC, UI checked signed-in; PR #8 open, 52/52 tests.
- **2026-11-17** — PR #8 merged/pulled; cancellation fix `6897fe2` pushed/deployed, full suite 54/54.
- **2026-11-17 09:53** — Replay removal deployed (`ec73b40`); apps healthy, worker tests/navigation smoke passed,
  first/repeat timings recorded. Next: uncached-path optimisation.
- **2026-11-17 10:07** — Local catalog optimization narrows detail SKU lookups and skips product listings for history; build and 55/55 pass. Not deployed; next: deploy and remeasure.
- **2026-11-17 10:19** — Release checklist repeated: build clean, 55/55 tests pass, worker healthy; committing before deployment and remeasurement.
