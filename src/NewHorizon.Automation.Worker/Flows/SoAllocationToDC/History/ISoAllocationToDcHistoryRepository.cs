using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.History;

public interface ISoAllocationToDcHistoryRepository
{
    Task RecordRunAsync(
        SoAllocationToDcRunRecord run,
        List<SoAllocationToDcExecutionRecord> executions,
        CancellationToken cancellationToken = default);

    Task<SoAllocationToDcDashboardDto> GetDashboardAsync(
        int days = 30,
        int recentCount = 10,
        CancellationToken cancellationToken = default);

    Task<SoAllocationToDcSummaryDto> GetSummaryAsync(
        CancellationToken cancellationToken = default);

    Task<List<SoAllocationToDcDailyStatDto>> GetDailyStatsAsync(
        int days = 30,
        CancellationToken cancellationToken = default);

    Task<SoAllocationToDcHistoryPagedDto> GetHistoryAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    Task<SoAllocationToDcExecutionRecord?> GetExecutionByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}
