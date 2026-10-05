using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor;

/// <summary>
/// EF Core persistence for Issue to Shop Floor conversions, outcomes, and history queries.
/// </summary>
public sealed class IssueToShopFloorTrackingRepository : IIssueToShopFloorTrackingRepository
{
    private readonly AutomationDbContext _dbContext;

    public IssueToShopFloorTrackingRepository(AutomationDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddRunAsync(AutomationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        _dbContext.Runs.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        return _dbContext.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
    }

    public async Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_dbContext.Entry(run).State is EntityState.Detached)
        {
            _dbContext.Runs.Attach(run);
            _dbContext.Entry(run).State = EntityState.Modified;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<IssueToShopFloorConversion?> FindConversionAsync(string documentNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);
        var trimmed = documentNumber.Trim();
        return _dbContext.IssueToShopFloorConversions
            .FirstOrDefaultAsync(c => c.DocumentNumber == trimmed, cancellationToken);
    }

    public async Task<IssueToShopFloorConversion> AddConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        _dbContext.IssueToShopFloorConversions.Add(conversion);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return conversion;
    }

    public async Task UpdateConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        if (_dbContext.Entry(conversion).State is EntityState.Detached)
        {
            _dbContext.IssueToShopFloorConversions.Attach(conversion);
            _dbContext.Entry(conversion).State = EntityState.Modified;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddOutcomeAsync(IssueToShopFloorOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _dbContext.IssueToShopFloorOutcomes.Add(outcome);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<IssueToShopFloorOutcome?> GetOutcomeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return _dbContext.IssueToShopFloorOutcomes
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken);
    }

    public Task<Job?> GetJobWithStepsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return _dbContext.Jobs
            .Include(j => j.Steps)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<IssueToShopFloorHistoryRow>> ListHistoryRowsAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pagedJobs = await FilteredJobs(query)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        if (pagedJobs.Count == 0)
        {
            return [];
        }

        var jobIds = pagedJobs.Select(j => j.Id).ToList();
        var runIds = pagedJobs.Where(j => j.RunId.HasValue).Select(j => j.RunId!.Value).Distinct().ToList();

        var outcomes = await _dbContext.IssueToShopFloorOutcomes
            .AsNoTracking()
            .Where(o => jobIds.Contains(o.JobId))
            .ToDictionaryAsync(o => o.JobId, cancellationToken);

        var runs = await _dbContext.Runs
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        return pagedJobs.Select(job =>
        {
            outcomes.TryGetValue(job.Id, out var outcome);
            AutomationRun? run = null;
            if (job.RunId.HasValue)
            {
                runs.TryGetValue(job.RunId.Value, out run);
            }

            return new IssueToShopFloorHistoryRow(
                job.Id,
                job.RunId,
                job.DocumentType,
                job.DocumentId,
                job.Status.ToString(),
                job.CurrentStage,
                outcome?.IssueNumber,
                outcome?.LineCount ?? 0,
                outcome?.TotalQuantity ?? 0m,
                run?.TriggerSource.ToString() ?? "Manual",
                run?.TriggeredBy,
                job.StartedAtUtc ?? job.CreatedAtUtc,
                job.CompletedAtUtc,
                job.DurationMs,
                outcome?.RefusalReason);
        }).ToList();
    }

    public Task<int> CountHistoryRowsAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FilteredJobs(query).CountAsync(cancellationToken);
    }

    public async Task<IssueToShopFloorHistorySummary> GetSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var baseQuery = FilteredJobs(query);

        var total = await baseQuery.CountAsync(cancellationToken);
        var created = await baseQuery.CountAsync(j => j.Status == JobStatus.Completed, cancellationToken);
        var shortages = await baseQuery.CountAsync(j => j.Status == JobStatus.Skipped, cancellationToken);
        var failures = await baseQuery.CountAsync(j => j.Status == JobStatus.Failed, cancellationToken);

        var avgDuration = total > 0
            ? (await baseQuery.Where(j => j.DurationMs.HasValue).AverageAsync(j => (double?)j.DurationMs, cancellationToken) ?? 0.0)
            : 0.0;

        var successRate = total > 0 ? ((double)created / total) * 100.0 : 0.0;

        return new IssueToShopFloorHistorySummary(
            total,
            created,
            shortages,
            failures,
            Math.Round(successRate, 1),
            Math.Round(avgDuration, 1));
    }

    public async Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryRowsAsync(
        string documentNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);

        var doc = documentNumber.Trim();
        var query = new IssueToShopFloorHistoryQuery { Search = doc, Page = 1, PageSize = 100 };
        return await ListHistoryRowsAsync(query, cancellationToken);
    }

    private IQueryable<Job> FilteredJobs(IssueToShopFloorHistoryQuery query)
    {
        var jobs = _dbContext.Jobs.AsNoTracking().Where(j => j.WorkflowType == WorkflowNames.IssueToShopFloor);

        if (query.Status.HasValue)
        {
            jobs = jobs.Where(j => j.Status == query.Status.Value);
        }

        if (query.FromUtc.HasValue)
        {
            jobs = jobs.Where(j => j.CreatedAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            jobs = jobs.Where(j => j.CreatedAtUtc <= query.ToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.IssueSource))
        {
            var source = query.IssueSource.Trim();
            jobs = jobs.Where(j => j.DocumentType == source);
        }

        if (query.TriggerSource.HasValue)
        {
            var trigger = query.TriggerSource.Value;
            jobs = jobs.Where(j => j.RunId != null
                && _dbContext.Runs.Any(r => r.Id == j.RunId && r.TriggerSource == trigger));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            jobs = jobs.Where(j =>
                j.DocumentId.Contains(term)
                || j.CorrelationId.Contains(term)
                || (_dbContext.Runs.Any(r => r.Id == j.RunId && r.TriggeredBy != null && r.TriggeredBy.Contains(term)))
                || (_dbContext.IssueToShopFloorOutcomes.Any(o => o.JobId == j.Id && o.IssueNumber != null && o.IssueNumber.Contains(term))));
        }

        return jobs;
    }
}
