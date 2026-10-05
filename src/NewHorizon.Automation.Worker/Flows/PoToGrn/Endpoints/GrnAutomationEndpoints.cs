using System.Globalization;
using System.Security.Claims;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Endpoints;

/// <summary>
/// The PO → GRN settings API, <c>/api/automation/grn-automation</c>: built like Indent → PO's
/// <c>/api/automation/po-automation</c>. The run itself goes through <c>POST /api/automation/po-to-grn</c>.
/// </summary>
/// <remarks>
/// Takes the inbound API key — no ERP login needed (confirmed 2026-09-25) — or, for a future
/// dashboard screen, an ERP bearer token. A token caller is also checked against the PO Automation
/// Role Management form (011171: <c>I</c> to view, <c>E</c> to change) and is stamped as "updated
/// by"; an API-key caller is stamped <c>api-key</c>.
/// </remarks>
public static class GrnAutomationEndpoints
{
    public const string Route = "/api/automation/grn-automation";

    public static IEndpointRouteBuilder MapGrnAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(Route).AddEndpointFilter<ErpUserOrApiKeyFilter>();

        group.MapGet(string.Empty, GetAsync).WithName("GetGrnAutomation")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Inquiry);
        group.MapPut(string.Empty, UpdateAsync).WithName("UpdateGrnAutomation")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);
        group.MapPut("/enabled", SetEnabledAsync).WithName("SetGrnAutomationEnabled")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);
        group.MapPut("/po-types", SetPoTypesAsync).WithName("SetGrnAutomationPoTypes")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);
        group.MapPut("/po-numbers", SetPoNumbersAsync).WithName("SetGrnAutomationPoNumbers")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        IPoGrnAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        if (!configs.IsEnabled)
        {
            return NoDatabase();
        }

        return Results.Ok(ToResponse(await configs.GetAsync(cancellationToken)));
    }

    /// <summary>The master switch. Off ⇒ the scheduler runs nothing and every run is refused (409).</summary>
    private static Task<IResult> SetEnabledAsync(
        SetGrnAutomationEnabledRequest? request,
        IPoGrnAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) =>
        request is null
            ? Task.FromResult(Results.Problem("An { \"enabled\": true|false } body is required.", statusCode: StatusCodes.Status400BadRequest))
            : SaveAsync(new PoGrnAutomationConfigUpdate { IsActive = request.Enabled }, configs, user, cancellationToken);

    /// <summary>Limits every later run to these PO types; an empty list means both.</summary>
    private static Task<IResult> SetPoTypesAsync(
        SetGrnPoTypesRequest? request,
        IPoGrnAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) =>
        request is null
            ? Task.FromResult(Results.Problem("A { \"poTypes\": [\"Regular\", \"Capital\"] } body is required.", statusCode: StatusCodes.Status400BadRequest))
            : SaveAsync(new PoGrnAutomationConfigUpdate { PoTypes = request.PoTypes ?? [] }, configs, user, cancellationToken);

    /// <summary>Limits every later run to these POs; blank means every eligible PO.</summary>
    private static Task<IResult> SetPoNumbersAsync(
        SetGrnPoNumbersRequest? request,
        IPoGrnAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) =>
        request is null
            ? Task.FromResult(Results.Problem("A { \"poNumbers\": \"26-27/TE/NF1/000190\" } body is required.", statusCode: StatusCodes.Status400BadRequest))
            : SaveAsync(new PoGrnAutomationConfigUpdate { PoNumbers = request.PoNumbers ?? string.Empty }, configs, user, cancellationToken);

    private static async Task<IResult> SaveAsync(
        PoGrnAutomationConfigUpdate update,
        IPoGrnAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!configs.IsEnabled)
        {
            return NoDatabase();
        }

        try
        {
            return Results.Ok(ToResponse(await configs.UpdateAsync(update, ActorOf(user), cancellationToken)));
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UpdateAsync(
        UpdateGrnAutomationRequest? request,
        IPoGrnAutomationConfigRepository configs,
        IPoToGrnService service,
        IPoGrnHistory history,
        IClock clock,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem("A settings body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!configs.IsEnabled)
        {
            return NoDatabase();
        }

        if (!TryParseEnum<PoGrnRunMode>(request.RunMode, "run mode", out var runMode, out var modeError))
        {
            return modeError!;
        }

        if (!TryParseEnum<GrnReceiptMode>(request.ReceiptMode, "receipt mode", out var receiptMode, out var receiptError))
        {
            return receiptError!;
        }

        if (!TryParseTime(request.ScheduleTime, out var scheduleTime, out var timeError))
        {
            return timeError!;
        }

        // Validate any PoTypes sent inline on this request.
        if (!TryParsePoTypes(request.PoTypes, out var poTypesForConfig, out var poTypeError))
        {
            return poTypeError!;
        }

        var update = new PoGrnAutomationConfigUpdate
        {
            IsActive = request.IsActive,
            RunMode = runMode,
            ScheduleTime = scheduleTime,
            ClearScheduleTime = request.ClearScheduleTime,
            ReceiptMode = receiptMode,
            InvoiceNumber = request.InvoiceNumber,
            Sites = request.Sites,
            DryRun = request.DryRun,
            MaxPosPerRun = request.MaxPosPerRun,
            ClearMaxPosPerRun = request.ClearMaxPosPerRun,
            // PoTypes and PoNumbers can now be set directly from this unified request body.
            PoTypes = request.PoTypes,
            PoNumbers = request.PoNumbers,
        };

        PoGrnAutomationConfig savedConfig;

        try
        {
            savedConfig = await configs.UpdateAsync(update, ActorOf(user), cancellationToken);
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var configResponse = ToResponse(savedConfig);

        // ── Trigger logic ──────────────────────────────────────────────────────────────
        // Toggle is OFF → just return the saved config; nothing runs.
        if (!savedConfig.IsActive)
        {
            return Results.Ok(new UpdateGrnAutomationResponse(configResponse, Execution: null));
        }

        // Toggle is ON + scheduleTime is set → the scheduler will fire at that time; nothing now.
        if (savedConfig.ScheduleTime.HasValue)
        {
            return Results.Ok(new UpdateGrnAutomationResponse(configResponse, Execution: null));
        }

        // Toggle is ON + no scheduleTime → "Run Now": execute immediately and return results.
        if (!savedConfig.HasInvoiceNumber)
        {
            return Results.Problem(
                title: "No invoice number is set.",
                detail: "Set invoiceNumber in the request body before triggering an immediate run.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Resolve the effective filters from the freshly saved config.
        var effectiveReceiptMode = savedConfig.ReceiptMode;
        var effectiveSites = savedConfig.SiteIds();
        var effectivePoTypes = savedConfig.PoTypeList();
        var effectivePoNumbers = savedConfig.PoNumberList() is { Count: > 0 } saved ? saved : null;

        var sweepRequest = new PoGrnSweepRequest
        {
            Sites = effectiveSites.Count > 0 ? effectiveSites : null,
            PoTypes = effectivePoTypes,
            PoIds = null,
            PoNumbers = effectivePoNumbers,
            ReceiptMode = effectiveReceiptMode,
            InvoiceNumber = savedConfig.InvoiceNumber,
            MaxPos = savedConfig.MaxPosPerRun ?? PoToGrnEndpoints.DefaultMaxPos,
            DryRun = savedConfig.DryRun,
        };

        await history.StartRunAsync(
            new StartGrnRunRequest(
                TriggerSource.Manual,
                "RunNow",
                httpContext.TraceIdentifier,
                effectiveReceiptMode,
                effectiveSites.Count > 0 ? string.Join(", ", effectiveSites) : null),
            cancellationToken);

        try
        {
            var sweep = await service.ReceiveEligibleAsync(sweepRequest, cancellationToken);

            await history.CompleteRunAsync(cancellationToken);
            await StampRunAsync(configs, clock, RunStatus.Completed, history.RunId, loggerFactory.CreateLogger(nameof(GrnAutomationEndpoints)), cancellationToken);

            var executionResponse = PoToGrnEndpoints.BuildReceivePosResponse(
                TriggerSource.Manual, effectiveReceiptMode, effectivePoTypes, history.RunId, sweep);

            // Re-read config so the response stamps (LastTriggeredAtUtc etc.) are fresh.
            var stamped = await configs.GetAsync(cancellationToken);

            return Results.Ok(new UpdateGrnAutomationResponse(ToResponse(stamped), executionResponse));
        }
        catch (ErpException ex)
        {
            await history.FailRunAsync(ex.LaymanMessage, CancellationToken.None);
            await StampRunAsync(configs, clock, RunStatus.Failed, history.RunId, loggerFactory.CreateLogger(nameof(GrnAutomationEndpoints)), CancellationToken.None);

            return Results.Problem(
                title: ex.LaymanMessage,
                detail: ex.TechnicalMessage,
                statusCode: ex.IsTransient
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status400BadRequest);
        }
    }

    // Validates PoTypes strings → PoGrnType enum values (shared with PoToGrnEndpoints logic).
    private static bool TryParsePoTypes(
        IReadOnlyList<string>? values,
        out IReadOnlyList<PoGrnType> types,
        out IResult? error)
    {
        types = PoGrnTypes.All;
        error = null;

        if (values is not { Count: > 0 })
        {
            return true;
        }

        var parsed = new List<PoGrnType>();

        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            PoGrnType type;
            if (string.Equals(trimmed, "RP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Regular", StringComparison.OrdinalIgnoreCase))
            {
                type = PoGrnType.Regular;
            }
            else if (string.Equals(trimmed, "CP", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(trimmed, "Capital", StringComparison.OrdinalIgnoreCase))
            {
                type = PoGrnType.Capital;
            }
            else
            {
                error = Results.Problem(
                    $"'{value}' is not a PO type. Expected Regular (RP) and/or Capital (CP).",
                    statusCode: StatusCodes.Status400BadRequest);
                return false;
            }

            if (!parsed.Contains(type))
            {
                parsed.Add(type);
            }
        }

        types = parsed;
        return true;
    }

    /// <summary>Stamps LastRun / LastStatus on the settings row; a stale stamp is not worth a 500.</summary>
    private static async Task StampRunAsync(
        IPoGrnAutomationConfigRepository configs,
        IClock clock,
        RunStatus status,
        Guid? runId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await configs.GetAsync(cancellationToken);
            fresh.MarkRun(clock.UtcNow, status, runId);
            await configs.SaveAsync(fresh, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not stamp the GRN Automation Last Run columns");
        }
    }

    /// <summary>
    /// Who to stamp on the change — the ERP user's full name, falling back to their id; an API-key
    /// caller has no login to read one from.
    /// </summary>
    private static string ActorOf(ClaimsPrincipal user) =>
        user.FindFirstValue("userFullName")
        ?? user.FindFirstValue("userName")
        ?? user.FindFirstValue("id")
        ?? user.Identity?.Name
        ?? "api-key";

    internal static GrnAutomationConfigResponse ToResponse(PoGrnAutomationConfig config) =>
        new(
            config.IsActive,
            config.RunMode.ToString(),
            config.ScheduleTime,
            config.ReceiptMode.ToString(),
            config.InvoiceNumber,
            config.Sites,
            [.. config.PoTypeList().Select(type => type.ToString())],
            config.PoNumbers,
            config.DryRun,
            config.MaxPosPerRun,
            config.LastScheduledRunDate,
            config.LastTriggeredAtUtc,
            config.LastRunStatus,
            config.LastRunReference,
            config.UpdatedAtUtc,
            config.UpdatedBy);

    internal static IResult NoDatabase() =>
        Results.Problem(
            "GRN Automation needs the automation database, and this installation has none it can use.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static bool TryParseEnum<T>(string? value, string what, out T? parsed, out IResult? error)
        where T : struct, Enum
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out T result) && Enum.IsDefined(result))
        {
            parsed = result;
            return true;
        }

        error = Results.Problem(
            $"'{value}' is not a {what}. Expected one of: {string.Join(", ", Enum.GetNames<T>())}.",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }

    private static bool TryParseTime(string? value, out TimeOnly? time, out IResult? error)
    {
        time = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        error = Results.Problem(
            $"'{value}' is not a time. Use 24-hour HH:mm, e.g. \"18:30\".",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }
}
