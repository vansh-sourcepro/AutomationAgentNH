using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Turns one authorised Service Indent into Service Purchase Orders.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as the material conversion in <c>IndentConversion.cs</c> — discover the indent,
/// resolve a vendor per item, group by the ERP's own break key, run the create sequence once per
/// group, record a refusal as a note rather than throwing — and a different set of ERP calls
/// underneath, because Service Indent → Service PO is a separate module rather than a variant of
/// the material one. Seven calls where the material sequence takes nine or ten: a service line has
/// no warehouse to resolve and no purchase/internal UOM conversion to look up.
/// </para>
/// <para>
/// Idempotency is the ERP's here too, and by the same mechanism: <c>CSP_XPOSDTL_Insert</c> raises
/// <c>XINDSDTL.XINDSDPOQTY</c> by the ordered quantity, closes the line when it reaches
/// <c>XINDSDINDQTY</c>, and closes the whole indent when every line is closed. A second sweep finds
/// the line gone from the pending-lines query and the indent no longer open, so it orders nothing.
/// The ERP refuses an over-order outright, which is the backstop.
/// </para>
/// </remarks>
public sealed partial class IndentToPoService
{
    /// <summary>
    /// What one run of the Service PO sequence is asked to produce: everything the indent's items
    /// that resolve to these vendor terms still have outstanding.
    /// </summary>
    private sealed record ServicePoBuildRequest(
        ServiceIndentRecord Indent,
        VendorTerms Terms,
        IReadOnlyList<string> ItemCodes);

    private ServicePoSystemFlags? _servicePoSystemFlags;

    private async Task<IndentConversionResult> ConvertServiceIndentAsync(
        IndentReference indent,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var record = await GetServiceIndentAsync(
            indent.IndentId,
            indent.Year,
            indent.Group,
            indent.SiteCode,
            indent.Number,
            listRow: null,
            cancellationToken)
            ?? throw new ErpBusinessException(
                $"Service indent {indent.DisplayNumber} could not be read from the ERP.",
                $"getSerIndentDetail returned no header for XINDAUTOID {indent.IndentId}.",
                _endpoints.ServiceIndentDetail);

        GuardServiceIndentIsConvertible(indent, record);

        var reference = record.ToReference();
        var notes = new List<string>();

        // Group the indent's items by the vendor terms the ERP would offer for each. Same break key
        // the material flow uses and the same one the ERP's own bulk generation uses, so an indent
        // whose services come from two vendors becomes two orders rather than one impossible one.
        var groups = new Dictionary<VendorTerms, List<string>>();

        foreach (var itemCode in record.ItemCodes)
        {
            var terms = await ResolveServiceVendorTermsAsync(itemCode, cancellationToken);

            if (terms is null)
            {
                notes.Add(
                    $"Service item {itemCode} has no item/vendor record in the ERP, so no vendor can "
                    + "be asked to supply it. Add one in Item Master - Service, then run again.");
                continue;
            }

            if (terms.RateStructure.Length == 0)
            {
                notes.Add(
                    $"Service item {itemCode} names vendor {terms.VendorCode} but no rate structure, "
                    + "so its taxes cannot be worked out. Set one in Item Master - Service.");
                continue;
            }

            if (!groups.TryGetValue(terms, out var items))
            {
                items = [];
                groups[terms] = items;
            }

            items.Add(itemCode);
        }

        if (groups.Count == 0)
        {
            if (notes.Count == 0)
            {
                notes.Add("This service indent has no item lines left to order.");
            }

            _logger.LogInformation(
                "Service indent {IndentNumber} has nothing that can be ordered: {Notes}",
                reference.DisplayNumber,
                string.Join(" ", notes));

            return new IndentConversionResult(reference, [], notes);
        }

        var purchaseOrders = new List<IndentPoResult>(groups.Count);
        var planned = 0;

        foreach (var (terms, items) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                var plan =
                    $"Would raise a service purchase order on {terms.Describe()} at site "
                    + $"{record.SiteId} for {items.Count} service item(s): {string.Join(", ", items)}.";

                notes.Add(plan);
                planned++;

                // Logged, not just returned: a dry run started by the timer has no caller to read
                // the response, and the log is the only place its findings can land.
                _logger.LogInformation("Service indent {IndentNumber}: {Plan}", reference.DisplayNumber, plan);

                continue;
            }

            try
            {
                purchaseOrders.Add(await RunServiceSequenceAsync(
                    new ServicePoBuildRequest(record, terms, items),
                    cancellationToken));
            }
            catch (ErpException ex)
            {
                // A refusal — business or transient — for one vendor leaves the others, and any
                // purchase order(s) already created in earlier groups, untouched: recorded as a note
                // rather than thrown, so a transient hiccup on this group can never discard an earlier
                // group's real, already-created purchase order by unwinding past `purchaseOrders`.
                notes.Add($"{terms.Describe()}: {ex.LaymanMessage}");
            }
        }

        if (!dryRun)
        {
            if (purchaseOrders.Count == 0 && notes.Count > 0)
            {
                _logger.LogWarning(
                    "Service indent {IndentNumber} produced no purchase order: {Reasons}",
                    reference.DisplayNumber,
                    string.Join(" ", notes));
            }
            else
            {
                _logger.LogInformation(
                    "Service indent {IndentNumber} produced {Count} purchase order(s): {PoNumbers}{Reasons}",
                    reference.DisplayNumber,
                    purchaseOrders.Count,
                    purchaseOrders.Count == 0 ? "none" : string.Join(", ", purchaseOrders.Select(po => po.PoNumber)),
                    notes.Count == 0 ? string.Empty : $". Not ordered: {string.Join(" ", notes)}");
            }
        }

        return new IndentConversionResult(reference, purchaseOrders, notes, planned);
    }

    /// <summary>The ERP call sequence for one Service PO.</summary>
    private async Task<IndentPoResult> RunServiceSequenceAsync(
        ServicePoBuildRequest request,
        CancellationToken cancellationToken)
    {
        var profile = IndentPoProfile.For(IndentType.Service);
        var record = request.Indent;
        var siteId = RequireSite(record);
        var poDate = DateOnly.FromDateTime(_clock.UtcNow.LocalDateTime);
        var reference = record.ToReference();

        _logger.LogInformation(
            "Creating a service purchase order at site {SiteId} for vendor {VendorCode} dated "
            + "{PoDate:yyyy-MM-dd}, limited to service indent {IndentNumber}",
            siteId,
            request.Terms.VendorCode,
            poDate,
            reference.DisplayNumber);

        var userId = await _tokenProvider.GetUserIdAsync(cancellationToken);

        // 1. How the order is numbered, and in which financial year. Same Document Control lookup
        //    the material flow uses, keyed on this profile's PR/SP instead of PR/RP.
        var documentControl = await GetDocumentControlAsync(profile, siteId, poDate, cancellationToken);

        GuardAutoNumbering(documentControl, siteId, reference);

        if (documentControl.LocationId != siteId)
        {
            // The order has to be raised at the indent's site or the ERP's pending-lines query
            // (XINDSHSITE = @MBMBPLOCID) matches nothing. Document Control's own site is followed
            // on the material side; here it is reported and the indent's site wins.
            _logger.LogWarning(
                "Document Control answered with site {DocumentControlSite} for a {DocSubType} order, "
                + "but service indent {IndentNumber} was raised at site {IndentSite}; using the "
                + "indent's site, which is the only one its outstanding lines can be found at",
                documentControl.LocationId,
                profile.DocSubType,
                reference.DisplayNumber,
                siteId);
        }

        // 2. The site's own country and state, for the domestic/import and reverse-charge
        //    decisions. A service order carries no delivery address, but this is where the ERP
        //    keeps the site's codes and the material flow already asks for them.
        var address = await GetDeliveryAddressAsync(siteId, cancellationToken);

        // 3. The vendor: currency (checked against the ERP's own), terms, GSTIN, country, state.
        var vendor = await GetVendorAsync(request.Terms.VendorCode, poDate, cancellationToken);

        // 4. The tax component template, for the one field the pricing call does not return.
        var rateStructureDetail = await GetJsonArrayAsync(
            _endpoints.RateStructureDetail(request.Terms.RateStructure),
            cancellationToken);

        // 5. The two installation flags the ERP's save validation branches on.
        var systemFlags = await GetServicePoSystemFlagsAsync(cancellationToken);

        // 6. The outstanding service-indent lines. This is the call that decides what the order
        //    contains, and the one that answers "already converted" by returning nothing.
        var lines = await GetPendingServiceLinesAsync(
            request, siteId, poDate, cancellationToken);

        // 7. Per line, the ERP's own pricing of it.
        var totals = new List<ServicePoLineTotals>(lines.Count);
        var itemLine = 1;

        foreach (var line in lines)
        {
            var components = await GetRateComponentsForAsync(
                DiscountedRate(line),
                request.Terms.RateStructure,
                vendor.Currency,
                cancellationToken);

            totals.Add(ServicePoLineTotals.For(line, itemLine, components));
            itemLine++;
        }

        var context = new ServicePoContext
        {
            Indent = reference with { SiteId = siteId },
            Profile = profile,
            Options = _options,
            UserId = userId,
            PoDate = poDate,
            DocumentControl = documentControl,
            Vendor = vendor,
            SiteCountryCode = address.CountryCode,
            SiteStateCode = address.StateCode,
            RateStructureCode = request.Terms.RateStructure,
            RateStructureDetail = rateStructureDetail,
            SystemFlags = systemFlags,
            IndentDate = record.IndentDate is { } date ? ServicePoPayloadBuilder.ErpDate(date) : null,
            Lines = totals,
        };

        var payload = ServicePoPayloadBuilder.Build(context);

        // 8. Create.
        return await SubmitServicePurchaseOrderAsync(payload, context, cancellationToken);
    }

    /// <summary>
    /// The indent's outstanding lines for these vendor terms, narrowed to this indent and these
    /// items.
    /// </summary>
    /// <remarks>
    /// The ERP answers this per vendor and site, not per indent — it hands back everything that
    /// vendor has outstanding across every authorised service indent at the site — so the narrowing
    /// happens here, on <c>AUTOID</c>, exactly as the material flow narrows on <c>pidindid</c>.
    /// Ordering another indent's lines by accident would be a real and silent overreach.
    /// </remarks>
    private async Task<IReadOnlyList<ServiceIndentLine>> GetPendingServiceLinesAsync(
        ServicePoBuildRequest request,
        int siteId,
        DateOnly poDate,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            // Ignored by the ERP for an indent-based request — its procedure does not filter on it
            // — but the screen sends a space and the parameter is not optional.
            ["itemCode"] = " ",
            ["rateCode"] = request.Terms.RateStructure,
            ["vendorCode"] = request.Terms.VendorCode,
            ["buyerCode"] = string.IsNullOrWhiteSpace(_options.BuyerCode) ? " " : _options.BuyerCode,
            // "IN" as against "PO": indent-based, not direct.
            ["poType"] = "IN",
            ["mode"] = string.Empty,
            ["poDate"] = ServicePoPayloadBuilder.ErpDate(poDate),
            ["LocId"] = siteId,
            ["sonumber"] = 0,
            ["isbudgetso"] = false,
        };

        var answer = await PostJsonArrayAsync(_endpoints.PendingServiceIndentItems, body, cancellationToken);
        var view = answer.OfType<JsonObject>().FirstOrDefault();

        var wantedItems = request.ItemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deliveries = view?["itemDeliveryDetail"].Objects().ToList() ?? [];
        var lines = new List<ServiceIndentLine>();
        var skipped = new List<string>();

        foreach (var row in view?["itemDetail"].Objects() ?? [])
        {
            if ((long)row.Decimal("indentId") != request.Indent.IndentId)
            {
                continue;
            }

            var itemCode = row.String("itemCode").Trim();

            if (itemCode.Length == 0 || !wantedItems.Contains(itemCode))
            {
                continue;
            }

            // The ERP's own srno on this answer, which its delivery rows carry. It numbers every
            // pending line for the vendor, so it survives only as the join key — the line number
            // the order is written with is assigned afterwards, over the lines actually taken.
            var pendingRow = row.Int("srno");

            var deliveryDates = deliveries
                .Where(delivery => delivery.Int("srno") == pendingRow)
                .Select(delivery => delivery.ErpDate("deldate"))
                .Where(date => date.Length > 0)
                .ToList();

            var line = new ServiceIndentLine(
                IndentId: request.Indent.IndentId,
                ItemCode: itemCode,
                ItemDescription: row.String("itemDesc"),
                Uom: row.String("uom").Trim(),
                SacCode: row.String("saccode").Trim(),
                RateStructureCode: request.Terms.RateStructure,
                IndentLineNumber: row.Int("indentLineNo"),
                PendingRowNumber: pendingRow,
                IndentQuantity: row.Decimal("indentQty"),
                OrderedQuantity: row.Decimal("alrPoQty"),
                ShortClosedQuantity: row.Decimal("indentShortClose"),
                BasicPrice: row.Decimal("basicprice"),
                DiscountType: DiscountTypeName(row.String("disctype")),
                DiscountValue: row.Decimal("discvalue"),
                ServiceFromDate: row.ErpDateOrNull("serreq"),
                ServiceToDate: row.ErpDateOrNull("todate"),
                DeliveryDates: deliveryDates);

            if (line.Outstanding <= 0m)
            {
                // Already fully ordered. Not a problem and not worth a note: it is what a second
                // conversion of the same indent looks like.
                continue;
            }

            if (line.BasicPrice <= 0m)
            {
                skipped.Add(
                    $"service item {itemCode} has no rate for vendor {request.Terms.VendorCode}");
                continue;
            }

            if (deliveryDates.Count == 0)
            {
                // The ERP refuses an order line with no delivery row, and the only dates available
                // are the indent's own. Naming the gap beats posting something it will reject.
                skipped.Add($"service item {itemCode} has no delivery date on the indent");
                continue;
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            throw new ErpBusinessException(
                skipped.Count == 0
                    ? $"Service indent {request.Indent.Number} has nothing left to order from vendor "
                        + $"'{request.Terms.VendorCode}'."
                    : $"Nothing can be ordered from vendor '{request.Terms.VendorCode}': "
                        + string.Join("; ", skipped) + ".",
                $"'{_endpoints.PendingServiceIndentItems}' returned no usable line for XINDAUTOID "
                + $"{request.Indent.IndentId} at site {siteId}.",
                _endpoints.PendingServiceIndentItems);
        }

        return lines;
    }

    private async Task<IndentPoResult> SubmitServicePurchaseOrderAsync(
        JsonObject payload,
        ServicePoContext context,
        CancellationToken cancellationToken)
    {
        // Not retried at the transport level — same reasoning as the material create call in
        // SubmitPurchaseOrderAsync: a replayed create would skip the fresh pending-lines re-check
        // just performed and could raise a second, real service purchase order.
        var created = await PostJsonObjectAsync(
            _endpoints.CreateServicePurchaseOrder, payload, cancellationToken, retryable: false);

        var header = created["headerDetail"] as JsonObject;
        var number = header.String("poNumber");

        if (string.IsNullOrWhiteSpace(number))
        {
            throw new ErpTransientException(
                "The ERP accepted the service purchase order but did not return its number.",
                $"'{_endpoints.CreateServicePurchaseOrder}' reported success with no "
                + "data.headerDetail.poNumber.",
                _endpoints.CreateServicePurchaseOrder);
        }

        // The ERP returns only the serial; the number a person reads is the same four parts the
        // create call echoes back, joined the way every other document number in the ERP is.
        var poNumber = string.Join(
            '/',
            header.String("poYear", context.DocumentControl.FinancialYear),
            header.String("poGroup", context.DocumentControl.GroupCode),
            context.Indent.SiteCode,
            number);

        var poId = (long)header.Decimal("autoId");

        _logger.LogInformation(
            "Created service purchase order {PoNumber} (id {PoId}) for vendor {VendorCode} with "
            + "{ItemCount} service item(s) from indent {IndentNumber}",
            poNumber,
            poId,
            context.Vendor.VendorCode,
            context.Lines.Count,
            context.Indent.DisplayNumber);

        return new IndentPoResult(
            poNumber,
            poId,
            context.Vendor.VendorCode,
            context.Lines.Count,
            context.Vendor.Currency,
            context.RateStructureCode);
    }

    /// <summary>
    /// The two installation-wide flags the Service PO save validation reads, asked for once per
    /// scope.
    /// </summary>
    /// <remarks>
    /// A refusal from either endpoint falls back to "off, off" with a warning rather than failing
    /// the conversion: those are the values every installation seen so far reports, and they are
    /// also the stricter pair — the validation they select actually checks the rate structure, so a
    /// wrong guess here surfaces as a refusal from the ERP rather than as a mis-stamped order.
    /// </remarks>
    private async Task<ServicePoSystemFlags> GetServicePoSystemFlagsAsync(CancellationToken cancellationToken)
    {
        if (_servicePoSystemFlags is { } cached)
        {
            return cached;
        }

        var lineLevel = false;
        var nonGst = false;

        try
        {
            var policy = await GetJsonObjectAsync(_endpoints.PurchasePolicy, cancellationToken);
            lineLevel = (policy["purchasePolicyModel"] as JsonObject).Flag("enableLineLevelRateStructure");
        }
        catch (ErpBusinessException ex)
        {
            _logger.LogWarning(
                "Could not read the purchase policy ({Reason}); assuming line-level rate structures "
                + "are off, which is what the header-level rate structure this flow sends expects",
                ex.TechnicalMessage);
        }

        try
        {
            var configuration = await GetJsonObjectAsync(_endpoints.TngConfiguration, cancellationToken);
            nonGst = configuration.Flag("nonGST");
        }
        catch (ErpBusinessException ex)
        {
            _logger.LogWarning(
                "Could not read the system configuration ({Reason}); assuming GST is in use",
                ex.TechnicalMessage);
        }

        var flags = new ServicePoSystemFlags(lineLevel, nonGst);
        _servicePoSystemFlags = flags;

        return flags;
    }

    private static void GuardServiceIndentIsConvertible(IndentReference indent, ServiceIndentRecord record)
    {
        if (!record.IsAuthorised)
        {
            throw new ErpBusinessException(
                $"Service indent {indent.DisplayNumber} has not been authorised, so it cannot become "
                + "a purchase order.",
                $"getSerIndentDetail reported no authoriser for XINDAUTOID {record.IndentId}.");
        }

        if (!record.IsOpen)
        {
            throw new ErpBusinessException(
                $"Service indent {indent.DisplayNumber} is {record.DocumentStatusDisplay.ToLowerInvariant()}, "
                + "so there is nothing left on it to order.",
                $"getSerIndentDetail reported XINDSHSTATUS '{record.DocumentStatus}' for XINDAUTOID "
                + $"{record.IndentId}.");
        }
    }

    /// <summary>
    /// A site whose Document Control does not number Service POs automatically cannot be served:
    /// the alternative is for the agent to invent a document number, which is not its business and
    /// would collide with the next one a person types.
    /// </summary>
    private static void GuardAutoNumbering(
        DocumentControlDefaults documentControl,
        int siteId,
        IndentReference indent)
    {
        if (documentControl.AutoNumberRequired.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ErpBusinessException(
            $"Document Control does not generate service purchase order numbers automatically at "
            + $"site {siteId}, so {indent.DisplayNumber} cannot be converted without a person "
            + "choosing the number.",
            $"getDefaultDocumentDetail reported isAutoNumberGenerated false for PR/SP at site {siteId} "
            + $"in financial year {documentControl.FinancialYear}.");
    }

    /// <summary>
    /// The ERP stores the item/vendor discount type as one letter and its screen expands it before
    /// posting it back. <c>PIDSDiscTyp</c> is a single character, so the ERP takes the first letter
    /// of whatever it is sent — but the arithmetic here reads the expanded word, so expanding it is
    /// not cosmetic.
    /// </summary>
    private static string DiscountTypeName(string erpDiscountType) =>
        erpDiscountType.Trim().ToUpperInvariant() switch
        {
            "P" or "PERCENTAGE" => "Percentage",
            "V" or "VALUE" => "Value",
            _ => "None",
        };

    private static decimal DiscountedRate(ServiceIndentLine line) =>
        line.DiscountType.Equals("Percentage", StringComparison.OrdinalIgnoreCase)
            ? line.BasicPrice - (line.BasicPrice * line.DiscountValue / 100m)
            : line.BasicPrice - line.DiscountValue;
}
