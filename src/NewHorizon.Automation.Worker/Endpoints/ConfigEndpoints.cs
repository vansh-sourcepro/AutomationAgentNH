using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Configuration;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Contracts;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Per-module runtime settings — the ones an administrator changes without a redeploy.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>appsettings.json</c>, which carries only what the agent needs to
/// start and connect. Everything here is read fresh at the start of each job, so a change applies
/// from the next run; a job already running keeps the mode it began with.
/// </remarks>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/automation/config").AddEndpointFilter<ApiKeyFilter>();

        group.MapGet("/", ListConfigAsync).WithName("ListConfig");
        group.MapGet("/{module}", GetConfigAsync).WithName("GetConfig");
        group.MapPost("/{module}", UpdateConfigAsync).WithName("UpdateConfig");

        return endpoints;
    }

    private static async Task<IResult> ListConfigAsync(
        IAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        var all = await configs.ListAsync(cancellationToken);

        return Results.Ok(all.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetConfigAsync(
        string module,
        IAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        // A module with no stored row still answers, with the defaults the agent would apply.
        var config = await configs.GetOrDefaultAsync(module, cancellationToken);

        return Results.Ok(ToResponse(config));
    }

    private static async Task<IResult> UpdateConfigAsync(
        string module,
        UpdateConfigRequest request,
        IAutomationConfigRepository configs,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.Problem("A settings body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        AutomationMode? mode = null;

        if (request.Mode is not null)
        {
            if (!Enum.TryParse<AutomationMode>(request.Mode, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    $"'{request.Mode}' is not an automation mode. Expected Full or Partial.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            mode = parsed;
        }

        var update = new AutomationConfigUpdate
        {
            EnableAgent = request.EnableAgent,
            EnableModule = request.EnableModule,
            Mode = mode,
            PollIntervalSeconds = request.PollIntervalSeconds,
            ReconcileIntervalMinutes = request.ReconcileIntervalMinutes,
            WorkingHoursStart = request.WorkingHoursStart,
            WorkingHoursEnd = request.WorkingHoursEnd,
            ClearWorkingHours = request.ClearWorkingHours,
            RetryCount = request.RetryCount,
            ParallelWorkers = request.ParallelWorkers,
            LoggingLevel = request.LoggingLevel,
            IsLicensed = request.IsLicensed,
            PayloadRetentionDays = request.PayloadRetentionDays,
            LogRetentionDays = request.LogRetentionDays,
            ErrorRetentionDays = request.ErrorRetentionDays,
        };

        try
        {
            var saved = await configs.UpsertAsync(module, update, request.UpdatedBy, cancellationToken);

            return Results.Ok(ToResponse(saved));
        }
        catch (DomainException ex)
        {
            // A non-positive interval or a negative retry count is the caller's mistake; the domain
            // rejects it rather than storing a setting that would break the next run.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static AutomationConfigResponse ToResponse(AutomationConfig config) => new(
        config.Module,
        config.EnableAgent,
        config.EnableModule,
        config.Mode.ToString(),
        config.PollIntervalSeconds,
        config.ReconcileIntervalMinutes,
        config.WorkingHoursStart,
        config.WorkingHoursEnd,
        config.RetryCount,
        config.ParallelWorkers,
        config.LoggingLevel,
        config.IsLicensed,
        config.PayloadRetentionDays,
        config.LogRetentionDays,
        config.ErrorRetentionDays,
        config.UpdatedAtUtc,
        config.UpdatedBy);
}
