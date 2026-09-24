using System.Globalization;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>Document Control's GRN numbering row for a site and year.</summary>
internal sealed record GrnDocumentControl(
    string FinancialYear,
    string GroupCode,
    string SiteCode,
    string SiteRequired,
    string AutoNumberRequired);

/// <summary>Everything one <c>inventory/grn/create</c> body is built from.</summary>
internal sealed record GrnContext
{
    public required PoGrnCandidate Po { get; init; }

    public required string VendorCode { get; init; }

    public required string VendorName { get; init; }

    public required string Currency { get; init; }

    /// <summary>The header warehouse; 0 when warehouses are per line.</summary>
    public required int WarehouseId { get; init; }

    public required bool ItemLevelWarehouse { get; init; }

    public required GrnDocumentControl DocumentControl { get; init; }

    public required DateOnly GrnDate { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    public required string InvoiceNumber { get; init; }

    public required int CompanyId { get; init; }

    public required int UserId { get; init; }

    public required string Remark { get; init; }

    public required IReadOnlyList<GrnLineTotals> Lines { get; init; }
}

/// <summary>
/// Assembles the <c>GRNDataModel</c> the GRN screen posts, from what the ERP has already said.
/// </summary>
/// <remarks>
/// <para>Pure — no HTTP, no clock — so it can be tested without an ERP.</para>
/// <para>
/// Three choices here are load-bearing:
/// <list type="bullet">
/// <item><c>AuthorizationRequired</c> is always <c>"Y"</c>. With <c>"N"</c> the ERP authorises the GRN
/// and posts it to Finance inside the create; the confirmed rule is that a person approves it.</item>
/// <item><c>rateStructureDetail</c> is never empty. The ERP raises the PO's received quantity only
/// when tax rows were inserted (<c>GRNRepository.insertRateStructureDetail</c> →
/// <c>CSP_XGRNDTL_UpdatePOQTY</c>); without them the PO keeps looking unreceived and the next run
/// would receive it again.</item>
/// <item>The invoice date is the day before the GRN date — confirmed 2026-09-24.</item>
/// </list>
/// </para>
/// </remarks>
internal static class GrnPayloadBuilder
{
    public const string DocType = "GR";

    public static JsonObject Build(GrnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Lines.Count == 0)
        {
            throw new InvalidOperationException("A GRN needs at least one line.");
        }

        if (context.Lines.Any(line => line.TaxRows.Count == 0))
        {
            throw new InvalidOperationException(
                "Every GRN line needs its tax rows: without them the ERP does not record the receipt against the PO.");
        }

        var document = context.DocumentControl;
        var first = context.Lines[0].Source;
        var siteId = context.Po.SiteId.ToString(CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["iogrpControlID"] = document.GroupCode,
            ["iositeControlID"] = siteId,
            ["ioyearControlID"] = document.FinancialYear,
            ["potypeControl"] = context.Po.PoType == PoGrnType.Capital ? "Capital" : "Regular",
            ["autoID"] = 0,
            ["CompanyId"] = context.CompanyId,
            ["FinYear"] = document.FinancialYear,
            ["FromLocId"] = context.Po.SiteId,
            ["SiteRequired"] = document.SiteRequired,
            ["AutoNumberRequired"] = document.AutoNumberRequired,
            ["AuthorizationRequired"] = "Y",
            ["SitePeriodStartDate"] = Date(context.PeriodStart),
            ["SitePeriodEndDate"] = Date(context.PeriodEnd),
            // Overwritten by the ERP from the bearer token; sent so the body is complete.
            ["UserId"] = context.UserId,
            ["DocType"] = DocType,
            ["DocSubType"] = context.Po.PoType.GrnSubType(),
            ["yearControl"] = document.FinancialYear,
            ["groupControl"] = document.GroupCode,
            // Blank: the ERP allocates the number (AutoNumberRequired = "Y").
            ["numberControl"] = string.Empty,
            ["siteControl"] = siteId,
            ["dateControl"] = Date(context.GrnDate),
            ["warehouseControl"] = context.WarehouseId,
            ["vendorControl"] = context.VendorCode,
            ["grnVendorName"] = context.VendorName,
            ["vendadd1Control"] = string.Empty,
            ["vendadd2Control"] = string.Empty,
            ["vendadd3Control"] = string.Empty,
            ["vendcityControl"] = string.Empty,
            ["vendpincodeControl"] = string.Empty,
            ["vendphoneControl"] = string.Empty,
            ["vendemailControl"] = string.Empty,
            ["vendchallannoControl"] = string.Empty,
            ["challandateControl"] = null,
            ["vendgstControl"] = string.Empty,
            ["vendcurrencyControl"] = context.Currency,
            // "N" is the screen's default supplier type.
            ["suppliertypeControl"] = "N",
            ["gateinwardnoControl"] = string.Empty,
            ["gateinwarddateControl"] = null,
            ["lrnoControl"] = string.Empty,
            ["transporterName"] = string.Empty,
            ["matlyingatControl"] = string.Empty,
            ["issucertnoControl"] = string.Empty,
            ["issucertdateControl"] = null,
            ["invoicenoControl"] = context.InvoiceNumber,
            ["invoicedateControl"] = Date(context.GrnDate.AddDays(-1)),
            ["reccertnoControl"] = string.Empty,
            ["reccertdateControl"] = null,
            ["billofentryControl"] = string.Empty,
            ["insprepoattachControl"] = false,
            ["remarkControl"] = $"{context.Remark} from PO {context.Po.DisplayNumber}",
            // Domestic currency only; a foreign-currency PO is refused before this is built.
            ["exrate"] = 1,
            ["rateStructure"] = context.Lines[0].RateStructureCode,
            ["xgrnhrcmtype"] = first.Text("rcmtype"),
            ["xgrnhrcmgst"] = false,
            ["siteCode"] = document.SiteCode,
            // Read by the ERP only when it authorises inside the create, which it never does here.
            ["strtExciseFlag"] = "N",
            ["strFinanceFlag"] = "N",
            ["strInwAllcAllw"] = string.Empty,
            ["strPORej"] = "N",
            ["strValChallanNo"] = false,
            ["loginLocId"] = context.Po.SiteId,
            // "S": the GRN was raised against a selected PO.
            ["poflag"] = "S",
            ["isitmlvlwh"] = context.ItemLevelWarehouse,
            ["isSoBudgeting"] = false,
            // Sent empty, exactly as the screen does when these sections are left untouched.
            ["manufacturerDetails"] = EmptyManufacturer(),
            ["grnvendorDetails"] = EmptyTradingVendor(),
            ["captionDetails"] = new JsonArray(),
            ["rateStructureDetailTxBifFlg"] = new JsonArray(),
            ["rateStructureDetail"] = new JsonArray([.. context.Lines.SelectMany(line => line.TaxRows).Select(row => row!.DeepClone())]),
            ["grnitemDetails"] = new JsonArray([.. context.Lines.Select(line => (JsonNode)BuildItem(line))]),
            ["itemDialogDetailsModules"] = new JsonArray(),
        };
    }

    private static JsonObject BuildItem(GrnLineTotals line)
    {
        var source = line.Source;

        return new JsonObject
        {
            ["itmcode"] = line.ItemCode,
            ["itemName"] = source.Text("itmname"),
            ["xgrndwhid"] = source.Whole("xgrndwhid").ToString(CultureInfo.InvariantCulture),
            ["warehousecode"] = source.Text("warecode"),
            ["warehousename"] = source.Text("warename"),
            ["rowid"] = line.RowId,
            ["xgrndsize"] = string.Empty,
            ["xgrndinwd"] = string.Empty,
            ["xgrndheat"] = string.Empty,
            ["xgrnditmsrno"] = string.Empty,
            ["xgrndmbatch"] = string.Empty,
            ["xgrndqcflg"] = source.Flag("xgrndqcflg"),
            ["basicPrice"] = JsonValue.Create(source.Number("basicprice")),
            ["iuom"] = source.Text("iuom"),
            ["iuomqt"] = JsonValue.Create(line.QuantityIuom),
            ["puom"] = source.Text("puom"),
            ["puomqt"] = JsonValue.Create(line.QuantityPuom),
            ["popuomqty"] = JsonValue.Create(source.Number("popuomqty")),
            ["popuomrcvd"] = JsonValue.Create(source.Number("popuomrcvd")),
            ["altuomValue"] = 0,
            ["iuomconv"] = source.Number("iuomconv").ToString(CultureInfo.InvariantCulture),
            ["puomconv"] = JsonValue.Create(source.Number("puomconv")),
            ["unitrate"] = JsonValue.Create(line.UnitRate),
            ["purvalue"] = JsonValue.Create(line.PurchaseValue),
            ["purchaseRate"] = JsonValue.Create(line.PurchaseValue),
            ["disctype"] = source.Text("disctype"),
            ["discvalue"] = JsonValue.Create(source.Number("discvalue")),
            ["convfact"] = source.Text("convfact"),
            ["convlimit"] = JsonValue.Create(source.Number("convlimit")),
            ["xgrndcalwgth"] = 0,
            ["xgrndlineno"] = 0,
            ["xgrnddensity"] = JsonValue.Create(source.Number("xgrnddensity")),
            ["xgrndformula"] = source.Text("xgrndformula"),
            ["xgrndpoid"] = JsonValue.Create(line.PoId),
            ["xgrndpoamd"] = source.Whole("xgrndpoamd"),
            ["xgrndpoline"] = source.Whole("xgrndpoline"),
            ["exhchangerate"] = 1,
            ["indentQTY"] = null,
            ["xgr23ordno"] = line.RowId,
            ["xgr23suprg23d"] = string.Empty,
            ["xgr23mfgrg23d"] = string.Empty,
            ["xgr23mfginvno"] = string.Empty,
            ["xgr23mfginvdt"] = null,
            ["xgr23assval"] = null,
            // Delivered in full: the challan says what was received.
            ["chalanqty"] = JsonValue.Create(line.QuantityPuom),
            ["grnhsnCode"] = source.Text("grnhsncode"),
            ["pohsncode"] = source.Text("pohsncode"),
            ["shelfLifeDate"] = null,
            ["xgrndbincd"] = source.Text("xgrndbincd"),
            ["rateDeviation"] = 0,
            ["mtmText1"] = string.Empty,
            ["mtmText2"] = string.Empty,
            ["mtmText3"] = string.Empty,
            ["mtmText4"] = string.Empty,
            ["mtmText5"] = string.Empty,
        };
    }

    private static JsonObject EmptyManufacturer() => new()
    {
        ["mvoid"] = 0,
        ["manunameControl"] = string.Empty,
        ["manuadd1Control"] = string.Empty,
        ["manuadd2Control"] = string.Empty,
        ["manuadd3Control"] = string.Empty,
        ["manucityControl"] = string.Empty,
        ["manuzipControl"] = string.Empty,
        ["manuexciseregistrationnumber"] = string.Empty,
        ["manuexciserange"] = string.Empty,
        ["manuexcisedivision"] = string.Empty,
        ["manucommissionerate"] = string.Empty,
        ["manuvatno"] = string.Empty,
        ["manucstno"] = string.Empty,
    };

    private static JsonObject EmptyTradingVendor() => new()
    {
        ["mvcid"] = 0,
        ["grnvendnameControl"] = string.Empty,
        ["grnvendadd1Control"] = string.Empty,
        ["grnvendadd2Control"] = string.Empty,
        ["grnvendadd3Control"] = string.Empty,
        ["grnvendcityControl"] = string.Empty,
        ["grnvendzipControl"] = string.Empty,
        ["basicamtControl"] = 0,
        ["totalpayablevendControl"] = 0,
        ["basicamtdomControl"] = 0,
        ["totalpayablevenddomControl"] = 0,
        ["grnvendexciseregistrationnumber"] = string.Empty,
        ["grnvendexciserange"] = string.Empty,
        ["grnvendexcisedivision"] = string.Empty,
        ["grnvendcommissionerate"] = string.Empty,
        ["grnvendvatno"] = string.Empty,
        ["grnvendcstno"] = string.Empty,
    };

    // DateOnly refuses time specifiers (':' included), so the midnight suffix is appended.
    private static string Date(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00";
}
