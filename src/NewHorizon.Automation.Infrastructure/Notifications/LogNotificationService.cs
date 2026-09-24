using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Notifications;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Notifications;

/// <summary>
/// Writes the notification to the structured log, where Serilog's file sink picks it up and the
/// ERP's monitoring can watch for it.
/// </summary>
/// <remarks>
/// The delivery channel for human notifications (ERP in-app alert, email, or both) is not settled
/// yet — design doc §18. This adapter exists so the port is always satisfied and nothing that needs
/// a human is ever silently dropped; swap it for the real channel once the client confirms one,
/// or register the real one alongside and fan out.
/// <para>
/// Per the port's contract this never throws: a notification failure must not fail a job that
/// otherwise succeeded.
/// </para>
/// </remarks>
public sealed class LogNotificationService : INotificationService
{
    private readonly ILogger<LogNotificationService> _logger;

    public LogNotificationService(ILogger<LogNotificationService> logger)
    {
        _logger = logger;
    }

    public Task NotifyJobFailedAsync(Job job, AutomationError error, CancellationToken cancellationToken)
    {
        if (job is null || error is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _logger.LogError(
                "NOTIFY job-failed: job {JobId} ({WorkflowType} on {DocumentType} {DocumentId}, "
                + "correlation {CorrelationId}) failed at stage {Stage} with {ErrorType}. "
                + "Layman: {LaymanMessage} | Technical: {TechnicalMessage} | Endpoint: {ApiEndpoint}",
                job.Id,
                job.WorkflowType,
                job.DocumentType,
                job.DocumentId,
                job.CorrelationId,
                job.CurrentStage ?? "(none)",
                error.ErrorType,
                error.LaymanMessage,
                error.TechnicalMessage,
                error.ApiEndpoint ?? "(none)");
        }
        catch (Exception ex)
        {
            SwallowAsync(ex);
        }

        return Task.CompletedTask;
    }

    public Task NotifyApprovalRequiredAsync(
        Job job,
        string stage,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _logger.LogWarning(
                "NOTIFY approval-required: job {JobId} ({WorkflowType} on {DocumentType} {DocumentId}, "
                + "correlation {CorrelationId}) is waiting for a decision on {Stage}/{OperationName}.",
                job.Id,
                job.WorkflowType,
                job.DocumentType,
                job.DocumentId,
                job.CorrelationId,
                stage,
                operationName);
        }
        catch (Exception ex)
        {
            SwallowAsync(ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Last-resort handling: if even logging the notification fails there is nowhere left to
    /// report it, and throwing would break the port's no-throw contract.
    /// </summary>
    private void SwallowAsync(Exception ex)
    {
        try
        {
            _logger.LogDebug(ex, "Notification could not be written.");
        }
        catch
        {
            // Nothing further can be done.
        }
    }
}
