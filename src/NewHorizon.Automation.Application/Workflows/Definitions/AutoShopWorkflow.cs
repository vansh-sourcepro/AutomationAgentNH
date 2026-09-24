using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// AutoShop — the full sales-order run that chains the earlier stages.
/// </summary>
/// <remarks>
/// SCAFFOLD — design doc §18.4 is still open: the exact operation list has not been confirmed.
/// The stage order below reflects the sequence you described (SO → OAF → SJO → CBOM → AutoShop),
/// with the two ERP-owned transitions verified rather than performed. The final AutoShop stage
/// holds one placeholder operation that is intentionally inert until the ERP team confirms what
/// it should call — it skips rather than guessing, so a job runs end to end without inventing an
/// ERP document.
/// </remarks>
public static class AutoShopWorkflow
{
    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.AutoShop)
            // The ERP performs SO → OAF itself when its flag is on; verify, and fall back to doing
            // it here when the flag is off.
            .Stage(WorkflowNames.Oaf, stage => stage
                .VerifyThenExecute(
                    "OafLinkAttachment",
                    ErpTransitions.SalesOrderToOaf,
                    AttachOafLinkAsync))

            .Stage(WorkflowNames.Sjo, stage => stage
                .Execute("DeAllocation", DeAllocateAsync)
                .Execute("Allocation", AllocateAsync)
                .Execute(
                    "WorkOrderGeneration",
                    CreateWorkOrderAsync,
                    precondition: ErpOperations.ChildrenAreAllocatedAsync)
                .Execute(
                    "PurchaseRequisition",
                    CreatePurchaseRequisitionAsync,
                    requiresApprovalInPartial: true))

            // SJO → CBOM is entirely the ERP's, so it is only ever confirmed.
            .Stage(WorkflowNames.Cbom, stage => stage
                .VerifyOnly("CbomGeneration", ErpTransitions.SjoToCbom))

            .Stage(WorkflowNames.AutoShop, stage => stage
                // TODO(§18.4): replace with the confirmed AutoShop operations.
                .Execute("AutoShopRelease", NotYetSpecifiedAsync))
            .Build();

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
                new PurchaseRequisitionRequest(request, request.DocumentId),
                ct),
            cancellationToken);

    /// <summary>
    /// Placeholder for an operation whose ERP call is not yet confirmed. It skips rather than
    /// calling anything: an inert step is recoverable, an invented ERP document is not.
    /// </summary>
    private static Task<OperationResult> NotYetSpecifiedAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Skip(
            "AutoShop release is not yet configured; awaiting the confirmed ERP operation list (design doc §18.4)."));
}
