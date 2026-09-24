namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// The ERP application paths the agent calls, relative to <c>AutomationAgent:ErpApi:BaseUrl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Collected in one place so the surface the agent depends on can be reviewed with the ERP team at
/// a glance — and bound from <c>AutomationAgent:ErpEndpoints</c> so a path can be corrected on the
/// server without a rebuild. That matters more than usual here: only the three AutoShop paths the
/// client confirmed on 2026-07-27 are known to be right. The rest are placeholders.
/// </para>
/// <para>
/// One base URL serves all of them. There is no separate host for AutoShop.
/// </para>
/// </remarks>
public sealed class ErpEndpointOptions
{
    /// <summary>Placeholder replaced with the Site ID in the per-site templates.</summary>
    public const string SiteIdToken = "{siteId}";

    public string DeAllocation { get; init; } = "/api/automation/deallocation";

    public string Allocation { get; init; } = "/api/automation/allocation";

    public string WorkOrder { get; init; } = "/api/automation/workorder";

    public string PurchaseRequisition { get; init; } = "/api/automation/purchase-requisition";

    public string LaborRequisition { get; init; } = "/api/automation/labor-requisition";

    public string OafLink { get; init; } = "/api/automation/oaf-link";

    /// <summary>Query-before-create, so a resumed job adopts what an earlier attempt made.</summary>
    public string ExistingDocument { get; init; } = "/api/automation/existing-document";

    /// <summary>
    /// Confirms a transition the ERP automates internally (SO → OAF, SJO → CBOM). The agent asks
    /// rather than acts, so it can never duplicate the ERP's own flag-driven automation.
    /// </summary>
    public string VerifyAutomation { get; init; } = "/api/automation/verify-automation";

    public string NetShortage { get; init; } = "/api/automation/net-shortage";

    public string MilShortage { get; init; } = "/api/automation/mil-shortage";

    public string AllocationStatus { get; init; } = "/api/automation/allocation-status";

    public string PendingDocuments { get; init; } = "/api/automation/pending-documents";

    // ---- AutoShop cycle ----------------------------------------------------

    /// <summary>Site ID collection. Confirmed 2026-07-27; the ERP team is modifying its response.</summary>
    public string SiteList { get; init; } = "/api/v1/admin/location/list";

    /// <summary>
    /// One site's SJOs (BOM already created). The same path serves GET and POST — the ERP's
    /// existing convention here. Confirmed 2026-07-27.
    /// </summary>
    public string SjoSequenceTemplate { get; init; } =
        "/api/v1/planning/autoshopsjosequence/GetSJODetails/" + SiteIdToken + "/S";

    /// <summary>AutoShop read/submit for one site. Still upcoming from the ERP team.</summary>
    public string AutoShopTemplate { get; init; } = "/api/v1/planning/autoshop/" + SiteIdToken;

    /// <summary>The cycle's entry point: OAFs still awaiting an SJO. Path to be confirmed.</summary>
    public string OafAwaitingSjo { get; init; } = "/api/v1/planning/oaf/pending-sjo";

    /// <summary>OAF → SJO creation. Path to be confirmed.</summary>
    public string CreateSjoFromOaf { get; init; } = "/api/v1/planning/sjo/create-from-oaf";

    // ---- Indent → PO -------------------------------------------------------
    // Read off a capture of the ERP UI creating a PO from an indent, so these are the paths the ERP
    // actually serves rather than proposals. Templates are formatted with the values named in each
    // comment; every one of them is URL-escaped by the Format helpers below.

    /// <summary>
    /// Document Control's default row for a document type: group code, site, and the three Y/N
    /// flags. Formatted with financial year, doc type, doc sub-type, location id.
    /// </summary>
    public string DocumentControlDefaultTemplate { get; init; } =
        "/api/v1/admin/documentControl/getDefaultDocumentDetail/{0}/{1}/{2}/{3}";

    /// <summary>The PO delivery address. Formatted with OAF id (always 0 here) and site id.</summary>
    public string PoAddressTemplate { get; init; } = "/api/v1/Purchase/POEntry/GetPoAddress/{0}/{1}";

    public string VendorInformation { get; init; } = "/api/v1/Purchase/POEntry/GetVendorInformation";

    public string RateStructureLov { get; init; } = "/api/v1/inventory/grnwithoutpo/GetRateStructureLOVforItem";

    /// <summary>The rate-structure component rows that become TaxDetails. Formatted with the code.</summary>
    public string RateStructureDetailTemplate { get; init; } =
        "/api/v1/inventory/grnwithoutpo/getratestructuredetail/{0}";

    public string PendingItemsFromIndent { get; init; } =
        "/api/v1/Purchase/POEntry/GetPendingItemsFromIndentnew";

    public string ItemVendorPuom { get; init; } = "/api/v1/Purchase/POEntry/GetItemVndPUOMLOVForPO";

    /// <summary>
    /// The warehouses an item may be received into. Needed because the indent frequently carries no
    /// warehouse at all, and the ERP rejects a PO line that names none.
    /// </summary>
    public string WarehouseLov { get; init; } = "/api/v1/Purchase/POEntry/GetWarCode4POEntry";

    /// <summary>
    /// Per-component tax amounts computed by the ERP for one basic price, so the agent never has to
    /// own the tax engine. Formatted with rate structure code, discounted basic rate, currency.
    /// </summary>
    public string RateStructureCalculationTemplate { get; init; } =
        "/api/v1/purchase/ItemVendorPurchase/getAllRateStructureDetails/P/{0}/{1}/{2}/?currencyCode={2}";

    public string CreatePurchaseOrder { get; init; } = "/api/v1/Purchase/POEntry/create";

    // ---- Authorized-indent discovery ---------------------------------------
    // The PO Entry sequence above is vendor-first: it can only answer "what is pending for this
    // vendor at this site". These three turn the question round to "which indents were authorised
    // and still need a PO", which is what the automation is actually asked to do. All three are
    // existing ERP application endpoints, verified against the live API.

    /// <summary>
    /// The indent list, filtered by authorisation status. Posted with
    /// <c>{ Status = "A", LocationIds = "1,2", PageSize, PageNumber }</c>; "A" is the ERP's own code
    /// for authorised (<c>XINDHAUBY</c> and <c>XINDHAUDT</c> both set).
    /// </summary>
    public string IndentEntryList { get; init; } = "/api/v1/Purchase/indententry/indentEntryList";

    /// <summary>
    /// One indent's header and item lines. Formatted with site flag, year, group, site code,
    /// number, mode, indent type and indent document sub-type.
    /// </summary>
    public string IndentDetailTemplate { get; init; } =
        "/api/v1/Purchase/indententry/getIndentDetail/{0}/{1}/{2}/{3}/{4}/{5}/{6}/{7}";

    /// <summary>
    /// The item/vendor purchase master, searched by item code. It is what decides whether an indent
    /// line can be ordered at all: with <c>MSCSYSPUR.MSCAUTOITMVNDPUR = 0</c> the pending-indent
    /// query only returns items that have an active row here for the vendor being asked about.
    /// </summary>
    public string ItemVendorPurchaseList { get; init; } = "/api/v1/purchase/ItemVendorPurchase/list";

    // ---- Service Indent -> Service PO --------------------------------------
    // A separate ERP module, not a variant of the material one: its own controllers, its own tables
    // (XINDSHDR/XINDSDTL and XPOSHEAD/XPOSDTL) and its own create endpoint. Read off WebAPICore's
    // ServiceIndentController / ServicePOController and the stored procedures behind them.

    /// <summary>
    /// The service-indent list. Posted with the paging body; the sites travel in the path and the
    /// authorisation status in the query string, which is this controller's own convention rather
    /// than the material list's. Formatted with the comma-separated site ids.
    /// </summary>
    public string ServiceIndentListTemplate { get; init; } =
        "/api/v1/purchase/ServiceIndentCont/indentEntryList/{0}?Status=A";

    /// <summary>One service indent's header, item lines and delivery dates.</summary>
    public string ServiceIndentDetail { get; init; } =
        "/api/v1/purchase/ServiceIndentCont/getSerIndentDetail";

    /// <summary>
    /// A service item's row in the service item master. Only one field matters here: its
    /// <c>MSMITMAUTOID</c>, which is the key the item/vendor lookup below is addressed by.
    /// </summary>
    public string ServiceItemDetail { get; init; } = "/api/v1/Purchase/itemservices/getItemServiceDetail";

    /// <summary>
    /// The vendors that can supply one service item, with the currency, rate structure and basic
    /// price each offers. The service counterpart of <see cref="ItemVendorPurchaseList"/>, and the
    /// call that lets an indent-first automation ask a vendor-first ERP the right question.
    /// Formatted with the item's <c>MSMITMAUTOID</c>.
    /// </summary>
    public string ServiceItemVendorTemplate { get; init; } =
        "/api/v1/Purchase/itemservices/getItemServiceVendorDetail/{0}";

    /// <summary>
    /// The outstanding service-indent lines for a vendor at a site — the service equivalent of
    /// <see cref="PendingItemsFromIndent"/>, and the call that decides what the order contains.
    /// </summary>
    public string PendingServiceIndentItems { get; init; } =
        "/api/v1/purchase/ServicePO/getItemDetailForSerPO";

    public string CreateServicePurchaseOrder { get; init; } =
        "/api/v1/purchase/ServicePO/createServicePOEntry";

    /// <summary>
    /// Purchase policy. Read for one flag — <c>enableLineLevelRateStructure</c> — which decides
    /// which of the two rate-structure checks the Service PO save validation runs.
    /// </summary>
    public string PurchasePolicy { get; init; } = "/api/v1/admin/purchasePolicy/getDetail";

    /// <summary>System configuration. Read for one flag: <c>nonGST</c>, the other input to that check.</summary>
    public string TngConfiguration { get; init; } = "/api/v1/admin/tngConfiguration/getDetail";

    // ---- Session establishment ---------------------------------------------
    // Discovered 2026-08-18 against the live ERP: /auth/login alone issues a valid, correctly
    // signed JWT, but every other endpoint still answers 401 for it. A custom pipeline middleware
    // (RequestResponseLoggingMiddleware, despite its name) reads the token's "uid" claim on every
    // request and 401s before routing unless a session row keyed by that uid already exists — the
    // ERP UI creates it with this call immediately after login. Not in the design doc or in
    // .claude/context: no capture of the ERP UI's own traffic recorded it.
    public string AddUserSession { get; init; } = "/api/v1/setting/addusersession";

    // ---- Authorization ----------------------------------------------------
    // The caller's effective form rights, as a { formId: rightLetters } map. Called with the
    // browser's own bearer token (not the agent's service token) so the ERP resolves the rights of
    // the user actually making the request. Used to authorize the browser-facing PO Automation
    // endpoints against forms 011171 / 011172.
    public string FormRights { get; init; } = "/api/v1/auth/form-rights";

    public string DocumentControlDefault(string financialYear, string docType, string docSubType, int locationId) =>
        Format(DocumentControlDefaultTemplate, financialYear, docType, docSubType, locationId.ToString());

    public string PoAddress(int oafId, int siteId) =>
        Format(PoAddressTemplate, oafId.ToString(), siteId.ToString());

    public string RateStructureDetail(string rateStructureCode) =>
        Format(RateStructureDetailTemplate, rateStructureCode);

    public string RateStructureCalculation(string rateStructureCode, string basicPrice, string currency) =>
        Format(RateStructureCalculationTemplate, rateStructureCode, basicPrice, currency);

    public string ServiceIndentList(IReadOnlyList<int> siteIds)
    {
        ArgumentNullException.ThrowIfNull(siteIds);

        // Not escaped, deliberately: the ids are the agent's own integers and the ERP splits this
        // path segment on the comma. Escaping it to %2C relies on the host decoding it back.
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ServiceIndentListTemplate,
            string.Join(',', siteIds));
    }

    public string ServiceItemVendor(long itemAutoId) =>
        Format(
            ServiceItemVendorTemplate,
            itemAutoId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string IndentDetail(
        string siteFlag,
        string indentYear,
        string indentGroup,
        string siteCode,
        string indentNumber,
        string mode,
        string indentType,
        string indentDocSubType) =>
        Format(
            IndentDetailTemplate,
            siteFlag,
            indentYear,
            indentGroup,
            siteCode,
            indentNumber,
            mode,
            indentType,
            indentDocSubType);

    public string SjoSequence(string siteId) => Resolve(SjoSequenceTemplate, siteId);

    public string AutoShop(string siteId) => Resolve(AutoShopTemplate, siteId);

    private static string Resolve(string template, string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return template.Replace(SiteIdToken, Uri.EscapeDataString(siteId), StringComparison.Ordinal);
    }

    /// <summary>
    /// Fills a positional template, escaping every argument. Codes like the rate structure come
    /// from ERP responses rather than from the agent, so they are escaped rather than trusted.
    /// </summary>
    private static string Format(string template, params string[] arguments) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template,
            Array.ConvertAll<string, object>(arguments, argument => Uri.EscapeDataString(argument)));
}
