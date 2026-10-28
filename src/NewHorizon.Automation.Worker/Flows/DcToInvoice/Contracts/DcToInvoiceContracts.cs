using System.Collections.Generic;
using System.Linq;
using NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Contracts;

/// <summary>
/// Request body for creating a Sales Invoice from an existing Delivery Challan.
/// </summary>
public sealed record CreateInvoiceFromDcRequest(
    long? DcId = null,
    string? DcNo = null,
    string? DcNumber = null,
    string? CustomerCode = null,
    bool DryRun = false)
{
    public string? ResolvedDcNo => !string.IsNullOrWhiteSpace(DcNo) ? DcNo : DcNumber;

    public bool HasAnyIdentifier =>
        (DcId.HasValue && DcId.Value > 0) ||
        !string.IsNullOrWhiteSpace(ResolvedDcNo);
}

/// <summary>
/// Response for GET /api/v1/sales/salesinvoice/PendingDeliveryChallans.
/// </summary>
public sealed record PendingDeliveryChallansResponse(
    bool Success,
    string? Message,
    IReadOnlyList<PendingDcSummary>? Data,
    string? ErrorMessage);

/// <summary>
/// Successful response for individual invoice creation.
/// </summary>
public sealed record CreateInvoiceFromDcResponse(
    bool Success,
    string? Message,
    CreateInvoiceFromDcData? Data,
    IReadOnlyList<InvoiceValidationErrorResponse>? Errors,
    string? ErrorMessage);

public sealed record CreateInvoiceFromDcData(
    string InvoiceNumber,
    long InvoiceId,
    long DcId,
    string? DcNo,
    string? CustomerCode,
    int ItemCount,
    decimal TotalInvoiceQty,
    decimal TotalInvoiceAmount,
    string Status = "Created",
    bool Authorized = false,
    string? RateStructureCode = null,
    bool LineLevelRateStructureApplied = false);

public sealed record InvoiceValidationErrorResponse(string Field, string Message);

/// <summary>
/// Request body for creating Sales Invoices from Delivery Challans (all, multiple, or single).
/// </summary>
public sealed record BatchInvoiceCreationRequestDto(
    string? CustomerCode = null,
    string? CompanyCode = null,
    int? SiteId = null,
    string? SiteCode = null,
    string? DcYear = null,
    long? DcId = null,
    IReadOnlyList<long>? DcIds = null,
    IReadOnlyList<string>? DcNumbers = null)
{
    public BatchInvoiceCreationRequest ToRequest()
    {
        var resolvedDcNumbers = DcNumbers?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedDcIds = DcIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<long>();

        if (DcId.HasValue && DcId.Value > 0 && !resolvedDcIds.Contains(DcId.Value))
        {
            resolvedDcIds.Add(DcId.Value);
        }

        return new BatchInvoiceCreationRequest(
            CustomerCode: CustomerCode,
            SiteId: SiteId,
            SiteCode: SiteCode,
            DcYear: DcYear,
            DcIds: resolvedDcIds.Count > 0 ? resolvedDcIds : null,
            DcNumbers: resolvedDcNumbers != null && resolvedDcNumbers.Count > 0 ? resolvedDcNumbers : null);
    }
}

/// <summary>
/// Master summary response returned by POST /api/v1/sales/salesinvoice/CreateInvoicesFromDeliveryChallans.
/// </summary>
public sealed record BatchInvoiceCreationResponse(
    bool Success,
    string Message,
    BatchInvoiceCreationData? Data,
    string? ErrorMessage = null);
