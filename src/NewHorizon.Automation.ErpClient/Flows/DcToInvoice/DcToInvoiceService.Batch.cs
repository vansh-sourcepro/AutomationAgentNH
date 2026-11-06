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
            string? resolvedRateStru = dc.DcRateStructureCode;
            if (string.IsNullOrWhiteSpace(resolvedRateStru))
            {
                try
                {
                    var available = await FetchDcRateStructuresAsync(dc.DcId, dc.DcSite, dc.BillTo, cancellationToken);
                    if (available.Count > 0)
                    {
                        resolvedRateStru = available[0].RateStructureCode;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve rate structure for pending DC {DcId}", dc.DcId);
                }
            }

            try
            {
                var (items, _) = await FetchInvoiceItemsAndTaxesAsync(
                    resolvedRateStru ?? string.Empty,
                    dc.DcId,
                    dc.CurrencyCode,
                    dc.OrderId,
                    dc.DcSite,
                    dc.OafId,
                    cancellationToken);

                if (items.Count == 0)
                {
                    var available = await FetchDcRateStructuresAsync(dc.DcId, dc.DcSite, dc.BillTo, cancellationToken);
                    foreach (var alt in available)
                    {
                        if (string.Equals(alt.RateStructureCode, resolvedRateStru, StringComparison.OrdinalIgnoreCase)) continue;
                        var (altItems, _) = await FetchInvoiceItemsAndTaxesAsync(
                            alt.RateStructureCode,
                            dc.DcId,
                            dc.CurrencyCode,
                            dc.OrderId,
                            dc.DcSite,
                            dc.OafId,
                            cancellationToken);
                        if (altItems.Count > 0)
                        {
                            items = altItems;
                            resolvedRateStru = alt.RateStructureCode;
                            break;
                        }
                    }
                }

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

        _logger.LogInformation(
            "DcToInvoice: Master batch automation started. SiteId={SiteId}, CustomerFilter={Cust}",
            siteId, request?.CustomerCode);

        // ── Direct Processing for Explicit DC Numbers or DC IDs ──
        bool hasExplicitNumbers = request?.DcNumbers != null && request.DcNumbers.Count > 0;
        bool hasExplicitIds = request?.DcIds != null && request.DcIds.Count > 0;

        if (hasExplicitNumbers || hasExplicitIds)
        {
            var targets = new List<(long? DcId, string? DcNo)>();
            if (hasExplicitIds)
            {
                foreach (var id in request!.DcIds!)
                {
                    if (id > 0) targets.Add((id, null));
                }
            }
            if (hasExplicitNumbers)
            {
                foreach (var no in request!.DcNumbers!)
                {
                    if (!string.IsNullOrWhiteSpace(no)) targets.Add((null, no.Trim()));
                }
            }

            _logger.LogInformation("DcToInvoice: Master batch executing directly for {Count} target DC(s)", targets.Count);

            var specificResults = new List<DcInvoiceResult>();
            int specificSuccess = 0;
            int specificFailed = 0;
            int specificTotalItems = 0;
            int specificSuccessItems = 0;
            int specificFailedItems = 0;
            int specificCreatedInvoices = 0;

            foreach (var target in targets)
            {
                var label = target.DcId.HasValue ? $"Id: {target.DcId.Value}" : $"No: '{target.DcNo}'";
                try
                {
                    _logger.LogInformation("DcToInvoice: Directly invoking CreateInvoiceFromDeliveryChallanAsync for DC {Target}", label);
                    var invoiceResult = await CreateInvoiceFromDeliveryChallanAsync(
                        dcId: target.DcId,
                        dcNo: target.DcNo,
                        customerCode: request?.CustomerCode,
                        cancellationToken: cancellationToken);

                    bool isAlreadyCreated = string.Equals(invoiceResult.Status, "AlreadyCreated", StringComparison.OrdinalIgnoreCase);

                    if (!isAlreadyCreated)
                    {
                        specificCreatedInvoices++;
                    }
                    specificSuccess++;
                    specificTotalItems += invoiceResult.ItemCount;
                    specificSuccessItems += invoiceResult.ItemCount;

                    decimal targetAvgRate = invoiceResult.TotalInvoiceQty > 0
                        ? Math.Round(invoiceResult.TotalInvoiceAmount / invoiceResult.TotalInvoiceQty, 2)
                        : 0m;

                    string itemStatus = isAlreadyCreated ? "ALREADY_CREATED" : "SUCCESS";
                    string itemMessage = isAlreadyCreated
                        ? (string.Equals(invoiceResult.InvoiceNumber, "ALREADY_INVOICED", StringComparison.OrdinalIgnoreCase)
                            ? $"Delivery Challan {invoiceResult.DcNo} has already been invoiced or has 0 pending items."
                            : $"Delivery Challan {invoiceResult.DcNo} is already converted into Sales Invoice {invoiceResult.InvoiceNumber}.")
                        : $"Invoice {invoiceResult.InvoiceNumber} created successfully.";

                    string dcStatus = isAlreadyCreated ? "ALREADY_CREATED" : "SUCCESS";
                    string dcMessage = isAlreadyCreated
                        ? (string.Equals(invoiceResult.InvoiceNumber, "ALREADY_INVOICED", StringComparison.OrdinalIgnoreCase)
                            ? $"Delivery Challan {invoiceResult.DcNo} was already invoiced in ERP. Duplicate creation skipped."
                            : $"Delivery Challan {invoiceResult.DcNo} is already converted into Sales Invoice {invoiceResult.InvoiceNumber}. Duplicate invoice creation skipped.")
                        : $"Invoice {invoiceResult.InvoiceNumber} created successfully.";

                    var itemResults = new List<InvoiceItemResult>
                    {
                        new(
                            ItemCode: "ALL_ITEMS",
                            ItemName: "All Dispatched Items",
                            DcQuantity: invoiceResult.TotalInvoiceQty,
                            InvoiceQuantity: invoiceResult.TotalInvoiceQty,
                            Rate: targetAvgRate,
                            Amount: invoiceResult.TotalInvoiceAmount,
                            RateStructureCode: invoiceResult.RateStructureCode ?? "",
                            Status: itemStatus,
                            InvoiceCreated: !isAlreadyCreated,
                            InvoiceId: invoiceResult.InvoiceId,
                            InvoiceNumber: invoiceResult.InvoiceNumber,
                            Message: itemMessage)
                    };

                    specificResults.Add(new DcInvoiceResult(
                        DcId: invoiceResult.DcId,
                        DcNo: invoiceResult.DcNo,
                        CustomerCode: invoiceResult.CustomerCode ?? request?.CustomerCode ?? "",
                        ShipmentCode: "",
                        Status: dcStatus,
                        InvoiceNumber: invoiceResult.InvoiceNumber,
                        InvoiceId: invoiceResult.InvoiceId,
                        Items: itemResults,
                        TotalAmount: invoiceResult.TotalInvoiceAmount,
                        TotalQuantity: invoiceResult.TotalInvoiceQty,
                        Message: dcMessage,
                        Authorized: invoiceResult.Authorized,
                        RateStructureCode: invoiceResult.RateStructureCode,
                        LineLevelRateStructureApplied: invoiceResult.LineLevelRateStructureApplied));
                }
                catch (Exception ex)
                {
                    specificFailed++;
                    specificTotalItems++;
                    specificFailedItems++;
                    _logger.LogError(ex, "DcToInvoice: Failure processing specific DC {Target}: {Message}", label, ex.Message);
                    specificResults.Add(new DcInvoiceResult(
                        DcId: target.DcId ?? 0,
                        DcNo: target.DcNo ?? "",
                        CustomerCode: request?.CustomerCode ?? "",
                        ShipmentCode: "",
                        Status: "FAILED",
                        InvoiceNumber: null,
                        InvoiceId: null,
                        Items: Array.Empty<InvoiceItemResult>(),
                        TotalAmount: 0m,
                        TotalQuantity: 0m,
                        Message: ex.Message));
                }
            }

            int createdCount = specificResults.Count(r => r.Status == "SUCCESS");
            int alreadyCount = specificResults.Count(r => r.Status == "ALREADY_CREATED");

            string summaryMsg;
            if (specificFailed == 0)
            {
                if (alreadyCount > 0 && createdCount == 0)
                {
                    summaryMsg = $"Processed {specificResults.Count} Delivery Challan(s): all {alreadyCount} are already converted into Sales Invoices.";
                }
                else if (alreadyCount > 0)
                {
                    summaryMsg = $"Processed {specificResults.Count} Delivery Challan(s): {createdCount} new invoice(s) created, {alreadyCount} already converted.";
                }
                else
                {
                    summaryMsg = $"Successfully processed {specificSuccess} Delivery Challan(s): {createdCount} invoice(s) created.";
                }
            }
            else
            {
                summaryMsg = $"Processed {specificResults.Count} Delivery Challan(s) with {specificFailed} failure(s).";
            }

            return new BatchInvoiceCreationResult(
                Success: specificFailed == 0,
                Message: summaryMsg,
                Data: new BatchInvoiceCreationData(
                    TotalDeliveryChallans: targets.Count,
                    ProcessedDeliveryChallans: targets.Count,
                    SuccessfulDeliveryChallans: specificSuccess,
                    FailedDeliveryChallans: specificFailed,
                    TotalItems: specificTotalItems,
                    SuccessfulItems: specificSuccessItems,
                    FailedItems: specificFailedItems,
                    CreatedInvoiceCount: specificCreatedInvoices,
                    Results: specificResults));
        }

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

                    // Idempotency: Skip Delivery Challans that have already been converted into invoices
                    if (_tracker.IsDcConverted(dc.DcId, dc.DcNo, out var alreadyConverted) && alreadyConverted != null)
                    {
                        _logger.LogInformation(
                            "DcToInvoice: DC {DcNo} (Id: {DcId}) already has Invoice {InvNo}. Skipping duplicate.",
                            dc.DcNo, dc.DcId, alreadyConverted.InvoiceNumber);
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

        if (candidateDcs.Count == 0)
        {
            var emptyMessage = "No pending authorized Delivery Challans found to convert. All eligible Delivery Challans are already converted into Sales Invoices or have 0 pending quantity.";
            _logger.LogInformation("DcToInvoice: {Message}", emptyMessage);
            return new BatchInvoiceCreationResult(
                Success: true,
                Message: emptyMessage,
                Data: new BatchInvoiceCreationData(
                    TotalDeliveryChallans: 0,
                    ProcessedDeliveryChallans: 0,
                    SuccessfulDeliveryChallans: 0,
                    FailedDeliveryChallans: 0,
                    TotalItems: 0,
                    SuccessfulItems: 0,
                    FailedItems: 0,
                    CreatedInvoiceCount: 0,
                    Results: Array.Empty<DcInvoiceResult>()));
        }

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

            try
            {
                var invoiceResult = await CreateInvoiceFromDeliveryChallanAsync(
                    dc.DcId, dc.DcNo, dc.BillTo, cancellationToken: cancellationToken);

                successfulCount++;
                createdInvoiceCount++;
                totalItemsCount += invoiceResult.ItemCount;
                successfulItemsCount += invoiceResult.ItemCount;

                decimal batchAvgRate = invoiceResult.TotalInvoiceQty > 0
                    ? Math.Round(invoiceResult.TotalInvoiceAmount / invoiceResult.TotalInvoiceQty, 2)
                    : 0m;

                var itemResults = new List<InvoiceItemResult>
                {
                    new(
                        ItemCode: "ALL_ITEMS",
                        ItemName: "All Dispatched Items",
                        DcQuantity: invoiceResult.TotalInvoiceQty,
                        InvoiceQuantity: invoiceResult.TotalInvoiceQty,
                        Rate: batchAvgRate,
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
                if (string.Equals(ex.Field, "InvoiceItems", StringComparison.OrdinalIgnoreCase))
                {
                    // DC has 0 pending items because its items were already invoiced in ERP.
                    // Record in tracker so future batch runs immediately recognize it as completed.
                    _tracker.RecordConversion(new ConvertedDcRecord(
                        DcId: dc.DcId,
                        DcNo: dc.DcNo,
                        CustomerCode: dc.BillTo,
                        InvoiceNumber: "ALREADY_INVOICED",
                        InvoiceId: 0,
                        ItemCount: 0,
                        TotalQuantity: 0m,
                        TotalAmount: 0m,
                        RateStructureCode: dc.DcRateStructureCode,
                        ConvertedAtUtc: DateTime.UtcNow));
                }

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

        var message = $"Processed {candidateDcs.Count} Delivery Challans: {successfulCount} succeeded ({createdInvoiceCount} invoices created), {failedCount} failed.";

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
