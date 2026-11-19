using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

/// <summary>
/// History query endpoints for SJO to Issue to Shop Floor executions.
/// </summary>
public static class IssueToShopFloorHistoryEndpoints
{
    public static IEndpointRouteBuilder MapIssueToShopFloorHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var primaryGroup = endpoints.MapGroup("/api/automation/issue-to-shop-floor/history")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        primaryGroup.MapGet(string.Empty, ListHistoryAsync).WithName("ListIssueToShopFloorHistory");
        primaryGroup.MapGet("/summary", GetSummaryAsync).WithName("GetIssueToShopFloorHistorySummary");
        primaryGroup.MapGet("/{jobId:guid}", GetDetailAsync).WithName("GetIssueToShopFloorHistoryDetail");
        primaryGroup.MapGet("/document/{**documentNumber}", GetDocumentHistoryAsync).WithName("GetIssueToShopFloorDocumentHistory");

        // Legacy / alias path for SJO to Issue
        var legacyGroup = endpoints.MapGroup("/api/automation/sjo-to-issue/history")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        legacyGroup.MapGet(string.Empty, ListHistoryAsync).WithName("ListSjoToIssueHistoryLegacy");
        legacyGroup.MapGet("/summary", GetSummaryAsync).WithName("GetSjoToIssueHistorySummaryLegacy");
        legacyGroup.MapGet("/{jobId:guid}", GetDetailAsync).WithName("GetSjoToIssueHistoryDetailLegacy");
        legacyGroup.MapGet("/document/{**documentNumber}", GetDocumentHistoryAsync).WithName("GetSjoToIssueDocumentHistoryLegacy");

        return endpoints;
    }

    private static async Task<IResult> ListHistoryAsync(
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken,
        string? search = null,
        string? issueSource = null,
        string? status = null,
        string? triggerSource = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1)
        {
            return Results.Problem(title: "page must be at least 1.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (pageSize < 1 || pageSize > 200)
        {
            return Results.Problem(title: "pageSize must be between 1 and 200.", statusCode: StatusCodes.Status400BadRequest);
        }

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
            Page = page,
            PageSize = pageSize,
        };

        var result = await service.ListHistoryAsync(query, cancellationToken);

        var rows = result.Items.Select(r => new IssueToShopFloorHistoryRowResponse(
            r.JobId,
            r.RunId,
            r.IssueSource,
            r.DocumentNumber,
            r.Status,
            r.CurrentStage,
            r.IssueNumber,
            r.LineCount,
            r.TotalQuantity,
            r.Trigger,
            r.TriggeredBy,
            r.StartedAtUtc,
            r.CompletedAtUtc,
            r.DurationMs,
            r.Reason)).ToList();

        return Results.Ok(new PagedResult<IssueToShopFloorHistoryRowResponse>(
            rows,
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

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
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status.Trim(), ignoreCase: true, out var parsedStatus))
        {
            jobStatus = parsedStatus;
        }

        TriggerSource? trigger = null;
        if (!string.IsNullOrWhiteSpace(triggerSource) && Enum.TryParse<TriggerSource>(triggerSource.Trim(), ignoreCase: true, out var parsedTrigger))
        {
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

        var summary = await service.GetSummaryAsync(query, cancellationToken);

        return Results.Ok(new IssueToShopFloorHistorySummaryResponse(
            summary.TotalAttempts,
            summary.IssuesCreated,
            summary.ShortagesOrRefusals,
            summary.SystemFailures,
            summary.SuccessRatePercentage,
            summary.AverageDurationMs));
    }

    private static async Task<IResult> GetDetailAsync(
        Guid jobId,
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken)
    {
        var detail = await service.GetDetailAsync(jobId, cancellationToken);
        if (detail is null)
        {
            return Results.NotFound(new { Message = $"No execution history found for job ID {jobId}." });
        }

        var steps = detail.Steps.Select(s => new IssueToShopFloorStepResponse(
            s.Stage,
            s.OperationName,
            s.Status,
            s.StartedAtUtc,
            s.CompletedAtUtc,
            s.DurationMs,
            s.Remarks,
            s.ErpDocumentRef)).ToList();

        JsonElement? checks = TryParseJson(detail.ChecksJson);
        JsonElement? shortages = TryParseJson(detail.ShortagesJson);
        JsonElement? lines = TryParseJson(detail.LinesJson);

        return Results.Ok(new IssueToShopFloorHistoryDetailResponse(
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
            checks,
            shortages,
            lines));
    }

    private static async Task<IResult> GetDocumentHistoryAsync(
        string documentNumber,
        [FromServices] IIssueToShopFloorHistoryService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            return Results.Problem(title: "documentNumber is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var doc = System.Net.WebUtility.UrlDecode(documentNumber.Trim());
        var rows = await service.GetDocumentHistoryAsync(doc, cancellationToken);

        var result = rows.Select(r => new IssueToShopFloorHistoryRowResponse(
            r.JobId,
            r.RunId,
            r.IssueSource,
            r.DocumentNumber,
            r.Status,
            r.CurrentStage,
            r.IssueNumber,
            r.LineCount,
            r.TotalQuantity,
            r.Trigger,
            r.TriggeredBy,
            r.StartedAtUtc,
            r.CompletedAtUtc,
            r.DurationMs,
            r.Reason)).ToList();

        return Results.Ok(result);
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
