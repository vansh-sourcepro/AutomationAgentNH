# AutomationAgent — what each part actually does

Plain-English reference for `AutomationAgentNH`, written from a direct read of the current code
(not from the design doc's original intentions). It skips pure data-shape files (DTOs, EF migration
files) except where naming what they carry helps explain a function. If something here ever looks
wrong, trust the code over this file and let a future session fix the mismatch.

## 1. The big picture

This agent does two unrelated jobs for the same ERP:

1. **Indent → PO** — take an already-authorised purchase indent and turn it into a purchase order in
   the ERP, automatically. This is the feature the PO Automation screen, its scheduler, and the
   `Skipped`/`Failed` status work all belong to.
2. **AutoShop Cycle** — a separate, older, more general "workflow engine" that walks a document
   (right now, one kind: the AutoShop cycle) through a sequence of ERP operations, stage by stage,
   with automatic retry and resume if the process crashes mid-way.

They share almost nothing at runtime — different trigger, different tables, different code paths —
but they live in the same solution and the same running process (`NewHorizon.Automation.Worker.exe`).

The five projects, and who is allowed to depend on whom (arrows point "depends on"):

```
Worker  →  Infrastructure  →  Application  →  Domain
Worker  →  ErpClient       →  Application  →  Domain
```

- **Domain** — the business rules, as C# classes that refuse to be misused (e.g. `Job.Fail()` throws
  if the job isn't `Running`). No dependency on anything else in the solution.
- **Application** — the use cases and the *interfaces* other layers must implement (`IErpClient`,
  `IJobRepository`, `IWorkflowEngine`...), plus the AutoShop workflow engine itself and its stage
  definitions.
- **Infrastructure** — the concrete database code (EF Core) and the background timers/dispatchers
  that actually run continuously.
- **ErpClient** — everything that makes an HTTP call to the ERP: login, the Indent → PO logic, the
  AutoShop ERP calls.
- **Worker** — the actual `.exe`. Wires everything together at startup and exposes the HTTP API.

## 2. Domain layer — the rules, as objects that enforce themselves

### `Job` (`Domain/Jobs/Job.cs`) — one attempt at one document, for the AutoShop engine

A `Job` is a little state machine. Every method either succeeds and moves the status forward, or
throws `InvalidJobTransitionException` if you tried it from the wrong state — this is what stops a
bug from silently corrupting a job's history.

| Method | What it does |
|---|---|
| `Create(...)` | Makes a brand-new job, status `Pending`. |
| `PlanSteps(...)` | Lays down the ordered list of operations this job will do. Can only be called once. |
| `Claim(now)` | `Pending → Running`. A dispatcher calls this when it picks the job up to work on. |
| `NextStep()` | Returns the first operation that isn't finished yet — this is how the engine (and a crash-resume) knows where to continue. |
| `PauseForApproval(...)` | `Running → AwaitingApproval`. Used when a human must sign off before continuing (Partial mode only). |
| `Approve(...)` / `Reject(...)` | A human's decision at that gate — resume, or cancel with a reason. |
| `Fail(now)` | `Running → Failed`. A technical/system problem stopped the job. |
| `RequeueForRetry(boost)` | `Failed → Pending`, with priority raised so a manually-retried job jumps the queue. |
| `MarkResumable()` | Used by the orphan-recovery sweep: a job whose worker crashed goes back to `Pending` without touching its retry count (it wasn't the job's fault). |
| `Complete(now)` | `Running → Completed`, only once every step is finished. |
| `Cancel(actor, reason, now)` | Ends the job early, with an audit trail of who and why. |
| `ExpandPlan(...)` | Lets a running operation discover more work partway through (e.g. "turned out there were 3 sites, not 1") and add steps to the same job. |

### `JobStatus`, `RunStatus`, `StepStatus` — the three status vocabularies, and why there are three

- `JobStatus` (`Pending, Running, AwaitingApproval, Failed, Skipped, Completed, Cancelled`) — one
  **attempt at one document/indent**. `Failed` = a technical/system error; `Skipped` = a business-rule
  refusal (added this session — see §6 for exactly how the two get told apart).
- `RunStatus` (`Running, Completed, Failed, Cancelled`) — one **trigger invocation** (e.g. one click of
  "Run now", or one scheduler tick). Deliberately shorter: a run is never claimed, retried, or paused
  for approval — only the jobs inside it are.
- `StepStatus` — one **operation inside a job** (AutoShop only): `Pending, Running, Completed, Failed,
  Skipped`.

### `AutomationRun` (`Domain/Jobs/AutomationRun.cs`) — one trigger invocation, Indent → PO only

| Method | What it does |
|---|---|
| `Start(...)` | Opens a run: records who triggered it, what it's allowed to touch (indent types, sites), and starts it as `Running`. |
| `RecordProgress(examined, jobsCreated, posCreated)` | Updates the running totals as the sweep goes, so a run killed mid-flight still shows how far it got. |
| `Complete(now)` | The trigger invocation itself finished — **not** the same as "everything converted successfully". A run that examined 40 indents and converted none is still `Completed`. |
| `Fail(reason, now)` | The run itself broke (e.g. the ERP was unreachable before it could even look at an indent) — a per-indent refusal is *not* this. |
| `Cancel(reason, now)` | Manually stopped. |

### `IndentPoAutomationConfig` (`Domain/Flows/IndentToPo/IndentPoAutomationConfig.cs`) — the three rows behind the PO Automation screen

Exactly three rows exist (Regular/Capital/Service), seeded once by migration, never created or
deleted through the API.

| Method | What it does |
|---|---|
| `Update(update, now, updatedBy)` | Applies whichever fields were sent (schedule time, run mode, sites, indent numbers, active/dry-run flags) to **this same row** — always a plain in-place field change, never a delete-and-recreate. If the schedule time actually changes, it also clears `LastScheduledRunDate` so the row gets a fresh chance to fire today at the new time instead of waiting until tomorrow. |
| `ShouldRunOnSchedule(now, today)` | The scheduler's yes/no check: master toggle on, mode is `Timer`/`Both`, a time is set, that time has passed, and it hasn't already run today. |
| `MarkScheduledRun(...)` / `MarkManualRun(...)` | Stamp the "Last Run" bookkeeping columns the screen shows — the first also claims today's slot so the scheduler can't fire the same row twice in one tick window. |
| `SiteIds()` / `IndentNumberList()` | Turn the stored comma-separated text into a real list, or an empty list meaning "use the default." |

### `IndentPoOutcome` (`Domain/Flows/IndentToPo/IndentPoOutcome.cs`) — one vendor-group's result

One indent can produce several purchase orders (one per vendor/currency/rate-structure group), so this
is a child row, not a column on the job.

- `Created(...)` — a PO was actually made; carries its number and line count.
- `Skipped(...)` — nothing was ordered and it isn't a fault (nothing outstanding, no vendor master data).
- `Failed(...)` — the ERP refused the create, or it threw, for this specific vendor group.

### `AutomationError` — one failure, with two messages

Every failure is recorded with a `LaymanMessage` (shown to the ERP user) and a `TechnicalMessage`
(the real diagnostic detail, for support). `ErrorType` (`Technical`/`Business`) drives retry policy:
only `Technical` failures are ever auto-retried.

## 3. Application layer — the use cases

### `ProcessJobService` / `ProcessJobOrchestrator` — the Indent → PO tracking funnel

This is the class that turns "a conversion happened" into database rows, and decides `Failed` vs
`Skipped`.

| Method | What it does |
|---|---|
| `StartExecutionAsync(...)` | Opens one `AutomationJob` row for one indent, status `Running`. |
| `FailExecutionAsync(layman, technical, transient, ct)` | **The Fail-vs-Skip decision point.** `transient: true` (a timeout/5xx/network problem) → `job.Fail()`, `ErrorType.Technical`. `transient: false` (an `ErpBusinessException` — not authorised, LBT item, wrong document status, or a genuinely unexpected bug) → `job.Skip()`, `ErrorType.Business`. Either way it also records the child `AutomationError` row. |
| `CompleteExecutionAsync(outcomes, ct)` | The job finished — even if it ordered nothing, as long as that's a legitimate "nothing outstanding" answer. Records every vendor-group's `IndentPoOutcome` alongside it. |
| `CompleteRunAsync(examined, ct)` / `FailRunAsync(reason, examined, ct)` | Closes the `AutomationRun` — the trigger invocation, not the individual indent. |
| `RunTrackedAsync(...)` (orchestrator) | Wraps a conversion: opens the run, calls the actual conversion delegate, closes the run either way, and maps an `ErpException` to the same 400/503 split the untracked endpoint uses. |

### `WorkflowEngine` (`Application/Workflows/WorkflowEngine.cs`) — the AutoShop step-by-step runner

`RunAsync(job, ct)` is the whole engine, as a loop:

1. Ask the job for its `NextStep()`. None left → `job.Complete()`, done.
2. Look up that step's real definition (`WorkflowCatalog`). Missing (the workflow was redefined while
   this job was mid-flight) → fail loudly rather than guess.
3. If this operation needs human approval in `Partial` mode and hasn't been approved yet → pause the
   job and stop.
4. Check the operation's precondition, if it has one. Doesn't hold → mark the step `Skipped`
   (a legitimate outcome, not an error) and move to the next step.
5. Mark the step `Running` and **save immediately, before the ERP call** — so if the process dies
   mid-call, recovery sees "Running" and knows to re-enter this exact step (and query the ERP first,
   rather than blindly retrying a create).
6. Make the actual ERP call (`ExecuteAsync`) — or, for a "the ERP might already be doing this itself"
   operation, `VerifyAsync` asks the ERP what happened instead of acting, so the agent never races the
   ERP's own automation and creates a duplicate document.
7. Apply the result: success → step `Complete`, maybe expand the plan with newly-discovered steps;
   skipped → step `Skipped`; a transient ERP problem → back off and retry (or fail if retries are
   exhausted); a business refusal → fail the whole job with a human-readable reason.

### Workflow definitions (`Application/Workflows/Definitions/*.cs`)

`SjoWorkflow`, `OafWorkflow`, `CbomWorkflow`, `MilWorkflow`, `AutoShopWorkflow`,
`AutoShopCycleWorkflow` — these don't run anything themselves. Each is just a declared list of
**stages**, each stage a list of **operations**, each operation a small function reference plus
metadata (does it need approval in Partial mode, does it verify-first, how long to wait). The engine
above is the only thing that actually executes them. `AutoShopCycleWorkflow.DiscoverSitesAsync` is
the one operation worth naming specifically: it calls `erpClient.GetSitesAsync()` to get every site
from the ERP and turns that into one pair of steps (SJO sequence + AutoShop) per site — this is the
dynamic, ERP-driven site discovery that the Indent → PO feature notably does *not* use (see §7).

## 4. ErpClient layer — the functions that actually talk to the ERP

### `IndentDiscovery` — "which indents can even be considered"

- `ResolveSites(request)` — picks the site list: the request's own sites, else the config's
  `PurchaseOrder:Sites` array, else (only if that's also empty) a single fallback site with a logged
  warning. Never "every site the ERP has" despite what one doc comment claims — see §7.
- `FindEligibleMaterialIndentsAsync(...)` — sends **one combined request** to the ERP's
  `indententry/indentEntryList`, naming every resolved site at once (`LocationIds: "1,2,4,7,8"`) and
  `Status: "A"` (the ERP's own "authorised" flag). Pages through the results (200 at a time), and only
  *after* the ERP answers does it filter down to specific indent numbers the caller asked for — that
  filter is never sent to the ERP.

### `IndentConversion` — the per-indent business rules and the actual conversion

- `GuardIndentIsConvertible(indent, detail)` — throws a business refusal if the ERP's own `authStatus`
  doesn't say authorised, or `docStatusDisp` isn't `Open`.
- `GuardNoLbtItems(indent, detail)` — throws a business refusal if any item line is LBT (dimensioned),
  because the ERP's own over-order guard is scoped to skip exactly these lines, which would silently
  under-order rather than error.
- `ConvertAsync(...)` — the whole per-indent funnel: checks the master toggle, runs the two guards
  above, and (via the private `ConvertCoreAsync`) resolves vendors, builds the PO payload(s), and
  calls the ERP to create them. Its `catch` blocks are exactly what decides `Failed` vs `Skipped` —
  see §3's `FailExecutionAsync`.
- `ConvertEligibleAsync(...)` — the sweep: calls discovery, then `ConvertAsync` for each result up to
  the requested max.

### `VendorResolver` / `IndentPoPayloadBuilder` — turning items into orderable groups

Groups an indent's items by (vendor, currency, rate structure) — the ERP's own Bulk-PO break rule —
resolving each item's vendor via `ItemVendorPurchase/list` (the vendor marked `isDefault`, else the
lowest `vndpriority`). Builds the actual PO creation payload per group, pulling in document control
(financial year), the PO address, vendor info, and rate structure along the way.

### `ServiceIndentDiscovery` / `ServiceIndentConversion` / `ServicePoPayloadBuilder`

Same shape as the material flow above, but for **service** indents — a genuinely different ERP
module with its own tables and endpoints (`ServiceIndentCont/indentEntryList`, `getSerIndentDetail`,
`itemservices/getItemServiceVendorDetail`, `ServicePO/createServicePOEntry`). Notably: the service
list has no site/status filter of its own (an indent converted last week can still appear), there's
no "preferred vendor" concept, and the create payload is hand-typed rather than echoed through like
the material flow's.

### `HttpErpClient` — the AutoShop-side ERP calls, plus one shared utility

Implements every ERP call the AutoShop workflow definitions reference (SJO creation, OAF processing,
sequence submission, etc.) and `GetSitesAsync()` — the one call that returns every site the ERP has,
via `GET /api/v1/admin/location/list`.

### Authentication — `ErpTokenProvider` and `ErpAuthHandler`

- `ErpTokenProvider.GetTokenAsync()` — returns the cached token if it's still valid; otherwise takes a
  lock (so N parallel calls at expiry produce exactly one login, not N), logs in
  (`POST /api/v1/auth/login`), and separately calls `AddUserSession` — the ERP's session-gate
  middleware requires this second call or every other endpoint 401s regardless of the token.
- `ErpAuthHandler.SendAsync(request, ct)` — a `DelegatingHandler` sitting in front of every outgoing
  ERP call: attaches the bearer token and the `CompanyId` header automatically. On a 401, it
  invalidates the cached token, re-logs in, and retries **exactly once** — a second 401 after that is
  treated as a real authorisation problem, not something worth looping on.

## 5. Worker layer — the process itself

### Hosted services (run forever, in the background, independent of any browser)

| Service | What it does |
|---|---|
| `PoAutomationSchedulerService` | Ticks every 1 minute. For each of the 3 config rows whose `ShouldRunOnSchedule` says yes, claims today's slot and calls `POST /api/automation/indent-to-po/convert?trigger=timer` on itself over loopback HTTP — the exact same endpoint "Run now" hits. |
| `JobDispatcherService` | Ticks (interval from config), claims a batch of `Pending` AutoShop jobs (`UPDLOCK`/`READPAST` so parallel agents never double-claim), and runs each through `WorkflowEngine.RunAsync` in parallel, one DB scope per job. |
| `CycleSchedulerService` | Ticks every `ReconcileIntervalMinutes`, and enqueues one AutoShop cycle if none is already live. |
| `OrphanRecoveryService` | Every 5 minutes, finds jobs stuck `Running` for more than 30 minutes (a crashed worker) and calls `job.MarkResumable()` so the next dispatcher pass picks them back up at their last checkpoint. |

### Endpoint groups (`Worker/Endpoints/*.cs` shared; `Worker/Flows/IndentToPo/Endpoints/*.cs` for the first three) — what's exposed over HTTP, and who's allowed to call it

| Group | Routes | Guarded by |
|---|---|---|
| `PurchaseOrderEndpoints` | `POST /indent-to-po` (+ `/convert` alias), `GET /indent-to-po/eligible`, `POST /indent-to-po/vendor` | `ErpUserOrApiKeyFilter` + form `011171` (Edit for convert, Inquiry for eligible) on the first two; API-key-only on `/vendor`. |
| `PoAutomationEndpoints` | `GET /`, `PUT /enabled`, `GET/PUT /{indentType}` | ERP bearer token required (`.RequireAuthorization()`) + form `011171` Inquiry/Edit. **No API key accepted here** — this is why my own live test of the schedule-time endpoint got refused. |
| `ProcessJobEndpoints` | `POST/GET /`, `GET /summary`, `GET /daily-summary`, `GET /{jobId}`, `GET /indent/{id}`, `POST /{jobId}/retry`, `GET /runs(/{id})` | API key or ERP token, + form `011172` Inquiry. |
| `JobEndpoints` | `GET/POST /jobs*`, `POST /run-now`, `GET /dashboard` | API key (dashboard also accepts a bearer token). |
| `ConfigEndpoints` | `GET/POST /config*` | API key only — per-module runtime settings (poll interval, retry count, working hours), not exposed to the ERP UI at all. |
| `HealthEndpoints` | `GET /health`, `GET /api/automation/health` | Nothing — deliberately open, loopback-bound, for monitoring. |

## 6. Two things worth walking through end to end

**A manual "Run now" click, Regular indents, no numbers named:**
`PurchaseOrderEndpoints.ConvertAsync` (trigger=manual) → checks the master toggle
(`IPoAutomationGate`) → `ProcessJobOrchestrator.RunTrackedAsync` opens an `AutomationRun` →
`IndentToPoService.ConvertEligibleAsync` → `IndentDiscovery` (1 ERP call per page, all sites at
once) → per indent, `IndentConversion.ConvertAsync`: `ProcessJobService.StartExecutionAsync` opens
the `AutomationJob`, the two guards run, vendor resolution + PO creation happen, and either
`CompleteExecutionAsync` (success, `IndentPoOutcome` rows) or `FailExecutionAsync` (`Skip` for a
business refusal, `Fail` for a technical one) closes it out → back at the top, `CompleteRunAsync`
closes the run and `StampConfigsAsync` updates the config row's Last Run columns.

**The daily scheduler firing a Timer-mode row:** `PoAutomationSchedulerService` tick →
`ShouldRunOnSchedule` says yes → claims the slot (`MarkScheduledRun`, saved immediately) → calls
`POST .../convert?trigger=timer` on itself → from here on, identical to the manual path above,
because it's the same endpoint and the same `IndentToPoService` code.

## 7. ERP calls per conversion — the honest count

For **one indent that produces one purchase order** (material flow):

1. `indententry/indentEntryList` — discovery (shared across every indent examined in the sweep, not one call each).
2. `getIndentDetail` — per indent, feeds the two guards.
3. `ItemVendorPurchase/list` — per item needing a vendor.
4. `getDefaultDocumentDetail` — document control / financial year, per vendor group.
5. `GetPoAddress` — per vendor group.
6. `GetVendorInformation` — per vendor group.
7. `getratestructuredetail` / `getAllRateStructureDetails` — per vendor group.
8. `GetPendingItemsFromIndentnew` — pending lines, per vendor group.
9. `POEntry/create` — the actual write, per vendor group.

So roughly **7–9 ERP calls** for a simple one-group conversion; more if the indent's items split
across several vendor groups (steps 4–9 repeat per group); a business refusal short-circuits after
step 1–2 with zero writes. The service-indent flow costs about the same number of calls against a
different set of endpoints (§4).

## 8. A discrepancy worth knowing about: the `Sites` configuration

`AutomationAgent:PurchaseOrder:Sites` (currently `[1, 2, 4, 7, 8]`) is a hand-typed config array with
no validation against what sites actually exist. `IndentPoOptions.Sites`'s own doc comment claims
"empty means every site the ERP lists," but `IndentDiscovery.ResolveSites()` does not do that — empty
falls back to one single hardcoded site. The capability to do it properly already exists and is used
by the *other* feature: `HttpErpClient.GetSitesAsync()` / `AutoShopCycleWorkflow.DiscoverSitesAsync`
fetch every site from the ERP live, with zero configuration. The Indent → PO flow could call the same
method instead of reading a static list — this has been discussed but not implemented.
