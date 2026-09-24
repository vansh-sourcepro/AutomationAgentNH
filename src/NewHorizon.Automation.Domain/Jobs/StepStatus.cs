namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// Lifecycle of one operation — the checkpoint unit. Resume looks for the first step that is
/// not <see cref="Completed"/> or <see cref="Skipped"/>.
/// </summary>
public enum StepStatus
{
    Pending = 0,
    Running = 1,

    /// <summary>Finished successfully. A completed step is never re-run.</summary>
    Completed = 2,

    Failed = 3,

    /// <summary>Precondition not met (e.g. no net shortage, children not allocated). Not an error.</summary>
    Skipped = 4,
}
