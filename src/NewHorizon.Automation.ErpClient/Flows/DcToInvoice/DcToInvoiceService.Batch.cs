using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

public sealed partial class DcToInvoiceService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingDcSummary>> GetPendingDeliveryChallansAsync(
        string? customerCode = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveCustomerCode = !string.IsNullOrWhiteSpace(customerCode)
            ? customerCode.Trim()
            : _options.CustomerCode;

        var today = DateTime.Today;
        var formattedDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var effectiveSiteId = _options.SiteId;

        var pendingDcs = await FetchPendingDcsAsync(
            effectiveCustomerCode, _options.Currency, _options.ExNonEx, formattedDate, effectiveSiteId, cancellationToken);

        var summaries = new List<PendingDcSummary>();
        foreach (var dc in pendingDcs)
        {
            var itemsSummary = new List<PendingDcItemSummary>();
            try
            {
                var (items, _) = await FetchInvoiceItemsAndTaxesAsync(
                    dc.DcRateStructureCode ?? "",
                    dc.DcId,
                    dc.CurrencyCode,
                    dc.OrderId,
                    dc.DcSite,
                    dc.OafId,
                    cancellationToken);

                foreach (var itm in items)
                {
                    itemsSummary.Add(new PendingDcItemSummary(
                        SalesItemCode: itm.SalesItemCode,
                        SalesItemName: itm.SalesItemName,
                        IntItmCode: itm.IntItmCode,
                        DcQty: itm.DcQty,
                        InvoiceQty: itm.InvoiceQty,
                        OrderRate: itm.OrdRateDis,
                        Suom: itm.Suom,
                        Iuom: itm.Iuom,
                        ConvFact: itm.ConvFact,
                        OafId: itm.OafId,
                        OafDetailLineNo: itm.OafDetailLineNo,
                        HsnCode: itm.HsnCode,
                        RateStructureCode: itm.RateStructureCode ?? dc.DcRateStructureCode));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load item summary for DC {DcId}", dc.DcId);
            }

            bool isReady = itemsSummary.Count > 0 && itemsSummary.Any(i => i.InvoiceQty > 0);
            string statusMsg = isReady
                ? "Ready for Invoicing"
                : (itemsSummary.Count == 0 ? "No pending items" : "All quantities already invoiced");

            summaries.Add(new PendingDcSummary(
                DcId: dc.DcId,
                DcNo: dc.DcNo,
                DcDate: dc.DcDate,
                DcYear: dc.DcYear,
                DcGroup: dc.DcGroup,
                DcSite: dc.DcSite,
                CustomerCode: dc.BillTo,
                CustomerName: dc.BillToName,
                ShipmentCode: dc.ShipCode,
                CurrencyCode: dc.CurrencyCode,
                RateStructureCode: dc.DcRateStructureCode,
                CreditDays: dc.CreditDays,
                CreditLimit: dc.CreditLimit,
                IsReadyForInvoice: isReady,
                StatusMessage: statusMsg,
                Items: itemsSummary));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<BatchInvoiceCreationResult> CreateInvoicesFromDeliveryChallansAsync(
        BatchInvoiceCreationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var siteId = request?.SiteId ?? _options.SiteId;
        var dryRun = request?.DryRun ?? false;

        _logger.LogInformation(
            "DcToInvoice: Master batch automation started. SiteId={SiteId}, CustomerFilter={Cust}, DryRun={DryRun}",
            siteId, request?.CustomerCode, dryRun);

        // ── Step 1: Discover candidate customers ──
        var customerCodes = new List<string>();
        if (!string.IsNullOrWhiteSpace(request?.CustomerCode))
        {
            customerCodes.Add(request.CustomerCode.Trim());
        }
        else
        {
            var custUrl = $"/api/v1/sales/salesinvoice/GetCustCodeListForSalesInvoice/All/A/L/E/C";
            var filterPayload = new JsonObject
            {
                ["pageSize"] = 0,
                ["pageNumber"] = 0,
                ["sortField"] = "",
                ["sortDirection"] = "",
                ["searchValue"] = "",
                ["siteId"] = siteId
            };

            try
            {
                var custResp = await _httpClient.PostAsJsonAsync(custUrl, filterPayload, cancellationToken);
                if (custResp.IsSuccessStatusCode)
                {
                    var envelope = await custResp.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
                    if (envelope?.Data != null)
                    {
                        foreach (var c in envelope.Data)
                        {
                            var code = c["customerCode"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(code) && !customerCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                            {
                                customerCodes.Add(code);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query customer list for invoice discovery");
            }

            try
            {
                var dcCusts = await GetCustomersForDcAsync(siteId, cancellationToken);
                foreach (var code in dcCusts)
                {
                    if (!string.IsNullOrWhiteSpace(code) && !customerCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                    {
                        customerCodes.Add(code);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query DC customer list for invoice discovery");
            }

            if (customerCodes.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(_options.CustomerCode))
                {
                    customerCodes.Add(_options.CustomerCode.Trim());
                }
            }
        }

        _logger.LogInformation("DcToInvoice: Discovered {Count} customer(s) to inspect for pending DCs", customerCodes.Count);

        // ── Step 2: Discover all candidate Delivery Challans ──
        var today = DateTime.Today;
        var formattedDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var candidateDcs = new List<ErpSalInvDcRow>();

        foreach (var cust in customerCodes)
        {
            try
            {
                var dcs = await FetchPendingDcsAsync(
                    cust, _options.Currency, _options.ExNonEx, formattedDate, siteId, cancellationToken);

                foreach (var dc in dcs)
                {
                    // Filter by DcYear if specified
                    if (!string.IsNullOrWhiteSpace(request?.DcYear) &&
                        !string.Equals(dc.DcYear, request.DcYear.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Filter by DcIds if specified
                    if (request?.DcIds != null && request.DcIds.Count > 0 &&
                        !request.DcIds.Contains(dc.DcId))
                    {
                        continue;
                    }

                    // Filter by DcNumbers if specified
                    if (request?.DcNumbers != null && request.DcNumbers.Count > 0 &&
                        !request.DcNumbers.Any(n => string.Equals(n.Trim(), dc.DcNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (!candidateDcs.Any(d => d.DcId == dc.DcId))
                    {
                        candidateDcs.Add(dc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch pending DCs for customer {Customer}", cust);
            }
        }

        _logger.LogInformation("DcToInvoice: Discovered {Count} eligible Delivery Challans to process", candidateDcs.Count);

        var results = new List<DcInvoiceResult>();
        int successfulCount = 0;
        int failedCount = 0;
        int totalItemsCount = 0;
        int successfulItemsCount = 0;
        int failedItemsCount = 0;
        int createdInvoiceCount = 0;

        // ── Step 3: Process eligible Delivery Challans sequentially ──
        for (int i = 0; i < candidateDcs.Count; i++)
        {
            var dc = candidateDcs[i];
            _logger.LogInformation(
                "DcToInvoice: [{Index}/{Total}] Processing DC Id={DcId}, No={DcNo}, Customer={Cust}",
                i + 1, candidateDcs.Count, dc.DcId, dc.DcNo, dc.BillTo);

            if (dryRun)
            {
                results.Add(new DcInvoiceResult(
                    DcId: dc.DcId,
                    DcNo: dc.DcNo,
                    CustomerCode: dc.BillTo,
                    ShipmentCode: dc.ShipCode,
                    Status: "WOULD_CREATE",
                    InvoiceNumber: null,
                    InvoiceId: null,
                    Items: Array.Empty<InvoiceItemResult>(),
                    TotalAmount: 0m,
                    TotalQuantity: 0m,
                    Message: "Dry run: Delivery Challan is eligible for Sales Invoice creation."));
                continue;
            }

            try
            {
                var invoiceResult = await CreateInvoiceFromDeliveryChallanAsync(
                    dc.DcId, dc.DcNo, dc.BillTo, cancellationToken);

                successfulCount++;
                createdInvoiceCount++;
                totalItemsCount += invoiceResult.ItemCount;
                successfulItemsCount += invoiceResult.ItemCount;

                var itemResults = new List<InvoiceItemResult>
                {
                    new(
                        ItemCode: "ALL_ITEMS",
                        ItemName: "All Dispatched Items",
                        DcQuantity: invoiceResult.TotalInvoiceQty,
                        InvoiceQuantity: invoiceResult.TotalInvoiceQty,
                        Rate: 0m,
                        Amount: invoiceResult.TotalInvoiceAmount,
                        RateStructureCode: invoiceResult.RateStructureCode ?? dc.DcRateStructureCode ?? "",
                        Status: "SUCCESS",
                        InvoiceCreated: true,
                        InvoiceId: invoiceResult.InvoiceId,
                        InvoiceNumber: invoiceResult.InvoiceNumber,
                        Message: $"Invoice {invoiceResult.InvoiceNumber} created successfully.")
                };

                results.Add(new DcInvoiceResult(
                    DcId: dc.DcId,
                    DcNo: dc.DcNo,
                    CustomerCode: dc.BillTo,
                    ShipmentCode: dc.ShipCode,
                    Status: "SUCCESS",
                    InvoiceNumber: invoiceResult.InvoiceNumber,
                    InvoiceId: invoiceResult.InvoiceId,
                    Items: itemResults,
                    TotalAmount: invoiceResult.TotalInvoiceAmount,
                    TotalQuantity: invoiceResult.TotalInvoiceQty,
                    Message: $"Invoice {invoiceResult.InvoiceNumber} created successfully.",
                    Authorized: invoiceResult.Authorized,
                    RateStructureCode: invoiceResult.RateStructureCode,
                    LineLevelRateStructureApplied: invoiceResult.LineLevelRateStructureApplied));
            }
            catch (InvoiceValidationException ex)
            {
                failedCount++;
                totalItemsCount++;
                failedItemsCount++;

                _logger.LogWarning(ex, "DcToInvoice: Validation failure processing DC {DcNo} (Id: {DcId}): {Message}",
                    dc.DcNo, dc.DcId, ex.Message);

                results.Add(new DcInvoiceResult(
                    DcId: dc.DcId,
                    DcNo: dc.DcNo,
                    CustomerCode: dc.BillTo,
                    ShipmentCode: dc.ShipCode,
                    Status: "FAILED",
                    InvoiceNumber: null,
                    InvoiceId: null,
                    Items: new List<InvoiceItemResult>
                    {
                        new(
                            ItemCode: "ALL_ITEMS",
                            ItemName: "Delivery Challan Items",
                            DcQuantity: 0m,
                            InvoiceQuantity: 0m,
                            Rate: 0m,
                            Amount: 0m,
                            RateStructureCode: dc.DcRateStructureCode ?? "",
                            Status: "FAILED",
                            InvoiceCreated: false,
                            InvoiceId: null,
                            InvoiceNumber: null,
                            Message: ex.Message,
                            FailedField: ex.Field)
                    },
                    TotalAmount: 0m,
                    TotalQuantity: 0m,
                    Message: ex.Message,
                    FailedField: ex.Field));
            }
            catch (Exception ex)
            {
                failedCount++;
                totalItemsCount++;
                failedItemsCount++;

                _logger.LogError(ex, "DcToInvoice: Unexpected failure processing DC {DcNo} (Id: {DcId}): {Message}",
                    dc.DcNo, dc.DcId, ex.Message);

                results.Add(new DcInvoiceResult(
                    DcId: dc.DcId,
                    DcNo: dc.DcNo,
                    CustomerCode: dc.BillTo,
                    ShipmentCode: dc.ShipCode,
                    Status: "FAILED",
                    InvoiceNumber: null,
                    InvoiceId: null,
                    Items: new List<InvoiceItemResult>
                    {
                        new(
                            ItemCode: "ALL_ITEMS",
                            ItemName: "Delivery Challan Items",
                            DcQuantity: 0m,
                            InvoiceQuantity: 0m,
                            Rate: 0m,
                            Amount: 0m,
                            RateStructureCode: dc.DcRateStructureCode ?? "",
                            Status: "FAILED",
                            InvoiceCreated: false,
                            InvoiceId: null,
                            InvoiceNumber: null,
                            Message: ex.Message)
                    },
                    TotalAmount: 0m,
                    TotalQuantity: 0m,
                    Message: ex.Message));
            }
        }

        var message = dryRun
            ? $"Dry run: {candidateDcs.Count} Delivery Challans evaluated."
            : $"Processed {candidateDcs.Count} Delivery Challans: {successfulCount} succeeded ({createdInvoiceCount} invoices created), {failedCount} failed.";

        _logger.LogInformation("DcToInvoice: Master batch automation finished. {Summary}", message);

        return new BatchInvoiceCreationResult(
            Success: failedCount == 0 && candidateDcs.Count > 0,
            Message: message,
            Data: new BatchInvoiceCreationData(
                TotalDeliveryChallans: candidateDcs.Count,
                ProcessedDeliveryChallans: candidateDcs.Count,
                SuccessfulDeliveryChallans: successfulCount,
                FailedDeliveryChallans: failedCount,
                TotalItems: totalItemsCount,
                SuccessfulItems: successfulItemsCount,
                FailedItems: failedItemsCount,
                CreatedInvoiceCount: createdInvoiceCount,
                Results: results));
    }
}
