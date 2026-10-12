using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

/// <summary>
/// Configuration and scheduling API for Issue to Shop Floor automation.
/// </summary>
public static class IssueToShopFloorConfigEndpoints
{
    public static IEndpointRouteBuilder MapIssueToShopFloorConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var primaryGroup = endpoints.MapGroup("/api/automation/issue-to-shop-floor/config")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        MapRoutes(primaryGroup, "IssueToShopFloor");

        var legacyGroup = endpoints.MapGroup("/api/automation/sjo-to-issue/config")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        MapRoutes(legacyGroup, "SjoToIssueLegacy");

        return endpoints;
    }

    private static void MapRoutes(RouteGroupBuilder group, string prefix)
    {
        group.MapGet(string.Empty, ListAsync).WithName($"List{prefix}Configs");
        group.MapPut("/enabled", SetEnabledAsync).WithName($"Set{prefix}ConfigEnabled");
        group.MapPut("/enable", SetEnabledAsync).WithName($"Set{prefix}ConfigEnableAlias");
        group.MapGet("/{issueSource}", GetAsync).WithName($"Get{prefix}Config");
        group.MapPut("/{issueSource}", UpdateAsync).WithName($"Update{prefix}Config");
    }

    private static async Task<IResult> ListAsync(
        IIssueToShopFloorAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        var all = await configs.GetAllAsync(cancellationToken);
        return Results.Ok(all.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(
        string issueSource,
        IIssueToShopFloorAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        if (!TryParseSource(issueSource, out var source, out var error))
        {
            return error!;
        }

        var config = await configs.GetAsync(source, cancellationToken);
        return Results.Ok(ToResponse(config));
    }

    private static async Task<IResult> SetEnabledAsync(
        [FromBody] SetIssueToShopFloorEnabledRequest? request,
        IIssueToShopFloorAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem("An { \"enabled\": true|false } body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!configs.IsEnabled)
        {
            return Results.Problem(
                "Issue to Shop Floor automation cannot be turned on or off without an automation database on this installation.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var saved = await configs.SetAllActiveAsync(request.Enabled, ActorOf(user), cancellationToken);
        return Results.Ok(saved.Select(ToResponse).ToList());
    }

    private static async Task<IResult> UpdateAsync(
        string issueSource,
        [FromBody] UpdateIssueToShopFloorConfigRequest? request,
        IIssueToShopFloorAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!TryParseSource(issueSource, out var source, out var sourceError))
        {
            return sourceError!;
        }

        if (request is null)
        {
            return Results.Problem("A settings body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!configs.IsEnabled)
        {
            return Results.Problem(
                "Automation settings cannot be saved without an automation database on this installation.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        PoAutomationRunMode? mode = null;
        if (request.RunMode is not null)
        {
            if (!Enum.TryParse(request.RunMode.Trim(), ignoreCase: true, out PoAutomationRunMode parsed))
            {
                return Results.Problem(
                    $"'{request.RunMode}' is not a run mode. Expected one of: {string.Join(", ", Enum.GetNames<PoAutomationRunMode>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            mode = parsed;
        }

        if (!TryParseTime(request.ScheduleTime, out var scheduleTime, out var timeError))
        {
            return timeError!;
        }

        var update = new IssueToShopFloorAutomationConfigUpdate
        {
            RunMode = mode,
            ScheduleTime = scheduleTime,
            ClearScheduleTime = request.ClearScheduleTime,
            Sites = request.Sites,
            DocumentNumbers = request.DocumentNumbers,
            ClearDocumentNumbers = request.ClearDocumentNumbers,
            IsActive = request.IsActive,
            DryRun = request.DryRun,
            MaxDocumentsPerRun = request.MaxDocumentsPerRun,
            ClearMaxDocumentsPerRun = request.ClearMaxDocumentsPerRun,
        };

        try
        {
            var saved = await configs.UpsertAsync(source, update, ActorOf(user), cancellationToken);
            return Results.Ok(ToResponse(saved));
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IssueToShopFloorConfigResponse ToResponse(IssueToShopFloorAutomationConfig config) => new(
        config.Source.ToString(),
        config.RunMode.ToString(),
        config.ScheduleTime,
        config.Sites,
        config.DocumentNumbers,
        config.IsActive,
        config.DryRun,
        config.MaxDocumentsPerRun,
        config.LastScheduledRunDate,
        config.LastTriggeredAtUtc,
        config.LastRunStatus,
        config.LastRunReference,
        config.UpdatedAtUtc,
        config.UpdatedBy);

    private static bool TryParseSource(string? value, out IssueToShopFloorSource source, out IResult? error)
    {
        source = IssueToShopFloorSource.Sjo;
        error = null;

        var text = value?.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            error = Results.Problem(
                $"issueSource is required. Expected one of: {string.Join(", ", Enum.GetNames<IssueToShopFloorSource>())}.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        switch (text.ToUpperInvariant())
        {
            case "SJO":
            case "SJOWISE":
            case "SJ":
            case "S":
                source = IssueToShopFloorSource.Sjo;
                return true;

            case "WORKORDER":
            case "WORKORDERWISE":
            case "WO":
            case "W":
                source = IssueToShopFloorSource.WorkOrder;
                return true;

            case "SALESOAF":
            case "SALESOAFWISE":
            case "OAF":
            case "OF":
            case "OA":
            case "O":
                source = IssueToShopFloorSource.SalesOaf;
                return true;

            default:
                error = Results.Problem(
                    $"'{value}' is not an issue source. Expected one of: {string.Join(", ", Enum.GetNames<IssueToShopFloorSource>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
                return false;
        }
    }

    private static bool TryParseTime(string? value, out TimeOnly? time, out IResult? error)
    {
        time = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "HH:mm:ss"], out var parsed))
        {
            time = parsed;
            return true;
        }

        error = Results.Problem(
            $"'{value}' is not a time. Expected \"HH:mm\" or \"HH:mm:ss\".",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }

    private static string? ActorOf(ClaimsPrincipal user) =>
        user.FindFirstValue("userFullName")
        ?? user.FindFirstValue("userName")
        ?? user.FindFirstValue("id")
        ?? user.Identity?.Name;
}
