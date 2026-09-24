using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// Runs a claimed job through its stages and operations until it completes, pauses at an
/// approval gate, or fails. Implemented in Phase 4.
/// </summary>
public interface IWorkflowEngine
{
    Task RunAsync(Job job, CancellationToken cancellationToken);
}
