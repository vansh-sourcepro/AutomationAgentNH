using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Endpoints;

/// <summary>
/// The PO → GRN trigger: <c>POST /api/automation/po-to-grn</c>. The dashboard's Run (later), an API
/// caller and the daily scheduler all come through here, told apart by <c>?trigger=</c>.
/// </summary>
public static class PoToGrnEndpoints
{
    public const string Route = "/api/automation/po-to-grn";

    private const int DefaultMaxPos = 25;

    public static IEndpointRouteBuilder MapPoToGrnEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(Route, ReceiveAsync)
            .WithName("ReceiveAuthorisedPos")
            .AddEndpointFilter<ApiKeyFilter>();

        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        ReceivePosRequest? request,
        IPoToGrnService service,
        IPoGrnAutomationConfigRepository configs,
        IPoGrnHistory history,
        IClock clock,
        HttpContext httpContext,
        ILogger<PoToGrnService> logger,
        CancellationToken cancellationToken,
        string? trigger = null)
    {
        request ??= new ReceivePosRequest();

        if (request.BindingError is { } bindingError)
        {
            return Results.Problem(
                title: "The request body could not be read.",
                detail: $"{bindingError} Accepted fields: poIds, poNumbers, sites, poTypes, receiptMode, maxPos, dryRun.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseTrigger(trigger, out var triggerSource))
        {
            return Results.Problem(
                title: "Unknown trigger.",
                detail: $"'{trigger}' is not a trigger. Expected 'manual', 'timer' or 'api'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParsePoTypes(request.PoTypes, out var poTypes, out var typeError))
        {
            return typeError!;
        }

        if (!GrnAutomationEndpoints.TryParseEnum<GrnReceiptMode>(request.ReceiptMode, "receipt mode", out var modeOverride, out var modeError))
        {
            return modeError!;
        }

        if (!configs.IsEnabled)
        {
            return GrnAutomationEndpoints.NoDatabase();
        }

        PoGrnAutomationConfig config;

        try
        {
            config = await configs.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read the GRN Automation settings");
            return GrnAutomationEndpoints.NoDatabase();
        }

        // The master switch governs every path: Run, API and scheduler alike.
        if (!config.IsActive)
        {
            return Results.Problem(
                title: "GRN Automation is turned off.",
                detail: "Turn it on with PUT /api/automation/grn-automation {\"isActive\": true}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (!request.DryRun && !config.HasInvoiceNumber)
        {
            return Results.Problem(
                title: "No invoice number is set.",
                detail: "Every GRN carries the invoice number from the settings. Set it with "
                    + "PUT /api/automation/grn-automation {\"invoiceNumber\": \"...\"}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var receiptMode = modeOverride ?? config.ReceiptMode;
        var sites = request.Sites is { Count: > 0 } ? request.Sites : config.SiteIds();

        var sweepRequest = new PoGrnSweepRequest
        {
            Sites = sites.Count > 0 ? sites : null,
            PoTypes = poTypes,
            PoIds = request.PoIds,
            PoNumbers = request.PoNumbers,
            ReceiptMode = receiptMode,
            InvoiceNumber = config.InvoiceNumber,
            MaxPos = request.MaxPos is > 0 ? request.MaxPos.Value : config.MaxPosPerRun ?? DefaultMaxPos,
            DryRun = request.DryRun,
        };

        // A dry run creates nothing, so it has no history and stamps nothing.
        if (request.DryRun)
        {
            try
            {
                return Results.Ok(ToResponse(triggerSource, receiptMode, poTypes, runId: null,
                    await service.ReceiveEligibleAsync(sweepRequest, cancellationToken)));
            }
            catch (ErpException ex)
            {
                return Failure(ex);
            }
        }

        await history.StartRunAsync(
            new StartGrnRunRequest(
                triggerSource,
                triggerSource == TriggerSource.Timer ? "Scheduler" : null,
                httpContext.TraceIdentifier,
                receiptMode,
                sites.Count > 0 ? string.Join(", ", sites) : null),
            cancellationToken);

        try
        {
            var sweep = await service.ReceiveEligibleAsync(sweepRequest, cancellationToken);

            await history.CompleteRunAsync(cancellationToken);
            await StampAsync(configs, clock, RunStatus.Completed, history.RunId, logger, cancellationToken);

            return Results.Ok(ToResponse(triggerSource, receiptMode, poTypes, history.RunId, sweep));
        }
        catch (ErpException ex)
        {
            await history.FailRunAsync(ex.LaymanMessage, CancellationToken.None);
            await StampAsync(configs, clock, RunStatus.Failed, history.RunId, logger, CancellationToken.None);

            return Failure(ex);
        }
    }

    /// <summary>Last Run / Last Status on the settings row. A stale stamp is not worth a 500.</summary>
    private static async Task StampAsync(
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

    private static IResult Failure(ErpException exception) =>
        Results.Problem(
            title: exception.LaymanMessage,
            detail: exception.TechnicalMessage,
            statusCode: exception.IsTransient
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status400BadRequest);

    private static bool TryParseTrigger(string? trigger, out TriggerSource source)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            source = TriggerSource.Api;
            return true;
        }

        return Enum.TryParse(trigger.Trim(), ignoreCase: true, out source)
            && source is TriggerSource.Manual or TriggerSource.Timer or TriggerSource.Api;
    }

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
            if (!Enum.TryParse(value?.Trim(), ignoreCase: true, out PoGrnType type) || !Enum.IsDefined(type))
            {
                error = Results.Problem(
                    $"'{value}' is not a PO type. Expected Regular and/or Capital.",
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

    private static ReceivePosResponse ToResponse(
        TriggerSource trigger,
        GrnReceiptMode receiptMode,
        IReadOnlyList<PoGrnType> poTypes,
        Guid? runId,
        PoGrnSweepResult sweep) =>
        new(
            trigger.ToString(),
            sweep.DryRun,
            receiptMode.ToString(),
            [.. poTypes.Select(type => type.ToString())],
            runId,
            sweep.Examined,
            sweep.GrnsCreated,
            sweep.GrnsPlanned,
            sweep.StoppedReason,
            sweep.NotFound,
            [.. sweep.Results.Select(result => new PoGrnResultResponse(
                result.PoId,
                result.PoNumber,
                result.PoType,
                result.SiteId,
                result.VendorCode,
                result.WarehouseId,
                result.Planned ? "Planned" : result.Status.ToString(),
                result.Planned,
                result.GrnId,
                result.GrnNumber,
                result.LinesReceived,
                result.LinesSkipped,
                result.Notes))]);
}
