# Indent → PO

Converts authorised material (Regular, Capital) and Service indents into purchase orders through
the ERP's own APIs. The first automation flow; it runs **outside** the job queue and workflow
engine.

## Where the code lives

Each layer keeps this flow's code in its own `Flows/IndentToPo/` folder; the namespace follows
the folder.

| Layer | Folder | What is there |
|---|---|---|
| Domain | `src/NewHorizon.Automation.Domain/Flows/IndentToPo/` | `IndentKind`, `IndentPoConversion`, `IndentPoOutcome`, `IndentPoAutomationConfig`, stages, run mode |
| Application | `src/NewHorizon.Automation.Application/Flows/IndentToPo/` | tracking ports + `ProcessJobService`, `PoAutomationGate`, config repository port, `IndentToPoRegistration` |
| ErpClient | `src/NewHorizon.Automation.ErpClient/Flows/IndentToPo/` | discovery, vendor resolution, payload builders, `IndentToPoService` (material + service), `IndentPoOptions`, `IndentToPoRegistration` |
| Infrastructure | `src/NewHorizon.Automation.Infrastructure/Flows/IndentToPo/` | the two repositories, EF configurations under `Configurations/`, `IndentToPoRegistration` |
| Worker | `src/NewHorizon.Automation.Worker/Flows/IndentToPo/` | `Endpoints/`, `Contracts/`, `Services/` (scheduler, orchestrator, mapper), `IndentToPoModule` |
| Tests | `tests/*/Flows/IndentToPo/` | unit and integration tests for this flow |
| SQL seeds | `deploy/sql/flows/indent-to-po/` | `003_SeedIndentToPoConfig.sql`, `004_SeedIndentPoAutomation.sql` |
| API walkthroughs | this folder | `01-regular-indent-to-regular-po.md`, `02-capital-indent-to-capital-po.md` |

EF migrations stay in the shared `Infrastructure/Persistence/Migrations` — there is one database
and one migration history.

## How it runs (no queue, no engine)

The second live feature, and the only one that does **not** go through the queue or the engine. It
lives in `ErpClient/Flows/IndentToPo/` and is reached five ways:

```
POST /api/automation/indent-to-po             THE TRIGGER: {"indentTypes":["Regular"],"dryRun":false}
POST /api/automation/indent-to-po/convert     the same handler under its older path
GET  /api/automation/indent-to-po/eligible    authorised indents still awaiting a PO (read-only)
POST /api/automation/indent-to-po/vendor      the vendor-driven form: everything for one vendor
POST /api/process-jobs                        the same conversion, recorded — see Process tracking
GET  /api/process-jobs                        the grid: every conversion, newest first
```

The conversion itself still needs no job row and no database: the ERP's own bookkeeping is what
stops an indent being ordered twice. What the database adds is the history of it.

### Nothing converts on its own — the master toggle is off, and no row has a Schedule Time

**Starting the agent converts nothing**, for two independent reasons: the PO Automation master
switch ships **off**, and no `IndentPoAutomationConfig` row has a `ScheduleTime`. `PoAutomationSchedulerService`
runs but is inert. A fresh install behaves exactly as before.

The ERP's **PO Automation** screen (WebApp2) is a grid — `Indent Type`, `Mode`, `Indent Numbers`,
`Schedule Time`, last-run columns — one row per indent type, under a master on/off toggle:

- **The "PO Automation" toggle** is the master switch, persisted server-side on the `IsActive`
  column of all three rows (set together by `PUT /api/automation/po-automation/enabled`, bearer
  auth). OFF ⇒ the scheduler skips every type **and every conversion entry point is refused 409** —
  `/convert` (all triggers, `api` included), `/vendor`, `POST /api/process-jobs` and its retry.
  It loads its saved state whenever the screen's panel is opened.
- **`Mode`** — `Api` "Indent-based" (Run only), `Timer` "Timer-based" (scheduler only), `Both`.
- **`Run`** builds `POST /api/automation/indent-to-po/convert?trigger=manual` from that row and
  sends it now — but only when PO Automation is ON and the row has no Schedule Time.
- **`Schedule Time`** set ⇒ `PoAutomationSchedulerService` self-calls the *same* endpoint
  (`?trigger=timer`) daily at that time with the *same* body. Empty ⇒ no automatic run.
- **`Indent Numbers`** empty ⇒ convert every eligible authorised indent of that type (the endpoint's
  existing default); named ⇒ convert only those.

`IndentPoAutomationConfig.ShouldRunOnSchedule` is the scheduler rule: `IsActive` (master toggle),
mode admits a timer (`Timer`/`Both`), `ScheduleTime` set, slot passed today, not already run today
(one rule that also catches up a slot missed during downtime). The runtime gate is the injected
`IPoAutomationGate.OffReasonAsync(kinds, ct)` (`PoAutomationGate` in `Application/Flows/IndentToPo/`, scoped)
— null when every targeted type is `IsActive`, else the refusal detail. It reads `IsActive` **fresh
on every call — never cached** — and is checked in three places:
1. at the door — `PurchaseOrderEndpoints.ConvertAsync` + `CreateForVendorAsync`,
   `ProcessJobOrchestrator.StartAsync` / `RetryAsync` (→ 409 / `ProcessJobRefusal`);
2. **before every indent inside a sweep** — `IndentToPoService`'s private `ConvertAsync` funnel and
   the `ConvertEligibleAsync` loop (`ErpClient/Flows/IndentToPo/IndentConversion.cs`). So turning PO
   Automation off *mid-run* stops it before the next indent — a PO already created is not undone,
   nothing after it converts. The scheduler needs no code for this: its `?trigger=timer` self-call
   runs the same sweep.
It is a no-op when `configs.IsEnabled` is false, so a **DB-less install still converts on request**
exactly as before (the invariant `Startup_converts_nothing` depends on). `ApiTriggerGate` and the
earlier static `PoAutomationGate` (a run-mode refusal) were deleted; `Sites` / `DryRun` /
`MaxIndentsPerRun` remain unused columns on the row. `Startup_converts_nothing` boots from the real
`appsettings.json` and asserts nothing converts;
`Only_the_gated_scheduler_may_reach_the_conversion` asserts no *ungated* auto-converter is wired.

The scheduler and the `po-automation` endpoints are gated on a *usable* database, the same as the
AutoShop timers and process tracking — no database ⇒ neither.

### The `indentTypes` allow-list

`IndentType` is `Regular`, `Capital` or `Service`. Every entry point takes the same multi-valued
allow-list, and it means the same thing in each: *which types this run may convert*, never *how many
orders to make*.

```jsonc
{ "indentTypes": ["regular", "service"] }   // Regular and Service may convert; Capital may not
{ "indentTypes": ["service"] }              // only Service
{ }                                         // no filter: all three
```

Case-insensitive; duplicates collapse; unknown values are a 400 naming the offender. On `/eligible`
it is a query parameter, repeated (`?indentTypes=regular&indentTypes=service`) or comma-separated.
The singular `indentType` that shipped first still works and is read as part of the same set.

Three things about it that are load-bearing:

- **Empty means all three, and that is deliberate** — an absent filter is not a filter.
  `IndentTypeSelection` is the single place it is decided, so it cannot drift between entry points.
- **It is applied before any PO work, and enforced twice.** Discovery narrows to the selected
  types, so an unselected type's ERP list is *never called* rather than called and discarded. Then
  `ConvertAsync` refuses anything outside the selection immediately before the first ERP write. The
  second check never fires in normal operation; it is there so no future entry point can reach the
  conversion around the filter.
- **The response echoes the selection back** (`indentTypes`), which is the difference between "found
  no Capital indents" and "was never allowed to look for Capital indents". `skipped` alongside it
  lists every examined indent that produced nothing, with the reason.

`dryRun: true` reports what it would convert and creates nothing; `dryRun: false` — the default —
creates the orders. The orders come out **unauthorised** where Document Control says authorisation
is required, so a person still approves them.

### The `indentNumbers` allow-list

A second allow-list on `/convert`, narrowing *with* `indentTypes`, never widening it:
`{"indentTypes":["Regular"],"indentNumbers":["000123"]}` converts that indent if it is Regular, and
nothing else. Absent or empty is no filter, exactly as before. `IndentNumberSelection` is the one
place the rule lives, and it is applied in discovery *and* again immediately before the first ERP
write, for the same reason the type filter is: narrowing the search is an optimisation, the check
before the write is the guarantee.

- **Either form of the number matches**: the whole document number `24-25/PI/NF1/000100`, or the
  bare running number `000100`. Both are exact, case- and whitespace-insensitive — never a
  substring or a prefix, so `0001` matches nothing.
- **A bare number that fits two eligible indents refuses the whole call**, with a 400 naming the
  candidates. The same `000100` exists in other years and at other sites; converting both would
  order an indent nobody named and choosing one would be a guess.
- **An unknown property in the body is a 400, not a shrug.** `ConvertIndentsRequest.BindAsync`
  reads with `UnmappedMemberHandling.Disallow` because a mistyped `indentnumber` used to bind to
  nothing, leaving a request that read as "no filter" — one letter turned "convert this indent"
  into twenty real purchase orders. The failure is carried on the request and turned into a 400 by
  the endpoint; thrown from the binder it would surface as a 500, and a caller's typo is not a
  server fault. Scoped to this type deliberately: `/api/process-jobs` ignores unknown fields on
  purpose, and is tested for it.
- **A number that matched nothing is reported in `notFound`, not an error** — one unknown number
  must not stop the others converting. `indentNumbers` is echoed back as understood, the way
  `indentTypes` is. The log names all three: requested, selected, and actually converted.

Regular and Capital are the material flow described below; Service is a **different ERP module**
and is described under it. `/vendor` runs the material sequence once per selected material type —
Regular and Capital are separate documents even for one vendor — and reports `Service` as a note
rather than an order, because the ERP has no vendor-first pending-lines question for service
indents. An `indentId` alone can be ambiguous, because `XINDID` (material) and `XINDAUTOID`
(service) are keys into different tables; if the id is live in both, the call is refused and asks
for the type. `indentTypes` disambiguates it too.

Things that are easy to get wrong here, all of them learned the hard way against the live ERP:

- **The site is the indent's, never the configured one.** `CSP_GETPENDINGINDENT4PO` filters
  `XINDHLOCID = @SiteId`, and the warehouse LOV filters on the same site. A single configured site
  hides every authorised indent raised anywhere else — which is exactly how this feature came to
  do nothing. List every site in `PurchaseOrder:Sites`.
- **The ERP is vendor-first; the automation has to be indent-first.** Nothing in the ERP answers
  "which indents need a PO" for a *vendor-less* question, so the flow discovers indents
  (`indentEntryList`, `Status = "A"`), resolves a vendor per item
  (`ItemVendorPurchase/list`: `isDefault`, else lowest `vndpriority`), groups by
  (vendor, currency, rate structure) — the ERP's own Bulk PO break rule — and runs the PO Entry
  sequence once per group, filtering `indentdtlmodel` to `pidindid == the indent`.
- **An item with no `MITMVND` row cannot be ordered from anybody** while
  `MSCSYSPUR.MSCAUTOITMVNDPUR = 0`, and an item the warehouse LOV offers nothing for cannot be
  received. Both are ERP master data. The agent names the item and moves on; it must not invent
  either.
- **The financial year is the PO's, and it moves every April.** `getDefaultDocumentDetail` is the
  first call of the create sequence, and a year Document Control has no row for kills every
  conversion right there. `PurchaseOrder:FinancialYear` is therefore blank by default, meaning "the
  April–March year the PO date falls in" — the same year the ERP screen sends. A value pins it, and
  is tried first with the PO date's year as the fallback, with a warning naming whichever answered.
  Whatever answers is what the payload is stamped with, so `POHORDYEAR` can never disagree with the
  numbering the ERP just handed out.
- **A refusal for one vendor group is a note, not an exception.** It is recorded per indent and
  logged — an indent that converts to nothing must never read as "produced 0 purchase order(s):
  none" with the reason nowhere, which is exactly how a stale financial year hid for a day.
- **Idempotency is the ERP's, not ours.** Creating the order raises `XINDITM.XINDIPOQTY` and closes
  the line, so a second run finds nothing outstanding. That is why this needs no job row — and why
  it keeps working with the database switched off.
- Bulk PO (`Purchase/BulkPO/*`) is deliberately **not** driven: its first call
  `TRUNCATE`s the shared `XPNDINDBULKPO` / `XVNDITMBULKPO` staging tables table-wide, which would
  wipe a human's in-progress session, and it hard-codes `XINDHTYP = 'R'`.

## Process tracking for Indent → PO

**Every conversion is recorded, whichever endpoint asked.** `POST /api/process-jobs` answers with
the run; `POST /api/automation/indent-to-po` (and its `/convert` alias) answers with the purchase
orders. Both open exactly one `AutomationRun` through the same lifecycle —
`ProcessJobOrchestrator.RunTrackedAsync`, which opens it, counts what was examined and closes it
either way. The conversion endpoints record `TriggerSource.Api` with a null `TriggeredBy`: they
authenticate with a shared API key and there is no authentication middleware, no `HttpContext.User`
and no claim to read, so a name there would be invented — callers that know who they are say so
(`triggeredBy` on `/api/process-jobs`, `cancelledBy` on cancel). `TriggerReference` carries the
request's trace id, which is the way back to the line in the request log.

Three new tables, and the four that already existed are reused rather than duplicated:

| Grain | Table | |
|---|---|---|
| one authorised indent | `IndentPoConversion` | new — identity held once, however many attempts |
| one trigger invocation | `AutomationRun` | new — trigger, mode, allow-list, totals |
| one attempt | `AutomationJob` | existing, plus `ConversionId`, `RunId`, computed `DurationMs` |
| one stage / task | `AutomationJobStep` | existing |
| one ERP call | `AutomationLog` | existing |
| one failure | `AutomationError` | existing |
| one vendor group's result | `IndentPoOutcome` | new — the PO, or the reason there is none |

Stages, in the order the conversion walks them: `Discovery`, `VendorResolution`, `DocumentControl`,
`PendingLines`, `CreatePurchaseOrder`.

Things here that are load-bearing:

- **No foreign key points at the ERP.** `IndentId`, `SiteId`, `PoId` and `PoNumber` are values, not
  constraints, because the automation database is not the ERP database and must survive an ERP row
  being archived. They name `XINDHDR.XINDID` / `XINDSHDR.XINDAUTOID`, `MLOCMST.MLOCID`, and
  `XPOHEAD.POHAUTOID` / `XPOSHEAD.POHSAUTOID`.
- **A conversion job is inserted already `Running`**, by `ProcessJobService`, because it is executed
  inline by whoever triggered it. Leaving it `Pending` would offer it to `JobDispatcherService`,
  which would hand it to an engine that has no `WorkflowDefinition` for `IndentToPurchaseOrder` —
  and deliberately has none. Orphan recovery and the claim query both skip
  `ConversionId IS NOT NULL` for the same reason, and `/api/automation/jobs/{id}/retry` refuses
  them, pointing at `/api/process-jobs/{id}/retry` instead.
- **Two filtered unique indexes on `IdempotencyKey`, not one.** `UX_AutomationJob_IdempotencyKey_Live`
  keeps the per-document rule (`ConversionId IS NULL`); `UX_AutomationJob_LiveIndentConversion`
  allows one attempt *still running* per indent and any number of finished ones, because an indent
  whose lines are still open is meant to be converted again. "Finished" includes **Failed** and
  **Skipped**, not just Completed and Cancelled: a business-rule refusal (`Skipped`) is over exactly
  like a technical failure (`Failed`) is, and leaving either in the filter holds the indent's key for
  ever — retry then answers 200 having created no second attempt at all. Both
  indexes must be declared with the **named** `HasIndex(expression, name)` overload: EF keys an
  index by its property list, so a second unnamed `HasIndex(job => job.IdempotencyKey)` silently
  overwrites the first, and the migration drops per-document duplicate safety with nothing in the
  diff that looks wrong.
- **An ERP refusal is the caller's 400, not a 500.** `ProcessJobOrchestrator` maps `ErpException`
  the way `PurchaseOrderEndpoints.Failure` does — business to 400, transient to 503 — so the same
  refusal cannot answer differently depending on which endpoint asked. The run is still recorded
  as Failed either way.
- **An outcome that ordered nothing must name its reason**, enforced by `CK_IndentPoOutcome_Result`
  as well as by the factory. This is the durable form of the `Notes` list, and the reason an indent
  that converted to nothing can never again read as "produced 0 purchase order(s): none".
- **`RequestedIndentTypes` is stored expanded**, never blank — the difference between "found no
  Capital indents" and "was never allowed to look for Capital indents". A run that created no jobs
  at all still has a row, which is the only place that distinction survives.
- **Tracking is best-effort, and "configured" is not "usable".** `AddProcessTracking()` is called
  only when `DatabaseAvailability.Probe` says the database can actually be opened — the same gate
  the three hosted services already sat behind. Otherwise `NullProcessJobService` stands and every
  call is a no-op: the conversion runs at full speed and still returns the purchase order, and
  `/api/process-jobs` answers 503 saying so. Gating on *configured* instead would make every
  tracked write retry three times with backoff before failing, on every indent, for as long as the
  process runs — EF classes "cannot open database" as transient. On top of that, the run lifecycle
  itself swallows and logs a tracking failure (`ProcessJobOrchestrator.TryTrackAsync`), so a
  database that dies mid-life costs the history and not the purchase orders. Dry runs are not
  tracked at all — they create nothing.
- **Durations are computed columns** (`DATEDIFF_BIG(MILLISECOND, StartedAtUtc, CompletedAtUtc)`),
  and the attempt number is derived by ordering a conversion's executions. Neither is stored, so
  neither can disagree with what it is derived from.

## Service Indent → Service PO

Same feature, same entry points, same shape — a **different ERP module**, so almost none of the
material flow's calls apply. `ServiceIndentDiscovery.cs`, `ServiceItemVendorResolver.cs`,
`ServiceIndentConversion.cs`, `ServicePoPayloadBuilder.cs`, `ServicePoLineTotals.cs`. Where the two
genuinely share an ERP call it is reused rather than copied: Document Control
(`getDefaultDocumentDetail`, keyed `PR`/`SP` instead of `PR`/`RP`), `GetPoAddress`,
`GetVendorInformation`, `getratestructuredetail` and `getAllRateStructureDetails`.

| | Material | Service |
|---|---|---|
| Indent tables | `XINDHDR` / `XINDITM` | `XINDSHDR` / `XINDSDTL` / `XINDSDEL` |
| PO tables | `XPOHEAD` / `XPOITMDTL` | `XPOSHEAD` / `XPOSDTL` / `XPOSDEL` |
| Doc type / sub-type | `PR`/`RP` or `PR`/`CP` | `PR`/`SP` |
| List | `indententry/indentEntryList` (body carries sites + status) | `ServiceIndentCont/indentEntryList/{sites}?Status=A` |
| Detail | `getIndentDetail` (GET, 8 path parts) | `getSerIndentDetail` (POST) |
| Item → vendor | `ItemVendorPurchase/list` (`MITMVND`) | `itemservices/getItemServiceVendorDetail/{itemAutoId}` (`MSERITMVND`) |
| Pending lines | `GetPendingItemsFromIndentnew` | `ServicePO/getItemDetailForSerPO` with `poType: "IN"` |
| Create | `POEntry/create` | `ServicePO/createServicePOEntry` |
| Ordered qty | `XINDITM.XINDIPOQTY` | `XINDSDTL.XINDSDPOQTY` |

What differs beyond the paths, and is easy to get wrong:

- **The service list answers less.** It carries no site id and no open/closed status — only "is
  this authorised" — so an indent converted last week is still on it. Both facts come from
  `getSerIndentDetail`, which is why service discovery is two calls per candidate where the
  material one is one. It also filters on `XINDFRSITEID` (the raising site) while the pending-lines
  query filters on `XINDSHSITE` (the indent's own); they are normally the same, and the order is
  always raised at the indent's.
- **The payload is typed, not passed through.** `createServicePOEntry` binds a hand-written
  ~20-property DTO and ignores everything else, so — unlike the material flow, where echoing the
  ERP's own 130-property row back is the whole point — there is nothing to preserve by cloning.
- **There is no preferred vendor.** `MSERITMVND` has no default flag and no priority column, so
  when a service lists several vendors the lowest vendor code wins and the log says so. The ERP's
  own query just takes an arbitrary first row.
- **Two flags decide which save validation runs**: `MSCSYSPUR.MSCENABLELINELEVELRTSTR` (from
  `purchasePolicy/getDetail`) and `MSCSYSFLAGS.MSFNONGST` (from `tngConfiguration/getDetail`). The
  same rate structure is stamped on both the header and every line, so either branch sees the same
  answer; the flags are still reported honestly, because they are what tells the ERP which to run.
  A refusal from either endpoint falls back to `false, false` with a warning.
- **Reverse charge follows the screen's own rule**, which is only that the vendor's GSTIN is
  non-blank and at least 15 characters. A stricter check here would turn ordinary orders into
  reverse-charge ones nobody asked for.
- **A site that does not auto-number Service POs is refused**, rather than the agent inventing a
  document number that would collide with the next one a person types.
- **Idempotency is the ERP's, again**: `CSP_XPOSDTL_Insert` raises `XINDSDPOQTY`, closes the line at
  `XINDSDINDQTY`, closes the indent when every line is closed, and refuses an over-order outright.

