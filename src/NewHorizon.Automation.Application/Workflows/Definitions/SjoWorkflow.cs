using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// Manufacturing flow, per §8 of the design doc:
/// De-Allocation → Allocation → Work Order → Purchase Requisition → Labor PR.
/// </summary>
public static class SjoWorkflow
{
    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.Sjo)
            .Stage(WorkflowNames.Sjo, stage => stage
                .Execute("DeAllocation", DeAllocateAsync)
                .Execute("Allocation", AllocateAsync)
                .Execute(
                    "WorkOrderGeneration",
                    CreateWorkOrderAsync,
                    // MRP rule: no work order for a manufacturing item whose children were not
                    // allocated. Not an error — the operation is simply skipped.
                    precondition: ErpOperations.ChildrenAreAllocatedAsync)
                .Execute(
                    "PurchaseRequisition",
                    CreatePurchaseRequisitionAsync,
                    requiresApprovalInPartial: true)
                .Execute(
                    "LaborPR",
                    CreateLaborRequisitionAsync,
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

    private static Task<OperationResult> CreateWorkOrderAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        ErpOperations.CreateOnShortageAsync(
            context,
            erpClient,
            DocumentKinds.WorkOrder,
            (request, ct) => erpClient.CreateWorkOrderAsync(new WorkOrderRequest(request), ct),
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
                // The requisition carries the source document reference so the ERP can link it back.
                new PurchaseRequisitionRequest(request, request.DocumentId),
                ct),
            cancellationToken);

    private static async Task<OperationResult> CreateLaborRequisitionAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        // Labour requisitions attach to the work order this run produced. No work order means the
        // step above was skipped, so there is nothing to raise labour against.
        var workOrderRef = context.DocumentRefFor(WorkflowNames.Sjo, "WorkOrderGeneration");

        if (workOrderRef is null)
        {
            return OperationResult.Skip("No work order was created, so no labour requisition is needed.");
        }

        return await ErpOperations.CreateOnShortageAsync(
            context,
            erpClient,
            DocumentKinds.LaborRequisition,
            (request, ct) => erpClient.CreateLaborRequisitionAsync(
                new LaborRequisitionRequest(request, workOrderRef, request.DocumentId),
                ct),
            cancellationToken);
    }
}
