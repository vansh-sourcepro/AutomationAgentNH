using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>What opened a run, and what it was allowed to do.</summary>
/// <param name="RequestedIndentTypes">
/// The allow-list as the agent understood it — expanded when the caller named nothing, because an
/// absent filter is not a filter. Stored verbatim so a reader can tell "found no Capital indents"
/// from "was never allowed to look for Capital indents".
/// </param>
public sealed record StartRunRequest(
    TriggerSource TriggerSource,
    string RequestedIndentTypes,
    string? TriggeredBy = null,
    string? TriggerReference = null,
    string? RequestedSites = null,
    int? MaxIndents = null);

/// <summary>The indent an execution is an attempt at. Everything here comes from the ERP's answer.</summary>
public sealed record TrackedIndent(
    long IndentId,
    IndentKind Kind,
    string IndentNumber,
    int SiteId,
    DateOnly? IndentDate = null);

/// <summary>One vendor group's result, as the conversion reports it.</summary>
public sealed record TrackedOutcome(
    PoOutcomeKind Kind,
    string? VendorCode = null,
    string? CurrencyCode = null,
    string? RateStructureCode = null,
    long? PoId = null,
    string? PoNumber = null,
    int LineCount = 0,
    string? Reason = null)
{
    public static TrackedOutcome Order(
        string vendorCode,
        long poId,
        string poNumber,
        int lineCount,
        string? currencyCode = null,
        string? rateStructureCode = null) =>
        new(PoOutcomeKind.Created, vendorCode, currencyCode, rateStructureCode, poId, poNumber, lineCount);

    public static TrackedOutcome Note(string reason, string? vendorCode = null) =>
        new(PoOutcomeKind.Skipped, vendorCode, Reason: reason);

    public static TrackedOutcome Refusal(string reason, string? vendorCode = null) =>
        new(PoOutcomeKind.Failed, vendorCode, Reason: reason);
}

/// <summary>
/// One attempt at one indent, with the indent it belongs to and what it produced.
/// </summary>
/// <param name="AttemptNo">
/// Which attempt this is, counting from one. Derived by ordering the conversion's executions
/// rather than stored: a stored copy would be a second version of the same ordering.
/// </param>
/// <param name="FailureReason">
/// Why no purchase order came of this execution: the most recent recorded error's layman message
/// (covers a whole-indent guard refusal — not authorised, contains an LBT item — which is thrown
/// before any vendor group is reached and so has no outcome row of its own), falling back to the
/// business reason recorded against a non-Created outcome when there is no error. Null when a
/// purchase order was created, or the job has not finished.
/// </param>
public sealed record ProcessExecution(
    Job Job,
    IndentPoConversion Conversion,
    IReadOnlyList<IndentPoOutcome> Outcomes,
    int AttemptNo,
    string? FailureReason = null);

/// <summary>An execution with the failures recorded against it.</summary>
public sealed record ProcessExecutionDetail(
    ProcessExecution Execution,
    IReadOnlyList<AutomationError> Errors);

/// <summary>One trigger invocation and the executions it started.</summary>
public sealed record ProcessRunDetail(
    AutomationRun Run,
    IReadOnlyList<ProcessExecution> Executions);

/// <summary>
/// One conversion as a grid line: which indent, under which trigger, how far it got, how long it
/// took.
/// </summary>
/// <remarks>
/// Flat on purpose. It is projected straight out of the join rather than assembled from
/// <see cref="ProcessExecution"/>, which carries the job aggregate and its five steps — fifty grid
/// lines would otherwise drag two hundred and fifty step rows into memory to display none of them.
/// <para>
/// No attempt number: ranking within a conversion costs a window function per page, and the
/// number is already on the detail and per-indent views, which is where anyone reads it.
/// </para>
/// </remarks>
/// <param name="PoNumber">
/// The purchase order(s) this execution produced, joined for display — null when it created none
/// (still running, failed, or completed having found nothing left to order).
/// </param>
/// <param name="FailureReason">
/// Why no purchase order came of this execution: the recorded error's layman message when the job
/// is <see cref="JobStatus.Failed"/> (technical) or <see cref="JobStatus.Skipped"/> (business-rule
/// refusal), else the business reason recorded against a non-Created outcome (an execution that
/// completed but had nothing left to order). Null when a purchase order was created, or the job has
/// not finished.
/// </param>
public sealed record ProcessExecutionRow(
    Guid JobId,
    Guid? RunId,
    string CorrelationId,
    long IndentId,
    IndentKind IndentKind,
    string IndentNumber,
    int SiteId,
    string WorkflowType,
    TriggerSource? TriggerSource,
    string? TriggeredBy,
    AutomationMode Mode,
    string? CurrentStage,
    JobStatus Status,
    long? DurationMs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? PoNumber,
    string? FailureReason);

/// <summary>
/// Filter for the conversion grid. Page size is clamped by the service, not the caller.
/// </summary>
/// <remarks>
/// Every filter is optional and they narrow together. <see cref="Stage"/> is a string rather than
/// an enum because stages are declared as constants in <c>IndentPoStages</c>, but the endpoint
/// still validates it against that list — a typo must be refused, not answered with an empty grid
/// that reads as "nothing happened".
/// </remarks>
public sealed record ProcessJobQuery
{
    /// <summary>
    /// Partial, case-insensitive text match across the fields a person actually searches by: the
    /// indent number, the correlation id, who triggered it, and the purchase order numbers the
    /// conversion produced. Translated to <c>LIKE</c>, so it runs in SQL Server, not in memory.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// The workflow the job belongs to — `AutomationJob.WorkflowType`. Every row in this grid is
    /// `IndentToPurchaseOrder` today, because the grid is scoped to conversions; the filter exists
    /// so it keeps working when a second workflow starts recording conversions.
    /// </summary>
    public string? WorkflowType { get; init; }

    /// <summary>
    /// The ERP company. There is no company column — see
    /// <c>.claude/context/single-tenant-deployment.md</c> — so this is matched against the
    /// configured <c>ErpApi:CompanyId</c> rather than against a row. A value that matches filters
    /// nothing out; a value that does not returns an empty page without querying at all.
    /// </summary>
    public string? Company { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public JobStatus? Status { get; init; }

    public TriggerSource? TriggerSource { get; init; }

    public string? Stage { get; init; }

    public IndentKind? IndentKind { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Aggregate counts and durations across the conversion grid — the same population
/// <see cref="ProcessJobQuery"/> would page through, collapsed to totals instead of rows.
/// </summary>
/// <param name="TotalJobs">Every job matching the filter, whatever its status.</param>
/// <param name="CountsByStatus">
/// One entry per <see cref="JobStatus"/> actually present, keyed by its name. Open-ended on
/// purpose — a new status, or a new breakdown a caller wants (by trigger, by indent type), belongs
/// here or as a sibling dictionary rather than as a new named property, so adding one is additive
/// and never breaks an existing reader.
/// </param>
/// <param name="SuccessCount">
/// Jobs with at least one <see cref="PoOutcomeKind.Created"/> outcome — a job can be
/// <see cref="JobStatus.Completed"/> having ordered nothing (there was nothing left to order), so
/// this is a narrower and more useful "did it actually work" figure than the status count alone.
/// </param>
/// <param name="SuccessRate">
/// <see cref="SuccessCount"/> over <see cref="TotalJobs"/>, or null when there are no jobs to rate.
/// </param>
/// <param name="AverageDurationMs">
/// Mean of <c>AutomationJob.DurationMs</c> across jobs that have finished (a running job has no
/// duration yet, so it is excluded rather than counted as zero); null when none have.
/// </param>
/// <param name="TotalTriggerAttempts">
/// Every <see cref="AutomationRun"/> in the filter — one row per call to a conversion endpoint,
/// whether or not it found anything to convert. Always &gt;= <paramref name="TotalJobs"/>: a run
/// that found an eligible, authorised indent and started converting it is also counted there, but a
/// run that found nothing is counted only here.
/// </param>
/// <param name="TriggerAttemptsWithoutEligibleIndent">
/// Of <paramref name="TotalTriggerAttempts"/>, how many opened a run but never found an eligible,
/// authorised indent to start converting (<see cref="AutomationRun.JobsCreated"/> is zero) — a
/// trigger that must not be counted as a conversion process.
/// </param>
/// <param name="BusinessRefusalCount">
/// Jobs that reached a definite business-rule "no" and produced zero purchase orders — the union of
/// (a) <see cref="JobStatus.Completed"/> jobs whose only <see cref="IndentPoOutcome"/> rows are
/// <see cref="PoOutcomeKind.Skipped"/> (e.g. "nothing left to order", "no item/vendor purchase
/// record") and (b) every <see cref="JobStatus.Skipped"/> job (e.g. a whole-indent guard refusal —
/// not authorised, contains an LBT item). Distinct from <see cref="SuccessCount"/> (got at least one
/// PO) and from <see cref="TechnicalFailureCount"/> (below). A genuinely unexpected/unhandled
/// exception is also recorded with <see cref="ErrorType.Business"/> and lands on
/// <see cref="JobStatus.Skipped"/> today (there is no third "unknown bug" bucket), so this count can
/// very slightly overstate true business refusals; accepted as rare in practice.
/// </param>
/// <param name="TechnicalFailureCount">
/// Every <see cref="JobStatus.Failed"/> job — technical/system errors only, since a business-rule
/// refusal now lands on <see cref="JobStatus.Skipped"/> instead and is counted in
/// <paramref name="BusinessRefusalCount"/>. <paramref name="SuccessCount"/> +
/// <paramref name="BusinessRefusalCount"/> + <see cref="TechnicalFailureCount"/> together account for
/// every <see cref="JobStatus.Completed"/>, <see cref="JobStatus.Failed"/> and
/// <see cref="JobStatus.Skipped"/> job exactly once.
/// </param>
public sealed record ProcessJobSummary(
    int TotalJobs,
    IReadOnlyDictionary<string, int> CountsByStatus,
    int SuccessCount,
    double? SuccessRate,
    double? AverageDurationMs,
    int TotalTriggerAttempts,
    int TriggerAttemptsWithoutEligibleIndent,
    int BusinessRefusalCount,
    int TechnicalFailureCount);

/// <summary>Filter for the runs list. Page size is clamped by the service, not the caller.</summary>
public sealed record ProcessRunQuery
{
    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public TriggerSource? TriggerSource { get; init; }

    public RunStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <summary>
/// One calendar day's (UTC) worth of conversion activity — the unit the dashboard's charts are
/// built from.
/// </summary>
/// <param name="PurchaseOrdersCreated">
/// How many purchase order <em>documents</em> were created this day (a job that produces orders for
/// several vendor groups counts more than once here) — the "Daily PO Generated" chart's value.
/// </param>
/// <param name="IndentsConverted">
/// How many conversion processes (jobs) actually produced at least one purchase order this day —
/// narrower than <see cref="PurchaseOrdersCreated"/> when one job produced several, and the same
/// "did it actually work" rule <see cref="ProcessJobSummary.SuccessCount"/> uses. The "Converted to
/// PO" series of the "Daily PO Conversion Status" chart.
/// </param>
/// <param name="IndentsFailed">
/// How many conversion processes ended <see cref="JobStatus.Failed"/> this day — a genuine
/// technical/system failure, not a business-rule refusal (<see cref="JobStatus.Skipped"/>) and not a
/// job that completed having found nothing left to order. The "Failed" series of the same chart.
/// </param>
public sealed record DailyConversionStat(
    DateOnly Date,
    int PurchaseOrdersCreated,
    int IndentsConverted,
    int IndentsFailed);

/// <summary>
/// Every attempt at one indent id. <paramref name="Ambiguous"/> is true when the id is live as
/// both a material and a service indent and the caller did not say which — <c>XINDID</c> and
/// <c>XINDAUTOID</c> are keys into different ERP tables, so guessing would be a coin toss.
/// </summary>
public sealed record IndentExecutionHistory(
    IReadOnlyList<ProcessExecution> Executions,
    IReadOnlyList<IndentPoConversion> Conversions,
    bool Ambiguous)
{
    public static IndentExecutionHistory None { get; } = new([], [], false);
}
