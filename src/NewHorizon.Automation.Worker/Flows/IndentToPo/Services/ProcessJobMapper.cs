using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Contracts;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Services;

/// <summary>
/// Entities in, DTOs out. Kept in one place so no endpoint is tempted to return an entity, and so
/// a field added to a domain object does not leak into the API by accident.
/// </summary>
internal static class ProcessJobMapper
{
    public static ProcessRunResponse ToResponse(AutomationRun run) =>
        new(
            run.Id,
            run.CorrelationId,
            run.WorkflowType,
            run.TriggerSource.ToString(),
            run.TriggeredBy,
            run.TriggerReference,
            run.Mode.ToString(),
            run.RequestedIndentTypes,
            run.RequestedSites,
            run.MaxIndents,
            run.Status.ToString(),
            run.IndentsExamined,
            run.JobsCreated,
            run.PurchaseOrdersCreated,
            run.FailureReason,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.DurationMs);

    /// <summary>
    /// One grid line. The company is passed in rather than read from the row: it is configuration,
    /// the same on every row of this installation, and storing it per row would be a tenant column
    /// this deployment deliberately does not have.
    /// </summary>
    public static ProcessJobRowResponse ToResponse(ProcessExecutionRow row, int companyId) =>
        new(
            row.JobId,
            row.RunId,
            row.CorrelationId,
            row.IndentId,
            row.IndentKind.ToString(),
            row.IndentNumber,
            row.SiteId,
            companyId,
            row.WorkflowType,

            // Null stays null rather than becoming "Unknown": a job with no run genuinely has no
            // trigger, and inventing a word for that would make it unfilterable.
            row.TriggerSource?.ToString(),
            row.TriggeredBy,
            row.Mode.ToString(),
            row.CurrentStage,
            row.Status.ToString(),
            row.DurationMs,
            row.CreatedAtUtc,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.PoNumber,
            row.FailureReason);

    public static ProcessJobSummaryResponse ToResponse(ProcessJobSummary summary) =>
        new(
            summary.TotalJobs,
            summary.CountsByStatus,
            summary.SuccessCount,
            summary.SuccessRate,
            summary.AverageDurationMs,
            summary.TotalTriggerAttempts,
            summary.TriggerAttemptsWithoutEligibleIndent,
            summary.BusinessRefusalCount,
            summary.TechnicalFailureCount);

    public static TrackedIndentResponse ToResponse(IndentPoConversion conversion) =>
        new(
            conversion.Id,
            conversion.IndentId,
            conversion.IndentKind.ToString(),
            conversion.IndentNumber,
            conversion.SiteId,
            conversion.IndentDate,
            conversion.FirstSeenAtUtc);

    public static ProcessOutcomeResponse ToResponse(IndentPoOutcome outcome) =>
        new(
            outcome.Sequence,
            outcome.Outcome.ToString(),
            outcome.VendorCode,
            outcome.CurrencyCode,
            outcome.RateStructureCode,
            outcome.PoId,
            outcome.PoNumber,
            outcome.LineCount,
            outcome.Reason);

    public static ProcessStageResponse ToResponse(JobStep step) =>
        new(
            step.Sequence,
            step.Stage,

            // Remarks carries the finer task when the conversion reported one; the operation name
            // is the stage's primary task, fixed when the plan was laid down.
            step.Remarks ?? step.OperationName,
            step.Status.ToString(),
            step.RetryCount,
            step.ErpDocumentRef,
            step.StartedAtUtc,
            step.CompletedAtUtc);

    public static ProcessExecutionResponse ToResponse(ProcessExecution execution) =>
        new(
            execution.Job.Id,
            execution.Job.CorrelationId,
            execution.Job.RunId,
            ToResponse(execution.Conversion),
            execution.Job.WorkflowType,
            execution.Job.Mode.ToString(),
            execution.Job.Status.ToString(),
            execution.Job.CurrentStage,
            execution.AttemptNo,
            execution.Job.RetryCount,
            execution.Job.CreatedAtUtc,
            execution.Job.StartedAtUtc,
            execution.Job.CompletedAtUtc,
            execution.Job.DurationMs,
            [.. execution.Outcomes.Select(ToResponse)],
            execution.FailureReason);

    public static ProcessExecutionDetailResponse ToResponse(ProcessExecutionDetail detail) =>
        new(
            ToResponse(detail.Execution),
            [.. detail.Execution.Job.Steps.OrderBy(step => step.Sequence).Select(ToResponse)],
            [.. detail.Errors.Select(ToResponse)]);

    public static ProcessRunDetailResponse ToResponse(ProcessRunDetail detail) =>
        new(
            ToResponse(detail.Run),
            [.. detail.Executions.Select(ToResponse)]);

    public static DailyConversionStatResponse ToResponse(DailyConversionStat stat) =>
        new(
            stat.Date.ToString("yyyy-MM-dd"),
            stat.PurchaseOrdersCreated,
            stat.IndentsConverted,
            stat.IndentsFailed);

    public static IndentHistoryResponse ToResponse(long indentId, IndentExecutionHistory history) =>
        new(
            indentId,
            history.Ambiguous,
            [.. history.Conversions.Select(ToResponse)],
            [.. history.Executions.Select(ToResponse)]);

    private static JobErrorResponse ToResponse(AutomationError error) =>
        new(
            error.Id,
            error.StepId,
            error.ErrorType.ToString(),
            error.LaymanMessage,
            error.TechnicalMessage,
            error.ApiEndpoint,
            error.CreatedAtUtc);
}
