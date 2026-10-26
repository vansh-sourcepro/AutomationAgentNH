using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;

/// <summary>
/// Configuration endpoints for Quotation to Sales Order automation:
/// reads and updates automation settings (schedule time, quotation numbers, master on/off toggle, run mode).
/// </summary>
public static class SoAutomationEndpoints
{
    public const string Route = "/api/automation/so-automation";

    public static IEndpointRouteBuilder MapSoAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(Route)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        group.MapGet("/", GetConfigAsync).WithName("GetSoAutomationConfig");
        group.MapPut("/", UpdateConfigAsync).WithName("UpdateSoAutomationConfig");
        group.MapPut("/enabled", SetEnabledAsync).WithName("SetSoAutomationEnabled");

        return endpoints;
    }

    private static async Task<IResult> GetConfigAsync(
        IQuotationSoAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        var config = await configs.GetAsync(cancellationToken);
        return Results.Ok(ToResponse(config));
    }

    private static async Task<IResult> UpdateConfigAsync(
        UpdateSoAutomationRequest? request,
        IQuotationSoAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem("A configuration body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        SoAutomationRunMode? mode = null;
        if (request.RunMode is not null)
        {
            if (!Enum.TryParse(request.RunMode.Trim(), ignoreCase: true, out SoAutomationRunMode parsed))
            {
                return Results.Problem(
                    $"'{request.RunMode}' is not a valid run mode. Expected one of: {string.Join(", ", Enum.GetNames<SoAutomationRunMode>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            mode = parsed;
        }

        if (!TryParseTime(request.ScheduleTime, out var scheduleTime, out var timeError))
        {
            return timeError!;
        }

        var update = new QuotationSoAutomationConfigUpdate
        {
            RunMode = mode,
            ScheduleTime = scheduleTime,
            ClearScheduleTime = request.ClearScheduleTime,
            QuotationNumbers = request.QuotationNumbers,
            ClearQuotationNumbers = request.ClearQuotationNumbers,
            CustomerCode = request.CustomerCode,
            ClearCustomerCode = request.ClearCustomerCode,
            SiteCode = request.SiteCode,
            ClearSiteCode = request.ClearSiteCode,
            IsActive = request.IsActive,
            DryRun = request.DryRun,
        };

        var saved = await configs.UpsertAsync(update, ActorOf(user), cancellationToken);
        return Results.Ok(ToResponse(saved));
    }

    private static async Task<IResult> SetEnabledAsync(
        SetSoAutomationEnabledRequest? request,
        IQuotationSoAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem("An { \"enabled\": true|false } body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var saved = await configs.SetActiveAsync(request.Enabled, ActorOf(user), cancellationToken);
        return Results.Ok(ToResponse(saved));
    }

    public static SoAutomationConfigResponse ToResponse(QuotationSoAutomationConfig config) => new(
        config.IsActive,
        config.RunMode.ToString(),
        config.ScheduleTime,
        config.QuotationNumbers,
        config.CustomerCode,
        config.SiteCode,
        config.DryRun,
        config.LastScheduledRunDate,
        config.LastTriggeredAtUtc,
        config.LastRunStatus,
        config.LastRunMessage,
        config.UpdatedAtUtc,
        config.UpdatedBy);

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
            $"'{value}' is not a valid time. Expected \"HH:mm\" or \"HH:mm:ss\".",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }

    private static string? ActorOf(ClaimsPrincipal user) =>
        user.FindFirstValue("userFullName")
        ?? user.FindFirstValue("userName")
        ?? user.FindFirstValue("id")
        ?? user.Identity?.Name;
}
