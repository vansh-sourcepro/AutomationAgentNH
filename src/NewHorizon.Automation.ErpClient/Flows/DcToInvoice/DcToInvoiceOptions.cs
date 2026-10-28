namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

/// <summary>
/// Configuration options for the Delivery Challan to Invoice automation flow.
/// </summary>
public sealed class DcToInvoiceOptions
{
    public const string SectionName = "AutomationAgent:DcToInvoice";

    /// <summary>Default customer code if none is supplied in the request.</summary>
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>Default Site/Location ID (e.g. 8).</summary>
    public int SiteId { get; set; } = 8;

    /// <summary>Default Location Code (e.g. "CT2").</summary>
    public string SiteCode { get; set; } = "CT2";

    /// <summary>Document Type for Sales Invoice (default: "SI").</summary>
    public string DocType { get; set; } = "SI";

    /// <summary>Document SubType for Domestic Sales Invoice (default: "DS").</summary>
    public string DocSubType { get; set; } = "DS";

    /// <summary>Default Currency (default: "INR").</summary>
    public string Currency { get; set; } = "INR";

    /// <summary>Invoice Nature flag for Product/Spare (default: "E").</summary>
    public string ExNonEx { get; set; } = "E";

    /// <summary>Nature of sales (default: "Normal" -> "N").</summary>
    public string NatureOfSales { get; set; } = "N";

    /// <summary>Invoice of (default: "ProductSpare" -> "P").</summary>
    public string InvoiceOf { get; set; } = "P";

    /// <summary>Default Rate Structure Code if DC carries none (default: "GST18").</summary>
    public string DefaultRateStructureCode { get; set; } = "GST18";
}
