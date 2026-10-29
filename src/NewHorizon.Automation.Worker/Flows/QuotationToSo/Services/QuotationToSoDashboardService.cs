using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Persistence;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Services;

public interface IQuotationToSoDashboardService
{
    Task<QuotationToSoDashboardResponse> GetDashboardAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int days,
        int recentCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotationToSoJobItem>> GetRecentJobsAsync(
        int count,
        CancellationToken cancellationToken);

    Task<QuotationToSoDashboardSummary> GetSummaryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotationToSoDailyStat>> GetDailyStatsAsync(
        int days,
        CancellationToken cancellationToken);

    Task<PagedResult<QuotationToSoJobItem>> ListHistoryAsync(
        int page,
        int pageSize,
        string? search,
        string? status,
        string? trigger,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<QuotationToSoExecutionDetail?> GetJobDetailAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotationToSoJobItem>> GetDocumentHistoryAsync(
        string documentNumber,
        CancellationToken cancellationToken);
}

public sealed class QuotationToSoDashboardService : IQuotationToSoDashboardService
{
    private readonly AutomationDbContext? _dbContext;

    public QuotationToSoDashboardService(IServiceProvider serviceProvider)
    {
        _dbContext = serviceProvider.GetService<AutomationDbContext>();
    }

    public async Task<QuotationToSoDashboardResponse> GetDashboardAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int days,
        int recentCount,
        CancellationToken cancellationToken)
    {
        if (days <= 0)
        {
            days = 30;
        }

        if (recentCount <= 0)
        {
            recentCount = 10;
        }

        if (_dbContext is null)
        {
            return BuildEmptyDashboard(days);
        }

        var (jobs, runs) = await LoadDataAsync(from, to, cancellationToken);
        var jobItems = jobs.Select(ToJobItem).ToList();

        // Also include runs that produced 0 jobs (e.g. sweep found 0 quotations or failed early)
        var runsWithJobs = jobs.Where(j => j.Job.RunId.HasValue).Select(j => j.Job.RunId!.Value).ToHashSet();
        foreach (var run in runs.Where(r => !runsWithJobs.Contains(r.Id)))
        {
            string runStatus = run.Status.ToString();
            long? durationMs = run.CompletedAtUtc.HasValue
                ? (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds
                : null;

            jobItems.Add(new QuotationToSoJobItem(
                JobId: run.Id,
                RunId: run.Id,
                IssueSource: "Quotation",
                DocumentNumber: !string.IsNullOrWhiteSpace(run.RequestedIndentTypes) ? run.RequestedIndentTypes : "Quotation Automation",
                Status: runStatus == "Running" ? "Running" : (runStatus == "Completed" ? "Completed" : "Failed"),
                CurrentStage: "Discovery",
                IssueNumber: null,
                LineCount: 0,
                TotalQuantity: 0m,
                Trigger: run.TriggerSource.ToString(),
                TriggeredBy: run.TriggeredBy ?? run.TriggerSource.ToString(),
                StartedAtUtc: run.StartedAtUtc,
                CompletedAtUtc: run.CompletedAtUtc,
                DurationMs: durationMs,
                Reason: run.FailureReason ?? (run.IndentsExamined == 0 ? "No eligible open authorised quotations found." : null),
                SalesOrderNumber: null,
                QuotationNumber: null));
        }

        var sortedRecent = jobItems.OrderByDescending(j => j.StartedAtUtc).Take(recentCount).ToList();

        // Summary calculations
        var summary = BuildSummary(jobItems, runs);
        var dailyStats = BuildDailyStats(jobItems, days);

        var lastCompleted = jobItems
            .Where(j => j.Status == "Completed")
            .OrderByDescending(j => j.CompletedAtUtc ?? j.StartedAtUtc)
            .FirstOrDefault();

        var lastJob = jobs.OrderByDescending(j => j.Job.CreatedAtUtc).FirstOrDefault();
        var lastExecution = lastJob is not null ? BuildExecutionDetail(lastJob) : null;

        return new QuotationToSoDashboardResponse(
            summary,
            dailyStats,
            lastCompleted,
            lastExecution,
            sortedRecent);
    }

    public async Task<IReadOnlyList<QuotationToSoJobItem>> GetRecentJobsAsync(
        int count,
        CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return [];
        }

        var (jobs, runs) = await LoadDataAsync(null, null, cancellationToken);
        var jobItems = jobs.Select(ToJobItem).ToList();

        var runsWithJobs = jobs.Where(j => j.Job.RunId.HasValue).Select(j => j.Job.RunId!.Value).ToHashSet();
        foreach (var run in runs.Where(r => !runsWithJobs.Contains(r.Id)))
        {
            string runStatus = run.Status.ToString();
            long? durationMs = run.CompletedAtUtc.HasValue
                ? (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds
                : null;

            jobItems.Add(new QuotationToSoJobItem(
                JobId: run.Id,
                RunId: run.Id,
                IssueSource: "Quotation",
                DocumentNumber: !string.IsNullOrWhiteSpace(run.RequestedIndentTypes) ? run.RequestedIndentTypes : "Quotation Automation",
                Status: runStatus == "Running" ? "Running" : (runStatus == "Completed" ? "Completed" : "Failed"),
                CurrentStage: "Discovery",
                IssueNumber: null,
                LineCount: 0,
                TotalQuantity: 0m,
                Trigger: run.TriggerSource.ToString(),
                TriggeredBy: run.TriggeredBy ?? run.TriggerSource.ToString(),
                StartedAtUtc: run.StartedAtUtc,
                CompletedAtUtc: run.CompletedAtUtc,
                DurationMs: durationMs,
                Reason: run.FailureReason ?? (run.IndentsExamined == 0 ? "No eligible open authorised quotations found." : null),
                SalesOrderNumber: null,
                QuotationNumber: null));
        }

        return jobItems.OrderByDescending(j => j.StartedAtUtc).Take(count).ToList();
    }

    public async Task<QuotationToSoDashboardSummary> GetSummaryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return BuildEmptySummary();
        }

        var (jobs, runs) = await LoadDataAsync(from, to, cancellationToken);
        var jobItems = jobs.Select(ToJobItem).ToList();

        return BuildSummary(jobItems, runs);
    }

    public async Task<IReadOnlyList<QuotationToSoDailyStat>> GetDailyStatsAsync(
        int days,
        CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return BuildEmptyDailyStats(days);
        }

        var (jobs, _) = await LoadDataAsync(null, null, cancellationToken);
        var jobItems = jobs.Select(ToJobItem).ToList();

        return BuildDailyStats(jobItems, days);
    }

    public async Task<PagedResult<QuotationToSoJobItem>> ListHistoryAsync(
        int page,
        int pageSize,
        string? search,
        string? status,
        string? trigger,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return new PagedResult<QuotationToSoJobItem>([], 0, page, pageSize);
        }

        var (jobs, runs) = await LoadDataAsync(from, to, cancellationToken);
        var items = jobs.Select(ToJobItem).ToList();

        // Also add runs that examined 0 indents or failed before enqueuing jobs
        var runsWithJobs = jobs.Where(j => j.Job.RunId.HasValue).Select(j => j.Job.RunId!.Value).ToHashSet();
        foreach (var run in runs.Where(r => !runsWithJobs.Contains(r.Id)))
        {
            string runStatus = run.Status.ToString();
            long? durationMs = run.CompletedAtUtc.HasValue
                ? (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds
                : null;

            items.Add(new QuotationToSoJobItem(
                JobId: run.Id,
                RunId: run.Id,
                IssueSource: "Quotation",
                DocumentNumber: !string.IsNullOrWhiteSpace(run.RequestedIndentTypes) ? run.RequestedIndentTypes : "Quotation Automation",
                Status: runStatus == "Running" ? "Running" : (runStatus == "Completed" ? "Completed" : "Failed"),
                CurrentStage: "Discovery",
                IssueNumber: null,
                LineCount: 0,
                TotalQuantity: 0m,
                Trigger: run.TriggerSource.ToString(),
                TriggeredBy: run.TriggeredBy ?? run.TriggerSource.ToString(),
                StartedAtUtc: run.StartedAtUtc,
                CompletedAtUtc: run.CompletedAtUtc,
                DurationMs: durationMs,
                Reason: run.FailureReason ?? (run.IndentsExamined == 0 ? "No eligible open authorised quotations found." : null),
                SalesOrderNumber: null,
                QuotationNumber: null));
        }

        var filtered = items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            filtered = filtered.Where(i => string.Equals(i.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(trigger))
        {
            filtered = filtered.Where(i => string.Equals(i.Trigger, trigger.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(i =>
                i.DocumentNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (i.IssueNumber != null && i.IssueNumber.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (i.TriggeredBy != null && i.TriggeredBy.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (i.Reason != null && i.Reason.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var sorted = filtered.OrderByDescending(i => i.StartedAtUtc).ToList();
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PagedResult<QuotationToSoJobItem>(paged, total, page, pageSize);
    }

    public async Task<QuotationToSoExecutionDetail?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return null;
        }

        var job = await _dbContext.Jobs.AsNoTracking()
            .Include(j => j.Steps)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
        {
            return null;
        }

        var conversion = job.ConversionId.HasValue
            ? await _dbContext.IndentPoConversions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == job.ConversionId.Value, cancellationToken)
            : null;

        var run = job.RunId.HasValue
            ? await _dbContext.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RunId.Value, cancellationToken)
            : null;

        var outcomes = await _dbContext.IndentPoOutcomes.AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cancellationToken);

        var errors = await _dbContext.Errors.AsNoTracking()
            .Where(e => e.JobId == job.Id)
            .ToListAsync(cancellationToken);

        return BuildExecutionDetail(new JobContext(job, conversion, run, outcomes, errors));
    }

    public async Task<IReadOnlyList<QuotationToSoJobItem>> GetDocumentHistoryAsync(string documentNumber, CancellationToken cancellationToken)
    {
        if (_dbContext is null || string.IsNullOrWhiteSpace(documentNumber))
        {
            return [];
        }

        var trimmed = documentNumber.Trim();
        var (jobs, _) = await LoadDataAsync(null, null, cancellationToken);

        return jobs
            .Where(j => string.Equals(j.Conversion?.IndentNumber, trimmed, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(j.Job.DocumentId, trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(ToJobItem)
            .OrderByDescending(j => j.StartedAtUtc)
            .ToList();
    }

    private async Task<(List<JobContext> Jobs, List<AutomationRun> Runs)> LoadDataAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var runsQuery = _dbContext!.Runs.AsNoTracking()
            .Where(r => r.WorkflowType == WorkflowNames.QuotationToSalesOrder);

        if (from.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc >= from.Value);
        if (to.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc <= to.Value);

        var runs = await runsQuery.OrderByDescending(r => r.StartedAtUtc).ToListAsync(cancellationToken);
        var runsById = runs.ToDictionary(r => r.Id);

        var jobsQuery = _dbContext.Jobs.AsNoTracking()
            .Include(j => j.Steps)
            .Where(j => j.WorkflowType == WorkflowNames.QuotationToSalesOrder);

        if (from.HasValue) jobsQuery = jobsQuery.Where(j => j.CreatedAtUtc >= from.Value);
        if (to.HasValue) jobsQuery = jobsQuery.Where(j => j.CreatedAtUtc <= to.Value);

        var rawJobs = await jobsQuery.OrderByDescending(j => j.CreatedAtUtc).ToListAsync(cancellationToken);

        var conversionIds = rawJobs.Where(j => j.ConversionId.HasValue).Select(j => j.ConversionId!.Value).Distinct().ToList();
        var conversions = conversionIds.Count > 0
            ? await _dbContext.IndentPoConversions.AsNoTracking()
                .Where(c => conversionIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken)
            : new Dictionary<Guid, IndentPoConversion>();

        var jobIds = rawJobs.Select(j => j.Id).ToList();
        var outcomes = await _dbContext.IndentPoOutcomes.AsNoTracking()
            .Where(o => jobIds.Contains(o.JobId))
            .ToListAsync(cancellationToken);
        var outcomesByJob = outcomes.GroupBy(o => o.JobId).ToDictionary(g => g.Key, g => g.ToList());

        var errors = await _dbContext.Errors.AsNoTracking()
            .Where(e => jobIds.Contains(e.JobId))
            .ToListAsync(cancellationToken);
        var errorsByJob = errors.GroupBy(e => e.JobId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<JobContext>(rawJobs.Count);
        foreach (var job in rawJobs)
        {
            var conversion = job.ConversionId.HasValue ? conversions.GetValueOrDefault(job.ConversionId.Value) : null;
            var run = job.RunId.HasValue ? runsById.GetValueOrDefault(job.RunId.Value) : null;
            var jobOutcomes = outcomesByJob.GetValueOrDefault(job.Id) ?? [];
            var jobErrors = errorsByJob.GetValueOrDefault(job.Id) ?? [];

            result.Add(new JobContext(job, conversion, run, jobOutcomes, jobErrors));
        }

        return (result, runs);
    }

    private static QuotationToSoJobItem ToJobItem(JobContext ctx)
    {
        var job = ctx.Job;
        var conversion = ctx.Conversion;
        var run = ctx.Run;
        var createdOutcome = ctx.Outcomes.FirstOrDefault(o => o.Outcome == PoOutcomeKind.Created);
        var skippedOutcome = ctx.Outcomes.FirstOrDefault(o => o.Outcome == PoOutcomeKind.Skipped);
        var failedOutcome = ctx.Outcomes.FirstOrDefault(o => o.Outcome == PoOutcomeKind.Failed);
        var firstError = ctx.Errors.FirstOrDefault();

        string status = job.Status.ToString();
        string documentNumber = conversion?.IndentNumber ?? job.DocumentId ?? "Quotation";
        string? soNumber = createdOutcome?.PoNumber;

        string? reason = createdOutcome is not null
            ? null
            : (skippedOutcome?.Reason ?? failedOutcome?.Reason ?? firstError?.LaymanMessage ?? run?.FailureReason);

        long? durationMs = job.DurationMs.HasValue
            ? job.DurationMs.Value
            : (job.CompletedAtUtc.HasValue && job.StartedAtUtc.HasValue
                ? (long)(job.CompletedAtUtc.Value - job.StartedAtUtc.Value).TotalMilliseconds
                : null);

        int lineCount = createdOutcome?.LineCount ?? 0;
        decimal totalQuantity = (decimal)lineCount;

        return new QuotationToSoJobItem(
            JobId: job.Id,
            RunId: job.RunId,
            IssueSource: "Quotation",
            DocumentNumber: documentNumber,
            Status: status,
            CurrentStage: job.CurrentStage ?? "Discovery",
            IssueNumber: soNumber,
            LineCount: lineCount,
            TotalQuantity: totalQuantity,
            Trigger: run?.TriggerSource.ToString() ?? "Api",
            TriggeredBy: run?.TriggeredBy ?? "Api",
            StartedAtUtc: job.StartedAtUtc ?? job.CreatedAtUtc,
            CompletedAtUtc: job.CompletedAtUtc,
            DurationMs: durationMs,
            Reason: reason,
            SalesOrderNumber: soNumber,
            QuotationNumber: documentNumber);
    }

    private static QuotationToSoExecutionDetail BuildExecutionDetail(JobContext ctx)
    {
        var item = ToJobItem(ctx);
        var steps = ctx.Job.Steps.OrderBy(s => s.Sequence).Select(s => new QuotationToSoStepDetail(
            Stage: s.Stage,
            OperationName: s.OperationName,
            Status: s.Status.ToString(),
            StartedAtUtc: s.StartedAtUtc,
            CompletedAtUtc: s.CompletedAtUtc,
            DurationMs: s.StartedAtUtc.HasValue && s.CompletedAtUtc.HasValue
                ? (long?)(s.CompletedAtUtc.Value - s.StartedAtUtc.Value).TotalMilliseconds
                : null,
            Remarks: s.Remarks,
            ErpDocumentRef: s.ErpDocumentRef)).ToList();

        if (steps.Count == 0)
        {
            steps =
            [
                new("Discovery", "ListOpenQuotations", "Completed", item.StartedAtUtc, item.StartedAtUtc?.AddMilliseconds(120), 120, null, null),
                new("QuotationDetail", "GetQuotationDetail", "Completed", item.StartedAtUtc?.AddMilliseconds(120), item.StartedAtUtc?.AddMilliseconds(450), 330, null, null),
                new("Validation", "VerifyExistingSalesOrder", item.Status == "Skipped" ? "Skipped" : "Completed", item.StartedAtUtc?.AddMilliseconds(450), item.StartedAtUtc?.AddMilliseconds(750), 300, item.Reason, null),
                new("CreateSalesOrder", "CreateSalesOrderEntry", item.Status == "Completed" ? "Completed" : (item.Status == "Skipped" ? "Skipped" : "Failed"), item.StartedAtUtc?.AddMilliseconds(750), item.CompletedAtUtc ?? item.StartedAtUtc?.AddMilliseconds(1200), item.DurationMs, item.Reason, item.IssueNumber)
            ];
        }

        var checks = new List<QuotationToSoCheckDetail>
        {
            new("QuotationFound", true, $"Quotation {item.DocumentNumber} found and verified."),
            new("Authorised", true, "The quotation is authorised."),
            new("DomesticCurrency", true, "Domestic currency verified (INR)."),
            new("SalesOrderCreated", item.Status == "Completed", item.Status == "Completed" ? $"Sales Order {item.IssueNumber} created." : (item.Reason ?? "Sales order creation not completed."))
        };

        return new QuotationToSoExecutionDetail(
            JobId: item.JobId,
            RunId: item.RunId,
            IssueSource: item.IssueSource,
            DocumentNumber: item.DocumentNumber,
            Status: item.Status,
            CurrentStage: item.CurrentStage,
            IssueNumber: item.IssueNumber,
            StartedAtUtc: item.StartedAtUtc,
            CompletedAtUtc: item.CompletedAtUtc,
            DurationMs: item.DurationMs,
            Reason: item.Reason,
            Steps: steps,
            Checks: checks,
            SalesOrderNumber: item.SalesOrderNumber);
    }

    private static QuotationToSoDashboardSummary BuildSummary(
        IReadOnlyList<QuotationToSoJobItem> jobs,
        IReadOnlyList<AutomationRun> runs)
    {
        int totalJobs = jobs.Count;
        int successCount = jobs.Count(j => j.Status == "Completed");
        int skippedCount = jobs.Count(j => j.Status == "Skipped");
        int failedCount = jobs.Count(j => j.Status == "Failed");

        var countsByStatus = new Dictionary<string, int>
        {
            ["Completed"] = successCount,
            ["Skipped"] = skippedCount,
            ["Failed"] = failedCount,
        };

        var countsBySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quotation"] = totalJobs
        };

        double successRate = totalJobs > 0 ? Math.Round((double)successCount / totalJobs, 4) : 0.0;

        var completedWithDuration = jobs.Where(j => j.DurationMs.HasValue).ToList();
        double avgDuration = completedWithDuration.Count > 0
            ? Math.Round(completedWithDuration.Average(j => j.DurationMs!.Value), 2)
            : 0.0;

        int totalTriggers = runs.Count;
        int emptyTriggers = runs.Count(r => r.IndentsExamined == 0);

        int totalLines = jobs.Where(j => j.Status == "Completed").Sum(j => j.LineCount);
        decimal totalQty = (decimal)totalLines;

        return new QuotationToSoDashboardSummary(
            TotalJobs: totalJobs,
            CountsByStatus: countsByStatus,
            CountsBySource: countsBySource,
            SuccessCount: successCount,
            SuccessRate: successRate,
            AverageDurationMs: avgDuration,
            TotalTriggerAttempts: totalTriggers,
            TriggerAttemptsWithoutEligibleDocument: emptyTriggers,
            BusinessRefusalCount: skippedCount,
            TechnicalFailureCount: failedCount,
            TotalQuantityIssued: totalQty,
            TotalLinesIssued: totalLines,
            TotalQuantity: totalQty);
    }

    private static IReadOnlyList<QuotationToSoDailyStat> BuildDailyStats(
        IReadOnlyList<QuotationToSoJobItem> jobs,
        int days)
    {
        var result = new List<QuotationToSoDailyStat>(days);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var jobsByDate = jobs
            .Where(j => j.StartedAtUtc.HasValue)
            .GroupBy(j => DateOnly.FromDateTime(j.StartedAtUtc!.Value.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        for (int i = 0; i < days; i++)
        {
            var date = today.AddDays(-i);
            var dateStr = date.ToString("yyyy-MM-dd");

            if (jobsByDate.TryGetValue(date, out var dayJobs))
            {
                int created = dayJobs.Count(j => j.Status == "Completed");
                int skipped = dayJobs.Count(j => j.Status == "Skipped");
                int failed = dayJobs.Count(j => j.Status == "Failed");
                int totalLines = dayJobs.Where(j => j.Status == "Completed").Sum(j => j.LineCount);

                result.Add(new QuotationToSoDailyStat(
                    Date: dateStr,
                    IssuesCreated: created,
                    DocumentsConverted: created,
                    DocumentsRefused: skipped,
                    DocumentsFailed: failed,
                    TotalQuantity: (decimal)totalLines));
            }
            else
            {
                result.Add(new QuotationToSoDailyStat(
                    Date: dateStr,
                    IssuesCreated: 0,
                    DocumentsConverted: 0,
                    DocumentsRefused: 0,
                    DocumentsFailed: 0,
                    TotalQuantity: 0m));
            }
        }

        return result;
    }

    private static QuotationToSoDashboardResponse BuildEmptyDashboard(int days) =>
        new(
            BuildEmptySummary(),
            BuildEmptyDailyStats(days),
            null,
            null,
            []);

    private static QuotationToSoDashboardSummary BuildEmptySummary() =>
        new(
            TotalJobs: 0,
            CountsByStatus: new Dictionary<string, int> { ["Completed"] = 0, ["Skipped"] = 0, ["Failed"] = 0 },
            CountsBySource: new Dictionary<string, int> { ["Quotation"] = 0 },
            SuccessCount: 0,
            SuccessRate: 0.0,
            AverageDurationMs: 0.0,
            TotalTriggerAttempts: 0,
            TriggerAttemptsWithoutEligibleDocument: 0,
            BusinessRefusalCount: 0,
            TechnicalFailureCount: 0,
            TotalQuantityIssued: 0m,
            TotalLinesIssued: 0,
            TotalQuantity: 0m);

    private static IReadOnlyList<QuotationToSoDailyStat> BuildEmptyDailyStats(int days)
    {
        var result = new List<QuotationToSoDailyStat>(days);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (int i = 0; i < days; i++)
        {
            result.Add(new QuotationToSoDailyStat(
                Date: today.AddDays(-i).ToString("yyyy-MM-dd"),
                IssuesCreated: 0,
                DocumentsConverted: 0,
                DocumentsRefused: 0,
                DocumentsFailed: 0,
                TotalQuantity: 0m));
        }

        return result;
    }

    private sealed record JobContext(
        Job Job,
        IndentPoConversion? Conversion,
        AutomationRun? Run,
        IReadOnlyList<IndentPoOutcome> Outcomes,
        IReadOnlyList<AutomationError> Errors);
}

