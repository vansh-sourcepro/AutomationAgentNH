# ERP frontend: PO Automation screen — rights & menu

The PO Automation screens live in the ERP frontend (`WebApp2`, module `automation`). They call the
Automation Agent directly (`environment.automationApiUrl`), not the ERP API — but their **menu
visibility and in-page rights** come from the ERP the same way every other page's do.

## How ERP rights work here

`WebAPICore/NewHorizon.API/wwwroot/Module/ModuleInfo.xml` is the single source of truth for pages,
their URLs and their applicable rights letters. `AuthController` reads it at login to build the
user's menu (filtered to the forms their roles grant); `RoleMasterController` reads it to build the
rights grid in Role Master. There is **no separate DB seed for forms** — add the node to the XML,
restart the API, grant it in Role Master.

`GlobalService.CheckRights()` in WebApp2 matches a page on its **first three path segments**, so the
routes are `/automation/poautomation/config/{list|edit|view}` and
`/automation/poautomation/history/{list|run}` — every page under a section resolves to one
`pageUrl` (`/automation/poautomation/config/` or `/automation/poautomation/history/`).

Rights letters: `A` add, `E` edit, `D` delete, `I` inquiry/list, `P` print, `U` authorize/update,
`X` export. This feature uses `E` (save config), `I` (see the screens), `U` (Run now).

## What is already done

1. **`ModuleInfo.xml`** — two treenodes added under `FrmAdminMasters` (`NodeData 0.11.1`):
   - `ID="011171"` `frmPoAutomationConfig` — `PageUrl="/automation/poautomation/config/"`,
     `Url=".../config/list"`, `Rights="EIU"`, `PageTitle="menu_poautomationconfig"`.
   - `ID="011172"` `frmPoAutomationHistory` — `PageUrl="/automation/poautomation/history/"`,
     `Url=".../history/list"`, `Rights="I"`, `PageTitle="menu_poautomationhistory"`.
2. **`menu.json`** (all 8 languages, `WebApp2/src/assets/languages/*/common/common/menu.json`) —
   `menu_poautomation`, `menu_poautomationconfig`, `menu_poautomationhistory` added. These feed
   `localStorage.menupagetitle` at login, which the nav and breadcrumbs resolve titles against.
3. **Interim client-side nav injection** — `WebApp2 app-nav.component.ts` `injectAutomationDashboardMenu()`
   still pushes the two entries into the Admin Masters column with interim rights (`EIU` / `I`) so
   the feature is usable before step 4 is run. **Remove that method and its call once step 4 is
   confirmed on the target ERP** — the entries then arrive in `currentUser.menu` normally and
   `CheckRights()` returns the real per-role rights.

## Remaining manual steps (per ERP installation)

1. Deploy the updated `ModuleInfo.xml` with the ERP API and **restart it** so the new forms load.
   Super users are auto-granted (`AssignNewAddedFormToSuperUser`); they see the pages at next login.
2. In the ERP: **Admin → User Management → Role Management** (`/admin/usermgmt/roles`), open each
   role that should manage automation, tick the rights for *PO Automation Configuration* and
   *PO Automation Run History*, save.
3. Users in those roles log out and back in — the nav entry appears and the in-page Edit / Run now
   buttons light up per their granted rights.
4. Delete `injectAutomationDashboardMenu()` (and its call, and the `translation()` interim
   fallbacks) from `app-nav.component.ts`, and rebuild WebApp2.

## Agent side (not ERP DB)

The Agent must be reachable from the browser: `AutomationAgent:InboundJwt:SigningKey` = the ERP's
JWT signing secret, `Host:BindToLoopbackOnly=false`, `Cors:AllowedOrigins` = the WebApp2 origin(s),
and the port firewalled to the ERP/web tier. See `deploy/README.md`.
