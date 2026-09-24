using System.Text.Json.Nodes;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// Canned ERP responses for the Indent → PO sequence, shaped as the live ERP shapes them.
/// </summary>
/// <remarks>
/// The default scenario is one authorised, open Regular indent at site 2 — id 60901, one item,
/// one vendor — because that is the case that failed in the field: an indent authorised at a site
/// the automation was not configured for. Each builder takes the values a test wants to vary and
/// leaves the rest at values the ERP would plausibly send.
/// </remarks>
internal static class ErpFixtures
{
    public const long IndentId = 60901;
    public const string IndentNumber = "000001";
    public const string ItemCode = "0500000DQ";
    public const string VendorCode = "0006";
    public const string RateStructure = "PSC3";
    public const string Currency = "RS";
    public const int SiteId = 2;
    public const string SiteCode = "SP1";

    /// <summary>An indent-list row. Defaults describe an authorised, open, Regular indent.</summary>
    public static JsonObject IndentListRow(
        long indentId = IndentId,
        string indentNumber = IndentNumber,
        string status = "A",
        string indentStatus = "Open",
        string indentTypeCode = "R",
        int siteId = SiteId,
        int totalRows = 1) =>
        new()
        {
            ["totalRows"] = totalRows,
            ["autoId"] = indentId,
            ["indentYear"] = "26-27",
            ["groupCode"] = "IN",
            ["indentNumber"] = indentNumber,
            ["indentDate"] = "2026-06-10T00:00:00",
            ["siteCode"] = SiteCode,
            ["siteFullName"] = "SP1 - PGTPL - SHRIPAL - SPARE",
            ["requestedByFullName"] = "SPY - Sandeep P Yadav",
            ["indentType"] = indentTypeCode == "C" ? "Capital" : "Regular",
            ["indentLocationId"] = siteId,
            ["status"] = status,
            ["indentTypeCode"] = indentTypeCode,
            ["indentStatus"] = indentStatus,
            ["authorizerName"] = "SU - super",
        };

    /// <summary>The getIndentDetail body: header plus the item lines, nested as the ERP nests them.</summary>
    public static JsonObject IndentDetail(
        string authStatus = "Authorized",
        string docStatus = "Open",
        params JsonObject[] items) =>
        new()
        {
            ["headerDetail"] = new JsonObject
            {
                ["indentId"] = IndentId,
                ["indentYear"] = "26-27",
                ["indentGroup"] = "IN",
                ["indentLocationId"] = SiteId,
                ["indentNumber"] = IndentNumber,
                ["indentLocationCode"] = SiteCode,
                ["indentType"] = "R",
                ["docStatusDisp"] = docStatus,
                ["authStatus"] = authStatus,
            },
            ["itemDetails"] = new JsonObject
            {
                ["itemDetails"] = new JsonArray(
                    items.Length > 0 ? items : [IndentDetailItem()]),
            },
        };

    public static JsonObject IndentDetailItem(
        string itemCode = ItemCode, string status = "O", decimal quantity = 100m, string itemSize = "") =>
        new()
        {
            ["itemCode"] = itemCode,
            ["itemName"] = "DQ DOCUMENTS",
            ["quantity"] = quantity,
            ["vendorCode"] = VendorCode,
            ["status"] = status,
            ["iuom"] = "NO",
            // Blank for a regular item; the ERP sends this as the item's L x B x T size string
            // (XINDITMSIZE) on a dimensioned/LBT indent line.
            ["itemSize"] = itemSize,
        };

    /// <summary>An item/vendor purchase master row — what decides which vendor can supply an item.</summary>
    public static JsonObject ItemVendorRow(
        string itemCode = ItemCode,
        string vendorCode = VendorCode,
        string rateStructure = RateStructure,
        bool isDefault = true,
        int priority = 1,
        bool active = true) =>
        new()
        {
            ["mivhitmcd"] = itemCode,
            ["mivhvndcd"] = vendorCode,
            ["mivhcurcd"] = Currency,
            ["mivhrtstrcd"] = rateStructure,
            ["mivhpuom"] = "NO",
            ["mivhvndpriority"] = priority,
            ["mivhisdefault"] = isDefault,
            ["mivhactive"] = active,
        };

    public static JsonArray DocumentControl(int siteId = SiteId) =>
        [
            new JsonObject
            {
                ["groupCode"] = "RG",
                ["locationId"] = siteId,
                ["locationCode"] = SiteCode,
                ["locationName"] = "SHRIPAL SPARE",
                ["isLocationRequired"] = true,
                ["isAutoNumberGenerated"] = true,
                ["isAutorisationRequired"] = true,
                ["isDefault"] = "Y",
            },
        ];

    public static JsonArray Address() =>
        [
            new JsonObject
            {
                ["add1"] = "Plot 1",
                ["add2"] = "GIDC",
                ["add3"] = string.Empty,
                ["city"] = "AHM",
                ["pinno"] = "380001",
                ["mclstate"] = "GJ",
                ["mclcountry"] = "IND",
            },
        ];

    public static JsonArray Vendor(string vendorCode = VendorCode) =>
        [
            new JsonObject
            {
                ["pohvndcode"] = vendorCode,
                ["mvmname"] = "OM ENGINEERING WORKS",
                ["pohcurcd"] = Currency,
                ["vengstno"] = "24AAACO0000A1Z5",
                ["pohdlcd"] = "D01",
                ["pohpaycd"] = "P01",
                ["pohincd"] = "I01",
                ["pohfrcd"] = "F01",
                ["pohpkcd"] = "K01",
                ["pohinscd"] = "N01",
                ["pohmod"] = "ROAD",
                ["paymentdays"] = 30,
            },
        ];

    /// <summary>The rate-structure component template every item's tax rows are built from.</summary>
    public static JsonArray RateStructureDetail() =>
        [
            new JsonObject
            {
                ["rateCode"] = "IGST18",
                ["rateDescription"] = "IGST 18%",
                ["rateValue"] = 18.0,
                ["rateType"] = "P",
                ["accountCode"] = "IGSTIN",
                ["srno"] = 1,
            },
        ];

    /// <summary>What the ERP charges per unit at a given basic rate.</summary>
    public static JsonArray RateComponents() =>
        [
            new JsonObject
            {
                ["rtcode"] = "IGST18",
                ["rtamt"] = 18.0,
                ["landprice"] = 118.0,
                ["rtvalue"] = 18.0,
            },
        ];

    public static JsonArray Warehouses() =>
        [
            new JsonObject
            {
                ["whCode"] = "PE",
                ["desc"] = "Purchase Engineering",
                ["id"] = 10,
            },
        ];

    /// <summary>One pending item row, trimmed to what the builder reads.</summary>
    public static JsonObject PendingItem(string itemCode = ItemCode, int warehouseId = 10) =>
        new()
        {
            ["itemCode"] = itemCode,
            ["itemName"] = "DQ DOCUMENTS",
            ["whCode"] = warehouseId > 0 ? "PE" : string.Empty,
            ["whDesc"] = warehouseId > 0 ? "Purchase Engineering" : string.Empty,
            ["whId"] = warehouseId,
            ["puom"] = "NO",
            ["iuom"] = "NO",
            ["pendingQty"] = 100.0,
            ["purchaseRate"] = 100.0,
            ["discountType"] = "None",
            ["discountValue"] = 0.0,
            ["purConvFact"] = 1.0,
            ["intConvFact"] = 1.0,
            ["puomdigaftdec"] = 4.0,
            ["iuomdigaftdec"] = 4.0,
            ["hsncode"] = "84282011",
            ["rateStructureCode"] = RateStructure,
        };

    /// <summary>One outstanding delivery row, belonging to <paramref name="indentId"/>.</summary>
    public static JsonObject PendingDelivery(
        string itemCode = ItemCode,
        long indentId = IndentId,
        decimal outstanding = 100m,
        int deliveryLine = 1) =>
        new()
        {
            ["itemCode"] = null,
            ["indentNo"] = "26-27/IN/SP1/000001",
            ["indentdate"] = "2026-06-10T00:00:00",
            ["indentdeliverydate"] = "2026-09-05T00:00:00",
            ["podeliverydate"] = "2026-09-05T00:00:00",
            ["indentqtyiuom"] = outstanding,
            ["xindiodqty"] = outstanding,
            ["pendingpoqtyiuom"] = 0.0,
            ["currentpoqtyiuom"] = 0.0,
            ["a"] = "2100-01-01T00:00:00",
            ["pilitmrefline"] = 1,
            ["pildelline"] = deliveryLine,
            ["pidindid"] = indentId,
            ["xindlindid"] = indentId,
            ["pilinddelline"] = deliveryLine,
            ["pilinditmline"] = 1,
            ["xinditmcd"] = itemCode,
            ["srno"] = 1,
        };

    public static JsonObject Pending(JsonArray items, JsonArray deliveries) =>
        new()
        {
            ["itemIndentmodel"] = items,
            ["indentdtlmodel"] = deliveries,
        };

    public static JsonObject CreatedPo(string documentNo = "26-27/RG/SP1/000794", long documentId = 107432) =>
        new()
        {
            ["documentNo"] = documentNo,
            ["documentID"] = documentId,
        };

    /// <summary>
    /// A fake ERP wired for the happy path: one authorised indent, one item, one vendor, one
    /// outstanding delivery line, and a purchase order at the end of it.
    /// </summary>
    public static FakeErp HappyPath()
    {
        var erp = new FakeErp();

        erp.Route("indententry/indentEntryList", new JsonArray(IndentListRow()));
        erp.Route("getIndentDetail", IndentDetail());
        erp.Route("ItemVendorPurchase/list", new JsonArray(ItemVendorRow()));
        erp.Route("getDefaultDocumentDetail", DocumentControl());
        erp.Route("GetPoAddress", Address());
        erp.Route("GetVendorInformation", body => Vendor(body?["vendorCode"]?.GetValue<string>() ?? VendorCode));
        erp.Route("GetRateStructureLOVforItem", new JsonArray(
            new JsonObject { ["rateStructureCode"] = RateStructure, ["isDefault"] = true }));
        erp.Route("getratestructuredetail", RateStructureDetail());
        erp.Route("getAllRateStructureDetails", RateComponents());
        erp.Route("GetWarCode4POEntry", Warehouses());
        erp.Route("GetItemVndPUOMLOVForPO", new JsonArray());
        erp.Route(
            "GetPendingItemsFromIndentnew",
            Pending(new JsonArray(PendingItem()), new JsonArray(PendingDelivery())));
        erp.Route("POEntry/create", CreatedPo());

        // A material sweep still asks the service module, because an unqualified sweep asks for
        // every indent type. Answering "none" keeps the material tests about material indents.
        erp.Route("ServiceIndentCont/indentEntryList", new JsonArray());

        return erp;
    }

    public static IndentReference Reference(IndentType indentType = IndentType.Regular) =>
        new(IndentId, "26-27", "IN", IndentNumber, SiteId, SiteCode, indentType);
}
