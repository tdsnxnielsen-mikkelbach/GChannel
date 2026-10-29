# GChannel — instructions for coding agents

## Session convention (always)

- **Start of a session:** read [`docs/session-handoff.md`](../docs/session-handoff.md) first. It states
  the branch/commit you should be on, what is deployed, the open items and the next steps. Run its
  *Start-of-session checklist* before changing anything, and say so in your first progress update.
- **End of a session** (when the user says they are wrapping up, or when asked for a handoff): verify the
  current state (build, tests, and the Azure deployment if it was touched), then **rewrite**
  `docs/session-handoff.md` so it describes reality — update *Last updated*, *Current state*, *Open items*
  and *Next steps*, append one line to *Session log* — and commit it. It is a rolling document: keep it
  under ~150 lines, link to `docs/` or commit messages for detail, never paste secrets into it.
- Also update the handoff when you finish a large unit of work mid-session (so a lost session still hands
  off cleanly).

## Project facts

- .NET 10 / Aspire 13.6 solution `GChannel.slnx`: `GChannel.AppHost` (orchestration + Azure Bicep via
  `Azure.Provisioning`), `GChannel.ApiService` (Wolverine.HTTP), `GChannel.Worker` (Wolverine handlers,
  single replica), `GChannel.Web` (Blazor Server, **Radzen.Blazor**), `GChannel.Core` (Marten event store,
  Google clients, Wolverine topology), `GChannel.Shared` (contracts), `tests/GChannel.Tests` (xUnit v3 on
  Microsoft.Testing.Platform, Testcontainers PostgreSQL).
- Build: `dotnet build GChannel.slnx`. Tests: `dotnet test --project tests\GChannel.Tests\GChannel.Tests.csproj`
  (SDK 10 syntax — no `--` separator, no `--logger trx`). Stop a running AppHost before building (file locks).
- Deployment is `azd` (`azure.yaml` → AppHost): `azd provision --preview` to see changes, `azd deploy` for
  images only, `azd up` for both. Environment `gchannel`, resource group `rg-gchannel` (Sweden Central).
  `.azure/` is git-ignored and contains secrets — never commit or print its values.
- All Azure data-plane access is **managed identity** (Postgres Entra-only, Service Bus local-auth off,
  Redis access-key auth off, Key Vault references). Do not introduce connection strings with secrets.
- The Service Bus topology (topic, subscriptions, queues **including `wolverine-dead-letter-queue`**) is
  declared in `AppHost.cs` and mirrored by `MessagingTopology`; Wolverine never auto-provisions. Add any
  new queue/topic in both places.
- The API and the worker use separate Wolverine message-store schemas (`wolverine_api`, `wolverine_worker`).
- Production loads **pre-generated** Wolverine code (`TypeLoadMode.Static`): the azd `predeploy` hook runs
  `codegen write` into the git-ignored `Internal/Generated/` folders, which only Release builds compile. Never
  commit them. If a host won't start after a deploy, check that codegen ran. Both hosts end with
  `RunJasperFxCommands(args)`, so start-up side effects (e.g. dev seeding) belong in `ApplicationStarted`, not
  top-level code.
- UI pages must keep the `data-onboarding` / `data-walkthrough` attributes (Driver.js tours) and go through
  `UiNotifier` for toasts/confirms. Use Radzen `rz-*` utilities, not Bootstrap classes.
- Show dates with `DisplayFormat.Date` / `DisplayFormat.Timestamp` (`dd-MM-yyyy` / `dd-MM-yyyy HH:mm`, UTC),
  never ad-hoc `ToString("…")` date formats or `LocalDateTime`.
- Docs to keep in sync when behaviour changes: `docs/architecture.md`, `docs/configuration.md`,
  `docs/deployment.md`, `README.md`.
- Commit messages: imperative subject, explanatory body, trailer
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. Use
  `git -c core.safecrlf=false commit` to silence CRLF noise.
