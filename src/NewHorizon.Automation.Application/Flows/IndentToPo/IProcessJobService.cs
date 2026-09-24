using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// The read side of process tracking: what happened, to which indent, under which trigger.
/// </summary>
/// <remarks>
/// Reads only. Starting a conversion is the composition of this and the ERP client, and lives in
/// the host — the Application layer cannot reference the ERP client, and should not: tracking has
/// no opinion about how a purchase order gets made.
/// </remarks>
public interface IProcessJobService
{
    /// <summary>False when there is no automation database, in which case everything below is empty.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Every conversion as a grid line, newest first — which indent, trigger, mode, stage, status
    /// and duration. The only read that spans conversions rather than drilling into one.
    /// </summary>
    Task<PagedResult<ProcessExecutionRow>> ListExecutionsAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same grid, collapsed to totals — status counts, how many actually produced a purchase
    /// order, and the average time a finished one took. Takes the same filter as
    /// <see cref="ListExecutionsAsync"/> (its paging is ignored) so a caller can ask "how is this
    /// slice doing" with the same query it already lists rows with.
    /// </summary>
    Task<ProcessJobSummary> GetSummaryAsync(ProcessJobQuery query, CancellationToken cancellationToken);

    /// <summary>One execution with its indent, stage timeline, outcomes and failures.</summary>
    Task<ProcessExecutionDetail?> GetExecutionAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Every execution for one indent, oldest first — the history and retry view.
    /// </summary>
    /// <param name="kind">
    /// Required only when the id exists as both a material and a service indent; null asks the
    /// service to work it out and report ambiguity rather than pick.
    /// </param>
    Task<IndentExecutionHistory> GetExecutionsByIndentAsync(
        long indentId,
        IndentKind? kind,
        CancellationToken cancellationToken);

    /// <summary>What one trigger invocation did, including a run that did nothing.</summary>
    Task<ProcessRunDetail?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<PagedResult<AutomationRun>> ListRunsAsync(
        ProcessRunQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// One row per calendar day (UTC) over the trailing <paramref name="days"/>, oldest first, with
    /// no gaps — a day nothing happened on is still a row, at zero, so a chart's x-axis stays a
    /// continuous date range rather than skipping quiet days.
    /// </summary>
    Task<IReadOnlyList<DailyConversionStat>> GetDailyStatsAsync(int days, CancellationToken cancellationToken);
}
