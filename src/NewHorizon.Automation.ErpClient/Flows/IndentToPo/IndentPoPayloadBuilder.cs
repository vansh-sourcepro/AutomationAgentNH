using System.Globalization;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Assembles the <c>Purchase/POEntry/create</c> body from what the ERP has already told the agent.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no HTTP, no clock, no configuration lookup. Everything it needs arrives in
/// <see cref="IndentPoContext"/>, which is what makes the two recorded traces usable as test
/// fixtures — the interesting half of this feature can be verified without an ERP.
/// </para>
/// <para>
/// The item and delivery rows are the ERP's own JSON, cloned and decorated rather than re-typed.
/// The alternative — a C# model of a 130-property item row — would silently drop every property the
/// agent does not know about, and those properties are exactly what the ERP expects back.
/// </para>
/// </remarks>
internal static class IndentPoPayloadBuilder
{
    /// <summary>The PO Entry screen's form id, carried on the notification parameters.</summary>
    private const string FormId = "01139";

    public static JsonObject Build(IndentPoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var lines = context.Lines.Select((line, index) => LineTotals.For(line, index + 1)).ToList();

        if (lines.Count == 0)
        {
            throw new InvalidOperationException("A purchase order needs at least one item line.");
        }

        var basicValue = lines.Sum(line => line.AmountAfterDiscount);
        var taxValue = lines.Sum(line => line.RateStructureAmount);

        var itemDetails = new JsonArray();
        var deliveryDetails = new JsonArray();
        var taxDetails = new JsonArray();
        var deliveryLine = 1;

        foreach (var line in lines)
        {
            itemDetails.Add(BuildItem(context, line));

            foreach (var delivery in line.Deliveries)
            {
                deliveryDetails.Add(BuildDelivery(line, delivery, deliveryLine));
                deliveryLine++;
            }

            foreach (var tax in BuildTaxRows(context, line))
            {
                taxDetails.Add(tax);
            }
        }

        return new JsonObject
        {
            // Always empty: the screen declares it and the code that would fill it is commented out.
            ["drPOITMRemark"] = new JsonArray(),
            ["Poheader"] = new JsonArray(BuildHeader(context, basicValue, taxValue)),
            ["DetailDesc"] = new JsonArray(BuildDetailDesc(context)),
            ["drNonStand"] = new JsonArray(),
            ["drHeader"] = new JsonArray(
                BuildTextBlock(context, "HDRTX"),
                BuildTextBlock(context, "FTRTX")),
            // Always empty. The footer text really does travel in drHeader's FTRTX entry.
            ["drFooter"] = new JsonArray(),
            ["TaxDetails"] = taxDetails,
            ["DelDetails"] = deliveryDetails,
            ["ItemDetails"] = itemDetails,
            ["PaySchedule"] = new JsonArray(),
            ["dtHstDetails"] = new JsonArray(),
            ["clsNotiParams"] = new JsonArray(BuildNotificationParams(context)),
            // No SJO or OAF is linked: automation starts from an indent, and the screen forces both
            // to zero for a Capital PO in any case.
            ["SjoDetails"] = new JsonArray(),
            ["posorefdtl"] = new JsonArray(),
        };
    }

    private static JsonObject BuildHeader(IndentPoContext context, decimal basicValue, decimal taxValue)
    {
        var document = context.DocumentControl;
        var vendor = context.Vendor;

        return new JsonObject
        {
            ["POHPODLVDATE"] = string.Empty,
            ["POHORDDT"] = context.PoDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            ["POHFRSITEID"] = document.LocationId.ToString(CultureInfo.InvariantCulture),
            ["POHTYPE"] = context.Profile.PoType,
            // "Normal" sub type. The alternative, "Site", is a manual choice on the screen.
            ["POHSUBTYP"] = "N",
            ["POHEOQMOQ"] = false,
            ["POHDOCTYP"] = IndentPoProfile.DocType,
            ["POHDOCSUBTYP"] = context.Profile.DocSubType,
            ["CREUSRID"] = context.UserId,
            ["SITEREQ"] = document.SiteRequired,
            ["AUTONUMREQ"] = document.AutoNumberRequired,
            ["POHFMSITEID"] = document.LocationId,
            ["POHAUTOID"] = 0,
            ["AUTHREQ"] = document.AuthorisationRequired,
            // Domestic currency only: the vendor's currency is checked against the ERP's before we
            // get here, so the rate is 1 and there are no foreign taxes.
            ["POHEXRATE"] = 1,
            ["POHOAFID"] = 0,
            ["POHPRICECONTRLPO"] = false,
            ["POHSALORDTYP"] = string.Empty,
            ["POHVATTNPER"] = string.Empty,
            ["POHPOCT3"] = false,
            ["XRCMGST"] = false,
            ["RCMTYPE"] = "IGST",
            ["POHNETVAL"] = Money(basicValue + taxValue),
            ["POHIntBranch"] = false,
            ["poheffuptodt"] = string.Empty,
            ["XATTCHTYPE"] = "UDP",
            // Indent-based, always. This flow has no other reason to exist.
            ["pobasis"] = "I",
            ["CMPID"] = context.Options.CompanyId,
            ["POHORDYEAR"] = context.DocumentControl.FinancialYear,
            ["POHGRPCD"] = document.GroupCode,
            // Blank: the ERP allocates the number, which is what AUTONUMREQ = "Y" means.
            ["POHORDNO"] = string.Empty,
            ["POHEFFFRMDT"] = null,
            ["POHEFFUPTODT"] = null,
            ["POHVNDCODE"] = vendor.VendorCode,
            ["POHVNDREF"] = string.Empty,
            ["POHBYRCD"] = context.Options.BuyerCode,
            ["POHCURCD"] = vendor.Currency,
            ["POHDELADD1"] = context.Address.Address1,
            ["POHDELADD2"] = context.Address.Address2,
            ["POHDELADD3"] = context.Address.Address3,
            ["POHDELCITCD"] = context.Address.CityCode,
            ["POHDELPINCD"] = context.Address.PinCode,
            ["POHDELSTACD"] = context.Address.StateCode,
            ["POHCOUCD"] = context.Address.CountryCode,
            ["POHFOB"] = string.Empty,
            ["POHPORT"] = string.Empty,
            ["POHDLCD"] = vendor.DeliveryCode,
            ["POHPAYCD"] = vendor.PaymentCode,
            ["POHINCD"] = vendor.InspectionCode,
            ["POHFRCD"] = vendor.FreightCode,
            ["POHPKCD"] = vendor.PackingCode,
            ["POHINSCD"] = vendor.InsuranceCode,
            // Octroi and warranty are both seeded from the vendor's GSTIN. That is not a sensible
            // mapping, but it is what the ERP screen posts, and this flow's job is to match it.
            ["POHOCTCD"] = vendor.GstNumber,
            ["POHMOD"] = vendor.DispatchMode,
            ["POHRTSTRCD"] = context.RateStructureCode,
            // No header-level discount: any discount is per item, from the item/vendor master.
            ["POHDISCPA"] = "None",
            ["POHDISCVAL"] = 0,
            ["POHPOVALAFDISC"] = Money(basicValue),
            ["POHPOFORCURTAX"] = Money(0m),
            ["POHPODOMCURTAX"] = Money(taxValue),
            ["POHPOVALBFDISC"] = Money(basicValue),
            ["POHWRNTCD"] = vendor.GstNumber,
            ["POHVNDCONTDTLID"] = 0,
            ["SiteCode"] = document.LocationCode,
            ["POHPAYTERM"] = vendor.PaymentDays,
            ["POHINCOTERM"] = string.Empty,
            ["POVALIDFROMDDT"] = null,
            ["POVALIDTODDT"] = null,
            ["POMAXRCPTVAL"] = null,
            ["PODELADDRESSCODE"] = string.Empty,
            ["POHDELGSTNO"] = string.Empty,
            ["POHISSOBUDGET"] = false,
            ["POHSOID"] = 0,
        };
    }

    private static JsonObject BuildDetailDesc(IndentPoContext context) => new()
    {
        ["MTMDOCTYP"] = IndentPoProfile.DocType,
        ["MTMDOCSUBTYP"] = context.Profile.DocSubType,
        ["MTMLOCCODE"] = context.DocumentControl.LocationId,
        ["CREUSRID"] = context.UserId,
        ["MTMID"] = null,
        ["MTMDOCID"] = 0,
        ["MTMLineNo"] = null,
        ["MTMAmdSrNo"] = 0,
    };

    /// <summary>Header and footer note rows. Both are sent empty; the ERP expects them to exist.</summary>
    private static JsonObject BuildTextBlock(IndentPoContext context, string blockType) => new()
    {
        ["MTMDOCTYPE"] = blockType,
        ["MTMID"] = context.DocumentControl.FinancialYear
            + context.DocumentControl.GroupCode
            + context.DocumentControl.LocationCode,
        ["MTMTEXT"] = string.Empty,
        ["MTMLOCCODE"] = context.DocumentControl.LocationId,
        ["CREUSRID"] = context.UserId,
        ["MTMLineNo"] = 1,
        ["MTMDOCTYP"] = IndentPoProfile.DocType,
        ["MTMDOCSUBTYP"] = context.Profile.DocSubType,
        ["MTMAmdSrNo"] = 0,
        ["MTMDOCID"] = 0,
    };

    private static JsonObject BuildNotificationParams(IndentPoContext context) => new()
    {
        ["LocationID"] = context.DocumentControl.LocationId,
        ["LocationCode"] = context.DocumentControl.LocationCode,
        ["LocationName"] = context.DocumentControl.LocationName,
        ["IP"] = string.Empty,
        ["MAC"] = string.Empty,
        ["FORMID"] = FormId,
        ["docYear"] = context.DocumentControl.FinancialYear,
        ["docGroup"] = context.DocumentControl.GroupCode,
        ["docSite"] = context.DocumentControl.LocationCode,
        ["docNumber"] = string.Empty,
        ["VENDORCODE"] = context.Vendor.VendorCode,
        ["VENDORNAME"] = context.Vendor.VendorName,
        ["CUSTOMERCODE"] = string.Empty,
        ["CUSTOMERNAME"] = string.Empty,
        ["PONO"] = string.Empty,
        ["USRID"] = context.UserId,
        ["companyId"] = context.Options.CompanyId,
    };

    /// <summary>
    /// The ERP's own item row, decorated with the quantities and amounts this PO adds. Nothing is
    /// removed.
    /// </summary>
    private static JsonObject BuildItem(IndentPoContext context, LineTotals line)
    {
        var item = (JsonObject)line.Line.Item.DeepClone();

        NullifyItemDensity(item);
        NormalizeIntegerPassthroughFields(item);

        item["qtypuom"] = JsonValue.Create(line.QuantityPuom);
        item["qtyiuom"] = JsonValue.Create(line.QuantityIuom);
        item["pendingQty"] = JsonValue.Create(line.QuantityIuom);
        item["excessQty"] = JsonValue.Create(0);
        item["purchaseRate"] = JsonValue.Create(line.PurchaseRate);
        item["discountType"] = line.DiscountType;
        item["discountValue"] = JsonValue.Create(line.DiscountValue);
        item["purConvFact"] = JsonValue.Create(line.PurchaseConversionFactor);
        item["intConvFact"] = JsonValue.Create(line.InternalConversionFactor);

        // The pending-items call reports this as 0; the item/vendor row is the only place the real
        // delivery lead time appears, and the ERP expects the corrected value back.
        item["mimdelldtm"] = JsonValue.Create(line.DeliveryLeadTimeDays);

        item["creusrid"] = context.UserId;
        item["rateStructureCode"] = context.RateStructureCode;

        item["itemtotalamount"] = JsonValue.Create(line.GrossAmount);
        item["itemAmtDiscount"] = Money(line.AmountAfterDiscount);
        item["rateStruAmt"] = JsonValue.Create(line.RateStructureAmount);
        item["foreigntaxes"] = JsonValue.Create(0);
        item["itemAmtDiscRateStru"] = JsonValue.Create(line.AmountAfterDiscount + line.RateStructureAmount);
        item["landedPrice"] = JsonValue.Create(line.LandedPrice);

        item["PIDITMLINE"] = line.ItemLine;
        item["PIDQTYPUOM"] = JsonValue.Create(line.QuantityPuom);
        item["PIDQTYIUOM"] = JsonValue.Create(line.QuantityIuom);
        // Pieces are not tracked on this flow; the screen hardcodes zero too.
        item["PIDPCS"] = 0;
        item["PIDDISCBSCRT"] = JsonValue.Create(line.DiscountedRate);
        item["PIDNETRATE"] = JsonValue.Create(line.LandedPrice);

        return item;
    }

    /// <summary>
    /// <c>itemDensity</c> on the ERP's own <c>PoItemDetailsmdl</c> create DTO (<c>WebAPICore/
    /// NewHorizon.Model/Purchase/POEntryModel.cs</c>) is a non-nullable <c>decimal</c>, but
    /// <c>GetPendingItemsFromIndentnew</c> was observed sending it as JSON <c>null</c> on every item
    /// against a live ERP on 2026-08-18. Newtonsoft rejects a null into a non-nullable value type,
    /// and because that failure aborts deserialising the rest of the same object, this one null
    /// silently invalidates every other property already on the row too. Zero is what the field
    /// already reads elsewhere on rows that do carry a value, so it is the safe, neutral substitute.
    /// </summary>
    private static void NullifyItemDensity(JsonObject item)
    {
        // A JsonObject represents JSON null as an absent node, same as a property that was never
        // sent at all — harmless, since a genuinely missing property leaves the ERP's own default
        // (also zero) in place. Only an explicit null needs correcting.
        if (item["itemDensity"] is null)
        {
            item["itemDensity"] = JsonValue.Create(0);
        }
    }

    /// <summary>
    /// These fields are declared as non-nullable <c>int</c> on the ERP's own
    /// <c>PoItemDetailsmdl</c> create DTO, but <c>GetPendingItemsFromIndentnew</c> was observed
    /// sending every one of them with a decimal point against a live ERP on 2026-08-18 — e.g.
    /// <c>"mimprlmt":0.0</c>, <c>"mimminlvl":0.0000</c>, <c>"puomdigaftdec":3.0</c>. Newtonsoft
    /// rejects a floating-point-formatted token into an <c>int</c> property outright, whole number
    /// or not, and — as with a null (see <see cref="NullifyItemDensity"/>) — that failure aborts
    /// deserialising the rest of the row, so one mis-formatted field invalidates every sibling
    /// property already on it too. Re-emitting the same value with the decimal point stripped keeps
    /// the number the ERP actually sent; only its JSON formatting changes.
    /// </summary>
    private static readonly string[] NonNullableIntegerPassthroughFields =
    [
        "mimprlmt",
        "mimminlvl",
        "mimsamsiz",
        "mimecnmord",
        "mimmaxordq",
        "mimminordq",
        "mimrordlvl",
        "mimsftystk",
        "mimlbrcurrt",
        "poDlvPieces",
        "puomdigaftdec",
        "mimtlitmlstcount",
    ];

    private static void NormalizeIntegerPassthroughFields(JsonObject item)
    {
        foreach (var field in NonNullableIntegerPassthroughFields)
        {
            // Decimal(), not GetValue<T>: it already tolerates the row sending a number one call
            // and a numeric string the next (see JsonRow's own remarks), so this fix does not need
            // to care which representation this particular row used.
            var whole = (int)Math.Round(item.Decimal(field), MidpointRounding.AwayFromZero);
            item[field] = JsonValue.Create(whole);
        }
    }

    private static JsonObject BuildDelivery(LineTotals line, DeliveryTotals delivery, int deliveryLine)
    {
        var source = delivery.Source;

        return new JsonObject
        {
            ["PILDELLINE"] = deliveryLine,
            ["XINDITMCD"] = source.String("xinditmcd"),
            ["pildeldt"] = source.ErpDate("podeliverydate"),
            ["PILQTYPO"] = 0,
            ["PILQTYPUOM"] = JsonValue.Create(delivery.QuantityPuom),
            ["PILQTYIUOM"] = JsonValue.Create(delivery.QuantityIuom),
            ["PIDINDID"] = source.Int("pidindid"),
            ["PILITMREFLINE"] = line.ItemLine,
            ["PILSJOQTY"] = 0,
            ["PILINDDELLINE"] = source.Int("pilinddelline"),
            // No SJO or OAF link on an indent-driven PO.
            ["PILSJOID"] = 0,
            ["OAFID"] = 0,
            ["PILINDITMLINE"] = source.Int("pilinditmline"),
            ["indentNo"] = source.String("indentNo"),
            ["indentdate"] = source.ErpDate("indentdate"),
            ["indentdeliverydate"] = source.ErpDate("indentdeliverydate"),
            ["indentqtyiuom"] = source.Raw("indentqtyiuom") ?? JsonValue.Create(0),
            ["xindiodqty"] = source.Raw("xindiodqty") ?? JsonValue.Create(0),
            ["pendingpoqtyiuom"] = source.Raw("pendingpoqtyiuom") ?? JsonValue.Create(0),
            // A string, formatted to the item's own IUOM precision — the ERP screen sends
            // "20.0000" for a 4-decimal UOM and "10" for one with none.
            ["currentpoqtyiuom"] = delivery.QuantityIuom.ToString(
                "F" + line.InternalUomDecimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture),
            ["oafno"] = source.String("oafno"),
            ["sjono1"] = source.String("sjono1"),
            ["indentitemremarks"] = source.String("indentitemremarks"),
            ["a"] = source.Raw("a"),
            ["pilpcs"] = source.Int("pilpcs"),
            ["pidpcs"] = source.Int("pidpcs"),
            ["xshsjoyear"] = source.String("xshsjoyear"),
            ["xshsjosite"] = source.Int("xshsjosite"),
            ["xshsjogrp"] = source.String("xshsjogrp"),
            ["xshsjono"] = source.String("xshsjono"),
            ["sjolocation"] = source.String("sjolocation"),
            ["xindlindid"] = source.Int("xindlindid"),
            ["xindlpcs"] = source.Int("xindlpcs"),
            ["mivdelldtm"] = source.Int("mivdelldtm"),
            ["srno"] = source.Int("srno"),
            ["indhdrrmktext"] = source.String("indhdrrmktext"),
        };
    }

    /// <summary>
    /// One tax row per rate-structure component, per item. The template comes from the rate
    /// structure; only the amount, the item it belongs to, and the four document keys are added.
    /// </summary>
    private static IEnumerable<JsonObject> BuildTaxRows(IndentPoContext context, LineTotals line)
    {
        foreach (var template in context.RateStructureDetail.OfType<JsonObject>())
        {
            var row = (JsonObject)template.DeepClone();
            var rateCode = row.String("rateCode");

            row["itemCode"] = line.ItemCode;
            row["mspstrcd"] = context.RateStructureCode;
            row["rateAmount"] = JsonValue.Create(line.TaxAmountFor(rateCode));
            row["XDTDTMCD"] = line.ItemCode;
            row["XDTDCATTYPE"] = IndentPoProfile.DocType;
            row["XDTDSUBTYPE"] = context.Profile.DocSubType;
            row["XDTDAMDSRNO"] = 0;

            yield return row;
        }
    }

    /// <summary>Amounts travel as 2-decimal strings on the header and the item discount field.</summary>
    private static string Money(decimal value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);
}
