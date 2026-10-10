namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

/// <summary>
/// Composite dashboard response for the PO → GRN flow matching the automation dashboard view standard.
/// </summary>
public sealed record PoGrnDashboardResponse(
    PoGrnDashboardSummary Summary,
    IReadOnlyList<PoGrnDailyStat> DailyStats,
    PoGrnJobItem? LastCompletedJob,
    PoGrnExecutionDetail? LastExecution,
    IReadOnlyList<PoGrnJobItem> RecentJobs);

public sealed record PoGrnDashboardSummary(
    int TotalJobs,
    IReadOnlyDictionary<string, int> CountsByStatus,
    IReadOnlyDictionary<string, int> CountsBySource,
    int SuccessCount,
    double SuccessRate,
    double AverageDurationMs,
    int TotalTriggerAttempts,
    int TriggerAttemptsWithoutEligibleDocument,
    int BusinessRefusalCount,
    int TechnicalFailureCount,
    decimal TotalQuantityIssued,
    int TotalLinesIssued,
    decimal? TotalQuantityReceived = null,
    int? TotalLinesReceived = null,
    decimal? TotalQuantity = null);

public sealed record PoGrnDailyStat(
    string Date,
    int IssuesCreated,
    int DocumentsConverted,
    int DocumentsRefused,
    int DocumentsFailed,
    decimal TotalQuantity,
    int? GrnsCreated = null,
    int? PosConverted = null,
    int? PosRefused = null,
    int? PosFailed = null);

public sealed record PoGrnJobItem(
    Guid JobId,
    Guid RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string CurrentStage,
    string? IssueNumber,
    int LineCount,
    decimal TotalQuantity,
    string Trigger,
    string? TriggeredBy,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason,
    string? GrnNumber = null);

public sealed record PoGrnExecutionDetail(
    Guid JobId,
    Guid RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string CurrentStage,
    string? IssueNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason,
    IReadOnlyList<PoGrnStepDetail> Steps,
    IReadOnlyList<PoGrnCheckDetail> Checks,
    object? Shortages,
    IReadOnlyList<PoGrnLineDetail> Lines,
    string? GrnNumber = null);

public sealed record PoGrnStepDetail(
    string Stage,
    string OperationName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Remarks,
    string? ErpDocumentRef);

public sealed record PoGrnCheckDetail(
    string Name,
    bool Passed,
    string Detail);

public sealed record PoGrnLineDetail(
    string ItemCode,
    string WarehouseCode,
    long? StockId,
    int LineNo,
    decimal Quantity,
    long? SjoId,
    long? WoId,
    int? RandomNumber,
    string InwardNo,
    long? PoId = null);
