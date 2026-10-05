using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// Persistence interface for Issue to Shop Floor conversions, outcomes, and history queries.
/// </summary>
public interface IIssueToShopFloorTrackingRepository
{
    Task AddRunAsync(AutomationRun run, CancellationToken cancellationToken);

    Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken);

    Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<IssueToShopFloorConversion?> FindConversionAsync(string documentNumber, CancellationToken cancellationToken);

    Task<IssueToShopFloorConversion> AddConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken);

    Task UpdateConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken);

    Task AddOutcomeAsync(IssueToShopFloorOutcome outcome, CancellationToken cancellationToken);

    Task<IssueToShopFloorOutcome?> GetOutcomeAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IssueToShopFloorHistoryRow>> ListHistoryRowsAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken);

    Task<int> CountHistoryRowsAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken);

    Task<IssueToShopFloorHistorySummary> GetSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryRowsAsync(
        string documentNumber,
        CancellationToken cancellationToken);

    Task<Job?> GetJobWithStepsAsync(Guid jobId, CancellationToken cancellationToken);
}
