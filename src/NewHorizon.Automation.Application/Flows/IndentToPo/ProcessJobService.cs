using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// Process tracking for indent → purchase order: both what the conversion records as it runs and
/// what the API reads back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// One class behind two interfaces because they share the same scope and the same state. The
/// conversion calls <see cref="IIndentPoTracker"/> as it goes; the endpoints call
/// <see cref="IProcessJobService"/> afterwards. Registering them as two instances would give the
/// conversion a tracker whose run the reader could not see.
/// </para>
/// <para>
/// It creates jobs but never queues them. A conversion is executed inline by whoever triggered it,
/// so the job is inserted already Running: leaving it Pending would offer it to the dispatcher,
/// which would hand it to an engine that has no definition for this workflow.
/// </para>
/// </remarks>
public sealed class ProcessJobService : IProcessJobService, IIndentPoTracker
{
    /// <summary>
    /// The ceiling on any paged read. An unbounded page size would let one call pull the whole
    /// history into memory; the same limit applies to runs and to the conversion grid.
    /// </summary>
    private const int MaxPageSize = 200;

    /// <summary>The ceiling on the daily-stats chart window — a dashboard chart, not a report export.</summary>
    private const int MaxDailyStatsDays = 90;

    /// <summary>Stage → the task recorded against it when nothing more specific is reported.</summary>
    private static readonly Dictionary<string, string> PrimaryTasks = new(StringComparer.Ordinal)
    {
        [IndentPoStages.Discovery] = IndentPoTasks.GetIndentDetail,
        [IndentPoStages.VendorResolution] = IndentPoTasks.GroupByVendorTerms,
        [IndentPoStages.DocumentControl] = IndentPoTasks.GetDefaultDocumentDetail,
        [IndentPoStages.PendingLines] = IndentPoTasks.GetPendingItems,
        [IndentPoStages.CreatePurchaseOrder] = IndentPoTasks.CreatePoEntry,
    };

    private readonly IIndentPoTrackingRepository _tracking;
    private readonly IJobRepository _jobs;
    private readonly IAutomationConfigRepository _configs;

    /// <summary>Read for one thing only: the ERP company id the company filter is matched against.</summary>
    private readonly IOptions<AutomationAgentOptions> _options;

    private readonly IClock _clock;
    private readonly ILogger<ProcessJobService> _logger;

    private AutomationRun? _run;
    private Job? _execution;
    private int _jobsCreated;
    private int _purchaseOrdersCreated;

    public ProcessJobService(
        IIndentPoTrackingRepository tracking,
        IJobRepository jobs,
        IAutomationConfigRepository configs,
        IOptions<AutomationAgentOptions> options,
        IClock clock,
        ILogger<ProcessJobService> logger)
    {
        _tracking = tracking;
        _jobs = jobs;
        _configs = configs;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Guid? RunId => _run?.Id;

    public Guid? ExecutionId => _execution?.Id;

    // ---- write side: what the conversion reports as it goes ----------------

    public async Task StartRunAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_run is not null)
        {
            throw new InvalidOperationException(
                $"Run {_run.Id} is already open on this scope; one trigger is one run.");
        }

        // Captured once, here, and stamped on every job the run creates. A configuration change
        // mid-run cannot make one run behave two ways.
        var config = await _configs.GetOrDefaultAsync(
            WorkflowNames.IndentToPurchaseOrder,
            cancellationToken);

        _run = AutomationRun.Start(
            WorkflowNames.IndentToPurchaseOrder,
            request.TriggerSource,
            config.Mode,
            request.RequestedIndentTypes,
            _clock.UtcNow,
            request.TriggeredBy,
            request.TriggerReference,
            request.RequestedSites,
            request.MaxIndents);

        await _tracking.AddRunAsync(_run, cancellationToken);

        _logger.LogInformation(
            "Run {RunId} opened by {TriggerSource} in {Mode} mode for indent type(s) {IndentTypes}",
            _run.Id,
            request.TriggerSource,
            config.Mode,
            request.RequestedIndentTypes);
    }

    public async Task StartExecutionAsync(TrackedIndent indent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indent);

        if (_execution is not null)
        {
            // A sweep converts its indents one after another, so this means a previous execution
            // was never closed. Close it as failed rather than losing it.
            await FailExecutionAsync(
                "The conversion moved on to another indent without finishing this one.",
                $"Execution {_execution.Id} was still open when indent {indent.IndentId} started.",
                transient: false,
                cancellationToken);
        }

        var conversion = await OpenConversionAsync(indent, cancellationToken);

        // Read, never assumed. A run captures the mode once at StartRunAsync and every job it
        // creates is stamped with that capture, so one configuration change mid-run cannot make one
        // run behave two ways. With no run open there is still a configured mode, and reading it is
        // the only honest answer — defaulting to Full stamped Full on every job ever recorded here,
        // including on an installation set to Partial, and nothing in the row said it was a guess.
        var mode = _run?.Mode
            ?? (await _configs.GetOrDefaultAsync(WorkflowNames.IndentToPurchaseOrder, cancellationToken)).Mode;

        var job = Job.Create(
            WorkflowNames.IndentToPurchaseOrder,
            IndentDocumentTypes.For(indent.Kind),
            indent.IndentId.ToString(CultureInfo.InvariantCulture),
            mode,
            _clock.UtcNow,
            priority: 0,
            correlationId: _run?.CorrelationId);

        job.LinkToConversion(conversion.Id, _run?.Id);
        job.PlanSteps(IndentPoStages.InOrder.Select(stage =>
            new PlannedOperation(stage, PrimaryTasks[stage])));

        // Running from the moment it is inserted: see the class remarks.
        job.Claim(_clock.UtcNow);

        var result = await _jobs.EnqueueAsync(job, cancellationToken);
        if (!result.WasCreated)
        {
            // Another attempt at this indent is still live. Two would race for the same indent
            // lines, so this one is not tracked — and says so rather than silently sharing a row.
            _logger.LogWarning(
                "Indent {IndentNumber} already has live execution {JobId}; this attempt is untracked",
                conversion.IndentNumber,
                result.Job.Id);

            _execution = null;
            return;
        }

        _execution = result.Job;
        _jobsCreated++;

        await EnterStageAsync(
            IndentPoStages.Discovery,
            IndentPoTasks.ListAuthorisedIndents,
            cancellationToken);
    }

    public async Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        if (_execution is null)
        {
            return;
        }

        var now = _clock.UtcNow;

        CompleteRunningStep(now, erpDocumentRef: null);

        var step = _execution.Steps.FirstOrDefault(candidate =>
            candidate.Stage == stage && candidate.Status is StepStatus.Pending);

        if (step is null)
        {
            // Either the stage is not one of the five, or it has already been through. Neither is
            // worth failing a conversion over; the stage on the job still moves.
            _logger.LogDebug(
                "Execution {JobId} has no pending step for stage {Stage}",
                _execution.Id,
                stage);
        }
        else
        {
            step.Start(now);

            // The step's OperationName is the stage's primary task, fixed when the plan was laid
            // down. The task actually running is finer than that, so it is recorded beside it.
            if (!string.IsNullOrWhiteSpace(task) && task != step.OperationName)
            {
                step.SetRemarks(task);
            }
        }

        _execution.EnterStage(stage);

        await _jobs.SaveAsync(_execution, cancellationToken);
    }

    public async Task CompleteExecutionAsync(
        IReadOnlyList<TrackedOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (_execution is null)
        {
            return;
        }

        var job = _execution;
        var now = _clock.UtcNow;
        var orders = outcomes.Where(outcome => outcome.Kind is PoOutcomeKind.Created).ToList();

        // The create step carries the ERP's own reference, which is what makes the timeline
        // useful: the PO number is right there against the operation that produced it.
        CompleteRunningStep(now, DocumentRef(orders));

        // A stage the conversion never reached is skipped, not left pending: a job may only
        // complete once every operation is terminal, and "we never got there" is the truth.
        foreach (var pending in job.Steps.Where(step => !step.IsTerminal))
        {
            pending.Skip(now, "The conversion finished before this stage was needed.");
        }

        job.Complete(now);
        await _jobs.SaveAsync(job, cancellationToken);

        await RecordOutcomesAsync(job.Id, outcomes, cancellationToken);

        _purchaseOrdersCreated += orders.Count;
        _execution = null;
    }

    public async Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laymanMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(technicalMessage);

        if (_execution is null)
        {
            return;
        }

        var job = _execution;
        var now = _clock.UtcNow;

        var running = job.Steps.FirstOrDefault(step => step.Status is StepStatus.Running);
        running?.Fail(now, requestPayload: null, responsePayload: null);

        // Technical (transient) errors are recoverable — Failed, so retry/resume can pick them back
        // up. A business-rule refusal is not: retrying without the underlying data changing just
        // reproduces the same refusal, so it goes to the terminal Skipped status instead.
        if (transient)
        {
            job.Fail(now);
        }
        else
        {
            job.Skip(now);
        }

        await _jobs.SaveAsync(job, cancellationToken);

        await _jobs.AddErrorAsync(
            AutomationError.Create(
                job.Id,
                running?.Id,
                transient ? ErrorType.Technical : ErrorType.Business,
                technicalMessage,
                laymanMessage,
                now),
            cancellationToken);

        _execution = null;
    }

    public async Task CompleteRunAsync(int indentsExamined, CancellationToken cancellationToken)
    {
        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(indentsExamined, _jobsCreated, _purchaseOrdersCreated);
        _run.Complete(_clock.UtcNow);

        await _tracking.UpdateRunAsync(_run, cancellationToken);

        _logger.LogInformation(
            "Run {RunId} finished: {Examined} indent(s) examined, {Jobs} tracked, {Orders} purchase order(s)",
            _run.Id,
            indentsExamined,
            _jobsCreated,
            _purchaseOrdersCreated);
    }

    public async Task FailRunAsync(string reason, int indentsExamined, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(indentsExamined, _jobsCreated, _purchaseOrdersCreated);
        _run.Fail(reason, _clock.UtcNow);

        await _tracking.UpdateRunAsync(_run, cancellationToken);
    }

    // ---- read side ---------------------------------------------------------

    public async Task<PagedResult<ProcessExecutionRow>> ListExecutionsAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Clamped here rather than at the endpoint, so every caller gets the same ceiling — the
        // same rule ListRunsAsync applies.
        var bounded = query with
        {
            Page = Math.Max(query.Page, 1),
            PageSize = Math.Clamp(query.PageSize, 1, MaxPageSize),
        };

        // Company is configuration, not a column: this installation serves one client's ERP, so
        // every row belongs to the same company and there is nothing per-row to filter on. Asking
        // for the configured company is therefore no filter at all; asking for any other company
        // is a question whose answer is empty, and it is answered without touching the database.
        if (!MatchesConfiguredCompany(bounded.Company))
        {
            return new PagedResult<ProcessExecutionRow>([], 0, bounded.Page, bounded.PageSize);
        }

        var items = await _tracking.ListExecutionRowsAsync(bounded, cancellationToken);
        var total = await _tracking.CountExecutionRowsAsync(bounded, cancellationToken);

        return new PagedResult<ProcessExecutionRow>(items, total, bounded.Page, bounded.PageSize);
    }

    public async Task<ProcessJobSummary> GetSummaryAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Same company rule as ListExecutionsAsync — a company that is not this installation's asks
        // a question whose answer is empty, without touching the database.
        if (!MatchesConfiguredCompany(query.Company))
        {
            return new ProcessJobSummary(0, new Dictionary<string, int>(), 0, null, null, 0, 0, 0, 0);
        }

        return await _tracking.GetSummaryAsync(query, cancellationToken);
    }

    /// <summary>
    /// True when no company was asked for, or the one asked for is this installation's.
    /// </summary>
    private bool MatchesConfiguredCompany(string? company)
    {
        if (string.IsNullOrWhiteSpace(company))
        {
            return true;
        }

        var configured = _options.Value.ErpApi.CompanyId
            .ToString(CultureInfo.InvariantCulture);

        return company.Trim().Equals(configured, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProcessExecutionDetail?> GetExecutionAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken);
        if (job?.ConversionId is null)
        {
            return null;
        }

        var conversion = await _tracking.GetConversionAsync(job.ConversionId.Value, cancellationToken);
        if (conversion is null)
        {
            return null;
        }

        var outcomes = await _tracking.GetOutcomesAsync(jobId, cancellationToken);
        var errors = await _jobs.GetErrorsAsync(jobId, cancellationToken);
        var attemptNo = await AttemptNumberAsync(conversion.Id, job, cancellationToken);

        return new ProcessExecutionDetail(
            new ProcessExecution(job, conversion, outcomes, attemptNo, ComputeFailureReason(errors, outcomes)),
            errors);
    }

    /// <summary>
    /// Why no purchase order came of this execution: the most recent error's layman message first —
    /// this is what carries a whole-indent guard refusal (not authorised, an LBT item), which is
    /// thrown before any vendor group is reached and so has no outcome row of its own — falling back
    /// to the business reason recorded against a non-Created outcome when there is no error.
    /// </summary>
    private static string? ComputeFailureReason(
        IReadOnlyList<AutomationError> errors,
        IReadOnlyList<IndentPoOutcome> outcomes)
    {
        if (errors.Count > 0)
        {
            return errors[0].LaymanMessage;
        }

        if (outcomes.Any(outcome => outcome.Outcome == PoOutcomeKind.Created))
        {
            return null;
        }

        return outcomes.OrderBy(outcome => outcome.Sequence).Select(outcome => outcome.Reason).FirstOrDefault();
    }

    public async Task<IndentExecutionHistory> GetExecutionsByIndentAsync(
        long indentId,
        IndentKind? kind,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IndentPoConversion> conversions;

        if (kind is null)
        {
            conversions = await _tracking.FindConversionsAsync(indentId, cancellationToken);
        }
        else
        {
            var single = await _tracking.FindConversionAsync(indentId, kind.Value, cancellationToken);
            conversions = single is null ? [] : [single];
        }

        if (conversions.Count == 0)
        {
            return IndentExecutionHistory.None;
        }

        if (conversions.Count > 1)
        {
            // The same number is a plausible XINDID and XINDAUTOID. Reported, not guessed at.
            return new IndentExecutionHistory([], conversions, Ambiguous: true);
        }

        var conversion = conversions[0];
        var jobs = await _tracking.GetExecutionsAsync(conversion.Id, cancellationToken);
        var jobIds = jobs.Select(job => job.Id).ToList();
        var outcomes = await _tracking.GetOutcomesAsync(jobIds, cancellationToken);
        var errors = await _jobs.GetErrorsAsync(jobIds, cancellationToken);

        var executions = jobs
            .Select((job, index) =>
            {
                var forJob = outcomes.TryGetValue(job.Id, out var jobOutcomes) ? jobOutcomes : [];
                var jobErrors = errors.TryGetValue(job.Id, out var forJobErrors) ? forJobErrors : [];

                return new ProcessExecution(job, conversion, forJob, index + 1, ComputeFailureReason(jobErrors, forJob));
            })
            .ToList();

        return new IndentExecutionHistory(executions, conversions, Ambiguous: false);
    }

    public async Task<ProcessRunDetail?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await _tracking.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var jobs = await _tracking.GetExecutionsByRunAsync(runId, cancellationToken);
        if (jobs.Count == 0)
        {
            // A run that converted nothing still has everything worth reading on the run row.
            return new ProcessRunDetail(run, []);
        }

        var jobIds = jobs.Select(job => job.Id).ToList();
        var outcomes = await _tracking.GetOutcomesAsync(jobIds, cancellationToken);
        var errors = await _jobs.GetErrorsAsync(jobIds, cancellationToken);

        var executions = new List<ProcessExecution>(jobs.Count);
        foreach (var job in jobs)
        {
            var conversion = job.ConversionId is null
                ? null
                : await _tracking.GetConversionAsync(job.ConversionId.Value, cancellationToken);

            if (conversion is null)
            {
                continue;
            }

            var forJob = outcomes.TryGetValue(job.Id, out var jobOutcomes) ? jobOutcomes : [];
            var jobErrors = errors.TryGetValue(job.Id, out var forJobErrors) ? forJobErrors : [];

            executions.Add(new ProcessExecution(
                job,
                conversion,
                forJob,
                await AttemptNumberAsync(conversion.Id, job, cancellationToken),
                ComputeFailureReason(jobErrors, forJob)));
        }

        return new ProcessRunDetail(run, executions);
    }

    public async Task<PagedResult<AutomationRun>> ListRunsAsync(
        ProcessRunQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Capped here rather than at the endpoint, so every caller gets the same ceiling.
        var bounded = query with
        {
            Page = Math.Max(query.Page, 1),
            PageSize = Math.Clamp(query.PageSize, 1, MaxPageSize),
        };

        var items = await _tracking.ListRunsAsync(bounded, cancellationToken);
        var total = await _tracking.CountRunsAsync(bounded, cancellationToken);

        return new PagedResult<AutomationRun>(items, total, bounded.Page, bounded.PageSize);
    }

    public async Task<IReadOnlyList<DailyConversionStat>> GetDailyStatsAsync(
        int days,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(days, 1, MaxDailyStatsDays);

        // Anchored on today's UTC date rather than the exact instant, so the window is always whole
        // calendar days — "the trailing N days" is a day count, not a duration.
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var from = today.AddDays(-(bounded - 1));

        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(today.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        var raw = await _tracking.GetDailyStatsAsync(fromUtc, toUtc, cancellationToken);
        var byDate = raw.ToDictionary(stat => stat.Date);

        // Filled day by day rather than returning `raw` as-is: a day nothing finished on has no row
        // from the repository at all, and a chart's x-axis needs every day present, at zero, so a
        // quiet day reads as "zero" rather than silently disappearing from the series.
        var filled = new List<DailyConversionStat>(bounded);
        for (var date = from; date <= today; date = date.AddDays(1))
        {
            filled.Add(byDate.TryGetValue(date, out var stat) ? stat : new DailyConversionStat(date, 0, 0, 0));
        }

        return filled;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The case for this indent, created on first sight and refreshed after that. Refreshed rather
    /// than duplicated: an indent's number and site can be re-read, but its identity does not move.
    /// </summary>
    private async Task<IndentPoConversion> OpenConversionAsync(
        TrackedIndent indent,
        CancellationToken cancellationToken)
    {
        var existing = await _tracking.FindConversionAsync(
            indent.IndentId,
            indent.Kind,
            cancellationToken);

        if (existing is not null)
        {
            existing.Refresh(indent.IndentNumber, indent.SiteId, indent.IndentDate);
            await _tracking.UpdateConversionAsync(existing, cancellationToken);

            return existing;
        }

        var conversion = IndentPoConversion.Open(
            indent.IndentId,
            indent.Kind,
            indent.IndentNumber,
            indent.SiteId,
            _clock.UtcNow,
            indent.IndentDate);

        // The stored row, which is not necessarily the one just built: another trigger can have
        // opened the same case a moment earlier, and the natural key decides which one wins.
        return await _tracking.AddConversionAsync(conversion, cancellationToken);
    }

    private async Task RecordOutcomesAsync(
        Guid jobId,
        IReadOnlyList<TrackedOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (outcomes.Count == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        var createStep = _execution?.Steps
            .FirstOrDefault(step => step.Stage == IndentPoStages.CreatePurchaseOrder);

        var rows = new List<IndentPoOutcome>(outcomes.Count);
        var sequence = 1;

        foreach (var outcome in outcomes)
        {
            rows.Add(outcome.Kind switch
            {
                PoOutcomeKind.Created => IndentPoOutcome.Created(
                    jobId,
                    sequence,
                    outcome.VendorCode ?? string.Empty,
                    outcome.CurrencyCode,
                    outcome.RateStructureCode,
                    outcome.PoId ?? 0,
                    outcome.PoNumber ?? string.Empty,
                    outcome.LineCount,
                    now,
                    createStep?.Id),

                PoOutcomeKind.Failed => IndentPoOutcome.Failed(
                    jobId,
                    sequence,
                    outcome.Reason ?? "The ERP refused the order and gave no reason.",
                    now,
                    outcome.VendorCode),

                _ => IndentPoOutcome.Skipped(
                    jobId,
                    sequence,
                    outcome.Reason ?? "Nothing was ordered and no reason was recorded.",
                    now,
                    outcome.VendorCode),
            });

            sequence++;
        }

        await _tracking.AddOutcomesAsync(rows, cancellationToken);
    }

    private void CompleteRunningStep(DateTimeOffset nowUtc, string? erpDocumentRef)
    {
        var running = _execution?.Steps.FirstOrDefault(step => step.Status is StepStatus.Running);

        running?.Complete(nowUtc, erpDocumentRef, requestPayload: null, responsePayload: null);
    }

    /// <summary>
    /// Which attempt this job is, counting from one. Derived from the ordering rather than stored,
    /// so it cannot drift from the rows it counts.
    /// </summary>
    private async Task<int> AttemptNumberAsync(
        Guid conversionId,
        Job job,
        CancellationToken cancellationToken)
    {
        var executions = await _tracking.GetExecutionsAsync(conversionId, cancellationToken);

        for (var index = 0; index < executions.Count; index++)
        {
            if (executions[index].Id == job.Id)
            {
                return index + 1;
            }
        }

        return 1;
    }

    /// <summary>
    /// The ERP references produced, trimmed to the column. One order is the usual case; several
    /// vendors on one indent is the reason this is a list at all.
    /// </summary>
    private static string? DocumentRef(IReadOnlyList<TrackedOutcome> orders)
    {
        if (orders.Count == 0)
        {
            return null;
        }

        var joined = string.Join(", ", orders.Select(order => order.PoNumber));

        return joined.Length <= 100 ? joined : joined[..100];
    }
}
