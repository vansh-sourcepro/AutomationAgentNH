# Issue to Shop Floor (SJO-wise, Work-Order-wise, Sales-OAF-wise)

Takes one **SJO, Work Order or Sales OAF number** and creates its **Issue to Shop Floor**, as the
matching tab of the ERP screen would. Stock is taken warehouse by warehouse until each item's
requirement is met, and the Issue is created **only when every check passes**. There are no partial
issues. The flow runs outside the job queue and engine, and needs no automation database. The first
path, `/api/automation/sjo-to-issue` (from when the flow was SJO-only), still works as an alias.

```
POST /api/automation/issue-to-shop-floor        header X-Automation-Api-Key
{ "issueType": "sjo",       "documentNumber": "26-27/SJ/NF1/000123", "dryRun": false }
{ "issueType": "workOrder", "documentNumber": "26-27/WO/NF1/000010" }
{ "issueType": "salesOaf",  "documentNumber": "26-27/OF/NF1/000042" }
```

| `issueType` | ERP tab | Saved as |
|---|---|---|
| `sjo` (default when omitted; also `SJ`, `S`) | SJO-wise, single SJO | `issueType S`, `sjowoType S` |
| `workOrder` (also `WO`, `W`) | Work-order-wise, single WO | `issueType W`, `sjowoType S` |
| `salesOaf` (also `OAF`, `OA`, `O`) | Sales-OAF-wise | `issueType O`, `sjowoType M` |

`sjoNumber` is still accepted in place of `documentNumber`, so callers of the first version keep
working. An unknown `issueType` is a 400 that lists the accepted values. In every mode `docId` is the
first line's SJO id, because that's what the screen's `onSave` sends on all three tabs.

| Answer | Meaning |
|---|---|
| 200, `created: true` | The Issue was created. `issueNumber` and the `lines` (item, warehouse, stock row, quantity) are returned. |
| 200, `dryRun: true`, `created: false` | Every check passed. `lines` is what would be issued. Nothing was created. |
| 400 with `reason`, `checks[]`, `shortages[]` | Refused. The last entry in `checks` is the one that failed. Nothing was created. |
| 400 / 503 problem | The ERP refused a call (400) or could not be reached (503). Nothing was created, and asking again is safe. |

An SJO can also be given by its bare running number (`000123` or `123`) when only one SJO has it.
When several do, the request is refused and the candidates are listed. A Work Order or Sales OAF
needs its full `year/group/site/number`.

## Where the code lives

| Layer | Folder | What is there |
|---|---|---|
| ErpClient | `src/NewHorizon.Automation.ErpClient/Flows/IssueToShopFloor/` | `IssueToShopFloorService` (checks + ERP calls), `IssueAllocator` (the warehouse-by-warehouse rule), `IssuePayloadBuilder`, `DocumentNumberSelection`, `IssueToShopFloorOptions`, `IssueToShopFloorRegistration` |
| Worker | `src/NewHorizon.Automation.Worker/Flows/IssueToShopFloor/` | `Endpoints/IssueToShopFloorEndpoints`, `Contracts/`, `IssueToShopFloorModule` |
| Tests | `tests/*/Flows/IssueToShopFloor/` | allocator, number matching, the service against a fake ERP, the endpoint |
| Settings | `appsettings.json` → `AutomationAgent:IssueToShopFloor` | sites, Issue To / Issue By, company, ERP paths |

There is no Domain, Application or Infrastructure code yet, because the flow keeps no history.
Tracking runs the way Indent → PO does would add those folders.

## What it does, in order

Every call is an **existing** ERP endpoint, the same ones the SJO, Allocation and Issue to Shop Floor
screens use. No ERP change was needed. The first failed check stops the run with its own reason.

| # | Check | ERP call | Refused when |
|---|---|---|---|
| 1 | SJO found | `POST Planning/sjoentry/sjoentryList/{sites}` (search, paged 100 at a time — never `pageSize 0`, whose SQL in `CSP_XSJO_List_EM` is broken and makes the ERP answer 500) | no exact match, or a bare number matches several |
| 2 | Authorised | same row, `status` | `status` ≠ `A` (`CSP_XSJO_List_EM`: `A` authorised, `P` pending, `D` deleted) |
| 3 | CBOM generated | `POST Planning/AllocationWorkorder/GetSJOItemCode4Allocation` (flag `V`, the SJO item) | no frozen CBOM row (`XDMDSJO`, `XCDISFROZEN = 1`) |
| 4 | Work Order | `GET Planning/AllocationWorkorder/GetWODtl4AllocationWoCreation/{sjoId}/{rand}` | no Work Order, or none `OPEN` |
| 5 | Issue numbering | `GET admin/documentControl/getDefaultDocumentDetail/{yy-yy}/IS/NI/{location}` | no Document Control row, or the Issue is not auto-numbered |
| 6 | Work Allocation | `POST Inventory/IssuetoShopFloor/getWarehouseCodeForIssue` (SJ, flag L, transaction `02108`) | no warehouse still holds stock allocated to the SJO |
| 7 | ERP eligibility | `POST Inventory/IssuetoShopFloor/getIssueOfItmDocNoLOV/SJ/{yr}/{grp}/{site}/V` | the Issue screen would not accept the SJO (WO not authorised, nothing pending) |
| 8 | Pending items | `POST Inventory/IssuetoShopFloor/getListOfItems4IssueEntry` | nothing pending; a barcode item (when scanning is on) |
| 9 | Stock | `GET Inventory/IssuetoShopFloor/getItemCombinations4IssueEntry/...` per item, then `IssueAllocator` | **any** item cannot be covered in full. Every short item is listed with required vs available. |
| — | Create | `POST Inventory/IssuetoShopFloor/createIssuetoShopFloor` (non-retryable) | — |

### Work Order and Sales OAF

Steps 1–4 and 7 are replaced by the Issue screen's own site and number validation. The rest (5, 6,
8, 9 and the create) are the same.

| # | Check | ERP call | Refused when |
|---|---|---|---|
| 1 | Document found | `GET Inventory/IssuetoShopFloor/getIssueOfItmSite/{WO\|OA}/{yr}/{grp}/V?Site={code}` → site id, then `GET .../getIssueOfItmDocNo/{WO\|OA}/{yr}/{grp}/{siteId}/V?DocNo={number}` → document id | the ERP doesn't offer it for issue |

The ERP only answers with a document it would issue:
- **Work Order:** open (`XWOSTATUS='O'`), authorised, with allocated stock left.
- **Sales OAF:** has a customer-order SJO whose Work Order is open and authorised, with allocated stock left.

Because of that, the reason names all three possibilities, rather than a single cause the ERP didn't
tell us. A Work Order or OAF can span several SJOs, so each line's stock is looked up for **that
line's own SJO**. The OAF warehouse lookup sends `whType S`, because `CSP_XISSHDR_GetWarehouseList`
only handles OA in its single-warehouse branch. `Sites` is not needed for these two modes: the site
comes from the number.

Step 7 is the Issue screen's own SJO validation (`CSP_XISSHDR_GetDocumentNumber_EM`). It combines
every rule the ERP applies, but only answers yes or no. That's why the specific checks run first: each
one can say *what* is missing. Step 7 runs last so a rule the specific checks miss still stops the run.

## Rules that matter

- **Inward-tracked items take the oldest inward first.** This applies while the MRP policy has
  inward-wise allocation off. The screen's Fill skips these items and a person picks the inwards in a
  popup; the agent picks them itself, by business decision.
  - The ERP gives no date to sort on: that branch of `CSP_XISSHDR_GetItemCombinations4Issue` has no
    inward date and lists rows by inward-number text.
  - So the receipt date is read from the inward number (`INW` + `ddMMyyyy` + time), and stock id
    breaks same-day ties. The time part doesn't follow receipt order, and stock ids do.
  - Each line in the answer shows its `inwardNo`. See `InwardFifo`.
  - With inward-wise allocation on, the ERP's own order is used, as Fill does.
- **Warehouse by warehouse.** Each item needs `min(pending, required)`. The allocator walks the
  item's stock rows in the order the ERP returns them and takes `min(available, still needed)` from
  each. For example, 22 needed from 12 + 200 available gives 12 + 10. This is the screen's own **Fill**
  (`onFillData` in `issuetoshop.component.ts`), so an automated issue splits the same way a person's
  would.
- **The warehouses are the allocated ones.** The ERP only offers stock from warehouses where Work
  Allocation reserved material for this SJO, and only warehouses the agent's ERP user has rights to
  under transaction `02108`. There is no separate "default warehouse" setting.
- **A stock row shared by two lines is not promised twice.** The same item at two CBOM positions
  sees the same row. The second line only gets what the first left over.
- **All or nothing.** A shortage on any item refuses the whole issue. The single write happens only
  after every check and the full allocation have succeeded.
- **The ERP prevents double issues.** Saving raises the issued quantities, so a second run finds
  nothing allocated and pending, and is refused with that reason.
- **The agent never invents a document number.** A site that doesn't auto-number the Issue is refused.
- **Issue To / Issue By** are required by the ERP, and a person picks them on the screen. For the
  agent they come from configuration. Until `Sites`, `IssueTo` and `IssueBy` are set, every request
  is refused with a message naming the missing setting. Nothing else in the agent is affected.

## To confirm against the live ERP

These are read from the ERP's code, but not yet exercised against a live ERP. Run a `dryRun` first.

- JSON property names of each response. These are camelCased by the ERP from its C# models, e.g.
  `sjoEntryId`, `randomno`, `wonumber`/`wostatus`, `warehouseId`, `stockId`/`lineNo`/`wareHouseCode`/`quantity`.
- The codes to use for `IssueTo` / `IssueBy`.
- The agent's ERP user has warehouse rights under transaction `02108` for the allocated warehouses.
- `CompanyCode` is set, so the MRP policy (inward-wise allocation) can be read. When it can't be read,
  the flow treats inward-wise allocation as off, the ERP's default, and picks inwards FIFO. On
  `Main_4Automation_C_Fab_2511` the policy is off anyway (`MSCMRP.MSCINWALC = 'N'`, company `NFT`).

## Verifying

1. `dryRun: true` on an authorised, allocated SJO lists the planned lines.
2. An unauthorised SJO, one without CBOM, one without WO and one without allocation each return
   their own reason, with nothing created.
3. A real run, then open the Issue in the ERP's Issue to Shop Floor screen and compare the
   warehouse split.
4. Run it again. It must be refused as having nothing left to issue.
