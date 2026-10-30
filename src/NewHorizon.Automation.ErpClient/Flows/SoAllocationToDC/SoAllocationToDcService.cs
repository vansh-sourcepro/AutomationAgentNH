using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

/// <summary>
/// Walks the ERP's Delivery Challan entry sequence for a completed Sales Allocation and
/// calls the ERP's own <c>createDCEntry</c> endpoint â€” the same call the UI makes â€”
/// returning the generated DC number and DC id.
/// </summary>
/// <remarks>
/// <para>
/// This is an automation bridge, not a reimplementation. Every field value comes from the
/// same ERP APIs the DC entry screen calls; every calculation stays in the ERP's own layer.
/// The agent's only contribution is the orchestration.
/// </para>
/// <para>
/// It borrows the named ERP <see cref="HttpClient"/> so it inherits auth and resilience
/// without touching <see cref="IErpClient"/> â€” the same approach as IndentToPoService.
/// </para>
/// </remarks>
public sealed partial class SoAllocationToDcService : ISoAllocationToDcService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IErpTokenProvider _tokenProvider;
    private readonly SoAllocationToDcOptions _options;
    private readonly ILogger<SoAllocationToDcService> _logger;

    public SoAllocationToDcService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        SoAllocationToDcOptions options,
        ILogger<SoAllocationToDcService> logger)
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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  Public entry point
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc />
    /// <inheritdoc />
    public Task<DcCreationResult> CreateDeliveryChallanFromAllocationAsync(
        int oafId,
        CancellationToken cancellationToken)
        => CreateDeliveryChallanFromAllocationAsync(oafId, null, null, null, cancellationToken);

    /// <inheritdoc />
    public async Task<DcCreationResult> CreateDeliveryChallanFromAllocationAsync(
        int? oafId,
        string? oafNo = null,
        string? intOrdNo = null,
        string? customerCode = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "SoAllocationToDC: CreateDeliveryChallanFromAllocation called. OafId={OafId}, OafNo={OafNo}, IntOrdNo={IntOrdNo}, Cust={Cust}",
            oafId, oafNo, intOrdNo, customerCode);

        // ── Step 1: Customer code from request or dynamic discovery ──
        var customerCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(customerCode))
        {
            customerCandidates.Add(customerCode.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(_options.CustomerCode))
        {
            customerCandidates.Add(_options.CustomerCode.Trim());
        }
        else
        {
            var siteId = _options.SiteId;
            var allocCustomers = await GetCustomersForAllocationAsync(siteId, cancellationToken);
            foreach (var c in allocCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c.CustCode) &&
                    !customerCandidates.Contains(c.CustCode, StringComparer.OrdinalIgnoreCase))
                {
                    customerCandidates.Add(c.CustCode);
                }
            }

            var dcCustomers = await GetCustomersForDcAsync(siteId, cancellationToken);
            foreach (var c in dcCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c) &&
                    !customerCandidates.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    customerCandidates.Add(c);
                }
            }
        }

        if (customerCandidates.Count == 0)
        {
            throw new DcValidationException(
                "CustomerCode",
                "No customers found to inspect for the specified OAF allocation.");
        }

        // ── Step 2: Shipment & OAF matching ──────────────────────
        string? resolvedCustomerCode = null;
        ErpShipment? shipment = null;
        ErpOafDetail? matchingOaf = null;

        var allCandidateOafs = new List<(string CustomerCode, ErpShipment Shipment, ErpOafDetail Oaf)>();

        foreach (var candidateCust in customerCandidates)
        {
            var shipments = await GetShipmentsAsync(candidateCust, cancellationToken);
            foreach (var candidateShipment in shipments)
            {
                var oafList = await GetOafDetailListAsync(
                    candidateCust, candidateShipment.ShipmentCode, cancellationToken);

                foreach (var oaf in oafList)
                {
                    allCandidateOafs.Add((candidateCust, candidateShipment, oaf));
                }
            }
        }

        // 1. If oafId is provided, try exact match on oafAutoId
        if (oafId.HasValue && oafId.Value > 0)
        {
            var matchById = allCandidateOafs.FirstOrDefault(x => x.Oaf.OafAutoId == oafId.Value);
            if (matchById != default)
            {
                (resolvedCustomerCode, shipment, matchingOaf) = matchById;
            }
        }
        // 2. If both oafNo and intOrdNo are provided, match both
        else if (!string.IsNullOrWhiteSpace(oafNo) && !string.IsNullOrWhiteSpace(intOrdNo))
        {
            var matchBoth = allCandidateOafs.FirstOrDefault(x =>
                MatchesDocNumber(x.Oaf.OafNumber, oafNo) &&
                MatchesDocNumber(x.Oaf.IntOrderNumber, intOrdNo));

            if (matchBoth != default)
            {
                (resolvedCustomerCode, shipment, matchingOaf) = matchBoth;
            }
        }
        // 3. If only oafNo is provided, match by OafNumber
        else if (!string.IsNullOrWhiteSpace(oafNo))
        {
            var matchByOafNo = allCandidateOafs.FirstOrDefault(x =>
                MatchesDocNumber(x.Oaf.OafNumber, oafNo));

            if (matchByOafNo != default)
            {
                (resolvedCustomerCode, shipment, matchingOaf) = matchByOafNo;
            }
        }
        // 4. If only intOrdNo is provided, match by IntOrderNumber
        else if (!string.IsNullOrWhiteSpace(intOrdNo))
        {
            var matchByIntOrdNo = allCandidateOafs.FirstOrDefault(x =>
                MatchesDocNumber(x.Oaf.IntOrderNumber, intOrdNo));

            if (matchByIntOrdNo != default)
            {
                (resolvedCustomerCode, shipment, matchingOaf) = matchByIntOrdNo;
            }
        }

        if (matchingOaf is null || shipment is null || resolvedCustomerCode is null)
        {
            var searchDesc = new List<string>();
            if (oafId.HasValue && oafId.Value > 0) searchDesc.Add($"OAF ID {oafId.Value}");
            if (!string.IsNullOrWhiteSpace(oafNo)) searchDesc.Add($"OAF No '{oafNo}'");
            if (!string.IsNullOrWhiteSpace(intOrdNo)) searchDesc.Add($"Internal Order No '{intOrdNo}'");

            var requestedStr = searchDesc.Count > 0 ? string.Join(", ", searchDesc) : "Requested OAF";

            throw new DcValidationException(
                "OAF",
                $"{requestedStr} was not found in any shipment's DC OAF list across candidate customers. " +
                "Verify that the OAF belongs to one of these customers and has pending items for Delivery Challan.");
        }

        var effectiveCustomerCode = resolvedCustomerCode;
        var resolvedOafId = matchingOaf.OafAutoId;

        _logger.LogInformation(
            "SoAllocationToDC: Matched OAF {OafNumber} (id={OafId}, intOrder={IntOrder}) ─ resolved Shipment = {ShipCode}",
            matchingOaf.OafNumber, resolvedOafId, matchingOaf.IntOrderNumber, shipment.ShipmentCode);

        // ── Step 4: OAF item details — the DC-side view of dispatchable items ───────
        var oafItemDetails = await GetOafItemDetailsAsync(
            effectiveCustomerCode,
            shipment.ShipmentCode,
            matchingOaf.OafYear,
            matchingOaf.OafGroup,
            matchingOaf.OafNumber,
            cancellationToken);

        if (oafItemDetails.Count == 0)
        {
            throw new DcValidationException(
                "OAFItemDetails",
                $"No dispatchable item details returned for OAF {matchingOaf.OafNumber} (id {resolvedOafId}). " +
                "The OAF may already be fully dispatched or may have no items.");
        }

        // ── Step 5: Build allocation rows from OAF item details ─────────────────────
        // The ERP's getOAFItemDetailsForDC returns allocationID for each item, giving us
        // both the allocation reference and the exact OAF qty per item.
        // We treat every OAF item that has an allocationID > 0 as an allocated item.
        var allocationRows = oafItemDetails
            .Where(i => i.MaxDcQtyIuom > 0)
            .Select(i => {
                _logger.LogInformation("Item {Code}: OafQty={OafQty}, MaxDc={MaxDc}, AlreadyDc={Already}, AllocId={AllocId}", 
                    i.SalesItemCode, i.OafQty, i.MaxDcQtyIuom, i.AlreadyDcQty, i.AllocationId);
                return new ErpAllocationRow(
                OafId: i.OafId,
                ItemCode: i.IntItemCode,
                SalesItemCode: i.SalesItemCode,
                AllocatedQty: i.MaxDcQtyIuom,
                OafDetailLineId: i.OafDetailLineId,
                OafDetailLineNo: i.OafDetailLineNo,
                SoItemLineNo: i.SoItemLineNo,
                AllocationId: i.AllocationId,
                WareHouseId: i.WareHouseId,
                WarehouseCode: i.WarehouseCode);
            })
            .ToList();

        if (allocationRows.Count == 0)
        {
            throw new DcValidationException(
                "Allocation",
                $"No allocated items found for OAF {matchingOaf.OafNumber} (id {resolvedOafId}). " +
                "The Sales Allocation must be completed before calling this API.");
        }

        _logger.LogInformation(
            "SoAllocationToDC: OAF {OafNumber} (id {OafId}) ─ {Count} allocated item(s), total OAF qty = {Qty}",
            matchingOaf.OafNumber, resolvedOafId, allocationRows.Count,
            allocationRows.Sum(r => r.AllocatedQty));

        // ── Step 6: Duplicate DC check ────────────────────────────
        CheckForDuplicateDc(oafItemDetails, allocationRows);

        // ── Step 7: Map allocation rows to DC items ───────────────
        var (dcItems, mappingErrors) = MapAllocationsToDcItems(
            allocationRows, oafItemDetails, resolvedOafId);

        if (mappingErrors.Count > 0)
        {
            throw new DcValidationException(
                "Unable to map all allocated items to Delivery Challan items.",
                mappingErrors);
        }

        // ── Step 8: Quantity validation against ERP's maxDCQtyIUOM ───────────────────
        ValidateDcQuantities(dcItems);

        // ── Step 9: Document control default → DC year, group, site ──────────────────
        var dcDate = DateOnly.FromDateTime(DateTime.Now);
        var documentControl = await GetDocumentControlAsync(dcDate, cancellationToken);

        var shipmentInfoList = await GetShipmentInfoAsync(resolvedOafId, shipment.ShipmentCode, cancellationToken);
        var combinationsByItem = new Dictionary<int, List<(ErpItemCombination Comb, decimal Qty)>>();

        foreach (var dcItem in dcItems)
        {
            var combinations = await GetItemCombinationsAsync(dcItem.OafItem, resolvedOafId, shipment.ShipmentCode, cancellationToken);
            var remainingQty = dcItem.DcQty;
            var assignedCombs = new List<(ErpItemCombination Comb, decimal Qty)>();

            foreach (var comb in combinations)
            {
                if (remainingQty <= 0) break;
                if (comb.OnHandQty <= 0) continue;

                var takeQty = Math.Min(remainingQty, comb.OnHandQty);
                if (comb.Alloc > 0)
                {
                    takeQty = Math.Min(takeQty, comb.Alloc);
                }
                
                if (takeQty <= 0) continue;

                assignedCombs.Add((comb, takeQty));
                remainingQty -= takeQty;
            }

            combinationsByItem[dcItem.OafItem.OafDetailLineNo] = assignedCombs;
        }

        var userId = await _tokenProvider.GetUserIdAsync(cancellationToken);

        var payload = BuildDcPayload(
            documentControl,
            effectiveCustomerCode,
            shipment,
            matchingOaf,
            resolvedOafId,
            dcItems,
            shipmentInfoList,
            combinationsByItem,
            dcDate,
            userId);

        _logger.LogInformation(
            "SoAllocationToDC: submitting DC for OAF {OafNumber} (id {OafId}), {ItemCount} item(s), " +
            "total DC qty = {TotalQty}",
            matchingOaf.OafNumber, resolvedOafId, dcItems.Count, dcItems.Sum(i => i.DcQty));

        var (dcNumber, dcAutoId) = await SubmitDcAsync(payload, cancellationToken);

        var result = new DcCreationResult(
            DcNumber: dcNumber,
            DcId: dcAutoId,
            OafId: resolvedOafId,
            OafNo: matchingOaf.OafNumber,
            IntOrdNo: matchingOaf.IntOrderNumber,
            AllocatedItemCount: allocationRows.Count,
            DcItemCount: dcItems.Count,
            TotalAllocatedQty: allocationRows.Sum(r => r.AllocatedQty),
            TotalDcQty: dcItems.Sum(i => i.DcQty));

        _logger.LogInformation(
            "SoAllocationToDC: DC {DcNumber} (id={DcId}) created for OAF {OafNumber} (id {OafId}, intOrder={IntOrdNo}). " +
            "AllocItems={AllocCount} DCItems={DcCount} AllocQty={AllocQty} DcQty={DcQty}",
            dcNumber, dcAutoId, matchingOaf.OafNumber, resolvedOafId, matchingOaf.IntOrderNumber,
            result.AllocatedItemCount, result.DcItemCount,
            result.TotalAllocatedQty, result.TotalDcQty);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingOafSummary>> GetPendingAllocationsAsync(
        string? customerCode = null,
        CancellationToken cancellationToken = default)
    {
        var targetCustomers = new List<string>();

        if (!string.IsNullOrWhiteSpace(customerCode))
        {
            targetCustomers.Add(customerCode.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(_options.CustomerCode))
        {
            targetCustomers.Add(_options.CustomerCode.Trim());
        }
        else
        {
            var siteId = _options.SiteId;
            var allocCustomers = await GetCustomersForAllocationAsync(siteId, cancellationToken);
            foreach (var c in allocCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c.CustCode) &&
                    !targetCustomers.Contains(c.CustCode, StringComparer.OrdinalIgnoreCase))
                {
                    targetCustomers.Add(c.CustCode);
                }
            }

            var dcCustomers = await GetCustomersForDcAsync(siteId, cancellationToken);
            foreach (var c in dcCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c) &&
                    !targetCustomers.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    targetCustomers.Add(c);
                }
            }
        }

        var results = new List<PendingOafSummary>();

        foreach (var effectiveCustomerCode in targetCustomers)
        {
            var shipments = await GetShipmentsAsync(effectiveCustomerCode, cancellationToken);

            foreach (var shipment in shipments)
            {
                var oafList = await GetOafDetailListAsync(
                    effectiveCustomerCode, shipment.ShipmentCode, cancellationToken);

                foreach (var oaf in oafList)
                {
                    var oafItems = await GetOafItemDetailsAsync(
                        effectiveCustomerCode,
                        shipment.ShipmentCode,
                        oaf.OafYear,
                        oaf.OafGroup,
                        oaf.OafNumber,
                        cancellationToken);

                    var shipmentInfoList = await GetShipmentInfoAsync(
                        oaf.OafAutoId, shipment.ShipmentCode, cancellationToken);

                    var itemSummaries = new List<PendingOafItemSummary>();
                    var anyReadyItem = false;

                    foreach (var item in oafItems)
                    {
                        var hasShipRecord = shipmentInfoList.Any(s =>
                            string.Equals(s.SalesItemCode, item.SalesItemCode, StringComparison.OrdinalIgnoreCase));

                        var isReady = hasShipRecord && item.MaxDcQtyIuom > 0;
                        if (isReady)
                        {
                            anyReadyItem = true;
                        }

                        itemSummaries.Add(new PendingOafItemSummary(
                            SalesItemCode: item.SalesItemCode,
                            ItemName: item.SalesItemName,
                            OafQty: item.OafQty,
                            AllocatedQty: item.MaxDcQtyIuom > 0 ? item.MaxDcQtyIuom : item.OafQty,
                            AlreadyDcQty: item.AlreadyDcQty,
                            PendingDcQty: item.MaxDcQtyIuom,
                            AllocationId: item.AllocationId,
                            WarehouseCode: item.WarehouseCode,
                            HasPendingShipmentRecord: hasShipRecord));
                    }

                    string statusMessage;
                    bool isReadyForDc;

                    if (oafItems.Count == 0)
                    {
                        isReadyForDc = false;
                        statusMessage = "No dispatchable items found on OAF.";
                    }
                    else if (anyReadyItem)
                    {
                        isReadyForDc = true;
                        statusMessage = "Ready to convert into Delivery Challan.";
                    }
                    else if (oafItems.All(i => i.AlreadyDcQty > 0 && i.MaxDcQtyIuom == 0))
                    {
                        isReadyForDc = false;
                        statusMessage = "Already fully dispatched into Delivery Challan.";
                    }
                    else if (oafItems.All(i => i.AllocationId == 0))
                    {
                        isReadyForDc = false;
                        statusMessage = "Sales Allocation not completed or not saved (Allocation ID is 0).";
                    }
                    else if (!shipmentInfoList.Any())
                    {
                        isReadyForDc = false;
                        statusMessage = "No active shipment schedule / dispatch records found.";
                    }
                    else
                    {
                        isReadyForDc = false;
                        statusMessage = "Pending allocation checks.";
                    }

                    results.Add(new PendingOafSummary(
                        OafNo: oaf.OafNumber,
                        IntOrdNo: oaf.IntOrderNumber,
                        OafId: oaf.OafAutoId,
                        CustomerCode: effectiveCustomerCode,
                        ShipmentCode: shipment.ShipmentCode,
                        CustomerOrderNumber: oaf.CustomerOrderNumber,
                        IsReadyForDc: isReadyForDc,
                        StatusMessage: statusMessage,
                        Items: itemSummaries));
                }
            }
        }

        return results;
    }

    private static bool MatchesDocNumber(string? candidate, string? requested)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requested))
            return false;

        var cand = candidate.Trim();
        var req = requested.Trim();

        if (string.Equals(cand, req, StringComparison.OrdinalIgnoreCase))
            return true;

        if (cand.EndsWith(req, StringComparison.OrdinalIgnoreCase) || req.EndsWith(cand, StringComparison.OrdinalIgnoreCase))
            return true;

        var candTrimmed = cand.TrimStart('0');
        var reqTrimmed = req.TrimStart('0');
        if (!string.IsNullOrEmpty(candTrimmed) && string.Equals(candTrimmed, reqTrimmed, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  ERP calls â€” all use existing ERP screen endpoints
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Shipment list for the DC screen:
    /// <c>GET /api/v1/sales/deliverychallanentry/getShipmentForDC/{customerCode}/L</c>.
    /// Exactly what the manual DC entry screen calls after selecting a customer.
    /// </summary>
    private async Task<IReadOnlyList<ErpShipment>> GetShipmentsAsync(
        string customerCode,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/deliverychallanentry/getShipmentForDC/" +
                  $"{Uri.EscapeDataString(customerCode)}/L";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpShipment(
                ShipmentCode: node["shipmentCode"]?.GetValue<string>() ?? "",
                ShipmentName: node["shipmentName"]?.GetValue<string>() ?? "",
                Add1: node["add1"]?.GetValue<string>(),
                Add2: node["add2"]?.GetValue<string>(),
                Add3: node["add3"]?.GetValue<string>(),
                CityCode: node["cityCode"]?.GetValue<string>(),
                CityDesc: node["cityDesc"]?.GetValue<string>(),
                StateCode: node["stateCode"]?.GetValue<string>(),
                StateDesc: node["stateDesc"]?.GetValue<string>(),
                CountryCode: node["countryCode"]?.GetValue<string>(),
                CountryDesc: node["countryDesc"]?.GetValue<string>(),
                PinCode: node["pinCode"]?.GetValue<string>(),
                GstNo: node["gstNo"]?.GetValue<string>()))
            .Where(s => !string.IsNullOrWhiteSpace(s.ShipmentCode))
            .ToList();
    }

    /// <summary>
    /// OAF list for DC:
    /// <c>GET .../getOAFDetailListForDC/{custCode}/{billTo}/{shipCode}/{siteId}/F/true/false</c>.
    /// Exactly what the DC entry screen calls after customer + shipment are selected.
    /// </summary>
    private Task<IReadOnlyList<ErpOafDetail>> GetOafDetailListAsync(
        string customerCode,
        string shipmentCode,
        CancellationToken cancellationToken) =>
        GetOafDetailListAsync(customerCode, shipmentCode, siteId: null, cancellationToken);

    private async Task<IReadOnlyList<ErpOafDetail>> GetOafDetailListAsync(
        string customerCode,
        string shipmentCode,
        int? siteId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveSiteId = siteId ?? _options.SiteId;
        var url = "/api/v1/sales/deliverychallanentry/getOAFDetailListForDC/" +
                  $"{Uri.EscapeDataString(customerCode)}/" +
                  $"{Uri.EscapeDataString(customerCode)}/" +
                  $"{Uri.EscapeDataString(shipmentCode)}/" +
                  $"{effectiveSiteId}/F/true/false";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpOafDetail(
                OafYear: node["oafYear"]?.GetValue<string>() ?? "",
                OafGroup: node["oafGroup"]?.GetValue<string>() ?? "",
                LocationCode: node["locationCode"]?.GetValue<string>() ?? "",
                OafNumber: node["oafNumber"]?.GetValue<string>() ?? "",
                OafAutoId: node["oafAutoId"]?.GetValue<int>() ?? 0,
                IntOrderYear: node["intOrderYear"]?.GetValue<string>() ?? "",
                IntOrderGroup: node["intOrderGroup"]?.GetValue<string>() ?? "",
                IntOrderLocation: node["intOrderLocation"]?.GetValue<string>() ?? "",
                IntOrderNumber: node["intOrderNumber"]?.GetValue<string>() ?? "",
                CustomerOrderNumber: node["customerOrderNumber"]?.GetValue<string>() ?? ""))
            .ToList();
    }

    /// <summary>
    /// OAF item details:
    /// <c>POST /api/v1/sales/deliverychallanentry/getOAFItemDetailsForDC</c>.
    /// Exactly what the DC entry screen calls after selecting an OAF. Returns one row per
    /// OAF line, including the <c>allocationID</c> that ties it back to the allocation.
    /// </summary>
    private Task<IReadOnlyList<ErpOafItemDetail>> GetOafItemDetailsAsync(
        string customerCode,
        string shipmentCode,
        string oafYear,
        string oafGroup,
        string oafNumber,
        CancellationToken cancellationToken) =>
        GetOafItemDetailsAsync(customerCode, shipmentCode, oafYear, oafGroup, oafNumber, siteId: null, cancellationToken);

    private async Task<IReadOnlyList<ErpOafItemDetail>> GetOafItemDetailsAsync(
        string customerCode,
        string shipmentCode,
        string oafYear,
        string oafGroup,
        string oafNumber,
        int? siteId = null,
        CancellationToken cancellationToken = default)
    {
        const string url = "/api/v1/sales/deliverychallanentry/getOAFItemDetailsForDC";

        var body = JsonSerializer.Serialize(new
        {
            custCode = customerCode,
            shipCode = shipmentCode,
            siteId = siteId ?? _options.SiteId,
            docYear = oafYear,
            docGrp = oafGroup,
            docNumber = oafNumber,
            insAutoId = 0,
            isGSTApplicable = true,
            packingList = "F",
            sjoId = 0,
        }, SerializerOptions);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpOafItemDetail(
                SalesItemCode: node["salesItemCode"]?.GetValue<string>() ?? "",
                SalesItemName: node["salesItemName"]?.GetValue<string>() ?? "",
                IntItemCode: node["intItemCode"]?.GetValue<string>() ?? "",
                IntItemName: node["intItemName"]?.GetValue<string>() ?? "",
                WarehouseCode: node["warehouseCode"]?.GetValue<string>() ?? "",
                OafQty: node["oafQty"]?.GetValue<decimal>() ?? 0m,
                AlreadyDcQty: node["alreadyDCQty"]?.GetValue<decimal>() ?? 0m,
                DcQty: node["dcQty"]?.GetValue<decimal>() ?? 0m,
                OafId: node["oafid"]?.GetValue<int>() ?? 0,
                WareHouseId: node["wareHouseId"]?.GetValue<int>() ?? 0,
                Suom: node["suom"]?.GetValue<string>() ?? "",
                Precision: node["precision"]?.GetValue<int>() ?? 0,
                ManufacturingBatchReq: node["manufacturingBatchReq"]?.GetValue<bool>() ?? false,
                OafDetailLineId: node["oafDetailLineId"]?.GetValue<int>() ?? 0,
                MaxDcQtyIuom: node["maxDCQtyIUOM"]?.GetValue<decimal>() ?? 0m,
                AllocationId: node["allocationID"]?.GetValue<int>() ?? 0,
                SoItemLineNo: node["soitemlineno"]?.GetValue<int>() ?? 0,
                OafDetailLineNo: node["oafDetailLineNo"]?.GetValue<int>() ?? 0,
                SizeReq: node["sizeReq"]?.GetValue<bool>() ?? false,
                InwardReq: node["inwardReq"]?.GetValue<bool>() ?? false,
                HeatReq: node["heatReq"]?.GetValue<bool>() ?? false,
                SrNoReq: node["srNoReq"]?.GetValue<bool>() ?? false,
                DimFlagVal: node["dimFlagVal"]?.GetValue<string>(),
                DensityValue: node["densityValue"]?.GetValue<decimal>() ?? 0m,
                DimensionFormula: node["dimensionFormula"]?.GetValue<string>(),
                DimensionSize: node["dimensionSize"]?.GetValue<string>(),
                NoOfPiece: node["noOfPiece"]?.GetValue<int>() ?? 0,
                TotalWeight: node["totalWeight"]?.GetValue<decimal>() ?? 0m,
                ServiceSalesItem: node["servicesalesitem"]?.GetValue<string>(),
                XcdRemark: node["xcdremark"]?.GetValue<string>(),
                TagNo: node["tagno"]?.GetValue<string>()))
            .ToList();
    }

    /// <summary>
    /// Document-control default for the DC document type:
    /// <c>GET /api/v1/admin/documentControl/getDefaultDocumentDetail/{year}/DC/DS/{locationId}</c>.
    /// Provides the financial year, group code and site the DC header is stamped with.
    /// </summary>
    private Task<ErpDocumentControlDefault> GetDocumentControlAsync(
        DateOnly date,
        CancellationToken cancellationToken) =>
        GetDocumentControlAsync(date, siteId: null, siteCode: null, cancellationToken);

    private async Task<ErpDocumentControlDefault> GetDocumentControlAsync(
        DateOnly date,
        int? siteId = null,
        string? siteCode = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveSiteId = siteId ?? _options.SiteId;
        var effectiveSiteCode = !string.IsNullOrWhiteSpace(siteCode)
            ? siteCode.Trim()
            : (!string.IsNullOrWhiteSpace(_options.SiteCode) ? _options.SiteCode.Trim() : "NF1");
        var year = ToFinancialYear(date);
        var url = $"/api/v1/admin/documentControl/getDefaultDocumentDetail/" +
                  $"{Uri.EscapeDataString(year)}/DC/DS/{effectiveSiteId}";

        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Non-fatal: fall back to sensible defaults derived from configuration.
            _logger.LogWarning(
                "SoAllocationToDC: document-control lookup returned {Status}. " +
                "Using configuration defaults.",
                (int)response.StatusCode);

            return new ErpDocumentControlDefault(
                FinancialYear: year,
                GroupCode: "TE",
                LocationCode: _options.SiteCode,
                LocationName: "Fabcon Machines Pvt. Ltd.",
                LocationId: _options.SiteId,
                SiteRequired: "Y",
                AutoNumberRequired: "Y",
                AuthorisationRequired: "N");
        }

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        var row = envelope?.Data?.FirstOrDefault();

        if (row is null)
        {
            return new ErpDocumentControlDefault(
                FinancialYear: year,
                GroupCode: "TE",
                LocationCode: _options.SiteCode,
                LocationName: "Fabcon Machines Pvt. Ltd.",
                LocationId: _options.SiteId,
                SiteRequired: "Y",
                AutoNumberRequired: "Y",
                AuthorisationRequired: "N");
        }

        var isLocReq = row["isLocationRequired"]?.GetValue<bool>() == true || row["siteReq"]?.GetValue<string>() == "Y";
        var isAutoNo = row["isAutoNumberGenerated"]?.GetValue<bool>() == true || row["autoNoReq"]?.GetValue<string>() == "Y";
        var isAuthReq = row["isAutorisationRequired"]?.GetValue<bool>() == true || row["authReq"]?.GetValue<string>() == "Y";

        return new ErpDocumentControlDefault(
            FinancialYear: row["finYear"]?.GetValue<string>() ?? year,
            GroupCode: row["groupCode"]?.GetValue<string>() ?? row["grpCode"]?.GetValue<string>() ?? "TE",
            LocationCode: row["locationCode"]?.GetValue<string>() ?? _options.SiteCode,
            LocationName: row["locationName"]?.GetValue<string>() ?? "Fabcon Machines Pvt. Ltd.",
            LocationId: row["locationId"]?.GetValue<int>() ?? _options.SiteId,
            SiteRequired: isLocReq ? "Y" : "N",
            AutoNumberRequired: isAutoNo ? "Y" : "N",
            AuthorisationRequired: isAuthReq ? "Y" : "N");
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  Mapping + validation  (no new business rules)
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed record MappedDcItem(
        ErpAllocationRow Allocation,
        ErpOafItemDetail OafItem,
        decimal DcQty);

    /// <summary>
    /// Maps every allocation row to its OAF item detail using the stable IDs the ERP uses:
    /// allocationID, then oafDetailLineId, then (oafDetailLineNo + soitemlineno), then salesItemCode.
    /// </summary>
    private static (IReadOnlyList<MappedDcItem> Items, IReadOnlyList<DcValidationError> Errors)
        MapAllocationsToDcItems(
            IReadOnlyList<ErpAllocationRow> allocations,
            IReadOnlyList<ErpOafItemDetail> oafItems,
            int oafId)
    {
        var items = new List<MappedDcItem>();
        var errors = new List<DcValidationError>();

        foreach (var alloc in allocations)
        {
            ErpOafItemDetail? match = null;

            // 1. Most specific: allocationID
            if (alloc.AllocationId > 0)
            {
                match = oafItems.FirstOrDefault(i => 
                    i.AllocationId == alloc.AllocationId && 
                    string.Equals(i.SalesItemCode, alloc.SalesItemCode, StringComparison.OrdinalIgnoreCase));
            }

            // 2. OAF detail line id
            if (match is null && alloc.OafDetailLineId > 0)
            {
                match = oafItems.FirstOrDefault(i => i.OafDetailLineId == alloc.OafDetailLineId);
            }

            // 3. OAF detail line number + SO item line number
            if (match is null && alloc.OafDetailLineNo > 0)
            {
                match = oafItems.FirstOrDefault(i =>
                    i.OafDetailLineNo == alloc.OafDetailLineNo &&
                    i.SoItemLineNo == alloc.SoItemLineNo);
            }

            // 4. Item code (last resort; fails on duplicates)
            if (match is null)
            {
                var byCode = oafItems
                    .Where(i => string.Equals(
                        i.SalesItemCode, alloc.SalesItemCode,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (byCode.Count == 1)
                {
                    match = byCode[0];
                }
                else if (byCode.Count > 1)
                {
                    errors.Add(new DcValidationError(
                        alloc.SalesItemCode,
                        $"Item '{alloc.SalesItemCode}' appears more than once in OAF {oafId}. " +
                        "Cannot map it unambiguously by item code alone."));
                    continue;
                }
            }

            if (match is null)
            {
                errors.Add(new DcValidationError(
                    alloc.SalesItemCode,
                    $"Unable to map allocated item '{alloc.SalesItemCode}' " +
                    $"(allocationId={alloc.AllocationId}, lineId={alloc.OafDetailLineId}) " +
                    $"to a Delivery Challan item for OAF {oafId}."));
                continue;
            }

            var itemDcQty = alloc.AllocatedQty > 0
                ? alloc.AllocatedQty
                : (match.MaxDcQtyIuom > 0 ? match.MaxDcQtyIuom : match.OafQty);

            if (itemDcQty > 0)
            {
                items.Add(new MappedDcItem(alloc, match, itemDcQty));
            }
        }

        return (items, errors);
    }

    private static void CheckForDuplicateDc(
        IReadOnlyList<ErpOafItemDetail> oafItems,
        IReadOnlyList<ErpAllocationRow> allocations)
    {
        if (oafItems.Count > 0 && oafItems.All(i => i.MaxDcQtyIuom <= 0 && i.AlreadyDcQty > 0))
        {
            throw new DcValidationException(
                "Duplicate",
                "A Delivery Challan has already been created for this OAF. No quantity is available to create another DC.");
        }
    }

    /// <summary>
    /// Verifies that every mapped DC quantity does not exceed the ERP's own
    /// <c>maxDCQtyIUOM</c>. Uses the ERP-returned value - no new calculation.
    /// </summary>
    private static void ValidateDcQuantities(IReadOnlyList<MappedDcItem> items)
    {
        var errors = new List<DcValidationError>();

        foreach (var item in items)
        {
            if (item.DcQty <= 0)
            {
                errors.Add(new DcValidationError(
                    item.Allocation.SalesItemCode,
                    $"DC quantity for '{item.Allocation.SalesItemCode}' resolved to zero or negative."));
            }
            else if (item.DcQty > item.OafItem.MaxDcQtyIuom)
            {
                errors.Add(new DcValidationError(
                    item.Allocation.SalesItemCode,
                    $"DC quantity {item.DcQty} exceeds ERP max allowable DC quantity " +
                    $"{item.OafItem.MaxDcQtyIuom} (IUOM) for '{item.Allocation.SalesItemCode}'."));
            }
        }

        if (errors.Count > 0)
        {
            throw new DcValidationException(
                "One or more DC quantities exceed allowable limits.",
                errors);
        }
    }

    /// <summary>
    /// Shipment info for the DC screen:
    /// <c>GET /api/v1/sales/deliverychallanentry/getShipmentInfo/{oafId}/{shipmentCode}</c>.
    /// Supplies rate structure code, delivery line references and record IDs.
    /// </summary>
    private async Task<IReadOnlyList<ErpShipmentInfo>> GetShipmentInfoAsync(
        int oafId,
        string shipmentCode,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/deliverychallanentry/getShipmentInfo/{oafId}/{Uri.EscapeDataString(shipmentCode)}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpShipmentInfo(
                SalesItemCode: node["salesItemCode"]?.GetValue<string>() ?? "",
                ShipCode: node["shipCode"]?.GetValue<string>() ?? "",
                RateStructureCode: node["rateStructureCode"]?.GetValue<string>() ?? "",
                BillToCode: node["billToCode"]?.GetValue<string>() ?? "",
                RecordId: node["recordId"]?.GetValue<long>() ?? 0,
                OafDetailRefLineId: node["oafDetailRefLineId"]?.GetValue<int>() ?? 0,
                OafQtyOfLineId: node["oafQtyOfLineId"]?.GetValue<int>() ?? 0,
                OafRefId: node["oafRefId"]?.GetValue<int>() ?? 0))
            .ToList();
    }

    /// <summary>
    /// Item combinations for DC:
    /// <c>POST /api/v1/sales/deliverychallanentry/getItemCombinations4DCForItm</c>.
    /// Supplies stockId, lineNo and onHandQty for deduction.
    /// </summary>
    private async Task<IReadOnlyList<ErpItemCombination>> GetItemCombinationsAsync(
        ErpOafItemDetail item,
        int oafId,
        string shipmentCode,
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/sales/deliverychallanentry/getItemCombinations4DCForItm";

        var body = JsonSerializer.Serialize(new
        {
            dcId = 0,
            inWardReq = item.InwardReq ? "Y" : "N",
            inWardwiseAlloc = "N",
            insAutoId = "0",
            insDetailAutoId = "0",
            itemCode = item.IntItemCode,
            oafDetailLineNo = item.OafDetailLineNo,
            oafId = oafId > 0 ? oafId : item.OafId,
            packingList = "F",
            rndno = 0,
            salesItemCode = item.SalesItemCode,
            shipCode = shipmentCode,
            sjoId = 0,
            soitemlineno = item.SoItemLineNo,
            warehouseId = item.WareHouseId,
            warId = item.WareHouseId,
        }, SerializerOptions);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpItemCombination(
                RowId: node["rowID"]?.GetValue<int>() ?? 0,
                LineNo: node["lineNo"]?.GetValue<long>() ?? 0,
                Quantity: node["quantity"]?.GetValue<decimal>() ?? 0m,
                OnHandQty: node["onHandQty"]?.GetValue<decimal>() ?? 0m,
                Alloc: node["alloc"]?.GetValue<decimal>() ?? 0m,
                StockId: node["stockId"]?.GetValue<long>() ?? 0,
                ItemCode: node["itemCode"]?.GetValue<string>() ?? "",
                DocId: node["docId"]?.GetValue<int>() ?? 0,
                WarehouseId: node["warehouseId"]?.GetValue<int>() ?? 0))
            .ToList();
    }

    /// <summary>
    /// Builds the JSON body that the ERP's <c>createDCEntry</c> endpoint expects, assembling
    /// the full DeliveryChallanEntry model structure.
    /// </summary>
    private JsonObject BuildDcPayload(
        ErpDocumentControlDefault documentControl,
        string customerCode,
        ErpShipment shipment,
        ErpOafDetail oafDetail,
        int oafId,
        IReadOnlyList<MappedDcItem> dcItems,
        IReadOnlyList<ErpShipmentInfo> shipmentInfoList,
        IReadOnlyDictionary<int, List<(ErpItemCombination Comb, decimal Qty)>> combinationsByItem,
        DateOnly dcDate,
        int userId,
        string transporterCode = "",
        string modeOfDispatchCode = "")
    {
        var itemMiscArray = new JsonArray();
        var lineBifArray = new JsonArray();
        var oafRelationArray = new JsonArray();
        var rateStructureCode = "";

        foreach (var mapped in dcItems)
        {
            var i = mapped.OafItem;
            var shipInfo = shipmentInfoList.FirstOrDefault(s =>
                string.Equals(s.SalesItemCode, i.SalesItemCode, StringComparison.OrdinalIgnoreCase));

            if (shipInfo is not null && !string.IsNullOrWhiteSpace(shipInfo.RateStructureCode))
            {
                rateStructureCode = shipInfo.RateStructureCode;
            }

            var oafRefId = shipInfo?.OafRefId > 0 ? shipInfo.OafRefId : (i.OafId > 0 ? i.OafId : oafId);
            var oafDtlRefLineId = shipInfo?.OafDetailRefLineId > 0 ? shipInfo.OafDetailRefLineId : (i.OafDetailLineId > 0 ? i.OafDetailLineId : i.OafDetailLineNo);
            var oafDeliveryId = shipInfo?.OafQtyOfLineId ?? 0;
            var oafRecordId = shipInfo?.RecordId ?? 0;

            if (!combinationsByItem.TryGetValue(i.OafDetailLineNo, out var assignedCombs) || assignedCombs.Count == 0)
            {
                // Fallback to a single entry with stockId 0 if no combinations found (which will likely fail later)
                assignedCombs = new List<(ErpItemCombination Comb, decimal Qty)> { (null!, mapped.DcQty) };
            }

            foreach (var (comb, splitQty) in assignedCombs)
            {
                var stockId = comb?.StockId ?? 0;
                var lineNo = comb?.LineNo ?? 0;
                var docId = comb?.DocId ?? 0;

                itemMiscArray.Add(new JsonObject
                {
                    ["oafId"] = i.OafId > 0 ? i.OafId : oafId,
                    ["allocationId"] = lineNo,
                    ["qty"] = splitQty,
                    ["qtysuom"] = splitQty,
                    ["weight"] = i.TotalWeight,
                    ["stockId"] = stockId,
                    ["fifoId"] = 0,
                    ["shipCode"] = shipment.ShipmentCode,
                    ["itemCode"] = i.SalesItemCode,
                    ["flag"] = "Y",
                    ["lineNumber"] = i.OafDetailLineNo,
                    ["itemRemark"] = i.XcdRemark ?? "",
                    ["disInsAutoId"] = 0,
                    ["disInsDetAutoId"] = 0,
                    ["sjoId"] = 0,
                    ["rndNumber"] = 0,
                    ["packingList"] = "F",
                    ["soitemlineno"] = i.SoItemLineNo,
                    ["oafDetailLineNo"] = i.OafDetailLineNo,
                    ["densityValue"] = i.DensityValue,
                    ["dimensionFormula"] = i.DimensionFormula ?? "",
                    ["dimensionSize"] = i.DimensionSize ?? "",
                    ["noOfPiece"] = i.NoOfPiece,
                    ["totalWeight"] = i.TotalWeight
                });

                lineBifArray.Add(new JsonObject
                {
                    ["oafRefrenceId"] = oafRefId,
                    ["oafDetailRefLineId"] = oafDtlRefLineId,
                    ["oafDeliveryId"] = oafDeliveryId,
                    ["oafRecordId"] = oafRecordId,
                    ["oafldcqty"] = splitQty,
                    ["itemCode"] = shipInfo?.SalesItemCode ?? i.SalesItemCode,
                    ["soitemlineno"] = i.SoItemLineNo
                });

                oafRelationArray.Add(new JsonObject
                {
                    ["dcHeaderRefId"] = 0,
                    ["dcRefStkId"] = stockId,
                    ["dcRefLineNo"] = i.OafDetailLineNo,
                    ["oafHdrRefId"] = docId,
                    ["oafDtlRefLineId"] = oafDtlRefLineId,
                    ["oafLineBifId"] = oafRecordId,
                    ["dcQtyIUOM"] = splitQty,
                    ["dcQtySUOM"] = splitQty,
                    ["salesItemCode"] = i.SalesItemCode,
                    ["oafDeliveryRefId"] = oafDeliveryId
                });
            }
        }

        var header = new JsonObject
        {
            ["docType"] = "DC",
            ["docSubType"] = "DS",
            ["dcNumber"] = "",
            ["dcYear"] = documentControl.FinancialYear,
            ["dcGroupCode"] = documentControl.GroupCode,
            ["dcLocId"] = documentControl.LocationId,
            ["dcLocCode"] = documentControl.LocationCode,
            ["dcfrLocId"] = documentControl.LocationId,
            ["custOrVend"] = "C",
            ["chargeable"] = "E",
            ["returnable"] = "N",
            ["custOrVendCode"] = customerCode,
            ["shipmentCode"] = shipment.ShipmentCode,
            ["userId"] = userId,
            ["autoNumReq"] = "Y",
            ["siteReq"] = "Y",
            ["authFlag"] = "Y",
            ["docDate"] = $"{dcDate:yyyy-MM-dd}T00:00:00",
            ["marks"] = "",
            ["preCarriageBy"] = "",
            ["placeOfReceipt"] = "",
            ["reference"] = "",
            ["lodingFrom"] = "",
            ["lodingTo"] = "",
            ["isForPackingList"] = false,
            ["billTo"] = customerCode,
            ["TransportDriverName"] = "",
            ["TransportDriverContactNo"] = ""
        };

        var detail = new JsonObject
        {
            ["modeOfDispatchID"] = modeOfDispatchCode,
            ["transporterCode"] = transporterCode,
            ["transporterAddress1"] = "",
            ["transporterAddress2"] = "",
            ["lrNumber"] = "",
            ["lrDate"] = (string?)null,
            ["roadPermitNo"] = "",
            ["roadPermitDate"] = (string?)null,
            ["vehicleNo"] = "",
            ["netWeight"] = 0,
            ["grossWeight"] = 0,
            ["packetList"] = "F",
            ["custRefNo"] = "",
            ["custRefDate"] = (string?)null,
            ["dlvName"] = shipment.ShipmentName ?? "",
            ["dlvAdd1"] = shipment.Add1 ?? "",
            ["dlvAdd2"] = shipment.Add2 ?? "",
            ["dlvAdd3"] = shipment.Add3 ?? "",
            ["dlvCityCd"] = shipment.CityCode ?? "",
            ["dlvPinCd"] = shipment.PinCode ?? "",
            ["numberOfBoxes"] = "0",
            ["contactName"] = "",
            ["contactNo"] = "",
            ["xdcPlaceOfSupply"] = "",
            ["xdcrtstrcd"] = rateStructureCode,
            ["dlvGSTNo"] = shipment.GstNo ?? ""
        };

        var remarks = new JsonObject
        {
            ["docType"] = "DC",
            ["docSubType"] = "DS",
            ["dcGroupCode"] = documentControl.GroupCode,
            ["siteId"] = documentControl.LocationId,
            ["remarks"] = "",
            ["userId"] = userId
        };

        return new JsonObject
        {
            ["dchallanHeaderEntry"] = header,
            ["insertDCDetailEntryModel"] = detail,
            ["insertDCRemarksEntryModel"] = remarks,
            ["insertDCforItemMISCDetail"] = itemMiscArray,
            ["insertLineBifForDC"] = lineBifArray,
            ["insertOAFDCRelation"] = oafRelationArray,
            ["authorizeDCModel"] = new JsonObject
            {
                ["fmLocationId"] = documentControl.LocationId,
                ["userId"] = userId,
                ["authDate"] = $"{dcDate:yyyy-MM-dd}T00:00:00",
                ["finFlag"] = "N",
                ["currCode"] = "INR"
            },
            ["dCEntryNotificationModel"] = new JsonObject
            {
                ["locationId"] = documentControl.LocationId,
                ["locationCode"] = documentControl.LocationCode,
                ["locationName"] = documentControl.LocationName,
                ["ip"] = "",
                ["macId"] = "",
                ["userId"] = userId,
                ["companyId"] = 1
            }
        };
    }

    /// <summary>
    /// Posts the DC payload and parses the ERP's response message
    /// <c>DCEntrysavedKey#26-27/DS/CT2/000107#110530</c>.
    /// Marked non-retryable at the transport level: if a network failure occurs after the
    /// ERP receives the POST, the DC may already exist â€” the caller should query before
    /// retrying, rather than the transport blindly replaying the create.
    /// </summary>
    private async Task<(string DcNumber, long DcId)> SubmitDcAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/sales/deliverychallanentry/createDCEntry";

        using var content = new StringContent(
            payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json");

        // Non-retryable: replaying a create blindly may duplicate the DC.
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Options.Set(DependencyInjection.NonRetryableKey, true);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("createDCEntry failed with HTTP {StatusCode}: {Body}. Payload: {Payload}",
                (int)response.StatusCode, body, payload.ToJsonString(SerializerOptions));
            throw new ErpTransientException(
                $"ERP returned HTTP {(int)response.StatusCode} creating the Delivery Challan: {body}",
                $"POST {url} -> {(int)response.StatusCode}: {body}");
        }

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeScalar>(
            SerializerOptions, cancellationToken);

        if (envelope?.Success != true)
        {
            var msg = !string.IsNullOrWhiteSpace(envelope?.ErrorMessage)
                ? envelope.ErrorMessage
                : (!string.IsNullOrWhiteSpace(envelope?.Message) ? envelope.Message : "ERP reported failure creating the Delivery Challan.");

            throw new DcValidationException("createDCEntry", msg);
        }

        // ERP message: "sale_saledp_dcsales_DCEntrysavedKey#26-27/DS/CT2/000107#110530"
        var message = envelope.Message ?? "";
        var parts = message.Split('#');

        if (parts.Length < 3)
        {
            throw new ErpTransientException(
                "ERP created the Delivery Challan but the response message could not be parsed " +
                "to extract the DC number and id.",
                $"Message was: '{message}'");
        }

        var dcNumber = parts[1];
        long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dcId);

        return (dcNumber, dcId);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  Utilities
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Converts a calendar date to the ERP's "yy-yy" financial year (April start).
    /// </summary>
    private static string ToFinancialYear(DateOnly date)
    {
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        var endYear = startYear + 1;

        return $"{startYear % 100:D2}-{endYear % 100:D2}";
    }

    private static void EnsureSuccess(HttpResponseMessage response, string url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ErpTransientException(
                $"ERP returned HTTP {(int)response.StatusCode}.",
                $"GET {url} â†’ {(int)response.StatusCode}");
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  ERP envelope wrappers â€” local to this service
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class ErpEnvelopeList<T>
    {
        public IReadOnlyList<T>? Data { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class ErpEnvelopeScalar
    {
        public object? Data { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

