using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Logging;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// Records what the engine persisted. <see cref="SaveCount"/> matters: the checkpoint guarantee is
/// that a save happens after every operation, before the next one begins.
/// </summary>
public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly Dictionary<Guid, Job> _jobs = [];

    public List<AutomationError> Errors { get; } = [];

    public int SaveCount { get; private set; }

    public Task<JobEnqueueResult> EnqueueAsync(Job job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        return Task.FromResult(new JobEnqueueResult(job, true));
    }

    public Task<IReadOnlyList<Guid>> ClaimPendingJobsAsync(int batchSize, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            _jobs.Values.Where(job => job.Status is JobStatus.Pending).Take(batchSize).Select(job => job.Id).ToList());

    public Task<Job?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.GetValueOrDefault(jobId));

    public Task<Job?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.Values.FirstOrDefault(job =>
            job.IdempotencyKey == idempotencyKey && job.Status is not JobStatus.Cancelled));

    public Task SaveAsync(Job job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<PagedResult<Job>> ListAsync(JobQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<Job>(_jobs.Values.ToList(), _jobs.Count, 1, _jobs.Count));

    public Task AddErrorAsync(AutomationError error, CancellationToken cancellationToken)
    {
        Errors.Add(error);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutomationError>> GetErrorsAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AutomationError>>(Errors.Where(error => error.JobId == jobId).ToList());

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<AutomationError>>> GetErrorsAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<AutomationError>>>(
            Errors
                .Where(error => jobIds.Contains(error.JobId))
                .GroupBy(error => error.JobId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<AutomationError>)group.ToList()));

    public Task AddLogAsync(AutomationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> GetOrphanedRunningJobsAsync(
        DateTimeOffset olderThanUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyDictionary<JobStatus, int>> GetStatusCountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<JobStatus, int>>(
            _jobs.Values.GroupBy(job => job.Status).ToDictionary(group => group.Key, group => group.Count()));

    public Task<IReadOnlySet<string>> GetExistingIdempotencyKeysAsync(
        IReadOnlyCollection<string> idempotencyKeys,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(
            _jobs.Values.Select(job => job.IdempotencyKey).Where(idempotencyKeys.Contains).ToHashSet());
}
