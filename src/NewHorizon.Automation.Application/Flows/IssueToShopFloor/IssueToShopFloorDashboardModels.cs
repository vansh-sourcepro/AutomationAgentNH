namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// Operational KPI totals across SJO to Issue to Shop Floor conversions.
/// </summary>
public sealed record IssueToShopFloorDashboardSummary(
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
/// Trailing daily conversion volume and status for dashboard charts.
/// </summary>
public sealed record DailyIssueConversionStat(
    DateOnly Date,
    int IssuesCreated,
    int DocumentsConverted,
    int DocumentsRefused,
    int DocumentsFailed,
    decimal TotalQuantity);

/// <summary>
/// Complete dashboard payload containing summary metrics, daily timeline series,
/// last completed conversion, last execution details, and recent jobs.
/// </summary>
public sealed record IssueToShopFloorDashboardComposite(
    IssueToShopFloorDashboardSummary Summary,
    IReadOnlyList<DailyIssueConversionStat> DailyStats,
    IssueToShopFloorHistoryRow? LastCompletedJob,
    IssueToShopFloorHistoryDetail? LastExecution,
    IReadOnlyList<IssueToShopFloorHistoryRow> RecentJobs);
