using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

/// <summary>
/// Request payload for converting open authorised quotations to Sales Order Receipts.
/// </summary>
public sealed record ConvertQuotationsRequest
{
    /// <summary>Specific quotation number to convert (e.g. "26-QT-000001", "QTN/26/000001", or "000001").</summary>
    public string? QuotationNumber { get; init; }

    /// <summary>Customer code whose authorised quotations will be converted (if converting by customer).</summary>
    public string? CustomerCode { get; init; }

    /// <summary>Site/location code where the quotations reside (e.g. "AHM", "HO").</summary>
    public string? SiteCode { get; init; }
}

/// <summary>
/// Response payload for the Quotation to Sales Order automation API.
/// </summary>
public sealed record ConvertQuotationsResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? SalesOrderNumber { get; init; }
    public QuotationConversionItemResult? Item { get; init; }
    public QuotationToSoSweepResult? Result { get; init; }
}
