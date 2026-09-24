using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// Proves the agent can still authenticate to the ERP. Acquiring a token exercises reachability,
/// credentials and the login endpoint in one call — and because the token is cached, a frequent
/// health probe costs nothing after the first.
/// </summary>
public sealed class ErpApiHealthCheck : IHealthCheck
{
    private readonly IErpTokenProvider _tokenProvider;

    public ErpApiHealthCheck(IErpTokenProvider tokenProvider) => _tokenProvider = tokenProvider;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _tokenProvider.GetTokenAsync(cancellationToken);

            return string.IsNullOrWhiteSpace(token)
                ? HealthCheckResult.Unhealthy("ERP returned an empty login token.")
                : HealthCheckResult.Healthy("ERP API reachable and the login token is valid.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"ERP API check failed: {ex.Message}", ex);
        }
    }
}
