using System.Text.Json.Serialization;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

/// <summary>
/// A single Quotation → Sales Order conversion job item formatted to match
/// the standard automation dashboard and history response structure.
/// </summary>
public sealed record QuotationToSoJobItem(
    Guid JobId,
    Guid? RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string? CurrentStage,
    string? IssueNumber,
    int LineCount,
    decimal TotalQuantity,
    string? Trigger,
    string? TriggeredBy,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SalesOrderNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? QuotationNumber = null);

/// <summary>
/// Operational KPI totals across Quotation to Sales Order conversions for dashboard cards.
/// </summary>
public sealed record QuotationToSoDashboardSummary(
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
    decimal? TotalQuantity = null);

/// <summary>
/// Trailing calendar day conversion volume and outcome stats for dashboard trend charts.
/// </summary>
public sealed record QuotationToSoDailyStat(
    string Date,
    int IssuesCreated,
    int DocumentsConverted,
    int DocumentsRefused,
    int DocumentsFailed,
    decimal TotalQuantity);

/// <summary>
/// Step timeline execution detail for a single job.
/// </summary>
public sealed record QuotationToSoStepDetail(
    string Stage,
    string OperationName,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Remarks,
    string? ErpDocumentRef);

/// <summary>
/// Verification / rule checks run for a conversion.
/// </summary>
public sealed record QuotationToSoCheckDetail(
    string Name,
    bool Passed,
    string Detail);

/// <summary>
/// Execution detail for a single Quotation to Sales Order conversion.
/// </summary>
public sealed record QuotationToSoExecutionDetail(
    Guid JobId,
    Guid? RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string? CurrentStage,
    string? IssueNumber,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason,
    IReadOnlyList<QuotationToSoStepDetail> Steps,
    IReadOnlyList<QuotationToSoCheckDetail> Checks,
    string? SalesOrderNumber = null);

/// <summary>
/// Complete composite dashboard payload containing summary metrics, daily timeline series,
/// last completed conversion, last execution details, and recent jobs.
/// </summary>
public sealed record QuotationToSoDashboardResponse(
    QuotationToSoDashboardSummary Summary,
    IReadOnlyList<QuotationToSoDailyStat> DailyStats,
    QuotationToSoJobItem? LastCompletedJob,
    QuotationToSoExecutionDetail? LastExecution,
    IReadOnlyList<QuotationToSoJobItem> RecentJobs);

