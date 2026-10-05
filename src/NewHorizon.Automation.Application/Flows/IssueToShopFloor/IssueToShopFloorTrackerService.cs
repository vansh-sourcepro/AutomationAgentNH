using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// Process tracking implementation for Issue to Shop Floor: handles run/execution lifecycle recording and read-side history queries.
/// </summary>
public sealed class IssueToShopFloorTrackerService : IIssueToShopFloorTracker, IIssueToShopFloorHistoryService
{
    private static readonly Dictionary<string, string> PrimaryTasks = new(StringComparer.Ordinal)
    {
        [IssueToShopFloorStages.Discovery] = IssueToShopFloorTasks.ValidateDocument,
        [IssueToShopFloorStages.DocumentControl] = IssueToShopFloorTasks.VerifyIssueNumbering,
        [IssueToShopFloorStages.WorkAllocation] = IssueToShopFloorTasks.CheckWorkAllocation,
        [IssueToShopFloorStages.ErpEligibility] = IssueToShopFloorTasks.ValidateErpEligibility,
        [IssueToShopFloorStages.PendingItems] = IssueToShopFloorTasks.FetchPendingItems,
        [IssueToShopFloorStages.StockAllocation] = IssueToShopFloorTasks.AllocateStock,
        [IssueToShopFloorStages.CreateIssue] = IssueToShopFloorTasks.CreateIssueSlip,
    };

    private readonly IIssueToShopFloorTrackingRepository _tracking;
    private readonly IJobRepository _jobs;
    private readonly IAutomationConfigRepository _configs;
    private readonly IClock _clock;
    private readonly ILogger<IssueToShopFloorTrackerService> _logger;

    private AutomationRun? _run;
    private Job? _execution;
    private IssueToShopFloorConversion? _conversion;

    public IssueToShopFloorTrackerService(
        IIssueToShopFloorTrackingRepository tracking,
        IJobRepository jobs,
        IAutomationConfigRepository configs,
        IClock clock,
        ILogger<IssueToShopFloorTrackerService> logger)
    {
        _tracking = tracking;
        _jobs = jobs;
        _configs = configs;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Guid? RunId => _run?.Id;

    public Guid? ExecutionId => _execution?.Id;

    // ---- Write Side ----

    public async Task StartRunAsync(StartIssueRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_run is not null)
        {
            _logger.LogWarning("Run {RunId} is already open on this scope; reusing it.", _run.Id);
            return;
        }

        var config = await _configs.GetOrDefaultAsync(WorkflowNames.IssueToShopFloor, cancellationToken);

        _run = AutomationRun.Start(
            WorkflowNames.IssueToShopFloor,
            request.TriggerSource,
            config.Mode,
            request.IssueSource,
            _clock.UtcNow,
            request.TriggeredBy,
            request.TriggerReference,
            request.RequestedSites,
            maxIndents: 1);

        await _tracking.AddRunAsync(_run, cancellationToken);

        _logger.LogInformation(
            "Issue run {RunId} opened by {TriggerSource} for {IssueSource}",
            _run.Id,
            request.TriggerSource,
            request.IssueSource);
    }

    public async Task StartExecutionAsync(
        string issueSource,
        string documentNumber,
        long documentId,
        int siteId,
        string? siteCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);

        if (_execution is not null)
        {
            await FailExecutionAsync(
                "The conversion moved on to another document without finishing this one.",
                $"Execution {_execution.Id} was still open when document {documentNumber} started.",
                transient: false,
                cancellationToken);
        }

        var now = _clock.UtcNow;

        _conversion = await _tracking.FindConversionAsync(documentNumber, cancellationToken);
        if (_conversion is null)
        {
            _conversion = IssueToShopFloorConversion.Open(issueSource, documentNumber, documentId, siteId, siteCode, now);
            _conversion = await _tracking.AddConversionAsync(_conversion, cancellationToken);
        }

        var config = await _configs.GetOrDefaultAsync(WorkflowNames.IssueToShopFloor, cancellationToken);
        var mode = _run?.Mode ?? config.Mode;

        var job = Job.Create(
            WorkflowNames.IssueToShopFloor,
            issueSource.Trim(),
            documentNumber.Trim(),
            mode,
            now,
            priority: 0,
            correlationId: _run?.CorrelationId);

        job.LinkToConversion(_conversion.Id, _run?.Id);
        job.PlanSteps(IssueToShopFloorStages.InOrder.Select(stage =>
            new PlannedOperation(stage, PrimaryTasks.GetValueOrDefault(stage, stage))));

        job.Claim(now);

        var result = await _jobs.EnqueueAsync(job, cancellationToken);
        if (!result.WasCreated)
        {
            _logger.LogWarning(
                "Document {DocumentNumber} already has live execution {JobId}; this attempt is untracked",
                documentNumber,
                result.Job.Id);

            _execution = null;
            return;
        }

        _execution = result.Job;

        await EnterStageAsync(IssueToShopFloorStages.Discovery, PrimaryTasks[IssueToShopFloorStages.Discovery], cancellationToken);
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

        var step = _execution.Steps.FirstOrDefault(c =>
            c.Stage == stage && c.Status is StepStatus.Pending);

        if (step is not null)
        {
            step.Start(now);
            if (!string.IsNullOrWhiteSpace(task) && task != step.OperationName)
            {
                step.SetRemarks(task);
            }
        }

        _execution.EnterStage(stage);
        await _jobs.SaveAsync(_execution, cancellationToken);
    }

    public async Task CompleteExecutionAsync(
        string outcome,
        string? issueNumber,
        int lineCount,
        decimal totalQuantity,
        string? refusalReason,
        string? checksJson,
        string? shortagesJson,
        string? linesJson,
        CancellationToken cancellationToken)
    {
        if (_execution is null)
        {
            return;
        }

        var job = _execution;
        var now = _clock.UtcNow;

        CompleteRunningStep(now, issueNumber);

        foreach (var pending in job.Steps.Where(s => !s.IsTerminal))
        {
            pending.Skip(now, "Finished before this stage was needed.");
        }

        bool isCreated = outcome.Equals("Created", StringComparison.OrdinalIgnoreCase);

        if (isCreated)
        {
            job.Complete(now);
        }
        else
        {
            // Business rule refusal / shortage
            job.Skip(now);
        }

        var outcomeEntity = IssueToShopFloorOutcome.Create(
            job.Id,
            job.DocumentType,
            job.DocumentId,
            outcome,
            issueNumber,
            lineCount,
            totalQuantity,
            refusalReason,
            checksJson,
            shortagesJson,
            linesJson,
            now);

        await _tracking.AddOutcomeAsync(outcomeEntity, cancellationToken);

        if (_conversion is not null)
        {
            _conversion.RecordAttempt(now, isCreated, issueNumber);
            await _tracking.UpdateConversionAsync(_conversion, cancellationToken);
        }

        await _jobs.SaveAsync(job, cancellationToken);
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

        var running = job.Steps.FirstOrDefault(s => s.Status is StepStatus.Running);
        running?.Fail(now, requestPayload: null, responsePayload: null);

        foreach (var pending in job.Steps.Where(s => !s.IsTerminal))
        {
            pending.Skip(now, "The execution failed before this stage could run.");
        }

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

        var outcomeEntity = IssueToShopFloorOutcome.Create(
            job.Id,
            job.DocumentType,
            job.DocumentId,
            "Failed",
            issueNumber: null,
            lineCount: 0,
            totalQuantity: 0,
            refusalReason: laymanMessage,
            checksJson: null,
            shortagesJson: null,
            linesJson: null,
            now);

        await _tracking.AddOutcomeAsync(outcomeEntity, cancellationToken);

        if (_conversion is not null)
        {
            _conversion.RecordAttempt(now, succeeded: false, issueNumber: null);
            await _tracking.UpdateConversionAsync(_conversion, cancellationToken);
        }

        _execution = null;
    }

    public async Task CompleteRunAsync(int documentsExamined, int issuesCreated, CancellationToken cancellationToken)
    {
        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(documentsExamined, jobsCreated: documentsExamined, purchaseOrdersCreated: issuesCreated);
        _run.Complete(_clock.UtcNow);

        await _tracking.UpdateRunAsync(_run, cancellationToken);
        _run = null;
    }

    public async Task FailRunAsync(string reason, int documentsExamined, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(documentsExamined, jobsCreated: documentsExamined, purchaseOrdersCreated: 0);
        _run.Fail(reason, _clock.UtcNow);

        await _tracking.UpdateRunAsync(_run, cancellationToken);
        _run = null;
    }

    private void CompleteRunningStep(DateTimeOffset now, string? erpDocumentRef)
    {
        if (_execution is null)
        {
            return;
        }

        var running = _execution.Steps.FirstOrDefault(s => s.Status is StepStatus.Running);
        running?.Complete(now, erpDocumentRef, requestPayload: null, responsePayload: null);
    }

    // ---- Read Side ----

    public async Task<PagedResult<IssueToShopFloorHistoryRow>> ListHistoryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken)
    {
        query ??= new IssueToShopFloorHistoryQuery();

        var rows = await _tracking.ListHistoryRowsAsync(query, cancellationToken);
        var total = await _tracking.CountHistoryRowsAsync(query, cancellationToken);

        return new PagedResult<IssueToShopFloorHistoryRow>(
            rows,
            total,
            query.Page,
            query.PageSize);
    }

    public async Task<IssueToShopFloorHistoryDetail?> GetDetailAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await _tracking.GetJobWithStepsAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var outcome = await _tracking.GetOutcomeAsync(jobId, cancellationToken);

        var steps = job.Steps.OrderBy(s => s.Sequence).Select(s => new IssueToShopFloorStepRow(
            s.Stage,
            s.OperationName,
            s.Status.ToString(),
            s.StartedAtUtc,
            s.CompletedAtUtc,
            s.StartedAtUtc.HasValue && s.CompletedAtUtc.HasValue
                ? (long?)(s.CompletedAtUtc.Value - s.StartedAtUtc.Value).TotalMilliseconds
                : null,
            s.Remarks,
            s.ErpDocumentRef)).ToList();

        return new IssueToShopFloorHistoryDetail(
            job.Id,
            job.RunId,
            job.DocumentType,
            job.DocumentId,
            job.Status.ToString(),
            job.CurrentStage,
            outcome?.IssueNumber,
            job.StartedAtUtc ?? job.CreatedAtUtc,
            job.CompletedAtUtc,
            job.DurationMs,
            outcome?.RefusalReason,
            steps,
            outcome?.ChecksJson,
            outcome?.ShortagesJson,
            outcome?.LinesJson);
    }

    public Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryAsync(
        string documentNumber,
        CancellationToken cancellationToken)
    {
        return _tracking.GetDocumentHistoryRowsAsync(documentNumber, cancellationToken);
    }

    public Task<IssueToShopFloorHistorySummary> GetSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken)
    {
        return _tracking.GetSummaryAsync(query, cancellationToken);
    }
}
