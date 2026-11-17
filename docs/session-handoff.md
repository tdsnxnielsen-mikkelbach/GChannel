# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-11-17 11:08 UTC (mid-session) · **branch:** `main`, deployed application code `67cbea5`.
Page timings are deployed; lower-level Google call timings are local. No CI workflows.

## Current state

### Code

- **Catalog optimization (`268b71b`):** detail SKUs limited to its product; history offers skip products.
  Names preserved; two HTTP tests verify skipped requests/display names; live detail/history/pricing work.
- **Page-stage timing (`67cbea5`):** alert feed/links/customer/history and detail entitlement/history/pricing;
  counts only, no customer/entitlement IDs. Google-call splits are local (build, 55/55), pending deploy.
- **Earlier fixes:** pre-await token capture with regressions (`6897fe2`); replay removal (`ec73b40`).
  Notification handling, durable dead letters and cleanup remain intact; signed-in navigation smoke passed.
- **PR #8 merged:** spend defaults to last complete UTC month; `month=YYYYMM` selects one, `months=N`
  includes current (never `months=1` for "last month"). Window: `InvoiceMonths`; picker: `/api/billing/spend/months`.
  See [billing-permissions.md](billing-permissions.md) Part 4 and [the todo](todos/15-cloud-billing-bigquery-export.md).
- **Stack:** Marten/Wolverine on PostgreSQL/Service Bus; Radzen.Blazor 12.1.8 (`UiNotifier`, Driver.js attributes).
  Aspire 13.6.4; packages current, `Microsoft.OpenApi` intentionally pinned to 2.x.
- Quality gates: build 0 errors; suite 55/55; Release publish/codegen passed. Existing tests emit xUnit1051 / CS8602.

#### Production profiling (2026-11-17; no cache flush)

| Page | Previous first visit | After optimization | Previous repeat | After repeat |
| --- | --- | --- | --- | --- |
| License alerts (50 rows) | 27.820 s | 21.905 s | 0.295 s | 0.099-0.901 s (4 samples) |
| Same entitlement detail | 25.063 s | 19.712 s | 0.342 s | 0.348 s |

- **Method:** signed-in same routes/entitlement after over 300 s idle; no cache flush. Live observations,
  not controlled A/B or proven cold-cache benchmarks. Background tab used programmatic navigation/DOM timing;
  detail also waited for history/pricing. Initial timeout/full-document probes are excluded.
- **Checks:** all 50 alert rows, friendly offer/product/SKU names and detail history/pricing remain intact;
  successful measured runs had no browser errors, error UI or load-failure messages. Alerts shell: 39 ms.
- **Remaining latency:** first samples are about 21% lower but still around 20 s. Sequential customer/history
  enrichment and per-history offer lookups remain; stage timings are needed before further quota-sensitive changes.
- **Stage evidence (`67cbea5`, one run):** alerts 35.250 s: feed 1.362, links 4.848, 2 customers 2.084,
  4 histories 26.948; warm repeat 0.104 s. Detail 18.717 s: entitlement 9.994, history 6.305, pricing
  2.417 (offer 1.437, repricing 0.978). Alert used reload, not comparable to prior SPA first visit.

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed 11:02 UTC via `azd deploy --environment gchannel --no-prompt` from `main` (`67cbea5`):
  API `0000083`, worker `0000075`, web `0000078`, Healthy/Running, zero restarts; API/web 100% traffic.
  Predeploy generated 91 API / 11 worker files; both hosts log Static mode.
  Page-stage timing logs confirm histories dominate alerts. No unhandled/circuit failures. The 3 previously noted unrelated
  `EventStreamUnexpectedMaxEventIdException` dead letters were not replayed or deleted.

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
2. **Month-boundary edge:** UTC "last complete" can differ from GCP's US Pacific invoice month for a few hours.
   Accepted; Google may adjust it for days, and daily sync re-reads the last 13 months.
3. Marketplace orders on Spend trends show Provider "—" and a blank Product. Seen in a screenshot; not investigated.
4. **Operation deep-link smoke:** no recent operations were available for a real sample; verify a valid
   `?operation=...` link when one exists. Regular mid-load navigation passed on both pages.
5. **Slow first visits:** alert history and detail entitlement/history stages dominate. Deploy local Google-call
   timing splits to separate offer-catalog fetches from entitlement history and product/offer/SKU lookup costs.
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

1. Deploy Google-call timing splits, inspect offer/catalog vs history API durations, then target the dominant call (item 5).
2. Verify a real operation deep link when available (item 4).
3. Triage the 3 unrelated notification dead letters without blindly replaying (item 1).
4. Get Postgres Entra admin access and drop the old schema (item 6).
5. Optional: noise filters, per-app identities, the Marketplace Provider gap (item 3).

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect main; deployed application code 67cbea5
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

- **2026-10-28/29** — Radzen/Aspire, identity/messaging hardening; PR #1/#2/#4/#5/#6 grid/snapshot, replay, dates/static Wolverine; `eb042b6`, 16/16, healthy.
- **2026-11-02** — Billing-month fix deployed 12:46 UTC, UI checked signed-in; PR #8 open, 52/52 tests.
- **2026-11-17** — PR #8 merged/pulled; cancellation fix `6897fe2` pushed/deployed, full suite 54/54.
- **2026-11-17 09:53** — Replay removal deployed (`ec73b40`); apps healthy, worker/navigation smoke passed, first/repeat baseline recorded.
- **2026-11-17 10:48/11:07** — Catalog optimization `268b71b` deployed; first visits 21.905/19.712 s. Page timings `67cbea5` show alert history 26.948 s and detail entitlement/history 16.299 s; apps healthy, build and 55/55 pass. Google-call splits local.
