namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// The ordered stages and standard tasks of the Issue to Shop Floor automation flow.
/// </summary>
public static class IssueToShopFloorStages
{
    public const string Discovery = "Discovery";
    public const string DocumentControl = "DocumentControl";
    public const string WorkAllocation = "WorkAllocation";
    public const string ErpEligibility = "ErpEligibility";
    public const string PendingItems = "PendingItems";
    public const string StockAllocation = "StockAllocation";
    public const string CreateIssue = "CreateIssue";

    public static IReadOnlyList<string> InOrder { get; } =
    [
        Discovery,
        DocumentControl,
        WorkAllocation,
        ErpEligibility,
        PendingItems,
        StockAllocation,
        CreateIssue
    ];
}

public static class IssueToShopFloorTasks
{
    public const string ValidateDocument = "Validate document and prerequisites";
    public const string VerifyIssueNumbering = "Verify issue numbering configuration";
    public const string CheckWorkAllocation = "Check allocated warehouses";
    public const string ValidateErpEligibility = "Validate ERP issue eligibility";
    public const string FetchPendingItems = "Fetch pending BOM item lines";
    public const string AllocateStock = "Calculate warehouse-by-warehouse FIFO stock allocation";
    public const string CreateIssueSlip = "Create Issue to Shop Floor in ERP";
}
