using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Walks the ERP's PO Entry sequence for an indent-based purchase order and returns the PO number.
/// </summary>
/// <remarks>
/// <para>
/// Nine calls, in the order the screen makes them — ten once a warehouse has to be resolved. The
/// screen fires roughly fifty, but the rest populate dropdowns or recompute figures the ERP is
/// asked for directly here; none of their responses reach the create payload.
/// </para>
/// <para>
/// The sequence is vendor-first, because that is how the ERP's pending-indent query is written. The
/// indent-first half of this class — discovery in <c>IndentDiscovery.cs</c>, vendor resolution in
/// <c>VendorResolver.cs</c>, conversion in <c>IndentConversion.cs</c> — puts the question the other
/// way round so that authorising an indent is enough to get a purchase order out of it.
/// </para>
/// <para>
/// It borrows the ERP <see cref="HttpClient"/> by name rather than implementing
/// <see cref="IErpClient"/>, so it inherits the auth handler and the resilience pipeline without
/// widening that interface — nothing else in the agent has a reason to know about purchase orders.
/// </para>
/// </remarks>
public sealed partial class IndentToPoService : IIndentToPoService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IErpTokenProvider _tokenProvider;
    private readonly ErpEndpointOptions _endpoints;
    private readonly IndentPoOptions _options;
    private readonly IClock _clock;

    /// <summary>
    /// Where the run, the execution and its stages are recorded. Injected rather than optional so
    /// there is one code path: on an installation with no automation database this is the no-op
    /// implementation, and the conversion behaves exactly as it always has.
    /// </summary>
    private readonly IIndentPoTracker _tracker;

    /// <summary>
    /// The PO Automation master switch. Consulted before every indent in a sweep — never once at the
    /// start — so a user turning automation off part-way through stops the run before the next
    /// conversion. A no-op on an installation with no automation database.
    /// </summary>
    private readonly IPoAutomationGate _poAutomationGate;

    private readonly ILogger<IndentToPoService> _logger;

    /// <summary>
    /// Rate structures the ERP has already priced this scope, keyed by the request that produced
    /// them. A sweep prices the same service at the same rate on indent after indent, and the ERP's
    /// answer cannot change while it runs.
    /// </summary>
    private readonly Dictionary<string, JsonArray> _rateComponentsByEndpoint = new(StringComparer.Ordinal);

    public IndentToPoService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        IOptions<ErpEndpointOptions> endpoints,
        IOptions<IndentPoOptions> options,
        IClock clock,
        IIndentPoTracker tracker,
        IPoAutomationGate poAutomationGate,
        ILogger<IndentToPoService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _tokenProvider = tokenProvider;
        _endpoints = endpoints.Value;
        _options = options.Value;
        _clock = clock;
        _tracker = tracker;
        _poAutomationGate = poAutomationGate;
        _logger = logger;
    }

    /// <summary>
    /// What one run of the PO Entry sequence is being asked to produce.
    /// </summary>
    /// <param name="SiteId">
    /// The site the order is raised from. It is a parameter rather than a setting because the ERP
    /// filters pending indent lines on <c>XINDHLOCID = @SiteId</c>: an indent-driven order has to
    /// use the indent's own site, or the ERP correctly answers that there is nothing to order.
    /// </param>
    /// <param name="RateStructureCode">Null means "ask the ERP for the vendor's default".</param>
    /// <param name="OnlyIndentId">
    /// Restricts the order to one indent's delivery lines, leaving the vendor's other outstanding
    /// lines where they are. Null orders everything outstanding, which is what the vendor-driven
    /// entry point has always done.
    /// </param>
    private sealed record PoBuildRequest(
        IndentPoProfile Profile,
        int SiteId,
        string VendorCode,
        string? RateStructureCode,
        long? OnlyIndentId);

    /// <inheritdoc />
    public async Task<IndentPoResult> CreateAsync(IndentPoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IndentType == IndentType.Service)
        {
            // The ERP has no vendor-first "what does this vendor have outstanding" question for
            // service indents that is not already scoped to one of them, so this entry point has no
            // service counterpart. Converting a named service indent, or sweeping for them, does.
            throw new ErpBusinessException(
                "Service indents are converted per indent, not per vendor. Use "
                + "/api/automation/indent-to-po/convert with an indentId, or let the sweep find them.",
                "CreateAsync is the vendor-driven material PO sequence; IndentType.Service has no "
                + "equivalent there.");
        }

        var profile = IndentPoProfile.For(request.IndentType);
        var vendorCode = ResolveVendorCode(request);

        return await RunSequenceAsync(
            new PoBuildRequest(
                profile,
                _options.LocationId,
                vendorCode,
                string.IsNullOrWhiteSpace(_options.RateStructureCode) ? null : _options.RateStructureCode.Trim(),
                OnlyIndentId: null),
            cancellationToken);
    }

    /// <summary>
    /// The ERP call sequence itself, shared by the vendor-driven and indent-driven entry points.
    /// </summary>
    private async Task<IndentPoResult> RunSequenceAsync(
        PoBuildRequest request,
        CancellationToken cancellationToken)
    {
        var poDate = DateOnly.FromDateTime(_clock.UtcNow.LocalDateTime);

        _logger.LogInformation(
            "Creating a {PoType} purchase order at site {SiteId} for vendor {VendorCode} dated {PoDate:yyyy-MM-dd}{IndentScope}",
            request.Profile.PoType,
            request.SiteId,
            request.VendorCode,
            poDate,
            request.OnlyIndentId is { } indentId ? $", limited to indent {indentId}" : string.Empty);

        var userId = await _tokenProvider.GetUserIdAsync(cancellationToken);

        // 1-2. Where the document is raised, and where it is delivered.
        await _tracker.EnterStageAsync(
            IndentPoStages.DocumentControl,
            IndentPoTasks.GetDefaultDocumentDetail,
            cancellationToken);

        var documentControl = await GetDocumentControlAsync(request.Profile, request.SiteId, poDate, cancellationToken);
        var address = await GetDeliveryAddressAsync(documentControl.LocationId, cancellationToken);

        // 3-4. Who it is raised on, and under which rate structure.
        await _tracker.EnterStageAsync(
            IndentPoStages.PendingLines,
            IndentPoTasks.GetVendorInformation,
            cancellationToken);

        var vendor = await GetVendorAsync(request.VendorCode, poDate, cancellationToken);
        var rateStructureCode = request.RateStructureCode
            ?? await ResolveRateStructureAsync(vendor, poDate, documentControl, cancellationToken);

        // 5. The tax component template every item's TaxDetails rows are built from.
        var rateStructureDetail = await GetJsonArrayAsync(
            _endpoints.RateStructureDetail(rateStructureCode),
            cancellationToken);

        // 6. The outstanding indent lines. This is the call that decides what the PO contains.
        var pending = await GetPendingItemsAsync(
            request.Profile, vendor, rateStructureCode, documentControl, poDate, userId, cancellationToken);

        // 7-8. Per item: the vendor's purchase terms, then the ERP's own pricing of the line.
        var lines = await BuildLinesAsync(
            pending,
            vendor,
            rateStructureCode,
            request.Profile,
            documentControl.LocationId,
            request.OnlyIndentId,
            cancellationToken);

        var context = new IndentPoContext
        {
            Profile = request.Profile,
            Options = _options,
            UserId = userId,
            PoDate = poDate,
            DocumentControl = documentControl,
            Address = address,
            Vendor = vendor,
            RateStructureCode = rateStructureCode,
            RateStructureDetail = rateStructureDetail,
            Lines = lines,
        };

        var payload = IndentPoPayloadBuilder.Build(context);

        // 9. Create.
        await _tracker.EnterStageAsync(
            IndentPoStages.CreatePurchaseOrder,
            IndentPoTasks.CreatePoEntry,
            cancellationToken);

        return await SubmitPurchaseOrderAsync(
            payload,
            vendor,
            lines.Count,
            rateStructureCode,
            cancellationToken);
    }

    private string ResolveVendorCode(IndentPoRequest request)
    {
        var vendorCode = string.IsNullOrWhiteSpace(request.VendorCode)
            ? _options.DefaultVendorCode
            : request.VendorCode.Trim();

        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            // Not an ERP failure, so it is raised as a business refusal rather than a transient one:
            // no amount of retrying supplies a vendor.
            throw new ErpBusinessException(
                "No vendor was given and no default vendor is configured, so there is nothing to raise the order on.",
                "IndentPoRequest.VendorCode was blank and AutomationAgent:PurchaseOrder:DefaultVendorCode is not set.");
        }

        return vendorCode;
    }

    /// <summary>
    /// Document Control's default row, and with it the financial year the order is raised in.
    /// </summary>
    /// <remarks>
    /// The year is tried rather than assumed. A year Document Control has no row for kills the
    /// conversion on its very first call, and the right year moves every April — so the configured
    /// year is tried first and the year the PO date falls in second, and whichever answers is the
    /// one the rest of the payload is stamped with. Leaving
    /// <c>AutomationAgent:PurchaseOrder:FinancialYear</c> blank means "always the PO date's year".
    /// </remarks>
    private async Task<DocumentControlDefaults> GetDocumentControlAsync(
        IndentPoProfile profile,
        int siteId,
        DateOnly poDate,
        CancellationToken cancellationToken)
    {
        var configured = _options.FinancialYear?.Trim() ?? string.Empty;
        var derived = FinancialYears.For(poDate);

        var candidates = configured.Length == 0
            ? [derived]
            : configured.Equals(derived, StringComparison.OrdinalIgnoreCase)
                ? new[] { configured }
                : [configured, derived];

        var attempts = new List<string>(candidates.Length);
        string? lastEndpoint = null;

        foreach (var financialYear in candidates)
        {
            var endpoint = _endpoints.DocumentControlDefault(
                financialYear,
                IndentPoProfile.DocType,
                profile.DocSubType,
                siteId);

            lastEndpoint = endpoint;

            JsonArray rows;
            try
            {
                rows = await GetJsonArrayAsync(endpoint, cancellationToken);
            }
            catch (ErpBusinessException ex)
            {
                // Refused rather than empty — an unconfigured year can arrive either way. Recorded
                // and carried into the final message, so nothing the ERP said is lost.
                attempts.Add($"{financialYear}: {ex.TechnicalMessage}");
                continue;
            }

            // The ERP returns every configured row and marks one default; anything else is a
            // misconfiguration a human has to fix.
            var candidateRow = rows.OfType<JsonObject>().FirstOrDefault(r => r.String("isDefault").Equals("Y", StringComparison.OrdinalIgnoreCase))
                ?? rows.OfType<JsonObject>().FirstOrDefault();

            if (candidateRow is null)
            {
                attempts.Add($"{financialYear}: '{endpoint}' returned no document control rows");
                continue;
            }

            if (!financialYear.Equals(configured, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Document Control has no {DocSubType} numbering for financial year '{ConfiguredYear}' at "
                    + "site {SiteId}; using '{FinancialYear}', the year the purchase order date falls in. "
                    + "Set AutomationAgent:PurchaseOrder:FinancialYear to '{FinancialYear}' (or blank, to "
                    + "always follow the PO date) to save a call per order.",
                    profile.DocSubType,
                    configured.Length == 0 ? "(not set)" : configured,
                    siteId,
                    financialYear,
                    financialYear);
            }

            return Read(financialYear, candidateRow);
        }

        throw new ErpBusinessException(
            $"The ERP has no document numbering set up for a {profile.DocSubType} purchase order at site "
            + $"{siteId} in financial year {string.Join(" or ", candidates)}, so no order can be numbered.",
            $"Document Control returned no usable default row. Tried {string.Join("; ", attempts)}. Set "
            + "AutomationAgent:PurchaseOrder:FinancialYear to the ERP's open Document Control year, or "
            + "leave it blank to follow the purchase order date.",
            lastEndpoint);

        DocumentControlDefaults Read(string financialYear, JsonObject row) => new(
            FinancialYear: financialYear,
            GroupCode: row.String("groupCode"),
            LocationId: row.Int("locationId", siteId),
            LocationCode: row.String("locationCode"),
            LocationName: row.String("locationName"),
            SiteRequired: YesNo(row, "isLocationRequired"),
            AutoNumberRequired: YesNo(row, "isAutoNumberGenerated"),
            AuthorisationRequired: YesNo(row, "isAutorisationRequired"));
    }

    private async Task<PoDeliveryAddress> GetDeliveryAddressAsync(int siteId, CancellationToken cancellationToken)
    {
        // OAF id is always zero: this flow never links one.
        var endpoint = _endpoints.PoAddress(0, siteId);
        var rows = await GetJsonArrayAsync(endpoint, cancellationToken);
        var row = rows.OfType<JsonObject>().FirstOrDefault();

        return new PoDeliveryAddress(
            Address1: row.String("add1"),
            Address2: row.String("add2"),
            Address3: row.String("add3"),
            CityCode: row.String("city"),
            PinCode: row.String("pinno"),
            StateCode: row.String("mclstate"),
            CountryCode: row.String("mclcountry"));
    }

    private async Task<VendorInformation> GetVendorAsync(
        string vendorCode,
        DateOnly poDate,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["vendorCode"] = vendorCode,
            ["flag"] = "V",
            ["poBasis"] = "I",
            ["poDate"] = poDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        var rows = await PostJsonArrayAsync(_endpoints.VendorInformation, body, cancellationToken);

        var row = rows.OfType<JsonObject>().FirstOrDefault()
            ?? throw new ErpBusinessException(
                $"Vendor '{vendorCode}' was not found in the ERP.",
                $"'{_endpoints.VendorInformation}' returned no rows for vendor '{vendorCode}'.",
                _endpoints.VendorInformation);

        var currency = row.String("pohcurcd", _options.Currency);

        if (!currency.Equals(_options.DomesticCurrency, StringComparison.OrdinalIgnoreCase))
        {
            // A foreign-currency PO needs an exchange rate and a foreign-tax split that this flow
            // does not compute. Refusing is honest; producing a PO valued at par would not be.
            throw new ErpBusinessException(
                $"Vendor '{vendorCode}' trades in {currency}. Automated purchase orders currently "
                + $"support {_options.DomesticCurrency} only.",
                $"Vendor currency '{currency}' differs from the configured domestic currency "
                + $"'{_options.DomesticCurrency}'; exchange-rate handling is not implemented.",
                _endpoints.VendorInformation);
        }

        return new VendorInformation(
            VendorCode: row.String("pohvndcode", vendorCode),
            VendorName: row.String("mvmname"),
            Currency: currency,
            // The screen seeds both the octroi and the warranty term code from the vendor's GSTIN.
            GstNumber: row.String("vengstno"),
            DeliveryCode: row.String("pohdlcd"),
            PaymentCode: row.String("pohpaycd"),
            InspectionCode: row.String("pohincd"),
            FreightCode: row.String("pohfrcd"),
            PackingCode: row.String("pohpkcd"),
            InsuranceCode: row.String("pohinscd"),
            DispatchMode: row.String("pohmod"),
            PaymentDays: row.Decimal("paymentdays"),
            // Only the Service PO reads these four. The octroi code is the vendor's real octroi
            // terms; the material header deliberately carries the GSTIN there instead, because
            // that is what its screen posts.
            OctroiCode: row.String("pohoctcd"),
            CountryCode: row.String("countrycode"),
            StateCode: row.String("vstatecode"),
            ContactName: row.String("pohvattnper"));
    }

    private async Task<string> ResolveRateStructureAsync(
        VendorInformation vendor,
        DateOnly poDate,
        DocumentControlDefaults documentControl,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["vendorCode"] = vendor.VendorCode,
            ["currencyCode"] = vendor.Currency,
            // The ERP's own spelling. Correcting it here would simply be ignored.
            ["rateStructrureCode"] = string.Empty,
            ["flag"] = "L",
            ["poBasis"] = "I",
            ["poDate"] = poDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["itemCode"] = string.Empty,
            ["site"] = documentControl.LocationId.ToString(CultureInfo.InvariantCulture),
        };

        var rows = await PostJsonArrayAsync(_endpoints.RateStructureLov, body, cancellationToken);

        var row = rows.OfType<JsonObject>().FirstOrDefault(r => r.Decimal("isDefault") != 0m
                || r.String("isDefault").Equals("true", StringComparison.OrdinalIgnoreCase))
            ?? rows.OfType<JsonObject>().FirstOrDefault()
            ?? throw new ErpBusinessException(
                $"Vendor '{vendor.VendorCode}' has no purchase rate structure configured.",
                $"'{_endpoints.RateStructureLov}' returned no rows for vendor '{vendor.VendorCode}'.",
                _endpoints.RateStructureLov);

        return row.String("rateStructureCode");
    }

    private async Task<JsonObject> GetPendingItemsAsync(
        IndentPoProfile profile,
        VendorInformation vendor,
        string rateStructureCode,
        DocumentControlDefaults documentControl,
        DateOnly poDate,
        int userId,
        CancellationToken cancellationToken)
    {
        // Lower-case keys are the ERP's, not a slip: this endpoint differs from its neighbours.
        var body = new JsonObject
        {
            ["vendorcode"] = vendor.VendorCode,
            ["currency"] = vendor.Currency,
            ["ratestructure"] = rateStructureCode,
            ["potype"] = profile.PoType,
            ["locationid"] = documentControl.LocationId,
            ["oafno"] = 0,
            ["ponumber"] = 0,
            ["type"] = "Y",
            ["podate"] = poDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["SJOSEL"] = string.Empty,
            ["OAFSEL"] = string.Empty,
            // "Any SJO" / "Any OAF" — the screen sends these literals unconditionally.
            ["strSJOType"] = "AS",
            ["strOAFType"] = "AO",
            ["IsInterBranch"] = false,
            ["companyId"] = _options.CompanyId,
            ["userId"] = userId,
            ["IsRcmGST"] = true,
            ["exsie"] = "Y",
            ["pobasic"] = "I",
            // No "default vendor only" filter, so every indent line this vendor can supply is offered.
            ["DefItemVendor"] = false,
            ["sonumber"] = 0,
            ["isbudgetso"] = false,
        };

        return await PostJsonObjectAsync(_endpoints.PendingItemsFromIndent, body, cancellationToken);
    }

    private async Task<IReadOnlyList<IndentPoLine>> BuildLinesAsync(
        JsonObject pending,
        VendorInformation vendor,
        string rateStructureCode,
        IndentPoProfile profile,
        int siteId,
        long? onlyIndentId,
        CancellationToken cancellationToken)
    {
        var items = pending["itemIndentmodel"].Objects().ToList();
        var deliveries = pending["indentdtlmodel"].Objects().ToList();

        if (onlyIndentId is { } indentId)
        {
            // The same narrowing the PO Entry screen performs in its "selected indent" mode: the
            // ERP hands back everything outstanding for the vendor and the operator picks. Matching
            // on pidindid rather than the printed indent number because that is the ERP's own key,
            // and it is what ends up in XPOITMDEL.PILINDID linking the order back to the indent.
            deliveries = deliveries.Where(row => row.Int("pidindid") == indentId).ToList();

            var itemCodes = deliveries
                .Select(row => row.String("xinditmcd"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            items = items.Where(item => itemCodes.Contains(item.String("itemCode"))).ToList();
        }

        if (items.Count == 0)
        {
            throw new ErpBusinessException(
                onlyIndentId is null
                    ? $"Vendor '{vendor.VendorCode}' has no pending indent items to order."
                    : $"Indent {onlyIndentId} has nothing left to order from vendor '{vendor.VendorCode}'.",
                $"'{_endpoints.PendingItemsFromIndent}' returned no items"
                + (onlyIndentId is null ? " at all." : $" for indent {onlyIndentId}."),
                _endpoints.PendingItemsFromIndent);
        }

        var lines = new List<IndentPoLine>(items.Count);

        foreach (var item in items)
        {
            var itemCode = item.String("itemCode");

            var itemDeliveries = deliveries
                .Where(row => row.String("xinditmcd").Equals(itemCode, StringComparison.OrdinalIgnoreCase)
                    && row.Decimal("xindiodqty") > 0m)
                .ToList();

            if (itemDeliveries.Count == 0)
            {
                // The item is offered but every indent line for it is already fully ordered.
                _logger.LogInformation(
                    "Skipping item {ItemCode}: no indent line has an outstanding quantity",
                    itemCode);
                continue;
            }

            if (!await EnsureWarehouseAsync(item, itemCode, profile, siteId, cancellationToken))
            {
                continue;
            }

            var vendorPuom = await GetItemVendorPuomAsync(
                item.String("puom"), itemCode, vendor, rateStructureCode, cancellationToken);

            var rateComponents = await GetRateComponentsAsync(
                item, vendorPuom, vendor, rateStructureCode, cancellationToken);

            lines.Add(new IndentPoLine(item, itemDeliveries, vendorPuom, rateComponents));
        }

        if (lines.Count == 0)
        {
            throw new ErpBusinessException(
                $"Nothing can be ordered from vendor '{vendor.VendorCode}': every pending indent line "
                + "is either already ordered in full or has no warehouse configured for its item.",
                $"'{_endpoints.PendingItemsFromIndent}' returned {items.Count} items, but none had "
                + "both an outstanding quantity and a warehouse to receive it into.",
                _endpoints.PendingItemsFromIndent);
        }

        return lines;
    }

    /// <summary>
    /// Makes sure the item names the warehouse its goods will be received into, resolving one from
    /// the ERP when the indent does not carry it. Returns false when the item cannot be ordered.
    /// </summary>
    /// <remarks>
    /// The pending-items query often answers with <c>whCode: ""</c> and <c>whId: 0</c> for items
    /// whose master has no default warehouse. The PO Entry screen handles exactly this: it fetches
    /// the warehouses valid for the item and picks the only one when there is only one, and its save
    /// validation refuses any line still lacking a warehouse. Posting <c>whId: 0</c> instead gets a
    /// raw foreign-key violation out of the ERP, so the same rule is applied here — resolve if
    /// possible, otherwise leave the item out and say so.
    /// </remarks>
    private async Task<bool> EnsureWarehouseAsync(
        JsonObject item,
        string itemCode,
        IndentPoProfile profile,
        int siteId,
        CancellationToken cancellationToken)
    {
        if (item.Int("whId") > 0 && !string.IsNullOrWhiteSpace(item.String("whCode")))
        {
            return true;
        }

        var body = new JsonObject
        {
            // The warehouse class selector, not the PO type: Capital draws on a different set.
            ["potype"] = profile.PoType == "C" ? "X" : "N",
            // The ERP scopes warehouses by site (@FMSITEID). It has to be the site the order is
            // raised from; the configured default would find nothing for an order raised anywhere
            // else, and report it as an item with no warehouse.
            ["locationId"] = siteId,
            ["flag"] = "L",
            ["companyId"] = _options.CompanyId,
            ["userId"] = await _tokenProvider.GetUserIdAsync(cancellationToken),
            ["poBase"] = "I",
            ["itemcode"] = itemCode,
            ["exciseFlag"] = "Y",
            ["IsRcmGST"] = true,
        };

        var warehouses = await PostJsonArrayAsync(_endpoints.WarehouseLov, body, cancellationToken);

        // Prefer the code the indent named, if it named one the ERP still offers; otherwise the
        // single valid warehouse, which is what the screen auto-selects.
        var wanted = item.String("whCode");

        var warehouse = warehouses.OfType<JsonObject>()
                .FirstOrDefault(row => !string.IsNullOrEmpty(wanted)
                    && row.String("whCode").Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? warehouses.OfType<JsonObject>().FirstOrDefault();

        if (warehouse is null)
        {
            _logger.LogWarning(
                "Skipping item {ItemCode}: it has no warehouse and the ERP offers none for it, so a "
                + "purchase order line cannot be received anywhere",
                itemCode);

            return false;
        }

        item["whCode"] = warehouse.String("whCode");
        item["whDesc"] = warehouse.String("desc");
        item["whId"] = warehouse.Int("id");

        _logger.LogInformation(
            "Item {ItemCode} carried no warehouse; using {WarehouseCode} from the ERP",
            itemCode,
            warehouse.String("whCode"));

        return true;
    }

    private async Task<JsonObject?> GetItemVendorPuomAsync(
        string purchaseUom,
        string itemCode,
        VendorInformation vendor,
        string rateStructureCode,
        CancellationToken cancellationToken)
    {
        // Mixed casing is the ERP's; copied exactly.
        var body = new JsonObject
        {
            ["PUOM"] = purchaseUom,
            ["vendorCode"] = vendor.VendorCode,
            ["currency"] = vendor.Currency,
            ["rateStructure"] = rateStructureCode,
            ["itemcode"] = itemCode,
            ["flag"] = "V",
        };

        var rows = await PostJsonArrayAsync(_endpoints.ItemVendorPuom, body, cancellationToken);
        var row = rows.OfType<JsonObject>().FirstOrDefault();

        if (row is null)
        {
            // Not fatal: the pending-items row already carries a rate and conversion factors. Only
            // the delivery lead time is lost, and the ERP recomputes that on its side.
            _logger.LogWarning(
                "Item {ItemCode} has no purchase row for vendor {VendorCode}; "
                + "falling back to the indent's own rate and conversion factors",
                itemCode,
                vendor.VendorCode);
        }

        return row;
    }

    /// <summary>
    /// Asks the ERP to price one line. The tax engine stays where it belongs — on the ERP — and the
    /// agent only multiplies the per-unit figures by quantity.
    /// </summary>
    private async Task<JsonArray> GetRateComponentsAsync(
        JsonObject item,
        JsonObject? vendorPuom,
        VendorInformation vendor,
        string rateStructureCode,
        CancellationToken cancellationToken)
    {
        var rate = vendorPuom is null
            ? item.Decimal("purchaseRate")
            : vendorPuom.Decimal("pidbpurrt", item.Decimal("purchaseRate"));

        var discountType = vendorPuom is null
            ? item.String("discountType", "None")
            : vendorPuom.String("piddisctyp", item.String("discountType", "None"));

        var discountValue = vendorPuom is null
            ? item.Decimal("discountValue")
            : vendorPuom.Decimal("piddiscval", item.Decimal("discountValue"));

        var discounted = discountType.Equals("Percentage", StringComparison.OrdinalIgnoreCase)
            ? rate - (rate * discountValue / 100m)
            : rate - discountValue;

        return await GetRateComponentsForAsync(discounted, rateStructureCode, vendor.Currency, cancellationToken);
    }

    /// <summary>
    /// Asks the ERP what one basic rate costs under a rate structure, component by component.
    /// </summary>
    /// <remarks>
    /// Shared by the material and service flows, which differ only in where the discounted rate
    /// comes from. Answers are memoised for the life of the scope: a sweep prices the same service
    /// at the same rate on indent after indent, and the ERP's answer cannot change while it runs.
    /// </remarks>
    private async Task<JsonArray> GetRateComponentsForAsync(
        decimal discountedRate,
        string rateStructureCode,
        string currency,
        CancellationToken cancellationToken)
    {
        var basicPrice = discountedRate.ToString(CultureInfo.InvariantCulture);
        var endpoint = _endpoints.RateStructureCalculation(rateStructureCode, basicPrice, currency);

        if (_rateComponentsByEndpoint.TryGetValue(endpoint, out var cached))
        {
            return (JsonArray)cached.DeepClone();
        }

        var components = await GetJsonArrayAsync(endpoint, cancellationToken);
        _rateComponentsByEndpoint[endpoint] = components;

        return (JsonArray)components.DeepClone();
    }


    private async Task<IndentPoResult> SubmitPurchaseOrderAsync(
        JsonObject payload,
        VendorInformation vendor,
        int itemCount,
        string rateStructureCode,
        CancellationToken cancellationToken)
    {
        // Not retried at the transport level: a replayed create would skip the fresh pending-items
        // re-check RunSequenceAsync just did, and could raise a second, real purchase order for an
        // indent the first (successful but seemingly-failed) attempt already consumed. A genuine
        // transient failure here surfaces once and is recovered by a full re-attempt instead.
        var created = await PostJsonObjectAsync(
            _endpoints.CreatePurchaseOrder, payload, cancellationToken, retryable: false);

        var poNumber = created.String("documentNo");

        if (string.IsNullOrWhiteSpace(poNumber))
        {
            throw new ErpTransientException(
                "The ERP accepted the purchase order but did not return its number.",
                $"'{_endpoints.CreatePurchaseOrder}' reported success with no data.documentNo.",
                _endpoints.CreatePurchaseOrder);
        }

        var poId = (long)created.Decimal("documentID");

        _logger.LogInformation(
            "Created purchase order {PoNumber} (id {PoId}) for vendor {VendorCode} with {ItemCount} item(s)",
            poNumber,
            poId,
            vendor.VendorCode,
            itemCount);

        return new IndentPoResult(
            poNumber,
            poId,
            vendor.VendorCode,
            itemCount,
            vendor.Currency,
            rateStructureCode);
    }

    private static string YesNo(JsonObject row, string property)
    {
        var raw = row.String(property);

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("Y", StringComparison.OrdinalIgnoreCase)
            ? "Y"
            : "N";
    }

    private async Task<JsonArray> GetJsonArrayAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private async Task<JsonObject> GetJsonObjectAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonObject>(response, endpoint, cancellationToken);
    }

    private async Task<JsonArray> PostJsonArrayAsync(
        string endpoint,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(endpoint, body);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private async Task<JsonObject> PostJsonObjectAsync(
        string endpoint,
        JsonObject body,
        CancellationToken cancellationToken,
        bool retryable = true)
    {
        using var request = CreateJsonRequest(endpoint, body, retryable);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonObject>(response, endpoint, cancellationToken);
    }

    private static HttpRequestMessage CreateJsonRequest(string endpoint, JsonObject body, bool retryable = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        if (!retryable)
        {
            request.Options.Set(DependencyInjection.NonRetryableKey, true);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // ExecutionRejectedException covers Polly's own refusals - the attempt timeout firing and
        // the circuit breaker being open. Neither derives from the exception types above, so without
        // this they would escape as an unhandled 500 instead of the 503 they actually are.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
            or Polly.ExecutionRejectedException)
        {
            throw new ErpTransientException(
                "The ERP could not be reached. Please try again shortly.",
                $"'{request.RequestUri}' failed: {ex.Message}",
                request.RequestUri?.ToString(),
                ex);
        }
    }
}
