using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

/// <summary>
/// Dashboard and KPI metrics endpoints for SJO to Issue to Shop Floor automation.
/// </summary>
public static class IssueToShopFloorDashboardEndpoints
{
    public static IEndpointRouteBuilder MapIssueToShopFloorDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var primaryGroup = endpoints.MapGroup("/api/automation/issue-to-shop-floor/dashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        primaryGroup.MapGet(string.Empty, GetDashboardAsync).WithName("GetIssueToShopFloorDashboard");
        primaryGroup.MapGet("/summary", GetSummaryAsync).WithName("GetIssueToShopFloorDashboardSummary");
        primaryGroup.MapGet("/daily-summary", GetDailySummaryAsync).WithName("GetIssueToShopFloorDashboardDailySummary");
        primaryGroup.MapGet("/recent", GetRecentAsync).WithName("GetIssueToShopFloorDashboardRecent");

        // Legacy / alias path for SJO to Issue dashboard
        var legacyGroup = endpoints.MapGroup("/api/automation/sjo-to-issue/dashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        legacyGroup.MapGet(string.Empty, GetDashboardAsync).WithName("GetSjoToIssueDashboardLegacy");
        legacyGroup.MapGet("/summary", GetSummaryAsync).WithName("GetSjoToIssueDashboardSummaryLegacy");
        legacyGroup.MapGet("/daily-summary", GetDailySummaryAsync).WithName("GetSjoToIssueDashboardDailySummaryLegacy");
        legacyGroup.MapGet("/recent", GetRecentAsync).WithName("GetSjoToIssueDashboardRecentLegacy");

        return endpoints;
    }

    /// <summary>
    /// Complete dashboard payload in a single call: summary metrics, daily timeline series,
    /// last completed conversion, last execution details, and recent jobs.
    /// </summary>
    private static async Task<IResult> GetDashboardAsync(
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken,
        int days = 30,
        int recentCount = 10,
        string? issueSource = null)
    {
        if (days < 1 || days > 365)
        {
            return Results.Problem(
                title: "days must be between 1 and 365.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (recentCount < 1 || recentCount > 50)
        {
            return Results.Problem(
                title: "recentCount must be between 1 and 50.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var composite = await service.GetDashboardAsync(days, recentCount, issueSource?.Trim(), cancellationToken);

        return Results.Ok(ToResponse(composite));
    }

    /// <summary>
    /// Filterable KPI totals across SJO to Issue to Shop Floor conversions.
    /// </summary>
    private static async Task<IResult> GetSummaryAsync(
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken,
        string? search = null,
        string? issueSource = null,
        string? status = null,
        string? triggerSource = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        JobStatus? jobStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JobStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
            {
                return Results.Problem(
                    title: "Unknown status.",
                    detail: $"'{status}' is not a valid status. Expected one of: {string.Join(", ", Enum.GetNames<JobStatus>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            jobStatus = parsedStatus;
        }

        TriggerSource? trigger = null;
        if (!string.IsNullOrWhiteSpace(triggerSource))
        {
            if (!Enum.TryParse<TriggerSource>(triggerSource.Trim(), ignoreCase: true, out var parsedTrigger))
            {
                return Results.Problem(
                    title: "Unknown triggerSource.",
                    detail: $"'{triggerSource}' is not a valid trigger. Expected one of: {string.Join(", ", Enum.GetNames<TriggerSource>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            trigger = parsedTrigger;
        }

        var query = new IssueToShopFloorHistoryQuery
        {
            Search = search?.Trim(),
            IssueSource = issueSource?.Trim(),
            Status = jobStatus,
            TriggerSource = trigger,
            FromUtc = from,
            ToUtc = to,
            Page = 1,
            PageSize = 1,
        };

        var summary = await service.GetDashboardSummaryAsync(query, cancellationToken);

        return Results.Ok(ToResponse(summary));
    }

    /// <summary>
    /// Trailing calendar day conversion volume and status for dashboard charts.
    /// </summary>
    private static async Task<IResult> GetDailySummaryAsync(
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken,
        int days = 30)
    {
        if (days < 1 || days > 365)
        {
            return Results.Problem(
                title: "days must be between 1 and 365.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var stats = await service.GetDailyStatsAsync(days, cancellationToken);

        return Results.Ok(stats.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Top recent conversion jobs for the dashboard grid.
    /// </summary>
    private static async Task<IResult> GetRecentAsync(
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken,
        int count = 10,
        string? issueSource = null)
    {
        if (count < 1 || count > 50)
        {
            return Results.Problem(
                title: "count must be between 1 and 50.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = new IssueToShopFloorHistoryQuery
        {
            IssueSource = issueSource?.Trim(),
            Page = 1,
            PageSize = count,
        };

        var paged = await service.ListHistoryAsync(query, cancellationToken);
        var rows = paged.Items.Select(ToResponse).ToList();

        return Results.Ok(rows);
    }

    // ---- Mappings ----

    private static IssueToShopFloorDashboardResponse ToResponse(IssueToShopFloorDashboardComposite composite) =>
        new(
            ToResponse(composite.Summary),
            composite.DailyStats.Select(ToResponse).ToList(),
            composite.LastCompletedJob is not null ? ToResponse(composite.LastCompletedJob) : null,
            composite.LastExecution is not null ? ToResponse(composite.LastExecution) : null,
            composite.RecentJobs.Select(ToResponse).ToList());

    private static IssueToShopFloorDashboardSummaryResponse ToResponse(IssueToShopFloorDashboardSummary summary) =>
        new(
            summary.TotalJobs,
            summary.CountsByStatus,
            summary.CountsBySource,
            summary.SuccessCount,
            summary.SuccessRate,
            summary.AverageDurationMs,
            summary.TotalTriggerAttempts,
            summary.TriggerAttemptsWithoutEligibleDocument,
            summary.BusinessRefusalCount,
            summary.TechnicalFailureCount,
            summary.TotalQuantityIssued,
            summary.TotalLinesIssued);

    private static DailyIssueConversionStatResponse ToResponse(DailyIssueConversionStat stat) =>
        new(
            stat.Date.ToString("yyyy-MM-dd"),
            stat.IssuesCreated,
            stat.DocumentsConverted,
            stat.DocumentsRefused,
            stat.DocumentsFailed,
            stat.TotalQuantity);

    private static IssueToShopFloorHistoryRowResponse ToResponse(IssueToShopFloorHistoryRow row) =>
        new(
            row.JobId,
            row.RunId,
            row.IssueSource,
            row.DocumentNumber,
            row.Status,
            row.CurrentStage,
            row.IssueNumber,
            row.LineCount,
            row.TotalQuantity,
            row.Trigger,
            row.TriggeredBy,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.DurationMs,
            row.Reason);

    private static IssueToShopFloorHistoryDetailResponse ToResponse(IssueToShopFloorHistoryDetail detail)
    {
        var steps = detail.Steps.Select(s => new IssueToShopFloorStepResponse(
            s.Stage,
            s.OperationName,
            s.Status,
            s.StartedAtUtc,
            s.CompletedAtUtc,
            s.DurationMs,
            s.Remarks,
            s.ErpDocumentRef)).ToList();

        return new IssueToShopFloorHistoryDetailResponse(
            detail.JobId,
            detail.RunId,
            detail.IssueSource,
            detail.DocumentNumber,
            detail.Status,
            detail.CurrentStage,
            detail.IssueNumber,
            detail.StartedAtUtc,
            detail.CompletedAtUtc,
            detail.DurationMs,
            detail.Reason,
            steps,
            TryParseJson(detail.ChecksJson),
            TryParseJson(detail.ShortagesJson),
            TryParseJson(detail.LinesJson));
    }

    private static JsonElement? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
