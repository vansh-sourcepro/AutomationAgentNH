using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

/// <summary>
/// Automation service: reads an eligible Delivery Challan from the ERP and creates the
/// corresponding Sales Invoice using the same data, validations, mappings and calculations
/// that the manual Sales Invoice entry screen uses.
/// </summary>
public interface IDcToInvoiceService
{
    /// <summary>
    /// Finds the Delivery Challan for <paramref name="dcId"/> (or <paramref name="dcNo"/>), validates it,
    /// determines Rate Structure handling according to MSCENABLELINELEVELRTSTR, resolves mandatory fields
    /// using first/top valid dropdown options, and calls the ERP's own <c>sales/salesinvoice/create</c> endpoint.
    /// </summary>
    Task<InvoiceCreationResult> CreateInvoiceFromDeliveryChallanAsync(
        long? dcId = null,
        string? dcNo = null,
        string? customerCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans for Delivery Challans under the customer/site and inspects their pending invoice status,
    /// returning which ones are ready to be converted into a Sales Invoice.
    /// </summary>
    Task<IReadOnlyList<PendingDcSummary>> GetPendingDeliveryChallansAsync(
        string? customerCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Master batch automation API: processes eligible Delivery Challans sequentially using
    /// the identical sequential API flow as the manual Invoice screen, and creates Sales Invoices.
    /// </summary>
    Task<BatchInvoiceCreationResult> CreateInvoicesFromDeliveryChallansAsync(
        BatchInvoiceCreationRequest? request = null,
        CancellationToken cancellationToken = default);
}
