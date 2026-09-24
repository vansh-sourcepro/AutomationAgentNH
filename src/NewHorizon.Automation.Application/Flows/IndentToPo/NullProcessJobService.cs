using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// Tracking for an installation that has no automation database: every call succeeds and records
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// Indent → PO has always worked without a database — it needs no job row, because the ERP itself
/// is what stops an indent being ordered twice — and a deployment without one is supported. This
/// keeps that true: the conversion runs, the purchase order is created and returned, and only the
/// history is missing.
/// </para>
/// <para>
/// It says so, rather than pretending. <see cref="IsEnabled"/> is false, so an endpoint can answer
/// "tracking is off on this installation" instead of handing back a job id that leads nowhere.
/// One warning is logged the first time a run would have been recorded, not on every call.
/// </para>
/// </remarks>
public sealed class NullProcessJobService : IProcessJobService, IIndentPoTracker
{
    private readonly ILogger<NullProcessJobService> _logger;
    private bool _warned;

    public NullProcessJobService(ILogger<NullProcessJobService> logger) => _logger = logger;

    public bool IsEnabled => false;

    public Guid? RunId => null;

    public Guid? ExecutionId => null;

    public Task StartRunAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        if (!_warned)
        {
            _warned = true;
            _logger.LogWarning(
                "Process tracking is off: no automation database is configured, so this conversion "
                + "will create purchase orders but leave no run, job or outcome behind");
        }

        return Task.CompletedTask;
    }

    public Task StartExecutionAsync(TrackedIndent indent, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CompleteExecutionAsync(
        IReadOnlyList<TrackedOutcome> outcomes,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteRunAsync(int indentsExamined, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task FailRunAsync(string reason, int indentsExamined, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<PagedResult<ProcessExecutionRow>> ListExecutionsAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<ProcessExecutionRow>([], 0, 1, 0));

    public Task<ProcessJobSummary> GetSummaryAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ProcessJobSummary(0, new Dictionary<string, int>(), 0, null, null, 0, 0, 0, 0));

    public Task<ProcessExecutionDetail?> GetExecutionAsync(
        Guid jobId,
        CancellationToken cancellationToken) => Task.FromResult<ProcessExecutionDetail?>(null);

    public Task<IndentExecutionHistory> GetExecutionsByIndentAsync(
        long indentId,
        IndentKind? kind,
        CancellationToken cancellationToken) => Task.FromResult(IndentExecutionHistory.None);

    public Task<ProcessRunDetail?> GetRunAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult<ProcessRunDetail?>(null);

    public Task<PagedResult<AutomationRun>> ListRunsAsync(
        ProcessRunQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<AutomationRun>([], 0, 1, 0));

    public Task<IReadOnlyList<DailyConversionStat>> GetDailyStatsAsync(
        int days,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DailyConversionStat>>([]);
}
