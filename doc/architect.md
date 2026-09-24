# NewHorizon Automation Agent — Architecture

| | |
|---|---|
| **Solution** | `NewHorizon.AutomationAgent.slnx` |
| **Platform** | .NET 10 · Windows Service · SQL Server |
| **Baseline** | `Plan/NewHorizon_AutomationAgent_Design_v2.md`, as amended by `.claude/context/` |
| **Status** | Scaffolded and building; AutoShopCycle is the one live workflow |

> A Word copy can be produced from this document with `python doc/make_architect_docx.py`.
> This file is the source of truth; the `.docx` is a generated artifact and is not committed.

---

## 1. Purpose and scope

The Automation Agent runs unattended alongside the NewHorizon ERP and drives the document chain that
planners would otherwise walk by hand. It is a separate process with its own database, talking to the
ERP only over HTTP APIs, so it can be installed, upgraded, paused or removed without touching the ERP
deployment.

This document describes the architecture **as built**. Where it differs from the design baseline the
difference is deliberate and recorded in `.claude/context/` — those notes win over the design doc, and
the two largest are noted in §2 and §8.

---

## 2. Business context

### 2.1 Who performs which transition

The agent does not own the whole chain, and knowing where it starts and stops is the single most
load-bearing fact in this design.

| Transition | Performed by | Consequence for the agent |
|---|---|---|
| SO → OAF | A person, inside the ERP | Automation begins *after* this. There is no ERP event to push, so the agent is timer-driven. |
| OAF → SJO | **The agent** | First stage of the cycle. |
| SJO → CBOM | The ERP itself | The agent waits and verifies; it does not create the CBOM. |
| SJO sequencing | **The agent** | Per site: fetch rows, sort by delivery date, flag, post back. |
| AutoShop | **The agent** | Per site, after sequencing. |

### 2.2 Deployment shape

Every client gets their own on-premise Windows server, their own agent installation, their own
automation database and their own ERP. There is therefore **no company or tenant dimension anywhere**
in the solution — no `Company` column, no tenant id in the API surface. Site ID is a real dimension
and is modelled; tenancy is not.

---

## 3. Architectural invariants

These five constraints are what make the design safe. Violating any one of them silently breaks
duplicate-safety or the ERP boundary, so they are stated as rules rather than preferences.

- **API-only in both directions.** ERP → Agent for control and read; Agent → ERP for execution.
  Neither side ever opens the other's database. No EF entity, connection string or SQL in this
  solution points at the ERP database.
- **The agent never writes ERP data directly.** Every mutation goes through an ERP application API,
  so ERP validation, permissions, audit and transactions all still apply.
- **AI is never in the execution path.** `IDecisionService` only *recommends* — vendor, priority,
  risk. Every create is a deterministic ERP API call.
- **Everything is idempotent and resumable.** At job level, a unique filtered index on the
  idempotency key; at operation level, a stored `ErpDocumentRef` or a query-before-create. Both
  layers must hold, because more than one trigger can reach the same document.
- **Automation is license and config gated.** Switched off, the ERP behaves exactly as it does today.

---

## 4. Solution structure

Clean Architecture, with dependencies pointing inward only. Domain references no other project;
Application defines the ports; Infrastructure and ErpClient implement them; the Worker composes
everything and hosts the process.

```
NewHorizon.AutomationAgent.slnx
├── src/
│   ├── NewHorizon.Automation.Worker          Windows Service host + management/read API
│   ├── NewHorizon.Automation.Application     use cases, workflow orchestration, ports
│   ├── NewHorizon.Automation.Domain          entities, workflow model, state machine (pure)
│   ├── NewHorizon.Automation.Infrastructure  EF Core, hosted services, Serilog
│   └── NewHorizon.Automation.ErpClient       typed ERP HTTP clients + Polly + auth handler
└── tests/{UnitTests, IntegrationTests}
```

| Project | Holds | Depends on |
|---|---|---|
| **Domain** | `Job`, `JobStep`, `AutomationLog`, `AutomationError`, `AutomationConfig`, the job state machine, the idempotency key | *nothing* |
| **Application** | Workflow definitions, `WorkflowEngine`, enqueue/claim use cases, ports (`IErpClient`, `IJobRepository`, `IWorkflowEngine`, `IDecisionService`, `INotificationService`, `IClock`) | Domain |
| **Infrastructure** | EF Core `DbContext`, repositories, the hosted services, notifications, clock | Application, Domain |
| **ErpClient** | `HttpErpClient`, `ErpTokenProvider`, `ErpAuthHandler`, the Polly pipeline, health check | Application, Domain |
| **Worker** | Program composition, Kestrel, minimal API endpoints, Serilog, options validation | all of the above |

The practical test of the rule: *a workflow can be added by writing a `WorkflowDefinition` in
Application, with no change to the engine, the queue, retry, logging or the API surface.*

---

## 5. Execution model

### 5.1 Four levels

| Level | Meaning | Persisted as |
|---|---|---|
| Workflow | One run for one document (or one cycle) | `AutomationJob` — one row |
| Stage | SJO / OAF / MIL / CBOM / AutoShop, run in order | grouping column on the steps |
| **Operation** | An API-group inside a stage — **the checkpoint unit** | `AutomationJobStep` — one row each |
| ERP API call | A single HTTP request and its outcome | `AutomationLog` — one row each |

The agent checkpoints after *every* operation — status, `ErpDocumentRef` and payloads — before
advancing. Resume is therefore defined without ambiguity: **the first operation whose status is not
`Completed`**.

### 5.2 Job states

```
Pending ──► Running ──┬──► Completed
                      ├──► Failed            ──(retry/resume)──► Running
                      ├──► AwaitingApproval  ──(approve)───────► Running
                      └──► Cancelled
```

| Concern | Rule |
|---|---|
| `resume` vs `approve` | `resume` is failure recovery. `approve` / `reject` are business decisions on an `AwaitingApproval` gate and must record actor and remarks for audit. The approval UI never calls `resume`. |
| What is retried | Only transient failures — timeout, 5xx, network drop, open circuit breaker — retry with backoff and jitter. A business refusal goes straight to human review with a layman message and is never retried. |
| Manual retry | Re-queues the job at elevated priority; the claiming query orders by `Priority`. |
| Claiming | `UPDATE TOP (@batch) … WITH (UPDLOCK, READPAST)`, so parallel workers skip locked rows instead of blocking on them. |
| Error messages | Every error carries both a `TechnicalMessage` and a `LaymanMessage`; the ERP UI shows the layman one by default. |

---

## 6. The AutoShop cycle

The only live workflow. A timer enqueues one cycle, `JobDispatcherService` claims it, and the engine
walks its stages.

| Stage | Operation | What it does |
|---|---|---|
| `OafToSjo` | `CreateSjoFromPendingOaf` | Fetch OAFs awaiting an SJO and create them. Nothing pending is the normal quiet case, not a failure. |
| `Discovery` | `DiscoverSites` | Fetch the site list from the ERP and expand the plan to one step per site. |
| `SjoSequence` | `SequenceSite` (per site) | GET the SJO rows for the site, sort by delivery date, set the selection flag, POST back. |
| `AutoShop` | `AutoShopSite` (per site) | GET, build the body, POST — once per site. |

### 6.1 Two things this workflow does differently

**A cycle has no document.** Its `DocumentId` is its start timestamp and its `DocumentType` is
`Cycle`, so the per-document idempotency key cannot express "only one at a time". A second filtered
unique index, `UX_AutomationJob_LiveCycle`, admits one live cycle per workflow type — excluding
`Completed` as well as `Cancelled`, because unlike a document a cycle is meant to run again.
`JobRepository.FindLiveEquivalentAsync` mirrors whichever index applies.

**The agent holds no business logic here.** Each operation is GET → build body → POST. The rows travel
as `JsonObject`, not a typed model, so every property the ERP sent comes back untouched; the agent
only sorts by delivery date and sets one flag. Typing the row would silently drop fields on the way
back.

Because the sites are discovered rather than declared, each site is its own checkpoint: *a failure at
the seventh site resumes at the seventh site.*

---

## 7. Triggers and hosted services

Three trigger sources funnel into one idempotent `enqueue`, so execution logic is written once and
duplicate-safety is proved in one place:

1. ERP push on Sales Order save, fire-and-forget
2. The `Pending` job set itself, acting as the internal queue — there is no external broker
3. A five-minute reconciliation poll asking the ERP for documents with no started job

For the AutoShop cycle specifically **only the timer applies**: automation begins after a manually
authorised OAF, so there is no ERP-side event to push.

| Hosted service | Responsibility |
|---|---|
| `CycleSchedulerService` | The cycle's only trigger. A tick that finds a cycle already running does nothing — the normal case, not an error. |
| `JobDispatcherService` | Claims `Pending` jobs and runs them through the engine, up to the configured parallel worker count. |
| `OrphanRecoveryService` | Sweeps up jobs left `Running` by a process that stopped, so a restart does not strand work. |
| `ErpLoginStartupService` | Signs in to the ERP as soon as the agent starts, so the token is warm and a wrong password surfaces at startup rather than mid-job. |

All four are registered separately from `AddAutomationInfrastructure()` — by
`AddAutomationHostedServices()` and `AddErpLoginStartup()` — so an integration-test host can compose
the same application without a live timer or a real sign-in.

---

## 8. ERP integration

### 8.1 Authentication

The ERP has no client-credentials endpoint, so the agent signs in through the same call the ERP UI
makes. **This supersedes §15 of the design baseline.**

```http
POST {BaseUrl}/api/v1/auth/login
{ "userName": "…", "password": "…", "connStr": "Server=…;Database=…",
  "isCEFlag": false, "appID": "", "userId": "" }

200 → { "data": { "token": { "value": "<jwt>", "validTo": "…Z" } },
        "success": true, "message": null }
```

- **The body decides the outcome, not the status code.** A refusal arrives as HTTP 400 with
  `success: false` and a message key (`InvalidUsernamePasswordKey`), not as a 401.
- **Tokens last 24 hours** and the response states an absolute `validTo`, which is what the cache
  honours. The configured `TokenTtlHours` is only a fallback for a response that omits it.
- **`connStr` is the ERP's own database**, parsed by the ERP to resolve the login. The agent never
  opens it, and it is never the automation database.

`ErpTokenProvider` holds one token for the whole process, so a stampede of parallel workers causes one
login rather than N, and logs *"ERP login successful"* with the timestamp and expiry.
`ErpAuthHandler : DelegatingHandler` attaches the token to every call, refreshes inside a two-minute
margin so a token cannot lapse mid-request, and on a 401 re-authenticates and replays the request
exactly once — a second 401 is a real authorisation problem that retrying would only hide. Operation
code never sees a token, an expiry or a 401.

### 8.2 Resilience pipeline

Registration order is execution order, outermost first. Resilience **wraps** auth, so a retried
attempt re-enters the auth handler and picks up a refreshed token; the other way round, a retry after
a long backoff could replay a token that had since expired.

```
total timeout ─► retry (exponential + jitter) ─► circuit breaker ─► attempt timeout
```

Only transient conditions are retried or counted against the breaker. A 400 for a missing vendor is
deterministic: retrying produces the same answer and would wrongly trip the breaker against a
perfectly healthy ERP.

### 8.3 Error classification

| Exception | Transient | Outcome |
|---|---|---|
| `ErpTransientException` | yes | Timeout, 5xx, network drop, open breaker — retried with backoff. |
| `ErpBusinessException` | no | The ERP understood and refused. Straight to human review with the layman message. |
| `ErpAuthenticationException` | yes | Could not obtain a token. Transient by nature, but called out separately because the usual cause is a wrong password, which should be obvious in the log rather than buried in retries. |

### 8.4 Endpoints as configuration

ERP paths (`AutomationAgent:ErpEndpoints`) and the SJO row property names (`AutomationAgent:AutoShop`)
are **configuration, not code**, because most are still unconfirmed by the ERP team. Correct them
there rather than editing and rebuilding. Only `SiteList` and `SjoSequenceTemplate` are confirmed.

---

## 9. Data model

A separate automation database. EF Core migrations live in
`Infrastructure/Persistence/Migrations`, with `deploy/sql/001_Schema.sql` as an idempotent equivalent
for servers where `dotnet ef` cannot run.

| Table | Holds | Notable indexes |
|---|---|---|
| `AutomationJob` | One row per workflow run: status, priority, mode, document identity, idempotency key, timestamps | `IX_AutomationJob_Claim`; `UX_AutomationJob_IdempotencyKey_Live` (filtered, `Status <> Cancelled`); `UX_AutomationJob_LiveCycle` |
| `AutomationJobStep` | One row per operation — the checkpoint unit — with status, `ErpDocumentRef` and payloads | `UX_AutomationJobStep_Job_Sequence`; `IX_AutomationJobStep_Job_Status` |
| `AutomationLog` | One row per ERP API call, with correlation id and timings | `IX_AutomationLog_JobId`; `IX_AutomationLog_CorrelationId`; `IX_AutomationLog_StartedAtUtc` |
| `AutomationError` | Failures with both technical and layman messages | `IX_AutomationError_JobId`; `IX_AutomationError_CreatedAtUtc` |
| `AutomationConfig` | Per-module runtime behaviour, edited through the UI | `UX_AutomationConfig_Module` |

`READ_COMMITTED_SNAPSHOT` is on, so the read API's queries never block the workers' job-claiming
`UPDLOCK`/`READPAST` updates.

---

## 10. Management and read API

A minimal API hosted inside the Worker, for the ERP only. Two boundaries protect it: loopback-only
Kestrel binding on the outside, a shared inbound API key on the inside.

| Route | Purpose |
|---|---|
| `GET /api/automation/jobs` | List jobs, for the ERP's monitoring view. |
| `GET /api/automation/jobs/{id}` | One job with its steps. |
| `GET /api/automation/jobs/{id}/errors` | Errors for a job — layman message by default. |
| `POST /api/automation/jobs/{id}/retry` | Re-queue at elevated priority. |
| `POST /api/automation/jobs/{id}/resume` | Failure recovery — resume at the first incomplete operation. |
| `POST /api/automation/jobs/{id}/cancel` | Cancel a job. |
| `POST /api/automation/run-now` | Enqueue a cycle immediately, without waiting for the timer. |
| `GET /api/automation/dashboard` | Aggregate counts for the ERP dashboard. |
| `GET /api/automation/config[/{module}]`, `POST …/{module}` | Read and update per-module runtime configuration. |
| `GET /health`, `GET /api/automation/health` | Liveness and readiness, including database and ERP reachability. |

---

## 11. Configuration

The split is deliberate and worth preserving:

| Bootstrap — `appsettings.json` | Runtime — `AutomationConfig` table |
|---|---|
| SQL connection string; ERP base URL, login path and credentials; host port, loopback binding and inbound API key; ERP endpoint paths; defaults (poll interval, parallel workers, max retry) | Full/Partial mode, working hours, retry count, parallel workers, retention windows — per module, changed through the UI, never in the file |

The agent reads runtime config **fresh at the start of each job**; a job already running keeps the
mode it captured when it was created.

**On secrets:** the automation database connection string and the inbound API key belong in a
protected store — user-secrets locally, environment variables or DPAPI on a server. The ERP login
credentials are the deliberate exception and sit in `appsettings.json` in clear, at the client's
request: the agent runs on a private network on the client's own server, and the API port changes per
installation, so an operator must be able to correct all of it without a rebuild.

---

## 12. Deployment

The Worker is hosted as a Windows Service. `ContentRootPath` is pinned to the binary location, because
a service's working directory is `%WINDIR%\System32`, which would otherwise hide `appsettings.json`.

| Asset | Purpose |
|---|---|
| `deploy/install.ps1` | Publish, install and start the service. |
| `deploy/update.ps1` | Stop, replace binaries, restart. |
| `deploy/uninstall.ps1` | Stop and remove the service. |
| `deploy/sql/001_Schema.sql` | Every migration as one idempotent script, safe to re-run and safe against a partly migrated database. |
| `deploy/README.md` | Database and secret setup, and the SQL login the agent should use. |

Logging is Serilog. The ERP sign-in writes an Information line with the timestamp and the token's
expiry, so a failed or stale login is visible in the log without attaching a debugger.

---

## 13. Testing

Two projects. Unit tests cover the pure Domain and Application logic — the state machine, idempotency
keys, token expiry arithmetic, options validation. Integration tests host the real application and a
real Kestrel stub of the ERP, rather than a stubbed message handler, so the actual pipeline is
exercised: sockets, status codes, headers, JSON, and the auth handler's 401 replay. A mutable clock
lets token expiry be tested without waiting for it.

---

## 14. Open questions

Carried from §18 of the design baseline. These are to be **confirmed, not invented**:

- Whether ERP create endpoints accept an idempotency key — this decides how much query-before-create
  logic each operation needs.
- The exact operation lists for the CBOM and AutoShop stages.
- Which operations require approval in Partial mode.
- Whether the AutoShop APIs' site-scoped batch shape can be reconciled with per-document jobs, or
  whether the cycle remains the unit of work.
- The real notification channel, currently served by a log-based placeholder.
