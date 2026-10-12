namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// The source document family for Issue to Shop Floor automation: SJO, Work Order, or Sales OAF.
/// </summary>
public enum IssueToShopFloorSource
{
    Sjo = 0,
    WorkOrder = 1,
    SalesOaf = 2,
}
