using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
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
        IQuotationToSoService service,
        IQuotationToSoTracker tracker,
        IClock clock,
        ILogger<QuotationToSoService> logger,
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

        var clearQuotations = request.ClearQuotationNumbers
            || (request.QuotationNumbers is not null && (string.IsNullOrWhiteSpace(request.QuotationNumbers) || string.Equals(request.QuotationNumbers.Trim(), "ALL", StringComparison.OrdinalIgnoreCase)));

        var clearCustomer = request.ClearCustomerCode
            || (request.CustomerCode is not null && (string.IsNullOrWhiteSpace(request.CustomerCode) || string.Equals(request.CustomerCode.Trim(), "ALL", StringComparison.OrdinalIgnoreCase)));

        var clearSite = request.ClearSiteCode
            || (request.SiteCode is not null && (string.IsNullOrWhiteSpace(request.SiteCode) || string.Equals(request.SiteCode.Trim(), "ALL", StringComparison.OrdinalIgnoreCase)));

        var quotationNumbersToSave = clearQuotations
            ? null
            : request.QuotationNumbers?.Trim();

        var customerCodeToSave = clearCustomer
            ? null
            : request.CustomerCode?.Trim();

        var siteCodeToSave = clearSite
            ? null
            : request.SiteCode?.Trim();

        var update = new QuotationSoAutomationConfigUpdate
        {
            RunMode = mode,
            ScheduleTime = scheduleTime,
            ClearScheduleTime = request.ClearScheduleTime,
            QuotationNumbers = quotationNumbersToSave,
            ClearQuotationNumbers = clearQuotations,
            CustomerCode = customerCodeToSave,
            ClearCustomerCode = clearCustomer,
            SiteCode = siteCodeToSave,
            ClearSiteCode = clearSite,
            IsActive = request.IsActive,
            DryRun = request.DryRun,
        };

        var saved = await configs.UpsertAsync(update, ActorOf(user), cancellationToken);

        // If the configuration is active and either RunMode is Api, ClearScheduleTime is true, or no ScheduleTime is set,
        // automatically execute the conversion immediately in the same call so the user does not have to call a separate API.
        string? createdSoNumber = null;
        var shouldExecuteImmediately = saved.IsActive
            && (saved.RunMode == SoAutomationRunMode.Api
                || request.ClearScheduleTime
                || (saved.ScheduleTime is null && saved.RunMode != SoAutomationRunMode.Timer));

        if (shouldExecuteImmediately)
        {
            logger.LogInformation("Configuration set to immediate API execution (RunMode: {RunMode}). Auto-triggering conversion...", saved.RunMode);

            saved.RecordRunStarted(clock.UtcNow, null);
            await configs.SaveAsync(saved, cancellationToken);

            var quotationList = string.IsNullOrWhiteSpace(saved.QuotationNumbers) || string.Equals(saved.QuotationNumbers.Trim(), "ALL", StringComparison.OrdinalIgnoreCase)
                ? null
                : saved.QuotationNumbers
                    .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var triggeredBy = ActorOf(user) ?? "API";
            await tracker.StartRunAsync(
                TriggerSource.Api,
                triggeredBy,
                triggerReference: null,
                saved.QuotationNumbers,
                cancellationToken);

            try
            {
                if (quotationList is { Count: 1 })
                {
                    var qtnNo = quotationList[0];
                    var itemResult = await service.ConvertQuotationByNumberAsync(
                        qtnNo,
                        saved.CustomerCode,
                        saved.SiteCode,
                        cancellationToken);

                    var status = itemResult.Success ? "Success" : "Failed";
                    var message = itemResult.Success
                        ? $"Quotation '{itemResult.QuotationNumber}' successfully converted to Sales Order '{itemResult.SalesOrderNumber}'."
                        : itemResult.ErrorMessage ?? $"Failed to convert quotation '{qtnNo}'.";

                    createdSoNumber = itemResult.SalesOrderNumber;
                    saved.RecordRunCompleted(status, message, clock.UtcNow);
                    await configs.SaveAsync(saved, cancellationToken);

                    await tracker.CompleteRunAsync(1, cancellationToken);
                }
                else if (quotationList is { Count: > 1 })
                {
                    var succeeded = 0;
                    var failed = 0;
                    var soNumbers = new List<string>();

                    foreach (var qtn in quotationList)
                    {
                        var item = await service.ConvertQuotationByNumberAsync(
                            qtn,
                            saved.CustomerCode,
                            saved.SiteCode,
                            cancellationToken);

                        if (item.Success)
                        {
                            succeeded++;
                            if (!string.IsNullOrWhiteSpace(item.SalesOrderNumber))
                            {
                                soNumbers.Add(item.SalesOrderNumber);
                            }
                        }
                        else
                        {
                            failed++;
                        }
                    }

                    var status = failed == 0 ? "Success" : succeeded > 0 ? "PartialSuccess" : "Failed";
                    var message = $"Processed {quotationList.Count} quotation(s): {succeeded} converted successfully, {failed} failed.";
                    if (soNumbers.Count > 0)
                    {
                        createdSoNumber = string.Join(", ", soNumbers);
                    }

                    saved.RecordRunCompleted(status, message, clock.UtcNow);
                    await configs.SaveAsync(saved, cancellationToken);

                    await tracker.CompleteRunAsync(quotationList.Count, cancellationToken);
                }
                else
                {
                    var sweepResult = await service.ConvertAllEligibleQuotationsAsync(
                        saved.CustomerCode,
                        saved.SiteCode,
                        cancellationToken);

                    var status = sweepResult.Failed == 0 ? "Success" : sweepResult.Succeeded > 0 ? "PartialSuccess" : "Failed";
                    var message = sweepResult.TotalFound == 0
                        ? "No eligible open authorised quotations found for conversion."
                        : $"Processed {sweepResult.TotalFound} quotation(s): {sweepResult.Succeeded} converted successfully, {sweepResult.Failed} failed.";

                    saved.RecordRunCompleted(status, message, clock.UtcNow);
                    await configs.SaveAsync(saved, cancellationToken);

                    await tracker.CompleteRunAsync(sweepResult.TotalFound, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic conversion execution failed during configuration update.");
                saved.RecordRunCompleted("Failed", ex.Message, clock.UtcNow);
                await configs.SaveAsync(saved, cancellationToken);

                await tracker.FailRunAsync(ex.Message, 0, cancellationToken);
            }
        }

        return Results.Ok(ToResponse(saved, createdSoNumber));
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

    public static SoAutomationConfigResponse ToResponse(QuotationSoAutomationConfig config, string? salesOrderNumber = null) => new(
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
        config.UpdatedBy,
        salesOrderNumber);

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
