using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

public sealed partial class DcToInvoiceService
{
    /// <inheritdoc />
    public Task<DcToInvoiceHistoryPagedResult> GetHistoryAsync(
        DcToInvoiceHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var allHistory = _tracker.GetAllHistory();
        var items = allHistory.Select(MapToHistoryItem);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            var searchUnpadded = search.TrimStart('0');

            items = items.Where(i =>
                (!string.IsNullOrEmpty(i.DcNo) && (
                    i.DcNo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(searchUnpadded) && i.DcNo.TrimStart('0').Equals(searchUnpadded, StringComparison.OrdinalIgnoreCase)))) ||
                (!string.IsNullOrEmpty(i.InvoiceNumber) && (
                    i.InvoiceNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(searchUnpadded) && i.InvoiceNumber.TrimStart('0').Equals(searchUnpadded, StringComparison.OrdinalIgnoreCase)))) ||
                (!string.IsNullOrEmpty(i.CustomerCode) && i.CustomerCode.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(i.CustomerName) && i.CustomerName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (i.DcId > 0 && i.DcId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            if (!string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(i => string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.CustomerCode))
        {
            var cust = query.CustomerCode.Trim();
            items = items.Where(i => string.Equals(i.CustomerCode, cust, StringComparison.OrdinalIgnoreCase));
        }

        if (query.FromUtc.HasValue)
        {
            var from = query.FromUtc.Value.UtcDateTime;
            items = items.Where(i => i.ConvertedAtUtc >= from);
        }

        if (query.ToUtc.HasValue)
        {
            var to = query.ToUtc.Value.UtcDateTime;
            items = items.Where(i => i.ConvertedAtUtc <= to);
        }

        var ordered = items.OrderByDescending(i => i.ConvertedAtUtc).ToList();
        int totalCount = ordered.Count;
        int page = Math.Max(1, query.Page);
        int pageSize = Math.Clamp(query.PageSize, 1, 200);

        var pagedItems = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(new DcToInvoiceHistoryPagedResult(
            Items: pagedItems,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize));
    }

    /// <inheritdoc />
    public Task<DcSpecificHistoryResult> GetDcHistoryAsync(
        string? dcNo = null,
        long? dcId = null,
        CancellationToken cancellationToken = default)
    {
        var cleanNo = dcNo?.Trim();
        var unpadded = cleanNo?.TrimStart('0');

        bool isConverted = _tracker.IsDcConverted(dcId, cleanNo, out var trackerRecord);
        var allHistory = _tracker.GetAllHistory();

        var matching = allHistory
            .Where(r =>
                (dcId.HasValue && dcId.Value > 0 && r.DcId == dcId.Value) ||
                (!string.IsNullOrWhiteSpace(cleanNo) && (
                    string.Equals(r.DcNo, cleanNo, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(unpadded) && string.Equals(r.DcNo.TrimStart('0'), unpadded, StringComparison.OrdinalIgnoreCase)))))
            .Select(MapToHistoryItem)
            .OrderByDescending(r => r.ConvertedAtUtc)
            .ToList();

        var latest = matching.FirstOrDefault() ?? (trackerRecord != null ? MapToHistoryItem(trackerRecord) : null);
        var effectiveDcNo = cleanNo ?? latest?.DcNo ?? trackerRecord?.DcNo ?? string.Empty;

        return Task.FromResult(new DcSpecificHistoryResult(
            DcNo: effectiveDcNo,
            DcId: dcId ?? latest?.DcId ?? trackerRecord?.DcId,
            IsConverted: isConverted,
            LatestRecord: latest,
            History: matching));
    }

    /// <inheritdoc />
    public async Task<DcToInvoiceDashboardSummary> GetDashboardSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var allHistory = _tracker.GetAllHistory();
        var items = allHistory.Select(MapToHistoryItem).ToList();

        int totalProcessed = items.Count;
        int invoicesCreated = items.Count(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(i.InvoiceNumber)
            && !string.Equals(i.InvoiceNumber, "ALREADY_INVOICED", StringComparison.OrdinalIgnoreCase));

        int alreadyConverted = items.Count(i => string.Equals(i.Status, "ALREADY_CONVERTED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Status, "AlreadyCreated", StringComparison.OrdinalIgnoreCase));

        int failedCount = items.Count(i => string.Equals(i.Status, "FAILED", StringComparison.OrdinalIgnoreCase));

        decimal totalAmount = items
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.TotalAmount);

        decimal totalQuantity = items
            .Where(i => string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.TotalQuantity);

        double successRate = totalProcessed > 0
            ? Math.Round((double)(invoicesCreated + alreadyConverted) / totalProcessed * 100.0, 2)
            : 100.0;

        var countsByStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUCCESS"] = invoicesCreated,
            ["ALREADY_CONVERTED"] = alreadyConverted,
            ["FAILED"] = failedCount
        };

        // Query pending DCs in ERP safely
        int pendingDcCount = 0;
        try
        {
            var pending = await GetPendingDeliveryChallansAsync(cancellationToken: cancellationToken);
            pendingDcCount = pending?.Count ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DcToInvoice: Could not fetch pending DC count for dashboard");
        }

        var topCustomers = items
            .Where(i => !string.IsNullOrWhiteSpace(i.CustomerCode) && string.Equals(i.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.CustomerCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CustomerConversionMetric(
                CustomerCode: g.Key,
                CustomerName: g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CustomerName))?.CustomerName,
                InvoiceCount: g.Count(),
                TotalAmount: g.Sum(x => x.TotalAmount),
                TotalQuantity: g.Sum(x => x.TotalQuantity)))
            .OrderByDescending(c => c.TotalAmount)
            .Take(10)
            .ToList();

        var recentConversions = items
            .OrderByDescending(i => i.ConvertedAtUtc)
            .Take(10)
            .ToList();

        return new DcToInvoiceDashboardSummary(
            TotalProcessed: totalProcessed,
            InvoicesCreated: invoicesCreated,
            AlreadyConvertedCount: alreadyConverted,
            FailedCount: failedCount,
            TotalInvoiceAmount: totalAmount,
            TotalQuantity: totalQuantity,
            SuccessRate: successRate,
            PendingDcCount: pendingDcCount,
            CountsByStatus: countsByStatus,
            TopCustomers: topCustomers,
            RecentConversions: recentConversions);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DcToInvoiceDailyStat>> GetDailyStatsAsync(
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 365);
        var endDate = DateTime.UtcNow.Date;
        var startDate = endDate.AddDays(-(days - 1));

        var allHistory = _tracker.GetAllHistory();
        var items = allHistory.Select(MapToHistoryItem).ToList();

        var grouped = items
            .Where(i => i.ConvertedAtUtc.Date >= startDate && i.ConvertedAtUtc.Date <= endDate)
            .GroupBy(i => i.ConvertedAtUtc.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DcToInvoiceDailyStat>(days);
        for (var dt = startDate; dt <= endDate; dt = dt.AddDays(1))
        {
            if (grouped.TryGetValue(dt, out var dayList))
            {
                var created = dayList.Count(x => string.Equals(x.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
                var failed = dayList.Count(x => string.Equals(x.Status, "FAILED", StringComparison.OrdinalIgnoreCase));
                var amount = dayList.Where(x => string.Equals(x.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)).Sum(x => x.TotalAmount);
                var qty = dayList.Where(x => string.Equals(x.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)).Sum(x => x.TotalQuantity);

                result.Add(new DcToInvoiceDailyStat(
                    Date: dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    InvoicesCreated: created,
                    DeliveryChallansConverted: dayList.Count,
                    TotalAmount: amount,
                    TotalQuantity: qty,
                    Failures: failed));
            }
            else
            {
                result.Add(new DcToInvoiceDailyStat(
                    Date: dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    InvoicesCreated: 0,
                    DeliveryChallansConverted: 0,
                    TotalAmount: 0m,
                    TotalQuantity: 0m,
                    Failures: 0));
            }
        }

        return Task.FromResult<IReadOnlyList<DcToInvoiceDailyStat>>(result);
    }

    private static DcToInvoiceHistoryItem MapToHistoryItem(ConvertedDcRecord record)
    {
        return new DcToInvoiceHistoryItem(
            DcId: record.DcId,
            DcNo: record.DcNo,
            CustomerCode: record.CustomerCode,
            CustomerName: record.CustomerName,
            InvoiceNumber: record.InvoiceNumber,
            InvoiceId: record.InvoiceId,
            ItemCount: record.ItemCount,
            TotalQuantity: record.TotalQuantity,
            TotalAmount: record.TotalAmount,
            RateStructureCode: record.RateStructureCode,
            ConvertedAtUtc: record.ConvertedAtUtc,
            Status: !string.IsNullOrWhiteSpace(record.Status) ? record.Status : "SUCCESS",
            TriggerSource: !string.IsNullOrWhiteSpace(record.TriggerSource) ? record.TriggerSource : "Api",
            Message: record.Message,
            FailureReason: record.FailureReason);
    }
}

