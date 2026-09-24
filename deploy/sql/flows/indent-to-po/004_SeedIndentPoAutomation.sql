-- Ensures the three per-indent-type automation rows exist in IndentPoAutomationConfig.
--
-- The AddIndentPoAutomationConfig migration already seeds these rows (HasData), so on a normal
-- migrated database this script inserts nothing. It is here for the same reason 002/003 are: to
-- make the rows visible and to restore one that was deleted by hand.
--
-- All three ship INERT: RunMode = 'Disabled', IsActive = 0. Nothing converts on a schedule or an
-- API call until a person turns a type on from the PO Automation screen. This preserves the
-- long-standing invariant that starting the agent converts nothing on its own.
--
-- IndentKind must match the Domain enum names exactly ('Regular' | 'Capital' | 'Service'):
-- IndentPoAutomationConfigRepository and the scheduler look the row up by that value.
--
-- Idempotent: safe to re-run. An existing row is left exactly as the administrator set it.
-- Run against the AUTOMATION database (e.g. PGTPL_AutomationAgent), never the ERP database.

SET NOCOUNT ON;

DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

INSERT INTO [IndentPoAutomationConfig] ([Id], [IndentKind], [RunMode], [IsActive], [DryRun], [UpdatedAtUtc], [UpdatedBy])
SELECT NEWID(), k.[IndentKind], 'Disabled', 0, 0, @now, 'seed-004'
FROM (VALUES ('Regular'), ('Capital'), ('Service')) AS k([IndentKind])
WHERE NOT EXISTS (
    SELECT 1 FROM [IndentPoAutomationConfig] c WHERE c.[IndentKind] = k.[IndentKind]);

SELECT [IndentKind], [RunMode], [IsActive], [ScheduleTime], [Sites], [LastRunStatus], [UpdatedBy]
FROM [IndentPoAutomationConfig]
ORDER BY [IndentKind];
