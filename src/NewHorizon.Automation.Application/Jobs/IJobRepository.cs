using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Logging;

namespace NewHorizon.Automation.Application.Jobs;

/// <summary>
/// Persistence for jobs and their operations, against the automation database only.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Inserts the job unless a live one already exists for the same idempotency key, in which
    /// case the existing job is returned untouched. This is the single funnel all three triggers
    /// go through, so the API push and the reconciliation poll can never both create a run.
    /// </summary>
    Task<JobEnqueueResult> EnqueueAsync(Job job, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> pending jobs, highest priority first.
    /// Uses UPDLOCK/READPAST so parallel workers skip locked rows instead of blocking, and no two
    /// workers ever claim the same job.
    /// </summary>
    Task<IReadOnlyList<Guid>> ClaimPendingJobsAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>Loads a job with its full operation list. Returns null when unknown.</summary>
    Task<Job?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<Job?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task SaveAsync(Job job, CancellationToken cancellationToken);

    Task<PagedResult<Job>> ListAsync(JobQuery query, CancellationToken cancellationToken);

    Task AddErrorAsync(AutomationError error, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutomationError>> GetErrorsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// The same errors, for many jobs in one round trip — a history page asks for every attempt at
    /// once, and a query per attempt is how a page of jobs becomes a page of queries.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AutomationError>>> GetErrorsAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken);

    Task AddLogAsync(AutomationLog log, CancellationToken cancellationToken);

    /// <summary>
    /// Returns jobs left Running by a process that died, so startup recovery can requeue them.
    /// Identified by having been running longer than any healthy operation would take.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOrphanedRunningJobsAsync(
        DateTimeOffset olderThanUtc,
        CancellationToken cancellationToken);

    /// <summary>Counts by status for the dashboard, in one round trip.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken);

    /// <summary>Document identities that already have a job, so reconciliation enqueues only gaps.</summary>
    Task<IReadOnlySet<string>> GetExistingIdempotencyKeysAsync(
        IReadOnlyCollection<string> idempotencyKeys,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of an enqueue. <paramref name="WasCreated"/> false means an equivalent live job already
/// existed and was returned instead — the normal, healthy outcome when push and reconciliation
/// both see the same document.
/// </summary>
public sealed record JobEnqueueResult(Job Job, bool WasCreated);
