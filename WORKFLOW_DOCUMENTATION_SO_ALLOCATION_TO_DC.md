# Simple Guide: Sales Order Allocation to Delivery Challan Automation

---

## 1. What Does This Automation Do?

In New Horizon ERP, when a Sales Order is approved and stock is reserved (allocated), someone has to manually create a **Delivery Challan (DC)** so goods can be shipped.

This automation does that job **automatically**:
* It takes approved order allocations.
* It checks warehouse stock.
* It creates the Delivery Challan in ERP automatically without any manual typing.

---

## 2. What Problems Did We Fix?

### Problem 1: Stop Automatic Approval (Leave DC "Open" for Manager)
* **What was happening:** Previously, the computer created the Delivery Challan and **immediately authorized/approved it**.
* **Why that was wrong:** In the real warehouse, a manager must first physically check the items in the boxes before approving the DC.
* **The fix:** We changed the settings so the computer only **creates** the DC and leaves it in **"Open"** status. The warehouse manager can now check the boxes and click "Authorize" manually in the ERP.

---

### Problem 2: Fix Remaining / Partial Orders (Why Orders 60 & 61 Failed)
* **What was happening:**
  * Imagine a customer ordered **2 items**.
  * Last week, you delivered **1 item**.
  * Today, there is only **1 item left** to deliver.
  * The computer made a mistake: it tried to create a DC for **2 items** again! The ERP rejected it saying: *"You cannot deliver 2 items, only 1 is left!"*
* **The fix:** We fixed the calculation. Now the computer checks: *"If 1 item was already delivered, only create the DC for the remaining 1 item!"*
* **Result:** Orders `000060` and `000061` converted successfully into Delivery Challans.

---

### Problem 3: Block Duplicate Delivery Challans
* If an order was already 100% delivered, the computer stops and says:
  > *"This order already has Delivery Challan (000087). No duplicate created."*

---

## 3. The 3 Flags We Changed (Explained in Simple Words)

A **flag** is just a simple **YES / NO** switch. Here are the 3 flags we work with:

| Flag Name | Where It Is | Value | What It Does in Plain English |
| :--- | :--- | :--- | :--- |
| **`finFlag`** | In the DC creation request | Changed from **`"Y"` to `"N"`** | **The Auto-Approve Switch.**<br>• `"Y"` = Auto-approve DC immediately *(Wrong)*.<br>• `"N"` = Do **not** auto-approve. Leave DC in **"Open"** status for manual manager approval *(Correct)*. |
| **`authFlag`** | In the DC header | Set to **`"Y"`** | **The "Needs Approval" Switch.**<br>Tells ERP: *"Yes, this DC requires a manager's sign-off before shipping."* |
| **`automationEnabled`** | In the Automation Config | **`true` (ON)** or **`false` (OFF)** | **The Master Power Switch.**<br>• `true` = Automation is turned ON.<br>• `false` = Automation is turned OFF and safely deletes any saved times so nothing runs by accident. |

---

## 4. How the 3 Automation Modes Work

When you turn the automation ON (`automationEnabled: true`), you can choose **1 of 3 modes**:

### Mode 1: `"TIMER"` (Daily Scheduled Alarm)
* **What it means:** The computer automatically runs every day at a specific time (e.g. `12:30 PM`) and converts **all** eligible orders across all customers.
* **Rule:** You only set the time. Order numbers are left blank (`null`).

### Mode 2: `"INDIVIDUAL"` (Hand-Picked Orders Only)
* **What it means:** The computer does **not** use a timer. It only converts the exact order numbers you give it (e.g., `["000060", "000061"]`).
* **Rule:** You only set order numbers. Time is left blank (`null`).

### Mode 3: `"BOTH"` (Timer + Hand-Picked Orders)
* **What it means:** At the scheduled time (e.g. `12:30 PM`), the computer runs, but **only** for the specific orders you listed.

---

## 5. How to Test the 2 APIs (Simple Examples)

### API 1: Setting Up the Automation (`AutomationConfig`)

#### Example A: Turn the Master Switch OFF (Safety Shutoff)
* **URL:** `POST http://localhost:5080/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig`
* **Header:** `X-Automation-Api-Key: dev-local-key`
* **Send this Body:**
  ```json
  {
    "automationEnabled": false
  }
  ```
* **What happens:** The system turns OFF and clears all timers and order numbers to `null`.

#### Example B: Set Daily Timer to 12:30 PM
* **Send this Body:**
  ```json
  {
    "automationEnabled": true,
    "automationMode": "TIMER",
    "automationTime": "12:30"
  }
  ```
* **What happens:** The system will automatically run every day at 12:30 PM for all ready orders.

---

### API 2: Converting Orders into Delivery Challans (`CreateDeliveryChallansFromAllocations`)

#### Example: Convert Orders 000060 and 000061
* **URL:** `POST http://localhost:5080/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallansFromAllocations`
* **Header:** `X-Automation-Api-Key: dev-local-key`
* **Send this Body:**
  ```json
  {
    "oafNumbers": ["000060", "000061"]
  }
  ```
* **What You Get Back:**
  ```json
  {
    "success": true,
    "message": "Delivery Challan created successfully. Authorization is pending.",
    "data": {
      "createdDCCount": 2,
      "results": [
        {
          "oafNo": "000060",
          "dcNumber": "26-27/TE/NF1/000087",
          "dcStatus": "Open",
          "authorized": false,
          "message": "Delivery Challan created successfully. Pending manual authorization."
        },
        {
          "oafNo": "000061",
          "dcNumber": "26-27/TE/NF1/000088",
          "dcStatus": "Open",
          "authorized": false,
          "message": "Delivery Challan created successfully. Pending manual authorization."
        }
      ]
    }
  }
  ```

---

## 6. Live Test Proof (Already Tested in ERP)

We tested this live in your ERP database:
1. **OAF `000060`** ➔ Created Delivery Challan **`26-27/TE/NF1/000087`** (Status: **Open**, Waiting for Manager Approval).
2. **OAF `000061`** ➔ Created Delivery Challan **`26-27/TE/NF1/000088`** (Status: **Open**, Waiting for Manager Approval).
3. Re-running the same orders immediately returned:
   > *"Allocation already has a Delivery Challan. Duplicate DC prevented."*
4. All **377 automated tests** passed with 0 errors.
