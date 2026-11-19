namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

/// <summary>
/// Operational KPI totals across SJO to Issue to Shop Floor conversions.
/// </summary>
public sealed record IssueToShopFloorDashboardSummaryResponse(
    int TotalJobs,
    IReadOnlyDictionary<string, int> CountsByStatus,
    IReadOnlyDictionary<string, int> CountsBySource,
    int SuccessCount,
    double? SuccessRate,
    double? AverageDurationMs,
    int TotalTriggerAttempts,
    int TriggerAttemptsWithoutEligibleDocument,
    int BusinessRefusalCount,
    int TechnicalFailureCount,
    decimal TotalQuantityIssued,
    int TotalLinesIssued);

/// <summary>
/// One calendar day's conversion volume and outcome stats for dashboard trend charts.
/// </summary>
public sealed record DailyIssueConversionStatResponse(
    string Date,
    int IssuesCreated,
    int DocumentsConverted,
    int DocumentsRefused,
    int DocumentsFailed,
    decimal TotalQuantity);

/// <summary>
/// Complete dashboard payload containing summary metrics, daily timeline series,
/// last completed conversion, last execution details, and recent jobs.
/// </summary>
public sealed record IssueToShopFloorDashboardResponse(
    IssueToShopFloorDashboardSummaryResponse Summary,
    IReadOnlyList<DailyIssueConversionStatResponse> DailyStats,
    IssueToShopFloorHistoryRowResponse? LastCompletedJob,
    IssueToShopFloorHistoryDetailResponse? LastExecution,
    IReadOnlyList<IssueToShopFloorHistoryRowResponse> RecentJobs);
