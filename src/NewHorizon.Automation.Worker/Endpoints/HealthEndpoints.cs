using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHorizon.Automation.Worker.Contracts;
using NewHorizon.Automation.Worker.Diagnostics;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Liveness/readiness surface for the ERP and for operators. Deliberately unauthenticated and
/// loopback-bound: it reveals no job data, and the service monitor must be able to reach it.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/automation/health", GetHealthAsync)
            .WithName("GetAutomationHealth");

        // Convenience alias for service monitors and the install scripts.
        endpoints.MapGet("/health", GetHealthAsync)
            .WithName("GetHealth");

        return endpoints;
    }

    private static async Task<IResult> GetHealthAsync(
        AgentRuntimeInfo runtimeInfo,
        HealthCheckService healthCheckService,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);

        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = "Healthy",
        };

        foreach (var (name, entry) in report.Entries)
        {
            checks[name] = entry.Status.ToString();
        }

        var response = new HealthResponse(
            Status: report.Status.ToString(),
            Version: runtimeInfo.Version,
            StartedAtUtc: runtimeInfo.StartedAtUtc,
            UptimeSeconds: Math.Round(runtimeInfo.UptimeAt(nowUtc).TotalSeconds, 3),
            Checks: checks);

        // Degraded still answers 200: the service is running and can report why it is unhappy.
        return report.Status is HealthStatus.Unhealthy
            ? Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(response);
    }
}
