using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// One outstanding service-indent line, as <c>getItemDetailForSerPO</c> answers for it.
/// </summary>
/// <remarks>
/// Typed rather than passed through as the ERP's own JSON, which is the opposite of what the
/// material flow does — and for a good reason. <c>createServicePOEntry</c> takes a hand-written
/// DTO of about twenty properties (<c>ServicePOEntryModel</c>), not the ~130-property row the
/// pending-items call handed out, so there is nothing to preserve by echoing the original back and
/// the mapping has to be explicit anyway.
/// </remarks>
/// <param name="PendingRowNumber">
/// The <c>srno</c> the ERP put on its own answer. It is the key its delivery rows carry, so it is
/// how the two halves of that response are matched — and nothing else. The line number the order
/// is written with is assigned later, contiguously, over the lines this order actually takes.
/// </param>
/// <param name="IndentLineNumber">
/// <c>XINDSDLINE</c>, the indent's own line. This is what <c>PIDSINDTLNNO</c> carries, and
/// therefore which indent line the ERP raises <c>XINDSDPOQTY</c> against when the order is saved.
/// </param>
internal sealed record ServiceIndentLine(
    long IndentId,
    string ItemCode,
    string ItemDescription,
    string Uom,
    string SacCode,
    string RateStructureCode,
    int IndentLineNumber,
    int PendingRowNumber,
    decimal IndentQuantity,
    decimal OrderedQuantity,
    decimal ShortClosedQuantity,
    decimal BasicPrice,
    string DiscountType,
    decimal DiscountValue,
    string? ServiceFromDate,
    string? ServiceToDate,
    IReadOnlyList<string> DeliveryDates)
{
    /// <summary>
    /// What is left to order. The ERP's own pending query is <c>XINDSDINDQTY - XINDSDPOQTY &gt; 0</c>
    /// and its insert refuses anything that would push the two past each other, so this is the
    /// largest quantity the line will accept. The short-closed quantity is subtracted as well: it
    /// is always zero today — nothing in the ERP writes <c>XINDSHSCLQTY</c> — but if service
    /// short-close ever ships, erring towards ordering less is the recoverable direction.
    /// </summary>
    public decimal Outstanding => Math.Max(0m, IndentQuantity - OrderedQuantity - ShortClosedQuantity);
}

/// <summary>Everything gathered from the ERP before a Service PO's create body is assembled.</summary>
internal sealed record ServicePoContext
{
    public required IndentReference Indent { get; init; }

    public required IndentPoProfile Profile { get; init; }

    public required IndentPoOptions Options { get; init; }

    public required int UserId { get; init; }

    public required DateOnly PoDate { get; init; }

    public required DocumentControlDefaults DocumentControl { get; init; }

    public required VendorInformation Vendor { get; init; }

    /// <summary>The PO site's own country and state, for the domestic/import and RCM decisions.</summary>
    public required string SiteCountryCode { get; init; }

    public required string SiteStateCode { get; init; }

    public required string RateStructureCode { get; init; }

    /// <summary>
    /// The rate structure's component rows. Only one field is taken from them —
    /// <c>mprtaxtyp</c> per rate code — because the pricing call that supplies everything else
    /// does not carry it and <c>XDOCTXDTL.XDTDTAXTYP</c> wants it.
    /// </summary>
    public required JsonArray RateStructureDetail { get; init; }

    /// <inheritdoc cref="ServicePoSystemFlags.EnableLineLevelRateStructure" />
    public required ServicePoSystemFlags SystemFlags { get; init; }

    public required IReadOnlyList<ServicePoLineTotals> Lines { get; init; }

    /// <summary>
    /// The indent's own date, already in the ERP's <c>MM/dd/yyyy</c>, or null when the list row did
    /// not carry one. Display only — it rides along on each item row exactly as the screen sends
    /// it, and nothing is stored from it.
    /// </summary>
    public string? IndentDate { get; init; }

    /// <summary>
    /// A component's tax type (<c>MPRTAXTYP</c>), from the rate structure's detail rows. Blank when
    /// the structure does not list the code, which the ERP treats as no tax type rather than as an
    /// error.
    /// </summary>
    public string TaxTypeFor(string rateCode) =>
        RateStructureDetail
            .OfType<JsonObject>()
            .FirstOrDefault(row => row.String("rateCode").Equals(rateCode, StringComparison.OrdinalIgnoreCase))
            .String("mprtaxtyp");

    /// <summary>
    /// True when the site and the vendor are in the same country, which is the only case the ERP's
    /// save validation checks a rate structure for GST components at all.
    /// </summary>
    public bool IsDomestic =>
        SiteCountryCode.Length > 0
        && Vendor.CountryCode.Length > 0
        && SiteCountryCode.Trim().Equals(Vendor.CountryCode.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reverse charge. The ERP screen turns it on exactly when a domestic, GST-enabled order is
    /// raised on a vendor whose GSTIN does not look like one; this repeats that rule rather than
    /// inventing a stricter or looser one, because the save validation is written against it.
    /// </summary>
    public bool IsReverseCharge =>
        IsDomestic && !SystemFlags.NonGst && !LooksLikeGstin(Vendor.GstNumber);

    /// <summary>
    /// Same state means CGST + SGST, a different one means IGST. The ERP screen decides it from
    /// these two codes alone.
    /// </summary>
    public string ReverseChargeType =>
        SiteStateCode.Trim().Equals(Vendor.StateCode.Trim(), StringComparison.OrdinalIgnoreCase)
            ? "SGST"
            : "IGST";

    /// <summary>
    /// The whole of the ERP screen's own GSTIN check: present, and at least fifteen characters.
    /// Deliberately no stricter — a check the ERP would pass and the agent would fail turns into a
    /// reverse-charge order nobody asked for.
    /// </summary>
    private static bool LooksLikeGstin(string? gstin) =>
        !string.IsNullOrWhiteSpace(gstin) && gstin.Trim().Length >= 15;
}

/// <summary>
/// The two installation-wide flags the Service PO's save validation reads. They are asked for
/// rather than assumed because both change which of its two rate-structure checks runs, and
/// answering them wrongly turns every conversion into a refusal.
/// </summary>
/// <param name="EnableLineLevelRateStructure">
/// <c>MSCSYSPUR.MSCENABLELINELEVELRTSTR</c>. On: each item carries its own rate structure and the
/// header's is left blank. Off: the header carries it. This flow sets both to the same code, so
/// either check sees the same structure — the flag still has to be reported honestly, because it
/// is what tells the ERP which one to run.
/// </param>
/// <param name="NonGst">
/// <c>MSCSYSFLAGS.MSFNONGST</c>. On, the rate-structure check is skipped entirely.
/// </param>
internal sealed record ServicePoSystemFlags(bool EnableLineLevelRateStructure, bool NonGst);
