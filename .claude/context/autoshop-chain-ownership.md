# AutoShop chain ownership

**Confirmed by the client's team on 2026-07-27** (full request/response specs due 2026-07-28).

| Step | Transition | Performed by | Agent's part |
|---|---|---|---|
| 1 | SO → OAF | ERP, only after **manual user authorisation** | **None** — automation deliberately starts after this |
| 2 | OAF → SJO | **Agent** | Create. This is the agent's starting point |
| 3 | SJO → CBOM | ERP | **None** — explicitly not the agent's concern |
| 4 | SJO sequencing → AutoShop | **Agent** | Per-site GET, sort, POST |

Because the agent never touches steps 1 and 3, it does **not** need to verify or wait on them. The
site GET returns only SJOs whose BOM already exists, so an SJO created in one cycle is picked up by
a later cycle once the ERP has built its BOM — naturally eventually consistent, no waiting.

## AutoShop endpoints

Base `http://192.168.0.189:8011`. Reached in the ERP UI via
Sales → Shop order maintenance → AutoShop.

- `GET /api/v1/admin/location/list` — Site ID collection (**ERP team must modify this**)
- `GET /api/v1/planning/autoshopsjosequence/GetSJODetails/{SiteID}/S` — that site's SJOs
- `POST /api/v1/planning/autoshopsjosequence/GetSJODetails/{SiteID}/S` — submit the sequence
- Rows sorted by **delivery date ascending** before submission
- AutoShop's own Get/Post were still "upcoming" as of 2026-07-27
- Plus a new API to find all OAF where SJO is pending (the cycle's entry point)

**Why:** the ownership split decides which steps the agent may act on; acting on an ERP-owned step
would duplicate documents the ERP already created.

**How to apply:** the agent's scope is steps 2 and 4 only. See
[batch-vs-per-document.md](batch-vs-per-document.md) for the cycle shape.
