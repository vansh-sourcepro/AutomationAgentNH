using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// Persistence for the three process-tracking tables, against the automation database only.
/// </summary>
/// <remarks>
/// Jobs, steps, errors and logs are not here: they belong to <see cref="Jobs.IJobRepository"/> and
/// are reused rather than re-implemented. This interface owns only what that one does not.
/// </remarks>
public interface IIndentPoTrackingRepository
{
    Task AddRunAsync(AutomationRun run, CancellationToken cancellationToken);

    Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken);

    Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutomationRun>> ListRunsAsync(
        ProcessRunQuery query,
        CancellationToken cancellationToken);

    Task<int> CountRunsAsync(ProcessRunQuery query, CancellationToken cancellationToken);

    /// <summary>The case for this indent, or null when it has never been attempted.</summary>
    Task<IndentPoConversion?> FindConversionAsync(
        long indentId,
        IndentKind kind,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every case with this id — at most two, since a material and a service indent can share one.
    /// Lets a read endpoint say "which did you mean" instead of guessing.
    /// </summary>
    Task<IReadOnlyList<IndentPoConversion>> FindConversionsAsync(
        long indentId,
        CancellationToken cancellationToken);

    Task<IndentPoConversion?> GetConversionAsync(Guid conversionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the case, or returns the one that beat it there. Two triggers can discover the same
    /// indent at the same moment; the natural key is what arbitrates, and the loser adopts the
    /// winner rather than failing the caller.
    /// </summary>
    Task<IndentPoConversion> AddConversionAsync(
        IndentPoConversion conversion,
        CancellationToken cancellationToken);

    Task UpdateConversionAsync(IndentPoConversion conversion, CancellationToken cancellationToken);

    Task AddOutcomesAsync(
        IReadOnlyCollection<IndentPoOutcome> outcomes,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IndentPoOutcome>> GetOutcomesAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<IndentPoOutcome>>> GetOutcomesAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The conversion grid: one flat row per execution, newest first, joined to its indent and its
    /// run. Projected rather than materialised as aggregates — a grid never reads the steps.
    /// </summary>
    Task<IReadOnlyList<ProcessExecutionRow>> ListExecutionRowsAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken);

    Task<int> CountExecutionRowsAsync(ProcessJobQuery query, CancellationToken cancellationToken);

    /// <summary>The same grid, aggregated instead of projected to rows — see <see cref="ProcessJobSummary"/>.</summary>
    Task<ProcessJobSummary> GetSummaryAsync(ProcessJobQuery query, CancellationToken cancellationToken);

    /// <summary>Every attempt at one indent, oldest first — which is also attempt order.</summary>
    Task<IReadOnlyList<Job>> GetExecutionsAsync(Guid conversionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Job>> GetExecutionsByRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// One row per UTC calendar day that had at least one finished job in range — days with nothing
    /// finished are simply absent, which is why <see cref="IProcessJobService.GetDailyStatsAsync"/>
    /// (the zero-filling caller) exists as a separate layer rather than this being the public read.
    /// </summary>
    Task<IReadOnlyList<DailyConversionStat>> GetDailyStatsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
