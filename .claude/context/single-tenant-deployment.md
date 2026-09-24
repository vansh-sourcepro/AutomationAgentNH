# Single-tenant deployment — no Company dimension

**Confirmed by the user on 2026-07-27:** every client gets their **own** on-premise Windows server,
with its own agent installation, its own automation database and its own ERP. The user deploys each
one personally.

So there is no company/client/tenant dimension anywhere in the solution. `Company` was removed from
the domain, the ERP contracts, the database and the queries; `AutomationConfig` is keyed by Module
alone.

This is **not** the same as Site ID, which is a real dimension within one client's ERP (several
sites/plants under one installation) and still needs modelling — see
[batch-vs-per-document.md](batch-vs-per-document.md).

**Why:** re-introducing a tenant column would add a key nobody varies and would wrongly imply one
installation serves several clients.

**How to apply:** never add Company/Tenant/ClientId to entities, APIs or config. If something must
vary per site, use Site ID.
