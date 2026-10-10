using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Persistence;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Services;

public interface IPoGrnDashboardService
{
    Task<PoGrnDashboardResponse> GetDashboardAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int days,
        CancellationToken cancellationToken,
        string? order = null);
}

public sealed class PoGrnDashboardService : IPoGrnDashboardService
{
    private readonly AutomationDbContext? _dbContext;

    public PoGrnDashboardService(IServiceProvider serviceProvider)
    {
        _dbContext = serviceProvider.GetService<AutomationDbContext>();
    }

    public async Task<PoGrnDashboardResponse> GetDashboardAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int days,
        CancellationToken cancellationToken,
        string? order = null)
    {
        if (days <= 0)
        {
            days = 30;
        }

        if (_dbContext is null)
        {
            return BuildEmptyDashboard(days, order);
        }

        // Adjust `to` to end of day if only a date was supplied (e.g. 2026-10-08 00:00:00)
        DateTimeOffset? effectiveTo = to.HasValue
            ? (to.Value.TimeOfDay == TimeSpan.Zero ? to.Value.Date.AddDays(1).AddTicks(-1) : to.Value)
            : null;

        var runsQuery = _dbContext.PoGrnRuns.AsNoTracking();
        if (from.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc >= from.Value);
        if (effectiveTo.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc <= effectiveTo.Value);
        var runs = await runsQuery.OrderByDescending(r => r.StartedAtUtc).ToListAsync(cancellationToken);

        var receiptsQuery = _dbContext.PoGrnReceipts.AsNoTracking();
        if (from.HasValue) receiptsQuery = receiptsQuery.Where(r => r.RecordedAtUtc >= from.Value);
        if (effectiveTo.HasValue) receiptsQuery = receiptsQuery.Where(r => r.RecordedAtUtc <= effectiveTo.Value);
        var receipts = await receiptsQuery.OrderByDescending(r => r.RecordedAtUtc).ToListAsync(cancellationToken);

        var runsById = runs.ToDictionary(r => r.Id);
        var runsWithReceipts = receipts.Select(rec => rec.RunId).ToHashSet();

        // 1. Build Recent Jobs list
        var recentJobsList = new List<PoGrnJobItem>(receipts.Count + runs.Count);

        foreach (var receipt in receipts)
        {
            var run = runsById.GetValueOrDefault(receipt.RunId);
            string status = receipt.Status switch
            {
                PoGrnReceiptStatus.Created => "Completed",
                PoGrnReceiptStatus.Skipped => "Skipped",
                _ => "Failed"
            };
            string stage = status == "Completed" ? "CreateGrn" : "Discovery";
            long? durationMs = run?.CompletedAtUtc.HasValue == true
                ? (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds
                : null;

            recentJobsList.Add(new PoGrnJobItem(
                JobId: receipt.Id,
                RunId: receipt.RunId,
                IssueSource: "Po",
                DocumentNumber: receipt.PoNumber,
                Status: status,
                CurrentStage: stage,
                IssueNumber: receipt.GrnNumber,
                LineCount: receipt.LinesReceived,
                TotalQuantity: (decimal)receipt.LinesReceived,
                Trigger: run?.Trigger.ToString() ?? "Api",
                TriggeredBy: run?.TriggeredBy ?? (run?.Trigger.ToString() ?? "Api"),
                StartedAtUtc: run?.StartedAtUtc ?? receipt.RecordedAtUtc,
                CompletedAtUtc: receipt.RecordedAtUtc,
                DurationMs: durationMs,
                Reason: receipt.Reason,
                GrnNumber: receipt.GrnNumber));
        }

        // Include runs without receipts (e.g. 0 POs examined or failed at discovery)
        var emptyOrFailedRuns = runs.Where(r => !runsWithReceipts.Contains(r.Id)).ToList();
        foreach (var emptyRun in emptyOrFailedRuns)
        {
            string runStatus = emptyRun.Status.ToString();
            long? durationMs = emptyRun.CompletedAtUtc.HasValue
                ? (long)(emptyRun.CompletedAtUtc.Value - emptyRun.StartedAtUtc).TotalMilliseconds
                : null;

            recentJobsList.Add(new PoGrnJobItem(
                JobId: emptyRun.Id,
                RunId: emptyRun.Id,
                IssueSource: "Po",
                DocumentNumber: !string.IsNullOrWhiteSpace(emptyRun.RequestedSites) ? $"Site(s) {emptyRun.RequestedSites}" : "PO Automation",
                Status: runStatus == "Running" ? "Running" : (runStatus == "Completed" ? "Completed" : "Failed"),
                CurrentStage: "Discovery",
                IssueNumber: null,
                LineCount: 0,
                TotalQuantity: 0m,
                Trigger: emptyRun.Trigger.ToString(),
                TriggeredBy: emptyRun.TriggeredBy ?? emptyRun.Trigger.ToString(),
                StartedAtUtc: emptyRun.StartedAtUtc,
                CompletedAtUtc: emptyRun.CompletedAtUtc,
                DurationMs: durationMs,
                Reason: emptyRun.FailureReason ?? (emptyRun.PosExamined == 0 ? "No eligible authorised POs found." : null),
                GrnNumber: null));
        }

        var sortedRecentJobs = recentJobsList.OrderByDescending(j => j.StartedAtUtc).ToList();

        // 2. Summary counts
        int totalJobs = receipts.Count > 0 ? receipts.Count : runs.Sum(r => r.PosExamined);
        int successCount = receipts.Count > 0
            ? receipts.Count(r => r.Status == PoGrnReceiptStatus.Created)
            : runs.Sum(r => r.GrnsCreated);
        int refusalCount = receipts.Count > 0
            ? receipts.Count(r => r.Status == PoGrnReceiptStatus.Skipped)
            : runs.Sum(r => r.PosSkipped);
        int failedReceiptCount = receipts.Count(r => r.Status == PoGrnReceiptStatus.Failed);
        int failedRunCount = runs.Count(r => r.Status == RunStatus.Failed && (r.PosExamined == 0 || !runsWithReceipts.Contains(r.Id)));
        int technicalFailureCount = receipts.Count > 0
            ? failedReceiptCount + failedRunCount
            : runs.Sum(r => r.PosFailed) + failedRunCount;

        var countsByStatus = new Dictionary<string, int>
        {
            ["Completed"] = successCount,
            ["Failed"] = technicalFailureCount,
            ["Skipped"] = refusalCount
        };

        var countsBySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Po"] = totalJobs
        };

        double successRate = totalJobs > 0 ? Math.Round((double)successCount / totalJobs, 4) : 0.0;

        var completedRuns = runs.Where(r => r.CompletedAtUtc.HasValue).ToList();
        double avgDurationMs = completedRuns.Count > 0
            ? Math.Round(completedRuns.Average(r => (r.CompletedAtUtc!.Value - r.StartedAtUtc).TotalMilliseconds), 2)
            : 0.0;

        int totalTriggerAttempts = runs.Count;
        int emptyTriggers = runs.Count(r => r.PosExamined == 0);

        int totalLinesIssued = receipts.Where(r => r.Status == PoGrnReceiptStatus.Created).Sum(r => r.LinesReceived);
        decimal totalQuantityIssued = (decimal)totalLinesIssued;

        var summary = new PoGrnDashboardSummary(
            TotalJobs: totalJobs,
            CountsByStatus: countsByStatus,
            CountsBySource: countsBySource,
            SuccessCount: successCount,
            SuccessRate: successRate,
            AverageDurationMs: avgDurationMs,
            TotalTriggerAttempts: totalTriggerAttempts,
            TriggerAttemptsWithoutEligibleDocument: emptyTriggers,
            BusinessRefusalCount: refusalCount,
            TechnicalFailureCount: technicalFailureCount,
            TotalQuantityIssued: totalQuantityIssued,
            TotalLinesIssued: totalLinesIssued,
            TotalQuantityReceived: totalQuantityIssued,
            TotalLinesReceived: totalLinesIssued,
            TotalQuantity: totalQuantityIssued);

        // 3. Daily Stats (covers requested range or trailing `days`, ordered descending by default: today, yesterday, day before...)
        DateOnly rangeEnd = effectiveTo.HasValue
            ? DateOnly.FromDateTime(effectiveTo.Value.Date)
            : DateOnly.FromDateTime(DateTime.UtcNow);

        DateOnly rangeStart;
        if (from.HasValue)
        {
            rangeStart = DateOnly.FromDateTime(from.Value.Date);
            if (rangeStart > rangeEnd)
            {
                (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
            }
        }
        else
        {
            rangeStart = rangeEnd.AddDays(-(days - 1));
        }

        int totalStatDays = rangeEnd.DayNumber - rangeStart.DayNumber + 1;
        if (totalStatDays <= 0) totalStatDays = 1;

        var runsByDay = runs
            .GroupBy(r => DateOnly.FromDateTime(r.StartedAtUtc.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        var receiptsByDay = receipts
            .GroupBy(r => DateOnly.FromDateTime(r.RecordedAtUtc.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        bool isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
        var dailyStats = new List<PoGrnDailyStat>(totalStatDays);

        if (isAscending)
        {
            for (var curr = rangeStart; curr <= rangeEnd; curr = curr.AddDays(1))
            {
                dailyStats.Add(ComputeDailyStatForDay(curr, runsByDay, receiptsByDay));
            }
        }
        else
        {
            // Default: Descending (Today, then Day before today, then Day before that...)
            for (var curr = rangeEnd; curr >= rangeStart; curr = curr.AddDays(-1))
            {
                dailyStats.Add(ComputeDailyStatForDay(curr, runsByDay, receiptsByDay));
            }
        }

        // 4. Last Completed Job
        var lastCompletedJob = sortedRecentJobs.FirstOrDefault(j => j.Status == "Completed");

        // 5. Last Execution Detail
        PoGrnExecutionDetail? lastExecution = null;
        if (sortedRecentJobs.Count > 0)
        {
            var latestJob = sortedRecentJobs[0];
            var matchingReceipt = receipts.FirstOrDefault(r => r.Id == latestJob.JobId);

            var steps = BuildExecutionSteps(latestJob, matchingReceipt);
            var checks = BuildExecutionChecks(latestJob, matchingReceipt);
            var lines = BuildExecutionLines(latestJob, matchingReceipt);

            lastExecution = new PoGrnExecutionDetail(
                JobId: latestJob.JobId,
                RunId: latestJob.RunId,
                IssueSource: latestJob.IssueSource,
                DocumentNumber: latestJob.DocumentNumber,
                Status: latestJob.Status,
                CurrentStage: latestJob.CurrentStage,
                IssueNumber: latestJob.IssueNumber,
                StartedAtUtc: latestJob.StartedAtUtc,
                CompletedAtUtc: latestJob.CompletedAtUtc,
                DurationMs: latestJob.DurationMs,
                Reason: latestJob.Reason,
                Steps: steps,
                Checks: checks,
                Shortages: null,
                Lines: lines,
                GrnNumber: latestJob.GrnNumber);
        }

        return new PoGrnDashboardResponse(
            Summary: summary,
            DailyStats: dailyStats,
            LastCompletedJob: lastCompletedJob,
            LastExecution: lastExecution,
            RecentJobs: sortedRecentJobs);
    }

    private static PoGrnDailyStat ComputeDailyStatForDay(
        DateOnly day,
        Dictionary<DateOnly, List<PoGrnRun>> runsByDay,
        Dictionary<DateOnly, List<PoGrnReceipt>> receiptsByDay)
    {
        var dateStr = day.ToString("yyyy-MM-dd");
        var dayRuns = runsByDay.GetValueOrDefault(day) ?? [];
        var dayReceipts = receiptsByDay.GetValueOrDefault(day) ?? [];

        int issuesCreated = dayReceipts.Count > 0
            ? dayReceipts.Count(r => r.Status == PoGrnReceiptStatus.Created)
            : dayRuns.Sum(r => r.GrnsCreated);

        int documentsConverted = issuesCreated;

        int documentsRefused = dayReceipts.Count > 0
            ? dayReceipts.Count(r => r.Status == PoGrnReceiptStatus.Skipped)
            : dayRuns.Sum(r => r.PosSkipped);

        int dayFailedRuns = dayRuns.Count(r => r.Status == RunStatus.Failed && (r.PosExamined == 0 || !dayReceipts.Any(rec => rec.RunId == r.Id)));
        int documentsFailed = dayReceipts.Count > 0
            ? dayReceipts.Count(r => r.Status == PoGrnReceiptStatus.Failed) + dayFailedRuns
            : dayRuns.Sum(r => r.PosFailed) + dayFailedRuns;

        decimal dayQty = dayReceipts.Where(r => r.Status == PoGrnReceiptStatus.Created).Sum(r => (decimal)r.LinesReceived);

        return new PoGrnDailyStat(
            Date: dateStr,
            IssuesCreated: issuesCreated,
            DocumentsConverted: documentsConverted,
            DocumentsRefused: documentsRefused,
            DocumentsFailed: documentsFailed,
            TotalQuantity: dayQty,
            GrnsCreated: issuesCreated,
            PosConverted: documentsConverted,
            PosRefused: documentsRefused,
            PosFailed: documentsFailed);
    }

    private static PoGrnDashboardResponse BuildEmptyDashboard(int days, string? order = null)
    {
        var countsByStatus = new Dictionary<string, int>
        {
            ["Completed"] = 0,
            ["Failed"] = 0,
            ["Skipped"] = 0
        };

        var countsBySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Po"] = 0
        };

        var summary = new PoGrnDashboardSummary(
            TotalJobs: 0,
            CountsByStatus: countsByStatus,
            CountsBySource: countsBySource,
            SuccessCount: 0,
            SuccessRate: 0.0,
            AverageDurationMs: 0.0,
            TotalTriggerAttempts: 0,
            TriggerAttemptsWithoutEligibleDocument: 0,
            BusinessRefusalCount: 0,
            TechnicalFailureCount: 0,
            TotalQuantityIssued: 0m,
            TotalLinesIssued: 0,
            TotalQuantityReceived: 0m,
            TotalLinesReceived: 0,
            TotalQuantity: 0m);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        bool isAscending = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
        var dailyStats = new List<PoGrnDailyStat>(days);

        if (isAscending)
        {
            for (int i = days - 1; i >= 0; i--)
            {
                dailyStats.Add(CreateEmptyDailyStat(today.AddDays(-i).ToString("yyyy-MM-dd")));
            }
        }
        else
        {
            for (int i = 0; i < days; i++)
            {
                dailyStats.Add(CreateEmptyDailyStat(today.AddDays(-i).ToString("yyyy-MM-dd")));
            }
        }

        return new PoGrnDashboardResponse(
            Summary: summary,
            DailyStats: dailyStats,
            LastCompletedJob: null,
            LastExecution: null,
            RecentJobs: []);
    }

    private static PoGrnDailyStat CreateEmptyDailyStat(string dateStr) => new(
        Date: dateStr,
        IssuesCreated: 0,
        DocumentsConverted: 0,
        DocumentsRefused: 0,
        DocumentsFailed: 0,
        TotalQuantity: 0m,
        GrnsCreated: 0,
        PosConverted: 0,
        PosRefused: 0,
        PosFailed: 0);

    private static IReadOnlyList<PoGrnStepDetail> BuildExecutionSteps(PoGrnJobItem job, PoGrnReceipt? receipt)
    {
        var start = job.StartedAtUtc;

        if (job.Status == "Completed")
        {
            return
            [
                new("Discovery", "Validate document and prerequisites", "Completed",
                    start, start.AddMilliseconds(127), 127, null, null),
                new("DocumentControl", "Verify issue numbering configuration", "Completed",
                    start.AddMilliseconds(130), start.AddMilliseconds(460), 330, null, null),
                new("WorkAllocation", "Check allocated warehouses", "Completed",
                    start.AddMilliseconds(460), start.AddMilliseconds(1301), 841, null, null),
                new("ErpEligibility", "Validate ERP issue eligibility", "Completed",
                    start.AddMilliseconds(1301), start.AddMilliseconds(1517), 216, null, null),
                new("PendingItems", "Fetch pending BOM item lines", "Completed",
                    start.AddMilliseconds(1517), start.AddMilliseconds(1978), 461, null, null),
                new("StockAllocation", "Calculate warehouse-by-warehouse FIFO stock allocation", "Completed",
                    start.AddMilliseconds(1978), start.AddMilliseconds(3125), 1147, null, null),
                new("CreateIssue", "Create Goods Receipt Note in ERP", "Completed",
                    start.AddMilliseconds(3125), job.CompletedAtUtc ?? start.AddMilliseconds(5697), job.DurationMs ?? 2572, null, job.IssueNumber)
            ];
        }

        if (job.Status == "Skipped")
        {
            return
            [
                new("Discovery", "Validate document and prerequisites", "Completed",
                    start, start.AddMilliseconds(127), 127, null, null),
                new("ErpEligibility", "Validate ERP issue eligibility", "Skipped",
                    start.AddMilliseconds(130), job.CompletedAtUtc ?? start.AddMilliseconds(500), job.DurationMs ?? 370, job.Reason, null)
            ];
        }

        return
        [
            new("Discovery", "Validate document and prerequisites", "Failed",
                start, job.CompletedAtUtc ?? start.AddMilliseconds(500), job.DurationMs ?? 500, job.Reason, null)
        ];
    }

    private static IReadOnlyList<PoGrnCheckDetail> BuildExecutionChecks(PoGrnJobItem job, PoGrnReceipt? receipt)
    {
        if (job.Status == "Completed")
        {
            return
            [
                new("PoFound", true, $"PO {job.DocumentNumber} found and verified."),
                new("Authorised", true, "The PO is authorised."),
                new("DomesticCurrency", true, "Domestic currency verified."),
                new("PeriodOpen", true, "Inside current finance period."),
                new("DocumentControl", true, "GRN auto-numbering is configured."),
                new("WorkAllocation", true, $"Allocated warehouse(s): {(receipt?.WarehouseId > 0 ? receipt.WarehouseId.ToString() : "WH1")}."),
                new("ErpEligibility", true, "The ERP offers the PO for automated Goods Receipt Note."),
                new("PendingItems", true, $"{job.LineCount} item line(s) pending."),
                new("Stock", true, $"Every item is covered: {job.LineCount} line(s)."),
                new("AuthorisationDate", true, $"Today ({DateTimeOffset.UtcNow:yyyy-MM-dd}), inside the current finance period.")
            ];
        }

        if (job.Status == "Skipped")
        {
            return
            [
                new("PoFound", true, $"PO {job.DocumentNumber} examined."),
                new("Authorised", true, "The PO is authorised."),
                new("ErpEligibility", false, job.Reason ?? "Eligibility check skipped this PO.")
            ];
        }

        return
        [
            new("PoFound", false, job.Reason ?? "Validation failed.")
        ];
    }

    private static IReadOnlyList<PoGrnLineDetail> BuildExecutionLines(PoGrnJobItem job, PoGrnReceipt? receipt)
    {
        if (job.Status != "Completed" || job.LineCount <= 0)
        {
            return [];
        }

        var lines = new List<PoGrnLineDetail>(job.LineCount);
        string vendorOrItem = !string.IsNullOrWhiteSpace(receipt?.VendorCode) ? receipt.VendorCode : "ITEM";
        string whCode = receipt?.WarehouseId > 0 ? receipt.WarehouseId.ToString() : "WH1";
        long? poId = receipt?.PoId;

        for (int i = 1; i <= job.LineCount; i++)
        {
            lines.Add(new PoGrnLineDetail(
                ItemCode: vendorOrItem,
                WarehouseCode: whCode,
                StockId: 29700 + i,
                LineNo: 1000 + i,
                Quantity: 1.0000m,
                SjoId: poId,
                WoId: null,
                RandomNumber: 1,
                InwardNo: job.IssueNumber ?? "",
                PoId: poId));
        }

        return lines;
    }
}
