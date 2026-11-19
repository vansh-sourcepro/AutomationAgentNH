using NewHorizon.Automation.Application.Jobs;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// No-op tracker and history service used when automation database tracking is disabled or in unit test setups.
/// </summary>
public sealed class NullIssueToShopFloorTracker : IIssueToShopFloorTracker, IIssueToShopFloorHistoryService
{
    public static readonly NullIssueToShopFloorTracker Instance = new();

    public bool IsEnabled => false;

    public Guid? RunId => null;

    public Guid? ExecutionId => null;

    public Task StartRunAsync(StartIssueRunRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartExecutionAsync(
        string issueSource,
        string documentNumber,
        long documentId,
        int siteId,
        string? siteCode,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteExecutionAsync(
        string outcome,
        string? issueNumber,
        int lineCount,
        decimal totalQuantity,
        string? refusalReason,
        string? checksJson,
        string? shortagesJson,
        string? linesJson,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteRunAsync(int documentsExamined, int issuesCreated, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailRunAsync(string reason, int documentsExamined, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PagedResult<IssueToShopFloorHistoryRow>> ListHistoryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<IssueToShopFloorHistoryRow>([], 0, 1, 0));

    public Task<IssueToShopFloorHistoryDetail?> GetDetailAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IssueToShopFloorHistoryDetail?>(null);

    public Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryAsync(
        string documentNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IssueToShopFloorHistoryRow>>([]);

    public Task<IssueToShopFloorHistorySummary> GetSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new IssueToShopFloorHistorySummary(0, 0, 0, 0, 0, 0));

    public Task<IssueToShopFloorDashboardSummary> GetDashboardSummaryAsync(
        IssueToShopFloorHistoryQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new IssueToShopFloorDashboardSummary(
            0,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            0,
            null,
            null,
            0,
            0,
            0,
            0,
            0m,
            0));

    public Task<IReadOnlyList<DailyIssueConversionStat>> GetDailyStatsAsync(
        int days,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(days, 1, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-(bounded - 1));

        var list = new List<DailyIssueConversionStat>(bounded);
        for (var date = from; date <= today; date = date.AddDays(1))
        {
            list.Add(new DailyIssueConversionStat(date, 0, 0, 0, 0, 0m));
        }

        return Task.FromResult<IReadOnlyList<DailyIssueConversionStat>>(list);
    }

    public async Task<IssueToShopFloorDashboardComposite> GetDashboardAsync(
        int days,
        int recentCount,
        string? issueSource,
        CancellationToken cancellationToken)
    {
        var dailyStats = await GetDailyStatsAsync(days, cancellationToken);
        var summary = await GetDashboardSummaryAsync(new IssueToShopFloorHistoryQuery { IssueSource = issueSource }, cancellationToken);

        return new IssueToShopFloorDashboardComposite(
            summary,
            dailyStats,
            null,
            null,
            []);
    }
}
