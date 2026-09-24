using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NewHorizon.Automation.Infrastructure.Persistence;

/// <summary>
/// Confirms the automation database is reachable. Reported by <c>/api/automation/health</c> so
/// operators can tell "the service is up" from "the service is up but cannot record anything".
/// </summary>
public sealed class AutomationDatabaseHealthCheck : IHealthCheck
{
    private readonly AutomationDbContext _dbContext;

    public AutomationDatabaseHealthCheck(AutomationDbContext dbContext) => _dbContext = dbContext;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy("Automation database reachable.")
                : HealthCheckResult.Unhealthy("Automation database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Automation database check failed.", ex);
        }
    }
}
