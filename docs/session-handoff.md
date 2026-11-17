# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-11-17 12:17 UTC (mid-session) · **branch:** `main`, deployed application code `7976ea5`.
Every Channel operation is now recorded and correlated on the Operations page. No CI workflows.

## Current state

### Code

- **Operation log (`dff122e`, `7976ea5`):** Google can't list operations (501) and the saga state is deleted, so the API writes a
  Marten `OperationLogRecord` for every purchase, entitlement change, transfer and Cloud Identity provisioning. It holds
  the user, the customer and entitlement names, the inputs (offer, seats, billing account, PO; never the transfer token),
  and a subject. `ChannelOperationSaga` updates it on every poll with the outcome, Google's `error.details` and the
  entitlements a transfer produced. `/operations` lists the records (`?customer=` filters; customer page button);
  the tracked card shows everything above plus the gRPC status name. Toasts link to the operation
  (`UiNotifier.AddOperation`). Docs: architecture/UI/configuration/api-surface.
- **Transfer billing account (`2c99982`, `c73d806`):** multi-currency resellers get 0 transferable offers unless
  `listTransferableOffers` names a billing account. The page picks it via `queryEligibleBillingAccounts` and passes
  it to offers and transfer lines. After submit it opens the operation.
- **Catalog optimization (`268b71b`), timings (`67cbea5`, `db242b8`), caches (`5a5ba70`, `e65e5f6`):** detail SKUs
  limited to its product; timing logs hold counts only; lookups share Redis catalog keys (300 s TTL).
- **Earlier:** token capture `6897fe2`, replay removal `ec73b40`; PR #8 spend window (`InvoiceMonths`, see
  [billing-permissions.md](billing-permissions.md) Part 4).
- **Stack:** Marten/Wolverine on PostgreSQL/Service Bus; Radzen.Blazor 12.1.8 (`UiNotifier`, Driver.js attributes).
  Aspire 13.6.4; packages current, `Microsoft.OpenApi` intentionally pinned to 2.x.
- Quality gates: build 0 errors; suite 61/61; Release publish/codegen passed.

#### Production profiling (2026-11-17; uncontrolled live runs, no cache flush)

- License alerts (50 rows) first visit 27.8 → 11.98 s with offer-cache hits; warm repeat ~0.1 s. Cold entitlement
  detail 25.1 → 15.9 s (offers 4.9 s, products/SKU ~1.7 s each, uncached after deploy). Details: commits
  `5a5ba70`/`e65e5f6`/`12d46ce`. Not controlled benchmarks; warm detail performance unproven.

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed 12:15 UTC via `azd deploy --no-prompt` from `main` (`7976ea5`):
  API `0000090`, worker `0000082`, web `0000085`, Running; both hosts log Static codegen.
  One running replica each; no unhandled/circuit failures. The 3 previously noted unrelated
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
4. Operations written before `dff122e` have no log record; they can still be tracked by id (live state only).
5. **Slow first visits:** catalog caches are deployed. Verify cache reuse on a second same-product entitlement
   after a natural cache miss; avoid flushing caches or adding quota-risky concurrency.
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
10. **STG transfer-in FORBIDDEN:** three submits all failed with `FORBIDDEN` / PERMISSION_DENIED (code 7). The
    attempts were STG, ChromeOS Enterprise Upgrade, Annual Plan (Yearly), 1 seat, billing account
    `1-31035-47111`, no token. Google's only error detail is `ErrorDescription{traceId}`, with no ErrorInfo
    reason. The operations and traces are:
    `8b4eaed9-…` (`7173361907499791783`, 12:11), `1249ee58-…` (`-5367058062616662119`) and `f08feb12-…`
    (`3011650400553843846`). The SKU is `isEligible`; the customer is linked to channel partner `C01rm8xco`.
    Next: try the current reseller's transfer token, raise the trace ids with Partner Support, and compare with
    a Partner Sales Console submission. Purchase offers may also need a billing account
    (`listPurchasableOffers` returned 0).

## Next steps (suggested order)

1. Resolve the STG transfer FORBIDDEN with Google: token, trace ids, PSC comparison (item 10).
2. Verify product/SKU cache hits on another same-product entitlement and compare a natural cold detail load (item 5).
3. Triage the 3 unrelated notification dead letters without blindly replaying (item 1).
4. Get Postgres Entra admin access and drop the old schema (item 6).
5. Optional: noise filters, per-app identities, the Marketplace Provider gap (item 3).

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect main; deployed application code 7976ea5
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 61 tests (Docker required)
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

- **2026-10-28 → 11-02** — Radzen/Aspire, identity/messaging hardening; PRs #1–#8 (grid/snapshot, replay, dates, static Wolverine, billing month); 52/52.
- **2026-11-17** — PR #8 merged; cancellation fix `6897fe2`; replay removal `ec73b40` deployed, smoke passed, baseline recorded.
- **2026-11-17 10:48/11:07/11:11/11:21/11:29** — Catalog `268b71b`, timings and catalog caches `e65e5f6` deployed. Offer-cache reduced alerts 30.219→19.169 s; cold detail on latest rollout 15.853 s with uncached product/offer/SKU calls. Apps healthy; build/57 pass.
- **2026-11-17 11:43** — Transfer offers empty: billing account missing for multi-currency reseller; fixed `2c99982`, deployed, live 4 EUR plans; 59 pass. Real STG submit → Google FORBIDDEN (item 10); transfer page now opens the operation (not deployed).
- **2026-11-17 12:15** — Operation log `dff122e`/`7976ea5` deployed; 61 pass. Live STG transfer rerun recorded end to end (worker poll → Failed); Google returns only FORBIDDEN + traceId, no ErrorInfo.
