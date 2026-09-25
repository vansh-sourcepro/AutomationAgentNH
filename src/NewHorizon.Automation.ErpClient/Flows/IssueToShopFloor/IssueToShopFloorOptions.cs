using System.Globalization;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Bootstrap values for the SJO → Issue to Shop Floor flow, bound from <c>AutomationAgent:IssueToShopFloor</c>.
/// </summary>
/// <remarks>
/// <para>
/// The session values the Issue to Shop Floor screen reads from the logged-in user and an unattended
/// agent cannot, plus the ERP paths the flow calls. Paths are configuration for the same reason the
/// Indent → PO ones are: an operator must be able to correct one without a rebuild. The user id is
/// deliberately absent — it comes from the ERP login response.
/// </para>
/// <para>
/// Every path below is an <b>existing</b> ERP endpoint, the one the ERP's own screens call. Nothing
/// here needs an ERP change.
/// </para>
/// </remarks>
public sealed class IssueToShopFloorOptions
{
    public const string SectionName = "AutomationAgent:IssueToShopFloor";

    /// <summary><c>companyId</c> on the create payload.</summary>
    public int CompanyId { get; init; } = 1;

    /// <summary>
    /// The company code the MRP policy is keyed by (<c>admin/mrpPolicy/getDetail/{company}/{location}</c>).
    /// </summary>
    public string CompanyCode { get; init; } = string.Empty;

    /// <summary>
    /// The site the agent acts from — the screen's login location. Document Control's default Issue
    /// row is looked up for it, and it is <c>fromLocationId</c> on the create payload.
    /// </summary>
    public int LocationId { get; init; } = 1;

    /// <summary>
    /// The sites whose SJOs the agent may look up. The ERP's SJO list filters on the SJO's own
    /// location, so an SJO raised at a site not listed here is reported as not found.
    /// </summary>
    public IReadOnlyList<int> Sites { get; init; } = [];

    /// <summary>
    /// <c>issueTo</c> on the create payload. Required by the ERP, and on the screen a person picks it;
    /// an unattended agent has nobody to ask, so it is configured.
    /// </summary>
    public string IssueTo { get; init; } = string.Empty;

    /// <summary><c>issueBy</c> on the create payload. Same story as <see cref="IssueTo"/>.</summary>
    public string IssueBy { get; init; } = string.Empty;

    /// <summary><c>finFlag</c> on the create payload — the company's finance-integration flag.</summary>
    public string FinFlag { get; init; } = "N";

    /// <summary><c>currencyCode</c> on the create payload — the ERP's domestic currency.</summary>
    public string CurrencyCode { get; init; } = "INR";

    /// <summary>
    /// The ERP transaction the warehouse rights are checked against. <c>02108</c> is Issue to Shop
    /// Floor; the agent's ERP user needs rights to the allocated warehouses under it, or the ERP lists
    /// no warehouse at all.
    /// </summary>
    public string WarehouseTransaction { get; init; } = "02108";

    /// <summary>
    /// The SJO list's <c>Status</c> value for an authorised SJO. <c>CSP_XSJO_List_EM</c> answers
    /// <c>A</c> when <c>XSHSJAUTHBY</c> is set, <c>P</c> while pending authorisation, <c>D</c> when deleted.
    /// </summary>
    public string AuthorisedStatus { get; init; } = "A";

    /// <summary>
    /// Mirrors the ERP's barcode-scanning setting (the login's <c>barcodereqflag</c>). When on, an item
    /// the ERP flags as barcode-required must be scanned on the screen, so the agent refuses the SJO
    /// rather than issue it unscanned.
    /// </summary>
    public bool BarcodeScanningEnabled { get; init; }

    public IssueToShopFloorErpPaths Endpoints { get; init; } = new();
}

/// <summary>The ERP paths the flow calls. Templates are formatted with the values named on each.</summary>
public sealed class IssueToShopFloorErpPaths
{
    /// <summary>SJO list, searchable. Formatted with the location ids, comma-separated.</summary>
    public string SjoListTemplate { get; init; } = "/api/v1/Planning/sjoentry/sjoentryList/{0}";

    /// <summary>
    /// The SJO's CBOM row for an item (flag <c>V</c>): present only when the CBOM is generated and
    /// frozen. Also carries the random number the Work Order lookup is keyed by.
    /// </summary>
    public string CbomItem { get; init; } = "/api/v1/Planning/AllocationWorkorder/GetSJOItemCode4Allocation";

    /// <summary>The SJO's Work Orders. Formatted with SJO id and random number.</summary>
    public string WorkOrdersTemplate { get; init; } =
        "/api/v1/Planning/AllocationWorkorder/GetWODtl4AllocationWoCreation/{0}/{1}";

    /// <summary>The warehouses holding stock allocated to the SJO and not yet issued.</summary>
    public string IssueWarehouses { get; init; } = "/api/v1/Inventory/IssuetoShopFloor/getWarehouseCodeForIssue";

    /// <summary>
    /// The screen's own SJO number validation for Issue to Shop Floor (doc type SJ, flag V) — the
    /// ERP's combined eligibility check. Formatted with SJO year, group and site id.
    /// </summary>
    public string IssueEligibilityTemplate { get; init; } =
        "/api/v1/Inventory/IssuetoShopFloor/getIssueOfItmDocNoLOV/SJ/{0}/{1}/{2}/V";

    /// <summary>
    /// Validates a site code for Work Order or Sales OAF issues (flag <c>V</c>) and answers its id —
    /// only when the site has an open, authorised Work Order in that year and group. Formatted with
    /// doc type (WO/OA), year, group and site code.
    /// </summary>
    public string IssueSiteTemplate { get; init; } =
        "/api/v1/Inventory/IssuetoShopFloor/getIssueOfItmSite/{0}/{1}/{2}/V?Site={3}";

    /// <summary>
    /// The Issue screen's number validation for a Work Order or Sales OAF (flag <c>V</c>): answers the
    /// document's id only when it can be issued. Formatted with doc type, year, group, site id, number.
    /// </summary>
    public string IssueDocumentTemplate { get; init; } =
        "/api/v1/Inventory/IssuetoShopFloor/getIssueOfItmDocNo/{0}/{1}/{2}/{3}/V?DocNo={4}";

    /// <summary>The document's pending items for the given warehouses.</summary>
    public string IssueItems { get; init; } = "/api/v1/Inventory/IssuetoShopFloor/getListOfItems4IssueEntry";

    /// <summary>
    /// The stock rows an item can be issued from. Formatted with SJO id, random number, warehouse ids
    /// (comma-separated), inward-wise allocation (Y/N) and whether the item needs an inward (Y/N).
    /// Flag <c>A</c> (add) and issue number 0 are what the screen sends for a new issue.
    /// </summary>
    public string StockTemplate { get; init; } =
        "/api/v1/Inventory/IssuetoShopFloor/getItemCombinations4IssueEntry/{0}/{1}/{2}/{3}/{4}/A/0";

    /// <summary>
    /// The system configuration the ERP UI loads at login. Its <c>financePeriodSetting</c> gives each
    /// site's current finance period, which decides the authorisation date. The company comes from
    /// the <c>CompanyId</c> header the ERP client already sends.
    /// </summary>
    public string SystemConfig { get; init; } = "/api/v1/admin/common/getCommomSystemConfig";

    /// <summary>The MRP policy. Formatted with company code and location code.</summary>
    public string MrpPolicyTemplate { get; init; } = "/api/v1/admin/mrpPolicy/getDetail/{0}/{1}";

    public string CreateIssue { get; init; } = "/api/v1/Inventory/IssuetoShopFloor/createIssuetoShopFloor";

    internal string SjoList(IEnumerable<int> sites) =>
        Format(SjoListTemplate, string.Join(',', sites.Select(site => site.ToString(CultureInfo.InvariantCulture))));

    internal string WorkOrders(long sjoId, int randomNumber) => Format(WorkOrdersTemplate, sjoId, randomNumber);

    internal string IssueEligibility(string year, string group, int siteId) =>
        Format(IssueEligibilityTemplate, year, group, siteId);

    internal string IssueSite(IssueSource source, string year, string group, string siteCode) =>
        Format(IssueSiteTemplate, source.DocType(), year, group, siteCode);

    internal string IssueDocument(IssueSource source, string year, string group, int siteId, string number) =>
        Format(IssueDocumentTemplate, source.DocType(), year, group, siteId, number);

    internal string Stock(long sjoId, int randomNumber, string warehouseIds, bool inwardWiseAllocation, bool inwardRequired) =>
        Format(StockTemplate, sjoId, randomNumber, warehouseIds, inwardWiseAllocation ? "Y" : "N", inwardRequired ? "Y" : "N");

    internal string MrpPolicy(string companyCode, string locationCode) =>
        Format(MrpPolicyTemplate, companyCode, locationCode);

    private static string Format(string template, params object[] values) =>
        string.Format(
            CultureInfo.InvariantCulture,
            template,
            values.Select(value => (object)Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)).ToArray());
}
