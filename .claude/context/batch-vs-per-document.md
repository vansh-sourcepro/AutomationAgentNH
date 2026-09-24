# The unit of work is a batch cycle, not a document

**Confirmed by the user on 2026-07-27: batch, site-scoped, timer-driven.** Not per-document.

The agent runs a repeating **cycle**:

1. Find all OAF where SJO is pending → create SJO (OAF → SJO). Skip if none found.
2. *(ERP does SJO → CBOM on its own — explicitly not the agent's concern.)*
3. Fetch the Site ID collection from one API, then **loop over every Site ID**: GET the site's SJOs
   (only those whose BOM already exists), sort by delivery date ascending, POST the sequence.
   Skip a site with no data.
4. AutoShop: one GET and one POST. Cycle complete.

## Consequences for the built solution

- The per-document idempotency key (DocumentType + DocumentId + WorkflowType) does not fit. A cycle
  job needs a key that prevents two **overlapping cycles**.
- The design doc's §6 trigger model is largely obsolete for this workflow: no ERP push on Sales
  Order save, because automation deliberately starts *after* OAF creation, which is manually
  authorised. The trigger is a timer.
- Site ID is needed; Company is not, even though the client's ERP has one. See
  [single-tenant-deployment.md](single-tenant-deployment.md).

**Why:** the unit of work decides the idempotency key, the schema and every workflow definition.

**How to apply:** model the cycle as the job; treat each Site ID as a unit within it.
