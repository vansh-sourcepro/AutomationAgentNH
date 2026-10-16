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

    public DcToInvoiceService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        DcToInvoiceOptions options,
        ILogger<DcToInvoiceService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
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
            // If customer was empty, try resolving from any pending DC matching dcId or dcNo
            if (string.IsNullOrWhiteSpace(effectiveCustomerCode))
            {
                matchedDc = pendingDcs.FirstOrDefault();
            }

            if (matchedDc == null)
            {
                throw new InvoiceValidationException(
                    "DeliveryChallan",
                    $"Delivery Challan (Id: {dcId}, No: '{dcNo}') was not found in the list of eligible, " +
                    $"authorized, pending Delivery Challans for customer '{effectiveCustomerCode}' as of {formattedDate}. " +
                    "It may already be fully invoiced, unauthorized, or belong to a different site/currency.");
            }
        }

        effectiveCustomerCode = matchedDc.BillTo;
        var effectiveDcId = matchedDc.DcId;
        var effectiveDcNo = matchedDc.DcNo;
        var effectiveSiteCode = !string.IsNullOrWhiteSpace(docControl.LocationCode) ? docControl.LocationCode : matchedDc.DcSite;

        _logger.LogInformation(
            "DcToInvoice: Matched DC. DcId={DcId}, DcNo={DcNo}, Customer={Cust}, OrderId={OrdId}, OafId={OafId}",
            effectiveDcId, effectiveDcNo, effectiveCustomerCode, matchedDc.OrderId, matchedDc.OafId);

        // ── Step 5: Fetch DC Shipment Logistics Details ──
        var logistics = await GetDcShipmentLogisticsAsync(effectiveDcId, cancellationToken);

        // ── Step 6: Fetch Shipment Details & Place of Supply (Select first/top valid option) ──
        var shipment = await GetShipmentAsync(effectiveCustomerCode, matchedDc.ShipCode, matchedDc.OrderId, cancellationToken);

        // ── Step 7: Rate Structure Resolution (Respecting MSCENABLELINELEVELRTSTR) ──
        string headerRateStructureCode = string.Empty;
        if (!isLineLevelRateStructure)
        {
            // Case A: MSCENABLELINELEVELRTSTR = 0 -> Rate Structure is at Invoice Header level.
            // Fetch available rate structure options and select the first/top valid option.
            var availableRateStructures = await FetchDcRateStructuresAsync(
                effectiveDcId, effectiveSiteCode, effectiveCustomerCode, cancellationToken);

            if (availableRateStructures.Count > 0)
            {
                // Select first/top valid option in the order returned by the application
                headerRateStructureCode = availableRateStructures[0].RateStructureCode;
            }
            else if (!string.IsNullOrWhiteSpace(matchedDc.DcRateStructureCode))
            {
                headerRateStructureCode = matchedDc.DcRateStructureCode;
            }
            else
            {
                throw new InvoiceValidationException(
                    "RateStructure",
                    $"No valid Rate Structure could be resolved for Delivery Challan {effectiveDcId} at header level.");
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

                validItems.Add(item with { RateStructureCode = itemRateStru });
            }
            else
            {
                // Header level: all items inherit the selected header rate structure
                validItems.Add(item with { RateStructureCode = headerRateStructureCode });
            }
        }

        if (validItems.Count == 0)
        {
            throw new InvoiceValidationException(
                "InvoiceItems",
                $"Delivery Challan {effectiveDcId} has no items with pending invoice quantity greater than 0.");
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

        return new InvoiceCreationResult(
            InvoiceNumber: createResponse.InvoiceNumber,
            InvoiceId: createResponse.InvoiceId,
            DcId: effectiveDcId,
            DcNo: effectiveDcNo,
            CustomerCode: effectiveCustomerCode,
            ItemCount: validItems.Count,
            TotalInvoiceQty: validItems.Sum(i => i.InvoiceQty),
            TotalInvoiceAmount: validItems.Sum(i => i.AmountWithTax),
            Status: "Created",
            Authorized: string.Equals(docControl.AuthorisationRequired, "N", StringComparison.OrdinalIgnoreCase),
            RateStructureCode: headerRateStructureCode,
            LineLevelRateStructureApplied: isLineLevelRateStructure);
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

                if (string.IsNullOrWhiteSpace(item.DcRndNo))
                    errors.Add(new InvoiceValidationError("DcDetailLineId", $"{lineIdentifier} is missing Delivery Challan Line Identifier (dcRndNo)."));

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
                ["XIHDOCTYP"] = _options.DocType,
                ["XIHSUBTYP"] = _options.DocSubType,
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
                ["XIHATAMT"] = totalTaxAmount,
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
                ["XIHTMPNOREQ"] = "N",
                ["XIHAUTOID"] = "",
                ["XIHHISINTERBRANCH"] = false,
                ["XIHDIFFVALUE"] = 0.00m,
                ["XIHSHPCD"] = dc.ShipCode,
                ["XIHBILLTO"] = dc.BillTo,
                ["XIHINVLVLDISPER"] = 0.00m,
                ["XIHINVLVLDISAMT"] = 0.00m,
                ["XIHDISCTYP"] = "",
                ["STRAUTHREQ"] = docControl.AuthorisationRequired ?? "Y",
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
                ["XIHDOCTYP"] = _options.DocType,
                ["XIHSUBTYP"] = _options.DocSubType,
                ["ConvFact"] = item.ConvFact.ToString("0.0000", CultureInfo.InvariantCulture),
                ["XIDAMDSRNO"] = "0",
                ["XIDOAFID"] = item.OafId.ToString(),
                ["XIDOAFLINEID"] = item.OafDetailLineNo.ToString(),
                ["XIDCOMMAMT"] = "0.00",
                ["XIDSITMNAME"] = item.SalesItemName,
                ["XIDNETWGHT"] = item.NetWeight.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDGROSSWGHT"] = item.GrossWeight.ToString("0.00", CultureInfo.InvariantCulture),
                ["XIDCASESIZE"] = item.CaseSize ?? "",
                ["InvHsnNum"] = item.HsnCode ?? "",
                ["SOHsnNum"] = item.SoHsnCode ?? item.HsnCode ?? "",
                ["XIDSJOID"] = item.SjoId.ToString(),
                ["XIDRTSCD"] = appliedRateStru,
                ["xdcsoitmlineno"] = item.SoItmLineNo,
                ["XIDSOCNGRATE"] = 0.00m,
                ["XIDDISCTYP"] = item.DiscountType ?? "",
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

        // Rate structure / Tax lines
        var rateDtl = new JsonArray();
        for (int i = 0; i < taxLines.Count; i++)
        {
            var tax = taxLines[i];
            rateDtl.Add(new JsonObject
            {
                ["XDTDCATTYPE"] = _options.DocType,
                ["XDTDSUBTYPE"] = _options.DocSubType,
                ["XDTFRLOC"] = siteId,
                ["XDTDTMCD"] = tax.ItemCode,
                ["XDTDRATECD"] = tax.RateCode,
                ["RateDesc"] = tax.RateDesc,
                ["XDTDRATEAMT"] = tax.RateAmount,
                ["XDTDPERCVAL"] = tax.TaxValue,
                ["XDTDINCEXC"] = tax.Ie,
                ["XDTDPERVAL"] = tax.Pv,
                ["XDTDAPPON"] = tax.AppOnDisplay,
                ["XDTDPNYN"] = tax.PostNonPost,
                ["XDTDRTSTRCD"] = tax.RateStructureCode ?? headerRateStructureCode,
                ["XDTDSEQNO"] = tax.SeqNo > 0 ? tax.SeqNo : (i + 1),
                ["MPRTAXTYP"] = tax.MprTaxTyp
            });
        }

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

        return new JsonObject
        {
            ["dtInvHdr"] = dtInvHdr,
            ["dtInvDtl"] = dtInvDtl,
            ["dtDCINVRef"] = dtDCINVRef,
            ["RateDtl"] = rateDtl,
            ["dtInvGRNDtl"] = new JsonArray(),
            ["dtInvMiscDtl"] = new JsonArray(),
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
        var url = $"/api/v1/admin/documentControl/getDefaultDocumentDetail/{Uri.EscapeDataString(year)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(subType)}/{locationId}";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
            if (envelope?.Data != null && envelope.Data.Count > 0)
            {
                var row = envelope.Data[0];
                return new ErpInvoiceDocControlDefault(
                    FinancialYear: year,
                    GroupCode: row["groupCode"]?.ToString() ?? "SI",
                    LocationCode: row["locationCode"]?.ToString() ?? "CT2",
                    LocationName: row["locationName"]?.ToString() ?? "CTL SITE-2",
                    LocationId: row["locationId"]?.GetValue<int>() ?? locationId,
                    SiteRequired: row["isLocationRequired"]?.GetValue<bool>() == true ? "Y" : "N",
                    AutoNumberRequired: row["isAutoNumberGenerated"]?.GetValue<bool>() == true ? "Y" : "N",
                    AuthorisationRequired: row["isAutorisationRequired"]?.GetValue<bool>() == true ? "Y" : "N");
            }
        }

        return new ErpInvoiceDocControlDefault(
            FinancialYear: year,
            GroupCode: "SI",
            LocationCode: _options.SiteCode,
            LocationName: "CTL SITE-2",
            LocationId: locationId,
            SiteRequired: "Y",
            AutoNumberRequired: "Y",
            AuthorisationRequired: "Y");
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

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
        if (envelope?.Data == null || envelope.Data.Count == 0)
        {
            return Array.Empty<ErpSalInvDcRow>();
        }

        var results = new List<ErpSalInvDcRow>();
        foreach (var obj in envelope.Data)
        {
            var dcIdVal = obj["dcid"] ?? obj["DCID"];
            if (dcIdVal == null) continue;

            long id = Convert.ToInt64(dcIdVal.ToString(), CultureInfo.InvariantCulture);
            results.Add(new ErpSalInvDcRow(
                DcId: id,
                DcNo: obj["dcNo"]?.ToString() ?? string.Empty,
                DcDate: obj["dcDate"] != null ? DateTime.Parse(obj["dcDate"]!.ToString()) : DateTime.Today,
                DcYear: obj["dcYear"]?.ToString() ?? string.Empty,
                DcGroup: obj["dcGroup"]?.ToString() ?? string.Empty,
                DcSite: obj["dcSite"]?.ToString() ?? string.Empty,
                OafNo: obj["oafNo"]?.ToString() ?? string.Empty,
                OafYear: obj["oafYear"]?.ToString() ?? string.Empty,
                OafGroup: obj["oafGroup"]?.ToString() ?? string.Empty,
                OafSite: obj["oafSite"]?.ToString() ?? string.Empty,
                OafId: obj["xoafhautoid"] != null ? Convert.ToInt32(obj["xoafhautoid"]!.ToString()) : 0,
                OrderId: obj["ordid"] != null ? Convert.ToInt64(obj["ordid"]!.ToString()) : 0,
                IntOdrNo: obj["intOdrNo"]?.ToString() ?? string.Empty,
                IntOdrYear: obj["intOdrYear"]?.ToString() ?? string.Empty,
                IntOdrGroup: obj["intOdrGroup"]?.ToString() ?? string.Empty,
                IntOdrSite: obj["intOdrSite"]?.ToString() ?? string.Empty,
                CustPoNo: obj["custPOno"]?.ToString() ?? string.Empty,
                CustPoDate: obj["custPOdate"] != null ? DateTime.Parse(obj["custPOdate"]!.ToString()) : null,
                ShipCode: obj["shipCode"]?.ToString() ?? string.Empty,
                BillTo: obj["billTo"]?.ToString() ?? customerCode,
                BillToName: obj["billToName"]?.ToString() ?? string.Empty,
                CurrencyCode: obj["xobcurrcd"]?.ToString() ?? currency,
                ExchangeRate: obj["exchangeRate"] != null ? Convert.ToDecimal(obj["exchangeRate"]!.ToString(), CultureInfo.InvariantCulture) : 1.0m,
                DcRateStructureCode: obj["xdcrtstrcd"]?.ToString(),
                DeliveryName: obj["deliveryName"]?.ToString(),
                DeliveryCityCode: obj["deliveryCityCode"]?.ToString(),
                DeliveryCityName: obj["deliveryCityName"]?.ToString(),
                CreditDays: obj["creditDays"] != null ? Convert.ToInt32(obj["creditDays"]!.ToString()) : 30,
                CreditLimit: obj["creditLimit"] != null ? Convert.ToDecimal(obj["creditLimit"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                CrLimitInvFlg: obj["crLimitInvflg"]?.ToString() ?? "W",
                CreditLimitFlag: obj["creditLimitflag"]?.ToString() ?? "A",
                CreditLimitDays: obj["creditLimitdays"] != null ? Convert.ToInt32(obj["creditLimitdays"]!.ToString()) : 0
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
        var payload = new JsonObject
        {
            ["strFlag"] = "DC",
            ["strDCId"] = dcId.ToString(),
            ["strRtStruCd"] = "",
            ["strExNonEx"] = "E",
            ["strInvOf"] = "P",
            ["IsGSTApplicable"] = true,
            ["site"] = siteCode,
            ["custcode"] = custCode,
            ["isDiffCustBillTo"] = false,
            ["itemCode"] = ""
        };

        var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(SerializerOptions, cancellationToken);
            if (envelope?.Data != null && envelope.Data.Count > 0)
            {
                return envelope.Data.Select(d => new ErpRateStructureOption(
                    RateStructureCode: d["rateStructureCode"]?.ToString() ?? string.Empty,
                    RateStructureDescription: d["rateStructureDescription"]?.ToString() ?? string.Empty,
                    RateStructureFullName: d["rateStructureFullName"]?.ToString() ?? string.Empty,
                    IsGSTApplicable: d["isGSTApplicable"]?.GetValue<bool>() ?? true
                )).Where(r => !string.IsNullOrWhiteSpace(r.RateStructureCode)).ToList();
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
        var encodedRate = string.IsNullOrWhiteSpace(rateStructureCode) ? "%20" : Uri.EscapeDataString(rateStructureCode.Trim());
        var encodedCurr = string.IsNullOrWhiteSpace(currency) ? "%20" : Uri.EscapeDataString(currency.Trim());
        var url = $"/api/v1/sales/salesinvoice/FetchItemDtls/{encodedRate}/{dcId}/{encodedCurr}/E/true/{orderId}/%20/false/{Uri.EscapeDataString(siteCode)}?strOAFID={oafId}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(SerializerOptions, cancellationToken);
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
                    ConvFact: itemNode["convFact"] != null ? Convert.ToDecimal(itemNode["convFact"]!.ToString(), CultureInfo.InvariantCulture) : 1.0m,
                    DcQty: itemNode["dcQty"] != null ? Convert.ToDecimal(itemNode["dcQty"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    InvoiceQty: itemNode["invoiceQty"] != null ? Convert.ToDecimal(itemNode["invoiceQty"]!.ToString(), CultureInfo.InvariantCulture) : (itemNode["dcQty"] != null ? Convert.ToDecimal(itemNode["dcQty"]!.ToString(), CultureInfo.InvariantCulture) : 0m),
                    OrdRateDis: itemNode["ordRateDis"] != null ? Convert.ToDecimal(itemNode["ordRateDis"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    OrderRate: itemNode["orderRate"] != null ? Convert.ToDecimal(itemNode["orderRate"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    AssessableRate: itemNode["assessableRate"] != null ? Convert.ToDecimal(itemNode["assessableRate"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    Amount: itemNode["amount"] != null ? Convert.ToDecimal(itemNode["amount"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    AmountWithTax: itemNode["amountWithTax"] != null ? Convert.ToDecimal(itemNode["amountWithTax"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    OafId: itemNode["oafId"] != null ? Convert.ToInt32(itemNode["oafId"]!.ToString()) : oafId,
                    OafDetailLineNo: itemNode["oafDetailLineNo"] != null ? Convert.ToInt32(itemNode["oafDetailLineNo"]!.ToString()) : 1,
                    DcoafLineNo: itemNode["dcoaflineno"] != null ? Convert.ToInt32(itemNode["dcoaflineno"]!.ToString()) : 1,
                    DcRndNo: itemNode["dcRndNo"]?.ToString() ?? $"{dcId}1",
                    DcId: dcId,
                    OrdId: orderId,
                    SjoId: itemNode["sjoId"] != null ? Convert.ToInt64(itemNode["sjoId"]!.ToString()) : 0,
                    SoItmLineNo: itemNode["soItmLineNo"] != null ? Convert.ToInt32(itemNode["soItmLineNo"]!.ToString()) : 1,
                    HsnCode: itemNode["hsnCode"]?.ToString(),
                    SoHsnCode: itemNode["soHsnCode"]?.ToString(),
                    RateStructureCode: itemNode["ratestructurecd"]?.ToString(),
                    NetWeight: itemNode["netWeight"] != null ? Convert.ToDecimal(itemNode["netWeight"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    GrossWeight: itemNode["grossWeight"] != null ? Convert.ToDecimal(itemNode["grossWeight"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    CaseSize: itemNode["caseSize"]?.ToString(),
                    ItemSize: itemNode["itemSize"]?.ToString(),
                    ItemWeight: itemNode["itemWeight"] != null ? Convert.ToDecimal(itemNode["itemWeight"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    DiscountType: itemNode["discountType"]?.ToString(),
                    DiscountVal: itemNode["discountVal"] != null ? Convert.ToDecimal(itemNode["discountVal"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    DiscountAmt: itemNode["discountAmt"] != null ? Convert.ToDecimal(itemNode["discountAmt"]!.ToString(), CultureInfo.InvariantCulture) : 0m
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
                taxesList.Add(new ErpInvoiceRateStructureLine(
                    RateCode: taxNode["rateCode"]?.ToString() ?? string.Empty,
                    RateDesc: taxNode["rateDesc"]?.ToString() ?? string.Empty,
                    TaxValue: taxNode["taxValue"] != null ? Convert.ToDecimal(taxNode["taxValue"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    AppOnDisplay: taxNode["appOnDisplay"]?.ToString() ?? "Gross Value",
                    Ie: taxNode["ie"]?.ToString() ?? "I",
                    Pv: taxNode["pv"]?.ToString() ?? "P",
                    MprTaxTyp: taxNode["mprtaxtyp"]?.ToString() ?? "C",
                    RateAmount: taxNode["rateAmount"] != null ? Convert.ToDecimal(taxNode["rateAmount"]!.ToString(), CultureInfo.InvariantCulture) : 0m,
                    PostNonPost: taxNode["postNonPost"] != null ? Convert.ToInt32(taxNode["postNonPost"]!.ToString()) : 1,
                    UniqueNo: taxNode["uniqueno"] != null ? Convert.ToInt32(taxNode["uniqueno"]!.ToString()) : 0,
                    ItemCode: taxNode["itemCode"]?.ToString() ?? string.Empty,
                    SeqNo: taxNode["seqNo"] != null ? Convert.ToInt32(taxNode["seqNo"]!.ToString()) : seq++,
                    RateStructureCode: taxNode["ratestructurecd"]?.ToString() ?? rateStructureCode
                ));
            }
        }

        return (itemsList, taxesList);
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
