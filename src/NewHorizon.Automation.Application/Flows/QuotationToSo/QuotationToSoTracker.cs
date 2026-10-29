using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>
/// Process tracking implementation for Quotation → Sales Order:
/// records runs, jobs, steps and outcomes against the database.
/// </summary>
public sealed class QuotationToSoTracker : IQuotationToSoTracker
{
    private readonly IIndentPoTrackingRepository _tracking;
    private readonly IJobRepository _jobs;
    private readonly IClock _clock;
    private readonly ILogger<QuotationToSoTracker> _logger;

    private AutomationRun? _run;
    private Job? _execution;
    private TrackedQuotation? _currentQuotation;
    private int _jobsCreated;
    private int _ordersCreated;

    public QuotationToSoTracker(
        IIndentPoTrackingRepository tracking,
        IJobRepository jobs,
        IClock clock,
        ILogger<QuotationToSoTracker> logger)
    {
        _tracking = tracking;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Guid? RunId => _run?.Id;

    public Guid? ExecutionId => _execution?.Id;

    public async Task StartRunAsync(
        TriggerSource triggerSource,
        string? triggeredBy,
        string? triggerReference,
        string? quotationNumbers,
        CancellationToken cancellationToken)
    {
        if (_run is not null)
        {
            _logger.LogWarning("Run {RunId} is already open on this scope; reusing open run.", _run.Id);
            return;
        }

        _run = AutomationRun.Start(
            WorkflowNames.QuotationToSalesOrder,
            triggerSource,
            AutomationMode.Full,
            string.IsNullOrWhiteSpace(quotationNumbers) ? "All" : quotationNumbers,
            _clock.UtcNow,
            triggeredBy,
            triggerReference);

        await _tracking.AddRunAsync(_run, cancellationToken);

        _logger.LogInformation(
            "QuotationToSalesOrder Run {RunId} opened by {TriggerSource} for {Target}",
            _run.Id,
            triggerSource,
            _run.RequestedIndentTypes);
    }

    public async Task StartExecutionAsync(TrackedQuotation quotation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotation);

        if (_execution is not null)
        {
            await FailExecutionAsync(
                "The conversion moved on to another quotation without finishing this one.",
                $"Execution {_execution.Id} was still open when quotation {quotation.QuotationNumber} started.",
                transient: false,
                cancellationToken);
        }

        _currentQuotation = quotation;

        // Reuse or open conversion case in IndentPoConversions
        var conversion = await _tracking.FindConversionAsync(quotation.QuotationId, IndentKind.Regular, cancellationToken);
        if (conversion is not null)
        {
            conversion.Refresh(quotation.QuotationNumber, quotation.SiteId, quotation.QuotationDate);
            await _tracking.UpdateConversionAsync(conversion, cancellationToken);
        }
        else
        {
            conversion = IndentPoConversion.Open(
                quotation.QuotationId,
                IndentKind.Regular,
                quotation.QuotationNumber,
                quotation.SiteId,
                _clock.UtcNow,
                quotation.QuotationDate);

            conversion = await _tracking.AddConversionAsync(conversion, cancellationToken);
        }

        var job = Job.Create(
            WorkflowNames.QuotationToSalesOrder,
            "Quotation",
            quotation.QuotationNumber,
            AutomationMode.Full,
            _clock.UtcNow,
            priority: 0,
            correlationId: _run?.CorrelationId);

        job.LinkToConversion(conversion.Id, _run?.Id);
        job.PlanSteps(QuotationToSoStages.InOrder.Select(stage =>
            new PlannedOperation(stage, QuotationToSoTasks.For(stage))));

        job.Claim(_clock.UtcNow);

        var result = await _jobs.EnqueueAsync(job, cancellationToken);
        if (!result.WasCreated)
        {
            _logger.LogWarning(
                "Quotation {QuotationNumber} already has live execution {JobId}; this attempt is untracked",
                quotation.QuotationNumber,
                result.Job.Id);

            _execution = null;
            return;
        }

        _execution = result.Job;
        _jobsCreated++;

        await EnterStageAsync(
            QuotationToSoStages.Discovery,
            QuotationToSoTasks.ListOpenQuotations,
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
        string salesOrderNumber,
        long? salesOrderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salesOrderNumber);

        if (_execution is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        CompleteRunningStep(now, salesOrderNumber);

        foreach (var pending in _execution.Steps.Where(s => !s.IsTerminal))
        {
            pending.Skip(now, "The conversion completed before this stage was needed.");
        }

        _execution.Complete(now);
        await _jobs.SaveAsync(_execution, cancellationToken);

        var outcome = IndentPoOutcome.Created(
            _execution.Id,
            sequence: 1,
            vendorCode: _currentQuotation?.CustomerCode ?? "CUSTOMER",
            currencyCode: "INR",
            rateStructureCode: null,
            poId: salesOrderId ?? 0,
            poNumber: salesOrderNumber,
            lineCount: 1,
            now);

        await _tracking.AddOutcomesAsync([outcome], cancellationToken);

        _ordersCreated++;
        _execution = null;
        _currentQuotation = null;
    }

    public async Task SkipExecutionAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_execution is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        var running = _execution.Steps.FirstOrDefault(s => s.Status is StepStatus.Running);
        running?.Skip(now, reason);

        foreach (var pending in _execution.Steps.Where(s => !s.IsTerminal))
        {
            pending.Skip(now, reason);
        }

        _execution.Skip(now);
        await _jobs.SaveAsync(_execution, cancellationToken);

        var outcome = IndentPoOutcome.Skipped(
            _execution.Id,
            sequence: 1,
            reason,
            now,
            vendorCode: _currentQuotation?.CustomerCode);

        await _tracking.AddOutcomesAsync([outcome], cancellationToken);

        _execution = null;
        _currentQuotation = null;
    }

    public async Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(laymanMessage);

        if (_execution is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        var running = _execution.Steps.FirstOrDefault(s => s.Status is StepStatus.Running);
        running?.Fail(now, null, null);

        if (transient)
        {
            _execution.Fail(now);
        }
        else
        {
            _execution.Skip(now);
        }

        await _jobs.SaveAsync(_execution, cancellationToken);

        await _jobs.AddErrorAsync(
            AutomationError.Create(
                _execution.Id,
                running?.Id,
                transient ? ErrorType.Technical : ErrorType.Business,
                technicalMessage ?? laymanMessage,
                laymanMessage,
                now),
            cancellationToken);

        var outcome = IndentPoOutcome.Failed(
            _execution.Id,
            sequence: 1,
            laymanMessage,
            now,
            vendorCode: _currentQuotation?.CustomerCode);

        await _tracking.AddOutcomesAsync([outcome], cancellationToken);

        _execution = null;
        _currentQuotation = null;
    }

    public async Task CompleteRunAsync(int quotationsExamined, CancellationToken cancellationToken)
    {
        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(quotationsExamined, _jobsCreated, _ordersCreated);
        _run.Complete(_clock.UtcNow);

        await _tracking.UpdateRunAsync(_run, cancellationToken);

        _logger.LogInformation(
            "QuotationToSalesOrder Run {RunId} finished: {Examined} examined, {Jobs} tracked, {Orders} sales order(s) created",
            _run.Id,
            quotationsExamined,
            _jobsCreated,
            _ordersCreated);

        _run = null;
    }

    public async Task FailRunAsync(string reason, int quotationsExamined, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_run is null || _run.IsTerminal)
        {
            return;
        }

        _run.RecordProgress(quotationsExamined, _jobsCreated, _ordersCreated);
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

        var running = _execution.Steps.FirstOrDefault(step => step.Status is StepStatus.Running);
        if (running is null)
        {
            return;
        }

        running.Complete(now, erpDocumentRef, requestPayload: null, responsePayload: null);
    }
}
