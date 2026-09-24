-- Seeds the AutomationConfig row for the indent -> purchase order workflow.
--
-- 002 seeds the six modules that existed when it was written; this workflow arrived afterwards and
-- had no row, so GET /api/automation/config/IndentToPurchaseOrder was answering with
-- AutomationConfig.CreateDefault's values and a null UpdatedBy. Nothing was broken by that - the
-- fallback is deliberate - but an administrator could not see or change the settings, which is the
-- whole point of the table.
--
-- The module name must match WorkflowNames.IndentToPurchaseOrder exactly: that constant is what
-- ProcessJobService.StartRunAsync looks the row up by.
--
-- Which columns this workflow actually reads today:
--   Mode                     - captured once per run and stamped on every job it creates.
--   everything else          - stored and shown, but inert here. There is no timer and no
--                              dispatcher behind indent -> PO, so PollIntervalSeconds,
--                              ReconcileIntervalMinutes and ParallelWorkers change nothing; and
--                              EnableAgent, EnableModule and IsLicensed are honoured by
--                              CycleEnqueueService for the AutoShop cycle but are not yet checked
--                              on this path. Do not tune them here expecting an effect.
--
-- The values below are exactly AutomationConfig.CreateDefault's, so seeding changes no behaviour:
-- it only makes the current behaviour visible and editable.
--
-- Idempotent: safe to re-run. An existing row is left exactly as the administrator set it.
-- Run against the AUTOMATION database (e.g. PGTPL_AutomationAgent), never the ERP database.

SET NOCOUNT ON;

DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

INSERT INTO [AutomationConfig] (
    [Id], [Module], [EnableAgent], [EnableModule], [Mode],
    [PollIntervalSeconds], [ReconcileIntervalMinutes],
    [WorkingHoursStart], [WorkingHoursEnd],
    [RetryCount], [ParallelWorkers], [LoggingLevel], [IsLicensed],
    [PayloadRetentionDays], [LogRetentionDays], [ErrorRetentionDays],
    [UpdatedAtUtc], [UpdatedBy])
SELECT
    NEWID(), 'IndentToPurchaseOrder', 1, 1, 'Full',
    30, 5,
    NULL, NULL,            -- no working-hours window: nothing on this path consults one
    3, 4, 'Information', 1,
    90, 90, 365,
    @now, 'seed-003'
WHERE NOT EXISTS (
    SELECT 1 FROM [AutomationConfig] c WHERE c.[Module] = 'IndentToPurchaseOrder');

SELECT [Module], [Mode], [EnableAgent], [EnableModule], [IsLicensed], [UpdatedBy]
FROM [AutomationConfig]
ORDER BY [Module];
