# Work Summary - Tuesday, October 13, 2026

**Branch:** `PO_TO_GRN`  
**Author:** Meet <meet.sourcepro@gmail.com>

---

## Overview

Today we implemented two major features and guardrails for the **PO → GRN Automation Flow**:
1. **ERP Document Control Pre-Flight Check (GRN Authorization Guardrail)**
2. **Auto-Generation of Inward Numbers for Items**

---

## 1. GRN Authorization Check in ERP Document Control

### Requirement / Problem
The ERP allows organizations to configure whether GRNs require approval/authorization before posting stock. If Document Control has GRN Authorization turned **OFF**, automated GRN creation should **not** proceed because it violates the company's approval policy.
The user requested an instant check whenever clicking "Run" or turning ON the automation toggle. If authorization is OFF, the system must immediately stop and show a clear error message.

### What Was Done
1. **Pre-Flight Check in Automation Trigger & Toggle Endpoints:**
   - **`POST /api/automation/po-to-grn/run`** ([`PoToGrnEndpoints.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Worker/Flows/PoToGrn/Endpoints/PoToGrnEndpoints.cs)):
     Before starting a sweep, the service checks ERP Document Control for all configured sites and PO types (Regular `OR` and Capital `CG`). If any site has authorization disabled, it refuses the run immediately with HTTP `422 Unprocessable Entity`.
   - **`POST /api/automation/po-to-grn/settings/enabled` & `PUT /api/automation/po-to-grn/settings`** ([`GrnAutomationEndpoints.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.Worker/Flows/PoToGrn/Endpoints/GrnAutomationEndpoints.cs)):
     When toggling automation ON, it validates that GRN authorization is active in ERP Document Control before saving the setting.
2. **Pre-Flight Validation Method:**
   - Added `CheckAuthorizationRequiredAsync` in [`PoToGrnService.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoToGrnService.cs).
   - Queries `GET /api/v1/admin/documentControl/getDefaultDocumentDetail/{finYear}/GR/{subType}/{siteId}`.
   - Inspects the ERP flag `isAutorisationRequired` (also checking `isAuthorizationRequired` to handle spelling differences).
   - If disabled, returns a refusal message:
     ```
     Site NF1 (Regular): GRN Authorization is turned OFF in ERP Document Control. Enable GRN authorization in ERP Document Control first.
     ```
3. **Execution Guardrail:**
   - During background processing of each PO, `ReceivePoAsync` also re-verifies `document.IsAuthorizationRequired` as a failsafe before creating any GRN.

---

## 2. Auto-Generation of Item Inward Numbers

### Requirement / Problem
Previously, any PO item requiring an inward number (`miminwdreq == true`) was treated as a manual parameter item and skipped.
However, ERP has a policy setting (`isinwardAutoGen`) that allows the system to auto-generate inward numbers:
- **If `isinwardAutoGen` is ON**: Items requiring an inward number should **not** be skipped. The agent must call the ERP API to generate the inward number and stamp it on the GRN line.
- **If `isinwardAutoGen` is OFF**: Items requiring an inward number must be **skipped** with the error message:
  `"Item <ITEMCODE> needs an inward number (autogeneration of inward number is not enabled)"`.
- **Other parameter items**: Items that require a batch number, heat number, serial number, manufacturing batch number, shelf life expiry date, or LBT dimensions must **still be skipped**, because those values cannot be auto-generated.

### What Was Done
1. **ERP Policy Detection ([`PoToGrnService.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoToGrnService.cs)):**
   - Updated `GetInventoryPolicyAsync` to fetch and cache:
     - `isinwardAutoGen` (whether auto-generation is enabled).
     - `isinwardAutoGenItemWise` (whether the number series is per-item or global).
   - Stored in `RunContext` for each sweep.

2. **Line Classifier Logic ([`GrnLineClassifier.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/GrnLineClassifier.cs)):**
   - Updated `Select` and `ParameterReason`:
     - If `line.Flag("miminwdreq")`:
       - If `isinwardAutoGen` is **true**: The line is **allowed** (not flagged as needing manual inward input).
       - If `isinwardAutoGen` is **false**: Adds `"an inward number (autogeneration of inward number is not enabled)"` to the list of required details.
     - Batch (`mimbchreqd`), heat (`mimheatreq`), serial (`mimitmsrreqd`), mfg batch (`mimmfgreq`), shelf life (`isShelfLife`), and size (`xgrndsize`) remain blockers regardless of this flag.

3. **Inward Number Fetching & Sequential Generation ([`PoToGrnService.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoToGrnService.cs)):**
   - Added endpoint `/api/v1/inventory/grn/getmaxParamValue` to [`PoGrnOptions.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoGrnOptions.cs).
   - In `ReceiveAtWarehouseAsync`, for receivable lines needing inward numbers:
     - Calls `POST /api/v1/inventory/grn/getmaxParamValue` with `{ "prefix": "2627-", "itmcode": line.ItemCode }`.
     - Reads `data[0].maxinwd` (e.g. `"2627-0000001"`).
     - If multiple items in the same GRN require an inward number, sequences the 7-digit numeric suffix sequentially (`format + (((Number(maxsrnumber)+1)+'').padStart(7,'0'))`), matching the ERP web client's exact algorithm.
     - Stores the resolved inward number in `line.InwardNumber`.

4. **Payload Building ([`GrnPayloadBuilder.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/GrnPayloadBuilder.cs)):**
   - Line 188 now stamps `["xgrndinwd"] = line.InwardNumber ?? string.Empty` into the `grnitemDetails` array for `POST /api/v1/inventory/grn/create`.

---

## 3. Testing & Verification

1. **Unit Tests ([`GrnLineClassifierTests.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/tests/NewHorizon.Automation.UnitTests/Flows/PoToGrn/GrnLineClassifierTests.cs)):**
   - `Inward_number_item_is_skipped_when_inward_auto_gen_is_disabled`: Verifies proper skip reason when flag is off.
   - `Inward_number_item_is_receivable_when_inward_auto_gen_is_enabled`: Verifies item is accepted when flag is on.
   - `Inward_item_needing_other_parameters_is_still_skipped_even_when_inward_auto_gen_is_enabled`: Verifies batch/heat items are still skipped.

2. **Service Tests ([`PoToGrnServiceTests.cs`](file:///d:/TakeBkp/IMMS-TNG-WEB/ProjectNewHorizon_Dev/NH_Enterprise_General_Prod/AutomationAgentNH/tests/NewHorizon.Automation.UnitTests/Flows/PoToGrn/PoToGrnServiceTests.cs)):**
   - `ResolveNextInwardNumber_increments_consecutive_lines_within_same_grn`: Verifies multi-item numbering (`0000001`, `0000002`).
   - `ResolveNextInwardNumber_falls_back_to_0000001_when_empty`: Verifies fallback formatting.
   - `Po_with_inward_item_is_received_and_stamped_when_inward_auto_gen_is_enabled`: End-to-end receipt and payload verification.
   - `Po_with_inward_item_is_skipped_when_inward_auto_gen_is_disabled`: Verifies skip notes when policy flag is off.
   - `CheckAuthorizationRequiredAsync_reports_refusals_when_flag_is_off`: Verifies Document Control pre-flight refusal.

3. **Test Results:**
   - **Unit Tests:** All **437 unit tests** passed.
   - **PO → GRN API Tests:** All **33 integration tests** passed.

---

## Files Modified & Created

| File | Status | Description |
|---|---|---|
| `TODAY_WORK_SUMMARY.md` | Created | Work summary for Tuesday, October 13, 2026 |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/GrnLineClassifier.cs` | Modified | Added `isInwardAutoGen` flag support to line classification |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/GrnLineTotals.cs` | Modified | Added `InwardNumber` property |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/GrnPayloadBuilder.cs` | Modified | Populates `xgrndinwd` with generated inward number |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoGrnOptions.cs` | Modified | Added `MaxParamValue` endpoint URL |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoGrnModels.cs` | Modified | Added `GrnAuthorizationCheckResult` model |
| `src/NewHorizon.Automation.ErpClient/Flows/PoToGrn/PoToGrnService.cs` | Modified | Added Document Control check, inward number generation, policy caching |
| `src/NewHorizon.Automation.Worker/Flows/PoToGrn/Endpoints/GrnAutomationEndpoints.cs` | Modified | Added Document Control authorization check when toggling settings |
| `src/NewHorizon.Automation.Worker/Flows/PoToGrn/Endpoints/PoToGrnEndpoints.cs` | Modified | Added pre-flight Document Control check on trigger |
| `tests/NewHorizon.Automation.UnitTests/Flows/PoToGrn/GrnFakeErp.cs` | Modified | Added mock routes for `getmaxParamValue` and Document Control |
| `tests/NewHorizon.Automation.UnitTests/Flows/PoToGrn/GrnLineClassifierTests.cs` | Modified | Added unit tests for inward number auto-generation |
| `tests/NewHorizon.Automation.UnitTests/Flows/PoToGrn/PoToGrnServiceTests.cs` | Modified | Added unit tests for inward sequencing and service flow |
| `tests/NewHorizon.Automation.IntegrationTests/Flows/PoToGrn/PoToGrnApiTests.cs` | Modified | Updated integration tests with Document Control mock setup |
