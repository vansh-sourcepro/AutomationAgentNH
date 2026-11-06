using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.History;

public sealed class JsonSoAllocationToDcHistoryRepository : ISoAllocationToDcHistoryRepository
{
    private const int MaxStoredExecutions = 5000;
    private const int MaxStoredRuns = 1000;

    private readonly string _filePath;
    private readonly ILogger<JsonSoAllocationToDcHistoryRepository> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<SoAllocationToDcRunRecord> _runs = new();
    private List<SoAllocationToDcExecutionRecord> _executions = new();
    private bool _initialized;

    private sealed class HistoryStore
    {
        public List<SoAllocationToDcRunRecord> Runs { get; set; } = new();
        public List<SoAllocationToDcExecutionRecord> Executions { get; set; } = new();
    }

    public JsonSoAllocationToDcHistoryRepository(ILogger<JsonSoAllocationToDcHistoryRepository> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "SoAllocationToDcHistory.json");
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
                var data = JsonSerializer.Deserialize<HistoryStore>(json);
                if (data != null)
                {
                    _runs = data.Runs ?? new();
                    _executions = data.Executions ?? new();

                    foreach (var exec in _executions)
                    {
                        var isSuccess = exec.DcCreated || !string.IsNullOrWhiteSpace(exec.DcNumber) || exec.Status == "Completed" || exec.Status == "Success";
                        if (isSuccess && (exec.DcCreated || !string.IsNullOrWhiteSpace(exec.DcNumber)))
                        {
                            exec.Status = "Success";
                            exec.DcCreated = true;
                            exec.FailureReason = null;
                            exec.FailedField = null;
                            exec.FailedApi = null;
                        }
                        else if (!isSuccess && (exec.Status == "Skipped" ||
                            (exec.FailureReason != null && (exec.FailureReason.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase) || exec.FailureReason.Contains("Duplicate DC prevented", StringComparison.OrdinalIgnoreCase) || exec.FailureReason.Contains("No pending dispatchable allocation", StringComparison.OrdinalIgnoreCase)))))
                        {
                            exec.Status = "Skipped";
                        }
                    }

                    foreach (var run in _runs)
                    {
                        if (run.CreatedDcCount > 0 || run.SuccessfulAllocations > 0)
                        {
                            if (run.FailedAllocations == 0)
                            {
                                run.Status = "Success";
                                run.FailureReason = null;
                            }
                            else
                            {
                                run.Status = "Partial";
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Allocation-to-DC history from {Path}. Starting fresh.", _filePath);
            _runs = new();
            _executions = new();
        }
        finally
        {
            _initialized = true;
        }
    }

    private async Task SaveToFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new HistoryStore
            {
                Runs = _runs.Take(MaxStoredRuns).ToList(),
                Executions = _executions.Take(MaxStoredExecutions).ToList()
            };

            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Allocation-to-DC history to {Path}", _filePath);
        }
    }

    public async Task RecordRunAsync(
        SoAllocationToDcRunRecord run,
        List<SoAllocationToDcExecutionRecord> executions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executions);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);

            run.JobIds = executions.Select(e => e.JobId).ToList();
            _runs.Insert(0, run);

            foreach (var exec in executions)
            {
                exec.RunId = run.RunId;
                _executions.Insert(0, exec);
            }

            // Cap list size
            if (_runs.Count > MaxStoredRuns)
            {
                _runs = _runs.Take(MaxStoredRuns).ToList();
            }
            if (_executions.Count > MaxStoredExecutions)
            {
                _executions = _executions.Take(MaxStoredExecutions).ToList();
            }

            await SaveToFileAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SoAllocationToDcDashboardDto> GetDashboardAsync(
        int days = 30,
        int recentCount = 10,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);

            var summary = CalculateSummary(_runs, _executions);
            var dailyStats = CalculateDailyStats(_executions, days);
            var recentJobs = _executions
                .OrderByDescending(e => e.StartedAtUtc)
                .Take(recentCount)
                .ToList();

            var lastCompleted = _executions
                .Where(e => (e.Status == "Success" || e.Status == "Completed" || e.DcCreated) && !string.IsNullOrEmpty(e.DcNumber))
                .OrderByDescending(e => e.CompletedAtUtc ?? e.StartedAtUtc)
                .FirstOrDefault();

            var lastExecution = _executions
                .OrderByDescending(e => e.StartedAtUtc)
                .FirstOrDefault();

            return new SoAllocationToDcDashboardDto
            {
                Summary = summary,
                DailyStats = dailyStats,
                RecentJobs = recentJobs,
                LastCompletedJob = lastCompleted,
                LastExecution = lastExecution
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SoAllocationToDcSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return CalculateSummary(_runs, _executions);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<SoAllocationToDcDailyStatDto>> GetDailyStatsAsync(int days = 30, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return CalculateDailyStats(_executions, days);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SoAllocationToDcHistoryPagedDto> GetHistoryAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);

            IEnumerable<SoAllocationToDcExecutionRecord> query = _executions;

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                query = query.Where(e =>
                    (!string.IsNullOrEmpty(e.OafNo) && e.OafNo.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.IntOrdNo) && e.IntOrdNo.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.DcNumber) && e.DcNumber.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.CustomerCode) && e.CustomerCode.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.ShipmentCode) && e.ShipmentCode.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.FailureReason) && e.FailureReason.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(e => string.Equals(e.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (from.HasValue)
            {
                query = query.Where(e => e.StartedAtUtc >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(e => e.StartedAtUtc <= to.Value);
            }

            var all = query.OrderByDescending(e => e.StartedAtUtc).ToList();
            var totalCount = all.Count;

            var safePage = Math.Max(1, page);
            var safePageSize = Math.Clamp(pageSize, 1, 200);

            var items = all
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToList();

            return new SoAllocationToDcHistoryPagedDto
            {
                Items = items,
                TotalCount = totalCount,
                Page = safePage,
                PageSize = safePageSize
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SoAllocationToDcExecutionRecord?> GetExecutionByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _executions.FirstOrDefault(e => e.JobId == jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SoAllocationToDcSummaryDto CalculateSummary(
        List<SoAllocationToDcRunRecord> runs,
        List<SoAllocationToDcExecutionRecord> executions)
    {
        var totalProcesses = executions.Count;
        var successCount = executions.Count(e => e.DcCreated || e.Status == "Success" || e.Status == "Completed");
        var skippedCount = executions.Count(e => !e.DcCreated && (e.Status == "Skipped" ||
            (e.FailureReason != null && (e.FailureReason.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase) || e.FailureReason.Contains("Duplicate DC prevented", StringComparison.OrdinalIgnoreCase) || e.FailureReason.Contains("No pending dispatchable allocation", StringComparison.OrdinalIgnoreCase)))));
        var failedCount = executions.Count(e => !e.DcCreated && e.Status != "Skipped" && (e.Status == "Failed" || (e.FailureReason != null && !e.FailureReason.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase) && !e.FailureReason.Contains("Duplicate DC prevented", StringComparison.OrdinalIgnoreCase) && !e.FailureReason.Contains("No pending dispatchable allocation", StringComparison.OrdinalIgnoreCase))));

        var successRate = totalProcesses > 0
            ? Math.Round((double)successCount / totalProcesses, 4)
            : 0.0;

        var totalDcsCreated = executions.Count(e => e.DcCreated || !string.IsNullOrEmpty(e.DcNumber));
        var totalAllocatedQty = executions.Sum(e => e.AllocatedQty);
        var totalDcQty = executions.Sum(e => e.DcQty);

        var totalTriggerAttempts = runs.Count;
        var triggerAttemptsWithoutEligible = runs.Count(r => r.TotalAllocations == 0);

        var durations = executions
            .Where(e => e.DurationMs.HasValue)
            .Select(e => (double)e.DurationMs!.Value)
            .ToList();

        var avgDuration = durations.Count > 0 ? (double?)Math.Round(durations.Average(), 0) : null;

        return new SoAllocationToDcSummaryDto
        {
            TotalProcesses = totalProcesses,
            SuccessCount = successCount,
            FailedCount = failedCount,
            SkippedCount = skippedCount,
            SuccessRate = successRate,
            TotalDcsCreated = totalDcsCreated,
            TotalAllocatedQty = totalAllocatedQty,
            TotalDcQty = totalDcQty,
            TotalTriggerAttempts = totalTriggerAttempts,
            TriggerAttemptsWithoutEligibleAllocation = triggerAttemptsWithoutEligible,
            BusinessRefusalCount = skippedCount,
            TechnicalFailureCount = failedCount,
            TotalJobs = totalProcesses,
            AverageDurationMs = avgDuration,
            CountsByStatus = new Dictionary<string, int>
            {
                ["Success"] = successCount,
                ["Completed"] = successCount,
                ["Failed"] = failedCount,
                ["Skipped"] = skippedCount
            }
        };
    }

    private static List<SoAllocationToDcDailyStatDto> CalculateDailyStats(
        List<SoAllocationToDcExecutionRecord> executions,
        int days)
    {
        var safeDays = Math.Clamp(days, 1, 90);
        var today = DateTimeOffset.UtcNow.Date;
        var startDate = today.AddDays(-(safeDays - 1));

        var dailyMap = new Dictionary<DateTime, SoAllocationToDcDailyStatDto>();
        for (var i = 0; i < safeDays; i++)
        {
            var date = startDate.AddDays(i);
            dailyMap[date] = new SoAllocationToDcDailyStatDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                DcsCreated = 0,
                AllocationsConverted = 0,
                AllocationsFailed = 0,
                AllocationsSkipped = 0
            };
        }

        foreach (var exec in executions)
        {
            var execDate = exec.StartedAtUtc.UtcDateTime.Date;
            if (dailyMap.TryGetValue(execDate, out var stat))
            {
                var isSuccess = exec.DcCreated || !string.IsNullOrEmpty(exec.DcNumber) || exec.Status == "Success" || exec.Status == "Completed";
                var isSkipped = !isSuccess && (exec.Status == "Skipped" || (exec.FailureReason != null && (exec.FailureReason.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase) || exec.FailureReason.Contains("Duplicate DC prevented", StringComparison.OrdinalIgnoreCase))));

                if (exec.DcCreated || !string.IsNullOrEmpty(exec.DcNumber))
                {
                    stat.DcsCreated++;
                }

                if (isSuccess)
                {
                    stat.AllocationsConverted++;
                }
                else if (isSkipped)
                {
                    stat.AllocationsSkipped++;
                }
                else
                {
                    stat.AllocationsFailed++;
                }
            }
        }

        return dailyMap.Values.OrderBy(s => s.Date).ToList();
    }
}
