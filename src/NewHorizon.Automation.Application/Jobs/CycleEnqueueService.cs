using System.Globalization;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Jobs;

/// <summary>
/// The single funnel every trigger goes through to start a cycle — the timer, and the manual
/// "run now" call. Writing the enqueue once is what keeps the two from diverging.
/// </summary>
public interface ICycleEnqueueService
{
    /// <summary>
    /// Starts a cycle unless one is already live or configuration forbids it.
    /// </summary>
    /// <param name="respectSchedule">
    /// True for the timer, which must honour the licence, the enable flags and the working-hours
    /// window. False for a manual run, where an operator has asked for it explicitly and only the
    /// one-live-cycle rule still applies.
    /// </param>
    Task<CycleEnqueueOutcome> EnqueueAsync(
        string workflowType,
        bool respectSchedule,
        int priority,
        CancellationToken cancellationToken);
}

/// <summary>
/// Why a tick did or did not start a cycle. "Not started" is the common, healthy case — most ticks
/// find the previous cycle still running — so it is a result rather than an exception.
/// </summary>
public sealed record CycleEnqueueOutcome(bool Started, Guid? JobId, string Reason)
{
    public static CycleEnqueueOutcome Skipped(string reason) => new(false, null, reason);
}

public sealed class CycleEnqueueService : ICycleEnqueueService
{
    private readonly IWorkflowCatalog _catalog;
    private readonly IJobRepository _jobs;
    private readonly IAutomationConfigRepository _configs;
    private readonly IClock _clock;
    private readonly ILogger<CycleEnqueueService> _logger;

    public CycleEnqueueService(
        IWorkflowCatalog catalog,
        IJobRepository jobs,
        IAutomationConfigRepository configs,
        IClock clock,
        ILogger<CycleEnqueueService> logger)
    {
        _catalog = catalog;
        _jobs = jobs;
        _configs = configs;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CycleEnqueueOutcome> EnqueueAsync(
        string workflowType,
        bool respectSchedule,
        int priority,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        // Read fresh every time: a flag flipped in the UI takes effect on the next tick, with no
        // restart and no cached copy to go stale.
        var config = await _configs.GetOrDefaultAsync(workflowType, cancellationToken);

        if (respectSchedule)
        {
            if (!config.IsAutomationPermitted)
            {
                return CycleEnqueueOutcome.Skipped(
                    "Automation is disabled for this module, or the installation is not licensed.");
            }

            if (!config.IsWithinWorkingHours(_clock.LocalTimeOfDay))
            {
                return CycleEnqueueOutcome.Skipped("Outside the configured working hours.");
            }
        }

        var definition = _catalog.Get(workflowType);
        var nowUtc = _clock.UtcNow;

        // A cycle has no document. Its identity is the moment it started, which keeps every cycle
        // distinct in the job list; the live-cycle index is what stops two running at once.
        var cycleId = nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var job = Job.Create(
            definition.WorkflowType,
            DocumentTypes.Cycle,
            cycleId,
            config.Mode,
            nowUtc,
            priority);

        job.PlanSteps(definition.Plan());

        var result = await _jobs.EnqueueAsync(job, cancellationToken);

        if (!result.WasCreated)
        {
            _logger.LogDebug(
                "Cycle not started: {WorkflowType} job {JobId} is already live",
                workflowType,
                result.Job.Id);

            return new CycleEnqueueOutcome(false, result.Job.Id, "A cycle is already running.");
        }

        _logger.LogInformation(
            "Cycle {CycleId} enqueued as job {JobId} in {Mode} mode",
            cycleId,
            result.Job.Id,
            config.Mode);

        return new CycleEnqueueOutcome(true, result.Job.Id, "Cycle enqueued.");
    }
}
