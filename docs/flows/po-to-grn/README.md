# PO → GRN

Receives authorised purchase orders: for every authorised PO with quantity still pending, the agent
raises a GRN (Goods Receipt Note) through the ERP's own GRN API, at the **pending quantity**, because
vendors almost always deliver in full. The GRN is left **unauthorised** for a person to approve.

Like Indent → PO it runs outside the job queue and workflow engine, and it shares **nothing** with
Indent → PO: its own settings row, tables, endpoints and scheduler. The confirmed business rules are
in [`.claude/context/po-to-grn-decisions.md`](../../../.claude/context/po-to-grn-decisions.md).

## Where the code lives

| Layer | Folder | What is there |
|---|---|---|
| Domain | `src/NewHorizon.Automation.Domain/Flows/PoToGrn/` | `PoGrnAutomationConfig` (the settings row), `PoGrnRun`, `PoGrnReceipt`, modes |
| Application | `src/NewHorizon.Automation.Application/Flows/PoToGrn/` | `IPoGrnAutomationConfigRepository`, `IPoGrnHistory`, database-less defaults |
| ErpClient | `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/` | `PoToGrnService`, `GrnLineClassifier`, `GrnLineTotals`, `GrnPayloadBuilder`, options |
| Infrastructure | `src/NewHorizon.Automation.Infrastructure/Flows/PoToGrn/` | the two repositories, EF configurations (settings row seeded inert) |
| Worker | `src/NewHorizon.Automation.Worker/Flows/PoToGrn/` | the two endpoints, contracts, `GrnAutomationSchedulerService`, `PoToGrnModule` |
| Tests | `tests/*/Flows/PoToGrn/` | unit tests (classifier, pricing, payload, settings, service) and API tests |
| SQL seed | `deploy/sql/flows/po-to-grn/005_SeedPoGrnAutomation.sql` | restores the settings row |

## The two APIs

Both take the agent's inbound API key in `X-Automation-Api-Key` (value =
`AutomationAgent:Host:InboundApiKey`). There is no ERP-login access yet — the dashboard section comes
later.

### Settings — `GET` / `PUT /api/automation/grn-automation`

```jsonc
// PUT: every field optional; an absent field is left as it is
{
  "isActive": true,                 // master switch; off ⇒ every run is refused 409
  "receiptMode": "Complete",        // Complete | Partial — see below
  "invoiceNumber": "INV-2026-09",   // stamped on every GRN; editable; blank stops runs
  "runMode": "Both",                // Api (PO-based) | Timer | Both
  "scheduleTime": "18:30",          // daily, local time; ignored in Api mode
  "clearScheduleTime": false,
  "sites": "1, 2, 4",               // blank ⇒ AutomationAgent:PoToGrn:Sites
  "dryRun": false,                  // what the scheduler's run does
  "maxPosPerRun": 25,
  "updatedBy": "postman"
}
```

### Trigger — `POST /api/automation/po-to-grn[?trigger=manual|timer|api]`

```jsonc
{                                   // every field optional; {} = every eligible PO
  "poNumbers": ["26-27/PR/NF1/000012"], // or the bare "000012"; a bare number fitting two POs is a 400
  "poIds": [101],                   // POHAUTOID
  "poTypes": ["Regular", "Capital"],
  "sites": [1],
  "receiptMode": "Partial",         // overrides the saved mode for this run only
  "maxPos": 10,
  "dryRun": true                    // report what would be received; create nothing
}
```

Refusals: `401` no/wrong API key · `409` switched off · `400` no invoice number (real runs only),
unknown body field, unknown trigger or PO type · `503` no usable automation database. The answer
lists every PO examined with its GRN number or the reason it got none (`notes`), plus `notFound` for
named POs that were not eligible.

## What one run does (ERP calls)

| # | Call | Why |
|---|---|---|
| 1 | `GET admin/common/getCommomSystemConfig` | per-site open period — the GRN date must fall inside |
| 2 | `GET admin/inventoryPolicy/getDetail` | `islinelevelwhgrn`: one GRN per warehouse, or per PO |
| 3 | `POST Purchase/POEntry/List/{sites}/{company}/{location}?Status=A` | authorised POs; `isgrn` = quantity still pending. Once per PO type, paged, newest first |
| 4 | `GET inventory/grn/getpotogrnredirectdata/{poId}` | the PO's vendor, currency, warehouse(s) |
| 5 | `GET admin/documentControl/getDefaultDocumentDetail/{FY}/GR/{OR\|CG}/{site}` | GRN numbering (cached) |
| 6 | `GET inventory/grn/getposearchdetails/{site}/{wh}/{vendor}/{poId}/DIS/{cur}/{R\|C}/{date}` | the lines, pending quantities, item-master flags and the PO's tax rows |
| 7 | `GET purchase/ItemVendorPurchase/getAllRateStructureDetails/P/{rs}/{rate}/{cur}` | per-unit tax, × quantity (cached per price) |
| 8 | `POST inventory/grn/create` | the GRN. Never retried by the transport |

## Rules that are easy to get wrong

- **"Parameter" lines are never received by the agent**: an item needing a batch, inward, heat,
  serial or manufacturing-batch number, a shelf-life expiry date, or LBT size details. The flags come
  from the item master (`MITMMAST.MIMBCHREQD`, `MIMINWDREQ`, `MIMHEATREQ`, `MIMITMSRREQD`,
  `MIMMFGREQ`, the class's shelf-life flag) and already travel on each line of call 6. None of those
  values exist anywhere in the ERP before receipt. A line with no tax rows on the PO, or (line-level
  warehouses) no warehouse the agent's user may receive into, is treated the same way.
  - **Complete**: any such line ⇒ the whole PO is skipped.
  - **Partial**: only clean lines are put on the GRN; the rest stay pending on the PO for a person.
    Such a PO is looked at again, and noted again, on every run until a person receives those lines.
- **Tax rows must travel with every line.** The ERP raises the PO's received quantity
  (`CSP_XGRNDTL_UpdatePOQTY`) only when tax rows were inserted. A GRN without them leaves the PO
  looking unreceived and the next run would receive it again — so the builder refuses to send one.
- **Duplicate safety is the ERP's**, as with Indent → PO: once the GRN exists the PO has nothing
  pending (`isgrn = 0`), and the ERP refuses an over-receipt ("GRN QTY Can not Be greter then Po
  QTY", classified as a business refusal, not an outage).
- **`AuthorizationRequired` is always sent as `"Y"`.** With `"N"` the ERP would authorise the GRN and
  post it to Finance inside the create; a person approves it instead.
- **The GRN number arrives only in `message`** — `GRNCreated#26-27/GR/NF1/000101#5001` — unlike PO
  create, whose number is in `data`.
- **Dates**: GRN date = today; invoice date = yesterday; a PO dated (or amended) after today, or a
  site whose open period does not include today, is skipped with the reason.
- **Domestic currency only** (`AutomationAgent:PoToGrn:DomesticCurrency`); blanket POs are skipped.
- **"Authorised" is the rule, not "printed".** The ERP's own GRN button also requires the PO to be
  printed (`POHPRNFLAG = 1`). Set `AutomationAgent:PoToGrn:RequirePrintedPo` to `true` to match it.
- **The master switch is re-read before every PO**, so turning it off mid-run stops before the next
  one. GRNs already created stay.

## History

`PoGrnRun` (one per non-dry run) and `PoGrnReceipt` (one per PO × warehouse: the GRN, or why none —
`CK_PoGrnReceipt_Result`). `PoId`, `SiteId` and `GrnId` are values, never foreign keys into the ERP.
Recording is best-effort: a GRN that exists is never reported as failed because its history write
failed. There is no history API yet.

## Trying it with Postman

1. `dotnet ef database update -p src/NewHorizon.Automation.Infrastructure -s src/NewHorizon.Automation.Worker`
2. Start the agent (publish to `run/`, see CLAUDE.md).
3. `PUT /api/automation/grn-automation` `{"invoiceNumber":"INV-TEST","receiptMode":"Complete","isActive":true}`
4. `POST /api/automation/po-to-grn` `{"dryRun":true}` — check which POs it would receive and why others are skipped.
5. `POST /api/automation/po-to-grn` `{"poNumbers":["<one PO>"]}` — then open the ERP's GRN list: the
   GRN is there, unauthorised, at the pending quantity; running step 5 again finds nothing for that PO.
