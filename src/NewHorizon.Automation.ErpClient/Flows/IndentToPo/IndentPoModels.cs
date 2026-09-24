using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>What the caller asks for.</summary>
public sealed record IndentPoRequest
{
    public required IndentType IndentType { get; init; }

    /// <summary>
    /// Optional. The pending-indent query is vendor-driven, so when this is blank the configured
    /// <see cref="IndentPoOptions.DefaultVendorCode"/> is used instead.
    /// </summary>
    public string? VendorCode { get; init; }
}

/// <summary>What the ERP created.</summary>
/// <param name="PoNumber">
/// The document number, e.g. "26-27/TE/NF1/000002" — <c>data.documentNo</c> from the create call.
/// </param>
/// <param name="PoId">The ERP's surrogate key, <c>data.documentID</c>.</param>
/// <param name="Currency">
/// Reported alongside the vendor because the two, with the rate structure, are the break key that
/// decided this order existed separately from the next one. Process tracking records the group,
/// not just its result, so "why two purchase orders from one indent" has an answer.
/// </param>
public sealed record IndentPoResult(
    string PoNumber,
    long PoId,
    string VendorCode,
    int ItemCount,
    string Currency = "",
    string RateStructure = "");

/// <summary>Document Control's answer for this document type: group, site, and the three Y/N flags.</summary>
internal sealed record DocumentControlDefaults(
    // The year Document Control actually answered for, which is not necessarily the configured
    // one. Everything the payload stamps with a year takes it from here, so the order is numbered
    // in the year the ERP agreed to number it in.
    string FinancialYear,
    string GroupCode,
    int LocationId,
    string LocationCode,
    string LocationName,
    string SiteRequired,
    string AutoNumberRequired,
    string AuthorisationRequired);

/// <summary>The vendor master fields the header needs.</summary>
internal sealed record VendorInformation(
    string VendorCode,
    string VendorName,
    string Currency,
    string GstNumber,
    string DeliveryCode,
    string PaymentCode,
    string InspectionCode,
    string FreightCode,
    string PackingCode,
    string InsuranceCode,
    string DispatchMode,
    decimal PaymentDays,
    // Read for the Service PO, which has no delivery-address block of its own but whose save
    // validation compares the vendor's country with the site's (same country = a domestic PO,
    // which is the only case the rate-structure check applies to) and whose RCM type follows from
    // whether the two states match. The material flow does not read them.
    string OctroiCode = "",
    string CountryCode = "",
    string StateCode = "",
    string ContactName = "");

/// <summary>The delivery address block.</summary>
internal sealed record PoDeliveryAddress(
    string Address1,
    string Address2,
    string Address3,
    string CityCode,
    string PinCode,
    string StateCode,
    string CountryCode);

/// <summary>
/// One item of the PO, holding the ERP's own rows rather than a re-typed copy of them.
/// </summary>
/// <param name="Item">
/// The <c>itemIndentmodel</c> row exactly as the ERP sent it. It carries ~130 properties, most of
/// them item-master fields the agent has no opinion about; posting the original back means none of
/// them can be lost in translation.
/// </param>
/// <param name="DeliveryRows">The <c>indentdtlmodel</c> rows for this item — one PO delivery line each.</param>
/// <param name="VendorPuom">
/// The item/vendor purchase row. The only source of <c>mimdelldtm</c>, which the pending-items call
/// returns as 0 and the screen replaces.
/// </param>
/// <param name="RateComponents">Per-component tax amounts the ERP computed for this item's basic rate.</param>
internal sealed record IndentPoLine(
    JsonObject Item,
    IReadOnlyList<JsonObject> DeliveryRows,
    JsonObject? VendorPuom,
    JsonArray RateComponents);

/// <summary>Everything gathered from the ERP before the create call is assembled.</summary>
internal sealed record IndentPoContext
{
    public required IndentPoProfile Profile { get; init; }

    public required IndentPoOptions Options { get; init; }

    public required int UserId { get; init; }

    public required DateOnly PoDate { get; init; }

    public required DocumentControlDefaults DocumentControl { get; init; }

    public required PoDeliveryAddress Address { get; init; }

    public required VendorInformation Vendor { get; init; }

    public required string RateStructureCode { get; init; }

    /// <summary>The rate-structure component rows that become the TaxDetails template.</summary>
    public required JsonArray RateStructureDetail { get; init; }

    public required IReadOnlyList<IndentPoLine> Lines { get; init; }
}
