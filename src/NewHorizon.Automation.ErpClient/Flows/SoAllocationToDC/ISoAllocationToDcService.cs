namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

/// <summary>
/// Automation bridge: reads an existing Sales Allocation from the ERP and creates the
/// corresponding Delivery Challan using the same data, validations, mappings and calculations
/// that the manual DC entry screen uses. Every effect is an ERP application API call — the agent
/// never writes DC data itself and holds no business rules beyond orchestrating the ERP's own
/// screen sequence.
/// </summary>
public interface ISoAllocationToDcService
{
    /// <summary>
    /// Finds the allocation for <paramref name="oafId"/>, validates it, resolves every mandatory
    /// DC field from the same data the ERP screen uses, and calls the ERP's own
    /// <c>createDCEntry</c> endpoint. Returns the generated DC number and DC auto-id on success.
    /// </summary>
    /// <exception cref="DcValidationException">
    /// A required field could not be resolved, or a business-rule check failed. Deterministic —
    /// retrying would produce the same answer.
    /// </exception>
    /// <exception cref="Application.Erp.ErpTransientException">
    /// The ERP could not be reached or answered unhealthily. Worth another attempt.
    /// </exception>
    Task<DcCreationResult> CreateDeliveryChallanFromAllocationAsync(
        int? oafId,
        string? oafNo = null,
        string? intOrdNo = null,
        string? customerCode = null,
        CancellationToken cancellationToken = default);

    Task<DcCreationResult> CreateDeliveryChallanFromAllocationAsync(
        int oafId,
        CancellationToken cancellationToken = default)
        => CreateDeliveryChallanFromAllocationAsync(oafId, null, null, null, cancellationToken);

    /// <summary>
    /// Scans for OAFs under the customer and inspects their allocation and shipment status,
    /// returning which ones are ready to be converted into a Delivery Challan.
    /// </summary>
    Task<IReadOnlyList<PendingOafSummary>> GetPendingAllocationsAsync(
        string? customerCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Master automation API: finds ALL existing Sales Order Allocations, processes them
    /// one-by-one using the identical sequential API flow as the manual DC screen, and
    /// automatically creates the corresponding Delivery Challan for every valid allocated record.
    /// </summary>
    Task<BatchDcCreationResult> CreateDeliveryChallansFromAllocationsAsync(
        BatchDcCreationRequest? request = null,
        CancellationToken cancellationToken = default);
}
