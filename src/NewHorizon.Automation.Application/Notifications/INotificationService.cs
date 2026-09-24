using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Notifications;

/// <summary>
/// Tells humans that something needs them. Never throws into the execution path: a notification
/// outage must not fail a job that otherwise succeeded.
/// </summary>
public interface INotificationService
{
    Task NotifyJobFailedAsync(Job job, AutomationError error, CancellationToken cancellationToken);

    Task NotifyApprovalRequiredAsync(Job job, string stage, string operationName, CancellationToken cancellationToken);
}
