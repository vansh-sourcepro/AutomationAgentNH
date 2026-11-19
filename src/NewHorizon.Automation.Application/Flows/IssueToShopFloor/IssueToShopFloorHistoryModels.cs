using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// What opened an Issue to Shop Floor run.
/// </summary>
public sealed record StartIssueRunRequest(
    TriggerSource TriggerSource,
    string IssueSource,
    string? TriggeredBy = null,
    string? TriggerReference = null,
    string? RequestedSites = null);

public sealed class IssueToShopFloorHistoryQuery
{
    public string? Search { get; set; }
    public string? IssueSource { get; set; }
    public JobStatus? Status { get; set; }
    public TriggerSource? TriggerSource { get; set; }
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record IssueToShopFloorHistoryRow(
    Guid JobId,
    Guid? RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string? CurrentStage,
    string? IssueNumber,
    int LineCount,
    decimal TotalQuantity,
    string Trigger,
    string? TriggeredBy,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason);

public sealed record IssueToShopFloorStepRow(
    string Stage,
    string OperationName,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Remarks,
    string? ErpDocumentRef);

public sealed record IssueToShopFloorHistoryDetail(
    Guid JobId,
    Guid? RunId,
    string IssueSource,
    string DocumentNumber,
    string Status,
    string? CurrentStage,
    string? IssueNumber,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Reason,
    IReadOnlyList<IssueToShopFloorStepRow> Steps,
    string? ChecksJson,
    string? ShortagesJson,
    string? LinesJson);

public sealed record IssueToShopFloorHistorySummary(
    int TotalAttempts,
    int IssuesCreated,
    int ShortagesOrRefusals,
    int SystemFailures,
    double SuccessRatePercentage,
    double AverageDurationMs);
