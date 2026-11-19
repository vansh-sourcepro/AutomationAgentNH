# AutomationAgentNH — Complete API Reference & Usage Guide

This document provides a comprehensive specification and operational instruction manual for all REST APIs in the **New Horizon Automation Agent** (`AutomationAgentNH`).

---

## Table of Contents

1. [Architecture & Overview](#1-architecture--overview)
2. [Authentication & Authorization](#2-authentication--authorization)
   - [2.1 API Key Authentication (`X-Automation-Api-Key`)](#21-api-key-authentication)
   - [2.2 ERP JWT Bearer Token Authentication](#22-erp-jwt-bearer-token-authentication)
   - [2.3 ERP Role Management Form Rights (011171 & 011172)](#23-erp-role-management-form-rights)
3. [Base URL & Port Configuration](#3-base-url--port-configuration)
4. [Configuration APIs](#4-configuration-apis)
   - [4.1 PO Automation Configuration (Indent → PO)](#41-po-automation-configuration-indent--po)
   - [4.2 GRN Automation Configuration (PO → GRN)](#42-grn-automation-configuration-po--grn)
   - [4.3 System & Module Runtime Configuration](#43-system--module-runtime-configuration)
5. [History, Runs & Monitoring APIs](#5-history-runs--monitoring-apis)
   - [5.1 Unified & Indent Process History](#51-unified--indent-process-history)
   - [5.2 PO → GRN Run History](#52-po--grn-run-history)
   - [5.3 Summary & KPI Analytics](#53-summary--kpi-analytics)
   - [5.4 Daily Statistics & Trend Charts](#54-daily-statistics--trend-charts)
6. [Automation Execution & Trigger APIs](#6-automation-execution--trigger-apis)
   - [6.1 Convert Indents to Purchase Orders (Tracked & Direct)](#61-convert-indents-to-purchase-orders)
   - [6.2 Vendor-Specific Purchase Order Creation](#62-vendor-specific-purchase-order-creation)
   - [6.3 PO → GRN Receipt Creation](#63-po--grn-receipt-creation)
   - [6.4 Engine Cycle Trigger](#64-engine-cycle-trigger)
7. [Job Engine & Lifecycle Control APIs](#7-job-engine--lifecycle-control-apis)
   - [7.1 Automation Dashboard & Status Counts](#71-automation-dashboard--status-counts)
   - [7.2 List & Inspect Automation Jobs](#72-list--inspect-automation-jobs)
   - [7.3 Job Error Details](#73-job-error-details)
   - [7.4 Job Retry, Resume & Cancellation](#74-job-retry-resume--cancellation)
8. [Health & Diagnostics APIs](#8-health--diagnostics-apis)
9. [Calling & Testing Instructions (cURL & PowerShell Examples)](#9-calling--testing-instructions)

---

## 1. Architecture & Overview

The Automation Agent Worker exposes HTTP REST endpoints designed for two audiences:
1. **ERP Frontend (WebApp2 Browser Clients)**: User-facing operations (PO Automation settings screen, Run History grids, Dashboards, and manual "Run Now" actions).
2. **Automated Background Schedulers & Machine Clients**: System-to-system calls triggered by internal timers, cron workers, or CLI utilities.

### Core Modules Supported:
- **Indent to Purchase Order (`IndentToPo`)**: Converts authorised material (`Regular`, `Capital`) and `Service` indents into ERP Purchase Orders.
- **Purchase Order to Goods Receipt Note (`PoToGrn`)**: Automatically receives authorised POs and generates stock receipts / GRNs in the ERP.
- **Automation Job Engine (`Jobs` & `Config`)**: Generic workflow orchestration, multi-step execution tracking, error capturing, and lifecycle steering.

---

## 2. Authentication & Authorization

All endpoints (except `/health`) are gated by one or more authentication filters.

### 2.1 API Key Authentication
- **Header**: `X-Automation-Api-Key: <InboundApiKey>`
- **Filter**: `ApiKeyFilter`
- **Use Case**: Used by backend schedulers, background timers, curl scripts, and internal machine callers.
- **Configured in**: `appsettings.json` → `AutomationAgent:Host:InboundApiKey`.

### 2.2 ERP JWT Bearer Token Authentication
- **Header**: `Authorization: Bearer <JWT_TOKEN>`
- **Filter**: `ErpUserOrApiKeyFilter` or `.RequireAuthorization()`
- **Use Case**: Used by the ERP WebApp2 frontend when an authenticated user performs actions in the browser.
- **Configured in**: `appsettings.json` → `AutomationAgent:InboundJwt:SigningKey` and `Issuer`.

### 2.3 ERP Role Management Form Rights
When an endpoint is accessed with an **ERP Bearer Token**, the agent validates user permissions against the ERP Role Management system:
- **Form `011171` ("PO Automation Configuration")**:
  - Right `'I'` (Inquiry): Required to view configurations and eligible indents (`GET`).
  - Right `'E'` (Edit): Required to update settings, flip toggles, or click "Run now" (`PUT`, `POST`).
- **Form `011172` ("PO Automation Run History")**:
  - Right `'I'` (Inquiry): Required to inspect execution logs, summary metrics, and history grids (`GET`, `POST /retry`).

> **Note for Machine Callers**: If the caller presents a valid `X-Automation-Api-Key`, ERP Form Rights checks are bypassed because machine callers carry no user persona.

---

## 3. Base URL & Port Configuration

Default worker address and ports configured in `appsettings.json`:
- **Management API Port**: Typically `http://localhost:5050` or `http://127.0.0.1:5050` (configured via `AutomationAgent:Host:ManagementApiPort`).
- **Health Checks**: Available on loopback without authentication.

---

## 4. Configuration APIs

### 4.1 PO Automation Configuration (Indent → PO)

Managed per indent type (`Regular`, `Capital`, `Service`).

#### 4.1.1 Get All PO Automation Configurations
- **Method & Route**: `GET /api/automation/po-automation/`
- **Authentication**: ERP Bearer Token (Form `011171` `'I'`)
- **Description**: Returns the automation configuration rows for all three indent types.
- **Response `200 OK`**:
```json
[
  {
    "indentType": "Regular",
    "runMode": "Both",
    "scheduleTime": "02:00:00",
    "sites": "1,2",
    "indentNumbers": null,
    "isActive": true,
    "dryRun": false,
    "maxIndentsPerRun": 50,
    "lastScheduledRunDate": "2026-10-06",
    "lastTriggeredAtUtc": "2026-10-06T02:00:05.123Z",
    "lastRunStatus": "Completed",
    "lastRunReference": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "updatedAtUtc": "2026-10-01T10:00:00Z",
    "updatedBy": "admin"
  }
]
```

#### 4.1.2 Get PO Automation Config for Single Indent Type
- **Method & Route**: `GET /api/automation/po-automation/{indentType}`
- **Authentication**: ERP Bearer Token (Form `011171` `'I'`)
- **Path Parameter**: `indentType`: `Regular` | `Capital` | `Service`

#### 4.1.3 Toggle Master On/Off Switch for PO Automation
- **Method & Route**: `PUT /api/automation/po-automation/enabled`
- **Authentication**: ERP Bearer Token (Form `011171` `'E'`)
- **Description**: Enables or disables automation across all three indent types simultaneously.
- **Request Body**:
```json
{
  "enabled": true
}
```
- **Response `200 OK`**: Returns updated array of `PoAutomationConfigResponse`.

#### 4.1.4 Update Specific Indent Type Automation Settings
- **Method & Route**: `PUT /api/automation/po-automation/{indentType}`
- **Authentication**: ERP Bearer Token (Form `011171` `'E'`)
- **Path Parameter**: `indentType`: `Regular` | `Capital` | `Service`
- **Request Body** (`UpdatePoAutomationRequest`):
```json
{
  "runMode": "Both",
  "scheduleTime": "02:30:00",
  "clearScheduleTime": false,
  "sites": "1,2,3",
  "indentNumbers": "26-27/PI/NF1/000162,000163",
  "clearIndentNumbers": false,
  "isActive": true,
  "dryRun": false,
  "maxIndentsPerRun": 25,
  "clearMaxIndentsPerRun": false
}
```
- **Response `200 OK`**: Returns updated `PoAutomationConfigResponse`.

---

### 4.2 GRN Automation Configuration (PO → GRN)

Governs the automatic receiving of POs and generation of Goods Receipt Notes.

#### 4.2.1 Get GRN Automation Configuration
- **Method & Route**: `GET /api/automation/grn-automation`
- **Authentication**: ERP Bearer Token (Form `011171` `'I'`) OR `X-Automation-Api-Key`
- **Response `200 OK`**:
```json
{
  "isActive": true,
  "runMode": "Both",
  "scheduleTime": "04:00:00",
  "receiptMode": "Complete",
  "invoiceNumber": "AUTO-GRN-SYS",
  "sites": "1,2",
  "poTypes": ["Regular", "Capital"],
  "poNumbers": null,
  "dryRun": false,
  "maxPosPerRun": 25,
  "lastScheduledRunDate": "2026-10-06",
  "lastTriggeredAtUtc": "2026-10-06T04:00:02Z",
  "lastRunStatus": "Completed",
  "lastRunReference": "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d",
  "updatedAtUtc": "2026-10-05T14:30:00Z",
  "updatedBy": "admin"
}
```

#### 4.2.2 Update GRN Automation Settings (with "Run Now" support)
- **Method & Route**: `PUT /api/automation/grn-automation`
- **Authentication**: ERP Bearer Token (Form `011171` `'E'`) OR `X-Automation-Api-Key`
- **Description**: Updates settings. **Special Behaviour**: If `isActive=true` and `scheduleTime` is omitted or null, an immediate **Run Now** sweep is triggered synchronously, and the response contains both the updated config and the execution result!
- **Request Body** (`UpdateGrnAutomationRequest`):
```json
{
  "isActive": true,
  "runMode": "Both",
  "scheduleTime": "03:30",
  "clearScheduleTime": false,
  "receiptMode": "Complete",
  "invoiceNumber": "INV-2026-AUTO",
  "sites": "1,2",
  "dryRun": false,
  "maxPosPerRun": 30,
  "poTypes": ["Regular"],
  "poNumbers": "26-27/TE/NF1/000190"
}
```
- **Response `200 OK`**:
```json
{
  "config": { /* GrnAutomationConfigResponse */ },
  "execution": null /* Populated with ReceivePosResponse if immediate Run Now was triggered */
}
```

#### 4.2.3 Toggle Master Switch for GRN Automation
- **Method & Route**: `PUT /api/automation/grn-automation/enabled`
- **Request Body**: `{ "enabled": true }`

#### 4.2.4 Set Permitted PO Types for GRN Automation
- **Method & Route**: `PUT /api/automation/grn-automation/po-types`
- **Request Body**:
```json
{
  "poTypes": ["Regular", "Capital"]
}
```

#### 4.2.5 Set PO Numbers Filter for GRN Automation
- **Method & Route**: `PUT /api/automation/grn-automation/po-numbers`
- **Request Body**:
```json
{
  "poNumbers": "26-27/TE/NF1/000190, 000191"
}
```

---

### 4.3 System & Module Runtime Configuration

Per-module runtime settings for background dispatchers and engine cycles.

#### 4.3.1 List All Module Configurations
- **Method & Route**: `GET /api/automation/config`
- **Authentication**: `X-Automation-Api-Key`

#### 4.3.2 Get Configuration for a Specific Module
- **Method & Route**: `GET /api/automation/config/{module}`
- **Authentication**: `X-Automation-Api-Key`
- **Path Parameter**: `module` (e.g., `AutoShopCycle`)
- **Response `200 OK`**:
```json
{
  "module": "AutoShopCycle",
  "enableAgent": true,
  "enableModule": true,
  "mode": "Full",
  "pollIntervalSeconds": 300,
  "reconcileIntervalMinutes": 60,
  "workingHoursStart": "08:00:00",
  "workingHoursEnd": "20:00:00",
  "retryCount": 3,
  "parallelWorkers": 4,
  "loggingLevel": "Information",
  "isLicensed": true,
  "payloadRetentionDays": 30,
  "logRetentionDays": 60,
  "errorRetentionDays": 90,
  "updatedAtUtc": "2026-10-01T00:00:00Z",
  "updatedBy": "system"
}
```

#### 4.3.3 Update Configuration for a Module
- **Method & Route**: `POST /api/automation/config/{module}`
- **Authentication**: `X-Automation-Api-Key`
- **Request Body** (`UpdateConfigRequest`):
```json
{
  "enableAgent": true,
  "enableModule": true,
  "mode": "Full",
  "pollIntervalSeconds": 180,
  "workingHoursStart": "08:30:00",
  "workingHoursEnd": "19:30:00",
  "retryCount": 5,
  "parallelWorkers": 2,
  "updatedBy": "admin"
}
```

---

## 5. History, Runs & Monitoring APIs

### 5.1 Unified & Indent Process History

History of conversions with multi-stage workflow execution details.

#### 5.1.1 List Process Conversions (The Main History Grid)
- **Method & Route**: `GET /api/process-jobs`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `search` (string): Text filter on document/reference.
  - `workflow` (string): Filter by workflow name (e.g. `IndentToPurchaseOrder`).
  - `status` (string): `Pending`, `Running`, `Completed`, `Failed`, `Cancelled`, `Skipped`.
  - `company` (string): Company filter.
  - `trigger` (string): `Api`, `Manual`, `Timer`, `Chatbot`, `UserPrompt`, `ErpPush`.
  - `stage` (string): `Discovery`, `Allocation`, `Drafting`, `Authorisation`, `Finalisation`.
  - `indentType` (string): `Regular`, `Capital`, `Service`.
  - `from` / `to` (DateTimeOffset): UTC timestamp range.
  - `page` (int, default `1`): Page number.
  - `pageSize` (int, default `50`): Results per page (max 200).
- **Response `200 OK`**: Returns `PagedResult<ProcessJobRowResponse>`.

#### 5.1.2 Get Execution Details for a Specific Conversion
- **Method & Route**: `GET /api/process-jobs/{jobId:guid}`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Description**: Returns the complete stage timeline, outcomes per vendor group, created PO references, and any error diagnostics.

#### 5.1.3 Get History of All Attempts for a Specific Indent
- **Method & Route**: `GET /api/process-jobs/indent/{indentId:long}`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameter**: `indentType` (optional: `regular`, `capital`, `service` to disambiguate identical material vs service IDs).

#### 5.1.4 Retry a Failed Conversion as a New Execution
- **Method & Route**: `POST /api/process-jobs/{jobId:guid}/retry`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameter**: `triggeredBy` (string).

#### 5.1.5 List Trigger Runs (Unified or Module-Filtered)
- **Method & Route**: `GET /api/process-jobs/runs` OR `GET /api/automation/runs`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `module` (optional): `po-to-grn`, `indent-to-po`, or omit for unified merged runs.
  - `trigger`: Filter by trigger source.
  - `status`: Filter by run status (`Completed`, `Failed`, etc.).
  - `from` / `to`: UTC timestamp range.
  - `page` / `pageSize`: Pagination parameters.

#### 5.1.6 Get Details for a Trigger Run
- **Method & Route**: `GET /api/process-jobs/runs/{runId:guid}`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Description**: Automatically routes to either Indent-to-PO execution details or PO-to-GRN run details with associated receipts.

---

### 5.2 PO → GRN Run History

Dedicated endpoints for PO to GRN flow history.

#### 5.2.1 List PO → GRN Runs
- **Method & Route**: `GET /api/automation/po-to-grn/history` (or `GET /api/automation/runs?module=po-to-grn`)
- **Authentication**: ERP Bearer Token OR `X-Automation-Api-Key`
- **Query Parameters**: `trigger`, `status`, `from`, `to`, `page`, `pageSize`.
- **Response**: Each run item includes:
  - `runId` (Guid)
  - `workflow`: `"PoToGrn"`
  - `status`: `"Completed"` | `"Failed"` | `"Running"`
  - `posExamined` (int), `grnsCreated` (int)
  - `grnNumber`: string (e.g. `"26-27/GR/NF1/000045"`)
  - `grnNumbers`: string array (e.g. `["26-27/GR/NF1/000045"]`)
  - `poNumbers`: string (the PO numbers received in this run)

#### 5.2.2 Get Specific PO → GRN Run with Receipts
- **Method & Route**: `GET /api/automation/po-to-grn/history/{runId:guid}`
- **Authentication**: ERP Bearer Token OR `X-Automation-Api-Key`
- **Response**: Details of examined POs, created GRN numbers, lines received, skipped reasons, and receipt objects.

#### 5.2.3 List Itemized PO → GRN Receipts
- **Method & Route**: `GET /api/automation/po-to-grn/receipts`
- **Authentication**: ERP Bearer Token OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `search` (string): Text filter matching PO number, GRN number, or vendor code.
  - `status` (string): `Created`, `Skipped`, `Failed`.
  - `from` / `to`: UTC timestamp filter range.
  - `page` / `pageSize`: Pagination parameters.
- **Response**: Paged list of individual PO receipt records showing `poNumber`, `grnNumber`, `vendorCode`, `warehouseId`, `linesReceived`, `linesSkipped`, `status`, `reason`, and `recordedAtUtc`.

#### 5.2.4 PO → GRN Composite Dashboard
- **Method & Route**: `GET /api/automation/po-to-grn/dashboard` (also supports alias `/api/automation/po-to-grn/dashbord`)
- **Authentication**: ERP Bearer Token OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `days` (int, default `30`): Trailing days window for daily statistics.
  - `from` / `to`: Date or timestamp filter range. If `to` is a date without time (e.g. `2026-10-08`), it includes the entire day up to 23:59:59.
  - `date`: Filter for a single specific date (e.g. `2026-10-08`), automatically covering the entire day.
  - `order` (`desc` | `asc`, default `desc`): Order for `dailyStats`. Default is newest first (`desc`: today, yesterday, day before...), matching `recentJobs`.
- **Description**: Returns the unified dashboard structure containing overall execution summary, zero-filled activity statistics across the requested date window, the last completed job, detailed step/check breakdown for the last execution, and recent jobs.
- **Response `200 OK`**:
```json
{
  "summary": {
    "totalJobs": 7,
    "countsByStatus": {
      "Completed": 1,
      "Failed": 1,
      "Skipped": 5
    },
    "countsBySource": {
      "Po": 7
    },
    "successCount": 1,
    "successRate": 0.1429,
    "averageDurationMs": 2144.86,
    "totalTriggerAttempts": 9,
    "triggerAttemptsWithoutEligibleDocument": 1,
    "businessRefusalCount": 5,
    "technicalFailureCount": 1,
    "totalQuantityIssued": 3.0,
    "totalLinesIssued": 3
  },
  "dailyStats": [
    {
      "date": "2026-10-09",
      "issuesCreated": 1,
      "documentsConverted": 1,
      "documentsRefused": 0,
      "documentsFailed": 1,
      "totalQuantity": 1.0,
      "grnsCreated": 1,
      "posConverted": 1,
      "posRefused": 0,
      "posFailed": 1
    },
    {
      "date": "2026-10-08",
      "issuesCreated": 8,
      "documentsConverted": 8,
      "documentsRefused": 120,
      "documentsFailed": 0,
      "totalQuantity": 8.0,
      "grnsCreated": 8,
      "posConverted": 8,
      "posRefused": 120,
      "posFailed": 0
    }
  ],
  "lastCompletedJob": {
    "jobId": "e431f5b6-13a0-4ece-9cda-a5fa88b560c1",
    "runId": "545407ca-e428-4cc5-99f8-37236786aa38",
    "issueSource": "Po",
    "documentNumber": "26-27/PO/NF1/000145",
    "status": "Completed",
    "currentStage": "CreateGrn",
    "issueNumber": "26-27/GR/NF1/000080",
    "lineCount": 3,
    "totalQuantity": 3.0,
    "trigger": "Api",
    "triggeredBy": "Api",
    "startedAtUtc": "2026-10-05T12:07:36.8618562+00:00",
    "completedAtUtc": "2026-10-05T12:07:44.0708611+00:00",
    "durationMs": 7209,
    "reason": null
  },
  "lastExecution": {
    "jobId": "e431f5b6-13a0-4ece-9cda-a5fa88b560c1",
    "runId": "545407ca-e428-4cc5-99f8-37236786aa38",
    "issueSource": "Po",
    "documentNumber": "26-27/PO/NF1/000145",
    "status": "Completed",
    "currentStage": "CreateGrn",
    "issueNumber": "26-27/GR/NF1/000080",
    "startedAtUtc": "2026-10-05T12:07:36.8618562+00:00",
    "completedAtUtc": "2026-10-05T12:07:44.0708611+00:00",
    "durationMs": 7209,
    "reason": null,
    "steps": [
      {
        "stage": "Discovery",
        "operationName": "Validate document and prerequisites",
        "status": "Completed",
        "startedAtUtc": "2026-10-05T12:07:37.6584503+00:00",
        "completedAtUtc": "2026-10-05T12:07:37.7856501+00:00",
        "durationMs": 127,
        "remarks": null,
        "erpDocumentRef": null
      },
      {
        "stage": "DocumentControl",
        "operationName": "Verify issue numbering configuration",
        "status": "Completed",
        "startedAtUtc": "2026-10-05T12:07:38.502238+00:00",
        "completedAtUtc": "2026-10-05T12:07:38.8323935+00:00",
        "durationMs": 330,
        "remarks": null,
        "erpDocumentRef": null
      },
      {
        "stage": "CreateIssue",
        "operationName": "Create Goods Receipt Note in ERP",
        "status": "Completed",
        "startedAtUtc": "2026-10-05T12:07:41.4981754+00:00",
        "completedAtUtc": "2026-10-05T12:07:44.0708611+00:00",
        "durationMs": 2572,
        "remarks": null,
        "erpDocumentRef": "26-27/GR/NF1/000080"
      }
    ],
    "checks": [
      {
        "name": "PoFound",
        "passed": true,
        "detail": "PO 26-27/PO/NF1/000145 found and verified."
      },
      {
        "name": "Authorised",
        "passed": true,
        "detail": "The PO is authorised."
      }
    ],
    "shortages": null,
    "lines": [
      {
        "itemCode": "ITEM",
        "warehouseCode": "1",
        "stockId": 29701,
        "lineNo": 1001,
        "quantity": 1.0,
        "sjoId": 392,
        "woId": null,
        "randomNumber": 1,
        "inwardNo": "26-27/GR/NF1/000080"
      }
    ]
  },
  "recentJobs": [
    {
      "jobId": "e431f5b6-13a0-4ece-9cda-a5fa88b560c1",
      "runId": "545407ca-e428-4cc5-99f8-37236786aa38",
      "issueSource": "Po",
      "documentNumber": "26-27/PO/NF1/000145",
      "status": "Completed",
      "currentStage": "CreateGrn",
      "issueNumber": "26-27/GR/NF1/000080",
      "lineCount": 3,
      "totalQuantity": 3.0,
      "trigger": "Api",
      "triggeredBy": "Api",
      "startedAtUtc": "2026-10-05T12:07:36.8618562+00:00",
      "completedAtUtc": "2026-10-05T12:07:44.0708611+00:00",
      "durationMs": 7209,
      "reason": null
    }
  ]
}
```

---

### 5.3 Summary & KPI Analytics

#### 5.3.1 Process Conversion Summary (Unified / Flow-Specific)
- **Method & Route**: `GET /api/process-jobs/summary` OR `GET /api/automation/po-to-grn/summary`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `module` (optional): `indent-to-po`, `po-to-grn`, or omitted for **unified KPIs across both flows**.
  - `from` / `to`: Date filtering window.
- **Response `200 OK`**:
```json
{
  "totalJobs": 120,
  "countsByStatus": {
    "Completed": 105,
    "Failed": 10,
    "Skipped": 5
  },
  "successCount": 105,
  "successRate": 0.875,
  "averageDurationMs": 2450.5,
  "totalTriggerAttempts": 150,
  "triggerAttemptsWithoutEligibleIndent": 30,
  "businessRefusalCount": 5,
  "technicalFailureCount": 10
}
```

---

### 5.4 Daily Statistics & Trend Charts

#### 5.4.1 Daily Conversion Trends (Unified / Flow-Specific)
- **Method & Route**: `GET /api/process-jobs/daily-summary` OR `GET /api/automation/po-to-grn/daily-summary`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `module` (optional): `indent-to-po`, `po-to-grn`, or omit for unified combined counts.
  - `days` (int, default `30`): Number of trailing days.
- **Response `200 OK`**:
```json
[
  {
    "date": "2026-10-05",
    "purchaseOrdersCreated": 18,
    "indentsConverted": 18,
    "indentsFailed": 1
  },
  {
    "date": "2026-10-06",
    "purchaseOrdersCreated": 22,
    "indentsConverted": 22,
    "indentsFailed": 0
  }
]
```

---

## 6. Automation Execution & Trigger APIs

### 6.1 Convert Indents to Purchase Orders

Triggers creation of real POs in the ERP from pending authorised indents.

#### 6.1.1 List Eligible Indents Preview (Read-Only)
- **Method & Route**: `GET /api/automation/indent-to-po/eligible`
- **Authentication**: ERP Bearer Token (Form `011171` `'I'`) OR `X-Automation-Api-Key`
- **Query Parameters**:
  - `sites` (string): Comma-separated site IDs (e.g. `1,2`).
  - `indentTypes` (string[]): `regular`, `capital`, `service`.
  - `max` (int, default `100`): Maximum results to return.
- **Response `200 OK`**:
```json
[
  {
    "indentId": 10045,
    "indentNumber": "26-27/PI/NF1/000162",
    "indentType": "Regular",
    "siteId": 1,
    "siteCode": "NF1",
    "status": "Authorised",
    "indentDate": "2026-10-05",
    "requestedBy": "J. Doe"
  }
]
```

#### 6.1.2 Convert Eligible Indents (The Core Conversion Trigger)
- **Method & Route**: `POST /api/automation/indent-to-po` (Alias: `POST /api/automation/indent-to-po/convert`)
- **Authentication**: ERP Bearer Token (Form `011171` `'E'`) OR `X-Automation-Api-Key`
- **Query Parameter**: `?trigger=manual|timer|api`
- **Request Body** (`ConvertIndentsRequest`):
```json
{
  "indentId": null,
  "sites": [1, 2],
  "indentTypes": ["Regular", "Capital"],
  "indentNumbers": ["26-27/PI/NF1/000162"],
  "maxIndents": 10,
  "dryRun": false
}
```
- **Response `200 OK`** (`ConvertIndentsResponse`):
```json
{
  "examined": 1,
  "purchaseOrdersCreated": 1,
  "purchaseOrdersPlanned": 1,
  "dryRun": false,
  "indentTypes": ["Regular", "Capital"],
  "indentNumbers": ["26-27/PI/NF1/000162"],
  "indents": [
    {
      "indentId": 10045,
      "indentNumber": "26-27/PI/NF1/000162",
      "indentType": "Regular",
      "siteId": 1,
      "converted": true,
      "purchaseOrders": [
        {
          "poNumber": "26-27/TE/NF1/000045",
          "poId": 50123,
          "vendorCode": "VEND001",
          "itemCount": 3
        }
      ],
      "notes": []
    }
  ],
  "skipped": [],
  "notFound": []
}
```

#### 6.1.3 Start Tracked Conversion Job
- **Method & Route**: `POST /api/process-jobs`
- **Authentication**: ERP Bearer Token (Form `011172` `'I'`) OR `X-Automation-Api-Key`
- **Request Body** (`StartProcessJobRequest`):
```json
{
  "indentId": 10045,
  "indentTypes": ["Regular"],
  "sites": [1],
  "maxIndents": 1,
  "trigger": "Manual",
  "triggeredBy": "admin",
  "triggerReference": "Ticket #1234"
}
```

---

### 6.2 Vendor-Specific Purchase Order Creation

Orders everything a specific vendor has outstanding.

- **Method & Route**: `POST /api/automation/indent-to-po/vendor`
- **Authentication**: `X-Automation-Api-Key`
- **Request Body** (`CreatePurchaseOrderRequest`):
```json
{
  "vendorCode": "VEND001",
  "indentTypes": ["Regular", "Capital"]
}
```
- **Response `200 OK`**:
```json
{
  "indentTypes": ["Regular", "Capital"],
  "purchaseOrders": [
    {
      "poNumber": "26-27/TE/NF1/000046",
      "poId": 50124,
      "vendorCode": "VEND001",
      "itemCount": 5
    }
  ],
  "notes": []
}
```

---

### 6.3 PO → GRN Receipt Creation

Triggers automatic stock receiving and Goods Receipt Note creation for authorised POs.

- **Method & Route**: `POST /api/automation/po-to-grn`
- **Authentication**: `X-Automation-Api-Key`
- **Query Parameter**: `?trigger=manual|timer|api`
- **Request Body** (`ReceivePosRequest`):
```json
{
  "poIds": null,
  "poNumbers": ["26-27/TE/NF1/000046"],
  "sites": [1],
  "poTypes": ["Regular"],
  "receiptMode": "Complete",
  "maxPos": 25,
  "dryRun": false
}
```
- **Response `200 OK`** (`ReceivePosResponse`):
```json
{
  "trigger": "Manual",
  "dryRun": false,
  "receiptMode": "Complete",
  "poTypes": ["Regular"],
  "runId": "4c98a58a-67a3-48b9-873b-eb6368d71234",
  "examined": 1,
  "grnsCreated": 1,
  "grnsPlanned": 1,
  "stoppedReason": null,
  "notFound": [],
  "results": [
    {
      "poId": 50124,
      "poNumber": "26-27/TE/NF1/000046",
      "poType": "Regular",
      "siteId": 1,
      "vendorCode": "VEND001",
      "warehouseId": 10,
      "status": "Completed",
      "planned": false,
      "grnId": 90123,
      "grnNumber": "26-27/GR/NF1/000012",
      "linesReceived": 5,
      "linesSkipped": 0,
      "notes": []
    }
  ]
}
```

---

### 6.4 Engine Cycle Trigger

Immediately starts an AutoShop engine cycle without waiting for the timer.

- **Method & Route**: `POST /api/automation/run-now`
- **Authentication**: `X-Automation-Api-Key`
- **Response `200 OK`**:
```json
{
  "started": true,
  "jobId": "f7d33e72-d5aa-43d9-9524-811c759556a3",
  "reason": "Enqueued manually"
}
```

---

## 7. Job Engine & Lifecycle Control APIs

### 7.1 Automation Dashboard & Status Counts
- **Method & Route**: `GET /api/automation/dashboard`
- **Authentication**: ERP Bearer Token OR `X-Automation-Api-Key`
- **Response `200 OK`**:
```json
{
  "jobsByStatus": {
    "Completed": 450,
    "Running": 1,
    "Failed": 12,
    "Pending": 3
  },
  "totalJobs": 466,
  "liveCycleJobId": "f7d33e72-d5aa-43d9-9524-811c759556a3",
  "liveCycleStartedAtUtc": "2026-10-06T11:45:00Z"
}
```

### 7.2 List & Inspect Automation Jobs
- **Method & Route**: `GET /api/automation/jobs`
  - Auth: `X-Automation-Api-Key`
  - Query Params: `status`, `workflowType`, `documentId`, `from`, `to`, `page`, `pageSize`.
- **Method & Route**: `GET /api/automation/jobs/{jobId:guid}`
  - Auth: `X-Automation-Api-Key`
  - Returns full job information and step-by-step timeline.

### 7.3 Job Error Details
- **Method & Route**: `GET /api/automation/jobs/{jobId:guid}/errors`
  - Auth: `X-Automation-Api-Key`
  - Returns layman and technical error messages, failed steps, and ERP endpoint details.

### 7.4 Job Retry, Resume & Cancellation
- **Retry**: `POST /api/automation/jobs/{jobId:guid}/retry`
  - Auth: `X-Automation-Api-Key`
  - Elevates job priority (+100) and re-queues from first incomplete step.
- **Resume**: `POST /api/automation/jobs/{jobId:guid}/resume`
  - Auth: `X-Automation-Api-Key`
  - Re-queues job at standard priority without repeating completed steps.
- **Cancel**: `POST /api/automation/jobs/{jobId:guid}/cancel`
  - Auth: `X-Automation-Api-Key`
  - Request Body:
```json
{
  "cancelledBy": "admin",
  "reason": "Document cancelled in ERP"
}
```

---

## 8. Health & Diagnostics APIs

- **Method & Routes**:
  - `GET /health` (Convenience alias)
  - `GET /api/automation/health`
- **Authentication**: Unauthenticated (Loopback / Public monitoring)
- **Response `200 OK` (or `503 Service Unavailable` if unhealthy)**:
```json
{
  "status": "Healthy",
  "version": "1.0.0",
  "startedAtUtc": "2026-10-06T06:00:00Z",
  "uptimeSeconds": 20800.5,
  "checks": {
    "service": "Healthy",
    "database": "Healthy",
    "erp": "Healthy"
  }
}
```

---

## 9. Calling & Testing Instructions

### 9.1 Using cURL

#### Check Health:
```bash
curl -X GET http://localhost:5050/health
```

#### Trigger Indent-to-PO Conversion (API Key):
```bash
curl -X POST "http://localhost:5050/api/automation/indent-to-po?trigger=api" \
  -H "X-Automation-Api-Key: YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"indentTypes": ["Regular"], "dryRun": false}'
```

#### Get Process Job History Summary (ERP JWT):
```bash
curl -X GET "http://localhost:5050/api/process-jobs/summary" \
  -H "Authorization: Bearer YOUR_ERP_JWT_TOKEN"
```

#### Trigger PO-to-GRN Receiving (API Key):
```bash
curl -X POST "http://localhost:5050/api/automation/po-to-grn?trigger=manual" \
  -H "X-Automation-Api-Key: YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"dryRun": false, "receiptMode": "Complete"}'
```

---

### 9.2 Using PowerShell

#### Fetch Dashboard Stats:
```powershell
$headers = @{
    "X-Automation-Api-Key" = "YOUR_API_KEY"
}
Invoke-RestMethod -Uri "http://localhost:5050/api/automation/dashboard" -Headers $headers -Method GET
```

#### Toggle GRN Automation Enabled:
```powershell
$headers = @{
    "X-Automation-Api-Key" = "YOUR_API_KEY"
    "Content-Type"         = "application/json"
}
$body = @{ enabled = $true } | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5050/api/automation/grn-automation/enabled" `
    -Headers $headers `
    -Method PUT `
    -Body $body
```

#### Query Conversion History Grid:
```powershell
$headers = @{
    "X-Automation-Api-Key" = "YOUR_API_KEY"
}
$response = Invoke-RestMethod -Uri "http://localhost:5050/api/process-jobs?page=1&pageSize=10" `
    -Headers $headers `
    -Method GET

$response.items | Format-Table jobId, document, indentType, status, poNumber
```
