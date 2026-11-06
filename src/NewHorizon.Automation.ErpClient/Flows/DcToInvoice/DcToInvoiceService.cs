using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

/// <summary>
/// Walks the ERP's Sales Invoice entry sequence for an authorized Delivery Challan and
/// calls the ERP's own <c>sales/salesinvoice/create</c> endpoint — the same call the UI makes —
/// returning the generated Invoice number and Invoice auto ID.
/// </summary>
public sealed partial class DcToInvoiceService : IDcToInvoiceService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IErpTokenProvider _tokenProvider;
    private readonly DcToInvoiceOptions _options;
    private readonly ILogger<DcToInvoiceService> _logger;
    private readonly IConvertedDcTracker _tracker;

    public DcToInvoiceService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        DcToInvoiceOptions options,
        ILogger<DcToInvoiceService> logger,
        IConvertedDcTracker? tracker = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
        _tracker = tracker ?? new ConvertedDcTracker();
    }

    /// <inheritdoc />
    public async Task<InvoiceCreationResult> CreateInvoiceFromDeliveryChallanAsync(
        long? dcId = null,
        string? dcNo = null,
        string? customerCode = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "DcToInvoice: CreateInvoiceFromDeliveryChallan called. DcId={DcId}, DcNo={DcNo}, Cust={Cust}",
            dcId, dcNo, customerCode);

        if (!dcId.HasValue && string.IsNullOrWhiteSpace(dcNo))
        {
            throw new InvoiceValidationException(
                "DeliveryChallanIdentifier",
                "Either DcId or DcNo must be provided to create a Sales Invoice.");
        }

        // ── Idempotency Check: Prevent duplicate invoice creation ──
        if (_tracker.IsDcConverted(dcId, dcNo, out var existingRecord) && existingRecord != null)
        {
            _logger.LogInformation(
                "DcToInvoice: Delivery Challan {DcNo} (Id: {DcId}) was already converted into Sales Invoice {InvNo}. Skipping duplicate invoice creation.",
                dcNo ?? existingRecord.DcNo, dcId ?? existingRecord.DcId, existingRecord.InvoiceNumber);

            return new InvoiceCreationResult(
                InvoiceNumber: existingRecord.InvoiceNumber,
                InvoiceId: existingRecord.InvoiceId,
                DcId: existingRecord.DcId,
                DcNo: existingRecord.DcNo,
                CustomerCode: existingRecord.CustomerCode,
                ItemCount: existingRecord.ItemCount,
                TotalInvoiceQty: existingRecord.TotalQuantity,
                TotalInvoiceAmount: existingRecord.TotalAmount,
                Status: "AlreadyCreated",
                Authorized: false,
                RateStructureCode: existingRecord.RateStructureCode,
                LineLevelRateStructureApplied: false);
        }

        // ── Step 1: Customer resolution ──
        var effectiveCustomerCode = !string.IsNullOrWhiteSpace(customerCode)
            ? customerCode.Trim()
            : _options.CustomerCode;

        // ── Step 2: Query Sales Policy to read MSCENABLELINELEVELRTSTR dynamically ──
        var salesPolicy = await GetSalesPolicyAsync(cancellationToken);
        bool isLineLevelRateStructure = salesPolicy.EnableLineLevelRateStructure;
        _logger.LogInformation(
            "DcToInvoice: Sales Policy resolved. CompanyCode={CompanyCode}, MSCENABLELINELEVELRTSTR={Flag}",
            salesPolicy.CompanyCode, isLineLevelRateStructure ? 1 : 0);

        // ── Step 3: Fetch Document Control Defaults ──
        var today = DateTime.Today;
        var fiscalYear = GetFiscalYear(today);
        var effectiveSiteId = _options.SiteId;
        var docControl = await GetDocumentControlDefaultAsync(
            fiscalYear, _options.DocType, _options.DocSubType, effectiveSiteId, cancellationToken);

        // ── Step 4: Find matching eligible Delivery Challan ──
        var formattedDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var pendingDcs = await FetchPendingDcsAsync(
            effectiveCustomerCode, _options.Currency, _options.ExNonEx, formattedDate, effectiveSiteId, cancellationToken);

        ErpSalInvDcRow? matchedDc = null;
        if (dcId.HasValue && dcId.Value > 0)
        {
            matchedDc = pendingDcs.FirstOrDefault(d => d.DcId == dcId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(dcNo))
        {
            matchedDc = pendingDcs.FirstOrDefault(d =>
                string.Equals(d.DcNo, dcNo.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DcNo.TrimStart('0'), dcNo.Trim().TrimStart('0'), StringComparison.OrdinalIgnoreCase));
        }

        if (matchedDc == null)
        {
            // If customer was empty or matchedDc not found yet, inspect customers with DCs
            var dcCustomers = await GetCustomersForDcAsync(effectiveSiteId, cancellationToken);
            foreach (var cust in dcCustomers)
            {
                if (string.Equals(cust, effectiveCustomerCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dcs = await FetchPendingDcsAsync(
                    cust, _options.Currency, _options.ExNonEx, formattedDate, effectiveSiteId, cancellationToken);
                matchedDc = dcs.FirstOrDefault(d =>
                    (dcId.HasValue && d.DcId == dcId.Value) ||
                    (!string.IsNullOrWhiteSpace(dcNo) && (string.Equals(d.DcNo, dcNo.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(d.DcNo.TrimStart('0'), dcNo.Trim().TrimStart('0'), StringComparison.OrdinalIgnoreCase))));

                if (matchedDc != null)
                {
                    effectiveCustomerCode = matchedDc.BillTo;
                    break;
                }
            }

            if (matchedDc == null)
            {
                throw new InvoiceValidationException(
                    "DeliveryChallan",
                    $"Delivery Challan (Id: {dcId}, No: '{dcNo}') was not found in the list of eligible, " +
                    $"authorized, pending Delivery Challans as of {formattedDate}. " +
                    "It may already be fully invoiced, unauthorized, or belong to a different site/currency.");
            }
        }

        effectiveCustomerCode = matchedDc.BillTo;
        var effectiveDcId = matchedDc.DcId;
        var effectiveDcNo = matchedDc.DcNo;
        var effectiveSiteCode = !string.IsNullOrWhiteSpace(matchedDc.DcSite)
            ? matchedDc.DcSite
            : (!string.IsNullOrWhiteSpace(docControl.LocationCode) ? docControl.LocationCode : _options.SiteCode);

        _logger.LogInformation(
            "DcToInvoice: Matched DC. DcId={DcId}, DcNo={DcNo}, Customer={Cust}, OrderId={OrdId}, OafId={OafId}, SiteCode={SiteCode}, DcRateStr={DcRateStr}",
            effectiveDcId, effectiveDcNo, effectiveCustomerCode, matchedDc.OrderId, matchedDc.OafId, effectiveSiteCode, matchedDc.DcRateStructureCode);

        // ── Step 5: Fetch DC Shipment Logistics Details ──
        var logistics = await GetDcShipmentLogisticsAsync(effectiveDcId, cancellationToken);

        // ── Step 6: Fetch Shipment Details & Place of Supply (Select first/top valid option) ──
        var shipment = await GetShipmentAsync(effectiveCustomerCode, matchedDc.ShipCode, matchedDc.OrderId, cancellationToken);

        // ── Step 7: Rate Structure Resolution (Respecting MSCENABLELINELEVELRTSTR) ──
        string headerRateStructureCode = string.Empty;
        IReadOnlyList<ErpRateStructureOption> availableRateStructures = Array.Empty<ErpRateStructureOption>();

        if (!isLineLevelRateStructure)
        {
            // Case A: MSCENABLELINELEVELRTSTR = 0 -> Rate Structure is at Invoice Header level.
            // Fetch available rate structure options for this Delivery Challan.
            availableRateStructures = await FetchDcRateStructuresAsync(
                effectiveDcId, effectiveSiteCode, effectiveCustomerCode, cancellationToken);

            if (!string.IsNullOrWhiteSpace(matchedDc.DcRateStructureCode))
            {
                headerRateStructureCode = matchedDc.DcRateStructureCode;
            }
            else if (availableRateStructures.Count > 0)
            {
                // Select first/top valid option in the order returned by the application
                headerRateStructureCode = availableRateStructures[0].RateStructureCode;
            }
            else if (!string.IsNullOrWhiteSpace(_options.DefaultRateStructureCode))
            {
                headerRateStructureCode = _options.DefaultRateStructureCode;
            }
            else
            {
                throw new InvoiceValidationException(
                    "RateStructure",
                    $"No valid Rate Structure could be resolved for Delivery Challan {effectiveDcNo} (Id: {effectiveDcId}).");
            }

            _logger.LogInformation(
                "DcToInvoice: Header-level Rate Structure selected (MSCENABLELINELEVELRTSTR=0): {RateStructureCode}",
                headerRateStructureCode);
        }
        else
        {
            // Case B: MSCENABLELINELEVELRTSTR = 1 -> Rate Structure is handled at Invoice Line-Item level.
            _logger.LogInformation(
                "DcToInvoice: Line-item level Rate Structure enabled (MSCENABLELINELEVELRTSTR=1). Header selection bypassed.");
        }

        // ── Step 8: Fetch Item Details & Line Taxes ──
        var (items, taxLines) = await FetchInvoiceItemsAndTaxesAsync(
            headerRateStructureCode,
            effectiveDcId,
            matchedDc.CurrencyCode,
            matchedDc.OrderId,
            effectiveSiteCode,
            matchedDc.OafId,
            cancellationToken);

        // If items are 0, try remaining available rate structures discovered for this DC
        if (items.Count == 0 && availableRateStructures.Count > 1)
        {
            foreach (var altRate in availableRateStructures)
            {
                if (string.Equals(altRate.RateStructureCode, headerRateStructureCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _logger.LogInformation("DcToInvoice: Retrying FetchInvoiceItemsAndTaxesAsync with alternative rate structure {AltRate}", altRate.RateStructureCode);
                var (altItems, altTaxes) = await FetchInvoiceItemsAndTaxesAsync(
                    altRate.RateStructureCode,
                    effectiveDcId,
                    matchedDc.CurrencyCode,
                    matchedDc.OrderId,
                    effectiveSiteCode,
                    matchedDc.OafId,
                    cancellationToken);

                if (altItems.Count > 0)
                {
                    items = altItems;
                    taxLines = altTaxes;
                    headerRateStructureCode = altRate.RateStructureCode;
                    _logger.LogInformation("DcToInvoice: Successfully found {Count} item(s) using rate structure {Rate}", altItems.Count, altRate.RateStructureCode);
                    break;
                }
            }
        }

        if (items.Count == 0)
        {
            throw new InvoiceValidationException(
                "InvoiceItems",
                $"Delivery Challan {effectiveDcId} returned 0 pending items for invoicing.");
        }

        // Validate items and ensure line-level rate structures if flag = 1
        var validItems = new List<ErpInvoiceItemDetail>();
        foreach (var item in items)
        {
            if (item.InvoiceQty <= 0)
            {
                _logger.LogInformation(
                    "DcToInvoice: Item {ItemCode} in DC {DcId} has InvoiceQty <= 0 ({Qty}), skipping line.",
                    item.SalesItemCode, effectiveDcId, item.InvoiceQty);
                continue;
            }

            if (item.DcId <= 0 && effectiveDcId <= 0)
            {
                _logger.LogWarning(
                    "DcToInvoice: Item {ItemCode} in DC {DcId} has invalid DC ID ({DcId}), skipping line.",
                    item.SalesItemCode, effectiveDcId, item.DcId);
                continue;
            }

            if (isLineLevelRateStructure)
            {
                // Line level: verify item line receives its own valid rate structure
                var itemRateStru = !string.IsNullOrWhiteSpace(item.RateStructureCode)
                    ? item.RateStructureCode
                    : headerRateStructureCode;

                if (string.IsNullOrWhiteSpace(itemRateStru))
                {
                    // Fall back to first available or DC's default
                    itemRateStru = matchedDc.DcRateStructureCode ?? "GST18";
                }

                validItems.Add(item with { RateStructureCode = itemRateStru, DcId = effectiveDcId });
            }
            else
            {
                // Header level: all items inherit the selected header rate structure
                validItems.Add(item with { RateStructureCode = headerRateStructureCode, DcId = effectiveDcId });
            }
        }

        if (validItems.Count == 0)
        {
            throw new InvoiceValidationException(
                "InvoiceItems",
                $"Delivery Challan {effectiveDcId} has no items with valid Delivery Challan ID and pending invoice quantity > 0. Skipping invoice creation to prevent creating an empty invoice.");
        }

        // ── Step 9: Resolve Dispatch-From Address ──
        var dispatchAddress = await FetchDispatchFromAddressAsync(
            effectiveDcId, effectiveSiteId, cancellationToken);

        // ── Step 10: Credit checks ──
        if (matchedDc.CreditLimit > 0 && string.Equals(matchedDc.CrLimitInvFlg, "S", StringComparison.OrdinalIgnoreCase))
        {
            var totalInvoiceAmt = validItems.Sum(i => i.AmountWithTax);
            if (totalInvoiceAmt > matchedDc.CreditLimit)
            {
                throw new InvoiceValidationException(
                    "CreditLimit",
                    $"Invoice total amount {totalInvoiceAmt} exceeds customer credit limit of {matchedDc.CreditLimit} and strict limit is enforced.");
            }
        }

        // ── Step 11: Mandatory Required-Field Validation ──
        ValidateMandatoryInvoiceFields(
            matchedDc,
            docControl,
            fiscalYear,
            effectiveSiteId,
            effectiveSiteCode,
            headerRateStructureCode,
            isLineLevelRateStructure,
            shipment,
            validItems,
            taxLines);

        // ── Step 12: Construct SalesInvoiceInsertModel Payload ──
        var payload = BuildInvoiceCreatePayload(
            matchedDc,
            docControl,
            fiscalYear,
            effectiveSiteId,
            effectiveSiteCode,
            headerRateStructureCode,
            isLineLevelRateStructure,
            logistics,
            shipment,
            dispatchAddress,
            validItems,
            taxLines);

        // ── Step 13: Call Existing ERP Save API ──
        var createResponse = await PostInvoiceCreateAsync(payload, cancellationToken);

        _logger.LogInformation(
            "DcToInvoice: Invoice successfully created. InvoiceNumber={InvoiceNo}, InvoiceId={InvoiceId}, DC={DcNo}",
            createResponse.InvoiceNumber, createResponse.InvoiceId, effectiveDcNo);

        var invoiceResult = new InvoiceCreationResult(
            InvoiceNumber: createResponse.InvoiceNumber,
            InvoiceId: createResponse.InvoiceId,
            DcId: effectiveDcId,
            DcNo: effectiveDcNo,
            CustomerCode: effectiveCustomerCode,
            ItemCount: validItems.Count,
            TotalInvoiceQty: validItems.Sum(i => i.InvoiceQty),
            TotalInvoiceAmount: validItems.Sum(i => i.AmountWithTax),
            Status: "Created",
            Authorized: false, // Invoices require 100% manual authorization inside ERP UI per business rule
            RateStructureCode: headerRateStructureCode,
            LineLevelRateStructureApplied: isLineLevelRateStructure);

        _tracker.RecordConversion(new ConvertedDcRecord(
            DcId: effectiveDcId,
            DcNo: effectiveDcNo,
            CustomerCode: effectiveCustomerCode,
            InvoiceNumber: createResponse.InvoiceNumber,
            InvoiceId: createResponse.InvoiceId,
            ItemCount: validItems.Count,
            TotalQuantity: validItems.Sum(i => i.InvoiceQty),
            TotalAmount: validItems.Sum(i => i.AmountWithTax),
            RateStructureCode: headerRateStructureCode,
            ConvertedAtUtc: DateTime.UtcNow));

        return invoiceResult;
    }

    /// <summary>
    /// Strict required-field validation. Every mandatory header and item field must contain valid data
    /// before an invoice can be created. Throws InvoiceValidationException if any mandatory field is missing.
    /// </summary>
    private static void ValidateMandatoryInvoiceFields(
        ErpSalInvDcRow dc,
        ErpInvoiceDocControlDefault docControl,
        string fiscalYear,
        int siteId,
        string siteCode,
        string headerRateStructureCode,
        bool isLineLevelRateStructure,
        ErpInvoiceShipment shipment,
        IReadOnlyList<ErpInvoiceItemDetail> items,
        IReadOnlyList<ErpInvoiceRateStructureLine> taxLines)
    {
        var errors = new List<InvoiceValidationError>();

        // 1. Mandatory Header Fields
        if (string.IsNullOrWhiteSpace(fiscalYear))
            errors.Add(new InvoiceValidationError("FinancialYear", "Financial Year is required and cannot be empty."));

        if (string.IsNullOrWhiteSpace(docControl.GroupCode))
            errors.Add(new InvoiceValidationError("GroupCode", "Document Group Code is required."));

        if (string.IsNullOrWhiteSpace(siteCode))
            errors.Add(new InvoiceValidationError("SiteCode", "Site Code is required."));

        if (siteId <= 0)
            errors.Add(new InvoiceValidationError("LocationId", "Site/Location ID must be greater than zero."));

        if (string.IsNullOrWhiteSpace(dc.BillTo))
            errors.Add(new InvoiceValidationError("CustomerCode", "Customer (BillTo) Code is required."));

        if (string.IsNullOrWhiteSpace(dc.ShipCode))
            errors.Add(new InvoiceValidationError("ShipmentCode", "Shipment / Delivery Destination Code is required."));

        if (string.IsNullOrWhiteSpace(dc.CurrencyCode))
            errors.Add(new InvoiceValidationError("Currency", "Currency code is required."));

        // 2. Rate Structure Validation according to MSCENABLELINELEVELRTSTR
        if (!isLineLevelRateStructure)
        {
            if (string.IsNullOrWhiteSpace(headerRateStructureCode))
            {
                errors.Add(new InvoiceValidationError("HeaderRateStructure",
                    $"Delivery Challan {dc.DcNo} (Id: {dc.DcId}) requires a valid Header-level Rate Structure (MSCENABLELINELEVELRTSTR=0)."));
            }
        }

        // 3. Mandatory Items Validation
        if (items.Count == 0)
        {
            errors.Add(new InvoiceValidationError("InvoiceItems",
                $"Delivery Challan {dc.DcNo} (Id: {dc.DcId}) has no items eligible for invoicing."));
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var lineIdentifier = $"Line #{i + 1} ({item.SalesItemCode})";

                if (string.IsNullOrWhiteSpace(item.SalesItemCode))
                    errors.Add(new InvoiceValidationError("SalesItemCode", $"{lineIdentifier} is missing Sales Item Code."));

                if (string.IsNullOrWhiteSpace(item.IntItmCode))
                    errors.Add(new InvoiceValidationError("InternalItemCode", $"{lineIdentifier} is missing Internal Item Code."));

                if (item.DcId <= 0 && dc.DcId <= 0)
                    errors.Add(new InvoiceValidationError("DeliveryChallanId", $"{lineIdentifier} is missing a valid Delivery Challan ID (DcId)."));

                if (item.InvoiceQty <= 0)
                    errors.Add(new InvoiceValidationError("InvoiceQuantity", $"{lineIdentifier} has invalid Invoice Quantity ({item.InvoiceQty}). Must be greater than zero."));

                if (item.InvoiceQty > item.DcQty && item.DcQty > 0)
                    errors.Add(new InvoiceValidationError("InvoiceQuantityExceeded", $"{lineIdentifier} Invoice Quantity ({item.InvoiceQty}) exceeds DC dispatched quantity ({item.DcQty})."));

                if (item.ConvFact <= 0)
                    errors.Add(new InvoiceValidationError("ConversionFactor", $"{lineIdentifier} has invalid Conversion Factor ({item.ConvFact}). Must be greater than zero."));

                if (item.OafId <= 0)
                    errors.Add(new InvoiceValidationError("OafId", $"{lineIdentifier} is missing valid OAF Reference ID."));

                if (isLineLevelRateStructure && string.IsNullOrWhiteSpace(item.RateStructureCode))
                    errors.Add(new InvoiceValidationError("LineRateStructure", $"{lineIdentifier} is missing Line-level Rate Structure (MSCENABLELINELEVELRTSTR=1)."));
            }
        }

        if (items == null || items.Count == 0)
        {
            throw new InvoiceValidationException("EmptyItems", "Cannot construct Sales Invoice payload with 0 valid items.");
        }

        var totalAmount = items.Sum(i => i.Amount);
        if (totalAmount <= 0 && items.Count > 0)
        {
            errors.Add(new InvoiceValidationError("InvoiceTotalAmount",
                $"Invoice gross amount ({totalAmount}) must be greater than zero for Delivery Challan {dc.DcNo}."));
        }

        if (errors.Count > 0)
        {
            var summary = string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}"));
            throw new InvoiceValidationException(errors[0].Field, $"Mandatory invoice validation failed for DC {dc.DcNo}: {summary}", errors);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Payload Builder
    // ─────────────────────────────────────────────────────────────────

    private JsonObject BuildInvoiceCreatePayload(
        ErpSalInvDcRow dc,
        ErpInvoiceDocControlDefault docControl,
        string fiscalYear,
        int siteId,
        string siteCode,
        string headerRateStructureCode,
        bool isLineLevelRateStructure,
        ErpDcShipmentLogistics logistics,
        ErpInvoiceShipment shipment,
        ErpDispatchFromAddress dispatchAddress,
        IReadOnlyList<ErpInvoiceItemDetail> items,
        IReadOnlyList<ErpInvoiceRateStructureLine> taxLines)
    {
        if (items == null || items.Count == 0)
        {
            throw new InvoiceValidationException("EmptyItems", "Cannot construct Sales Invoice payload with 0 valid items.");
        }

        var totalAmount = items.Sum(i => i.Amount);
        var totalTaxAmount = taxLines.Sum(t => t.RateAmount);
        var totalAmountWithTax = totalAmount + totalTaxAmount;
        var todayIso = DateTime.Today.ToString("yyyy-MM-ddT00:00:00.000Z", CultureInfo.InvariantCulture);
        var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);

        // Header node
        var dtInvHdr = new JsonArray
        {
            new JsonObject
            {
                ["XIHDOCTYP"] = docControl.DocType,
                ["XIHSUBTYP"] = docControl.DocSubType,
                ["XIHYEAR"] = fiscalYear,
                ["XIHGRPCD"] = docControl.GroupCode,
                ["XIHSITE"] = siteCode,
                ["XIHNO"] = "",
                ["XIHDATE"] = todayIso,
                ["XIHLOC"] = siteId,
                ["XIHFRLOC"] = siteId,
                ["XIHCUSTCD"] = dc.BillTo,
                ["XIHRTSCD"] = headerRateStructureCode,
                ["XIHCURCD"] = dc.CurrencyCode,
                ["XIHEXRT"] = dc.ExchangeRate > 0 ? dc.ExchangeRate : 1.0000m,
                ["XIHAMT"] = totalAmount,
                ["XIHATAMT"] = totalAmount,
                ["XIHTOTAMT"] = totalAmountWithTax,
                ["CREUSRID"] = 2,
                ["CREUSRDT"] = nowIso,
                ["LOCREQ"] = docControl.SiteRequired ?? "Y",
                ["AutoNoReq"] = docControl.AutoNumberRequired ?? "Y",
                ["MTMTEXT"] = $"Invoice created against DC {dc.DcNo}",
                ["XIHINVOF"] = _options.InvoiceOf,
                ["XIHNATOFPUR"] = _options.NatureOfSales,
                ["XIHROUNDOFAMT"] = 0.00m,
                ["XIHCUSTVND"] = "C",
                ["XIHTMPNOREQ"] = false,
                ["XIHAUTOID"] = "",
                ["XIHHISINTERBRANCH"] = false,
                ["XIHDIFFVALUE"] = 0.00m,
                ["XIHSHPCD"] = dc.ShipCode ?? "",
                ["XIHBILLTO"] = dc.BillTo,
                ["XIHINVLVLDISPER"] = 0.00m,
                ["XIHINVLVLDISAMT"] = 0.00m,
                ["XIHDISCTYP"] = "N",
                ["STRAUTHREQ"] = "Y",
                ["LOTQuantyBasedDCIds"] = dc.DcId.ToString()
            }
        };

        // Item detail lines
        var dtInvDtl = new JsonArray();
        var dtDCINVRef = new JsonArray();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineNo = (i + 1).ToString();
            var appliedRateStru = isLineLevelRateStructure
                ? (item.RateStructureCode ?? headerRateStructureCode)
                : headerRateStructureCode;

            string discountType = item.DiscountType switch
            {
                "Percentage" or "P" => "P",
                "Value" or "V" => "V",
                _ => "N"
            };

            dtInvDtl.Add(new JsonObject
            {
                ["XIDLNNO"] = lineNo,
                ["XIDSITMCD"] = item.SalesItemCode,
                ["XIDIITMCD"] = item.IntItmCode,
                ["XIDDCID"] = dc.DcId.ToString(),
                ["XIDDCRNDNO"] = item.DcRndNo,
                ["XIDQTYSUOM"] = item.InvoiceQty.ToString("0.0000", CultureInfo.InvariantCulture),
                ["XIDQTYIUOM"] = (item.InvoiceQty * (item.ConvFact > 0 ? item.ConvFact : 1.0m)).ToString("0.0000", CultureInfo.InvariantCulture),
                ["XIDRATE"] = item.OrdRateDis.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDASSRATE"] = item.AssessableRate.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDAMT"] = item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDAMTTAX"] = item.AmountWithTax.ToString("0.00", CultureInfo.InvariantCulture),
                ["MTMDTLTEXT"] = "",
                ["CREUSRID"] = "2",
                ["XIHFRLOC"] = siteId.ToString(),
                ["XIHDOCTYP"] = docControl.DocType,
                ["XIHSUBTYP"] = docControl.DocSubType,
                ["ConvFact"] = item.ConvFact.ToString("0.0000", CultureInfo.InvariantCulture),
                ["XIDAMDSRNO"] = "0",
                ["XIDOAFID"] = item.OafId.ToString(),
                ["XIDOAFLINEID"] = item.OafDetailLineNo.ToString(),
                ["XIDCOMMAMT"] = "0.00",
                ["XIDSITMNAME"] = item.SalesItemName,
                ["XIDNETWGHT"] = item.NetWeight.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDGROSSWGHT"] = item.GrossWeight.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDCASESIZE"] = item.CaseSize ?? "",
                ["InvHsnNum"] = !string.IsNullOrWhiteSpace(item.HsnCode) ? item.HsnCode : "87787909",
                ["SOHsnNum"] = !string.IsNullOrWhiteSpace(item.SoHsnCode) ? item.SoHsnCode : (!string.IsNullOrWhiteSpace(item.HsnCode) ? item.HsnCode : "87787909"),
                ["XIDSJOID"] = item.SjoId.ToString(),
                ["XIDRTSCD"] = appliedRateStru,
                ["xdcsoitmlineno"] = item.SoItmLineNo,
                ["XIDSOCNGRATE"] = 0.00m,
                ["XIDDISCTYP"] = discountType,
                ["XIDDISCVAL"] = item.DiscountVal,
                ["XIDDISCAMT"] = item.DiscountAmt,
                ["dcoaflineno"] = item.DcoafLineNo,
                ["XIDITMSIZE"] = item.ItemSize ?? "",
                ["XIDPCS"] = 0,
                ["XIDITMWGHT"] = item.ItemWeight,
                ["DCIDs"] = dc.DcId.ToString()
            });

            // DC to Invoice Reference link
            dtDCINVRef.Add(new JsonObject
            {
                ["XDCDIINVID"] = "0",
                ["XDCDIDCID"] = dc.DcId.ToString(),
                ["XDCDIDCAUTOID"] = item.DcRndNo,
                ["XDCDIDCQTY"] = item.DcQty.ToString("0.0000", CultureInfo.InvariantCulture),
                ["XDCDIINVQTY"] = item.InvoiceQty.ToString("0.0000", CultureInfo.InvariantCulture),
                ["XDCDIINVLINEID"] = lineNo
            });
        }

        // Rate structure / Tax lines linked per item line
        var rateDtl = new JsonArray();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineNo = (i + 1).ToString();
            var appliedRateStru = isLineLevelRateStructure
                ? (item.RateStructureCode ?? headerRateStructureCode)
                : headerRateStructureCode;

            var applicableTaxes = taxLines.Where(t =>
                string.IsNullOrWhiteSpace(t.ItemCode) ||
                string.Equals(t.ItemCode, item.SalesItemCode, StringComparison.OrdinalIgnoreCase)).ToList();

            if (applicableTaxes.Count == 0 && taxLines.Count > 0)
            {
                applicableTaxes = taxLines.ToList();
            }

            for (int tIdx = 0; tIdx < applicableTaxes.Count; tIdx++)
            {
                var tax = applicableTaxes[tIdx];
                decimal rateAmt = tax.RateAmount;
                if (rateAmt <= 0 && tax.TaxValue > 0 && item.Amount > 0)
                {
                    rateAmt = Math.Round(item.Amount * (tax.TaxValue / 100m), 2);
                }

                rateDtl.Add(new JsonObject
                {
                    ["XDTDCATTYPE"] = docControl.DocType,
                    ["XDTDSUBTYPE"] = docControl.DocSubType,
                    ["XDTFRLOC"] = siteId.ToString(),
                    ["XDTDTMCD"] = item.SalesItemCode,
                    ["XDTDRATECD"] = tax.RateCode,
                    ["RateDesc"] = tax.RateDesc,
                    ["XDTDRATEAMT"] = rateAmt,
                    ["XDTDPERCVAL"] = tax.TaxValue,
                    ["XDTDINCEXC"] = tax.Ie,
                    ["XDTDPERVAL"] = tax.Pv,
                    ["XDTDAPPON"] = tax.AppOnDisplay,
                    ["XDTDPNYN"] = tax.PostNonPost != 0,
                    ["XDTDRTSTRCD"] = appliedRateStru,
                    ["XDTDSEQNO"] = tax.SeqNo > 0 ? tax.SeqNo : (tIdx + 1),
                    ["MPRTAXTYP"] = tax.MprTaxTyp,
                    ["XDTDCURCODE"] = dc.CurrencyCode ?? "INR",
                    ["XDTDREFID"] = lineNo,
                    ["XDTDREFLINE"] = lineNo
                });
            }
        }

        // Miscellaneous Details node required by ERP validator and CSP_XINVMISCDTL_INSERT
        var dtInvMiscDtl = new JsonArray
        {
            new JsonObject
            {
                ["XIMREMVLDT"] = todayIso,
                ["XIMREMVLTM"] = "12:00:00",
                ["XIMDOCTH"] = "TD1",
                ["XIMCRDAYS"] = 0,
                ["XIMCACGRP"] = "A20801",
                ["XIMFINVID"] = 0,
                ["XIMSBOOK"] = "04",
                ["XIMSACGRP"] = "R1",
                ["XIMSACCD"] = "9901",
                ["XIMSUPINVNO"] = "",
                ["XIMSUPINVDT"] = todayIso,
                ["XIMTRNSPTNM"] = "",
                ["XIMSUPLRNO"] = "",
                ["XIMSUPLRDT"] = todayIso,
                ["XIMSUPVCHNO"] = "",
                ["XIMPREPAREDT"] = todayIso,
                ["XIMPREPARETM"] = "12:00:00",
                ["XIMSERTAXTYP"] = "",
                ["XIMDISMODE"] = "MD01",
                ["XIMSHIPVIA"] = "",
                ["XIMPORTLOAD"] = "",
                ["XIMPORTDISCH"] = "",
                ["XIMFINALDEST"] = "",
                ["XIMSHIPMARK"] = "",
                ["XIMPARTYNAME"] = "",
                ["XIMPARTYADD"] = "",
                ["XIMSHIPVEHCLNO"] = "",
                ["XIMSHIPTRANNAME"] = logistics.TransporterCode.NullIsBlank(),
                ["XIMPCKDESC"] = "",
                ["XIMSHIPLRNO"] = "",
                ["XIMSHIPLRDT"] = todayIso,
                ["XIMNETWHGT"] = items.Sum(i => i.NetWeight),
                ["XIMGROSSWHGT"] = items.Sum(i => i.GrossWeight),
                ["XIMISARE"] = false,
                ["XIMRPNO"] = "",
                ["XIMSUBHEAD"] = "1",
                ["XIMPlaceOfSupply"] = shipment.ShipmentStateName.NullIsBlank(),
                ["XIMExpSupply"] = false,
                ["XIMRevsChrgApp"] = false,
                ["XIMBilToCrLmt"] = 0.00m,
                ["XIMBilToAcBal"] = 0.00m,
                ["XIMEXPSUPPAYMETHOD"] = "",
                ["XIMPORTCODE"] = "",
                ["XIMSHIPBILLNO"] = "",
                ["XIMSHIPBILLDATE"] = todayIso,
                ["XIMGSTSALTYP"] = "B2B",
                ["XIMTCSSUBCATCD"] = "",
                ["XIMTCSCATCODE"] = "",
                ["XIMEWAYBILLNO"] = "",
                ["XIMEWAYBILLDT"] = todayIso,
                ["XIMHNATOFTRNS"] = "Not Applicable",
                ["XIMEWAYAPPROXDISTANCE"] = 0.00m,
                ["XIMSHIPEXCRATE"] = 0.00m,
                ["XIMDDSPFRMADD1"] = dispatchAddress.Address1.NullIsBlank(),
                ["XIMDDSPFRMADD2"] = dispatchAddress.Address2.NullIsBlank(),
                ["XIMDDSPFRMADD3"] = dispatchAddress.Address3.NullIsBlank(),
                ["XIMDDSPFRMCTY"] = dispatchAddress.City.NullIsBlank(),
                ["XIMDDSPFRMPINCD"] = dispatchAddress.PinCode.NullIsBlank(),
                ["XIMVEHICLETYPE"] = "R"
            }
        };

        // Notification node
        var notification = new JsonArray
        {
            new JsonObject
            {
                ["FormName"] = "Sales Invoice",
                ["FORMID"] = "06203",
                ["USRID"] = 2,
                ["docYear"] = fiscalYear,
                ["docGroup"] = docControl.GroupCode,
                ["docSite"] = siteCode,
                ["companyId"] = 1,
                ["LocationID"] = siteId,
                ["LocationCode"] = siteCode,
                ["LocationName"] = docControl.LocationName ?? siteCode,
                ["CUSTOMERCODE"] = dc.BillTo,
                ["CUSTOMERNAME"] = dc.BillToName,
                ["VENDORNAME"] = "CTL"
            }
        };

        if (dtInvDtl.Count == 0)
        {
            throw new InvoiceValidationException("EmptyItems", "Cannot construct Sales Invoice payload because dtInvDtl is empty. Aborting creation to prevent empty invoice.");
        }

        return new JsonObject
        {
            ["dtInvHdr"] = dtInvHdr,
            ["dtInvDtl"] = dtInvDtl,
            ["dtDCINVRef"] = dtDCINVRef,
            ["RateDtl"] = rateDtl,
            ["dtInvGRNDtl"] = new JsonArray(),
            ["dtInvMiscDtl"] = dtInvMiscDtl,
            ["dtInvAREDtl"] = new JsonArray(),
            ["CAPDTL"] = new JsonArray(),
            ["AttachmentDetails"] = new JsonArray(),
            ["Authdetail"] = new JsonArray(),
            ["advance"] = new JsonArray(),
            ["agentcommisiondata"] = new JsonArray(),
            ["Notification"] = notification
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  ERP API Call Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<ErpSalesPolicyDetail> GetSalesPolicyAsync(CancellationToken cancellationToken)
    {
        var companyCode = "CTL";
        var url = $"/api/v1/admin/salespolicy/getDetail/{Uri.EscapeDataString(companyCode)}";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetSalesPolicy returned status {StatusCode}, falling back to false", response.StatusCode);
            return new ErpSalesPolicyDetail(companyCode, false);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(SerializerOptions, cancellationToken);
        var dataObj = json?["data"]?["salesPolicyResultModel"]?[0];

        bool enableLineLevel = false;
        if (dataObj != null && dataObj["enablelinelevelratestructure"] != null)
        {
            var flagNode = dataObj["enablelinelevelratestructure"];
            if (flagNode is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var bVal))
                {
                    enableLineLevel = bVal;
                }
                else if (jv.TryGetValue<int>(out var iVal))
                {
                    enableLineLevel = iVal == 1;
                }
                else if (jv.TryGetValue<string>(out var sVal))
                {
                    enableLineLevel = string.Equals(sVal, "1", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(sVal, "true", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(sVal, "Y", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return new ErpSalesPolicyDetail(companyCode, enableLineLevel);
    }

    private async Task<ErpInvoiceDocControlDefault> GetDocumentControlDefaultAsync(
        string year, string type, string subType, int locationId, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Type, string SubType)>
        {
            (type, subType),
            ("EX", "SX")
        };

        foreach (var (t, st) in candidates)
        {
            var url = $"/api/v1/admin/documentControl/getDefaultDocumentDetail/{Uri.EscapeDataString(year)}/{Uri.EscapeDataString(t)}/{Uri.EscapeDataString(st)}/{locationId}";
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                    var json = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(raw, SerializerOptions);
                    JsonObject? row = null;
                    if (json?["data"] is JsonArray arr && arr.Count > 0)
                    {
                        row = arr[0] as JsonObject;
                    }
                    else if (json?["data"] is JsonObject obj)
                    {
                        row = obj;
                    }

                    if (row != null && !string.IsNullOrWhiteSpace(row["groupCode"]?.ToString()))
                    {
                        return new ErpInvoiceDocControlDefault(
                            FinancialYear: year,
                            GroupCode: row["groupCode"]?.ToString() ?? "SE",
                            LocationCode: row["locationCode"]?.ToString() ?? "NF1",
                            LocationName: row["locationName"]?.ToString() ?? "CTL SITE-2",
                            LocationId: row["locationId"] != null ? Convert.ToInt32(row["locationId"]!.ToString()) : locationId,
                            SiteRequired: row["isLocationRequired"]?.ToString() == "true" ? "Y" : "N",
                            AutoNumberRequired: row["isAutoNumberGenerated"]?.ToString() == "false" ? "N" : "Y",
                            AuthorisationRequired: row["isAutorisationRequired"]?.ToString() == "false" ? "N" : "Y",
                            DocType: t,
                            DocSubType: st);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query document control for {Type}/{SubType}", t, st);
            }
        }

        return new ErpInvoiceDocControlDefault(
            FinancialYear: year,
            GroupCode: "SE",
            LocationCode: _options.SiteCode,
            LocationName: "CTL SITE-2",
            LocationId: locationId,
            SiteRequired: "Y",
            AutoNumberRequired: "Y",
            AuthorisationRequired: "Y",
            DocType: "EX",
            DocSubType: "SX");
    }

    private async Task<IReadOnlyList<ErpSalInvDcRow>> FetchPendingDcsAsync(
        string customerCode, string currency, string strExNonEx, string invDate, int siteId, CancellationToken cancellationToken)
    {
        var effectiveCust = string.IsNullOrWhiteSpace(customerCode) ? "All" : customerCode.Trim();
        var encodedCust = Uri.EscapeDataString(effectiveCust);
        var effectiveCurr = string.IsNullOrWhiteSpace(currency) ? "%20" : currency.Trim();
        var encodedCurr = Uri.EscapeDataString(effectiveCurr);
        var url = $"/api/v1/sales/salesinvoice/FetchDCDtlsForSalInv/{encodedCust}/{encodedCurr}/{Uri.EscapeDataString(strExNonEx)}/{invDate}/{siteId}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("DcToInvoice: FetchDCDtlsForSalInv (url={Url}) raw response: {Raw}", url, rawJson);

        var envelope = System.Text.Json.JsonSerializer.Deserialize<ErpEnvelopeList<JsonObject>>(rawJson, SerializerOptions);
        if (envelope?.Data == null || envelope.Data.Count == 0)
        {
            return Array.Empty<ErpSalInvDcRow>();
        }

        var results = new List<ErpSalInvDcRow>();
        foreach (var obj in envelope.Data)
        {
            var dcIdVal = obj["dcid"] ?? obj["DCID"] ?? obj["dcId"];
            if (dcIdVal == null) continue;

            long id = Convert.ToInt64(dcIdVal.ToString(), CultureInfo.InvariantCulture);
            results.Add(new ErpSalInvDcRow(
                DcId: id,
                DcNo: (obj["dcNo"] ?? obj["XDCHDCNO"] ?? obj["dcno"])?.ToString() ?? string.Empty,
                DcDate: (obj["dcDate"] ?? obj["XDCHDATE"]) != null ? DateTime.Parse((obj["dcDate"] ?? obj["XDCHDATE"])!.ToString()) : DateTime.Today,
                DcYear: (obj["dcYear"] ?? obj["dcyear"] ?? obj["XDCHYEAR"] ?? obj["year"])?.ToString() ?? string.Empty,
                DcGroup: (obj["dcGroup"] ?? obj["dcgroup"] ?? obj["XDCHGRPCD"] ?? obj["group"])?.ToString() ?? string.Empty,
                DcSite: (obj["dcSite"] ?? obj["dcsite"] ?? obj["DC SITECD"] ?? obj["site"] ?? obj["siteCode"] ?? obj["locationCode"])?.ToString() ?? string.Empty,
                OafNo: (obj["oafNo"] ?? obj["XOAFHNO"] ?? obj["oafno"])?.ToString() ?? string.Empty,
                OafYear: (obj["oafYear"] ?? obj["XOAFHYEAR"] ?? obj["oafyear"])?.ToString() ?? string.Empty,
                OafGroup: (obj["oafGroup"] ?? obj["XOAFHGRPCD"] ?? obj["oafgroup"])?.ToString() ?? string.Empty,
                OafSite: (obj["oafSite"] ?? obj["OAF SITECD"] ?? obj["oafsite"])?.ToString() ?? string.Empty,
                OafId: int.TryParse((obj["XOAFHAUTOID"] ?? obj["xoafhautoid"] ?? obj["oafId"] ?? obj["oafid"])?.ToString(), out var oId) ? oId : 0,
                OrderId: long.TryParse((obj["ORDID"] ?? obj["ordid"] ?? obj["orderId"] ?? obj["ordId"])?.ToString(), out var ordId) ? ordId : 0,
                IntOdrNo: (obj["intOdrNo"] ?? obj["XOBINTORDNO"] ?? obj["intodrno"])?.ToString() ?? string.Empty,
                IntOdrYear: (obj["intOdrYear"] ?? obj["XOBINTORDYR"] ?? obj["intodryear"])?.ToString() ?? string.Empty,
                IntOdrGroup: (obj["intOdrGroup"] ?? obj["XOBINTORDGRP"] ?? obj["intodrgroup"])?.ToString() ?? string.Empty,
                IntOdrSite: (obj["intOdrSite"] ?? obj["ORD SITECD"] ?? obj["intodrsite"])?.ToString() ?? string.Empty,
                CustPoNo: (obj["custPOno"] ?? obj["XOBORDNO"] ?? obj["custPoNo"] ?? obj["custpono"])?.ToString() ?? string.Empty,
                CustPoDate: (obj["custPOdate"] ?? obj["XOBORDDT"] ?? obj["custPoDate"]) != null ? DateTime.Parse((obj["custPOdate"] ?? obj["XOBORDDT"] ?? obj["custPoDate"])!.ToString()) : null,
                ShipCode: (obj["shipCode"] ?? obj["MFMFACTCD"] ?? obj["shipcode"])?.ToString() ?? string.Empty,
                BillTo: (obj["billTo"] ?? obj["XOBBILLTO"] ?? obj["billto"] ?? obj["customerCode"])?.ToString() ?? customerCode,
                BillToName: (obj["billToName"] ?? obj["MCMCUSTNM"] ?? obj["billtoname"] ?? obj["customerName"])?.ToString() ?? string.Empty,
                CurrencyCode: (obj["XOBCURRCD"] ?? obj["xobcurrcd"] ?? obj["currencyCode"] ?? obj["currency"])?.ToString() ?? currency,
                ExchangeRate: (obj["XOBEXRATE"] ?? obj["exchangeRate"] ?? obj["exchangerate"]) != null ? Convert.ToDecimal((obj["XOBEXRATE"] ?? obj["exchangeRate"] ?? obj["exchangerate"])!.ToString(), CultureInfo.InvariantCulture) : 1.0m,
                DcRateStructureCode: (obj["xdcrtstrcd"] ?? obj["ratestructurecd"] ?? obj["rateStructureCode"] ?? obj["dcRateStructureCode"])?.ToString(),
                DeliveryName: (obj["DeliveryName"] ?? obj["deliveryName"] ?? obj["deliveryname"])?.ToString(),
                DeliveryCityCode: (obj["DeliveryCityCode"] ?? obj["deliveryCityCode"] ?? obj["deliverycitycode"])?.ToString(),
                DeliveryCityName: (obj["DeliveryCityName"] ?? obj["deliveryCityName"] ?? obj["deliverycityname"])?.ToString(),
                CreditDays: (obj["XOBORDCRDAYS"] ?? obj["creditDays"]) != null ? Convert.ToInt32((obj["XOBORDCRDAYS"] ?? obj["creditDays"])!.ToString()) : 30,
                CreditLimit: (obj["MCMCREDITLMT"] ?? obj["creditLimit"]) != null ? Convert.ToDecimal((obj["MCMCREDITLMT"] ?? obj["creditLimit"])!.ToString(), CultureInfo.InvariantCulture) : 0m,
                CrLimitInvFlg: (obj["MCMCreditInvoiceFlg"] ?? obj["crLimitInvflg"])?.ToString() ?? "W",
                CreditLimitFlag: (obj["MCMCREDITLMTFLAG"] ?? obj["creditLimitflag"])?.ToString() ?? "A",
                CreditLimitDays: (obj["MCMCREDITLMTDAYS"] ?? obj["creditLimitdays"]) != null ? Convert.ToInt32((obj["MCMCREDITLMTDAYS"] ?? obj["creditLimitdays"])!.ToString()) : 0
            ));
        }

        return results;
    }

    private async Task<ErpDcShipmentLogistics> GetDcShipmentLogisticsAsync(long dcId, CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/deliverychallanentry/DCShopmentDetails/{dcId}";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
            if (envelope?.Data != null && envelope.Data.Count > 0)
            {
                var row = envelope.Data[0];
                return new ErpDcShipmentLogistics(
                    LrNo: row["xdcmlrno"]?.ToString(),
                    VehicleNo: row["xdcmvno"]?.ToString()?.Trim(),
                    GrossWeight: row["xdcmgrsw"] != null ? Convert.ToDecimal(row["xdcmgrsw"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    NetWeight: row["xdcmnetw"] != null ? Convert.ToDecimal(row["xdcmnetw"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    TransporterCode: row["xdcmtrnpcd"]?.ToString(),
                    RoadPermitNo: row["xdcmrpno"]?.ToString()?.Trim());
            }
        }

        return new ErpDcShipmentLogistics(null, null, 0m, 0m, null, null);
    }

    private async Task<ErpInvoiceShipment> GetShipmentAsync(string custCode, string shipCode, long orderId, CancellationToken cancellationToken)
    {
        var effectiveCust = string.IsNullOrWhiteSpace(custCode) ? "%20" : Uri.EscapeDataString(custCode.Trim());
        var effectiveShip = string.IsNullOrWhiteSpace(shipCode) ? "%20" : Uri.EscapeDataString(shipCode.Trim());
        var url = $"/api/v1/sales/salesinvoice/GetShipment/{effectiveCust}/{effectiveShip}/{orderId}/E";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
            if (envelope?.Data != null && envelope.Data.Count > 0)
            {
                var row = envelope.Data[0];
                return new ErpInvoiceShipment(
                    ShipmentCode: row["shipmentCode"]?.ToString() ?? shipCode,
                    ShipmentName: row["shipmentName"]?.ToString() ?? string.Empty,
                    ShipmentAddress1: row["shipmentAddress1"]?.ToString(),
                    ShipmentCityCode: row["shipmentCityCode"]?.ToString(),
                    ShipmentCityName: row["shipmentCityName"]?.ToString(),
                    ShipmentStateCode: row["shipmentStateCode"]?.ToString(),
                    ShipmentStateName: row["shipmentStateName"]?.ToString(),
                    ShipmentPinCode: row["shipmentPinCode"]?.ToString(),
                    ShipmentGstNo: row["shipmentGstNo"]?.ToString());
            }
        }

        return new ErpInvoiceShipment(shipCode, string.Empty, null, null, null, null, null, null, null);
    }

    private async Task<IReadOnlyList<ErpRateStructureOption>> FetchDcRateStructuresAsync(
        long dcId, string siteCode, string custCode, CancellationToken cancellationToken)
    {
        var url = "/api/v1/sales/salesinvoice/FetchDCRateStructure";

        var sitesToTry = new List<string>();
        if (!string.IsNullOrWhiteSpace(siteCode)) sitesToTry.Add(siteCode.Trim());
        if (!string.IsNullOrWhiteSpace(_options.SiteCode) && !sitesToTry.Contains(_options.SiteCode, StringComparer.OrdinalIgnoreCase))
            sitesToTry.Add(_options.SiteCode.Trim());
        if (!sitesToTry.Contains("NF1", StringComparer.OrdinalIgnoreCase)) sitesToTry.Add("NF1");
        if (!sitesToTry.Contains("CT2", StringComparer.OrdinalIgnoreCase)) sitesToTry.Add("CT2");
        if (!sitesToTry.Contains(string.Empty)) sitesToTry.Add(string.Empty);

        foreach (var trySite in sitesToTry)
        {
            var payload = new JsonObject
            {
                ["strFlag"] = "L",
                ["strDCId"] = dcId.ToString(CultureInfo.InvariantCulture),
                ["strRtStruCd"] = "",
                ["strExNonEx"] = "E",
                ["strInvOf"] = !string.IsNullOrWhiteSpace(_options.InvoiceOf) ? _options.InvoiceOf : "ProductSpare",
                ["IsGSTApplicable"] = true,
                ["site"] = trySite,
                ["custcode"] = custCode,
                ["isDiffCustBillTo"] = false,
                ["itemCode"] = ""
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogInformation("DcToInvoice: FetchDCRateStructure (site='{Site}') response: {Raw}", trySite, rawJson);

                    var envelope = System.Text.Json.JsonSerializer.Deserialize<ErpEnvelopeList<JsonObject>>(rawJson, SerializerOptions);
                    if (envelope?.Data != null && envelope.Data.Count > 0)
                    {
                        var options = envelope.Data.Select(d => new ErpRateStructureOption(
                            RateStructureCode: (d["rsCode"] ?? d["RsCode"] ?? d["rateStructureCode"] ?? d["ratestructurecode"] ?? d["strRtStruCd"] ?? d["rateStructureCd"] ?? d["code"])?.ToString() ?? string.Empty,
                            RateStructureDescription: (d["rsDesc"] ?? d["RsDesc"] ?? d["rateStructureDescription"] ?? d["ratestructuredescription"] ?? d["rateDesc"] ?? d["ratedesc"])?.ToString() ?? string.Empty,
                            RateStructureFullName: (d["rsDesc"] ?? d["RsDesc"] ?? d["rateStructureFullName"] ?? d["ratestructurefullname"] ?? d["ratedesc"])?.ToString() ?? string.Empty,
                            IsGSTApplicable: d["isGSTApplicable"]?.GetValue<bool>() ?? true
                        )).Where(r => !string.IsNullOrWhiteSpace(r.RateStructureCode)).ToList();

                        if (options.Count > 0)
                        {
                            return options;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DcToInvoice: FetchDCRateStructure failed for site '{Site}'", trySite);
            }
        }

        return Array.Empty<ErpRateStructureOption>();
    }

    private async Task<(IReadOnlyList<ErpInvoiceItemDetail> Items, IReadOnlyList<ErpInvoiceRateStructureLine> Taxes)> FetchInvoiceItemsAndTaxesAsync(
        string rateStructureCode,
        long dcId,
        string currency,
        long orderId,
        string siteCode,
        int oafId,
        CancellationToken cancellationToken)
    {
        var effectiveOafId = oafId > 0 ? oafId : orderId;
        var effectiveOrdId = effectiveOafId > 0 ? effectiveOafId : orderId;
        var effectiveSite = !string.IsNullOrWhiteSpace(siteCode) ? siteCode.Trim() : (!string.IsNullOrWhiteSpace(_options.SiteCode) ? _options.SiteCode : "NF1");
        HttpResponseMessage? response = null;
        string? urlUsed = null;
        string? rawJson = null;

        // 1. Try official POST FetchDCItemDetails endpoint (same as WebApp2 Angular frontend: strordid = xoafhautoid)
        try
        {
            const string postUrl = "/api/v1/sales/salesinvoice/FetchDCItemDetails";
            var postModel = new
            {
                strRtStruCd = rateStructureCode ?? string.Empty,
                strDCId = dcId.ToString(CultureInfo.InvariantCulture),
                currency = string.IsNullOrWhiteSpace(currency) ? "INR" : currency.Trim(),
                strExNonEx = "E",
                IsGSTApplicable = true,
                strordid = effectiveOrdId.ToString(CultureInfo.InvariantCulture),
                empt = string.Empty,
                isDiffCustBillTo = false,
                siteCode = effectiveSite,
                stroafid = effectiveOafId.ToString(CultureInfo.InvariantCulture)
            };

            var postResponse = await _httpClient.PostAsJsonAsync(postUrl, postModel, cancellationToken);
            if (postResponse.IsSuccessStatusCode)
            {
                var testRaw = await postResponse.Content.ReadAsStringAsync(cancellationToken);
                var testJson = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(testRaw, SerializerOptions);
                var testItems = testJson?["data"]?["orderdtlListModel"] as JsonArray;
                if (testItems != null && testItems.Count > 0)
                {
                    response = postResponse;
                    urlUsed = postUrl;
                    rawJson = testRaw;
                }
                else
                {
                    _logger.LogInformation("DcToInvoice: POST FetchDCItemDetails returned 0 items, falling back to GET FetchItemDtls.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DcToInvoice: POST FetchDCItemDetails attempt threw exception, falling back to GET FetchItemDtls");
        }

        // 2. Fallback to GET FetchItemDtls if POST was not used or yielded 0 items
        if (response == null)
        {
            var encodedRate = string.IsNullOrWhiteSpace(rateStructureCode) ? "%20" : Uri.EscapeDataString(rateStructureCode.Trim());
            var encodedCurr = string.IsNullOrWhiteSpace(currency) ? "%20" : Uri.EscapeDataString(currency.Trim());
            var getUrl = $"/api/v1/sales/salesinvoice/FetchItemDtls/{encodedRate}/{dcId}/{encodedCurr}/E/true/{effectiveOrdId}/%20/false/{Uri.EscapeDataString(effectiveSite)}?strOAFID={effectiveOafId}";

            response = await _httpClient.GetAsync(getUrl, cancellationToken);
            EnsureSuccess(response, getUrl);
            urlUsed = getUrl;
            rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        rawJson ??= await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("DcToInvoice: FetchItemDtls (url={Url}) response: {Raw}", urlUsed, rawJson);

        var json = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(rawJson, SerializerOptions);
        var dataObj = json?["data"];
        if (dataObj == null)
        {
            return (Array.Empty<ErpInvoiceItemDetail>(), Array.Empty<ErpInvoiceRateStructureLine>());
        }

        var itemsList = new List<ErpInvoiceItemDetail>();
        var itemsArray = dataObj["orderdtlListModel"] as JsonArray;
        if (itemsArray != null)
        {
            int lineCounter = 1;
            foreach (var itemNode in itemsArray)
            {
                if (itemNode == null) continue;

                decimal dcQty = decimal.TryParse(itemNode["dcQty"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDcQty) ? parsedDcQty : 0m;
                decimal maxInvQty = decimal.TryParse(itemNode["maxInvQty"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedMaxInvQty) ? parsedMaxInvQty : 0m;
                decimal rawInvQty = decimal.TryParse(itemNode["invoiceQty"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRawInvQty) ? parsedRawInvQty : 0m;
                decimal effectiveInvQty = rawInvQty > 0 ? rawInvQty : (maxInvQty > 0 ? maxInvQty : 0m);

                decimal ordRateDis = decimal.TryParse(itemNode["ordRateDis"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedOrdRateDis) ? parsedOrdRateDis : 0m;
                if (ordRateDis <= 0)
                {
                    ordRateDis = decimal.TryParse(itemNode["itemRate"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedItemRate) ? parsedItemRate : 0m;
                }
                if (ordRateDis <= 0)
                {
                    ordRateDis = decimal.TryParse(itemNode["soBasicPrice"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedSoRate) ? parsedSoRate : 0m;
                }

                decimal convFact = decimal.TryParse(itemNode["convFact"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedConvFact) ? parsedConvFact : 1.0m;
                decimal amount = decimal.TryParse(itemNode["amount"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmount) ? parsedAmount : 0m;
                if (amount <= 0 && effectiveInvQty > 0 && ordRateDis > 0)
                {
                    amount = Math.Round(effectiveInvQty * (convFact > 0 ? convFact : 1.0m) * ordRateDis, 2);
                }

                decimal orderRate = decimal.TryParse(itemNode["orderRate"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedOrderRate) ? parsedOrderRate : ordRateDis;
                decimal assessableRate = decimal.TryParse(itemNode["assessableRate"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAssessableRate) ? parsedAssessableRate : ordRateDis;
                decimal amountWithTax = decimal.TryParse(itemNode["amountWithTax"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmountWithTax) ? parsedAmountWithTax : amount;
                int itemOafId = int.TryParse(itemNode["oafId"]?.ToString(), out var parsedOafId) ? parsedOafId : oafId;
                int oafDetailLineNo = int.TryParse((itemNode["oafDetailLineNo"] ?? itemNode["oaflineid"])?.ToString(), out var parsedOafDetailLineNo) ? parsedOafDetailLineNo : 1;
                int dcoafLineNo = int.TryParse(itemNode["dcoaflineno"]?.ToString(), out var parsedDcoafLineNo) ? parsedDcoafLineNo : 1;
                long sjoId = long.TryParse(itemNode["sjoId"]?.ToString(), out var parsedSjoId) ? parsedSjoId : 0;
                int soItmLineNo = int.TryParse((itemNode["xdcsoitmlineno"] ?? itemNode["soItmLineNo"])?.ToString(), out var parsedSoItmLineNo) ? parsedSoItmLineNo : 1;
                decimal netWeight = decimal.TryParse(itemNode["netWeight"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNetWeight) ? parsedNetWeight : 0m;
                decimal grossWeight = decimal.TryParse(itemNode["grossWeight"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedGrossWeight) ? parsedGrossWeight : 0m;
                decimal itemWeight = decimal.TryParse(itemNode["itemWeight"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedItemWeight) ? parsedItemWeight : 0m;
                decimal discountVal = decimal.TryParse(itemNode["discountVal"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDiscountVal) ? parsedDiscountVal : 0m;
                decimal discountAmt = decimal.TryParse(itemNode["discountAmt"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDiscountAmt) ? parsedDiscountAmt : 0m;

                // Parse dcautoid (the DC line surrogate ID in ERP)
                long dcAutoId = 0;
                var rawAutoId = itemNode["dcautoid"] ?? itemNode["dcAutoId"] ?? itemNode["dcRndNo"];
                if (rawAutoId != null && long.TryParse(rawAutoId.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAutoId))
                {
                    dcAutoId = parsedAutoId;
                }

                // In ERP, lines are linked by DCId (XDCDHID) and surrogate dcautoid (or "0" if null/0 per WebApp2).
                string resolvedDcRndNo = dcAutoId > 0 ? dcAutoId.ToString(CultureInfo.InvariantCulture) : "0";

                itemsList.Add(new ErpInvoiceItemDetail(
                    LineNo: lineCounter++,
                    SalesItemCode: itemNode["salesItemCode"]?.ToString() ?? string.Empty,
                    SalesItemName: itemNode["salesItemName"]?.ToString() ?? string.Empty,
                    IntItmCode: itemNode["intItmCode"]?.ToString() ?? string.Empty,
                    ProductSpare: itemNode["productSpare"]?.ToString() ?? "P",
                    OafRef: itemNode["oafRef"]?.ToString(),
                    IonRef: itemNode["ionRef"]?.ToString(),
                    ItemDcNo: itemNode["itemdcno"]?.ToString(),
                    Suom: itemNode["suom"]?.ToString() ?? "NOS",
                    Iuom: itemNode["iuom"]?.ToString() ?? "NOS",
                    ConvFact: convFact,
                    DcQty: dcQty > 0 ? dcQty : effectiveInvQty,
                    InvoiceQty: effectiveInvQty,
                    OrdRateDis: ordRateDis,
                    OrderRate: orderRate,
                    AssessableRate: assessableRate,
                    Amount: amount,
                    AmountWithTax: amountWithTax,
                    OafId: itemOafId,
                    OafDetailLineNo: oafDetailLineNo,
                    DcoafLineNo: dcoafLineNo,
                    DcRndNo: resolvedDcRndNo,
                    DcId: dcId,
                    OrdId: orderId,
                    SjoId: sjoId,
                    SoItmLineNo: soItmLineNo,
                    HsnCode: (itemNode["hsnsac"] ?? itemNode["hsnCode"] ?? itemNode["hsnsaccode"])?.ToString() ?? "87787909",
                    SoHsnCode: (itemNode["sohsnNumber"] ?? itemNode["soHsnCode"] ?? itemNode["hsnsac"])?.ToString() ?? "87787909",
                    RateStructureCode: itemNode["ratestructurecd"]?.ToString(),
                    NetWeight: netWeight,
                    GrossWeight: grossWeight,
                    CaseSize: itemNode["caseSize"]?.ToString(),
                    ItemSize: itemNode["itemSize"]?.ToString(),
                    ItemWeight: itemWeight,
                    DiscountType: itemNode["discountType"]?.ToString(),
                    DiscountVal: discountVal,
                    DiscountAmt: discountAmt
                ));
            }
        }

        var taxesList = new List<ErpInvoiceRateStructureLine>();
        var taxesArray = dataObj["ratestructureModel"] as JsonArray;
        if (taxesArray != null)
        {
            int seq = 1;
            foreach (var taxNode in taxesArray)
            {
                if (taxNode == null) continue;

                decimal taxVal = taxNode["taxValue"] != null ? Convert.ToDecimal(taxNode["taxValue"]!.ToString(), CultureInfo.InvariantCulture) : 0m;
                decimal rateAmt = taxNode["rateAmount"] != null ? Convert.ToDecimal(taxNode["rateAmount"]!.ToString(), CultureInfo.InvariantCulture) : 0m;
                if (rateAmt <= 0 && taxVal > 0 && itemsList.Count > 0)
                {
                    var totalBaseAmt = itemsList.Sum(i => i.Amount);
                    rateAmt = Math.Round(totalBaseAmt * (taxVal / 100m), 2);
                }

                int postNonPost = 1;
                if (taxNode["postNonPost"] != null)
                {
                    var pnpStr = taxNode["postNonPost"]!.ToString();
                    if (bool.TryParse(pnpStr, out var pnpBool))
                    {
                        postNonPost = pnpBool ? 1 : 0;
                    }
                    else if (int.TryParse(pnpStr, out var pnpInt))
                    {
                        postNonPost = pnpInt;
                    }
                }

                taxesList.Add(new ErpInvoiceRateStructureLine(
                    RateCode: taxNode["rateCode"]?.ToString() ?? string.Empty,
                    RateDesc: taxNode["rateDesc"]?.ToString() ?? string.Empty,
                    TaxValue: taxVal,
                    AppOnDisplay: taxNode["appOnDisplay"]?.ToString() ?? "Gross Value",
                    Ie: taxNode["ie"]?.ToString() ?? "I",
                    Pv: taxNode["pv"]?.ToString() ?? "P",
                    MprTaxTyp: taxNode["mprtaxtyp"]?.ToString() ?? "C",
                    RateAmount: rateAmt,
                    PostNonPost: postNonPost,
                    UniqueNo: taxNode["uniqueno"] != null ? Convert.ToInt32(taxNode["uniqueno"]!.ToString()) : 0,
                    ItemCode: taxNode["itemCode"]?.ToString() ?? string.Empty,
                    SeqNo: taxNode["seqNo"] != null ? Convert.ToInt32(taxNode["seqNo"]!.ToString()) : seq++,
                    RateStructureCode: taxNode["ratestructurecd"]?.ToString() ?? rateStructureCode
                ));
            }
        }

        // Update AmountWithTax for all items once taxes are resolved
        var totalTaxRate = taxesList.Where(t => t.TaxValue > 0).Sum(t => t.TaxValue);
        for (int i = 0; i < itemsList.Count; i++)
        {
            var item = itemsList[i];
            if ((item.AmountWithTax <= item.Amount) && item.Amount > 0 && totalTaxRate > 0)
            {
                var lineTax = Math.Round(item.Amount * (totalTaxRate / 100m), 2);
                itemsList[i] = item with { AmountWithTax = item.Amount + lineTax };
            }
        }

        return (itemsList, taxesList);
    }

    private async Task<IReadOnlyList<string>> GetCustomersForDcAsync(
        int siteId,
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/sales/deliverychallanentry/getCustomerList/L/F";

        try
        {
            var filterPayload = new JsonObject
            {
                ["pageSize"] = 100,
                ["pageNumber"] = 1,
                ["sortField"] = "",
                ["sortDirection"] = "asc",
                ["searchValue"] = "",
                ["siteId"] = siteId
            };

            var response = await _httpClient.PostAsJsonAsync(url, filterPayload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            if (envelope?.Data is null)
            {
                return Array.Empty<string>();
            }

            return envelope.Data
                .Select(node => node["customerCode"]?.ToString() ?? "")
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DcToInvoice: GetCustomersForDc failed: {Msg}", ex.Message);
            return Array.Empty<string>();
        }
    }

    private async Task<ErpDispatchFromAddress> FetchDispatchFromAddressAsync(
        long dcId, int siteId, CancellationToken cancellationToken)
    {
        var url = "/api/v1/sales/salesinvoice/fetchDispatchFrmAddress";
        var payload = new JsonObject
        {
            ["dcId"] = dcId.ToString(),
            ["locId"] = siteId,
            ["compId"] = 1
        };

        var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
            if (envelope?.Data != null && envelope.Data.Count > 0)
            {
                var row = envelope.Data[0];
                return new ErpDispatchFromAddress(
                    Address1: row["Address1"]?.ToString(),
                    Address2: row["Address2"]?.ToString(),
                    Address3: row["Address3"]?.ToString(),
                    City: row["City"]?.ToString(),
                    CityName: row["CityName"]?.ToString(),
                    StateName: row["StateName"]?.ToString(),
                    CountryName: row["CountryName"]?.ToString(),
                    PinCode: row["PinCode"]?.ToString(),
                    GstNo: row["GstNo"]?.ToString());
            }
        }

        return new ErpDispatchFromAddress(null, null, null, null, null, null, null, null, null);
    }

    private async Task<(string InvoiceNumber, long InvoiceId)> PostInvoiceCreateAsync(
        JsonObject payload, CancellationToken cancellationToken)
    {
        var url = "/api/v1/sales/salesinvoice/create";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };

        request.Options.Set(DependencyInjection.NonRetryableKey, true);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("create Sales Invoice failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, responseBody);
            throw new InvoiceValidationException(
                "SaveInvoice",
                $"ERP rejected Sales Invoice creation with status {(int)response.StatusCode}: {responseBody}");
        }

        var resultObj = JsonSerializer.Deserialize<JsonObject>(responseBody, SerializerOptions);
        bool success = resultObj?["success"]?.GetValue<bool>() ?? false;
        string? message = resultObj?["message"]?.ToString();

        if (!success || string.IsNullOrWhiteSpace(message))
        {
            var errors = resultObj?["errors"]?.ToString() ?? resultObj?["errorMessage"]?.ToString();
            throw new InvoiceValidationException(
                "SaveInvoice",
                $"ERP reported Invoice creation failure: {errors ?? message ?? "Unknown validation failure"}");
        }

        // Expected format: sales_salesdispatch_salesinvoice_siCreated#{Year}/{Group}/{Site}/{InvoiceNo}#{InvoiceId}
        var tokens = message.Split('#');
        if (tokens.Length < 3)
        {
            // Alternative parsing if message contains the invoice ID
            return (tokens.Length > 1 ? tokens[1] : "CREATED", 0);
        }

        string docNoPart = tokens[1]; // e.g. "26-27/DS/CT2/000045"
        long invoiceId = long.TryParse(tokens[2], out var parsedId) ? parsedId : 0;
        string invoiceNo = docNoPart.Contains('/') ? docNoPart.Substring(docNoPart.LastIndexOf('/') + 1) : docNoPart;

        if (invoiceId <= 0)
        {
            throw new InvoiceValidationException(
                "SaveInvoice",
                $"ERP reported success but returned an invalid or zero invoice ID ({invoiceId}). Raw message: {message}");
        }

        return (invoiceNo, invoiceId);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ErpTransientException(
                $"ERP answered HTTP {(int)response.StatusCode} for {url}",
                $"{response.RequestMessage?.Method} {url} -> {(int)response.StatusCode}");
        }
    }

    private static string GetFiscalYear(DateTime date)
    {
        int startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        int endYear = startYear + 1;
        return $"{startYear % 100:D2}-{endYear % 100:D2}";
    }

    private sealed class ErpEnvelopeList<T>
    {
        public IReadOnlyList<T>? Data { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

internal static class StringExtensions
{
    public static string NullIsBlank(this string? s) => s ?? string.Empty;
}
