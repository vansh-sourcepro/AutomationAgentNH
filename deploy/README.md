# Deployment / environment setup

## The two databases

The agent has **its own** database, entirely separate from the ERP's. Nothing in this solution may
ever connect to the ERP database — the agent reads and writes ERP data only through ERP application
APIs, so ERP validation, permissions, audit and transactions always apply.

| | Database | Owned by | This solution connects? |
|---|---|---|---|
| ERP | e.g. `PGTPL_MihiR_A_11062026` | ERP product | **Never** |
| Automation agent | `PGTPL_AutomationAgent` | this solution | Yes — the only connection string it has |

The `PGTPL_` prefix matches the client-code grouping already used on the server. The `_<user>_A_<ddMMyyyy>`
suffix is deliberately **not** used: that pattern marks dated ERP copies, whereas the agent database
is long-lived state (jobs, checkpoints, logs) that must outlive any individual ERP refresh.

## Provisioning a new installation

```powershell
# 1. Create the database (run against master on the target instance)
sqlcmd -S <server>\<instance> -Q "IF DB_ID('PGTPL_AutomationAgent') IS NULL CREATE DATABASE [PGTPL_AutomationAgent];"
sqlcmd -S <server>\<instance> -d PGTPL_AutomationAgent -Q "ALTER DATABASE [PGTPL_AutomationAgent] SET RECOVERY SIMPLE; ALTER DATABASE [PGTPL_AutomationAgent] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"

# 2. Point the agent at it (see "Secrets" below), then create the schema
dotnet ef database update -p src/NewHorizon.Automation.Infrastructure -s src/NewHorizon.Automation.Worker

# 3. Seed the per-module settings rows (idempotent)
sqlcmd -S <server>\<instance> -d PGTPL_AutomationAgent -i deploy/sql/002_SeedAutomationConfig.sql
sqlcmd -S <server>\<instance> -d PGTPL_AutomationAgent -i deploy/sql/flows/indent-to-po/003_SeedIndentToPoConfig.sql
sqlcmd -S <server>\<instance> -d PGTPL_AutomationAgent -i deploy/sql/flows/indent-to-po/004_SeedIndentPoAutomation.sql
```

`004` ensures the three `IndentPoAutomationConfig` rows exist (the migration already seeds them; the
script only matters if a row was deleted). All three ship inert — nothing converts on a schedule
until a person turns a type on from the ERP's PO Automation screen.

`READ_COMMITTED_SNAPSHOT` is on so the management/read API's queries never block the workers'
job-claiming `UPDLOCK, READPAST` updates.

Offline alternative to step 2, where `dotnet ef` cannot run on the server:
`deploy/sql/001_Schema.sql` is every migration as one idempotent script — safe to re-run, and safe
to run against a database that is already partly migrated.

## Secrets

`appsettings.json` carries **placeholders only** (`<shared-secret>`) for the automation database and
the inbound API key, and is committed. Those real values never are.

The **ERP login** is the deliberate exception: `AutomationAgent:ErpApi` holds the base URL, path,
`UserName`, `Password` and `LoginConnectionString` in clear, because the agent runs on a private
network on the client's own server and the API port changes per installation — an operator must be
able to correct all of it without a rebuild. See
[`.claude/context/erp-login-authentication.md`](../.claude/context/erp-login-authentication.md).

Local development — per-developer secret store, outside the repository:

```powershell
cd src/NewHorizon.Automation.Worker
dotnet user-secrets set "AutomationAgent:Database:ConnectionString" "Server=<server>\<instance>;Database=PGTPL_AutomationAgent;Trusted_Connection=True;TrustServerCertificate=True"
dotnet user-secrets set "AutomationAgent:Host:InboundApiKey" "<real-key>"
```

Servers: use environment variables (`AutomationAgent__Database__ConnectionString`) or a
DPAPI/machine-protected store. `AutomationDbContextFactory` reads user secrets and environment
variables with the same precedence the running service uses, so `dotnet ef` always targets the same
database the agent does.

### Serving the ERP frontend (PO Automation screen)

The ERP frontend (WebApp2) calls the agent's management API straight from the browser, carrying the
same bearer token the ERP issued it at login.

- **The migration must be applied.** `dotnet ef database update` (step 2 above) creates
  `IndentPoAutomationConfig`. Without it the scheduler logs a one-time warning and
  `/api/automation/po-automation` returns 500.
- **`InboundJwt:SigningKey` defaults to `sourcepro-secret-key`** — the ERP's own JWT secret, already
  hard-coded in `WebAPICore`. The screen works out of the box. A hardened deployment rotates that
  secret in both places and sets it here via env var / user-secrets:
  ```powershell
  dotnet user-secrets set "AutomationAgent:InboundJwt:SigningKey" "<rotated-erp-jwt-secret>"
  ```
  (The agent verifies the HS256 signature itself — `Microsoft.IdentityModel` 8.x rejects the ERP's
  160-bit key for HS256 through the normal `IssuerSigningKey` path; see `Program.cs`.)
- On the server, set `AutomationAgent__Host__BindToLoopbackOnly=false` (Kestrel listens on the LAN),
  `AutomationAgent__Cors__AllowedOrigins__0=<webapp2-origin>`, and **firewall port 5080 to the
  ERP/web tier only** — off-loopback binding removes the outer boundary the API key alone is not
  meant to carry.

### SQL login

Use a **dedicated least-privilege SQL login** for the agent — `db_datareader`, `db_datawriter` and
`EXECUTE` on the automation database, and nothing on the ERP database. Do not ship `sa`, and do not
ship a blank password; either would give the agent's process full control of every database on the
instance, including the ERP's.

Creating the database is not the same as granting access to it. A login with no user in
`PGTPL_AutomationAgent` — including a Windows login that reaches the instance only through
`BUILTIN\Users` — gets SQL error 4060, *"Cannot open database … requested by the login"*. Map it
once, as an administrator:

```sql
USE [PGTPL_AutomationAgent];
CREATE USER [DOMAIN\user] FOR LOGIN [DOMAIN\user];   -- CREATE LOGIN first if the server has none
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\user];
ALTER ROLE db_datawriter ADD MEMBER [DOMAIN\user];
GRANT EXECUTE TO [DOMAIN\user];
```

The agent probes the database once at startup. If it cannot be opened, the cycle timer, the job
dispatcher and the orphan sweep do not start — they would otherwise retry and log a stack trace
every tick, because EF counts error 4060 among the transient ones and it is not — and the reason is
logged as a single error. ERP sign-in and the indent-to-PO endpoints are unaffected.

## Verifying an installation

```powershell
curl http://localhost:5080/api/automation/health
```

Expect `checks.database = "Healthy"`. `checks.erpApi` stays `Unhealthy` until `ErpApi:BaseUrl` and
the service credentials point at a reachable ERP.

## Scripts

| Script | Does |
|---|---|
| `install.ps1` | Publishes, applies the schema, seeds config, creates the service with restart-on-failure. Leaves it stopped so secrets can be set first — `-ShowSecretHelp` prints the variables. |
| `update.ps1` | Stop → deploy → migrate → start, keeping the previous build at `<InstallPath>.backup`. |
| `uninstall.ps1` | Stops and removes the service. Never touches the database. |

## Update sequence

Stop the service → deploy binaries → migrate → start; `update.ps1` does exactly this. Stopping
first lets in-flight jobs reach a checkpoint boundary. Anything still `Running` when the process
dies is re-queued by the orphan sweep on the next start and resumes at its first operation that is
not `Completed`, so no ERP document is ever created twice.

## What the agent does once started

A timer starts one cycle per `ReconcileIntervalMinutes` (from `AutomationConfig`, not the file),
subject to the licence, the enable flags and the working-hours window. A unique filtered index
permits only one live cycle at a time, so a slow cycle simply causes the next tick to do nothing.
`POST /api/automation/run-now` starts one immediately, ignoring the working-hours window but not
the one-live-cycle rule.

Every management endpoint requires the `X-Automation-Api-Key` header. `/api/automation/health` does
not — a service monitor must reach it, and it discloses no job data.
