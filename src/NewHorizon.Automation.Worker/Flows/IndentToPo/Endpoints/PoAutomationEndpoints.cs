using System.Security.Claims;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;

/// <summary>
/// The PO Automation screen's configuration API: read and change the three per-indent-type
/// automation rows. The conversion itself — "Run now" and the daily scheduler — goes through
/// <c>POST /api/automation/indent-to-po/convert</c> (see <see cref="PurchaseOrderEndpoints"/>).
/// </summary>
/// <remarks>
/// Browser-facing, so the whole group requires a valid ERP bearer token — the same token WebApp2
/// already holds from login. It is mapped only when the automation database is configured; without
/// one there is nothing to configure.
/// </remarks>
public static class PoAutomationEndpoints
{
    public static IEndpointRouteBuilder MapPoAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/automation/po-automation").RequireAuthorization();

        // Role Management form 011171 ("PO Automation Configuration"): 'I' opens the screen, 'E' is
        // every actionable thing on it — edit a row, flip the master toggle.
        group.MapGet("/", ListAsync).WithName("ListPoAutomations")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Inquiry);
        group.MapPut("/enabled", SetEnabledAsync).WithName("SetPoAutomationEnabled")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);
        group.MapGet("/{indentType}", GetAsync).WithName("GetPoAutomation")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Inquiry);
        group.MapPut("/{indentType}", UpdateAsync).WithName("UpdatePoAutomation")
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IIndentPoAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        var all = await configs.GetAllAsync(cancellationToken);

        return Results.Ok(all.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(
        string indentType,
        IIndentPoAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        if (!TryParseKind(indentType, out var kind, out var error))
        {
            return error!;
        }

        return Results.Ok(ToResponse(await configs.GetAsync(kind, cancellationToken)));
    }

    /// <summary>
    /// The master on/off toggle. Sets <c>IsActive</c> on all three rows in one transaction: off ⇒
    /// the scheduler runs nothing and a manual / timer conversion is refused (409).
    /// </summary>
    private static async Task<IResult> SetEnabledAsync(
        SetPoAutomationEnabledRequest? request,
        IIndentPoAutomationConfigRepository configs,
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
                "PO Automation cannot be turned on or off without an automation database on this installation.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var saved = await configs.SetAllActiveAsync(request.Enabled, ActorOf(user), cancellationToken);

        return Results.Ok(saved.Select(ToResponse).ToList());
    }

    private static async Task<IResult> UpdateAsync(
        string indentType,
        UpdatePoAutomationRequest? request,
        IIndentPoAutomationConfigRepository configs,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!TryParseKind(indentType, out var kind, out var kindError))
        {
            return kindError!;
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

        var update = new IndentPoAutomationConfigUpdate
        {
            RunMode = mode,
            ScheduleTime = scheduleTime,
            ClearScheduleTime = request.ClearScheduleTime,
            Sites = request.Sites,
            IndentNumbers = request.IndentNumbers,
            ClearIndentNumbers = request.ClearIndentNumbers,
            IsActive = request.IsActive,
            DryRun = request.DryRun,
            MaxIndentsPerRun = request.MaxIndentsPerRun,
            ClearMaxIndentsPerRun = request.ClearMaxIndentsPerRun,
        };

        try
        {
            var saved = await configs.UpsertAsync(kind, update, ActorOf(user), cancellationToken);

            return Results.Ok(ToResponse(saved));
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static PoAutomationConfigResponse ToResponse(IndentPoAutomationConfig config) => new(
        config.IndentKind.ToString(),
        config.RunMode.ToString(),
        config.ScheduleTime,
        config.Sites,
        config.IndentNumbers,
        config.IsActive,
        config.DryRun,
        config.MaxIndentsPerRun,
        config.LastScheduledRunDate,
        config.LastTriggeredAtUtc,
        config.LastRunStatus,
        config.LastRunReference,
        config.UpdatedAtUtc,
        config.UpdatedBy);

    private static bool TryParseKind(string value, out IndentKind kind, out IResult? error)
    {
        error = null;

        if (Enum.TryParse(value?.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind))
        {
            return true;
        }

        error = Results.Problem(
            $"'{value}' is not an indent type. Expected one of: {string.Join(", ", Enum.GetNames<IndentKind>())}.",
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

        // "HH:mm" or "HH:mm:ss" — the two shapes jqxDateTimeInput sends.
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

    /// <summary>Who to stamp on the change — the ERP user's full name, falling back to their id.</summary>
    private static string? ActorOf(ClaimsPrincipal user) =>
        user.FindFirstValue("userFullName")
        ?? user.FindFirstValue("userName")
        ?? user.FindFirstValue("id")
        ?? user.Identity?.Name;
}
