using System.Globalization;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>Bootstrap values for PO → GRN, bound from <c>AutomationAgent:PoToGrn</c>.</summary>
/// <remarks>
/// Only what differs per installation and an unattended agent cannot read from a session. The
/// behaviour a user changes — toggle, receipt mode, invoice number, schedule — lives in the
/// <c>PoGrnAutomationConfig</c> row instead.
/// </remarks>
public sealed class PoGrnOptions
{
    public const string SectionName = "AutomationAgent:PoToGrn";

    /// <summary>The <c>{companyId}</c> the PO list path and body carry.</summary>
    public int CompanyId { get; init; } = 1;

    /// <summary>The <c>{locationId}</c> the PO list path carries — the screen's login site.</summary>
    public int LocationId { get; init; } = 1;

    /// <summary>Sites to sweep when the settings row names none. Empty falls back to <see cref="LocationId"/>.</summary>
    public IReadOnlyList<int> Sites { get; init; } = [];

    /// <summary>Only POs in this currency are received; the GRN is valued at an exchange rate of 1.</summary>
    public string DomesticCurrency { get; init; } = "INR";

    /// <summary>
    /// Also require the PO to have been printed (<c>POHPRNFLAG = 1</c>), which is what the ERP's own
    /// PO list and GRN screen require. Off by default: the confirmed rule is "authorised POs".
    /// </summary>
    public bool RequirePrintedPo { get; init; }

    /// <summary>How far down the authorised PO list, newest first, one sweep looks.</summary>
    public int MaxScannedPos { get; init; } = 2000;

    /// <summary>Text written into the GRN's remark so a person can tell who made it.</summary>
    public string Remark { get; init; } = "Created by Automation Agent";
}

/// <summary>
/// The ERP paths PO → GRN calls, bound from <c>AutomationAgent:ErpEndpoints:PoToGrn</c> so a path can
/// be corrected on the server without a rebuild. Every one was read off WebAPICore's controllers and
/// the GRN / PO list screens in WebApp2.
/// </summary>
public sealed class PoGrnEndpointOptions
{
    public const string SectionName = "AutomationAgent:ErpEndpoints:PoToGrn";

    /// <summary>Per-site finance periods (<c>financePeriodSetting</c>): the GRN date must fall inside.</summary>
    public string SystemConfig { get; init; } = "/api/v1/admin/common/getCommomSystemConfig";

    /// <summary>Inventory policy — read for <c>islinelevelwhgrn</c> (warehouse per line, not per GRN).</summary>
    public string InventoryPolicy { get; init; } = "/api/v1/admin/inventoryPolicy/getDetail";

    /// <summary>PO list. Formatted with site ids, company id, location id; <c>?Status=A</c> is authorised.</summary>
    public string PoListTemplate { get; init; } = "/api/v1/Purchase/POEntry/List/{0}/{1}/{2}?Status=A";

    /// <summary>A PO's vendor, currency, type and warehouse(s). Formatted with the PO's auto id.</summary>
    public string PoToGrnDataTemplate { get; init; } = "/api/v1/inventory/grn/getpotogrnredirectdata/{0}";

    /// <summary>Document Control. Formatted with financial year, doc type, sub type, site id.</summary>
    public string DocumentControlDefaultTemplate { get; init; } =
        "/api/v1/admin/documentControl/getDefaultDocumentDetail/{0}/{1}/{2}/{3}";

    /// <summary>
    /// One PO's receivable lines and tax rows. Formatted with site, warehouse, vendor, PO id, mode
    /// (always <c>DIS</c>), currency, PO type and GRN date (yyyy-MM-dd).
    /// </summary>
    public string PoLinesTemplate { get; init; } =
        "/api/v1/inventory/grn/getposearchdetails/{0}/{1}/{2}/{3}/{4}/{5}/{6}/{7}";

    /// <summary>Per-unit tax components for one basic rate. Formatted with rate structure, rate, currency.</summary>
    public string RateStructureCalculationTemplate { get; init; } =
        "/api/v1/purchase/ItemVendorPurchase/getAllRateStructureDetails/P/{0}/{1}/{2}/?currencyCode={2}";

    public string CreateGrn { get; init; } = "/api/v1/inventory/grn/create";

    public string PoList(IReadOnlyList<int> sites, int companyId, int locationId) =>
        string.Format(
            CultureInfo.InvariantCulture,
            PoListTemplate,
            // Not escaped: the ERP splits this segment on the comma.
            string.Join(',', sites),
            companyId,
            locationId);

    public string PoToGrnData(long poId) => Format(PoToGrnDataTemplate, poId.ToString(CultureInfo.InvariantCulture));

    public string DocumentControlDefault(string financialYear, string docType, string docSubType, int siteId) =>
        Format(DocumentControlDefaultTemplate, financialYear, docType, docSubType, siteId.ToString(CultureInfo.InvariantCulture));

    public string PoLines(int siteId, int warehouseId, string vendorCode, long poId, string currency, string poType, DateOnly grnDate) =>
        Format(
            PoLinesTemplate,
            siteId.ToString(CultureInfo.InvariantCulture),
            warehouseId.ToString(CultureInfo.InvariantCulture),
            vendorCode,
            poId.ToString(CultureInfo.InvariantCulture),
            "DIS",
            currency,
            poType,
            grnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public string RateStructureCalculation(string rateStructureCode, decimal basicRate, string currency) =>
        Format(
            RateStructureCalculationTemplate,
            rateStructureCode,
            basicRate.ToString(CultureInfo.InvariantCulture),
            currency);

    private static string Format(string template, params string[] arguments) =>
        string.Format(
            CultureInfo.InvariantCulture,
            template,
            Array.ConvertAll<string, object>(arguments, argument => Uri.EscapeDataString(argument)));
}
