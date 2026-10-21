namespace NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

/// <summary>
/// Service that discovers open authorised quotations for a customer and converts them to Sales Order Receipts.
/// </summary>
public interface IQuotationToSoService
{
    /// <summary>
    /// Discovers all open, authorised quotations for the given customer and site.
    /// </summary>
    Task<IReadOnlyList<OpenQuotationSummary>> GetOpenAuthorisedQuotationsAsync(
        string customerCode,
        string siteCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches details for a single quotation, constructs the Sales Order payload, and creates the Sales Order Receipt in the ERP.
    /// </summary>
    Task<QuotationConversionItemResult> ConvertSingleQuotationAsync(
        OpenQuotationSummary quotation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Orchestrates the entire conversion for a customer: discovers all open authorised quotations and converts each one.
    /// </summary>
    Task<QuotationToSoSweepResult> ConvertQuotationsForCustomerAsync(
        string customerCode,
        string siteCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Converts a specific quotation (identified by its quotation number) to a Sales Order Receipt.
    /// Optional customerCode and siteCode can be provided if already known.
    /// </summary>
    Task<QuotationConversionItemResult> ConvertQuotationByNumberAsync(
        string quotationNumber,
        string? customerCode,
        string? siteCode,
        CancellationToken cancellationToken);
}
