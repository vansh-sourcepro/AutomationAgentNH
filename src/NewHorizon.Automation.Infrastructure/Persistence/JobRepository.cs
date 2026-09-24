using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Logging;

namespace NewHorizon.Automation.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation against the automation database.
/// </summary>
public sealed class JobRepository : IJobRepository
{
    /// <summary>SQL Server error numbers for a unique-index violation.</summary>
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<JobRepository> _logger;

    public JobRepository(AutomationDbContext dbContext, IClock clock, ILogger<JobRepository> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<JobEnqueueResult> EnqueueAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Cheap path: a live job for this document usually already exists when the reconciliation
        // poll re-reports something the push already handled. A cycle is matched on being live at
        // all rather than on its key, because every cycle's key is deliberately distinct.
        var existing = await FindLiveEquivalentAsync(job, cancellationToken);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Enqueue for {DocumentType} {DocumentId} matched live job {JobId}; not creating a duplicate",
                job.DocumentType,
                job.DocumentId,
                existing.Id);

            return new JobEnqueueResult(existing, WasCreated: false);
        }

        _dbContext.Jobs.Add(job);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new JobEnqueueResult(job, WasCreated: true);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Two triggers raced past the check above. The database is the real arbiter, so the
            // loser simply adopts the winner's job rather than failing the caller.
            //
            // The steps are detached with it, not just the job. They are tracked as Added, and
            // detaching only the root leaves them behind pointing at a job row that was never
            // inserted — so the next SaveChanges on this scope, which may belong to something else
            // entirely, fails on a foreign key nobody was looking at.
            Detach(job);

            var winner = await FindLiveEquivalentAsync(job, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            _logger.LogInformation(
                "Concurrent enqueue for {DocumentType} {DocumentId} resolved to job {JobId}",
                job.DocumentType,
                job.DocumentId,
                winner.Id);

            return new JobEnqueueResult(winner, WasCreated: false);
        }
    }

    /// <summary>
    /// The job an enqueue should adopt instead of creating a new one, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Three rules, because the three kinds of job mean different things by "duplicate".
    /// A document job is duplicated when another job exists for the same document. A cycle is
    /// duplicated when another cycle is simply still live — its identity is its start time, so no
    /// two cycles ever share a key. A conversion is duplicated only while an attempt is still
    /// running, because a finished attempt — completed, cancelled or failed — is over. Each rule
    /// mirrors the index that enforces it, so the racing
    /// path resolves to the same winner the database picked.
    /// </remarks>
    private Task<Job?> FindLiveEquivalentAsync(Job job, CancellationToken cancellationToken) =>
        // Mirrors UX_AutomationJob_LiveIndentConversion: a Failed or Skipped attempt is finished,
        // so it must not hold the indent’s key — that is what would make retry silently do nothing.
        job.ConversionId is not null
            ? _dbContext.Jobs
                .Include(candidate => candidate.Steps.OrderBy(step => step.Sequence))
                .FirstOrDefaultAsync(
                    candidate =>
                        candidate.IdempotencyKey == job.IdempotencyKey
                        && candidate.ConversionId != null
                        && candidate.Status != JobStatus.Completed
                        && candidate.Status != JobStatus.Cancelled
                        && candidate.Status != JobStatus.Failed
                        && candidate.Status != JobStatus.Skipped,
                    cancellationToken)
            : job.DocumentType == DocumentTypes.Cycle
            ? _dbContext.Jobs
                .AsNoTracking()
                .Where(candidate =>
                    candidate.DocumentType == DocumentTypes.Cycle
                    && candidate.WorkflowType == job.WorkflowType
                    && candidate.Status != JobStatus.Completed
                    && candidate.Status != JobStatus.Cancelled)
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : GetByIdempotencyKeyAsync(job.IdempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ClaimPendingJobsAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Claim and flip to Running in one statement so two workers can never take the same job.
        // READPAST makes a worker skip rows another worker holds instead of blocking behind them;
        // UPDLOCK takes the update lock at read time rather than upgrading later and deadlocking.
        //
        // The selection is a CTE rather than a bare UPDATE TOP because UPDATE TOP admits no
        // ORDER BY and would claim rows in arbitrary order — which would silently defeat the
        // priority bump a manual retry relies on to jump ahead of newly enqueued work.
        const string sql =
            """
            WITH claimable AS (
                SELECT TOP (@batchSize) j.Id, j.Status, j.StartedAtUtc, j.NotBeforeUtc
                FROM AutomationJob AS j WITH (UPDLOCK, READPAST)
                WHERE j.Status = 'Pending'
                  AND j.ConversionId IS NULL
                  AND (j.NotBeforeUtc IS NULL OR j.NotBeforeUtc <= @nowUtc)
                ORDER BY j.Priority DESC, j.CreatedAtUtc ASC
            )
            UPDATE claimable
            SET Status = 'Running',
                StartedAtUtc = COALESCE(StartedAtUtc, @nowUtc),
                NotBeforeUtc = NULL
            OUTPUT inserted.Id;
            """;

        var claimed = new List<Guid>(batchSize);

        var connection = _dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@batchSize", batchSize));
        command.Parameters.Add(new SqlParameter("@nowUtc", _clock.UtcNow));

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (_dbContext.Database.CurrentTransaction is { } transaction)
            {
                command.Transaction = transaction.GetDbTransaction();
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(reader.GetGuid(0));
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return claimed;
    }

    public Task<Job?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        _dbContext.Jobs
            // Ordered explicitly: without it SQL Server returns the steps in whatever order the
            // index yields, which would render the ERP's Stage â Operation timeline shuffled and
            // make any positional access to Steps meaningless.
            .Include(job => job.Steps.OrderBy(step => step.Sequence))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<Job?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Mirrors the filtered unique index: a cancelled job does not block a fresh run.
        return _dbContext.Jobs
            .Include(job => job.Steps.OrderBy(step => step.Sequence))
            .FirstOrDefaultAsync(
                job => job.IdempotencyKey == idempotencyKey && job.Status != JobStatus.Cancelled,
                cancellationToken);
    }

    public async Task SaveAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_dbContext.Entry(job).State is EntityState.Detached)
        {
            _dbContext.Jobs.Attach(job);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<Job>> ListAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var jobs = _dbContext.Jobs.AsNoTracking().AsQueryable();

        if (query.Status is { } status)
        {
            jobs = jobs.Where(job => job.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
        {
            jobs = jobs.Where(job => job.WorkflowType == query.WorkflowType);
        }

        if (!string.IsNullOrWhiteSpace(query.DocumentId))
        {
            jobs = jobs.Where(job => job.DocumentId == query.DocumentId);
        }

        if (query.CreatedFromUtc is { } from)
        {
            jobs = jobs.Where(job => job.CreatedAtUtc >= from);
        }

        if (query.CreatedToUtc is { } to)
        {
            jobs = jobs.Where(job => job.CreatedAtUtc <= to);
        }

        var totalCount = await jobs.CountAsync(cancellationToken);

        var items = await jobs
            .OrderByDescending(job => job.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Job>(items, totalCount, page, pageSize);
    }

    public async Task AddErrorAsync(AutomationError error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        _dbContext.Errors.Add(error);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationError>> GetErrorsAsync(Guid jobId, CancellationToken cancellationToken) =>
        await _dbContext.Errors
            .AsNoTracking()
            .Where(error => error.JobId == jobId)
            .OrderByDescending(error => error.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AutomationError>>> GetErrorsAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        if (jobIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AutomationError>>();
        }

        var rows = await _dbContext.Errors
            .AsNoTracking()
            .Where(error => jobIds.Contains(error.JobId))
            .OrderByDescending(error => error.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(error => error.JobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AutomationError>)group.ToList());
    }

    public async Task AddLogAsync(AutomationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);

        _dbContext.Logs.Add(log);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetOrphanedRunningJobsAsync(
        DateTimeOffset olderThanUtc,
        CancellationToken cancellationToken) =>
        await _dbContext.Jobs
            .AsNoTracking()
            .Where(job =>
                job.Status == JobStatus.Running
                && job.StartedAtUtc < olderThanUtc

                // Conversion jobs are run inline by whoever triggered them, not by the dispatcher,
                // so they are inserted Running and are never claimable. Requeueing one would hand
                // it to an engine that has no definition for its workflow.
                && job.ConversionId == null)
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<JobStatus, int>> GetStatusCountsAsync(
        CancellationToken cancellationToken)
    {
        var counts = await _dbContext.Jobs
            .AsNoTracking()
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(entry => entry.Status, entry => entry.Count);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdempotencyKeysAsync(
        IReadOnlyCollection<string> idempotencyKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKeys);

        if (idempotencyKeys.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var found = await _dbContext.Jobs
            .AsNoTracking()
            .Where(job => idempotencyKeys.Contains(job.IdempotencyKey) && job.Status != JobStatus.Cancelled)
            .Select(job => job.IdempotencyKey)
            .ToListAsync(cancellationToken);

        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Takes the job and its operations out of the change tracker, so nothing of a rejected insert
    /// is left to be replayed by the next save on this scope. Children first: detaching the root
    /// does not detach what it owns.
    /// </summary>
    private void Detach(Job job)
    {
        // Snapshot first: detaching a step makes EF's fixup remove it from the job's own
        // collection, and that is the collection being walked.
        foreach (var step in job.Steps.ToList())
        {
            _dbContext.Entry(step).State = EntityState.Detached;
        }

        _dbContext.Entry(job).State = EntityState.Detached;
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException
        && sqlException.Number is UniqueIndexViolation or UniqueConstraintViolation;
}
