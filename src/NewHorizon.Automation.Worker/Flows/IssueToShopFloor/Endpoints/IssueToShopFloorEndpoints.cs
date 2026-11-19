using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

/// <summary>
/// Issue to Shop Floor automation API supporting single document, bulk discovery, timer-based scheduling, and configuration modes.
/// </summary>
public static class IssueToShopFloorEndpoints
{
    public static IEndpointRouteBuilder MapIssueToShopFloorApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Machine or ERP browser callers: shared API key or valid ERP bearer token.
        endpoints.MapPost("/api/automation/issue-to-shop-floor", ConvertAsync)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .WithName("CreateIssueToShopFloor");

        // The path the flow shipped under when it was SJO-only. Same handler, kept so a caller built
        // against it keeps working.
        endpoints.MapPost("/api/automation/sjo-to-issue", ConvertAsync)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .WithName("CreateIssueToShopFloorLegacy");

        return endpoints;
    }

    private static async Task<IResult> ConvertAsync(
        IssueToShopFloorRequest? request,
        IIssueToShopFloorService service,
        IIssueToShopFloorTracker tracker,
        IIssueToShopFloorGate gate,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        [FromQuery] string? trigger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem(
                title: "A request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!request.TryGetSource(out var source))
        {
            return Results.Problem(
                title: $"issueType '{request.IssueType}' is not understood. Use one of: {IssueToShopFloorRequest.AcceptedIssueTypes}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!request.TryGetMode(out var explicitMode, out var modeError))
        {
            return Results.Problem(
                title: "Invalid mode.",
                detail: modeError,
                statusCode: StatusCodes.Status400BadRequest);
        }

        IssueToShopFloorExecutionMode mode;
        if (explicitMode is { } m)
        {
            mode = m;
        }
        else
        {
            // Backward compatibility when mode is omitted:
            if (!string.IsNullOrWhiteSpace(request.Number))
            {
                mode = IssueToShopFloorExecutionMode.NumberBased;
            }
            else if (!string.IsNullOrWhiteSpace(request.Time))
            {
                mode = IssueToShopFloorExecutionMode.TimerBased;
            }
            else
            {
                // Both mode and documentNumber are omitted
                return Results.Problem(
                    title: "documentNumber is required, e.g. \"26-27/SJ/NF1/000123\" with issueType \"sjo\".",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // Mode-specific validation
        TimeOnly? scheduleTime = null;
        if (mode is IssueToShopFloorExecutionMode.TimerBased or IssueToShopFloorExecutionMode.Both)
        {
            if (string.IsNullOrWhiteSpace(request.Time))
            {
                return Results.Problem(
                    title: "Missing time parameter.",
                    detail: $"The 'time' parameter is required for {GetModeName(mode)} mode (expected format: \"HH:mm\" or \"HH:mm:ss\").",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!TimeOnly.TryParseExact(request.Time.Trim(), ["HH:mm", "HH:mm:ss"], out var parsed))
            {
                return Results.Problem(
                    title: "Invalid time parameter.",
                    detail: $"'{request.Time}' is not a valid time. Expected format: \"HH:mm\" or \"HH:mm:ss\".",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            scheduleTime = parsed;
        }

        if (mode == IssueToShopFloorExecutionMode.Both && string.IsNullOrWhiteSpace(request.Number))
        {
            return Results.Problem(
                title: "Missing documentNumber parameter.",
                detail: "The 'documentNumber' parameter is required for both mode.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        TriggerSource triggerSource = TriggerSource.Api;
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            if (!Enum.TryParse(trigger.Trim(), ignoreCase: true, out triggerSource)
                || triggerSource is not (TriggerSource.Manual or TriggerSource.Timer or TriggerSource.Api))
            {
                return Results.Problem(
                    title: "Unknown trigger.",
                    detail: $"'{trigger}' is not a conversion trigger. Expected 'manual', 'timer' or 'api'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var domainSource = (IssueToShopFloorSource)(int)source;

        switch (mode)
        {
            case IssueToShopFloorExecutionMode.None:
                return await HandleNoneModeAsync(source, domainSource, request, automationConfigs, clock, user, cancellationToken);

            case IssueToShopFloorExecutionMode.TimerBased:
                return await HandleTimerModeAsync(source, domainSource, scheduleTime!.Value, request, automationConfigs, clock, user, cancellationToken);

            case IssueToShopFloorExecutionMode.Both:
                return await HandleBothModeAsync(source, domainSource, scheduleTime!.Value, request, automationConfigs, clock, user, cancellationToken);

            case IssueToShopFloorExecutionMode.NumberBased:
            default:
                if (!string.IsNullOrWhiteSpace(request.Number))
                {
                    return await ConvertSingleAsync(
                        source,
                        domainSource,
                        request.Number,
                        request.DryRun,
                        service,
                        tracker,
                        gate,
                        automationConfigs,
                        clock,
                        user,
                        triggerSource,
                        cancellationToken);
                }

                return await ConvertBulkAsync(
                    source,
                    domainSource,
                    request.DryRun,
                    service,
                    tracker,
                    gate,
                    automationConfigs,
                    clock,
                    user,
                    triggerSource,
                    cancellationToken);
        }
    }

    private static string GetModeName(IssueToShopFloorExecutionMode mode) => mode switch
    {
        IssueToShopFloorExecutionMode.TimerBased => "timerBased",
        IssueToShopFloorExecutionMode.NumberBased => "numberBased",
        IssueToShopFloorExecutionMode.Both => "both",
        IssueToShopFloorExecutionMode.None => "none",
        _ => mode.ToString(),
    };

    private static async Task<IResult> HandleNoneModeAsync(
        IssueSource source,
        IssueToShopFloorSource domainSource,
        IssueToShopFloorRequest request,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (automationConfigs.IsEnabled)
        {
            var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
            config.Update(new IssueToShopFloorAutomationConfigUpdate
            {
                RunMode = PoAutomationRunMode.Disabled,
                IsActive = false,
                ClearScheduleTime = true,
            }, clock.UtcNow, ActorOf(user) ?? "Api");
            await automationConfigs.SaveAsync(config, cancellationToken);
        }

        return Results.Ok(new IssueToShopFloorModeResponse(
            IssueType: source.ToString(),
            Mode: "none",
            Status: "Disabled",
            Message: $"{source.Noun()} automation execution has been disabled.",
            ConfiguredTime: null,
            DocumentNumber: null,
            DryRun: request.DryRun));
    }

    private static async Task<IResult> HandleTimerModeAsync(
        IssueSource source,
        IssueToShopFloorSource domainSource,
        TimeOnly scheduleTime,
        IssueToShopFloorRequest request,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!automationConfigs.IsEnabled)
        {
            return Results.Problem(
                title: "Automation database required.",
                detail: "Timer-based execution cannot be scheduled without an automation database on this installation.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
        config.Update(new IssueToShopFloorAutomationConfigUpdate
        {
            RunMode = PoAutomationRunMode.Timer,
            ScheduleTime = scheduleTime,
            ClearDocumentNumbers = true,
            IsActive = true,
            DryRun = request.DryRun,
        }, clock.UtcNow, ActorOf(user) ?? "Api");
        await automationConfigs.SaveAsync(config, cancellationToken);

        return Results.Ok(new IssueToShopFloorModeResponse(
            IssueType: source.ToString(),
            Mode: "timerBased",
            Status: "Scheduled",
            Message: $"{source.Noun()} automation scheduled daily at {scheduleTime:HH:mm:ss} via background scheduler.",
            ConfiguredTime: scheduleTime.ToString("HH:mm:ss"),
            DocumentNumber: null,
            DryRun: request.DryRun));
    }

    private static async Task<IResult> HandleBothModeAsync(
        IssueSource source,
        IssueToShopFloorSource domainSource,
        TimeOnly scheduleTime,
        IssueToShopFloorRequest request,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!automationConfigs.IsEnabled)
        {
            return Results.Problem(
                title: "Automation database required.",
                detail: "Scheduled execution cannot be saved without an automation database on this installation.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
        config.Update(new IssueToShopFloorAutomationConfigUpdate
        {
            RunMode = PoAutomationRunMode.Both,
            ScheduleTime = scheduleTime,
            DocumentNumbers = request.Number,
            IsActive = true,
            DryRun = request.DryRun,
        }, clock.UtcNow, ActorOf(user) ?? "Api");
        await automationConfigs.SaveAsync(config, cancellationToken);

        return Results.Ok(new IssueToShopFloorModeResponse(
            IssueType: source.ToString(),
            Mode: "both",
            Status: "Scheduled",
            Message: $"{source.Noun()} conversion for document {request.Number} scheduled daily at {scheduleTime:HH:mm:ss} via background scheduler.",
            ConfiguredTime: scheduleTime.ToString("HH:mm:ss"),
            DocumentNumber: request.Number,
            DryRun: request.DryRun));
    }

    private static async Task<IResult> ConvertSingleAsync(
        IssueSource source,
        IssueToShopFloorSource domainSource,
        string documentNumber,
        bool dryRun,
        IIssueToShopFloorService service,
        IIssueToShopFloorTracker tracker,
        IIssueToShopFloorGate gate,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        TriggerSource triggerSource,
        CancellationToken cancellationToken)
    {
        // If not manual override, verify that this source's automation is turned on
        if (triggerSource != TriggerSource.Manual)
        {
            var offReason = await gate.OffReasonAsync([domainSource], cancellationToken);
            if (offReason is not null)
            {
                return Results.Problem(
                    title: "Issue to Shop Floor automation is turned off.",
                    detail: offReason,
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        var triggeredBy = triggerSource switch
        {
            TriggerSource.Manual => ActorOf(user) ?? "Manual",
            TriggerSource.Timer => "Scheduler",
            _ => "Api",
        };

        bool shouldTrackRun = !dryRun && tracker.IsEnabled;
        if (shouldTrackRun)
        {
            await tracker.StartRunAsync(
                new StartIssueRunRequest(
                    triggerSource,
                    source.ToString(),
                    TriggeredBy: triggeredBy,
                    TriggerReference: documentNumber,
                    RequestedSites: null),
                cancellationToken);
        }

        try
        {
            var result = await service.ConvertAsync(source, documentNumber, dryRun, cancellationToken);

            if (shouldTrackRun)
            {
                await tracker.CompleteRunAsync(
                    documentsExamined: 1,
                    issuesCreated: result.Created ? 1 : 0,
                    cancellationToken);
            }

            if (automationConfigs.IsEnabled)
            {
                try
                {
                    var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
                    var status = result.Created ? RunStatus.Completed : (result.Ready ? RunStatus.Completed : RunStatus.Failed);
                    config.MarkManualRun(clock.UtcNow, status, tracker.RunId);
                    await automationConfigs.SaveAsync(config, cancellationToken);
                }
                catch
                {
                    // Swallowed on purpose: status update shouldn't fail conversion response
                }
            }

            return result.Ready ? Results.Ok(result) : Results.BadRequest(result);
        }
        catch (ErpException erp)
        {
            if (shouldTrackRun)
            {
                await tracker.FailRunAsync(erp.Message, documentsExamined: 1, cancellationToken);
            }

            if (automationConfigs.IsEnabled)
            {
                try
                {
                    var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
                    config.MarkManualRun(clock.UtcNow, RunStatus.Failed, tracker.RunId);
                    await automationConfigs.SaveAsync(config, cancellationToken);
                }
                catch
                {
                    // Swallowed on purpose
                }
            }

            return Results.Problem(
                title: erp.LaymanMessage,
                detail: erp.TechnicalMessage,
                statusCode: erp.IsTransient
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            if (shouldTrackRun)
            {
                await tracker.FailRunAsync(ex.Message, documentsExamined: 1, cancellationToken);
            }

            if (automationConfigs.IsEnabled)
            {
                try
                {
                    var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
                    config.MarkManualRun(clock.UtcNow, RunStatus.Failed, tracker.RunId);
                    await automationConfigs.SaveAsync(config, cancellationToken);
                }
                catch
                {
                    // Swallowed on purpose
                }
            }

            throw;
        }
    }

    private static async Task<IResult> ConvertBulkAsync(
        IssueSource source,
        IssueToShopFloorSource domainSource,
        bool dryRun,
        IIssueToShopFloorService service,
        IIssueToShopFloorTracker tracker,
        IIssueToShopFloorGate gate,
        IIssueToShopFloorAutomationConfigRepository automationConfigs,
        IClock clock,
        ClaimsPrincipal user,
        TriggerSource triggerSource,
        CancellationToken cancellationToken)
    {
        // If not manual override, verify that this source's automation is turned on
        if (triggerSource != TriggerSource.Manual)
        {
            var offReason = await gate.OffReasonAsync([domainSource], cancellationToken);
            if (offReason is not null)
            {
                return Results.Problem(
                    title: "Issue to Shop Floor automation is turned off.",
                    detail: offReason,
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        var triggeredBy = triggerSource switch
        {
            TriggerSource.Manual => ActorOf(user) ?? "Manual",
            TriggerSource.Timer => "Scheduler",
            _ => "Api",
        };

        IReadOnlyList<string> discoveredDocuments;
        try
        {
            discoveredDocuments = await service.DiscoverEligibleDocumentNumbersAsync(source, cancellationToken);
        }
        catch (ErpException erp)
        {
            return Results.Problem(
                title: erp.LaymanMessage,
                detail: erp.TechnicalMessage,
                statusCode: erp.IsTransient
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Failed to discover eligible documents.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        int? maxCeiling = null;
        if (automationConfigs.IsEnabled)
        {
            try
            {
                var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
                maxCeiling = config.MaxDocumentsPerRun;
            }
            catch
            {
                // Swallowed
            }
        }

        var targets = discoveredDocuments.AsEnumerable();
        if (maxCeiling is { } ceiling && ceiling > 0)
        {
            targets = targets.Take(ceiling);
        }

        var targetList = targets.ToList();

        if (targetList.Count == 0)
        {
            return Results.Ok(new IssueToShopFloorBulkResponse(
                IssueType: source.ToString(),
                Mode: "numberBased",
                TotalDiscovered: 0,
                TotalProcessed: 0,
                CreatedCount: 0,
                RefusedCount: 0,
                DryRun: dryRun,
                Results: Array.Empty<IssueToShopFloorResult>()));
        }

        bool shouldTrackRun = !dryRun && tracker.IsEnabled;
        if (shouldTrackRun)
        {
            await tracker.StartRunAsync(
                new StartIssueRunRequest(
                    triggerSource,
                    source.ToString(),
                    TriggeredBy: triggeredBy,
                    TriggerReference: $"Bulk ({targetList.Count} documents)",
                    RequestedSites: null),
                cancellationToken);
        }

        var results = new List<IssueToShopFloorResult>();
        var createdCount = 0;
        var refusedCount = 0;

        foreach (var docNumber in targetList)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var result = await service.ConvertAsync(source, docNumber, dryRun, cancellationToken);
                results.Add(result);
                if (result.Created)
                {
                    createdCount++;
                }
                else
                {
                    refusedCount++;
                }
            }
            catch (ErpException erp)
            {
                refusedCount++;
                results.Add(new IssueToShopFloorResult(
                    source,
                    docNumber,
                    Created: false,
                    DryRun: dryRun,
                    IssueNumber: null,
                    Reason: erp.LaymanMessage,
                    Checks: [new IssueCheck("ErpCall", false, erp.TechnicalMessage ?? erp.Message)],
                    Lines: Array.Empty<IssueLine>(),
                    Shortages: Array.Empty<IssueShortage>()));
            }
            catch (Exception ex)
            {
                refusedCount++;
                results.Add(new IssueToShopFloorResult(
                    source,
                    docNumber,
                    Created: false,
                    DryRun: dryRun,
                    IssueNumber: null,
                    Reason: ex.Message,
                    Checks: [new IssueCheck("Execution", false, ex.Message)],
                    Lines: Array.Empty<IssueLine>(),
                    Shortages: Array.Empty<IssueShortage>()));
            }
        }

        if (shouldTrackRun)
        {
            await tracker.CompleteRunAsync(
                documentsExamined: results.Count,
                issuesCreated: createdCount,
                cancellationToken);
        }

        if (automationConfigs.IsEnabled)
        {
            try
            {
                var config = await automationConfigs.GetAsync(domainSource, cancellationToken);
                var status = refusedCount == 0 ? RunStatus.Completed : (createdCount > 0 ? RunStatus.Completed : RunStatus.Failed);
                config.MarkManualRun(clock.UtcNow, status, tracker.RunId);
                await automationConfigs.SaveAsync(config, cancellationToken);
            }
            catch
            {
                // Swallowed
            }
        }

        return Results.Ok(new IssueToShopFloorBulkResponse(
            IssueType: source.ToString(),
            Mode: "numberBased",
            TotalDiscovered: discoveredDocuments.Count,
            TotalProcessed: results.Count,
            CreatedCount: createdCount,
            RefusedCount: refusedCount,
            DryRun: dryRun,
            Results: results));
    }

    private static string? ActorOf(ClaimsPrincipal user) =>
        user.FindFirstValue("userFullName")
        ?? user.FindFirstValue("userName")
        ?? user.FindFirstValue("id")
        ?? user.Identity?.Name;
}
