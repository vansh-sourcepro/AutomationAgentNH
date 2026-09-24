using System.Globalization;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Assembles the <c>ServicePO/createServicePOEntry</c> body from what the ERP has already told the
/// agent.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no HTTP, no clock, no configuration lookup. Everything it needs arrives in
/// <see cref="ServicePoContext"/>, which is what lets the interesting half of this feature be
/// tested without an ERP.
/// </para>
/// <para>
/// Unlike <see cref="IndentPoPayloadBuilder"/>, which clones the ERP's own item rows and decorates
/// them, this one writes every property out. That is not a different philosophy but a different
/// endpoint: <c>createServicePOEntry</c> binds a hand-written twenty-odd-property DTO
/// (<c>ServicePOEntryModel</c>, in <c>WebAPICore/NewHorizon.Model/Purchase/ServicePOModel.cs</c>)
/// and ignores everything else, so echoing the original row back would preserve nothing.
/// </para>
/// </remarks>
internal static class ServicePoPayloadBuilder
{
    /// <summary>Nothing here amends an existing order, so every amendment serial is zero.</summary>
    private const int AmendmentSerial = 0;

    public static JsonObject Build(ServicePoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Lines.Count == 0)
        {
            throw new InvalidOperationException("A service purchase order needs at least one item line.");
        }

        var basicValue = context.Lines.Sum(line => line.AmountAfterDiscount);
        var taxValue = context.Lines.Sum(line => line.TaxAmount);

        var items = new JsonArray();
        var taxRows = new JsonArray();

        foreach (var line in context.Lines)
        {
            items.Add(BuildItem(context, line));

            foreach (var row in BuildTaxRows(context, line))
            {
                taxRows.Add(row);
            }
        }

        return new JsonObject
        {
            // Document Control's three answers: whether the order needs a site, whether the ERP
            // numbers it, and whether it needs authorising. The agent reports them, never chooses
            // them - AuthFlag "N" is what makes the ERP authorise the order as it saves it.
            ["LocReqFlag"] = context.DocumentControl.SiteRequired,
            ["AutoNoFlag"] = context.DocumentControl.AutoNumberRequired,
            ["AuthFlag"] = context.DocumentControl.AuthorisationRequired,
            // "A" for add. There is no other mode this flow uses; it never edits an order.
            ["Mode"] = "A",
            ["companyId"] = context.Options.CompanyId,
            ["enableLineLevelRateStructure"] = context.SystemFlags.EnableLineLevelRateStructure,
            ["nonGST"] = context.SystemFlags.NonGst,
            ["HeaderDetail"] = BuildHeader(context, basicValue, taxValue, items),
            ["rsGrid"] = taxRows,
            // No bill of quantities: that belongs to the "Contract" order type and this flow raises
            // the "Service" one. No sales-order budget link either - an indent is not a budgeted SO.
            ["bOQ"] = new JsonArray(),
            ["posorefdtl"] = new JsonArray(),
        };
    }

    private static JsonObject BuildHeader(
        ServicePoContext context,
        decimal basicValue,
        decimal taxValue,
        JsonArray items)
    {
        var document = context.DocumentControl;
        var vendor = context.Vendor;

        return new JsonObject
        {
            ["autoId"] = 0,
            ["docType"] = IndentPoProfile.DocType,
            ["docSubtype"] = context.Profile.DocSubType,
            ["poYear"] = document.FinancialYear,
            ["poGroup"] = document.GroupCode,
            // The indent's site, never the configured one. The ERP's pending-lines query filters
            // XINDSHSITE = @MBMBPLOCID, so an order raised anywhere else would find nothing.
            ["poSite"] = context.Indent.SiteId,
            ["siteCode"] = context.Indent.SiteCode,
            // Blank: the ERP allocates the number, which is what AutoNoFlag "Y" means. A site whose
            // Document Control does not auto-number is refused before we get here - inventing a
            // document number is not the agent's business.
            ["poNumber"] = string.Empty,
            ["poDate"] = ErpDate(context.PoDate),
            ["poVendor"] = vendor.VendorCode,
            ["vendorName"] = vendor.VendorName,
            ["poBuyer"] = context.Options.BuyerCode,
            ["poLoginSiteId"] = context.Indent.SiteId,
            ["poLoginSiteCode"] = context.Indent.SiteCode,
            ["poLoginSiteName"] = document.LocationName,
            ["poCurrency"] = vendor.Currency,
            // Domestic only: the vendor's currency is checked against the ERP's before we get here,
            // so the rate is 1 and there are no foreign taxes. The ERP overwrites this from its own
            // exchange-rate master as it saves, in any case.
            ["poExRate"] = 1,
            ["paymentControl"] = vendor.PaymentCode,
            ["deliveryControl"] = vendor.DeliveryCode,
            ["inspectionControl"] = vendor.InspectionCode,
            ["freightControl"] = vendor.FreightCode,
            ["packingControl"] = vendor.PackingCode,
            ["insuranceControl"] = vendor.InsuranceCode,
            ["octroiTermControl"] = vendor.OctroiCode,
            ["dispatchModeControl"] = vendor.DispatchMode,
            ["poBasicValAftDis"] = JsonValue.Create(basicValue),
            ["taxesDomesticCurr"] = JsonValue.Create(taxValue),
            ["taxesForeignCurr"] = JsonValue.Create(0m),
            // Sent whether or not line-level rate structures are on. With them off this is the
            // structure the save validation checks; with them on it checks each item's, and every
            // item here carries this same code - so both branches see the same answer, and
            // POHSRTSTRCD is populated either way.
            ["poRateStructure"] = context.RateStructureCode,
            // No order-level discount: any discount belongs to the item/vendor master and is
            // already in each line's rate.
            ["discType"] = "None",
            ["discValue"] = 0,
            // "S" for a service order, as against "C" for a contract with a bill of quantities.
            ["selectOdrType"] = "S",
            ["poAmendmentSrNo"] = AmendmentSerial,
            ["contactPerson"] = 0,
            ["kindAttn"] = vendor.ContactName,
            ["isRCMunderGST"] = context.IsReverseCharge,
            ["poRCMType"] = context.ReverseChargeType,
            ["poWarrantyTerms"] = string.Empty,
            ["remarks"] = string.Empty,
            ["isNonstandardterms"] = false,
            ["nonStandardTermsControl"] = string.Empty,
            ["gstNum"] = vendor.GstNumber,
            // "IN" is the screen's own word for an indent-based order, as against "PO" for a direct
            // one. It is not the R/C of a material purchase order.
            ["poType"] = "IN",
            ["PCountryCode"] = context.SiteCountryCode,
            ["PStateCode"] = context.SiteStateCode,
            ["VCountryCode"] = vendor.CountryCode,
            ["VStateCode"] = vendor.StateCode,
            ["pohsissobudget"] = false,
            ["pohssoid"] = 0,
            ["ItemDetail"] = items,
        };
    }

    private static JsonObject BuildItem(ServicePoContext context, ServicePoLineTotals line)
    {
        var source = line.Source;

        return new JsonObject
        {
            ["srno"] = line.ItemLine,
            ["delsrno"] = line.ItemLine,
            ["itemCode"] = source.ItemCode,
            ["itemDesc"] = source.ItemDescription,
            ["uom"] = source.Uom,
            ["indentId"] = source.IndentId,
            ["indentNum"] = context.Indent.DisplayNumber,
            ["indentDate"] = context.IndentDate,
            // XINDSDLINE. This is the line the ERP raises XINDSDPOQTY against and closes once it
            // reaches the indented quantity, which is the whole of this feature's idempotency.
            ["indentLineNo"] = source.IndentLineNumber,
            ["indentQty"] = JsonValue.Create(source.IndentQuantity),
            ["alrPoQty"] = JsonValue.Create(source.OrderedQuantity),
            ["quantity"] = JsonValue.Create(line.Quantity),
            ["basicprice"] = JsonValue.Create(source.BasicPrice),
            ["disctype"] = source.DiscountType,
            ["discvalue"] = JsonValue.Create(source.DiscountValue),
            ["serreq"] = source.ServiceFromDate,
            ["todate"] = source.ServiceToDate,
            // The ERP's own pending-items answer leaves this blank, and whether a bill needs
            // certifying is a decision for whoever certifies it, not for the order.
            ["billcertificate"] = string.Empty,
            ["billcertamt"] = 0,
            ["billamt"] = 0,
            ["linkamt"] = 0,
            ["taxvalue"] = JsonValue.Create(line.TaxAmount),
            ["remark"] = string.Empty,
            ["itemRemark"] = string.Empty,
            ["saccode"] = source.SacCode,
            ["rateStructureCode"] = context.RateStructureCode,
            ["landedPrice"] = JsonValue.Create(line.LandedPrice),
            ["rowstatus"] = 0,
            ["DeliveryDetail"] = BuildDeliveries(line),
        };
    }

    /// <summary>
    /// The indent's own delivery dates, renumbered 1..n within the line - which is what the screen
    /// does at save time, and what <c>XPOSDEL.PIESRNDLINE</c> expects.
    /// </summary>
    private static JsonArray BuildDeliveries(ServicePoLineTotals line)
    {
        var deliveries = new JsonArray();
        var deliveryLine = 1;

        foreach (var date in line.Source.DeliveryDates)
        {
            deliveries.Add(new JsonObject
            {
                ["delsrno"] = deliveryLine,
                ["srno"] = line.ItemLine,
                ["itemcode"] = line.ItemCode,
                ["deldate"] = date,
                ["remarks1"] = string.Empty,
                ["rowstatus"] = 0,
            });

            deliveryLine++;
        }

        return deliveries;
    }

    /// <summary>
    /// One <c>XDOCTXDTL</c> row per rate-structure component, per item.
    /// </summary>
    /// <remarks>
    /// Built from the pricing call's own answer, which already carries each component's percentage,
    /// its inclusive/exclusive and percentage/value flags, what it applies on, and its sequence.
    /// Only the tax type comes from elsewhere - the rate structure's detail rows - because the
    /// pricing call does not return it and <c>XDTDTAXTYP</c> wants it. <c>XDTDREFID</c> is left at
    /// zero deliberately: the ERP reads these rows back keyed on the item code and
    /// <c>XDTDREFLINE</c> alone, and the screen only ever put a grid row index there.
    /// </remarks>
    private static IEnumerable<JsonObject> BuildTaxRows(ServicePoContext context, ServicePoLineTotals line)
    {
        foreach (var component in line.RateComponents.OfType<JsonObject>())
        {
            var rateCode = component.String("msprtcd");

            if (string.IsNullOrEmpty(rateCode))
            {
                continue;
            }

            yield return new JsonObject
            {
                ["docType"] = IndentPoProfile.DocType,
                ["docSubType"] = context.Profile.DocSubType,
                ["itemCode"] = line.ItemCode,
                ["rateCode"] = rateCode,
                ["rateAmount"] = JsonValue.Create(line.TaxAmountFor(rateCode)),
                ["amdSrNo"] = AmendmentSerial,
                ["taxValue"] = JsonValue.Create(component.Decimal("msprtval")),
                ["inc_Exc"] = component.String("mspincexc"),
                ["per_Val"] = component.String("mspperval"),
                ["applicableOn"] = component.String("mspappon"),
                ["post_NonPost"] = component.Flag("msppnyn"),
                ["rateStructCode"] = context.RateStructureCode,
                ["seqNo"] = component.Int("mspseqno"),
                ["fromLocId"] = context.Indent.SiteId,
                ["curCode"] = component.String("mprcurcode", context.Vendor.Currency),
                ["taxTyp"] = context.TaxTypeFor(rateCode),
                ["refId"] = 0,
                ["percentage"] = JsonValue.Create(component.Decimal("msprtval")),
                ["refLine"] = line.ItemLine,
            };
        }
    }

    /// <summary>Dates travel as <c>MM/dd/yyyy</c>, the format the ERP screen posts.</summary>
    internal static string ErpDate(DateOnly date) =>
        date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
}
