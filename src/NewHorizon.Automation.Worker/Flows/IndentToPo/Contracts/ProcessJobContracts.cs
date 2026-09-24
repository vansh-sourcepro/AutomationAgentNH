using NewHorizon.Automation.Worker.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;

/// <summary>
/// Asks for authorised indents to be converted, and for the whole thing to be recorded.
/// </summary>
/// <param name="IndentId">
/// Convert exactly this indent. Null sweeps for every eligible one, as the untracked trigger does.
/// </param>
/// <param name="IndentTypes">
/// Which types this run may convert — any of "Regular", "Capital", "Service". Case-insensitive,
/// duplicates collapse, absent means all three. Recorded on the run as understood, so the history
/// can tell "found no Capital indents" from "was never allowed to look for Capital indents".
/// </param>
/// <param name="Trigger">
/// How this was asked for: "Api", "UserPrompt", "Chatbot", "Timer", "ErpPush", "Reconcile" or
/// "Manual". Defaults to "Api" — the honest answer when the caller does not say, since something
/// did call the API.
/// </param>
/// <param name="TriggeredBy">The user, service account or bot behind it.</param>
/// <param name="TriggerReference">
/// A chat session, an ERP request id, a ticket — whatever leads back to what asked. Worth sending:
/// it is the only thing that connects a purchase order to the conversation that wanted it.
/// </param>
public sealed record StartProcessJobRequest(
    long? IndentId = null,
    IReadOnlyList<string>? IndentTypes = null,
    string? IndentType = null,
    IReadOnlyList<int>? Sites = null,
    int? MaxIndents = null,
    string? Trigger = null,
    string? TriggeredBy = null,
    string? TriggerReference = null);

/// <summary>
/// One conversion as a grid line — the row behind "which indent, under which trigger, how far did
/// it get, how long did it take".
/// </summary>
/// <param name="Document">
/// The indent number as a person reads it, e.g. <c>26-27/TE/NF1/000012</c>. <paramref name="IndentId"/>
/// beside it is the ERP's own key, for drilling through to <c>/indent/{indentId}</c>.
/// </param>
/// <param name="Trigger">
/// Null only for a job enqueued outside a run — there is no trigger to name. Every job started
/// through <c>POST /api/process-jobs</c> has one.
/// </param>
/// <param name="Company">
/// The ERP company every conversion on this installation runs under, from
/// <c>AutomationAgent:ErpApi:CompanyId</c> — the same value the agent sends as the <c>CompanyId</c>
/// header on every ERP call. It is a constant per installation, not a per-row fact: this deployment
/// serves one client's ERP, so there is no company column to store and none is invented here.
/// </param>
/// <param name="Stage">Where it is now, or where it stopped. Null before the first stage opens.</param>
/// <param name="DurationMs">Null while it is still running; computed by the database once it ends.</param>
/// <param name="PoNumber">The purchase order(s) this row produced, joined; null when it created none.</param>
/// <param name="FailureReason">
/// Why no purchase order came of this row — the technical failure when the job is Failed, else the
/// business reason a completed execution ordered nothing. Null when a purchase order was created,
/// or the job has not finished.
/// </param>
public sealed record ProcessJobRowResponse(
    Guid JobId,
    Guid? RunId,
    string CorrelationId,
    long IndentId,
    string IndentType,
    string Document,
    int SiteId,
    int Company,
    string Workflow,
    string? Trigger,
    string? TriggeredBy,
    string Mode,
    string? Stage,
    string Status,
    long? DurationMs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? PoNumber,
    string? FailureReason);

/// <summary>One vendor group's result: the order it produced, or the reason there is none.</summary>
/// <param name="Outcome">"Created", "Skipped" or "Failed".</param>
/// <param name="Reason">Always present unless the outcome is "Created".</param>
public sealed record ProcessOutcomeResponse(
    int Sequence,
    string Outcome,
    string? VendorCode,
    string? CurrencyCode,
    string? RateStructureCode,
    long? PoId,
    string? PoNumber,
    int LineCount,
    string? Reason);

/// <summary>One stage of one execution, and the task recorded against it.</summary>
public sealed record ProcessStageResponse(
    int Sequence,
    string Stage,
    string Task,
    string Status,
    int RetryCount,
    string? ErpDocumentRef,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>The indent an execution is an attempt at. Identity only — the ERP owns the rest.</summary>
public sealed record TrackedIndentResponse(
    Guid ConversionId,
    long IndentId,
    string IndentType,
    string IndentNumber,
    int SiteId,
    DateOnly? IndentDate,
    DateTimeOffset FirstSeenAtUtc);

/// <summary>
/// One attempt at one indent: the fields the process view asks for, in one row.
/// </summary>
/// <param name="AttemptNo">Which attempt this is, counting from one.</param>
/// <param name="DurationMs">Null while it is still running.</param>
public sealed record ProcessExecutionResponse(
    Guid JobId,
    string CorrelationId,
    Guid? RunId,
    TrackedIndentResponse Indent,
    string Workflow,
    string Mode,
    string Status,
    string? CurrentStage,
    int AttemptNo,
    int RetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    IReadOnlyList<ProcessOutcomeResponse> Outcomes,
    string? FailureReason);

/// <summary>An execution with its stage timeline and anything that went wrong.</summary>
public sealed record ProcessExecutionDetailResponse(
    ProcessExecutionResponse Execution,
    IReadOnlyList<ProcessStageResponse> Stages,
    IReadOnlyList<JobErrorResponse> Errors);

/// <summary>
/// One trigger invocation. Worth reading even when it started nothing: the counts and
/// <paramref name="IndentTypes"/> are what explain a run that converted nothing.
/// </summary>
public sealed record ProcessRunResponse(
    Guid RunId,
    string CorrelationId,
    string Workflow,
    string Trigger,
    string? TriggeredBy,
    string? TriggerReference,
    string Mode,
    string IndentTypes,
    string? Sites,
    int? MaxIndents,
    string Status,
    int IndentsExamined,
    int ExecutionsTracked,
    int PurchaseOrdersCreated,
    string? FailureReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs);

/// <summary>
/// The conversion grid collapsed to totals — the same filter <see cref="ProcessJobRowResponse"/>'s
/// grid takes, answered as counts and an average instead of rows.
/// </summary>
/// <param name="CountsByStatus">
/// Every <c>AutomationJob</c> status present, keyed by name (e.g. "Completed", "Running", "Failed").
/// Open-ended by design: a new status, or a breakdown a caller wants added later, belongs here or
/// as a sibling dictionary rather than a new named property, so adding one never breaks an existing
/// reader of this response.
/// </param>
/// <param name="SuccessCount">
/// Jobs that actually produced a purchase order — narrower than "Completed", which also counts a
/// job that examined an indent and found nothing left to order.
/// </param>
/// <param name="SuccessRate">0-1, or null when <paramref name="TotalJobs"/> is 0.</param>
/// <param name="AverageDurationMs">
/// Mean time a finished job took. Null when none in the filter have finished yet.
/// </param>
/// <param name="TotalTriggerAttempts">
/// Every call to a conversion endpoint in the filter, whether or not it found anything to convert —
/// always &gt;= <paramref name="TotalJobs"/>. This is "the system was asked to try"; <see cref="TotalJobs"/>
/// is "an eligible, authorised indent was found and conversion actually started".
/// </param>
/// <param name="TriggerAttemptsWithoutEligibleIndent">
/// Of <paramref name="TotalTriggerAttempts"/>, how many found no eligible, authorised indent and so
/// never became one of <paramref name="TotalJobs"/>.
/// </param>
/// <param name="BusinessRefusalCount">
/// Jobs that reached a definite business-rule "no" and produced zero purchase orders — e.g. "nothing
/// left to order", "no item/vendor purchase record", "contains an LBT item", "not authorised".
/// Distinct from <paramref name="SuccessCount"/> (got a PO) and from
/// <paramref name="TechnicalFailureCount"/>, even though both a business refusal and a technical
/// failure can be <c>Status = Failed</c> and so both land in the "Failed" bucket of
/// <paramref name="CountsByStatus"/> — that dictionary counts every Failed job regardless of reason.
/// </param>
/// <param name="TechnicalFailureCount">
/// Failed jobs that are NOT one of <paramref name="BusinessRefusalCount"/> — a genuine
/// technical/retryable problem (ERP timeout, network error), not a business rule refusing the
/// indent. <paramref name="SuccessCount"/> + <paramref name="BusinessRefusalCount"/> +
/// <paramref name="TechnicalFailureCount"/> together account for every Completed and Failed job
/// exactly once, unlike summing against <c>CountsByStatus["Failed"]</c> instead of this field.
/// </param>
public sealed record ProcessJobSummaryResponse(
    int TotalJobs,
    IReadOnlyDictionary<string, int> CountsByStatus,
    int SuccessCount,
    double? SuccessRate,
    double? AverageDurationMs,
    int TotalTriggerAttempts,
    int TriggerAttemptsWithoutEligibleIndent,
    int BusinessRefusalCount,
    int TechnicalFailureCount);

/// <summary>
/// One calendar day's (UTC) conversion activity — the dashboard chart's unit. <paramref name="Date"/>
/// is <c>yyyy-MM-dd</c> rather than a timestamp, since a day has no time component to disagree about.
/// </summary>
/// <param name="PurchaseOrdersCreated">PO documents created this day — the "Daily PO Generated" chart.</param>
/// <param name="IndentsConverted">Conversion processes that produced a PO this day.</param>
/// <param name="IndentsFailed">Conversion processes that ended Failed this day.</param>
public sealed record DailyConversionStatResponse(
    string Date,
    int PurchaseOrdersCreated,
    int IndentsConverted,
    int IndentsFailed);

/// <summary>A run and the executions it started.</summary>
public sealed record ProcessRunDetailResponse(
    ProcessRunResponse Run,
    IReadOnlyList<ProcessExecutionResponse> Executions);

/// <summary>
/// Every attempt at one indent, oldest first.
/// </summary>
/// <param name="Ambiguous">
/// True when the id is live as both a material and a service indent and the caller did not say
/// which. <c>XINDID</c> and <c>XINDAUTOID</c> are keys into different ERP tables, so the answer is
/// "say which", not a guess. Re-send with <c>?indentType=service</c>.
/// </param>
public sealed record IndentHistoryResponse(
    long IndentId,
    bool Ambiguous,
    IReadOnlyList<TrackedIndentResponse> Conversions,
    IReadOnlyList<ProcessExecutionResponse> Executions);
