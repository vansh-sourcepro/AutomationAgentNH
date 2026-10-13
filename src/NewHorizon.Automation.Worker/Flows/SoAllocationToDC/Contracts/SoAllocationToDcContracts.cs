using NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Contracts;

/// <summary>
/// Request body for creating a Delivery Challan from an existing Sales Allocation.
/// Callers can identify the allocation by OAF ID, OAF Number, and/or Internal Order (SO) Number.
/// </summary>
public sealed record CreateDcFromAllocationRequest(
    int? OafId = null,
    string? OafNo = null,
    string? OafNumber = null,
    string? IntOrdNo = null,
    string? IntOrderNumber = null,
    string? SoNo = null,
    string? CustomerCode = null)
{
    public string? ResolvedOafNo => !string.IsNullOrWhiteSpace(OafNo) ? OafNo : OafNumber;
    public string? ResolvedIntOrdNo => !string.IsNullOrWhiteSpace(IntOrdNo) ? IntOrdNo : (!string.IsNullOrWhiteSpace(IntOrderNumber) ? IntOrderNumber : SoNo);

    public bool HasAnyIdentifier =>
        (OafId.HasValue && OafId.Value > 0) ||
        !string.IsNullOrWhiteSpace(ResolvedOafNo) ||
        !string.IsNullOrWhiteSpace(ResolvedIntOrdNo);
}

/// <summary>
/// Response for GET /api/v1/sales/SalesAllocationDeAllocation/PendingAllocations.
/// </summary>
public sealed record PendingAllocationsResponse(
    bool Success,
    string? Message,
    IReadOnlyList<PendingOafSummary>? Data,
    string? ErrorMessage);

/// <summary>
/// Successful response: DC number, auto-id and the counts that confirm
/// every allocated item and quantity was transferred exactly.
/// </summary>
public sealed record CreateDcFromAllocationResponse(
    bool Success,
    string? Message,
    CreateDcFromAllocationData? Data,
    IReadOnlyList<DcValidationErrorResponse>? Errors,
    string? ErrorMessage);

public sealed record CreateDcFromAllocationData(
    string DcNumber,
    long DcId,
    int OafId,
    string? OafNo,
    string? IntOrdNo,
    int AllocatedItemCount,
    int DcItemCount,
    decimal TotalAllocatedQty,
    decimal TotalDcQty,
    string Status = "Open",
    bool Authorized = false);
public sealed record DcValidationErrorResponse(string Field, string Message);

/// <summary>
/// Request body for the master batch API creating Delivery Challans from all eligible allocations.
/// All fields are optional: if empty, all allocations for all eligible customers are processed.
/// </summary>
public sealed record BatchDcCreationRequestDto(
    string? CustomerCode = null,
    int? SiteId = null,
    string? SiteCode = null,
    string? OafYear = null,
    IReadOnlyList<int>? AllocationIds = null,
    IReadOnlyList<int>? OafIds = null,
    IReadOnlyList<string>? OafNumbers = null,
    bool DryRun = false)
{
    public BatchDcCreationRequest ToRequest() => new(
        CustomerCode: CustomerCode,
        SiteId: SiteId,
        SiteCode: SiteCode,
        OafYear: OafYear,
        AllocationIds: AllocationIds,
        OafIds: OafIds,
        OafNumbers: OafNumbers,
        DryRun: DryRun);
}

/// <summary>
/// Master summary response returned by POST /api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallansFromAllocations.
/// </summary>
public sealed record BatchDcCreationResponse(
    bool Success,
    string Message,
    BatchDcCreationData? Data,
    string? ErrorMessage = null);
