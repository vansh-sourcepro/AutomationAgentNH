using System.Globalization;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Endpoints;

/// <summary>
/// The PO → GRN settings API: <c>GET/PUT /api/automation/grn-automation</c>. Inbound API key only
/// for now; the dashboard section (and ERP-login access) comes later.
/// </summary>
public static class GrnAutomationEndpoints
{
    public const string Route = "/api/automation/grn-automation";

    public static IEndpointRouteBuilder MapGrnAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(Route).AddEndpointFilter<ApiKeyFilter>();

        group.MapGet(string.Empty, GetAsync).WithName("GetGrnAutomation");
        group.MapPut(string.Empty, UpdateAsync).WithName("UpdateGrnAutomation");

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

    private static async Task<IResult> UpdateAsync(
        UpdateGrnAutomationRequest? request,
        IPoGrnAutomationConfigRepository configs,
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

        try
        {
            var saved = await configs.UpdateAsync(
                update,
                string.IsNullOrWhiteSpace(request.UpdatedBy) ? "api-key" : request.UpdatedBy.Trim(),
                cancellationToken);

            return Results.Ok(ToResponse(saved));
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    internal static GrnAutomationConfigResponse ToResponse(PoGrnAutomationConfig config) =>
        new(
            config.IsActive,
            config.RunMode.ToString(),
            config.ScheduleTime,
            config.ReceiptMode.ToString(),
            config.InvoiceNumber,
            config.Sites,
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
