using System.Text.Json.Nodes;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// The two captures of the ERP UI creating a PO from an indent, as builder inputs.
/// </summary>
/// <remarks>
/// <para>
/// Both are real: file 01 produced Regular PO <c>26-27/TE/NF1/000002</c> and file 02 produced
/// Capital PO <c>26-27/CP/NF1/000002</c> against a live ERP. Every value below is copied from those
/// captures, so a test that reproduces the recorded create payload is evidence the agent would have
/// been accepted by the same ERP.
/// </para>
/// <para>
/// The rows are trimmed to the properties the builder reads or the assertions check. That is not a
/// gap in coverage: everything else is deep-cloned through untouched, so a field that is not
/// asserted here cannot be altered by the builder either.
/// </para>
/// </remarks>
internal static class RecordedTrace
{
    /// <summary>File 01 — Regular indent 26-27/PI/NF1/000002, vendor A032, 20 KG at 450 less 10%.</summary>
    public static IndentPoContext Regular() => new()
    {
        Profile = IndentPoProfile.For(IndentType.Regular),
        Options = new IndentPoOptions
        {
            CompanyId = 1,
            LocationId = 1,
            FinancialYear = "26-27",
            BuyerCode = "001",
        },
        UserId = 2,
        PoDate = new DateOnly(2026, 8, 6),
        DocumentControl = new DocumentControlDefaults(
            FinancialYear: "26-27",
            GroupCode: "TE",
            LocationId: 1,
            LocationCode: "NF1",
            LocationName: "Fabcon Machines Pvt. Ltd.",
            SiteRequired: "Y",
            AutoNumberRequired: "Y",
            AuthorisationRequired: "Y"),
        Address = new PoDeliveryAddress(
            Address1: "Thaltej 1",
            Address2: "Sourcepro",
            Address3: "BodakDev",
            CityCode: "AH",
            PinCode: "385024",
            StateCode: "GUJ",
            CountryCode: "IND"),
        Vendor = new VendorInformation(
            VendorCode: "A032",
            VendorName: "A S BEARING COMPANY",
            Currency: "INR",
            GstNumber: "06AAJPK0422D1ZC",
            DeliveryCode: string.Empty,
            PaymentCode: string.Empty,
            InspectionCode: string.Empty,
            FreightCode: string.Empty,
            PackingCode: string.Empty,
            InsuranceCode: string.Empty,
            DispatchMode: string.Empty,
            PaymentDays: 0),
        RateStructureCode = "P0002",
        RateStructureDetail = RateStructureTemplate(),
        Lines =
        [
            new IndentPoLine(
                Item: new JsonObject
                {
                    ["itemCode"] = "ITEM_IA_HNI14",
                    ["itemName"] = "Heat And Inward Type ItemITEM_IA_HNI14",
                    ["whCode"] = "AUT01",
                    ["puom"] = "KG",
                    ["iuom"] = "KG",
                    ["purchaseRate"] = 450,
                    ["discountType"] = "Percentage",
                    ["discountValue"] = 10,
                    ["purConvFact"] = 1,
                    ["intConvFact"] = 1,
                    ["puomdigaftdec"] = 4,
                    ["iuomdigaftdec"] = 4,
                    // Reported as zero here; the item/vendor row below is what corrects it.
                    ["mimdelldtm"] = 0,
                    ["hsncode"] = "84282011",
                    // A property the agent has no opinion about, to prove pass-through.
                    ["mimadddesc"] = "item for testing",
                },
                DeliveryRows:
                [
                    new JsonObject
                    {
                        ["xinditmcd"] = "ITEM_IA_HNI14",
                        ["indentNo"] = "26-27/PI/NF1/000002",
                        ["indentdate"] = "2026-08-06T00:00:00",
                        ["indentdeliverydate"] = "2026-09-05T00:00:00",
                        ["podeliverydate"] = "2026-09-05T00:00:00",
                        ["indentqtyiuom"] = 20,
                        ["xindiodqty"] = 20,
                        ["pendingpoqtyiuom"] = 0,
                        ["pidindid"] = 899,
                        ["xindlindid"] = 899,
                        ["pilinddelline"] = 1,
                        ["pilinditmline"] = 1,
                        ["srno"] = 23,
                        ["a"] = "2100-01-01T00:00:00",
                        ["oafno"] = string.Empty,
                        ["sjono1"] = string.Empty,
                        ["indentitemremarks"] = string.Empty,
                        ["indhdrrmktext"] = string.Empty,
                        ["xshsjoyear"] = string.Empty,
                        ["xshsjosite"] = 0,
                        ["xshsjogrp"] = string.Empty,
                        ["xshsjono"] = string.Empty,
                        ["sjolocation"] = string.Empty,
                        ["pilpcs"] = 0,
                        ["pidpcs"] = 0,
                        ["xindlpcs"] = 0,
                        ["mivdelldtm"] = 0,
                    },
                ],
                VendorPuom: new JsonObject
                {
                    ["pidpurum"] = "KG",
                    ["pidbpurrt"] = 450,
                    ["piddisctyp"] = "Percentage",
                    ["piddiscval"] = 10,
                    ["pidpurcnvfct"] = 1,
                    ["pidintcnvfct"] = 1,
                    ["mivdelldtm"] = 2,
                },
                // Priced by the ERP for a basic rate of 405.
                RateComponents: RateComponents(perUnitTax: 36.45m, landedPrice: 477.9m)),
        ],
    };

    /// <summary>File 02 — Capital indent 26-27/PR/NF1/000003, vendor F005, 10 BX at 63000, no discount.</summary>
    public static IndentPoContext Capital() => new()
    {
        Profile = IndentPoProfile.For(IndentType.Capital),
        Options = new IndentPoOptions
        {
            CompanyId = 1,
            LocationId = 1,
            FinancialYear = "26-27",
            BuyerCode = "001",
        },
        UserId = 2,
        PoDate = new DateOnly(2026, 8, 7),
        DocumentControl = new DocumentControlDefaults(
            FinancialYear: "26-27",
            GroupCode: "CP",
            LocationId: 1,
            LocationCode: "NF1",
            LocationName: "Fabcon Machines Pvt. Ltd.",
            SiteRequired: "Y",
            AutoNumberRequired: "Y",
            AuthorisationRequired: "Y"),
        Address = new PoDeliveryAddress(
            Address1: "Thaltej 1",
            Address2: "Sourcepro",
            Address3: "BodakDev",
            CityCode: "AH",
            PinCode: "385024",
            StateCode: "GUJ",
            CountryCode: "IND"),
        Vendor = new VendorInformation(
            VendorCode: "F005",
            VendorName: "FRISTAM PUMPS INDIA PVT. LTD",
            Currency: "INR",
            GstNumber: "27AAACF4158R1Z3",
            DeliveryCode: string.Empty,
            PaymentCode: string.Empty,
            InspectionCode: string.Empty,
            FreightCode: string.Empty,
            PackingCode: string.Empty,
            InsuranceCode: string.Empty,
            DispatchMode: string.Empty,
            PaymentDays: 0),
        RateStructureCode = "P0002",
        RateStructureDetail = RateStructureTemplate(),
        Lines =
        [
            new IndentPoLine(
                Item: new JsonObject
                {
                    ["itemCode"] = "D_AC_I1803",
                    ["itemName"] = "BLUE STAR AC 1.5TR CAPACITYD_AC_I1803",
                    ["whCode"] = "CG",
                    ["puom"] = "BX",
                    ["iuom"] = "BX",
                    ["purchaseRate"] = 63000,
                    ["discountType"] = "None",
                    ["discountValue"] = 0,
                    ["purConvFact"] = 1,
                    ["intConvFact"] = 1,
                    // No decimals on this UOM, which is why the recorded quantity string is "10".
                    ["puomdigaftdec"] = 0,
                    ["iuomdigaftdec"] = 0,
                    ["mimdelldtm"] = 0,
                    ["hsncode"] = "73090090",
                },
                DeliveryRows:
                [
                    new JsonObject
                    {
                        ["xinditmcd"] = "D_AC_I1803",
                        ["indentNo"] = "26-27/PR/NF1/000003",
                        ["indentdate"] = "2026-08-07T00:00:00",
                        ["indentdeliverydate"] = "2026-08-18T00:00:00",
                        ["podeliverydate"] = "2026-08-18T00:00:00",
                        ["indentqtyiuom"] = 10,
                        ["xindiodqty"] = 10,
                        ["pendingpoqtyiuom"] = 0,
                        ["pidindid"] = 904,
                        ["xindlindid"] = 904,
                        ["pilinddelline"] = 1,
                        ["pilinditmline"] = 1,
                        ["srno"] = 83,
                        ["a"] = "2100-01-01T00:00:00",
                        ["oafno"] = string.Empty,
                        ["sjono1"] = string.Empty,
                        ["indentitemremarks"] = string.Empty,
                        ["indhdrrmktext"] = string.Empty,
                        ["xshsjoyear"] = string.Empty,
                        ["xshsjosite"] = 0,
                        ["xshsjogrp"] = string.Empty,
                        ["xshsjono"] = string.Empty,
                        ["sjolocation"] = string.Empty,
                        ["pilpcs"] = 0,
                        ["pidpcs"] = 0,
                        ["xindlpcs"] = 0,
                        ["mivdelldtm"] = 0,
                    },
                ],
                VendorPuom: new JsonObject
                {
                    ["pidpurum"] = "BX",
                    ["pidbpurrt"] = 63000,
                    ["piddisctyp"] = "None",
                    ["piddiscval"] = 0,
                    ["pidpurcnvfct"] = 1,
                    ["pidintcnvfct"] = 1,
                    ["mivdelldtm"] = 2,
                },
                RateComponents: RateComponents(perUnitTax: 5670m, landedPrice: 74340m)),
        ],
    };

    /// <summary>
    /// Rate structure P0002 as <c>getratestructuredetail</c> returns it: three zero-valued expense
    /// components and the 9% + 9% GST pair. Identical in both captures.
    /// </summary>
    private static JsonArray RateStructureTemplate() =>
    [
        Component(1, "P0001", "FREIGHT", "V", "F", "E3", "0002", 0, atActual: true, postNonPost: false),
        Component(2, "P0002", "Packing  Forwarding", "V", "P", "E3", "0001", 0, atActual: true, postNonPost: false),
        Component(3, "P0003", "Others", "V", "Z", "E3", "0003", 0, atActual: true, postNonPost: false),
        Component(4, "P0005", "INPUT CGST 9%", "P", "M", "A21002", "0003", 9, atActual: false, postNonPost: true),
        Component(5, "P0004", "INPUT SGST 9%", "P", "N", "A21001", "0003", 9, atActual: false, postNonPost: true),
    ];

    private static JsonObject Component(
        int index,
        string rateCode,
        string rateDesc,
        string percentageOrValue,
        string taxType,
        string accountGroup,
        string accountCode,
        decimal taxValue,
        bool atActual,
        bool postNonPost) => new()
        {
            ["index"] = index,
            ["rateCode"] = rateCode,
            ["rateDesc"] = rateDesc,
            ["pv"] = percentageOrValue,
            ["mprtaxtyp"] = taxType,
            ["acGroup"] = accountGroup,
            ["acCode"] = accountCode,
            ["taxValue"] = taxValue,
            ["atactual"] = atActual,
            ["postnonpost"] = postNonPost,
            ["ie"] = "E",
            ["currencyCode"] = "INR",
            ["taxRateCode"] = "P0002",
            ["taxRateDesc"] = "BV + FRT + PKG + INSUR + SGST 9% + CGST 9%",
            ["mspstrcd"] = null,
            ["mspfmloccd"] = "1",
            ["msptype"] = "P",
            ["mspactyn"] = true,
            ["print"] = true,
            ["mprchngtxval"] = 1,
            ["mprroundoff"] = 0,
            ["mprnottopay"] = false,
            ["rateAmount"] = 0,
            ["itemCode"] = null,
            ["applicableOn"] = string.Empty,
            ["appOnDisplay"] = string.Empty,
        };

    /// <summary>
    /// <c>getAllRateStructureDetails</c>'s answer: per-unit amounts by rate code, and the landed
    /// price. Only the two GST components carry an amount, matching both captures.
    /// </summary>
    private static JsonArray RateComponents(decimal perUnitTax, decimal landedPrice) =>
    [
        Priced("P0001", 0, landedPrice),
        Priced("P0002", 0, landedPrice),
        Priced("P0003", 0, landedPrice),
        Priced("P0005", perUnitTax, landedPrice),
        Priced("P0004", perUnitTax, landedPrice),
    ];

    private static JsonObject Priced(string rateCode, decimal amount, decimal landedPrice) => new()
    {
        ["msprtcd"] = rateCode,
        ["rtamt"] = amount,
        ["landprice"] = landedPrice,
    };
}
