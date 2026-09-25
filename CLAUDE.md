# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Start here (every session, every machine)

1. Read `.claude/context/` — decisions confirmed with the client that the code and the design doc
   do **not** record. Several of them override the design doc; see the note below.
2. `Plan/NewHorizon_AutomationAgent_Design_v2.md` is the architecture baseline for structure, data
   model, API surface, and execution semantics — authoritative **except** where a `.claude/context/`
   note supersedes it (notably: the workflow is a timer-driven site-scoped batch cycle, not the
   §6 per-document push model, and there is no Company/tenant dimension).
3. Local SDK is .NET 10 (`dotnet --version` → 10.0.300).

This repo is worked on by several developers from different machines, mostly through Claude Code.
The shared Claude setup — `CLAUDE.md`, `.claude/settings.json`, `.claude/context/`, `.mcp.json` — is
committed, so a fresh clone can start work from a prompt with no manual setup. Per-machine
overrides belong in `.claude/settings.local.json`, which is git-ignored. See
[`.claude/README.md`](.claude/README.md).

## Current state

The solution is scaffolded and building: all five `src/` projects, both test projects, EF Core
migrations under `src/NewHorizon.Automation.Infrastructure/Persistence/Migrations`, and `deploy/`.
Layout (§4 of the design doc):

```
NewHorizon.AutomationAgent.slnx
└── src/
    ├── NewHorizon.Automation.Worker          # Windows Service host + minimal management/read API
    ├── NewHorizon.Automation.Application     # use cases, workflow orchestration, ports (interfaces)
    ├── NewHorizon.Automation.Domain          # entities, workflow model, state machine (pure)
    ├── NewHorizon.Automation.Infrastructure  # EF Core, hosted services, Serilog
    └── NewHorizon.Automation.ErpClient       # typed ERP HTTP clients + Polly + auth handler
    (each project also has Flows/<FlowName>/ — one folder per automation flow; see below)
└── tests/{UnitTests,IntegrationTests}        # flow tests under Flows/<FlowName>/
docs/flows/<flow-name>/                       # per-flow documentation
deploy/sql/flows/<flow-name>/                 # per-flow seed scripts
```

Clean Architecture: dependencies point inward only. Domain has no project references; Application
defines ports (`IErpClient`, `IJobRepository`, `IWorkflowEngine`, `IDecisionService`,
`INotificationService`, `IClock`) that Infrastructure and ErpClient implement.

## Commands

```powershell
dotnet build NewHorizon.AutomationAgent.slnx
dotnet run --project src/NewHorizon.Automation.Worker
dotnet test
dotnet test tests/NewHorizon.Automation.UnitTests                 # one project
dotnet test --filter "FullyQualifiedName~WorkflowEngineTests"    # one class/test
dotnet ef migrations add <Name> -p src/NewHorizon.Automation.Infrastructure -s src/NewHorizon.Automation.Worker
dotnet ef database update -p src/NewHorizon.Automation.Infrastructure -s src/NewHorizon.Automation.Worker
```

The real connection string lives in `dotnet user-secrets` (Worker project), never in
`appsettings.json`, which ships placeholders only. See [`deploy/README.md`](deploy/README.md) for
the database and secret setup, and for the install/update/uninstall scripts in `deploy/`.

**Keeping an instance running while you rebuild:** never launch
`src/NewHorizon.Automation.Worker/bin/Debug/net10.0/NewHorizon.Automation.Worker.exe` directly for
anything longer than a quick check — Windows locks its loaded DLLs for the life of the process, and
`dotnet build`/`dotnet run` on the same project then fails with "cannot be copied because it is
locked" (MSB3027/MSB3021). Instead, publish a standalone copy to `run/` (gitignored) and launch
*that* — it's a different file on disk, so it never blocks a build of the source tree:

```powershell
dotnet publish src/NewHorizon.Automation.Worker -c Debug -o run
./run/NewHorizon.Automation.Worker.exe
```

Re-publish to `run/` (after stopping that copy) whenever you need the running instance to pick up
new code.

## What actually runs (AutoShopCycle)

The only live *workflow* — but not the only live feature; see Indent → PO under Automation flows
below, which runs outside the job engine entirely.

A **timer** — not the design doc's §6 ERP push, which does not apply since
automation starts after a manually authorised OAF — enqueues one cycle; `JobDispatcherService`
claims it; the engine walks it. Three hosted services in
`Infrastructure/Hosting/`: scheduler, dispatcher, orphan recovery. They are registered by
`AddAutomationHostedServices()` separately from `AddAutomationInfrastructure()` so test hosts get
the application without a live timer.

Two things about this workflow that differ from the rest of the design:

- **A cycle has no document.** Its `DocumentId` is its start timestamp and its `DocumentType` is
  `Cycle`, so the per-document idempotency key cannot express "only one at a time". A second
  filtered unique index, `UX_AutomationJob_LiveCycle`, admits one live cycle per workflow type —
  excluding `Completed` as well as `Cancelled`, because unlike a document a cycle is meant to run
  again. `JobRepository.FindLiveEquivalentAsync` mirrors whichever index applies.
- **The agent holds no business logic here.** Each operation is GET → build body → POST. The rows
  travel as `JsonObject`, not a typed model, so every property the ERP sent comes back untouched;
  the agent only sorts by delivery date and sets one flag. Typing the row would silently drop
  fields on the way back.

ERP paths (`AutomationAgent:ErpEndpoints`) and the row property names
(`AutomationAgent:AutoShop`) are **configuration**, because most are still unconfirmed by the ERP
team — correct one there rather than editing code.

## Automation flows

Each automation flow lives in its **own `Flows/<FlowName>/` folder in every layer**, with its own
namespace (`NewHorizon.Automation.<Layer>.Flows.<FlowName>`), its own tests, SQL seeds and docs.
Shared plumbing — jobs, the workflow engine, ERP auth and HTTP client, `AutomationDbContext`,
migrations, auth filters, hosted-service infrastructure — stays where it is and is used by every
flow. A flow may use the shared code; **a flow never references another flow's folder.**

| Flow | Status | Docs |
|---|---|---|
| **Indent → PO** (Regular, Capital, Service) | live, outside the job engine | [`docs/flows/indent-to-po/README.md`](docs/flows/indent-to-po/README.md) — read it before touching `Flows/IndentToPo/` |
| **AutoShop cycle** | live, through the job engine | described above; its definitions stay in `Application/Workflows/Definitions/` |
| **PO → GRN** (Regular, Capital) | backend built, outside the job engine; API key on every API (settings also accept an ERP token); no dashboard yet | [`docs/flows/po-to-grn/README.md`](docs/flows/po-to-grn/README.md) — read it and `.claude/context/po-to-grn-decisions.md` before touching `Flows/PoToGrn/` |

```
src/NewHorizon.Automation.Domain/Flows/<Name>/          entities, enums, value objects
src/NewHorizon.Automation.Application/Flows/<Name>/     ports, services, <Name>Registration.cs
src/NewHorizon.Automation.ErpClient/Flows/<Name>/       ERP calls, payload builders, options, <Name>Registration.cs
src/NewHorizon.Automation.Infrastructure/Flows/<Name>/  repositories, Configurations/ (EF), <Name>Registration.cs
src/NewHorizon.Automation.Worker/Flows/<Name>/          Endpoints/, Contracts/, Services/, <Name>Module.cs
tests/NewHorizon.Automation.{UnitTests,IntegrationTests}/Flows/<Name>/
deploy/sql/flows/<name>/                                seed scripts
docs/flows/<name>/                                      README.md + API walkthroughs
```

### Adding a new flow

1. Create the `Flows/<Name>/` folders above, only in the layers the flow needs.
2. Registration: one extension method per layer in that layer's `Flows/<Name>/` folder, plus **one line**
   in the shared composition point — `AddAutomationApplication()` / `AddProcessTracking()`
   (Application), `AddErpClient()` (ErpClient), `AddAutomationInfrastructure()` (Infrastructure),
   `AddAutomationAgentOptions()` and `Program.cs` (Worker). Copy the `IndentToPo` ones.
3. EF: put `IEntityTypeConfiguration<>` classes in `Infrastructure/Flows/<Name>/Configurations/`
   (picked up by `ApplyConfigurationsFromAssembly`), add the `DbSet`s to `AutomationDbContext`,
   then `dotnet ef migrations add <Name>...` as usual — migrations stay shared.
4. Seed scripts in `deploy/sql/flows/<name>/`; list them in `deploy/README.md`.
5. Write `docs/flows/<name>/README.md` and add a row to the table above.

## Architecture invariants

These are the constraints that make the design work. Violating any of them silently breaks
duplicate-safety, tenancy, or the ERP boundary.

- **API-only in both directions.** ERP → Agent for control/read; Agent → ERP for execution.
  Neither side ever opens the other's database. No EF entity, connection string, or SQL in this
  solution may point at the ERP database.
- **The agent never writes ERP data directly.** Every ERP mutation goes through an ERP application
  API so ERP validation, permissions, audit, and transactions apply.
- **AI is never in the execution path.** `IDecisionService` only recommends (vendor, priority,
  risk); every create is a deterministic ERP API call.
- **Everything is idempotent and resumable.** Job level: unique filtered index on
  `IdempotencyKey = hash(Company, DocumentType, DocumentId, WorkflowType)` where
  `Status <> Cancelled`. Operation level: check stored `ErpDocumentRef` or query-before-create.
  Push triggers and the reconciliation poll both run, so both layers must hold.
- **Automation is license/config gated.** Disabled ⇒ the ERP behaves exactly as today.

## The execution model

Four levels, defined in §7 of the design doc:

| Level | Persisted as |
|---|---|
| Workflow — one run for one document | `AutomationJob` |
| Stage — SJO / OAF / MIL / CBOM / AutoShop, run sequentially | grouping column on steps |
| **Operation** — API-group inside a stage, **the checkpoint unit** | `AutomationJobStep` (one row each) |
| ERP API call | `AutomationLog` |

Checkpoint after *every* operation (status + `ErpDocumentRef` + payloads) before advancing.
**Resume = first operation whose status is not `Completed`.** Adding a new workflow means adding a
new `WorkflowDefinition` (ordered stages of ordered operations) — the engine, queue, retry, logging,
and API surface must not need changes.

Job states: `Pending → Running → {AwaitingApproval, Failed, Skipped, Completed, Cancelled}`, with
`Failed → Running` on retry/resume and `AwaitingApproval → Running` on approve. `Skipped` — a
business-rule refusal, added for the Indent → PO conversion flow — is terminal like `Completed`/
`Cancelled`: there is no `Skipped → Running` transition, because retrying a business refusal without
the underlying data changing just reproduces the same refusal; a future sweep re-examines the indent
from scratch instead.

Semantics that are easy to get wrong:
- `resume` is failure recovery; `approve`/`reject` are business decisions on an `AwaitingApproval`
  gate and must record actor + remarks for audit. The approval UI never calls `resume`.
- Only transient failures (timeout, 5xx, breaker-open, network) retry with backoff+jitter and land on
  `Failed`. A business-rule refusal lands on `Skipped` (see above) and is never retried in place.
- Manual retry re-queues at elevated priority; the claiming query orders by `Priority`.
- Job claiming uses `UPDATE TOP (@batch) ... WITH (UPDLOCK, READPAST)` so parallel workers skip
  locked rows instead of blocking.
- Errors carry both `TechnicalMessage` and `LaymanMessage`; the ERP UI shows layman by default.

## Triggers

Three sources funnel into one idempotent `enqueue`: (1) ERP push on Sales Order save
(fire-and-forget), (2) the `Pending` job set itself as the internal queue — no external broker,
(3) a 5-minute reconciliation poll that asks the ERP for documents with no started job. Write
execution logic once, behind `enqueue`.

## Configuration split

`appsettings.json` holds **only bootstrap**: SQL connection string, ERP base URL + service auth,
host port / loopback binding / inbound API key, and defaults (§16). Per-tenant runtime behavior —
Full/Partial mode, working hours, retry count, parallel workers, retention windows — lives in the
`AutomationConfig` table per Company+Module and is changed through the UI, never the file.
The agent reads config **fresh at the start of each job**; a running job keeps the mode it captured
at creation.

Indent → PO has its own runtime table, `IndentPoAutomationConfig` — one row per indent type, holding
the `IndentNumbers` CSV, the daily `ScheduleTime`, and the last-run bookkeeping. Read fresh on every
scheduler tick and every management call. Seeded inert by migration (no schedule time); the ERP's
four-column PO Automation screen is the only thing that changes it.

## Auth

Agent → ERP signs in at `POST /api/v1/auth/login` with `userName` / `password` / `connStr` — the
same endpoint the ERP UI uses. There is no service-token endpoint; this supersedes §15 of the design
doc. See [`.claude/context/erp-login-authentication.md`](.claude/context/erp-login-authentication.md)
for the verified contract and why the credentials sit in `appsettings.json` in clear.

`ErpTokenProvider` holds one token for the whole process (24-hour lifetime, honouring the ERP's
`validTo`), collapses a startup stampede into one login, and logs `ERP login successful …` with the
timestamp and expiry. `ErpAuthHandler : DelegatingHandler` attaches it, refreshes ~2 min before
expiry, and re-authenticates once on 401 — operation code never touches tokens.
`ErpLoginStartupService` (registered by `AddErpLoginStartup()`, separately from `AddErpClient()`)
signs in as soon as the agent starts.

ERP → Agent is protected by a shared inbound API key plus loopback-only binding — **or**, for the
browser-facing management API, a valid ERP bearer token. `AutomationAgent:InboundJwt` mirrors the
ERP API's own JWT settings (`sourcepro.issuer` / `sourcepro.audience`). `SigningKey` defaults to the
ERP's `sourcepro-secret-key` (already hard-coded in `WebAPICore` in this repo tree); a hardened
deployment rotates that secret and overrides via env/user-secrets. `ErpUserOrApiKeyFilter` accepts
either an API key or a token on the reads WebApp2 needs (`/api/process-jobs*`,
`/api/automation/indent-to-po/eligible`, `/api/automation/dashboard`);
`/api/automation/grn-automation/*` accepts either too (a token caller is also checked against form
011171). `/api/automation/po-automation/*` requires the token (`.RequireAuthorization()`). A blank
`SigningKey` leaves the JWT scheme unregistered and the `po-automation` endpoints unmapped — the
API-key callers are unaffected. `AutomationAgent:Cors:AllowedOrigins` lists the WebApp2 origins;
`Host:BindToLoopbackOnly` must be `false` for a deployment that serves the frontend, with the port
firewalled to the ERP/web tier.

**The agent verifies the HS256 signature itself, not through `IssuerSigningKey`.** The ERP's
`sourcepro-secret-key` is 160 bits; `Microsoft.IdentityModel` 8.x (this project) hard-rejects HS256
keys below 256 bits (`IDX10720`), so `SymmetricSignatureProvider` cannot use it and every token
fails as "signature key was not found". `Program.cs` sets `TokenValidationParameters.SignatureValidator`
to a delegate that recomputes `HMACSHA256` over `header.payload` with the raw ASCII bytes and
constant-time compares — identical crypto to the ERP's older library, nothing weakened — while the
handler still validates issuer, audience and lifetime (5-min clock skew, same as the ERP).
`OnAuthenticationFailed` logs the reason under the `InboundJwt` category, because the Serilog config
drops `Microsoft.AspNetCore` below Warning and would otherwise hide it.

A 401 from the agent must **not** log the ERP user out: `WebApp2`'s `auth.interceptor.ts` only
treats a 401 as "session expired" when the failing request went to `environment.apiUrl` (the ERP
API, which owns the session).

## Open questions

§18 of the design doc lists five items to confirm before/at build — notably whether ERP create
endpoints accept an idempotency key (decides query-before-create logic), the exact operation lists
for the CBOM and AutoShop stages, and which operations require approval in Partial mode. Don't
invent answers; flag them.
