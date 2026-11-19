namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Bootstrap values for the Indent → PO flow, bound from <c>AutomationAgent:PurchaseOrder</c>.
/// </summary>
/// <remarks>
/// These are the session values the PO Entry screen had and an unattended agent does not: the
/// company, the site, the financial year and the buyer. They live in configuration for the same
/// reason the ERP paths do — they differ per installation, and an operator must be able to correct
/// one without a rebuild. The user id is deliberately absent: it comes from the ERP login response.
/// </remarks>
public sealed class IndentPoOptions
{
    /// <summary><c>CMPID</c> / <c>companyId</c> on the create payload.</summary>
    public int CompanyId { get; init; } = 1;

    /// <summary>
    /// The site the vendor-driven call raises its PO from, and the fallback for the indent-driven
    /// one. An indent-driven PO takes its site from the indent instead: the ERP's pending-indent
    /// query filters <c>XINDHLOCID = @SiteId</c>, so a single configured site silently hides every
    /// authorised indent raised anywhere else.
    /// </summary>
    public int LocationId { get; init; } = 1;

    /// <summary>
    /// The sites to sweep for authorised indents. Empty — the default — means "every site the ERP
    /// lists", which is the right answer for almost every installation; name sites here only to
    /// deliberately restrict the automation to some of them.
    /// </summary>
    public IReadOnlyList<int> Sites { get; init; } = [];


    /// <summary>
    /// <c>POHORDYEAR</c>, e.g. "26-27" — the year the purchase order is raised in, which is the
    /// ERP's open Document Control year and <em>not</em> the indent's own year.
    /// </summary>
    /// <remarks>
    /// Blank means "the year the purchase order date falls in", which is what the ERP screen does
    /// and what stays right after every April. A value set here is tried first and the PO date's
    /// year second, so a stale setting costs one extra call and a warning rather than every
    /// conversion.
    /// </remarks>
    public string FinancialYear { get; init; } = string.Empty;

    /// <summary><c>POHBYRCD</c>. A static LOV pick on the screen, so it is configured here.</summary>
    public string BuyerCode { get; init; } = "001";

    /// <summary>
    /// Used when the caller does not name a vendor. The pending-indent query is vendor-driven, so
    /// there has to be one.
    /// </summary>
    public string DefaultVendorCode { get; init; } = string.Empty;

    /// <summary>
    /// Forces a rate structure instead of taking the vendor's default. Blank — the normal case —
    /// means "use whichever entry the ERP marks <c>isDefault</c> for this vendor".
    /// </summary>
    public string RateStructureCode { get; init; } = string.Empty;

    /// <summary>Fallback currency when the vendor master does not state one.</summary>
    public string Currency { get; init; } = "INR";

    /// <summary>
    /// The ERP's own currency. When the vendor's currency matches, the exchange rate is 1 and the
    /// foreign-tax total is zero, which is the only case this flow covers.
    /// </summary>
    public string DomesticCurrency { get; init; } = "INR";
}
