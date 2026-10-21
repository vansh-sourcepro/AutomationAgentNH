namespace NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

/// <summary>
/// Summary of an open authorised quotation returned from ERP discovery.
/// </summary>
public sealed record OpenQuotationSummary
{
    public required int QuotationId { get; init; }
    public required string QuotationNumber { get; init; }
    public required string QtnYear { get; init; }
    public required string QtnGroup { get; init; }
    public required string QtnNum { get; init; }
    public required string QuotationSiteCode { get; init; }
    public required int QuotationSiteId { get; init; }
    public required string CustomerCode { get; init; }
    public string? CustomerName { get; init; }
    public DateTime? QuotationDate { get; init; }
    public int RevisionNo { get; init; }
    public DateTime? RevisionDate { get; init; }
    public string? QuotationCurrency { get; init; }
    public string? AgentCode { get; init; }
    public string? InquiryNo { get; init; }
    public DateTime? InquiryDate { get; init; }
    public string? SalesmanCode { get; init; }
    public string? SalesmanName { get; init; }
    public string? ConsultantCode { get; init; }
    public string? ConsultantName { get; init; }
    public string? GstNo { get; init; }
}

/// <summary>
/// Result of converting an individual Quotation to a Sales Order Receipt.
/// </summary>
public sealed record QuotationConversionItemResult
{
    public required int QuotationId { get; init; }
    public required string QuotationNumber { get; init; }
    public required bool Success { get; init; }
    public string? SalesOrderNumber { get; init; }
    public int? SalesOrderId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Overall summary of processing all authorised quotations for a given Customer and Site.
/// </summary>
public sealed record QuotationToSoSweepResult
{
    public required string CustomerCode { get; init; }
    public required string SiteCode { get; init; }
    public required int TotalFound { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<QuotationConversionItemResult> Details { get; init; }
}
