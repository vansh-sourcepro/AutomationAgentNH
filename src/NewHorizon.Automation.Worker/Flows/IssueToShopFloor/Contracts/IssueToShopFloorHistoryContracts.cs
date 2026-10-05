using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

/// <summary>
/// Row representation for the Issue to Shop Floor history grid.
/// </summary>
public sealed record IssueToShopFloorHistoryRowResponse(
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

/// <summary>
/// Stage and task step row in an execution timeline.
/// </summary>
public sealed record IssueToShopFloorStepResponse(
    string Stage,
    string OperationName,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? Remarks,
    string? ErpDocumentRef);

/// <summary>
/// Full detail of one Issue to Shop Floor execution.
/// </summary>
public sealed record IssueToShopFloorHistoryDetailResponse(
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
    IReadOnlyList<IssueToShopFloorStepResponse> Steps,
    [property: JsonPropertyName("checks")] JsonElement? Checks,
    [property: JsonPropertyName("shortages")] JsonElement? Shortages,
    [property: JsonPropertyName("lines")] JsonElement? Lines);

/// <summary>
/// Aggregated summary statistics for Issue to Shop Floor executions.
/// </summary>
public sealed record IssueToShopFloorHistorySummaryResponse(
    int TotalAttempts,
    int IssuesCreated,
    int ShortagesOrRefusals,
    int SystemFailures,
    double SuccessRatePercentage,
    double AverageDurationMs);
