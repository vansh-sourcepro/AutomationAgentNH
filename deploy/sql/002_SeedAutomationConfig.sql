-- Seeds one AutomationConfig row per module.
--
-- Not strictly required: AutomationConfigRepository.GetOrDefaultAsync falls back to
-- AutomationConfig.CreateDefault for a module with no row. Seeding is still worth doing on a new
-- installation so the settings UI has rows to show and an administrator can see - and change -
-- what the agent is actually going to do, rather than relying on invisible defaults.
--
-- Idempotent: safe to re-run. Existing rows are left exactly as the administrator set them.
-- Run against the AUTOMATION database (e.g. PGTPL_AutomationAgent), never the ERP database.

SET NOCOUNT ON;

DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

;WITH Modules(Module) AS (
    SELECT 'SJO'
    UNION ALL SELECT 'OAF'
    UNION ALL SELECT 'MIL'
    UNION ALL SELECT 'CBOM'
    UNION ALL SELECT 'AutoShop'
    -- The agent's real unit of work: the repeating OAF -> SJO -> sequencing -> AutoShop cycle.
    UNION ALL SELECT 'AutoShopCycle'
)
INSERT INTO [AutomationConfig] (
    [Id], [Module], [EnableAgent], [EnableModule], [Mode],
    [PollIntervalSeconds], [ReconcileIntervalMinutes],
    [WorkingHoursStart], [WorkingHoursEnd],
    [RetryCount], [ParallelWorkers], [LoggingLevel], [IsLicensed],
    [PayloadRetentionDays], [LogRetentionDays], [ErrorRetentionDays],
    [UpdatedAtUtc], [UpdatedBy])
SELECT
    NEWID(), m.Module, 1, 1, 'Full',
    30, 5,
    NULL, NULL,            -- no working-hours window: the agent runs around the clock until told otherwise
    3, 4, 'Information', 1,
    90, 90, 365,
    @now, 'seed-002'
FROM Modules m
WHERE NOT EXISTS (SELECT 1 FROM [AutomationConfig] c WHERE c.[Module] = m.Module);

SELECT [Module], [Mode], [EnableAgent], [EnableModule], [IsLicensed], [UpdatedBy]
FROM [AutomationConfig]
ORDER BY [Module];
