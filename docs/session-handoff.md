# Session handoff

> **Convention.** This is a rolling document, not history. **Start of a session:** read it top to bottom,
> run the *Start-of-session checklist*, then continue from *Next steps*. **End of a session:** verify the
> current state (build, tests, deployment if touched), rewrite *Current state* / *Open items* / *Next
> steps* to describe reality, add one line to *Session log*, commit. Keep it under ~150 lines; details
> belong in `docs/` or commit messages, not here. The rules live in
> [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

**Last updated:** 2026-11-18 09:15 UTC · **branch:** `main`, deployed application code `c13e4bb`.
The Support feature (`8ac3f17`) was **removed** again in `c13e4bb` (see *Session log*). No CI workflows.

## Current state

### Code

- **No support-case feature.** The Cloud Support API covers only Google Cloud Customer Care cases (Standard,
  Enhanced or Premium plan), and its Pub/Sub event subscriptions need Premium. All of our cases are Google
  Workspace / reseller cases, which have no public API. A possible alternative is to parse case emails via the
  Gmail API (not started; the user hasn't decided).
- **Incoming transfers (`197192f`):** nightly `RunIncomingTransferScan` (03:00 UTC, `GoogleChannel:TransferScan*`)
  → `IncomingTransferRecord`; `/customers/transfers` with Re-check and Transfer deep link.
- **Operation log (`dff122e`, `7976ea5`):** Google can't list operations (501), so the API writes a Marten
  `OperationLogRecord` per LRO (never the transfer token). The saga updates it with the outcome and error details,
  and `/operations` lists it. Toasts link through `UiNotifier.AddOperation`.
- **Earlier:** transfer billing account `2c99982`; catalog caches `268b71b`/`5a5ba70`/`e65e5f6`; PR #8 spend window.
- **Stack:** Marten/Wolverine on PostgreSQL/Service Bus; Radzen.Blazor 12.1.8 (`UiNotifier`, Driver.js attributes).
  Aspire 13.6.4; `Microsoft.OpenApi` intentionally pinned to 2.x.
- Quality gates: build 0 errors; suite 63/63; Release publish/codegen passed via `azd up`.

### Azure (`rg-gchannel`, Sweden Central, azd env `gchannel`, sub `ba99b95e-…`)

- Deployed 2026-11-18 09:08 UTC via `azd up` from `main` (`c13e4bb`): API `0000093`, worker `0000085`,
  web `0000088`, all Healthy/Running, with Static codegen. The `GoogleSupport*` azd parameters were unset, and the
  orphaned `support` Service Bus queue (empty) was deleted by hand.

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

1. **GCP leftovers from the support attempt (user, optional cleanup)** in project `tdsgchannel` / org
   `427790891977`: topic `support-events`, subscription `gchannel-support-sub`, and the org roles granted to
   `gchannel-dashboard@…` (`cloudsupport.*`, `organizationViewer`) and to the user. They are harmless, but can
   be deleted. Keep `cloudresourcemanager.googleapis.com` enabled.
2. **Triage 3 notification dead letters** with `EventStreamUnexpectedMaxEventIdException` when appropriate.
   They are not the old snapshot-collision failures; do not blindly replay or delete them.
3. **Month-boundary edge:** UTC "last complete" can differ from GCP's US Pacific invoice month for a few hours. Accepted.
4. Marketplace orders on Spend trends show Provider "—" and a blank Product. Seen in a screenshot; not investigated.
5. Operations written before `dff122e` have no log record; they can still be tracked by id (live state only).
6. **Slow first visits:** catalog caches are deployed. Verify cache reuse on a second same-product entitlement
   after a natural cache miss; avoid flushing caches or adding quota-risky concurrency.
7. **Drop the orphaned `wolverine` schema** in `gchanneldb`. It needs an Entra login, and only `mi-676wfhbuks6uu`
   is admin today: add yourself with `az postgres flexible-server ad-admin create`, or use a container app shell.
8. **Single shared managed identity** (azd limited mode). Per-app identities would need a new ACA environment
   (new hostnames and OAuth redirect URI). Not done on purpose.
9. **Postgres SKU:** creating `Standard_B1ms` failed with `RegionalAllocationFailed` on 2026-10-28. If it
   ever needs recreating, retry or use `Standard_B2s` temporarily.
10. Noise, not bugs: the API logs `No known handler for ChannelNotificationArrived` (fix: a SQL filter on its
    `api-cache` subscription, in the AppHost and `MessagingTopology`) and the worker logs the same for
    `CustomerReadModelUpdated`; KEDA warns "no triggers" for the fixed-replica worker; Marten stream-version
    conflicts (`pk_mt_events_stream_and_version`) are absorbed by retries.
11. **STG transfer-in FORBIDDEN — ticket raised with Google (awaiting reply).** Three submits failed with
    `FORBIDDEN` / PERMISSION_DENIED (code 7): STG, ChromeOS Enterprise Upgrade, Annual Plan (Yearly), 1 seat, billing
    account `1-31035-47111`, no token. The only error detail is `ErrorDescription{traceId}`. Operations and traces:
    `8b4eaed9-…` (`7173361907499791783`), `1249ee58-…` (`-5367058062616662119`) and `f08feb12-…`
    (`3011650400553843846`). The SKU is `isEligible`; the customer is linked to channel partner `C01rm8xco`.
12. **Incoming-transfer scan errors:** resold customers return `FORBIDDEN: TRANSFER_TOKEN_DOES_NOT_EXIST`
    (likely the same cause as item 11); customers without Cloud Identity return `No cloud identity id found`.
    Consider labelling them "needs transfer token" / "nothing to transfer".

## Next steps (suggested order)

1. Optional: decide whether to build an email-based (Gmail API) Workspace case tracker; tidy GCP leftovers (item 1).
2. Act on Google's answer to the STG ticket: token, trace IDs, PSC comparison (items 11, 12).
3. Verify product/SKU cache hits on another same-product entitlement (item 6).
4. Triage the 3 unrelated notification dead letters without blindly replaying (item 2).
5. Get Postgres Entra admin access and drop the old schema (item 7).
6. Optional: noise filters, per-app identities, the Marketplace Provider gap (item 4).

## Start-of-session checklist

```powershell
git status -sb; git log --oneline -5                     # expect main; deployed application code c13e4bb
az account show --query name -o tsv                      # "Azure subscription 1"; else az login / azd auth login
dotnet build GChannel.slnx -nologo -v q                  # 0 errors
dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj   # 63 tests (Docker required)
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
- **2026-11-17 12:38** — Incoming transfers `197192f` deployed; 63 pass. The scan runs live; STG listed and deep link verified; resold customers return TRANSFER_TOKEN_DOES_NOT_EXIST (item 11).
- **2026-11-18 08:28** — STG ticket raised with Google. Support cases via the Pub/Sub connector, `8ac3f17`, deployed with `azd up` (new `support` queue); 75 pass. Awaiting user GCP setup and org ID (item 1).
- **2026-11-18 09:08** — Support events need Premium Support, and all of our cases are Workspace cases, which have no public API. Reverted in `c13e4bb`, deployed, `support` queue deleted; 63 pass.
