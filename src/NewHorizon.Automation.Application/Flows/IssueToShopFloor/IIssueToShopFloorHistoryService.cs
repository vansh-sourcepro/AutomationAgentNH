using NewHorizon.Automation.Application.Jobs;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// Read-side query service for Issue to Shop Floor automation history.
/// </summary>
public interface IIssueToShopFloorHistoryService
{
    Task<PagedResult<IssueToShopFloorHistoryRow>> ListHistoryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken);

    Task<IssueToShopFloorHistoryDetail?> GetDetailAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryAsync(
        string documentNumber,
        CancellationToken cancellationToken);

    Task<IssueToShopFloorHistorySummary> GetSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken);
}
