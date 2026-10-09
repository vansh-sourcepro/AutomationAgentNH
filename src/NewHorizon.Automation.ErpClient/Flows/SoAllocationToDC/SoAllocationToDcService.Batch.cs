using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

public sealed partial class SoAllocationToDcService
{
    private sealed record AllocationCandidateUnit(
        string CustomerCode,
        string ShipmentCode,
        ErpShipment Shipment,
        ErpOafDetail Oaf,
        IReadOnlyList<ErpOafItemDetail> Items,
        int PrimaryAllocationId);

    // ─────────────────────────────────────────────────────────────────
    //  MASTER BATCH ENTRY POINT
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<BatchDcCreationResult> CreateDeliveryChallansFromAllocationsAsync(
        BatchDcCreationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var siteId = request?.SiteId ?? _options.SiteId;
        var siteCode = !string.IsNullOrWhiteSpace(request?.SiteCode)
            ? request.SiteCode.Trim()
            : (!string.IsNullOrWhiteSpace(_options.SiteCode) ? _options.SiteCode.Trim() : "CT2");

        var dryRun = request?.DryRun ?? false;

        _logger.LogInformation(
            "SoAllocationToDC: Master automation started. SiteId={SiteId}, SiteCode={SiteCode}, FilterCustomer={Cust}, DryRun={DryRun}",
            siteId, siteCode, request?.CustomerCode, dryRun);

        // ── Step 1: Discover candidate customers ──
        var customerCodes = new List<string>();

        if (!string.IsNullOrWhiteSpace(request?.CustomerCode))
        {
            customerCodes.Add(request.CustomerCode.Trim());
        }
        else
        {
            // First source: ERP Sales Allocation customer list
            var allocCustomers = await GetCustomersForAllocationAsync(siteId, cancellationToken);
            foreach (var c in allocCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c.CustCode) &&
                    !customerCodes.Contains(c.CustCode, StringComparer.OrdinalIgnoreCase))
                {
                    customerCodes.Add(c.CustCode);
                }
            }

            // Second source: Delivery Challan customer list
            var dcCustomers = await GetCustomersForDcAsync(siteId, cancellationToken);
            foreach (var c in dcCustomers)
            {
                if (!string.IsNullOrWhiteSpace(c) &&
                    !customerCodes.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    customerCodes.Add(c);
                }
            }

            // Fallback: configured customer
            if (customerCodes.Count == 0 && !string.IsNullOrWhiteSpace(_options.CustomerCode))
            {
                customerCodes.Add(_options.CustomerCode.Trim());
            }
        }

        _logger.LogInformation(
            "SoAllocationToDC: Discovered {Count} customer(s) to inspect for allocations.",
            customerCodes.Count);

        // ── Step 2: Discover ALL candidate allocations across the system ──
        var candidates = await DiscoverAllAllocationsAsync(
            customerCodes, siteId, siteCode, request, cancellationToken);

        _logger.LogInformation(
            "SoAllocationToDC: Discovered {Count} allocation candidate unit(s) to process.",
            candidates.Count);

        var results = new List<AllocationDcItemResult>();
        var createdDCCount = 0;

        // ── Step 3: Process ALL allocations sequentially (1 -> 2 -> 3 -> ...) ──
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            _logger.LogInformation(
                "SoAllocationToDC: [{Index}/{Total}] Processing Allocation for Customer={Cust}, OAF={OafNo} (id {OafId}), ShipCode={Ship}",
                i + 1, candidates.Count, candidate.CustomerCode, candidate.Oaf.OafNumber, candidate.Oaf.OafAutoId, candidate.ShipmentCode);

            try
            {
                var itemResult = await ProcessSingleAllocationUnitAsync(
                    candidate, siteId, siteCode, dryRun, cancellationToken);

                results.Add(itemResult);
                var uniqueDcIds = itemResult.Items
                    .Where(i => i.DcCreated && i.DcId.HasValue && i.DcId.Value > 0)
                    .Select(i => i.DcId!.Value)
                    .Distinct()
                    .Count();
                createdDCCount += uniqueDcIds;
            }
            catch (DcValidationException valEx)
            {
                _logger.LogWarning(
                    "SoAllocationToDC: Validation failure on allocation for OAF {OafNo} (id {OafId}): {Message}",
                    candidate.Oaf.OafNumber, candidate.Oaf.OafAutoId, valEx.Message);

                var errorDetails = valEx.Errors
                    .Select(e => e.Message?.Trim())
                    .Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, valEx.Message.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var fullMessage = errorDetails.Count > 0
                    ? $"{valEx.Message.Trim()} ({string.Join("; ", errorDetails)})"
                    : valEx.Message.Trim();

                var failedField = valEx.Errors.FirstOrDefault()?.Field ?? "Validation";
                var failedApi = failedField switch
                {
                    "createDCEntry" => "createDCEntry",
                    "Shipment" => "getShipmentForDC",
                    "Transporter" => "getCodeTypeList/TR/GEN",
                    "ConversionFactor" => "getConversionFactorpost",
                    "Stock" => "getItemCombinations4DCForItm",
                    _ => "PreValidation"
                };

                results.Add(new AllocationDcItemResult(
                    AllocationId: candidate.PrimaryAllocationId,
                    OafId: candidate.Oaf.OafAutoId,
                    OafNo: candidate.Oaf.OafNumber,
                    IntOrdNo: candidate.Oaf.IntOrderNumber,
                    CustomerCode: candidate.CustomerCode,
                    ShipmentCode: candidate.ShipmentCode,
                    Status: "FAILED",
                    Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: fullMessage,
                        FailedField: failedField,
                        FailedApi: failedApi
                    )).ToList()));
            }
            catch (ErpTransientException erpEx)
            {
                _logger.LogError(erpEx,
                    "SoAllocationToDC: ERP API failure on allocation for OAF {OafNo} (id {OafId}): {Message}",
                    candidate.Oaf.OafNumber, candidate.Oaf.OafAutoId, erpEx.Message);

                var (cleanMessage, failedField, failedApi) = NormalizeErpError(erpEx.Message, "createDCEntry");

                results.Add(new AllocationDcItemResult(
                    AllocationId: candidate.PrimaryAllocationId,
                    OafId: candidate.Oaf.OafAutoId,
                    OafNo: candidate.Oaf.OafNumber,
                    IntOrdNo: candidate.Oaf.IntOrderNumber,
                    CustomerCode: candidate.CustomerCode,
                    ShipmentCode: candidate.ShipmentCode,
                    Status: "FAILED",
                    Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: cleanMessage,
                        FailedField: failedField,
                        FailedApi: failedApi
                    )).ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SoAllocationToDC: Unexpected error on allocation for OAF {OafNo} (id {OafId}): {Message}",
                    candidate.Oaf.OafNumber, candidate.Oaf.OafAutoId, ex.Message);

                results.Add(new AllocationDcItemResult(
                    AllocationId: candidate.PrimaryAllocationId,
                    OafId: candidate.Oaf.OafAutoId,
                    OafNo: candidate.Oaf.OafNumber,
                    IntOrdNo: candidate.Oaf.IntOrderNumber,
                    CustomerCode: candidate.CustomerCode,
                    ShipmentCode: candidate.ShipmentCode,
                    Status: "FAILED",
                    Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: ex.Message,
                        FailedField: "General",
                        FailedApi: null
                    )).ToList()));
            }
        }

        var successCount = results.Count(r => r.Status == "SUCCESS");
        var partialCount = results.Count(r => r.Status == "PARTIAL_SUCCESS");
        var failedCount = results.Count(r => r.Status == "FAILED");
        
        var totalItems = results.Sum(r => r.Items.Count);
        var successfulItems = results.Sum(r => r.Items.Count(i => i.Status == "SUCCESS"));
        var failedItems = results.Sum(r => r.Items.Count(i => i.Status == "FAILED"));

        var overallSuccess = failedCount == 0 && partialCount == 0;
        var summaryMessage = candidates.Count == 0
            ? "No eligible Sales Order Allocations found to process."
            : dryRun
                ? $"DRY RUN: Found {candidates.Count} allocation(s) eligible for DC conversion ({successCount} ready, {failedCount} with issues)."
                : failedCount == 0 && partialCount == 0
                    ? "Automatic Delivery Challan process completed successfully."
                    : $"Automatic Delivery Challan process completed with partial success.";

        _logger.LogInformation(
            "SoAllocationToDC: Master automation finished. TotalAlloc={TotalAlloc}, Success={Success}, Partial={Partial}, Failed={Failed}, CreatedDCs={DCs}, TotalItems={TotalItems}, SuccessItems={SuccessItems}, FailedItems={FailedItems}",
            candidates.Count, successCount, partialCount, failedCount, createdDCCount, totalItems, successfulItems, failedItems);

        return new BatchDcCreationResult(
            Success: overallSuccess,
            Message: summaryMessage,
            Data: new BatchDcCreationData(
                TotalAllocations: candidates.Count,
                ProcessedAllocations: results.Count,
                SuccessfulAllocations: successCount,
                PartialAllocations: partialCount,
                FailedAllocations: failedCount,
                TotalItems: totalItems,
                SuccessfulItems: successfulItems,
                FailedItems: failedItems,
                CreatedDCCount: createdDCCount,
                Results: results));
    }

    // ─────────────────────────────────────────────────────────────────
    //  ALLOCATION DISCOVERY ENGINE
    // ─────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AllocationCandidateUnit>> DiscoverAllAllocationsAsync(
        IReadOnlyList<string> customerCodes,
        int siteId,
        string siteCode,
        BatchDcCreationRequest? request,
        CancellationToken cancellationToken)
    {
        var candidates = new List<AllocationCandidateUnit>();
        var seenOafShipmentPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var customerCode in customerCodes)
        {
            var shipments = await GetShipmentsAsync(customerCode, cancellationToken);
            if (shipments.Count == 0)
            {
                // If the user explicitly requested this customer, record failure reason later
                if (!string.IsNullOrWhiteSpace(request?.CustomerCode) &&
                    string.Equals(request.CustomerCode.Trim(), customerCode, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "SoAllocationToDC: Mandatory Shipment dropdown has no available values for customer {Cust}.",
                        customerCode);
                }
                continue;
            }

            foreach (var shipment in shipments)
            {
                var oafs = await GetOafDetailListAsync(customerCode, shipment.ShipmentCode, siteId, cancellationToken);

                foreach (var oaf in oafs)
                {
                    // Apply filters if caller specified
                    if (request?.OafIds != null && request.OafIds.Count > 0 && !request.OafIds.Contains(oaf.OafAutoId))
                    {
                        continue;
                    }

                    if (request?.OafNumbers != null && request.OafNumbers.Count > 0 &&
                        !request.OafNumbers.Any(n => MatchesDocNumber(n, oaf.OafNumber)))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(request?.OafYear) &&
                        !string.Equals(oaf.OafYear, request.OafYear.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pairKey = $"{oaf.OafAutoId}#{shipment.ShipmentCode}";
                    if (!seenOafShipmentPairs.Add(pairKey))
                    {
                        continue;
                    }

                    // Retrieve OAF item details for Delivery Challan
                    var oafItems = await GetOafItemDetailsAsync(
                        customerCode,
                        shipment.ShipmentCode,
                        oaf.OafYear,
                        oaf.OafGroup,
                        oaf.OafNumber,
                        siteId,
                        cancellationToken);

                    if (oafItems.Count == 0)
                    {
                        continue;
                    }



                    // Find if any item has been allocated or has dispatchable qty
                    var allocatedItems = oafItems
                        .Where(i => i.MaxDcQtyIuom > 0)
                        .ToList();

                    if (allocatedItems.Count == 0)
                    {
                        continue;
                    }

                    var primaryAllocId = allocatedItems.FirstOrDefault(i => i.AllocationId > 0)?.AllocationId ?? 0;
                    if (primaryAllocId == 0 && allocatedItems.Count > 0)
                    {
                        try
                        {
                            var firstComb = await GetItemCombinationsAsync(allocatedItems[0], oaf.OafAutoId, shipment.ShipmentCode, cancellationToken);
                            primaryAllocId = (int)(firstComb.FirstOrDefault(c => c.LineNo > 0)?.LineNo ?? 0);
                        }
                        catch
                        {
                            // fallback remains 0
                        }
                    }

                    if (primaryAllocId > 0)
                    {
                        allocatedItems = allocatedItems
                            .Select(it => it.AllocationId > 0 ? it : it with { AllocationId = primaryAllocId })
                            .ToList();
                    }

                    // If caller specified AllocationIds, filter by them
                    if (request?.AllocationIds != null && request.AllocationIds.Count > 0 &&
                        !allocatedItems.Any(i => request.AllocationIds.Contains(i.AllocationId)))
                    {
                        continue;
                    }

                    candidates.Add(new AllocationCandidateUnit(
                        CustomerCode: customerCode,
                        ShipmentCode: shipment.ShipmentCode,
                        Shipment: shipment,
                        Oaf: oaf,
                        Items: allocatedItems,
                        PrimaryAllocationId: primaryAllocId));
                }
            }

            // Secondary discovery source: Sales Allocation API (SA_API.txt)
            try
            {
                var years = await GetOafYearsForAllocationAsync(customerCode, siteCode, cancellationToken);
                foreach (var yr in years)
                {
                    if (!string.IsNullOrWhiteSpace(request?.OafYear) &&
                        !string.Equals(yr, request.OafYear.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var groups = await GetGroupsForAllocationAsync(yr, customerCode, siteId, cancellationToken);
                    foreach (var grp in groups)
                    {
                        var oafRows = await GetOafNumbersForAllocationAsync(
                            customerCode, siteId, yr, grp, cancellationToken);

                        foreach (var oafRow in oafRows)
                        {
                            if (request?.OafIds != null && request.OafIds.Count > 0 &&
                                !request.OafIds.Contains(oafRow.OafId))
                            {
                                continue;
                            }

                            if (request?.OafNumbers != null && request.OafNumbers.Count > 0 &&
                                !request.OafNumbers.Any(n => MatchesDocNumber(n, oafRow.Number)))
                            {
                                continue;
                            }

                            // If already discovered from DC screen, skip duplicate
                            if (candidates.Any(c => c.Oaf.OafAutoId == oafRow.OafId ||
                                MatchesDocNumber(c.Oaf.OafNumber, oafRow.Number)))
                            {
                                continue;
                            }

                            // Dynamically select valid shipment for this OAF from the customer's actual shipments
                            ErpShipment? matchedShipment = null;
                            foreach (var s in shipments)
                            {
                                var sOafs = await GetOafDetailListAsync(customerCode, s.ShipmentCode, siteId, cancellationToken);
                                if (sOafs.Any(o => o.OafAutoId == oafRow.OafId || MatchesDocNumber(o.OafNumber, oafRow.Number)))
                                {
                                    matchedShipment = s;
                                    break;
                                }
                            }

                            matchedShipment ??= shipments.FirstOrDefault();
                            if (matchedShipment is null)
                            {
                                continue;
                            }

                            var shipment = matchedShipment;
                            var shipCode = matchedShipment.ShipmentCode;
                            var oafDetail = new ErpOafDetail(
                                OafYear: yr,
                                OafGroup: grp,
                                LocationCode: siteCode,
                                OafNumber: oafRow.Number,
                                OafAutoId: oafRow.OafId,
                                IntOrderYear: yr,
                                IntOrderGroup: "SO",
                                IntOrderLocation: siteCode,
                                IntOrderNumber: oafRow.InternalOrdNo,
                                CustomerOrderNumber: oafRow.CustomerPoNo);

                            var oafItems = await GetOafItemDetailsAsync(
                                customerCode,
                                shipCode,
                                yr,
                                grp,
                                oafRow.Number,
                                siteId,
                                cancellationToken);

                            if (oafItems.Count == 0)
                            {
                                continue;
                            }



                            var primaryAllocId = oafItems.FirstOrDefault(i => i.AllocationId > 0)?.AllocationId ?? 0;

                            candidates.Add(new AllocationCandidateUnit(
                                CustomerCode: customerCode,
                                ShipmentCode: shipCode,
                                Shipment: shipment,
                                Oaf: oafDetail,
                                Items: oafItems,
                                PrimaryAllocationId: primaryAllocId));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "SoAllocationToDC: Secondary allocation scan skipped for customer {Cust}: {Msg}",
                    customerCode, ex.Message);
            }
        }

        return candidates;
    }

    // ─────────────────────────────────────────────────────────────────
    //  SINGLE ALLOCATION UNIT SEQUENTIAL PROCESSOR
    // ─────────────────────────────────────────────────────────────────

    private async Task<AllocationDcItemResult> ProcessSingleAllocationUnitAsync(
        AllocationCandidateUnit candidate,
        int siteId,
        string siteCode,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var oaf = candidate.Oaf;
        var shipment = candidate.Shipment;
        var customerCode = candidate.CustomerCode;
        var oafItems = candidate.Items;
        var resolvedOafId = oaf.OafAutoId;

        // ── Validation 1: Already Created DC Check (Duplicate DC Protection) ──
        if (oafItems.Count > 0 && oafItems.All(i => i.MaxDcQtyIuom <= 0))
        {
            _logger.LogInformation(
                "SoAllocationToDC: OAF {OafNo} (id {OafId}) already has a Delivery Challan. Skipping duplicate.",
                oaf.OafNumber, resolvedOafId);

            return new AllocationDcItemResult(
                AllocationId: candidate.PrimaryAllocationId,
                OafId: resolvedOafId,
                OafNo: oaf.OafNumber,
                IntOrdNo: oaf.IntOrderNumber,
                CustomerCode: customerCode,
                ShipmentCode: candidate.ShipmentCode,
                Status: "FAILED",
                Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: "Allocation already has a Delivery Challan. Duplicate DC prevented.",
                        FailedField: "Duplicate",
                        FailedApi: null
                    )).ToList());
        }

        // ── Validation 2: Transporter LOV Resolution (First Valid Value Rule) ──
        var transporters = await GetTransportersAsync(cancellationToken);
        if (transporters.Count == 0)
        {
            throw new DcValidationException(
                "Transporter",
                "Delivery Challan cannot be created because mandatory field 'Transporter' has no available value.");
        }

        // Select the FIRST VALID VALUE from actual API response dynamically
        var selectedTransporter = transporters[0];
        var transporterCode = selectedTransporter.Code;

        var modeOfDispatchCode = await GetFirstModeOfDispatchAsync(cancellationToken);

        _logger.LogInformation(
            "SoAllocationToDC: Selected Transporter: Code='{Code}', Desc='{Desc}', ModeOfDispatch='{Mode}'",
            transporterCode, selectedTransporter.Description, modeOfDispatchCode);

        // ── Validation 3: Document Control & Sales Policy ──
        var dcDate = DateOnly.FromDateTime(DateTime.Now);
        var documentControl = await GetDocumentControlAsync(dcDate, siteId, siteCode, cancellationToken);
        var salesPolicy = await GetSalesPolicyAsync("CTL", cancellationToken);
        var decimalConfig = await GetDigitsAfterDecimalAsync(cancellationToken);

        // ── Validation 4: Resolve Allocation Rows & Items ──
        var allocationRows = oafItems
            .Where(i => i.MaxDcQtyIuom > 0)
            .Select(i => new ErpAllocationRow(
                OafId: i.OafId > 0 ? i.OafId : resolvedOafId,
                ItemCode: i.IntItemCode,
                SalesItemCode: i.SalesItemCode,
                AllocatedQty: i.MaxDcQtyIuom,
                OafDetailLineId: i.OafDetailLineId,
                OafDetailLineNo: i.OafDetailLineNo,
                SoItemLineNo: i.SoItemLineNo,
                AllocationId: i.AllocationId,
                WareHouseId: i.WareHouseId,
                WarehouseCode: i.WarehouseCode))
            .ToList();

        if (allocationRows.Count == 0)
        {
            throw new DcValidationException(
                "Allocation",
                $"No allocated items found for OAF {oaf.OafNumber} (id {resolvedOafId}).");
        }

        // ── Validation 5: Map allocations to DC items ──
        var (mappedDcItems, mappingErrors) = MapAllocationsToDcItems(
            allocationRows, oafItems, resolvedOafId);

        var dcItems = new List<MappedDcItem>();
        var itemErrors = new List<string>();

        if (mappingErrors.Count > 0)
        {
            itemErrors.AddRange(mappingErrors.Select(e => e.Message));
        }

        foreach (var item in mappedDcItems)
        {
            if (item.DcQty <= 0)
            {
                itemErrors.Add($"Item '{item.Allocation.SalesItemCode}' skipped (zero DC quantity).");
            }
            else if (item.DcQty > item.OafItem.MaxDcQtyIuom)
            {
                itemErrors.Add($"Item '{item.Allocation.SalesItemCode}' skipped (DC quantity {item.DcQty} exceeds ERP max allowable DC quantity {item.OafItem.MaxDcQtyIuom}).");
            }
            else
            {
                dcItems.Add(item);
            }
        }

        if (dcItems.Count == 0)
        {
            var alreadyDispatched = oafItems.Count > 0 && oafItems.All(i => i.AlreadyDcQty > 0 && i.MaxDcQtyIuom <= 0);
            var errMessage = alreadyDispatched 
                ? "Allocation already has a Delivery Challan. Duplicate DC prevented."
                : $"No pending dispatchable items found for OAF {oaf.OafNumber} (id {resolvedOafId}). Reasons: {string.Join(" | ", itemErrors)}";

            return new AllocationDcItemResult(
                AllocationId: candidate.PrimaryAllocationId,
                OafId: resolvedOafId,
                OafNo: oaf.OafNumber,
                IntOrdNo: oaf.IntOrderNumber,
                CustomerCode: customerCode,
                ShipmentCode: candidate.ShipmentCode,
                Status: "FAILED",
                Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: errMessage,
                        FailedField: alreadyDispatched ? "Duplicate" : "Quantity",
                        FailedApi: null
                    )).ToList());
        }

        // ── Validation 6: Conversion Factor API ──
        foreach (var dcItem in dcItems)
        {
            var conFactor = await GetConversionFactorAsync(
                dcItem.OafItem.SalesItemCode, resolvedOafId, dcItem.OafItem.OafDetailLineNo, cancellationToken);

            _logger.LogDebug(
                "SoAllocationToDC: Item {ItemCode} ConFactor={Factor}, AllocQty={AllocQty}, DcQty={DcQty}",
                dcItem.OafItem.SalesItemCode, conFactor, dcItem.Allocation.AllocatedQty, dcItem.DcQty);
        }

        // ── Validation 8: Shipment Info Schedule Resolution ──
        var shipmentInfoList = await GetShipmentInfoAsync(
            resolvedOafId, shipment.ShipmentCode, cancellationToken);

        // If the initially selected shipment has no dispatch records for this OAF's items,
        // dynamically check if another shipment for this customer has them
        if (!shipmentInfoList.Any(s => dcItems.Any(d => string.Equals(s.SalesItemCode, d.OafItem.SalesItemCode, StringComparison.OrdinalIgnoreCase))))
        {
            var allShipments = await GetShipmentsAsync(customerCode, cancellationToken);
            foreach (var otherShip in allShipments)
            {
                if (string.Equals(otherShip.ShipmentCode, shipment.ShipmentCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                var otherInfo = await GetShipmentInfoAsync(resolvedOafId, otherShip.ShipmentCode, cancellationToken);
                if (otherInfo.Any(s => dcItems.Any(d => string.Equals(s.SalesItemCode, d.OafItem.SalesItemCode, StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogInformation(
                        "SoAllocationToDC: Dynamically switched shipment for OAF {OafNo} from '{OldShip}' to '{NewShip}' based on pending dispatch records.",
                        oaf.OafNumber, shipment.ShipmentCode, otherShip.ShipmentCode);
                    shipment = otherShip;
                    shipmentInfoList = otherInfo;
                    break;
                }
            }
        }



        // ── Validation 9: Item Combinations ──
        var combinationsByItem = new Dictionary<int, List<(ErpItemCombination Comb, decimal Qty)>>();
        var updatedDcItems = new List<MappedDcItem>();

        foreach (var dcItem in dcItems)
        {
            var combinations = await GetItemCombinationsAsync(
                dcItem.OafItem, resolvedOafId, shipment.ShipmentCode, cancellationToken);

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
                
                _logger.LogInformation("Assigned {TakeQty} to Combination StockId={StockId} for {Code}. OnHand={OnHand}, Alloc={Alloc}", 
                    takeQty, comb.StockId, dcItem.OafItem.SalesItemCode, comb.OnHandQty, comb.Alloc);
            }

            combinationsByItem[dcItem.OafItem.OafDetailLineNo] = assignedCombs;

            var effectiveDcQty = dcItem.DcQty - remainingQty;
            updatedDcItems.Add(dcItem with { DcQty = effectiveDcQty });
        }
        dcItems = updatedDcItems;

        // ── Validation 10: Quantity vs maxDCQtyIUOM ──
        var validDcItems = new List<MappedDcItem>();
        foreach (var item in dcItems)
        {
            if (item.DcQty <= 0)
            {
                itemErrors.Add($"Item '{item.Allocation.SalesItemCode}' skipped (Combination stock restricted DC quantity to {item.DcQty}).");
            }
            else if (item.DcQty > item.OafItem.MaxDcQtyIuom)
            {
                itemErrors.Add($"Item '{item.Allocation.SalesItemCode}' skipped (Combination stock permitted DC quantity {item.DcQty} which exceeds MaxDcQtyIuom {item.OafItem.MaxDcQtyIuom}).");
            }
            else
            {
                validDcItems.Add(item);
            }
        }
        dcItems = validDcItems;

        if (dcItems.Count == 0)
        {
            return new AllocationDcItemResult(
                AllocationId: candidate.PrimaryAllocationId,
                OafId: resolvedOafId,
                OafNo: oaf.OafNumber,
                IntOrdNo: oaf.IntOrderNumber,
                CustomerCode: customerCode,
                ShipmentCode: candidate.ShipmentCode,
                Status: "FAILED",
                Items: candidate.Items.Select(it => new DcItemResult(
                        ItemCode: it.SalesItemCode,
                        Status: "FAILED",
                        DcCreated: false,
                        DcId: null,
                        DcNumber: null,
                        AllocatedQuantity: it.MaxDcQtyIuom,
                        DcQuantity: null,
                        Message: $"No dispatchable items remained after checking item combinations. Reasons: {string.Join(" | ", itemErrors)}",
                        FailedField: "Stock",
                        FailedApi: null
                    )).ToList());
        }

        var totalAllocatedQty = allocationRows.Sum(r => r.AllocatedQty);
        var totalDcQty = dcItems.Sum(i => i.DcQty);

        var resolvedAllocId = candidate.PrimaryAllocationId > 0
            ? candidate.PrimaryAllocationId
            : (int)(combinationsByItem.Values.SelectMany(l => l).FirstOrDefault(x => x.Comb != null && x.Comb.LineNo > 0).Comb?.LineNo ?? 0);

        var userId = await _tokenProvider.GetUserIdAsync(cancellationToken);
        var itemResults = new List<DcItemResult>();

        // ── DRY RUN: Report what would be created without calling createDCEntry ──
        if (dryRun)
        {
            _logger.LogInformation(
                "SoAllocationToDC: [DRY RUN] OAF {OafNo} (id {OafId}) is eligible. {Count} item(s), TotalDcQty={Qty}",
                oaf.OafNumber, resolvedOafId, dcItems.Count, totalDcQty);
                
            foreach(var item in dcItems)
            {
                itemResults.Add(new DcItemResult(
                    ItemCode: item.OafItem.SalesItemCode,
                    Status: "SUCCESS",
                    DcCreated: false,
                    DcId: null,
                    DcNumber: null,
                    AllocatedQuantity: item.OafItem.OafQty,
                    DcQuantity: item.DcQty,
                    Message: "[DRY RUN] Eligible for DC conversion. No DC created."
                ));
            }
        }
        else
        {
            // ── Step 10: Build DC Payload & Submit Batch ──
            var payload = BuildDcPayload(
                documentControl,
                customerCode,
                shipment,
                oaf,
                resolvedOafId,
                dcItems,
                shipmentInfoList,
                combinationsByItem,
                dcDate,
                userId,
                transporterCode: transporterCode,
                modeOfDispatchCode: modeOfDispatchCode);

            _logger.LogInformation(
                "SoAllocationToDC: Submitting DC for OAF {OafNo} (id {OafId}), {Count} item(s), TotalQty={Qty}",
                oaf.OafNumber, resolvedOafId, dcItems.Count, totalDcQty);

            try
            {
                var (dcNumber, dcAutoId) = await SubmitDcAsync(payload, cancellationToken);
                if (string.IsNullOrWhiteSpace(dcNumber) || dcAutoId <= 0)
                    throw new ErpTransientException("DC save completed but returned invalid DC identifiers", "createDCEntry");

                _logger.LogInformation("SoAllocationToDC: DC {DcNumber} (id {DcId}) created successfully for OAF {OafNo}.", dcNumber, dcAutoId, oaf.OafNumber);

                foreach (var item in dcItems)
                {
                    itemResults.Add(new DcItemResult(
                        ItemCode: item.OafItem.SalesItemCode,
                        Status: "SUCCESS",
                        DcCreated: true,
                        DcId: dcAutoId,
                        DcNumber: dcNumber,
                        AllocatedQuantity: item.OafItem.OafQty,
                        DcQuantity: item.DcQty,
                        Message: "Delivery Challan created successfully."
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SoAllocationToDC: Batch save failed for OAF {OafNo}. Falling back to individual item processing.", oaf.OafNumber);
                
                // Fallback to individual items if multiple items
                if (dcItems.Count <= 1)
                {
                    var singleItem = dcItems.FirstOrDefault();
                    if (singleItem != null)
                    {
                        var (cleanMessage, field, api) = NormalizeErpError(ex.Message);
                        itemResults.Add(new DcItemResult(
                            ItemCode: singleItem.OafItem.SalesItemCode,
                            Status: "FAILED",
                            DcCreated: false,
                            DcId: null,
                            DcNumber: null,
                            AllocatedQuantity: singleItem.OafItem.OafQty,
                            DcQuantity: null,
                            Message: cleanMessage,
                            FailedField: field,
                            FailedApi: api
                        ));
                    }
                }
                else
                {
                    foreach (var item in dcItems)
                    {
                        var singlePayload = BuildDcPayload(
                            documentControl,
                            customerCode,
                            shipment,
                            oaf,
                            resolvedOafId,
                            new List<MappedDcItem> { item },
                            shipmentInfoList,
                            combinationsByItem,
                            dcDate,
                            userId,
                            transporterCode: transporterCode,
                            modeOfDispatchCode: modeOfDispatchCode);
                            
                        try
                        {
                            var (dcNumber, dcAutoId) = await SubmitDcAsync(singlePayload, cancellationToken);
                            if (string.IsNullOrWhiteSpace(dcNumber) || dcAutoId <= 0)
                                throw new ErpTransientException("DC save completed but returned invalid DC identifiers", "createDCEntry");

                            itemResults.Add(new DcItemResult(
                                ItemCode: item.OafItem.SalesItemCode,
                                Status: "SUCCESS",
                                DcCreated: true,
                                DcId: dcAutoId,
                                DcNumber: dcNumber,
                                AllocatedQuantity: item.OafItem.OafQty,
                                DcQuantity: item.DcQty,
                                Message: "Delivery Challan created successfully."
                            ));
                        }
                        catch (Exception itemEx)
                        {
                            var (cleanMessage, field, api) = NormalizeErpError(itemEx.Message);
                            itemResults.Add(new DcItemResult(
                                ItemCode: item.OafItem.SalesItemCode,
                                Status: "FAILED",
                                DcCreated: false,
                                DcId: null,
                                DcNumber: null,
                                AllocatedQuantity: item.OafItem.OafQty,
                                DcQuantity: null,
                                Message: cleanMessage,
                                FailedField: field,
                                FailedApi: api
                            ));
                        }
                    }
                }
            }
        }

        var successCount = itemResults.Count(i => i.Status == "SUCCESS");
        var status = successCount == itemResults.Count ? "SUCCESS" 
                     : successCount > 0 ? "PARTIAL_SUCCESS" 
                     : "FAILED";

        return new AllocationDcItemResult(
            AllocationId: resolvedAllocId,
            OafId: resolvedOafId,
            OafNo: oaf.OafNumber,
            IntOrdNo: oaf.IntOrderNumber,
            CustomerCode: customerCode,
            ShipmentCode: candidate.ShipmentCode,
            Status: status,
            Items: itemResults
        );
    }

    // ─────────────────────────────────────────────────────────────────
    //  ERP CALLS (Discovered from SA_API.txt and DC_API.txt)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Customers for allocation:
    /// <c>GET /api/v1/sales/SalesAllocationDeAllocation/getCustomerForAllocationDeAllocation/{siteId}/A/L?CUSTCD=</c>.
    /// </summary>
    private async Task<IReadOnlyList<ErpAllocationCustomer>> GetCustomersForAllocationAsync(
        int siteId,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/SalesAllocationDeAllocation/getCustomerForAllocationDeAllocation/{siteId}/A/L?CUSTCD=";

        try
        {
            var searchBody = new
            {
                pageNumber = 1,
                pageSize = 100,
                sortField = "",
                sortDirection = "asc",
                searchValue = "",
                userId = 1,
                COMPID = 1
            };
            var response = await _httpClient.PostAsJsonAsync(url, searchBody, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response = await _httpClient.GetAsync(url, cancellationToken);
            }
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            if (envelope?.Data is null)
            {
                return [];
            }

            return envelope.Data
                .Select(node => new ErpAllocationCustomer(
                    CustCode: node["custCode"]?.GetValue<string>() ?? "",
                    CustName: node["custName"]?.GetValue<string>() ?? "",
                    City: node["city"]?.GetValue<string>(),
                    State: node["state"]?.GetValue<string>(),
                    Country: node["country"]?.GetValue<string>()))
                .Where(c => !string.IsNullOrWhiteSpace(c.CustCode))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoAllocationToDC: GetCustomersForAllocation failed: {Msg}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Customers for Delivery Challan entry:
    /// <c>POST /api/v1/sales/deliverychallanentry/getCustomerList/L/F</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCustomersForDcAsync(
        int siteId,
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/sales/deliverychallanentry/getCustomerList/L/F";

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                pageNumber = 1,
                pageSize = 100,
                sortField = "",
                sortDirection = "asc",
                searchValue = "",
                siteId = siteId
            }, SerializerOptions);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            if (envelope?.Data is null)
            {
                return [];
            }

            return envelope.Data
                .Select(node => node["customerCode"]?.GetValue<string>() ?? "")
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoAllocationToDC: GetCustomersForDc failed: {Msg}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Transporters list:
    /// <c>GET /api/v1/admin/code/getCodeTypeList/TR/GEN</c>.
    /// </summary>
    private async Task<IReadOnlyList<ErpTransporter>> GetTransportersAsync(
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/admin/code/getCodeTypeList/TR/GEN";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpTransporter(
                Code: node["code"]?.GetValue<string>() ?? "",
                Description: node["description"]?.GetValue<string>() ?? "",
                CodeFullName: node["codeFullName"]?.GetValue<string>() ?? ""))
            .Where(t => !string.IsNullOrWhiteSpace(t.Code))
            .ToList();
    }

    /// <summary>
    /// Mode of Dispatches list:
    /// <c>GET /api/v1/admin/code/getCodeTypeList/MOD/GEN</c>.
    /// </summary>
    private async Task<string> GetFirstModeOfDispatchAsync(CancellationToken cancellationToken)
    {
        const string url = "/api/v1/admin/code/getCodeTypeList/MOD/GEN";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        EnsureSuccess(response, url);

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return string.Empty;
        }

        var modes = envelope.Data
            .Select(node => node["code"]?.GetValue<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList();

        return modes.Count > 0 ? modes[0]! : string.Empty;
    }

    /// <summary>
    /// Conversion factor:
    /// <c>POST /api/v1/sales/deliverychallanentry/getConversionFactorpost</c>.
    /// </summary>
    private async Task<decimal> GetConversionFactorAsync(
        string itemCode,
        int oafAutoId,
        int itemLineNo,
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/sales/deliverychallanentry/getConversionFactorpost";

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                itemcode = itemCode,
                oafautoid = oafAutoId,
                itemLineNo = itemLineNo
            }, SerializerOptions);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return 1.0m;
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            var row = envelope?.Data?.FirstOrDefault();
            if (row != null && row["conFactor"] is not null)
            {
                return row["conFactor"]!.GetValue<decimal>();
            }

            return 1.0m;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoAllocationToDC: GetConversionFactor failed for {ItemCode}: {Msg}", itemCode, ex.Message);
            return 1.0m;
        }
    }

    /// <summary>
    /// Digits after decimal precision:
    /// <c>GET /api/v1/admin/code/GetDegitAfterDecimal</c>.
    /// </summary>
    private async Task<ErpDecimalConfig> GetDigitsAfterDecimalAsync(
        CancellationToken cancellationToken)
    {
        const string url = "/api/v1/admin/code/GetDegitAfterDecimal";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ErpDecimalConfig(0);
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            var row = envelope?.Data?.FirstOrDefault();
            if (row != null && int.TryParse(row["mcddigaftdec"]?.GetValue<string>(), out var digits))
            {
                return new ErpDecimalConfig(digits);
            }

            return new ErpDecimalConfig(0);
        }
        catch
        {
            return new ErpDecimalConfig(0);
        }
    }

    /// <summary>
    /// Sales Policy:
    /// <c>GET /api/v1/admin/salespolicy/getDetail/{companyCode}</c>.
    /// </summary>
    private async Task<ErpSalesPolicy> GetSalesPolicyAsync(
        string companyCode,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/admin/salespolicy/getDetail/{Uri.EscapeDataString(companyCode)}";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ErpSalesPolicy(companyCode, true, false);
            }

            var envelope = await response.Content.ReadFromJsonAsync<JsonObject>(
                SerializerOptions, cancellationToken);

            var models = envelope?["data"]?["salesPolicyResultModel"] as JsonArray;
            var first = models?.FirstOrDefault();

            var isDcBasePack = first?["isdcbasepack"]?.GetValue<bool>() ?? true;
            var autoAlloc = first?["autoallocitmonoafcreate"]?.GetValue<bool>() ?? false;

            return new ErpSalesPolicy(companyCode, isDcBasePack, autoAlloc);
        }
        catch
        {
            return new ErpSalesPolicy(companyCode, true, false);
        }
    }

    /// <summary>
    /// OAF years for allocation:
    /// <c>GET /api/v1/sales/SalesAllocationDeAllocation/getOafYearList/{customerCode}/{siteCode}/A</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetOafYearsForAllocationAsync(
        string customerCode,
        string siteCode,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/SalesAllocationDeAllocation/getOafYearList/" +
                  $"{Uri.EscapeDataString(customerCode)}/{Uri.EscapeDataString(siteCode)}/A";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => node["year"]?.GetValue<string>() ?? "")
            .Where(y => !string.IsNullOrWhiteSpace(y))
            .ToList();
    }

    /// <summary>
    /// Groups for allocation:
    /// <c>GET /api/v1/sales/SalesAllocationDeAllocation/GetGroupForAllocationDeAllocation/{year}/A/{customerCode}/{siteId}/A/2/1</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetGroupsForAllocationAsync(
        string year,
        string customerCode,
        int siteId,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/SalesAllocationDeAllocation/GetGroupForAllocationDeAllocation/" +
                  $"{Uri.EscapeDataString(year)}/A/{Uri.EscapeDataString(customerCode)}/{siteId}/A/2/1";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => node["group"]?.GetValue<string>() ?? "")
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToList();
    }

    /// <summary>
    /// OAF numbers for allocation:
    /// <c>GET /api/v1/sales/SalesAllocationDeAllocation/GetNumberForAllocationDeAllocation/{customerCode}/{siteId}/{year}/{group}/{siteId}/A</c>.
    /// </summary>
    private async Task<IReadOnlyList<ErpAllocationOafNumberRow>> GetOafNumbersForAllocationAsync(
        string customerCode,
        int siteId,
        string year,
        string group,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/SalesAllocationDeAllocation/GetNumberForAllocationDeAllocation/" +
                  $"{Uri.EscapeDataString(customerCode)}/{siteId}/{Uri.EscapeDataString(year)}/{Uri.EscapeDataString(group)}/{siteId}/A";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
            SerializerOptions, cancellationToken);

        if (envelope?.Data is null)
        {
            return [];
        }

        return envelope.Data
            .Select(node => new ErpAllocationOafNumberRow(
                Number: node["number"]?.GetValue<string>() ?? "",
                OafId: node["oafid"]?.GetValue<int>() ?? 0,
                SalesOrdType: node["salesOrdType"]?.GetValue<string>(),
                CustomerPoNo: node["customerPONo"]?.GetValue<string>() ?? "",
                InternalOrdNo: node["internalOrdNo"]?.GetValue<string>() ?? "",
                OafDate: node["oafDate"]?.GetValue<string>()))
            .Where(r => !string.IsNullOrWhiteSpace(r.Number) && r.OafId > 0)
            .ToList();
    }

    private sealed record ErpAllocationOafNumberRow(
        string Number,
        int OafId,
        string? SalesOrdType,
        string CustomerPoNo,
        string InternalOrdNo,
        string? OafDate);

    private static (string Message, string FailedField, string FailedApi) NormalizeErpError(string rawError, string defaultApi = "createDCEntry")
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return ("An unexpected ERP error occurred.", "ERP", defaultApi);

        var jsonStart = rawError.IndexOf('{');
        var jsonEnd = rawError.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = rawError.Substring(jsonStart, jsonEnd - jsonStart + 1);
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                string? extracted = null;

                if (root.TryGetProperty("errorMessage", out var errMsg) && errMsg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errMsg.GetString()))
                {
                    extracted = errMsg.GetString();
                }
                else if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errs.GetString()))
                {
                    extracted = errs.GetString();
                }
                else if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(msg.GetString()))
                {
                    extracted = msg.GetString();
                }

                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    extracted = extracted.Replace("\r\n", " ").Replace("\n", " ").Trim();
                    var field = extracted.Contains("XSTKALC", StringComparison.OrdinalIgnoreCase) || extracted.Contains("stock", StringComparison.OrdinalIgnoreCase)
                        ? "Stock"
                        : "createDCEntry";
                    return (extracted, field, defaultApi);
                }
            }
            catch
            {
                // Fall back
            }
        }

        return (rawError.Trim(), "ERP", defaultApi);
    }

    private async Task<IReadOnlyList<ErpExistingDcSummary>> GetExistingDcsAsync(
        string oafNumber,
        int siteId,
        CancellationToken cancellationToken)
    {
        var url = $"/api/v1/sales/deliverychallanentry/dCEntryList/{siteId}?status=";
        var body = JsonSerializer.Serialize(new
        {
            pageNumber = 1,
            pageSize = 20,
            sortField = "",
            sortDirection = "desc",
            searchValue = oafNumber,
            userId = 2,
            comCode = "CTL",
            docSubType = "DS",
            docType = "DC",
            mulLvlAuthRed = false
        }, SerializerOptions);

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var envelope = await response.Content.ReadFromJsonAsync<ErpEnvelopeList<JsonObject>>(
                SerializerOptions, cancellationToken);

            if (envelope?.Data is null)
            {
                return [];
            }

            return envelope.Data
                .Select(node => new ErpExistingDcSummary(
                    DcNumber: node["DCNumber"]?.GetValue<string>() ?? "",
                    DcYear: node["DCYear"]?.GetValue<string>() ?? "",
                    DcGroup: node["DCGroup"]?.GetValue<string>() ?? "",
                    SiteCode: node["SiteCode"]?.GetValue<string>() ?? "",
                    DcAutoId: node["DCAutoId"]?.GetValue<long>() ?? 0,
                    OafNumber: node["OAFNumber"]?.GetValue<string>() ?? "",
                    DcStatus: node["DCStatus"]?.GetValue<string>() ?? ""))
                .Where(d => !string.IsNullOrWhiteSpace(d.DcNumber))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoAllocationToDC: dCEntryList query failed for OAF {OafNo}", oafNumber);
            return [];
        }
    }

    private sealed record ErpExistingDcSummary(
        string DcNumber,
        string DcYear,
        string DcGroup,
        string SiteCode,
        long DcAutoId,
        string OafNumber,
        string DcStatus);
}
