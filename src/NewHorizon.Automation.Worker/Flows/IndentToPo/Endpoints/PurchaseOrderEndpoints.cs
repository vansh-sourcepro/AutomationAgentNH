using System.Security.Claims;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Services;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;

/// <summary>
/// Turns authorised indents into purchase orders, on request.
/// </summary>
/// <remarks>
/// Request-driven rather than queued, so it needs no database and stays available when the agent
/// runs without one. The trade-off is deliberate: the ERP allocates the PO number inside its own
/// transaction and returns it here, so the caller learns the outcome directly instead of polling a
/// job. A failure leaves nothing behind to resume — the caller simply asks again, and the ERP's own
/// bookkeeping of how much of an indent is already ordered stops that repeating anything.
/// </remarks>
public static class PurchaseOrderEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/automation/indent-to-po")
            .AddEndpointFilter<ApiKeyFilter>();

        // The conversion trigger. Three callers reach it, distinguished by the ?trigger= query:
        //   ?trigger=manual  the PO Automation screen's "Run now" — a browser bearer token
        //   ?trigger=timer   PoAutomationSchedulerService's daily self-call — the inbound API key
        //   (absent)/api     an external machine call — the inbound API key
        // Mapped outside the group so it can take either auth; /vendor stays API-key-only.
        // A browser caller (the "Run now" button) is an ERP user and must hold form 011171 'E' — it
        // creates real purchase orders, the same as editing the config. The inbound API key /
        // scheduler self-call carries no user identity, so RequireErpFormRight waves it through.
        endpoints.MapPost("/api/automation/indent-to-po", ConvertAsync)
            .WithName("ConvertAuthorisedIndents")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        // The same handler under its older path, so anything already calling /convert keeps
        // working. One handler, not two: a second copy is a second place for the type filter to
        // be got wrong.
        endpoints.MapPost("/api/automation/indent-to-po/convert", ConvertAsync)
            .WithName("ConvertAuthorisedIndentsAlias")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        // Read-only. What a conversion would consider, so the selection can be checked before
        // anything is created. Mapped outside the group's API-key-only filter because the ERP
        // frontend calls it straight from the browser with a bearer token.
        endpoints.MapGet("/api/automation/indent-to-po/eligible", FindEligibleAsync)
            .WithName("ListEligibleIndents")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Inquiry);

        // The vendor-driven original: order everything one vendor has outstanding. Moved off the
        // group's own path to make room for the trigger above, and kept because it is still the
        // right tool when a buyer knows the vendor and wants the lot.
        group.MapPost("/vendor", CreateForVendorAsync)
            .WithName("CreatePurchaseOrderForVendor");

        return endpoints;
    }

    /// <summary>
    /// Orders what a vendor has outstanding, of the indent types the caller allowed.
    /// </summary>
    /// <remarks>
    /// One run of the material PO sequence per selected type: Regular and Capital are separate
    /// documents in the ERP even for the same vendor, so asking for both means up to two orders.
    /// The sequence itself is untouched — the selection wraps it rather than reaching into it.
    /// </remarks>
    private static async Task<IResult> CreateForVendorAsync(
        CreatePurchaseOrderRequest? request,
        IIndentToPoService indentToPo,
        IPoAutomationGate poAutomationGate,
        CancellationToken cancellationToken)
    {
        request ??= new CreatePurchaseOrderRequest();

        var filter = IndentTypeFilter.Parse(request.IndentTypes, request.IndentType);

        if (!filter.IsValid)
        {
            return InvalidIndentType(filter.Error!);
        }

        var selection = IndentTypeSelection.Normalise(filter.Selection);

        // The master toggle: off ⇒ this vendor-driven conversion is refused too. Checked against
        // the material types it will actually order (Service is only noted below).
        var poAutomationOff = await poAutomationGate.OffReasonAsync(
            selection.Where(type => type != IndentType.Service)
                .Select(type => Enum.Parse<IndentKind>(type.ToString())),
            cancellationToken);

        if (poAutomationOff is not null)
        {
            return Results.Problem(
                title: "PO Automation is turned off.",
                detail: poAutomationOff,
                statusCode: StatusCodes.Status409Conflict);
        }

        var purchaseOrders = new List<CreatePurchaseOrderResponse>();
        var notes = new List<string>();

        try
        {
            foreach (var indentType in selection)
            {
                if (indentType == IndentType.Service)
                {
                    // Selected, and honestly reported as not convertible here rather than either
                    // silently dropped or allowed to fail a call that has Regular orders to make.
                    notes.Add(
                        "Service indents cannot be ordered by vendor: the ERP has no vendor-first "
                        + "pending-lines question for them. Use /api/automation/indent-to-po/convert.");
                    continue;
                }

                try
                {
                    var result = await indentToPo.CreateAsync(
                        new IndentPoRequest { IndentType = indentType, VendorCode = request.VendorCode },
                        cancellationToken);

                    purchaseOrders.Add(ToResponse(result));
                }
                catch (ErpException ex)
                {
                    // A refusal — business or transient — for one type leaves the other, and any
                    // purchase order(s) already made for a type earlier in the selection, untouched:
                    // recorded as a note rather than thrown, so a transient hiccup on Capital can
                    // never discard the Regular order already made a moment ago by unwinding past
                    // `purchaseOrders`.
                    notes.Add($"{indentType}: {ex.LaymanMessage}");
                }
            }
        }
        catch (Exception ex) when (ex is ErpException or InvalidOperationException)
        {
            // Nothing here can actually throw an ErpException today — every ERP call above is
            // already caught per-type — but this mirrors ConvertAsync's mapping so any future call
            // added to this method surfaces as the correct 503/400 rather than an unhandled 500.
            return Failure(ex);
        }

        // Nothing made and nothing to say means the selection was one this endpoint cannot serve at
        // all; that is a bad request, not an empty success.
        if (purchaseOrders.Count == 0 && selection.All(type => type == IndentType.Service))
        {
            return Results.Problem(
                title: "This endpoint orders by vendor, which the ERP only supports for material indents.",
                detail: string.Join(" ", notes),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(new CreatePurchaseOrdersResponse(
            selection.Select(type => type.ToString()).ToList(),
            purchaseOrders,
            notes));
    }

    /// <param name="indentTypes">
    /// Repeated (<c>?indentTypes=regular&amp;indentTypes=service</c>) or comma-separated
    /// (<c>?indentTypes=regular,service</c>). Absent means every type.
    /// </param>
    private static async Task<IResult> FindEligibleAsync(
        IIndentToPoService indentToPo,
        CancellationToken cancellationToken,
        string? sites = null,
        string[]? indentTypes = null,
        string? indentType = null,
        int max = 100)
    {
        var filter = IndentTypeFilter.Parse(indentTypes, indentType);

        if (!filter.IsValid)
        {
            return InvalidIndentType(filter.Error!);
        }

        try
        {
            var eligible = await indentToPo.FindEligibleAsync(
                new IndentDiscoveryRequest
                {
                    Sites = ParseSites(sites),
                    IndentTypes = filter.Selection,
                    MaxResults = max,
                },
                cancellationToken);

            return Results.Ok(eligible.Select(ToResponse).ToList());
        }
        catch (Exception ex) when (ex is ErpException or InvalidOperationException)
        {
            return Failure(ex);
        }
    }

    private static async Task<IResult> ConvertAsync(
        ConvertIndentsRequest? request,
        IIndentToPoService indentToPo,
        IProcessJobOrchestrator orchestrator,
        IPoAutomationGate poAutomationGate,
        IIndentPoAutomationConfigRepository automationConfigs,
        IIndentPoTracker tracker,
        Application.Abstractions.IClock clock,
        System.Security.Claims.ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        string? trigger = null)
    {
        request ??= new ConvertIndentsRequest();

        // manual = the screen's "Run now" (operator override); timer = the daily scheduler's
        // self-call; anything else / absent = an external machine call.
        TriggerSource triggerSource;
        if (string.IsNullOrWhiteSpace(trigger))
        {
            triggerSource = TriggerSource.Api;
        }
        else if (!Enum.TryParse(trigger.Trim(), ignoreCase: true, out triggerSource)
            || triggerSource is not (TriggerSource.Manual or TriggerSource.Timer or TriggerSource.Api))
        {
            return Results.Problem(
                title: "Unknown trigger.",
                detail: $"'{trigger}' is not a conversion trigger. Expected 'manual', 'timer' or 'api'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A field this endpoint does not recognise is refused, not discarded. Silently ignoring
        // "indentnumber" turned "convert this one indent" into "convert every eligible indent",
        // which on a real ERP is twenty purchase orders nobody asked for.
        if (request.BindingError is { } bindingError)
        {
            return Results.Problem(
                title: "The request body could not be read.",
                detail: $"{bindingError} Accepted fields: indentId, sites, indentTypes, indentType, "
                    + "indentNumbers, maxIndents, dryRun.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.IsNullOrWhiteSpace(request.VendorCode))
        {
            // This path used to be the vendor-driven form. Answering a vendor code with a sweep of
            // every authorised indent would be a silent and expensive misunderstanding, so it is
            // refused with the new address rather than quietly reinterpreted.
            return Results.Problem(
                title: "This endpoint converts authorised indents; it does not take a vendor.",
                detail: "The vendor-driven form moved to POST /api/automation/indent-to-po/vendor. "
                    + "To convert indents, drop vendorCode and name the indentTypes to convert.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var filter = IndentTypeFilter.Parse(request.IndentTypes, request.IndentType);

        if (!filter.IsValid)
        {
            return InvalidIndentType(filter.Error!);
        }

        var normalised = IndentTypeSelection.Normalise(filter.Selection);
        var selection = normalised.Select(type => type.ToString()).ToList();

        // The PO Automation screen's master toggle governs every conversion path — the scheduler
        // (also via ShouldRunOnSchedule), the screen's Run button, and an external API caller
        // alike. Turning it off means no authorised indent converts to a PO, however it was
        // triggered. This is the check at the door; the same gate is re-run before every indent
        // inside the sweep (IndentToPoService), so turning it off mid-run stops it too. A
        // database-less install has no toggle to persist and keeps converting on request exactly
        // as before (OffReasonAsync returns null when IsEnabled is false).
        var poAutomationOff = await poAutomationGate.OffReasonAsync(
            normalised.Select(type => Enum.Parse<IndentKind>(type.ToString())),
            cancellationToken);

        if (poAutomationOff is not null)
        {
            return Results.Problem(
                title: "PO Automation is turned off.",
                detail: poAutomationOff,
                statusCode: StatusCodes.Status409Conflict);
        }

        var numbers = IndentNumberSelection.Normalise(request.IndentNumbers);

        // Two different ways of saying which indents to convert. Answering one of them and
        // ignoring the other would convert something the caller did not ask for, so it is refused
        // and named rather than quietly resolved — the same treatment vendorCode gets above.
        if (request.IndentId is not null && numbers is not null)
        {
            return Results.Problem(
                title: "Give either an indentId or indentNumbers, not both.",
                detail: "indentId names one indent by its ERP key and indentNumbers names indents "
                    + "by their document number. Send whichever you have, on its own.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var named = numbers is null ? Array.Empty<string>() : [.. numbers.Order(StringComparer.OrdinalIgnoreCase)];

        try
        {
            // The conversion itself, byte for byte what it was: one indent by id, or the sweep.
            // Lifted into a local only so it can be run either inside a tracked run or, for a dry
            // run, on its own — without a second copy of the call or of the shape it answers with.
            // It reports the indents examined alongside its response, which is what the run records.
            async Task<(ConvertIndentsResponse? Value, int Examined)> ConvertCoreAsync()
            {
                if (request.IndentId is { } indentId)
                {
                    var one = await indentToPo.ConvertIndentAsync(
                        indentId,
                        indentNumber: null,
                        sites: request.Sites,
                        // The same allow-list, doing double duty: it says which types may be converted,
                        // and it says which family the id belongs to — material and service ids come
                        // from different tables and can collide.
                        indentTypes: filter.Selection,
                        dryRun: request.DryRun,
                        cancellationToken);

                    return (new ConvertIndentsResponse(
                        Examined: 1,
                        PurchaseOrdersCreated: one.PurchaseOrders.Count,
                        PurchaseOrdersPlanned: request.DryRun ? one.PlannedPurchaseOrders : one.PurchaseOrders.Count,
                        DryRun: request.DryRun,
                        IndentTypes: selection,
                        IndentNumbers: named,
                        Indents: [ToResponse(one)],
                        Skipped: SkippedOf([one], request.DryRun),
                        NotFound: []),
                        1);
                }

                var sweep = await indentToPo.ConvertEligibleAsync(
                    new IndentSweepRequest
                    {
                        Sites = request.Sites,
                        IndentTypes = filter.Selection,
                        IndentNumbers = request.IndentNumbers,
                        MaxIndents = request.MaxIndents ?? 25,
                        DryRun = request.DryRun,
                    },
                    cancellationToken);

                return (new ConvertIndentsResponse(
                    sweep.Examined,
                    sweep.PurchaseOrdersCreated,
                    sweep.DryRun ? sweep.PurchaseOrdersPlanned : sweep.PurchaseOrdersCreated,
                    sweep.DryRun,
                    selection,
                    named,
                    sweep.Results.Select(ToResponse).ToList(),
                    SkippedOf(sweep.Results, sweep.DryRun),

                    // Named but never examined. Computed from what the sweep actually looked at, so it
                    // covers every reason a number produced nothing without having to enumerate them.
                    IndentNumberSelection.Unmatched(numbers, sweep.Results.Select(result => result.Indent))),
                    sweep.Examined);
            }

            // A dry run creates nothing, so it has nothing to have a history of: no run is opened,
            // and the conversion pipeline already leaves it out of the tracking tables entirely.
            if (request.DryRun)
            {
                var (planned, _) = await ConvertCoreAsync();

                return Results.Ok(planned);
            }

            // Recorded under a run, so the history says what asked for these purchase orders. The
            // run lifecycle is the orchestrator's, reused rather than repeated here — and it does
            // not require a database: with tracking off every call inside it is a no-op and the
            // conversion still runs and still answers, which this endpoint has always done.
            var (converted, refusal) = await orchestrator.RunTrackedAsync(
                new StartRunRequest(
                    triggerSource,

                    // The allow-list as understood, never blank: it separates "found no Capital
                    // indents" from "was never allowed to look for Capital indents".
                    string.Join(", ", selection),

                    // For a manual "Run now" the browser carries the ERP user; the scheduler names
                    // itself; an unlabelled machine call carries no identity.
                    TriggeredBy: TriggeredByFor(triggerSource, user),

                    // The way back to the request that asked, which is what this field is for: it
                    // ties the run to its line in the request log.
                    TriggerReference: httpContext.TraceIdentifier,
                    RequestedSites: request.Sites is { Count: > 0 } ? string.Join(", ", request.Sites) : null,
                    MaxIndents: request.MaxIndents),
                ConvertCoreAsync,
                cancellationToken);

            // Stamp Last Run / Last Status / Last Run Reference on every config row this run
            // touched, so the PO Automation screen reflects it. The scheduler already owns
            // LastScheduledRunDate (it claims the slot before calling), so a timer run records the
            // outcome only — MarkManualRun leaves that date alone.
            await StampConfigsAsync(
                normalised,
                refusal is null ? RunStatus.Completed : RunStatus.Failed,
                tracker.RunId,
                automationConfigs,
                clock,
                cancellationToken);

            // The refusal already carries its status code, mapped from the ERP exception exactly as
            // Failure does below — so the same refusal cannot answer differently depending on
            // whether the run was open.
            return refusal is not null
                ? Results.Problem(title: refusal.Title, detail: refusal.Detail, statusCode: refusal.StatusCode)
                : Results.Ok(converted);
        }
        catch (Exception ex) when (ex is ErpException or InvalidOperationException)
        {
            return Failure(ex);
        }
    }

    /// <summary>
    /// The exception already carries the retry verdict, so the status code follows from it rather
    /// than from a second guess here: transient means "ask again", business means "a person has to
    /// change something first". The payload builder refuses an empty order with a plain
    /// <see cref="InvalidOperationException"/>, which is the same kind of answer and gets the same
    /// treatment rather than escaping as a 500.
    /// </summary>
    private static IResult Failure(Exception exception) => exception switch
    {
        ErpException erp => Results.Problem(
            title: erp.LaymanMessage,
            detail: erp.TechnicalMessage,
            statusCode: erp.IsTransient
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status400BadRequest),
        _ => Results.Problem(
            title: "The purchase order could not be assembled from what the ERP returned.",
            detail: exception.Message,
            statusCode: StatusCodes.Status400BadRequest),
    };

    /// <summary>
    /// A refusal the caller can act on without reading the source: which value was not understood,
    /// and what the accepted ones are. Named argument on purpose — <c>Results.Problem</c>'s first
    /// positional parameter is the detail, not the title, and a message put there is invisible to
    /// anything that reads the title.
    /// </summary>
    private static IResult InvalidIndentType(string message) =>
        Results.Problem(title: message, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Who to record as the trigger's actor: the ERP user for a manual run, the scheduler for a timer, else nobody.</summary>
    private static string? TriggeredByFor(TriggerSource trigger, ClaimsPrincipal user) => trigger switch
    {
        TriggerSource.Manual => user.FindFirstValue("userFullName")
            ?? user.FindFirstValue("userName")
            ?? user.FindFirstValue("id")
            ?? user.Identity?.Name,
        TriggerSource.Timer => "Scheduler",
        _ => null,
    };

    /// <summary>
    /// Records the outcome of a real run on the <see cref="IndentPoAutomationConfig"/> row of every
    /// indent type it touched, so the PO Automation screen's Last Run / Last Status columns are
    /// current. Best-effort: a stamp failure never fails the conversion the caller already got.
    /// </summary>
    private static async Task StampConfigsAsync(
        IReadOnlyList<IndentType> selection,
        RunStatus status,
        Guid? runId,
        IIndentPoAutomationConfigRepository automationConfigs,
        Application.Abstractions.IClock clock,
        CancellationToken cancellationToken)
    {
        if (!automationConfigs.IsEnabled)
        {
            return;
        }

        foreach (var type in selection.Select(t => Enum.Parse<IndentKind>(t.ToString())).Distinct())
        {
            try
            {
                var config = await automationConfigs.GetAsync(type, cancellationToken);
                config.MarkManualRun(clock.UtcNow, status, runId);
                await automationConfigs.SaveAsync(config, cancellationToken);
            }
            catch (Exception)
            {
                // Swallowed on purpose — the purchase order(s) already exist and the run row already
                // records the outcome; a stale "last run" column is not worth a 500.
            }
        }
    }

    private static IReadOnlyList<int>? ParseSites(string? sites)
    {
        if (string.IsNullOrWhiteSpace(sites))
        {
            return null;
        }

        var parsed = sites
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var siteId) ? siteId : (int?)null)
            .OfType<int>()
            .ToList();

        return parsed.Count > 0 ? parsed : null;
    }


    /// <summary>
    /// The examined indents that produced no purchase order, with their reasons.
    /// </summary>
    /// <remarks>
    /// A dry run is excluded outright rather than reporting every indent as skipped: it creates
    /// nothing by design, so "produced no purchase order" is not news about that indent. What a dry
    /// run would have done is already in <c>PurchaseOrdersPlanned</c> and in each indent's notes.
    /// </remarks>
    private static IReadOnlyList<SkippedIndentResponse> SkippedOf(
        IEnumerable<IndentConversionResult> results,
        bool dryRun) =>
        dryRun
            ? []
            : results
                .Where(result => !result.Converted)
                .Select(result => new SkippedIndentResponse(
                    result.Indent.IndentId,
                    result.Indent.DisplayNumber,
                    result.Indent.IndentType.ToString(),
                    result.Indent.SiteId,
                    // Never empty in practice — the conversion records why it ordered nothing — but
                    // a bare "skipped" with no reason would be the least useful line in the
                    // response, so it says so rather than leaving a hole.
                    result.Notes.Count > 0
                        ? result.Notes
                        : ["This indent produced no purchase order and the ERP gave no reason."]))
                .ToList();
    private static CreatePurchaseOrderResponse ToResponse(IndentPoResult result) =>
        new(result.PoNumber, result.PoId, result.VendorCode, result.ItemCount);

    private static EligibleIndentResponse ToResponse(EligibleIndent eligible) =>
        new(
            eligible.Indent.IndentId,
            eligible.Indent.DisplayNumber,
            eligible.Indent.IndentType.ToString(),
            eligible.Indent.SiteId,
            eligible.Indent.SiteCode,
            eligible.DocumentStatus,
            eligible.IndentDate,
            eligible.RequestedBy);

    private static IndentConversionResponse ToResponse(IndentConversionResult result) =>
        new(
            result.Indent.IndentId,
            result.Indent.DisplayNumber,
            result.Indent.IndentType.ToString(),
            result.Indent.SiteId,
            result.Converted,
            result.PurchaseOrders.Select(ToResponse).ToList(),
            result.Notes);
}
