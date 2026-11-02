# Quotation to Sales Order Automation API Reference

This document provides a concise reference for all endpoints and JSON request payloads used in the Quotation to Sales Order automation.

---

## 1. Common Authentication & Headers

All endpoints require either the automation API key or an ERP Bearer token:

| Header | Description | Example |
| :--- | :--- | :--- |
| `Content-Type` | Required for `POST` and `PUT` requests | `application/json` |
| `X-Api-Key` | Shared automation secret key | `<shared-secret>` |
| `Authorization` | *(Alternative)* ERP JWT token | `Bearer <erp-jwt-token>` |

---

## 2. Execution & Conversion Trigger Endpoints

### 2.1 Instant Conversion (`POST /api/automation/quotation-to-so`)
Triggers the conversion immediately without relying on the background timer.

#### Option A: Sweep & Convert ALL Open Authorised Quotations (Recommended)
```json
{}
```
*Or explicitly passing `"ALL"`:*
```json
{
  "quotationNumbers": "ALL"
}
```

#### Option B: Convert a Single Specific Quotation Number
*Using quotation number:*
```json
{
  "quotationNumber": "000008"
}
```
*Using full document format:*
```json
{
  "quotationNumber": "26-27/TE//000008"
}
```

#### Option C: Convert Multiple Specific Quotations
```json
{
  "quotationNumbers": "000008, 000009, 000010"
}
```

#### Option D: Filter by Site / Customer (Optional)
```json
{
  "siteCode": "1",
  "customerCode": "A001"
}
```

---

## 3. Configuration & Schedule Management Endpoints

### 3.1 Get Configuration (`GET /api/automation/so-automation`)
* **Method:** `GET`
* **Route:** `/api/automation/so-automation`
* **Request Body:** None
* **Description:** Retrieves the current automation state, active toggle, run mode, scheduled run time, and last execution details.

---

### 3.2 Update Configuration & Immediate Auto-Trigger (`PUT /api/automation/so-automation`)
Updates automation settings. When `runMode` is `"Api"` (or schedule time is cleared), it automatically executes conversion immediately.

#### Payload: Set API Mode & Convert All Quotations Instantly
```json
{
  "isActive": true,
  "runMode": "Api",
  "clearScheduleTime": true,
  "clearQuotationNumbers": true,
  "clearCustomerCode": true,
  "dryRun": false
}
```

#### Payload: Configure Daily Scheduled Timer (e.g. 18:30)
```json
{
  "isActive": true,
  "runMode": "Timer",
  "scheduleTime": "18:30:00",
  "clearQuotationNumbers": true,
  "clearCustomerCode": true,
  "dryRun": false
}
```

#### Payload: Configure for Specific Quotation Number
```json
{
  "isActive": true,
  "runMode": "Api",
  "quotationNumbers": "000008",
  "dryRun": false
}
```

---

### 3.3 Master On/Off Switch (`PUT /api/automation/so-automation/enabled`)
Toggles the master enable/disable switch without altering schedule or filter configurations.

#### Enable Automation
```json
{
  "enabled": true
}
```

#### Disable Automation
```json
{
  "enabled": false
}
```

---

## 4. Query & Inspection Endpoints

### 4.1 Preview Open Quotations (`GET /api/automation/quotation-to-so/open`)
Preview open authorised quotations from ERP before running conversion.

* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/open?customerCode={customerCode}&siteCode={siteCode}`
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/open?customerCode=A001&siteCode=1
  ```

---

## 5. Dashboard Endpoints

### 5.1 Main Composite Dashboard
Returns KPI summary cards, daily stats graph, last completed execution, and recent conversion jobs in a single call.

* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/dashboard` *(or `/api/automation/so-automation/dashboard`)*
* **Query Parameters (Optional):**
  * `days` (integer, default: `30`): Trailing days for stats.
  * `recentCount` (integer, default: `10`): Number of recent jobs to include.
  * `from` (string, ISO date): Filter start range (`yyyy-MM-dd`).
  * `to` (string, ISO date): Filter end range (`yyyy-MM-dd`).
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/dashboard?days=7&recentCount=10
  ```

---

### 5.2 Recent Conversion Jobs (Grid Feed)
Returns recent jobs formatted specifically for dashboard grids.

* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/dashboard/recent` *(or `/api/automation/so-automation/dashboard/recent`)*
* **Query Parameters:** `count` (default: `10`)
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/dashboard/recent?count=5
  ```

---

### 5.3 Dashboard Summary KPI
Returns total quotation jobs processed, success count, failure count, and success rate.

* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/dashboard/summary` *(or `/api/automation/quotation-to-so/summary`)*
* **Query Parameters (Optional):** `from`, `to`
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/dashboard/summary?from=2026-10-01&to=2026-10-31
  ```

---

### 5.4 Daily Trends / Charts
Returns daily counts and metrics over trailing days to display chart trends.

* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/dashboard/daily-summary` *(or `/api/automation/quotation-to-so/daily-summary`)*
* **Query Parameters (Optional):** `days` (default: `30`)
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/dashboard/daily-summary?days=14
  ```

---

## 6. History & Job Detail Endpoints

### 6.1 Paginated History Grid
* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/history` *(or `/api/automation/so-automation/history`)*
* **Query Parameters (Optional):**
  * `page` (default: `1`)
  * `pageSize` (default: `50`, max: `200`)
  * `search` (quotation number or sales order number)
  * `status` (`Success`, `Failed`, `Running`)
  * `trigger` (`manual`, `timer`, `api`)
  * `from` / `to` (date filters)
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/history?page=1&pageSize=20&status=Success
  ```

---

### 6.2 Job Execution Details
* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/history/{jobId}`
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/history/6011c210-9cbb-4629-9e87-19a9a3f24bf7
  ```

---

### 6.3 Document History
* **Method:** `GET`
* **Route:** `/api/automation/quotation-to-so/history/document/{documentNumber}`
* **Example:**
  ```http
  GET /api/automation/quotation-to-so/history/document/26-27/TE//000008
  ```

---

## 7. Execution Runs (Batch Runs)

* **List Runs:** `GET /api/automation/quotation-to-so/runs`
* **Get Run Detail:** `GET /api/automation/quotation-to-so/runs/{runId}`

