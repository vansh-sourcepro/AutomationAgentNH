namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// The write side of process tracking: what the conversion pipeline tells the database as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and deliberately stateful within that scope. The run and the execution in progress are
/// held by the implementation rather than passed back and forth, so the conversion code calls
/// <see cref="EnterStageAsync"/> without having to thread a job id through every method it already
/// has. One HTTP request or one sweep is one scope, and a sweep converts its indents one after the
/// other, so there is never more than one execution in flight per scope.
/// </para>
/// <para>
/// Every method is safe to call when tracking is off — see <see cref="IsEnabled"/>. That is what
/// keeps indent → PO working on an installation with no automation database, which it has always
/// done and must keep doing.
/// </para>
/// </remarks>
public interface IIndentPoTracker
{
    /// <summary>
    /// False when there is no automation database. Calls still succeed; they simply record nothing.
    /// Worth checking before an endpoint promises the caller a job id it will never get.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>The run in progress, or null when tracking is off or no run was opened.</summary>
    Guid? RunId { get; }

    /// <summary>The execution in progress, or null when none is.</summary>
    Guid? ExecutionId { get; }

    /// <summary>Opens the run. Called once, by whatever received the trigger.</summary>
    Task StartRunAsync(StartRunRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens an execution for one indent, reusing the conversion case if this indent has been
    /// attempted before. Refused — and recorded as not started — while another attempt at the same
    /// indent is still live, because two would race for the same indent lines.
    /// </summary>
    Task StartExecutionAsync(TrackedIndent indent, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the execution on: completes the stage that was running and starts this one. The
    /// stage is what the ERP UI shows as "where is it now".
    /// </summary>
    Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the execution as Completed and records what each vendor group produced — an order,
    /// or the reason there is none. An execution that ordered nothing is still Completed: it did
    /// its work and the answer was "nothing to order". Failure is <see cref="FailExecutionAsync"/>.
    /// </summary>
    Task CompleteExecutionAsync(
        IReadOnlyList<TrackedOutcome> outcomes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the execution as Failed and records the error in both registers. The layman message
    /// is what the ERP UI shows; the technical one is for whoever has to fix it.
    /// </summary>
    Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken);

    /// <summary>Closes the run, recording how far it got.</summary>
    Task CompleteRunAsync(int indentsExamined, CancellationToken cancellationToken);

    /// <summary>The run itself broke, as opposed to one indent inside it producing nothing.</summary>
    Task FailRunAsync(string reason, int indentsExamined, CancellationToken cancellationToken);
}
