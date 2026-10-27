# Quotation to Sales Order Automation

Automates the conversion of open, authorised customer quotations (`XQTNDTL`, Status `'A'`) into Sales Order Receipts (`XORDDTL`) via New Horizon ERP APIs.

---

## Architecture Overview

| Layer | Path | Responsibility |
|---|---|---|
| **Domain** | [`src/.../Domain/Flows/QuotationToSo/`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Domain/Flows/QuotationToSo/) | `QuotationSoAutomationConfig` entity, run mode enum, schedule evaluation |
| **Application** | [`src/.../Application/Flows/QuotationToSo/`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Application/Flows/QuotationToSo/) | `IQuotationSoAutomationConfigRepository` interface and in-memory fallback |
| **ErpClient** | [`src/.../ErpClient/Flows/QuotationToSo/`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/QuotationToSo/) | Quotation discovery, SO payload construction, ERP API integration |
| **Infrastructure** | [`src/.../Infrastructure/Flows/QuotationToSo/`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Infrastructure/Flows/QuotationToSo/) | EF Core repository, database table mappings |
| **Worker** | [`src/.../Worker/Flows/QuotationToSo/`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Worker/Flows/QuotationToSo/) | REST endpoints, background timer service (`QuotationToSoSchedulerService`) |

---

## Authentication

All endpoints require either an **ERP Bearer Token** or the shared **Automation API Key**:

```http
Authorization: Bearer <ERP_JWT_TOKEN>
# OR
X-Automation-Api-Key: dev-local-key
Content-Type: application/json
```

---

## API Reference

### 1. Execution API — Convert Quotations
> **`POST /api/automation/quotation-to-so`**
>
> Immediately triggers quotation-to-SO conversion. Governed by the master toggle and `runMode`.

#### Request Payloads:

| Use Case | JSON Payload | Description |
|---|---|---|
| **Single Quotation** | `{"quotationNumbers": "21"}` | Converts quotation `000021` in current FY |
| **Multiple Quotations** | `{"quotationNumbers": "21, 22, 23"}` | Converts listed quotations sequentially |
| **Use Saved Config** | `{}` | Uses target quotations saved in configuration |
| **All Eligible (System)** | `{"quotationNumbers": null}` | Sweeps and converts all open authorised quotations |
| **By Customer & Site** | `{"customerCode": "C0001", "siteCode": "AHM"}` | Converts all open quotations for specific customer/site |

#### Sample Response (`200 OK`):
```json
{
  "success": true,
  "message": "Quotation '26-27/TE/NF1/000021' successfully converted to Sales Order '26-27/SW/NF1/000052'.",
  "salesOrderNumber": "26-27/SW/NF1/000052",
  "item": {
    "quotationId": 1420,
    "quotationNumber": "26-27/TE/NF1/000021",
    "salesOrderNumber": "26-27/SW/NF1/000052",
    "success": true,
    "errorMessage": null
  }
}
```

---

### 2. Configuration API — Manage Settings & Immediate Execution
> **`PUT /api/automation/so-automation`** &nbsp;|&nbsp; **`GET /api/automation/so-automation`**
>
> Saves automation preferences. **Auto-executes conversion immediately** when configured with `runMode: "Api"` or `clearScheduleTime: true` (without a timer), returning the created Sales Order details directly in the response so no secondary API call is needed!

#### Setup Examples (`PUT`):

- **Immediate Execution via API Mode (Auto-runs on Save)**:
  ```json
  {
    "isActive": true,
    "runMode": "Api",
    "clearScheduleTime": true,
    "quotationNumbers": "21"
  }
  ```
  *Response immediately includes `salesOrderNumber`, `lastRunStatus`, and `lastRunMessage`.*

- **Timer + Specific Quotations (Runs at scheduled time)**:
  ```json
  {
    "isActive": true,
    "runMode": "Both",
    "scheduleTime": "11:30",
    "quotationNumbers": "21, 22"
  }
  ```

- **Timer + All Eligible Quotations**:
  ```json
  {
    "isActive": true,
    "runMode": "Both",
    "scheduleTime": "18:00",
    "clearQuotationNumbers": true
  }
  ```

#### Configuration Schema:
| Property | Type | Default | Description |
|---|---|---|---|
| `isActive` | `bool` | `true` | Master on/off toggle. |
| `runMode` | `string` | `"Both"` | `"Api"`, `"Timer"`, `"Both"`, or `"Disabled"`. |
| `scheduleTime` | `string` | `null` | Daily run time in 24-hr `"HH:mm"` format (e.g. `"11:30"`). |
| `clearScheduleTime`| `bool` | `false` | When `true`, removes schedule time. |
| `quotationNumbers` | `string` | `null` | Target quotations (`"1, 2, 3"`). Blank = all eligible. |
| `clearQuotationNumbers` | `bool` | `false` | When `true`, reverts to all eligible quotations. |

---

### 3. Master On/Off Switch
> **`PUT /api/automation/so-automation/enabled`**
>
> One-click enable/disable toggle.

```json
{
  "enabled": true
}
```

> [!NOTE]
> When `enabled: false`, any conversion request (manual or timer) returns **`409 Conflict`**:
> ```json
> {
>   "title": "Quotation to Sales Order automation is turned off.",
>   "status": 409,
>   "detail": "The master toggle is OFF. No quotations will be converted until automation is switched on."
> }
> ```

---

### 4. Preview Open Quotations (Read-only)
> **`GET /api/automation/quotation-to-so/open?customerCode={cust}&siteCode={site}`**

Returns all authorised quotations awaiting conversion without creating Sales Orders.

---

## Key Reliability Features

1. **Duplicate Prevention**:
   - Pre-checks `XORDDTL` before creation; returns existing order if already converted.
   - Disables transport retries (`NonRetryableKey = true`) to prevent race conditions.
   - Verifies database post-timeout before reporting failure.
2. **LOI Strategy**:
   - Generates unique random LOI numbers (`LOI-XXXXXX`) to satisfy ERP requirements and prevent duplicate PO errors.
3. **Data Sanitization**:
   - Omits empty optional collections (`termDetails`, etc.) to prevent SQL `0001-01-01` date overflow faults.
4. **Resilient Persistence**:
   - In-memory fallback allows the service to operate normally even if the configuration table is not yet migrated in SQL.
