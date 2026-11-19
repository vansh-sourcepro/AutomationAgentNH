using Microsoft.Extensions.Configuration;

namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

/// <summary>
/// Site-level configuration for the Sales Allocation -> Delivery Challan automation.
/// Bound from <c>AutomationAgent:SoAllocationToDC</c> in appsettings.json.
/// </summary>
public sealed class SoAllocationToDcOptions
{
    public const string SectionName = "AutomationAgent:SoAllocationToDC";

    /// <summary>ERP site id (integer location id used in API paths).</summary>
    public int SiteId { get; init; } = 1;

    /// <summary>ERP site code (e.g. "NF1"), used in document-control lookups.</summary>
    public string SiteCode { get; init; } = "NF1";

    /// <summary>
    /// Customer code the automation runs under. Must match the customer associated with
    /// the OAF / allocation. Required — blank means the automation cannot resolve the
    /// DC customer and the request is refused.
    /// </summary>
    public string CustomerCode { get; init; } = "";
}
