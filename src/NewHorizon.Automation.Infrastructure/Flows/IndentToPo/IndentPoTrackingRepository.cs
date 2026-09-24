using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo;

/// <summary>
/// EF Core implementation for the three process-tracking tables.
/// </summary>
/// <remarks>
/// Data access only. It never decides whether an indent may be converted, which attempt a job is,
/// or what a run's totals mean — those are the service's, and keeping them out of here is what
/// lets the same queries serve a future UI that asks different questions.
/// </remarks>
public sealed class IndentPoTrackingRepository : IIndentPoTrackingRepository
{
    /// <summary>SQL Server error numbers for a unique-index violation, as in JobRepository.</summary>
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private readonly AutomationDbContext _dbContext;

    public IndentPoTrackingRepository(AutomationDbContext dbContext) => _dbContext = dbContext;

    public async Task AddRunAsync(AutomationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        _dbContext.Runs.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
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

    public Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken) =>
        _dbContext.Runs
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == runId, cancellationToken);

    public async Task<IReadOnlyList<AutomationRun>> ListRunsAsync(
        ProcessRunQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await Filter(query)
            .OrderByDescending(run => run.StartedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountRunsAsync(ProcessRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Filter(query).CountAsync(cancellationToken);
    }

    public Task<IndentPoConversion?> FindConversionAsync(
        long indentId,
        IndentKind kind,
        CancellationToken cancellationToken) =>
        _dbContext.IndentPoConversions
            .FirstOrDefaultAsync(
                conversion => conversion.IndentId == indentId && conversion.IndentKind == kind,
                cancellationToken);

    public async Task<IReadOnlyList<IndentPoConversion>> FindConversionsAsync(
        long indentId,
        CancellationToken cancellationToken) =>
        await _dbContext.IndentPoConversions
            .AsNoTracking()
            .Where(conversion => conversion.IndentId == indentId)
            .OrderBy(conversion => conversion.IndentKind)
            .ToListAsync(cancellationToken);

    public Task<IndentPoConversion?> GetConversionAsync(
        Guid conversionId,
        CancellationToken cancellationToken) =>
        _dbContext.IndentPoConversions
            .AsNoTracking()
            .FirstOrDefaultAsync(conversion => conversion.Id == conversionId, cancellationToken);

    public async Task<IndentPoConversion> AddConversionAsync(
        IndentPoConversion conversion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversion);

        _dbContext.IndentPoConversions.Add(conversion);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return conversion;
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            // Two triggers found the same indent at the same moment and both tried to open its
            // case. UX_IndentPoConversion_Indent is the arbiter; the loser adopts the winner,
            // exactly as JobRepository.EnqueueAsync does for a job.
            _dbContext.Entry(conversion).State = EntityState.Detached;

            var winner = await FindConversionAsync(
                conversion.IndentId,
                conversion.IndentKind,
                cancellationToken);

            if (winner is null)
            {
                // A unique violation with nothing to adopt means the constraint that fired was not
                // the one assumed. Do not swallow it.
                throw;
            }

            return winner;
        }
    }

    public async Task UpdateConversionAsync(
        IndentPoConversion conversion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversion);

        if (_dbContext.Entry(conversion).State is EntityState.Detached)
        {
            _dbContext.IndentPoConversions.Attach(conversion);
            _dbContext.Entry(conversion).State = EntityState.Modified;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddOutcomesAsync(
        IReadOnlyCollection<IndentPoOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (outcomes.Count == 0)
        {
            return;
        }

        _dbContext.IndentPoOutcomes.AddRange(outcomes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IndentPoOutcome>> GetOutcomesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        await _dbContext.IndentPoOutcomes
            .AsNoTracking()
            .Where(outcome => outcome.JobId == jobId)
            .OrderBy(outcome => outcome.Sequence)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IndentPoOutcome>>> GetOutcomesAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        if (jobIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<IndentPoOutcome>>();
        }

        // One round trip for the whole page: a history view asks for every attempt at once, and a
        // query per attempt is how a five-row page becomes fifteen queries.
        var rows = await _dbContext.IndentPoOutcomes
            .AsNoTracking()
            .Where(outcome => jobIds.Contains(outcome.JobId))
            .OrderBy(outcome => outcome.Sequence)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(outcome => outcome.JobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IndentPoOutcome>)group.ToList());
    }

    public async Task<IReadOnlyList<ProcessExecutionRow>> ListExecutionRowsAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await FilteredRows(query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountExecutionRowsAsync(ProcessJobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return FilteredRows(query).CountAsync(cancellationToken);
    }

    /// <summary>
    /// Everything <see cref="ProcessJobSummary"/> needs, computed against the same population
    /// <see cref="FilteredRows"/> would page through — three aggregate queries, each translated to
    /// its own SQL rather than pulling rows into memory to sum them here.
    /// </summary>
    /// <remarks>
    /// The filter conditions are repeated from <see cref="FilteredRows"/> rather than shared,
    /// because the join they both start from has to stay an anonymous type for EF Core to compose
    /// further clauses over it (see that method's remarks) — an anonymous type cannot be the return
    /// type of a second method, so there is no boundary to share it across. Keep the two condition
    /// lists in step by hand if either changes.
    /// </remarks>
    public async Task<ProcessJobSummary> GetSummaryAsync(
        ProcessJobQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var jobs = FilteredJobs(query);

        var statusCounts = await jobs
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var totalJobs = statusCounts.Sum(entry => entry.Count);

        var successCount = await jobs
            .Where(job => _dbContext.IndentPoOutcomes.Any(outcome =>
                outcome.JobId == job.Id && outcome.Outcome == PoOutcomeKind.Created))
            .CountAsync(cancellationToken);

        // AVG over a nullable column: SQL Server ignores the NULLs (a job still Running has no
        // duration yet) and hands back NULL rather than 0 when nothing has finished — exactly the
        // "unknown" a manual `?? 0` average would otherwise have hidden. Queried fresh rather than
        // reusing `jobs` a third time: each use above already re-runs the query from scratch (an
        // IQueryable is a description, not a cached result), so there is nothing extra spent here.
        var averageDurationMs = await jobs
            .Select(job => (double?)job.DurationMs)
            .AverageAsync(cancellationToken);

        // The trigger side of the same picture: every AutomationRun this filter's From/To/Trigger
        // would also admit, whether or not it ever started a conversion. Reusing Filter(ProcessRunQuery)
        // rather than a second hand-written predicate keeps this in step with ListRunsAsync's own
        // rule for what a date range or a trigger source means.
        var runs = Filter(new ProcessRunQuery
        {
            FromUtc = query.FromUtc,
            ToUtc = query.ToUtc,
            TriggerSource = query.TriggerSource,
        });

        var totalTriggerAttempts = await runs.CountAsync(cancellationToken);

        // JobsCreated is the run's own tally of how many conversions it started (see
        // AutomationRun.RecordProgress) — zero means the trigger opened and closed without ever
        // finding an eligible, authorised indent, so nothing here should count toward TotalJobs.
        var triggerAttemptsWithoutEligibleIndent = await runs
            .CountAsync(run => run.JobsCreated == 0, cancellationToken);

        // A business "no" comes in two shapes: a job that Completed having recorded only Skipped
        // outcomes (nothing left to order, no vendor record — decided per item/vendor group, never
        // thrown), or a job that stopped on a whole-indent guard refusal (not authorised, contains an
        // LBT item — thrown before any outcome is recorded, so it has none at all), which now lands
        // on JobStatus.Skipped rather than Failed. A job that also failed on a genuinely unexpected
        // exception is recorded the same way (ErrorType.Business has no separate "unknown bug" bucket
        // today, and it now lands on Skipped too), so this can very slightly overstate true business
        // refusals — accepted as rare rather than adding a third error classification for it.
        var businessRefusalCount = await jobs
            .CountAsync(job =>
                (job.Status == JobStatus.Completed
                    && _dbContext.IndentPoOutcomes.Any(outcome => outcome.JobId == job.Id)
                    && !_dbContext.IndentPoOutcomes.Any(outcome =>
                        outcome.JobId == job.Id && outcome.Outcome == PoOutcomeKind.Created))
                || job.Status == JobStatus.Skipped,
                cancellationToken);

        // Every remaining Failed job is technical by construction now: a business-rule refusal moves
        // the job to Skipped instead, so Failed no longer needs the ErrorType join to tell them apart.
        // Kept as its own count, rather than reading CountsByStatus["Failed"] on the dashboard,
        // because that dictionary counts every Failed job regardless of reason and would double-count
        // against businessRefusalCount.
        var technicalFailureCount = await jobs
            .CountAsync(job => job.Status == JobStatus.Failed, cancellationToken);

        return new ProcessJobSummary(
            totalJobs,
            statusCounts.ToDictionary(entry => entry.Status.ToString(), entry => entry.Count),
            successCount,
            totalJobs == 0 ? null : (double)successCount / totalJobs,
            averageDurationMs,
            totalTriggerAttempts,
            triggerAttemptsWithoutEligibleIndent,
            businessRefusalCount,
            technicalFailureCount);
    }

    /// <summary>
    /// <see cref="FilteredRows"/>'s filters, applied straight to <c>Jobs</c> instead of the
    /// job×conversion×run join — everything <see cref="GetSummaryAsync"/> aggregates lives on the
    /// job row itself, so there is nothing here that needs the join, only the same "which jobs are
    /// in this grid" rule <see cref="FilteredRows"/> applies (a cycle job — null <c>ConversionId</c>
    /// — is excluded the same way, by requiring one, since this grid is conversions only).
    /// </summary>
    private IQueryable<Job> FilteredJobs(ProcessJobQuery query)
    {
        var jobs = _dbContext.Jobs.AsNoTracking().Where(job => job.ConversionId != null);

        if (query.FromUtc is { } from)
        {
            jobs = jobs.Where(job => job.CreatedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            jobs = jobs.Where(job => job.CreatedAtUtc <= to);
        }

        if (query.Status is { } status)
        {
            jobs = jobs.Where(job => job.Status == status);
        }

        if (query.TriggerSource is { } trigger)
        {
            jobs = jobs.Where(job => job.RunId != null
                && _dbContext.Runs.Any(run => run.Id == job.RunId && run.TriggerSource == trigger));
        }

        if (!string.IsNullOrWhiteSpace(query.Stage))
        {
            jobs = jobs.Where(job => job.CurrentStage == query.Stage);
        }

        if (query.IndentKind is { } kind)
        {
            jobs = jobs.Where(job => job.ConversionId != null
                && _dbContext.IndentPoConversions.Any(
                    conversion => conversion.Id == job.ConversionId && conversion.IndentKind == kind));
        }

        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
        {
            var workflow = query.WorkflowType.Trim();

            jobs = jobs.Where(job => job.WorkflowType == workflow);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            jobs = jobs.Where(job =>
                _dbContext.IndentPoConversions.Any(
                    conversion => conversion.Id == job.ConversionId && conversion.IndentNumber.Contains(term))
                || job.CorrelationId.Contains(term)
                || _dbContext.Runs.Any(
                    run => run.Id == job.RunId && run.TriggeredBy != null && run.TriggeredBy.Contains(term))
                || _dbContext.IndentPoOutcomes.Any(outcome =>
                    outcome.JobId == job.Id && outcome.PoNumber != null && outcome.PoNumber.Contains(term)));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<Job>> GetExecutionsAsync(
        Guid conversionId,
        CancellationToken cancellationToken) =>
        await _dbContext.Jobs
            .AsNoTracking()
            .Include(job => job.Steps.OrderBy(step => step.Sequence))

            // Oldest first, which is also attempt order: the service numbers them from this.
            .Where(job => job.ConversionId == conversionId)
            .OrderBy(job => job.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Job>> GetExecutionsByRunAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        await _dbContext.Jobs
            .AsNoTracking()
            .Include(job => job.Steps.OrderBy(step => step.Sequence))
            .Where(job => job.RunId == runId)
            .OrderBy(job => job.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DailyConversionStat>> GetDailyStatsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        // Only finished jobs have a day to be counted against — a job still Running has not
        // resolved to either outcome yet, and belongs on neither series until it does.
        var finished = _dbContext.Jobs.AsNoTracking().Where(job =>
            job.ConversionId != null
            && job.CompletedAtUtc != null
            && job.CompletedAtUtc >= fromUtc
            && job.CompletedAtUtc <= toUtc);

        var convertedByDay = await finished
            .Where(job => _dbContext.IndentPoOutcomes.Any(outcome =>
                outcome.JobId == job.Id && outcome.Outcome == PoOutcomeKind.Created))
            .GroupBy(job => job.CompletedAtUtc!.Value.Date)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var failedByDay = await finished
            .Where(job => job.Status == JobStatus.Failed)
            .GroupBy(job => job.CompletedAtUtc!.Value.Date)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Purchase order documents, not jobs: one job can produce several (one per vendor group),
        // and "Daily PO Generated" counts the documents, not the processes that produced them.
        var poByDay = await (
            from outcome in _dbContext.IndentPoOutcomes.AsNoTracking()
            join job in _dbContext.Jobs.AsNoTracking() on outcome.JobId equals job.Id
            where outcome.Outcome == PoOutcomeKind.Created
                && job.ConversionId != null
                && job.CompletedAtUtc != null
                && job.CompletedAtUtc >= fromUtc
                && job.CompletedAtUtc <= toUtc
            group outcome by job.CompletedAtUtc!.Value.Date into g
            select new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var dates = convertedByDay.Select(entry => entry.Date)
            .Concat(failedByDay.Select(entry => entry.Date))
            .Concat(poByDay.Select(entry => entry.Date))
            .Distinct()
            .OrderBy(date => date);

        return dates
            .Select(date => new DailyConversionStat(
                DateOnly.FromDateTime(date),
                poByDay.Find(entry => entry.Date == date)?.Count ?? 0,
                convertedByDay.Find(entry => entry.Date == date)?.Count ?? 0,
                failedByDay.Find(entry => entry.Date == date)?.Count ?? 0))
            .ToList();
    }

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException
        && sqlException.Number is UniqueIndexViolation or UniqueConstraintViolation;

    /// <summary>
    /// The conversion grid, filtered and ordered but not yet paged: job × its indent × the run
    /// that asked for it, projected to one flat row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inner join to <c>IndentPoConversion</c> is what makes this a conversion grid rather than
    /// a job list — a cycle job has a null <c>ConversionId</c> and drops out here. The join to
    /// <c>AutomationRun</c> is left, because a job enqueued outside a run has no trigger and must
    /// still be listed rather than vanishing.
    /// </para>
    /// <para>
    /// The intermediate join stays an <b>anonymous</b> type and never leaves this method. EF Core
    /// special-cases anonymous types and can follow their members back to the table aliases; a
    /// named holder type cannot be composed over, because the provider has no way to map
    /// <c>holder.Job.CreatedAtUtc</c> onto a column, and the whole query then fails to translate.
    /// Everything the caller sees is <see cref="ProcessExecutionRow"/>, produced by the final
    /// <c>Select</c> — after the filters and the ordering, which is why both reach SQL.
    /// </para>
    /// </remarks>
    private IQueryable<ProcessExecutionRow> FilteredRows(ProcessJobQuery query)
    {
        var joined =
            from job in _dbContext.Jobs.AsNoTracking()
            join conversion in _dbContext.IndentPoConversions.AsNoTracking()
                on job.ConversionId equals conversion.Id
            join run in _dbContext.Runs.AsNoTracking()
                on job.RunId equals run.Id into runs
            from run in runs.DefaultIfEmpty()
            select new { job, conversion, run };

        if (query.FromUtc is { } from)
        {
            joined = joined.Where(row => row.job.CreatedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            joined = joined.Where(row => row.job.CreatedAtUtc <= to);
        }

        if (query.Status is { } status)
        {
            joined = joined.Where(row => row.job.Status == status);
        }

        if (query.TriggerSource is { } trigger)
        {
            joined = joined.Where(row => row.run != null && row.run.TriggerSource == trigger);
        }

        if (!string.IsNullOrWhiteSpace(query.Stage))
        {
            joined = joined.Where(row => row.job.CurrentStage == query.Stage);
        }

        if (query.IndentKind is { } kind)
        {
            joined = joined.Where(row => row.conversion.IndentKind == kind);
        }

        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
        {
            var workflow = query.WorkflowType.Trim();

            joined = joined.Where(row => row.job.WorkflowType == workflow);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            // Contains rather than a hand-built LIKE: EF parameterises it and escapes % and _, so
            // a search for "50%" looks for those characters instead of matching everything.
            //
            // The purchase order is reached through its outcomes, which becomes an EXISTS — worth
            // the subquery, because "which conversion produced PO …" is the question support asks
            // most and the number is nowhere else on the row.
            joined = joined.Where(row =>
                row.conversion.IndentNumber.Contains(term)
                || row.job.CorrelationId.Contains(term)
                || (row.run != null && row.run.TriggeredBy != null && row.run.TriggeredBy.Contains(term))
                || _dbContext.IndentPoOutcomes.Any(outcome =>
                    outcome.JobId == row.job.Id
                    && outcome.PoNumber != null
                    && outcome.PoNumber.Contains(term)));
        }

        // Ordered on the column itself, before the projection, so this lands as
        // ORDER BY j.CreatedAtUtc DESC rather than anything the provider has to reason about.
        return joined
            .OrderByDescending(row => row.job.CreatedAtUtc)

            // Projected, not materialised: a grid line needs seventeen scalars, and loading the
            // job aggregate would bring its five steps with it for nothing.
            .Select(row => new ProcessExecutionRow(
                row.job.Id,
                row.job.RunId,
                row.job.CorrelationId,
                row.conversion.IndentId,
                row.conversion.IndentKind,
                row.conversion.IndentNumber,
                row.conversion.SiteId,
                row.job.WorkflowType,
                row.run == null ? null : row.run.TriggerSource,
                row.run == null ? null : row.run.TriggeredBy,
                row.job.Mode,
                row.job.CurrentStage,
                row.job.Status,
                row.job.DurationMs,
                row.job.CreatedAtUtc,
                row.job.StartedAtUtc,
                row.job.CompletedAtUtc,

                // The order(s) this execution actually produced — several vendor groups on one
                // indent are the reason there can be more than one, so every Created outcome is
                // joined rather than just the first. Guarded by Any() rather than trusting
                // string.Join's own empty-sequence answer: an execution that created nothing must
                // report null here, not "" — the grid tells the two apart.
                _dbContext.IndentPoOutcomes.Any(outcome =>
                    outcome.JobId == row.job.Id && outcome.Outcome == PoOutcomeKind.Created)
                    ? string.Join(
                        ", ",
                        _dbContext.IndentPoOutcomes
                            .Where(outcome => outcome.JobId == row.job.Id && outcome.Outcome == PoOutcomeKind.Created)
                            .OrderBy(outcome => outcome.Sequence)
                            .Select(outcome => outcome.PoNumber!))
                    : null,

                // A technical failure (AutomationError) if the job actually failed. Otherwise, only
                // when nothing was created, the business reason an outcome recorded for ordering
                // nothing — an execution that DID produce a purchase order can still carry a Skipped
                // note about a second vendor group, and that note is not why this row has no PO, so
                // it must not be read as a failure reason here.
                _dbContext.Errors
                    .Where(error => error.JobId == row.job.Id)
                    .OrderByDescending(error => error.CreatedAtUtc)
                    .Select(error => error.LaymanMessage)
                    .FirstOrDefault()
                ?? (_dbContext.IndentPoOutcomes.Any(outcome =>
                        outcome.JobId == row.job.Id && outcome.Outcome == PoOutcomeKind.Created)
                    ? null
                    : _dbContext.IndentPoOutcomes
                        .Where(outcome => outcome.JobId == row.job.Id && outcome.Outcome != PoOutcomeKind.Created)
                        .OrderBy(outcome => outcome.Sequence)
                        .Select(outcome => outcome.Reason)
                        .FirstOrDefault())));
    }

    private IQueryable<AutomationRun> Filter(ProcessRunQuery query)
    {
        var runs = _dbContext.Runs.AsNoTracking();

        if (query.FromUtc is { } from)
        {
            runs = runs.Where(run => run.StartedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            runs = runs.Where(run => run.StartedAtUtc <= to);
        }

        if (query.TriggerSource is { } trigger)
        {
            runs = runs.Where(run => run.TriggerSource == trigger);
        }

        if (query.Status is { } status)
        {
            runs = runs.Where(run => run.Status == status);
        }

        return runs;
    }
}
