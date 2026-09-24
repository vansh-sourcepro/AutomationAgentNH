namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// The unique workflow type names. These are stored on every job and hashed into its idempotency
/// key, so a name may not be changed once jobs exist for it.
/// </summary>
public static class WorkflowNames
{
    public const string Sjo = "SJO";
    public const string Oaf = "OAF";
    public const string Mil = "MIL";
    public const string Cbom = "CBOM";
    public const string AutoShop = "AutoShop";

    /// <summary>The agent's real unit of work: one repeating cycle covering OAF → SJO → sequencing → AutoShop.</summary>
    public const string AutoShopCycle = "AutoShopCycle";

    /// <summary>
    /// Authorised indent → purchase order. Deliberately absent from the workflow catalog: the
    /// conversion is run inline by <c>IIndentToPoService</c>, not walked by the engine, and a
    /// definition here would offer the dispatcher a job it has no operations for. The name exists
    /// so tracked jobs, runs and their AutomationConfig row all agree on what to call it.
    /// </summary>
    public const string IndentToPurchaseOrder = "IndentToPurchaseOrder";

    /// <summary>
    /// Every workflow name, for validating a caller's filter. Listing them back in a refusal is
    /// the difference between "you typed a workflow that does not exist" and an empty result the
    /// caller reads as "nothing has run".
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Sjo,
        Oaf,
        Mil,
        Cbom,
        AutoShop,
        AutoShopCycle,
        IndentToPurchaseOrder,
    ];
}

/// <summary>
/// What a job is about. Most workflows run for one ERP document; a cycle runs for no document at
/// all, and <see cref="Cycle"/> is what the live-cycle index keys off to allow only one at a time.
/// </summary>
public static class DocumentTypes
{
    public const string Cycle = "Cycle";
}

/// <summary>
/// Identifiers for transitions the ERP automates internally behind its own flag. The agent
/// confirms these rather than performing them.
/// </summary>
public static class ErpTransitions
{
    public const string SalesOrderToOaf = "SO-to-OAF";
    public const string SjoToCbom = "SJO-to-CBOM";
}

/// <summary>
/// Document kinds passed to query-before-create, so the ERP knows which document the agent is
/// asking about for this source document.
/// </summary>
public static class DocumentKinds
{
    public const string DeAllocation = "deallocation";
    public const string Allocation = "allocation";
    public const string WorkOrder = "workorder";
    public const string PurchaseRequisition = "purchase-requisition";
    public const string LaborRequisition = "labor-requisition";
    public const string OafLink = "oaf-link";
}
