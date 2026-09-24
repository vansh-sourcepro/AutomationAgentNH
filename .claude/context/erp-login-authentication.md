# ERP authentication is the user login endpoint, not a service token

**Confirmed against the live ERP on 2026-07-29** (`http://localhost:4400`, swagger at
`/swagger/index.html`): there is no client-credentials endpoint, and the ERP team is not adding one.
The agent signs in through the same endpoint the ERP UI uses. This **supersedes §15 of the design
doc** (`/api/auth/service-token`, `client_id` / `client_secret`, `type = service`, 60-minute TTL).

```
POST {BaseUrl}/api/v1/auth/login
{ "userName": "su", "password": "…", "connStr": "Server=…;Database=…;uid=sa;pwd=;…",
  "isCEFlag": false, "appID": "", "userId": "" }

200 → { "data": { "token": { "value": "<jwt>", "validTo": "2026-07-30T03:01:23Z" }, "uid": "…" },
        "success": true, "message": null }
```

Verified facts, each one checked against the running ERP rather than assumed:

- **The body decides the outcome, not the status.** A refusal is HTTP **400** with
  `success: false` and a message key (`InvalidUsernamePasswordKey`,
  `connStr - TypeCastingNotValidKey`), not a 401. `ErpTokenProvider` therefore reads the envelope
  before classifying.
- **`connStr` is a plain SQL connection string**, parsed by the ERP — a malformed one comes back as
  "Format of the initialization string does not conform…". It is the **ERP's** database, opened by
  the ERP to resolve the login; the agent never connects to it. Sending it empty makes the ERP fall
  back to its own configured database.
- **Swagger declares a required `CompanyId` header; the endpoint does not enforce it.** The agent
  does not send one. If a future ERP build starts rejecting the call, that is the first thing to look at.
- **Tokens last 24 hours** and the response states the absolute expiry, so `validTo` is what the
  cache honours; `TokenTtlHours` is only a fallback for a response that omits it.

Everything — URL, path, user name, password, `connStr` — lives in `AutomationAgent:ErpApi` in
`appsettings.json`, in clear.

**Why:** the client asked for it explicitly. The agent runs on a private network on a Windows
server the client owns, and the API **port changes per installation**, so an operator has to be able
to correct any of it without a rebuild. This overrides the design doc's "secrets in a protected
store" for these values — do not "fix" it back into user-secrets.

**How to apply:** never log in from operation code. `ErpAuthHandler` asks `ErpTokenProvider`, which
holds one cached token for the process, signs in once for a stampede of workers, and re-signs on
401. `ErpLoginStartupService` warms it at startup so a wrong password shows up in the log then
rather than mid-job. Related: [[single-tenant-deployment]].
