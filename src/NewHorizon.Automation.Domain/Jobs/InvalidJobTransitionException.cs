namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// A state transition the job state machine does not allow.
/// </summary>
public sealed class InvalidJobTransitionException : DomainException
{
    public InvalidJobTransitionException(Guid jobId, JobStatus from, JobStatus to, string? because = null)
        : base($"Job {jobId} cannot move from {from} to {to}." + (because is null ? string.Empty : $" {because}"))
    {
        JobId = jobId;
        From = from;
        To = to;
    }

    public Guid JobId { get; }

    public JobStatus From { get; }

    public JobStatus To { get; }
}
