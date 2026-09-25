using System.Globalization;
using System.Security.Claims;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
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
        };

        return await SaveAsync(update, configs, user, cancellationToken);
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
