# Claude Code setup for this repository

Development on this repo happens mostly through Claude Code (Opus) in PowerShell, from several
machines. Everything needed to reproduce that setup is committed, so after a `git pull` you can
open Claude Code in the repo root and start from a prompt.

## What is committed

| Path | Purpose |
|---|---|
| `../CLAUDE.md` | Project instructions — loaded automatically every session |
| `settings.json` | Shared settings: model, enabled plugins, permission allowlist |
| `context/` | Confirmed client/product decisions not derivable from code or design doc |
| `../.mcp.json` | Project-scoped MCP servers (Playwright, Chrome DevTools) |
| `../Plan/` | The architecture design doc |

## What is **not** committed (and must not be)

- `settings.local.json` — per-machine permissions and overrides (git-ignored)
- Anything under `~/.claude/` — that directory holds credentials, session history and machine state
- Real connection strings, ERP client secrets, inbound API keys. `appsettings.json` ships
  placeholders (`<store-protected>`, `<shared-secret>`); real values go in a DPAPI-protected store
  on the target server.

## First run on a new machine

```powershell
git clone https://github.com/ajaydhandare/AutomationAgentNH.git
cd AutomationAgentNH
dotnet build NewHorizon.AutomationAgent.slnx
claude            # then approve the project MCP servers when prompted
```

MCP servers in `.mcp.json` run via `npx`, so Node.js must be on PATH; the packages download on
first use. Verify with `claude mcp list` — both should report **Connected**. The plugins named in
`settings.json` come from the official marketplace and install on first launch.

## Conventions for working with Claude here

- Prefer `.slnx` (`NewHorizon.AutomationAgent.slnx`) in build commands — it is the solution file
  this repo uses.
- When a client decision is confirmed, add it to `context/` in the same commit as the code it
  drives, so the next machine picks it up. Do not leave it in a local Claude memory.
- §18 of the design doc lists open questions. Don't invent answers — flag them.
