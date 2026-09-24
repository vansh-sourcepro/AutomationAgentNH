using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Notifications;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// Walks a claimed job through its stages and operations. The loop is deliberately small: it
/// persists after every operation, and every exit path leaves the job in a state the next claim
/// can pick up from. Nothing here decides what an operation does — that lives in the definitions.
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly IWorkflowCatalog _catalog;
    private readonly IJobRepository _jobRepository;
    private readonly IAutomationConfigRepository _configRepository;
    private readonly IErpClient _erpClient;
    private readonly INotificationService _notifications;
    private readonly IClock _clock;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IWorkflowCatalog catalog,
        IJobRepository jobRepository,
        IAutomationConfigRepository configRepository,
        IErpClient erpClient,
        INotificationService notifications,
        IClock clock,
        ILogger<WorkflowEngine> logger)
    {
        _catalog = catalog;
        _jobRepository = jobRepository;
        _configRepository = configRepository;
        _erpClient = erpClient;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var definition = _catalog.Get(job.WorkflowType);

        // Read fresh, once per run: a configuration change applies from the next job, and the
        // retry limit cannot shift underneath a job that is midway through its attempts.
        var config = await _configRepository.GetOrDefaultAsync(job.WorkflowType, cancellationToken);

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = job.CorrelationId,
            ["JobId"] = job.Id,
            ["DocumentId"] = job.DocumentId,
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            var step = job.NextStep();

            if (step is null)
            {
                job.Complete(_clock.UtcNow);
                await _jobRepository.SaveAsync(job, cancellationToken);

                _logger.LogInformation("Job completed: all {StepCount} operations finished", job.Steps.Count);
                return;
            }

            var operation = definition.Find(step.Stage, step.OperationName);

            if (operation is null)
            {
                // The workflow was redefined while this job was in flight. Failing loudly is the
                // only safe answer: guessing which operation was meant could create the wrong
                // ERP document.
                await FailAsync(
                    job,
                    step,
                    ErrorType.Technical,
                    laymanMessage: "This automation step no longer exists. Please contact support.",
                    technicalMessage:
                        $"Operation '{step.Stage}/{step.OperationName}' is not present in the current "
                        + $"definition of workflow '{job.WorkflowType}'.",
                    cancellationToken);
                return;
            }

            job.EnterStage(step.Stage);

            if (ShouldPauseForApproval(job, operation, step))
            {
                job.PauseForApproval(step.Stage, step.OperationName);
                await _jobRepository.SaveAsync(job, cancellationToken);
                await NotifyApprovalAsync(job, step, cancellationToken);

                _logger.LogInformation(
                    "Job paused at approval gate {Stage}/{Operation}",
                    step.Stage,
                    step.OperationName);
                return;
            }

            if (!await PreconditionHoldsAsync(job, step, operation, config.RetryCount, cancellationToken))
            {
                continue;
            }

            step.Start(_clock.UtcNow);

            // Checkpoint before the ERP call, not after: if the process dies mid-call, the step is
            // visibly Running and recovery knows to re-enter it — where query-before-create
            // decides whether the ERP already did the work.
            await _jobRepository.SaveAsync(job, cancellationToken);

            var result = await ExecuteAsync(job, step, operation, cancellationToken);

            if (!await ApplyResultAsync(job, step, operation, result, config.RetryCount, cancellationToken))
            {
                return;
            }
        }

        // Graceful shutdown: the job stays Running and is recovered by the orphan sweep, so no
        // work is lost and nothing is duplicated.
        _logger.LogInformation("Engine stopping; job left resumable at {Stage}", job.CurrentStage);
    }

    private static bool ShouldPauseForApproval(Job job, OperationDefinition operation, JobStep step) =>
        job.Mode is AutomationMode.Partial
        && operation.RequiresApprovalInPartial
        // An approval already recorded on this step is what releases the gate; without this check
        // the job would pause again on the very next claim and never progress.
        && step.ApprovedBy is null;

    private async Task<bool> PreconditionHoldsAsync(
        Job job,
        JobStep step,
        OperationDefinition operation,
        int maxRetry,
        CancellationToken cancellationToken)
    {
        if (operation.Precondition is null)
        {
            return true;
        }

        try
        {
            var context = OperationContext.ForStep(job, step);

            if (await operation.Precondition(context, _erpClient, cancellationToken))
            {
                return true;
            }

            // A precondition that does not hold is a legitimate outcome, not an error: there is
            // simply nothing for this operation to do.
            step.Skip(_clock.UtcNow, $"Precondition for '{step.OperationName}' not met.");
            await _jobRepository.SaveAsync(job, cancellationToken);

            _logger.LogInformation(
                "Operation {Stage}/{Operation} skipped: precondition not met",
                step.Stage,
                step.OperationName);

            return false;
        }
        catch (ErpException ex)
        {
            await HandleFailureAsync(job, step, ex, maxRetry, cancellationToken);
            return false;
        }
    }

    private async Task<OperationResult> ExecuteAsync(
        Job job,
        JobStep step,
        OperationDefinition operation,
        CancellationToken cancellationToken)
    {
        var context = OperationContext.ForStep(job, step);

        try
        {
            return operation.VerifiesFirst
                ? await VerifyAsync(context, step, operation, cancellationToken)
                : await operation.Execute!(context, _erpClient, cancellationToken);
        }
        catch (ErpException ex)
        {
            return ex.IsTransient
                ? OperationResult.TransientFailure(ex.LaymanMessage, ex.TechnicalMessage)
                : OperationResult.BusinessFailure(ex.LaymanMessage, ex.TechnicalMessage);
        }
    }

    /// <summary>
    /// Handles the two kinds whose work the ERP performs itself. The agent asks what happened
    /// rather than acting, so it can never duplicate a document the ERP's own flag-driven
    /// automation already created.
    /// </summary>
    private async Task<OperationResult> VerifyAsync(
        OperationContext context,
        JobStep step,
        OperationDefinition operation,
        CancellationToken cancellationToken)
    {
        var outcome = await _erpClient.VerifyErpAutomationAsync(
            context.ToErpRequest(),
            operation.ErpTransitionKind!,
            cancellationToken);

        if (outcome.Completed)
        {
            return OperationResult.Success(
                outcome.ErpDocumentRef,
                responsePayload: $$"""{"performedBy":"ERP","transition":"{{operation.ErpTransitionKind}}"}""");
        }

        // The ERP is still working, or is about to. Wait rather than take over: acting now would
        // race the ERP and produce two documents.
        if (outcome.InProgress || outcome.ErpAutomationEnabled)
        {
            var waitedFor = _clock.UtcNow - (step.StartedAtUtc ?? _clock.UtcNow);

            if (waitedFor < operation.WaitBudget)
            {
                return OperationResult.TransientFailure(
                    $"Waiting for the ERP to complete {operation.ErpTransitionKind}.",
                    $"ERP automation for '{operation.ErpTransitionKind}' still in progress after {waitedFor:g}; "
                    + $"budget {operation.WaitBudget:g}.");
            }

            // Waited long enough. A human must look, because continuing would either stall
            // forever or risk duplicating whatever the ERP is stuck on.
            return OperationResult.BusinessFailure(
                $"The ERP has not completed {operation.ErpTransitionKind} for this document. Please check it.",
                $"ERP automation for '{operation.ErpTransitionKind}' exceeded its {operation.WaitBudget:g} wait budget.");
        }

        // The ERP flag is off and nothing is coming.
        if (operation.CanExecute)
        {
            _logger.LogInformation(
                "ERP automation for {Transition} is disabled; the agent will perform {Operation} itself",
                operation.ErpTransitionKind,
                step.OperationName);

            return await operation.Execute!(context, _erpClient, cancellationToken);
        }

        // VerifyOnly with the ERP flag off: by definition the agent must not do this work, so the
        // only honest outcome is to tell a human the ERP setting is wrong.
        return OperationResult.BusinessFailure(
            $"Automatic {operation.ErpTransitionKind} is switched off in the ERP, so this step cannot complete.",
            $"Operation '{step.OperationName}' is VerifyOnly but the ERP reports automation disabled "
            + $"for '{operation.ErpTransitionKind}'. Enable it in the ERP or change the workflow definition.");
    }

    /// <summary>Persists the outcome. Returns false when the job should stop for now.</summary>
    private async Task<bool> ApplyResultAsync(
        Job job,
        JobStep step,
        OperationDefinition operation,
        OperationResult result,
        int maxRetry,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case OperationOutcome.Succeeded:
                step.Complete(_clock.UtcNow, result.ErpDocumentRef, result.RequestPayload, result.ResponsePayload);

                if (result.DiscoveredSteps.Count > 0)
                {
                    // Appended in the same save as the discovering step's completion, so a crash
                    // leaves either both or neither — never a half-expanded plan.
                    job.ExpandPlan(result.DiscoveredSteps);

                    _logger.LogInformation(
                        "Operation {Stage}/{Operation} discovered {Count} further steps",
                        step.Stage,
                        step.DisplayName,
                        result.DiscoveredSteps.Count);
                }

                await _jobRepository.SaveAsync(job, cancellationToken);

                _logger.LogInformation(
                    "Operation {Stage}/{Operation} completed, ERP document {ErpDocumentRef}",
                    step.Stage,
                    step.DisplayName,
                    result.ErpDocumentRef ?? "(none)");
                return true;

            case OperationOutcome.Skipped:
                step.Skip(_clock.UtcNow, result.Reason ?? "Nothing to do.");
                await _jobRepository.SaveAsync(job, cancellationToken);

                _logger.LogInformation(
                    "Operation {Stage}/{Operation} skipped: {Reason}",
                    step.Stage,
                    step.DisplayName,
                    result.Reason);
                return true;

            case OperationOutcome.TransientFailure:
                await RequeueAsync(job, step, operation, result, maxRetry, cancellationToken);
                return false;

            case OperationOutcome.BusinessFailure:
            default:
                await FailAsync(
                    job,
                    step,
                    ErrorType.Business,
                    result.Reason ?? "The ERP rejected this step.",
                    result.TechnicalDetail ?? result.Reason ?? "No technical detail supplied.",
                    cancellationToken);
                return false;
        }
    }

    private async Task RequeueAsync(
        Job job,
        JobStep step,
        OperationDefinition operation,
        OperationResult result,
        int maxRetry,
        CancellationToken cancellationToken)
    {
        // A step waiting on the ERP's own automation is not failing, so it is not charged against
        // the retry budget — its bound is the wait budget instead.
        var isWaitingOnErp = operation.VerifiesFirst;

        if (!isWaitingOnErp && step.RetryCount >= maxRetry)
        {
            await FailAsync(
                job,
                step,
                ErrorType.Technical,
                result.Reason ?? "The ERP could not be reached.",
                $"Exhausted {maxRetry} retries. {result.TechnicalDetail}".TrimEnd(),
                cancellationToken);
            return;
        }

        var delay = isWaitingOnErp
            ? RetryPolicy.ErpAutomationPollInterval
            : RetryPolicy.BackoffFor(step.RetryCount);

        job.RequeueAfterTransientFailure(_clock.UtcNow, delay);
        await _jobRepository.SaveAsync(job, cancellationToken);

        _logger.LogWarning(
            "Operation {Stage}/{Operation} will be retried in {Delay} (attempt {Attempt}): {Reason}",
            step.Stage,
            step.DisplayName,
            delay,
            step.RetryCount,
            result.Reason);
    }

    private async Task FailAsync(
        Job job,
        JobStep step,
        ErrorType errorType,
        string laymanMessage,
        string technicalMessage,
        CancellationToken cancellationToken)
    {
        if (step.Status is StepStatus.Running)
        {
            step.Fail(_clock.UtcNow, requestPayload: null, responsePayload: null);
        }

        job.Fail(_clock.UtcNow);

        var error = AutomationError.Create(
            job.Id,
            step.Id,
            errorType,
            technicalMessage,
            laymanMessage,
            _clock.UtcNow);

        await _jobRepository.SaveAsync(job, cancellationToken);
        await _jobRepository.AddErrorAsync(error, cancellationToken);

        _logger.LogError(
            "Job failed at {Stage}/{Operation}: {LaymanMessage} ({TechnicalMessage})",
            step.Stage,
            step.DisplayName,
            laymanMessage,
            technicalMessage);

        await NotifyFailureAsync(job, error, cancellationToken);
    }

    private async Task NotifyFailureAsync(Job job, AutomationError error, CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.NotifyJobFailedAsync(job, error, cancellationToken);
        }
        catch (Exception ex)
        {
            // A notification outage must never change the recorded outcome of a job.
            _logger.LogWarning(ex, "Failure notification could not be sent for job {JobId}", job.Id);
        }
    }

    private async Task NotifyApprovalAsync(Job job, JobStep step, CancellationToken cancellationToken)
    {
        try
        {
            await _notifications.NotifyApprovalRequiredAsync(job, step.Stage, step.OperationName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Approval notification could not be sent for job {JobId}", job.Id);
        }
    }

    private async Task HandleFailureAsync(
        Job job,
        JobStep step,
        ErpException exception,
        int maxRetry,
        CancellationToken cancellationToken)
    {
        var result = exception.IsTransient
            ? OperationResult.TransientFailure(exception.LaymanMessage, exception.TechnicalMessage)
            : OperationResult.BusinessFailure(exception.LaymanMessage, exception.TechnicalMessage);

        if (result.Outcome is OperationOutcome.TransientFailure)
        {
            if (step.RetryCount >= maxRetry)
            {
                await FailAsync(job, step, ErrorType.Technical, exception.LaymanMessage, exception.TechnicalMessage, cancellationToken);
                return;
            }

            job.RequeueAfterTransientFailure(_clock.UtcNow, RetryPolicy.BackoffFor(step.RetryCount));
            await _jobRepository.SaveAsync(job, cancellationToken);
            return;
        }

        await FailAsync(job, step, ErrorType.Business, exception.LaymanMessage, exception.TechnicalMessage, cancellationToken);
    }
}
