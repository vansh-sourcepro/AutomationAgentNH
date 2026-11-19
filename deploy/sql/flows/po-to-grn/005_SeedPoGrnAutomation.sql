-- Ensures the single PO → GRN settings row exists in PoGrnAutomationConfig.
--
-- The AddPoToGrn migration already seeds it (HasData), so on a normally migrated database this
-- script inserts nothing. It is here, like 004, to make the row visible and to restore it if it was
-- deleted by hand.
--
-- The row ships INERT: IsActive = 0, RunMode = 'Api' (PO-based, no schedule), no invoice number.
-- Nothing is received until a person sets the invoice number and turns it on:
--   PUT /api/automation/grn-automation  {"invoiceNumber": "...", "isActive": true}
--
-- The Id is fixed: PoGrnAutomationConfigRepository looks the row up by it.
--
-- Idempotent: safe to re-run. An existing row is left exactly as it was set.
-- Run against the AUTOMATION database (e.g. PGTPL_AutomationAgent), never the ERP database.

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM [PoGrnAutomationConfig] WHERE [Id] = '7A2E5D30-0000-0000-0000-000000000001')
BEGIN
    INSERT INTO [PoGrnAutomationConfig] ([Id], [IsActive], [RunMode], [ReceiptMode], [DryRun], [UpdatedAtUtc], [UpdatedBy])
    VALUES ('7A2E5D30-0000-0000-0000-000000000001', 0, 'Api', 'Complete', 0, SYSDATETIMEOFFSET(), 'seed-005');
END;

SELECT [IsActive], [RunMode], [ReceiptMode], [InvoiceNumber], [ScheduleTime], [Sites], [LastRunStatus], [UpdatedBy]
FROM [PoGrnAutomationConfig];
