# Shared project context

These notes are decisions confirmed with the user or the client that are **not derivable from the
code, the git history, or the design doc**. They started as one developer's local Claude memory;
they live here so every machine and every developer gets them after a `git pull`.

`CLAUDE.md` instructs Claude to read this folder at the start of a session.

| Note | What it settles |
|---|---|
| [autoshop-chain-ownership.md](autoshop-chain-ownership.md) | Who performs each step of SO → OAF → SJO → CBOM → AutoShop, and the confirmed AutoShop endpoints |
| [batch-vs-per-document.md](batch-vs-per-document.md) | The agent is a site-scoped, timer-driven **batch cycle** — not a per-document workflow |
| [single-tenant-deployment.md](single-tenant-deployment.md) | One server per client, so no Company/Tenant dimension anywhere |

## Adding a note

One decision per file. Keep the header, say when and by whom it was confirmed, and end with
**Why** it matters and **How to apply** it. If a note is superseded, edit it rather than adding a
second one — a stale note is worse than no note. Dates are absolute, never "last week".
