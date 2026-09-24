using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// Bought-out plant-wise minimum inventory level, per §8 of the design doc:
/// Shortage = MIL − Free On-Hand, net of MIL pipeline documents; create only on net shortage.
/// </summary>
public static class MilWorkflow
{
    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.Mil)
            .Stage(WorkflowNames.Mil, stage => stage
                .Execute(
                    "PurchaseRequisition",
                    CreateOnMilShortageAsync,
                    requiresApprovalInPartial: true))
            .Build();

    private static async Task<OperationResult> CreateOnMilShortageAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        var request = context.ToErpRequest();

        // Query before create, as everywhere: a resumed job must adopt what a previous attempt
        // raised rather than re-computing shortage against stock it already committed.
        var existing = await erpClient.FindExistingDocumentAsync(
            request,
            DocumentKinds.PurchaseRequisition,
            cancellationToken);

        if (existing.Exists)
        {
            return OperationResult.Success(
                existing.ErpDocumentRef,
                responsePayload: """{"adopted":true,"documentKind":"purchase-requisition"}""");
        }

        // MIL uses its own shortage calculation — plant-wise minimum against free on-hand, net of
        // MIL pipeline documents — rather than the ordinary net-requirement one.
        var shortage = await erpClient.GetMilShortageAsync(new MilShortageRequest(request), cancellationToken);

        if (!shortage.HasShortage)
        {
            return OperationResult.Skip(
                shortage.Detail ?? "Free on-hand meets the minimum inventory level; nothing to procure.");
        }

        var created = await erpClient.CreatePurchaseRequisitionAsync(
            new PurchaseRequisitionRequest(request, request.DocumentId),
            cancellationToken);

        return OperationResult.Success(created.ErpDocumentRef, created.RequestPayload, created.ResponsePayload);
    }
}
