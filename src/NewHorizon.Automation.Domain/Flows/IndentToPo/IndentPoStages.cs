namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// The stages an indent → PO conversion moves through, and the tasks inside them. Named after what
/// the conversion actually does, in the order <c>IndentConversion.cs</c> and
/// <c>IndentToPoService.cs</c> do it, so a stage on screen means something a developer can find.
/// </summary>
public static class IndentPoStages
{
    /// <summary>Find the indent and re-read it from the ERP. Once per execution.</summary>
    public const string Discovery = "Discovery";

    /// <summary>Item → vendor, then group by (vendor, currency, rate structure). Once per execution.</summary>
    public const string VendorResolution = "VendorResolution";

    /// <summary>Year, group, site, flags and the delivery address. Once per vendor group.</summary>
    public const string DocumentControl = "DocumentControl";

    /// <summary>Vendor terms, rate structure, outstanding lines, per-line pricing. Once per vendor group.</summary>
    public const string PendingLines = "PendingLines";

    /// <summary>The one write. Once per vendor group.</summary>
    public const string CreatePurchaseOrder = "CreatePurchaseOrder";

    public static IReadOnlyList<string> InOrder { get; } =
    [
        Discovery,
        VendorResolution,
        DocumentControl,
        PendingLines,
        CreatePurchaseOrder,
    ];
}

/// <summary>The task names recorded on a step, one per ERP question the conversion asks.</summary>
public static class IndentPoTasks
{
    public const string ListAuthorisedIndents = "ListAuthorisedIndents";
    public const string GetIndentDetail = "GetIndentDetail";
    public const string ResolveItemVendor = "ResolveItemVendor";
    public const string GroupByVendorTerms = "GroupByVendorTerms";
    public const string GetDefaultDocumentDetail = "GetDefaultDocumentDetail";
    public const string GetPoAddress = "GetPoAddress";
    public const string GetVendorInformation = "GetVendorInformation";
    public const string GetRateStructureDetail = "GetRateStructureDetail";
    public const string GetPendingItems = "GetPendingItems";
    public const string PriceLines = "PriceLines";
    public const string CreatePoEntry = "CreatePoEntry";
    public const string CreateServicePoEntry = "CreateServicePoEntry";
}
