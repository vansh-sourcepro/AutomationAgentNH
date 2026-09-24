namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// How a run ended. Deliberately shorter than <see cref="JobStatus"/>: a run is never claimed,
/// never retried and never waits at an approval gate — only the jobs inside it do.
/// </summary>
public enum RunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
}
