using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// Trading flow, per §8 of the design doc:
/// De-Allocation → Allocation → OAF Link Attachment → Purchase Requisition.
/// </summary>
/// <remarks>
/// The sales order → OAF transition itself is automated inside the ERP behind a flag, so the
/// agent confirms it rather than creating it. <c>VerifyThenExecute</c> is used deliberately over
/// <c>VerifyOnly</c>: it adopts the ERP's document when the flag is on and does the work when it
/// is off, so toggling the ERP flag needs no change or redeploy here.
/// </remarks>
public static class OafWorkflow
{
    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.Oaf)
            .Stage(WorkflowNames.Oaf, stage => stage
                .Execute("DeAllocation", DeAllocateAsync)
                .Execute("Allocation", AllocateAsync)
                .VerifyThenExecute(
                    "OafLinkAttachment",
                    ErpTransitions.SalesOrderToOaf,
                    AttachOafLinkAsync)
                .Execute(
                    "PurchaseRequisition",
                    CreatePurchaseRequisitionAsync,
                    requiresApprovalInPartial: true))
            .Build();

    private static Task<OperationResult> DeAllocateAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        ErpOperations.CreateIfAbsentAsync(
            context,
            erpClient,
            DocumentKinds.DeAllocation,
            (request, ct) => erpClient.DeAllocateAsync(new DeAllocationRequest(request), ct),
            cancellationToken);

    private static Task<OperationResult> AllocateAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        ErpOperations.CreateIfAbsentAsync(
            context,
            erpClient,
            DocumentKinds.Allocation,
            (request, ct) => erpClient.AllocateAsync(new AllocationRequest(request), ct),
            cancellationToken);

    private static Task<OperationResult> AttachOafLinkAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        ErpOperations.CreateIfAbsentAsync(
            context,
            erpClient,
            DocumentKinds.OafLink,
            (request, ct) => erpClient.AttachOafLinkAsync(new OafLinkRequest(request), ct),
            cancellationToken);

    private static Task<OperationResult> CreatePurchaseRequisitionAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        ErpOperations.CreateOnShortageAsync(
            context,
            erpClient,
            DocumentKinds.PurchaseRequisition,
            (request, ct) => erpClient.CreatePurchaseRequisitionAsync(
                new PurchaseRequisitionRequest(request, request.DocumentId),
                ct),
            cancellationToken);
}
