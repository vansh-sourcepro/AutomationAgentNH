using System.Text.Json.Nodes;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// Canned ERP responses for the Service Indent → Service PO sequence.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="ErpFixtures"/> because almost nothing is shared: a different list, a
/// different detail call, a different item/vendor master and a different create endpoint. What the
/// two do share — Document Control, the delivery address, the vendor master and the two
/// rate-structure calls — is reused from there rather than duplicated here, which is also the point
/// the production code makes.
///
/// The default scenario is one authorised, open service indent at site 2: one service item, one
/// vendor, one delivery date, ten units outstanding at 500 each.
/// </remarks>
internal static class ServiceErpFixtures
{
    public const long IndentId = 70123;
    public const string IndentNumber = "000007";
    public const string IndentYear = "26-27";
    public const string IndentGroup = "SI";
    public const string ItemCode = "SRV-CAL-01";
    public const long ItemAutoId = 4411;
    public const string VendorCode = "0009";
    public const string RateStructure = "PSC9";
    public const string Currency = "RS";
    public const int SiteId = 2;
    public const string SiteCode = "SP1";
    public const decimal IndentQuantity = 10m;
    public const decimal BasicPrice = 500m;

    /// <summary>A row from the service indent list. Note it carries no site id and no open/closed flag.</summary>
    public static JsonObject IndentListRow(
        long indentId = IndentId,
        string indentNumber = IndentNumber,
        string status = "A",
        int totalRows = 1) =>
        new()
        {
            ["totalRows"] = totalRows,
            ["id"] = indentId,
            ["year"] = IndentYear,
            ["groupCode"] = IndentGroup,
            ["indentNumber"] = indentNumber,
            ["indentDate"] = "2026-08-14T00:00:00",
            ["siteCode"] = SiteCode,
            ["siteFullName"] = "SP1 - PGTPL - SHRIPAL - SPARE",
            ["requestedByFullName"] = "SPY - Sandeep P Yadav",
            ["isEdit"] = 0,
            ["isDelete"] = 0,
            ["status"] = status,
        };

    /// <summary>
    /// <c>getSerIndentDetail</c>: the header beside the item lines, which is where the site id and
    /// the open/closed status actually live.
    /// </summary>
    public static JsonObject IndentDetail(
        string documentStatus = "Open",
        bool authorised = true,
        int siteId = SiteId,
        params string[] itemCodes) =>
        new()
        {
            ["headerDetail"] = new JsonObject
            {
                ["indentId"] = IndentId,
                ["indentYear"] = IndentYear,
                ["indentGroupCode"] = IndentGroup,
                ["indentSiteId"] = siteId,
                ["indentNumber"] = IndentNumber,
                ["indentDate"] = "2026-08-14T00:00:00",
                ["siteCode"] = SiteCode,
                ["indentStatus"] = documentStatus,
                ["authUserId"] = authorised ? "SU" : string.Empty,
                ["authUserName"] = authorised ? "SU - super" : string.Empty,
                ["authDate"] = authorised ? "2026-08-15T11:04:00" : string.Empty,
                ["indentDoneFullName"] = "SPY - Sandeep P Yadav",
            },
            ["itemDetail"] = new JsonArray(
                (itemCodes.Length > 0 ? itemCodes : [ItemCode])
                    .Select((code, index) => (JsonNode)new JsonObject
                    {
                        ["itemCode"] = code,
                        ["itemDesc"] = "ANNUAL CALIBRATION",
                        ["uom"] = "NO",
                        ["itemQty"] = IndentQuantity,
                        ["itemLineNo"] = index + 1,
                    })
                    .ToArray()),
            ["itemDeliveryDetail"] = new JsonArray(),
        };

    /// <summary>The service item master row, whose only interesting field is its auto id.</summary>
    public static JsonArray ItemMaster(string itemCode = ItemCode, long itemAutoId = ItemAutoId) =>
        [
            new JsonObject
            {
                ["itemAutoID"] = itemAutoId,
                ["itemCode"] = itemCode,
                ["itemDescription"] = "ANNUAL CALIBRATION",
                ["uom"] = "NO",
            },
        ];

    /// <summary>
    /// The service item/vendor master. No default flag and no priority: that is the ERP's shape,
    /// not a simplification.
    /// </summary>
    public static JsonObject ItemVendorRow(
        string vendorCode = VendorCode,
        string rateStructure = RateStructure,
        decimal basicPrice = BasicPrice,
        string discountType = "",
        decimal discountValue = 0m) =>
        new()
        {
            ["vendorCode"] = vendorCode,
            ["name"] = "OM CALIBRATION SERVICES",
            ["vendorCurrency"] = Currency,
            ["vendorUOM"] = "NO",
            ["codeDescription"] = "NUMBERS",
            ["basicPrice"] = basicPrice,
            ["discountType"] = discountType,
            ["discountValue"] = discountValue,
            ["taxStructure"] = rateStructure,
            ["landedPrice"] = basicPrice,
            ["sacCode"] = "998346",
        };

    /// <summary>One outstanding line from <c>getItemDetailForSerPO</c> with <c>poType</c> "IN".</summary>
    public static JsonObject PendingLine(
        string itemCode = ItemCode,
        long indentId = IndentId,
        int pendingRow = 1,
        int indentLineNo = 1,
        decimal indentQty = IndentQuantity,
        decimal alreadyOrdered = 0m,
        decimal basicPrice = BasicPrice,
        string discountType = "") =>
        new()
        {
            ["delsrno"] = pendingRow,
            ["srno"] = pendingRow,
            ["itemCode"] = itemCode,
            ["itemDesc"] = "ANNUAL CALIBRATION",
            ["uom"] = "NO",
            ["indentId"] = indentId,
            ["indentNum"] = $"{IndentYear}/{IndentGroup}/{SiteCode}/{IndentNumber}",
            ["indentDate"] = "2026-08-14T00:00:00",
            ["indentLineNo"] = indentLineNo,
            ["indentQty"] = indentQty,
            ["alrPoQty"] = alreadyOrdered,
            // The ERP always answers zero here; deciding the quantity is the caller's job.
            ["quantity"] = 0.0,
            ["basicprice"] = basicPrice,
            ["disctype"] = discountType,
            ["discvalue"] = 0.0,
            ["serreq"] = "2026-09-01T00:00:00",
            ["todate"] = "2026-09-30T00:00:00",
            ["billcertificate"] = string.Empty,
            ["indentShortClose"] = 0.0,
            ["remark"] = string.Empty,
            ["saccode"] = "998346",
            ["rowstatus"] = 0,
            ["rateStructureCode"] = RateStructure,
            ["isItemVendorRateAvailable"] = true,
        };

    public static JsonObject PendingDelivery(
        string itemCode = ItemCode,
        int pendingRow = 1,
        int deliveryLine = 1,
        string date = "2026-09-05T00:00:00") =>
        new()
        {
            ["delsrno"] = deliveryLine,
            ["srno"] = pendingRow,
            ["itemcode"] = itemCode,
            ["deldate"] = date,
            ["rowstatus"] = 0,
        };

    /// <summary>The ERP wraps both halves in a single-element array; the agent reads element zero.</summary>
    public static JsonArray Pending(JsonArray lines, JsonArray deliveries) =>
        [
            new JsonObject
            {
                ["itemDetail"] = lines,
                ["itemDeliveryDetail"] = deliveries,
            },
        ];

    /// <summary>
    /// What <c>createServicePOEntry</c> answers with: the model it was sent, its header stamped
    /// with the id and the serial the ERP allocated.
    /// </summary>
    public static JsonObject CreatedServicePo(string number = "000031", long autoId = 90212) =>
        new()
        {
            ["headerDetail"] = new JsonObject
            {
                ["autoId"] = autoId,
                ["poNumber"] = number,
                ["poYear"] = "26-27",
                ["poGroup"] = "SP",
                ["siteCode"] = SiteCode,
            },
        };

    public static JsonArray DocumentControl(int siteId = SiteId, bool autoNumber = true) =>
        [
            new JsonObject
            {
                ["groupCode"] = "SP",
                ["locationId"] = siteId,
                ["locationCode"] = SiteCode,
                ["locationName"] = "SHRIPAL SPARE",
                ["isLocationRequired"] = true,
                ["isAutoNumberGenerated"] = autoNumber,
                ["isAutorisationRequired"] = true,
                ["isDefault"] = "Y",
            },
        ];

    /// <summary>
    /// A fake ERP wired for the service happy path: one authorised, open service indent with one
    /// item, one vendor, ten units outstanding and a service purchase order at the end of it.
    /// </summary>
    public static FakeErp HappyPath()
    {
        var erp = new FakeErp();

        erp.Route("ServiceIndentCont/indentEntryList", new JsonArray(IndentListRow()));
        erp.Route("getSerIndentDetail", IndentDetail());
        erp.Route("getItemServiceDetail", ItemMaster());
        erp.Route("getItemServiceVendorDetail", new JsonArray(ItemVendorRow()));
        erp.Route("getDefaultDocumentDetail", DocumentControl());
        erp.Route("GetPoAddress", ErpFixtures.Address());
        erp.Route("GetVendorInformation", body => ServiceVendor(
            body?["vendorCode"]?.GetValue<string>() ?? VendorCode));
        erp.Route("getratestructuredetail", RateStructureDetail());
        erp.Route("getAllRateStructureDetails", RateComponents());
        erp.Route("purchasePolicy/getDetail", new JsonObject
        {
            ["purchasePolicyModel"] = new JsonObject { ["enableLineLevelRateStructure"] = false },
        });
        erp.Route("tngConfiguration/getDetail", new JsonObject { ["nonGST"] = false });
        erp.Route("getItemDetailForSerPO", Pending(
            new JsonArray(PendingLine()),
            new JsonArray(PendingDelivery())));
        erp.Route("createServicePOEntry", CreatedServicePo());

        // A sweep asks the material list too. Answering "none" keeps these tests about services.
        erp.Route("indententry/indentEntryList", new JsonArray());

        return erp;
    }

    /// <summary>
    /// The vendor master as <c>GetVendorInformation</c> returns it, with the country and state the
    /// service PO's domestic/import and reverse-charge decisions read. Same country as the site in
    /// <see cref="ErpFixtures.Address"/>, and the same state, so the default scenario is a domestic
    /// SGST order.
    /// </summary>
    public static JsonArray ServiceVendor(
        string vendorCode = VendorCode,
        string gstNumber = "24AAACO0000A1Z5",
        string countryCode = "IND",
        string stateCode = "GJ") =>
        [
            new JsonObject
            {
                ["pohvndcode"] = vendorCode,
                ["mvmname"] = "OM CALIBRATION SERVICES",
                ["pohcurcd"] = Currency,
                ["vengstno"] = gstNumber,
                ["pohdlcd"] = "D01",
                ["pohpaycd"] = "P01",
                ["pohincd"] = "I01",
                ["pohfrcd"] = "F01",
                ["pohpkcd"] = "K01",
                ["pohinscd"] = "N01",
                ["pohoctcd"] = "O01",
                ["pohmod"] = "ROAD",
                ["pohvattnper"] = "Mr Shah",
                ["paymentdays"] = 30,
                ["countrycode"] = countryCode,
                ["vstatecode"] = stateCode,
            },
        ];

    /// <summary>
    /// The rate structure's component rows. Only <c>mprtaxtyp</c> is read from them, which is the
    /// one field the pricing call below does not carry.
    /// </summary>
    public static JsonArray RateStructureDetail() =>
        [
            new JsonObject
            {
                ["index"] = 1,
                ["rateCode"] = "P0005",
                ["rateDesc"] = "INPUT CGST 9%",
                ["mprtaxtyp"] = "M",
                ["taxRateCode"] = RateStructure,
            },
            new JsonObject
            {
                ["index"] = 2,
                ["rateCode"] = "P0004",
                ["rateDesc"] = "INPUT SGST 9%",
                ["mprtaxtyp"] = "N",
                ["taxRateCode"] = RateStructure,
            },
        ];

    /// <summary>
    /// What the ERP charges per unit at a given basic rate: 9% + 9% on 500 is 45 each, and 590
    /// landed. Every row carries the running landed price, as the live ERP's does.
    /// </summary>
    public static JsonArray RateComponents() =>
        [
            new JsonObject
            {
                ["msprtcd"] = "P0005",
                ["mprrtdesc"] = "INPUT CGST 9%",
                ["mspincexc"] = "E",
                ["mspperval"] = "P",
                ["msprtval"] = 9.0,
                ["mspappon"] = "BV",
                ["msppnyn"] = true,
                ["mspseqno"] = 1,
                ["mprcurcode"] = Currency,
                ["rtamt"] = 45.0,
                ["landprice"] = 545.0,
            },
            new JsonObject
            {
                ["msprtcd"] = "P0004",
                ["mprrtdesc"] = "INPUT SGST 9%",
                ["mspincexc"] = "E",
                ["mspperval"] = "P",
                ["msprtval"] = 9.0,
                ["mspappon"] = "BV",
                ["msppnyn"] = true,
                ["mspseqno"] = 2,
                ["mprcurcode"] = Currency,
                ["rtamt"] = 45.0,
                ["landprice"] = 590.0,
            },
        ];

    public static IndentReference Reference() =>
        new(IndentId, IndentYear, IndentGroup, IndentNumber, SiteId, SiteCode, IndentType.Service);
}
