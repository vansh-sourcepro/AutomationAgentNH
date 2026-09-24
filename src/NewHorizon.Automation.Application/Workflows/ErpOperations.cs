using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// Building blocks for operation bodies. Every creating helper here queries before it creates,
/// which is the second layer of idempotency: the unique index stops duplicate <em>jobs</em>, this
/// stops duplicate <em>ERP documents</em> when one job re-runs an operation after a crash or retry.
/// </summary>
public static class ErpOperations
{
    /// <summary>
    /// Adopts the document a previous attempt already created, or creates it. The
    /// <c>AlreadyExisted</c> flag is preserved in the response payload so an operator can see, in
    /// the timeline, that a re-run reused a document rather than making a second one.
    /// </summary>
    public static async Task<OperationResult> CreateIfAbsentAsync(
        OperationContext context,
        IErpClient erpClient,
        string documentKind,
        Func<ErpOperationRequest, CancellationToken, Task<ErpDocumentResult>> create,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(erpClient);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);

        var request = context.ToErpRequest();

        var existing = await erpClient.FindExistingDocumentAsync(request, documentKind, cancellationToken);
        if (existing.Exists)
        {
            return OperationResult.Success(
                existing.ErpDocumentRef,
                responsePayload: $$"""{"adopted":true,"documentKind":"{{documentKind}}"}""");
        }

        var created = await create(request, cancellationToken);

        return OperationResult.Success(
            created.ErpDocumentRef,
            created.RequestPayload,
            created.ResponsePayload);
    }

    /// <summary>
    /// Runs a creating operation only when the ERP reports a net shortage. No shortage is a
    /// skip, not a failure — there is genuinely nothing to procure.
    /// </summary>
    public static async Task<OperationResult> CreateOnShortageAsync(
        OperationContext context,
        IErpClient erpClient,
        string documentKind,
        Func<ErpOperationRequest, CancellationToken, Task<ErpDocumentResult>> create,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(erpClient);

        var request = context.ToErpRequest();

        // Check for an existing document first: a resumed job must not re-evaluate shortage
        // against stock its own earlier attempt already committed.
        var existing = await erpClient.FindExistingDocumentAsync(request, documentKind, cancellationToken);
        if (existing.Exists)
        {
            return OperationResult.Success(
                existing.ErpDocumentRef,
                responsePayload: $$"""{"adopted":true,"documentKind":"{{documentKind}}"}""");
        }

        var shortage = await erpClient.GetNetShortageAsync(request, cancellationToken);
        if (!shortage.HasShortage)
        {
            return OperationResult.Skip(shortage.Detail ?? "No net shortage; nothing to create.");
        }

        var created = await create(request, cancellationToken);

        return OperationResult.Success(
            created.ErpDocumentRef,
            created.RequestPayload,
            created.ResponsePayload);
    }

    /// <summary>
    /// Precondition: work-order generation is skipped unless the children under a manufacturing
    /// item were allocated, per the MRP rule.
    /// </summary>
    public static async Task<bool> ChildrenAreAllocatedAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(erpClient);

        var status = await erpClient.GetAllocationStatusAsync(context.ToErpRequest(), cancellationToken);

        return status.ChildrenAllocated;
    }
}
